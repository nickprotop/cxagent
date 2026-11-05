using System.Diagnostics;

namespace CxAgent.Core.Sandbox;

/// <summary>Where the sandbox stands for a session.</summary>
public enum SandboxStatusKind
{
    /// <summary>Turned off in settings.</summary>
    Off,

    /// <summary>bubblewrap starts; commands are contained.</summary>
    Active,

    /// <summary>bubblewrap is there and will not start — user namespaces blocked, typically. Each command asks
    /// before it runs uncontained.</summary>
    Failed,

    /// <summary>No sandbox on this system: not Linux, or bubblewrap is not installed. Commands run as they always
    /// did, through the normal prompts, and the session says so once.</summary>
    Unavailable,
}

/// <summary>The sandbox's state and, when it is not working, why and what to do about it.</summary>
/// <param name="Kind">Where it stands.</param>
/// <param name="Reason">For <see cref="SandboxStatusKind.Failed"/> and <see cref="SandboxStatusKind.Unavailable"/>:
/// what went wrong, in one short line — bubblewrap's own words where it gave any. Null otherwise. Short because a
/// permission prompt's heading shows it, and a heading is clipped; the fix is <see cref="Fix"/>.</param>
/// <param name="BwrapPath">The bubblewrap the probe ran, for <see cref="SandboxStatusKind.Active"/>: commands run this
/// exact file rather than looking bwrap up on PATH again, so what was tested is what runs.</param>
public sealed record SandboxStatus(SandboxStatusKind Kind, string? Reason, string? BwrapPath = null)
{
    /// <summary>What to do about it, said where there is room — the session's notice, <c>/sandbox</c>, F8 — and
    /// never in a prompt's clipped heading. Null when there is nothing to say.</summary>
    public string? Fix { get; init; }

    /// <summary>Off in settings.</summary>
    public static readonly SandboxStatus Off = new(SandboxStatusKind.Off, null);

    /// <summary>Working, with no path — for tests that do not run anything.</summary>
    public static readonly SandboxStatus Active = new(SandboxStatusKind.Active, null);
}

/// <summary>What a probe needs to know about the machine.</summary>
/// <param name="BwrapPath">bubblewrap's path, or null when it is not on PATH.</param>
/// <param name="IsLinux">Whether this is Linux, the one system bubblewrap runs on.</param>
public sealed record SandboxProbeRequest(string? BwrapPath, bool IsLinux)
{
    /// <summary>Whether commands will reach the network — the probe asks for no network when they will not, since
    /// that is a namespace of its own that can fail to start.</summary>
    public bool Network { get; init; } = true;

    /// <summary>A bubblewrap found and refused, said in the reason when none was accepted.</summary>
    public string? Rejected { get; init; }

    /// <summary>
    /// The bubblewrap this process uses, found ONCE, from the PATH it was started with.
    ///
    /// <para>NEVER SEARCHED AGAIN. A re-probe follows a command's failure, and a contained command can write into
    /// folders that sit early on PATH; a fresh search after it ran would find what it planted and make that the
    /// sandbox.</para>
    /// </summary>
    private static readonly Lazy<BwrapLocation> Machine = new(() => SandboxProbe.Locate(new BwrapSearch(
        Environment.GetEnvironmentVariable("PATH") ?? "",
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        System.IO.Path.GetTempPath())));

    /// <summary>This machine.</summary>
    /// <param name="network">Whether commands will reach the network.</param>
    public static SandboxProbeRequest ThisMachine(bool network = true) =>
        new(Machine.Value.Path, OperatingSystem.IsLinux()) { Network = network, Rejected = Machine.Value.Rejected };
}

/// <summary>
/// Whether bubblewrap starts here — asked once by starting it with nothing to do.
///
/// <para>STARTED, NOT LOOKED FOR. bubblewrap being installed says nothing about whether the kernel lets it make a
/// user namespace: Ubuntu restricts them through AppArmor, and a container usually has none. The only answer that
/// counts is a real start.</para>
/// </summary>
public static class SandboxProbe
{
    /// <summary>bubblewrap on the given PATH, or this process's PATH; null when it is not there.</summary>
    /// <param name="path">A PATH-style list, or null for the environment's.</param>
    public static string? FindOnPath(string? path = null)
    {
        foreach (var dir in (path ?? Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, "bwrap");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>
    /// The first bubblewrap on the search path that a contained command could not have written: not under the home or
    /// temp folders (both writable from inside, in part), not group- or world-writable, not reached through a
    /// relative PATH entry. Candidates passed over are named, so the reason can say why none was used.
    /// </summary>
    /// <param name="search">Where to look, and what counts as writable from inside.</param>
    public static BwrapLocation Locate(BwrapSearch search)
    {
        var rejected = new List<string>();
        var home = Permissions.PathResolver.RealFolder(search.Home);
        var temps = new[] { search.Temp, "/tmp" }.Where(Directory.Exists).Select(Permissions.PathResolver.RealFolder).ToList();
        static bool Under(string path, string folder) =>
            path.StartsWith(Path.TrimEndingDirectorySeparator(folder) + "/", StringComparison.Ordinal);

        foreach (var dir in search.Path.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Path.IsPathRooted(dir)) continue;
            var candidate = Path.Combine(dir, "bwrap");
            if (!File.Exists(candidate)) continue;
            var real = new FileInfo(candidate).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? candidate;
            real = Path.Combine(Permissions.PathResolver.RealFolder(Path.GetDirectoryName(real)!), Path.GetFileName(real));
            if (Under(real, home) || temps.Any(t => Under(real, t)))
            {
                rejected.Add($"{candidate} (in a folder a contained command can write)");
                continue;
            }
            if (OperatingSystem.IsLinux())
            {
                var mode = File.GetUnixFileMode(real);
                if ((mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
                {
                    rejected.Add($"{candidate} (writable by others)");
                    continue;
                }
                if ((mode & UnixFileMode.UserExecute) == 0) continue;
            }
            return new(candidate, null);
        }
        return new(null, rejected.Count == 0 ? null : string.Join(", ", rejected));
    }

    /// <summary>Starts bubblewrap with the sandbox's own isolation and nothing to run.</summary>
    /// <param name="request">The machine.</param>
    public static SandboxStatus Run(SandboxProbeRequest request)
    {
        if (!request.IsLinux)
            return new(SandboxStatusKind.Unavailable, "the sandbox is not available on this system");
        if (request.BwrapPath is not { } bwrap)
            return request.Rejected is { } refused
                ? new(SandboxStatusKind.Unavailable, $"no trusted bubblewrap — passed over {refused}")
                {
                    Fix = "Install bubblewrap system-wide (the bubblewrap package) so a contained command cannot replace it.",
                }
                : new(SandboxStatusKind.Unavailable, "bubblewrap is not installed")
                {
                    Fix = "Install the bubblewrap package to contain the model's commands.",
                };

        try
        {
            var psi = new ProcessStartInfo(bwrap)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            // THE FLAGS A COMMAND GETS, so "works" means a command will start: a bubblewrap that binds / but cannot
            // mount /proc, or make a network namespace, would otherwise read as working while every command failed.
            string[] args = ["--ro-bind", "/", "/", "--dev", "/dev", "--proc", "/proc", "--unshare-pid", "--new-session"];
            if (!request.Network) args = [.. args, "--unshare-net"];
            foreach (var a in args.Concat(["--", "true"])) psi.ArgumentList.Add(a);
            using var process = Process.Start(psi)!;
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(5000))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return new(SandboxStatusKind.Failed, "bubblewrap did not start within 5 seconds");
            }
            if (process.ExitCode == 0) return new(SandboxStatusKind.Active, null, bwrap);
            return new(SandboxStatusKind.Failed, FirstLine(stderr.Result) ?? $"bubblewrap exited with {process.ExitCode}")
            {
                Fix = "bubblewrap needs unprivileged user namespaces. On Ubuntu, AppArmor must allow bwrap (its profile "
                    + "ships with the package); elsewhere check kernel.unprivileged_userns_clone.",
            };
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            return new(SandboxStatusKind.Failed, $"bubblewrap could not be started: {ex.Message.TrimEnd('.')}");
        }
    }

    private static string? FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault()?.TrimEnd('.');
}

/// <summary>Where to look for bubblewrap, and which folders a contained command could have written it into.</summary>
/// <param name="Path">A PATH-style list of folders.</param>
/// <param name="Home">The user's home: anything under it a contained command may be able to write.</param>
/// <param name="Temp">The temp folder, writable by every contained command.</param>
public sealed record BwrapSearch(string Path, string Home, string Temp);

/// <summary>The bubblewrap to use, or null; and the candidates passed over, with why.</summary>
/// <param name="Path">The trusted bubblewrap, or null when none was found.</param>
/// <param name="Rejected">What was found and refused, for the reason line; null when nothing was.</param>
public sealed record BwrapLocation(string? Path, string? Rejected);
