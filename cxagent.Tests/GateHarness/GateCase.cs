using CxAgent.Core.Permissions;

namespace CxAgent.Tests.GateHarness;

/// <summary>
/// One combination the harness judges: a tool and verb, the path it names, the shell's working_dir, where
/// the agent works, the folder's trust and the edit mode.
/// </summary>
public sealed record GateCase(string Tool, string Verb, string Shape, string? WorkingDir, Place Place,
    TrustState Trust, EditMode Mode)
{
    /// <summary>How a failure names this case, so it can be found and rerun alone.</summary>
    public string Name =>
        $"{Tool} {Verb} {(Shape.Length == 0 ? "(no path)" : Shape)} · wd={WorkingDir ?? "-"} · {Place} · {Trust} · {Mode}";

    /// <summary>Whether the verb only reads.</summary>
    public bool IsRead => GateCases.ReadVerbs.Contains(Verb);
}

/// <summary>The case space of the harness spec, enumerated.</summary>
public static class GateCases
{
    public static readonly string[] FileVerbs =
        ["read", "write", "append", "delete", "replace", "create", "list", "search",
         "copy from", "copy to", "move from", "move to"];

    /// <summary>Shell verbs as templates; <c>{p}</c> is the shape, empty for a verb run with no path.</summary>
    public static readonly string[] ShellVerbs =
        ["cat {p}", "ls {p}", "head {p}", "wc {p}", "grep -r MARK {p}", "cd {p} && ls", "git -C {p} status",
         "rm {p}", "touch {p}"];

    public static readonly HashSet<string> ReadVerbs =
        ["read", "list", "search", "cat {p}", "ls {p}", "head {p}", "wc {p}", "grep -r MARK {p}", "cd {p} && ls",
         "git -C {p} status"];

    /// <summary>Verbs that also take no path at all: the working folder is what they walk.</summary>
    public static readonly HashSet<string> NoPathVerbs = ["list", "search", "ls {p}", "grep -r MARK {p}"];

    public static readonly string[] Shapes =
        ["in.txt", "./in.txt", "sub/in.txt", "../in.txt", "../../in.txt", "{abs-inside}", "{abs-real}",
         "{abs-outside}", "~/in.txt", "linkin", "linkup", "linkout", "dangling", "linkdir/secret.txt",
         ".git/hooks/pc", "sub/.git/hooks/pc"];

    public static readonly string?[] WorkingDirs =
        [null, ".", "sub", "..", "{abs-inside}", "{abs-outside}", "linkdir", "missing"];

    public static IEnumerable<GateCase> All()
    {
        foreach (var place in Enum.GetValues<Place>())
        foreach (var trust in new[] { TrustState.Trusted, TrustState.Untrusted })
        foreach (var mode in new[] { EditMode.AlwaysAsk, EditMode.AcceptEdits, EditMode.Auto })
        {
            foreach (var verb in FileVerbs)
            foreach (var shape in ShapesFor(verb))
                yield return new GateCase("file", verb, shape, null, place, trust, mode);

            foreach (var verb in ShellVerbs)
            foreach (var shape in ShapesFor(verb))
            foreach (var wd in WorkingDirs)
                yield return new GateCase("shell", verb, shape, wd, place, trust, mode);
        }
    }

    private static IEnumerable<string> ShapesFor(string verb) =>
        NoPathVerbs.Contains(verb) ? Shapes.Append("") : Shapes;
}
