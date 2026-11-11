using CxAgent.UI;
using SharpConsoleUI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The side panel's share bars (user, 2026-11-11): an instance's share of the session's tokens in its own colour, and
/// the lead against its workers as one split bar.
/// </summary>
public sealed class PanelBarsTests
{
    private static readonly Color A = new(200, 0, 0), B = new(0, 0, 200);

    [Fact]
    public void A_share_fills_its_part_of_the_width_and_the_rest_is_track()
    {
        var cells = PanelBars.Cells([new BarPart(0.25, A)], 20);

        Assert.Equal(20, cells.Count);
        Assert.Equal(5, cells.Count(c => c == A));
        Assert.All(cells.Skip(5), c => Assert.Equal(PanelGauge.Track, c));
    }

    [Fact]
    public void A_split_bar_fills_each_part_in_its_colour_and_rounds_into_the_whole_width()
    {
        var cells = PanelBars.Cells([new BarPart(1.0 / 3, A), new BarPart(2.0 / 3, B)], 20);

        Assert.Equal(7, cells.Count(c => c == A));
        Assert.Equal(13, cells.Count(c => c == B));
    }

    /// <summary>A sliver is still seen: a share above nothing never rounds away to an empty bar.</summary>
    [Fact]
    public void A_small_share_still_shows_one_cell()
    {
        Assert.Single(PanelBars.Cells([new BarPart(0.01, A)], 20), c => c == A);
        Assert.DoesNotContain(PanelBars.Cells([new BarPart(0, A)], 20), c => c == A);
    }

    [Fact]
    public void Instances_take_their_colours_in_a_fixed_order()
    {
        Assert.Equal(PanelBars.InstanceColour(0), PanelBars.InstanceColour(0));
        Assert.NotEqual(PanelBars.InstanceColour(0), PanelBars.InstanceColour(1));
        Assert.Equal(PanelBars.InstanceColour(0), PanelBars.InstanceColour(PanelBars.Palette));   // the sequence repeats
    }
}
