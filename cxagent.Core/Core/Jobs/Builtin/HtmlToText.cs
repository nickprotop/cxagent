using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace CxAgent.Core.Jobs.Builtin;

/// <summary>
/// Turns an HTML page into the text a reader would actually take from it.
///
/// <para>WHY THIS EXISTS, in one measurement: ten page fetches filled a 200k context. Raw HTML is
/// almost entirely markup — scripts, inline styles, SVG path data, class attributes, and the same
/// navigation on every page of a site — and a tool result is re-sent on EVERY subsequent turn, so a
/// page read once is paid for until compaction. A real page measured here went from 238,053
/// characters to under 24,000.</para>
///
/// <para>ANGLESHARP, NOT REGEX. An HTML document is not a regular language, and the first draft of
/// this class proved it: matching <c>&lt;a&gt;</c> without also matching <c>&lt;article&gt;</c>, or
/// dropping a script whose closing tag appears inside a string literal, are the kind of thing a
/// parser gets right by construction. AngleSharp already arrives transitively through
/// SharpConsoleUI; this only pins the version.</para>
///
/// <para>THE INPUT IS UNTRUSTED, which is worth saying out loud about a parser fed arbitrary pages
/// from the internet. What protects us is the shape of the use rather than the parser: this walks
/// the tree for text and never re-serializes HTML, so the sanitiser-bypass class of bug (mXSS) has
/// nowhere to land — nothing here emits markup for anything to render. The version comes from
/// SharpConsoleUI rather than being pinned here, so it moves forward with the UI package.</para>
/// </summary>
public static class HtmlToText
{
    /// <summary>
    /// Elements removed with their subtree, because none of it is ever content.
    ///
    /// <para>NAV AND FOOTER EARN THEIR PLACE: they are identical on every page of a site, so reading
    /// four pages of one documentation set otherwise pays for its sidebar four times. Both are
    /// semantic elements, so this is a reliable signal rather than a guess at class names — a page
    /// that marks them up gets the saving, and one that does not is no worse off.</para>
    /// </summary>
    private static readonly string[] DroppedElements =
    [
        "script", "style", "noscript", "svg", "canvas", "template",
        "iframe", "object", "embed", "nav", "footer",
    ];

    private static readonly HtmlParser Parser = new();

    /// <summary>
    /// True when this content type is HTML worth converting. JSON, plain text and everything else is
    /// passed through untouched — converting them would corrupt data the caller asked for.
    /// </summary>
    public static bool IsHtml(string? contentType) =>
        contentType is not null
        && (contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("application/xhtml", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The page's readable text, with headings, lists and code marked in markdown so its structure
    /// survives the conversion.
    /// </summary>
    /// <remarks>
    /// Never throws. A page that cannot be parsed at all yields "" rather than failing the tool
    /// call — the model asked to read a URL, and "nothing readable" is an answer it can act on.
    /// </remarks>
    public static string Convert(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";

        try
        {
            var document = Parser.ParseDocument(html);

            foreach (var element in document.QuerySelectorAll(string.Join(",", DroppedElements)))
                element.Remove();

            // THE BODY ONLY. <head> carries meta tags, JSON-LD blobs and preload hints — none of it
            // what a reader came for, and some of it enormous.
            var root = document.Body ?? (INode)document;

            var sb = new StringBuilder();
            Walk(root, sb, inPre: false);
            return Tidy(sb.ToString());
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>
    /// Walks the tree in document order, which is reading order — the property that makes this worth
    /// doing over a text dump.
    /// </summary>
    private static void Walk(INode node, StringBuilder sb, bool inPre)
    {
        foreach (var child in node.ChildNodes)
        {
            if (child.NodeType == NodeType.Text)
            {
                // INSIDE <pre>, WHITESPACE IS CONTENT. Collapsing it would reflow every code sample
                // on the page into one line, which is the opposite of useful for a coding agent.
                var text = child.TextContent;
                if (!inPre) text = Collapse(text);
                sb.Append(text);
                continue;
            }

            if (child is not IElement element) continue;

            switch (element.LocalName)
            {
                case "h1": case "h2": case "h3": case "h4": case "h5": case "h6":
                    var level = element.LocalName[1] - '0';
                    sb.Append("\n\n").Append('#', level).Append(' ');
                    Walk(element, sb, inPre);
                    sb.Append("\n\n");
                    break;

                case "li":
                    sb.Append("\n- ");
                    Walk(element, sb, inPre);
                    break;

                case "pre":
                    // FENCED, so the model can tell a code sample from prose that happens to be
                    // indented — and so its own reply can quote it back correctly.
                    sb.Append("\n\n```\n");
                    Walk(element, sb, inPre: true);
                    sb.Append("\n```\n\n");
                    break;

                case "code" when !inPre:
                    sb.Append('`');
                    Walk(element, sb, inPre);
                    sb.Append('`');
                    break;

                case "br":
                    sb.Append('\n');
                    break;

                case "hr":
                    sb.Append("\n\n---\n\n");
                    break;

                case "td": case "th":
                    Walk(element, sb, inPre);
                    sb.Append(" | ");
                    break;

                case "tr":
                    Walk(element, sb, inPre);
                    sb.Append('\n');
                    break;

                default:
                    // A BLOCK BREAKS THE LINE, everything else flows. Without this the page arrives
                    // as one paragraph and the model has to infer structure from punctuation.
                    var block = IsBlock(element.LocalName);
                    if (block) sb.Append("\n\n");
                    Walk(element, sb, inPre);
                    if (block) sb.Append("\n\n");
                    break;
            }
        }
    }

    private static bool IsBlock(string name) => name is
        "p" or "div" or "section" or "article" or "header" or "main" or "aside" or
        "ul" or "ol" or "dl" or "dt" or "dd" or "table" or "thead" or "tbody" or
        "form" or "figure" or "figcaption" or "blockquote" or "address" or "details";

    /// <summary>Runs of whitespace become one space, as a browser would render them.</summary>
    private static string Collapse(string text)
    {
        var sb = new StringBuilder(text.Length);
        var space = false;

        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!space) sb.Append(' ');
                space = true;
            }
            else
            {
                sb.Append(c);
                space = false;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Blank lines and trailing spaces are the cheapest thing to spend a context window on, and a
    /// tree walk leaves a great many of both.
    /// </summary>
    private static string Tidy(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var sb = new StringBuilder(text.Length);
        var blanks = 0;

        foreach (var line in lines)
        {
            var trimmed = line.TrimEnd();
            if (trimmed.Trim().Length == 0)
            {
                // At most one blank line anywhere, which is all a reader needs to see a break.
                if (++blanks > 1) continue;
                sb.Append('\n');
                continue;
            }

            blanks = 0;
            sb.Append(trimmed).Append('\n');
        }

        return sb.ToString().Trim();
    }
}
