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
        new("[Image #1 1568×882]", "abc", 1568, 882) { Bytes = new byte[bytes], OriginalWidth = 1920, OriginalHeight = 1080 };

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

    /// <summary>A real PNG of the given size, as the session store would hold it.</summary>
    private static AttachedImage Decodable(int width, int height, string label = "[Image #1 1568×882]")
    {
        using var ms = new MemoryStream();
        new StbImageWriteSharp.ImageWriter().WritePng(new byte[width * height * 3], width, height, StbImageWriteSharp.ColorComponents.RedGreenBlue, ms);
        return new AttachedImage(label, "h", width, height) { Bytes = ms.ToArray() };
    }

    /// <summary>
    /// On Kitty a message shows its image under it, ten rows tall and as wide as its shape makes that,
    /// and keeps its button: the thumbnail takes no keys or clicks, and the button opens the whole image.
    /// </summary>
    [Fact]
    public void OnKitty_AMessageShowsItsImage_TenRowsTall_AndKeepsItsButton()
    {
        var chat = new ChatTranscriptControl();
        var sink = ChatTranscriptSink.ForTest(chat, kitty: true);
        sink.UserTurnAdded(new ChatMessageId(1), "look");

        sink.UserImagesAttached(new ChatMessageId(1), [Decodable(1568, 882)]);

        var thumbnail = chat.GetMessageAttachment(chat.MessageIds[^1]);
        Assert.NotNull(thumbnail);
        var size = ((SharpConsoleUI.Layout.IDOMPaintable)thumbnail!).MeasureDOM(new SharpConsoleUI.Layout.LayoutConstraints(0, 120, 0, int.MaxValue));
        Assert.Equal(ImageThumbnails.Rows, size.Height);
        Assert.InRange(size.Width, 34, 37);
        Assert.NotNull(chat.NewestActionsRow());
    }

    /// <summary>Without Kitty a thumbnail is half-blocks, which show nothing useful at this size: none.</summary>
    [Fact]
    public void WithoutKitty_AMessageShowsNoThumbnail()
    {
        var chat = new ChatTranscriptControl();
        var sink = ChatTranscriptSink.ForTest(chat, kitty: false);
        sink.UserTurnAdded(new ChatMessageId(1), "look");

        sink.UserImagesAttached(new ChatMessageId(1), [Decodable(1568, 882)]);

        Assert.Null(chat.GetMessageAttachment(chat.MessageIds[^1]));
    }

    /// <summary>
    /// The portal's image fits the portal's height and leaves the caption's row. Inside a scroll panel the
    /// height is unbounded, and an image fitted to the width alone runs taller than the portal: a 16:9
    /// screenshot at 141 columns is 40 rows, and the caption scrolls out of sight.
    /// </summary>
    [Fact]
    public void ThePortalsImage_FitsItsRows_AndLeavesTheCaptionInSight()
    {
        var image = ImagePreview.PortalImage(Decodable(1568, 882).Bytes!, rows: 36);

        var size = ((SharpConsoleUI.Layout.IDOMPaintable)image).MeasureDOM(new SharpConsoleUI.Layout.LayoutConstraints(0, 141, 0, int.MaxValue));

        Assert.True(size.Height <= 36, $"the image took {size.Height} rows of 36");
    }

    /// <summary>An image whose file was not kept has nothing to draw; the others still show.</summary>
    [Fact]
    public void AThumbnail_SkipsAnImageWithoutBytes()
    {
        Assert.Null(ImageThumbnails.For([Image() with { Bytes = null }], kitty: true));
        Assert.NotNull(ImageThumbnails.For([Image() with { Bytes = null }, Decodable(40, 20)], kitty: true));
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
        var preview = new ImagePreview(System()) { Kitty = () => false, Launch = f => { launched = f; return true; } };

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
        var preview = new ImagePreview(System()) { Kitty = () => false, Launch = f => { launched = f; return true; } };
        var image = Image(bytes: 10) with { Hash = "stale" + Guid.NewGuid().ToString("N") };
        preview.Open(image);
        File.WriteAllBytes(launched!, [9, 9, 9]);

        preview.Open(image);

        Assert.Equal(new byte[10], File.ReadAllBytes(launched!));
        Assert.DoesNotContain(Path.Combine(Path.GetTempPath(), "cxagent-images") + Path.DirectorySeparatorChar, launched);
    }

    /// <summary>
    /// A launcher that opened nothing — no desktop, over SSH — leaves the copy where the toast says, and
    /// nothing else happens: no portal, no second try.
    /// </summary>
    [Fact]
    public void ALaunchThatOpenedNothing_LeavesTheCopy()
    {
        string? tried = null;
        var preview = new ImagePreview(System()) { Kitty = () => false, Launch = f => { tried = f; return false; } };

        preview.Open(Image(bytes: 10));

        Assert.Equal(new byte[10], File.ReadAllBytes(tried!));
        Assert.False(preview.IsOpen);
    }

    [Fact]
    public void WithKitty_TheImageOpensInAPortal_AndAnyKeyClosesIt()
    {
        var png = new MemoryStream();
        new StbImageWriteSharp.ImageWriter().WritePng(new byte[4 * 2 * 3], 4, 2, StbImageWriteSharp.ColorComponents.RedGreenBlue, png);
        var launched = false;
        var preview = new ImagePreview(System()) { Kitty = () => true, Launch = _ => launched = true };

        preview.Open(Image() with { Bytes = png.ToArray() });
        Assert.True(preview.IsOpen);
        Assert.False(launched);

        preview.ProcessKey(new ConsoleKeyInfo('\u001b', ConsoleKey.Escape, false, false, false));
        Assert.False(preview.IsOpen);
    }
}
