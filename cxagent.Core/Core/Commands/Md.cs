using System.Text;

namespace CxAgent.Core.Commands;

/// <summary>
/// Markdown helpers for text Core writes.
///
/// <para>ESCAPES FOR MARKDOWN, WHICH IS THE FORMAT CORE WRITES. Escaping for console markup instead —
/// doubling <c>[</c> into <c>[[</c> — is the wrong format for this text, where the character that
/// ruins a sentence is an underscore in a filename.</para>
///
/// <para>NO COLOUR OR TONE CONSTANTS BELONG HERE. Tone rides in <see cref="Severity"/>, which a front
/// end maps to its own emphasis. A constant named for a tone but holding a markdown emphasis marker
/// would put the naming of tones back in the layer that cannot know how it renders.</para>
/// </summary>
public static class Md
{
    // THE CHARACTERS THAT CHANGE RENDERING, not every character markdown knows. A conservative list
    // keeps ordinary prose untouched: escaping punctuation nobody typed for a reason makes error
    // messages read like source code.
    private const string Special = @"\`*_[]";

    /// <summary>
    /// Escapes a value being interpolated into a markdown sentence.
    /// </summary>
    /// <param name="text">A path, an error message, or anything else Core did not author.</param>
    /// <returns>The text, safe to interpolate.</returns>
    public static string Escape(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (Special.Contains(c)) sb.Append('\\');
            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Escapes a value being interpolated into a markdown TABLE CELL.
    /// </summary>
    /// <remarks>
    /// SEPARATE FROM <see cref="Escape"/> BECAUSE THE CONTRACTS DIFFER. In a sentence a pipe is
    /// ordinary punctuation — <c>/sessions resume &lt;number|id&gt;</c> must render with its pipe
    /// intact. In a cell the pipe is the column delimiter: a session title containing one splits the
    /// row into more cells than the header declares, and Markdig drops the overflow silently. Adding
    /// <c>|</c> to <see cref="Escape"/>'s own set would fix the cell but break every sentence that
    /// mentions a pipe on purpose — this table's own footer among them.
    ///
    /// <para><see cref="Escape"/> RUNS FIRST, so a literal backslash already in the input is doubled
    /// before the pipe escape adds its own — a title carrying both a backslash and a pipe still
    /// renders as one escaped pipe rather than an accidentally-escaped backslash.</para>
    /// </remarks>
    /// <param name="text">A path, a title, or anything else Core did not author, landing in a cell.</param>
    /// <returns>The text, safe to interpolate into a `|`-delimited row.</returns>
    public static string EscapeCell(string text) =>
        string.IsNullOrEmpty(text) ? text : Escape(text).Replace("|", @"\|");

    /// <summary>
    /// Wraps a value in a CODE SPAN, escaping only what can break out of one.
    /// </summary>
    /// <remarks>
    /// A THIRD ESCAPE BECAUSE A CODE SPAN HAS A THIRD CONTRACT, and it is the loosest of the three:
    /// markdown processes no emphasis, no links and no backslash escapes inside one, so every
    /// character <see cref="Escape"/> guards against is already inert. Its backslash therefore buys
    /// nothing and does not disappear — <c>`read\_file`</c> renders with the backslash ON SCREEN.
    ///
    /// <para>NOT A CORNER CASE. Tool names are the values that land here and nearly all of them
    /// carry an underscore (<c>read_file</c>, <c>run_shell</c>, <c>write_file</c>), so escaping for
    /// prose would put a stray backslash on almost every row of a table built from them.</para>
    ///
    /// <para>THE BACKTICK IS THE ONE REAL HAZARD, and the fix is the same one <see cref="Fence"/>
    /// uses for the same reason: a span cannot escape its own delimiter, so the delimiter must
    /// OUTRUN the longest run inside it. A pipe needs nothing — a span hides one from the table
    /// parser on its own, which is the finding <c>CoreMarkdownTests</c> pins.</para>
    /// </remarks>
    /// <param name="text">A tool name, a command, or anything else shown as literal code.</param>
    /// <returns>The value wrapped in backticks, safe in a sentence or in a cell.</returns>
    public static string CodeSpan(string text)
    {
        if (string.IsNullOrEmpty(text)) return "``";

        var longest = 0;
        var run = 0;
        foreach (var c in text)
        {
            run = c == '`' ? run + 1 : 0;
            if (run > longest) longest = run;
        }

        var ticks = new string('`', longest + 1);

        // A SPACE WHEN THE VALUE'S OWN EDGE IS A BACKTICK, which markdown then strips: without it
        // the delimiters and the content run together and the span's boundaries are unreadable.
        var pad = text.StartsWith('`') || text.EndsWith('`') ? " " : "";

        return $"{ticks}{pad}{text}{pad}{ticks}";
    }

    /// <summary>
    /// Wraps drawn output in a fence long enough that its own content cannot close it.
    /// </summary>
    /// <remarks>
    /// FENCED CONTENT CANNOT BE ESCAPED — that is the entire guarantee a fence offers, and the
    /// reason drawn output goes in one. So the only lever is fence LENGTH, which is markdown's own
    /// answer: a fence is closed by a run of at least as many backticks, so a longer opener cannot
    /// be closed by anything inside it.
    ///
    /// <para>NOT A CORNER CASE, WHICH IS WHY A HARDCODED <c>```</c> WILL NOT DO. This app edits
    /// markdown constantly — instruction files, briefs, reports — so a <c>/diff</c> line reading
    /// <c>+```csharp</c> is ordinary, and a three-backtick fence around it closes at that line and
    /// spills the rest of the diff into the transcript as prose.</para>
    /// </remarks>
    /// <param name="body">The drawn text, verbatim. No trailing newline needed.</param>
    /// <param name="language">The fence's info string — <c>diff</c> to get a highlighter,
    /// <c>text</c> for a picture that only wants the "do not reflow" promise.</param>
    /// <returns>The body between an opening and closing fence.</returns>
    public static string Fence(string body, string language = "text")
    {
        // The longest run of backticks anywhere in the body, so the fence can outrun it. Three is
        // the floor because a shorter fence is not a fence.
        var longest = 0;
        var run = 0;
        foreach (var c in body)
        {
            run = c == '`' ? run + 1 : 0;
            if (run > longest) longest = run;
        }

        var fence = new string('`', Math.Max(3, longest + 1));
        return $"{fence}{language}\n{body}\n{fence}";
    }
}
