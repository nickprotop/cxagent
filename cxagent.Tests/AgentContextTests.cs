using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The agent's context, and the compaction that makes room in it.
///
/// <para>These cover the defect that forced the old design: the compressor split the conversation at
/// a blind midpoint, so a tool result could survive while the assistant message that called for it
/// was summarised away. Providers reject that pairing, which is why the loop used to discard its
/// entire working context at the end of every goal rather than keep tool messages at all. With the
/// hazard closed the context can persist, which is what makes an agent self-contained.</para>
/// </summary>
public class AgentContextTests
{
    private static ChatMessage User(string text) => new() { Role = "user", Content = text };
    private static ChatMessage Call(string id) =>
        new() { Role = "assistant", Content = "", ToolCalls = [new ToolCall { Name = "read_file", Id = id }] };


    private static ChatMessage Result(string id, string content = "contents") =>
        new() { Role = "tool", Content = content, ToolCallId = id };

    /// <summary>
    /// THE REGRESSION. A midpoint cut lands mid-pair on any conversation with an even number of tool
    /// pairs — simulated across list shapes before the fix, it orphaned a result in half of them.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void SafeCut_NeverLeavesAToolResultWithoutItsCall(int pairs)
    {
        var messages = new List<ChatMessage> { User("goal") };
        for (var i = 0; i < pairs; i++) { messages.Add(Call($"t{i}")); messages.Add(Result($"t{i}")); }

        var cut = SessionCompressor.SafeCut(messages);
        var kept = messages.Skip(cut).ToList();

        foreach (var m in kept.Where(m => m.ToolCallId is not null))
            Assert.Contains(kept, k => k.ToolCalls?.Any(c => c.Id == m.ToolCallId) == true);
    }

    /// <summary>The cut still has to make progress, or compression would never free anything.</summary>
    [Fact]
    public void SafeCut_StillCutsSomething()
    {
        var messages = new List<ChatMessage>();
        for (var i = 0; i < 10; i++) messages.Add(User($"m{i}"));

        Assert.True(SessionCompressor.SafeCut(messages) > 0);
    }

    /// <summary>
    /// THE COMPRESSOR RE-ESTIMATES THE READING ITSELF. It takes the context rather than a bare list
    /// precisely so it can: it is what rewrote the conversation, so it is what knows the last
    /// measurement no longer describes it. Leaving that to the caller means a caller that forgets
    /// leaves the status bar confidently reporting a number for a context that no longer exists.
    /// </summary>
    [Fact]
    public async Task CompressAsync_ReEstimatesTheOccupancyReading()
    {
        var context = new AgentContext(window: 100_000);
        for (var i = 0; i < 12; i++)
            context.Add(new ChatMessage { Role = "user", Content = new string('x', 5_000) });
        context.RecordUsage(40_000);

        await SessionCompressor.CompressAsync(context, new SummarisingProvider(), CancellationToken.None);

        Assert.True(context.IsEstimated, "the reading was not marked as arithmetic");
        Assert.NotNull(context.Used);
        Assert.True(context.Used < 40_000, $"the reading did not fall (was {context.Used})");
    }

    /// <summary>A provider that answers a summarisation request with a fixed summary.</summary>
    private sealed class SummarisingProvider : ILlmProvider
    {
        public string ProviderId => "test";
        public string DisplayName => "test";
        public string ModelId => "test";
        public bool SupportsToolCalling => false;
        public bool SupportsStreaming => false;
        public ILlmProvider WithModel(string model) => this;

        public Task<LlmResponse> ChatAsync(List<ChatMessage> messages, List<ToolDefinition>? tools,
            CancellationToken ct) => Task.FromResult(new LlmResponse { Text = "earlier: a summary." });

        public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(List<ChatMessage> messages,
            List<ToolDefinition>? tools,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    /// <summary>
    /// THE SUMMARISER MUST SEE THE TOOL CALLS. Verified by driving, twice, before this was written.
    ///
    /// <para>An assistant message that makes a tool call carries EMPTY Content — the call lives in
    /// ToolCalls — so rendering the transcript from Content alone handed the model blank lines
    /// exactly where the work was. Two live compactions produced 100-character "summaries" that were
    /// a bare tool name and nothing else. With the calls rendered, the same session produced 1,375
    /// characters of structured notes, and the agent afterwards recalled specific detail from files
    /// whose contents had been summarised away.</para>
    /// </summary>
    [Fact]
    public void Render_ShowsToolCallsThatCarryNoContent()
    {
        var call = new ChatMessage
        {
            Role = "assistant", Content = "",
            ToolCalls = [new ToolCall
            {
                Name = "read_file",
                Arguments = System.Text.Json.JsonDocument.Parse("""{"path":"a.cs"}""").RootElement,
            }],
        };

        var line = SessionCompressor.RenderForTest(call);

        Assert.Contains("read_file", line);
        Assert.Contains("a.cs", line);
    }

    /// <summary>
    /// NAME AND TARGET, NOT A CALLABLE BLOB. Rendering the raw arguments gave the model something
    /// that looked like a call to MAKE: driven live, it answered a summarise request by emitting a
    /// tool-call block, and that block became the summary.
    /// </summary>
    [Fact]
    public void Render_DoesNotEmitSomethingThatLooksLikeACall()
    {
        var call = new ChatMessage
        {
            Role = "assistant", Content = "",
            ToolCalls = [new ToolCall
            {
                Name = "read_file",
                Arguments = System.Text.Json.JsonDocument.Parse("""{"path":"a.cs"}""").RootElement,
            }],
        };

        var line = SessionCompressor.RenderForTest(call);

        Assert.DoesNotContain("<invoke", line);
        Assert.DoesNotContain("<parameter", line);
        Assert.DoesNotContain("{\"path\"", line);
    }

    /// <summary>
    /// A tool RESULT is capped. Summarising exists to condense file contents; re-sending a 35,000
    /// character read in full, to be told about it, costs the whole saving.
    /// </summary>
    [Fact]
    public void Render_CapsAToolResult()
    {
        var result = new ChatMessage
        {
            Role = "tool", ToolCallId = "t0", Content = new string('x', 35_000),
        };

        var line = SessionCompressor.RenderForTest(result);

        Assert.True(line.Length < 5_000, $"a 35,000-char result rendered as {line.Length} chars");
        Assert.Contains("more characters", line);
    }

    /// <summary>Occupancy is a measurement; a reported zero is an absent one, not an empty context.</summary>
    [Fact]
    public void RecordUsage_IgnoresANonMeasurement()
    {
        var ctx = new AgentContext(window: 100_000);
        ctx.RecordUsage(40_000);
        ctx.RecordUsage(0);

        Assert.Equal(40_000, ctx.Used);
        Assert.Equal(0.4, ctx.UsedFraction);
    }

    /// <summary>
    /// ESTIMATED FROM TOKEN DENSITY, not from a before/after ratio on the conversation. The two sizes
    /// such a ratio compares are taken at different moments — the reading when a turn is SENT, the
    /// size after that turn's tool results have been appended — and measured live that made a −32%
    /// compaction move the estimate by 1%. A density is stable across both moments.
    /// </summary>
    [Fact]
    public void EstimateUsageAfterCompaction_AppliesTheMeasuredTokenDensity()
    {
        var ctx = new AgentContext(window: 100_000);
        ctx.RecordUsage(40_000, atChars: 1_000);      // 40 tokens per character

        // The conversation GREW after that reading (this turn's tool results), then compaction cut it
        // to 250 characters. The estimate must follow the surviving size, not the growth.
        ctx.Add(new ChatMessage { Role = "user", Content = new string('x', 250) });
        ctx.EstimateUsageAfterCompaction();

        Assert.Equal(10_000, ctx.Used);               // 250 chars x 40 tokens/char
        Assert.True(ctx.IsEstimated);
    }

    /// <summary>A real measurement supersedes an estimate, and clears the marker.</summary>
    [Fact]
    public void RecordUsage_ClearsTheEstimatedMarker()
    {
        var ctx = new AgentContext(window: 100_000);
        ctx.Add(new ChatMessage { Role = "user", Content = new string('x', 1_000) });
        ctx.RecordUsage(40_000, atChars: 1_000);
        ctx.EstimateUsageAfterCompaction();
        ctx.RecordUsage(12_000, atChars: 1_000);

        Assert.False(ctx.IsEstimated);
        Assert.Equal(12_000, ctx.Used);
    }

    /// <summary>Without a window there is no denominator, and a guessed one is worse than none.</summary>
    [Fact]
    public void UsedFraction_IsNullWithoutAWindow()
    {
        var ctx = new AgentContext(window: null);
        ctx.RecordUsage(40_000);

        Assert.Equal(40_000, ctx.Used);
        Assert.Null(ctx.UsedFraction);
    }
}
