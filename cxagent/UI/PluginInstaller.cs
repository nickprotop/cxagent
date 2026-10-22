using CxAgent.Core.Plugins;
using System.IO.Compression;
using System.Security.Cryptography;

namespace CxAgent.UI;

/// <summary>How an install went.</summary>
public abstract record InstallResult
{
    private InstallResult() { }

    /// <param name="Directory">The plugin's own directory, now holding its files.</param>
    /// <param name="Files">What was written, for an uninstall to remove exactly.</param>
    public sealed record Installed(string Directory, IReadOnlyList<string> Files) : InstallResult;

    /// <param name="Reason">Why nothing was written, in words a user can act on.</param>
    public sealed record Refused(string Reason) : InstallResult;

    /// <param name="Expected">What the catalog said.</param>
    /// <param name="Actual">What arrived.</param>
    public sealed record HashMismatch(string Expected, string Actual) : InstallResult;
}

/// <summary>
/// Downloads a plugin, verifies it against the catalog's hash, and extracts it into a directory of
/// its own.
///
/// <para>INSTALLING IS NOT APPROVING. This writes files and stops. Nothing is loaded, and the load
/// gate asks separately — showing a hash of the whole load set, which is a different question from
/// the one this class answers.</para>
/// </summary>
public sealed partial class PluginInstaller(HttpClient? client = null)
{
    // THE SHAPE A COMMAND NAME TAKES, for the same reason: a name, nothing a path could be made of.
    [System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial System.Text.RegularExpressions.Regex FolderName();

    [System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z0-9_.-]+$")]
    private static partial System.Text.RegularExpressions.Regex FileName();

    private static readonly HttpClient Shared = new();
    private readonly HttpClient _client = client ?? Shared;

    /// <summary>
    /// Whether <paramref name="folder"/> is a plugin's own folder — it holds a sidecar at its top level
    /// and is not marked as a store's — which a store of the same name must not take over. One
    /// definition, so <c>/plugin store add</c> refuses the name the installer would refuse to install into.
    ///
    /// <para>THE TOP LEVEL ONLY, because that is where an installed plugin's sidecar is and the only level
    /// the plain search reads in a plugin's folder. A folder of the store's name holding no plugin hides
    /// nothing, so a store may still use it.</para>
    /// </summary>
    public static bool HoldsUnmarkedPlugin(string folder) =>
        Directory.Exists(folder)
        && !PluginStores.IsStoreFolder(folder)
        && Directory.EnumerateFiles(folder, "*.plugin.json").Any();

    public async Task<InstallResult> InstallAsync(
        CatalogEntry entry, string pluginsFolder, CancellationToken ct)
    {
        // A CATALOG NAME IS A FOLDER NAME, AND ITS FILE A FILE NAME — never paths. The folder the name gives is
        // replaced recursively below, so a name that climbed out of the plugins folder would remove whatever it
        // reached; refused before anything is fetched, written or removed.
        if (!FolderName().IsMatch(entry.Name) || !FileName().IsMatch(entry.File) || entry.File.Trim('.').Length == 0)
            return new InstallResult.Refused(
                $"the catalog entry '{entry.Name}' names a path where a plain name belongs, so it was not installed.");

        // A STORE'S PLUGIN, UNDER ITS STORE'S FOLDER. The store name becomes a folder name too, so it is
        // held to the store-name rule here as well as in config — and a store's entry must be a file the
        // store hosts with its hash: a release source means cxagent's own release, which is no store's.
        if (entry.Store is { } store)
        {
            if (PluginStores.Problem(store, "https://store") is not null)
                return new InstallResult.Refused($"'{store}' is not a store name.");
            if (entry.FromRelease)
                return new InstallResult.Refused("a store's plugin must be a url download with its own sha256.");
            pluginsFolder = Path.Combine(pluginsFolder, store);
        }

        // THIS MACHINE'S FILE: the single download, or for a native plugin the build for this platform
        // — each checked against its own hash below.
        if (entry.DownloadFor(System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier) is not { } download)
            return new InstallResult.Refused(entry.Sources.Count > 0
                ? $"'{entry.Name}' is not available for this machine."
                : $"'{entry.Name}' has no download in the catalog.");

        // NO HASH, NO INSTALL. The catalog carries one for everything the release pipeline builds,
        // so its absence means either a hand-written entry or a stale catalog — and installing on
        // trust is the one thing the published hash exists to avoid.
        if (string.IsNullOrWhiteSpace(download.Sha256))
            return new InstallResult.Refused(
                $"the catalog carries no checksum for '{entry.Name}', so what arrives cannot be verified.");

        // plugins/<name>/ IS ONE NAMESPACE FOR BOTH KINDS OF FOLDER: an official plugin's own, and a store's
        // holding that store's plugins. Neither may land on the other. An official install replaces its
        // destination recursively, so into a store's folder it would delete every plugin installed from
        // that store; a store install marks its folder, so into a plugin's folder it would hide that
        // plugin from every search by name, which skips a store's folder.
        if (entry.Store is null && PluginStores.IsStoreFolder(Path.Combine(pluginsFolder, entry.Name)))
            return new InstallResult.Refused(
                $"{Path.Combine(pluginsFolder, entry.Name)} is store '{entry.Name}''s folder, and installing the "
              + $"official '{entry.Name}' there would delete that store's plugins. Uninstall store "
              + $"'{entry.Name}''s plugins first to install this one.");
        if (entry.Store is { } owner && HoldsUnmarkedPlugin(pluginsFolder))
            return new InstallResult.Refused(
                $"{pluginsFolder} holds an installed plugin, and installing store '{owner}''s plugins into it "
              + $"would hide that plugin. Uninstall it first, or remove store '{owner}' and add it under another name.");

        // A LOOSE COPY SHADOWS A NESTED ONE, because a plugins folder is searched before its
        // subdirectories. Installing beside it would leave the user running the old copy while the
        // dialog described the new one.
        var loose = Path.Combine(pluginsFolder, entry.File);
        if (File.Exists(loose))
            return new InstallResult.Refused(
                $"'{entry.File}' is already installed directly in {pluginsFolder}, where it would "
              + $"shadow this install. Remove it and its sidecar first.");

        byte[] payload;
        try
        {
            payload = await _client.GetByteArrayAsync(download.Url, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return new InstallResult.Refused($"could not download '{entry.Name}': {ex.Message}");
        }

        var actual = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        if (!string.Equals(actual, download.Sha256, StringComparison.OrdinalIgnoreCase))
            return new InstallResult.HashMismatch(download.Sha256, actual);

        // EXTRACTED TO A TEMPORARY DIRECTORY FIRST, then moved. A half-extracted plugin directory
        // is a load set whose hash means nothing, and an archive that fails partway through would
        // leave exactly that.
        //
        // STAGED BESIDE THE DESTINATION, NOT IN THE SYSTEM TEMP DIRECTORY. Directory.Move is
        // rename(2), which cannot cross filesystems: staging under Path.GetTempPath() fails with
        // "Invalid cross-device link" whenever /tmp is a different mount from the config directory,
        // which on Linux it usually is — /tmp a tmpfs and $HOME a real disk. Sharing the parent
        // guarantees one filesystem, so the move stays the atomic swap this whole dance exists to
        // get. A leading dot keeps a half-written directory out of the plugin search, which reads
        // this folder's immediate subdirectories.
        Directory.CreateDirectory(pluginsFolder);
        var staging = Path.Combine(pluginsFolder, ".cxagent-install-" + Guid.NewGuid().ToString("N"));
        var destination = Path.Combine(pluginsFolder, entry.Name);

        try
        {
            Directory.CreateDirectory(staging);

            // SafeZip REFUSES THE WHOLE ARCHIVE on an entry that escapes — the same check verify makes.
            if (SafeZip.ExtractTo(payload, staging) is { } escaping)
                return new InstallResult.Refused(
                    $"'{entry.Name}' contains an entry that would write outside its own "
                  + $"directory ({escaping}).");

            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            // THE MARK THAT MAKES THIS A STORE'S FOLDER to every search: its plugins are found as
            // store/name and never by a bare file name. Written before the move, so the plugin never
            // sits in an unmarked folder; and on install rather than on `store add`, so a removed
            // store's plugins stay findable for as long as they are installed.
            if (entry.Store is not null)
                File.WriteAllText(Path.Combine(pluginsFolder, PluginStores.Marker), "");

            Directory.Move(staging, destination);

            var written = Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories)
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();

            return new InstallResult.Installed(destination, written);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new InstallResult.Refused($"could not install '{entry.Name}': {ex.Message}");
        }
        finally
        {
            if (Directory.Exists(staging))
                try { Directory.Delete(staging, recursive: true); } catch (IOException) { }
        }
    }
}
