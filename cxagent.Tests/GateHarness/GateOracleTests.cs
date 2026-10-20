using CxAgent.Core.Permissions;
using Xunit;

namespace CxAgent.Tests.GateHarness;

/// <summary>
/// The oracle checked by hand on paths whose answer is known, before it judges ten thousand cases. A
/// harness is only as right as its oracle; an oracle that calls a leaking link "inside" passes the very
/// hole the harness exists to catch.
/// </summary>
[Collection("working-directory")]
public sealed class GateOracleTests : IDisposable
{
    private readonly GateFixture _f = new();

    public void Dispose() => _f.Dispose();

    [Fact]
    public void OracleSelfTest()
    {
        if (OperatingSystem.IsWindows()) return;
        var proj = GateOracle.RealOf(_f.Proj);
        var sub = GateOracle.RealOf(_f.Sub);

        Assert.True(GateOracle.Inside(GateOracle.RealOf(Path.Combine(_f.Proj, "in.txt")), proj));
        Assert.False(GateOracle.Inside(GateOracle.RealOf(Path.Combine(_f.Proj, "linkout")), proj));
        // OPENED THROUGH THE ALIAS, the file is still in the project: the boundary is the real folder.
        Assert.True(GateOracle.Inside(GateOracle.RealOf(Path.Combine(_f.Alias, "in.txt")), _f.Proj));
        Assert.False(GateOracle.Inside(GateOracle.RealOf(Path.Combine(_f.Sub, "linkup")), sub));
        Assert.False(GateOracle.Inside(GateOracle.RealOf(Path.Combine(_f.Sub, "..", "in.txt")), sub));
        Assert.False(GateOracle.Inside(GateOracle.RealOf(Path.Combine(_f.Proj, "dangling")), proj));
        // A SIBLING WITH A SHARED PREFIX is not inside: proj-x is not under proj.
        Assert.False(GateOracle.Inside(proj + "-x/in.txt", proj));

        // THE TWO TOOLS DISAGREE on `linkdir/..`: the file tool collapses it as text (back in sub), the
        // shell walks the link first (into outside's parent).
        var file = Touch(new GateCase("file", "read", "linkdir/../in.txt", null, Place.RootedChild,
            TrustState.Trusted, EditMode.AlwaysAsk));
        var shell = Touch(new GateCase("shell", "cat {p}", "linkdir/../in.txt", null, Place.RootedChild,
            TrustState.Trusted, EditMode.AlwaysAsk));
        Assert.Equal(Path.Combine(sub, "in.txt"), file);
        Assert.Equal(Path.Combine(GateOracle.RealOf(_f.Top), "in.txt"), shell);

        // `~` IS THE HOME FOLDER to the shell and an ordinary name to the file tool.
        Assert.False(GateOracle.Inside(Touch(new GateCase("shell", "cat {p}", "~/in.txt", null, Place.NewFolder,
            TrustState.Trusted, EditMode.AlwaysAsk)), proj));
        Assert.Equal(Path.Combine(proj, "~", "in.txt"), Touch(new GateCase("file", "read", "~/in.txt", null,
            Place.NewFolder, TrustState.Trusted, EditMode.AlwaysAsk)));

        // A WORKING_DIR MOVES WHERE A RELATIVE PATH LANDS: `in.txt` from `..` of sub is the project's.
        Assert.Equal(Path.Combine(proj, "in.txt"), Touch(new GateCase("shell", "cat {p}", "in.txt", "..",
            Place.RootedChild, TrustState.Trusted, EditMode.AlwaysAsk)));
    }

    [Fact]
    public void TheOracle_ExpectsWhatTheSpecSays()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.Equal(Expect.Silent, Expected("file", "read", "in.txt", null, TrustState.Trusted, EditMode.AlwaysAsk));
        Assert.Equal(Expect.Asks, Expected("file", "read", "linkout", null, TrustState.Trusted, EditMode.AcceptEdits));
        Assert.Equal(Expect.Asks, Expected("file", "read", "in.txt", null, TrustState.Untrusted, EditMode.AcceptEdits));
        Assert.Equal(Expect.Asks, Expected("file", "write", "in.txt", null, TrustState.Trusted, EditMode.AlwaysAsk));
        Assert.Equal(Expect.Silent, Expected("file", "write", "in.txt", null, TrustState.Trusted, EditMode.AcceptEdits));
        // DECISION 6: a code folder below the top level, and a move OUT of one.
        Assert.Equal(Expect.Asks, Expected("file", "write", "sub/.git/hooks/pc", null, TrustState.Trusted, EditMode.AcceptEdits));
        Assert.Equal(Expect.Asks, Expected("file", "move from", ".git/hooks/pc", null, TrustState.Trusted, EditMode.AcceptEdits));
        Assert.Equal(Expect.Silent, Expected("file", "copy from", ".git/hooks/pc", null, TrustState.Trusted, EditMode.AcceptEdits));
        Assert.Equal(Expect.Silent, Expected("shell", "cat {p}", "in.txt", null, TrustState.Trusted, EditMode.AlwaysAsk));
        Assert.Equal(Expect.Asks, Expected("shell", "cat {p}", "in.txt", "{abs-outside}", TrustState.Trusted, EditMode.AlwaysAsk));
        Assert.Equal(Expect.Asks, Expected("shell", "touch {p}", "in.txt", null, TrustState.Trusted, EditMode.AcceptEdits));
        Assert.Equal(Expect.AnyAnswer, Expected("shell", "git -C {p} status", "sub/in.txt", null, TrustState.Trusted, EditMode.AlwaysAsk));
        Assert.Equal(Expect.NotApprovable, Expected("shell", "cat {p}", "linkout", null, TrustState.Trusted, EditMode.Auto));
        Assert.Equal(Expect.AnyAnswer, Expected("shell", "cat {p}", "in.txt", null, TrustState.Trusted, EditMode.Auto));
    }

    [Fact]
    public void TheCaseSpace_IsWhatTheSpecSays()
    {
        var cases = GateCases.All().ToList();

        Assert.True(cases.Count > 10_000, $"{cases.Count} cases");
        Assert.Equal(cases.Count, cases.Select(c => c.Name).Distinct().Count());
        Assert.All(cases.Where(c => c.Tool == "file"), c => Assert.Null(c.WorkingDir));
        Assert.Equal(Enum.GetValues<Place>().Length, cases.Select(c => c.Place).Distinct().Count());
        Assert.Equal(GateCases.FileVerbs.Length + GateCases.ShellVerbs.Length, cases.Select(c => c.Verb).Distinct().Count());
        Assert.Equal(GateCases.Shapes.Length + 1, cases.Select(c => c.Shape).Distinct().Count());
        Assert.Equal(GateCases.WorkingDirs.Length, cases.Where(c => c.Tool == "shell").Select(c => c.WorkingDir).Distinct().Count());
    }

    private string Touch(GateCase c) => GateOracle.Touches(c, _f)[0].Target;

    private Expect Expected(string tool, string verb, string shape, string? wd, TrustState trust, EditMode mode) =>
        GateOracle.Expected(new GateCase(tool, verb, shape, wd, Place.NewFolder, trust, mode), _f);
}
