namespace CxAgent.Core.Permissions;

/// <summary>
/// Where a path really lands, given the folder it is relative to — the one answer the permission gate and the
/// executors that act on a model's path both use, so they cannot come to two.
///
/// <para>THREE KINDS, BECAUSE THE TOOLS DIFFER. The file tool collapses <c>..</c> as text against its folder
/// and then follows links (<see cref="AsFileTool"/>), because its executor does exactly that before opening
/// anything. A shell command hands the raw path to the kernel, which follows each link where it sits and
/// applies <c>..</c> to where the walk really is (<see cref="AsShellArgument"/>). And a leading <c>cd</c> is the
/// shell's logical kind where it can be (<see cref="AsShellCd"/>). A call site that uses the wrong one judges
/// a different file from the one that is touched — every hole the gate harness found was that.</para>
///
/// <para>BUILT PER CALL, with the folder the path is relative to: the agent's folder for a file-tool argument,
/// the folder a command runs in for a shell argument. The folder is stored as given; the answers are real.</para>
/// </summary>
internal sealed class PathResolver(string folder)
{
    /// <summary>The folder relative paths are resolved from, as given.</summary>
    public string Folder { get; } = folder;

    /// <summary>The file tool's resolution: <c>..</c> collapsed as text against <see cref="Folder"/>, then every
    /// link followed component by component. Null when it cannot be resolved — which every caller treats as
    /// outside, so a failure asks.</summary>
    public string? AsFileTool(string path) => FileToolFrom(path, Folder);

    /// <summary>
    /// The folder a call's relative paths are resolved from: its own, or — for a caller that has none — the
    /// process directory.
    ///
    /// <para>THE ONE PLACE THAT FALLBACK IS MADE. The agent always sends a folder; an embedder or a test may not, and
    /// the process directory is then the only folder there is. Written once, here, so the gate and the executors
    /// cannot fall back to two different ones.</para>
    /// </summary>
    public static string FolderOrProcess(string? folder) =>
        folder is { Length: > 0 } given ? given : Directory.GetCurrentDirectory();

    /// <summary>A folder as it really is on disk — symlinks followed — the form every path is compared in.</summary>
    public static string RealFolder(string folder) =>
        FileToolFrom(folder, null) ?? Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

    /// <summary>Whether something — a file, a folder, or a link even if dangling — is at <paramref name="path"/>,
    /// relative to <see cref="Folder"/>.</summary>
    public bool Names(string path) => NamesAt(Path.Combine(Folder, path));

    /// <summary>
    /// Whether <paramref name="real"/> is <paramref name="folder"/> or under it. Both must already be real: this
    /// compares, it does not resolve.
    /// </summary>
    public static bool Contains(string folder, string real)
    {
        var f = Path.TrimEndingDirectorySeparator(folder);
        return real == f || real.StartsWith(f + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    /// <summary>
    /// The real path a shell command's argument names: relative to <see cref="Folder"/> when it is relative, every
    /// link followed where it sits, and each <c>..</c> applied to where the walk really is — the kernel's
    /// order, which <c>realpath</c> calls physical. Null when it cannot be walked: a loop of links, or a
    /// path the file system rejects.
    /// </summary>
    public string? AsShellArgument(string path)
    {
        // OFF UNIX THE SHELL IS NOT THE KERNEL'S WALK described here, and the collapsed form is the one there is.
        if (OperatingSystem.IsWindows()) return AsFileTool(path);
        try
        {
            var full = Path.IsPathRooted(path) ? path : Path.Combine(Folder, path);
            var pending = new List<string>(full.Split('/', StringSplitOptions.RemoveEmptyEntries));
            var resolved = "/";
            var links = 0;
            while (pending.Count > 0)
            {
                var part = pending[0];
                pending.RemoveAt(0);
                if (part == ".") continue;
                if (part == "..") { resolved = Path.GetDirectoryName(resolved) ?? "/"; continue; }

                var next = Path.Combine(resolved, part);

                // A LINK IS REPLACED BY WHAT IT SAYS, read from where it sits — and that text walked in turn,
                // so its own links and `..` land where the kernel's would. Forty links is the kernel's own
                // limit before it calls a path a loop.
                if (new FileInfo(next).LinkTarget is { } target)
                {
                    if (++links > 40) return null;
                    if (Path.IsPathRooted(target)) resolved = "/";
                    pending.InsertRange(0, target.Split('/', StringSplitOptions.RemoveEmptyEntries));
                    continue;
                }
                resolved = next;
            }
            return resolved;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The folder a command's paths are read in: <see cref="Folder"/>, or past a leading <c>cd</c>, the folder it
    /// enters — or, for a target the shell decides, <see cref="ShellCd.ShellDecides"/>.
    ///
    /// <para>THE SHELL'S TARGETS ARE NOT NAMES. <c>~</c> is the home folder, <c>-</c> is <c>$OLDPWD</c>, and a
    /// variable is whatever it holds; joined onto the folder as text, each would read as a folder inside it.
    /// <c>CDPATH</c> is the other way a cd leaves, and the shell executor empties it for that reason.</para>
    /// </summary>
    public ShellCd AsShellCd(string? cd)
    {
        if (cd is null) return new ShellCd(Folder, ShellDecides: false);
        if (cd == "-" || cd.StartsWith('~') || cd.Contains('$', StringComparison.Ordinal)) return ShellCd.Decided;

        // A cd IS LOGICAL WHERE IT CAN BE: `sh` collapses `cd link/..` as text and enters the folder that
        // names, so the target is collapsed the same way and the paths read after it walk from there. Where
        // that folder does not exist, dash fails the cd and bash — /bin/sh on macOS — enters the one the
        // kernel's walk reaches, so that one is judged.
        try
        {
            var logical = Path.GetFullPath(Path.Combine(Folder, cd));
            return Directory.Exists(logical)
                ? new ShellCd(logical, ShellDecides: false)
                : new ShellCd(AsShellArgument(Path.Combine(Folder, cd)), ShellDecides: false);
        }
        catch (ArgumentException) { return new ShellCd(null, ShellDecides: false); }
    }

    /// <summary>Whether something — a file, a folder, or a link even if dangling — is at <paramref name="path"/>.
    /// When that cannot be told, the answer is yes: the token is then judged as a path, which asks.</summary>
    public static bool NamesAt(string path)
    {
        try { return File.Exists(path) || Directory.Exists(path) || new FileInfo(path).LinkTarget is not null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException) { return true; }   // cannot tell: treat it as a path
    }

    /// <summary>The file tool's resolution of <paramref name="path"/> against <paramref name="root"/> (the process
    /// directory when null — see decision 8's one edge in PermissionPolicy.RequestsFor).</summary>
    internal static string? FileToolFrom(string path, string? root)
    {
        try
        {
            var full = root is { Length: > 0 } ? Path.GetFullPath(path, root) : Path.GetFullPath(path);

            // COMPONENT BY COMPONENT, NOT JUST THE DEEPEST EXISTING ENTRY. Walking up to the first
            // entry that exists and resolving THAT lets a symlinked DIRECTORY through whenever
            // anything below it exists: the entry landed on is a plain file, ResolveLinkTarget on a
            // plain file is null — the link is the PARENT, and nothing looks at it.
            //
            // MEASURED against a real fixture with `root/link -> outside`, on a trusted folder, the
            // deepest-entry walk gives:
            //     link/x.txt       (existing)          silently allowed   WRONG
            //     link/sub/new.txt (nested, new)       silently allowed   WRONG
            //     link/x.txt       (not existing)      asked              correct
            // The only shape it catches is the narrow one where the link itself is the deepest
            // existing entry — which is exactly what a naive symlink test constructs, so such a test
            // passes with the escape sitting underneath it.
            //
            // That is INVERTED RELATIVE TO RISK: creating a new file outside the boundary is caught,
            // overwriting an existing one is not. No attacker needed — node_modules links, vendor/,
            // monorepo package links and a docs -> ../shared-docs convention are ordinary layouts.
            //
            // Components below the deepest existing entry cannot be links (they do not exist), so
            // walking the whole path costs nothing extra for them and follows a link at any depth.
            var parts = new List<string>();
            for (var cursor = full; cursor is not null; cursor = Path.GetDirectoryName(cursor))
            {
                var name = Path.GetFileName(cursor);
                if (string.IsNullOrEmpty(name))
                {
                    parts.Add(cursor);   // the filesystem root ("/" or "C:\")
                    break;
                }
                parts.Add(name);
            }
            parts.Reverse();

            var resolved = parts[0];
            for (var i = 1; i < parts.Count; i++)
            {
                resolved = Path.Combine(resolved, parts[i]);

                // A LINK IS FOLLOWED WHEREVER IT SITS. returnFinalTarget walks a chain of links to
                // its end; a component that does not exist is not a link, so this is a no-op there.
                var target = Directory.Exists(resolved)
                    ? Directory.ResolveLinkTarget(resolved, returnFinalTarget: true)
                    : File.Exists(resolved)
                        ? File.ResolveLinkTarget(resolved, returnFinalTarget: true)
                        : null;

                if (target is not null)
                    resolved = target.FullName;
            }

            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(resolved));
        }
        // ArgumentException is in this list because RequestsFor RESOLVES rather than matching raw
        // strings, so a path that Path.GetFullPath rejects outright — an embedded NUL, an empty
        // string — reaches here and throws. Without it the exception escapes RequestsFor and
        // surfaces as a raw "Null character in path" crash caught by the job executor, losing
        // PermissionDenied = true. Fail toward asking, as everything else here does.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or NotSupportedException or ArgumentException)
        {
            return null;   // fail toward asking: never let a resolution error produce silent allow.
        }
    }
}

/// <summary>Where a leading <c>cd</c> goes: <see cref="Folder"/>, or — <see cref="ShellDecides"/> — a target the
/// shell resolves at run time (<c>-</c>, <c>~</c>, a variable). A null folder with the shell not deciding is a cd
/// that cannot be resolved; both fail toward asking.</summary>
internal sealed record ShellCd(string? Folder, bool ShellDecides)
{
    public static readonly ShellCd Decided = new(null, ShellDecides: true);
}
