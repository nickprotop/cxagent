using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using CxAgent.Core.Commands;
using CxAgent.Core.Permissions;
using CxAgent.Core.Storage;
using CxAgent.UI;
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

    /// <summary>
    /// THE DOWNLOAD QUESTION REACHES THE USER. The manager calls the gate directly rather than
    /// through PermissionGatedExecutor, so it stamps its own request with the session's policy —
    /// and a request arriving unstamped is refused by PermissionDecider before any prompt renders.
    ///
    /// <para>WHY THIS TEST LOOKS INDIRECT. AskDownloadAsync is private to the dialog and needs a
    /// live session and window, so what is pinned here is the invariant it must satisfy: the same
    /// PermissionKind.Http request, stamped the same way, survives the REAL decider and reaches the
    /// prompt. Unstamped, the prompt is never called and the outcome is a denial indistinguishable
    /// from the user saying no — which is exactly how this shipped broken while every unit test
    /// passed.</para>
    ///
    /// <para>WithPrompt, NOT ForTesting: ForTesting sets StampForTesting, which patches the missing
    /// policy and hides the defect. See McpToolsetTests for the same trap in the MCP path.</para>
    /// </summary>
    [Fact]
    public async Task TheDownloadQuestion_StampedAsTheManagerStampsIt_ReachesThePrompt()
    {
        var rules = new PermissionRulesStore(new AppPaths(_dir));
        var asked = 0;
        var notices = new List<Message>();
        var gate = PermissionDecider.WithPrompt(rules, notices.Add,
            (_, _, _) => { asked++; return Task.FromResult(PermissionChoice.Once); });

        var request = new PermissionRequest(PermissionKind.Http,
            "download 'calculator' 1.0.0 from https://example.invalid/calculator.zip",
            AlwaysRule: null)
        { Policy = new PermissionPolicy(_dir, rules, EditMode.AcceptEdits) };

        var outcome = await gate.RequestAsync(request, CancellationToken.None);

        Assert.True(outcome.Allowed);
        Assert.Equal(1, asked);
        Assert.DoesNotContain(notices, n => n.Text.Contains("no session policy"));
    }

    /// <summary>
    /// THE SAME REQUEST UNSTAMPED IS REFUSED WITHOUT ASKING — the production defect, pinned so a
    /// caller that stops stamping fails here rather than silently in a user's terminal.
    /// </summary>
    [Fact]
    public async Task TheDownloadQuestion_Unstamped_IsRefusedWithoutAsking()
    {
        var rules = new PermissionRulesStore(new AppPaths(_dir));
        var asked = 0;
        var notices = new List<Message>();
        var gate = PermissionDecider.WithPrompt(rules, notices.Add,
            (_, _, _) => { asked++; return Task.FromResult(PermissionChoice.Once); });

        var outcome = await gate.RequestAsync(
            new PermissionRequest(PermissionKind.Http, "download 'calculator' 1.0.0",
                AlwaysRule: null),
            CancellationToken.None);

        Assert.False(outcome.Allowed);
        Assert.Equal(0, asked);
        Assert.Contains(notices, n => n.Text.Contains("no session policy"));
    }

    /// <summary>The repository root, found by walking up from the test binary until plugins.json is
    /// there — the same shape PluginCatalogTests uses.</summary>
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir, "plugins", "plugins.json"))) return dir;
            dir = Path.GetDirectoryName(dir);
        }

        throw new InvalidOperationException(
            $"plugins/plugins.json not found walking up from '{AppContext.BaseDirectory}'.");
    }

    /// <summary>
    /// THE MANAGER STILL STAMPS ITS DOWNLOAD REQUEST. The two tests above pin the decider's half of
    /// the contract; this pins the caller's, which is the half that actually broke — the dialog
    /// asked the gate without a policy and every download was refused before the user saw anything.
    ///
    /// <para>READ FROM SOURCE, because AskDownloadAsync is private to a UI class that needs a live
    /// session and window to construct. The alternative was no check at all on the one line whose
    /// absence shipped the bug: a source assertion is coarse, but it fails the moment someone
    /// deletes the stamp, and it names what to put back.</para>
    /// </summary>
    [Fact]
    public void TheManagersDownloadRequest_CarriesTheSessionsPolicy()
    {
        var source = File.ReadAllText(
            Path.Combine(RepoRoot(), "cxagent", "UI", "PluginManagerDialog.cs"));

        var call = source.IndexOf("PermissionKind.Http", StringComparison.Ordinal);
        Assert.True(call >= 0, "PluginManagerDialog no longer asks for PermissionKind.Http.");

        // MATCHED AS A PATTERN, NOT A LITERAL. What must hold is that the request carries a
        // Policy taken from the session; the field's name is not the contract, and pinning it
        // verbatim would fail this test on a rename that broke nothing — a false alarm is how a
        // check like this stops being believed.
        var window = source[call..Math.Min(source.Length, call + 400)];
        Assert.True(
            System.Text.RegularExpressions.Regex.IsMatch(window, @"Policy\s*=\s*\w*[Ss]ession\??\.Policy"),
            "The manager's download request must be stamped with the session's policy "
          + "({ Policy = _session.Policy }); unstamped, PermissionDecider refuses it and the "
          + "download question never reaches the user.");
    }

    /// <summary>
    /// INSTALLING ONTO A DIFFERENT FILESYSTEM FROM THE SYSTEM TEMP DIRECTORY. This is the ordinary
    /// case on Linux — /tmp is commonly a tmpfs while $HOME is a real disk — and it was broken:
    /// staging under Path.GetTempPath() and finishing with Directory.Move meant rename(2) across
    /// devices, which fails with "Invalid cross-device link". Every other test here passes because
    /// its destination is itself under the system temp directory, so the move never crosses
    /// anything, and neither did any manual test whose config directory was a scratch path in /tmp.
    ///
    /// <para>The install now stages beside its destination, so the two always share a filesystem.
    /// Where no second filesystem exists this asserts nothing and says so — a vacuous pass on a
    /// machine that cannot reproduce the bug is worth less than an honest no-op.</para>
    /// </summary>
    [Fact]
    public async Task InstallingOntoAnotherFilesystemSucceeds()
    {
        var other = SecondFilesystemDir();
        if (other is null) return;

        var target = Path.Combine(other, "plugins-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (zip, sha) = MakeZip();
            var installer = new PluginInstaller(new HttpClient(new Serves(zip)));

            var result = await installer.InstallAsync(Entry(sha), target, CancellationToken.None);

            Assert.IsType<InstallResult.Installed>(result);
            Assert.True(File.Exists(Path.Combine(target, "demo", "demo.dll")),
                $"expected the plugin at {target}/demo — install returned: {result}");
        }
        finally
        {
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        }
    }

    /// <summary>
    /// A writable directory on a filesystem OTHER than the one holding the system temp directory,
    /// or null when this machine has none. /dev/shm and $HOME are the candidates on an ordinary
    /// Linux box; both are skipped unless a probe directory can actually be created, because
    /// /dev/shm can be mounted read-only and discovering that through an install failure would
    /// blame the installer for the mount.
    /// </summary>
    private static string? SecondFilesystemDir()
    {
        if (!OperatingSystem.IsLinux()) return null;

        var tempDevice = DeviceOf(Path.GetTempPath());
        if (tempDevice is null) return null;

        foreach (var candidate in new[]
                 {
                     "/dev/shm",
                     Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                 })
        {
            if (string.IsNullOrEmpty(candidate) || !Directory.Exists(candidate)) continue;
            if (DeviceOf(candidate) is not { } device || device == tempDevice) continue;

            var probe = Path.Combine(candidate, "cxagent-fs-probe-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(probe);
                Directory.Delete(probe);
                return candidate;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        return null;
    }

    /// <summary>The st_dev of a path, read through `stat`, or null if it cannot be determined.
    /// .NET exposes no device id, and DriveInfo reports the root volume rather than the mount a
    /// path actually sits on — which would call /tmp and $HOME the same filesystem.</summary>
    private static string? DeviceOf(string path)
    {
        try
        {
            using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "stat",
                ArgumentList = { "-c", "%d", path },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (proc is null) return null;
            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(5000);
            return proc.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}
