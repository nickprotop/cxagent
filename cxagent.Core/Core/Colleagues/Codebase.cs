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

    // THE FILES THAT MAKE A FOLDER A PROJECT, outside git — a build or package manifest of the common toolchains.
    private static readonly string[] ProjectFiles =
    [
        "*.csproj", "*.fsproj", "*.vbproj", "*.sln", "*.slnx", "package.json", "Cargo.toml", "go.mod", "pyproject.toml",
        "setup.py", "requirements.txt", "pom.xml", "build.gradle", "build.gradle.kts", "CMakeLists.txt", "Makefile",
        "Gemfile", "composer.json", "mix.exs", "deno.json",
    ];

    /// <summary>
    /// Whether <paramref name="folder"/> is a project — inside a git repository, or holding a project file of its own —
    /// and so a codebase a colleague keeps experience of.
    ///
    /// <para>NOT EVERY FOLDER A SESSION RAN IN (user, 2026-11-11): one run in /tmp gave a colleague a "tmp" codebase,
    /// experience of a scratch folder that is not one thing. Work there still counts: it is kept in the colleague's past,
    /// and what holds generally reaches its core.</para>
    /// </summary>
    public static bool IsProject(string folder)
    {
        try
        {
            var start = RealPath(folder);
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var dotGit = Path.Combine(dir.FullName, ".git");
                if (Directory.Exists(dotGit) || File.Exists(dotGit)) return true;
            }
            return Directory.Exists(start) && ProjectFiles.Any(pattern => Directory.EnumerateFiles(start, pattern).Any());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
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
