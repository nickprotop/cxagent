using CxAgent.Core.Jobs.Builtin;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The spec for shifting a replacement onto the file's indentation.
///
/// <para>These exist because the same logic, embedded in the file executor, took eight failed attempts
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

    [Fact]
    public void ARaggedMultiLinePatternStillPlacesAOneLineReplacement()
    {
        // THE LIVE FAILURE, from a drive against a ConsoleEx clone. The model sent a two-line pattern
        // whose FIRST line it had de-indented but whose continuation still carried the file's own
        // tabs — relative shape [0,+5] against the file's [0,+2]. The lines genuinely disagree, so
        // refusing to derive one shift is right; what was wrong is what refusal then wrote.
        //
        // As-sent put the replacement at column 0 among neighbours three tabs deep. That is visibly
        // broken, which was the intent — but in a real diff it reads as a deliberate edit, and it
        // shipped alongside a reverted fix without anything flagging it.
        var result = IndentShift.Apply(
            matched: "\t\t\tColor bg = _backgroundColor\n\t\t\t\t\t?? (Container?.Background ?? Transparent);",
            pattern: "Color bg = _backgroundColor\n\t\t\t\t\t?? (Container?.Background ?? Transparent);",
            replacement: "Color bg = _backgroundColor ?? Transparent;");

        Assert.Equal("\t\t\tColor bg = _backgroundColor ?? Transparent;", result);
    }

    [Fact]
    public void AMultiLinePatternDeIndentedOnlyOnItsFIRSTLineIsStillPlaced()
    {
        // THE SECOND LIVE FAILURE, from a re-drive after the one-line fix. A model sent a three-line
        // pattern whose FIRST line it had de-indented while lines 2 and 3 carried the file's real
        // tabs — shape [+1,+3,+3] against the file's [+3,+3,+3]. No single shift explains that, so
        // refusal is right; but the replacement was three lines, so the one-line rule did not apply
        // and the ragged first line went in as sent: one tab among neighbours at three.
        //
        // It IS placeable, and without guessing. Every replacement line here keeps its pattern
        // counterpart's indentation exactly, which says "leave this line's depth alone" — so each
        // takes the depth of the FILE line that counterpart matched. Per line, from the file.
        var result = IndentShift.Apply(
            matched: "\t\t\tColor bg = A;\n\t\t\tColor fg = B;\n\t\t\tvar m = C;",
            pattern: "\tColor bg = A;\n\t\t\tColor fg = B;\n\t\t\tvar m = C;",
            replacement: "\tColor bg = D;\n\t\t\tColor fg = B;\n\t\t\tvar m = bg;");

        Assert.Equal("\t\t\tColor bg = D;\n\t\t\tColor fg = B;\n\t\t\tvar m = bg;", result);
    }

    [Fact]
    public void ALineTheModelDELIBERATELYReIndentedIsLeftAlone()
    {
        // The limit of the rule above, and what keeps it from being a guess. Rebasing is triggered by
        // a replacement line MATCHING its pattern counterpart's indent — an explicit "unchanged".
        // Where the model moved a line on purpose, its choice stands: re-indenting a body it chose to
        // nest deeper would be exactly the silent reshaping this design exists to prevent.
        var result = IndentShift.Apply(
            matched: "\t\t\tif (x)\n\t\t\tGo();",
            pattern: "\tif (x)\n\t\t\tGo();",
            replacement: "\tif (x)\n\t\t\t\tGo();");   // second line deliberately deeper

        // First line rebased to the file; second left exactly as the model wrote it.
        Assert.Equal("\t\t\tif (x)\n\t\t\t\tGo();", result);
    }

    [Fact]
    public void ABlockRetypedOneLevelShortIsPlacedAtTheFilesDepth()
    {
        // THE THIRD LIVE FAILURE, from a drive against ColorResolver. The model retyped a five-line
        // method one tab short — and short by TWO on its opening line, because it dropped the shared
        // indent there as well. Outdent removes the MINIMUM indent, which that first line had made
        // zero, so the rest kept an extra tab, the per-line offsets disagreed, and the shift refused.
        // The method landed a level out, its doc comment at column 0 beside a neighbour at two tabs.
        //
        // Four of the five lines agree on a one-tab offset. That dominant offset is the evidence the
        // model meant the file's shape, and it is what the shape check now looks for.
        var result = IndentShift.Apply(
            matched: "\t\t/// <summary>\n\t\tpublic static Color R(Color? e)\n\t\t\t=> A(e)\n\t\t\t?? B;",
            pattern: "/// <summary>\n\tpublic static Color R(Color? e)\n\t\t=> A(e)\n\t\t?? B;",
            replacement: "/// <summary>\n\tpublic static Color R(Color? e)\n\t\t=> A(e)\n\t\t?? C\n\t\t?? B;");

        Assert.Equal(
            "\t\t/// <summary>\n\t\tpublic static Color R(Color? e)\n\t\t\t=> A(e)\n\t\t\t?? C\n\t\t\t?? B;",
            result);
    }

    [Fact]
    public void ALineTheReplacementADDEDFollowsItsNeighbours()
    {
        // An added line has no pattern counterpart to correlate with, so the per-line rebase skipped
        // it — and left it behind while every line around it moved. Measured live: a new `?? C`
        // clause stayed a tab short of the expression it belongs to.
        var result = IndentShift.Apply(
            matched: "\t\tvar x = A()\n\t\t\t?? B;",
            pattern: "\tvar x = A()\n\t\t?? B;",
            replacement: "\tvar x = A()\n\t\t?? B\n\t\t?? C;");

        // The added last line takes the same depth as the line before it, not the depth it was sent at.
        Assert.EndsWith("\n\t\t\t?? C;", result, StringComparison.Ordinal);
    }

    [Fact]
    public void AGenuinelyRESHAPEDBlockIsStillWrittenAsSent()
    {
        // The limit of the dominant-offset rule, and what keeps it from being a guess. When the
        // offsets SCATTER the model restructured the block rather than mistyping its base, and
        // reconstructing the file's shape over it would be the silent reshaping this design exists to
        // prevent.
        //
        // Four lines, offsets +2, +2, -1, -1: a plurality but no MAJORITY, which is the line the rule
        // draws. My first version of this test used three lines that happened to be 2-of-3 in
        // agreement — a real majority — and it failed because the code was right and the example was
        // not "reshaped" at all.
        const string replacement = "if (x)\n\tGo();\n\t\t\t\t\tDeep();\n\t\t\tEnd();";
        var result = IndentShift.Apply(
            matched: "\t\tif (x)\n\t\t\tGo();\n\t\t\t\tDeep();\n\t\tEnd();",
            pattern: "if (x)\n\tGo();\n\t\t\t\t\tDeep();\n\t\t\tEnd();",
            replacement: replacement);

        Assert.Equal(replacement, result);
    }

    [Fact]
    public void AMultiLineReplacementStillFallsBackToAsSent()
    {
        // The limit of the rule above. A one-line replacement has no SHAPE, so the file's line start
        // places it unambiguously. A multi-line one does — and when the pattern's lines disagreed
        // there is no basis for choosing it, so inventing one would be the silent reshaping this
        // whole design exists to prevent. Visibly wrong is still the answer here.
        const string replacement = "alpha();\nbeta();";
        var result = IndentShift.Apply(
            matched: "\t\talpha();\n\t\t\t\tbeta();",
            pattern: "alpha();\nbeta();",
            replacement: replacement);

        Assert.Equal(replacement, result);
    }

    [Fact]
    public void AMixedUnitRefusalStillPlacesAOneLiner()
    {
        // The other refusal path — the file's indent does not BEGIN with the sent indent (tabs vs
        // spaces) — reaches the same conclusion for the same reason.
        var result = IndentShift.Apply(
            matched: "\t\tif (x)\n\t\t\tGo();",
            pattern: "if (x)\n    Go();",
            replacement: "if (y) Go();");

        Assert.Equal("\t\tif (y) Go();", result);
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
        // How the executor calls this after an EXACT match: the span begins at the pattern's first
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
        // THE SEAM, pinned. The executor passes the matched span extended to its line start, and the
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
