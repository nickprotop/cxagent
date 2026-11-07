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
    private readonly string _project = Directory.CreateTempSubdirectory("desk-project-").FullName;
    private readonly Scripted _model = new();

    public void Dispose()
    {
        foreach (var d in new[] { _config, _project })
            try { Directory.Delete(d, recursive: true); } catch (IOException) { }
    }

    private ColleagueStore Library => new(_config);

    private Session Open(int? window = 200_000, bool on = true)
    {
        var resolution = ResolvedConfig.ForTesting(_model) is var r
            ? r with { Model = r.Model! with { ContextWindow = window }, Catalog = r.Catalog with { Colleagues = on } }
            : null!;
        var session = new Session(_project);
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
        Library.WriteExperience("Charting", "## Lessons\nLegends follow the series.", null, null);
        var session = Open();

        var envelope = await session.Colleagues!.BringAsync(new BringOrder("Charting", "draw the sales chart", null, false, session.SessionId!), CancellationToken.None);

        Assert.Equal("charting", SubAgentEnvelope.NameOf(envelope));
        var brief = session.SubAgents.Find("charting")!.Agent.Agent.Setup.Brief.CallerContext!;
        Assert.Contains("You are Charting", brief);
        Assert.Contains("Legends follow the series.", brief);
        Assert.Equal("colleague:Charting", session.SubAgents.OriginOf("charting"));
    }

    [Fact]
    public async Task Bring_twice_returns_the_live_handle()
    {
        Library.Create(new ColleagueRecord { Name = "Charting", Type = "general", Created = DateTimeOffset.UtcNow });
        var session = Open();
        await session.Colleagues!.BringAsync(new BringOrder("Charting", "one", null, false, session.SessionId!), CancellationToken.None);

        var again = await session.Colleagues.BringAsync(new BringOrder("Charting", "two", null, false, session.SessionId!), CancellationToken.None);

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

    [Fact]
    public async Task Experience_grown_elsewhere_arrives_as_one_line_at_the_next_run()
    {
        Library.Create(new ColleagueRecord { Name = "Charting", Type = "general", Created = DateTimeOffset.UtcNow });
        var session = Open();
        await session.Colleagues!.BringAsync(new BringOrder("Charting", "one", null, false, session.SessionId!), CancellationToken.None);
        await session.Colleagues.Idle();

        // ANOTHER SESSION, ELSEWHERE, DISTILLED A ROUND MEANWHILE.
        Library.Update("Charting", r => r with { ExperienceVersion = r.ExperienceVersion + 1, GrownIn = ["/home/dev/source/invoice"] });
        await Send(session, "charting", "two");
        await session.Colleagues.Idle();

        var mail = session.SubAgents.Find("charting")!.Agent.Agent.Mailbox.DrainEntries();
        var line = Assert.Single(mail);
        Assert.Contains("grown since", line.Text);
        Assert.Contains("invoice", line.Text);
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
        Library.WriteExperience("Charting", "## Lessons\nPanes first.", null, null);
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

        public Task<LlmResponse> ChatAsync(List<ChatMessage> messages, List<ToolDefinition>? tools, CancellationToken ct) =>
            Task.FromResult(new LlmResponse
            {
                Text = messages.Any(m => m.Role == "system" && m.Content.StartsWith("You keep the experience", StringComparison.Ordinal))
                    ? "NO CHANGE"
                    : "done — moved it and checked the render",
                StopReason = "end_turn",
            });

        public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(List<ChatMessage> messages, List<ToolDefinition>? tools,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            var r = await ChatAsync(messages, tools, ct);
            yield return new LlmStreamChunk(r.Text, null, true, null, r.StopReason);
        }
    }
}
