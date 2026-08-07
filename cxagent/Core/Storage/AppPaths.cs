namespace CxAgent.Core.Storage;

/// <summary>
/// Resolves cxagent's on-disk locations. Per-OS by default (via ApplicationData /
/// XDG_CONFIG_HOME), or an explicit override directory (used by tests).
/// </summary>
public class AppPaths
{
    public string ConfigDir { get; }
    public string DatabasePath => Path.Combine(ConfigDir, "cxagent.db");
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
    /// the common default of 0002 that yields 775, i.e. group- and world-traversable. ProviderConfigWriter
    /// separately forces config.json itself to 0600, but a readable directory still exposes the file
    /// listing and anything a future writer forgets to chmod.
    ///
    /// The chmod is applied unconditionally rather than only on creation, so an install made before this
    /// was enforced is repaired on the next startup instead of staying loose forever.
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
            // app from starting. The file-level 0600 in ProviderConfigWriter is the stronger guarantee.
        }
    }
}
