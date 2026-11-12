using CxAgent.Core.Helpers;
using CxAgent.Core.Sessions;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Helpers;
using SharpConsoleUI.Layout;

namespace CxAgent.UI;

/// <summary>
/// The side panel's per-turn sparklines: the lead's tokens, the workers' on a row of their own once they spent anything,
/// the cache rate and the context — one point per lead model response, from the session's <see cref="TurnHistory"/>,
/// each under a muted label carrying its latest value.
///
/// <para>THE LIBRARY'S OWN SPARKLINES, coloured along gradients from the theme's roles and recoloured by
/// <see cref="Recolour"/> on a theme change. TWO ROWS FOR TOKENS, NOT ONE STACKED: the sparkline draws one series per row,
/// and the user chose two rows over the up-and-down mode (2026-11-11).</para>
///
/// <para>A RESPONSE THAT REPORTED NO CACHE DRAWS AS AN EMPTY CELL — the control has no gap — and the label names the
/// latest rate that was reported.</para>
/// </summary>
public sealed class TurnCharts
{
    // ROWS PER CHART: one reads as noise at this width, three crowds the panel. Drawn in Braille, whose
    // dots read as a fine line in the narrow panel where full blocks read as a solid wall.
    private const int ChartHeight = 2;

    private sealed record Series(string Name, Func<TurnPoint, double> Value, Func<IReadOnlyList<TurnPoint>, string> Latest,
        bool Ratio)
    {
        public required MarkupControl Label { get; init; }
        public required SparklineControl Chart { get; init; }
    }

    private readonly List<Series> _series;
    private readonly PanelControl _box;
    private string _text = "";

    public TurnCharts()
    {
        Series Make(string name, Func<TurnPoint, double> value, Func<IReadOnlyList<TurnPoint>, string> latest, bool ratio) =>
            new(name, value, latest, ratio)
            {
                Label = Controls.Markup().WithMargin(1, 0, 1, 0).Build(),
                Chart = new SparklineBuilder().WithHeight(ChartHeight).WithMode(SparklineMode.Braille).WithMinValue(0)
                    .WithMargin(1, 0, 1, 0).Build(),
            };

        _series =
        [
            Make("tokens", p => p.Tokens, ps => DisplayNumber.Compact(ps[^1].Tokens), ratio: false),
            Make("workers", p => p.WorkerTokens, ps => DisplayNumber.Compact(ps[^1].WorkerTokens), ratio: false),
            Make("cache", p => p.Cache ?? 0,
                ps => ps.LastOrDefault(p => p.Cache is not null)?.Cache is { } rate ? $"{Math.Round(rate * 100)}%" : "—", ratio: true),
            Make("context", p => p.Window is > 0 and var w ? (double)p.Context / w : 0,
                ps => ps[^1].Window is > 0 and var w ? $"{Math.Round(100.0 * ps[^1].Context / w)}%" : DisplayNumber.Compact(ps[^1].Context),
                ratio: true),
        ];

        var box = Controls.Panel().NoBorder().WithPadding(0);
        foreach (var s in _series) box.AddControl(s.Label).AddControl(s.Chart);
        _box = box.Build();
        _box.Visible = false;
        Recolour();
    }

    /// <summary>The block the panel places under its Context section.</summary>
    public PanelControl Control => _box;

    /// <summary>The labels as drawn, for tests and the panel's text.</summary>
    public string Text => _text;

    /// <summary>Test seam: the values a series' chart was given.</summary>
    internal IReadOnlyList<double> DataFor(string name) => _series.First(s => s.Name == name).Chart.DataPoints;

    /// <summary>Draws <paramref name="points"/>, the newest that fit <paramref name="width"/> columns.</summary>
    public void Update(IReadOnlyList<TurnPoint> points, int width)
    {
        if (points.Count == 0)
        {
            if (_box.Visible) _box.Visible = false;
            _text = "";
            return;
        }

        var shown = points.Count > width ? points.Skip(points.Count - width).ToList() : points;
        var lines = new List<string>();
        foreach (var s in _series)
        {
            // THE WORKERS' ROW WAITS FOR A WORKER IN THE TURNS SHOWN: an empty chart is a row saying nothing, and one that
            // spent only in turns too old to fit draws nothing here.
            var on = s.Name != "workers" || shown.Any(p => p.WorkerTokens > 0);
            s.Label.Visible = on;
            s.Chart.Visible = on;
            if (!on) continue;

            var label = $"{s.Name} · last {s.Latest(points)}";
            // A GAP IS A RESPONSE THAT REPORTED NO CACHE — named, so the empty cells are not a puzzle.
            if (s.Name == "cache" && shown.Any(p => p.Cache is null)) label += " · gaps: not reported";
            lines.Add(label);
            s.Label.SetContent([$"[{ColorScheme.MutedMarkup}]{SharpConsoleUI.Parsing.MarkupParser.Escape(label)}[/]"]);
            s.Chart.MaxDataPoints = width;
            if (s.Ratio) s.Chart.MaxValue = 1;
            s.Chart.SetDataPoints(shown.Select(s.Value));
        }
        _text = string.Join("\n", lines);
        if (!_box.Visible) _box.Visible = true;
    }

    /// <summary>The charts' colours from the theme now — called when it changes.</summary>
    public void Recolour()
    {
        var accent = ColorScheme.RoleRgb(ColorScheme.Accent, new Color(0x4f, 0xc3, 0xf7));
        var structure = ColorScheme.RoleRgb(ColorScheme.Structure, new Color(0xb3, 0x9d, 0xdb));
        foreach (var s in _series)
        {
            s.Chart.BackgroundColor = ColorScheme.PanelSurface;
            s.Chart.Gradient = s.Name switch
            {
                // SPEND RISES FROM THE SURFACE INTO THE ACCENT: a taller bar is a brighter one.
                "tokens" => ColorGradient.FromColors(SharpConsoleUI.Helpers.PaletteColors.Mix(ColorScheme.PanelSurface, accent, 0.45), accent),
                "workers" => ColorGradient.FromColors(SharpConsoleUI.Helpers.PaletteColors.Mix(ColorScheme.PanelSurface, structure, 0.45), structure),
                // A HIT IS GOOD: the cache rises from caution into calm.
                "cache" => ColorGradient.FromColors(PanelGauge.Caution, PanelGauge.Calm),
                // THE CONTEXT FILLS AS THE GAUGE DOES, calm to danger.
                _ => ColorGradient.FromColors(PanelGauge.Calm, PanelGauge.Caution, PanelGauge.Danger),
            };
        }
    }
}
