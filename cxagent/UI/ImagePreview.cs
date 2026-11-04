using System.Globalization;
using CxAgent.Core.Models;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Core;
using SharpConsoleUI.Helpers;
using SharpConsoleUI.Imaging;
using SharpConsoleUI.Layout;

namespace CxAgent.UI;

/// <summary>
/// Shows an attached image in a window of its own, and offers the system viewer from there.
///
/// <para>A WINDOW, NOT A PORTAL. A window lays its controls out and paints them through the same pipeline
/// as every other control, so the image control is just a control. A desktop portal is copied to the
/// terminal by a path of its own, and Kitty draws no image from what reaches it that way.</para>
///
/// <para>ON EVERY TERMINAL. Kitty draws the image's own pixels; any other terminal draws half-blocks, two
/// pixels to a cell, which in a window this size still show what the picture is. The viewer button is for
/// the image at its full size, which the half-blocks are not.</para>
/// </summary>
internal sealed class ImagePreview(ConsoleWindowSystem system)
{
    private Window? _open;

    /// <summary>
    /// The app's one preview. Process-wide because the composers and the transcripts that open it are made
    /// before the window system exists; the composition root sets it once.
    /// </summary>
    internal static ImagePreview? Current { get; set; }

    /// <summary>
    /// Opens a file in the system viewer, and says whether anything opened; a seam so a drive or a test
    /// never opens one on a desktop.
    ///
    /// <para>THE LAUNCHER DECIDES WHETHER THERE IS A DESKTOP. On Linux without a display xdg-open falls back
    /// to a text browser in this same terminal, which would take the screen from the app; the launcher
    /// refuses that case and says so, and the viewer's answer to "nothing opened" is the file's path.</para>
    /// </summary>
    internal Func<string, bool> Launch { get; init; } = file => UrlLauncher.TryOpen(new Uri(file).AbsoluteUri);

    /// <summary>The one line an image's button carries: <c>▣ Image #1 1568×882 · 42 KB</c>.</summary>
    internal static string Line(AttachedImage image) => string.Create(CultureInfo.InvariantCulture,
        $"▣ {image.Label.Trim('[', ']')} · {Math.Max(1, (image.Bytes?.Length ?? 0) / 1024)} KB");

    /// <summary>The sizes in the window's title: <c>1920×1080 → 1568×882, 42 KB</c>.</summary>
    internal static string Sizes(AttachedImage image)
    {
        var sent = $"{image.Width}×{image.Height}";
        var kb = Math.Max(1, (image.Bytes?.Length ?? 0) / 1024);
        return image.OriginalWidth > 0 && (image.OriginalWidth, image.OriginalHeight) != (image.Width, image.Height)
            ? string.Create(CultureInfo.InvariantCulture, $"{image.OriginalWidth}×{image.OriginalHeight} → {sent}, {kb} KB")
            : string.Create(CultureInfo.InvariantCulture, $"{sent}, {kb} KB");
    }

    /// <summary>Shows <paramref name="image"/> in its window, replacing one already open.</summary>
    public void Open(AttachedImage image)
    {
        if (image.Bytes is not { Length: > 0 } bytes)
        {
            system.ToastService.Show($"{image.Unavailable}", NotificationSeverity.Warning);
            return;
        }

        // DECODED BEFORE ANY WINDOW, so a picture that will not decode — its file changed on disk since it
        // was pasted — is said where the user clicked, not thrown from inside the click.
        PixelBuffer pixels;
        try
        {
            pixels = PixelBuffer.FromStream(new MemoryStream(bytes));
        }
        catch (Exception e)
        {
            system.ToastService.Show($"can't show {image.Label.Trim('[', ']')}: {e.Message}", NotificationSeverity.Warning);
            return;
        }

        Close();
        var desktop = system.DesktopDimensions;
        var window = new WindowBuilder(system)
            .WithTitle($"{image.Label.Trim('[', ']')} · {Sizes(image)}")
            .WithSize(Math.Max(40, desktop.Width * 9 / 10), Math.Max(12, desktop.Height * 9 / 10))
            .Centered()
            .AsModal()
            .Styled()
            .Minimizable(false)
            .OnKeyPressed((_, e) =>
            {
                if (e.KeyInfo.Key != ConsoleKey.Escape) return;
                Close();
                e.Handled = true;
            })
            .OnClosed((_, _) => _open = null)
            .Build();
        DialogLook.Titled(window);

        window.AddControl(new ImageControl
        {
            Source = pixels,
            ScaleMode = ImageScaleMode.Fit,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Fill,
        });
        DialogLook.AddFooter(window, "Esc closes",
            Controls.Button(" Open in viewer ").OnClick((_, _) => OpenInViewer(image)).Build());

        _open = window;
        system.AddWindow(window);
    }

    /// <summary>Closes the window, if one is open.</summary>
    public void Close()
    {
        if (_open is not { } window) return;
        _open = null;
        system.CloseWindow(window);
    }

    /// <summary>
    /// The system viewer, on a copy of the bytes the model was sent.
    ///
    /// <para>A COPY IN THIS USER'S OWN TEMP FOLDER, not the session's own file: the session folder gets the
    /// image when the turn ends and saves, and a click on a message still being answered comes before that.
    /// Per user, because the system temp folder is everyone's.</para>
    ///
    /// <para>WITHOUT A DESKTOP IT SAYS WHERE THE FILE IS, and so does a launch that fails: over SSH there
    /// is nothing to open it on, and the path is what lets the user copy it somewhere that has one.</para>
    /// </summary>
    internal void OpenInViewer(AttachedImage image)
    {
        if (image.Bytes is not { Length: > 0 } png) return;
        var file = Path.Combine(ViewerFolder, image.FileName);
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

    /// <summary>Where the viewer's copies go; a seam so a test's copies never land in the user's own folder.</summary>
    internal string ViewerFolder { get; init; } = DefaultViewerFolder();

    /// <summary>
    /// The app's folder for the viewer's copies: <c>$XDG_RUNTIME_DIR/cxagent-images</c>, which only this user
    /// can read, else a folder in the temp directory named for the user.
    /// </summary>
    /// <param name="env">Reads an environment variable; the process's own unless a test says.</param>
    internal static string DefaultViewerFolder(Func<string, string?>? env = null) =>
        (env ?? Environment.GetEnvironmentVariable)("XDG_RUNTIME_DIR") is { Length: > 0 } runtime
            ? Path.Combine(runtime, "cxagent-images")
            : Path.Combine(Path.GetTempPath(), "cxagent-images-" + Environment.UserName);
}
