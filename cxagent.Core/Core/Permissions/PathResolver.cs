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
    /// Reads a file that reaches the prompt — an instruction file, a command, a skill, a sidecar — only from where it
    /// belongs, and only as much as is wanted.
    ///
    /// <para>ITS REAL PATH MUST STAY IN <see cref="ConfinedRead.Root"/>. A repository's <c>AGENTS.md</c> linked to
    /// <c>~/.ssh/id_rsa</c> or <c>/proc/self/environ</c> would otherwise put the key in the system prompt on the
    /// first turn — sent to the provider, trusted folder or not. A link that stays inside reads as before.</para>
    ///
    /// <para>A FILE WITH NO SIZE IS NEVER OPENED. A device, a pipe and a <c>/proc</c> file all report a size of
    /// nothing, and .NET reports them otherwise as ordinary files: <c>/dev/zero</c> read to its end never ends, and a
    /// FIFO blocks the open itself. An empty file is no instructions either, so nothing is lost. A file with a size
    /// is read through one stream, never past <see cref="ConfinedRead.MaxBytes"/>.</para>
    /// </summary>
    public static ConfinedText ReadConfined(ConfinedRead read)
    {
        try
        {
            if (FileToolFrom(read.Path, null) is not { } real)
                return new ConfinedText(null, "could not be resolved", Truncated: false);
            if (read.Root is { } root && !Contains(RealFolder(root), real))
                return new ConfinedText(null, $"links outside {root} (→ {real}) — not read", Truncated: false);

            var info = new FileInfo(real);
            if (!info.Exists) return new ConfinedText(null, null, Truncated: false);
            if (info.Length == 0) return new ConfinedText("", null, Truncated: false);

            using var stream = new FileStream(real, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = new byte[Math.Min(read.MaxBytes + 1L, info.Length + 1)];
            var total = 0;
            int n;
            while (total < buffer.Length && (n = stream.Read(buffer, total, buffer.Length - total)) > 0) total += n;
            var truncated = total > read.MaxBytes;
            return new ConfinedText(Decode(buffer, Math.Min(total, read.MaxBytes), truncated), null, truncated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new ConfinedText(null, $"could not be read: {ex.Message}", Truncated: false);
        }
    }

    /// <summary>
    /// Text from the bytes read, decoded as <c>File.ReadAllText</c> decodes — by a byte-order mark when there is one
    /// (UTF-8, UTF-16 and UTF-32, either order), UTF-8 otherwise — so a file that read before reads the same. A
    /// sidecar saved by an editor that writes a mark still parses; an instruction file written as UTF-16 reaches the
    /// prompt as its text, not as bytes with NULs between them.
    ///
    /// <para>A CUT NEVER SPLITS A CHARACTER. When the cap stopped the read, the decoder is not flushed, so a character
    /// whose bytes run past the cap is left out rather than turned into a replacement character.</para>
    /// </summary>
    private static string Decode(byte[] bytes, int count, bool truncated)
    {
        var (encoding, skip) = bytes switch
        {
            [0xEF, 0xBB, 0xBF, ..] => (System.Text.Encoding.UTF8, 3),
            [0xFF, 0xFE, 0x00, 0x00, ..] => (System.Text.Encoding.UTF32, 4),
            [0x00, 0x00, 0xFE, 0xFF, ..] => (new System.Text.UTF32Encoding(bigEndian: true, byteOrderMark: true), 4),
            [0xFF, 0xFE, ..] => (System.Text.Encoding.Unicode, 2),
            [0xFE, 0xFF, ..] => (System.Text.Encoding.BigEndianUnicode, 2),
            _ => (System.Text.Encoding.UTF8, 0),
        };
        if (count <= skip) return "";

        var decoder = encoding.GetDecoder();
        var chars = new char[encoding.GetMaxCharCount(count - skip)];
        var written = decoder.GetChars(bytes, skip, count - skip, chars, 0, flush: !truncated);
        return new string(chars, 0, written);
    }

    /// <summary>
    /// Every file under a folder, as a recursive enumeration finds them — except through a link that lands outside
    /// <see cref="WalkRequest.Within"/>, or back into a folder the walk is already inside.
    ///
    /// <para>A LINK IS FOLLOWED ONLY WHERE IT STAYS. The folder a caller judged is not the folder a link under it
    /// leads to: followed blindly, a link to a folder elsewhere lists that folder's files, a search reads them, a
    /// removal deletes them and a hash covers them. Each link is resolved and judged by where it lands; one landing
    /// anywhere else is reported (<see cref="WalkResult.Unfollowed"/>), never walked.</para>
    ///
    /// <para>A CYCLE ENDS. A link into a folder already in the chain above it would walk that chain for ever, and
    /// the plain enumeration does exactly that until the path grows too long; such a link is reported, not entered.
    /// A link to a sibling is not a cycle, and is walked as the plain enumeration walks it.</para>
    ///
    /// <para>THE PLAIN ENUMERATION'S FILES wherever no link leaves, each real folder walked once — a folder's files
    /// before its sub-folders, breadth first — matched as <c>Directory.EnumerateFiles</c> matches a pattern.
    /// One read of each folder, links told apart by the entry's own attributes, and only links resolved.</para>
    /// </summary>
    public static WalkResult Walk(WalkRequest request)
    {
        // THE MATCHING Directory.EnumerateFiles(path, pattern, SearchOption) DOES — the Win32 form of the pattern,
        // case-sensitive where the file system is — so the same names match.
        var expression = System.IO.Enumeration.FileSystemName.TranslateWin32Expression(request.Pattern);
        var ignoreCase = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false };

        var start = RealFolder(request.Start);
        bool Followable(string real) => request.Within is null || request.Within.Any(folder => Contains(folder, real));

        var files = new List<string>();
        var unfollowed = new List<UnfollowedLink>();
        var tooDeep = new List<string>();
        var unreadable = new List<UnreadableFolder>();
        var visited = 0;
        // EACH REAL FOLDER ONCE. Folders that link to one another — workspace packages — are a tree of so many folders,
        // and walking each again through every chain of links to it grows factorially; a folder already walked is
        // not walked again, so a file is listed under the first path that reached it.
        var walked = new HashSet<string>(StringComparer.Ordinal) { start };
        var queue = new Queue<PendingFolder>([new PendingFolder(request.Start, start, [start], 0)]);
        while (queue.TryDequeue(out var pending))
        {
            var folders = new List<PendingFolder>();
            // A FOLDER THAT CANNOT BE READ COSTS ITSELF, NOT THE REST: recorded, and the walk goes on. The folder is
            // opened as the enumerable is built, so that is inside the try too.
            List<FolderEntry> listed;
            try
            {
                listed = [.. new System.IO.Enumeration.FileSystemEnumerable<FolderEntry>(pending.Path,
                    (ref System.IO.Enumeration.FileSystemEntry e) => new FolderEntry(
                        e.ToFullPath(), e.FileName.ToString(), e.IsDirectory,
                        e.Attributes.HasFlag(FileAttributes.ReparsePoint)), options)];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                unreadable.Add(new UnreadableFolder(pending.Path, ex.Message));
                continue;
            }

            foreach (var entry in listed)
            {
                if (request.MaxVisited is { } cap && ++visited > cap)
                    return new WalkResult(files, unfollowed, unreadable, tooDeep, Truncated: true);

                // A LINK IS RESOLVED, and judged by where it lands; anything else is where its folder is.
                var real = entry.IsLink
                    ? FileToolFrom(entry.Path, null)
                    : pending.Real + Path.DirectorySeparatorChar + entry.Name;
                if (entry.IsLink)
                {
                    var cycle = entry.IsDirectory && real is not null && pending.Above.Contains(real);
                    if (real is null || cycle || !Followable(real))
                    {
                        unfollowed.Add(new UnfollowedLink(entry.Path, real, real is not null && Contains(start, real), cycle));
                        continue;
                    }
                }

                if (!entry.IsDirectory)
                {
                    if (System.IO.Enumeration.FileSystemName.MatchesWin32Expression(expression, entry.Name, ignoreCase))
                        files.Add(entry.Path);
                }
                else if (!pending.Above.Contains(real!))
                {
                    if (request.MaxDepth is { } deepest && pending.Depth + 1 > deepest) tooDeep.Add(entry.Path);
                    else if (walked.Add(real!))
                        folders.Add(new PendingFolder(entry.Path, real!, [.. pending.Above, real!], pending.Depth + 1));
                }
            }

            // FILES OF A FOLDER BEFORE ITS SUB-FOLDERS, breadth first: the plain enumeration's order.
            foreach (var folder in folders) queue.Enqueue(folder);
        }
        return new WalkResult(files, unfollowed, unreadable, tooDeep, Truncated: false);
    }

    /// <summary>One entry of a folder, as the walk reads it.</summary>
    private readonly record struct FolderEntry(string Path, string Name, bool IsDirectory, bool IsLink);

    /// <summary>A folder still to walk: where it is as spelled, where it really is, every folder (real) the walk passed
    /// through to reach it — a link back into one of those is a cycle — and how deep it is.</summary>
    private sealed record PendingFolder(string Path, string Real, HashSet<string> Above, int Depth);

    /// <summary>
    /// Whether <paramref name="real"/> is <paramref name="folder"/> or under it. Both must already be real: this
    /// compares, it does not resolve.
    /// </summary>
    public static bool Contains(string folder, string real)
    {
        var f = Path.TrimEndingDirectorySeparator(folder);

        // A FILESYSTEM ROOT CONTAINS EVERYTHING ON IT. Its spelling already ends in the separator (`/`, `C:\`), and a
        // prefix of root-plus-separator (`//`) matched nothing — a session at the root contained nothing, by accident.
        // A root is never trusted (PermissionPolicy.CanBeTrusted), so containing everything asks for everything.
        if (f.EndsWith(Path.DirectorySeparatorChar) || f.EndsWith(Path.AltDirectorySeparatorChar))
            return real.StartsWith(f, StringComparison.Ordinal);
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

/// <summary>A read of one file: where it is, the folder its real path must stay inside (null: anywhere — the user's
/// own files), and the most bytes to read.</summary>
internal sealed record ConfinedRead(string Path, string? Root, int MaxBytes);

/// <summary>What a confined read got: the text (null when there is no file, or it was refused — then
/// <see cref="Problem"/> says why; empty when the file has no size), and whether the cap cut it short.</summary>
internal sealed record ConfinedText(string? Text, string? Problem, bool Truncated);

/// <summary>What a walk is asked for: where it starts, the file names it lists (a Win32 pattern), the folders a link
/// may land in to be followed — none at all for an empty list, and every one for null — and the most entries it
/// visits and folders it descends (null: no bound).</summary>
internal sealed record WalkRequest(string Start, string Pattern, IReadOnlyList<string>? Within,
    int? MaxVisited = null, int? MaxDepth = null);

/// <summary>What a walk found: the files it listed, as spelled under its start; the links it did not follow; the
/// folders it could not read; and whether it stopped at its bounds — folders past the depth, or entries past the
/// visit cap.</summary>
internal sealed record WalkResult(IReadOnlyList<string> Files, IReadOnlyList<UnfollowedLink> Unfollowed,
    IReadOnlyList<UnreadableFolder> Unreadable, IReadOnlyList<string> TooDeep, bool Truncated);

/// <summary>A folder a walk could not read, and why — the walk goes on past it.</summary>
internal sealed record UnreadableFolder(string Path, string Reason);

/// <summary>A link a walk did not follow: where it is, what it really points at (null when that cannot be told),
/// whether that is under the walk's start, and whether following it would have walked a folder already in the
/// chain above it.</summary>
internal sealed record UnfollowedLink(string Path, string? Target, bool InsideStart, bool Cycle);

/// <summary>Where a leading <c>cd</c> goes: <see cref="Folder"/>, or — <see cref="ShellDecides"/> — a target the
/// shell resolves at run time (<c>-</c>, <c>~</c>, a variable). A null folder with the shell not deciding is a cd
/// that cannot be resolved; both fail toward asking.</summary>
internal sealed record ShellCd(string? Folder, bool ShellDecides)
{
    public static readonly ShellCd Decided = new(null, ShellDecides: true);
}
