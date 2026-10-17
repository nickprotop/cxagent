using SharpConsoleUI.Controls;
using SharpConsoleUI.Core;
using SharpConsoleUI.Helpers;

namespace CxAgent.UI;

/// <summary>When a selection copies, and on what key.</summary>
/// <param name="Key">The key that copies.</param>
/// <param name="Modifiers">The modifiers that go with it.</param>
/// <param name="Enabled">Whether the control lets the key copy at all.</param>
/// <param name="Exact">Whether the modifiers must match exactly, or only include Control.</param>
public sealed record CopyShortcut(ConsoleKey Key, ConsoleModifiers Modifiers, bool Enabled, bool Exact);

/// <summary>A toast's text and how it is shown.</summary>
public sealed record CopyNote(string Text, NotificationSeverity Severity);

/// <summary>
/// The toast that says a selection was copied, and where it went.
///
/// <para>THE WINDOW FRAMEWORK COPIES AND SAYS NOTHING. It consumes the key, so cxagent never learns a copy
/// happened, and a copy that reached no clipboard looked exactly like one that worked. The toast is decided
/// from the window's preview of the key — before the framework copies — by the framework's own rule, which
/// is why that rule is restated here rather than approximated.</para>
/// </summary>
public static class CopyToast
{
    /// <summary>
    /// How the active selection copies, or null when there is nothing selected.
    ///
    /// <para>TWO RULES, AS THE FRAMEWORK HAS THEM (WindowEventDispatcher.ProcessInput): a copyable control —
    /// a transcript body — copies on its own key and exact modifiers, if it allows copying at all; any other
    /// selection, the composer's, copies on C with Control among the modifiers.</para>
    /// </summary>
    public static CopyShortcut? ShortcutOf(ISelectableControl? active) => active switch
    {
        not { HasSelection: true } => null,
        ICopyableControl copyable => new(copyable.CopyKey, copyable.CopyModifiers, copyable.CopyEnabled, Exact: true),
        _ => new(ConsoleKey.C, ConsoleModifiers.Control, Enabled: true, Exact: false),
    };

    /// <summary>
    /// What to show for this key, or null when it copies nothing.
    ///
    /// <para>NO CLIPBOARD TOOL IS SAID OUTRIGHT. Without wl-copy, xclip or xsel the framework keeps the text
    /// in its own process and sends it to the terminal by OSC 52, which many terminals ignore — so a plain
    /// "Copied" there would repeat the silence this exists to end. Short, because a toast cuts its line at
    /// about forty characters, and a cut-off reason is no reason.</para>
    /// </summary>
    public static CopyNote? For(ConsoleKeyInfo key, CopyShortcut? shortcut, ClipboardBackend backend)
    {
        if (shortcut is not { Enabled: true } s || key.Key != s.Key) return null;
        var matches = s.Exact ? key.Modifiers == s.Modifiers : key.Modifiers.HasFlag(s.Modifiers);
        if (!matches) return null;

        return backend == ClipboardBackend.InternalFallback
            ? new CopyNote("Copied · no clipboard tool found", NotificationSeverity.Warning)
            : new CopyNote("Copied", NotificationSeverity.Success);
    }
}
