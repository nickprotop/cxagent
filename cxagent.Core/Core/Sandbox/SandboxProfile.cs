namespace CxAgent.Core.Sandbox;

/// <summary>How one command runs inside the sandbox.</summary>
/// <param name="Unset">Variables removed from its environment — see <see cref="SandboxEnv"/>.</param>
public sealed record SandboxRun(IReadOnlyList<string> Unset);

/// <summary>
/// A <see cref="SandboxLayout"/> as bubblewrap arguments. Pure: the layout already holds real paths that exist.
///
/// <para>ORDER IS THE SECURITY ARGUMENT. bubblewrap applies mounts in the order given, and a later mount over a
/// path wins. So: the whole filesystem read-only, then what may be written, then the code that runs later held
/// read-only again over it, then the secrets hidden over everything — so a writable home still has an empty
/// <c>~/.ssh</c> — and last the agent's log folder put back, read-only, inside the hidden config folder.</para>
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
        if (layout.Logs is { } logs) args.AddRange(["--ro-bind", logs, logs]);

        foreach (var name in run.Unset) args.AddRange(["--unsetenv", name]);

        // A SEPARATE PID NAMESPACE, so a contained command cannot signal anything outside. A new session, so it
        // cannot push keystrokes into the terminal.
        //
        // NO --die-with-parent. A call handed over at its deadline, or moved to the background, outlives the turn
        // and the app as an uncontained one always has; the flag would kill it at exit. Cancelling still kills the
        // whole tree, from outside.
        args.AddRange(["--unshare-pid", "--new-session"]);
        if (!layout.Network) args.Add("--unshare-net");
        args.AddRange(["--chdir", layout.WorkingDir, "--"]);
        return args;
    }
}
