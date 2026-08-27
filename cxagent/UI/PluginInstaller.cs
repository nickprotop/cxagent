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
public sealed class PluginInstaller(HttpClient? client = null)
{
    private static readonly HttpClient Shared = new();
    private readonly HttpClient _client = client ?? Shared;

    public async Task<InstallResult> InstallAsync(
        CatalogEntry entry, string pluginsFolder, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(entry.DownloadUrl))
            return new InstallResult.Refused($"'{entry.Name}' has no download in the catalog.");

        // NO HASH, NO INSTALL. The catalog carries one for everything the release pipeline builds,
        // so its absence means either a hand-written entry or a stale catalog — and installing on
        // trust is the one thing the published hash exists to avoid.
        if (string.IsNullOrWhiteSpace(entry.Sha256))
            return new InstallResult.Refused(
                $"the catalog carries no checksum for '{entry.Name}', so what arrives cannot be verified.");

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
            payload = await _client.GetByteArrayAsync(entry.DownloadUrl, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return new InstallResult.Refused($"could not download '{entry.Name}': {ex.Message}");
        }

        var actual = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        if (!string.Equals(actual, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            return new InstallResult.HashMismatch(entry.Sha256, actual);

        // EXTRACTED TO A TEMPORARY DIRECTORY FIRST, then moved. A half-extracted plugin directory
        // is a load set whose hash means nothing, and an archive that fails partway through would
        // leave exactly that.
        var staging = Path.Combine(Path.GetTempPath(), "cxagent-install-" + Guid.NewGuid().ToString("N"));
        var destination = Path.Combine(pluginsFolder, entry.Name);

        try
        {
            Directory.CreateDirectory(staging);
            using (var archive = new ZipArchive(new MemoryStream(payload), ZipArchiveMode.Read))
            {
                var root = Path.GetFullPath(staging);
                foreach (var item in archive.Entries)
                {
                    if (string.IsNullOrEmpty(item.Name)) continue;

                    // AN ENTRY THAT ESCAPES IS THE WHOLE ARCHIVE REFUSED, not skipped. A zip
                    // carrying "../x" is not a plugin with one bad file in it; it is an archive
                    // doing something no plugin needs to do.
                    var target = Path.GetFullPath(Path.Combine(staging, item.FullName));
                    if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                        return new InstallResult.Refused(
                            $"'{entry.Name}' contains an entry that would write outside its own "
                          + $"directory ({item.FullName}).");

                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    item.ExtractToFile(target, overwrite: true);
                }
            }

            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
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
