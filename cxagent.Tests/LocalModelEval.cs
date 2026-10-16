// THE BACKGROUND SUB-AGENT EVAL, AGAINST A REAL LOCAL MODEL. Ten scenarios, three runs each, scoring
// whether the model uses background agents the way the tool descriptions and receipts teach it to — the
// part of the feature no scripted provider can test, since a script says what the model does instead of
// asking it.
//
// WHAT IT NEEDS: llama.cpp serving qwen3.6-35b-a3b-ud-iq4_xs at http://localhost:8771/v1. Every test here
// is skipped unless CXAGENT_EVAL=1 is set, so the ordinary suite never waits on a server that is not there.
// Run it with:
//
//     CXAGENT_EVAL=1 dotnet test cxagent.Tests --filter Category=Eval
//
// Results are appended to <temp>/cxagent-eval/results.md, one section per run, with the parent's calls
// and its final answer — a scenario's PASS is a heuristic over those, and the calls are what to read.
using System.Runtime.CompilerServices;
using System.Text;
using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using CxAgent.Core.Llm.Providers;
using CxAgent.Core.Models;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// A theory that runs only when CXAGENT_EVAL=1 — see LocalModelEval's header for what it needs.
///
/// <para>A SKIP, NOT AN EARLY RETURN: a test that returns early reports as passed, and a suite that says
/// thirty evals passed when none ran is the failure this exists to avoid.</para>
/// </summary>
public sealed class EvalTheoryAttribute : TheoryAttribute
{
    public EvalTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("CXAGENT_EVAL") != "1")
            Skip = "eval against a local model — set CXAGENT_EVAL=1 to run (see LocalModelEval.cs)";
    }
}

[Trait("Category", "Eval")]
public class LocalModelEval
{
    private static readonly string Out = Path.Combine(Path.GetTempPath(), "cxagent-eval", "results.md");

    /// <summary>
    /// The fixture repository the scenarios work in, beside this file in the source tree.
    ///
    /// <para>COPIED PER RUN rather than used in place: a scenario's agents may write files — a planner's
    /// plan, a builder's edit — and a fixture changed by one run is a different fixture for the next.</para>
    /// </summary>
    private static string FreshShop([CallerFilePath] string here = "")
    {
        var source = Path.Combine(Path.GetDirectoryName(here)!, "eval-shop");
        var copy = Path.Combine(Path.GetTempPath(), "cxagent-eval", "shop-" + Guid.NewGuid().ToString("N"));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (relative.Split(Path.DirectorySeparatorChar)[0] is "bin" or "obj") continue;
            var target = Path.Combine(copy, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        return copy;
    }

    /// <summary>A child that answers by keyword after a delay, or never until stopped.</summary>
    private sealed class StubChild(int delayMs, bool slow) : ILlmProvider
    {
        public string ProviderId => "stub"; public string ModelId => "stub"; public string DisplayName => "Stub";
        public bool SupportsToolCalling => true; public bool SupportsStreaming => false;
        public async Task<LlmResponse> ChatAsync(List<ChatMessage> m, List<ToolDefinition>? t, CancellationToken ct)
        {
            await Task.Delay(slow ? Timeout.Infinite : delayMs, ct);
            var all = string.Join("\n", m.Select(x => x.Content)).ToLowerInvariant();
            var lastUser = m.LastOrDefault(x => x.Role == "user")?.Content?.ToLowerInvariant() ?? "";
            string text =
                lastUser.Contains("batchorders") || lastUser.Contains("error handling") ? "BatchOrders.cs has no try/catch around PlaceOrder; a failed order aborts the whole batch."
                : all.Contains("plan") && all.Contains("discount") ? "PLAN: 1. Add DiscountCode to Order. 2. Validate in CheckoutController. 3. Apply in PriceCalculator. Plan file written."
                : all.Contains("test") ? "Tests covering checkout: CheckoutTests.cs covers CheckoutController only. Nothing covers BatchOrders."
                : all.Contains("caller") || all.Contains("placeorder") ? "PlaceOrder is called from CheckoutController.cs:42 and BatchOrders.cs:17."
                : "Done: nothing notable found.";
            return new LlmResponse { Text = text, StopReason = "end_turn" };
        }
        public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(List<ChatMessage> m, List<ToolDefinition>? t,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        { var r = await ChatAsync(m, t, ct); yield return new LlmStreamChunk(r.Text, null, true); }
    }

    private sealed record Rig(Session Session, BufferedChatSink Sink, BufferedJobPanel Jobs, SessionManager Manager);

    private static Rig Open(ILlmProvider kid, bool background = true)
    {
        var local = new OpenAiCompatibleProvider(new OpenAiProviderOptions
        {
            ProviderId = "local", DisplayName = "local", Model = "qwen3.6-35b-a3b-ud-iq4_xs.gguf",
            BaseUrl = "http://localhost:8771/v1",
        });
        var registry = ProviderRegistry.FromProviders(
            new Dictionary<string, ILlmProvider> { ["local"] = local, ["kid"] = kid }, "local");
        var types = new Dictionary<string, AgentTypeConfig>();
        foreach (var name in new[] { "general", "explore", "planner", "builder", "reviewer" })
            types[name] = new AgentTypeConfig("", Provider: "kid");
        var config = new ResolvedConfig(registry.Use("local"), new ProviderCatalog(Instances: registry, AgentTypes: types), []);
        var dir = Path.Combine(Path.GetTempPath(), "eval-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var manager = SessionManager.Create(new AppPaths(dir));
        var sink = new BufferedChatSink();
        var jobs = new BufferedJobPanel();
        var session = manager.Open(FreshShop(), config, new SessionPorts
        {
            Observer = sink, ToolObserver = jobs, BackgroundAgents = background,
        }, AgentMode.FanOut);
        return new Rig(session, sink, jobs, manager);
    }

    /// <summary>
    /// Settled: no background run still going, every report handed over, and the session idle for three
    /// seconds in a row — a woken turn starts a moment after a report lands, so one idle reading is not
    /// enough.
    /// </summary>
    private static async Task Idle(Session s, int seconds = 240)
    {
        var quiet = 0;
        for (var i = 0; i < seconds * 5 && quiet < 15; i++)
        {
            await Task.Delay(200);
            var going = s.SubAgents.Background().Any(r => !r.Handoff.Ended);
            quiet = s.IsBusy || s.TurnOwned || going ? 0 : quiet + 1;
        }
    }

    /// <summary>Settled as far as the parent goes — for scenarios whose child never ends on its own.</summary>
    private static async Task ParentIdle(Session s, int seconds = 120)
    {
        var quiet = 0;
        for (var i = 0; i < seconds * 5 && quiet < 15; i++)
        {
            await Task.Delay(200);
            quiet = s.IsBusy || s.TurnOwned ? 0 : quiet + 1;
        }
    }

    /// <summary>The parent's own tool calls, in order, with their arguments.</summary>
    private static List<string> CallsOf(Rig r) =>
        r.Session.Host!.Context.Messages.Where(m => m.ToolCalls is { Count: > 0 })
            .SelectMany(m => m.ToolCalls!).Select(c => $"{c.Name} {c.Arguments.GetRawText()}").ToList();

    private static string Final(Rig r) =>
        r.Session.Host!.Context.Messages.LastOrDefault(m => m.Role == "assistant" && !string.IsNullOrWhiteSpace(m.Content))?.Content ?? "";

    private static void Record(string scenario, int run, bool pass, List<string> calls, string final)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Out)!);
            File.AppendAllText(Out, $"\n### {scenario} — run {run}: {(pass ? "PASS" : "FAIL")}\n"
                + string.Join("\n", calls.Select(c => "- " + (c.Length > 220 ? c[..220] + "…" : c)))
                + $"\n> {final.Replace("\n", " ").Trim()[..Math.Min(400, final.Trim().Length)]}\n");
        }
    }

    private static readonly object Gate = new();

    public static IEnumerable<object[]> Runs => [[1], [2], [3]];

    [EvalTheory, MemberData(nameof(Runs))]
    public async Task S01_TwoIndependentSearches(int run)
    {
        var r = Open(new StubChild(4000, false));
        r.Session.Submit("Use two sub-agents in parallel: one finds every caller of PlaceOrder, the other lists the tests that cover checkout. Then tell me which caller has no test.");
        await Idle(r.Session);
        var calls = CallsOf(r);
        var spawns = calls.Count(c => c.StartsWith("agent "));
        var polls = calls.Count(c => c.StartsWith("task_list"));
        var final = Final(r);
        Record("S01 two independent searches", run, spawns >= 2 && polls <= 1 && final.Contains("BatchOrders"), calls, final);
    }

    [EvalTheory, MemberData(nameof(Runs))]
    public async Task S02_PlannerThenBuilder(int run)
    {
        var r = Open(new StubChild(3000, false));
        r.Session.Submit("Spawn a planner to plan adding a discount code to checkout, then spawn a builder to carry out that plan.");
        await Idle(r.Session);
        var calls = CallsOf(r);
        var first = calls.FirstOrDefault(c => c.StartsWith("agent ")) ?? "";
        var waited = first.Contains("\"background\":false") || calls.Any(c => c.StartsWith("task_output") && c.Contains("\"wait\":true"));
        var builderAfter = calls.Count(c => c.StartsWith("agent ")) >= 2;
        Record("S02 planner then builder", run, waited && builderAfter, calls, Final(r));
    }

    [EvalTheory, MemberData(nameof(Runs))]
    public async Task S03_StopTheSlowOne(int run)
    {
        var r = Open(new StubChild(0, slow: true));
        r.Session.Submit("Send a sub-agent in the background to find every caller of PlaceOrder.");
        await ParentIdle(r.Session);
        r.Session.Submit("That is taking too long — stop it.");
        await ParentIdle(r.Session);
        var calls = CallsOf(r);
        Record("S03 stop the slow one", run, calls.Any(c => c.StartsWith("task_stop")), calls, Final(r));
        r.Session.SubAgents.StopAll();
    }

    [EvalTheory, MemberData(nameof(Runs))]
    public async Task S04_WhatIsItDoing(int run)
    {
        var r = Open(new StubChild(0, slow: true));
        r.Session.Submit("Send a sub-agent in the background to find every caller of PlaceOrder.");
        await ParentIdle(r.Session);
        r.Session.Submit("How far has it got?");
        await ParentIdle(r.Session);
        var calls = CallsOf(r);
        var last = calls.Skip(calls.FindIndex(c => c.StartsWith("agent ")) + 1).ToList();
        var pass = last.Any(c => (c.StartsWith("task_output") && !c.Contains("\"wait\":true")) || c.StartsWith("task_list"));
        Record("S04 what is it doing", run, pass, calls, Final(r));
        r.Session.SubAgents.StopAll();
    }

    [EvalTheory, MemberData(nameof(Runs))]
    public async Task S05_ReportArrivesMidTurn(int run)
    {
        var r = Open(new StubChild(6000, false));
        r.Session.Submit("Send a sub-agent to find every caller of PlaceOrder. While it works, read the README in this folder and summarise it. Then give me both.");
        await Idle(r.Session);
        var final = Final(r);
        Record("S05 report mid-turn", run, final.Contains("CheckoutController") || final.Contains("BatchOrders"), CallsOf(r), final);
    }

    [EvalTheory, MemberData(nameof(Runs))]
    public async Task S06_ReportWhileIdle(int run)
    {
        var r = Open(new StubChild(8000, false));
        r.Session.Submit("Send a sub-agent in the background to find every caller of PlaceOrder. You do not need to wait for it; just confirm you sent it.");
        await Idle(r.Session);
        var final = Final(r);
        Record("S06 report while idle (wake)", run, final.Contains("CheckoutController") || final.Contains("BatchOrders"), CallsOf(r), final);
    }

    [EvalTheory, MemberData(nameof(Runs))]
    public async Task S07_StoppedNote(int run)
    {
        var r = Open(new StubChild(0, slow: true));
        r.Session.Submit("Send a sub-agent in the background to find every caller of PlaceOrder.");
        await ParentIdle(r.Session);
        foreach (var a in r.Session.SubAgents.All()) r.Session.SubAgents.Stop(a.Name, byUser: true);
        await Task.Delay(1000);
        var before = CallsOf(r).Count(c => c.StartsWith("agent "));
        r.Session.Submit("What happened to the search?");
        await ParentIdle(r.Session);
        var calls = CallsOf(r);
        var final = Final(r).ToLowerInvariant();
        var pass = (final.Contains("stop") || final.Contains("cancel")) && calls.Count(c => c.StartsWith("agent ")) == before;
        Record("S07 stopped note acknowledged, not re-spawned", run, pass, calls, Final(r));
    }

    [EvalTheory, MemberData(nameof(Runs))]
    public async Task S08_UserStoppedThenCarryOn(int run)
    {
        var r = Open(new StubChild(0, slow: true));
        r.Session.Submit("Send a sub-agent in the background to find every caller of PlaceOrder.");
        await ParentIdle(r.Session);
        foreach (var a in r.Session.SubAgents.All()) r.Session.SubAgents.Stop(a.Name, byUser: true);
        await Task.Delay(1000);
        r.Session.Submit("Carry on.");
        await ParentIdle(r.Session);
        var calls = CallsOf(r);
        var sends = calls.Count(c => c.StartsWith("agent_send"));
        var refusedThenFine = !calls.Any(c => c.StartsWith("agent_send")) || Final(r).ToLowerInvariant().Contains("stop");
        Record("S08 user-stopped + carry on", run, sends == 0 || refusedThenFine, calls, Final(r));
        r.Session.SubAgents.StopAll();
    }

    [EvalTheory, MemberData(nameof(Runs))]
    public async Task S09_AskTheFinderForMore(int run)
    {
        var r = Open(new StubChild(2000, false));
        r.Session.Submit("Use a sub-agent with background: false to find every caller of PlaceOrder.");
        await Idle(r.Session);
        var spawnsBefore = CallsOf(r).Count(c => c.StartsWith("agent "));
        r.Session.Submit("Ask that same agent to also check BatchOrders for error handling.");
        await Idle(r.Session);
        var calls = CallsOf(r);
        var pass = calls.Any(c => c.StartsWith("agent_send")) && calls.Count(c => c.StartsWith("agent ")) == spawnsBefore;
        Record("S09 agent_send, not a new spawn", run, pass, calls, Final(r));
    }

    [EvalTheory, MemberData(nameof(Runs))]
    public async Task S10_Headless(int run)
    {
        var r = Open(new StubChild(2000, false), background: false);
        r.Session.Submit("Use two sub-agents: one finds every caller of PlaceOrder, the other lists the tests that cover checkout. Then tell me which caller has no test.");
        await Idle(r.Session);
        var final = Final(r);
        var calls = CallsOf(r);
        Record("S10 headless (foreground)", run, final.Contains("BatchOrders") && !calls.Any(c => c.StartsWith("task_")), calls, final);
    }
}
