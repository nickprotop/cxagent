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
    /// The detail's head: the item's name in the title colour, the actions on THIS item at the right of
    /// the same line — beside what they act on, not in a toolbar over the list — the subtitle muted under
    /// it, then a rule.
    /// </summary>
    public static IWindowControl Header(string name, string subtitle, params ButtonControl[] actions)
    {
        var p = DialogLook.Palette;
        var grid = new GridControl { HorizontalAlignment = HorizontalAlignment.Stretch };
        grid.ColumnDefinitions.Add(GridLength.Star(1));
        grid.ColumnDefinitions.Add(GridLength.Auto());
        grid.RowDefinitions.Add(GridLength.Cells(1));
        grid.RowDefinitions.Add(GridLength.Cells(1));
        grid.RowDefinitions.Add(GridLength.Cells(1));
        grid.RowDefinitions.Add(GridLength.Cells(1));
        grid.Place(new MarkupControl([$"[bold {p.Title.ToMarkup()}]{MarkupParser.Escape(name)}[/]"]) { Wrap = false }, 0, 0);
        if (actions.Length > 0)
        {
            var bar = new ToolbarControl { ItemSpacing = 1, HorizontalAlignment = HorizontalAlignment.Right };
            foreach (var a in actions) bar.AddItem(a);
            grid.Place(bar, 0, 1);
        }
        grid.Place(new MarkupControl([$"[{p.Muted.ToMarkup()}]{MarkupParser.Escape(subtitle)}[/]"]) { Wrap = false }, 1, 0, colSpan: 2);
        grid.Place(DialogLook.Rule(), 2, 0, colSpan: 2);
        return grid;
    }

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
