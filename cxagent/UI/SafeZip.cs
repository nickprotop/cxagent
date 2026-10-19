using System.IO.Compression;

namespace CxAgent.UI;

/// <summary>
/// Extracts a plugin zip, refusing one that reaches outside its folder — the one implementation, used
/// by install and by <c>cxagent plugin verify</c>, so the two cannot disagree on what is safe.
/// </summary>
internal static class SafeZip
{
    /// <summary>
    /// Extracts <paramref name="zip"/> into <paramref name="folder"/>; returns the name of the first
    /// entry that would write outside it, or null when every entry stays inside.
    ///
    /// <para>AN ENTRY THAT ESCAPES IS THE WHOLE ARCHIVE REFUSED, not skipped. A zip carrying "../x" is
    /// not a plugin with one bad file in it; it is an archive doing something no plugin needs to do.</para>
    /// </summary>
    public static string? ExtractTo(byte[] zip, string folder)
    {
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        var root = Path.GetFullPath(folder);
        foreach (var item in archive.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(folder, item.FullName));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return item.FullName;
            if (string.IsNullOrEmpty(item.Name)) continue;

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            item.ExtractToFile(target, overwrite: true);
        }

        return null;
    }
}
