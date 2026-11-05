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
    /// <summary>
    /// Sockets and socket folders a contained command must not reach, besides <see cref="RuntimeDir"/>: a read-only
    /// bind does not stop <c>connect()</c>. A tmux server's socket lets a command type into a pane outside the sandbox,
    /// X11's lets it send keystrokes, Docker's is root on the machine. Hidden where they exist.
    /// </summary>
    public IReadOnlyList<string> Sockets { get; init; } = [];

    /// <summary>
    /// cxagent's own files — where it runs from, and where a single-file build extracts its native libraries — held
    /// read-only: under a writable cache, a command could replace a library the next launch loads, outside the sandbox.
    /// </summary>
    public IReadOnlyList<string> AppFolders { get; init; } = [];

    /// <summary>The home this process was started with.</summary>
    /// <param name="configDir">The config folder in use.</param>
    public static SandboxHome FromEnvironment(string? configDir)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var uid = OperatingSystem.IsLinux() ? Posix.UserId() : 0;

        // THE TEMP FOLDERS SOCKETS SIT IN: this process's, the conventional /tmp, and tmux's own override.
        var temps = new[] { Path.GetTempPath(), "/tmp", Environment.GetEnvironmentVariable("TMUX_TMPDIR") }
            .OfType<string>().Where(t => t.Length > 0).Select(Path.TrimEndingDirectorySeparator).Distinct().ToList();
        return new(home, configDir,
            Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } run ? run : null)
        {
            Sockets =
            [
                // THE RUNTIME FOLDER BY ITS USUAL NAME TOO: XDG_RUNTIME_DIR is unset under ssh, cron and sudo -u, and the
                // session bus is still there for a command to start an unsandboxed process through.
                $"/run/user/{uid}",
                .. temps.Select(t => Path.Combine(t, $"tmux-{uid}")),
                .. temps.Select(t => Path.Combine(t, ".X11-unix")),
                "/run/docker.sock", "/var/run/docker.sock", "/run/podman/podman.sock",
            ],
            AppFolders =
            [
                AppContext.BaseDirectory,
                Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR") is { Length: > 0 } extract
                    ? extract
                    : Path.Combine(home, ".cache", "dotnet_bundle_extract"),
            ],
        };
    }
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

    /// <summary>
    /// The home's top-level dot entries, held read-only right after the writable root when that root is the home
    /// itself: a shell rc, a systemd unit, a gitconfig's fsmonitor all run code later, outside the sandbox. Caches
    /// among them are made writable again afterwards. Empty unless the root is the home.
    /// </summary>
    public IReadOnlyList<string> HomeDotfiles { get; init; } = [];

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
        ".config/hub", ".config/gcloud", ".npmrc", ".pypirc", ".pgpass", ".my.cnf", ".vault-token", ".password-store",
        ".cargo/credentials", ".cargo/credentials.toml", ".nuget/NuGet/NuGet.Config",
        ".claude/.credentials.json", ".codex/auth.json",
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
        var realHome = Real(home.Home);
        var repo = request.Narrowed ? null : Real(Hooks.HookSources.RepoRoot(agent));

        // A DOTFILES REPOSITORY AT THE HOME IS NOT THE PROJECT: widening the root to it would make every dotfile in the
        // home writable for a session that only meant to work in one folder of it.
        var root = repo is not null && !(repo == realHome && agent != realHome) ? repo : agent;

        var writable = new List<string> { root };
        var readOnly = new List<string>();

        // .git BOUND ONTO ITSELF, and every submodule's git folder: a folder that is not a mount point can be renamed,
        // and `mv .git .git2` followed by a rebuilt .git with its own hooks walked past the read-only parts below.
        var gitFolder = Path.Combine(root, ".git");
        if (Directory.Exists(gitFolder))
        {
            writable.Add(gitFolder);
            writable.AddRange(GitFolders.SubmoduleGitDirs(gitFolder));
            if (GitFolders.HooksPath(gitFolder, root) is { } hooksPath && Real(hooksPath) is { } realHooks) readOnly.Add(realHooks);
        }

        // A LINKED WORKTREE'S .git IS A FILE pointing into the main repository's .git, where its objects, refs and
        // index live. Without that folder writable, add and commit fail in every worktree — the worktree plugin's
        // whole purpose. The main checkout's own files are not included.
        if (GitFolders.CommonDir(root) is { } common)
        {
            writable.Add(common);
            writable.AddRange(GitFolders.SubmoduleGitDirs(common));
            if (GitFolders.HooksPath(common, root) is { } hooksPath && Real(hooksPath) is { } realHooks) readOnly.Add(realHooks);
            readOnly.Add(Path.Combine(root, ".git"));
            readOnly.AddRange(GitFolders.ExecutingParts(common));
        }

        foreach (var temp in new[] { Path.GetTempPath(), "/tmp" }.Select(Real).OfType<string>().Distinct())
            if (Directory.Exists(temp)) writable.Add(temp);
        writable.AddRange(Caches.Select(c => Path.Combine(home.Home, c)).Where(Directory.Exists).Select(Real).OfType<string>());
        writable.AddRange(request.Settings.Writable.Where(SandboxSettings.IsAnchored).Select(p => Expand(p, home.Home))
            .Where(Directory.Exists).Select(Real).OfType<string>());

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

        // cxagent's OWN FILES, read-only even where they sit under a writable cache — but not a build of cxagent inside
        // the project itself: that is the project's own output, and the next build has to replace it.
        readOnly.AddRange(home.AppFolders.Select(Real).OfType<string>()
            .Where(a => a != root && !a.StartsWith(root + "/", StringComparison.Ordinal)));

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
        foreach (var socket in home.Sockets) Hide(socket);
        foreach (var h in request.Settings.Hidden.Where(SandboxSettings.IsAnchored)) Hide(Expand(h, home.Home));

        return new SandboxLayout
        {
            WorkingDir = work,
            Writable = [.. writable.Distinct()],
            HomeDotfiles = root == realHome ? DotEntries(root) : [],
            ReadOnly = [.. readOnly.Distinct()],
            HiddenFolders = [.. hiddenFolders.Distinct()],
            HiddenFiles = [.. hiddenFiles.Distinct()],
            Logs = request.Logs is { } logs && Directory.Exists(logs) ? Real(logs) : null,
            Network = request.Settings.Network,
        };
    }

    /// <summary>The dot entries at the top of a folder, as real paths.</summary>
    private static IReadOnlyList<string> DotEntries(string folder)
    {
        try
        {
            return [.. Directory.EnumerateFileSystemEntries(folder, ".*", SearchOption.TopDirectoryOnly)
                .Select(Real).OfType<string>().Distinct()];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
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
