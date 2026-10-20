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

/// <summary>A path a case touches, and whether it is written (or removed) or only read.</summary>
public sealed record Touch(string Target, bool Writes);

/// <summary>
/// Where a case's paths really land, and so what the gate should answer — decided WITHOUT any cxagent
/// resolver, by the system's <c>realpath</c>, so the oracle cannot share the bug it is looking for.
///
/// <para>EACH TOOL'S OWN RESOLUTION. The file tool collapses <c>..</c> as text against the agent's folder
/// before anything touches the disk; a shell command hands the raw path to the kernel, which follows links
/// as it goes — <c>linkdir/../x</c> lands in different places for the two. The oracle copies each, then
/// asks <c>realpath -m</c>, which names a path whether or not it exists.</para>
/// </summary>
public static class GateOracle
{
    private static readonly string[] CodeFolders = [".git", ".vscode", ".claude", ".idea"];
    private static readonly ConcurrentDictionary<string, string> Real = new(StringComparer.Ordinal);

    /// <summary>Every path the case touches, resolved.</summary>
    public static IReadOnlyList<Touch> Touches(GateCase c, GateFixture f)
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
        var target = ShellTarget(Substitute(c.Shape, setup, f), runFolder);
        var writes = c.Verb is "rm {p}" or "touch {p}";
        // THE FOLDER IT RUNS IN IS TOUCHED TOO: a working_dir outside is a cd there (decision 8).
        return [new(target, writes), new(ShellTarget("", runFolder), false)];
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
                "rm {p}" or "touch {p}" => Expect.Asks,
                "git -C {p} status" => Expect.AnyAnswer,
                _ => Expect.Silent,
            };

        return c.IsRead ? Expect.Silent
            : c.Mode == EditMode.AcceptEdits ? Expect.Silent : Expect.Asks;
    }

    /// <summary>Whether <paramref name="target"/> (already real) is <paramref name="boundary"/> or under it.</summary>
    public static bool Inside(string target, string boundary)
    {
        var b = RealOf(boundary);
        return target == b || target.StartsWith(b + "/", StringComparison.Ordinal);
    }

    /// <summary>The real path, through <c>realpath -m</c> (cached).</summary>
    public static string RealOf(string path) => Real.GetOrAdd(path, Realpath);

    /// <summary>Resolves every path in one realpath call each batch, filling the cache.</summary>
    public static void Prime(IEnumerable<string> paths)
    {
        foreach (var chunk in paths.Distinct().Where(p => !Real.ContainsKey(p)).Chunk(400))
        {
            var psi = new ProcessStartInfo("realpath") { RedirectStandardOutput = true };
            psi.ArgumentList.Add("-m");
            psi.ArgumentList.Add("-z");
            psi.ArgumentList.Add("--");
            foreach (var p in chunk) psi.ArgumentList.Add(p);
            using var process = Process.Start(psi)!;
            var output = process.StandardOutput.ReadToEnd().Split('\0');
            process.WaitForExit();
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

    // THE FILE TOOL: `..` collapsed as text against the agent's folder; `~` is an ordinary name.
    private static string FileTarget(string shape, string agentFolder) =>
        RealOf(Path.GetFullPath(shape.Length == 0 ? "." : shape, agentFolder));

    // THE SHELL: the raw join, links followed by the kernel as it walks; `~` is the home folder.
    private static string ShellTarget(string shape, string runFolder)
    {
        if (shape.StartsWith("~/", StringComparison.Ordinal))
            return RealOf(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), shape[2..]));
        if (shape.Length == 0) return RealOf(runFolder);
        return RealOf(Path.IsPathRooted(shape) ? shape : runFolder + "/" + shape);
    }

    private static string RunFolder(string? workingDir, PlaceSetup setup, GateFixture f) => workingDir switch
    {
        null => setup.AgentFolder,
        var wd => Substitute(wd, setup, f) is var s && Path.IsPathRooted(s) ? s : setup.AgentFolder + "/" + s,
    };

    /// <summary>Fills the placeholders a shape or working_dir may carry.</summary>
    public static string Substitute(string text, PlaceSetup setup, GateFixture f) => text switch
    {
        "{abs-inside}" => Path.Combine(setup.AgentFolder, "in.txt"),
        "{abs-real}" => Path.Combine(setup.Boundary, "in.txt"),
        "{abs-outside}" => Path.Combine(f.Outside, "in.txt"),
        _ => text,
    };
}
