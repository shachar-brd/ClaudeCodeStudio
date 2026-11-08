using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeStudioShared;

// Hooks registered over claude's stdio control channel (the Agent SDK protocol)
// instead of as command hooks in settings.json. The agent sends them in the
// `initialize` control_request; claude then asks with
// control_request{subtype:"hook_callback", callback_id} and blocks on our
// control_response. No helper process is spawned per tool call, and nothing has
// to reach a Windows named pipe, so the same wiring works with claude running
// somewhere else (e.g. over ssh).
//
// Probed against CLI 2.1.309 (scripts/remote-claude/probe-control-channel.js):
// PreToolUse fires for Bash/Write/MCP tools and not for Read; a deny output
// blocks the tool and its reason reaches the model; PostToolUse
// additionalContext reaches the model verbatim; a callback that misses its
// timeout fails CLOSED ("The tool call was not executed"), unlike a command
// hook, which lets the tool run.
public static class ControlHooks
{
    public const string PreToolUseCallbackId = "claudestudio-pre";
    public const string PostToolUseCallbackId = "claudestudio-post";

    // FAIL-CLOSED: gate everything except tools that only read.
    //
    // The old matcher listed the dangerous tools by name, so every tool the CLI
    // added later ran with NO gate at all — in ask/plan mode we spawn under
    // bypassPermissions, so a name this regex misses is simply allowed and no
    // modal ever appears. That is how PowerShell, the default shell tool on
    // Windows since CLI 2.1.226, ended up running "git status" unprompted
    // (rodada 17).
    //
    // Inverting it means a new tool defaults to *asking*, which is recoverable
    // (allow for the session, or add a permission rule) instead of silent. The
    // exclusions are read-only or conversation-local: Read/Glob/Grep/NotebookRead
    // inspect, ToolSearch loads a schema, TodoWrite writes the task list,
    // BashOutput reads an existing shell's buffer, WebSearch returns results
    // (WebFetch, which pulls a URL, is deliberately NOT here).
    //
    // Negative lookahead verified against CLI 2.1.226 (rodada 17) and again for
    // callback hooks on 2.1.309: the hook fires for a shell command and does not
    // fire for Read. If a future CLI stops honouring it the regex matches nothing
    // and NOTHING is gated, so re-run the probe when bumping the CLI.
    //
    // AskUserQuestion and ExitPlanMode are excluded because they are already
    // gated on the control channel — the question card and the "Approve plan?"
    // modal. Neither executes anything, so this hook has nothing to protect, and
    // gating them here too shows the same plan for approval twice.
    public const string PreToolUseMatcher =
        "^(?!Read$|Glob$|Grep$|NotebookRead$|ToolSearch$|TodoWrite$|BashOutput$|WebSearch$|AskUserQuestion$|ExitPlanMode$).*";

    // After a file edit, the VS Error List for that file goes back to claude.
    public const string PostToolUseMatcher = "Edit|Write|MultiEdit|NotebookEdit";

    // The `initialize` control_request body. preToolUseTimeoutSeconds must outlive
    // the longest the permission modal can wait (see PermissionBroker). Without
    // hooks (yolo mode, which only needs the control channel for the MCP server)
    // the request registers nothing.
    public static JsonObject BuildInitializeRequest(int preToolUseTimeoutSeconds, bool includeHooks = true) =>
        includeHooks
            ? new JsonObject { ["subtype"] = "initialize", ["hooks"] = BuildHooks(preToolUseTimeoutSeconds) }
            : new JsonObject { ["subtype"] = "initialize" };

    private static JsonObject BuildHooks(int preToolUseTimeoutSeconds) => new()
    {
        ["PreToolUse"] = new JsonArray(new JsonObject
        {
            ["matcher"] = PreToolUseMatcher,
            ["hookCallbackIds"] = new JsonArray(PreToolUseCallbackId),
            ["timeout"] = preToolUseTimeoutSeconds
        }),
        ["PostToolUse"] = new JsonArray(new JsonObject
        {
            ["matcher"] = PostToolUseMatcher,
            ["hookCallbackIds"] = new JsonArray(PostToolUseCallbackId)
        })
    };

    public sealed class HookCall
    {
        public string CallbackId = "";
        public string? ToolName;
        public string? ToolUseId;
        public string? ToolInputJson;
        public string? Cwd;
        public string? FilePath;
    }

    // Reads the `request` object of a hook_callback control_request.
    public static HookCall ParseHookCallback(JsonElement request)
    {
        static string? Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;

        var call = new HookCall { CallbackId = Str(request, "callback_id") ?? "" };
        if (request.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object)
        {
            call.ToolName = Str(input, "tool_name");
            call.ToolUseId = Str(input, "tool_use_id");
            call.Cwd = Str(input, "cwd");
            if (input.TryGetProperty("tool_input", out var ti))
            {
                call.ToolInputJson = ti.GetRawText();
                call.FilePath = Str(ti, "file_path");
            }
        }
        call.ToolUseId ??= Str(request, "tool_use_id");
        return call;
    }

    // No opinion: claude continues with its normal permission flow (under
    // bypassPermissions the tool runs). Same as a command hook exiting 0.
    public static JsonObject Continue() => new();

    public static JsonObject DenyPreToolUse(string reason) => new()
    {
        ["hookSpecificOutput"] = new JsonObject
        {
            ["hookEventName"] = "PreToolUse",
            ["permissionDecision"] = "deny",
            ["permissionDecisionReason"] = reason
        }
    };

    public static JsonObject PostToolUseContext(string? additionalContext) =>
        string.IsNullOrWhiteSpace(additionalContext)
            ? new JsonObject()
            : new JsonObject
            {
                ["hookSpecificOutput"] = new JsonObject
                {
                    ["hookEventName"] = "PostToolUse",
                    ["additionalContext"] = additionalContext
                }
            };
}
