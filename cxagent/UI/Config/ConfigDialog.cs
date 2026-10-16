using CxAgent.Core.Sessions;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Parsing;

namespace CxAgent.UI.Config;

/// <summary>Which half of the configuration dialog is in front.</summary>
public enum ConfigTab { Providers, Mcp }

/// <summary>What the dialog needs from the app: where to draw, how a save becomes live, and who is in front.</summary>
/// <param name="System">The window system.</param>
/// <param name="Main">The main window — its window is the parent, and its panel shows MCP state.</param>
/// <param name="Apply">How a save reaches the file, the config, the sessions and the MCP servers.</param>
/// <param name="ActiveSession">The session in front, for what the dialog reports about tabs.</param>
/// <param name="McpCommand">The <c>/mcp</c> command, whose login the MCP tab runs.</param>
public sealed record ConfigDialogHost(
    ConsoleWindowSystem System, MainWindow Main, ConfigApplyContext Apply,
    Func<Session> ActiveSession, McpCommand McpCommand)
{
    /// <summary>
    /// First run only: wires the startup session once the first instance is saved. That session has
    /// no instance yet, so no rebind reaches it.
    /// </summary>
    public Action<Core.Llm.ResolvedConfig>? WireFirst { get; init; }
}

/// <summary>
/// The configuration dialog: providers and MCP servers, edited in the app and applied live.
///
/// <para>THE PLUGIN MANAGER'S SHAPE, ON PURPOSE — a rail of named things beside the selected one's
/// detail, a toolbar over them and a status bar under them — so the app has one dialog idiom rather
/// than two to learn. Driven by hand, not through <c>Flow</c>: this is one screen with its own
/// actions, and a wizard's frame around it would mean nothing.</para>
/// </summary>
public static class ConfigDialog
{
    /// <summary>The open dialog, or null — static because the key handlers registered at startup must
    /// know whether one is up, and a second would clobber the first's state.</summary>
    private static Window? _window;

    // WHERE THE USER LEFT IT, and on which tab — remembered across opens, because the dialog is opened
    // repeatedly in one session and a window that will not stay put is one the user moves every time.
    private static (int Left, int Top)? _placement;
    private static (int Width, int Height)? _size;
    private static ConfigTab _lastTab = ConfigTab.Providers;

    private static TabControl? _tabs;
    private static Action? _onClosed;

    /// <summary>Whether the dialog is up.</summary>
    public static bool IsOpen => _window is not null;

    /// <summary>
    /// Opens the dialog, on <paramref name="tab"/> or the tab last used; brings it forward when it is
    /// already open.
    /// </summary>
    /// <param name="host">What the dialog needs from the app.</param>
    /// <param name="tab">The tab to show, or null for the one last used.</param>
    /// <param name="firstRun">No provider yet: open on the preset gallery, and wire the session on the first save.</param>
    /// <param name="onClosed">Called once the window closes.</param>
    public static void Show(ConfigDialogHost host, ConfigTab? tab = null, bool firstRun = false, Action? onClosed = null)
    {
        var ws = host.System;
        if (_window is not null)
        {
            if (tab is { } wanted) SwitchTo(wanted);
            ws.SetActiveWindow(_window);
            return;
        }

        _onClosed = onClosed;

        // 90% OF THE TERMINAL, CAPPED — the plugin manager's sizing, which is what this shape is proven at.
        var width = Math.Min((int)(Console.WindowWidth * 0.9), 150);
        var height = Math.Min((int)(Console.WindowHeight * 0.9), 40);

        var window = new WindowBuilder(ws)
            .WithTitle("Configuration")
            .WithSize(_size?.Width ?? width, _size?.Height ?? height)
            .Centered()
            .AsModal()
            .WithBorderStyle(BorderStyle.Single)
            .Resizable(true)
            .Movable(true)
            .Minimizable(false)
            .Build();

        _tabs = new TabControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Fill,
        };
        _tabs.AddTab("Providers", ProvidersContent(host, firstRun));
        _tabs.AddTab("MCP servers", McpContent(host));
        window.AddControl(_tabs);

        window.AddControl(new RuleControl
        {
            StickyPosition = StickyPosition.Bottom,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        });

        // THE STATUS BAR: what this edits, and how to leave. Esc is named and Close is clickable — the
        // key is the fast path, the button the discoverable one.
        var status = new MarkupControl([
            // THE PATH LAST: it is the long part, and the end of the line is what a narrow window cuts.
            $"[{ColorScheme.MutedMarkup}]Saved changes apply at once · Esc closes · {MarkupParser.Escape(Path.Combine(host.Apply.Paths.ConfigDir, "config.json"))}[/]",
        ]) { Wrap = false };
        var statusBar = new HorizontalGridControl { HorizontalAlignment = HorizontalAlignment.Stretch };
        var statusColumn = new ColumnContainer(statusBar) { Width = width - 14 };
        statusColumn.AddContent(status);
        statusBar.AddColumn(statusColumn);
        var closeColumn = new ColumnContainer(statusBar);
        closeColumn.AddContent(Controls.Button("  Close  ").OnClick((_, _) => CloseIfOpen()).Build());
        statusBar.AddColumn(closeColumn);
        statusBar.StickyPosition = StickyPosition.Bottom;
        window.AddControl(statusBar);

        if (_placement is { } seen) { window.Left = seen.Left; window.Top = seen.Top; }

        window.OnClosed += (_, _) =>
        {
            // REMEMBERED ON CLOSE, not as it moves: only the final resting place matters.
            _placement = (window.Left, window.Top);
            _size = (window.Width, window.Height);
            if (_tabs is { } tabs) _lastTab = tabs.ActiveTabIndex == 1 ? ConfigTab.Mcp : ConfigTab.Providers;
            _window = null;
            _tabs = null;
            var closed = _onClosed;
            _onClosed = null;
            closed?.Invoke();
        };

        _window = window;
        ws.AddWindow(window);
        SwitchTo(firstRun ? ConfigTab.Providers : tab ?? _lastTab);
        ws.SetActiveWindow(window);
    }

    /// <summary>Closes the dialog if it is open. Returns whether it was.</summary>
    public static bool CloseIfOpen()
    {
        if (_window is not { } window) return false;
        window.Close();
        return true;
    }

    private static void SwitchTo(ConfigTab tab)
    {
        if (_tabs is null) return;
        _tabs.ActiveTabIndex = tab == ConfigTab.Mcp ? 1 : 0;
    }

    private static IWindowControl ProvidersContent(ConfigDialogHost host, bool firstRun) =>
        new MarkupControl(["Providers"]);

    private static IWindowControl McpContent(ConfigDialogHost host) =>
        new MarkupControl(["MCP servers"]);
}
