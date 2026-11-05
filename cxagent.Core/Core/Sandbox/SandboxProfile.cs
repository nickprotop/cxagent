namespace CxAgent.Core.Sandbox;

/// <summary>How one command runs inside the sandbox.</summary>
/// <param name="Foreground">True when a caller waits for it. A background or detached command must outlive the
/// turn that started it, so it does not die with its parent.</param>
/// <param name="Unset">Variables removed from its environment — see <see cref="SandboxEnv"/>.</param>
public sealed record SandboxRun(bool Foreground, IReadOnlyList<string> Unset);

/// <summary>
/// A <see cref="SandboxLayout"/> as bubblewrap arguments. Pure: the layout already holds real paths that exist.
///
/// <para>ORDER IS THE SECURITY ARGUMENT. bubblewrap applies mounts in the order given, and a later mount over a
/// path wins. So: the whole filesystem read-only, then what may be written, then the code that runs later held
/// read-only again over it, then the secrets hidden over everything — so a writable home still has an empty
/// <c>~/.ssh</c> — and last the call's own job folder put back inside the hidden config folder.</para>
/// </summary>
public static class SandboxProfile
{
    /// <summary>The arguments up to and including <c>--</c>; the caller appends the command.</summary>
    /// <param name="layout">The folders.</param>
    /// <param name="run">How this command runs.</param>
    public static IReadOnlyList<string> Arguments(SandboxLayout layout, SandboxRun run)
    {
        var args = new List<string> { "--ro-bind", "/", "/", "--dev", "/dev", "--proc", "/proc" };

        foreach (var w in layout.Writable) args.AddRange(["--bind", w, w]);
        foreach (var r in layout.ReadOnly) args.AddRange(["--ro-bind", r, r]);
        foreach (var h in layout.HiddenFolders) args.AddRange(["--tmpfs", h]);
        foreach (var h in layout.HiddenFiles) args.AddRange(["--ro-bind", "/dev/null", h]);
        if (layout.JobDir is { } job) args.AddRange(["--bind", job, job]);

        foreach (var name in run.Unset) args.AddRange(["--unsetenv", name]);

        // A SEPARATE PID NAMESPACE, so a contained command cannot signal anything outside, and killing bubblewrap
        // takes everything it started with it. A new session, so it cannot push keystrokes into the terminal.
        args.AddRange(["--unshare-pid", "--new-session"]);
        if (run.Foreground) args.Add("--die-with-parent");
        if (!layout.Network) args.Add("--unshare-net");
        args.AddRange(["--chdir", layout.WorkingDir, "--"]);
        return args;
    }
}
