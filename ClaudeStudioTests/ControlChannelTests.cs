using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ClaudeStudioShared;
using Xunit;

namespace ClaudeStudioTests;

public class ControlHooksTests
{
    [Fact]
    public void Initialize_registers_both_hooks_with_the_timeout()
    {
        var req = ControlHooks.BuildInitializeRequest(3900);
        Assert.Equal("initialize", (string?)req["subtype"]);
        var pre = req["hooks"]!["PreToolUse"]![0]!;
        Assert.Equal(ControlHooks.PreToolUseMatcher, (string?)pre["matcher"]);
        Assert.Equal(ControlHooks.PreToolUseCallbackId, (string?)pre["hookCallbackIds"]![0]);
        Assert.Equal(3900, (int)pre["timeout"]!);
        var post = req["hooks"]!["PostToolUse"]![0]!;
        Assert.Equal(ControlHooks.PostToolUseCallbackId, (string?)post["hookCallbackIds"]![0]);
    }

    [Theory]
    [InlineData("Bash", true)]
    [InlineData("PowerShell", true)]
    [InlineData("Write", true)]
    [InlineData("WebFetch", true)]
    [InlineData("mcp__claudestudio-vs__vs_build", true)]
    [InlineData("SomeFutureTool", true)]
    [InlineData("Read", false)]
    [InlineData("Grep", false)]
    [InlineData("TodoWrite", false)]
    [InlineData("AskUserQuestion", false)]
    [InlineData("ExitPlanMode", false)]
    public void Pre_tool_use_matcher_gates_everything_but_read_only_tools(string tool, bool gated)
    {
        Assert.Equal(gated, Regex.IsMatch(tool, ControlHooks.PreToolUseMatcher));
    }

    [Fact]
    public void Parses_hook_callback_request()
    {
        var req = JsonDocument.Parse("""
            {"subtype":"hook_callback","callback_id":"claudestudio-post","tool_use_id":"toolu_outer",
             "input":{"hook_event_name":"PostToolUse","tool_name":"Write","tool_use_id":"toolu_1","cwd":"C:\\repo",
                      "tool_input":{"file_path":"C:\\repo\\a.cs","content":"x"}}}
            """).RootElement;
        var call = ControlHooks.ParseHookCallback(req);
        Assert.Equal(ControlHooks.PostToolUseCallbackId, call.CallbackId);
        Assert.Equal("Write", call.ToolName);
        Assert.Equal("toolu_1", call.ToolUseId);
        Assert.Equal("C:\\repo", call.Cwd);
        Assert.Equal("C:\\repo\\a.cs", call.FilePath);
        Assert.Contains("\"content\":\"x\"", call.ToolInputJson);
    }

    [Fact]
    public void Falls_back_to_the_request_tool_use_id()
    {
        var req = JsonDocument.Parse("""
            {"subtype":"hook_callback","callback_id":"claudestudio-pre","tool_use_id":"toolu_outer",
             "input":{"tool_name":"Bash","tool_input":{"command":"ls"}}}
            """).RootElement;
        Assert.Equal("toolu_outer", ControlHooks.ParseHookCallback(req).ToolUseId);
    }

    [Fact]
    public void Deny_carries_the_reason()
    {
        var o = ControlHooks.DenyPreToolUse("Permission denied: no");
        Assert.Equal("deny", (string?)o["hookSpecificOutput"]!["permissionDecision"]);
        Assert.Equal("Permission denied: no", (string?)o["hookSpecificOutput"]!["permissionDecisionReason"]);
    }

    [Fact]
    public void Post_tool_use_context_is_empty_without_diagnostics()
    {
        Assert.Empty(ControlHooks.PostToolUseContext(""));
        Assert.Equal("err", (string?)ControlHooks.PostToolUseContext("err")["hookSpecificOutput"]!["additionalContext"]);
    }
}

public class VsBridgeMcpServerTests
{
    private static JsonObject Msg(string json) => (JsonObject)JsonNode.Parse(json)!;

    private static Task<string> NoBridge(string spec) => throw new InvalidOperationException("not expected");

    [Fact]
    public void Config_declares_an_sdk_server()
    {
        var cfg = JsonNode.Parse(VsBridgeMcpServer.McpConfigJson)!;
        Assert.Equal("sdk", (string?)cfg["mcpServers"]![VsBridgeMcpServer.ServerName]!["type"]);
    }

    [Fact]
    public async Task Initialize_echoes_the_protocol_version()
    {
        var r = await VsBridgeMcpServer.HandleAsync(
            Msg("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}"""), null, NoBridge);
        Assert.Equal(1, (int)r["id"]!);
        Assert.Equal("2025-06-18", (string?)r["result"]!["protocolVersion"]);
        Assert.Equal(VsBridgeMcpServer.ServerName, (string?)r["result"]!["serverInfo"]!["name"]);
    }

    [Fact]
    public async Task Notifications_get_an_empty_result()
    {
        var r = await VsBridgeMcpServer.HandleAsync(
            Msg("""{"jsonrpc":"2.0","method":"notifications/initialized"}"""), null, NoBridge);
        Assert.NotNull(r["result"]);
        Assert.Null(r["error"]);
    }

    [Fact]
    public async Task Lists_both_tools()
    {
        var r = await VsBridgeMcpServer.HandleAsync(Msg("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}"""), null, NoBridge);
        var names = r["result"]!["tools"]!.AsArray().Select(t => (string?)t!["name"]).ToArray();
        Assert.Equal(new[] { "vs_build", "vs_test" }, names);
    }

    [Fact]
    public async Task Build_call_runs_an_encoded_spec_with_the_session_directory()
    {
        string? seen = null;
        var r = await VsBridgeMcpServer.HandleAsync(
            Msg("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"vs_build","arguments":{"action":"rebuild","projects":["A"," "]}}}"""),
            "C:\\sln",
            spec => { seen = spec; return Task.FromResult("Rebuild of project A [Debug] succeeded (0 warning(s))."); });

        Assert.True(VsBridge.TryDecode(seen, out VsBuildSpec build));
        Assert.Equal("rebuild", build.Action);
        Assert.Equal(new[] { "A" }, build.Projects);
        Assert.Equal("C:\\sln", build.WorkingDirectory);
        Assert.False((bool)r["result"]!["isError"]!);
    }

    [Fact]
    public async Task Test_call_reports_failures_as_tool_errors()
    {
        string? seen = null;
        var r = await VsBridgeMcpServer.HandleAsync(
            Msg("""{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"vs_test","arguments":{"filter":"Name=X","build":false}}}"""),
            "C:\\sln",
            spec => { seen = spec; return Task.FromResult("Tests failed: 1 passed, 1 failed, 0 skipped, 1.0s."); });

        Assert.True(VsBridge.TryDecode(seen, out VsTestSpec test));
        Assert.Equal("Name=X", test.Filter);
        Assert.False(test.Build);
        Assert.True((bool)r["result"]!["isError"]!);
    }

    [Fact]
    public async Task Empty_bridge_result_is_an_error_with_a_hint()
    {
        var r = await VsBridgeMcpServer.HandleAsync(
            Msg("""{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"vs_build","arguments":{}}}"""),
            null, _ => Task.FromResult(""));
        Assert.True((bool)r["result"]!["isError"]!);
        Assert.Contains("did not return a result", (string?)r["result"]!["content"]![0]!["text"]);
    }

    [Fact]
    public async Task Invalid_action_is_rejected_without_running()
    {
        var r = await VsBridgeMcpServer.HandleAsync(
            Msg("""{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"vs_build","arguments":{"action":"publish"}}}"""),
            null, NoBridge);
        Assert.True((bool)r["result"]!["isError"]!);
    }

    [Fact]
    public async Task Unknown_method_is_a_json_rpc_error()
    {
        var r = await VsBridgeMcpServer.HandleAsync(Msg("""{"jsonrpc":"2.0","id":7,"method":"resources/list"}"""), null, NoBridge);
        Assert.Equal(-32601, (int)r["error"]!["code"]!);
    }
}
