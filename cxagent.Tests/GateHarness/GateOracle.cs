using System.Collections.Concurrent;
using System.Diagnostics;
using CxAgent.Core.Permissions;

namespace CxAgent.Tests.GateHarness;

/// <summary>What the gate should answer for a case.</summary>
public enum Expect
{
    /// <summary>Passes without a prompt.</summary>
    Silent,

    /// <summary>A person is asked.</summary>
    Asks,

    /// <summary>Auto mode: the reviewer may not be the one to approve it.</summary>
    NotApprovable,

    /// <summary>Either answer is acceptable (only SAFETY is held).</summary>
    AnyAnswer,
}

/// <summary>
/// A path a case touches, and whether it is written (or removed) or only read. <see cref="Target"/> is
/// <see cref="GateOracle.Unknown"/> where the shell decides it at run time.
/// </summary>
public sealed record Touch(string Target, bool Writes);

/// <summary>
/// Where a case's paths really land, and so what the gate should answer — decided WITHOUT any cxagent
/// resolver, by the system's <c>realpath</c>, so the oracle cannot share the bug it is looking for.
///
/// <para>EACH TOOL'S OWN RESOLUTION. The file tool collapses <c>..</c> as text against the agent's folder
/// before anything touches the disk; a shell command hands the raw path to the kernel, which follows links
/// as it goes — <c>linkdir/../x</c> lands in different places for the two. The oracle copies each, then
/// asks <c>realpath -m</c>, which names a path whether or not it exists.</para>
///
/// <para>WHAT THE SHELL DECIDES IS UNKNOWN, and unknown is outside: <c>cd -</c> goes to <c>$OLDPWD</c>, and a
/// call's own env can move a cd or swap the program. <c>$HOME</c> is the one expansion the oracle resolves,
/// as <c>~</c>, because it is the home folder either way.</para>
/// </summary>
public static class GateOracle
{
    /// <summary>A target only the shell knows at run time.</summary>
    public const string Unknown = "\0unknown";

    private static readonly string[] CodeFolders =
        [".git", ".vscode", ".claude", ".idea", ".github", ".husky", ".envrc", ".devcontainer"];
    private static readonly ConcurrentDictionary<string, string> Real = new(StringComparer.Ordinal);

    /// <summary>Every path the case touches, resolved.</summary>
    public static IReadOnlyList<Touch> Touches(GateCase c, GateFixture f) =>
        RawTouches(c, f).Select(t => t.Target == Unknown ? t : t with { Target = RealOf(t.Target) }).ToList();

    /// <summary>
    /// Every path the case touches, as the tool would hand it to the kernel — before <c>realpath</c>, so a
    /// caller can resolve a whole case space in one batch (<see cref="Prime"/>).
    /// </summary>
    public static IReadOnlyList<Touch> RawTouches(GateCase c, GateFixture f)
    {
        var setup = f.SetupFor(c.Place);
        if (c.Tool == "file")
        {
            var shape = FileTarget(Substitute(c.Shape, setup, f), setup.AgentFolder);
            var inside = FileTarget(Path.Combine(setup.AgentFolder, "in.txt"), setup.AgentFolder);
            var copied = FileTarget(Path.Combine(setup.AgentFolder, "copied.txt"), setup.AgentFolder);
            return c.Verb switch
            {
                "read" or "list" or "search" => [new(shape, false)],
                "copy from" => [new(shape, false), new(copied, true)],
                "copy to" => [new(inside, false), new(shape, true)],
                // A MOVE REMOVES ITS SOURCE, so the source is written too (decision 6).
                "move from" => [new(shape, true), new(copied, true)],
                "move to" => [new(inside, true), new(shape, true)],
                _ => [new(shape, true)],
            };
        }

        var runFolder = RunFolder(c.WorkingDir, setup, f);
        var writes = GateCases.ShellWrites.Contains(c.Verb);
        // THE FOLDER IT RUNS IN IS TOUCHED TOO: a working_dir outside is a cd there.
        var touches = new List<Touch> { new(runFolder, false) };
        if (c.Env is not null) touches.Add(new(Unknown, false));

        // A cd FIRST moves where the path is read: `sub` is a folder under the run folder, `-` is the shell's.
        var from = runFolder;
        if (c.Verb.StartsWith("cd sub && ", StringComparison.Ordinal))
        {
            from = runFolder + "/sub";
            touches.Add(new(from, false));
        }
        else if (c.Verb.StartsWith("cd - && ", StringComparison.Ordinal))
        {
            touches.Add(new(Unknown, false));
        }

        var target = ShellTarget(Substitute(c.Shape, setup, f), from);
        touches.Add(new(target, writes));

        // A WALK THAT FOLLOWS LINKS reads where each link under it points.
        if (c.Verb == "grep -R MARK {p}")
            touches.AddRange(LinksUnder(target).Select(link => new Touch(link, false)));
        return touches;
    }

    /// <summary>What the gate should answer.</summary>
    public static Expect Expected(GateCase c, GateFixture f)
    {
        var setup = f.SetupFor(c.Place);
        var boundary = RealOf(setup.Boundary);
        var touches = Touches(c, f);
        var leaves = touches.Any(t => !Inside(t.Target, boundary));
        var codeWrite = touches.Any(t => t.Writes && InCodeFolder(t.Target, boundary));

        if (c.Mode == EditMode.Auto)
            return c.Trust != TrustState.Trusted || leaves || codeWrite ? Expect.NotApprovable : Expect.AnyAnswer;
        if (c.Trust != TrustState.Trusted || leaves || codeWrite) return Expect.Asks;

        if (c.Tool == "shell")
            return c.Verb switch
            {
                _ when GateCases.ShellWrites.Contains(c.Verb) => Expect.Asks,
                // HELD TO SAFETY ONLY: git is off the read-only list, and a walk that follows links is asked
                // about whatever it finds, because the folder it names does not say where it will go.
                "git -C {p} status" or "grep -R MARK {p}" => Expect.AnyAnswer,
                _ => Expect.Silent,
            };

        return c.IsRead ? Expect.Silent
            : c.Mode == EditMode.AcceptEdits ? Expect.Silent : Expect.Asks;
    }

    /// <summary>Whether <paramref name="target"/> (already real) is <paramref name="boundary"/> or under it.</summary>
    public static bool Inside(string target, string boundary)
    {
        if (target == Unknown) return false;
        var b = RealOf(boundary);
        return target == b || target.StartsWith(b + "/", StringComparison.Ordinal);
    }

    /// <summary>The real path, through <c>realpath -m</c> (cached).</summary>
    public static string RealOf(string path) => Real.GetOrAdd(path, Realpath);

    /// <summary>Resolves every path in as few realpath calls as the argument limit allows, filling the cache.</summary>
    public static void Prime(IEnumerable<string> paths)
    {
        foreach (var chunk in paths.Where(p => p != Unknown).Distinct().Where(p => !Real.ContainsKey(p)).Chunk(400))
        {
            var psi = new ProcessStartInfo("realpath") { RedirectStandardOutput = true };
            psi.ArgumentList.Add("-m");
            psi.ArgumentList.Add("-z");
            psi.ArgumentList.Add("--");
            foreach (var p in chunk) psi.ArgumentList.Add(p);
            using var process = Process.Start(psi)!;
            var output = process.StandardOutput.ReadToEnd().Split('\0');
            process.WaitForExit();

            // A realpath WITHOUT -m OR -z (BSD's) fails rather than answering, and an empty answer read as a
            // path would call everything outside — so the oracle refuses to judge instead.
            if (process.ExitCode != 0 || output.Length < chunk.Length)
                throw new InvalidOperationException($"realpath -m -z failed (exit {process.ExitCode}); the harness needs GNU realpath");
            for (var i = 0; i < chunk.Length; i++) Real[chunk[i]] = output[i];
        }
    }

    private static string Realpath(string path)
    {
        Prime([path]);
        return Real[path];
    }

    private static bool InCodeFolder(string target, string boundary)
    {
        var b = RealOf(boundary);
        if (!target.StartsWith(b + "/", StringComparison.Ordinal)) return false;
        return target[(b.Length + 1)..].Split('/').Any(segment => CodeFolders.Contains(segment));
    }

    // EVERY LINK UNDER A FOLDER, walked without following, each one a place a following walk reads. A link
    // into the walked folder is walked anyway; one anywhere else is what the boundary is judged against.
    private static IEnumerable<string> LinksUnder(string folder)
    {
        if (!Directory.Exists(folder)) return [];
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = true };
        return Directory.EnumerateFileSystemEntries(folder, "*", options)
            .Where(entry => new FileInfo(entry).LinkTarget is not null);
    }

    // THE FILE TOOL: `..` collapsed as text against the agent's folder; `~` and `$HOME` are ordinary names.
    private static string FileTarget(string shape, string agentFolder) =>
        Path.GetFullPath(shape.Length == 0 ? "." : shape, agentFolder);

    // THE SHELL: the raw join, links followed by the kernel as it walks; `~` and `$HOME` are the home folder.
    private static string ShellTarget(string shape, string from)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var prefix in new[] { "~/", "$HOME/", "${HOME}/" })
            if (shape.StartsWith(prefix, StringComparison.Ordinal))
                return Path.Combine(home, shape[prefix.Length..]);
        if (shape.Length == 0) return from;
        return Path.IsPathRooted(shape) ? shape : from + "/" + shape;
    }

    private static string RunFolder(string? workingDir, PlaceSetup setup, GateFixture f) => workingDir switch
    {
        null => setup.AgentFolder,
        var wd => SubstituteFolder(wd, setup, f) is var s && Path.IsPathRooted(s) ? s : setup.AgentFolder + "/" + s,
    };

    /// <summary>Fills the placeholders a shape may carry.</summary>
    public static string Substitute(string text, PlaceSetup setup, GateFixture f) => text switch
    {
        "{abs-inside}" => Path.Combine(setup.AgentFolder, "in.txt"),
        "{abs-real}" => Path.Combine(setup.Boundary, "in.txt"),
        "{abs-outside}" => Path.Combine(f.Outside, "in.txt"),
        _ => text,
    };

    /// <summary>Fills the placeholders a working_dir may carry: as a folder, an absolute path names the
    /// folder itself rather than a file in it.</summary>
    public static string SubstituteFolder(string text, PlaceSetup setup, GateFixture f) => text switch
    {
        "{abs-inside}" => setup.AgentFolder,
        "{abs-outside}" => f.Outside,
        _ => text,
    };
}
