using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Parsing;
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

    /// <summary>
    /// The width for <paramref name="size"/>: S 60 and M 86 read as one family across dialogs, L is most
    /// of the screen for the two dialogs that list things. Never wider than the terminal less a margin,
    /// so a narrow terminal still shows both borders.
    /// </summary>
    public static int Width(DialogSize size, int terminalWidth)
    {
        var wanted = size switch
        {
            DialogSize.S => 60,
            DialogSize.M => 86,
            _ => Math.Min((int)(terminalWidth * 0.9), 150),
        };
        return Math.Max(20, Math.Min(wanted, terminalWidth - 4));
    }

    /// <summary>An L dialog's height: 90% of the terminal, at most 40 rows.</summary>
    public static int LargeHeight(int terminalHeight) => Math.Min((int)(terminalHeight * 0.9), 40);

    /// <summary>
    /// The chrome every dialog shares. Padding is 2 at the sides and 1 at the top but NONE at the bottom:
    /// the footer's rule already separates the buttons, and a padded row under them reads as dead space.
    /// No maximise — a dialog has its own sizes; [X] stays so a mouse user has a way out.
    /// </summary>
    public static WindowBuilder Styled(this WindowBuilder b, DialogTone tone = DialogTone.Normal)
    {
        var p = Palette;
        return b.WithBackgroundColor(p.Surface)
            .WithBorderStyle(BorderStyle.Single)
            .WithActiveBorderColor(tone == DialogTone.Caution ? p.CautionBorder : p.ActiveBorder)
            .WithInactiveBorderColor(p.InactiveBorder)
            .WithPadding(new Padding(2, 1, 2, 0))
            .Maximizable(false);
    }

    /// <summary>The title colours, which the builder has no setter for, so they are set once built.</summary>
    public static void Titled(Window w)
    {
        w.ActiveTitleForegroundColor = Palette.Title;
        w.InactiveTitleForegroundColor = Palette.Muted;
    }

    /// <summary>The footer, built but not added: a rule, then hints left and buttons right, pinned.</summary>
    public static DialogFooter Footer(string hints, params ButtonControl[] buttons)
    {
        var rule = new RuleControl { Color = Palette.Rule, StickyPosition = StickyPosition.Bottom, HorizontalAlignment = HorizontalAlignment.Stretch };
        var row = new GridControl { HorizontalAlignment = HorizontalAlignment.Stretch, StickyPosition = StickyPosition.Bottom };
        row.ColumnDefinitions.Add(GridLength.Star(1));
        row.ColumnDefinitions.Add(GridLength.Auto());
        row.RowDefinitions.Add(GridLength.Cells(1));
        // HINTS GIVE WAY, BUTTONS DO NOT: the hint column is the star track, so a narrow window cuts a
        // hint before it hides a button someone needs to press.
        var text = new MarkupControl([$"[{Palette.Muted.ToMarkup()}]{MarkupParser.Escape(hints)}[/]"]) { Wrap = false };
        var bar = new ToolbarControl { ItemSpacing = 1, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var button in buttons) bar.AddItem(button);
        row.Place(text, 0, 0);
        row.Place(bar, 0, 1);
        return new DialogFooter(rule, row, text, bar);
    }

    /// <summary>Adds <see cref="Footer"/> to a window.</summary>
    public static void AddFooter(Window w, string hints, params ButtonControl[] buttons)
    {
        var footer = Footer(hints, buttons);
        w.AddControl(footer.Rule);
        w.AddControl(footer.Row);
    }

    /// <summary>A rule in the one rule colour; labelled, it heads a section of the dialog.</summary>
    public static RuleControl Rule(string? label = null)
    {
        var rule = new RuleControl { Color = Palette.Rule, HorizontalAlignment = HorizontalAlignment.Stretch };
        if (label is not null) rule.Title = $"[{Palette.Muted.ToMarkup()}]{MarkupParser.Escape(label)}[/]";
        return rule;
    }

    /// <summary>
    /// The main button takes the accent; any other the default role. Toggled, not just set, so a Save
    /// that has nothing to save can step back down.
    /// </summary>
    public static ButtonControl Main(ButtonControl b, bool main = true)
    {
        // ONLY ON A CHANGE: the Configuration dialog asks after every key, and a setter that invalidates
        // would repaint the button each time for nothing.
        var role = main ? ColorRole.Primary : ColorRole.Default;
        if (b.ColorRole != role) b.ColorRole = role;
        return b;
    }
}

/// <summary>The three dialog widths; heights stay the dialog's own, from its content.</summary>
public enum DialogSize { S, M, L }

/// <summary>Caution colours the border of a dialog whose main answer throws something away.</summary>
public enum DialogTone { Normal, Caution }

/// <summary>A dialog's bottom band: a rule, then hints at the left and buttons at the right.</summary>
public sealed record DialogFooter(RuleControl Rule, GridControl Row, MarkupControl Hints, ToolbarControl Buttons);
