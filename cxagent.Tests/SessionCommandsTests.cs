using CxAgent.Core.Models;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

public class SessionCommandsTests
{
    private static ChatMessage Msg(string role, string content) =>
        new() { Role = role, Content = content };

    [Fact]
    public void Clear_EmptiesTheConversation_AndSaysSo()
    {
        var conversation = new List<ChatMessage> { Msg("user", "old goal"), Msg("assistant", "old answer") };

        Assert.True(SessionCommands.TryHandle("/clear", conversation, out var reply));

        Assert.Empty(conversation);
        Assert.Contains("cleared", reply, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compress_IsRecognised_ButLeftToTheCallerToSummarise()
    {
        // /compress means COMPRESS, not truncate. TryHandle is synchronous (a key handler calls it)
        // and summarising needs a provider call, so the command is RECOGNISED here and serviced by
        // the caller via SessionCompressor.CompressAsync — the same routine auto-compression uses,
        // so the manual and automatic paths can never diverge.
        //
        // Truncation (SessionCommands.Compress, still below) survives only as the FALLBACK when the
        // summarisation call fails.
        var conversation = new List<ChatMessage>();
        for (int i = 0; i < 20; i++) conversation.Add(Msg("user", $"goal-{i:D2}"));

        Assert.Equal(CommandOutcome.NeedsProvider, SessionCommands.Match("/compress")?.Outcome);
        Assert.True(SessionCommands.TryHandle("/compress", conversation, out _));

        // TryHandle must NOT truncate it — that would race the caller's summarisation and delete the
        // very turns the summary is about to be built from.
        Assert.Equal(20, conversation.Count);
    }

    [Fact]
    public void CompressDoesNotMatchAnOrdinaryGoal()
    {
        // Same false-positive rule as the other commands: "compress the log files" is a GOAL.
        Assert.Null(SessionCommands.Match("compress the log files"));
        Assert.Null(SessionCommands.Match("what does /compress do?"));
    }

    [Fact]
    public void Compress_OnAShortConversation_KeepsEverything()
    {
        // The principle, as a test. Compression is LOSSY; running it on a conversation that fits costs
        // the user context for nothing. Nothing should be dropped just because the command was typed.
        var conversation = new List<ChatMessage> { Msg("user", "one goal"), Msg("assistant", "one answer") };

        SessionCommands.TryHandle("/compress", conversation, out _);

        Assert.Equal(2, conversation.Count);
    }

    [Fact]
    public void AnOrdinaryGoalIsNotACommand()
    {
        // "/clear" is a command; "clear the build output" is a GOAL. Only an exact leading-slash token
        // counts, or a user loses work to a false positive.
        var conversation = new List<ChatMessage>();

        Assert.False(SessionCommands.TryHandle("clear the build output", conversation, out _));
        Assert.False(SessionCommands.TryHandle("what does /clear do?", conversation, out _));
    }

    [Fact]
    public void AnUnknownSlashCommandIsReported_NotRunAsAGoal()
    {
        // A typo'd command must not become a goal — "/claer" would otherwise be planned and executed.
        Assert.True(SessionCommands.TryHandle("/claer", new List<ChatMessage>(), out var reply));
        Assert.Contains("/clear", reply);   // names the real commands
    }
}
