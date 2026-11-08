namespace CxAgent.Core.Sandbox;

/// <summary>How one command runs inside the sandbox.</summary>
/// <param name="Set">The call's own variables, set INSIDE with <c>--setenv</c>. Never on bubblewrap's process: an
/// <c>LD_PRELOAD</c> there would load into bubblewrap before any namespace exists, outside the sandbox.</param>
public sealed record SandboxRun(IReadOnlyDictionary<string, string> Set);

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

        // THE ROOT FIRST, THEN THE HOME'S DOTFILES OVER IT (when the root is the home), THEN EVERYTHING ELSE WRITABLE —
        // so a cache inside the home is writable again after its dot folder was held.
        for (var i = 0; i < layout.Writable.Count; i++)
        {
            args.AddRange(["--bind", layout.Writable[i], layout.Writable[i]]);
            if (i == 0)
                foreach (var dot in layout.HomeDotfiles) args.AddRange(["--ro-bind", dot, dot]);
        }
        foreach (var r in layout.ReadOnly) args.AddRange(["--ro-bind", r, r]);
        foreach (var h in layout.HiddenFolders) args.AddRange(["--tmpfs", h]);
        foreach (var h in layout.HiddenFiles) args.AddRange(["--ro-bind", "/dev/null", h]);
        foreach (var r in layout.ReplacedFiles) args.AddRange(["--ro-bind", r.Source, r.Target]);
        if (layout.Logs is { } logs) args.AddRange(["--ro-bind", logs, logs]);

        foreach (var (name, value) in run.Set) args.AddRange(["--setenv", name, value]);

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
