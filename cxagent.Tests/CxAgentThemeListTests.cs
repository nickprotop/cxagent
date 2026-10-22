using CxAgent.UI;
using SharpConsoleUI;
using SharpConsoleUI.Drivers;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The cxagent theme's lists: the selected row is the one that stands out.
///
/// <para>ModernGray, which this theme starts from, draws a focused list's ordinary rows in Cyan1 and
/// its selected row on a grey one step lighter than the panel — so the rows NOT chosen were the most
/// vivid things on screen, and a two-answer trust question looked answered the wrong way.</para>
/// </summary>
public class CxAgentThemeListTests
{
    private static SharpConsoleUI.Themes.ITheme Installed()
    {
        var system = new ConsoleWindowSystem(new HeadlessConsoleDriver(80, 24));
        CxAgentTheme.Install(system, null);
        return system.ThemeStateService.CurrentTheme;
    }

    /// <summary>The same shape as every other theme: ordinary rows in the theme's accent.</summary>
    [Fact]
    public void OrdinaryRows_InAFocusedList_AreTheAccent()
    {
        var theme = Installed();

        Assert.Equal(theme.PrimaryColor, theme.ListFocusedForegroundColor);
    }

    /// <summary>
    /// The selection is a bar that reads as one: clearly apart from the list's own background, with
    /// light text on it. ModernGray's was twenty shades from the background — almost invisible.
    /// </summary>
    [Fact]
    public void TheSelectedRow_IsAClearBar_WithLightText()
    {
        var theme = Installed();
        static int Brightness(SharpConsoleUI.Color? c) => c is { } x ? x.R + x.G + x.B : 0;

        Assert.True(Brightness(theme.ListSelectedBackgroundColor) - Brightness(theme.ListBackgroundColor) >= 90,
            $"bar {theme.ListSelectedBackgroundColor} against background {theme.ListBackgroundColor}");
        Assert.True(Brightness(theme.ListSelectedForegroundColor) > 600);
    }

    /// <summary>Only cxagent's own theme changes; a theme the user picked keeps its author's lists.</summary>
    [Fact]
    public void OtherThemes_KeepTheirOwnLists()
    {
        var system = new ConsoleWindowSystem(new HeadlessConsoleDriver(80, 24));
        CxAgentTheme.Install(system, "ModernGray");

        Assert.Equal(SharpConsoleUI.Color.Cyan1, system.ThemeStateService.CurrentTheme.ListFocusedForegroundColor);
    }

    /// <summary>NO CYAN IN A DIALOG: ModernGray's modal border and title are Cyan1, which this theme never
    /// uses anywhere else. The library's own dialogs (Flow questions, file pickers) read these.</summary>
    [Fact]
    public void ModalChrome_IsTheDialogPalettes()
    {
        var theme = Installed();
        var p = DialogPalette.For(theme);
        Assert.Equal(p.ActiveBorder, theme.ModalBorderForegroundColor);
        Assert.Equal(p.Title, theme.ModalTitleForegroundColor);
        Assert.Equal(p.Surface, theme.ModalBackgroundColor);
    }

    [Fact]
    public void TheActiveTab_IsAccentText_WithNoFill()
    {
        var theme = Installed();
        var p = DialogPalette.For(theme);
        Assert.Equal(p.Title, theme.TabHeaderActiveForegroundColor);
        Assert.Equal(p.Surface, theme.TabHeaderActiveBackgroundColor);
        Assert.Equal(p.Muted, theme.TabHeaderForegroundColor);
        Assert.Equal(p.Surface, theme.TabHeaderBackgroundColor);
    }

    [Fact]
    public void Scrollbars_AreMutedOnARuleTrack()
    {
        var theme = Installed();
        var p = DialogPalette.For(theme);
        Assert.Equal(p.Muted, theme.ScrollbarThumbUnfocusedColor);
        Assert.Equal(p.Rule, theme.ScrollbarTrackColor);
    }

    [Fact]
    public void AnUnfocusedListsSelection_IsTheDialogSelectionStep()
    {
        var theme = Installed();
        Assert.Equal(DialogPalette.For(theme).Selection, theme.ListUnfocusedHighlightBackgroundColor);
    }
}
