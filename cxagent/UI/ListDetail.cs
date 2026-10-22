using System.Runtime.CompilerServices;
using SharpConsoleUI;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Parsing;

namespace CxAgent.UI;

/// <summary>
/// The pieces Configuration and Plugins share: both are a list on the left and the selected item's detail
/// on the right, and one set of pieces keeps the two from drifting into two looks.
/// </summary>
public static class ListDetail
{
    /// <summary>The detail pane's inset from the divider, the same one column the rail has.</summary>
    public static readonly Margin DetailInset = new(1, 0, 0, 0);

    // THE TEXT A CALLER SET, per item. Mark writes a one-column prefix into each item's text, so without
    // this a second Mark would prefix the prefix; and a caller that rewrites an item (a rename, an unsaved
    // dot) must become the new base rather than be overwritten by the old one.
    private sealed class Marked { public string Base = ""; public string Written = ""; }
    private static readonly ConditionalWeakTable<ListItem, Marked> Marks = new();

    /// <summary>
    /// Names in the normal foreground, never the accent — a list of accent rows makes the one that is
    /// chosen the least distinct thing in it — and the selection a step up on the dialog surface, marked
    /// by <see cref="Mark"/>.
    /// </summary>
    public static void StyleRail(ListControl rail)
    {
        var p = DialogLook.Palette;
        rail.BackgroundColor = p.Surface;
        rail.ForegroundColor = p.Foreground;
        rail.FocusedForegroundColor = p.Foreground;
        rail.HighlightBackgroundColor = p.Selection;
        rail.HighlightForegroundColor = p.Foreground;
        rail.SelectedIndexChanged += (_, _) => Mark(rail);
    }

    /// <summary>
    /// The accent ▌ at the left edge of every line of the selected row, a space on every other. A row
    /// with no tag is a heading or a spacer and never takes the bar, though it keeps the column so the
    /// names line up.
    /// </summary>
    public static void Mark(ListControl rail)
    {
        var selected = rail.SelectedIndex;
        var bar = $"[{DialogLook.Palette.Title.ToMarkup()}]▌[/]";
        for (var i = 0; i < rail.Items.Count; i++)
        {
            var item = rail.Items[i];
            var m = Marks.GetOrCreateValue(item);
            if (m.Written != item.Text) m.Base = item.Text;
            var prefix = i == selected && item.Tag is not null ? bar : " ";
            var written = string.Join("\n", m.Base.Split('\n').Select(l => prefix + l));
            m.Written = written;
            if (item.Text != written) item.Text = written;
        }
    }

    /// <summary>
    /// A single thin line in the rule colour between list and detail. Drawn, not dragged: the library's
    /// draggable splitter always draws a double line, which outshouts everything else in the dialog, so
    /// the list keeps a fixed width.
    /// </summary>
    public static void Divider(GridControl grid, int afterColumn = 0)
    {
        grid.GridlineStyle = BorderStyle.Single;
        grid.GridlineColor = DialogLook.Palette.Rule;
        grid.AddGridlineAfterColumn(afterColumn);
    }

    /// <summary>
    /// The detail's head: the item's name in the title colour, the actions on THIS item beside it —
    /// beside what they act on, not in a toolbar over the list — the subtitle muted under it, then a
    /// rule. <paramref name="paneWidth"/> decides whether the actions fit beside the name; when they do
    /// not, they wrap on rows of their own under it, so none runs off the pane's edge.
    /// </summary>
    public static DetailHeader Header(string name, string subtitle, int paneWidth, params ButtonControl[] actions)
    {
        var p = DialogLook.Palette;
        var widths = actions.Select(Cells).ToArray();
        var under = ActionRows(name.Length, widths, paneWidth);

        var grid = new GridControl { HorizontalAlignment = HorizontalAlignment.Stretch };
        // THE ACTIONS TAKE WHAT THEY NEED, THE NAME THE REST. Sized from the labels, because the grid
        // measures a toolbar as the whole row in an Auto track and leaves the name a column of nothing.
        grid.ColumnDefinitions.Add(GridLength.Star(1));
        if (under == 0 && actions.Length > 0)
            grid.ColumnDefinitions.Add(GridLength.Cells(widths.Sum() + widths.Length - 1));
        var span = grid.ColumnDefinitions.Count;

        grid.RowDefinitions.Add(GridLength.Cells(1));
        grid.Place(new MarkupControl([$"[bold {p.Title.ToMarkup()}]{MarkupParser.Escape(name)}[/]"]) { Wrap = false }, 0, 0);
        var row = 1;
        if (actions.Length > 0)
        {
            var bar = new ToolbarControl { ItemSpacing = 1, Wrap = under > 0, HorizontalAlignment = under > 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right };
            foreach (var a in actions) bar.AddItem(a);
            if (under == 0) grid.Place(bar, 0, 1);
            else
            {
                grid.RowDefinitions.Add(GridLength.Cells(under));
                grid.Place(bar, row++, 0);
            }
        }
        grid.RowDefinitions.Add(GridLength.Cells(1));
        grid.Place(new MarkupControl([$"[{p.Muted.ToMarkup()}]{MarkupParser.Escape(subtitle)}[/]"]) { Wrap = false }, row++, 0, colSpan: span);
        grid.RowDefinitions.Add(GridLength.Cells(1));
        grid.Place(DialogLook.Rule(), row++, 0, colSpan: span);
        // A BLANK ROW UNDER THE RULE, so the first field does not sit against it.
        grid.RowDefinitions.Add(GridLength.Cells(1));
        // NAME, SUBTITLE, RULE AND BLANK, plus every row the wrapped actions take.
        return new DetailHeader(grid, 4 + under);
    }

    /// <summary>
    /// How many rows the actions need UNDER the name: none when they fit beside it, otherwise as many as
    /// they wrap onto at <paramref name="paneWidth"/>, one cell apart.
    /// </summary>
    public static int ActionRows(int nameLength, IReadOnlyList<int> actionWidths, int paneWidth)
    {
        if (actionWidths.Count == 0) return 0;
        var total = actionWidths.Sum() + actionWidths.Count - 1;
        if (nameLength + 2 + total <= paneWidth) return 0;
        var rows = 1;
        var used = 0;
        foreach (var w in actionWidths)
        {
            var need = used == 0 ? w : used + 1 + w;
            if (need > paneWidth && used > 0) { rows++; used = w; }
            else used = need;
        }
        return rows;
    }

    /// <summary>
    /// The detail pane's width in a list/detail dialog <paramref name="windowWidth"/> wide: less the
    /// border, the window's side padding, the list, the divider, the pane's inset and its scrollbar.
    /// </summary>
    public static int PaneWidth(int windowWidth, int railCells) => windowWidth - 2 - 4 - railCells - 1 - 1 - 1;

    // A button is its label and a cell either side.
    private static int Cells(ButtonControl b) => b.Text.Length + 2;

    /// <summary>
    /// An empty pane: what is missing in the normal foreground, what to do about it muted under it,
    /// centred — one muted line in a corner reads as a pane that failed to load.
    /// </summary>
    public static IWindowControl Empty(string line, string hint)
    {
        var width = Math.Max(line.Length, hint.Length);
        string Centre(string s) =>
            new string(' ', (width - s.Length) / 2) + MarkupParser.Escape(s) + new string(' ', width - s.Length - (width - s.Length) / 2);
        return new MarkupControl([.. Enumerable.Repeat("", 8), Centre(line), $"[{DialogLook.Palette.Muted.ToMarkup()}]{Centre(hint)}[/]"])
        {
            HorizontalAlignment = HorizontalAlignment.Center,
        };
    }

    /// <summary>
    /// A text input on its own ground with one column of inner padding, so an empty field is visibly a
    /// field and not a blank line. The prompt draws the padding, since the control has none of its own.
    /// </summary>
    public static PromptControl Input(PromptControl p)
    {
        var palette = DialogLook.Palette;
        var label = p.Prompt ?? "";
        p.InputBackgroundColor = palette.Input;
        p.InputFocusedBackgroundColor = palette.Selection;
        void Paint(Color bg) => p.Prompt = $"{label}[on {bg.ToMarkup()}] [/]";
        Paint(palette.Input);
        p.GotFocus += (_, _) => Paint(palette.Selection);
        p.LostFocus += (_, _) => Paint(palette.Input);
        return p;
    }

    /// <summary>
    /// The path as a person reads it: home as ~, and when it does not fit, its END kept — the file name
    /// is what identifies it, and a path cut mid-way reads as a different path.
    /// </summary>
    public static string ElidePath(string path, string home, int width)
    {
        if (home.Length > 0 && (path == home || path.StartsWith(home + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
            path = "~" + path[home.Length..];
        if (width <= 1) return "…"[..Math.Max(0, width)];
        return path.Length <= width ? path : "…" + path[^(width - 1)..];
    }
}

/// <summary>A detail header, and how many rows it takes, for a host that gives it a fixed track.</summary>
public sealed record DetailHeader(IWindowControl Control, int Height);
