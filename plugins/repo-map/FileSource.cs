using System.Diagnostics;

namespace CxAgent.Plugins.RepoMap;

/// <summary>The files under a folder that a map is made of, and how that list was decided.</summary>
/// <param name="Files">Absolute paths, in a stable ordinal order so the same tree maps the same way.</param>
/// <param name="FromGit">Whether git chose them, or the walk this falls back to without it.</param>
/// <param name="Truncated">Whether the folder held more readable files than one map reads.</param>
public sealed record FileList(IReadOnlyList<string> Files, bool FromGit, bool Truncated);

/// <summary>
/// Which files under a folder a map reads.
///
/// <para>GIT DECIDES, NOT A LIST OF NAMES. <c>git ls-files</c> already knows what this repository
/// tracks and what it ignores — nested .gitignore files, the global excludes file, negations — and a
/// hardcoded <c>bin</c>/<c>obj</c> list is wrong in both directions at once. Untracked files that are
/// not ignored are included: a file someone is writing right now is exactly what they are asking
/// about. Core's own file search asks git the same question for the same reason.</para>
///
/// <para>WITHOUT GIT, A WALK THAT SKIPS THE USUAL BUILD OUTPUT. A folder that is not a repository —
/// or a machine without git — still gets a map. The walk's skip list is the guess git would have
/// made unnecessary, so it is used only when there is no git to ask.</para>
///
/// <para>A SYMLINK IS NEVER FOLLOWED. git lists one as a link, not as its target's content, and the
/// walk does not descend into a linked folder: a link is how a map of this folder would otherwise
/// end up reading somewhere else entirely.</para>
/// </summary>
public static class FileSource
{
    /// <summary>How many files one map reads. A tree larger than this is mapped from its first files
    /// by path, and the map says so — narrowing the path is the answer, not a slower map.</summary>
    public const int MaxFiles = 20_000;

    /// <summary>A file larger than this is generated, vendored or data; nobody wrote that much code by hand in one file.</summary>
    public const long MaxBytes = 1_000_000;

    private static readonly HashSet<string> SkippedWithoutGit = new(StringComparer.Ordinal)
    {
        "bin", "obj", "node_modules", "dist", "build", "out", "target", "vendor", "__pycache__", "venv",
    };

    /// <param name="root">The folder to list.</param>
    /// <param name="onProcess">Told each git process's id as it starts, so the host can reap it.</param>
    public static FileList List(string root, Action<int> onProcess)
    {
        var fromGit = GitFiles(root, onProcess);
        var all = fromGit ?? Walk(root);

        // EACH FILE INSIDE THE ROOT, JUDGED WHERE IT LANDS. git lists a tracked file from its index
        // without looking at the disk, so a folder replaced by a link since it was committed still
        // lists its old files — and reading them would read wherever the link points.
        var real = new Dictionary<string, string>(StringComparer.Ordinal);
        var realRoot = Paths.Real(root, real);
        var readable = all
            .Where(f => Language.For(f) is not null)
            .Where(IsPlainFile)
            .Where(f => Paths.Contains(realRoot, Paths.Real(f, real)))
            .ToList();
        readable.Sort(StringComparer.Ordinal);

        var truncated = readable.Count > MaxFiles;
        return new FileList(truncated ? readable.Take(MaxFiles).ToList() : readable, fromGit is not null, truncated);
    }

    private static bool IsPlainFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            // EMPTY IS SKIPPED, and that is also what keeps a FIFO out: .NET reports one as an empty
            // regular file, and opening it to read blocks until something writes to it.
            return info.Exists && info.LinkTarget is null && info.Length > 0 && info.Length <= MaxBytes;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// What git says is in the folder, or null when git cannot answer — not a repository, git
    /// missing, or a failure — and the walk should be used instead.
    /// </summary>
    private static List<string>? GitFiles(string root, Action<int> onProcess)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            // -z: a path with a newline or a quote in it arrives as itself, not C-quoted.
            foreach (var arg in new[] { "ls-files", "-z", "--cached", "--others", "--exclude-standard" })
                psi.ArgumentList.Add(arg);

            using var git = Process.Start(psi);
            if (git is null) return null;
            // ONLY WHILE IT RUNS: a small repository's list is done in milliseconds, and a process
            // already gone has nothing to reap. A host older than 0.9.44 reports one registered
            // after it exited as unrecordable, in the user's transcript.
            if (!git.HasExited) onProcess(git.Id);

            // DRAINED WHILE IT RUNS: a large repository's list overfills the pipe, and git would
            // block writing it while this waited for git to exit.
            var stderr = git.StandardError.ReadToEndAsync();
            var output = git.StandardOutput.ReadToEndAsync();
            if (!git.WaitForExit(30_000))
            {
                try { git.Kill(entireProcessTree: true); } catch (Exception) { }
                return null;
            }
            _ = stderr.Result;
            if (git.ExitCode != 0) return null;

            return output.Result
                .Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Select(relative => Path.GetFullPath(Path.Combine(root, relative)))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static List<string> Walk(string root)
    {
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0 && files.Count <= MaxFiles * 4)
        {
            var dir = pending.Pop();
            try
            {
                foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
                {
                    if (entry.LinkTarget is not null) continue;
                    if (entry is DirectoryInfo sub)
                    {
                        // A DOT-FOLDER is tooling — .git, .vs, .idea, a virtualenv — never source
                        // anyone asked to see mapped.
                        if (sub.Name.StartsWith('.') || SkippedWithoutGit.Contains(sub.Name)) continue;
                        pending.Push(sub.FullName);
                    }
                    else
                    {
                        files.Add(entry.FullName);
                    }
                }
            }
            catch (Exception)
            {
                // An unreadable folder is left out, as a reader without permission would leave it.
            }
        }

        return files;
    }
}
