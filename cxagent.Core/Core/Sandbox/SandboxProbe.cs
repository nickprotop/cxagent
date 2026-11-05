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
/// the reason with the fix in it. Null otherwise.</param>
/// <param name="BwrapPath">The bubblewrap the probe ran, for <see cref="SandboxStatusKind.Active"/>: commands run this
/// exact file rather than looking bwrap up on PATH again, so what was tested is what runs.</param>
public sealed record SandboxStatus(SandboxStatusKind Kind, string? Reason, string? BwrapPath = null)
{
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
    /// <summary>This machine.</summary>
    public static SandboxProbeRequest ThisMachine() => new(SandboxProbe.FindOnPath(), OperatingSystem.IsLinux());
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

    /// <summary>Starts bubblewrap with the sandbox's own isolation and nothing to run.</summary>
    /// <param name="request">The machine.</param>
    public static SandboxStatus Run(SandboxProbeRequest request)
    {
        if (!request.IsLinux)
            return new(SandboxStatusKind.Unavailable, "the sandbox is not available on this system — commands are not contained");
        if (request.BwrapPath is not { } bwrap)
            return new(SandboxStatusKind.Unavailable,
                "bubblewrap is not installed — commands are not contained. Install the bubblewrap package to contain them.");

        try
        {
            var psi = new ProcessStartInfo(bwrap)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            foreach (var a in new[] { "--ro-bind", "/", "/", "--unshare-pid", "--new-session", "--", "true" })
                psi.ArgumentList.Add(a);
            using var process = Process.Start(psi)!;
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(5000))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return new(SandboxStatusKind.Failed, "bubblewrap did not start within 5 seconds.");
            }
            if (process.ExitCode == 0) return new(SandboxStatusKind.Active, null, bwrap);
            return new(SandboxStatusKind.Failed,
                $"{stderr.Result.Trim()} — bubblewrap needs unprivileged user namespaces. On Ubuntu, AppArmor must "
                + "allow bwrap (its profile ships with the package); elsewhere check kernel.unprivileged_userns_clone.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            return new(SandboxStatusKind.Failed, $"bubblewrap could not be started: {ex.Message}");
        }
    }
}
