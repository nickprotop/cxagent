using CxAgent.Core.Llm;
using CxAgent.UI;
using SharpConsoleUI.Controls;
using Xunit;
using ChatMessageId = CxAgent.Core.Sessions.ChatMessageId;

namespace CxAgent.Tests;

/// <summary>
/// A step's thinking as its own block: placed where the spinner was, folded when the answer starts,
/// one block per step, and the latest opened or folded by Ctrl+O.
/// </summary>
public class ThinkingBlockTests
{
    private static (ChatTranscriptSink Sink, ChatTranscriptControl Chat) Sink()
    {
        var chat = new ChatTranscriptControl();
        return (ChatTranscriptSink.ForTest(chat), chat);
    }

    private static readonly ChatMessageId T1 = new(1);
    private static readonly ChatMessageId T2 = new(2);
    private static ThinkingSummary Sum(int seconds = 9, int words = 412) =>
        new(TimeSpan.FromSeconds(seconds), words, Hidden: false, Redacted: false);

    private static IEnumerable<SharpConsoleUI.Controls.ChatMessageId> Blocks(ChatTranscriptControl chat) =>
        chat.MessageIds.Where(i => chat.GetRole(i) == ChatRole.Tool);

    [Fact]
    public void Thinking_TakesTheSpinnersPlace_AndTheAnswerLandsBelowIt()
    {
        var (sink, chat) = Sink();
        sink.AssistantTurnBegan(T1);
        sink.AssistantThinkingAppended(T1, "Look at prices.py");
        sink.AssistantThinkingEnded(T1, Sum());
        sink.AssistantTextAppended(T1, "Fixed.");
        sink.AssistantTurnEnded(T1);

        var ids = chat.MessageIds;
        Assert.Equal(2, ids.Count);
        Assert.Equal(ChatRole.Tool, chat.GetRole(ids[0]));
        Assert.Equal(ChatRole.Assistant, chat.GetRole(ids[1]));
        Assert.False(chat.IsExpanded(ids[0]));
    }

    [Fact]
    public void WhileThinking_TheBlockIsOpen()
    {
        var (sink, chat) = Sink();
        sink.AssistantTurnBegan(T1);
        sink.AssistantThinkingAppended(T1, "hmm");
        Assert.True(chat.IsExpanded(Assert.Single(Blocks(chat))));
    }

    [Fact]
    public void FoldedHeader_SaysDurationAndWords() =>
        Assert.Equal("◆ Thought for 9s · 412 words", ChatTranscriptSink.FoldedHeader(Sum()));

    [Fact]
    public void FoldedHeader_Hidden_HasNoWordCount() =>
        Assert.Equal("◆ Thought for 4s",
            ChatTranscriptSink.FoldedHeader(new(TimeSpan.FromSeconds(4), 0, Hidden: true, Redacted: false)));

    [Fact]
    public void FoldedHeader_Redacted_SaysSo() =>
        Assert.Equal("◆ Thought for 4s (redacted)",
            ChatTranscriptSink.FoldedHeader(new(TimeSpan.FromSeconds(4), 0, Hidden: true, Redacted: true)));

    [Fact]
    public void FoldedHeader_UnderASecond_SaysOne() =>
        Assert.Equal("◆ Thought for 1s · 2 words",
            ChatTranscriptSink.FoldedHeader(new(TimeSpan.FromMilliseconds(200), 2, false, false)));

    [Fact]
    public void LiveHeader_CountsSeconds() =>
        Assert.Equal("◆ Thinking · 6s", ChatTranscriptSink.LiveHeader(TimeSpan.FromSeconds(6.7)));

    // A STEP THAT ONLY CALLS TOOLS leaves its folded block and nothing else — no empty answer.
    [Fact]
    public void ThinkingThenNoText_LeavesOnlyTheFoldedBlock()
    {
        var (sink, chat) = Sink();
        sink.AssistantTurnBegan(T1);
        sink.AssistantThinkingAppended(T1, "call grep");
        sink.AssistantThinkingEnded(T1, Sum());
        sink.AssistantTurnEnded(T1);

        var id = Assert.Single(chat.MessageIds);
        Assert.Equal(ChatRole.Tool, chat.GetRole(id));
        Assert.False(chat.IsExpanded(id));
    }

    // A NOTE BEFORE A TOOL CALL comes after the step's text and joins the step's one block.
    [Fact]
    public void ThinkingAfterText_JoinsTheSameBlock()
    {
        var (sink, chat) = Sink();
        sink.AssistantTurnBegan(T1);
        sink.AssistantThinkingAppended(T1, "a");
        sink.AssistantThinkingEnded(T1, Sum());
        sink.AssistantTextAppended(T1, "b");
        sink.AssistantThinkingAppended(T1, "c");
        sink.AssistantThinkingEnded(T1, Sum());
        sink.AssistantTurnEnded(T1);

        Assert.Equal(2, chat.MessageIds.Count);
        Assert.Single(Blocks(chat));
    }

    [Fact]
    public void HiddenThinking_StillMakesABlock()
    {
        var (sink, chat) = Sink();
        sink.AssistantTurnBegan(T1);
        sink.AssistantThinkingEnded(T1, new(TimeSpan.FromSeconds(4), 0, Hidden: true, Redacted: false));
        sink.AssistantTextAppended(T1, "ok");
        sink.AssistantTurnEnded(T1);

        Assert.Single(Blocks(chat));
    }

    [Fact]
    public void NoThinking_IsExactlyAsBefore()
    {
        var (sink, chat) = Sink();
        sink.AssistantTurnBegan(T1);
        sink.AssistantTextAppended(T1, "hi");
        sink.AssistantTurnEnded(T1);
        Assert.Equal(ChatRole.Assistant, chat.GetRole(Assert.Single(chat.MessageIds)));
    }

    [Fact]
    public void ALabel_SetBeforeThinking_ReachesTheAnswer()
    {
        var (sink, chat) = Sink();
        sink.AssistantTurnBegan(T1);
        sink.AssistantLabelled(T1, "planner");
        sink.AssistantThinkingAppended(T1, "a");
        sink.AssistantThinkingEnded(T1, Sum());
        sink.AssistantTextAppended(T1, "b");
        Assert.Equal(ChatRole.Assistant, chat.GetRole(chat.MessageIds[^1]));
        Assert.Equal("planner", sink.HeaderForTest(T1));
    }

    [Fact]
    public void CtrlO_TogglesOnlyTheLatestBlock()
    {
        var (sink, chat) = Sink();
        sink.AssistantTurnBegan(T1);
        sink.AssistantThinkingAppended(T1, "a");
        sink.AssistantThinkingEnded(T1, Sum());
        sink.AssistantTurnBegan(T2);
        sink.AssistantThinkingAppended(T2, "b");
        sink.AssistantThinkingEnded(T2, Sum());
        var blocks = Blocks(chat).ToList();

        sink.ToggleLatestThinking();
        Assert.False(chat.IsExpanded(blocks[0]));
        Assert.True(chat.IsExpanded(blocks[1]));

        sink.ToggleLatestThinking();
        Assert.False(chat.IsExpanded(blocks[1]));
    }

    [Fact]
    public void CtrlO_WithNoThinking_DoesNothing()
    {
        var (sink, chat) = Sink();
        sink.ToggleLatestThinking();
        Assert.Empty(chat.MessageIds);
    }

    [Fact]
    public void AHandOpenedBlock_StaysOpen_WhenTheNextStepFolds()
    {
        var (sink, chat) = Sink();
        sink.AssistantTurnBegan(T1);
        sink.AssistantThinkingAppended(T1, "a");
        sink.AssistantThinkingEnded(T1, Sum());
        var first = Assert.Single(Blocks(chat));
        chat.SetExpanded(first, true);

        sink.AssistantTurnBegan(T2);
        sink.AssistantThinkingAppended(T2, "b");
        sink.AssistantThinkingEnded(T2, Sum());
        Assert.True(chat.IsExpanded(first));
    }

    [Fact]
    public void Forget_DropsTheBlocks_SoCtrlOTouchesNothingCleared()
    {
        var (sink, chat) = Sink();
        sink.AssistantTurnBegan(T1);
        sink.AssistantThinkingAppended(T1, "a");
        sink.AssistantThinkingEnded(T1, Sum());
        chat.Clear();
        sink.Forget();
        sink.ToggleLatestThinking();   // must not touch an id the transcript no longer has
    }

    // A SAFETY NET: whatever cut a turn short, a block still live when the turn ends folds.
    [Fact]
    public void ABlockStillLive_WhenTheTurnEnds_Folds()
    {
        var (sink, chat) = Sink();
        sink.AssistantTurnBegan(T1);
        sink.AssistantThinkingAppended(T1, "weighing it");
        sink.AssistantTurnEnded(T1);

        var block = Assert.Single(Blocks(chat));
        Assert.False(chat.IsExpanded(block));
        Assert.Equal(0, sink.LiveBlocksForTest);
    }
}
