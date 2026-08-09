using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// P11 Task 3: SUMMARISE the oldest turns instead of deleting them. Truncation deleted four goals'
/// worth of findings outright on a live drive — nothing condensed, everything discarded. A summary
/// preserves the referent a follow-up needs ("earlier: listed ~/bin, 6 files, 4 ending .sh"); deletion
/// loses it entirely.
/// </summary>
public class SessionCompressorTests
{
    private static ChatMessage Msg(string role, string content) =>
        new() { Role = role, Content = content };

    private static LlmResponse Usage(LlmResponse r) =>
        r with { Usage = new LlmUsage { InputTokens = 10, OutputTokens = 5 } };

    [Fact]
    public async Task Compress_ReplacesOldTurnsWithASummary_NotWithNothing()
    {
        // Truncation deleted four goals' worth of findings on a live drive. A summary preserves the
        // referent a follow-up needs: "earlier: listed ~/bin, 6 files, 4 ending .sh".
        var provider = new RecordingProvider(
            Usage(new LlmResponse { Text = "Earlier: listed ~/bin, found 6 files, 4 ending .sh." }));
        var context = new AgentContext();
        var conversation = context.Messages;
        for (int i = 0; i < 12; i++) conversation.Add(Msg("user", $"goal-{i:D2}"));

        await SessionCompressor.CompressAsync(context, provider, CancellationToken.None);

        var text = string.Concat(conversation.Select(m => m.Content));
        Assert.Contains("4 ending .sh", text);        // the FINDING survived
        Assert.True(conversation.Count < 12);          // ...and the conversation actually shrank
    }

    /// <summary>
    /// The working-directory preamble outlives compression.
    ///
    /// <para>It is the message that stops the model guessing absolute paths — measured before it
    /// existed, ten of twenty shell calls hunted for paths that do not exist on this machine
    /// (/Users/&lt;someone&gt;/…, /home/user, bare /). Compression removed from index 0, which is
    /// exactly where it sits, so the FIRST compaction of any session deleted it.</para>
    /// </summary>
    [Fact]
    public async Task Compress_KeepsTheSystemPreamble_AtTheHead()
    {
        var provider = new RecordingProvider(Usage(new LlmResponse { Text = "summary" }));
        var context = new AgentContext();
        var conversation = context.Messages;
        conversation.Add(Msg("system", "Your working directory is /home/nick/source/cxagent."));
        for (int i = 0; i < 12; i++) conversation.Add(Msg("user", $"goal-{i:D2}"));

        await SessionCompressor.CompressAsync(context, provider, CancellationToken.None);

        Assert.Equal("system", conversation[0].Role);
        Assert.Contains("working directory", conversation[0].Content);

        // ...and the summary landed BELOW it, not on top of it.
        Assert.Contains(conversation.Skip(1), m => m.Content.Contains("summary"));
    }

    /// <summary>
    /// The truncation fallback must pin the head too. SessionCommands.Compress also removed from
    /// index 0, so a summarisation failure destroyed the preamble by the other route — the same bug
    /// reached by the path taken when things are already going wrong.
    /// </summary>
    [Fact]
    public async Task Compress_WhenSummarisationFails_StillKeepsTheSystemPreamble()
    {
        var provider = new ThrowingProvider();
        var context = new AgentContext();
        var conversation = context.Messages;
        conversation.Add(Msg("system", "Your working directory is /home/nick/source/cxagent."));
        for (int i = 0; i < 12; i++) conversation.Add(Msg("user", $"goal-{i:D2}"));

        await SessionCompressor.CompressAsync(context, provider, CancellationToken.None);

        Assert.Equal("system", conversation[0].Role);
        Assert.Contains("working directory", conversation[0].Content);
    }

    /// <summary>
    /// No preamble, no pin. A sub-agent or a test context that never had a system message must still
    /// compress from the very front rather than mysteriously keeping its oldest user turn.
    /// </summary>
    [Fact]
    public async Task Compress_WithNoSystemMessage_StillCompressesFromTheFront()
    {
        var provider = new RecordingProvider(Usage(new LlmResponse { Text = "summary" }));
        var context = new AgentContext();
        var conversation = context.Messages;
        for (int i = 0; i < 12; i++) conversation.Add(Msg("user", $"goal-{i:D2}"));

        await SessionCompressor.CompressAsync(context, provider, CancellationToken.None);

        Assert.DoesNotContain(conversation, m => m.Content.Contains("goal-00"));
        Assert.Contains("summary", conversation[0].Content);
    }

    [Fact]
    public async Task Compress_KeepsTheMostRecentTurnsVERBATIM()
    {
        // The newest turns are the likeliest referent. Summarising them too would lose the precision a
        // follow-up depends on.
        var provider = new RecordingProvider(Usage(new LlmResponse { Text = "summary" }));
        var context = new AgentContext();
        var conversation = context.Messages;
        for (int i = 0; i < 12; i++) conversation.Add(Msg("user", $"goal-{i:D2}"));

        await SessionCompressor.CompressAsync(context, provider, CancellationToken.None);

        Assert.Contains(conversation, m => m.Content.Contains("goal-11"));
    }

    [Fact]
    public async Task Compress_WhenSummarisationFails_FallsBackToTruncation_AndSaysSo()
    {
        // A housekeeping failure must not kill a working session — but it must not be silent either.
        // Truncation is the fallback, never the primary.
        var provider = new ThrowingProvider();
        var context = new AgentContext();
        var conversation = context.Messages;
        for (int i = 0; i < 12; i++) conversation.Add(Msg("user", $"goal-{i:D2}"));

        var result = await SessionCompressor.CompressAsync(context, provider, CancellationToken.None);

        Assert.True(conversation.Count < 12);           // it still shrank
        Assert.False(result.Summarised);                 // ...but the caller can tell it degraded
    }

    [Fact]
    public async Task Compress_OnASingleMessage_DoesNothing()
    {
        // REPLACES Compress_OnAShortConversation_StillDoesNothing, which pinned a message-count floor
        // of eight. That floor was wrong for both callers: this runs on an explicit /compress or on
        // measured TOKEN pressure, and a count of messages answers neither — eight messages carrying
        // four large file reads is exactly the case that needs compressing, and the floor declined it
        // silently, indistinguishable from a compression that found nothing to do.
        //
        // What remains is arithmetic: one message has no older half to summarise.
        var provider = new RecordingProvider();   // no scripted reply — a call would throw
        var context = new AgentContext();
        context.Add(Msg("user", "one"));
        var conversation = context.Messages;

        await SessionCompressor.CompressAsync(context, provider, CancellationToken.None);

        Assert.Single(conversation);
    }

    [Fact]
    public async Task Compress_OnAShortConversation_NowCompressesIt()
    {
        // The other half of removing the floor: a short conversation the user asked to compress IS
        // compressed. Two messages is the smallest thing that can be halved at all.
        var provider = new RecordingProvider(
            Usage(new LlmResponse { Text = "read Foo.cs, changed the parser" }));
        var context = new AgentContext();
        context.AddRange([Msg("user", "one"), Msg("assistant", "two")]);
        var conversation = context.Messages;

        var result = await SessionCompressor.CompressAsync(context, provider, CancellationToken.None);

        Assert.True(result.Summarised);
        Assert.Equal(2, conversation.Count);   // one summary + the newest half
        Assert.Contains("changed the parser", conversation[0].Content, System.StringComparison.Ordinal);
    }
}
