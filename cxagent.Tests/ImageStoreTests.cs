using System.Security.Cryptography;
using CxAgent.Core.Models;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// An attached image is kept in the folder of the agent that saves the context carrying it, so every
/// session folder holds what its own context needs and a folder deleted takes its images with it.
/// </summary>
public class ImageStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "imgstore-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;
    private readonly FolderSessionStore _store;

    public ImageStoreTests()
    {
        Directory.CreateDirectory(_dir);
        _paths = new AppPaths(_dir);
        _paths.EnsureCreated();
        _store = new FolderSessionStore(_paths);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    internal static AttachedImage Image(byte[] png, string label = "[Image #1 4×2]") =>
        new(label, Convert.ToHexStringLower(SHA256.HashData(png)), 4, 2) { Bytes = png };

    private static ChatMessage Said(string text, params AttachedImage[] images) =>
        new() { Role = "user", Content = text, Images = images };

    private void Save(string agent, IReadOnlyList<ChatMessage> context) =>
        _store.SaveTurn(new FolderSessionStore.ResumeTurn(agent, context, 0, 0));

    private string ImageFile(string agent, AttachedImage image) =>
        Path.Combine(new SessionFolder(_paths, agent).ImagesDir, image.Hash + ".png");

    /// <summary>A JPEG is kept as one, under its own extension, and read back with its type.</summary>
    [Fact]
    public void AJpeg_IsStoredAsJpg_AndLoadsAsOne()
    {
        var image = Image([0xFF, 0xD8, 0xFF]) with { MediaType = "image/jpeg" };

        Save("agent-j", [Said("look", image)]);

        var file = Assert.Single(Directory.GetFiles(new SessionFolder(_paths, "agent-j").ImagesDir));
        Assert.Equal(image.Hash + ".jpg", Path.GetFileName(file));
        var loaded = _store.LoadById("agent-j")!.Context.SelectMany(m => m.Images ?? []).Single();
        Assert.Equal("image/jpeg", loaded.MediaType);
        Assert.Equal([0xFF, 0xD8, 0xFF], loaded.Bytes);
    }

    /// <summary>An image the SDK was handed as GIF or WebP keeps its own extension, on disk and in an export.</summary>
    [Theory]
    [InlineData("image/png", ".png")]
    [InlineData("image/jpeg", ".jpg")]
    [InlineData("image/gif", ".gif")]
    [InlineData("image/webp", ".webp")]
    [InlineData("image/x-unknown", ".png")]
    public void FileName_TakesTheExtensionOfTheType(string mediaType, string extension)
    {
        Assert.Equal("h" + extension, new AttachedImage("[Image #1 4×2]", "h", 4, 2) { MediaType = mediaType }.FileName);
    }

    [Fact]
    public void SaveTurn_WritesEachImageOnce_ByHash()
    {
        var image = Image([1, 2, 3]);

        Save("agent-a", [Said("look", image), Said("again", image)]);

        var files = Directory.GetFiles(new SessionFolder(_paths, "agent-a").ImagesDir);
        Assert.Equal([ImageFile("agent-a", image)], files);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(files[0]));
    }

    /// <summary>
    /// A resume and a re-wire both hand the context to a new agent, whose folder is a new one. Its first
    /// save must carry the images over, or deleting the older folder would take them from a live session.
    /// </summary>
    [Fact]
    public void SaveTurn_IntoANewAgentFolder_WritesTheImagesItCarries()
    {
        var image = Image([9, 8, 7]);
        Save("agent-a", [Said("look", image)]);

        var loaded = _store.LoadById("agent-a")!.Context;
        Save("agent-b", loaded);

        Assert.Equal([9, 8, 7], File.ReadAllBytes(ImageFile("agent-b", image)));
    }

    [Fact]
    public void LoadById_FillsBytesFromTheFolder()
    {
        var image = Image([5, 5, 5]);
        Save("agent-a", [Said("look", image)]);

        var loaded = _store.LoadById("agent-a")!.Context[0].Images!.Single();

        Assert.Equal(image with { Bytes = null }, loaded with { Bytes = null });
        Assert.Equal([5, 5, 5], loaded.Bytes);
    }

    [Fact]
    public void LoadById_AMissingFile_LeavesBytesNull()
    {
        var image = Image([4, 4]);
        Save("agent-a", [Said("look", image)]);
        File.Delete(ImageFile("agent-a", image));

        var loaded = _store.LoadById("agent-a")!.Context[0].Images!.Single();

        Assert.Null(loaded.Bytes);
    }

    /// <summary>The bytes are in their own file, never a second copy inside the context.</summary>
    [Fact]
    public void TheContextFile_HoldsTheReferenceNotTheBytes()
    {
        Save("agent-a", [Said("look", Image(new byte[4096]))]);

        var context = File.ReadAllText(new SessionFolder(_paths, "agent-a").ContextPath);

        Assert.DoesNotContain("AAAAAAAA", context);
        Assert.Contains("\"Hash\"", context);
    }

    /// <summary>
    /// What is worked out from a message is not saved with it: a size, a weight, a placeholder. Saved, it
    /// is written into every message of every save, and read back as a value nothing reads.
    /// </summary>
    [Fact]
    public void TheContextFile_HoldsNothingComputed()
    {
        Save("agent-a", [Said("look", Image([1]))]);

        var context = File.ReadAllText(new SessionFolder(_paths, "agent-a").ContextPath);

        Assert.DoesNotContain("SizeChars", context);
        Assert.DoesNotContain("WeightChars", context);
        Assert.DoesNotContain("Placeholder", context);
        Assert.DoesNotContain("Unavailable", context);
    }
}
