using CxAgent.Core.Agents;
using CxAgent.UI;
using SharpConsoleUI.Controls;
using Xunit;

namespace CxAgent.Tests;

public class AsideTranscriptSinkTests
{
    /// <summary>
    /// /clear wipes the transcript while an aside may still be finishing. Its late writes have no
    /// block to land in, and are dropped rather than thrown on the UI thread or re-created.
    /// </summary>
    [Fact]
    public void WritesToABlockTheTranscriptCleared_AreDropped()
    {
        var chat = new ChatTranscriptControl();
        var sink = AsideTranscriptSink.ForTest(chat);

        sink.Asked(1, "q");
        Assert.Single(chat.MessageIds);

        chat.Clear();
        sink.AnswerAppended(1, "late");
        sink.Finished(1, AsideEnd.Stopped, null);

        Assert.Empty(chat.MessageIds);
    }

    /// <summary>
    /// ONE BLOCK PER /btw, ALWAYS IN VIEW. A follow-up asked after the main agent has written below the
    /// block would land out of sight, so the block moves to the bottom, carrying the whole thread.
    /// </summary>
    [Fact]
    public void AFollowUp_AfterTheMainAgentWroteBelow_MovesTheBlockToTheBottom()
    {
        var chat = new ChatTranscriptControl();
        var sink = AsideTranscriptSink.ForTest(chat);

        sink.Asked(1, "one");
        sink.AnswerAppended(1, "answer to one");
        chat.AddMessage(ChatRole.Assistant, "the main agent, still working");

        sink.Asked(1, "two");
        sink.AnswerAppended(1, "answer to two");

        Assert.Equal(2, chat.MessageIds.Count);
        Assert.Equal(ChatRole.Assistant, chat.GetRole(chat.MessageIds[0]));
        Assert.Equal(ChatRole.Error, chat.GetRole(chat.MessageIds[^1]));
        var thread = sink.ThreadTextForTest(1);
        Assert.True(thread.IndexOf("answer to one", StringComparison.Ordinal)
                    < thread.IndexOf("two", thread.IndexOf("answer to one", StringComparison.Ordinal), StringComparison.Ordinal));
        Assert.EndsWith("answer to two", thread);
    }

    /// <summary>A follow-up's question lands in the block its aside already opened.</summary>
    [Fact]
    public void AFollowUp_WritesIntoTheSameBlock()
    {
        var chat = new ChatTranscriptControl();
        var sink = AsideTranscriptSink.ForTest(chat);

        sink.Asked(1, "one");
        sink.AnswerAppended(1, "answer");
        sink.Asked(1, "two");
        sink.Asked(2, "a new aside");

        Assert.Equal(2, chat.MessageIds.Count);
    }
}
