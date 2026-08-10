using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The markup boundary: the agent emits semantics, the sink escapes and colours.
/// </summary>
public class ChatTranscriptSinkTests
{
    /// <summary>
    /// MODEL TEXT SURVIVES THE MARKUP PARSER.
    ///
    /// <para>The control parses what it is given, so an unescaped "[red]" in model output is
    /// SWALLOWED and an unclosed tag recolours everything after it. This was live: body text went
    /// through unescaped while reasoning — the only path that built markup — was escaped, so the
    /// omission was invisible. A model discussing this very codebase writes "[red]" as prose.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("we[red]ird")]
    [InlineData("[dim]note[/]")]
    [InlineData("the tag [bold] opens a scope")]
    public void Escape_KeepsTagLikeTextVisible(string modelText)
    {
        var rendered = string.Concat(SharpConsoleUI.Parsing.MarkupParser
            .Parse(ChatTranscriptSink.Escape(modelText),
                   SharpConsoleUI.Color.White, SharpConsoleUI.Color.Black)
            .Select(c => c.Character));

        Assert.Equal(modelText, rendered);
    }

    /// <summary>Without the escape the same text is eaten — the assertion that makes the one above
    /// mean something rather than merely pass.</summary>
    [Fact]
    public void WithoutEscaping_TagLikeTextIsSwallowed()
    {
        var rendered = string.Concat(SharpConsoleUI.Parsing.MarkupParser
            .Parse("we[red]ird", SharpConsoleUI.Color.White, SharpConsoleUI.Color.Black)
            .Select(c => c.Character));

        Assert.Equal("weird", rendered);
    }

    /// <summary>Ordinary brackets are untouched — escaping must not mangle code the model quotes.</summary>
    [Fact]
    public void Escape_LeavesNonTagBracketsRendering()
    {
        var rendered = string.Concat(SharpConsoleUI.Parsing.MarkupParser
            .Parse(ChatTranscriptSink.Escape("array[0] and list[1]"),
                   SharpConsoleUI.Color.White, SharpConsoleUI.Color.Black)
            .Select(c => c.Character));

        Assert.Equal("array[0] and list[1]", rendered);
    }
}
