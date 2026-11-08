using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace ClaudeStudioShared;

// The Visual Studio bridge MCP server (vs_build, vs_test), transport-free. claude
// declares it as an "sdk" server (--mcp-config {"type":"sdk"}) and tunnels the
// MCP JSON-RPC messages over its stdio control channel as
// control_request{subtype:"mcp_message"}; the agent hands each message here and
// returns the reply as the control_response's mcp_response.
public static class VsBridgeMcpServer
{
    public const string ServerName = "claudestudio-vs";

    // Passed to claude with --mcp-config: an in-process server, no command to spawn.
    public static string McpConfigJson =>
        new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                [ServerName] = new JsonObject { ["type"] = "sdk", ["name"] = ServerName }
            }
        }.ToJsonString();

    private const string Instructions =
        "This session runs inside Visual Studio. To build, rebuild or clean the open solution or some of its projects, " +
        "ALWAYS use the vs_build tool. To run unit tests, ALWAYS use the vs_test tool. Both run in the IDE (output " +
        "visible in the VS Output window) and return a summary. Do not call msbuild, dotnet build or dotnet test " +
        "through the shell for the solution the IDE has open.";

    private static JsonObject ProjectsProperty(string description) => new()
    {
        ["type"] = "array",
        ["items"] = new JsonObject { ["type"] = "string" },
        ["description"] = description
    };

    private static readonly JsonObject BuildSchema = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["action"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray("build", "rebuild", "clean"),
                ["description"] = "build (default), rebuild (clean then build) or clean."
            },
            ["projects"] = ProjectsProperty(
                "Projects to act on, by name (e.g. MyApp.Core) or project file path. Omit for the whole solution."),
            ["configuration"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Solution configuration for project builds, e.g. Release. Default: the IDE's active configuration."
            }
        }
    };

    private static readonly JsonObject TestSchema = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["projects"] = ProjectsProperty(
                "Test projects to run, by name or project file path. Omit to run every test project in the solution."),
            ["filter"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "dotnet test --filter expression, e.g. FullyQualifiedName~ParserTests or Name=MyTest."
            },
            ["build"] = new JsonObject
            {
                ["type"] = "boolean",
                ["description"] = "Build the test projects in VS first (default true). Set false only if they were just built."
            }
        }
    };

    // Handles one MCP JSON-RPC message. runBridge runs an encoded VsBridge spec in
    // Visual Studio and returns its summary ("" = VS gave no result).
    // workingDirectory is the session's, the base for relative project paths.
    // Notifications (no id) get an empty result: the control channel still
    // expects an answer for every mcp_message.
    public static async Task<JsonObject> HandleAsync(
        JsonObject message, string? workingDirectory, Func<string, Task<string>> runBridge)
    {
        var method = message["method"] is JsonValue m && m.TryGetValue<string>(out var s) ? s : null;
        var id = message["id"]?.DeepClone();
        if (id == null)
            return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 0, ["result"] = new JsonObject() };

        JsonNode? result = null;
        JsonObject? error = null;
        switch (method)
        {
            case "initialize":
                result = new JsonObject
                {
                    ["protocolVersion"] = message["params"]?["protocolVersion"] is JsonValue pv &&
                                          pv.TryGetValue<string>(out var v) ? v : "2024-11-05",
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = ServerName, ["version"] = "1.2.0" },
                    ["instructions"] = Instructions
                };
                break;
            case "ping":
                result = new JsonObject();
                break;
            case "tools/list":
                result = new JsonObject
                {
                    ["tools"] = new JsonArray(
                        new JsonObject
                        {
                            ["name"] = "vs_build",
                            ["description"] = "Build, rebuild or clean the solution or specific projects in the running Visual Studio IDE " +
                                              "and return success/failure plus Error List errors and warnings. Prefer this over msbuild/dotnet build in the shell.",
                            ["inputSchema"] = BuildSchema.DeepClone()
                        },
                        new JsonObject
                        {
                            ["name"] = "vs_test",
                            ["description"] = "Run unit tests of the open solution from Visual Studio (builds in the IDE first, output in the " +
                                              "VS Output window) and return pass/fail counts plus failing tests with messages. Prefer this over dotnet test in the shell.",
                            ["inputSchema"] = TestSchema.DeepClone()
                        })
                };
                break;
            case "tools/call":
                result = await CallToolAsync(message["params"] as JsonObject, workingDirectory, runBridge);
                break;
            default:
                error = new JsonObject { ["code"] = -32601, ["message"] = $"Method not found: {method}" };
                break;
        }

        var resp = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id };
        if (error != null) resp["error"] = error; else resp["result"] = result;
        return resp;
    }

    private static async Task<JsonNode> CallToolAsync(
        JsonObject? p, string? workingDirectory, Func<string, Task<string>> runBridge)
    {
        var args = p?["arguments"] as JsonObject;
        string Arg(string n) => args?[n] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "";

        List<string> Projects()
        {
            var list = new List<string>();
            if (args?["projects"] is JsonArray arr)
                foreach (var n in arr)
                    if (n is JsonValue v && v.TryGetValue<string>(out var s) && s.Trim() != "") list.Add(s.Trim());
            // Back-compat with the original single-project argument.
            var single = Arg("project");
            if (single != "" && !list.Contains(single)) list.Add(single);
            return list;
        }

        string spec;
        bool test;
        switch (p?["name"] is JsonValue nv && nv.TryGetValue<string>(out var name) ? name : null)
        {
            case "vs_build":
            {
                var action = Arg("action").ToLowerInvariant();
                if (action == "") action = "build";
                if (action is not ("build" or "rebuild" or "clean"))
                    return ToolResult("Invalid action. Use build, rebuild or clean.", true);
                var projects = Projects();
                if (Arg("scope").Equals("project", StringComparison.OrdinalIgnoreCase) && projects.Count == 0)
                    return ToolResult("Project name required when scope is project.", true);
                var config = Arg("configuration");
                spec = VsBridge.Encode(new VsBuildSpec
                {
                    Action = action,
                    Projects = projects,
                    Configuration = config == "" ? null : config,
                    WorkingDirectory = workingDirectory
                });
                test = false;
                break;
            }
            case "vs_test":
            {
                var filter = Arg("filter");
                var build = args?["build"] is not JsonValue b || !b.TryGetValue<bool>(out var bv) || bv;
                spec = VsBridge.Encode(new VsTestSpec
                {
                    Projects = Projects(),
                    Filter = filter == "" ? null : filter,
                    Build = build,
                    WorkingDirectory = workingDirectory
                });
                test = true;
                break;
            }
            default:
                return ToolResult("Unknown tool.", true);
        }

        string text;
        try { text = await runBridge(spec); }
        catch (Exception ex) { return ToolResult($"Visual Studio bridge unavailable: {ex.Message}", true); }
        if (string.IsNullOrWhiteSpace(text))
            text = "Visual Studio did not return a result. Is a solution open in the IDE with the Claude Code Studio window running?";
        return ToolResult(text, !IsSuccess(text, test));
    }

    // The extension's success summaries start with these; anything else (errors,
    // failed tests, "not found") is reported as a tool error.
    public static bool IsSuccess(string text, bool test) => test
        ? text.StartsWith("Tests passed", StringComparison.Ordinal)
        : text.StartsWith("Clean of ", StringComparison.Ordinal) ||
          ((text.StartsWith("Build of ", StringComparison.Ordinal) || text.StartsWith("Rebuild of ", StringComparison.Ordinal))
           && text.Contains(" succeeded"));

    private static JsonNode ToolResult(string text, bool isError) => new JsonObject
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = isError
    };
}
