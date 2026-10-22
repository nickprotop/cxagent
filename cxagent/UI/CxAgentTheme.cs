using SharpConsoleUI;
using SharpConsoleUI.Themes;

namespace CxAgent.UI;

/// <summary>
/// cxagent's own theme: ModernGray with the darker ground this app was designed against.
///
/// <para>WHY A THEME AND NOT A DARKER DERIVATION. Every surface in <see cref="ColorScheme"/> is a
/// step away from the active theme's window background, which is what lets a theme change move the
/// whole palette together. ModernGray's background is lighter than the near-black cxagent shipped
/// with, so deriving from it directly would have lightened the entire app on upgrade — with no user
/// action and no way back.</para>
///
/// <para>The alternative considered was shading whatever background a theme gives us until it
/// matched. That keeps today's look and makes every OTHER theme render darker than its author
/// intended — Ocean's blue ground and Daylight's white one both dragged toward cxagent's taste.
/// Naming our ground as a theme instead means cxagent looks like itself, every other theme looks
/// like itself, and the difference is visible in the picker rather than hidden in a multiplier.</para>
/// </summary>
public static class CxAgentTheme
{
    /// <summary>The registered name, and what the picker shows.</summary>
    public const string Name = "cxagent";

    private const string Summary = "cxagent's own — ModernGray on a near-black ground";

    /// <summary>
    /// The near-black chat ground: the value <see cref="ColorScheme"/> carried as a literal before
    /// surfaces were derived, kept here so the app's own look survives the move to themes.
    /// </summary>
    private static readonly Color Ground = new(0x0d, 0x0d, 0x0d);

    /// <summary>
    /// The accent: a warm amber, replacing the cyan cxagent inherited by spelling ModernGray's
    /// <c>PrimaryColor</c> by hand.
    ///
    /// <para>WARM AGAINST A NEAR-BLACK GROUND, and warm against the violet the wordmark fades into
    /// (<c>Heading</c>, #9d7cd8) — amber and violet sit far enough apart on the wheel that the
    /// gradient travels rather than muddying, which two cool colours would not. Cyan1 is the
    /// brightest, most saturated colour the terminal has; on this ground it reads as glare rather
    /// than emphasis, and it was never chosen so much as inherited.</para>
    ///
    /// <para>This is the accent EVERYWHERE it appears — the wordmark, the mode indicator, command
    /// names, diff paths, panel headings, the context readout below its first threshold — because
    /// all of those resolve through <c>ColorRole.Primary</c> now rather than through a literal.</para>
    /// </summary>
    private static readonly Color Accent = new(0xe8, 0x9e, 0x64);

    /// <summary>
    /// Registers the theme and makes it active unless the caller asks for another by name.
    /// </summary>
    /// <param name="system">The window system whose registry and theme state to use.</param>
    /// <param name="preferred">
    /// A theme name from configuration, or null for cxagent's own. An unknown name falls back to
    /// cxagent's rather than failing: a misspelling in config is not worth refusing to start over,
    /// and the wrong colours are self-evident in a way a stack trace is not.
    /// </param>
    /// <returns>The name of the theme that ended up active.</returns>
    public static string Install(ConsoleWindowSystem system, string? preferred)
    {
        var theme = Theme.From(new ModernGrayTheme())
            .WithName(Name)
            .WithDescription(Summary)
            .With(t =>
            {
                t.WindowBackgroundColor = Ground;
                t.PrimaryColor = Accent;

                // LISTS IN THE SHAPE EVERY OTHER THEME USES: ordinary rows in the accent, the
                // selection a clearly darker accent bar with light text. ModernGray drew ordinary
                // rows in Cyan1 — the brightest colour the terminal has, and not this app's accent —
                // and its selection twenty shades from the background, so the rows NOT chosen were the
                // most vivid on screen and a two-answer trust question looked answered the wrong way.
                // The bar is the accent at about 45% over the ground, as Amber's is over its own.
                t.ListFocusedForegroundColor = Accent;
                t.ListSelectedBackgroundColor = new Color(0x6f, 0x4e, 0x34);
                t.ListSelectedForegroundColor = new Color(0xe0, 0xdc, 0xd5);

                // THE DIALOG PALETTE, FOR WHAT ONLY THE THEME CAN SET. Modal chrome reaches the dialogs
                // the library draws itself; tab headers and scrollbars have no per-control colour. Taken
                // from the palette of the theme being built, so they agree with cxagent's own dialogs.
                // Theme-wide by nature: every tab strip and scrollbar in the app takes them.
                var dialog = DialogPalette.For(t);
                t.ModalBackgroundColor = dialog.Surface;
                t.ModalBorderForegroundColor = dialog.ActiveBorder;
                t.ModalTitleForegroundColor = dialog.Title;
                // SINGLE, as every cxagent dialog is: a double line only on the library's own would mark
                // them as from somewhere else.
                t.UseDoubleLineBorderForModal = false;
                t.TabHeaderActiveBackgroundColor = dialog.Surface;
                t.TabHeaderActiveFocusedBackgroundColor = dialog.Surface;
                t.TabHeaderActiveForegroundColor = dialog.Title;
                t.TabHeaderActiveFocusedForegroundColor = dialog.Title;
                t.TabHeaderBackgroundColor = dialog.Surface;
                t.TabHeaderFocusedBackgroundColor = dialog.Surface;
                t.TabHeaderForegroundColor = dialog.Muted;
                t.TabHeaderFocusedForegroundColor = dialog.Foreground;
                t.TabHeaderDisabledBackgroundColor = dialog.Surface;
                t.ScrollbarThumbColor = dialog.Title;
                t.ScrollbarThumbUnfocusedColor = dialog.Muted;
                t.ScrollbarTrackColor = dialog.Rule;
                t.ScrollbarTrackUnfocusedColor = dialog.Rule;
                // An unfocused list keeps its selection visible as the card-selected step, not a brown
                // bar: in a list/detail dialog the list is unfocused whenever the detail is being edited.
                t.ListUnfocusedHighlightBackgroundColor = dialog.Selection;
                t.ListUnfocusedHighlightForegroundColor = dialog.Foreground;
                // EVERY OTHER CYAN MODERNGRAY CARRIES — a radio's dot, a focused button's label, a focused
                // section header, table chrome — in the accent, or in the border and rule colours for
                // chrome: each leftover is a colour this theme uses nowhere else, glaring inside a dialog.
                t.ActiveBorderForegroundColor = dialog.ActiveBorder;
                t.ActiveTitleForegroundColor = dialog.Title;
                t.ButtonFocusedForegroundColor = dialog.Title;
                t.MenuBarHighlightForegroundColor = dialog.Title;
                t.DropdownFocusedForegroundColor = dialog.Title;
                t.ProgressBarFilledColor = dialog.Title;
                t.TableBorderColor = dialog.Rule;
                t.TableHeaderForegroundColor = dialog.Title;
                t.TableScrollbarThumbColor = dialog.Muted;
                t.CollapsibleHeaderFocusedForegroundColor = dialog.Title;
                t.TabContentBorderColor = dialog.Rule;
                t.DatePickerFocusedForegroundColor = dialog.Title;
                t.DatePickerCalendarTodayColor = dialog.Title;
                t.DatePickerCalendarHeaderColor = dialog.Title;
                t.TimePickerFocusedForegroundColor = dialog.Title;
                t.SliderFilledTrackColor = dialog.Title;
                t.CheckboxFocusedForegroundColor = dialog.Title;
                t.CheckboxCheckmarkColor = dialog.Title;
            })
            .Build();

        system.ThemeRegistryService.RegisterTheme(Name, Summary, () => theme);

        // ORDER MATTERS: register before switching, so a config naming "cxagent" finds it.
        if (string.IsNullOrWhiteSpace(preferred))
            return system.ThemeStateService.SwitchTheme(Name) ? Name : Name;

        // CASE-INSENSITIVE, because nobody types "ModernGray" with the capitals in the right places
        // and a colour scheme is not worth refusing over one. `--theme ocean` finds Ocean; the
        // REGISTERED spelling is what gets switched to and reported, so the status bar still shows
        // the name the theme's author gave it rather than whatever the user typed.
        var match = system.ThemeRegistryService.GetAvailableThemes()
            .FirstOrDefault(t => string.Equals(t.Name, preferred, StringComparison.OrdinalIgnoreCase));

        var wanted = match?.Name ?? preferred!;
        return system.ThemeStateService.SwitchTheme(wanted) ? wanted
            : system.ThemeStateService.SwitchTheme(Name) ? Name
            : system.ThemeStateService.CurrentTheme.Name ?? Name;
    }
}
