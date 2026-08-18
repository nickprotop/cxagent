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

        // AS SENT, kept before Outdent rewrites them in place. Refuse needs the model's ORIGINAL
        // indentation: its whole question is whether the model was quoting the file, and an
        // outdented copy has had exactly that evidence removed.
        var sentPattern = (string[])patternLines.Clone();
        var sentReplace = (string[])replaceLines.Clone();

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
            if (!fileIndent.StartsWith(sentIndent, StringComparison.Ordinal))
                return Refuse(matchedLines, sentPattern, sentReplace, replacement);

            var offset = fileIndent[sentIndent.Length..];
            if (add is null) add = offset;
            else if (add != offset) return Refuse(matchedLines, sentPattern, sentReplace, replacement);
        }

        // Nothing to add: the outdented replacement is already at the file's depth.
        if (string.IsNullOrEmpty(add)) return string.Join('\n', replaceLines);

        return string.Join('\n',
            replaceLines.Select(l => IsBlank(l) ? l : add + l));
    }

    /// <summary>
    /// What to write when no single shift describes the edit.
    ///
    /// <para>Refusal used to mean writing the model's text exactly as sent, which for a replacement
    /// sent at the wrong depth means a line landing at the wrong column in an indented file —
    /// visibly wrong, which was the point, but measured on two live drives it is wrong in a way that
    /// also LOOKS like a deliberate edit: one line at one tab among neighbours at three, inside an
    /// otherwise plausible diff, with nothing flagging it.</para>
    ///
    /// <para>Two cases can be placed without guessing, and both take their answer from the file
    /// rather than inventing one:</para>
    /// <list type="number">
    ///   <item>A ONE-LINE replacement has no shape to preserve, so the matched region's own line
    ///     start places it unambiguously.</item>
    ///   <item>A replacement whose line KEEPS ITS PATTERN COUNTERPART'S INDENTATION verbatim is
    ///     saying "leave this line's depth alone" — so it gets the depth of the FILE line that
    ///     counterpart matched. This is per-line, not one global shift: each answer comes from the
    ///     matched line it corresponds to, and a line the model re-indented on purpose is left
    ///     exactly as sent.</item>
    /// </list>
    ///
    /// <para>The second case is the common one and the reason refusal kept producing broken files: a
    /// model that de-indents only the FIRST line of a multi-line pattern — copying the rest from the
    /// file verbatim — is self-consistent about every line's shape and wrong only about the base,
    /// which is precisely what the file can supply.</para>
    /// </summary>
    private static string Refuse(
        string[] matchedLines, string[] patternLines, string[] replaceLines, string replacement)
    {
        if (matchedLines.Length == 0) return replacement;

        // ONE LINE: no shape, so the region's own start is the answer. The first NON-BLANK matched
        // line is where the region really begins; a leading blank has no indentation to copy.
        if (replaceLines.Length == 1)
        {
            foreach (var line in matchedLines)
            {
                if (IsBlank(line)) continue;
                return IsBlank(replaceLines[0]) ? replaceLines[0] : Indent(line) + replaceLines[0];
            }

            return replacement;
        }

        // MULTI-LINE. The question is whether the model was QUOTING the file's indentation or
        // writing its own, and the pattern answers it: a pattern line whose indent equals the file
        // line it matched was copied verbatim out of the file.
        //
        // That distinction is the whole rule. In the live case the model copied lines 2 and 3 from
        // the file and only mis-typed the first line's base — its intent was plainly the file's
        // shape, and the file can supply what it got wrong. Where a model indents everything itself
        // (a pattern sent flush left, or one using spaces against a tabbed file), it quoted nothing,
        // so there is no evidence it meant the file's shape and reconstructing it would be the
        // silent reshaping this design exists to prevent.
        var quotesTheFile = false;
        for (var i = 0; i < patternLines.Length && i < matchedLines.Length && !quotesTheFile; i++)
        {
            var quoted = Indent(patternLines[i]);
            quotesTheFile = quoted.Length > 0 && quoted == Indent(matchedLines[i]);
        }

        // OR THE SHAPE MATCHES, which is the same evidence by another route. A model that retypes a
        // block one tab short quotes NOTHING verbatim — yet its lines sit at the same depths relative
        // to each other as the file's do, which is exactly the claim "I meant your shape, I got the
        // base wrong".
        //
        // Seen live: a five-line method sent with every line one tab short and the FIRST line short
        // by two, because the model dropped the shared indent from line 0 as well. Outdent removes
        // the MINIMUM indent, which line 0 had made zero, so the remaining lines kept an extra tab,
        // the per-line offsets disagreed, and the shift refused. The block landed one level out —
        // its doc comment at column 0 beside a neighbour at two tabs.
        if (!quotesTheFile && !ShiftedByAConstant(matchedLines, patternLines)) return replacement;

        var placed = new string[replaceLines.Length];
        var changed = false;
        string? lastShift = null;

        for (var i = 0; i < replaceLines.Length; i++)
        {
            placed[i] = replaceLines[i];

            // A LINE THE REPLACEMENT ADDED has no counterpart to correlate with — but it is being
            // written into a block every other line of which just moved, so leaving it behind is the
            // one outcome that is certainly wrong. Seen live: a new `?? c?.BackgroundColor` clause
            // stayed a tab short of the expression it belongs to, while the lines above and below it
            // were placed correctly.
            //
            // It takes the same shift as the last line that HAD a counterpart, which is what "the
            // model wrote it at the depth it wrote its neighbours" means in practice.
            if (i >= patternLines.Length || i >= matchedLines.Length)
            {
                if (lastShift is { } carry && !IsBlank(replaceLines[i]))
                {
                    var sent = Indent(replaceLines[i]);
                    var moved = carry + replaceLines[i][sent.Length..];
                    if (moved != placed[i]) { placed[i] = moved; changed = true; }
                }
                continue;
            }
            if (IsBlank(replaceLines[i]) || IsBlank(matchedLines[i])) continue;

            // "Unchanged depth" is the signal for a given line: where the replacement's indent
            // differs from the pattern's, the model moved that line deliberately and its choice
            // stands.
            var sentIndent = Indent(replaceLines[i]);
            if (sentIndent != Indent(patternLines[i])) continue;

            // What this line's indent became, so an ADDED line after it can follow the same move.
            lastShift = Indent(matchedLines[i]);

            var rebased = lastShift + replaceLines[i][sentIndent.Length..];
            if (rebased == placed[i]) continue;

            placed[i] = rebased;
            changed = true;
        }

        return changed ? string.Join('\n', placed) : replacement;
    }

    /// <summary>
    /// Whether MOST lines are short by the same amount — a block retyped at the wrong depth.
    /// </summary>
    /// <remarks>
    /// <para>The evidence is a DOMINANT per-line offset, not a matching shape measured from the
    /// first line. My first attempt anchored on line 0 and failed on the very case it was written
    /// for: a model that retypes a method one tab short usually drops the shared indent from its
    /// OPENING line entirely, so line 0 is short by two while every other line is short by one.
    /// Anchored there, the block looks restructured; counted across all lines, four of five agree
    /// and one is an outlier.</para>
    ///
    /// <para>A strict majority is required, so a genuinely reshaped block — where offsets scatter —
    /// still refuses and is written as sent. Lengths rather than strings, because a model typing
    /// spaces where the file has tabs is describing the same shape and refusing over notation would
    /// send us back to writing a visibly broken block.</para>
    /// </remarks>
    private static bool ShiftedByAConstant(string[] matchedLines, string[] patternLines)
    {
        var offsets = new List<int>();
        for (var i = 0; i < matchedLines.Length && i < patternLines.Length; i++)
        {
            if (IsBlank(matchedLines[i]) || IsBlank(patternLines[i])) continue;
            offsets.Add(Indent(matchedLines[i]).Length - Indent(patternLines[i]).Length);
        }

        // TWO LINES MINIMUM. One line has nothing to be consistent WITH, and calling that "shifted
        // by a constant" would make this an unconditional yes.
        if (offsets.Count < 2) return false;

        var best = offsets.GroupBy(o => o).OrderByDescending(g => g.Count()).First();
        return best.Count() * 2 > offsets.Count;
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
