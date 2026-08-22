using System;
using CxAgent.Core.Commands;
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

    // ---- which PATH escapes: found on a live drive -------------------------------------------

    /// <summary>Renders a string the way the Assistant role actually does — SetMarkdown wraps the
    /// body in a [markdown] tag, and the parser converts it.</summary>
    private static string AsAssistantBody(string text) =>
        string.Concat(SharpConsoleUI.Parsing.MarkupParser
            .Parse($"[markdown]{text}[/]", SharpConsoleUI.Color.White, SharpConsoleUI.Color.Black)
            .Select(c => c.Character));

    /// <summary>
    /// BODY TEXT MUST NOT BE ESCAPED BY THIS SINK — INSIDE AN INLINE-CODE SPAN THE ESCAPE REACHES
    /// THE SCREEN.
    ///
    /// <para>Found live, in the model's own words: it wrote <c>`[abc]`</c> in backticks and the
    /// transcript showed <c>[[abc]</c>. A code span is literal to the markdown converter — it neither
    /// re-escapes nor unescapes what is inside — so a bracket the sink had already doubled passed
    /// straight through.</para>
    ///
    /// <para>OUTSIDE code spans the doubling is invisible, which is precisely why this survived:
    /// every other test in this file uses plain text, and plain text renders identically either way.
    /// A model discussing code writes backticks constantly.</para>
    /// </summary>
    [Theory]
    [InlineData("code `[abc]` here")]
    [InlineData("call `segments[0].Length` on it")]
    [InlineData("plain [abc] with no backticks")]
    public void AssistantBody_RendersModelTextVerbatim_EvenInsideCodeSpans(string modelText)
    {
        // What AssistantTextAppended does: hand the token over untouched.
        var rendered = AsAssistantBody(modelText);

        Assert.DoesNotContain("[[", rendered, StringComparison.Ordinal);
        Assert.Contains(modelText.Replace("`", ""), rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// The assertion that makes the one above mean something: pre-escaping the body text leaves the
    /// doubled bracket ON SCREEN inside a code span.
    /// </summary>
    [Fact]
    public void PreEscapingBodyText_LeavesADoubledBracketOnScreen()
    {
        var rendered = AsAssistantBody(ChatTranscriptSink.Escape("call `segments[0].Length` on it"));

        Assert.Contains("[[", rendered, StringComparison.Ordinal);
    }

    // ---- severity keeps its colour while the System role renders markdown ---------------------

    /// <summary>
    /// A COLOURED ROW IS MARKUP, AND SAYS SO PER MESSAGE.
    ///
    /// <para>The System role renders markdown, which is what Core writes. The two severity branches
    /// are the exception: this sink wraps them in a colour tag, and a colour tag handed to the
    /// markdown converter reaches the screen as a literal "[red]". The per-message override is what
    /// lets one row be markup while the rest of the role stays markdown.</para>
    /// </summary>
    [Theory]
    [InlineData(Severity.Error)]
    [InlineData(Severity.Warning)]
    public void ColouredRows_RenderAsMarkup(Severity severity)
    {
        var row = ChatTranscriptSink.Row(new Message("could not save this rule", severity));

        Assert.False(row.Markdown);
        Assert.Contains("[", row.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// An ordinary row is untouched, so it inherits the role — which renders markdown. Forcing
    /// markup here would undo the whole point: Core's tables and headings would show their syntax.
    /// </summary>
    [Fact]
    public void InfoRows_DeferToTheRole()
    {
        var row = ChatTranscriptSink.Row(new Message("## Sessions\n| # | id |"));

        Assert.Null(row.Markdown);
        Assert.Equal("## Sessions\n| # | id |", row.Text);
    }

    /// <summary>An error still gets its mark and its danger tone — the colour did not go away, it
    /// merely stopped fighting the renderer.</summary>
    [Fact]
    public void ErrorRows_KeepTheirMarkAndTone()
    {
        var row = ChatTranscriptSink.Row(new Message("boom", Severity.Error));

        Assert.Contains("\u2717 boom", row.Text, StringComparison.Ordinal);
        Assert.Contains(ColorScheme.DangerMarkup, row.Text, StringComparison.Ordinal);
    }
}
