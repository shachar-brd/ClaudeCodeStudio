using System.Diagnostics;
using System.Text.Json.Nodes;
using ClaudeStudioAgent;
using ClaudeStudioShared;
using Xunit;

namespace ClaudeStudioTests;

// Drives a real Git Bash; skipped when Git for Windows is not installed.
public class PersistentShellTests : IAsyncLifetime
{
    private static readonly string? Bash = PersistentShellPool.FindGitBash();
    private static readonly TimeSpan T = TimeSpan.FromSeconds(30);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cs-shell-" + Guid.NewGuid().ToString("N"));
    private PersistentShell _shell = null!;

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _shell = new PersistentShell(Bash ?? "bash.exe", _dir);
        return default;
    }

    public async ValueTask DisposeAsync()
    {
        await _shell.DisposeAsync();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static void RequireBash() => Assert.SkipWhen(Bash == null, "Git Bash not installed");

    [Fact]
    public async Task Runs_a_command_and_returns_its_output()
    {
        RequireBash();
        var r = await _shell.RunAsync("echo hello; echo world", T);
        Assert.Equal("hello\nworld", r.Output);
        Assert.Equal(0, r.ExitCode);
        Assert.False(r.TimedOut);
        Assert.False(r.ShellEnded);
    }

    [Fact]
    public async Task Output_without_a_trailing_newline_is_kept()
    {
        RequireBash();
        Assert.Equal("abc", (await _shell.RunAsync("printf abc", T)).Output);
        Assert.Equal("", (await _shell.RunAsync("true", T)).Output);
        Assert.Equal("\nx", (await _shell.RunAsync("printf '\\nx\\n'", T)).Output);
    }

    [Fact]
    public async Task Reports_the_exit_code_and_stderr()
    {
        RequireBash();
        var r = await _shell.RunAsync("echo oops >&2; false", T);
        Assert.Equal(1, r.ExitCode);
        Assert.Equal("oops", r.Output);
    }

    [Fact]
    public async Task Quoting_reaches_bash_unchanged()
    {
        RequireBash();
        var r = await _shell.RunAsync("x='a \"b\" $HOME'; printf '%s|' \"$x\" 'c\\d' \"e'f\"", T);
        Assert.Equal("a \"b\" $HOME|c\\d|e'f|", r.Output);
        var heredoc = await _shell.RunAsync("cat <<'EOF'\nline1\n  line2\nEOF", T);
        Assert.Equal("line1\n  line2", heredoc.Output);
    }

    [Fact]
    public async Task Directory_and_exports_persist_between_commands()
    {
        RequireBash();
        await _shell.RunAsync("mkdir -p sub && cd sub && export CS_TEST_VAR=42", T);
        var r = await _shell.RunAsync("basename \"$PWD\"; echo $CS_TEST_VAR", T);
        Assert.Equal("sub\n42", r.Output);
    }

    [Fact]
    public async Task Starts_in_the_working_directory()
    {
        RequireBash();
        var r = await _shell.RunAsync("pwd -W", T);
        Assert.Equal(Path.GetFullPath(_dir).TrimEnd('\\'), r.Output.Replace('/', '\\'), ignoreCase: true);
    }

    [Fact]
    public async Task Stdin_is_empty_so_reading_commands_do_not_hang()
    {
        RequireBash();
        var r = await _shell.RunAsync("cat; read -r x; echo \"after:$x\"", T);
        Assert.Equal("after:", r.Output);
    }

    [Fact]
    public async Task Exit_ends_the_shell_and_the_next_command_gets_a_fresh_one()
    {
        RequireBash();
        await _shell.RunAsync("export CS_GONE=1", T);
        var r = await _shell.RunAsync("echo bye; exit 3", T);
        Assert.True(r.ShellEnded);
        Assert.Equal(3, r.ExitCode);
        Assert.Equal("bye", r.Output);
        var next = await _shell.RunAsync("echo \"[$CS_GONE]\"", T);
        Assert.Equal("[]", next.Output);
        Assert.Equal(0, next.ExitCode);
    }

    [Fact]
    public async Task Set_e_from_a_command_does_not_leak_into_the_next()
    {
        RequireBash();
        await _shell.RunAsync("set -e", T);
        var r = await _shell.RunAsync("false; echo still-here", T);
        Assert.Equal("still-here", r.Output);
        Assert.False(r.ShellEnded);
    }

    [Fact]
    public async Task Timeout_kills_the_command_and_the_shell_recovers()
    {
        RequireBash();
        var sw = Stopwatch.StartNew();
        var r = await _shell.RunAsync("echo started; sleep 30", TimeSpan.FromSeconds(2));
        Assert.True(r.TimedOut);
        Assert.Equal("started", r.Output);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15));
        Assert.Equal("ok", (await _shell.RunAsync("echo ok", T)).Output);
    }

    [Fact]
    public async Task Long_output_keeps_head_and_tail()
    {
        RequireBash();
        var r = await _shell.RunAsync("seq 1 20000", T);
        Assert.StartsWith("1\n2\n", r.Output);
        Assert.EndsWith("19999\n20000", r.Output);
        Assert.Contains("characters truncated", r.Output);
        Assert.True(r.Output.Length < 32000);
    }

    [Fact]
    public async Task Pool_runs_commands_in_parallel()
    {
        RequireBash();
        await using var pool = new PersistentShellPool(Bash!, _dir, 3);
        pool.Warm();
        var results = await Task.WhenAll(Enumerable.Range(1, 3)
            .Select(i => pool.RunAsync($"sleep 1; echo {i}", T)));
        Assert.Equal(new[] { "1", "2", "3" }, results.Select(r => r.Output));
    }
}

public class ShellToolTests
{
    private static JsonObject Msg(string json) => (JsonObject)JsonNode.Parse(json)!;
    private static Task<string> NoBridge(string spec) => throw new InvalidOperationException("not expected");

    [Fact]
    public async Task Bash_tool_is_listed_only_with_a_shell()
    {
        var list = Msg("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        var without = await VsBridgeMcpServer.HandleAsync(list, null, NoBridge);
        Assert.DoesNotContain("bash", without["result"]!["tools"]!.AsArray().Select(t => (string?)t!["name"]));
        var with = await VsBridgeMcpServer.HandleAsync(list, null, NoBridge,
            (_, _) => Task.FromResult(new ShellRunResult()));
        Assert.Contains("bash", with["result"]!["tools"]!.AsArray().Select(t => (string?)t!["name"]));
    }

    [Fact]
    public async Task Call_passes_command_and_clamped_timeout()
    {
        string? cmd = null;
        int timeout = 0;
        var r = await VsBridgeMcpServer.HandleAsync(
            Msg("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"bash","arguments":{"command":"ls -la","timeout":9999999}}}"""),
            null, NoBridge,
            (c, t) => { cmd = c; timeout = t; return Task.FromResult(new ShellRunResult { Output = "files" }); });
        Assert.Equal("ls -la", cmd);
        Assert.Equal(VsBridgeMcpServer.MaxShellTimeoutMs, timeout);
        Assert.False((bool)r["result"]!["isError"]!);
        Assert.Equal("files", (string?)r["result"]!["content"]![0]!["text"]);
    }

    [Fact]
    public async Task Non_zero_exit_is_an_error_with_the_code()
    {
        var r = await VsBridgeMcpServer.HandleAsync(
            Msg("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"bash","arguments":{"command":"false"}}}"""),
            null, NoBridge, (_, _) => Task.FromResult(new ShellRunResult { ExitCode = 2, Output = "bad" }));
        Assert.True((bool)r["result"]!["isError"]!);
        Assert.Equal("bad\nExit code 2", (string?)r["result"]!["content"]![0]!["text"]);
    }

    [Fact]
    public void Timeout_message_says_the_shell_was_reset()
    {
        var text = VsBridgeMcpServer.FormatShellResult(new ShellRunResult { TimedOut = true, Output = "x" }, 5000);
        Assert.Contains("timed out after 5s", text);
    }

    [Fact]
    public void Shell_tool_shows_as_Bash()
    {
        Assert.Equal("Bash", StreamEventParser.DisplayToolName(VsBridgeMcpServer.ShellToolFullName));
        Assert.Equal("Read", StreamEventParser.DisplayToolName("Read"));
    }

    [Fact]
    public void Initialize_without_hooks_registers_none()
    {
        Assert.Null(ControlHooks.BuildInitializeRequest(10, includeHooks: false)["hooks"]);
        Assert.NotNull(ControlHooks.BuildInitializeRequest(10)["hooks"]);
    }
}
