using SharpConsoleUI.Configuration;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Imaging;
using SharpConsoleUI.Layout;

namespace CxAgent.UI;

/// <summary>
/// An image held to a number of rows, at its own shape.
///
/// <para>THE HEIGHT IS BOUNDED IN THE MEASURE, NOT BY SHRINKING THE PIXELS. A transcript, or a scroll panel,
/// lays its content out with no limit on height, and an image control asked that way fits the width alone:
/// a 16:9 screenshot at 141 columns is 40 rows, where a thumbnail wants ten. A smaller source would
/// fix the size and blur it, since Kitty is sent the pixels it is given and scales them into the cells;
/// bounding the measure keeps every pixel and fits them in.</para>
/// </summary>
internal sealed class BoundedImage : ImageControl
{
    /// <param name="source">The image.</param>
    /// <param name="rows">The most rows it may take.</param>
    public BoundedImage(PixelBuffer source, int rows)
    {
        Source = source;
        Rows = rows;
        ScaleMode = ImageScaleMode.Fit;
    }

    /// <summary>The most rows the image takes.</summary>
    public int Rows { get; }

    /// <summary>How wide it draws at its full height: its rows, at the image's shape, in cells two pixels tall.</summary>
    public int Columns => Source is { } s
        ? (int)Math.Ceiling((double)Rows * ImagingDefaults.PixelsPerCell * s.Width / s.Height)
        : 0;

    public override LayoutSize MeasureDOM(LayoutConstraints constraints) =>
        base.MeasureDOM(constraints with { MaxHeight = Math.Min(constraints.MaxHeight, Rows) });
}
