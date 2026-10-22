using SharpConsoleUI.Themes;

namespace CxAgent.UI;

/// <summary>The one dialog look: its colours here, the window and footer helpers beside them.</summary>
public static class DialogLook
{
    /// <summary>
    /// The active theme's dialog colours. Set by <see cref="ColorScheme.DeriveFrom"/>, so a theme switch
    /// moves every dialog opened after it; starts from ModernGray so a host that never derives (a
    /// headless test) still has colours.
    /// </summary>
    public static DialogPalette Palette { get; internal set; } = DialogPalette.For(new ModernGrayTheme());
}
