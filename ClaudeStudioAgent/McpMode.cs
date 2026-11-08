using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeStudioShared;

namespace ClaudeStudioAgent;

// Minimal MCP stdio server (newline-delimited JSON-RPC) that claude spawns via
// --mcp-config. Exposes vs_build and vs_test, which ask the agent's pipe server to
// run the build or the tests inside the live Visual Studio instance.
internal static class McpMode
{
    public const string ServerName = "claudestudio-vs";

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

    public static async Task<int> RunAsync(string pipeName)
    {
        using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

        string? line;
        while ((line = await stdin.ReadLineAsync()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonNode? msg;
            try { msg = JsonNode.Parse(line); } catch { continue; }
            if (msg is not JsonObject obj) continue;

            var method = obj["method"]?.GetValue<string>();
            var id = obj["id"]?.DeepClone();
            if (id == null) continue; // notification

            JsonNode? result = null;
            JsonObject? error = null;
            switch (method)
            {
                case "initialize":
                    result = new JsonObject
                    {
                        ["protocolVersion"] = obj["params"]?["protocolVersion"]?.GetValue<string>() ?? "2024-11-05",
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                        ["serverInfo"] = new JsonObject { ["name"] = ServerName, ["version"] = "1.1.0" },
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
                    result = await CallToolAsync(pipeName, obj["params"] as JsonObject);
                    break;
                default:
                    error = new JsonObject { ["code"] = -32601, ["message"] = $"Method not found: {method}" };
                    break;
            }

            var resp = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id };
            if (error != null) resp["error"] = error; else resp["result"] = result;
            await stdout.WriteLineAsync(resp.ToJsonString());
        }
        return 0;
    }

    private static async Task<JsonNode> CallToolAsync(string pipeName, JsonObject? p)
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
        switch (p?["name"]?.GetValue<string>())
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
                    WorkingDirectory = Environment.CurrentDirectory
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
                    WorkingDirectory = Environment.CurrentDirectory
                });
                test = true;
                break;
            }
            default:
                return ToolResult("Unknown tool.", true);
        }

        try
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(5000);
            var req = new { type = "build_request", request_id = Guid.NewGuid().ToString("N"), spec };
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(req) + "\n");
            await pipe.WriteAsync(bytes);
            await pipe.FlushAsync();

            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            var line = await reader.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(line)) return ToolResult("No response from Visual Studio bridge.", true);
            var text = JsonDocument.Parse(line).RootElement.GetProperty("text").GetString() ?? "";
            return ToolResult(text, !IsSuccess(text, test));
        }
        catch (Exception ex)
        {
            return ToolResult($"Visual Studio bridge unavailable: {ex.Message}", true);
        }
    }

    // The extension's success summaries start with these; anything else (errors,
    // failed tests, "not found") is reported as a tool error.
    private static bool IsSuccess(string text, bool test) => test
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
