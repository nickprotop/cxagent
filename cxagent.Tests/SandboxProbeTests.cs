using CxAgent.Core.Permissions;
using CxAgent.Core.Sandbox;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Whether bubblewrap works on this machine, and what the session does with the answer. Fakes stand in for
/// bwrap where the answer must be a failure; the one test that needs the real thing finds it on PATH and returns
/// early where there is none.
/// </summary>
public sealed class SandboxProbeTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sbx-probe-").FullName;

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } }

    /// <summary>A script standing in for bubblewrap. Linux only, as every caller already checks first.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private string FakeBwrap(string body)
    {
        var path = Path.Combine(_dir, "bwrap");
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    [Fact]
    public void Not_linux_is_unavailable()
    {
        var status = SandboxProbe.Run(new SandboxProbeRequest("/whatever/bwrap", IsLinux: false));
        Assert.Equal(SandboxStatusKind.Unavailable, status.Kind);
        Assert.Contains("not available on this system", status.Reason);
    }

    [Fact]
    public void No_bwrap_is_unavailable_with_install_hint()
    {
        var status = SandboxProbe.Run(new SandboxProbeRequest(null, IsLinux: true));
        Assert.Equal(SandboxStatusKind.Unavailable, status.Kind);
        Assert.Contains("bubblewrap", status.Reason);
        Assert.Contains("Install", status.Fix);
    }

    [Fact]
    public void Bwrap_failure_is_failed_with_its_stderr()
    {
        if (!OperatingSystem.IsLinux()) return;
        var bwrap = FakeBwrap("echo 'bwrap: setting up uid map: Permission denied' >&2; exit 1");
        var status = SandboxProbe.Run(new SandboxProbeRequest(bwrap, IsLinux: true));
        Assert.Equal(SandboxStatusKind.Failed, status.Kind);
        // THE REASON IS BUBBLEWRAP'S OWN LINE, short enough for a prompt heading; the fix is said apart from it,
        // where there is room — a live drive clipped the hint mid-word when the two were one string.
        Assert.Equal("bwrap: setting up uid map: Permission denied", status.Reason);
        Assert.Contains("user namespaces", status.Fix);
    }

    [Fact]
    public void Working_bwrap_is_active()
    {
        if (!OperatingSystem.IsLinux() || SandboxProbe.FindOnPath() is not { } bwrap) return;
        Assert.Equal(SandboxStatusKind.Active, SandboxProbe.Run(new SandboxProbeRequest(bwrap, IsLinux: true)).Kind);
    }

    [Fact]
    public void Find_on_path_reads_the_path_it_is_given()
    {
        if (!OperatingSystem.IsLinux()) return;
        var bwrap = FakeBwrap("exit 0");
        Assert.Equal(bwrap, SandboxProbe.FindOnPath($"/nonexistent:{_dir}"));
        Assert.Null(SandboxProbe.FindOnPath("/nonexistent"));
    }

    /// <summary>
    /// A BUBBLEWRAP A COMMAND COULD HAVE WRITTEN IS NOT BUBBLEWRAP. A writable folder early on PATH — a venv's bin,
    /// node_modules/.bin, pnpm's home — would otherwise let a contained command plant the sandbox itself.
    /// </summary>
    [Fact]
    public void A_bwrap_under_home_or_temp_is_not_trusted()
    {
        if (!OperatingSystem.IsLinux()) return;
        var planted = FakeBwrap("exit 0");
        var found = SandboxProbe.Locate(new BwrapSearch(_dir, Home: Path.GetTempPath(), Temp: "/nonexistent-temp"));
        Assert.Null(found.Path);
        Assert.Contains(planted, found.Rejected);
    }

    [Fact]
    public void A_relative_path_entry_is_skipped()
    {
        if (!OperatingSystem.IsLinux()) return;
        FakeBwrap("exit 0");
        var cwd = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_dir);
        try { Assert.Null(SandboxProbe.Locate(new BwrapSearch(".", Home: "/nonexistent-home", Temp: "/nonexistent-temp")).Path); }
        finally { Directory.SetCurrentDirectory(cwd); }
    }

    [Fact]
    public void The_system_bwrap_is_trusted()
    {
        if (!OperatingSystem.IsLinux() || SandboxProbe.FindOnPath() is not { } system) return;
        var found = SandboxProbe.Locate(new BwrapSearch(Path.GetDirectoryName(system)!,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Path.GetTempPath()));
        if (system.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.Ordinal)) return;
        Assert.Equal(system, found.Path);
    }

    /// <summary>THE PROBE ASKS FOR WHAT A COMMAND GETS. A bubblewrap that starts with a bare root bind but fails to
    /// mount /proc would otherwise read as working while every command failed.</summary>
    [Fact]
    public void The_probe_uses_the_flags_commands_get()
    {
        if (!OperatingSystem.IsLinux()) return;
        var bwrap = FakeBwrap("case \"$*\" in *--proc*) echo 'bwrap: cannot mount proc' >&2; exit 1;; esac; exit 0");
        Assert.Equal(SandboxStatusKind.Failed, SandboxProbe.Run(new SandboxProbeRequest(bwrap, IsLinux: true)).Kind);
    }

    private static SandboxHome AnyHome => new(Path.GetTempPath(), null, null);

    [Fact]
    public void Disabled_settings_are_off_without_probing()
    {
        var probed = false;
        var state = new SandboxState(SandboxSettings.Off, AnyHome, () => { probed = true; return SandboxStatus.Active; });
        Assert.Equal(SandboxStatusKind.Off, state.Status.Kind);
        Assert.False(probed);
    }

    [Fact]
    public void Enabled_settings_take_the_probes_answer()
    {
        var state = new SandboxState(SandboxSettings.AppDefault, AnyHome, () => new SandboxStatus(SandboxStatusKind.Failed, "no"));
        Assert.Equal(SandboxStatusKind.Failed, state.Status.Kind);
    }

    [Fact]
    public void Reprobe_asks_again()
    {
        var calls = 0;
        var state = new SandboxState(SandboxSettings.AppDefault, AnyHome, () => { calls++; return SandboxStatus.Active; });
        _ = state.Status;
        _ = state.Status;
        state.Reprobe();
        _ = state.Status;
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Child_policy_sees_the_parents_sandbox()
    {
        var dir = Directory.CreateTempSubdirectory("sbx-policy-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "child"));
            var parent = new PermissionPolicy(dir, new PermissionRulesStore(new AppPaths(dir)));
            var state = new SandboxState(SandboxSettings.AppDefault, AnyHome, () => SandboxStatus.Active);
            parent.Sandbox = state;
            var child = parent.NarrowedTo(Path.Combine(dir, "child"));
            Assert.Same(state, child.Sandbox);
            Assert.True(child.IsNarrowed);
            Assert.False(parent.IsNarrowed);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
