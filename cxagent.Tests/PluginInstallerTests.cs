using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using CxAgent.Core.Plugins;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Downloading a plugin, verifying it, and putting it where the loader will find it.
///
/// <para>INSTALLING IS NOT APPROVING. Every test here ends with files on disk and nothing loaded —
/// the load gate asks separately, showing a hash of exactly what it is about to run.</para>
/// </summary>
public class PluginInstallerTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "plugin-install-" + Guid.NewGuid().ToString("N"));

    public PluginInstallerTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    /// <summary>A zip holding the two files a plugin ships, and its SHA-256.</summary>
    private static (byte[] Zip, string Sha256) MakeZip()
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var w = new StreamWriter(archive.CreateEntry("demo.dll").Open()))
                w.Write("not a real assembly");
            using (var w = new StreamWriter(archive.CreateEntry("demo.plugin.json").Open()))
                w.Write("""{"pluginContract":2,"name":"demo","version":"1.0.0","spawns":false,"tools":[]}""");
        }

        var bytes = buffer.ToArray();
        return (bytes, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    private sealed class Serves(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent(body) });
    }

    private static CatalogEntry Entry(string sha) => new(
        "demo", "Demo", "1.0.0", "A demo.", "someone", "MIT", "https://example",
        "code-intelligence", "demo.dll", "managed", 2, [],
        "https://example/demo.zip", sha, null, null);

    /// <summary>
    /// A DIRECTORY OF ITS OWN. The load-set design turns on this: a plugin's hash covers its folder,
    /// so sharing one means neither plugin is separately identifiable.
    /// </summary>
    [Fact]
    public async Task AVerifiedPluginLandsInItsOwnDirectory()
    {
        var (zip, sha) = MakeZip();
        var installer = new PluginInstaller(new HttpClient(new Serves(zip)));

        var result = await installer.InstallAsync(Entry(sha), _dir, CancellationToken.None);

        var installed = Assert.IsType<InstallResult.Installed>(result);
        Assert.Equal(Path.Combine(_dir, "demo"), installed.Directory);
        Assert.True(File.Exists(Path.Combine(_dir, "demo", "demo.dll")));
        Assert.True(File.Exists(Path.Combine(_dir, "demo", "demo.plugin.json")));
        Assert.Equal(2, installed.Files.Count);
    }

    /// <summary>
    /// A MISMATCH LEAVES NOTHING BEHIND. The catalog and the release are separate origins, so a
    /// disagreement means one of them is wrong and neither is trustworthy until someone finds out
    /// which — writing the bytes anyway would install exactly what could not be vouched for.
    /// </summary>
    [Fact]
    public async Task AHashMismatchAbortsAndWritesNothing()
    {
        var (zip, _) = MakeZip();
        var installer = new PluginInstaller(new HttpClient(new Serves(zip)));

        var result = await installer.InstallAsync(
            Entry("0000000000000000000000000000000000000000000000000000000000000000"),
            _dir, CancellationToken.None);

        var mismatch = Assert.IsType<InstallResult.HashMismatch>(result);
        Assert.Equal(64, mismatch.Actual.Length);
        Assert.False(Directory.Exists(Path.Combine(_dir, "demo")),
            "a failed verification must leave no directory behind.");
    }

    /// <summary>
    /// AN ENTRY WITH NO HASH CANNOT BE VERIFIED, so it is refused rather than installed on trust.
    /// The catalog carries one for every plugin the release pipeline builds.
    /// </summary>
    [Fact]
    public async Task AnEntryWithNoHashIsRefused()
    {
        var (zip, _) = MakeZip();
        var installer = new PluginInstaller(new HttpClient(new Serves(zip)));

        var result = await installer.InstallAsync(Entry(null!) with { Sha256 = null }, _dir, CancellationToken.None);

        var refused = Assert.IsType<InstallResult.Refused>(result);
        Assert.Contains("checksum", refused.Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// AN EXISTING LOOSE COPY WINS THE SEARCH, so installing beside it would leave the user running
    /// one copy and reading about another. Refused, naming what to do.
    /// </summary>
    [Fact]
    public async Task ALooseCopyOfTheSamePluginRefusesTheInstall()
    {
        File.WriteAllText(Path.Combine(_dir, "demo.dll"), "the loose copy");
        var (zip, sha) = MakeZip();
        var installer = new PluginInstaller(new HttpClient(new Serves(zip)));

        var result = await installer.InstallAsync(Entry(sha), _dir, CancellationToken.None);

        var refused = Assert.IsType<InstallResult.Refused>(result);
        Assert.Contains("demo.dll", refused.Reason);
    }

    /// <summary>
    /// A ZIP THAT ESCAPES ITS DESTINATION IS REFUSED. An entry named "../../x" is the classic
    /// archive traversal, and an installer that writes where the archive says would write outside
    /// the plugins folder entirely.
    /// </summary>
    [Fact]
    public async Task AnArchiveEntryThatEscapesIsRefused()
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            using (var w = new StreamWriter(archive.CreateEntry("../escaped.dll").Open()))
                w.Write("elsewhere");

        var bytes = buffer.ToArray();
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var installer = new PluginInstaller(new HttpClient(new Serves(bytes)));

        var result = await installer.InstallAsync(Entry(sha), _dir, CancellationToken.None);

        Assert.IsType<InstallResult.Refused>(result);
        Assert.False(File.Exists(Path.Combine(_dir, "..", "escaped.dll")));
    }
}
