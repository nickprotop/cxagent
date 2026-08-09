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

    /// <summary>After compaction the last reading no longer describes the conversation.</summary>
    [Fact]
    public void InvalidateUsage_DropsTheStaleReading()
    {
        var ctx = new AgentContext(window: 100_000);
        ctx.RecordUsage(40_000);
        ctx.InvalidateUsage();

        Assert.Null(ctx.Used);
        Assert.Null(ctx.UsedFraction);
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
