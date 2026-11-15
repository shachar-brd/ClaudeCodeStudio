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
