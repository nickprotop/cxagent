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
    [InlineData("\\\\server\\share\\x.png", "\\\\server\\share\\x.png")]
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
        Assert.NotNull(image.Bytes);
    }

    /// <summary>
    /// A photo whose PNG would pass Anthropic's 5 MB, once base64, goes as a JPEG. Noise is the worst
    /// case PNG meets: nothing to compress.
    /// </summary>
    [Fact]
    public void APhotoTooLargeAsPng_IsSentAsAJpeg()
    {
        var noise = new byte[1568 * 1107 * 3];
        new Random(7).NextBytes(noise);
        using var ms = new MemoryStream();
        new ImageWriter().WritePng(noise, 1568, 1107, ColorComponents.RedGreenBlue, ms);

        var prepared = ImageTokens.Encode(ms.ToArray(), "photo.png", out _)!;

        Assert.Equal("image/jpeg", prepared.MediaType);
        Assert.Equal([0xFF, 0xD8], prepared.Bytes[..2]);
        Assert.True(prepared.Bytes.Length <= ImageTokens.MaxSentBytes);
    }

    /// <summary>
    /// A shrink averages what each sent pixel covers: a one-pixel checkerboard halved is flat grey. Sampled
    /// instead, it comes out all black or all white, which is how a screenshot's thin strokes vanish.
    /// </summary>
    [Fact]
    public void AShrink_AveragesWhatEachPixelCovers()
    {
        const int width = ImageTokens.MaxEdge * 2, height = 4;
        var checker = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                if ((x + y) % 2 == 0) checker.AsSpan((y * width + x) * 3, 3).Fill(255);
        using var ms = new MemoryStream();
        new ImageWriter().WritePng(checker, width, height, ColorComponents.RedGreenBlue, ms);

        var sent = SharpConsoleUI.Imaging.PixelBuffer.FromStream(new MemoryStream(ImageTokens.Encode(ms.ToArray(), "c.png", out _)!.Bytes));

        Assert.Equal((ImageTokens.MaxEdge, 2), (sent.Width, sent.Height));
        for (var x = 0; x < sent.Width; x++)
            Assert.InRange(sent.GetPixel(x, 0).R, 120, 135);
    }

    private static byte[] NoisePng(int size)
    {
        var noise = new byte[size * size * 3];
        new Random(11).NextBytes(noise);
        using var ms = new MemoryStream();
        new ImageWriter().WritePng(noise, size, size, ColorComponents.RedGreenBlue, ms);
        return ms.ToArray();
    }

    private static int JpegLength(byte[] png, int quality)
    {
        var pixels = SharpConsoleUI.Imaging.PixelBuffer.FromStream(new MemoryStream(png));
        var rgb = new byte[pixels.Width * pixels.Height * 3];
        for (var y = 0; y < pixels.Height; y++)
            for (var x = 0; x < pixels.Width; x++)
            {
                var p = pixels.GetPixel(x, y);
                (rgb[(y * pixels.Width + x) * 3], rgb[(y * pixels.Width + x) * 3 + 1], rgb[(y * pixels.Width + x) * 3 + 2]) = (p.R, p.G, p.B);
            }
        using var ms = new MemoryStream();
        new ImageWriter().WriteJpg(rgb, pixels.Width, pixels.Height, ColorComponents.RedGreenBlue, ms, quality);
        return (int)ms.Length;
    }

    /// <summary>A JPEG that is still over the limit at its first quality is written lower until it fits.</summary>
    [Fact]
    public void AJpegStillTooLarge_IsWrittenAtALowerQuality()
    {
        var png = NoisePng(400);
        var limit = JpegLength(png, 85) - 1;

        var prepared = ImageTokens.Encode(png, "noise.png", out var refusal, maxBytes: limit)!;

        Assert.Null(refusal);
        Assert.Equal("image/jpeg", prepared.MediaType);
        Assert.True(prepared.Bytes.Length <= limit, $"{prepared.Bytes.Length} bytes over a {limit}-byte limit");
    }

    /// <summary>An image no quality brings under the limit is refused, and says so, rather than sent to be refused.</summary>
    [Fact]
    public void AnImageNoQualityFits_IsRefusedWithAReason()
    {
        var prepared = ImageTokens.Encode(NoisePng(400), "noise.png", out var refusal, maxBytes: 1000);

        Assert.Null(prepared);
        Assert.Contains("too large", refusal);
    }

    /// <summary>A screenshot compresses well, and stays a PNG: JPEG blurs the edges of text.</summary>
    [Fact]
    public void AScreenshot_StaysAPng()
    {
        using var ms = new MemoryStream();
        new ImageWriter().WritePng(new byte[1568 * 1107 * 3], 1568, 1107, ColorComponents.RedGreenBlue, ms);

        Assert.Equal("image/png", ImageTokens.Encode(ms.ToArray(), "shot.png", out _)!.MediaType);
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

    /// <summary>
    /// kitty, dropping outside a shell prompt, pastes the bare path: no quotes round a name with spaces.
    /// </summary>
    [Fact]
    public void AnUnquotedPathWithSpaces_IsAnImage()
    {
        var composer = MainWindow.NewPrompt();

        composer.Paste(Png(10, 10, "Screenshot from 2026-11-04 07-23-41.png"));

        Assert.Equal("[Image #1 10×10]", composer.InputLabelText);
    }

    /// <summary>kitty's drop of several files outside a shell prompt: one bare path to a line.</summary>
    [Fact]
    public void BarePathsOnePerLine_AreTwoTokens_InOrder()
    {
        var composer = MainWindow.NewPrompt();

        composer.Paste(Png(10, 10, "a one.png") + "\n" + Png(20, 20, "b two.png"));

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

    /// <summary>A file that starts like a PNG and does not decode — cut short, corrupt — stays its path, and says why.</summary>
    [Fact]
    public void AFileThatDoesNotDecode_IsTextWithAReason()
    {
        var path = Path.Combine(_dir, "broken.png");
        File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3]);
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
    /// <summary>Ctrl+G beside an image opens it and leaves the caret where it was.</summary>
    [Fact]
    public void Ctrl_G_BesideAnImage_LeavesTheCaretWhereItWas()
    {
        var composer = MainWindow.NewPrompt();
        composer.Paste(Png(40, 20));
        composer.ProcessKey(new ConsoleKeyInfo('\0', ConsoleKey.Home, false, false, false));

        ImageTokens.ExpandPasteAtCaret(composer, out _);
        composer.ProcessKey(new ConsoleKeyInfo('x', ConsoleKey.X, false, false, false));

        Assert.Equal("x[Image #1 40×20]", composer.InputLabelText);
    }

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

    /// <summary>An endpoint whose /props answers only when the test lets it.</summary>
    private sealed class AnswersWhenLet(string props) : HttpMessageHandler
    {
        public TaskCompletionSource Let { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Let.Task.WaitAsync(ct);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(props) };
        }
    }

    /// <summary>
    /// The first image after a start or a /model goes before the probe has answered. When it then says
    /// no, the user hears it while the line is still in front of them, and the token stays: the image
    /// goes as its placeholder.
    /// </summary>
    [Fact]
    public async Task AnAnswerAfterThePaste_ThatTheModelCannotSee_IsSaid()
    {
        var said = new List<string>();
        var heard = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new AnswersWhenLet("""{"modalities":{"vision":false}}""");
        var provider = new CxAgent.Core.Llm.Providers.OpenAiCompatibleProvider(new CxAgent.Core.Llm.Providers.OpenAiProviderOptions
        {
            ProviderId = "local", DisplayName = "local", Model = "qwen3", BaseUrl = "http://h/v1", Client = new HttpClient(server),
        });
        var composer = MainWindow.NewPrompt();
        ImageTokens.For(composer).CannotSee = () => ImageTokens.Refusal(provider, text => { said.Add(text); heard.TrySetResult(); });

        composer.Paste(Png(10, 10));
        Assert.Equal("[Image #1 10×10]", composer.InputLabelText);
        Assert.Empty(said);

        server.Let.SetResult();
        await heard.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["local:qwen3 can't see images; the image will go as its placeholder"], said);
        Assert.Equal("[Image #1 10×10]", composer.InputLabelText);
    }

    /// <summary>Several images pasted before the answer comes hear it once: it is one answer about one model.</summary>
    [Fact]
    public async Task ALateAnswer_IsSaidOnce_HoweverManyImagesWaitedForIt()
    {
        var said = new List<string>();
        var server = new AnswersWhenLet("""{"modalities":{"vision":false}}""");
        var provider = new CxAgent.Core.Llm.Providers.OpenAiCompatibleProvider(new CxAgent.Core.Llm.Providers.OpenAiProviderOptions
        {
            ProviderId = "local", DisplayName = "local", Model = "qwen3", BaseUrl = "http://h/v1", Client = new HttpClient(server),
        });
        var composer = MainWindow.NewPrompt();
        ImageTokens.For(composer).CannotSee = () => ImageTokens.Refusal(provider, text => { lock (said) said.Add(text); });

        composer.Paste(Png(10, 10, "one.png"));
        composer.Paste(Png(10, 10, "two.png"));
        composer.Paste(Png(10, 10, "three.png"));
        server.Let.SetResult();
        Assert.True(await Poll.Until(() => { lock (said) return said.Count > 0; }), "no answer was said");
        await Task.Delay(200);

        lock (said) Assert.Single(said);
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

    /// <summary>
    /// Spreadsheet cells and slides copy as text and a picture of it together. Ctrl+V pastes the text,
    /// and leaves the key to the composer's own paste; the image is not even read.
    /// </summary>
    [Fact]
    public void AClipboardWithTextAndAnImage_PastesTheText()
    {
        var composer = MainWindow.NewPrompt();
        var imageRead = false;

        var taken = ImageTokens.For(composer).PasteClipboard(
            () => { imageRead = true; return File.ReadAllBytes(Png(10, 10)); }, sayWhenEmpty: false, hasText: () => true);

        Assert.False(taken);
        Assert.False(imageRead);
        Assert.Equal("", composer.InputLabelText);
    }

    /// <summary>With no text beside it, the image is pasted.</summary>
    [Fact]
    public void AClipboardWithOnlyAnImage_PastesTheImage()
    {
        var composer = MainWindow.NewPrompt();
        var bytes = File.ReadAllBytes(Png(10, 10));

        Assert.True(ImageTokens.For(composer).PasteClipboard(() => bytes, sayWhenEmpty: false, hasText: () => false));
        Assert.Equal("[Image #1 10×10]", composer.InputLabelText);
    }

    [Fact]
    public void AClipboardThatIsNotAnImage_IsRefused()
    {
        var token = ImageTokens.For(MainWindow.NewPrompt()).FromBytes([1, 2, 3], out var refusal);

        Assert.Null(token);
        Assert.Equal("can't read the clipboard's image: not a format cxagent reads (PNG, JPEG, BMP, GIF)", refusal);
    }

    /// <summary>
    /// Pasting a source file's path is routine in a coding agent: it stays text, says nothing, and the file
    /// is not read — even with a model that cannot see images, whose refusal is only for images.
    /// </summary>
    [Fact]
    public void ANonImagePath_IsTextAndSilent_EvenForAModelThatCannotSee()
    {
        var path = Path.Combine(_dir, "Program.cs");
        File.WriteAllText(path, "class Program {}");
        var said = new List<string>();
        var before = ImageTokens.Refused;
        ImageTokens.Refused = said.Add;
        try
        {
            var composer = MainWindow.NewPrompt();
            ImageTokens.For(composer).CannotSee = () => "local:qwen can't see images";

            composer.Paste(path);

            Assert.Equal(path, composer.InputLabelText);
            Assert.Empty(said);
        }
        finally { ImageTokens.Refused = before; }
    }
}
