namespace CxAgent.Plugins.RepoMap;

/// <summary>Where a path really is, and whether it lies inside a folder.</summary>
public static class Paths
{
    private static readonly StringComparison Comparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Whether <paramref name="path"/> is <paramref name="folder"/> or inside it. Both real paths.</summary>
    public static bool Contains(string folder, string path)
    {
        var withSeparator = folder.EndsWith(Path.DirectorySeparatorChar) ? folder : folder + Path.DirectorySeparatorChar;
        return path.Equals(folder, Comparison) || path.StartsWith(withSeparator, Comparison);
    }

    /// <summary>
    /// The path a path really is, every symlink along it followed.
    ///
    /// <para><see cref="Path.GetFullPath(string)"/> only collapses <c>..</c>; a link inside a folder
    /// that points out of it would pass a check on the spelling and be read wherever it points.</para>
    /// </summary>
    /// <param name="known">Folders already resolved, so a tree of files sharing parents resolves
    /// each parent once.</param>
    public static string Real(string path, Dictionary<string, string>? known = null)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (known is not null && known.TryGetValue(full, out var cached)) return cached;

        var parent = Path.GetDirectoryName(full);
        string current;
        if (parent is null) current = full;
        else
        {
            current = Path.Combine(Real(parent, known), Path.GetFileName(full));
            try
            {
                if (new FileInfo(current).ResolveLinkTarget(returnFinalTarget: true) is { } target)
                    current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target.FullName));
            }
            catch (IOException)
            {
                // A broken link resolves to nothing; the spelling stands, and nothing is there to read.
            }
        }

        if (known is not null) known[full] = current;
        return current;
    }
}
