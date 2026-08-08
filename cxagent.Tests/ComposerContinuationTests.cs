using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Enter submits; a line ending in a backslash continues onto the next.
///
/// <para>Shift+Enter was the first answer and it is not deliverable: most Unix terminals send a bare
/// '\r' for Enter with no modifier bits, so Shift+Enter and Enter arrive as the same byte. It was
/// documented in three places and reachable in none. A trailing backslash needs no modifier to
/// survive — it is in the TEXT — which is why the shell has used it for decades.</para>
/// </summary>
public class ComposerContinuationTests
{
    [Fact]
    public void ATrailingBackslashContinuesTheLine()
    {
        Assert.Equal("first\n", AppBootstrap.ComposerContinuationForTest("first\\"));
    }

    [Fact]
    public void TheBackslashIsConsumed_ItIsPunctuationNotContent()
    {
        // The marker tells the editor what to do; it is not part of the goal the model receives.
        var continued = AppBootstrap.ComposerContinuationForTest("write a haiku\\");

        Assert.NotNull(continued);
        Assert.DoesNotContain("\\", continued!, System.StringComparison.Ordinal);
    }

    [Fact]
    public void TrailingWhitespaceAfterTheBackslashStillContinues()
    {
        // A stray space after the backslash is invisible. Without this the goal would SUBMIT rather
        // than continue — a difference the user cannot see and cannot undo.
        Assert.Equal("first\n", AppBootstrap.ComposerContinuationForTest("first\\   "));
    }

    [Fact]
    public void OrdinaryTextSubmits()
    {
        Assert.Null(AppBootstrap.ComposerContinuationForTest("just a goal"));
    }

    [Fact]
    public void EmptyAndNullSubmit()
    {
        Assert.Null(AppBootstrap.ComposerContinuationForTest(""));
        Assert.Null(AppBootstrap.ComposerContinuationForTest(null));
    }

    [Fact]
    public void AnESCAPEDBackslashIsNotAContinuation()
    {
        // "C:\path\\" ends in a literal backslash the user typed on purpose. Only an ODD number of
        // them is the continuation marker, exactly as a shell reads it — otherwise a Windows path
        // could never be the last thing on a submitted line.
        Assert.Null(AppBootstrap.ComposerContinuationForTest(@"C:\path\\"));
    }

    [Fact]
    public void AnOddRunOfBackslashesStillContinues()
    {
        // Three is odd: the first two escape each other and SURVIVE as one literal pair, the third is
        // the marker and is consumed. My first expectation here dropped two, which would have quietly
        // eaten a character of the user's text — the exact class of bug the escape rule exists to
        // prevent.
        Assert.Equal("C:\\path\\\\\n", AppBootstrap.ComposerContinuationForTest("C:\\path\\\\\\"));
    }

    [Fact]
    public void ASecondContinuationAppendsToWhatIsAlreadyThere()
    {
        // The composer's content grows line by line; each Enter operates on the whole buffer.
        Assert.Equal("one\ntwo\n", AppBootstrap.ComposerContinuationForTest("one\ntwo\\"));
    }
}
