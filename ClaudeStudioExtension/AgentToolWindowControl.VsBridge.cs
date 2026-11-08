using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using ClaudeStudioShared;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace ClaudeStudioExtension;

// VS bridge: builds and test runs that the agent asks for (the vs_build / vs_test
// MCP tools and redirected shell builds). Everything here runs in the IDE process,
// i.e. outside any sandbox the agent and claude run in; the agent only sends these
// requests after the user approved the tool call.
public partial class AgentToolWindowControl
{
    private static readonly Guid TestPaneGuid = new("5e3c1f0a-2b7d-4c55-9a41-7f0d2c8e9b13");
    private const string TestPaneName = "Claude Code Studio: Tests";

    // Builds in VS can wait on long builds, but must answer before the agent's
    // 15-minute bridge timeout.
    private static readonly TimeSpan BridgeBuildTimeout = TimeSpan.FromMinutes(14);
    private static readonly TimeSpan TestRunTimeout = TimeSpan.FromMinutes(40);
    private const int MaxReportedFailures = 25;

    // True while a bridge build runs, so OnSolutionBuildDone does not also post
    // its result into the chat.
    private bool _bridgeBuildActive;

    private sealed class BridgeProject
    {
        public string Name = "";
        public string UniqueName = "";
        public string FullPath = "";
        public EnvDTE.Project? Project;
    }

    // Builds the solution or the requested projects through VS (output in the
    // Build pane) and returns a summary plus Error List entries. Empty string =
    // could not take the request (only used for shell builds, which then run as
    // approved).
    private async Task<string> RunVsBuildAsync(VsBuildSpec spec)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
        if (dte?.Solution == null || string.IsNullOrEmpty(dte.Solution.FullName))
            return "";

        var build = dte.Solution.SolutionBuild;
        if (build.BuildState == EnvDTE.vsBuildState.vsBuildStateInProgress)
            return "A build is already running in Visual Studio. Wait for it to finish and try again.";

        var (wholeSolution, projects, error) = ResolveProjects(dte, spec.Projects, spec.WorkingDirectory);
        if (error != null)
            return spec.FromShell ? "" : error;

        var action = spec.Action is "rebuild" or "clean" ? spec.Action : "build";
        var activeConfig = build.ActiveConfiguration?.Name ?? "Debug";
        var config = string.IsNullOrWhiteSpace(spec.Configuration) ? activeConfig : spec.Configuration!;
        var configNote = "";
        if (wholeSolution && !string.Equals(config, activeConfig, StringComparison.OrdinalIgnoreCase))
        {
            // Solution builds always use the active configuration; switching it
            // would change the user's IDE state.
            configNote = $" Note: Visual Studio built the active configuration '{activeConfig}', not '{config}'.";
            config = activeConfig;
        }

        var target = wholeSolution
            ? "solution"
            : (projects.Count == 1 ? "project " : "projects ") + string.Join(", ", projects.Select(p => p.Name));
        var targetWithConfig = $"{target} [{config}]";

        var steps = action == "rebuild" ? new[] { "clean", "build" } : new[] { action };
        var failed = 0;
        _bridgeBuildActive = true;
        try
        {
            ShowBuildOutputPane(dte);
            foreach (var step in steps)
            {
                if (wholeSolution)
                {
                    var ok = await RunBuildAndWaitAsync(dte, () =>
                    {
                        ThreadHelper.ThrowIfNotOnUIThread();
                        if (step == "clean") build.Clean(false); else build.Build(false);
                    });
                    if (!ok) return $"Timed out waiting for Visual Studio to finish the {step} of the {target}.";
                    if (step == "build") failed += build.LastBuildInfo;
                    continue;
                }

                foreach (var p in projects)
                {
                    var ok = await RunBuildAndWaitAsync(dte, () =>
                    {
                        ThreadHelper.ThrowIfNotOnUIThread();
                        if (step == "build") build.BuildProject(config, p.UniqueName, false);
                        else CleanProject(p.UniqueName);
                    });
                    if (!ok) return $"Timed out waiting for Visual Studio to finish the {step} of project {p.Name}.";
                    if (step == "build") failed += build.LastBuildInfo;
                }
            }
        }
        catch (Exception ex)
        {
            return $"Visual Studio could not {action} the {targetWithConfig}: {ex.Message}";
        }
        finally
        {
            _bridgeBuildActive = false;
        }

        if (action == "clean")
            return $"Clean of {targetWithConfig} completed.{configNote}";

        // Let the Error List catch up with the finished build.
        await Task.Delay(700);

        var (errors, warnings, prompt) = await Diagnostics.ErrorListReader.ReadAllAsync(_lastWorkingDir ?? spec.WorkingDirectory);
        var verb = action == "rebuild" ? "Rebuild" : "Build";
        if (failed == 0 && errors == 0)
            return $"{verb} of {targetWithConfig} succeeded ({warnings} warning(s)).{configNote}";
        if (errors == 0)
            return $"{verb} of {targetWithConfig} failed in {failed} project(s) but the Error List has no errors; check the VS Output window.{configNote}";
        return prompt + configNote;
    }

    // Starts a build operation and waits for OnBuildDone. False on timeout.
    private static async Task<bool> RunBuildAndWaitAsync(EnvDTE.DTE dte, Action start)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        // Held in a local for the whole wait: a DTE events object that is only
        // reachable through the delegate can be collected, and the event lost.
        var events = dte.Events.BuildEvents;
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EnvDTE._dispBuildEvents_OnBuildDoneEventHandler handler = (s, a) => done.TrySetResult(true);
        events.OnBuildDone += handler;
        try
        {
            start();
            ShowBuildOutputPane(dte);
            var completed = await Task.WhenAny(done.Task, Task.Delay(BridgeBuildTimeout));
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            return completed == done.Task;
        }
        finally
        {
            events.OnBuildDone -= handler;
            GC.KeepAlive(events);
        }
    }

    // Maps request entries (project names, project/solution paths, directories)
    // to projects of the open solution. Empty entries = whole solution.
    private static (bool wholeSolution, List<BridgeProject> projects, string? error) ResolveProjects(
        EnvDTE.DTE dte, IReadOnlyCollection<string> entries, string? workingDirectory)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var all = EnumerateProjects(dte.Solution.Projects).ToList();
        if (entries.Count == 0) return (true, all, null);

        var solutionPath = SafeFullPath(dte.Solution.FullName) ?? dte.Solution.FullName;
        var solutionDir = Path.GetDirectoryName(solutionPath) ?? "";
        var baseDir = string.IsNullOrWhiteSpace(workingDirectory) ? solutionDir : workingDirectory!;

        var result = new List<BridgeProject>();
        foreach (var raw in entries)
        {
            var entry = raw.Trim();
            if (entry.Length == 0) continue;

            BridgeProject? match = null;
            if (!LooksLikePath(entry))
            {
                match = all.FirstOrDefault(p =>
                    string.Equals(p.Name, entry, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Path.GetFileNameWithoutExtension(p.FullPath), entry, StringComparison.OrdinalIgnoreCase));
            }

            if (match == null)
            {
                var full = SafeFullPath(Path.Combine(baseDir, entry));
                if (full == null)
                    return (false, result, $"'{entry}' is not a valid project name or path.");

                if (string.Equals(full, solutionPath, StringComparison.OrdinalIgnoreCase))
                    return (true, all, null);

                if (Directory.Exists(full))
                {
                    var dir = full.TrimEnd('\\', '/');
                    if (string.Equals(dir, solutionDir.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                        return (true, all, null);
                    var inDir = all.Where(p => string.Equals(Path.GetDirectoryName(p.FullPath), dir, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (inDir.Count == 1) match = inDir[0];
                    else if (inDir.Count > 1)
                        return (false, result, $"'{entry}' holds several projects ({string.Join(", ", inDir.Select(p => p.Name))}); name one.");
                }
                else
                {
                    match = all.FirstOrDefault(p => string.Equals(p.FullPath, full, StringComparison.OrdinalIgnoreCase));
                    var ext = Path.GetExtension(full);
                    if (match == null && (ext.Equals(".sln", StringComparison.OrdinalIgnoreCase) || ext.Equals(".slnx", StringComparison.OrdinalIgnoreCase)))
                        return (false, result, $"'{entry}' is not the solution open in Visual Studio ({Path.GetFileName(solutionPath)}).");
                }
            }

            if (match == null)
                return (false, result, $"Project '{entry}' was not found in the open solution. Projects: {string.Join(", ", all.Select(p => p.Name))}.");
            if (!result.Contains(match)) result.Add(match);
        }

        return result.Count == 0 ? (true, all, null) : (false, result, null);
    }

    private static bool LooksLikePath(string entry) =>
        entry == "." || entry == ".." || entry.IndexOfAny(new[] { '\\', '/' }) >= 0 ||
        new[] { ".csproj", ".vbproj", ".fsproj", ".vcxproj", ".sqlproj", ".sln", ".slnx" }
            .Any(e => entry.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    private static string? SafeFullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return null; }
    }

    private static IEnumerable<BridgeProject> EnumerateProjects(EnvDTE.Projects projects)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var list = new List<BridgeProject>();
        foreach (EnvDTE.Project p in projects) CollectProjects(p, list);
        return list;
    }

    private static void CollectProjects(EnvDTE.Project? p, List<BridgeProject> into)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (p == null) return;
        if (p.Kind == "{66A26720-8FB5-11D2-AA7E-00C04F688DDE}") // solution folder
        {
            if (p.ProjectItems == null) return;
            foreach (EnvDTE.ProjectItem item in p.ProjectItems)
                CollectProjects(item.SubProject, into);
            return;
        }

        string fullName;
        try { fullName = p.FullName; } catch { return; } // unloaded projects throw
        if (string.IsNullOrEmpty(fullName)) return;
        into.Add(new BridgeProject
        {
            Name = p.Name,
            UniqueName = p.UniqueName,
            FullPath = SafeFullPath(fullName) ?? fullName,
            Project = p
        });
    }

    // Brings up the Output window with the "Build" pane selected, so the user
    // watches the build in the same place as when pressing Build themselves.
    private static void ShowBuildOutputPane(EnvDTE.DTE dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            dte.ExecuteCommand("View.Output");
            var window = dte.Windows.Item(EnvDTE.Constants.vsWindowKindOutput);
            window.Visible = true;
            var output = (EnvDTE.OutputWindow)window.Object;
            foreach (EnvDTE.OutputWindowPane pane in output.OutputWindowPanes)
            {
                if (pane.Guid.Equals("{1BD8A850-02D1-11D1-BEE7-00A0C913D1F8}", StringComparison.OrdinalIgnoreCase))
                {
                    pane.Activate();
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            OutputLog.Warn($"could not select the Build output pane: {ex.Message}");
        }
    }

    private static void CleanProject(string uniqueName)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var sln = (IVsSolution)Package.GetGlobalService(typeof(SVsSolution));
        sln.GetProjectOfUniqueName(uniqueName, out var hier);
        var mgr = (IVsSolutionBuildManager2)Package.GetGlobalService(typeof(SVsSolutionBuildManager));
        mgr.StartSimpleUpdateProjectConfiguration(hier, null, null,
            (uint)VSSOLNBUILDUPDATEFLAGS.SBF_OPERATION_CLEAN, 0, 0);
    }

    // ── Tests ───────────────────────────────────────────────────────────────

    // Builds the test projects in VS, then runs `dotnet test --no-build` from the
    // IDE process with output in the "Claude Code Studio: Tests" pane, and returns
    // counts plus failing tests parsed from the TRX results.
    private async Task<string> RunVsTestAsync(VsTestSpec spec)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
        if (dte?.Solution == null || string.IsNullOrEmpty(dte.Solution.FullName))
            return "";
        if (dte.Solution.SolutionBuild.BuildState == EnvDTE.vsBuildState.vsBuildStateInProgress)
            return "A build is already running in Visual Studio. Wait for it to finish and try again.";

        var (wholeSolution, resolved, error) = ResolveProjects(dte, spec.Projects, spec.WorkingDirectory);
        if (error != null) return error;
        var projects = wholeSolution ? resolved.Where(p => IsTestProject(p.FullPath)).ToList() : resolved;
        if (projects.Count == 0)
            return "No test projects found in the open solution (looked for Microsoft.NET.Test.Sdk, MSTest.Sdk, IsTestProject or a legacy MSTest project).";

        if (spec.Build)
        {
            var buildText = await RunVsBuildAsync(new VsBuildSpec
            {
                Action = "build",
                Projects = projects.Select(p => p.FullPath).ToList(),
                WorkingDirectory = spec.WorkingDirectory
            });
            if (!(buildText.StartsWith("Build of ", StringComparison.Ordinal) && buildText.Contains(" succeeded")))
                return "Tests were not run because the build did not succeed.\n" + buildText;
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        }

        // Configuration/platform each project actually builds with, so --no-build
        // finds the binaries VS just produced. Legacy (non-SDK) projects have no
        // VSTest target, so `dotnet test` silently does nothing for them; those run
        // the built assembly through the IDE's vstest.console.exe instead.
        var vstest = FindVsTestConsole(dte);
        var resultsRoot = Path.Combine(Path.GetTempPath(), "ClaudeStudio", "tests", Guid.NewGuid().ToString("N"));
        var runs = new List<(BridgeProject project, string tool, List<string> args, string trxName)>();
        foreach (var p in projects)
        {
            string config = "Debug";
            string? platform = null;
            string? assembly = null;
            var sdkStyle = IsSdkStyleProject(p.FullPath);
            try
            {
                var active = p.Project?.ConfigurationManager?.ActiveConfiguration;
                if (active != null)
                {
                    config = active.ConfigurationName;
                    var plat = active.PlatformName;
                    if (!string.IsNullOrEmpty(plat) && !plat.Replace(" ", "").Equals("AnyCPU", StringComparison.OrdinalIgnoreCase))
                        platform = plat;
                    if (!sdkStyle)
                        assembly = GetOutputAssembly(p, active);
                }
            }
            catch { }

            var trxName = SafeFileName(p.Name) + ".trx";
            if (!sdkStyle)
            {
                if (vstest == null)
                    return $"{p.Name} is a legacy (non-SDK) test project and vstest.console.exe was not found under the Visual Studio installation.";
                if (assembly == null || !File.Exists(assembly))
                    return $"Could not find the built test assembly for {p.Name}" + (assembly != null ? $" (expected {assembly})" : "") +
                           ". Build it first (vs_build) or run vs_test with build=true.";
                var vsArgs = new List<string>
                {
                    assembly, "/logger:trx;LogFileName=" + trxName, "/ResultsDirectory:" + resultsRoot
                };
                var vsPlatform = platform?.Replace(" ", "");
                if (vsPlatform != null && new[] { "x86", "x64", "ARM64" }.Contains(vsPlatform, StringComparer.OrdinalIgnoreCase))
                    vsArgs.Add("/Platform:" + vsPlatform);
                if (!string.IsNullOrWhiteSpace(spec.Filter)) vsArgs.Add("/TestCaseFilter:" + spec.Filter);
                runs.Add((p, vstest, vsArgs, trxName));
                continue;
            }

            var args = new List<string>
            {
                "test", p.FullPath, "--no-build", "--nologo", "-c", config,
                "--logger", "trx;LogFileName=" + trxName,
                "--results-directory", resultsRoot
            };
            if (platform != null) args.Add("-p:Platform=" + platform);
            if (!string.IsNullOrWhiteSpace(spec.Filter)) { args.Add("--filter"); args.Add(spec.Filter!); }
            runs.Add((p, "dotnet", args, trxName));
        }

        var pane = GetTestPane(dte);
        pane?.Clear();
        pane?.OutputStringThreadSafe($"Claude Code Studio: running tests for {string.Join(", ", projects.Select(p => p.Name))}" +
                                     (spec.Filter != null ? $" (filter: {spec.Filter})" : "") + Environment.NewLine);

        var sw = Stopwatch.StartNew();
        var outcomes = new List<(string project, int exitCode, string? trx, string tail)>();
        try
        {
            Directory.CreateDirectory(resultsRoot);
            foreach (var (project, tool, args, trxName) in runs)
            {
                pane?.OutputStringThreadSafe($"{Environment.NewLine}> {QuoteArg(tool)} {string.Join(" ", args.Select(QuoteArg))}{Environment.NewLine}");
                var (exit, tail) = await RunProcessToPaneAsync(
                    tool, args, Path.GetDirectoryName(project.FullPath) ?? resultsRoot, pane, TestRunTimeout);
                var trxPath = Path.Combine(resultsRoot, trxName);
                outcomes.Add((project.Name, exit, File.Exists(trxPath) ? trxPath : null, tail));
            }

            return SummarizeTestRun(outcomes, sw.Elapsed, spec.Filter);
        }
        finally
        {
            try { Directory.Delete(resultsRoot, recursive: true); } catch { }
        }
    }

    private static bool IsTestProject(string projectPath)
    {
        try
        {
            var text = File.ReadAllText(projectPath);
            return text.IndexOf("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("MSTest.Sdk", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("<IsTestProject>true", StringComparison.OrdinalIgnoreCase) >= 0
                // Legacy .NET Framework test projects (MSTest v1 project type / TeamTest).
                || text.IndexOf("<TestProjectType>UnitTest", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("3AC096D0-A1C2-E12C-1390-A8335801FDAB", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("Microsoft.VisualStudio.QualityTools.UnitTestFramework", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch { return false; }
    }

    private static bool IsSdkStyleProject(string projectPath)
    {
        try
        {
            var root = XDocument.Load(projectPath).Root;
            if (root == null) return true;
            return root.Attribute("Sdk") != null
                || root.Elements().Any(e => e.Name.LocalName == "Sdk")
                || root.Elements().Any(e => e.Name.LocalName == "Import" && e.Attribute("Sdk") != null);
        }
        catch { return true; }
    }

    // Full path of the assembly the project's active configuration builds.
    private static string? GetOutputAssembly(BridgeProject p, EnvDTE.Configuration active)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            var outputPath = active.Properties?.Item("OutputPath")?.Value as string;
            var fileName = p.Project?.Properties?.Item("OutputFileName")?.Value as string;
            if (string.IsNullOrEmpty(outputPath) || string.IsNullOrEmpty(fileName)) return null;
            var dir = Path.GetDirectoryName(p.FullPath) ?? "";
            return Path.GetFullPath(Path.Combine(dir, outputPath, fileName));
        }
        catch { return null; }
    }

    // vstest.console.exe shipped with the running IDE (Common7\IDE\Extensions\TestPlatform).
    private static string? FindVsTestConsole(EnvDTE.DTE dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            var ideDir = Path.GetDirectoryName(dte.FullName);
            if (ideDir == null) return null;
            var path = Path.Combine(ideDir, "Extensions", "TestPlatform", "vstest.console.exe");
            return File.Exists(path) ? path : null;
        }
        catch { return null; }
    }

    private static string SummarizeTestRun(
        List<(string project, int exitCode, string? trx, string tail)> outcomes, TimeSpan elapsed, string? filter)
    {
        int total = 0, passed = 0, failed = 0, skipped = 0;
        var failures = new List<string>();
        var problems = new List<string>();

        foreach (var o in outcomes)
        {
            if (o.trx == null)
            {
                problems.Add(o.exitCode != 0
                    ? $"{o.project}: the test run exited with code {o.exitCode} and wrote no results. Last output:\n{o.tail}"
                    : $"{o.project}: the test run finished but wrote no results (no test adapter ran for this project?). Last output:\n{o.tail}");
                continue;
            }

            var failedBefore = failed;
            var problemsBefore = problems.Count;
            try
            {
                var doc = XDocument.Load(o.trx);
                XNamespace ns = doc.Root?.Name.Namespace ?? XNamespace.None;
                foreach (var r in doc.Descendants(ns + "UnitTestResult"))
                {
                    total++;
                    var outcome = (string?)r.Attribute("outcome") ?? "";
                    if (outcome == "Passed") { passed++; continue; }
                    if (outcome is "NotExecuted" or "Inconclusive" or "Pending") { skipped++; continue; }

                    failed++;
                    if (failures.Count >= MaxReportedFailures) continue;
                    var name = (string?)r.Attribute("testName") ?? "(unnamed test)";
                    var info = r.Element(ns + "Output")?.Element(ns + "ErrorInfo");
                    var message = Truncate(info?.Element(ns + "Message")?.Value?.Trim(), 600);
                    var stack = string.Join("\n", (info?.Element(ns + "StackTrace")?.Value ?? "")
                        .Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).Take(5));
                    var sb = new StringBuilder($"- {name} ({o.project}, {outcome})");
                    if (!string.IsNullOrEmpty(message)) sb.Append("\n  ").Append(message!.Replace("\n", "\n  "));
                    if (stack.Length > 0) sb.Append("\n  ").Append(stack.Replace("\n", "\n  "));
                    failures.Add(sb.ToString());
                }
            }
            catch (Exception ex)
            {
                problems.Add($"{o.project}: could not read the test results ({ex.Message}).");
            }

            // A non-zero exit with every test passing is a crashed or aborted run.
            if (o.exitCode != 0 && failed == failedBefore && problems.Count == problemsBefore)
                problems.Add($"{o.project}: the test run exited with code {o.exitCode}. Last output:\n{o.tail}");
        }

        var counts = $"{passed} passed, {failed} failed, {skipped} skipped, {elapsed.TotalSeconds:F1}s";
        if (failed == 0 && problems.Count == 0)
        {
            if (total == 0)
                return "No tests were run" + (filter != null ? $" (no test matched the filter '{filter}')." : ".");
            return $"Tests passed: {counts}.";
        }

        var text = new StringBuilder($"Tests failed: {counts}.");
        if (failures.Count > 0) text.Append("\n\n").Append(string.Join("\n", failures));
        if (failed > failures.Count)
            text.Append($"\n...and {failed - failures.Count} more (see the '{TestPaneName}' output pane).");
        foreach (var p in problems) text.Append("\n\n").Append(p);
        return text.ToString();
    }

    private static string? Truncate(string? s, int max) =>
        s == null || s.Length <= max ? s : s.Substring(0, max) + "...";

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    // Runs a process (no shell), streaming stdout/stderr into the pane. Returns the
    // exit code (-1 on timeout or start failure) and the last lines of output.
    private static async Task<(int exitCode, string tail)> RunProcessToPaneAsync(
        string fileName, IEnumerable<string> args, string workingDirectory, IVsOutputWindowPane? pane, TimeSpan timeout)
    {
        var tail = new Queue<string>();
#pragma warning disable VSTHRD010 // OutputStringThreadSafe is free-threaded by contract
        void OnLine(string? line)
        {
            if (line == null) return;
            pane?.OutputStringThreadSafe(line + Environment.NewLine);
            lock (tail)
            {
                tail.Enqueue(line);
                while (tail.Count > 40) tail.Dequeue();
            }
        }
#pragma warning restore VSTHRD010

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = string.Join(" ", args.Select(QuoteArg)),
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.EnvironmentVariables["DOTNET_CLI_UI_LANGUAGE"] = "en";
        psi.EnvironmentVariables["DOTNET_NOLOGO"] = "1";

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) => OnLine(e.Data);
        proc.ErrorDataReceived += (_, e) => OnLine(e.Data);
        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            return (-1, $"could not start {fileName}: {ex.Message}");
        }
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        var exited = await Task.Run(() => proc.WaitForExit((int)timeout.TotalMilliseconds));
        if (!exited)
        {
            KillProcessTree(proc.Id);
            OnLine($"Timed out after {timeout.TotalMinutes:F0} minutes; test run killed.");
            return (-1, TailText());
        }
        proc.WaitForExit(); // flush the async output readers
        return (proc.ExitCode, TailText());

        string TailText()
        {
            lock (tail) return string.Join("\n", tail);
        }
    }

    private static void KillProcessTree(int pid)
    {
        try
        {
            using var killer = Process.Start(new ProcessStartInfo("taskkill", $"/T /F /PID {pid}")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            });
            killer?.WaitForExit(5000);
        }
        catch { }
    }

    // Windows command-line quoting (CommandLineToArgvW rules).
    private static string QuoteArg(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"', ';', '&', '|', '<', '>' }) < 0)
            return arg;
        var sb = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"') sb.Append('\\', backslashes * 2 + 1);
            else sb.Append('\\', backslashes);
            backslashes = 0;
            sb.Append(c);
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }

    private static IVsOutputWindowPane? GetTestPane(EnvDTE.DTE dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            if (Package.GetGlobalService(typeof(SVsOutputWindow)) is not IVsOutputWindow window) return null;
            var guid = TestPaneGuid;
            if (ErrorHandler.Failed(window.GetPane(ref guid, out var pane)) || pane == null)
            {
                window.CreatePane(ref guid, TestPaneName, fInitVisible: 1, fClearWithSolution: 0);
                window.GetPane(ref guid, out pane);
            }
            dte.ExecuteCommand("View.Output");
            pane?.Activate();
            return pane;
        }
        catch (Exception ex)
        {
            OutputLog.Warn($"could not open the tests output pane: {ex.Message}");
            return null;
        }
    }
}
