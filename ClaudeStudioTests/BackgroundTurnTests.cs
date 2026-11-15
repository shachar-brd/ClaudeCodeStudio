using System.Text.Json;
using ClaudeStudioExtension.Agent;
using ClaudeStudioShared;
using Xunit;

namespace ClaudeStudioTests;

public class BackgroundTurnTests
{
    private static JsonElement Evt(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    [Theory]
    [InlineData("""{"type":"stream_event","event":{"type":"message_start"}}""")]
    [InlineData("""{"type":"assistant","message":{"content":[]}}""")]
    [InlineData("""{"type":"user","message":{"content":"<task-notification>"}}""")]
    [InlineData("""{"type":"assistant","parent_tool_use_id":null,"message":{}}""")]
    public void StartsTurn_for_main_agent_turn_events(string json) =>
        Assert.True(StreamEventParser.StartsTurn(Evt(json)));

    [Theory]
    // subagent work while the main agent is idle: the user can keep chatting
    [InlineData("""{"type":"assistant","parent_tool_use_id":"toolu_1","message":{}}""")]
    [InlineData("""{"type":"stream_event","parent_tool_use_id":"toolu_1","event":{}}""")]
    // between-turn noise: no turn, no result to wait for
    [InlineData("""{"type":"system","subtype":"task_progress"}""")]
    [InlineData("""{"type":"rate_limit_event"}""")]
    [InlineData("""{"type":"result","subtype":"success"}""")]
    [InlineData("""{"type":"control_response","response":{}}""")]
    [InlineData("""{"no_type":1}""")]
    public void StartsTurn_not_for_subagent_or_idle_events(string json) =>
        Assert.False(StreamEventParser.StartsTurn(Evt(json)));

    private static List<ChatChunk> Parse(string json) =>
        StreamEventParser.Process(Evt(json), new StreamEventState(), 0, null, "default", "ask").Chunks;

    [Fact]
    public void Background_tasks_list_becomes_a_chunk_with_the_list()
    {
        var chunks = Parse("""{"type":"system","subtype":"background_tasks_changed","tasks":[{"task_id":"a2","task_type":"local_agent","subagent_type":"Explore","description":"pong"}]}""");
        var c = Assert.Single(chunks);
        Assert.Equal("background-tasks", c.Type);
        Assert.Equal("a2", JsonSerializer.Deserialize<JsonElement>(c.Text)[0].GetProperty("task_id").GetString());
        Assert.True(StreamEventParser.IsIdleProgress(c));
        Assert.Equal("[]", Assert.Single(Parse("""{"type":"system","subtype":"background_tasks_changed","tasks":[]}""")).Text);
    }

    [Fact]
    public void Task_progress_carries_step_tool_usage_and_ids()
    {
        var c = Assert.Single(Parse("""{"type":"system","subtype":"task_progress","task_id":"a2","tool_use_id":"toolu_1","description":"Running Sleep","subagent_type":"general-purpose","usage":{"total_tokens":11753,"tool_uses":1,"duration_ms":2480},"last_tool_name":"Bash"}"""));
        Assert.Equal("task-progress", c.Type);
        Assert.Equal("Running Sleep", c.Text);
        Assert.Equal("Bash", c.Tool);
        Assert.Equal("a2", c.ToolId);
        Assert.Equal("toolu_1", c.ParentToolId);
        Assert.Equal(1, JsonSerializer.Deserialize<JsonElement>(c.ToolInput!).GetProperty("tool_uses").GetInt32());
        Assert.True(StreamEventParser.IsIdleProgress(c));
    }

    [Theory]
    [InlineData("subagent-tool_use", true)]
    [InlineData("subagent-text", true)]
    [InlineData("chunk", false)]
    [InlineData("done", false)]
    [InlineData("session", false)]
    [InlineData("tokens-live", false)]
    public void Only_progress_is_forwarded_between_turns(string type, bool forwarded) =>
        Assert.Equal(forwarded, StreamEventParser.IsIdleProgress(new ChatChunk { Type = type }));

    [Fact]
    public void Background_flag_is_left_out_of_normal_chunks_round_trip()
    {
        var plain = JsonSerializer.Deserialize<ChatChunk>(JsonSerializer.Serialize(new ChatChunk { Type = "chunk", Text = "x" }))!;
        var background = JsonSerializer.Deserialize<ChatChunk>(JsonSerializer.Serialize(new ChatChunk { Type = "done", Background = true }))!;
        Assert.NotEqual(true, plain.Background);
        Assert.True(background.Background);
    }

    [Fact]
    public async Task LineQueue_reads_in_order_then_null_once_complete_and_drained()
    {
        var q = new LineQueue();
        q.Write("a");
        q.Write("b");
        q.Complete();
        q.Write("ignored after complete");
        Assert.Equal("a", await q.ReadAsync());
        Assert.True(q.TryRead(out var b));
        Assert.Equal("b", b);
        Assert.Null(await q.ReadAsync());
        Assert.False(await q.WaitToReadAsync());
    }

    [Fact]
    public async Task LineQueue_wakes_a_waiting_reader_on_write_and_on_complete()
    {
        var ct = TestContext.Current.CancellationToken;
        var q = new LineQueue();
        var waiting = q.ReadAsync();
        await Task.Delay(50, ct);
        Assert.False(waiting.IsCompleted);
        q.Write("late");
        Assert.Equal("late", await waiting.WaitAsync(TimeSpan.FromSeconds(5), ct));

        var idle = q.WaitToReadAsync();
        await Task.Delay(50, ct);
        Assert.False(idle.IsCompleted);
        q.Complete();
        Assert.False(await idle.WaitAsync(TimeSpan.FromSeconds(5), ct));
    }

    [Fact]
    public async Task LineQueue_gives_each_line_to_exactly_one_of_many_readers()
    {
        var ct = TestContext.Current.CancellationToken;
        var q = new LineQueue();
        var got = new System.Collections.Concurrent.ConcurrentBag<string>();
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            while (await q.ReadAsync() is { } line) got.Add(line);
        }, ct)).ToArray();
        for (var i = 0; i < 1000; i++) q.Write(i.ToString());
        q.Complete();
        await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.Equal(1000, got.Count);
        Assert.Equal(1000, got.Distinct().Count());
    }
}
