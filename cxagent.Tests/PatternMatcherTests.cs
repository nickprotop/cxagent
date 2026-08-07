using CxAgent.Core.Plugins.Builtin;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The spec for locating a replace pattern.
///
/// <para>Extracted for the same reason as <see cref="IndentShift"/>: embedded in the plugin, a wrong
/// answer could come from the matcher, the span it returned, the shift applied afterwards, or a
/// stale build. Two strings in, a list out.</para>
/// </summary>
public class PatternMatcherTests
{
    // --- Exact matching --------------------------------------------------------------------------

    [Fact]
    public void AnExactMatchIsFound()
    {
        var m = PatternMatcher.FindAll("\t\tvar a = 1;\n", "var a = 1;");

        Assert.Single(m);
        Assert.Equal(2, m[0].Start);
        Assert.Equal(10, m[0].Length);
    }

    [Fact]
    public void AMidLineFragmentIsFound()
    {
        // THE GAP. The line-based pass compares whole lines, so `a + b` inside `int t = a + b;`
        // reported "not found even ignoring indentation" — indentation blamed for a substring the
        // matcher could not express. Every other tool in this space does substring replacement.
        var m = PatternMatcher.FindAll("\t\tint t = a + b;\n", "a + b");

        Assert.Single(m);
        Assert.False(m[0].WholeLines);   // a fragment: no indentation to correct
    }

    [Fact]
    public void AWholeLineMatchIsMarkedAsSuch()
    {
        var m = PatternMatcher.FindAll("\t\tvar a = 1;\n", "var a = 1;");
        Assert.True(m[0].WholeLines);
    }

    [Fact]
    public void EveryOccurrenceIsReported()
    {
        // The caller decides what to do about ambiguity; the matcher's job is to report it, not to
        // silently pick one.
        var m = PatternMatcher.FindAll("x();\ny();\nx();\n", "x();");
        Assert.Equal(2, m.Count);
    }

    [Fact]
    public void AnExactMatchSuppressesTheTolerantPass()
    {
        // An exact hit is what the caller literally asked for. Mixing in formatting variants they
        // did not mean would make an unambiguous request look ambiguous and refuse it.
        var m = PatternMatcher.FindAll("var a = 1;\nvar  a  =  1;\n", "var a = 1;");
        Assert.Single(m);
    }

    // --- Whitespace tolerance --------------------------------------------------------------------

    [Fact]
    public void IndentationIsIgnored()
    {
        // The reason tolerance exists: a model cannot know a file's exact leading bytes.
        var m = PatternMatcher.FindAll("\t\t\tvar a = 1;\n", "var a = 1;");
        Assert.Single(m);
    }

    [Fact]
    public void HouseStyleSpacingIsIgnored()
    {
        var m = PatternMatcher.FindAll("\t\tif (x)   { Go(); }\n", "if (x) { Go(); }");
        Assert.Single(m);
    }

    [Fact]
    public void TabsAndSpacesAreInterchangeableForIndentation()
    {
        var m = PatternMatcher.FindAll("\t\tvar a = 1;\n", "    var a = 1;");
        Assert.Single(m);
    }

    [Fact]
    public void AWordBoundaryIsNOTIgnored()
    {
        // THE OTHER GAP. Squash removed EVERY whitespace character, so `foo bar` matched `foobar` —
        // two different identifiers, meaning a replace aimed at one could edit the other. Tolerating
        // spacing is the point; erasing the distinction between separated and joined tokens is a far
        // larger claim.
        Assert.Empty(PatternMatcher.FindAll("\t\tvar foobar = 1;\n", "var foo bar = 1;"));
    }

    [Fact]
    public void AMultiLinePatternMatchesAcrossLines()
    {
        var m = PatternMatcher.FindAll("\t\tif (x)\n\t\t{\n\t\t\tGo();\n\t\t}\n", "if (x)\n{\n    Go();\n}");

        Assert.Single(m);
        Assert.True(m[0].WholeLines);
    }

    // --- Properties -------------------------------------------------------------------------------

    [Fact]
    public void TheSpanSlicesBackToTheOriginalText()
    {
        // The offsets must address the ORIGINAL string, not a normalised copy — everything
        // downstream slices with them, and an off-by-one writes into the middle of a token.
        const string text = "one();\n\t\ttwo();\n\t\tthree();\n";
        var m = PatternMatcher.FindAll(text, "two();");

        Assert.Single(m);
        Assert.Equal("two();", text.Substring(m[0].Start, m[0].Length));
    }

    [Fact]
    public void ATolerantSpanCoversTheFileSOWNText()
    {
        // A tolerant match slices whole lines INCLUDING their real indentation, because that is what
        // gets replaced. A span that covered only the normalised form would drop the file's tabs.
        const string text = "\t\tvar a = 1;\n";
        var m = PatternMatcher.FindAll(text, "    var a = 1;");

        Assert.Equal("\t\tvar a = 1;", text.Substring(m[0].Start, m[0].Length));
    }

    [Fact]
    public void NothingMatchesWhenTheContentDiffers()
    {
        Assert.Empty(PatternMatcher.FindAll("\t\tvar a = 1;\n", "var b = 2;"));
    }

    [Fact]
    public void AnEmptyPatternMatchesNothing()
    {
        // Not "matches everywhere": an empty pattern is a caller mistake, and returning every offset
        // in the file would turn it into an ambiguity error rather than something legible.
        Assert.Empty(PatternMatcher.FindAll("anything", ""));
    }

    [Fact]
    public void CrlfTextIsMatchedByLfPattern()
    {
        // A file with Windows line endings must be reachable by a model that sent Unix ones.
        var m = PatternMatcher.FindAll("\t\tif (x)\r\n\t\t{\r\n\t\t}\r\n", "if (x)\n{\n}");
        Assert.Single(m);
    }
}
