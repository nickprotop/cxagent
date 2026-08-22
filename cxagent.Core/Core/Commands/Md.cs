using System.Text;

namespace CxAgent.Core.Commands;

/// <summary>
/// Markdown helpers for text Core writes.
///
/// <para>REPLACES <c>Markup</c>, WHICH ESCAPED FOR THE WRONG FORMAT. That class turned <c>[</c> into
/// <c>[[</c> because Core wrote SharpConsoleUI markup; Core writes markdown now, where the character
/// that ruins a sentence is an underscore in a filename.</para>
///
/// <para>ITS COLOUR CONSTANTS ARE GONE RATHER THAN TRANSLATED. <c>Muted</c>, <c>Accent</c>,
/// <c>Danger</c> and <c>Caution</c> existed to name tones, and tone now rides in
/// <see cref="Severity"/>. A constant called <c>Caution</c> holding a markdown emphasis marker would
/// be the same mistake in a new spelling.</para>
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
}
