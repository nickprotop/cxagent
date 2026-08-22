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
