using System.Text;
using CxAgent.UI;
using SharpConsoleUI;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Parsing;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The status bar's right-hand readout must keep a gap from the hints and never be clipped. The
/// library starts the right group where the left one ends when both do not fit, and clips what runs
/// past the edge — "F7:Jobsctx 2%" and "↑58" were that.
/// </summary>
public class FittedStatusBarTests
{
    private static readonly string[] Forms =
    [
        "ctx 1% · 1,234/200,000 · 12,345 spent ↑58k ↓1k",
        "ctx 1% · 12,345 spent ↑58k ↓1k",
        "ctx 1% · 12,345 spent",
        "ctx 1%",
    ];

    /// <summary>A bar with the left hints the app has, and a readout that picks a form to fit.</summary>
    private static (FittedStatusBar Bar, Func<string> Row) BarOfWidth(int width)
    {
        var bar = new FittedStatusBar(stickyBottom: false);
        bar.AddLeft("F7", "Jobs", null);
        bar.AddLeft("F9", "Theme Daylight", null);
        bar.AddLeft("F2", "Plugins", null);
        bar.AddLeft("F3", "Panel", null);
        var readout = bar.AddRight(string.Empty, Forms[0]);
        bar.Readout = readout;

        var widths = Forms.Select(MarkupParser.StripLength).ToList();
        bar.FitReadout = room =>
        {
            var pick = FittedStatusBar.PickForm(widths, room);
            readout.IsVisible = pick >= 0;
            if (pick >= 0) readout.Label = Forms[pick];
        };

        string Row()
        {
            var buffer = new CharacterBuffer(width, 1);
            var rect = new LayoutRect(0, 0, width, 1);
            bar.PaintDOM(buffer, rect, rect, Color.White, Color.Black);
            var text = new StringBuilder();
            for (var x = 0; x < width; x++) text.Append(buffer.GetCell(x, 0).Character.ToString());
            return text.ToString();
        }
        return (bar, Row);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(112)]
    [InlineData(90)]
    [InlineData(70)]
    public void TheReadoutIsNeverRunIntoTheHintsOrClipped(int width)
    {
        var (_, row) = BarOfWidth(width);

        var text = row();

        // Whatever form was chosen is there in full, so nothing was clipped at the edge...
        var shown = Forms.FirstOrDefault(f => text.Contains(f, StringComparison.Ordinal));
        if (shown is not null)
        {
            // ...and at least MinGap blanks sit between the last hint and it.
            var at = text.IndexOf(shown, StringComparison.Ordinal);
            var before = text[..at];
            Assert.EndsWith(new string(' ', FittedStatusBar.MinGap), before);
        }
        else
        {
            // Too narrow for even the shortest form: hidden, not mangled.
            Assert.DoesNotContain("ctx", text);
        }
    }

    [Fact]
    public void AWideBarKeepsTheFullReadout()
    {
        var (_, row) = BarOfWidth(200);

        Assert.Contains(Forms[0], row());
    }

    /// <summary>Narrowing the bar only ever takes things away, in the order of how little they are
    /// missed — the form shown never gets richer as the width shrinks, and every form is reached.</summary>
    [Fact]
    public void ANarrowerBarDropsTheLeastUsefulPartsFirst()
    {
        var seen = new List<int>();
        for (var width = 200; width >= 40; width--)
        {
            var text = BarOfWidth(width).Row();
            var index = Array.FindIndex(Forms, f => text.Contains(f, StringComparison.Ordinal));
            seen.Add(index < 0 ? Forms.Length : index);   // Forms.Length stands for "hidden"
        }

        Assert.Equal(seen.OrderBy(i => i).ToList(), seen);
        Assert.Equal(Enumerable.Range(0, Forms.Length + 1), seen.Distinct());
    }

    [Fact]
    public void PickFormTakesTheRichestThatFits()
    {
        Assert.Equal(0, FittedStatusBar.PickForm([40, 30, 20], 40));
        Assert.Equal(1, FittedStatusBar.PickForm([40, 30, 20], 39));
        Assert.Equal(2, FittedStatusBar.PickForm([40, 30, 20], 20));
        Assert.Equal(-1, FittedStatusBar.PickForm([40, 30, 20], 19));
    }

    [Fact]
    public void TheReadoutFormsRunRichestFirstAndNeverRepeat()
    {
        var forms = MainWindow.ContextLabelFormsForTest(used: 2_000, spent: 12_345, window: 200_000,
            input: 58_000, output: 1_000);

        Assert.Equal(4, forms.Count);
        Assert.True(forms[0].Length > forms[1].Length && forms[1].Length > forms[2].Length
                    && forms[2].Length > forms[3].Length);
        Assert.Contains("200,000", forms[0]);
        Assert.DoesNotContain("200,000", forms[1]);
        Assert.Contains("↑", forms[1]);
        Assert.DoesNotContain("↑", forms[2]);
        Assert.DoesNotContain("spent", forms[3]);
        Assert.Equal(forms.Count, forms.Distinct().Count());
    }
}
