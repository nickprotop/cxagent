namespace CxAgent.Core.Plugins.Builtin;

/// <summary>
/// Shifts a replacement onto the indentation of the text it is replacing.
///
/// <para>A PURE FUNCTION, deliberately. This logic lived inside <see cref="FileJobPlugin"/> reading
/// file offsets and slices, and every attempt to fix it was validated through a whole tool call —
/// which meant a wrong answer could come from the matcher, the slice, the shift, or a stale build,
/// and telling those apart cost more than the fix. Three strings in, one string out: the spec is
/// testable directly and a failure has exactly one place to be.</para>
///
/// <para>THE ALGORITHM is Aider's (editblock_coder.py: replace_part_with_missing_leading_whitespace),
/// which is the only implementation in this space whose failure mode is structurally impossible
/// rather than merely unobserved:</para>
/// <list type="number">
///   <item>OUTDENT the pattern and the replacement, each by its own shallowest line, so both start
///     at column 0 and neither side's chosen base pollutes the comparison.</item>
///   <item>MEASURE what the file's matched lines add back. Every line must agree.</item>
///   <item>PREPEND that one string to every non-blank replacement line.</item>
/// </list>
///
/// <para>PREPEND, NEVER REBUILD. The engine this replaces stripped each line's indentation and
/// reconstructed it from a computed base, which is how a non-uniform block got silently reshaped and
/// an "anchored" replacement turned three tabs into eight. Prepending cannot do either: whatever
/// structure the model wrote survives because nothing touches it. Roo Code rebuilds the same way and
/// carries unfixed issues for exactly that symptom.</para>
///
/// <para>REFUSES rather than guesses. When the lines disagree there is no single answer, and
/// inventing one is what caused the original failure. The replacement is then written exactly as the
/// model sent it — visibly wrong beats silently wrong, and the result is echoed back so the model
/// can see it.</para>
/// </summary>
public static class IndentShift
{
    /// <summary>
    /// The replacement, shifted onto <paramref name="matched"/>'s indentation.
    /// </summary>
    /// <param name="matched">
    /// The text being replaced, INCLUDING the indentation of the line it starts on. A caller with a
    /// character offset must extend it back to the line start — the matched span itself begins at
    /// the pattern's first character, so its own indentation is not in it, and measuring there finds
    /// nothing to compare.
    /// </param>
    /// <param name="pattern">The pattern as the model sent it.</param>
    /// <param name="replacement">The replacement as the model sent it.</param>
    /// <returns>
    /// The shifted replacement, or <paramref name="replacement"/> unchanged when no single shift
    /// describes the edit.
    /// </returns>
    public static string Apply(string matched, string pattern, string replacement)
    {
        var matchedLines = Split(matched);
        var patternLines = Split(pattern);
        var replaceLines = Split(replacement);

        // Each side by its OWN base. Aider outdents both by their shared minimum, which is
        // equivalent when the model indents both consistently — and it does not when the pattern is
        // sent at column 0 and the replacement is not. Then the shared minimum is 0, nothing moves,
        // and the file's indent lands on top of the replacement's own.
        Outdent(patternLines);
        Outdent(replaceLines);

        // What the file adds back, if one string explains every line.
        string? add = null;
        for (var i = 0; i < matchedLines.Length && i < patternLines.Length; i++)
        {
            if (IsBlank(matchedLines[i])) continue;

            var fileIndent = Indent(matchedLines[i]);
            var sentIndent = Indent(patternLines[i]);

            // The file's line must literally BEGIN with the outdented pattern's indent. When it does
            // not, the two differ by something other than a prefix — mixed tabs and spaces, most
            // often — and no amount of prepending reconciles them.
            if (!fileIndent.StartsWith(sentIndent, StringComparison.Ordinal)) return replacement;

            var offset = fileIndent[sentIndent.Length..];
            if (add is null) add = offset;
            else if (add != offset) return replacement;   // lines disagree: write as sent
        }

        // Nothing to add: the outdented replacement is already at the file's depth.
        if (string.IsNullOrEmpty(add)) return string.Join('\n', replaceLines);

        return string.Join('\n',
            replaceLines.Select(l => IsBlank(l) ? l : add + l));
    }

    private static string[] Split(string s) => s.Replace("\r\n", "\n").Split('\n');

    /// <summary>Blank in the sense that matters here: no indentation to measure and none to add.</summary>
    private static bool IsBlank(string line) => line.Trim().Length == 0;

    /// <summary>The run of spaces and tabs opening a line.</summary>
    private static string Indent(string line)
    {
        var i = 0;
        while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
        return line[..i];
    }

    /// <summary>
    /// Removes the shallowest common indentation, in place.
    ///
    /// <para>Cannot lose structure: the minimum is by definition the shallowest line, so every other
    /// line keeps its depth relative to it. That property is what makes the later prepend safe, and
    /// it is precisely what rebuilding each line from a computed base gave up.</para>
    ///
    /// <para>Blank lines are left alone — they have no indentation to remove, and trimming them
    /// would add trailing whitespace to lines that had none.</para>
    /// </summary>
    private static void Outdent(string[] lines)
    {
        var min = lines.Where(l => !IsBlank(l)).Select(l => Indent(l).Length)
            .DefaultIfEmpty(0).Min();
        if (min == 0) return;

        for (var i = 0; i < lines.Length; i++)
            if (!IsBlank(lines[i])) lines[i] = lines[i][min..];
    }
}
