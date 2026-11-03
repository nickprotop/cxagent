using CxAgent.UI;
using SharpConsoleUI.Controls;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// A long paste becomes one token in the composer: <c>[Pasted text #1 +18 lines]</c> in the box, the
/// whole text in what is sent.
/// </summary>
public class PasteTokenRuleTests
{
    private static string Lines(int n) => string.Join("\n", Enumerable.Range(1, n).Select(i => $"line {i}"));

    [Theory]
    [InlineData("git status")]
    [InlineData("first\nsecond")]
    [InlineData("first\nsecond\n")]     // a trailing newline does not make a third line
    public void AShortPaste_StaysText(string pasted)
    {
        Assert.Null(new PasteTokens().For(pasted));
    }

    [Fact]
    public void ThreeLines_BecomeAToken()
    {
        var token = new PasteTokens().For(Lines(3));

        Assert.NotNull(token);
        Assert.Equal("[Pasted text #1 +3 lines]", token!.Label);
        Assert.Equal(Lines(3), token.Value);
        Assert.Equal(PasteTokens.Tag, token.Tag);
    }

    [Fact]
    public void OneLongLine_BecomesATokenCountedInCharacters()
    {
        Assert.Null(new PasteTokens().For(new string('x', 799)));

        var token = new PasteTokens().For(new string('x', 2000));

        Assert.Equal("[Pasted text #1 2,000 chars]", token!.Label);
    }

    /// <summary>A Windows clipboard's line endings are not what the model should be sent.</summary>
    [Fact]
    public void CarriageReturns_AreNormalisedInTheValue()
    {
        var token = new PasteTokens().For("a\r\nb\r\nc");

        Assert.Equal("a\nb\nc", token!.Value);
        Assert.Equal("[Pasted text #1 +3 lines]", token.Label);
    }

    /// <summary>Numbers name the tokens apart, so a paste that stayed text does not use one up.</summary>
    [Fact]
    public void TokensAreNumberedInTurn_AndAShortPasteTakesNoNumber()
    {
        var pastes = new PasteTokens();

        var first = pastes.For(Lines(4));
        Assert.Null(pastes.For("short"));
        var second = pastes.For(Lines(5));

        Assert.StartsWith("[Pasted text #1 ", first!.Label);
        Assert.StartsWith("[Pasted text #2 ", second!.Label);
    }
}

/// <summary>The composer as cxagent builds it: the rule wired in, and what a submit reads.</summary>
public class ComposerTokenTests
{
    private static readonly string Pasted = string.Join("\n", Enumerable.Range(1, 18).Select(i => $"line {i}"));

    private static PromptControl ComposerWithPaste()
    {
        var composer = MainWindow.NewPrompt();
        composer.Input = "fix this: ";
        composer.Paste(Pasted);
        return composer;
    }

    [Fact]
    public void ALongPaste_ShowsTheLabel_AndKeepsTheText()
    {
        var composer = ComposerWithPaste();

        Assert.Equal("fix this: [Pasted text #1 +18 lines]", composer.InputLabelText);
        Assert.Equal("fix this: " + Pasted, composer.Input);
    }

    /// <summary>The model gets the text; the transcript row and the session list get the label.</summary>
    [Fact]
    public void ASubmittedLine_SendsTheText_AndEchoesTheLabel()
    {
        var line = ComposerLine.Of(ComposerWithPaste());

        Assert.Equal("fix this: " + Pasted, line.Text);
        Assert.Equal("fix this: [Pasted text #1 +18 lines]", line.Echo);
    }

    /// <summary>Without a token there is nothing to echo differently, so nothing changes.</summary>
    [Fact]
    public void ALineWithoutTokens_HasNoEcho()
    {
        var composer = MainWindow.NewPrompt();
        composer.Input = "plain goal";

        var line = ComposerLine.Of(composer);

        Assert.Equal("plain goal", line.Text);
        Assert.Null(line.Echo);
    }

    /// <summary>↑ brings the message back as it was typed: the token, still holding its text.</summary>
    [Fact]
    public void HistoryRecallsTheToken()
    {
        var composer = ComposerWithPaste();
        var line = ComposerLine.Of(composer);

        composer.RecordHistory(line.Segments);
        composer.Input = "";
        composer.ProcessKey(new ConsoleKeyInfo('\0', ConsoleKey.UpArrow, false, false, false));

        Assert.Equal("fix this: [Pasted text #1 +18 lines]", composer.InputLabelText);
        Assert.Equal("fix this: " + Pasted, composer.Input);
    }

    [Fact]
    public void ExpandingAToken_TurnsItBackIntoText()
    {
        var composer = ComposerWithPaste();
        var token = composer.InputSegments.Single(s => s.IsToken).Token!;

        PasteTokens.Expand(composer, token);

        Assert.DoesNotContain(composer.InputSegments, s => s.IsToken);
        Assert.Equal("fix this: " + Pasted, composer.InputLabelText);
        Assert.Equal("fix this: " + Pasted, composer.Input);
    }

    /// <summary>Expanding one token leaves another beside it alone.</summary>
    [Fact]
    public void ExpandingOneToken_LeavesTheOther()
    {
        var composer = ComposerWithPaste();
        composer.Paste(Pasted);
        var first = composer.InputSegments.First(s => s.IsToken).Token!;

        PasteTokens.Expand(composer, first);

        Assert.Single(composer.InputSegments, s => s.IsToken);
        Assert.EndsWith("[Pasted text #2 +18 lines]", composer.InputLabelText);
    }
}

/// <summary>
/// The composer's own rewrites keep its tokens: a continuation and a restored queue edit around them
/// rather than flattening the whole line to text.
/// </summary>
public class ComposerRewriteKeepsTokensTests
{
    private static readonly PromptToken Token = new("[Pasted text #1 +3 lines]", "a\nb\nc", PasteTokens.Tag);

    [Fact]
    public void ABackslashAfterAToken_ContinuesTheLine_AndKeepsTheToken()
    {
        var continued = AppBootstrap.SegmentContinuationForTest(
            [PromptSegment.FromText("fix "), PromptSegment.FromToken(Token), PromptSegment.FromText(" \\")]);

        Assert.NotNull(continued);
        Assert.Equal(
            [PromptSegment.FromText("fix "), PromptSegment.FromToken(Token), PromptSegment.FromText(" \n")],
            continued!);
    }

    /// <summary>A line ending in a token ends in no backslash, so Enter submits it.</summary>
    [Fact]
    public void ALineEndingInAToken_Submits()
    {
        Assert.Null(AppBootstrap.SegmentContinuationForTest(
            [PromptSegment.FromText("fix "), PromptSegment.FromToken(Token)]));
    }

    [Fact]
    public void RestoringTheQueue_PutsItAboveTheTokens()
    {
        var restored = PromptQueue.Restore("queued", [PromptSegment.FromToken(Token)]);

        Assert.Equal([PromptSegment.FromText("queued\n"), PromptSegment.FromToken(Token)], restored);
    }

    [Fact]
    public void RestoringNothing_LeavesTheComposerAlone()
    {
        IReadOnlyList<PromptSegment> composer = [PromptSegment.FromToken(Token)];

        Assert.Equal(composer, PromptQueue.Restore(null, composer));
    }

    [Fact]
    public void RestoringIntoAnEmptyComposer_IsJustTheQueue()
    {
        Assert.Equal([PromptSegment.FromText("queued")], PromptQueue.Restore("queued", []));
    }
}
