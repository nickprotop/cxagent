using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The agent's context and the two things that make room in it.
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

    /// <summary>A replace_in_file call — its result echoes the file back, so it is a snapshot too.</summary>
    private static ChatMessage Edit(string id, string path) => new()
    {
        Role = "assistant", Content = "",
        ToolCalls = [new ToolCall
        {
            Name = "replace_in_file", Id = id,
            Arguments = System.Text.Json.JsonDocument.Parse($$"""{"path":"{{path}}"}""").RootElement,
        }],
    };

    /// <summary>A read_file call naming a path — what deduplication keys on.</summary>
    private static ChatMessage Read(string id, string path) => new()
    {
        Role = "assistant", Content = "",
        ToolCalls = [new ToolCall
        {
            Name = "read_file", Id = id,
            Arguments = System.Text.Json.JsonDocument.Parse($$"""{"path":"{{path}}"}""").RootElement,
        }],
    };
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
    /// Pruning empties the BODY and leaves the message — which is what makes it safe. Removing tool
    /// messages is what creates orphans; this cannot, because nothing is removed.
    /// </summary>
    [Fact]
    public void Prune_ClearsSupersededBodiesButKeepsPairingIntact()
    {
        // The same file read three times: the first two are superseded by the third.
        var messages = new List<ChatMessage> { User("goal") };
        for (var i = 0; i < 3; i++)
        {
            messages.Add(Read($"t{i}", "a.cs"));
            messages.Add(Result($"t{i}", new string('x', 30_000)));
        }
        var countBefore = messages.Count;

        var result = ToolOutputPruner.Prune(messages, minimumGain: 1_000);

        Assert.True(result.Pruned);
        Assert.Equal(countBefore, messages.Count);              // nothing removed
        foreach (var m in messages.Where(m => m.ToolCallId is not null))
            Assert.Contains(messages, k => k.ToolCalls?.Any(c => c.Id == m.ToolCallId) == true);
        Assert.Contains(messages, m => m.Content == ToolOutputPruner.Tombstone);
    }

    /// <summary>
    /// THE POINT OF DEDUPLICATION. The freshest copy of a file is the one the model is working from
    /// and is never cleared, however large — only the copies something later replaced.
    /// </summary>
    [Fact]
    public void Prune_NeverClearsTheFreshestCopy()
    {
        var messages = new List<ChatMessage> { User("goal") };
        for (var i = 0; i < 3; i++)
        {
            messages.Add(Read($"t{i}", "a.cs"));
            messages.Add(Result($"t{i}", new string('x', 20_000)));
        }

        ToolOutputPruner.Prune(messages, minimumGain: 1_000);

        Assert.NotEqual(ToolOutputPruner.Tombstone, messages[^1].Content);   // newest read survives
        Assert.Equal(ToolOutputPruner.Tombstone, messages[2].Content);       // superseded one cleared
    }

    /// <summary>
    /// A file read ONCE is not superseded by anything, so it is never cleared no matter how big or
    /// old. This is what makes the rule lossless — and it is the whole difference from clearing by
    /// age, which would discard this content unread.
    /// </summary>
    [Fact]
    public void Prune_KeepsResultsThatNothingSupersedes()
    {
        var messages = new List<ChatMessage> { User("goal") };
        foreach (var (id, path) in new[] { ("t0", "a.cs"), ("t1", "b.cs"), ("t2", "c.cs") })
        {
            messages.Add(Read(id, path));
            messages.Add(Result(id, new string('x', 40_000)));
        }

        var result = ToolOutputPruner.Prune(messages, minimumGain: 1);

        Assert.False(result.Pruned, "cleared a result that nothing supersedes");
        Assert.DoesNotContain(messages, m => m.Content == ToolOutputPruner.Tombstone);
    }

    /// <summary>
    /// AN EDIT SUPERSEDES THE READ BEFORE IT. A replace_in_file result echoes the file's new contents
    /// ("…the file now reads: …"), so it is a fresh copy of what the read produced — and the read is
    /// now not merely redundant but WRONG, describing a state of the tree that no longer exists.
    /// Covering reads only left an edit-heavy session accumulating a full copy per edit, which is the
    /// session shape this exists for.
    /// </summary>
    [Fact]
    public void Prune_TreatsAnEditAsSupersedingAnEarlierRead()
    {
        var messages = new List<ChatMessage>
        {
            User("goal"),
            Read("t0", "a.cs"), Result("t0", new string('x', 30_000)),
            Edit("t1", "a.cs"), Result("t1", new string('y', 30_000)),
        };

        var result = ToolOutputPruner.Prune(messages, minimumGain: 1_000);

        Assert.True(result.Pruned, "an edit did not supersede the read that preceded it");
        Assert.Equal(ToolOutputPruner.Tombstone, messages[2].Content);        // the stale read
        Assert.NotEqual(ToolOutputPruner.Tombstone, messages[^1].Content);    // the edit's echo
    }

    /// <summary>
    /// Only SNAPSHOTS are deduplicated. Two searches of one path are different questions, so a later
    /// one does not replace an earlier one's answer.
    /// </summary>
    [Fact]
    public void Prune_DoesNotDeduplicateSearches()
    {
        var messages = new List<ChatMessage> { User("goal") };
        for (var i = 0; i < 3; i++)
        {
            messages.Add(new ChatMessage
            {
                Role = "assistant", Content = "",
                ToolCalls = [new ToolCall
                {
                    Name = "search_files", Id = $"s{i}",
                    Arguments = System.Text.Json.JsonDocument.Parse("""{"path":"src"}""").RootElement,
                }],
            });
            messages.Add(Result($"s{i}", new string('x', 40_000)));
        }

        var result = ToolOutputPruner.Prune(messages, minimumGain: 1);

        Assert.False(result.Pruned, "deduplicated searches, which are not snapshots of one thing");
    }

    /// <summary>
    /// Rewriting history invalidates the prompt cache from the first changed message on, so a prune
    /// that reclaims a trivial amount costs more than it saves.
    /// </summary>
    [Fact]
    public void Prune_DoesNothingWhenTheGainIsTrivial()
    {
        var messages = new List<ChatMessage>
        {
            User("goal"),
            Read("t0", "a.cs"), Result("t0", "small"),
            Read("t1", "a.cs"), Result("t1", "small"),
        };

        var result = ToolOutputPruner.Prune(messages, minimumGain: 10_000);

        Assert.False(result.Pruned);
        Assert.Equal("small", messages[^1].Content);
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
