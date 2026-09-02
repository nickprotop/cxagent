using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Choosing an <c>@</c> row rewrites the reference and nothing else.
///
/// <para>THE TRAP THIS PINS: a command row replaces the whole composer, because a command IS the
/// line. An <c>@</c> reference sits mid-sentence, so the same treatment would delete every word in
/// front of it — a completion that eats the question you were asking.</para>
/// </summary>
public class AtSpliceTests
{
    [Fact]
    public void TheWordsBeforeTheReferenceSurvive()
    {
        var text = "fix the bug in @Shell";
        var at = AtToken.At(text, text.Length)!.Value;

        Assert.Equal("fix the bug in cxagent/UI/ShellWindow.cs",
            CommandMenu.Splice(text, at, "cxagent/UI/ShellWindow.cs"));
    }

    /// <summary>The tail survives too — a reference completed mid-sentence keeps what follows it.</summary>
    [Fact]
    public void TheWordsAfterTheCaretSurvive()
    {
        var text = "look at @Sh and tell me";
        var at = AtToken.At(text, caret: 11)!.Value;

        Assert.Equal("look at src/Shell.cs and tell me",
            CommandMenu.Splice(text, at, "src/Shell.cs"));
    }

    [Fact]
    public void ABareAt_BecomesThePath()
    {
        var text = "@";
        var at = AtToken.At(text, 1)!.Value;

        Assert.Equal("README.md", CommandMenu.Splice(text, at, "README.md"));
    }

    /// <summary>A directory keeps its separator, so typing can continue into it.</summary>
    [Fact]
    public void ADirectoryKeepsItsTrailingSeparator()
    {
        var text = "read @src";
        var at = AtToken.At(text, text.Length)!.Value;

        Assert.Equal("read src/UI/", CommandMenu.Splice(text, at, "src/UI/"));
    }

    /// <summary>
    /// DESCENDING NEEDS THE MARKER TO SURVIVE. Splice puts back whatever it is given, so the caller
    /// decides — and it must keep the <c>@</c> on a directory. Without it the composer reads
    /// "look at src/UI/", which contains no reference, and the menu has nothing to reopen on: the
    /// path can never be finished. Observed live, on the first drive of this feature.
    /// </summary>
    [Fact]
    public void ADirectoryCompletionKeepsTheAt_SoTheMenuCanReopen()
    {
        var text = "look at @cx";
        var at = AtToken.At(text, text.Length)!.Value;

        var spliced = CommandMenu.Splice(text, at, "@cxgpu.Tests/");

        Assert.Equal("look at @cxgpu.Tests/", spliced);
        Assert.NotNull(AtToken.At(spliced, spliced.Length));
    }
}
