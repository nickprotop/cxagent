using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The side panel's context gauge (user, 2026-11-11: the panel should use the TUI's gauges and gradients, from the
/// theme): one cell per column, filled along a calm-to-danger gradient, the rest a track, and a tick where
/// auto-compress starts.
/// </summary>
public sealed class PanelGaugeTests
{
    [Fact]
    public void Half_full_fills_half_the_cells()
    {
        var cells = PanelGauge.Cells(new GaugeReading(0.5, null), 20);

        Assert.Equal(20, cells.Count);
        Assert.Equal(10, cells.Count(c => c.Filled));
        Assert.All(cells.Take(10), c => Assert.True(c.Filled));
    }

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(1.0, 20)]
    [InlineData(1.7, 20)]   // past the window: full, never wider
    [InlineData(-0.2, 0)]
    public void The_fill_is_clamped_to_the_bar(double fraction, int filled) =>
        Assert.Equal(filled, PanelGauge.Cells(new GaugeReading(fraction, null), 20).Count(c => c.Filled));

    [Fact]
    public void The_compress_tick_sits_where_compressing_starts_full_or_not()
    {
        Assert.Equal(16, PanelGauge.Cells(new GaugeReading(0.3, 0.8), 20).FindIndex(c => c.Tick));
        Assert.Equal(16, PanelGauge.Cells(new GaugeReading(0.95, 0.8), 20).FindIndex(c => c.Tick));
        Assert.DoesNotContain(PanelGauge.Cells(new GaugeReading(0.3, null), 20), c => c.Tick);
    }

    /// <summary>A GRADIENT, NOT ONE COLOUR: the fill's far end, reached only when nearly full, is the danger end.</summary>
    [Fact]
    public void The_fill_runs_from_calm_to_danger()
    {
        var cells = PanelGauge.Cells(new GaugeReading(1.0, null), 20);

        Assert.NotEqual(cells[0].Colour, cells[^1].Colour);
        Assert.Equal(PanelGauge.Calm, cells[0].Colour);
        Assert.Equal(PanelGauge.Danger, cells[^1].Colour);
    }

    [Fact]
    public void The_markup_has_one_cell_per_column()
    {
        var markup = PanelGauge.Markup(new GaugeReading(0.4, 0.8), 24);

        Assert.Equal(24, SharpConsoleUI.Parsing.MarkupParser.StripLength(markup));
    }
}
