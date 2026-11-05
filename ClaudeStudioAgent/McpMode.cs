using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeStudioAgent;

// Minimal MCP stdio server (newline-delimited JSON-RPC) that claude spawns via
// --mcp-config. Exposes one tool, vs_build, which asks the agent's pipe server to
// run the build inside the live Visual Studio instance.
internal static class McpMode
{
    public const string ServerName = "claudestudio-vs";

    private const string Instructions =
        "This session runs inside Visual Studio. To build, rebuild or clean the open solution or a project, " +
        "ALWAYS use the vs_build tool. It runs in the IDE (output visible in the VS Output window) and returns " +
        "the Error List. Do not locate or call msbuild/dotnet build through the shell for the solution the IDE has open.";

    private static readonly JsonObject ToolSchema = new()
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
            ["scope"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray("solution", "project"),
                ["description"] = "solution (default) or a single project."
            },
            ["project"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Project name (required when scope is project), e.g. MyApp.Core."
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
                        ["serverInfo"] = new JsonObject { ["name"] = ServerName, ["version"] = "1.0.0" },
                        ["instructions"] = Instructions
                    };
                    break;
                case "ping":
                    result = new JsonObject();
                    break;
                case "tools/list":
                    result = new JsonObject
                    {
                        ["tools"] = new JsonArray(new JsonObject
                        {
                            ["name"] = "vs_build",
                            ["description"] = "Build, rebuild or clean the solution or a project in the running Visual Studio IDE " +
                                              "and return success/failure plus Error List errors and warnings. Prefer this over msbuild/dotnet build in the shell.",
                            ["inputSchema"] = ToolSchema.DeepClone()
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
        string Arg(string n) => p?["arguments"]?[n]?.GetValue<string>() ?? "";
        if (p?["name"]?.GetValue<string>() != "vs_build")
            return ToolResult("Unknown tool.", true);

        var action = Arg("action").ToLowerInvariant();
        if (action == "") action = "build";
        var scope = Arg("scope").ToLowerInvariant();
        var project = Arg("project");
        if (scope == "") scope = project != "" ? "project" : "solution";
        if (action is not ("build" or "rebuild" or "clean") || scope is not ("solution" or "project"))
            return ToolResult("Invalid action or scope.", true);
        if (scope == "project" && project == "")
            return ToolResult("Project name required when scope is project.", true);

        try
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(5000);
            var req = new { type = "build_request", request_id = Guid.NewGuid().ToString("N"), action, scope, project };
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(req) + "\n");
            await pipe.WriteAsync(bytes);
            await pipe.FlushAsync();

            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            var line = await reader.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(line)) return ToolResult("No response from Visual Studio bridge.", true);
            var text = JsonDocument.Parse(line).RootElement.GetProperty("text").GetString() ?? "";
            var isError = text.Contains("error(s)") && !text.StartsWith("Build succeeded");
            return ToolResult(text, isError);
        }
        catch (Exception ex)
        {
            return ToolResult($"Visual Studio bridge unavailable: {ex.Message}", true);
        }
    }

    private static JsonNode ToolResult(string text, bool isError) => new JsonObject
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = isError
    };
}
