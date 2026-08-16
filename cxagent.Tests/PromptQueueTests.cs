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
    /// ESCAPE GIVES THE TEXT BACK. It was never sent, so cancelling the run must not eat it — the
    /// user gets it back editable and decides. Escape is how someone changes their mind; losing their
    /// words is the opposite of what they asked for.
    /// </summary>
    [Fact]
    public void Restore_PutsThePendingTextInTheComposer()
    {
        // ALREADY JOINED WHEN IT ARRIVES. Session.Steer appends as each line is typed, so what comes
        // back is one message that happens to contain newlines — this must not re-join anything, and
        // taking a string rather than a collection is what makes that unexpressible.
        Assert.Equal("queued one\nqueued two",
            PromptQueue.Restore("queued one\nqueued two", composer: ""));
    }

    /// <summary>
    /// ABOVE anything already typed, because the queued lines were typed FIRST. Order is what makes
    /// the result readable as the sequence of thoughts it actually was.
    /// </summary>
    [Fact]
    public void Restore_PutsTheQueueAboveTextAlreadyInTheComposer()
    {
        Assert.Equal("typed while running\nhalf-written when I hit escape",
            PromptQueue.Restore("typed while running", composer: "half-written when I hit escape"));
    }

    /// <summary>Nothing pending leaves whatever was being typed exactly as it is — Escape with an
    /// empty queue must not disturb the composer.</summary>
    [Fact]
    public void Restore_WithNothingPending_LeavesTheComposerAlone()
    {
        // NULL AND EMPTY BOTH, because TakePendingSteer returns null when nothing was typed and the
        // caller passes it straight through rather than testing it twice.
        Assert.Equal("mid-sentence", PromptQueue.Restore(null, composer: "mid-sentence"));
        Assert.Equal("mid-sentence", PromptQueue.Restore("", composer: "mid-sentence"));
        Assert.Equal("", PromptQueue.Restore(null, composer: ""));
    }
}
