using CxAgent.Core.Agents;
using CxAgent.Core.Models;
using CxAgent.UI;
using SharpConsoleUI.Controls;
using StbImageWriteSharp;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// A dropped image file becomes a token: the terminal types the file's path, and a paste that is
/// nothing but paths to images is taken as those images.
/// </summary>
public class ImageTokenTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "imgtok-" + Guid.NewGuid().ToString("N"));

    public ImageTokenTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    /// <summary>A PNG of the given size, written where the test can drop it.</summary>
    private string Png(int width, int height, string name = "shot.png")
    {
        var path = Path.Combine(_dir, name);
        using var file = File.Create(path);
        new ImageWriter().WritePng(new byte[width * height * 3], width, height, ColorComponents.RedGreenBlue, file);
        return path;
    }

    private string Jpeg(string name)
    {
        var path = Path.Combine(_dir, name);
        using var file = File.Create(path);
        new ImageWriter().WriteJpg(new byte[8 * 8 * 3], 8, 8, ColorComponents.RedGreenBlue, file, 90);
        return path;
    }

    [Theory]
    [InlineData("'/a/b c.png'", "/a/b c.png")]
    [InlineData("/a/b\\ c.png", "/a/b c.png")]
    [InlineData("\"C:\\x\\y z.png\"", "C:\\x\\y z.png")]
    [InlineData("file:///a/b%20c.png", "/a/b c.png")]
    [InlineData("  '/a/one.png'  ", "/a/one.png")]
    public void SplitPaths_ReadsEachTerminalsForm(string pasted, string path)
    {
        Assert.Equal([path], ImageTokens.SplitPaths(pasted));
    }

    [Fact]
    public void SplitPaths_TwoDroppedFiles_AreTwoPaths()
    {
        Assert.Equal(["/a/one.png", "/b/two two.png"], ImageTokens.SplitPaths("'/a/one.png' '/b/two two.png'"));
    }

    [Theory]
    [InlineData("look at /tmp/a.png")]
    [InlineData("shot.png")]
    [InlineData("/a/b.png\n/c/d.png")]
    [InlineData("'/a/unclosed.png")]
    public void SplitPaths_AnythingElse_IsNotPaths(string pasted)
    {
        Assert.Null(ImageTokens.SplitPaths(pasted));
    }

    /// <summary>The label names the size sent, which a 3000×2000 screenshot is not.</summary>
    [Fact]
    public void AnImagePath_GivesATokenLabelledWithTheSentSize()
    {
        var composer = MainWindow.NewPrompt();

        composer.Paste($"'{Png(3000, 2000)}'");

        var token = Assert.Single(composer.InputSegments, s => s.Token is not null).Token!;
        Assert.Equal("[Image #1 1568×1045]", token.Label);
        Assert.Equal(ImageTokens.Tag, token.Tag);
        var image = ImageTokens.Of(token)!;
        Assert.Equal((1568, 1045), (image.Width, image.Height));
        Assert.NotNull(image.Png);
    }

    [Fact]
    public void ASmallImage_IsSentAtItsOwnSize()
    {
        var composer = MainWindow.NewPrompt();

        composer.Paste(Png(40, 20));

        Assert.Equal("[Image #1 40×20]", composer.InputLabelText);
    }

    [Fact]
    public void TwoDroppedFiles_AreTwoTokens_InOrder()
    {
        var composer = MainWindow.NewPrompt();

        composer.Paste($"'{Png(10, 10, "a.png")}' '{Png(20, 20, "b.png")}'");

        Assert.Equal("[Image #1 10×10][Image #2 20×20]", composer.InputLabelText);
    }

    [Fact]
    public void ANonImageFile_IsText()
    {
        var path = Path.Combine(_dir, "notes.cs");
        File.WriteAllText(path, "class C {}");
        var composer = MainWindow.NewPrompt();

        composer.Paste(path);

        Assert.Equal(path, composer.InputLabelText);
        Assert.DoesNotContain(composer.InputSegments, s => s.Token is not null);
    }

    [Fact]
    public void AMissingPath_IsText()
    {
        var composer = MainWindow.NewPrompt();

        composer.Paste("/nowhere/shot.png");

        Assert.Equal("/nowhere/shot.png", composer.InputLabelText);
    }

    /// <summary>Read by its content, so a JPEG saved as .png is still an image.</summary>
    [Fact]
    public void ARenamedJpeg_IsAnImage()
    {
        var composer = MainWindow.NewPrompt();

        composer.Paste(Jpeg("actually-a-jpeg.png"));

        Assert.Equal("[Image #1 8×8]", composer.InputLabelText);
    }

    /// <summary>A .png that is not an image stays its path, and says why.</summary>
    [Fact]
    public void AFileThatDoesNotDecode_IsTextWithAReason()
    {
        var path = Path.Combine(_dir, "broken.png");
        File.WriteAllText(path, "not an image");
        var said = new List<string>();
        var before = ImageTokens.Refused;
        ImageTokens.Refused = said.Add;
        try
        {
            var composer = MainWindow.NewPrompt();
            composer.Paste(path);

            Assert.Equal(path, composer.InputLabelText);
            Assert.Equal(["can't read broken.png: not a format cxagent reads (PNG, JPEG, BMP, GIF)"], said);
        }
        finally { ImageTokens.Refused = before; }
    }

    /// <summary>The size check comes before the read, so a huge file is never decoded on the UI thread.</summary>
    [Fact]
    public void OverFiftyMegabytes_IsRefusedWithoutBeingRead()
    {
        var path = Path.Combine(_dir, "huge.png");
        using (var file = File.Create(path)) file.SetLength(ImageTokens.MaxFileBytes + 1);
        var read = false;

        var prepared = ImageTokens.Prepare(p => { read = true; return File.ReadAllBytes(p); }, path, out var refusal);

        Assert.Null(prepared);
        Assert.False(read);
        Assert.Equal("huge.png is over 50 MB; pasted as its path", refusal);
    }

    /// <summary>The model gets the label in the text where the image was, and the image beside it.</summary>
    [Fact]
    public void ComposerLine_CarriesTheImage()
    {
        var composer = MainWindow.NewPrompt();
        composer.Input = "what is ";
        composer.Paste(Png(40, 20));

        var line = ComposerLine.Of(composer);

        Assert.Equal("what is [Image #1 40×20]", line.Text);
        Assert.Null(line.Echo);
        Assert.Equal("[Image #1 40×20]", Assert.Single(line.Attachments.Images).Label);
    }

    /// <summary>A click on an image token must not turn it into its label as text, which drops the image.</summary>
    [Fact]
    public void Ctrl_G_BesideAnImage_KeepsTheImage()
    {
        var composer = MainWindow.NewPrompt();
        composer.Paste(Png(40, 20));

        var acted = ImageTokens.ExpandPasteAtCaret(composer, out var image);

        Assert.True(acted);
        Assert.NotNull(image);
        Assert.Single(ComposerLine.Of(composer).Attachments.Images);
    }

    [Fact]
    public void ABtwLineWithOnlyAnImage_GoesToTheAsideWithIt()
    {
        var composer = MainWindow.NewPrompt();
        composer.Input = "/btw what is ";
        composer.Paste(Png(40, 20));

        var line = ComposerLine.Of(composer);

        Assert.True(line.IsBtw(out var question, out var shown));
        Assert.Equal("what is [Image #1 40×20]", question);
        Assert.Equal("what is [Image #1 40×20]", shown);
        Assert.Single(line.Attachments.Images);
    }

    /// <summary>A cancelled queue gives the line back as typed, and its image token still carries its image.</summary>
    [Fact]
    public void AQueuedLineWithAnImage_RestoresItsToken()
    {
        var composer = MainWindow.NewPrompt();
        composer.Input = "check ";
        composer.Paste(Png(40, 20));
        var line = ComposerLine.Of(composer);

        var restored = PromptQueue.Restore(line.Text, [line.Segments], []);

        var token = Assert.Single(restored, s => s.Token is not null).Token;
        Assert.NotNull(ImageTokens.Of(token));
    }

    /// <summary>A model known not to see images refuses one at the paste, and the path stays text.</summary>
    [Fact]
    public void AModelThatCannotSee_RefusesTheImageAtThePaste()
    {
        var said = new List<string>();
        var before = ImageTokens.Refused;
        ImageTokens.Refused = said.Add;
        try
        {
            var composer = MainWindow.NewPrompt();
            var provider = new CxAgent.Core.Llm.Providers.OpenAiCompatibleProvider(new CxAgent.Core.Llm.Providers.OpenAiProviderOptions
            {
                ProviderId = "local", DisplayName = "local", Model = "qwen3", BaseUrl = "http://h/v1", Images = false,
            });
            ImageTokens.For(composer).CannotSee = () => ImageTokens.Refusal(provider);
            var path = Png(10, 10);

            composer.Paste(path);

            Assert.Equal(path, composer.InputLabelText);
            Assert.Equal(["local:qwen3 can't see images"], said);
        }
        finally { ImageTokens.Refused = before; }
    }

    /// <summary>A clipboard's image takes the same road as a dropped file's, and numbers on from it.</summary>
    [Fact]
    public void AClipboardImage_GivesTheSameTokenAsTheFile()
    {
        var composer = MainWindow.NewPrompt();
        var bytes = File.ReadAllBytes(Png(3000, 2000));
        composer.Paste(Png(10, 10, "first.png"));

        var token = ImageTokens.For(composer).FromBytes(bytes, out var refusal);

        Assert.Null(refusal);
        Assert.Equal("[Image #2 1568×1045]", token!.Label);
        Assert.Equal((3000, 2000), (ImageTokens.Of(token)!.OriginalWidth, ImageTokens.Of(token)!.OriginalHeight));
    }

    [Fact]
    public void AClipboardThatIsNotAnImage_IsRefused()
    {
        var token = ImageTokens.For(MainWindow.NewPrompt()).FromBytes([1, 2, 3], out var refusal);

        Assert.Null(token);
        Assert.Equal("can't read the clipboard's image: not a format cxagent reads (PNG, JPEG, BMP, GIF)", refusal);
    }
}
