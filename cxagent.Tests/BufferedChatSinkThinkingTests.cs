using CxAgent.Core.Llm;
using CxAgent.Core.Sessions;
using Xunit;

namespace CxAgent.Tests;

/// <summary>A child's latest thought, for its row in the parent's transcript.</summary>
public class BufferedChatSinkThinkingTests
{
    private static readonly ChatMessageId Step = new(1);

    [Fact]
    public void LatestThinkingLine_IsTheLastNonEmptyLine_AcrossDeltas()
    {
        var s = new BufferedChatSink();
        s.AssistantThinkingAppended(Step, "first line\nsecond ");
        s.AssistantThinkingAppended(Step, "half\n");
        Assert.Equal("second half", s.LatestThinkingLine);
    }

    [Fact]
    public void LatestThinkingLine_IsNull_OnceTheRunEnds()
    {
        var s = new BufferedChatSink();
        s.AssistantThinkingAppended(Step, "considering");
        s.AssistantThinkingEnded(Step, new ThinkingSummary(TimeSpan.FromSeconds(1), 1, false, false));
        Assert.Null(s.LatestThinkingLine);
    }

    [Fact]
    public void Thinking_StaysInTheTranscript_AndOutOfTheBody()
    {
        var s = new BufferedChatSink();
        s.AssistantThinkingAppended(Step, "why");
        s.AssistantTextAppended(Step, "answer");
        Assert.Contains("why", s.Transcript);
        Assert.Equal("answer", s.Body);
    }
}
