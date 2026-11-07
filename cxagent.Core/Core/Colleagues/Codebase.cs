using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace CxAgent.Core.Colleagues;

/// <summary>
/// What counts as one codebase for a colleague's experience: a git repository — every worktree and subfolder of it the
/// same one — or, outside git, the folder itself.
///
/// <para>THE REPOSITORY, NOT THE FOLDER: a colleague that worked on a library from its main checkout and later from a
/// worktree learned about one codebase, and two experience files for it would split what it knows in half.</para>
/// </summary>
public static partial class Codebase
{
    /// <summary>The root <paramref name="folder"/> belongs to: its repository's main checkout, or itself.</summary>
    public static string Root(string folder)
    {
        var start = RealPath(folder);
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            var dotGit = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(dotGit)) return Trim(dir.FullName);
            if (File.Exists(dotGit)) return WorktreeRoot(dotGit) ?? Trim(dir.FullName);
        }
        return start;
    }

    /// <summary>
    /// The key a codebase's experience file is named by: the root's own name, readable to a person browsing the
    /// library, and a hash of its whole path, so two repositories of one name stay apart.
    /// </summary>
    public static string Key(string folder)
    {
        var root = Root(folder);
        var key = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? root.ToLowerInvariant() : root;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..8].ToLowerInvariant();
        var name = SafeChars().Replace(Path.GetFileName(root), "-").Trim('-');
        return name.Length == 0 ? hash : $"{name}-{hash}";
    }

    /// <summary>
    /// The main checkout a worktree's <c>.git</c> file leads to: <c>gitdir:</c> names the worktree's own git folder,
    /// whose <c>commondir</c> names the repository's. Null when the file is not a worktree's — a submodule's has no
    /// <c>commondir</c>, and a submodule is a codebase of its own.
    /// </summary>
    private static string? WorktreeRoot(string dotGitFile)
    {
        try
        {
            var line = File.ReadLines(dotGitFile).FirstOrDefault() ?? "";
            if (!line.StartsWith("gitdir:", StringComparison.Ordinal)) return null;
            var gitDir = Path.GetFullPath(line["gitdir:".Length..].Trim(), Path.GetDirectoryName(dotGitFile)!);
            var common = Path.Combine(gitDir, "commondir");
            if (!File.Exists(common)) return null;
            var commonDir = Path.GetFullPath(File.ReadAllText(common).Trim(), gitDir);
            return Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(commonDir)) is { } root ? Trim(RealPath(root)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static string RealPath(string dir)
    {
        var full = Trim(Path.GetFullPath(dir));
        try { return new DirectoryInfo(full).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? full; }
        catch (IOException) { return full; }
    }

    private static string Trim(string path) => Path.TrimEndingDirectorySeparator(path);

    [GeneratedRegex("[^A-Za-z0-9._-]+")]
    private static partial Regex SafeChars();
}
