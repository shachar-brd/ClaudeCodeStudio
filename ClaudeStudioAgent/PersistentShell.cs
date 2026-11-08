using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace ClaudeStudioAgent;

// One long-lived Git Bash that runs many commands, so a shell command costs no
// process start of its own (only the programs it runs, like git). Claude's
// built-in Bash tool starts Git Bash for every call; inside the Sandboxie box
// each process start costs ~240 ms on top of ~100 ms outside it, and Git's
// bin\bash.exe launcher is two processes (scripts/shell-perf/README.md).
//
// Each command is sent on bash's stdin as a quoted here-doc read into a
// variable (no escaping needed), then eval'd with stdin from /dev/null (a
// command can never read the next one) and stderr folded into stdout, followed
// by a marker line with a random nonce and the exit code. The shell is a login
// shell (same PATH/env as Git Bash) started once; cd and exported variables
// persist between commands. A command that ends the shell (exit, exec, set -e)
// or times out kills its process tree; the next command starts a fresh shell.
internal sealed class PersistentShell : IAsyncDisposable
{
    public sealed record Result(string Output, int ExitCode, bool TimedOut, bool ShellEnded);

    // Same cap as the built-in Bash tool's default output limit.
    private const int MaxOutputChars = 30000;
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);

    private readonly string _bashPath;
    private readonly string? _workingDirectory;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Process? _proc;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private Task? _startTask;
    private readonly object _startLock = new();

    public PersistentShell(string bashPath, string? workingDirectory)
    {
        _bashPath = bashPath;
        _workingDirectory = workingDirectory;
    }

    private bool Alive => _proc is { HasExited: false } && _stdin != null && _stdout != null;

    // Starts the shell in the background: a login shell takes seconds in the box,
    // so the first command should not pay for it.
    public void Warm() => _ = EnsureStartedAsync().ContinueWith(t => _ = t.Exception, TaskScheduler.Default);

    private Task EnsureStartedAsync()
    {
        lock (_startLock)
        {
            if (_startTask == null || _startTask.IsFaulted || (_startTask.IsCompleted && !Alive))
                _startTask = StartAsync();
            return _startTask;
        }
    }

    private async Task StartAsync()
    {
        Kill();
        var psi = new ProcessStartInfo(_bashPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (!string.IsNullOrEmpty(_workingDirectory) && Directory.Exists(_workingDirectory))
            psi.WorkingDirectory = _workingDirectory;
        // usr\bin\bash.exe directly (one process, not Git's bin\ launcher): set
        // what the launcher would, so the login profile builds the usual PATH and
        // stays in the working directory.
        psi.ArgumentList.Add("-l");
        psi.Environment["MSYSTEM"] = "MINGW64";
        psi.Environment["CHERE_INVOKING"] = "1";
        // Nothing may wait for a human: no editor, pager or credential prompt.
        psi.Environment["GIT_EDITOR"] = "true";
        psi.Environment["GIT_PAGER"] = "cat";
        psi.Environment["PAGER"] = "cat";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

        var proc = Process.Start(psi) ?? throw new InvalidOperationException("could not start " + _bashPath);
        var stdin = new StreamWriter(proc.StandardInput.BaseStream, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = false };
        var stdout = proc.StandardOutput;
        // bash's own stderr (profile noise); commands fold theirs into stdout.
        _ = Task.Run(async () =>
        {
            try { while (await proc.StandardError.ReadLineAsync() != null) { } } catch { }
        });

        var ready = "__CS_READY_" + Guid.NewGuid().ToString("N");
        await stdin.WriteAsync($"printf '%s\\n' {ready}\n");
        await stdin.FlushAsync();
        using var cts = new CancellationTokenSource(StartTimeout);
        try
        {
            string? line;
            while ((line = await stdout.ReadLineAsync(cts.Token)) != null)
                if (line.TrimEnd('\r') == ready) break;
            if (line == null)
                throw new InvalidOperationException($"{_bashPath} exited during startup (exit {SafeExitCode(proc)})");
        }
        catch (OperationCanceledException)
        {
            KillTree(proc);
            throw new TimeoutException($"{_bashPath} did not start within {StartTimeout.TotalSeconds:F0}s");
        }

        _proc = proc;
        _stdin = stdin;
        _stdout = stdout;
    }

    public async Task<Result> RunAsync(string command, TimeSpan timeout, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await EnsureStartedAsync();
            var nonce = Guid.NewGuid().ToString("N");
            var eof = "__CS_EOF_" + nonce;
            var done = "__CS_DONE_" + nonce + " ";
            var body = command.Replace("\r\n", "\n").Replace('\r', '\n');
            var script =
                $"IFS= read -r -d '' __cs_cmd <<'{eof}'\n{body}\n{eof}\n" +
                "__cs_cmd=${__cs_cmd%$'\\n'}; eval \"$__cs_cmd\" </dev/null 2>&1; __cs_rc=$?; " +
                $"set +e +u +o pipefail 2>/dev/null; printf '\\n{done}%d\\n' \"$__cs_rc\"\n";
            await _stdin!.WriteAsync(script);
            await _stdin.FlushAsync();

            var output = new OutputBuffer(MaxOutputChars);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try
            {
                string? line;
                while ((line = await _stdout!.ReadLineAsync(cts.Token)) != null)
                {
                    if (line.StartsWith(done, StringComparison.Ordinal))
                    {
                        int.TryParse(line.AsSpan(done.Length).Trim(), out var rc);
                        return new Result(output.ToString(dropFinalEmptyLine: true), rc, false, false);
                    }
                    output.AddLine(line);
                }
                // The command ended the shell (exit, exec, set -e failure).
                var exit = SafeExitCode(_proc);
                Kill();
                return new Result(output.ToString(dropFinalEmptyLine: false), exit, false, true);
            }
            catch (OperationCanceledException)
            {
                Kill();
                if (ct.IsCancellationRequested) throw;
                return new Result(output.ToString(dropFinalEmptyLine: false), -1, true, true);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private static int SafeExitCode(Process? p)
    {
        try { if (p != null && p.WaitForExit(2000)) return p.ExitCode; } catch { }
        return -1;
    }

    private static void KillTree(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        try { p.Dispose(); } catch { }
    }

    private void Kill()
    {
        var p = _proc;
        _proc = null;
        try { _stdin?.Dispose(); } catch { }
        _stdin = null;
        _stdout = null;
        if (p != null) KillTree(p);
    }

    public ValueTask DisposeAsync()
    {
        Kill();
        return default;
    }

    // Keeps the head and the tail of a long output, like the built-in Bash tool.
    private sealed class OutputBuffer
    {
        private readonly int _half;
        private readonly StringBuilder _head = new();
        private int _headLines;
        private readonly LinkedList<string> _tail = new();
        private int _tailChars;
        private long _omitted;

        public OutputBuffer(int max) => _half = max / 2;

        public void AddLine(string line)
        {
            line = line.TrimEnd('\r');
            if (_head.Length + line.Length + 1 <= _half && _tail.Count == 0)
            {
                if (_headLines++ > 0) _head.Append('\n');
                _head.Append(line);
                return;
            }
            _tail.AddLast(line);
            _tailChars += line.Length + 1;
            while (_tailChars > _half && _tail.Count > 1)
            {
                _omitted += _tail.First!.Value.Length + 1;
                _tailChars -= _tail.First.Value.Length + 1;
                _tail.RemoveFirst();
            }
        }

        public string ToString(bool dropFinalEmptyLine)
        {
            // The done marker is printed after a newline, so a command whose output
            // ended with one leaves an empty last line.
            if (dropFinalEmptyLine)
            {
                if (_tail.Count > 0) { if (_tail.Last!.Value.Length == 0) _tail.RemoveLast(); }
                else if (_headLines > 0 && (_head.Length == 0 || _head[^1] == '\n'))
                {
                    if (_head.Length > 0) _head.Length--;
                    _headLines--;
                }
            }
            if (_tail.Count == 0) return _head.ToString();
            var sb = new StringBuilder(_head.ToString());
            if (_omitted > 0) sb.Append($"\n\n... [{_omitted} characters truncated] ...\n");
            foreach (var l in _tail) sb.Append('\n').Append(l);
            return sb.ToString();
        }
    }
}

// A few shells, so parallel tool calls do not queue behind each other; started
// lazily, the first one warmed at session start.
internal sealed class PersistentShellPool : IAsyncDisposable
{
    private readonly string _bashPath;
    private readonly string? _workingDirectory;
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentBag<PersistentShell> _idle = new();
    private readonly ConcurrentBag<PersistentShell> _all = new();

    public PersistentShellPool(string bashPath, string? workingDirectory, int size)
    {
        _bashPath = bashPath;
        _workingDirectory = workingDirectory;
        _slots = new SemaphoreSlim(size, size);
    }

    public string BashPath => _bashPath;

    public void Warm()
    {
        var shell = New();
        shell.Warm();
        _idle.Add(shell);
    }

    private PersistentShell New()
    {
        var shell = new PersistentShell(_bashPath, _workingDirectory);
        _all.Add(shell);
        return shell;
    }

    public async Task<PersistentShell.Result> RunAsync(string command, TimeSpan timeout)
    {
        await _slots.WaitAsync();
        if (!_idle.TryTake(out var shell)) shell = New();
        try { return await shell.RunAsync(command, timeout); }
        finally
        {
            _idle.Add(shell);
            _slots.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var s in _all) await s.DisposeAsync();
    }

    // usr\bin\bash.exe of the Git for Windows install claude would use:
    // CLAUDE_CODE_GIT_BASH_PATH if set, else the git.exe on PATH, else the default
    // install. null when there is none.
    public static string? FindGitBash()
    {
        static string? UsrBash(string? root)
        {
            if (string.IsNullOrEmpty(root)) return null;
            var p = Path.Combine(root, "usr", "bin", "bash.exe");
            return File.Exists(p) ? p : null;
        }

        var configured = Environment.GetEnvironmentVariable("CLAUDE_CODE_GIT_BASH_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(configured));
            // <git>\bin\bash.exe is the launcher; prefer its usr\bin\bash.exe.
            if (dir != null && Path.GetFileName(dir).Equals("bin", StringComparison.OrdinalIgnoreCase)
                && UsrBash(Path.GetDirectoryName(dir)) is { } usr
                && !Path.GetFileName(Path.GetDirectoryName(dir)!).Equals("usr", StringComparison.OrdinalIgnoreCase))
                return usr;
            return configured;
        }

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                if (string.IsNullOrWhiteSpace(dir) || !File.Exists(Path.Combine(dir, "git.exe"))) continue;
                var parent = Path.GetDirectoryName(Path.GetFullPath(dir));      // <git>\cmd, <git>\bin
                var found = UsrBash(parent) ?? UsrBash(Path.GetDirectoryName(parent)); // <git>\mingw64\bin
                if (found != null) return found;
            }
            catch { }
        }

        return UsrBash(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git"));
    }
}
