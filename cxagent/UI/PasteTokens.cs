using System.Globalization;
using SharpConsoleUI.Controls;

namespace CxAgent.UI;

/// <summary>
/// Which pastes become a token in the composer, and what the token says.
///
/// <para>A LONG PASTE IS ONE CHIP, NOT A SCREENFUL. Eighteen lines of a stack trace pushed the goal
/// typed around them out of the three-row box, and the box then scrolled through text nobody meant to
/// edit. As <c>[Pasted text #1 +18 lines]</c> the goal stays readable, and what is sent is still the
/// whole text: the control's <c>Input</c> gives each token's value.</para>
///
/// <para>ONE PER COMPOSER, because the numbers name the tokens in one message apart. They count up
/// for the life of the composer rather than per message, so a recalled message's #1 is never
/// confused with a fresh #1 pasted beside it.</para>
/// </summary>
internal sealed class PasteTokens
{
    /// <summary>The token's tag, so a later kind (an image) can be told apart from a paste.</summary>
    internal const string Tag = "paste";

    /// <summary>
    /// Three lines or more. One or two lines are what a user usually goes on to edit — a command, a
    /// path, a sentence — and hiding them in a chip would cost a keystroke to get them back.
    /// </summary>
    internal const int MinLines = 3;

    /// <summary>One line this long fills the box as surely as many lines do: a minified file, a log line.</summary>
    internal const int MinChars = 800;

    private int _count;

    /// <summary>The token for a paste, or null when it should paste as text.</summary>
    internal PromptToken? For(string pasted)
    {
        // THE VALUE IS WHAT THE MODEL READS, so a Windows clipboard's \r\n is folded here, the same
        // as the control folds a paste it keeps as text.
        var text = pasted.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = LineCount(text);
        if (lines < MinLines && text.Length < MinChars) return null;

        _count++;
        var size = lines >= MinLines
            ? $"+{lines} lines"
            : string.Create(CultureInfo.InvariantCulture, $"{text.Length:N0} chars");
        return new PromptToken($"[Pasted text #{_count} {size}]", text, Tag);
    }

    /// <summary>
    /// Lines as a reader counts them: a final newline ends the last line rather than starting one.
    /// Copying whole lines from an editor brings that newline along, and "+3 lines" over two would
    /// read as a miscount.
    /// </summary>
    private static int LineCount(string text)
    {
        var body = text.EndsWith('\n') ? text[..^1] : text;
        return body.Count(c => c == '\n') + 1;
    }

    /// <summary>
    /// Turns one token back into its text, in place.
    ///
    /// <para>BY IDENTITY, NOT AT THE CARET. A click raises TokenActivated before the control moves its
    /// caret to the token, so the control's own ExpandTokenAtCaret would act on wherever the caret
    /// was; and with two tokens side by side, a caret between them is "at" both. Rebuilding the
    /// segments with this one replaced cannot pick the wrong one. The cost is the caret, which goes
    /// to the end of the text.</para>
    /// </summary>
    internal static void Expand(PromptControl composer, PromptToken token)
    {
        var segments = composer.InputSegments;
        if (!segments.Any(s => ReferenceEquals(s.Token, token))) return;

        composer.SetInputSegments(segments.Select(s =>
            ReferenceEquals(s.Token, token) ? PromptSegment.FromText(token.Value) : s));
    }
}

/// <summary>
/// What a submit reads from the composer.
/// </summary>
/// <param name="Text">What is sent: each token as its text.</param>
/// <param name="Echo">
/// What the transcript row shows, and the session list titles the session by — each token as its
/// label. Null when that is the same as <paramref name="Text"/>, which is what "no echo" means to
/// Session.Submit.
/// </param>
/// <param name="Segments">The line with its tokens, for ↑ to bring back as it was typed.</param>
internal sealed record ComposerLine(string Text, string? Echo, IReadOnlyList<PromptSegment> Segments)
{
    internal static ComposerLine Of(PromptControl composer)
    {
        var text = composer.Input;
        var shown = composer.InputLabelText;
        return new(text, shown == text ? null : shown, composer.InputSegments);
    }
}
