using CxAgent.Core.Models;
using CxAgent.Core.Sessions;
using CxAgent.UI;
using SharpConsoleUI;
using SharpConsoleUI.Configuration;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Drivers;
using Xunit;
using ChatMessageId = CxAgent.Core.Sessions.ChatMessageId;

namespace CxAgent.Tests;

/// <summary>
/// A message's images show as one button each on it, and a button opens the image the way the terminal
/// can: a portal where it draws pixels, the system viewer where it does not.
/// </summary>
public class ImageRowTests
{
    private static AttachedImage Image(int bytes = 43_008) =>
        new("[Image #1 1568×882]", "abc", 1568, 882) { Png = new byte[bytes], OriginalWidth = 1920, OriginalHeight = 1080 };

    [Fact]
    public void TheButton_NamesTheImageItsSizeAndWeight()
    {
        Assert.Equal("▣ Image #1 1568×882 · 42 KB", ImagePreview.Line(Image()));
    }

    [Fact]
    public void ThePortalCaption_ShowsWhatTheImageWasShrunkFrom()
    {
        Assert.Equal("1920×1080 → 1568×882, 42 KB", ImagePreview.Sizes(Image()));
        Assert.Equal("1568×882, 42 KB", ImagePreview.Sizes(Image() with { OriginalWidth = 1568, OriginalHeight = 882 }));
    }

    [Fact]
    public void AUserTurnWithImages_GetsAButtonPerImage()
    {
        var chat = new ChatTranscriptControl();
        var sink = ChatTranscriptSink.ForTest(chat);
        var id = new ChatMessageId(1);

        sink.UserTurnAdded(id, "what is [Image #1 1568×882]?");
        sink.UserImagesAttached(id, [Image()]);

        var row = chat.NewestActionsRow();
        Assert.NotNull(row);
        Assert.Contains(row!.Items.OfType<ButtonControl>(), b => b.Text.Contains("Image #1 1568×882 · 42 KB"));
    }

    [Fact]
    public void TheFanOut_PassesImagesOn()
    {
        var fan = new ObserverFanOut();
        var seen = new List<IReadOnlyList<AttachedImage>>();
        fan.Add(new Watcher(seen));

        fan.UserImagesAttached(new ChatMessageId(1), [Image()]);

        Assert.Single(seen);
    }

    private sealed class Watcher(List<IReadOnlyList<AttachedImage>> seen) : ISessionObserver
    {
        public void UserImagesAttached(ChatMessageId id, IReadOnlyList<AttachedImage> images) => seen.Add(images);
        public void UserTurnAdded(ChatMessageId id, string text) { }
        public void AssistantTurnBegan(ChatMessageId id) { }
        public void AssistantTextAppended(ChatMessageId id, string token) { }
        public void AssistantThinkingAppended(ChatMessageId id, string text) { }
        public void AssistantThinkingEnded(ChatMessageId id, CxAgent.Core.Llm.ThinkingSummary summary) { }
        public void AssistantTurnEnded(ChatMessageId id) { }
        public void AssistantLabelled(ChatMessageId id, string header) { }
        public void Said(CxAgent.Core.Commands.Message message) { }
    }

    private static ConsoleWindowSystem System() =>
        new(new HeadlessConsoleDriver(120, 40), new ConsoleWindowSystemOptions(InstallSynchronizationContext: true));

    /// <summary>Without pixels, the viewer gets a file with the PNG the model was sent.</summary>
    [Fact]
    public void WithoutKitty_TheImageOpensInTheViewer()
    {
        string? launched = null;
        var preview = new ImagePreview(System()) { Kitty = () => false, Desktop = () => true, Launch = f => launched = f };

        preview.Open(Image(bytes: 10));

        Assert.NotNull(launched);
        Assert.Equal(new byte[10], File.ReadAllBytes(launched!));
        Assert.False(preview.IsOpen);
    }

    /// <summary>
    /// The viewer opens the bytes the model was sent, whatever is already at the copy's path: a file left
    /// there — by an older run, or by someone else on a shared temp folder — is never shown in its place.
    /// </summary>
    [Fact]
    public void TheViewerCopy_IsWrittenEveryTime()
    {
        string? launched = null;
        var preview = new ImagePreview(System()) { Kitty = () => false, Desktop = () => true, Launch = f => launched = f };
        var image = Image(bytes: 10) with { Hash = "stale" + Guid.NewGuid().ToString("N") };
        preview.Open(image);
        File.WriteAllBytes(launched!, [9, 9, 9]);

        preview.Open(image);

        Assert.Equal(new byte[10], File.ReadAllBytes(launched!));
        Assert.DoesNotContain(Path.Combine(Path.GetTempPath(), "cxagent-images") + Path.DirectorySeparatorChar, launched);
    }

    /// <summary>
    /// With no desktop to open it on — over SSH, or a console without a display — nothing is launched.
    /// xdg-open there falls back to a text browser, which takes the terminal from the app.
    /// </summary>
    [Fact]
    public void WithoutADesktop_NothingIsLaunched()
    {
        var launched = false;
        var preview = new ImagePreview(System()) { Kitty = () => false, Desktop = () => false, Launch = _ => launched = true };

        preview.Open(Image(bytes: 10));

        Assert.False(launched);
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData(":0", null, true)]
    [InlineData(null, "wayland-0", true)]
    [InlineData("", "", false)]
    public void OnLinux_ADesktopIsADisplayOrAWaylandSocket(string? display, string? wayland, bool desktop)
    {
        Assert.Equal(desktop, ImagePreview.HasDesktop(name => name switch
        {
            "DISPLAY" => display,
            "WAYLAND_DISPLAY" => wayland,
            _ => null,
        }, linux: true));
    }

    [Fact]
    public void WithKitty_TheImageOpensInAPortal_AndAnyKeyClosesIt()
    {
        var png = new MemoryStream();
        new StbImageWriteSharp.ImageWriter().WritePng(new byte[4 * 2 * 3], 4, 2, StbImageWriteSharp.ColorComponents.RedGreenBlue, png);
        var launched = false;
        var preview = new ImagePreview(System()) { Kitty = () => true, Launch = _ => launched = true };

        preview.Open(Image() with { Png = png.ToArray() });
        Assert.True(preview.IsOpen);
        Assert.False(launched);

        preview.ProcessKey(new ConsoleKeyInfo('\u001b', ConsoleKey.Escape, false, false, false));
        Assert.False(preview.IsOpen);
    }
}
