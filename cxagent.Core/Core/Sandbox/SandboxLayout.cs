using CxAgent.Core.Permissions;

namespace CxAgent.Core.Sandbox;

/// <summary>
/// The machine a sandboxed command runs on, as far as the sandbox cares: whose home the built-in lists are
/// relative to, which config folder is cxagent's own, and where the session's sockets live.
///
/// <para>PASSED IN, NEVER LOOKED UP HERE, so nothing in the sandbox assumes a path: the session reads them from
/// the environment it was started in, and a test hands in a fake home under a temp folder.</para>
/// </summary>
/// <param name="Home">The user's home folder.</param>
/// <param name="ConfigDir">The config folder this process uses — <c>--config-dir</c> included — hidden from
/// commands because it holds the API keys. Null for an embedder that has none.</param>
/// <param name="RuntimeDir"><c>$XDG_RUNTIME_DIR</c>, or null when the session has none.</param>
public sealed record SandboxHome(string Home, string? ConfigDir, string? RuntimeDir)
{
    /// <summary>The home this process was started with.</summary>
    /// <param name="configDir">The config folder in use.</param>
    public static SandboxHome FromEnvironment(string? configDir) => new(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        configDir,
        Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } run ? run : null);
}

/// <summary>What a layout is built from.</summary>
/// <param name="AgentFolder">The agent's own folder — the boundary its permission policy judges against. The
/// writable root is worked out from this, NEVER from where the call asked to run: a <c>working_dir</c> of the
/// user's home would otherwise make the whole home writable.</param>
/// <param name="RunFolder">Where the command runs (the call's <c>working_dir</c>, or the agent's folder).</param>
/// <param name="Narrowed">True for a sub-agent spawned with a folder of its own: it writes only there, as its
/// file tools do, instead of the whole repository.</param>
/// <param name="Logs">The agent's log folder — earlier calls' spill and background output — or null.</param>
/// <param name="Home">The machine's folders.</param>
/// <param name="Settings">The user's sandbox settings.</param>
public sealed record SandboxLayoutRequest(string AgentFolder, string RunFolder, bool Narrowed, string? Logs,
    SandboxHome Home, SandboxSettings Settings);

/// <summary>
/// Every folder a contained command is given, resolved on this machine: real paths, only ones that exist.
///
/// <para>THE DISK WORK HAPPENS HERE AND ONLY HERE. <see cref="SandboxProfile"/> turns a layout into arguments
/// without touching anything, so the order of binds — which is the whole security argument — is testable as a
/// list.</para>
///
/// <para>REAL PATHS, because bubblewrap mounts on the path it is given: a bind on a symlink's own name would
/// leave the folder it points at untouched, and a hide on <c>~/.ssh</c> that is a link would hide the link while
/// the keys stay readable through its target. Inside the sandbox every link resolves to the real path, and the
/// real path is what is bound.</para>
/// </summary>
public sealed record SandboxLayout
{
    /// <summary>Where the command runs, as a real path.</summary>
    public required string WorkingDir { get; init; }

    /// <summary>Folders bound read-write, in this order.</summary>
    public required IReadOnlyList<string> Writable { get; init; }

    /// <summary>Paths bound read-only over the writable ones: code that runs later, outside the sandbox.</summary>
    public required IReadOnlyList<string> ReadOnly { get; init; }

    /// <summary>Folders replaced by an empty tmpfs.</summary>
    public required IReadOnlyList<string> HiddenFolders { get; init; }

    /// <summary>Files replaced by <c>/dev/null</c>.</summary>
    public required IReadOnlyList<string> HiddenFiles { get; init; }

    /// <summary>
    /// The agent's log folder, bound back READ-ONLY after the config folder above it is hidden: a later command
    /// reads an earlier one's spill file and background output there. cxagent writes those files itself, from
    /// outside, so nothing inside needs to write them.
    /// </summary>
    public string? Logs { get; init; }

    /// <summary>Whether the command reaches the network.</summary>
    public bool Network { get; init; } = true;

    /// <summary>
    /// Package caches a build writes to. A cache is a download area, not the user's work or secrets, and
    /// without these `dotnet restore`, `npm install` and `cargo build` fail on the first run.
    /// </summary>
    private static readonly string[] Caches =
    [
        ".cache", ".nuget/packages", ".npm", ".cargo/registry", ".cargo/git", "go/pkg/mod",
        ".m2/repository", ".gradle/caches", ".local/share/pnpm", ".yarn",
    ];

    /// <summary>Where credentials live. Seen empty, so a command reading them gets nothing to send anywhere.</summary>
    private static readonly string[] Credentials =
    [
        ".ssh", ".aws", ".gnupg", ".azure", ".kube", ".docker/config.json", ".netrc", ".git-credentials", ".config/gh",
    ];

    /// <summary>Builds the layout for one command.</summary>
    /// <param name="request">The command's folder and the machine's.</param>
    public static SandboxLayout For(SandboxLayoutRequest request)
    {
        var home = request.Home;
        var work = PathResolver.RealFolder(request.RunFolder);
        var agent = PathResolver.RealFolder(request.AgentFolder);

        // THE REPOSITORY, NOT THE FOLDER, for the main agent: a session started in repo/src/app still commits, and
        // a build that writes repo/bin still works. A narrowed sub-agent is held to its own folder instead, as its
        // file tools are — unless that folder is itself a worktree, which gets its common .git below.
        var root = request.Narrowed ? agent : Real(Hooks.HookSources.RepoRoot(agent)) ?? agent;

        var writable = new List<string> { root };
        var readOnly = new List<string>();

        // A LINKED WORKTREE'S .git IS A FILE pointing into the main repository's .git, where its objects, refs and
        // index live. Without that folder writable, add and commit fail in every worktree — the worktree plugin's
        // whole purpose. The main checkout's own files are not included.
        if (GitFolders.CommonDir(root) is { } common)
        {
            writable.Add(common);
            readOnly.Add(Path.Combine(root, ".git"));
            readOnly.AddRange(GitFolders.ExecutingParts(common));
        }

        foreach (var temp in new[] { Path.GetTempPath(), "/tmp" }.Select(Real).OfType<string>().Distinct())
            if (Directory.Exists(temp)) writable.Add(temp);
        writable.AddRange(Caches.Select(c => Path.Combine(home.Home, c)).Where(Directory.Exists).Select(Real).OfType<string>());
        writable.AddRange(request.Settings.Writable.Select(p => Expand(p, home.Home)).Where(Directory.Exists).Select(Real).OfType<string>());

        // CODE THAT RUNS LATER, OUTSIDE THE SANDBOX — the same folders a file-tool write into always asks about.
        // .git is the exception: its objects, refs and index must stay writable or commit fails, so only the parts
        // that run something are held.
        foreach (var name in PermissionPolicy.ExecutableConfigNames)
        {
            var path = Path.Combine(root, name);
            if (name == ".git")
            {
                if (Directory.Exists(path)) readOnly.AddRange(GitFolders.ExecutingParts(path));
            }
            else if (Directory.Exists(path) || File.Exists(path)) readOnly.Add(path);
        }

        var hiddenFolders = new List<string>();
        var hiddenFiles = new List<string>();
        void Hide(string path)
        {
            if (Real(path) is not { } real) return;
            if (Directory.Exists(real)) hiddenFolders.Add(real);
            else if (File.Exists(real)) hiddenFiles.Add(real);
        }
        foreach (var c in Credentials) Hide(Path.Combine(home.Home, c));
        if (home.ConfigDir is { } config) Hide(config);
        // THE SESSION'S SOCKETS. A read-only bind does not stop connect() on a unix socket: through the session
        // bus `systemd-run --user` starts a process with no sandbox at all, the SSH agent signs with the user's
        // key, and cxagent's own diagnostics socket answers its permission prompts.
        if (home.RuntimeDir is { } run) Hide(run);
        foreach (var h in request.Settings.Hidden) Hide(Expand(h, home.Home));

        return new SandboxLayout
        {
            WorkingDir = work,
            Writable = [.. writable.Distinct()],
            ReadOnly = [.. readOnly.Distinct()],
            HiddenFolders = [.. hiddenFolders.Distinct()],
            HiddenFiles = [.. hiddenFiles.Distinct()],
            Logs = request.Logs is { } logs && Directory.Exists(logs) ? Real(logs) : null,
            Network = request.Settings.Network,
        };
    }

    /// <summary><c>~</c> and <c>~/x</c> against this machine's home; anything else as given.</summary>
    private static string Expand(string path, string home) =>
        path == "~" ? home
        : path.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(home, path[2..])
        : Path.GetFullPath(path);

    /// <summary>The real path of something that exists, or null.</summary>
    private static string? Real(string? path)
    {
        if (path is null) return null;
        if (Directory.Exists(path)) return PathResolver.RealFolder(path);
        if (!File.Exists(path)) return null;
        var folder = Path.GetDirectoryName(Path.GetFullPath(path));
        var target = new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName;
        return target ?? (folder is null ? Path.GetFullPath(path) : Path.Combine(PathResolver.RealFolder(folder), Path.GetFileName(path)));
    }
}
