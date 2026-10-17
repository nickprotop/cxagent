using CxAgent.UI;
using SharpConsoleUI.Core;
using SharpConsoleUI.Helpers;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The toast that says a copy happened. The window framework copies a selection on Ctrl+C and says
/// nothing, so a copy that reached no clipboard looked exactly like one that worked.
/// </summary>
public class CopyToastTests
{
    private static readonly ConsoleKeyInfo CtrlC = new('\x03', ConsoleKey.C, false, false, true);
    private static readonly ConsoleKeyInfo CtrlShiftC = new('\x03', ConsoleKey.C, true, false, true);
    private static readonly ConsoleKeyInfo CtrlV = new('\x16', ConsoleKey.V, false, false, true);

    private static readonly CopyShortcut Transcript = new(ConsoleKey.C, ConsoleModifiers.Control, Enabled: true, Exact: true);
    private static readonly CopyShortcut Composer = new(ConsoleKey.C, ConsoleModifiers.Control, Enabled: true, Exact: false);

    [Fact]
    public void NothingSelected_SaysNothing() =>
        Assert.Null(CopyToast.For(CtrlC, null, ClipboardBackend.WlClipboard));

    [Fact]
    public void ACopyWithAClipboardTool_SaysCopied()
    {
        var note = CopyToast.For(CtrlC, Transcript, ClipboardBackend.WlClipboard)!;

        Assert.Equal("Copied", note.Text);
        Assert.Equal(NotificationSeverity.Success, note.Severity);
    }

    /// <summary>
    /// NO TOOL, AND IT SAYS SO. With no wl-copy, xclip or xsel, the text goes only to the terminal by
    /// OSC 52 — which many terminals ignore — so "Copied" alone would be the very silence this replaces.
    /// </summary>
    [Fact]
    public void ACopyWithNoClipboardTool_WarnsWhereItWent()
    {
        var note = CopyToast.For(CtrlC, Transcript, ClipboardBackend.InternalFallback)!;

        Assert.Contains("no clipboard tool", note.Text);
        Assert.Equal(NotificationSeverity.Warning, note.Severity);
    }

    [Fact]
    public void AnotherKey_SaysNothing() =>
        Assert.Null(CopyToast.For(CtrlV, Transcript, ClipboardBackend.WlClipboard));

    [Fact]
    public void CopyTurnedOff_SaysNothing() =>
        Assert.Null(CopyToast.For(CtrlC, Transcript with { Enabled = false }, ClipboardBackend.WlClipboard));

    /// <summary>
    /// THE FRAMEWORK'S OWN RULE, both halves: a copyable control copies on its exact modifiers, any other
    /// selection on anything holding Control. A toast on a key that copied nothing would be the opposite lie.
    /// </summary>
    [Fact]
    public void ExtraModifiers_CopyOnlyWhereTheFrameworkWould()
    {
        Assert.Null(CopyToast.For(CtrlShiftC, Transcript, ClipboardBackend.WlClipboard));
        Assert.NotNull(CopyToast.For(CtrlShiftC, Composer, ClipboardBackend.WlClipboard));
    }

    [Fact]
    public void AControlWithNoSelection_HasNoShortcut() =>
        Assert.Null(CopyToast.ShortcutOf(new SharpConsoleUI.Controls.MarkupControl(["text"])));
}
