namespace CxAgent.Core.Storage;

/// <summary>
/// Resolves cxagent's on-disk locations. Per-OS by default (via ApplicationData /
/// XDG_CONFIG_HOME), or an explicit override directory (used by tests).
/// </summary>
public class AppPaths
{
    public string ConfigDir { get; }
    public string DatabasePath => Path.Combine(ConfigDir, "cxagent.db");

    /// <summary>
    /// Usage history — a SEPARATE file from the resume database, deliberately.
    ///
    /// <para><see cref="SqliteSessionStore"/>'s own doc draws this line: it is "a RESUME BUFFER, not
    /// an archive: persistence-as-history is a different feature with different requirements, and
    /// pretending one is the other produces a database that grows forever and a schema that serves
    /// neither." Two files keep both truths — resume stays small and disposable (delete it and you
    /// lose nothing but a crash recovery), history grows and is the thing worth keeping.</para>
    /// </summary>
    public string HistoryPath => Path.Combine(ConfigDir, "history.db");

    public string LogsDir => Path.Combine(ConfigDir, "logs");

    public AppPaths(string? overrideDir = null)
    {
        if (overrideDir is not null)
        {
            ConfigDir = overrideDir;
            return;
        }

        // Linux honours XDG_CONFIG_HOME; ApplicationData covers macOS/Windows and
        // the XDG default (~/.config) on Linux.
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var baseDir = !string.IsNullOrEmpty(xdg)
            ? xdg
            : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        ConfigDir = Path.Combine(baseDir, "cxagent");
    }

    /// <summary>
    /// Creates ConfigDir and LogsDir if absent, and forces both to owner-only (0700) on Unix.
    ///
    /// The mode is NOT cosmetic: this directory holds config.json (which stores API KEYS), cxagent.db,
    /// and every job's log output. A bare Directory.CreateDirectory inherits the process umask — under
    /// the common default of 0002 that yields 775, i.e. group- and world-traversable. The config writer
    /// separately forces config.json itself to 0600, but a readable directory still exposes the file
    /// listing and anything a future writer forgets to chmod.
    ///
    /// The chmod is applied unconditionally rather than only on creation, so a directory left loose by
    /// any other means — an older install, a manual mkdir, a restore — is repaired on the next startup
    /// instead of staying that way forever.
    /// </summary>
    public void EnsureCreated()
    {
        Directory.CreateDirectory(ConfigDir);
        Directory.CreateDirectory(LogsDir);
        RestrictToOwner(ConfigDir);
        RestrictToOwner(LogsDir);
    }

    private static void RestrictToOwner(string dir)
    {
        if (OperatingSystem.IsWindows()) return;   // POSIX modes only; NTFS ACLs are inherited
        try
        {
            File.SetUnixFileMode(dir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort: a filesystem that rejects chmod (some network/FUSE mounts) must not stop the
            // app from starting. The stronger guarantee is the file's own 0600, set by whatever writes
            // config.json — this directory mode is defence in depth, not the primary control.
        }
    }
}
