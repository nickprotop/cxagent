using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

public class AppPathsTests
{
    /// <summary>
    /// Regression (found by the P5c live drive): the config directory holds config.json, which holds
    /// API KEYS, plus cxagent.db and the job logs. `EnsureCreated` used a bare `Directory.CreateDirectory`
    /// with no explicit mode, so the directory inherited the process umask — observed 775 under the
    /// default umask 0002, i.e. group- and world-readable. `ProviderConfigWriter` correctly forces the
    /// FILE to 0600, but a traversable directory still leaks the file listing (and anything a future
    /// writer forgets to chmod). The spec calls for 0700; enforce it here.
    /// </summary>
    [Fact]
    public void EnsureCreated_MakesConfigAndLogsDirs_OwnerOnly()
    {
        if (OperatingSystem.IsWindows()) return;   // POSIX modes only

        var tmp = Path.Combine(Path.GetTempPath(), "cxagent-mode-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(tmp);
            paths.EnsureCreated();

            const UnixFileMode ownerOnly =
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

            Assert.Equal(ownerOnly, File.GetUnixFileMode(paths.ConfigDir));
            Assert.Equal(ownerOnly, File.GetUnixFileMode(paths.LogsDir));
        }
        finally
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        }
    }

    /// <summary>
    /// EnsureCreated is called on every startup, so it must also REPAIR a directory that already
    /// exists with loose permissions — otherwise an install whose directory was created 775 stays
    /// 775 forever.
    /// </summary>
    [Fact]
    public void EnsureCreated_TightensAnExistingLooseDir()
    {
        if (OperatingSystem.IsWindows()) return;

        var tmp = Path.Combine(Path.GetTempPath(), "cxagent-mode2-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tmp);
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);   // 755

            new AppPaths(tmp).EnsureCreated();

            const UnixFileMode ownerOnly =
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            Assert.Equal(ownerOnly, File.GetUnixFileMode(tmp));
        }
        finally
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void OverrideDir_ResolvesDbAndLogsUnderIt()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "cxagent-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(tmp);
            Assert.Equal(tmp, paths.ConfigDir);
            Assert.Equal(Path.Combine(tmp, "cxagent.db"), paths.DatabasePath);
            Assert.Equal(Path.Combine(tmp, "logs"), paths.LogsDir);

            paths.EnsureCreated();
            Assert.True(Directory.Exists(paths.ConfigDir));
            Assert.True(Directory.Exists(paths.LogsDir));
        }
        finally
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void DefaultDir_IsUnderApplicationData()
    {
        var paths = new AppPaths();
        Assert.Contains("cxagent", paths.ConfigDir);
        Assert.EndsWith("cxagent.db", paths.DatabasePath);
    }
}
