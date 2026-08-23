using CxAgent.Core.Sessions;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The agent's context, and the compaction that makes room in it.
///
/// <para>These cover the hazard that makes a persistent context possible at all. Split the
/// conversation at a blind midpoint and a tool result survives while the assistant message that
/// called for it is summarised away; providers reject that pairing, and the only safe response to an
/// unsplittable context is to discard the whole working set at the end of every goal. Cutting only at
/// a safe boundary is what lets the context persist, which is what makes an agent self-contained.</para>
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
    /// THE SUMMARISER MUST SEE THE TOOL CALLS. Verified by driving, twice.
    ///
    /// <para>An assistant message that makes a tool call carries EMPTY Content — the call lives in
    /// ToolCalls — so rendering the transcript from Content alone hands the model blank lines exactly
    /// where the work was. Two live compactions of that shape produced 100-character "summaries" that
    /// were a bare tool name and nothing else. With the calls rendered, the same session produced
    /// 1,375 characters of structured notes, and the agent afterwards recalled specific detail from
    /// files whose contents had been summarised away.</para>
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
    /// A READING THAT CANNOT BE TRUE IS NOT A MEASUREMENT.
    ///
    /// <para>Measured on this machine, twice, in independent sessions: 31,060 characters reported as
    /// 132,495 input tokens, and 57,088 characters reported as 249,125. Both are about 17x more
    /// tokens than characters — impossible for any tokenizer, since a token is at minimum one
    /// character — and the second also exceeds the 212,992-token window the model was serving. 2 of
    /// 25 logged readings were in this state.</para>
    ///
    /// <para>Acting on one is destructive: against the default 40,000 threshold either figure
    /// triggers a compression of a nearly-empty context, which summarises history away to free space
    /// that was never occupied. The previous reading is kept instead, exactly as for a reported 0.</para>
    /// </summary>
    [Fact]
    public void RecordUsage_RejectsAReadingWithMoreTokensThanCharacters()
    {
        var ctx = new AgentContext(window: 212_992);
        foreach (var _ in Enumerable.Range(0, 10))
            ctx.Add(new ChatMessage { Role = "user", Content = new string('x', 5_708) });   // 57,080 chars

        ctx.RecordUsage(1_500);          // plausible: ~38 chars/token
        ctx.RecordUsage(249_125);        // the live garbage reading

        Assert.Equal(1_500, ctx.Used);   // the impossible one did not land
    }

    /// <summary>
    /// The first reading of a session has nothing to compare against, so the density check is the
    /// only thing standing between it and the trigger. A context of 371 characters cannot be 249,125
    /// tokens — that was the actual pairing in the log.
    /// </summary>
    [Fact]
    public void RecordUsage_RejectsAnImpossibleFirstReading()
    {
        var ctx = new AgentContext(window: 212_992);
        ctx.Add(new ChatMessage { Role = "user", Content = new string('x', 371) });

        ctx.RecordUsage(249_125);

        Assert.Null(ctx.Used);
    }

    /// <summary>
    /// A reading larger than the window it was served by cannot describe what the provider received.
    /// Kept separate from the density rule: a small context with a huge reading fails both, but a
    /// LARGE context can exceed the window while still looking dense enough to pass.
    /// </summary>
    [Fact]
    public void RecordUsage_RejectsAReadingBeyondTheWindow()
    {
        var ctx = new AgentContext(window: 100_000);
        foreach (var _ in Enumerable.Range(0, 100))
            ctx.Add(new ChatMessage { Role = "user", Content = new string('x', 10_000) });  // 1M chars

        ctx.RecordUsage(30_000);
        ctx.RecordUsage(150_000);        // 6.7 chars/token — plausible density, impossible total

        Assert.Equal(30_000, ctx.Used);
    }

    /// <summary>
    /// A DENSE BUT REAL reading still lands. Code, JSON and CJK all tokenize far below English prose,
    /// and rejecting them would silence the trigger on exactly the sessions that fill a window
    /// fastest. The gate is for the impossible, not the merely unusual.
    /// </summary>
    [Fact]
    public void RecordUsage_AcceptsADenseButPossibleReading()
    {
        var ctx = new AgentContext(window: 212_992);
        foreach (var _ in Enumerable.Range(0, 10))
            ctx.Add(new ChatMessage { Role = "user", Content = new string('x', 1_000) });   // 10,000 chars

        ctx.RecordUsage(8_000);          // 1.25 chars/token — dense, but a token is still >= 1 char

        Assert.Equal(8_000, ctx.Used);
    }

    /// <summary>
    /// THE TRIGGER IS THE REAL WINDOW, less a reserve. We know the window (it is configured, and
    /// shown in the panel) and we know occupancy, so a threshold derived from the actual limit beats
    /// any number picked by hand.
    /// </summary>
    [Fact]
    public void IsUnderPressure_IsTrue_WhenOccupancyReachesTheWindowLessTheReserve()
    {
        var ctx = new AgentContext(window: 100_000);
        ctx.Add(new ChatMessage { Role = "user", Content = new string('x', 400_000) });

        ctx.RecordUsage(84_000, atChars: 400_000);   // under 85% — room left
        Assert.False(ctx.IsUnderPressure);

        ctx.RecordUsage(86_000, atChars: 400_000);   // past the reserve
        Assert.True(ctx.IsUnderPressure);
    }

    /// <summary>
    /// NO READING, NO PRESSURE. If the provider does not report usage there is nothing here to
    /// compensate with — a character estimate would be guessing at the exact number it declined to
    /// give. Measured across 52 logged turns, every turn without a reading was turn 000 or 001, the
    /// opening exchanges before the first one arrives; a session never ran blind.
    /// </summary>
    [Fact]
    public void IsUnderPressure_IsFalse_WhenNoUsageHasBeenReported()
    {
        var ctx = new AgentContext(window: 100_000);
        ctx.Add(new ChatMessage { Role = "user", Content = new string('x', 5_000_000) });

        Assert.False(ctx.IsUnderPressure);
    }

    /// <summary>With no window there is nothing to be under pressure against — a percentage needs a
    /// denominator, and so does a ceiling.</summary>
    [Fact]
    public void IsUnderPressure_IsFalse_WithoutAWindow()
    {
        var ctx = new AgentContext(window: null);
        ctx.Add(new ChatMessage { Role = "user", Content = new string('x', 5_000_000) });

        Assert.False(ctx.IsUnderPressure);
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

        // A QUARTER TOKEN PER CHARACTER — i.e. 4 chars/token, ordinary English prose. This fixture
        // used 40 tokens per CHARACTER as arithmetic convenience, which RecordUsage now rejects as
        // impossible (a token is at minimum one character). The scaling under test is unchanged; only
        // the density is now one a real provider could report.
        ctx.RecordUsage(1_000, atChars: 4_000);

        // The conversation GREW after that reading (this turn's tool results), then compaction cut it
        // to 1,000 characters. The estimate must follow the surviving size, not the growth.
        ctx.Add(new ChatMessage { Role = "user", Content = new string('x', 1_000) });
        ctx.EstimateUsageAfterCompaction();

        Assert.Equal(250, ctx.Used);                  // 1,000 chars x 0.25 tokens/char
        Assert.True(ctx.IsEstimated);
    }

    /// <summary>A real measurement supersedes an estimate, and clears the marker.</summary>
    [Fact]
    public void RecordUsage_ClearsTheEstimatedMarker()
    {
        var ctx = new AgentContext(window: 100_000);
        ctx.Add(new ChatMessage { Role = "user", Content = new string('x', 4_000) });
        ctx.RecordUsage(1_000, atChars: 4_000);      // 4 chars/token — a density a provider can report
        ctx.EstimateUsageAfterCompaction();
        ctx.RecordUsage(1_200, atChars: 4_000);

        Assert.False(ctx.IsEstimated);
        Assert.Equal(1_200, ctx.Used);
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
