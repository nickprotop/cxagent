using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// What happens to text typed while a turn is running.
///
/// <para>The corruption this exists to prevent is real today: two <c>SendAsync</c> calls on one agent
/// append to a single live <c>Context.Messages</c> from two loops, and the second submission disposes
/// the FIRST turn's cancellation token — so pressing Enter twice does not start two turns, it breaks
/// the one that was running. Invisible only because turns currently last seconds.</para>
/// </summary>
public class PromptQueueTests
{
    /// <summary>
    /// TWO MESSAGES ARE APPENDED, NOT REPLACED — the whole point of queueing rather than dropping.
    ///
    /// <para>Two messages typed in quick succession are usually one thought completed: a correction
    /// and then its qualifier. Keeping only the last silently discards half of what someone said,
    /// with nothing on screen to say which half survived.</para>
    /// </summary>
    [Fact]
    public void Join_AppendsEveryMessage_InTheOrderTyped()
    {
        Assert.Equal("check the tests\nespecially the async ones",
            PromptQueue.Join(["check the tests", "especially the async ones"]));
    }

    /// <summary>Separate thoughts, so a line break rather than a space — structure a model reads as
    /// such.</summary>
    [Fact]
    public void Join_SeparatesWithNewline_NotASpace()
    {
        Assert.DoesNotContain("one two", PromptQueue.Join(["one", "two"]), StringComparison.Ordinal);
        Assert.Equal("one\ntwo", PromptQueue.Join(["one", "two"]));
    }

    [Fact]
    public void Join_OfOneMessage_IsThatMessage()
    {
        Assert.Equal("only this", PromptQueue.Join(["only this"]));
    }

    /// <summary>
    /// ESCAPE GIVES THE TEXT BACK. It was never sent, so cancelling the run must not eat it — the
    /// user gets it back editable and decides. Escape is how someone changes their mind; losing their
    /// words is the opposite of what they asked for.
    /// </summary>
    [Fact]
    public void Restore_PutsTheQueueInTheComposer()
    {
        Assert.Equal("queued one\nqueued two",
            PromptQueue.Restore(["queued one", "queued two"], composer: ""));
    }

    /// <summary>
    /// ABOVE anything already typed, because the queued lines were typed FIRST. Order is what makes
    /// the result readable as the sequence of thoughts it actually was.
    /// </summary>
    [Fact]
    public void Restore_PutsTheQueueAboveTextAlreadyInTheComposer()
    {
        Assert.Equal("typed while running\nhalf-written when I hit escape",
            PromptQueue.Restore(["typed while running"], composer: "half-written when I hit escape"));
    }

    /// <summary>An empty queue leaves whatever was being typed exactly as it is — Escape with nothing
    /// queued must not disturb the composer.</summary>
    [Fact]
    public void Restore_WithNothingQueued_LeavesTheComposerAlone()
    {
        Assert.Equal("mid-sentence", PromptQueue.Restore([], composer: "mid-sentence"));
        Assert.Equal("", PromptQueue.Restore([], composer: ""));
    }
}
