using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ClaudeStudioShared;

namespace ClaudeStudioAgent;

// Decides PreToolUse permission (rules, session allows, the UI modal), fetches
// post-edit diagnostics and runs Visual Studio bridge requests (vs_build,
// vs_test). claude reaches it through its stdio control channel — hook_callback
// and mcp_message control_requests that ClaudeSession dispatches here — so no
// helper process or named pipe is involved. Talks to the extension by writing
// ChatChunk lines (permission_request, diagnostics_request) to our stdout; the
// answers come back through Respond / RespondDiagnostics.
internal sealed class PermissionBroker : IAsyncDisposable
{
    // 0 = wait for the answer, bounded by HardCeiling (the "wait (max 1 hour)"
    // option); a positive value denies after that many minutes. Swapped per chat
    // request like _rules, so changing it in the UI applies on the next send
    // without a respawn.
    private volatile int _timeoutMinutes;

    /// <summary>
    /// Longest this server will wait for an answer, even under the "wait" setting.
    /// </summary>
    /// <remarks>
    /// "Wait for answer" is about the user taking their time; it is not a promise to
    /// block forever on a prompt nobody can see. If the card never renders — the
    /// webview reloaded, the tool window was closed without cancelling, the dispatcher
    /// call threw — claude stays parked inside PreToolUse for the rest of the session.
    /// The old three-minute clock papered over all of those; this restores a floor
    /// without touching what the setting means.
    /// <para>
    /// It used to be a security bound too: a PreToolUse *command* hook that hits its
    /// timeout does not block the tool, so under bypassPermissions it ran unapproved.
    /// Callback hooks fail closed instead (probed on 2.1.309: "The tool call was not
    /// executed"), but the ceiling stays strictly below <see cref="HookTimeoutSeconds"/>
    /// so the user always sees a deny with a reason, not a hook timeout.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan HardCeiling = TimeSpan.FromHours(1);

    /// <summary>
    /// The `timeout` (seconds) registered for the PreToolUse callback, so the hook
    /// outlives the longest wait this broker can perform (the CLI default is 600s).
    /// Kept above <see cref="HardCeiling"/> by a margin so the ceiling always fires first.
    /// </summary>
    public static int HookTimeoutSeconds => (int)HardCeiling.TotalSeconds + 300;

    public void SetPermissionTimeout(int minutes) => _timeoutMinutes = minutes > 0 ? minutes : 0;

    // Diagnostics requests time out faster than permission — they're a courtesy,
    // never worth stalling the turn for long.
    private static readonly TimeSpan DiagnosticsTimeout = TimeSpan.FromSeconds(8);

    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<Decision>> _pending = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pendingDiag = new();
    private readonly HashSet<string> _sessionAllowed = new();
    private readonly object _sessionAllowedLock = new();

    public bool Respond(string toolUseId, bool allow, string? reason)
    {
        if (_pending.TryGetValue(toolUseId, out var tcs))
            return tcs.TrySetResult(new Decision(allow, reason));
        return false;
    }

    public bool RespondDiagnostics(string requestId, string text)
    {
        if (_pendingDiag.TryGetValue(requestId, out var tcs))
            return tcs.TrySetResult(text ?? "");
        return false;
    }

    // Deny every wait still blocking a hook (turn canceled). claude sits inside
    // PreToolUse until the callback is answered — disposing the session while it
    // was blocked left the process ignoring stdin-close and the cancel wedged
    // (rodada 12). Entries are removed by each handler's finally, not here.
    public void FailPending(string reason)
    {
        foreach (var kvp in _pending)
            kvp.Value.TrySetResult(new Decision(false, reason, Auto: true));
        foreach (var kvp in _pendingDiag)
            kvp.Value.TrySetResult("");
    }

    public void AllowForSession(string toolName)
    {
        lock (_sessionAllowedLock) _sessionAllowed.Add(toolName);
    }

    private bool IsSessionAllowed(string toolName)
    {
        lock (_sessionAllowedLock) return _sessionAllowed.Contains(toolName);
    }

    // ── V6 permission rules ────────────────────────────────────────────────
    // Claude-style rule strings ("Bash(git *)", "Read") evaluated before the
    // modal round-trip. Snapshot is swapped whole on every chat request, so
    // reads need no lock.
    private sealed record PermRule(string Tool, string? Spec, string Raw);
    private sealed record RuleSet(List<PermRule> Allow, List<PermRule> Ask, List<PermRule> Deny);
    private volatile RuleSet? _rules;

    private enum RuleVerdict { None, Allow, Ask, Deny }

    public void SetRules(MergedPermissionRules? settings)
    {
        if (settings == null ||
            ((settings.Allow?.Count ?? 0) == 0 &&
             (settings.Ask?.Count ?? 0) == 0 &&
             (settings.Deny?.Count ?? 0) == 0))
        {
            _rules = null;
            return;
        }

        static List<PermRule> Parse(List<string>? raw, string bucket)
        {
            var list = new List<PermRule>();
            foreach (var r in raw ?? [])
            {
                // Hyphens are legal in MCP server names, so `mcp__github-tools__search`
                // has to parse — the stricter pattern dropped exactly the rules the
                // settings-file merge was written to honour, and dropped them silently.
                var m = Regex.Match(r.Trim(), @"^([A-Za-z_][A-Za-z0-9_.-]*)\s*(?:\((.*)\))?$", RegexOptions.Singleline);
                if (m.Success)
                {
                    list.Add(new PermRule(m.Groups[1].Value, m.Groups[2].Success ? m.Groups[2].Value.Trim() : null, r.Trim()));
                }
                else
                {
                    // A rule that cannot be parsed does nothing, and a `deny` the user
                    // believes is protecting them doing nothing is worth a line. The UI
                    // rejects malformed rules as they are typed; rules coming from a
                    // settings file have no such gate, so this is their only feedback.
                    EmitWarn($"ignoring malformed permission rule in {bucket}: {r.Trim()}");
                }
            }
            return list;
        }

        _rules = new RuleSet(
            Parse(settings.Allow, "allow"),
            Parse(settings.Ask, "ask"),
            Parse(settings.Deny, "deny"));
    }

    private (RuleVerdict verdict, string? rule) EvaluateRules(string toolName, string? toolInputJson)
    {
        var rules = _rules;
        if (rules == null) return (RuleVerdict.None, null);

        var input = ExtractPrimaryInput(toolInputJson);
        string? Match(List<PermRule> bucket)
        {
            foreach (var r in bucket)
            {
                if (!SettingsPermissions.ToolMatches(r.Tool, toolName)) continue;
                if (r.Spec is null || r.Spec.Length == 0 || SpecMatches(r.Spec, input)) return r.Raw;
            }
            return null;
        }

        if (Match(rules.Deny) is { } d) return (RuleVerdict.Deny, d);
        if (Match(rules.Ask) is { } a) return (RuleVerdict.Ask, a);
        if (Match(rules.Allow) is { } al) return (RuleVerdict.Allow, al);
        return (RuleVerdict.None, null);
    }

    // The value a rule specifier is matched against, per tool shape: the first
    // present of command (Bash) / file_path (Edit|Write|Read) / path / url
    // (WebFetch) / pattern (Glob|Grep).
    private static string? ExtractPrimaryInput(string? toolInputJson)
    {
        if (string.IsNullOrEmpty(toolInputJson)) return null;
        try
        {
            var el = JsonSerializer.Deserialize<JsonElement>(toolInputJson!);
            if (el.ValueKind != JsonValueKind.Object) return null;
            foreach (var key in new[] { "command", "file_path", "path", "url", "pattern" })
                if (el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                    return v.GetString();
        }
        catch { }
        return null;
    }

    // True when the tool call is claude writing/updating its own plan document
    // (plan mode keeps them under <config>/plans/).
    private static readonly string PlansDir = ClaudeConfig.PlansDir;

    private static bool IsPlanFileWrite(string toolName, string? toolInputJson)
    {
        if (toolName is not ("Write" or "Edit" or "MultiEdit" or "NotebookEdit")) return false;
        var filePath = ExtractPrimaryInput(toolInputJson);
        if (string.IsNullOrEmpty(filePath)) return false;
        try
        {
            var full = Path.GetFullPath(filePath!);
            var plans = Path.GetFullPath(PlansDir);
            return full.StartsWith(plans + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static bool SpecMatches(string spec, string? value)
    {
        if (value == null) return false;
        // Claude's Bash prefix idiom: "npm run test:*" matches "npm run test" + anything.
        if (spec.EndsWith(":*", StringComparison.Ordinal))
        {
            var prefix = spec.Substring(0, spec.Length - 2);
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        }
        if (!spec.Contains('*'))
            return string.Equals(spec, value, StringComparison.OrdinalIgnoreCase);
        var pattern = "^" + Regex.Escape(spec).Replace("\\*", ".*") + "$";
        return Regex.IsMatch(value, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
    }

    // PreToolUse (hook_callback). Rule evaluation first (deny > ask > allow), then
    // the session allowlist short-circuit, then the UI modal. An ask-rule match
    // forces the modal even when the tool was session-allowed or has an allow rule.
    // Returns the callback output: Continue (claude's normal flow, which under
    // bypassPermissions runs the tool) or a deny with the reason claude sees.
    public async Task<JsonObject> DecidePreToolUseAsync(ControlHooks.HookCall call)
    {
        var ct = _cts.Token;
        var toolUseId = call.ToolUseId;
        var toolName = call.ToolName;
        var toolInput = call.ToolInputJson;
        var cwd = call.Cwd;

        if (string.IsNullOrEmpty(toolUseId) || string.IsNullOrEmpty(toolName))
        {
            EmitError("hook: callback missing tool_use_id or tool_name");
            return ControlHooks.DenyPreToolUse("Permission denied: malformed permission request");
        }

        try
        {
            var (verdict, matchedRule) = EvaluateRules(toolName!, toolInput);

            // A decision taken here never reaches the UI, so without a line in the
            // log an auto-approval is indistinguishable from "the tool just ran"
            // — which is exactly why 44.5 took a whole round to pin down
            // (rodada 16, 48.3).
            void LogAuto(string what) => EmitChunk(new ChatChunk
            {
                Type = "permission_auto",
                Tool = toolName,
                Text = what
            });

            // The VS bridge tools (vs_build, vs_test) go through the normal flow on
            // purpose: they run project build targets and test code inside Visual
            // Studio, outside any sandbox claude itself runs in.
            Decision decision;
            if (IsPlanFileWrite(toolName!, toolInput))
            {
                // Plan mode saves its plan document to ~/.claude/plans/<slug>.md
                // through the Write tool — CLI bookkeeping, not a workspace edit.
                // Without this bypass every plan-mode turn pops a scary Write
                // modal for the plan file (validation round 2026-07-16).
                decision = new Decision(true, null);
                LogAuto("allowed: plan-file write");
            }
            else if (verdict == RuleVerdict.Deny)
            {
                decision = new Decision(false, $"Denied by permission rule: {matchedRule}");
                LogAuto($"denied by rule: {matchedRule}");
            }
            else if (verdict == RuleVerdict.Allow)
            {
                decision = new Decision(true, null);
                LogAuto($"allowed by rule: {matchedRule}");
            }
            else if (verdict != RuleVerdict.Ask && IsSessionAllowed(toolName!))
            {
                decision = new Decision(true, null);
                LogAuto("allowed for session");
            }
            else
            {
                if (verdict == RuleVerdict.Ask)
                    LogAuto($"prompting: forced by ask rule: {matchedRule}");
                decision = await PromptAsync(toolUseId!, toolName!, toolInput, cwd, ct);
            }

            // Nobody clicked: retract the card before claude moves on, so it can
            // never be answered into a tool_use_id that no longer exists.
            if (decision.Auto)
            {
                EmitChunk(new ChatChunk
                {
                    Type = "permission_resolved",
                    ToolId = toolUseId,
                    Text = decision.Reason ?? "Resolved without an answer"
                });
            }

            // An approved plain shell build runs in VS instead (Output window, Error
            // List) and its result goes back to claude as the denial reason, so the
            // shell build never runs. Only after approval: the build executes
            // project targets outside the sandbox, so it must not skip the prompt
            // the shell command gets. If VS cannot take it, the command runs as
            // approved.
            if (decision.Allow && toolName is "Bash" or "PowerShell"
                && BuildCommandParser.TryParse(ExtractCommand(toolInput), cwd, out var buildSpec))
            {
                var text = await RunBridgeAsync(toolUseId!, VsBridge.Encode(buildSpec));
                if (!string.IsNullOrWhiteSpace(text))
                    decision = new Decision(false, "The shell build was not run. Visual Studio ran it instead " +
                                                   "(visible in its Output window). Result:\n" + text);
            }

            if (decision.Allow) return ControlHooks.Continue();
            return ControlHooks.DenyPreToolUse(!string.IsNullOrEmpty(decision.Reason)
                ? $"Permission denied: {decision.Reason}"
                : "Permission denied by user");
        }
        catch (Exception ex)
        {
            EmitError($"permission handler error: {ex.Message}");
            return ControlHooks.DenyPreToolUse($"Permission denied: {ex.Message}");
        }
    }

    // Shows the modal and waits for Respond, bounded by the user's timeout and the
    // ceiling. Cancel and shutdown release the wait through ct.
    private async Task<Decision> PromptAsync(string toolUseId, string toolName, string? toolInput, string? cwd, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<Decision>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(toolUseId, tcs))
        {
            EmitError($"hook: duplicate tool_use_id {toolUseId}");
            return new Decision(false, "duplicate permission request", Auto: true);
        }

        try
        {
            EmitChunk(new ChatChunk
            {
                Type = "permission_request",
                Tool = toolName,
                ToolInput = toolInput,
                ToolId = toolUseId,
                Cwd = cwd
            });

            int minutes = _timeoutMinutes;
            var wait = minutes > 0 ? TimeSpan.FromMinutes(minutes) : HardCeiling;

            // Linked source so the timer is disposed the moment the user answers:
            // a bare Task.Delay(…, ct) leaves its registration on the token until
            // the broker shuts down, and at one per prompt a long session
            // accumulates them.
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var timeoutTask = Task.Delay(wait, timer.Token);

            var completed = await Task.WhenAny(tcs.Task, timeoutTask);
            timer.Cancel();

            if (completed == tcs.Task)
                return await tcs.Task;
            if (ct.IsCancellationRequested)
                return new Decision(false, "Agent shutting down", Auto: true);
            if (minutes <= 0)
            {
                // Only reachable through the ceiling, so say so rather than
                // reporting a timeout the user never configured.
                return new Decision(
                    false,
                    $"No answer after {HardCeiling.TotalHours:F0}h — the prompt may never have appeared",
                    Auto: true);
            }
            return new Decision(false, $"Permission timed out ({minutes}min)", Auto: true);
        }
        catch (OperationCanceledException)
        {
            return new Decision(false, "Agent shutting down", Auto: true);
        }
        finally
        {
            _pending.TryRemove(toolUseId, out _);
        }
    }

    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(45);

    private static string? ExtractCommand(string? toolInput)
    {
        if (string.IsNullOrEmpty(toolInput)) return null;
        try
        {
            using var doc = JsonDocument.Parse(toolInput!);
            return doc.RootElement.TryGetProperty("command", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString()
                : null;
        }
        catch { return null; }
    }

    // Asks the extension to run a VsBridge request (build or test) inside Visual
    // Studio and waits for the result text. Empty = VS gave no result.
    public async Task<string> RunBridgeAsync(string requestId, string spec)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingDiag.TryAdd(requestId, tcs)) return "";

        var timeout = spec.StartsWith(VsBridge.TestMarker, StringComparison.Ordinal) ? TestTimeout : BuildTimeout;
        string text = "";
        try
        {
            EmitChunk(new ChatChunk { Type = "diagnostics_request", ToolId = requestId, Text = spec });
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeout, _cts.Token));
            if (completed == tcs.Task) text = await tcs.Task;
        }
        catch (OperationCanceledException) { }
        finally { _pendingDiag.TryRemove(requestId, out _); }
        return text;
    }

    // PostToolUse (hook_callback): the VS Error List entries of the edited file, or
    // "" when there are none or VS did not answer in time.
    public async Task<string> GetDiagnosticsAsync(string? requestId, string? filePath)
    {
        if (string.IsNullOrEmpty(requestId) || string.IsNullOrEmpty(filePath)) return "";

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingDiag.TryAdd(requestId!, tcs)) return "";
        try
        {
            // Ask the extension to read the VS Error List for this file.
            EmitChunk(new ChatChunk
            {
                Type = "diagnostics_request",
                ToolId = requestId,
                Text = filePath
            });

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(DiagnosticsTimeout, _cts.Token));
            return completed == tcs.Task ? await tcs.Task : "";
        }
        catch (OperationCanceledException) { return ""; }
        finally { _pendingDiag.TryRemove(requestId!, out _); }
    }

    private static void EmitChunk(ChatChunk chunk)
    {
        Console.WriteLine(JsonSerializer.Serialize(chunk));
        Console.Out.Flush();
    }

    private static void EmitError(string text)
    {
        Console.WriteLine(JsonSerializer.Serialize(new ChatChunk { Type = "error", Text = text }));
        Console.Out.Flush();
    }

    /// <summary>Goes to the Output window only — never into the chat.</summary>
    private static void EmitWarn(string text)
    {
        Console.WriteLine(JsonSerializer.Serialize(new ChatChunk { Type = "warn", Text = text }));
        Console.Out.Flush();
    }

    public ValueTask DisposeAsync()
    {
        try { _cts.Cancel(); } catch { }
        foreach (var kvp in _pending)
            kvp.Value.TrySetCanceled();
        _pending.Clear();
        foreach (var kvp in _pendingDiag)
            kvp.Value.TrySetResult("");
        _pendingDiag.Clear();
        _cts.Dispose();
        return default;
    }

    // Auto marks a decision nobody clicked — timeout, cancel, shutdown. The card
    // is still on screen at that point, so the UI has to be told, or the user
    // answers a request the agent already closed and gets "no pending permission
    // request for tool_use_id" on their next message (rodada 15, item 43.5).
    internal readonly record struct Decision(bool Allow, string? Reason, bool Auto = false);
}
