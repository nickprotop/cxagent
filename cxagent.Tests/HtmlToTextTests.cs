using CxAgent.Core.Plugins.Builtin;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Ten page fetches filled a 200k context. Raw HTML is almost entirely markup, and a tool result is
/// re-sent on every subsequent turn — so a page read once is paid for until compaction. These tests
/// are about what survives conversion and, more importantly, what does not.
/// </summary>
public class HtmlToTextTests
{
    [Fact]
    public void Convert_KeepsTheText()
    {
        var text = HtmlToText.Convert("<html><body><p>Hello there.</p></body></html>");

        Assert.Equal("Hello there.", text);
    }

    /// <summary>
    /// THE WHOLE POINT. Stripping tags alone would leave a page's entire JavaScript source as
    /// "text" — the single biggest source of the waste this exists to stop.
    /// </summary>
    [Fact]
    public void Convert_DropsScriptsAndStyles_WithTheirContents()
    {
        var html = """
            <html><head><style>.a{color:red}</style></head>
            <body>
              <script>var x = 1; function noise() { return "not content"; }</script>
              <p>The actual text.</p>
              <noscript>Enable JavaScript</noscript>
            </body></html>
            """;

        var text = HtmlToText.Convert(html);

        Assert.Contains("The actual text.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("function noise", text, StringComparison.Ordinal);
        Assert.DoesNotContain("color:red", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Enable JavaScript", text, StringComparison.Ordinal);
    }

    /// <summary>An SVG icon is a few hundred characters of path data and no words at all.</summary>
    [Fact]
    public void Convert_DropsSvgPathData()
    {
        var html = "<body><svg viewBox=\"0 0 24 24\"><path d=\"M12 2L2 7l10 5 10-5-10-5z\"/></svg>"
                 + "<p>Text beside an icon.</p></body>";

        var text = HtmlToText.Convert(html);

        Assert.Equal("Text beside an icon.", text);
    }

    /// <summary>
    /// The head carries meta tags, JSON-LD and preload hints — none of it what a reader came for,
    /// and some of it enormous.
    /// </summary>
    [Fact]
    public void Convert_IgnoresTheHead()
    {
        var html = """
            <html><head><title>Page title</title>
            <meta name="description" content="a summary nobody asked for">
            </head><body><p>Body text.</p></body></html>
            """;

        var text = HtmlToText.Convert(html);

        Assert.Equal("Body text.", text);
    }

    /// <summary>
    /// Structure survives as markdown. Without it the page is one line and the model has to infer
    /// headings from punctuation.
    /// </summary>
    [Fact]
    public void Convert_KeepsHeadingsAndListsAsMarkdown()
    {
        var html = "<body><h2>Setup</h2><ul><li>First step</li><li>Second step</li></ul></body>";

        var text = HtmlToText.Convert(html);

        Assert.Contains("## Setup", text, StringComparison.Ordinal);
        Assert.Contains("- First step", text, StringComparison.Ordinal);
        Assert.Contains("- Second step", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Convert_DecodesEntities()
    {
        Assert.Equal("a < b && c > d", HtmlToText.Convert("<body><p>a &lt; b &amp;&amp; c &gt; d</p></body>"));
    }

    /// <summary>Blank lines are the cheapest thing to spend a context window on.</summary>
    [Fact]
    public void Convert_CollapsesTheWhitespaceIndentationLeavesBehind()
    {
        var html = "<body>\n  <div>\n    <p>One.</p>\n  </div>\n\n\n  <div><p>Two.</p></div>\n</body>";

        var text = HtmlToText.Convert(html);

        Assert.DoesNotContain("\n\n\n", text, StringComparison.Ordinal);
        Assert.Contains("One.", text, StringComparison.Ordinal);
        Assert.Contains("Two.", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A tag name must match exactly: dropping &lt;a&gt; must not eat an &lt;article&gt;, and this
    /// is the bug a naive IndexOf("<a") would ship.
    /// </summary>
    [Fact]
    public void Convert_DoesNotConfuseATagWithALongerOneStartingTheSameWay()
    {
        var html = "<body><article><p>Article content.</p></article></body>";

        Assert.Contains("Article content.", HtmlToText.Convert(html), StringComparison.Ordinal);
    }

    /// <summary>
    /// A page whose script never closes has nothing readable after it — but it must not throw, and
    /// it must not return the script.
    /// </summary>
    [Fact]
    public void Convert_WithAnUnclosedScript_DropsTheRestRatherThanThrowing()
    {
        var text = HtmlToText.Convert("<body><p>Before.</p><script>var x = 1;");

        Assert.Contains("Before.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("var x", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Convert_OnEmptyOrWhitespace_ReturnsEmpty()
    {
        Assert.Equal("", HtmlToText.Convert(""));
        Assert.Equal("", HtmlToText.Convert("   \n  "));
    }

    /// <summary>
    /// THE MEASUREMENT THAT JUSTIFIES THE TOOL. A page shaped like a real documentation page —
    /// navigation, inline script, styles, class attributes on everything — against the words a
    /// reader would actually take from it.
    /// </summary>
    [Fact]
    public void Convert_OnARealisticPage_ReclaimsMostOfIt()
    {
        var nav = string.Concat(Enumerable.Range(0, 40).Select(i =>
            $"<li class=\"nav-item nav-item-{i} sidebar-link\"><a href=\"/docs/page-{i}\" "
            + $"class=\"link primary\" data-track=\"nav-{i}\">Section {i}</a></li>"));

        var script = "<script>" + string.Concat(Enumerable.Range(0, 200).Select(i =>
            $"window.analytics.track('event-{i}', {{ id: {i}, ts: Date.now() }});")) + "</script>";

        var styles = "<style>" + string.Concat(Enumerable.Range(0, 100).Select(i =>
            $".cls-{i} {{ margin: {i}px; padding: {i}px; color: #ff{i:D2}00; }}")) + "</style>";

        var html = $"<html><head>{styles}</head><body><nav><ul>{nav}</ul></nav>{script}"
                 + "<main><h1>Installing the thing</h1>"
                 + "<p>Run the installer and follow the prompts.</p>"
                 + "<p>It requires .NET 10 or later.</p></main></body></html>";

        var text = HtmlToText.Convert(html);

        Assert.Contains("Installing the thing", text, StringComparison.Ordinal);
        Assert.Contains("Run the installer", text, StringComparison.Ordinal);
        Assert.Contains(".NET 10 or later", text, StringComparison.Ordinal);
        Assert.DoesNotContain("window.analytics", text, StringComparison.Ordinal);
        Assert.DoesNotContain("margin:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("data-track", text, StringComparison.Ordinal);

        // The reduction is the feature. Ten of these at full size is the 200k context.
        Assert.True(text.Length < html.Length / 8,
            $"expected under an eighth of {html.Length} chars, got {text.Length}");
    }

    /// <summary>
    /// WHITESPACE INSIDE &lt;pre&gt; IS CONTENT. Collapsing it reflows every code sample on the page
    /// into one line — the opposite of useful for a coding agent, and the thing a naive whitespace
    /// pass gets wrong.
    /// </summary>
    [Fact]
    public void Convert_KeepsTheShapeOfCodeBlocks()
    {
        var html = "<body><pre><code>def f(x):\n    return x * 2\n</code></pre></body>";

        var text = HtmlToText.Convert(html);

        Assert.Contains("    return x * 2", text, StringComparison.Ordinal);
        Assert.Contains("```", text, StringComparison.Ordinal);
    }

    /// <summary>Inline code stays inline, so a sentence about `dotnet build` still reads as one.</summary>
    [Fact]
    public void Convert_MarksInlineCodeWithoutBreakingTheSentence()
    {
        var text = HtmlToText.Convert("<body><p>Run <code>dotnet build</code> first.</p></body>");

        Assert.Equal("Run `dotnet build` first.", text);
    }

    /// <summary>
    /// Malformed HTML is the normal case on the real web — unclosed tags, stray text, misnesting.
    /// A parser recovers where a regex pass produces nonsense.
    /// </summary>
    [Fact]
    public void Convert_RecoversFromMalformedMarkup()
    {
        var text = HtmlToText.Convert("<body><p>First<p>Second<div>Third</body>");

        Assert.Contains("First", text, StringComparison.Ordinal);
        Assert.Contains("Second", text, StringComparison.Ordinal);
        Assert.Contains("Third", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A closing tag inside a JavaScript string literal does not end the script. This is exactly the
    /// case the hand-rolled version got wrong, and the reason a parser is worth a dependency.
    /// </summary>
    [Fact]
    public void Convert_IsNotFooledByATagInsideAScriptString()
    {
        var html = "<body><script>var s = \"</div><p>fake</p>\"; var n = 1;</script>"
                 + "<p>Real text.</p></body>";

        var text = HtmlToText.Convert(html);

        Assert.Equal("Real text.", text);
        Assert.DoesNotContain("fake", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Navigation and footers are identical on every page of a site, so reading four pages of one
    /// documentation set otherwise pays for its sidebar four times.
    /// </summary>
    [Fact]
    public void Convert_DropsNavigationAndFooterChrome()
    {
        var html = "<body><nav><a href=/a>Home</a><a href=/b>Docs</a></nav>"
                 + "<main><p>The page itself.</p></main>"
                 + "<footer>© 2026 Someone. All rights reserved.</footer></body>";

        var text = HtmlToText.Convert(html);

        Assert.Equal("The page itself.", text);
    }

    [Theory]
    [InlineData("text/html; charset=utf-8", true)]
    [InlineData("application/xhtml+xml", true)]
    [InlineData("application/json", false)]
    [InlineData("text/plain", false)]
    [InlineData(null, false)]
    public void IsHtml_RecognisesOnlyHtml(string? contentType, bool expected) =>
        Assert.Equal(expected, HtmlToText.IsHtml(contentType));
}
