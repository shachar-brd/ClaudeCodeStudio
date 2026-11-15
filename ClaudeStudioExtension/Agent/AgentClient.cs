using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClaudeStudioShared;

namespace ClaudeStudioExtension.Agent;

// What the chat does with a turn's chunks (AgentClient.DispatchChunk): the
// callbacks of AskStreamingAsync, and for a turn claude starts by itself
// (AgentClient.BackgroundCallbacks) the moments it starts and ends.
public sealed class TurnCallbacks
{
    public Action<string> OnChunk { get; set; } = _ => { };
    public Action<string>? OnTiming { get; set; }
    public Action<string>? OnTokens { get; set; }
    public Action<string>? OnSession { get; set; }
    public Action<string, string, string?, string?, string?>? OnTool { get; set; }
    public Action<string, string?, string, string?>? OnPermissionRequest { get; set; }
    public Action<string, string>? OnDiagnosticsRequest { get; set; }
    public Action<string>? OnThinking { get; set; }
    public Action<ChatChunk>? OnSubagentEvent { get; set; }
    public Action<ChatChunk>? OnBackgroundTasks { get; set; }
    public Action<string, string>? OnPermissionResolved { get; set; }
    // "steer" when a steering message opened it, else ""
    public Action<string>? OnBackgroundStart { get; set; }
    public Action? OnBackgroundEnd { get; set; }
}

public class AgentClient
{
    private Process? _process;

    private StreamWriter? _writer;

    private StreamReader? _reader;

    private TaskCompletionSource<bool>? _cancelTcs;

    // Serializes AskStreamingAsync calls. The StreamReader owned by this client
    // can't have two concurrent ReadLineAsync — if a new request arrives before
    // the previous turn's read loop has exited (e.g. user clicked cancel and
    // immediately typed a new question), the second call throws "stream in use".
    // The semaphore makes the new call wait for the old one to wind down
    // naturally (cancel triggers a hard cancel on the agent → done → loop exits
    // → semaphore released).
    private readonly SemaphoreSlim _streamingSemaphore = new(1, 1);

    private readonly JsonlWatcher _jsonlWatcher = new();

    public string? PendingResumeSessionId { get; set; }
    // #12: consumed together with PendingResumeSessionId on the next turn —
    // set only by the History "resume as fork" action.
    public bool PendingForkSession { get; set; }
    public string? CurrentSessionId { get; private set; }

    // True while the in-flight/last turn was sent with --resume or --continue
    // (its session forks the previous transcript instead of starting empty).
    public bool LastTurnResumed { get; private set; }

    // #19: true once the live session was born from a resume (History) or a
    // fork/branch. Drives carrying CurrentSessionId forward as the resume id on
    // later turns so a resumed conversation isn't silently restarted after its
    // first reply. Cleared by ResetSession (new chat / solution reset).
    private bool _sessionResumedLineage;

    // U4: fired with claude.exe's PID right after the agent spawns it, so the
    // control can point a FileSystemWatcher at the CLI's presence file
    // (~/.claude/sessions/<pid>.json — carries status/waitingFor).
    public Action<int>? OnClaudePid { get; set; }

    // User-configurable claude settings (V7). Set by the control from the UI;
    // attached to each outbound ChatRequest so the agent writes them into the
    // settings.json it passes to claude.
    public ClaudeStudioShared.ClaudeSettings? ClaudeSettings { get; set; }

    // Explicit claude.exe path (D7). Set by the control from the UI; attached
    // to each outbound ChatRequest so the agent spawns that binary instead of
    // searching PATH (a change respawns claude via the session key).
    public string? CliPath { get; set; }

    public void CancelCurrent()
    {
        OutputLog.Info("request cancel requested");
        _cancelTcs?.TrySetResult(true);
        // ■ during a turn claude started by itself: the same hard cancel; what
        // it still writes until its done is dropped.
        if (_backgroundActive) _backgroundCancelled = true;
        // Hard cancel on the agent side so the read loop unblocks and the next
        // request can start without "stream in use" errors. Fire-and-forget —
        // we don't want to block the UI thread; the agent's response will
        // arrive via the existing read loop and emit `done`.
        _ = Task.Run(async () =>
        {
            try
            {
                if (_writer == null) return;
                var json = JsonSerializer.Serialize(new ChatRequest { Message = "", CancelTurn = true });
                await _writer.WriteLineAsync(json);
                await _writer.FlushAsync();
            }
            catch (Exception ex)
            {
                OutputLog.Warn($"cancel-turn write failed: {ex.Message}");
            }
        });
    }

    // Set when StartAsync replaced a dead agent process; consumed by the next turn so it
    // re-resumes the live session instead of letting the new agent start an empty one.
    private bool _agentRestartedSinceLastTurn;

    public async Task StartAsync()
    {
        if (_process != null)
        {
            // A crashed/killed agent leaves its carcass here — early-returning
            // would hand the caller dead streams (rodada 12). Sweep and respawn.
            bool alive;
            try { alive = !_process.HasExited; } catch { alive = false; }
            if (alive) return;
            OutputLog.Warn("agent process was dead — restarting");
            _agentRestartedSinceLastTurn = true;
            try { _reader?.Dispose(); } catch { }
            try { _writer?.Dispose(); } catch { }
            try { _process.Dispose(); } catch { }
            _process = null;
            _writer = null;
            _reader = null;
        }

        var agentPath = GetAgentPath();
        OutputLog.Info($"starting agent: {agentPath}");

        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = agentPath,

                WorkingDirectory = Path.GetDirectoryName(agentPath),

                RedirectStandardInput = true,
                RedirectStandardOutput = true,

                RedirectStandardError = true,

                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        // Phase 2: enable the PreToolUse hook pipeline so the agent intercepts
        // tool calls under permissionMode == "ask" and routes prompts to the UI.
        _process.StartInfo.EnvironmentVariables["CLAUDESTUDIO_HOOK_ENABLE"] = "1";

        _process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                OutputLog.Warn($"agent stderr: {e.Data}");
        };

        _process.Start();
        _process.BeginErrorReadLine();

        _writer = _process.StandardInput;
        _reader = _process.StandardOutput;

        var ready = await _reader.ReadLineAsync();

        if (ready != "READY")
        {
            OutputLog.Error($"agent failed to signal READY (got: {ready ?? "NULL"})");
            throw new Exception(
                $"ClaudeStudioAgent failed to start.\n" +
                $"Response: {ready ?? "NULL"}");
        }

        OutputLog.Info($"agent started (pid {_process.Id})");

        var lines = new LineQueue();
        _lines = lines;
        var reader = _reader;
        _ = Task.Run(() => PumpAgentLinesAsync(reader, lines));
        _ = Task.Run(() => ReadIdleAsync(lines));
    }

    // A message for the turn running now (ChatRequest.Steer). False when there
    // is no agent to take it.
    public async Task<bool> SteerAsync(string text)
    {
        if (_writer == null)
        {
            OutputLog.Warn("steer dropped — agent not running");
            return false;
        }
        OutputLog.Info($"steer → bytes={text.Length}");
        await _writer.WriteLineAsync(JsonSerializer.Serialize(new ChatRequest { Message = "", Steer = text }));
        await _writer.FlushAsync();
        return true;
    }

    public async Task SendPermissionResponseAsync(string toolUseId, bool allow, string? reason, string? allowSession = null)
    {
        if (_writer == null)
        {
            OutputLog.Warn($"permission-response dropped — agent not running (toolUseId={toolUseId})");
            return;
        }

        var request = new ChatRequest
        {
            Message = "",
            PermissionResponse = new PermissionResponse
            {
                ToolUseId = toolUseId,
                Allow = allow,
                Reason = reason,
                AllowSession = allowSession
            }
        };
        var json = JsonSerializer.Serialize(request);

        OutputLog.Info($"permission-response → toolUseId={toolUseId} allow={allow} allowSession={allowSession ?? "-"}");
        await _writer.WriteLineAsync(json);
        await _writer.FlushAsync();
    }

    // Answer to an AskUserQuestion. The agent translates tool_use_id → the pending
    // control_request's request_id and writes a control_response to claude.stdin.
    public async Task SendAskAnswerAsync(string toolUseId, string answersJson, bool dismissed)
    {
        if (_writer == null)
        {
            OutputLog.Warn($"ask-answer dropped — agent not running (toolUseId={toolUseId})");
            return;
        }

        var request = new ChatRequest
        {
            Message = "",
            AskAnswer = new AskAnswer
            {
                ToolUseId = toolUseId,
                AnswersJson = answersJson,
                Dismissed = dismissed
            }
        };
        var json = JsonSerializer.Serialize(request);

        OutputLog.Info($"ask-answer → toolUseId={toolUseId} dismissed={dismissed}");
        await _writer.WriteLineAsync(json);
        await _writer.FlushAsync();
    }

    // Reply to a diagnostics_request with the formatted VS Error List entries.
    public async Task SendDiagnosticsResponseAsync(string requestId, string text)
    {
        if (_writer == null)
        {
            OutputLog.Warn($"diagnostics-response dropped — agent not running (id={requestId})");
            return;
        }

        var request = new ChatRequest
        {
            Message = "",
            DiagnosticsResponse = new DiagnosticsResponse { RequestId = requestId, Text = text ?? "" }
        };
        var json = JsonSerializer.Serialize(request);

        OutputLog.Info($"diagnostics-response → id={requestId} chars={text?.Length ?? 0}");
        await _writer.WriteLineAsync(json);
        await _writer.FlushAsync();
    }

    // One-shot file rewind. Sends a rewind request and reads until the agent's
    // "rewind-result" chunk. Returns (resultJson, error): resultJson is the inner
    // control_response payload ({canRewind, filesChanged, insertions, deletions}),
    // or error is set when the agent reported a failure. Shares the streaming
    // semaphore so it never races an active turn's read loop.
    public async Task<(string? resultJson, string? error)> RewindAsync(string userMessageId, bool dryRun)
    {
        await _streamingSemaphore.WaitAsync();
        try
        {
            if (_writer == null || _reader == null)
                return (null, "agent not running");

            var request = new ChatRequest
            {
                Message = "",
                RewindRequest = new RewindRequest { UserMessageId = userMessageId, DryRun = dryRun }
            };
            OutputLog.Info($"rewind → userMessageId={userMessageId} dryRun={dryRun}");
            await _writer.WriteLineAsync(JsonSerializer.Serialize(request));
            await _writer.FlushAsync();

            string? resultJson = null;
            string? error = null;
            var lines = _lines;
            string? line;
            while (lines != null && (line = await ReadLineOrNullAsync(lines)) != null)
            {
                var chunk = JsonSerializer.Deserialize<ChatChunk>(line);
                if (chunk == null) continue;
                // not this request's: a turn claude started by itself, or what
                // background agents need meanwhile
                if (chunk.Background == true) { HandleBackgroundChunk(chunk); continue; }
                if (chunk.Type is "permission_request" or "permission_resolved" or "diagnostics_request" or "shell-log")
                {
                    HandleStrayChunk(chunk);
                    continue;
                }
                if (chunk.Type == "rewind-result") resultJson = chunk.Text;
                else if (chunk.Type == "error") { error = chunk.Text; OutputLog.Error($"rewind error: {chunk.Text}"); }
                else if (chunk.Type == "done") break;
            }
            return (resultJson, error);
        }
        finally { _streamingSemaphore.Release(); }
    }

    // One-shot context usage probe (V12). Same contract as RewindAsync: shares
    // the streaming semaphore, returns (usageJson, error) where usageJson is the
    // inner get_context_usage response payload.
    public async Task<(string? usageJson, string? error)> GetContextUsageAsync()
    {
        await _streamingSemaphore.WaitAsync();
        try
        {
            if (_writer == null || _reader == null)
                return (null, "agent not running");

            var request = new ChatRequest { Message = "", ContextUsage = true };
            await _writer.WriteLineAsync(JsonSerializer.Serialize(request));
            await _writer.FlushAsync();

            string? usageJson = null;
            string? error = null;
            var lines = _lines;
            string? line;
            while (lines != null && (line = await ReadLineOrNullAsync(lines)) != null)
            {
                var chunk = JsonSerializer.Deserialize<ChatChunk>(line);
                if (chunk == null) continue;
                // not this request's: a turn claude started by itself, or what
                // background agents need meanwhile
                if (chunk.Background == true) { HandleBackgroundChunk(chunk); continue; }
                if (chunk.Type is "permission_request" or "permission_resolved" or "diagnostics_request" or "shell-log")
                {
                    HandleStrayChunk(chunk);
                    continue;
                }
                if (chunk.Type == "context-usage-result") usageJson = chunk.Text;
                else if (chunk.Type == "error") { error = chunk.Text; OutputLog.Error($"context-usage error: {chunk.Text}"); }
                else if (chunk.Type == "done") break;
            }
            return (usageJson, error);
        }
        finally { _streamingSemaphore.Release(); }
    }

    // One-shot session title generation (V18). Same contract as RewindAsync:
    // shares the streaming semaphore, returns (title, error). Title may be
    // empty when claude declines to generate one.
    public async Task<(string? title, string? error)> GenerateSessionTitleAsync(string description)
    {
        await _streamingSemaphore.WaitAsync();
        try
        {
            if (_writer == null || _reader == null)
                return (null, "agent not running");

            var request = new ChatRequest { Message = "", SessionTitleDescription = description };
            await _writer.WriteLineAsync(JsonSerializer.Serialize(request));
            await _writer.FlushAsync();

            string? title = null;
            string? error = null;
            var lines = _lines;
            string? line;
            while (lines != null && (line = await ReadLineOrNullAsync(lines)) != null)
            {
                var chunk = JsonSerializer.Deserialize<ChatChunk>(line);
                if (chunk == null) continue;
                // not this request's: a turn claude started by itself, or what
                // background agents need meanwhile
                if (chunk.Background == true) { HandleBackgroundChunk(chunk); continue; }
                if (chunk.Type is "permission_request" or "permission_resolved" or "diagnostics_request" or "shell-log")
                {
                    HandleStrayChunk(chunk);
                    continue;
                }
                // No OutputLog.Error here: the caller classifies. Canceling or
                // clearing right after a turn kills claude before this request
                // lands ("no active session") — an expected race, not a failure.
                if (chunk.Type == "session-title-result") title = chunk.Text;
                else if (chunk.Type == "error") error = chunk.Text;
                else if (chunk.Type == "done") break;
            }
            return (title, error);
        }
        finally { _streamingSemaphore.Release(); }
    }

    // One-shot MCP status probe (V20): returns (serversJson, error) where
    // serversJson is the live session's mcpServers array.
    public async Task<(string? serversJson, string? error)> GetMcpStatusAsync()
    {
        await _streamingSemaphore.WaitAsync();
        try
        {
            if (_writer == null || _reader == null)
                return (null, "agent not running");

            var request = new ChatRequest { Message = "", McpStatus = true };
            await _writer.WriteLineAsync(JsonSerializer.Serialize(request));
            await _writer.FlushAsync();

            string? serversJson = null;
            string? error = null;
            var lines = _lines;
            string? line;
            while (lines != null && (line = await ReadLineOrNullAsync(lines)) != null)
            {
                var chunk = JsonSerializer.Deserialize<ChatChunk>(line);
                if (chunk == null) continue;
                // not this request's: a turn claude started by itself, or what
                // background agents need meanwhile
                if (chunk.Background == true) { HandleBackgroundChunk(chunk); continue; }
                if (chunk.Type is "permission_request" or "permission_resolved" or "diagnostics_request" or "shell-log")
                {
                    HandleStrayChunk(chunk);
                    continue;
                }
                if (chunk.Type == "mcp-status-result") serversJson = chunk.Text;
                else if (chunk.Type == "error") { error = chunk.Text; OutputLog.Error($"mcp-status error: {chunk.Text}"); }
                else if (chunk.Type == "done") break;
            }
            return (serversJson, error);
        }
        finally { _streamingSemaphore.Release(); }
    }

    // One-shot side question (V19): returns (answer, error). Answer is empty
    // when claude declines to answer.
    public async Task<(string? answer, string? error)> AskSideQuestionAsync(string question)
    {
        await _streamingSemaphore.WaitAsync();
        try
        {
            if (_writer == null || _reader == null)
                return (null, "agent not running");

            var request = new ChatRequest { Message = "", SideQuestion = question };
            await _writer.WriteLineAsync(JsonSerializer.Serialize(request));
            await _writer.FlushAsync();

            string? answer = null;
            string? error = null;
            var lines = _lines;
            string? line;
            while (lines != null && (line = await ReadLineOrNullAsync(lines)) != null)
            {
                var chunk = JsonSerializer.Deserialize<ChatChunk>(line);
                if (chunk == null) continue;
                // not this request's: a turn claude started by itself, or what
                // background agents need meanwhile
                if (chunk.Background == true) { HandleBackgroundChunk(chunk); continue; }
                if (chunk.Type is "permission_request" or "permission_resolved" or "diagnostics_request" or "shell-log")
                {
                    HandleStrayChunk(chunk);
                    continue;
                }
                if (chunk.Type == "side-question-result") answer = chunk.Text;
                else if (chunk.Type == "error") { error = chunk.Text; OutputLog.Error($"side-question error: {chunk.Text}"); }
                else if (chunk.Type == "done") break;
            }
            return (answer, error);
        }
        finally { _streamingSemaphore.Release(); }
    }

    // One-shot MCP reconnect (V20). Returns the error, or null on success.
    public async Task<string?> ReconnectMcpServerAsync(string serverName)
    {
        await _streamingSemaphore.WaitAsync();
        try
        {
            if (_writer == null || _reader == null)
                return "agent not running";

            var request = new ChatRequest { Message = "", McpReconnectServer = serverName };
            await _writer.WriteLineAsync(JsonSerializer.Serialize(request));
            await _writer.FlushAsync();

            string? error = null;
            var ok = false;
            var lines = _lines;
            string? line;
            while (lines != null && (line = await ReadLineOrNullAsync(lines)) != null)
            {
                var chunk = JsonSerializer.Deserialize<ChatChunk>(line);
                if (chunk == null) continue;
                // not this request's: a turn claude started by itself, or what
                // background agents need meanwhile
                if (chunk.Background == true) { HandleBackgroundChunk(chunk); continue; }
                if (chunk.Type is "permission_request" or "permission_resolved" or "diagnostics_request" or "shell-log")
                {
                    HandleStrayChunk(chunk);
                    continue;
                }
                if (chunk.Type == "mcp-reconnect-result") ok = true;
                else if (chunk.Type == "error") { error = chunk.Text; OutputLog.Error($"mcp-reconnect error: {chunk.Text}"); }
                else if (chunk.Type == "done") break;
            }
            return ok ? null : (error ?? "no response");
        }
        finally { _streamingSemaphore.Release(); }
    }

    // True while a turn is in flight. The rename-session handler consults this
    // before appending a native custom-title line to the live session's JSONL
    // (claude is appending to the same file mid-turn).
    public bool IsStreaming { get; private set; }

    public async Task AskStreamingAsync(string message, string model, string? effort, string permissionMode, Action<string> onChunk, Action<string>? onTiming = null, Action<string>? onTokens = null, string? workingDirectory = null, bool autoResume = false, Action<string>? onSession = null, Action<string, string, string?, string?, string?>? onTool = null, Action<string, string?, string, string?>? onPermissionRequest = null, Action<string, string>? onDiagnosticsRequest = null, Action<string>? onThinking = null, decimal? maxBudgetUsd = null, string? fallbackModel = null, Action<ChatChunk>? onSubagentEvent = null, Action<string, string>? onPermissionResolved = null)
    {
        await _streamingSemaphore.WaitAsync();
        IsStreaming = true;
        try
        {
            await AskStreamingCoreAsync(message, model, effort, permissionMode, onChunk, onTiming, onTokens, workingDirectory, autoResume, onSession, onTool, onPermissionRequest, onDiagnosticsRequest, onThinking, maxBudgetUsd, fallbackModel, onSubagentEvent, onPermissionResolved);
        }
        finally
        {
            IsStreaming = false;
            _streamingSemaphore.Release();
        }
    }

    // Preload (⚙ Preload Claude CLI): has the agent start claude with what the next
    // AskStreamingAsync will send, without a message. Peeks at the per-turn state
    // (pending resume/fork, the agent-restart re-resume) instead of consuming it,
    // so the first real turn builds an identical request and the agent reuses the
    // warm session. Waits behind a running turn like a send.
    public async Task PreloadAsync(string model, string? effort, string permissionMode, string? workingDirectory, bool autoResume,
        decimal? maxBudgetUsd, string? fallbackModel, Action<string>? onSession, Action<string, string, string?, string?, string?>? onTool)
    {
        await _streamingSemaphore.WaitAsync();
        try
        {
            await AskStreamingCoreAsync("", model, effort, permissionMode, _ => { }, onTiming: null, onTokens: null,
                workingDirectory, autoResume, onSession, onTool, maxBudgetUsd: maxBudgetUsd, fallbackModel: fallbackModel, preload: true);
        }
        finally
        {
            _streamingSemaphore.Release();
        }
    }

    private async Task AskStreamingCoreAsync(string message, string model, string? effort, string permissionMode, Action<string> onChunk, Action<string>? onTiming = null, Action<string>? onTokens = null, string? workingDirectory = null, bool autoResume = false, Action<string>? onSession = null, Action<string, string, string?, string?, string?>? onTool = null, Action<string, string?, string, string?>? onPermissionRequest = null, Action<string, string>? onDiagnosticsRequest = null, Action<string>? onThinking = null, decimal? maxBudgetUsd = null, string? fallbackModel = null, Action<ChatChunk>? onSubagentEvent = null, Action<string, string>? onPermissionResolved = null, bool preload = false)
    {
        // A send can queue on the semaphore behind a stuck turn; by the time it
        // runs here, a clear/stop may have nulled the streams (rodada 12: NRE at
        // WriteLineAsync) or the agent may have died. The caller's StartAsync
        // ran BEFORE the semaphore wait and can be stale — revalidate under it.
        bool agentDown;
        try { agentDown = _writer == null || _reader == null || _process == null || _process.HasExited; }
        catch { agentDown = true; }
        if (agentDown)
        {
            OutputLog.Info("agent not running at turn start — restarting");
            _agentRestartedSinceLastTurn = true;
            await StartAsync();
        }

        // A preload only peeks: the first real turn must see (and consume) the same state.
        var agentRestarted = _agentRestartedSinceLastTurn;
        var resumeId = PendingResumeSessionId;
        var forkSession = PendingForkSession;
        if (!preload)
        {
            _agentRestartedSinceLastTurn = false;
            PendingResumeSessionId = null;
            PendingForkSession = false;
        }

        // The agent process died (crash, sandbox time limit) while this chat had a live
        // session. The restarted agent knows nothing about it and would spawn claude with a
        // fresh --session-id, so the next prompt would land in an empty session while the chat
        // window still shows the old conversation. Re-resume the same transcript instead.
        // Skipped when a resume/fork was explicitly requested, or the session never started.
        if (agentRestarted && resumeId == null && !forkSession && !string.IsNullOrEmpty(CurrentSessionId))
        {
            resumeId = CurrentSessionId;
            if (!preload)
            {
                _sessionResumedLineage = true;
                OutputLog.Info($"agent restarted mid-chat — resuming session {resumeId}");
                onTool?.Invoke("system-info", "", null, "Agent restarted — session resumed.", null);
            }
        }

        // #19: keep a resumed/forked conversation going across turns. The pending
        // resume id is consumed after a single turn (#12 fork semantics), so on the
        // NEXT turn resumeId is null again — and because a resumed turn's spawn key
        // carries the id while a null turn's does not, the agent tore down the warm
        // claude process and spawned a fresh, empty --session-id. The second query
        // then landed in a brand-new session with no context (the #19 report).
        // Once a session has a resumed/forked lineage, carry the live session id
        // forward as the resume id: the spawn key stays stable (warm reuse) and any
        // forced respawn re-resumes the same transcript instead of starting over.
        // Normal (never-resumed) chats keep resumeId == null and are untouched.
        if (resumeId != null || forkSession)
        {
            if (!preload) _sessionResumedLineage = true;
        }
        else if (_sessionResumedLineage && CurrentSessionId != null)
            resumeId = CurrentSessionId;

        // Whether this turn carries the previous transcript forward (--resume /
        // --continue). The control forwards it with session-info so the UI can
        // tell a forked-with-history session from a fresh one (rewind base).
        LastTurnResumed = resumeId != null || autoResume;

        // If we already know the session id from a resume or prior turn, start
        // the JSONL watcher early. Forking is the exception: --fork-session
        // mints a NEW id different from resumeId, so pre-arming on resumeId
        // would attach to the original session's file — wait for the real
        // "session" chunk instead, same as any brand-new session (#12).
        var preKnownSessionId = forkSession ? null : (resumeId ?? CurrentSessionId);
        if (!string.IsNullOrEmpty(preKnownSessionId))
            StartJsonlWatcher(workingDirectory, preKnownSessionId, onTool);

        var request = new ChatRequest
        {
            Message = message,
            Preload = preload,
            Model = model,
            Effort = effort,
            PermissionMode = permissionMode,
            ResumeSessionId = resumeId,
            ForkSession = forkSession,
            WorkingDirectory = workingDirectory,
            AutoResume = autoResume,
            ClaudeSettings = ClaudeSettings,
            CliPath = CliPath,
            MaxBudgetUsd = maxBudgetUsd,
            FallbackModel = fallbackModel,
            // U2: when resuming a session whose custom title we know, pass it
            // along so the agent spawns claude with --name — the CLI re-appends
            // its native custom-title line and the terminal picker stays in
            // sync even for renames done while the session was live. Custom
            // only: generated titles already persist natively as ai-title.
            // Gated on an actual resume (--resume/--continue): without it, a
            // respawn that starts a FRESH session (e.g. model switch with
            // auto-resume off) would stamp the previous session's title onto
            // the new one (audit 2026-07-10 #1).
            SessionName = (resumeId != null || autoResume) && !string.IsNullOrEmpty(preKnownSessionId)
                ? SessionTitlesStore.GetCustom(preKnownSessionId!) : null
        };
        var json = JsonSerializer.Serialize(request);

        OutputLog.Info($"request → model={model} effort={effort ?? "-"} perm={permissionMode} cwd={workingDirectory ?? "-"} resume={resumeId ?? "-"} autoResume={autoResume} bytes={message?.Length ?? 0}");

        await _writer!.WriteLineAsync(json);
        await _writer.FlushAsync();

        _cancelTcs = new TaskCompletionSource<bool>();
        bool cancelled = false;
        var callbacks = new TurnCallbacks
        {
            OnChunk = onChunk, OnTiming = onTiming, OnTokens = onTokens, OnSession = onSession, OnTool = onTool,
            OnPermissionRequest = onPermissionRequest, OnDiagnosticsRequest = onDiagnosticsRequest, OnThinking = onThinking,
            OnSubagentEvent = onSubagentEvent, OnPermissionResolved = onPermissionResolved
        };

        while (true)
        {
            // The agent can be torn down mid-turn: a cancel puts this loop into
            // drain mode (see `if (cancelled) continue` below), and a following
            // clear/reset runs StopAsync, which disposes the agent's stream and so
            // closes its line channel. Snapshot the field and bail if it's already
            // gone (rodada 15).
            var lines = _lines;
            if (lines == null) break;

            string? responseJson;
            var readTask = ReadLineOrNullAsync(lines);
            var winner = await Task.WhenAny(readTask, _cancelTcs.Task);
            if (winner == _cancelTcs.Task) cancelled = true;
            responseJson = await readTask;

            if (responseJson == null)
            {
                OutputLog.Info("read loop ended: agent stream closed mid-turn");
                break;
            }

            var chunk = JsonSerializer.Deserialize<ChatChunk>(responseJson);

            if (chunk == null) continue;

            // A turn claude started by itself before this request's answer: its
            // own, not this request's, done included.
            if (chunk.Background == true)
            {
                HandleBackgroundChunk(chunk);
                continue;
            }

            if (chunk.Type == "done")
            {
                OutputLog.Info(cancelled ? "request done (after cancel)" : "request done");
                break;
            }

            // Drain silently after cancel — don't forward any callbacks.
            //
            // Retractions are the exception, and they are the whole reason the cancel
            // path exists: FailPending answers every waiting hook when the user
            // cancels, and those answers arrive here as permission_resolved. Dropping
            // them leaves the modal on screen for a request the agent already closed,
            // which is the failure this chunk was introduced to remove. The timeout
            // path is the only other producer and it is opt-in, so without this the
            // feature is dead under stock settings.
            if (cancelled && chunk.Type != "permission_resolved") continue;

            DispatchChunk(chunk, callbacks, workingDirectory);
        }

        _cancelTcs = null;
    }

    // What a turn's chunks do: the same for a request's turn and for a turn
    // claude started by itself (background agents finishing).
    private void DispatchChunk(ChatChunk chunk, TurnCallbacks cb, string? workingDirectory)
    {
        var onChunk = cb.OnChunk;
        var onTool = cb.OnTool;

        if (chunk.Type == "timing")
        {
            OutputLog.Info($"timing: {chunk.Text}");
            cb.OnTiming?.Invoke(chunk.Text);
            return;
        }

        if (chunk.Type == "warn")
        {
            OutputLog.Warn(chunk.Text);
            return;
        }

        // Persistent shell commands and results, for the Shell output pane.
        if (chunk.Type == "shell-log")
        {
            ShellLog.Write(chunk.Text);
            return;
        }

        // Agent-side notes that belong in the log but never in the chat —
        // e.g. how many permission rules came from claude's settings files.
        if (chunk.Type == "info")
        {
            OutputLog.Info(chunk.Text);
            return;
        }

        if (chunk.Type == "claude-pid")
        {
            if (int.TryParse(chunk.Text, out var claudePid))
            {
                try { OnClaudePid?.Invoke(claudePid); }
                catch (Exception ex) { OutputLog.Warn($"presence watcher start failed: {ex.Message}"); }
            }
            return;
        }

        if (chunk.Type == "tokens")
        {
            OutputLog.Info($"tokens: {chunk.Text}");
            cb.OnTokens?.Invoke(chunk.Text);
            return;
        }

        if (chunk.Type == "tokens-live")
        {
            onTool?.Invoke("tokens-live", "", null, chunk.Text, null);
            return;
        }

        // CLI informational notices ("Unknown command: /x") — shown as a
        // muted system line so the turn doesn't look like a silent no-op.
        if (chunk.Type == "system-info")
        {
            OutputLog.Info($"system-info: {chunk.Text}");
            onTool?.Invoke("system-info", "", null, chunk.Text, null);
            return;
        }

        if (chunk.Type == "session")
        {
            CurrentSessionId = chunk.Text;
            OutputLog.Info($"session: {chunk.Text}");
            cb.OnSession?.Invoke(chunk.Text);
            StartJsonlWatcher(workingDirectory, chunk.Text, onTool);
            return;
        }

        if (chunk.Type == "thinking")
        {
            cb.OnThinking?.Invoke(chunk.Text);
            return;
        }

        // #7/#9: piggyback on the existing wide onTool callback (kind, name,
        // input, text, id) the same way "system-info" already does — avoids
        // growing this signature with two more single-purpose Actions.
        if (chunk.Type == "user-ack")
        {
            onTool?.Invoke("user-ack", "", null, null, null);
            return;
        }

        if (chunk.Type == "rate-limit")
        {
            onTool?.Invoke("rate-limit", "", null, chunk.Text, null);
            return;
        }

        // #21: same onTool piggyback as user-ack/rate-limit/model-used —
        // "compacting" is a start/stop presence signal, "compact-boundary"
        // carries the pre/post token metadata as a raw JSON blob in text.
        if (chunk.Type == "compacting" || chunk.Type == "compact-boundary")
        {
            onTool?.Invoke(chunk.Type, "", null, chunk.Text, null);
            return;
        }

        // #14: --fallback-model engagement/recovery signal — same onTool
        // piggyback as user-ack/rate-limit above.
        if (chunk.Type == "model-used")
        {
            onTool?.Invoke("model-used", "", null, chunk.Text, null);
            return;
        }

        // #13: subagent events carry more correlation fields (ParentToolId,
        // SubagentType, TaskDescription) than onTool's 5 positional params
        // fit — pass the whole ChatChunk through instead of piggybacking.
        if (chunk.Type.StartsWith("subagent-", StringComparison.Ordinal))
        {
            cb.OnSubagentEvent?.Invoke(chunk);
            return;
        }

        // Background tasks running and their steps, also between turns. A
        // request's callbacks have no handler for them: the background ones do.
        if (chunk.Type is "background-tasks" or "task-progress")
        {
            (cb.OnBackgroundTasks ?? BackgroundCallbacks?.OnBackgroundTasks)?.Invoke(chunk);
            return;
        }

        // #11: same sentinel-on-onChunk trick as CLAUDE_NOT_FOUND:: below —
        // a terminal, turn-ending condition that needs distinct UI treatment
        // (not a new callback param, not a plain chat error bubble).
        if (chunk.Type == "budget-exceeded")
        {
            onChunk("BUDGET_EXCEEDED::" + (chunk.Text ?? ""));
            return;
        }

        // NEVER break on error/claude-not-found: the agent always follows a
        // fatal one with "done" (and mid-turn errors are informational — the
        // turn keeps streaming). Breaking early left that "done" orphaned in
        // the pipe, and every subsequent request then completed instantly
        // against the previous request's done — the chat ran one message
        // behind until the agent restarted (D7, validation 2026-07-18).
        if (chunk.Type == "error")
        {
            OutputLog.Error($"agent error: {chunk.Text}");
            if (!string.IsNullOrEmpty(chunk.Text))
            {
                // An expired session survives the pre-flight IsSignedIn() check
                // (oauthAccount stays in ~/.claude.json — it holds profile fields
                // only, no token and no expiry), so the CLI is the first thing to
                // notice. Same sentinel trick as CLAUDE_NOT_FOUND:: below, routing
                // it to the sign-in card instead of a dead-end error bubble.
                // "Prompt is too long" gets the same treatment: the session's
                // context is over the ceiling and every resume of it refails, so
                // route it to the "start a new session" card.
                // A model that needs paid usage credits (e.g. Fable) fails
                // the turn on the way in; route it to the "choose another
                // model" card instead of a raw bubble.
                string routed;
                if (ClaudeStudioShared.AuthErrors.IsAuthFailure(chunk.Text))
                    routed = "AUTH_REQUIRED::" + chunk.Text;
                else if (ClaudeStudioShared.ContextErrors.IsContextOverflow(chunk.Text))
                    routed = "CONTEXT_FULL::" + chunk.Text;
                else if (ClaudeStudioShared.CreditErrors.IsOutOfCredits(chunk.Text))
                    routed = "CREDITS_REQUIRED::" + chunk.Text;
                else
                    routed = chunk.Text;
                onChunk(routed);
            }
            return;
        }

        // claude.exe missing — route through onChunk with a sentinel so the
        // control can show a dedicated install card instead of a chat error.
        if (chunk.Type == "claude-not-found")
        {
            OutputLog.Error($"claude not found: {chunk.Text}");
            onChunk("CLAUDE_NOT_FOUND::" + (chunk.Text ?? ""));
            return;
        }

        if (chunk.Type == "chunk" && !string.IsNullOrEmpty(chunk.Text))
            onChunk(chunk.Text);

        if (chunk.Type == "tool_use" || chunk.Type == "tool_result" || chunk.Type == "tool_error")
        {
            OutputLog.Info($"{chunk.Type}: {chunk.Tool ?? "-"} id={chunk.ToolId ?? "-"} bytes={(chunk.ToolInput ?? chunk.Text ?? "").Length}");
            onTool?.Invoke(chunk.Type, chunk.Tool ?? "", chunk.ToolInput, chunk.Text, chunk.ToolId);
        }

        // Decided by a rule / session allowlist, so no modal and no UI trace —
        // the log line is the only way to tell it apart from a tool that
        // simply never needed permission.
        if (chunk.Type == "permission_auto")
        {
            OutputLog.Info($"permission auto: {chunk.Tool ?? "-"} — {chunk.Text}");
            return;
        }

        if (chunk.Type == "permission_request")
        {
            OutputLog.Info($"permission_request: {chunk.Tool ?? "-"} id={chunk.ToolId ?? "-"} cwd={chunk.Cwd ?? "-"}");
            cb.OnPermissionRequest?.Invoke(chunk.Tool ?? "", chunk.ToolInput, chunk.ToolId ?? "", chunk.Cwd);
        }

        // Timeout/cancel/shutdown resolved it without the user, so the card
        // has to come down before claude moves on — otherwise it is answered
        // into a tool_use_id the agent already closed.
        if (chunk.Type == "permission_resolved")
        {
            OutputLog.Info($"permission_resolved: id={chunk.ToolId ?? "-"} ({chunk.Text})");
            cb.OnPermissionResolved?.Invoke(chunk.ToolId ?? "", chunk.Text ?? "");
        }

        if (chunk.Type == "diagnostics_request")
        {
            // Text carries the file path, ToolId the correlation id.
            OutputLog.Info($"diagnostics_request: file={chunk.Text} id={chunk.ToolId ?? "-"}");
            cb.OnDiagnosticsRequest?.Invoke(chunk.Text ?? "", chunk.ToolId ?? "");
        }
    }

    // ── Between requests ─────────────────────────────────────────────────────
    // The agent's stdout has one reader for the agent's life (PumpAgentLinesAsync),
    // so what the agent writes when no request is waiting is read too: a turn
    // claude started by itself (Background chunks), and what background agents
    // need meanwhile (permission prompts, VS bridge requests). Request loops read
    // _lines under _streamingSemaphore; ReadIdleAsync reads it between them.
    private LineQueue? _lines;

    // What the chat shows a turn claude started by itself with, and the working
    // directory its session runs in. Set by the control on every send.
    public TurnCallbacks? BackgroundCallbacks { get; set; }
    public string? BackgroundWorkingDirectory { get; set; }
    private bool _backgroundActive, _backgroundCancelled;

    private static Task<string?> ReadLineOrNullAsync(LineQueue lines) => lines.ReadAsync();

    private static async Task PumpAgentLinesAsync(StreamReader reader, LineQueue lines)
    {
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
                lines.Write(line);
        }
        catch { /* the agent stopped: its stream is disposed or broken */ }
        finally { lines.Complete(); }
    }

    private async Task ReadIdleAsync(LineQueue lines)
    {
        try
        {
            while (await lines.WaitToReadAsync())
            {
                await _streamingSemaphore.WaitAsync();
                try
                {
                    while (lines.TryRead(out var line))
                    {
                        ChatChunk? chunk;
                        try { chunk = JsonSerializer.Deserialize<ChatChunk>(line); }
                        catch { continue; }
                        if (chunk == null) continue;
                        if (chunk.Background == true) HandleBackgroundChunk(chunk);
                        else HandleStrayChunk(chunk);
                    }
                }
                finally { _streamingSemaphore.Release(); }
            }
        }
        catch (Exception ex)
        {
            OutputLog.Warn($"idle agent reader failed: {ex.Message}");
        }
    }

    private void HandleBackgroundChunk(ChatChunk chunk)
    {
        var cb = BackgroundCallbacks;
        if (chunk.Type == "background-turn")
        {
            OutputLog.Info(chunk.Text == "steer" ? "background turn: the steering message, after its turn ended"
                : "background turn: claude continues on its own (background agents)");
            _backgroundActive = true;
            _backgroundCancelled = false;
            cb?.OnBackgroundStart?.Invoke(chunk.Text);
            return;
        }
        if (chunk.Type == "done")
        {
            OutputLog.Info(_backgroundCancelled ? "background turn done (after cancel)" : "background turn done");
            _backgroundActive = false;
            cb?.OnBackgroundEnd?.Invoke();
            return;
        }
        if (cb == null || _backgroundCancelled && chunk.Type != "permission_resolved") return;
        DispatchChunk(chunk, cb, BackgroundWorkingDirectory);
    }

    // A chunk outside any request or background turn: what the agent writes for
    // background agents (their permission prompts, VS bridge requests) and its
    // logs. Anything else has no turn to go to.
    private static readonly HashSet<string> StrayChunkTypes = new(StringComparer.Ordinal)
    {
        "permission_request", "permission_resolved", "permission_auto", "diagnostics_request",
        "shell-log", "timing", "info", "warn", "error"
    };

    private void HandleStrayChunk(ChatChunk chunk)
    {
        if (!StrayChunkTypes.Contains(chunk.Type) && !StreamEventParser.IsIdleProgress(chunk))
        {
            OutputLog.Info($"agent output between requests ignored: {chunk.Type}");
            return;
        }
        if (chunk.Type == "error")
        {
            OutputLog.Error($"agent error: {chunk.Text}");
            return;
        }
        if (BackgroundCallbacks is { } cb) DispatchChunk(chunk, cb, BackgroundWorkingDirectory);
        else if (chunk.Type is "permission_request" or "diagnostics_request")
            OutputLog.Warn($"{chunk.Type} between requests with no chat to show it in (id={chunk.ToolId ?? "-"})");
    }

    private string? _watcherSessionId;
    private void StartJsonlWatcher(string? cwd, string sessionId, Action<string, string, string?, string?, string?>? onTool)
    {
        if (onTool == null) return;
        if (_watcherSessionId == sessionId) return; // already watching this session
        _watcherSessionId = sessionId;
        _jsonlWatcher.OnEvent = onTool;
        _jsonlWatcher.Start(cwd, sessionId);
    }

    // New-chat reset (rodada 12): without this a cleared chat kept the dead
    // session's id — the JSONL watcher re-attached to the old file and title/
    // branch/rewind kept targeting the previous session.
    public void ResetSession()
    {
        CurrentSessionId = null;
        PendingResumeSessionId = null;
        PendingForkSession = false;
        LastTurnResumed = false;
        _sessionResumedLineage = false; // #19
    }

    public async Task StopAsync()
    {
        _jsonlWatcher.Stop();
        _watcherSessionId = null;

        // Take a local snapshot so concurrent StopAsync calls (e.g. solution
        // event reset firing while user clicks ✎) don't NullRef on _process
        // being cleared mid-finally by the other thread.
        var proc = _process;
        if (proc == null) return;
        _process = null; // claim it — subsequent callers see null and bail out

        // Claim the streams too, atomically with _process. Killing the agent can
        // take seconds (WaitForExit timeout), during which a `clear` may already
        // have called StartAsync and set _writer/_reader to the NEW agent. If we
        // disposed/nulled the live fields in the delayed finally we'd clobber the
        // new agent's streams (→ NullRef in the in-flight read loop). Operate only
        // on these locals so a concurrent StartAsync's fresh fields are untouched.
        var writer = _writer;
        var reader = _reader;
        _writer = null;
        _reader = null;

        var pid = proc.Id;
        OutputLog.Info($"stopping agent (pid {pid})");

        try
        {
            if (!proc.HasExited)
            {
                writer?.Close();

                var exited = await Task.Run(() =>
                    proc.WaitForExit(2000));

                if (!exited && !proc.HasExited)
                {
                    OutputLog.Warn($"agent (pid {pid}) didn't exit in 2s, killing the process tree");
                    KillProcessTree(proc, pid);
                    proc.WaitForExit();
                }
            }
        }
        finally
        {
            reader?.Dispose();
            writer?.Dispose();
            proc.Dispose();
            OutputLog.Info($"agent (pid {pid}) stopped");
        }
    }

    /// <summary>
    /// Force-kills an agent process and its whole tree, so the claude CLI it spawned
    /// dies with it.
    /// </summary>
    /// <remarks>
    /// The agent only tears down its claude child when it shuts down gracefully; a hard
    /// kill of just the agent (this path fires when the agent is busy mid-turn and
    /// doesn't exit within the grace window, which is exactly what a solution reload
    /// does) leaves claude orphaned. That orphan keeps running and fires PreToolUse
    /// hooks at a pipe named after the now-dead agent's PID, so every gated call comes
    /// back "agent pipe not reachable" and the turn wedges. .NET Framework has no
    /// <c>Process.Kill(entireProcessTree: true)</c>, so this shells out to taskkill /T
    /// while the agent is still alive (its PID, and thus the tree, is still valid).
    /// </remarks>
    private static void KillProcessTree(Process proc, int pid)
    {
        try
        {
            using var tk = Process.Start(new ProcessStartInfo
            {
                FileName = "taskkill",
                Arguments = $"/PID {pid} /T /F",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            tk?.WaitForExit(3000);
        }
        catch (Exception ex)
        {
            // taskkill missing or refused: fall back to killing just the agent. The
            // orphan risk returns, but a dead agent beats a stuck one.
            OutputLog.Warn($"taskkill tree kill failed for pid {pid}: {ex.Message}, falling back to Kill()");
            try { proc.Kill(); } catch { }
        }
    }

    private static string GetAgentPath()
    {
        var extensionDirectory = Path.GetDirectoryName(
            typeof(AgentClient).Assembly.Location)!;

        var agentPath = Path.Combine(
            extensionDirectory,
            "ClaudeStudioAgent",
            "ClaudeStudioAgent.exe");

        if (File.Exists(agentPath))
            return agentPath;

        throw new FileNotFoundException(
            $"ClaudeStudioAgent.exe not found at: {agentPath}");
    }

    //private static string GetAgentPath()
    //{
    //    var currentDirectory = new DirectoryInfo(
    //        Path.GetDirectoryName(typeof(AgentClient).Assembly.Location)!);

    //    while (currentDirectory != null)
    //    {
    //        var agentProjectPath = Path.Combine(
    //            currentDirectory.FullName,
    //            "ClaudeStudioAgent");

    //        if (Directory.Exists(agentProjectPath))
    //        {
    //            var agentPath = Directory
    //                .GetFiles(
    //                    agentProjectPath,
    //                    "ClaudeStudioAgent.exe",
    //                    SearchOption.AllDirectories)
    //                .FirstOrDefault(path =>
    //                    path.Contains(@"\bin\Debug\") ||
    //                    path.Contains(@"\bin\Release\"));

    //            if (agentPath != null)
    //                return agentPath;
    //        }

    //        currentDirectory = currentDirectory.Parent;
    //    }

    //    throw new FileNotFoundException(
    //        "ClaudeStudioAgent.exe not found. Build the ClaudeStudioAgent project first.");
    //}
}