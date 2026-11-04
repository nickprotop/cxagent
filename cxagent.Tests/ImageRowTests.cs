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
/// A message's images show as one button each on it, a thumbnail on Kitty, and a button opens the image in
/// a window of its own, with the system viewer a button away from there.
/// </summary>
public class ImageRowTests : IDisposable
{
    /// <summary>Where these tests' viewer copies go: never the user's own folder, which a run would litter.</summary>
    private readonly string _viewer = Path.Combine(Path.GetTempPath(), "imgrow-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_viewer)) Directory.Delete(_viewer, recursive: true);
    }

    private static AttachedImage Image(int bytes = 43_008) =>
        new("[Image #1 1568×882]", "abc", 1568, 882) { Bytes = new byte[bytes], OriginalWidth = 1920, OriginalHeight = 1080 };

    [Fact]
    public void TheButton_NamesTheImageItsSizeAndWeight()
    {
        Assert.Equal("▣ Image #1 1568×882 · 42 KB", ImagePreview.Line(Image()));
    }

    [Fact]
    public void TheSizes_ShowWhatTheImageWasShrunkFrom()
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

    /// <summary>Every control under <paramref name="root"/>, depth first.</summary>
    private static IEnumerable<IWindowControl> All(IEnumerable<IWindowControl> root)
    {
        foreach (var control in root)
        {
            yield return control;
            if (control is IContainerControl container)
                foreach (var child in All(container.GetChildren())) yield return child;
        }
    }

    private static Window OpenedIn(ConsoleWindowSystem system) =>
        Assert.Single(system.Windows.Values, w => w.Title.Contains("Image #1"));

    /// <summary>
    /// The image opens in a window of its own, large, with a single border and only a close button, titled
    /// with what it is and what it was shrunk from. Kitty draws its pixels there; any other terminal draws
    /// half-blocks, which at that size still show what the picture is.
    /// </summary>
    [Fact]
    public void Opening_ShowsTheImageInAWindow_TitledWithItsSizes()
    {
        var system = System();
        var preview = new ImagePreview(system);

        preview.Open(Decodable(1568, 882) with { OriginalWidth = 1920, OriginalHeight = 1080 });

        var window = OpenedIn(system);
        Assert.Contains("1920×1080 → 1568×882", window.Title);
        Assert.Equal(BorderStyle.Single, window.BorderStyle);
        Assert.False(window.IsMinimizable);
        Assert.False(window.IsMaximizable);
        Assert.True(window.IsClosable);
        var image = Assert.Single(All(window.GetControls()).OfType<ImageControl>());
        Assert.Equal((1568, 882), (image.Source!.Width, image.Source.Height));
    }

    /// <summary>The window's button opens the image in the system viewer, on a copy of the bytes the model was sent.</summary>
    [Fact]
    public void TheViewerButton_OpensTheImageInTheSystemViewer()
    {
        string? launched = null;
        var system = System();
        var preview = new ImagePreview(system) { ViewerFolder = _viewer, Launch = f => { launched = f; return true; } };
        var image = Decodable(40, 20);
        preview.Open(image);

        var button = Assert.Single(All(OpenedIn(system).GetControls()).OfType<ButtonControl>(), b => b.Text.Contains("Open in viewer"));
        button.ProcessKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false));

        Assert.NotNull(launched);
        Assert.Equal(image.Bytes, File.ReadAllBytes(launched!));
    }

    /// <summary>Opening another image replaces the window rather than stacking a second one.</summary>
    [Fact]
    public void OpeningAgain_ReplacesTheWindow()
    {
        var system = System();
        var preview = new ImagePreview(system);

        preview.Open(Decodable(40, 20));
        preview.Open(Decodable(40, 20));

        OpenedIn(system);
    }

    /// <summary>
    /// The viewer opens the bytes the model was sent, whatever is already at the copy's path: a file left
    /// there — by an older run, or by someone else on a shared temp folder — is never shown in its place.
    /// </summary>
    [Fact]
    public void TheViewerCopy_IsWrittenEveryTime()
    {
        string? launched = null;
        var preview = new ImagePreview(System()) { ViewerFolder = _viewer, Launch = f => { launched = f; return true; } };
        var image = Image(bytes: 10) with { Hash = "stale" + Guid.NewGuid().ToString("N") };
        preview.OpenInViewer(image);
        File.WriteAllBytes(launched!, [9, 9, 9]);

        preview.OpenInViewer(image);

        Assert.Equal(new byte[10], File.ReadAllBytes(launched!));
    }

    /// <summary>The copies go where the preview is told, so a test's never reach the user's own folder.</summary>
    [Fact]
    public void TheViewerCopy_GoesInTheFolderItIsGiven()
    {
        string? launched = null;
        var preview = new ImagePreview(System()) { ViewerFolder = _viewer, Launch = f => { launched = f; return true; } };

        preview.OpenInViewer(Image(bytes: 10));

        Assert.Equal(_viewer, Path.GetDirectoryName(launched));
    }

    /// <summary>
    /// The app's own folder is this user's: the runtime folder, which only they can read, where there is
    /// one; else a folder in the temp directory named for them, since the temp directory is everyone's.
    /// </summary>
    [Theory]
    [InlineData("/run/user/1000", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void TheDefaultViewerFolder_IsThisUsersOwn(string? runtime, bool inRuntime)
    {
        var folder = ImagePreview.DefaultViewerFolder(name => name == "XDG_RUNTIME_DIR" ? runtime : null);

        Assert.Equal(inRuntime
            ? Path.Combine("/run/user/1000", "cxagent-images")
            : Path.Combine(Path.GetTempPath(), "cxagent-images-" + Environment.UserName), folder);
    }

    /// <summary>An image that does not decode — its file changed on disk — is said, and opens no window.</summary>
    [Fact]
    public void AnImageThatDoesNotDecode_IsSaid_AndOpensNoWindow()
    {
        var system = System();
        var preview = new ImagePreview(system);

        preview.Open(Image(bytes: 10));

        Assert.DoesNotContain(system.Windows.Values, w => w.Title.Contains("Image #1"));
    }
}
