using System.Text.Json;
using CxAgent.Core.Colleagues;
using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The desk against a spawner that records what it was asked: which model a colleague is put on, two brings at once,
/// and what is left marked when a colleague could not be equipped.
/// </summary>
public sealed class ColleagueDeskReviewTests : IDisposable
{
    private readonly string _config = Directory.CreateTempSubdirectory("desk-review-config-").FullName;
    private readonly string _project = Directory.CreateTempSubdirectory("desk-review-proj-").FullName;
    private readonly SubAgentStore _workers = new();
    private readonly List<WorkerMark> _marks = [];
    private readonly MockLlmProvider _main = new("m");

    public void Dispose()
    {
        foreach (var d in new[] { _config, _project })
            try { Directory.Delete(d, recursive: true); } catch (IOException) { }
    }

    private ColleagueStore Library => new(_config);

    private ColleagueDesk Desk(Recording spawner) => new(new ColleagueDeskParts
    {
        Library = Library,
        Workers = _workers,
        Spawner = spawner,
        Folder = _project,
        SessionId = () => "session",
        NoteWorker = _marks.Add,
        Models = name => name is null or "main" ? new ModelChoice(_main, "main:m", 200_000, "main") : null,
    });

    private void Charting() =>
        Library.Create(new ColleagueRecord { Name = "Charting", Type = "general", Created = DateTimeOffset.UtcNow, Instance = "main" });

    private static BringOrder Bring(string? model = null) => new("Charting", "chart it", model, false, "lead");

    /// <summary>
    /// THE MODEL THAT PASSED THE 128k CHECK IS THE ONE IT RUNS ON — named in the order even when it is the session's
    /// own, or the spawner falls back to the type's routing, which may be another, smaller model.
    /// </summary>
    [Fact]
    public async Task Bring_names_the_checked_model_even_when_it_is_the_session_s()
    {
        Charting();
        var spawner = new Recording(_workers);

        await Desk(spawner).BringAsync(Bring(), CancellationToken.None);
        // DISMISSED FIRST: a colleague still here would be handed the task rather than brought in again.
        _workers.Forget("charting");
        await Desk(new Recording(_workers)).BringAsync(Bring("main"), CancellationToken.None);

        Assert.Equal("main", spawner.Orders.Single().Instance);
        Assert.Equal("main", _marks.First(m => m.Name is not null).Instance);
    }

    /// <summary>
    /// ALREADY HERE ON ANOTHER MODEL, A BRING NAMING A MODEL SENDS NOTHING: the task went to the worker here, on the model
    /// it was brought on — a paid one, where the user had asked for the local — and nothing said so.
    /// </summary>
    [Fact]
    public async Task A_bring_naming_another_model_for_a_colleague_here_is_refused()
    {
        Charting();
        var spawner = new Recording(_workers);
        var desk = new ColleagueDesk(new ColleagueDeskParts
        {
            Library = Library, Workers = _workers, Spawner = spawner, Folder = _project, SessionId = () => "session",
            NoteWorker = _marks.Add,
            Models = name => name switch
            {
                null or "main" => new ModelChoice(_main, "main:m", 200_000, "main"),
                "local" => new ModelChoice(new MockLlmProvider("q"), "local:q", 200_000, "local"),
                _ => null,
            },
        });
        await desk.BringAsync(new BringOrder("Charting", "", null, true, "lead"), CancellationToken.None);

        var said = await desk.BringAsync(Bring("local"), CancellationToken.None);

        Assert.Contains("the task was not sent", said);
        Assert.Contains("local:q", said);
        Assert.Contains("There is no provider entry 'nowhere'", await desk.BringAsync(Bring("nowhere"), CancellationToken.None));
        Assert.Single(spawner.Orders);
    }

    /// <summary>
    /// A DISTILLING MODEL WHOSE WINDOW IS NOT KNOWN is said to be that, with its remedy — not "not configured", said of an
    /// entry that was configured and only needed contextWindow set.
    /// </summary>
    [Fact]
    public async Task A_distilling_model_with_no_known_window_is_said_to_be_that()
    {
        Library.Create(new ColleagueRecord { Name = "Charting", Type = "general", Created = DateTimeOffset.UtcNow, Instance = "main", DistillInstance = "slow" });
        var said = new List<string>();
        using var desk = new ColleagueDesk(new ColleagueDeskParts
        {
            Library = Library, Workers = _workers, Spawner = new Recording(_workers), Folder = _project, SessionId = () => "session",
            NoteWorker = _marks.Add, Notify = said.Add,
            Models = name => name switch
            {
                null or "main" => new ModelChoice(_main, "main:m", 200_000, "main"),
                "slow" => new ModelChoice(new MockLlmProvider("s"), "slow:s", null, "slow"),
                _ => null,
            },
        });
        await desk.BringAsync(new BringOrder("Charting", "", null, true, "lead"), CancellationToken.None);
        Assert.True(_workers.TryClaim("charting"));
        _workers.AnnounceBegin("charting", "chart it");
        _workers.Find("charting")!.Agent.Agent.Context.Add(new ChatMessage { Role = "assistant", Content = new string('c', 600) });

        _workers.EndSend("charting");
        await desk.Idle();

        Assert.Contains(said, s => s.Contains("slow:s's context window is not known", StringComparison.Ordinal));
        Assert.DoesNotContain(said, s => s.Contains("not configured", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Two_brings_at_once_make_one_incarnation()
    {
        Charting();
        var spawner = new Recording(_workers) { Delay = TimeSpan.FromMilliseconds(300) };
        var desk = Desk(spawner);

        var answers = await Task.WhenAll(desk.BringAsync(Bring(), CancellationToken.None), desk.BringAsync(Bring(), CancellationToken.None));

        Assert.Single(spawner.Orders);
        Assert.Contains(answers, a => a.Contains("already being brought in", StringComparison.Ordinal));
    }

    /// <summary>A colleague that came in without its experience is an ordinary worker: no mark is left to mislead.</summary>
    [Fact]
    public async Task A_bring_whose_door_refused_leaves_no_mark()
    {
        Charting();
        var spawner = new Recording(_workers) { Refused = "there is no colleague called Charting any more" };

        var answer = await Desk(spawner).BringAsync(Bring(), CancellationToken.None);

        Assert.Contains("without its experience", answer);
        Assert.Null(_workers.OriginOf("charting"));
        Assert.Null(_marks.Single().Origin);
    }

    [Fact]
    public void Keep_records_the_entry_the_worker_ran_on_not_a_guess_from_its_model_id()
    {
        var desk = Desk(new Recording(_workers));
        var named = Child(new MockLlmProvider("qwen3:32b"), instance: "local");
        var unnamed = Child(new MockLlmProvider("qwen3:32b"), instance: null);
        var a = _workers.Keep(named, "a");
        var b = _workers.Keep(unnamed, "b");

        desk.Keep(new KeepOrder(a, "Named"));
        desk.Keep(new KeepOrder(b, "Unnamed"));

        Assert.Equal("local", Library.Find("Named")!.Instance);
        Assert.Null(Library.Find("Unnamed")!.Instance);
    }

    [Fact]
    public void Keep_with_no_name_makes_one_a_name_may_hold()
    {
        var desk = Desk(new Recording(_workers));
        var handle = _workers.Keep(Child(new MockLlmProvider()), "Café menü layout");

        var said = desk.Keep(new KeepOrder(handle, null));

        Assert.StartsWith("Kept", said);
        Assert.Single(Library.List());
        Assert.Null(ColleagueStore.NameRefusal(Library.List()[0].Name));
    }

    /// <summary>A keep that fails half-way leaves nothing behind, so the same name can be kept once the cause is fixed.</summary>
    [Fact]
    public void A_keep_that_fails_half_way_leaves_no_colleague()
    {
        var desk = Desk(new Recording(_workers));
        var child = Child(new MockLlmProvider());
        // A TOOL CALL WITH NO ARGUMENTS cannot be written as JSON: appending the first round throws.
        child.Agent.Context.Replace([new ChatMessage { Role = "assistant", Content = "", ToolCalls = [new ToolCall { Id = "k", Name = "x" }] }]);
        var handle = _workers.Keep(child, "broken");

        var said = desk.Keep(new KeepOrder(handle, "Broken"));

        Assert.StartsWith("Could not keep", said);
        Assert.Empty(Library.List());
        Assert.Null(_workers.OriginOf(handle));
        Assert.False(Directory.Exists(Library.FolderOf("Broken")));
    }

    [Fact]
    public void A_path_no_file_can_have_is_not_a_workplace_read()
    {
        var other = Directory.CreateTempSubdirectory("desk-review-other-").FullName;
        try
        {
            var record = new ColleagueRecord
            {
                Name = "Charting", Type = "general", Created = DateTimeOffset.UtcNow,
                Workplaces = [new Workplace(Codebase.Key(other), other, 1, DateTimeOffset.UtcNow)],
            };
            ChatMessage Read(string path) => new()
            {
                Role = "assistant", Content = "",
                ToolCalls = [new ToolCall { Id = "k", Name = "read_file", Arguments = JsonSerializer.SerializeToElement(new { path }) }],
            };

            var read = ColleagueDesk.ReadsOf(new RoundReadsRequest([Read("a\0b"), Read(Path.Combine(other, "lib.cs"))], record, _project));

            Assert.Equal([other], read.Workplaces);
        }
        finally
        {
            Directory.Delete(other, recursive: true);
        }
    }

    private static SubAgent Child(ILlmProvider provider, string? instance = null, Func<string, ChildAugment?>? augment = null, string? origin = null) =>
        new SubAgentFactory(new SubAgentFactory.SubAgentRuntime
        {
            Provider = provider, InstanceName = instance, Ledger = new TokenLedger(), MaxTurns = 5,
            Tools = new AgentTools(JobRegistry.CreateWithBuiltins()), Augment = augment,
        }).Create(new SubAgentFactory.ChildRequest { Origin = origin });

    /// <summary>Records each order, keeps a child for it under its handle, and answers an envelope.</summary>
    private sealed class Recording(SubAgentStore workers) : ISubAgentSpawner
    {
        public List<SpawnOrder> Orders { get; } = [];
        public TimeSpan Delay { get; init; }
        public string? Refused { get; init; }
        public string ToolName => Tool.Agent;
        public ToolDefinition Definition => new(Tool.Agent, "spawns", JsonSerializer.SerializeToElement(new { type = "object" }));
        public void SwapDefaultProvider(ILlmProvider provider, int? contextWindow, string? instanceName) { }

        public Task<string?> TryInvokeAsync(ToolCall call, Action<SubAgent>? onChild, CancellationToken ct,
            string? parentAgentId = null, ToolSelection? turnTools = null) => throw new NotSupportedException();

        public async Task<string?> SpawnAsync(SpawnOrder order, Action<SubAgent>? onChild, CancellationToken ct)
        {
            lock (Orders) Orders.Add(order);
            await Task.Delay(Delay, ct);
            var child = Child(new MockLlmProvider(), augment: Refused is { } why ? _ => throw new InvalidOperationException(why) : null,
                origin: order.Origin);
            onChild?.Invoke(child);
            var handle = workers.Keep(child, order.Description, order.Handle);
            return $"<sub_agent id=\"{child.Agent.Id}\" name=\"{handle}\" state=\"completed\">ok</sub_agent>";
        }
    }
}
