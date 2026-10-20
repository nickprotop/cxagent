using Xunit;

namespace CxAgent.Tests.GateHarness;

/// <summary>
/// Every path-bearing case — tool and verb × path shape × working_dir × where the agent works × trust ×
/// mode — held to both directions: never silent where its target leaves the boundary, an untrusted folder
/// or a code folder; silent where the rules say it passes. And every read the gate lets through, run for
/// real, to catch the gate and the executor disagreeing about where a path is.
///
/// <para>THE HOLES THIS GATE CAN HAVE ARE CELLS OF A TABLE — an action that raises no request, a read that
/// leaves through a link, a working_dir judged in one folder and run in another, a path resolved against the
/// process directory, a project opened through a symlink — and a hand probe visits the cells somebody
/// thought of. The table is probed whole, every run, so a hole in a cell nobody thought of fails the
/// suite.</para>
///
/// <para>A FAILURE NAMES ITS CASE as one line, the way <see cref="GateCase.Name"/> spells it, so it can be
/// rebuilt alone.</para>
/// </summary>
[Collection("working-directory")]
public sealed class GateHarnessTests(GateFixture f) : IClassFixture<GateFixture>
{
    private readonly GateFixture _f = f;

    [Fact]
    public void ThePolicyPass_HasNoFailures()
    {
        if (!GateFixture.Supported) return;
        var failures = GateHarnessRunner.PolicyFailures(_f);
        Assert.True(failures.Count == 0, Report(failures));
    }

    [Fact]
    public async Task TheRealRunPass_HasNoFailures()
    {
        if (!GateFixture.Supported) return;
        var report = await GateHarnessRunner.RealRunFailures(_f);

        // A PASS THAT RAN NOTHING IS NOT A PASS: the trusted, in-folder reads alone number in the hundreds.
        Assert.True(report.Ran > 500, $"only {report.Ran} cases ran");
        Assert.True(report.Failures.Count == 0, Report(report.Failures));
    }

    private static string Report(IReadOnlyList<string> failures) =>
        $"{failures.Count} failing cases:\n" + string.Join("\n", failures.Take(60));
}
