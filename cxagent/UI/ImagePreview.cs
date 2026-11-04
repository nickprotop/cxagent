using System.Globalization;
using CxAgent.Core.Models;
using SharpConsoleUI;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Core;
using SharpConsoleUI.Drawing;
using SharpConsoleUI.Events;
using SharpConsoleUI.Helpers;
using SharpConsoleUI.Imaging;
using SharpConsoleUI.Layout;
using SRectangle = System.Drawing.Rectangle;

namespace CxAgent.UI;

/// <summary>
/// Shows an attached image: in a portal over the app where the terminal draws pixels, and in the
/// system's own viewer everywhere else.
///
/// <para>NO HALF-BLOCKS. Without the Kitty graphics protocol the library draws an image as half-block
/// cells, two pixels to a cell, and a screenshot in a 128×36 cell portal is 128×72 pixels: colour bands
/// where the text was. The system viewer shows the PNG the model was sent at its full size, which is
/// what someone opening it wants to check.</para>
/// </summary>
internal sealed class ImagePreview(ConsoleWindowSystem system)
{
    private DesktopPortal? _open;

    /// <summary>
    /// The app's one preview. Process-wide because the composers and the transcripts that open it are made
    /// before the window system exists; the composition root sets it once.
    /// </summary>
    internal static ImagePreview? Current { get; set; }

    /// <summary>Whether the terminal draws pixels; a seam so a test can say either.</summary>
    internal Func<bool> Kitty { get; init; } = () => TerminalCapabilities.SupportsKittyGraphics;

    /// <summary>
    /// Opens a file in the system viewer, and says whether anything opened; a seam so a drive or a test
    /// never opens one on a desktop.
    ///
    /// <para>THE LAUNCHER DECIDES WHETHER THERE IS A DESKTOP. On Linux without a display xdg-open falls back
    /// to a text browser in this same terminal, which would take the screen from the app; the launcher
    /// refuses that case and says so, and the viewer's answer to "nothing opened" is the file's path.</para>
    /// </summary>
    internal Func<string, bool> Launch { get; init; } = file => UrlLauncher.TryOpen(new Uri(file).AbsoluteUri);

    public bool IsOpen => _open is not null;

    /// <summary>The one line an image's button carries: <c>▣ Image #1 1568×882 · 42 KB</c>.</summary>
    internal static string Line(AttachedImage image) => string.Create(CultureInfo.InvariantCulture,
        $"▣ {image.Label.Trim('[', ']')} · {Math.Max(1, (image.Bytes?.Length ?? 0) / 1024)} KB");

    /// <summary>The sizes under the portal: <c>1920×1080 → 1568×882, 42 KB</c>.</summary>
    internal static string Sizes(AttachedImage image)
    {
        var sent = $"{image.Width}×{image.Height}";
        var kb = Math.Max(1, (image.Bytes?.Length ?? 0) / 1024);
        return image.OriginalWidth > 0 && (image.OriginalWidth, image.OriginalHeight) != (image.Width, image.Height)
            ? string.Create(CultureInfo.InvariantCulture, $"{image.OriginalWidth}×{image.OriginalHeight} → {sent}, {kb} KB")
            : string.Create(CultureInfo.InvariantCulture, $"{sent}, {kb} KB");
    }

    /// <summary>Shows <paramref name="image"/> the way this terminal can.</summary>
    public void Open(AttachedImage image)
    {
        if (image.Bytes is not { Length: > 0 } png)
        {
            system.ToastService.Show($"{image.Unavailable}", NotificationSeverity.Warning);
            return;
        }

        if (Kitty()) OpenPortal(image);
        else OpenInViewer(image, png);
    }

    /// <summary>
    /// The system viewer, on a copy of the PNG.
    ///
    /// <para>A COPY IN THIS USER'S OWN TEMP FOLDER, not the session's own file: the session folder gets the
    /// image when the turn ends and saves, and a click on a message still being answered comes before that.
    /// Per user, because the system temp folder is everyone's.</para>
    ///
    /// <para>WITHOUT A DESKTOP IT SAYS WHERE THE FILE IS, and so does a launch that fails: over SSH there
    /// is nothing to open it on, and the path is what lets the user copy it somewhere that has one.</para>
    /// </summary>
    private void OpenInViewer(AttachedImage image, byte[] png)
    {
        var file = Path.Combine(ViewerFolder(), image.FileName);
        try
        {
            // WRITTEN EVERY TIME, never trusted because it is there: whatever sits at this path — left by
            // an older run, or put there by someone else — is not what the model was sent.
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, png);
            if (!Launch(file)) system.ToastService.Show($"no desktop to open the image on; it is at {file}", NotificationSeverity.Info);
        }
        catch (Exception e)
        {
            system.ToastService.Show($"couldn't open the image ({e.Message}); it is at {file}", NotificationSeverity.Warning);
        }
    }

    /// <summary>
    /// Where the viewer's copies go: <c>$XDG_RUNTIME_DIR/cxagent-images</c>, which only this user can read,
    /// else a folder in the temp directory named for the user.
    /// </summary>
    private static string ViewerFolder() =>
        Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } runtime
            ? Path.Combine(runtime, "cxagent-images")
            : Path.Combine(Path.GetTempPath(), "cxagent-images-" + Environment.UserName);

    private void OpenPortal(AttachedImage image)
    {
        Close();

        var desktop = system.DesktopDimensions;
        var width = Math.Max(20, desktop.Width * 8 / 10);
        var height = Math.Max(8, desktop.Height * 8 / 10);
        var bounds = new SRectangle((desktop.Width - width) / 2, (desktop.Height - height) / 2, width, height);

        _open = system.DesktopPortalService.CreatePortal(new DesktopPortalOptions(
            Content: PortalContent(image, bounds),
            Bounds: bounds,
            DismissOnClickOutside: true,
            OnDismiss: () => _open = null));
    }

    /// <summary>What the portal hosts: the image, and its sizes under it.</summary>
    internal static PortalContentBase PortalContent(AttachedImage image, SRectangle bounds) => new ImagePortalContent(image, bounds);

    /// <summary>The portal's image, held to <paramref name="rows"/>: the portal's inside, less the caption.</summary>
    internal static IWindowControl PortalImage(byte[] png, int rows) =>
        new BoundedImage(PixelBuffer.FromStream(new MemoryStream(png)), rows)
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            MinimumHeight = rows,
        };

    /// <summary>Any key closes the portal; it has nothing to do with a key but go away.</summary>
    public void ProcessKey(ConsoleKeyInfo key) => Close();

    public void Close()
    {
        if (_open is null) return;
        system.DesktopPortalService.RemovePortal(_open);
        _open = null;
    }
}

/// <summary>
/// The preview portal: the image, and its sizes on the row under it.
///
/// <para>PAINTED HERE, NOT HOSTED IN A PANEL. A portal measures and paints its content directly, with no
/// window layout pass; a scroll panel places its children in that pass, so hosted in a portal it painted
/// nothing at all, image and caption both. Two things in a fixed arrangement need no panel: the image takes
/// every row but the last, and the caption that one.</para>
/// </summary>
internal sealed class ImagePortalContent : PortalContentBase
{
    private readonly SRectangle _bounds;
    private readonly IWindowControl _image;
    private readonly string _caption;

    /// <param name="image">The image; its bytes must be there.</param>
    /// <param name="bounds">Where the portal sits, in desktop coordinates.</param>
    public ImagePortalContent(AttachedImage image, SRectangle bounds)
    {
        _bounds = bounds;
        BorderStyle = BoxChars.Rounded;
        BorderColor = DialogLook.Palette.ActiveBorder;
        BorderBackgroundColor = DialogLook.Palette.Surface;
        BackgroundColor = DialogLook.Palette.Surface;
        ForegroundColor = ColorScheme.Heading;

        // INSIDE THE BORDER, LESS THE CAPTION'S ROW.
        _image = ImagePreview.PortalImage(image.Bytes!, rows: Math.Max(1, bounds.Height - 3));
        // THE CONTAINER IS WHAT REACHES THE DRIVER: the image asks it whether the terminal draws pixels.
        _image.Container = this;
        _caption = $"{ImagePreview.Sizes(image)} · any key closes";
    }

    /// <inheritdoc/>
    public override SRectangle GetPortalBounds() => _bounds;

    /// <summary>A click inside does nothing; outside, the portal closes.</summary>
    public override bool ProcessMouseEvent(MouseEventArgs args) => false;

    /// <inheritdoc/>
    protected override void PaintPortalContent(
        CharacterBuffer buffer, LayoutRect bounds, LayoutRect clipRect, Color defaultFg, Color defaultBg)
    {
        var background = BackgroundColor;
        var imageRect = new LayoutRect(bounds.X, bounds.Y, bounds.Width, Math.Max(0, bounds.Height - 1));
        if (_image is IDOMPaintable paintable)
        {
            paintable.MeasureDOM(LayoutConstraints.Tight(imageRect.Width, imageRect.Height));
            paintable.PaintDOM(buffer, imageRect, imageRect.Intersect(clipRect), ForegroundColor, background);
        }

        var x = bounds.X + Math.Max(0, (bounds.Width - _caption.Length) / 2);
        buffer.WriteStringClipped(x, bounds.Y + bounds.Height - 1, _caption, ColorScheme.MutedRgb, background, clipRect);
    }
}
