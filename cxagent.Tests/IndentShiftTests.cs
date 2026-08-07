using CxAgent.Core.Plugins.Builtin;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The spec for shifting a replacement onto the file's indentation.
///
/// <para>These exist because the same logic, embedded in the file plugin, took eight failed attempts
/// to fix. A wrong answer there could come from the matcher, the slice it produced, the shift, or a
/// stale build — and separating those cost more than the fix itself. Here the function takes three
/// strings and returns one, so a failure has exactly one place to be.</para>
/// </summary>
public class IndentShiftTests
{
    // --- The cases that were actually failing ---------------------------------------------------

    [Fact]
    public void ModelSendsSpaces_FileUsesTabs()
    {
        // The commonest case by far: a model writes standard 4-space C# into a tab-indented file.
        var result = IndentShift.Apply(
            matched: "\t\tvar a = 1;",
            pattern: "var a = 1;",
            replacement: "    var a = 2;");

        Assert.Equal("\t\tvar a = 2;", result);
    }

    [Fact]
    public void ANestedBlockKeepsItsRelativeStructure()
    {
        // The property that rebuilding destroyed: Go() is one level deeper than the braces, and it
        // must STAY one level deeper — in whatever the model used, since only the outer shift is
        // ours to decide.
        var result = IndentShift.Apply(
            matched: "\t\tif (x)\n\t\t{\n\t\t\tGo();\n\t\t}",
            pattern: "if (x)\n{\n\tGo();\n}",
            replacement: "if (y)\n{\n\tGo();\n\tStop();\n}");

        Assert.Equal("\t\tif (y)\n\t\t{\n\t\t\tGo();\n\t\t\tStop();\n\t\t}", result);
    }

    [Fact]
    public void AnAlreadyCorrectReplacementIsUnchanged()
    {
        // Idempotence. A model that got the indentation right must not have it "corrected".
        const string exact = "\t\tvar a = 1;";
        Assert.Equal(exact, IndentShift.Apply(exact, exact, exact));
    }

    [Fact]
    public void RepeatedApplicationIsStable()
    {
        // The strongest form: shifting an already-shifted result must be a no-op, or successive
        // edits to one region compound. That was a real failure — three tabs became eight.
        var once = IndentShift.Apply("\t\tvar a = 1;", "var a = 1;", "    var a = 2;");
        var twice = IndentShift.Apply("\t\tvar a = 2;", "var a = 2;", once);

        Assert.Equal(once, twice);
    }

    // --- Refusal: the cases where no single shift exists -----------------------------------------

    [Fact]
    public void NonUniformIndentIsWrittenAsSent()
    {
        // Two matched lines disagree about how much the file adds, so no one prefix explains the
        // edit. Inventing one is what silently reshaped code; writing the model's own text is
        // visibly wrong instead, and the result is echoed back for it to see.
        const string replacement = "alpha();\nbeta();";
        var result = IndentShift.Apply(
            matched: "\t\talpha();\n\t\t\t\tbeta();",
            pattern: "alpha();\nbeta();",
            replacement: replacement);

        Assert.Equal(replacement, result);
    }

    [Fact]
    public void ASingleLineIsShiftedWhateverUnitsEitherSideUsed()
    {
        // My first expectation here was wrong, and the reason is worth keeping: after OUTDENTING,
        // a single line has no indentation left on either side, so "the file uses tabs and the model
        // sent spaces" is not a conflict at all — there is nothing to conflict. Units only matter
        // when a line's indent must be compared against another's, which needs two lines.
        var result = IndentShift.Apply(
            matched: "\t\t\tGo();",
            pattern: "    Go();",
            replacement: "    Stop();");

        Assert.Equal("\t\t\tStop();", result);
    }

    [Fact]
    public void MixedUnitsACROSSLinesAreWrittenAsSent()
    {
        // Here units DO conflict: after outdenting, the second line still carries spaces while the
        // file's carries a tab, so the file's indent does not begin with the model's and no prefix
        // reconciles them.
        const string replacement = "if (y)\n    Go();";
        var result = IndentShift.Apply(
            matched: "\t\tif (x)\n\t\t\tGo();",
            pattern: "if (x)\n    Go();",
            replacement: replacement);

        Assert.Equal(replacement, result);
    }

    // --- Properties that must hold ---------------------------------------------------------------

    [Fact]
    public void BlankLinesGainNoTrailingWhitespace()
    {
        // A blank line has no indentation to add, and adding one leaves invisible trailing
        // whitespace that every linter flags and no reviewer can see.
        var result = IndentShift.Apply(
            matched: "\t\tone();\n\n\t\ttwo();",
            pattern: "one();\n\ntwo();",
            replacement: "one();\n\ntwo();");

        Assert.Equal("\t\tone();\n\n\t\ttwo();", result);
    }

    [Fact]
    public void OutdentingNeverFlattensDeeperLines()
    {
        // Outdenting by the MINIMUM is what makes prepending safe: the shallowest line reaches
        // column 0 and every other line keeps its depth relative to it. Same units on both sides
        // here, so the shift applies and the 4-space nest survives it.
        var result = IndentShift.Apply(
            matched: "        if (x)\n            Go();",   // 8 and 12 spaces
            pattern: "  if (x)\n      Go();",               // 2 and 6: the same 4-space nest
            replacement: "  if (y)\n      Go();");

        Assert.Equal("        if (y)\n            Go();", result);
    }

    [Fact]
    public void CrlfInputProducesConsistentOutput()
    {
        // Windows line endings must not survive into a shift decision as extra "content".
        var result = IndentShift.Apply(
            matched: "\t\ta();\r\n\t\tb();",
            pattern: "a();\r\nb();",
            replacement: "a();\r\nc();");

        Assert.Equal("\t\ta();\n\t\tc();", result);
    }

    [Fact]
    public void AnEmptyReplacementIsHandled()
    {
        Assert.Equal("", IndentShift.Apply("\t\tgone();", "gone();", ""));
    }

    [Fact]
    public void AFileWithNoIndentationAddsNothing()
    {
        var result = IndentShift.Apply("var a = 1;", "var a = 1;", "var a = 2;");
        Assert.Equal("var a = 2;", result);
    }

    [Fact]
    public void APatternRECONSTRUCTEDWithTheFilesIndentIsANoOp()
    {
        // How the plugin calls this after an EXACT match: the span begins at the pattern's first
        // character, so the caller extends both the span and the pattern back to the line start.
        // Both sides then carry the file's indentation, the outdent cancels it on each, and the
        // measured offset is empty — a replacement that already has the right indent keeps it.
        var result = IndentShift.Apply(
            matched: "\t\tvar a = 1;",
            pattern: "\t\tvar a = 1;",          // lead + pattern, as the caller builds it
            replacement: "\t\tvar a = 1;");

        Assert.Equal("\t\tvar a = 1;", result);
    }

    [Theory]
    // matched (span incl. its line's indent) | pattern as sent | replacement as sent | expected
    [InlineData("\t\tvar a = 1;", "var a = 1;",     "\t\tvar a = 1;", "\t\tvar a = 1;")]
    [InlineData("\t\tvar a = 1;", "var a = 1;",     "    var a = 2;", "\t\tvar a = 2;")]
    [InlineData("\t\tvar a = 1;", "\t\tvar a = 1;", "\t\tvar a = 2;", "\t\tvar a = 2;")]
    [InlineData("\t\tvar a = 1;", "    var a = 1;", "    var a = 2;", "\t\tvar a = 2;")]
    public void TheCallerSContract(string matched, string pattern, string replacement, string expected)
    {
        // THE SEAM, pinned. The plugin passes the matched span extended to its line start, and the
        // pattern and replacement exactly as the model sent them — neither reconstructed. Every
        // combination of "model indented it / model did not" must land on the file's indentation
        // once, and only once.
        //
        // This is the contract three call-site rewrites kept violating: reconstructing the pattern
        // with the file's leading whitespace while leaving the replacement raw makes the two sides
        // describe different things, and the file's indent is then added on top of indentation the
        // replacement already had.
        Assert.Equal(expected, IndentShift.Apply(matched, pattern, replacement));
    }
}
