using System.Collections.Concurrent;
using CxAgent.Core.Execution;
using CxAgent.Core.Jobs;
using CxAgent.Core.Models;
using CxAgent.Core.Permissions;
using CxAgent.Core.Storage;

namespace CxAgent.Tests.GateHarness;

/// <summary>
/// The harness's two passes over <see cref="GateCases.All"/>: every case judged at the policy level against
/// the oracle, and every silently-allowed read run for real through the gated tools. Each returns the
/// failures as one line per case, empty when the gate behaves.
/// </summary>
public static class GateHarnessRunner
{
    /// <summary>
    /// Every case whose gate answer disagrees with the oracle: silent where it should ask, asking where
    /// the rules say it should pass, or approvable by the reviewer where nobody but a person may approve.
    /// </summary>
    public static IReadOnlyList<string> PolicyFailures(GateFixture f)
    {
        var cases = GateCases.All().ToList();
        Prime(f);
        var policies = new Policies(f);
        var failures = new List<string>();
        foreach (var c in cases)
        {
            var want = GateOracle.Expected(c, f);
            var got = Judge(c, policies.For(c), f);
            if (!Agrees(want, got)) failures.Add($"{c.Name} → {got}, expected {want}");
        }
        return failures;
    }

    /// <summary>
    /// Every read the policy lets through silently, run for real, whose output carries a byte from a folder
    /// outside the boundary: the gate and the executor disagreeing about where a path is. The returned
    /// count of runs lets a caller see the pass did run.
    /// </summary>
    public static async Task<RealRunReport> RealRunFailures(GateFixture f)
    {
        var cases = GateCases.All()
            .Where(c => c.Trust == TrustState.Trusted && c.Mode is EditMode.AlwaysAsk or EditMode.AcceptEdits)
            .Where(c => c.IsRead && c.Verb != "git -C {p} status")
            .ToList();
        Prime(f);
        var policies = new Policies(f);
        var runnable = cases.Where(c => Judge(c, policies.For(c), f) == Answer.Silent).ToList();

        var failures = new ConcurrentBag<string>();
        await Parallel.ForEachAsync(runnable, new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (c, ct) =>
        {
            var run = await Run(c, policies, f, ct);
            var leaked = LeakMarkers(c.Place, f).FirstOrDefault(run.Output.Contains);
            if (leaked is not null && !run.Prompted)
                failures.Add($"{c.Name} → ran unasked and returned '{leaked}'");
        });

        // THE POSITIVE CONTROL: the same path that runs every case returns an inside file's marker from each
        // place. Without it a runner that never reached the executor — every run failing on a missing
        // parameter, say — would report no leaks and look like a pass.
        var controls = new List<string>();
        foreach (var place in Enum.GetValues<Place>())
        {
            var c = new GateCase("shell", "cat {p}", "in.txt", null, place, TrustState.Trusted, EditMode.AlwaysAsk);
            var own = place == Place.RootedChild ? "MARK:sub" : "MARK:proj";
            var run = await Run(c, policies, f, CancellationToken.None);
            if (!run.Output.Contains(own)) controls.Add($"control {c.Name} → '{run.Output.Trim()}', expected {own}");
        }

        return new RealRunReport([.. failures.Order(StringComparer.Ordinal), .. controls], runnable.Count);
    }

    private enum Answer { Silent, Asks, Approvable }

    private static bool Agrees(Expect want, Answer got) => want switch
    {
        Expect.Silent => got == Answer.Silent,
        Expect.Asks => got == Answer.Asks,
        // NOBODY BUT A PERSON: silent is worse than approvable, and both fail.
        Expect.NotApprovable => got == Answer.Asks,
        _ => true,
    };

    // A CASE PASSES WITHOUT A PERSON when every request it raises does: each one silent, or (auto mode) one
    // the reviewer may approve. No request at all passes by default — the class of hole where an action
    // raised nothing to ask about.
    private static Answer Judge(GateCase c, PermissionPolicy policy, GateFixture f)
    {
        var setup = f.SetupFor(c.Place);
        var requests = PermissionPolicy.RequestsFor(c.Tool, Parameters(c, f), setup.AgentFolder)
            .Select(r => r with { Policy = policy })
            .ToList();
        if (requests.All(policy.IsSilentlyAllowed)) return Answer.Silent;
        return requests.All(r => policy.IsSilentlyAllowed(r) || policy.EffectFor(r) == ReviewEffect.MayApprove)
            ? Answer.Approvable : Answer.Asks;
    }

    private static async Task<RunResult> Run(GateCase c, Policies policies, GateFixture f, CancellationToken ct)
    {
        var setup = f.SetupFor(c.Place);
        var policy = policies.For(c);
        var prompted = false;
        var gate = PermissionDecider.ForTesting(policy, policies.StoreFor(c), null, (_, _, _) =>
        {
            prompted = true;
            return Task.FromResult(PermissionChoice.Deny);
        });
        var registry = JobRegistry.CreateWithBuiltins(null, gate, policy);
        registry.TryGet(c.Tool, out var executor);
        var context = new JobContext("harness", Guid.NewGuid().ToString("N"), new Dictionary<string, JobResult>(), null)
        {
            WorkingDirectory = setup.AgentFolder,
            Policy = policy,
        };
        var result = await executor!.ExecuteAsync(Parameters(c, f), context, ct);
        var output = string.Join("\n", result.Output.Values.Select(v => v?.ToString())) + "\n" + result.ErrorMessage;
        return new RunResult(output, prompted);
    }

    /// <summary>The call's arguments, as the model would send them.</summary>
    public static JobParameters Parameters(GateCase c, GateFixture f)
    {
        var setup = f.SetupFor(c.Place);
        var shape = GateOracle.Substitute(c.Shape, setup, f);
        var values = new Dictionary<string, object?>();
        if (c.Tool == "shell")
        {
            values["command"] = c.Verb.Replace("{p}", shape).Replace("  ", " ").Trim();
            if (c.WorkingDir is { } wd) values["working_dir"] = GateOracle.SubstituteFolder(wd, setup, f);
            values["timeout_seconds"] = 5;
            return new JobParameters(values);
        }

        var (action, path, dest) = c.Verb switch
        {
            "copy from" => ("copy", shape, "copied.txt"),
            "copy to" => ("copy", "in.txt", shape),
            "move from" => ("move", shape, "copied.txt"),
            "move to" => ("move", "in.txt", shape),
            var verb => (verb, shape, (string?)null),
        };
        values["action"] = action;
        if (path.Length > 0) values["path"] = path;
        if (dest is not null) values["dest"] = dest;
        values["pattern"] = action == "list" ? "*" : "MARK";
        values["content"] = "x";
        values["replacement"] = "x";
        return new JobParameters(values);
    }

    // A BYTE FROM A FOLDER OUTSIDE THE BOUNDARY, by content or by a name only that folder has. A rooted child's
    // boundary is sub, so the project's own files are outside it too.
    private static string[] LeakMarkers(Place place, GateFixture f) => place == Place.RootedChild
        ? ["MARK:outside", "out-only.txt", "MARK:proj", "proj-only.txt"]
        : ["MARK:outside", "out-only.txt"];

    // THE OFTEN-ASKED PATHS IN ONE realpath CALL; each case's own targets resolve (and cache) as it is judged.
    private static void Prime(GateFixture f)
    {
        var paths = new List<string>();
        foreach (var place in Enum.GetValues<Place>())
        {
            var setup = f.SetupFor(place);
            paths.Add(setup.Boundary);
            paths.Add(setup.AgentFolder);
        }
        GateOracle.Prime(paths);
    }

    /// <summary>
    /// One policy per (place, trust, mode), built once and shared by every case in it: trust stored under
    /// the place's POLICY root — the project as it was opened — and a rooted child's narrowed from the
    /// project's, as the spawner builds it.
    /// </summary>
    private sealed class Policies
    {
        private readonly Dictionary<(Place, TrustState, EditMode), PermissionPolicy> _built = [];
        private readonly Dictionary<(Place, TrustState, EditMode), PermissionRulesStore> _stores = [];

        public Policies(GateFixture f)
        {
            foreach (var place in Enum.GetValues<Place>())
            foreach (var trust in new[] { TrustState.Trusted, TrustState.Untrusted })
            foreach (var mode in new[] { EditMode.AlwaysAsk, EditMode.AcceptEdits, EditMode.Auto })
            {
                var setup = f.SetupFor(place);
                var store = new PermissionRulesStore(new AppPaths(Path.Combine(f.Top, "config", $"{place}-{trust}-{mode}")));
                store.SetTrust(setup.PolicyRoot, trust);
                var policy = new PermissionPolicy(setup.PolicyRoot, store, mode);
                var used = setup.Narrow ? policy.NarrowedTo(setup.AgentFolder) : policy;
                _stores[(place, trust, mode)] = store;
                _built[(place, trust, mode)] = used;
            }
        }

        public PermissionPolicy For(GateCase c) => _built[(c.Place, c.Trust, c.Mode)];

        // WHERE AN "ALWAYS" WOULD BE FILED — never reached, since the hook only denies, but the gate needs one.
        public PermissionRulesStore StoreFor(GateCase c) => _stores[(c.Place, c.Trust, c.Mode)];
    }

    private sealed record RunResult(string Output, bool Prompted);
}

/// <summary>The real-run pass's failures, and how many cases it ran, so an empty list can be told from a
/// pass that ran nothing.</summary>
public sealed record RealRunReport(IReadOnlyList<string> Failures, int Ran);
