namespace CxAgent.Plugins.Lsp;

/// <summary>
/// Where a call lands in a file: the 0-based position the server takes, a note when it is not exactly where the
/// call said, or why it cannot be answered at all.
/// </summary>
/// <param name="Line">0-based line.</param>
/// <param name="Character">0-based column, in UTF-16 units — a tab is one, as the server counts it.</param>
/// <param name="Note">Said to the model beside the answer when the position was moved, so it knows what was asked.</param>
/// <param name="Problem">Why no position could be found; the call is refused with it.</param>
internal sealed record ResolvedPosition(int Line, int Character, string? Note = null, string? Problem = null);

/// <summary>
/// A call's position, found from a NAME rather than counted.
///
/// <para>THE MODEL CANNOT COUNT ITS WAY THERE. read_file shows no line numbers and grep shows lines with their
/// indentation stripped, so a line and column worked out from either is routinely a line off and a tab or two
/// short — and the server answers a position on a comment or a brace with nothing, which reads as "not indexed".
/// Given the symbol's name, the plugin finds the column itself, and forgives a line counted a few off.</para>
/// </summary>
internal static class SymbolPosition
{
    /// <summary>How far from the given line a name is looked for: a miscount, not a search — grep is for that.</summary>
    private const int Reach = 5;

    public static ResolvedPosition Resolve(IReadOnlyList<string> lines, int line, int? character, string? symbol)
    {
        if (line < 1 || line > lines.Count)
            return new(0, 0, Problem: $"line {line} is outside the file, which has {lines.Count} lines.");
        var index = line - 1;

        if (!string.IsNullOrWhiteSpace(symbol))
        {
            // A QUALIFIED NAME BY ITS LAST PART: `Device.Id` is written `d.Id` or `Id` where it is used.
            var name = symbol.Trim();
            var dot = name.LastIndexOf('.');
            if (dot >= 0 && dot < name.Length - 1) name = name[(dot + 1)..];

            // ITS OWN LINE FIRST, then outward — the line below before the one above at each distance, since a doc
            // comment sits above what it documents and is where a miscount lands.
            for (var distance = 0; distance <= Reach; distance++)
            {
                foreach (var candidate in distance == 0 ? [index] : new[] { index + distance, index - distance })
                {
                    if (candidate < 0 || candidate >= lines.Count) continue;
                    var column = Find(lines[candidate], name, distance == 0 ? character - 1 : null);
                    if (column < 0) continue;
                    return candidate == index
                        ? new(candidate, column)
                        : new(candidate, column, Note: $"'{name}' is on line {candidate + 1}, not line {line} — answered for line {candidate + 1}.");
                }
            }
            return new(index, 0, Problem: $"'{name}' is not on line {line} or within {Reach} lines of it. grep for it to find its line.");
        }

        if (character is not { } at)
            return new(index, 0, Problem: "say which symbol: give its name in 'symbol' (or a 'character' column).");

        // A COLUMN PAST THE END STAYS ON ITS LINE, at its end, as the protocol says it should. OmniSharp instead
        // carries it on into the next line and answers for whatever symbol it finds there.
        var length = lines[index].Length;
        return at - 1 > length
            ? new(index, length, Note: $"character {at} is past the end of line {line} ({length} characters) — answered for its end.")
            : new(index, Math.Max(0, at - 1));
    }

    /// <summary>
    /// The 0-based column of <paramref name="name"/> on <paramref name="text"/> as a whole identifier, or -1: the
    /// occurrence that holds <paramref name="near"/> when there is one, otherwise the first.
    /// </summary>
    private static int Find(string text, string name, int? near)
    {
        var first = -1;
        for (var at = text.IndexOf(name, StringComparison.Ordinal); at >= 0; at = text.IndexOf(name, at + 1, StringComparison.Ordinal))
        {
            // A WHOLE NAME ONLY: `Task` inside `TaskList` is another symbol.
            var before = at == 0 || !IsIdentifier(text[at - 1]);
            var after = at + name.Length == text.Length || !IsIdentifier(text[at + name.Length]);
            if (!before || !after) continue;
            if (near is { } n && n >= at && n < at + name.Length) return at;
            if (first < 0) first = at;
        }
        return first;
    }

    private static bool IsIdentifier(char c) => char.IsLetterOrDigit(c) || c == '_';
}
