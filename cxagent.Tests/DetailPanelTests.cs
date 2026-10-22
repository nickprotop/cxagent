using CxAgent.UI;
using CxAgent.UI.Config;
using SharpConsoleUI.Controls;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The detail pages' groups: each a rounded panel whose title is read as text, the folded one keeping the
/// dialog's surface when it has focus, and no empty row left at a panel's foot.
/// </summary>
[Collection("palette")]
public class DetailPanelTests
{
    private static MarkupControl Line(string text) => new([text]);

    [Fact]
    public void FieldsAreSpacedBetween_NotAfterTheLast()
    {
        var grid = new FieldGrid();
        grid.Add("A", "what a is", Line("a"));
        grid.Add("B", "what b is", Line("b"));
        // label, help, editor — a blank — label, help, editor; nothing trailing.
        Assert.Equal(7, grid.Control.RowDefinitions.Count);
    }

    [Fact]
    public void ASectionsTitle_IsDrawnInTheNormalText()
    {
        var section = ListDetail.Section("Connection", Line("x"));
        Assert.Contains(DialogLook.Palette.Foreground.ToMarkup(), section.Header);
        Assert.Contains("Connection", section.Header);
    }

    /// <summary>A FOCUSED FOLD KEEPS THE SURFACE: the library's own focus fill is the theme's accent, a
    /// solid block of it under every field the fold holds.</summary>
    [Fact]
    public void AFold_KeepsTheDialogsSurface_WhenFocused()
    {
        var fold = ListDetail.Folded("Advanced", Line("x"));
        Assert.Equal(DialogLook.Palette.Surface, fold.FocusedBackgroundColor);
        Assert.Equal(CollapsibleHeaderStyle.Rounded, fold.HeaderStyle);
        Assert.False(fold.IsExpanded);
    }

    /// <summary>AN EMPTY LINE UNDER A PANEL'S TITLE: its first field does not sit against the border.</summary>
    [Fact]
    public void EveryPanel_StartsWithAnEmptyLine()
    {
        Assert.Equal(1, ListDetail.Section("Connection", Line("x")).Padding.Top);
        Assert.Equal(1, ListDetail.Folded("Advanced", Line("x")).Padding.Top);
    }
}
