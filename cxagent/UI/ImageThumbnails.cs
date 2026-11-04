using CxAgent.Core.Models;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Imaging;

namespace CxAgent.UI;

/// <summary>
/// A message's images, small, under it.
///
/// <para>KITTY ONLY. Kitty draws the image's own pixels scaled into the cells, so ten rows show a screenshot
/// well enough to recognise. Half-blocks at that size are a smear of colour that says nothing a label does
/// not; the message's button opens the image in its own window, large enough for half-blocks to show it.</para>
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
        var thumbnails = images.Select(Decode).OfType<BoundedImage>().ToList();
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

    private static BoundedImage? Decode(AttachedImage image)
    {
        if (image.Bytes is not { Length: > 0 } bytes) return null;
        try
        {
            return new BoundedImage(PixelBuffer.FromStream(new MemoryStream(bytes)), Rows);
        }
        catch (Exception)
        {
            // The bytes were decoded once already, when pasted; a file changed on disk since is not worth
            // more than leaving its picture out. The button still names it.
            return null;
        }
    }
}
