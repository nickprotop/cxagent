namespace CxAgent.UI;

/// <summary>
/// An <c>@</c> reference being typed: where it starts, and what has been typed after it.
///
/// <para>THE WHOLE AMBIGUITY OF THE FEATURE, IN ONE FUNCTION. The menu opens on <see cref="At"/>'s
/// answer and nothing else, so it lives apart from the portal it drives — a question about a string
/// and a caret needs no window to be right, and this is the piece that has to be right.</para>
/// </summary>
/// <param name="Start">Index of the <c>@</c> itself, so a completion can splice around it.</param>
/// <param name="Prefix">
/// What follows the <c>@</c>, up to the caret. Empty for a bare <c>@</c>, which offers everything.
/// </param>
public readonly record struct AtToken(int Start, string Prefix)
{
    /// <summary>
    /// The reference the caret is inside, or null when it is not inside one.
    ///
    /// <para>AN <c>@</c> OPENS A TOKEN ONLY AT A WORD BOUNDARY — the start of the text, or after
    /// whitespace. Every other <c>@</c> is literal, and the case that decides the rule is an email
    /// address: <c>nick@example.com</c> is common, and a menu opening inside it is wrong every
    /// single time. Attribute syntax and <c>user@host</c> fall out of the same test.</para>
    ///
    /// <para>THE TOKEN ENDS AT THE CARET, NOT AT THE END OF THE TEXT. Someone editing the middle of
    /// a sentence is completing what is behind the caret; the words after it are already written and
    /// are not part of what they are naming.</para>
    ///
    /// <para>A SPACE CLOSES IT. <c>@src/UI now fix it</c> is a finished reference followed by prose,
    /// so the menu must be shut — reopening it on every later keystroke would put a portal over the
    /// transcript for the rest of the sentence.</para>
    /// </summary>
    /// <param name="text">The composer's whole contents.</param>
    /// <param name="caret">Where the caret is, as an index into <paramref name="text"/>.</param>
    public static AtToken? At(string? text, int caret)
    {
        if (string.IsNullOrEmpty(text)) return null;

        // CLAMPED RATHER THAN TRUSTED. A caret past the end means a control and a string that
        // disagree, which is a bug elsewhere — and crashing the composer is a worse way to report it
        // than completing against what is actually there.
        caret = Math.Clamp(caret, 0, text.Length);

        // Back to the nearest whitespace: everything from there to the caret is one word, and only
        // its first character decides whether this is a reference.
        var start = caret;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1])) start--;

        if (start >= caret || text[start] != '@') return null;

        return new AtToken(start, text[(start + 1)..caret]);
    }
}
