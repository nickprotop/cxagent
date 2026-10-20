using CxAgent.Core.Permissions;

namespace CxAgent.Tests.GateHarness;

/// <summary>
/// One combination the harness judges: a tool and verb, the path it names, the shell's working_dir and
/// environment, where the agent works, the folder's trust and the edit mode.
/// </summary>
public sealed record GateCase(string Tool, string Verb, string Shape, string? WorkingDir, Place Place,
    TrustState Trust, EditMode Mode)
{
    /// <summary>A variable the shell call sets in its own <c>env</c>, or null for none.</summary>
    public string? Env { get; init; }

    /// <summary>How a failure names this case, so it can be found and rerun alone.</summary>
    public string Name =>
        $"{Tool} {Verb} {(Shape.Length == 0 ? "(no path)" : Shape)} · wd={WorkingDir ?? "-"}"
        + (Env is null ? "" : $" · env={Env}") + $" · {Place} · {Trust} · {Mode}";

    /// <summary>Whether the verb only reads.</summary>
    public bool IsRead => GateCases.ReadVerbs.Contains(Verb);
}

/// <summary>The case space of the harness spec, enumerated.</summary>
public static class GateCases
{
    public static readonly string[] FileVerbs =
        ["read", "write", "append", "delete", "replace", "create", "list", "search",
         "copy from", "copy to", "move from", "move to"];

    /// <summary>
    /// Shell verbs as templates; <c>{p}</c> is the shape, empty for a verb run with no path. The <c>cd</c>
    /// forms put a path after a cd (read from the folder the cd entered) and a cd whose target the shell
    /// decides (<c>-</c> is <c>$OLDPWD</c>).
    /// </summary>
    public static readonly string[] ShellVerbs =
        ["cat {p}", "ls {p}", "head {p}", "wc {p}", "grep -r MARK {p}", "grep -R MARK {p}", "cd {p} && ls",
         "cd sub && cat {p}", "cd - && ls {p}", "git -C {p} status", "rm {p}", "touch {p}", "cd sub && touch {p}"];

    public static readonly HashSet<string> ReadVerbs =
        ["read", "list", "search", "cat {p}", "ls {p}", "head {p}", "wc {p}", "grep -r MARK {p}",
         "grep -R MARK {p}", "cd {p} && ls", "cd sub && cat {p}", "cd - && ls {p}", "git -C {p} status"];

    /// <summary>Shell verbs that write (or remove) what they name.</summary>
    public static readonly HashSet<string> ShellWrites = ["rm {p}", "touch {p}", "cd sub && touch {p}"];

    /// <summary>Verbs that also take no path at all: the working folder is what they walk.</summary>
    public static readonly HashSet<string> NoPathVerbs =
        ["list", "search", "ls {p}", "grep -r MARK {p}", "grep -R MARK {p}", "cd - && ls {p}"];

    public static readonly string[] Shapes =
        ["in.txt", "./in.txt", "sub/in.txt", "../in.txt", "../../in.txt", "{abs-inside}", "{abs-real}",
         "{abs-outside}", "~/in.txt", "$HOME/in.txt", "${HOME}/in.txt", "linkin", "linkup", "linkout",
         "subout", "dangling", "linkdir/secret.txt", ".git/hooks/pc", "sub/.git/hooks/pc"];

    public static readonly string?[] WorkingDirs =
        [null, ".", "sub", "..", "{abs-inside}", "{abs-outside}", "linkdir", "missing"];

    /// <summary>
    /// Variables a call may set in its own env, each one able to move what runs or where: OLDPWD and CDPATH
    /// a cd, PATH the program. Crossed with the agent's own folder only — where the command runs is the
    /// working_dir dimension's question.
    /// </summary>
    public static readonly string[] EnvVariables = ["OLDPWD", "CDPATH", "PATH"];

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
            {
                foreach (var wd in WorkingDirs)
                    yield return new GateCase("shell", verb, shape, wd, place, trust, mode);
                foreach (var env in EnvVariables)
                    yield return new GateCase("shell", verb, shape, null, place, trust, mode) { Env = env };
            }
        }
    }

    private static IEnumerable<string> ShapesFor(string verb) =>
        NoPathVerbs.Contains(verb) ? Shapes.Append("") : Shapes;
}
