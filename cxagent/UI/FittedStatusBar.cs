using SharpConsoleUI;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Drivers;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Parsing;

namespace CxAgent.UI;

/// <summary>
/// A status bar that makes its right-hand readout fit, instead of running it over the left hints.
///
/// <para>THE LIBRARY'S OVERFLOW RULE IS THE BUG. When the left items and the right group do not both
/// fit, <see cref="StatusBarControl"/> starts the right group exactly where the left one ended — no
/// gap, so "F7:Jobs" and "ctx 1%" read as one word — and then clips whatever runs past the edge,
/// which is how "↑58k" lost its "k". The bar is the chat column, not the terminal: the session panel
/// takes a slice of it, so even 140 columns leaves little more than a hundred.</para>
///
/// <para>FITTED AT PAINT TIME, because that is the one moment the real width is known — it is the
/// chat column's cell, which depends on the panel, the margin and the terminal, and which changes
/// whenever any of them does. Working it out anywhere earlier means restating that layout and being
/// wrong the first time it moves.</para>
///
/// <para>THE LEFT HINTS ARE THE FIXED PART. They are the keys a user needs to find; the readout is
/// the part with a shorter form, so it gives way and the hints do not.</para>
/// </summary>
public sealed class FittedStatusBar : StatusBarControl
{
    /// <summary>The least space left between the hints and the readout.</summary>
    public const int MinGap = 2;

    public FittedStatusBar(bool stickyBottom) : base(stickyBottom) { }

    /// <summary>
    /// Told how many columns the readout may use, once per paint. The owner answers by choosing a
    /// shorter or longer form of it, or hiding it when nothing fits.
    /// </summary>
    public Action<int>? FitReadout { get; set; }

    public override void PaintDOM(CharacterBuffer buffer, LayoutRect bounds, LayoutRect clipRect,
        Color defaultFg, Color defaultBg)
    {
        var contentWidth = bounds.Width - Margin.Left - Margin.Right;
        FitReadout?.Invoke(RoomForReadout(contentWidth));
        base.PaintDOM(buffer, bounds, clipRect, defaultFg, defaultBg);
    }

    /// <summary>Columns left for the readout after the left hints, the gap, and any other right-hand
    /// item (the composer hint) — which may be negative on a bar too narrow for the hints alone.</summary>
    private int RoomForReadout(int contentWidth)
    {
        var left = GroupWidth(LeftItems, skip: null);
        var otherRight = GroupWidth(RightItems, skip: item => ReferenceEquals(item, Readout));
        var spacing = otherRight > 0 ? ItemSpacing : 0;
        return contentWidth - left - MinGap - otherRight - spacing;
    }

    /// <summary>The readout item, which the owner designates so it is not counted as "other".</summary>
    public StatusBarItem? Readout { get; set; }

    private int GroupWidth(IReadOnlyList<StatusBarItem> items, Func<StatusBarItem, bool>? skip)
    {
        var total = 0;
        var first = true;
        foreach (var item in items)
        {
            if (!item.IsVisible || (skip?.Invoke(item) ?? false)) continue;
            if (!first) total += ItemSpacing;
            first = false;
            total += WidthOf(item);
        }
        return total;
    }

    /// <summary>The same arithmetic the library lays an item out with: shortcut, the separator
    /// between it and the label, then the label — markup stripped.</summary>
    private int WidthOf(StatusBarItem item)
    {
        if (item.IsSeparator) return MarkupParser.StripLength(SeparatorChar);

        var width = 0;
        if (!string.IsNullOrEmpty(item.Shortcut))
        {
            width += MarkupParser.StripLength(item.Shortcut);
            if (!string.IsNullOrEmpty(item.Label)) width += MarkupParser.StripLength(ShortcutLabelSeparator);
        }
        if (!string.IsNullOrEmpty(item.Label)) width += MarkupParser.StripLength(item.Label);
        return width;
    }

    /// <summary>
    /// The index of the richest form that fits in <paramref name="room"/> columns, or -1 when none
    /// does. <paramref name="widths"/> runs richest first, so the first that fits is the answer.
    /// </summary>
    public static int PickForm(IReadOnlyList<int> widths, int room)
    {
        for (var i = 0; i < widths.Count; i++)
            if (widths[i] <= room) return i;
        return -1;
    }
}
