using System.Globalization;
using CxAgent.Core.Models;
using SharpConsoleUI;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Core;
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

    /// <summary>Whether there is a desktop to open a viewer on; a seam for tests.</summary>
    internal Func<bool> Desktop { get; init; } = () => HasDesktop(Environment.GetEnvironmentVariable, OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD());

    /// <summary>
    /// Whether a viewer can open here.
    ///
    /// <para>ON LINUX ONLY WITH A DISPLAY. Without one xdg-open does not fail: it falls back to a text
    /// browser in this same terminal — w3m, lynx — which takes the screen from the app, and prints its
    /// search for one over the frame. macOS and Windows always have their desktop.</para>
    /// </summary>
    internal static bool HasDesktop(Func<string, string?> env, bool linux) =>
        !linux || !string.IsNullOrEmpty(env("DISPLAY")) || !string.IsNullOrEmpty(env("WAYLAND_DISPLAY"));

    /// <summary>Opens a file in the system viewer; a seam so a drive or a test never opens one on a desktop.</summary>
    internal Action<string> Launch { get; init; } = file => UrlLauncher.Open(new Uri(file).AbsoluteUri);

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

        if (Kitty()) OpenPortal(image, png);
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
        var file = Path.Combine(ViewerFolder(), image.Hash + ".png");
        try
        {
            // WRITTEN EVERY TIME, never trusted because it is there: whatever sits at this path — left by
            // an older run, or put there by someone else — is not what the model was sent.
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, png);
            if (Desktop()) Launch(file);
            else system.ToastService.Show($"no desktop to open the image on; it is at {file}", NotificationSeverity.Info);
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

    private void OpenPortal(AttachedImage image, byte[] png)
    {
        Close();

        var desktop = system.DesktopDimensions;
        var width = Math.Max(20, desktop.Width * 8 / 10);
        var height = Math.Max(8, desktop.Height * 8 / 10);
        var bounds = new SRectangle((desktop.Width - width) / 2, (desktop.Height - height) / 2, width, height);

        var panel = new ScrollablePanelControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Fill,
        };
        panel.AddControl(new ImageControl
        {
            Source = PixelBuffer.FromStream(new MemoryStream(png)),
            ScaleMode = ImageScaleMode.Fit,
            HorizontalAlignment = HorizontalAlignment.Center,
            MinimumHeight = height - 4,
        });
        panel.AddControl(new MarkupControl([$"[{ColorScheme.MutedMarkup}]{Sizes(image)} · any key closes[/]"])
        {
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        _open = system.DesktopPortalService.CreatePortal(new DesktopPortalOptions(
            Content: new ThemePortalContent(panel, bounds),
            Bounds: bounds,
            DismissOnClickOutside: true,
            OnDismiss: () => _open = null));
    }

    /// <summary>Any key closes the portal; it has nothing to do with a key but go away.</summary>
    public void ProcessKey(ConsoleKeyInfo key) => Close();

    public void Close()
    {
        if (_open is null) return;
        system.DesktopPortalService.RemovePortal(_open);
        _open = null;
    }
}
