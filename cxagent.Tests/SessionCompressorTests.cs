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
        var conversation = new List<ChatMessage>();
        for (int i = 0; i < 12; i++) conversation.Add(Msg("user", $"goal-{i:D2}"));

        await SessionCompressor.CompressAsync(conversation, provider, CancellationToken.None);

        var text = string.Concat(conversation.Select(m => m.Content));
        Assert.Contains("4 ending .sh", text);        // the FINDING survived
        Assert.True(conversation.Count < 12);          // ...and the conversation actually shrank
    }

    [Fact]
    public async Task Compress_KeepsTheMostRecentTurnsVERBATIM()
    {
        // The newest turns are the likeliest referent. Summarising them too would lose the precision a
        // follow-up depends on.
        var provider = new RecordingProvider(Usage(new LlmResponse { Text = "summary" }));
        var conversation = new List<ChatMessage>();
        for (int i = 0; i < 12; i++) conversation.Add(Msg("user", $"goal-{i:D2}"));

        await SessionCompressor.CompressAsync(conversation, provider, CancellationToken.None);

        Assert.Contains(conversation, m => m.Content.Contains("goal-11"));
    }

    [Fact]
    public async Task Compress_WhenSummarisationFails_FallsBackToTruncation_AndSaysSo()
    {
        // A housekeeping failure must not kill a working session — but it must not be silent either.
        // Truncation is the fallback, never the primary.
        var provider = new ThrowingProvider();
        var conversation = new List<ChatMessage>();
        for (int i = 0; i < 12; i++) conversation.Add(Msg("user", $"goal-{i:D2}"));

        var result = await SessionCompressor.CompressAsync(conversation, provider, CancellationToken.None);

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
        var conversation = new List<ChatMessage> { Msg("user", "one") };

        await SessionCompressor.CompressAsync(conversation, provider, CancellationToken.None);

        Assert.Single(conversation);
    }

    [Fact]
    public async Task Compress_OnAShortConversation_NowCompressesIt()
    {
        // The other half of removing the floor: a short conversation the user asked to compress IS
        // compressed. Two messages is the smallest thing that can be halved at all.
        var provider = new RecordingProvider(
            Usage(new LlmResponse { Text = "read Foo.cs, changed the parser" }));
        var conversation = new List<ChatMessage> { Msg("user", "one"), Msg("assistant", "two") };

        var result = await SessionCompressor.CompressAsync(conversation, provider, CancellationToken.None);

        Assert.True(result.Summarised);
        Assert.Equal(2, conversation.Count);   // one summary + the newest half
        Assert.Contains("changed the parser", conversation[0].Content, System.StringComparison.Ordinal);
    }
}
