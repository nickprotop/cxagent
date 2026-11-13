using CxAgent.Core.Colleagues;
using CxAgent.Core.Execution;
using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// A session's desk for colleagues: keeping a worker, bringing one in, catching each round it works and handing it to
/// the distiller — all through the worker code's ordinary paths.
/// </summary>
public sealed class ColleagueDeskTests : IDisposable
{
    private readonly string _config = Directory.CreateTempSubdirectory("desk-config-").FullName;
    private readonly string _project = TestProject.New("desk-project-");
    private readonly Scripted _model = new();

    public void Dispose()
    {
        foreach (var d in new[] { _config, _project })
            try { Directory.Delete(d, recursive: true); } catch (IOException) { }
    }

    private ColleagueStore Library => new(_config);

    private Session Open(int? window = 200_000, bool on = true, string? folder = null)
    {
        var resolution = ResolvedConfig.ForTesting(_model) is var r
            ? r with { Model = r.Model! with { ContextWindow = window }, Catalog = r.Catalog with { Colleagues = on } }
            : null!;
        var session = new Session(folder ?? _project);
        SessionFactory.Wire(session, resolution, new SharedServices { GlobalInstructionsDir = _config },
            new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() }, AgentMode.Single);
        return session;
    }

    /// <summary>A worker of the session's, kept under a handle, with a conversation behind it.</summary>
    private string Worker(Session session, string description = "chart layout")
    {
        var child = new SubAgentFactory(new SubAgentFactory.SubAgentRuntime
        {
            Provider = _model, Ledger = new TokenLedger(), MaxTurns = 5, ContextWindow = 200_000,
            Tools = new AgentTools(JobRegistry.CreateWithBuiltins()),
        }).Create();
        child.Agent.Context.Replace(
        [
            new ChatMessage { Role = "user", Content = "lay the chart out in panes" },
            new ChatMessage { Role = "assistant", Content = "I split it into three panes and wired the legend to the series list, because the series drive the colours." },
        ]);
        return session.SubAgents.Keep(child, description);
    }

    private static Task<string> Send(Session session, string handle, string prompt) =>
        new AgentReachTools(session.SubAgents).SendAsync(
            new SendRequest(To: handle, Prompt: prompt, Background: false, ParentAgentId: null, FromUser: true), CancellationToken.None);

    [Fact]
    public async Task Keep_makes_the_worker_a_colleague_with_its_conversation_as_round_one()
    {
        var session = Open();
        var handle = Worker(session);

        var said = session.Colleagues!.Keep(new KeepOrder(handle, "Charting"));
        await session.Colleagues.Idle();

        Assert.Contains("Charting", said);
        var record = Library.Find("Charting")!;
        Assert.Equal(AgentTypeCatalog.DefaultTypeName, record.Type);
        Assert.Equal(Codebase.Key(_project), Assert.Single(record.Workplaces).Codebase);
        var round = Assert.Single(Library.Past("Charting"));
        Assert.Contains(round.Messages, m => m.Content.Contains("three panes", StringComparison.Ordinal));
        Assert.Equal("colleague:Charting", session.SubAgents.OriginOf(handle));
        Assert.Equal(1, Library.Find("Charting")!.DistilledThrough);   // the scripted distiller answered NO CHANGE
    }

    [Fact]
    public void Keep_refuses_an_unknown_worker_a_taken_name_and_a_bad_one()
    {
        var session = Open();
        var handle = Worker(session);
        var other = Worker(session, "legend work");
        session.Colleagues!.Keep(new KeepOrder(handle, "Charting"));

        Assert.Contains("no worker", session.Colleagues.Keep(new KeepOrder("nobody", "X")), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("already a colleague", session.Colleagues.Keep(new KeepOrder(other, "charting")), StringComparison.Ordinal);
        Assert.Contains("1–40", session.Colleagues.Keep(new KeepOrder(other, "../x")), StringComparison.Ordinal);
        Assert.Contains("already", session.Colleagues.Keep(new KeepOrder(handle, "Again")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bring_spawns_a_worker_whose_prompt_carries_its_experience()
    {
        Library.Create(new ColleagueRecord { Name = "Charting", Type = "general", Created = DateTimeOffset.UtcNow });
        Library.WriteCore("Charting", "## Lessons\nLegends follow the series.");
        var session = Open();

        var envelope = await session.Colleagues!.BringAsync(new BringOrder("Charting", "draw the sales chart", null, false, session.SessionId!), CancellationToken.None);

        Assert.Equal("charting", SubAgentEnvelope.NameOf(envelope));
        var brief = session.SubAgents.Find("charting")!.Agent.Agent.Setup.Brief.CallerContext!;
        Assert.Contains("You are Charting", brief);
        Assert.Contains("Legends follow the series.", brief);
        Assert.Equal("colleague:Charting", session.SubAgents.OriginOf("charting"));
    }

    /// <summary>
    /// A colleague already here, brought in again with a task, is given the task — a live session's lead was told
    /// "use agent_send" and never did, and the task was lost (user, 2026-11-08: pass the task to it).
    /// </summary>
    [Fact]
    public async Task Bring_of_one_already_here_passes_the_task_to_it()
    {
        Library.Create(new ColleagueRecord { Name = "Charting", Type = "general", Created = DateTimeOffset.UtcNow });
        var session = Open();
        await session.Colleagues!.BringAsync(new BringOrder("Charting", "one", null, false, session.SessionId!), CancellationToken.None);

        var answer = await session.Colleagues.BringAsync(new BringOrder("Charting", "now chart the costs", null, false, session.SessionId!), CancellationToken.None);

        Assert.Single(session.SubAgents.All());
        Assert.Contains(session.SubAgents.Find("charting")!.Agent.Agent.Context.Messages, m => m.Content == "now chart the costs");
        Assert.Contains("done", answer);
    }

    [Fact]
    public async Task Bring_twice_returns_the_live_handle()
    {
        Library.Create(new ColleagueRecord { Name = "Charting", Type = "general", Created = DateTimeOffset.UtcNow });
        var session = Open();
        await session.Colleagues!.BringAsync(new BringOrder("Charting", "one", null, false, session.SessionId!), CancellationToken.None);

        var again = await session.Colleagues.BringAsync(new BringOrder("Charting", "", null, false, session.SessionId!), CancellationToken.None);

        Assert.Contains("already here as charting", again);
        Assert.Single(session.SubAgents.All());
    }

    [Fact]
    public async Task Bring_onto_a_small_window_is_refused()
    {
        Library.Create(new ColleagueRecord { Name = "Charting", Type = "general", Created = DateTimeOffset.UtcNow });
        var session = Open(window: 64_000);

        var answer = await session.Colleagues!.BringAsync(new BringOrder("Charting", "one", null, false, session.SessionId!), CancellationToken.None);

        Assert.Contains("128k", answer);
        Assert.Empty(session.SubAgents.All());
    }

    [Fact]
    public async Task Bring_of_an_unknown_colleague_names_the_list()
    {
        var session = Open();

        var answer = await session.Colleagues!.BringAsync(new BringOrder("Nobody", "one", null, false, session.SessionId!), CancellationToken.None);

        Assert.Contains("no colleague called 'Nobody'", answer);
    }

    [Fact]
    public async Task A_finished_run_of_a_marked_worker_is_a_round()
    {
        var session = Open();
        var handle = Worker(session);
        session.Colleagues!.Keep(new KeepOrder(handle, "Charting"));

        await Send(session, handle, "now move the legend under the plot");
        await session.Colleagues.Idle();

        var past = Library.Past("Charting");
        Assert.Equal(2, past.Count);
        Assert.Contains(past[1].Messages, m => m.Content.Contains("move the legend", StringComparison.Ordinal));
        Assert.DoesNotContain(past[1].Messages, m => m.Content.Contains("three panes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unmarked_worker_leaves_no_past()
    {
        var session = Open();
        var handle = Worker(session);

        await Send(session, handle, "again");
        await session.Colleagues!.Idle();

        Assert.Empty(Library.List());
    }

    /// <summary>
    /// EVERY ASK STARTS FRESH FROM ITS KNOWLEDGE AS IT IS NOW (user, 2026-11-11: "a colleague is its distilled knowledge;
    /// after a round is distilled, that is its starting point"). Nothing of the earlier ask is in the conversation, the
    /// newer experience is in the prompt, and no "grown elsewhere" note is posted — it would be false in a fresh ask.
    /// </summary>
    [Fact]
    public async Task Every_ask_starts_fresh_from_the_knowledge_as_it_is_now()
    {
        Library.Create(new ColleagueRecord { Name = "Charting", Type = "general", Created = DateTimeOffset.UtcNow });
        var session = Open();
        await session.Colleagues!.BringAsync(new BringOrder("Charting", "FIRST-ASK", null, false, session.SessionId!), CancellationToken.None);
        await session.Colleagues.Idle();

        // ANOTHER SESSION, ELSEWHERE, DISTILLED A ROUND MEANWHILE.
        Library.WriteCore("Charting", "## Lessons\n- NEWER-LESSON (observed, round 1)");
        Library.Update("Charting", r => r with { ExperienceVersion = r.ExperienceVersion + 1, GrownIn = ["/home/dev/source/invoice"] });
        await Send(session, "charting", "SECOND-ASK");
        await session.Colleagues.Idle();

        var sent = string.Join("\n", _model.LastMessages!.Select(m => m.Content));
        Assert.Contains("SECOND-ASK", sent);
        Assert.DoesNotContain("FIRST-ASK", sent);
        Assert.Contains("NEWER-LESSON", sent);
        Assert.Empty(session.SubAgents.Find("charting")!.Agent.Agent.Mailbox.DrainEntries());
    }

    [Fact]
    public async Task A_round_left_from_before_is_distilled_when_the_desk_opens()
    {
        Library.Create(new ColleagueRecord { Name = "Charting", Type = "general", Created = DateTimeOffset.UtcNow });
        Library.Append("Charting", new PastRound(0, "old", _project, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            [new ChatMessage { Role = "user", Content = "chart it" }, new ChatMessage { Role = "assistant", Content = new string('c', 400) }]));

        var session = Open();
        await session.Colleagues!.Idle();

        Assert.Empty(Library.Undistilled("Charting"));
    }

    [Fact]
    public async Task A_held_lock_is_waited_out_while_rounds_wait()
    {
        Library.Create(new ColleagueRecord { Name = "Charting", Type = "general", Created = DateTimeOffset.UtcNow });
        var held = DistillingLock.TryTake(Library.Root)!;
        Library.Append("Charting", new PastRound(0, "old", _project, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            [new ChatMessage { Role = "user", Content = "chart it" }, new ChatMessage { Role = "assistant", Content = new string('c', 400) }]));
        var session = Open();
        session.Colleagues!.BusyRetry = TimeSpan.FromMilliseconds(50);

        // ANOTHER DISTILLER HOLDS THE LOCK FOR A WHILE — longer than the old six tries would have waited at this pace.
        await Task.Delay(600);
        Assert.Single(Library.Undistilled("Charting"));
        held.Dispose();
        await session.Colleagues.Idle();

        Assert.Empty(Library.Undistilled("Charting"));
    }

    /// <summary>
    /// ITS OWN LEARNING IS NOT NEWS FROM ELSEWHERE: a live colleague was told after every round that it had "grown since
    /// you started here (from sessions in ConsoleEx)" — growth it had done itself, here.
    /// </summary>
    [Fact]
    public async Task Its_own_distilling_is_not_told_as_growth_elsewhere()
    {
        // AN EARLIER LIFE ELSEWHERE, as the live one had: its growth list already names another folder.
        Library.Create(new ColleagueRecord { Name = "Charting", Type = "general", Created = DateTimeOffset.UtcNow });
        Library.Update("Charting", r => r with { GrownIn = ["/home/dev/source/ConsoleEx"] });
        _model.Distil = $"<core>\nPanes first.\n</core>\n<description>charts</description>";
        var session = Open();
        await session.Colleagues!.BringAsync(new BringOrder("Charting", "", null, false, session.SessionId!), CancellationToken.None);

        // TWO ROUNDS OF REAL WORK HERE — each distilled by this session, each a new version of its experience.
        await Send(session, "charting", "lay the chart out " + new string('p', 400));
        await session.Colleagues.Idle();
        await Send(session, "charting", "now the legend " + new string('l', 400));
        await session.Colleagues.Idle();
        await Send(session, "charting", "and the title");
        await session.Colleagues.Idle();

        // THE LINE IS READ AT THE START OF THE NEXT RUN, so it shows as a round's message — or nowhere.
        Assert.True(Library.Find("Charting")!.ExperienceVersion >= 2);
        Assert.DoesNotContain(Library.Past("Charting").SelectMany(r => r.Messages), m => m.Content.Contains("grown since", StringComparison.Ordinal));
    }

    [Fact]
    public void Off_in_config_means_no_desk()
    {
        var session = Open(on: false);

        Assert.Null(session.Colleagues);
    }

    [Fact]
    public void Augment_equips_a_known_colleague_and_refuses_a_forgotten_one()
    {
        Library.Create(new ColleagueRecord { Name = "Charting", Type = "general", Created = DateTimeOffset.UtcNow });
        Library.WriteCore("Charting", "## Lessons\nPanes first.");
        var session = Open();

        Assert.Contains("Panes first.", session.Colleagues!.Augment("colleague:Charting")!.Context);
        Assert.Null(session.Colleagues.Augment("somebody-else:x"));
        Assert.Throws<InvalidOperationException>(() => session.Colleagues.Augment("colleague:Gone"));
    }

    /// <summary>The workers' model answers "done"; asked to distil, it answers NO CHANGE.</summary>
    private sealed class Scripted : ILlmProvider
    {
        public string ProviderId => "scripted";
        public string DisplayName => "Scripted";
        public string ModelId => "scripted-model";
        public bool SupportsToolCalling => true;
        public bool SupportsStreaming => false;

        /// <summary>What it answers when asked to distil.</summary>
        public string Distil { get; set; } = "NO CHANGE";

        /// <summary>What a worker — not the distiller — was last sent.</summary>
        public List<ChatMessage>? LastMessages { get; private set; }

        public Task<LlmResponse> ChatAsync(List<ChatMessage> messages, List<ToolDefinition>? tools, CancellationToken ct)
        {
            var distilling = messages.Any(m => m.Role == "system" && m.Content.StartsWith("You keep the experience", StringComparison.Ordinal));
            if (!distilling) LastMessages = [.. messages];
            return Task.FromResult(new LlmResponse
            {
                Text = distilling ? Distil : "done — moved it and checked the render",
                StopReason = "end_turn",
            });
        }

        public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(List<ChatMessage> messages, List<ToolDefinition>? tools,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            var r = await ChatAsync(messages, tools, ct);
            yield return new LlmStreamChunk(r.Text, null, true, null, r.StopReason);
        }
    }

    /// <summary>
    /// A SCRATCH FOLDER IS NO WORKPLACE (user, 2026-11-11): work kept from a session in a folder that is not a project
    /// is in the colleague's past all the same, but the folder becomes no codebase of it.
    /// </summary>
    [Fact]
    public async Task Work_in_a_folder_that_is_not_a_project_adds_no_workplace()
    {
        var scratch = Directory.CreateTempSubdirectory("desk-scratch-").FullName;
        try
        {
            var session = Open(folder: scratch);
            var handle = Worker(session);

            session.Colleagues!.Keep(new KeepOrder(handle, "Charting"));
            await session.Colleagues.Idle();

            Assert.Empty(Library.Find("Charting")!.Workplaces);
            Assert.Single(Library.Past("Charting"));
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); } catch (IOException) { }
        }
    }

    private void UndistilledRound() =>
        Library.Append("Charting", new PastRound(0, "elsewhere", _project, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            [new ChatMessage { Role = "user", Content = "chart it" }, new ChatMessage { Role = "assistant", Content = new string('c', 400) }]));

    private async Task<Session> Joined()
    {
        Library.Create(new ColleagueRecord { Name = "Charting", Type = "general", Created = DateTimeOffset.UtcNow });
        var session = Open();
        await session.Colleagues!.BringAsync(new BringOrder("Charting", "", null, true, session.SessionId!), CancellationToken.None);
        await session.Colleagues.Idle();
        session.Colleagues.BusyRetry = TimeSpan.FromMilliseconds(50);
        session.Colleagues.LearnPoll = TimeSpan.FromMilliseconds(20);
        return session;
    }

    /// <summary>
    /// AN ASK WAITS UNTIL WHAT THE COLLEAGUE LIVED IS LEARNED, read from the library — here the lock is held as another
    /// process would hold it — and the card says it is waiting to learn. It goes on once the round is distilled.
    /// </summary>
    [Fact]
    public async Task An_ask_waits_for_its_own_rounds_to_be_distilled()
    {
        var session = await Joined();
        var held = DistillingLock.TryTake(Library.Root)!;
        UndistilledRound();

        var ask = Send(session, "charting", "next");

        Assert.True(await Poll.Until(() => session.SubAgents.IsWaitingToLearn("charting")));
        Assert.False(ask.IsCompleted);
        var row = CxAgent.UI.AgentRows.Describe(session.SubAgents, session.SubAgents.Find("charting")!, DateTimeOffset.UtcNow)!;
        Assert.Equal(CxAgent.UI.AgentRowState.WaitingToLearn, row.State);
        Assert.Equal("waiting to learn", CxAgent.UI.AgentRows.ColleagueWord(row.State, learning: true));
        // THE CARD NAMES THE ASK THAT WAITS, not the one before it.
        Assert.Equal("next", session.Colleagues!.CardOf("charting")!.Task);
        // ITS CLOCK RUNS FROM WHEN THE WAIT BEGAN: a card read "waiting to learn 0s" for forty seconds.
        var later = CxAgent.UI.AgentRows.Describe(session.SubAgents, session.SubAgents.Find("charting")!, DateTimeOffset.UtcNow.AddSeconds(30))!;
        Assert.True(later.Elapsed >= TimeSpan.FromSeconds(29), $"waited {later.Elapsed}");
        held.Dispose();
        await ask.WaitAsync(TimeSpan.FromSeconds(10));
        // THE EARLIER ROUND WAS LEARNED BEFORE THE ASK RAN; the ask's own round is the next to learn.
        Assert.DoesNotContain(Library.Undistilled("Charting"), r => r.Session == "elsewhere");
        Assert.False(session.SubAgents.IsWaitingToLearn("charting"));
    }

    /// <summary>DON'T WAIT starts the ask at once, from what was learned before.</summary>
    [Fact]
    public async Task Dont_wait_starts_the_ask_at_once()
    {
        var session = await Joined();
        using var held = DistillingLock.TryTake(Library.Root)!;
        UndistilledRound();

        var ask = Send(session, "charting", "next");
        Assert.True(await Poll.Until(() => session.SubAgents.IsWaitingToLearn("charting")));
        Assert.True(session.Colleagues!.SkipWait("charting"));

        await ask.WaitAsync(TimeSpan.FromSeconds(10));
        // STILL WAITING TO BE LEARNED — the ask did not wait for it.
        Assert.Contains(Library.Undistilled("Charting"), r => r.Session == "elsewhere");
    }

    /// <summary>A FROZEN COLLEAGUE LEARNS NOTHING, so nothing is waited for.</summary>
    [Fact]
    public async Task A_frozen_colleague_never_waits()
    {
        var session = await Joined();
        using var held = DistillingLock.TryTake(Library.Root)!;
        Library.Freeze("Charting");
        UndistilledRound();

        await Send(session, "charting", "next").WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// A ROUND A COMPACTION CUT INTO is the summary and what followed — never the tail kept from before it, which the past
    /// already has: taking everything copied earlier rounds into the next, and the distiller read old evidence as new.
    /// </summary>
    [Fact]
    public void A_compacted_round_keeps_only_what_was_not_held()
    {
        ChatMessage M(string text) => new() { Role = "user", Content = text };
        var (a, b, c) = (M("a"), M("b"), M("c"));
        var held = ColleagueDesk.Held.Of([new ChatMessage { Role = "system", Content = "s" }, a, b, c]);
        var (summary, d, e) = (M("summary"), M("d"), M("e"));

        // THE RUN'S START SUMMARIZED AWAY, an older message kept: the round is the summary and what is new, not "b".
        Assert.Equal(["summary", "d", "e"], held.Since([summary, b, d, e]).Select(m => m.Content));
        Assert.Equal(["d", "e"], held.Since([a, b, c, d, e]).Select(m => m.Content));
    }
}
