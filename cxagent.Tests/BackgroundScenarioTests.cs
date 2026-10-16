using CxAgent.Core.Agents;
using CxAgent.Core.Commands;
using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Permissions;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The background-agent scenario matrix, end to end: a real session, a real delivery, a real spawner.
///
/// <para>EACH ROW OVER THE WHOLE PATH, not over the piece that decides it. The unit tests beside these
/// pin each decision on its own; what they cannot see is two correct pieces composed wrongly — a report
/// built right and handed to a delivery that wakes the wrong agent, or a mailbox drained a lap late.
/// Every test here starts at <c>Session.Submit</c> and ends at what the parent's model was sent.</para>
/// </summary>
public class BackgroundScenarioTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bg-scenario-" + Guid.NewGuid().ToString("N"));
    private SessionManager? _manager;

    public BackgroundScenarioTests() => Directory.CreateDirectory(_dir);

    /// <summary>
    /// Disposes the manager, then removes the temp root, tolerating a write still landing in it.
    ///
    /// <para>A WAKE IS A TURN NOBODY AWAITS — the behaviour under test — so its log may still be written
    /// while the directory goes. Cleanup of a temp folder must not fail a test that passed.</para>
    /// </summary>
    public void Dispose()
    {
        _manager?.Dispose();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
                return;
            }
            catch (IOException) { Thread.Sleep(50); }
            catch (UnauthorizedAccessException) { Thread.Sleep(50); }
        }
    }

    // ---- the rig ---------------------------------------------------------------------------------------

    /// <summary>
    /// What a scene varies. Every member defaults to the ordinary case, so a test names only what its
    /// row is about.
    /// </summary>
    private sealed record SceneSetup
    {
        /// <summary>The provider every agent type's children answer from.</summary>
        public required ILlmProvider Child { get; init; }

        /// <summary>
        /// A second child provider for the <c>explore</c> type — for the rows that need two children
        /// finishing at different moments, which one shared provider cannot arrange.
        /// </summary>
        public ILlmProvider? Explore { get; init; }

        /// <summary>maxConcurrentAgents, or null for uncapped.</summary>
        public int? Cap { get; init; }

        /// <summary>The permission gate, or null for none — every tool runs ungated.</summary>
        public IPermissionGate? Gate { get; init; }

        /// <summary>The general type's turn ceiling, for the capped-child row.</summary>
        public int? ChildMaxTurns { get; init; }

        /// <summary>A third instance, named "spare", for the row that switches the session's model.</summary>
        public ILlmProvider? Spare { get; init; }
    }

    /// <summary>A running scene, and what a test reads from it.</summary>
    private sealed record Scene(Session Session, ScriptedProvider Parent, BufferedChatSink Sink, ResolvedConfig Config);

    /// <summary>
    /// A FanOut session whose parent answers from a script and whose children answer from their own
    /// provider instance.
    ///
    /// <para>TWO INSTANCES, EVERY CHILD TYPE ROUTED TO ITS OWN — one shared provider would interleave
    /// parent and child requests, and every row here is about the ORDER the parent reads things in. This
    /// is AgentDeliveryTests.AReport_ReachesAParentThatSwitchedToSingle's wiring, extended to every
    /// built-in type so a planner or an explorer is routed too.</para>
    /// </summary>
    private Scene Open(ScriptedProvider parent, SceneSetup setup)
    {
        var providers = new Dictionary<string, ILlmProvider> { ["local"] = parent, ["kid"] = setup.Child };
        if (setup.Explore is { } explore) providers["fg"] = explore;
        if (setup.Spare is { } spare) providers["spare"] = spare;

        var types = new Dictionary<string, AgentTypeConfig>();
        foreach (var name in BuiltinAgentTypes.All.Select(t => t.Name).Append(AgentTypeCatalog.DefaultTypeName))
            types[name] = new AgentTypeConfig("", Provider: "kid");
        if (setup.Explore is not null) types["explore"] = new AgentTypeConfig("", Provider: "fg");
        if (setup.ChildMaxTurns is { } turns)
            types[AgentTypeCatalog.DefaultTypeName] = new AgentTypeConfig("", Provider: "kid", MaxTurns: turns);

        var registry = ProviderRegistry.FromProviders(providers, "local");
        var config = new ResolvedConfig(registry.Use("local"),
            new ProviderCatalog(Instances: registry, AgentTypes: types, MaxConcurrentAgents: setup.Cap), []);

        _manager = SessionManager.Create(new ProcessSetup
        {
            Paths = new AppPaths(_dir),
            BuildGate = setup.Gate is { } gate ? _ => gate : null,
        });
        var sink = new BufferedChatSink();
        var session = _manager.Open(_dir, config,
            new SessionPorts { Observer = sink, ToolObserver = new BufferedJobPanel() }, AgentMode.FanOut);
        return new Scene(session, parent, sink, config);
    }

    private Scene Open(ScriptedProvider parent, ILlmProvider child) => Open(parent, new SceneSetup { Child = child });

    /// <summary>Waits until no turn is running or owned.</summary>
    private static async Task Idle(Session s) => Assert.True(await Poll.Until(() => !s.IsBusy && !s.TurnOwned),
        "the session never went idle");

    /// <summary>Starts a user turn and waits for it to end.</summary>
    private static async Task Turn(Session s, string text)
    {
        await Assert.IsType<Session.SubmitOutcome.Started>(s.Submit(text)).Turn;
        await Idle(s);
    }

    /// <summary>
    /// Lets a moment pass and answers whether the parent stayed at this many requests — for the rows
    /// whose point is that NOTHING more happens.
    ///
    /// <para>A NEGATIVE NEEDS A WINDOW, and this one is short on purpose: every wake in this file starts
    /// within a few milliseconds of its cause, so 200 ms is many times the latency being ruled out
    /// without being a sleep the suite pays for twice.</para>
    /// </summary>
    private static async Task<bool> StaysAt(ScriptedProvider p, int calls)
    {
        await Task.Delay(200);
        return p.Calls == calls;
    }

    private static LlmResponse Answer(string text) => new() { Text = text, StopReason = "end_turn" };

    private static LlmResponse Use(params ToolCall[] calls) => new() { Text = "", StopReason = "tool_use", ToolCalls = [.. calls] };

    private static BackgroundRun RunOf(Scene s, string name = "find-thing") =>
        s.Session.SubAgents.BackgroundOf(name) ?? throw new InvalidOperationException($"no run '{name}'");

    // ---- S18: a stop racing the answer ---------------------------------------------------------------

    /// <summary>
    /// S18. A STOP RACING THE ANSWER: the user's stop lands while the child's model is returning its
    /// answer — before the runner has marked it answered, so the stop is accepted and the user is told
    /// "stopped". The parent must then be told the same thing: a cancelled report, left rather than woken.
    ///
    /// <para>Guards BackgroundRunner.RunAsync's <c>if (!run.MarkAnswered()) result = result with { Outcome =
    /// SendOutcome.Cancelled }</c>: without it the run finishes as Finished with a "completed" report that
    /// wakes the parent, while the run still says the user stopped it — so the parent's own agent_send to
    /// a child it was told completed is refused.</para>
    /// </summary>
    [Fact]
    public async Task StopRacingTheAnswer_EndToEnd()
    {
        var parent = new ScriptedProvider(Use(Calls.Spawn(id: "s1")), Answer("started"), Answer("read it"));
        Session? session = null;
        var child = new StoppingProvider("found it", () => session!.StopTask("stop find-thing"));
        var s = Open(parent, child);
        session = s.Session;

        await Turn(s.Session, "go");
        Assert.True(await Poll.Until(() => RunOf(s).Handoff.Ended));

        // THE STOP WAS ACCEPTED: the user was told so, and the run remembers the user did it.
        Assert.Contains(s.Sink.Notices, n => n.StartsWith("stopped 'find-thing'"));
        var run = RunOf(s);
        Assert.True(run.StoppedByUser);

        // SO THE PARENT IS TOLD IT WAS STOPPED — not that it completed.
        Assert.Equal(BackgroundState.Stopped, run.State);
        Assert.Contains("state=\"cancelled\"", run.Handoff.Result!);
    }
}

/// <summary>
/// Answers once, and in the instant before the answer returns, does something — a user's stop landing as
/// the child's model finishes.
/// </summary>
internal sealed class StoppingProvider(string answer, Action atTheAnswer) : ILlmProvider
{
    private int _calls;

    public string ProviderId => "stopping";
    public string ModelId => "stopping";
    public string DisplayName => "Stopping";
    public bool SupportsToolCalling => true;
    public bool SupportsStreaming => false;

    public Task<LlmResponse> ChatAsync(List<ChatMessage> messages, List<ToolDefinition>? tools, CancellationToken ct)
    {
        if (Interlocked.Increment(ref _calls) == 1) atTheAnswer();
        return Task.FromResult(new LlmResponse { Text = answer, StopReason = "end_turn" });
    }

    public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(List<ChatMessage> messages, List<ToolDefinition>? tools,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var r = await ChatAsync(messages, tools, ct);
        yield return new LlmStreamChunk(r.Text, null, true);
    }
}

/// <summary>
/// A parent model that answers from a script, records every request, and counts the most in flight.
///
/// <para>NOT MockLlmProvider: that one throws on an empty queue, is not safe across the threads a wake
/// arrives on, and keeps only the last request — while these rows ask what the parent read across
/// SEVERAL requests, and whether two ever overlapped. A step may be held on a task, for the rows that
/// need a report to land while a particular request is in flight.</para>
/// </summary>
internal sealed class ScriptedProvider : ILlmProvider
{
    private readonly object _gate = new();
    private readonly Queue<(LlmResponse Response, Task? Hold)> _steps = new();
    private readonly List<string> _requests = [];
    private int _inFlight, _maxInFlight;

    public ScriptedProvider(params LlmResponse[] script)
    {
        foreach (var r in script) Then(r);
    }

    /// <summary>Adds a step; with a hold, its answer waits for that task.</summary>
    public ScriptedProvider Then(LlmResponse response, Task? hold = null)
    {
        lock (_gate) _steps.Enqueue((response, hold));
        return this;
    }

    public int Calls { get { lock (_gate) return _requests.Count; } }

    public int MaxInFlight => Volatile.Read(ref _maxInFlight);

    /// <summary>Each request's messages, joined — what the model was shown, in order.</summary>
    public IReadOnlyList<string> Requests { get { lock (_gate) return [.. _requests]; } }

    public string Last { get { lock (_gate) return _requests[^1]; } }

    /// <summary>Every request, joined — for "did the model ever see this".</summary>
    public string All { get { lock (_gate) return string.Join("\n", _requests); } }

    public string ProviderId => "scripted";
    public string ModelId => "scripted";
    public string DisplayName => "Scripted";
    public bool SupportsToolCalling => true;
    public bool SupportsStreaming => false;

    public async Task<LlmResponse> ChatAsync(List<ChatMessage> messages, List<ToolDefinition>? tools, CancellationToken ct)
    {
        var now = Interlocked.Increment(ref _inFlight);
        int seen;
        while (now > (seen = Volatile.Read(ref _maxInFlight))
               && Interlocked.CompareExchange(ref _maxInFlight, now, seen) != seen) { }
        try
        {
            (LlmResponse Response, Task? Hold) step;
            lock (_gate)
            {
                _requests.Add(string.Join("\n", messages.Select(m => m.Content)));
                // RUN OFF THE END OF THE SCRIPT AND THE MODEL SAYS "ok": a wake the test did not
                // script is then visible as an extra request rather than as a throw on another thread.
                step = _steps.Count > 0 ? _steps.Dequeue() : (new LlmResponse { Text = "ok", StopReason = "end_turn" }, null);
            }

            // A DEADLINE, so a test that forgets to release fails instead of hanging the suite.
            if (step.Hold is { } hold) await hold.WaitAsync(TimeSpan.FromSeconds(15), ct);
            return step.Response;
        }
        finally { Interlocked.Decrement(ref _inFlight); }
    }

    public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(List<ChatMessage> messages, List<ToolDefinition>? tools,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var r = await ChatAsync(messages, tools, ct);
        for (var i = 0; i < r.ToolCalls.Count - 1; i++) yield return new LlmStreamChunk(null, r.ToolCalls[i], IsFinal: false);
        yield return new LlmStreamChunk(r.Text, r.ToolCalls.LastOrDefault(), IsFinal: true, Usage: r.Usage, StopReason: r.StopReason);
    }
}
