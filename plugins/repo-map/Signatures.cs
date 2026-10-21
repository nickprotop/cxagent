using System.Text.RegularExpressions;

namespace CxAgent.Plugins.RepoMap;

/// <summary>
/// A declaration's text as the map shows it: on one line, without its trailing punctuation, cut to a
/// length.
///
/// <para>SHARED BY BOTH READERS, so a declaration reads the same whichever one found it — a reader
/// who sees one file parsed and the next read as text should not see two styles.</para>
/// </summary>
public static class Signatures
{
    /// <summary>Longest signature quoted. A parameter list longer than this is cut, and says so.</summary>
    public const int MaxLength = 160;

    public static string Normalise(string raw)
    {
        // A parameter list begun on the line after its "(" joins as "( string": the space was the line break.
        var shown = Regex.Replace(Regex.Replace(raw, @"\s+", " "), @"([(\[]|(?<=\w)<) | ([)\]>])", "$1$2").Trim().TrimEnd(':').TrimEnd();
        // A TRAILING COMMA — rustfmt's, and many formatters' for a list that wraps — says nothing
        // once the list is on one line.
        return Regex.Replace(shown, @",\s*\)", ")");
    }

    /// <summary>Cut to <see cref="MaxLength"/>, ending in "…" when it was longer.</summary>
    public static string Cut(string shown) =>
        shown.Length <= MaxLength ? shown : shown[..MaxLength].TrimEnd() + "…";
}
