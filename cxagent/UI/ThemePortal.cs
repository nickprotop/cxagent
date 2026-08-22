using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Core;
using SharpConsoleUI.Drawing;
using SharpConsoleUI.Events;
using SharpConsoleUI.Helpers;
using SharpConsoleUI.Layout;
using SColor = SharpConsoleUI.Color;
using SRectangle = System.Drawing.Rectangle;

namespace CxAgent.UI;

/// <summary>
/// The theme list, floating above the status-bar item that opens it.
///
/// <para>THREE THINGS MAKE A PORTAL USABLE AND THEY ARE EASY TO MISS SEPARATELY. Built first from
/// raw markup in a desktop portal, this list drew correctly and then ignored arrow keys, never took
/// the cursor off the composer, and did not react to the mouse. Each is a different omission:
/// <c>PortalFocusedControl</c> is what routes keys into the portal (portals bypass the window's
/// focus manager), <c>ProcessMouseEvent</c> has to be forwarded by hand or the hosted control never
/// sees a click, and only a real list control tracks a selection at all.</para>
///
/// <para>A LIST CONTROL RATHER THAN RENDERED TEXT. <c>Controls.List</c> already handles selection,
/// keys, hover and activation; the markup version reimplemented the first two badly and could not do
/// the others. This follows cratis's status-bar chooser, which solved the same problem first.</para>
/// </summary>
public sealed class ThemePortal
{
    private readonly ConsoleWindowSystem _system;
    private DesktopPortal? _open;
    private ThemePortalContent? _content;

    /// <summary>Creates the picker for a window system's registered themes.</summary>
    /// <param name="system">The window system owning the theme registry and desktop portals.</param>
    public ThemePortal(ConsoleWindowSystem system) => _system = system;

    /// <summary>Raised with the chosen theme's name.</summary>
    public event EventHandler<string>? ThemeChosen;

    /// <summary>True while the list is on screen.</summary>
    public bool IsOpen => _open is not null;

    /// <summary>Opens the list, or closes it when already open — F9 toggles.</summary>
    public void Toggle()
    {
        if (IsOpen) { Close(); return; }

        var themes = _system.ThemeRegistryService.GetAvailableThemes();
        if (themes.Count == 0) return;

        var active = _system.ThemeStateService.CurrentTheme.Name;

        var builder = Controls.List("Theme")
            .Selectable()
            .WithAlignment(HorizontalAlignment.Stretch)
            .WithVerticalAlignment(VerticalAlignment.Fill);

        foreach (var info in themes)
        {
            // A SWATCH PER ROW, from the theme's own primary — the list says what each theme LOOKS
            // like, not only what it is called, which is the question a user opening it actually has.
            //
            // THROUGH THE ICON, NOT MARKUP IN THE LABEL. ListControl parses markup for its TITLE BAR
            // and paints item text in one flat colour, so a coloured block written into the label
            // renders in the row's foreground and every theme's swatch comes out identical. The icon
            // overload is the only route that carries a per-row colour.
            var candidate = _system.ThemeRegistryService.GetTheme(info.Name);
            var swatch = candidate?.PrimaryColor ?? ColorScheme.AccentRgb;
            var mark = info.Name == active ? "●" : " ";
            builder.AddItem($"{mark} {info.Name}", icon: "██", iconColor: swatch, tag: info.Name);
        }

        var list = builder.Build();

        var index = themes.ToList().FindIndex(t => t.Name == active);
        if (index >= 0) list.SelectedIndex = index;

        var widest = themes.Max(t => t.Name.Length);
        var width = Math.Clamp(widest + 12, 22, 44);
        var height = Math.Min(themes.Count + 2, Math.Max(5, _system.DesktopDimensions.Height / 2));
        var bounds = new SRectangle(0, Math.Max(0, _system.DesktopDimensions.Height - 1 - height), width, height);

        list.ItemActivated += (_, item) =>
        {
            if (item.Tag is not string name) return;

            // ON THE UI THREAD, DELIBERATELY. ItemActivated fires on the driver's mouse thread for a
            // click; removing a portal is structural work on the layout tree, and doing it from there
            // races the renderer.
            _system.EnqueueOnUIThread(() =>
            {
                Close();
                ThemeChosen?.Invoke(this, name);
            });
        };

        _content = new ThemePortalContent(list, bounds);
        _open = _system.DesktopPortalService.CreatePortal(new DesktopPortalOptions(
            Content: _content,
            Bounds: bounds,
            DismissOnClickOutside: true,
            OnDismiss: () => { _open = null; _content = null; }));
    }

    /// <summary>
    /// Routes a key into the open list. The host calls this from its PreviewKeyPressed and marks the
    /// event handled, because a desktop portal does not intercept keys before that handler runs.
    /// </summary>
    /// <param name="key">The key the window received.</param>
    public void ProcessKey(ConsoleKeyInfo key)
    {
        if (_content is null) return;
        if (key.Key == ConsoleKey.Escape) { Close(); return; }
        _content.ProcessHostedKey(key);
    }

    /// <summary>Closes the list if it is open. Safe when it is not.</summary>
    public void Close()
    {
        if (_open is null) return;
        _system.DesktopPortalService.RemovePortal(_open);
        _open = null;
        _content = null;
    }
}

/// <summary>
/// The bordered surface the list sits in, and the piece that makes it interactive.
/// </summary>
public sealed class ThemePortalContent : PortalContentBase
{
    private readonly SRectangle _bounds;

    /// <summary>Wraps a control as portal content.</summary>
    /// <param name="content">The list to host.</param>
    /// <param name="bounds">Where the portal sits, in desktop coordinates.</param>
    public ThemePortalContent(IWindowControl content, SRectangle bounds)
    {
        _bounds = bounds;

        BorderStyle = BoxChars.Rounded;
        BorderColor = ColorScheme.AccentRgb;
        BorderBackgroundColor = ColorScheme.PanelSurface;
        BackgroundColor = ColorScheme.PanelSurface;
        ForegroundColor = ColorScheme.Heading;
        Content = content;

        // PORTALS BYPASS THE WINDOW'S FOCUS MANAGER and route focus through this instead. Without it
        // the list draws but the composer keeps the keyboard — arrows scroll the transcript behind,
        // and the cursor never leaves the input, which is exactly how a broken portal LOOKS.
        if (content is IFocusableControl focusable) PortalFocusedControl = focusable;
    }

    /// <summary>Hands a key to the hosted list.</summary>
    /// <param name="key">The key to route.</param>
    public void ProcessHostedKey(ConsoleKeyInfo key)
    {
        if (Content is IInteractiveControl interactive) interactive.ProcessKey(key);
    }

    /// <inheritdoc/>
    public override SRectangle GetPortalBounds() => _bounds;

    /// <summary>
    /// Forwards the mouse to the hosted list, so hover highlights and a click selects.
    /// </summary>
    /// <param name="args">The mouse event.</param>
    /// <returns>True when the hosted control consumed it.</returns>
    public override bool ProcessMouseEvent(MouseEventArgs args) => ProcessHostedMouseEvent(args);

    /// <summary>Never called: the base paints the hosted control into the bordered rect.</summary>
    /// <param name="buffer">The target buffer.</param>
    /// <param name="bounds">The content bounds.</param>
    /// <param name="clipRect">The clip rectangle.</param>
    /// <param name="defaultFg">Default foreground.</param>
    /// <param name="defaultBg">Default background.</param>
    protected override void PaintPortalContent(
        CharacterBuffer buffer, LayoutRect bounds, LayoutRect clipRect, SColor defaultFg, SColor defaultBg)
    {
    }
}
