using CxAgent.Core.Colleagues;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Parsing;

namespace CxAgent.UI;

/// <summary>A question to forget a colleague, put in <paramref name="Session"/>'s conversation.</summary>
/// <param name="Session">Whose conversation asks, and whose desk forgets.</param>
/// <param name="Name">The colleague, by any name it has had.</param>
/// <param name="Then">Run after a yes — a library tab reloading.</param>
/// <summary>A question to distil a colleague's rounds again, put in <paramref name="Session"/>'s conversation.</summary>
/// <param name="Session">Whose conversation asks, and whose desk distils.</param>
/// <param name="Typed">What follows the verb: the name, and perhaps a round and a model.</param>
/// <param name="Then">Run after the distilling — a library tab reloading.</param>
public sealed record ColleagueDistilling(CxAgent.Core.Sessions.Session Session, string Typed, Action? Then = null);

public sealed record ColleagueForgetting(CxAgent.Core.Sessions.Session Session, string Name, Action? Then = null);

/// <summary>
/// The terminal's library: a rail of colleagues and, for the one picked, what it is, what it knows and its experience
/// — read-only — with Open (one-to-one), Bring in, Distil again and Forget. The browser's page draws the same <see cref="LibraryView"/>.
/// </summary>
public sealed class ColleagueLibraryTab
{
    /// <summary>What the tab's buttons do, by colleague name — carried out by the window that owns the session.</summary>
    /// <param name="Bring">Brings it into the session, idle.</param>
    /// <param name="Open">Opens its one-to-one.</param>
    /// <param name="Forget">Asks, in the session's conversation, whether to forget it.</param>
    /// <param name="Distill">Asks, in the session's conversation, whether to distil its last round again.</param>
    public sealed record Actions(Action<string> Bring, Action<string> Open, Action<string> Forget, Action<string> Distill);

    private const int RailCells = 26;

    private readonly ColleagueStore _library;
    private readonly Actions _actions;
    private readonly GridControl _root;
    private readonly ListControl _rail;
    private readonly ScrollablePanelControl _detail;
    private IReadOnlyList<LibraryEntry> _entries = [];
    private LibraryEntry? _shown;

    public ColleagueLibraryTab(ColleagueStore library, Actions actions)
    {
        _library = library;
        _actions = actions;
        _root = new GridControl { VerticalAlignment = VerticalAlignment.Fill, HorizontalAlignment = HorizontalAlignment.Stretch };
        _root.ColumnDefinitions.Add(GridLength.Cells(RailCells, min: 18));
        _root.ColumnDefinitions.Add(GridLength.Star(1));
        _root.RowDefinitions.Add(GridLength.Star(1));

        _rail = new ListControl { VerticalAlignment = VerticalAlignment.Fill, HorizontalAlignment = HorizontalAlignment.Stretch };
        _rail.SelectedIndexChanged += (_, i) => Show(i);
        ListDetail.StyleRail(_rail);
        _root.Place(_rail, 0, 0);
        ListDetail.Divider(_root);

        _detail = new ScrollablePanelControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Fill, Margin = ListDetail.DetailInset,
        };
        _root.Place(_detail, 0, 1);
        Reload();
    }

    /// <summary>The tab's content.</summary>
    public IWindowControl Control => _root;

    /// <summary>Reads the library again — a colleague kept or distilled since the tab opened.</summary>
    public void Reload()
    {
        _entries = LibraryView.Build(_library);
        _rail.ClearItems();
        foreach (var e in _entries) _rail.AddItem(new ListItem(MarkupParser.Escape(e.Name)) { Tag = e.Name });
        if (_entries.Count == 0)
        {
            _detail.ClearContents();
            _detail.AddControl(ListDetail.Empty("No colleagues yet.",
                "Keep a worker of a session as one: /colleagues keep <worker> [name], or ask the agent to."));
            return;
        }
        _rail.SelectedIndex = 0;
        Show(0);
    }

    private void Show(int index)
    {
        if (index < 0 || index >= _entries.Count) return;
        var e = _shown = _entries[index];
        _detail.ClearContents();

        var open = Controls.Button(" Open ").OnClick((_, _) => _actions.Open(e.Name)).Build();
        var bring = Controls.Button(" Bring in ").OnClick((_, _) => _actions.Bring(e.Name)).Build();
        var distill = Controls.Button(" Distil again… ").OnClick((_, _) => _actions.Distill(e.Name)).Build();
        var forget = Controls.Button(" Forget… ").OnClick((_, _) => _actions.Forget(e.Name)).Build();
        var buttons = new ToolbarControl { ItemSpacing = 2, HorizontalAlignment = HorizontalAlignment.Left };
        buttons.AddItem(open);
        buttons.AddItem(bring);
        buttons.AddItem(distill);
        buttons.AddItem(forget);

        _detail.AddControl(new MarkupControl(Facts(e)) { Wrap = true });
        _detail.AddControl(buttons);
        _detail.AddControl(ListDetail.Gap());
        // READ-ONLY, AS DISTILLED: it is corrected by talking to it (Open), not by editing it behind its back.
        foreach (var part in e.Experience)
            _detail.AddControl(ListDetail.Section(part.Title,
                new MarkupControl([.. part.Markdown.Split('\n').Select(MarkupParser.Escape)]) { Wrap = true }));
        if (e.Experience.Count == 0)
            _detail.AddControl(new MarkupControl([$"[{ColorScheme.MutedMarkup}]Nothing distilled yet.[/]"]) { Wrap = true });
    }

    private static List<string> Facts(LibraryEntry e)
    {
        var muted = ColorScheme.MutedMarkup;
        var lines = new List<string> { $"[bold]◆ {MarkupParser.Escape(e.Name)}[/]" };
        if (e.Description.Length > 0) lines.Add(MarkupParser.Escape(e.Description));
        lines.Add($"[{muted}]{MarkupParser.Escape(LibraryView.FactsLine(e.Facts))}[/]");
        if (e.Knows.Count > 0) lines.Add($"[{muted}]knows: {MarkupParser.Escape(string.Join(", ", e.Knows))}[/]");
        return lines;
    }

    /// <summary>Test seam: the names on the rail, in order.</summary>
    internal IReadOnlyList<string> RailNamesForTest => [.. _rail.Items.Select(i => (string)i.Tag!)];

    /// <summary>Test seam: everything the detail shows, as markup text.</summary>
    internal string DetailTextForTest => string.Join("\n", _detail.Children.SelectMany(TextOf));

    /// <summary>Test seam: the Bring in button of the one shown.</summary>
    internal void BringForTest() { if (_shown is { } e) _actions.Bring(e.Name); }

    /// <summary>Test seam: the Distil again button of the one shown.</summary>
    internal void DistillForTest() { if (_shown is { } e) _actions.Distill(e.Name); }

    /// <summary>Test seam: the Forget button of the one shown.</summary>
    internal void ForgetForTest() { if (_shown is { } e) _actions.Forget(e.Name); }

    /// <summary>Test seam: the Open button of the one shown.</summary>
    internal void OpenForTest() { if (_shown is { } e) _actions.Open(e.Name); }

    private static IEnumerable<string> TextOf(IWindowControl control) => control switch
    {
        MarkupControl m => [m.Text],
        PanelControl p => p.Children.SelectMany(TextOf),
        _ => [],
    };
}
