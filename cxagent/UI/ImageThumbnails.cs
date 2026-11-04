using CxAgent.Core.Models;
using SharpConsoleUI.Configuration;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Imaging;
using SharpConsoleUI.Layout;

namespace CxAgent.UI;

/// <summary>
/// A message's images, small, under it.
///
/// <para>KITTY ONLY. Kitty draws the image's own pixels scaled into the cells, so ten rows show a screenshot
/// well enough to recognise. Half-blocks at that size are a smear of colour that says nothing a label does
/// not, and the message's button opens the image in the system viewer instead.</para>
/// </summary>
internal static class ImageThumbnails
{
    /// <summary>How tall a thumbnail is, in rows: enough to know a screenshot again without pushing the
    /// conversation far down a screen.</summary>
    internal const int Rows = 10;

    /// <summary>The thumbnails for <paramref name="images"/>, side by side, or null when there is nothing to show.</summary>
    /// <param name="images">The message's images; one whose bytes are gone, or that does not decode, is left out.</param>
    /// <param name="kitty">Whether the terminal draws pixels.</param>
    internal static IWindowControl? For(IReadOnlyList<AttachedImage> images, bool kitty)
    {
        if (!kitty) return null;
        var thumbnails = images.Select(Decode).OfType<Thumbnail>().ToList();
        if (thumbnails.Count <= 1) return thumbnails.FirstOrDefault();

        var row = new HorizontalGridControl();
        foreach (var thumbnail in thumbnails)
        {
            // ONE COLUMN WIDER THAN THE IMAGE, so two side by side do not touch.
            var column = new ColumnContainer(row) { Width = thumbnail.Columns + 1 };
            column.AddContent(thumbnail);
            row.AddColumn(column);
        }
        return row;
    }

    private static Thumbnail? Decode(AttachedImage image)
    {
        if (image.Bytes is not { Length: > 0 } bytes) return null;
        try
        {
            return new Thumbnail(PixelBuffer.FromStream(new MemoryStream(bytes)));
        }
        catch (Exception)
        {
            // The bytes were decoded once already, when pasted; a file changed on disk since is not worth
            // more than leaving its picture out. The button still names it.
            return null;
        }
    }

    /// <summary>
    /// An image held to <see cref="Rows"/> rows.
    ///
    /// <para>THE HEIGHT IS BOUNDED HERE, NOT BY SHRINKING THE PIXELS. A transcript lays a message out with no
    /// limit on height, and an image control asked that way takes a cell per pixel: a screenshot thirty
    /// rows tall. A smaller source would fix the size and blur it, since Kitty is sent the pixels it is
    /// given and scales them into the cells; bounding the measure keeps every pixel and fits them in.</para>
    /// </summary>
    private sealed class Thumbnail : ImageControl
    {
        public Thumbnail(PixelBuffer source)
        {
            Source = source;
            ScaleMode = ImageScaleMode.Fit;
        }

        /// <summary>How wide the thumbnail draws: its rows, at the image's shape, in cells two pixels tall.</summary>
        public int Columns => Source is { } s
            ? (int)Math.Ceiling((double)Rows * ImagingDefaults.PixelsPerCell * s.Width / s.Height)
            : 0;

        public override LayoutSize MeasureDOM(LayoutConstraints constraints) =>
            base.MeasureDOM(constraints with { MaxHeight = Math.Min(constraints.MaxHeight, Rows) });
    }
}
