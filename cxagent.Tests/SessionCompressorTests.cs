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
    public async Task Compress_OnAShortConversation_StillDoesNothing()
    {
        // The floor survives: compression is lossy even when it summarises, and a conversation that
        // fits should not pay a provider call to shrink.
        var provider = new RecordingProvider();   // no scripted reply — a call would throw
        var conversation = new List<ChatMessage> { Msg("user", "one"), Msg("assistant", "two") };

        await SessionCompressor.CompressAsync(conversation, provider, CancellationToken.None);

        Assert.Equal(2, conversation.Count);
    }
}
