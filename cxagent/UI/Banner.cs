using SharpConsoleUI;
using SharpConsoleUI.Parsing;

namespace CxAgent.UI;

/// <summary>
/// The startup wordmark.
///
/// <para>The reason a terminal agent opens with one: the first screen of a session is the one
/// moment with nothing to read, and a name there tells you which tool you are talking to before you
/// have typed anything. A single
/// startup line carrying the provider, the model id and a keybinding hint at once is the least
/// glanceable arrangement of the three.</para>
///
/// <para>WHAT IT DOES NOT SAY is half the design. No keybinding hint (the composer's own placeholder
/// already reads "Enter to run · end a line with \ to continue") and no model id (it is in the mode
/// line under the prompt). Repeating either here would make the banner a third place to keep in
/// sync.</para>
///
/// <para>It scrolls away with the conversation, like Antigravity's. Pinning it would spend permanent
/// rows of the one pane whose entire job is the transcript.</para>
/// </summary>
public static class Banner
{
    /// <summary>
    /// Below this many columns the block wordmark wraps into nonsense, so the light one is used
    /// instead. The block form is 60 columns wide; the threshold leaves room for the transcript's
    /// margin and a little slack rather than sitting exactly on the edge.
    /// </summary>
    public const int MinBlockWidth = 70;

    /// <summary>Six rows, 60 columns. The form that actually reads as a logo.</summary>
    private static readonly string[] Block =
    [
        @" ██████╗██╗  ██╗ █████╗  ██████╗ ███████╗███╗   ██╗████████╗",
        @"██╔════╝╚██╗██╔╝██╔══██╗██╔════╝ ██╔════╝████╗  ██║╚══██╔══╝",
        @"██║      ╚███╔╝ ███████║██║  ███╗█████╗  ██╔██╗ ██║   ██║   ",
        @"██║      ██╔██╗ ██╔══██║██║   ██║██╔══╝  ██║╚██╗██║   ██║   ",
        @"╚██████╗██╔╝ ██╗██║  ██║╚██████╔╝███████╗██║ ╚████║   ██║   ",
        @" ╚═════╝╚═╝  ╚═╝╚═╝  ╚═╝ ╚═════╝ ╚══════╝╚═╝  ╚═══╝   ╚═╝   ",
    ];

    /// <summary>Three rows, 27 columns — the narrow-terminal fallback.</summary>
    private static readonly string[] Light =
    [
        "╭─╮ ╷ ╷ ╭─╮ ╭─╮ ╭─╮ ╭╮  ╶┬╴",
        "│   ╶╳╴ ├─┤ │ ┬ ├─  │││  │ ",
        "╰─╯ ╵ ╵ ╵ ╵ ╰─╯ ╰─╯ ╵╰╯  ╵ ",
    ];

    /// <summary>
    /// The banner as markup, ready for a System message.
    /// </summary>
    /// <param name="terminalWidth">Columns available; decides which wordmark is used.</param>
    /// <param name="subtitle">The orientation line under the mark — mode, provider, model.</param>
    public static string Render(int terminalWidth, string subtitle)
    {
        var art = terminalWidth >= MinBlockWidth ? Block : Light;
        var lines = art.Select(Gradient).ToList();

        lines.Add(string.Empty);
        lines.Add($"[{ColorScheme.MutedMarkup}]{MarkupParser.Escape(subtitle)}[/]");

        return string.Join('\n', lines);
    }

    /// <summary>
    /// One row of the wordmark, faded ACROSS its width from the accent to the code colour.
    ///
    /// <para>Horizontal, not vertical, and that is the whole reason this is per-column rather than
    /// per-row. A vertical fade darkens the bottom row — which is where a letterform's base sits —
    /// so the mark loses its footing exactly where it needs to be read. Across the width every row
    /// stays equally legible and the fade reads as one object rather than six stacked strips.</para>
    ///
    /// <para>Both endpoints are colours the app already uses: the accent it marks everything active
    /// with, and the markdown code colour. A decorative pair invented here would be one more thing
    /// to keep in step.</para>
    ///
    /// <para>CODE RATHER THAN HEADING, and the difference is the whole fade: headings ARE the accent
    /// on any theme whose accent already reads against its ground, so fading accent-to-heading
    /// travels nowhere and the wordmark comes out one flat colour. Code is guaranteed a visible
    /// luminance step from the accent by construction, which is exactly what a fade needs.</para>
    /// </summary>
    private static string Gradient(string row)
    {
        var sb = new System.Text.StringBuilder(row.Length * 24);
        var span = Math.Max(1, row.Length - 1);

        for (var i = 0; i < row.Length; i++)
        {
            // A space carries no ink, so colouring it is markup nobody sees — and at ~24 bytes of
            // tag per cell that is most of the string for a wordmark this sparse.
            if (row[i] == ' ') { sb.Append(' '); continue; }

            var c = Lerp(ColorScheme.AccentRgb, ColorScheme.Code, (double)i / span);
            sb.Append($"[#{c.R:x2}{c.G:x2}{c.B:x2}]{MarkupParser.Escape(row[i].ToString())}[/]");
        }

        return sb.ToString();
    }

    private static Color Lerp(Color from, Color to, double t) => new(
        (byte)Math.Round(from.R + (to.R - from.R) * t),
        (byte)Math.Round(from.G + (to.G - from.G) * t),
        (byte)Math.Round(from.B + (to.B - from.B) * t));
}
