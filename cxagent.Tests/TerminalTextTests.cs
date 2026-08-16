using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

public class TerminalTextTests
{
    private const string Esc = "\u001b";

    // THE REPORTED CASE. A freshly built binary run with --color: 24-bit SGR around every cell.
    // SharpConsoleUI's own per-rune sanitizer replaces the ESC and leaves "[38;2;78;205;196m" as
    // literal text, so the guard held and the transcript was still unreadable.
    [Fact]
    public void Strips24BitColourAndLeavesTheText()
    {
        var coloured = $"{Esc}[38;2;78;205;196m 45.2%{Esc}[0m";
        Assert.Equal(" 45.2%", TerminalText.Strip(coloured));
    }

    [Fact]
    public void StripsCursorMovementAndErase()
    {
        Assert.Equal("frame", TerminalText.Strip($"{Esc}[2A{Esc}[2Kframe{Esc}[K"));
    }

    // OSC carries a payload — the hyperlink form embeds a URL. Dropping the ESC alone would print
    // the address into the transcript.
    [Fact]
    public void StripsOscIncludingItsPayload_BelTerminated()
    {
        Assert.Equal("link", TerminalText.Strip($"{Esc}]8;;https://example.comlink"));
    }

    [Fact]
    public void StripsOscTerminatedByStringTerminator()
    {
        Assert.Equal("after", TerminalText.Strip($"{Esc}]0;window title{Esc}\\after"));
    }

    [Fact]
    public void StripsTwoAndThreeCharacterForms()
    {
        Assert.Equal("text", TerminalText.Strip($"{Esc}(Btext{Esc}="));
    }

    // Content, not control. A table that wraps and a Makefile's tabs must survive intact.
    [Fact]
    public void KeepsNewlinesAndTabs()
    {
        Assert.Equal("a\nb\tc", TerminalText.Strip("a\nb\tc"));
    }

    // CR rewrites the line. A progress bar arrives as every frame concatenated; without the CR the
    // frames at least read in order instead of overwriting each other.
    [Fact]
    public void DropsCarriageReturnAndBackspace()
    {
        // BOTH FRAMES SURVIVE, in order. Dropping the CR does not replay what the terminal would
        // have done — it cannot, there is no cursor here — it removes the instruction to overwrite.
        // "50%100%" is honest and legible; "50%" rendered on top of "100%" is neither.
        Assert.Equal("50%100%", TerminalText.Strip("50%\r100%"));
        Assert.Equal("ab", TerminalText.Strip("ab\b"));
    }

    // Output cut mid-sequence by the capture cap. Better to drop the orphan than render a glyph for
    // half a code nobody sent.
    [Fact]
    public void DropsATruncatedTrailingEscape()
    {
        Assert.Equal("text", TerminalText.Strip($"text{Esc}"));
    }

    [Fact]
    public void LeavesCleanTextExactlyAsItWas()
    {
        const string table = "┌──────┐\n│ GPU  │\n└──────┘";
        Assert.Same(table, TerminalText.Strip(table));   // same instance: no allocation on the fast path
    }

    [Fact]
    public void HandlesNullAndEmpty()
    {
        Assert.Equal(string.Empty, TerminalText.Strip(null));
        Assert.Equal(string.Empty, TerminalText.Strip(string.Empty));
    }

    // The whole bordered table from the drive that prompted this, coloured, must come back readable
    // and aligned — the columns are the point, and a partial strip would leave them ragged.
    [Fact]
    public void ARealColouredTableSurvivesReadable()
    {
        var row = $"│{Esc}[38;2;78;205;196m 45.2%{Esc}[0m│{Esc}[38;2;255;107;107m  85°C{Esc}[0m│";
        Assert.Equal("│ 45.2%│  85°C│", TerminalText.Strip(row));
    }
}
