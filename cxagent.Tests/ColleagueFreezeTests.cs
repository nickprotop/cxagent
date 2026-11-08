using System.Text.Json.Nodes;
using CxAgent.Core.Agents;
using CxAgent.Core.Colleagues;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.UI.Config;
using CxAgent.Web;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Freezing a colleague — the user's lever for keeping a good experience good: frozen, it works and its rounds are kept,
/// but nothing is distilled; unfrozen, it learns again from that moment, and the stretch it lived frozen never becomes
/// experience (user, 2026-11-08). The rounds waiting from before the freeze are still learned from.
/// </summary>
public sealed class ColleagueFreezeTests : IDisposable
{
    private readonly string _config = Directory.CreateTempSubdirectory("freeze-config-").FullName;
    private readonly string _project = Directory.CreateTempSubdirectory("freeze-proj-").FullName;
    private readonly MockLlmProvider _model = new();
    private readonly BufferedChatSink _seen = new();

    public ColleagueFreezeTests() =>
        Library.Create(new ColleagueRecord { Name = "Charting", Type = "general", Created = DateTimeOffset.UtcNow });

    public void Dispose()
    {
        foreach (var d in new[] { _config, _project })
            try { Directory.Delete(d, recursive: true); } catch (IOException) { }
    }

    private ColleagueStore Library => new(_config);

    private long Round(string said) =>
        Library.Append("Charting", new PastRound(0, "S", _project, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            [new ChatMessage { Role = "user", Content = said }, new ChatMessage { Role = "assistant", Content = said + new string('c', 600) }]));

    private Task<DistillOutcome> Drain() => new Distiller(Library).DrainAllAsync(_ => _ => _model, CancellationToken.None);

    [Fact]
    public async Task A_frozen_colleague_keeps_rounds_but_learns_nothing()
    {
        Library.Freeze("Charting");
        Round("chart it");

        await Drain();

        Assert.Equal(0, _model.ChatCallCount);
        Assert.Equal(1, Library.RoundCount("Charting"));
        Assert.Equal(0, Library.Find("Charting")!.ExperienceVersion);
        Assert.Empty(Library.Undistilled("Charting"));
    }

    [Fact]
    public async Task Unfreezing_learns_from_now_on()
    {
        Round("ROUND-ONE");
        Library.Freeze("Charting");
        Round("ROUND-TWO");
        Library.Unfreeze("Charting");
        Round("ROUND-THREE");
        _model.EnqueueResponse(new LlmResponse { Text = "<core>\n## Lessons\nPanes first.\n</core>", StopReason = "end_turn" });

        await Drain();

        var asked = string.Join("\n", _model.LastMessages!.Select(m => m.Content));
        Assert.Contains("ROUND-ONE", asked);
        Assert.Contains("ROUND-THREE", asked);
        Assert.DoesNotContain("ROUND-TWO", asked);
        Assert.Empty(Library.Undistilled("Charting"));
        Assert.Equal([new SeqRange(2, 2)], Library.Find("Charting")!.Unlearned);
    }

    [Fact]
    public void Freezing_twice_keeps_where_the_stretch_began()
    {
        Round("one");
        Library.Freeze("Charting");
        Round("two");
        Library.Freeze("Charting");

        Assert.Equal(1, Library.Find("Charting")!.FrozenAfter);
    }

    /// <summary>A freeze reaches a drain already under way: the rounds after it wait, as every frozen round does.</summary>
    [Fact]
    public async Task Freezing_during_a_drain_stops_it()
    {
        Round("ROUND-ONE");
        var freezing = new FreezingModel(Library, () => Round("ROUND-TWO"));

        await new Distiller(Library).DrainAllAsync(_ => _ => freezing, CancellationToken.None);

        Assert.Equal(1, freezing.Calls);
        Assert.Equal(1, Library.Find("Charting")!.DistilledThrough);
    }

    /// <summary>Answers once, freezing the colleague and living another round while the distiller waits on it.</summary>
    private sealed class FreezingModel(ColleagueStore library, Action during) : ILlmProvider
    {
        public int Calls { get; private set; }
        public string ProviderId => "freezing";
        public string DisplayName => "Freezing";
        public string ModelId => "freezing";
        public bool SupportsToolCalling => false;
        public bool SupportsStreaming => false;

        public Task<LlmResponse> ChatAsync(List<ChatMessage> messages, List<ToolDefinition>? tools, CancellationToken ct)
        {
            Calls++;
            library.Freeze("Charting");
            during();
            return Task.FromResult(new LlmResponse { Text = "<core>\n## Lessons\nPanes first.\n</core>", StopReason = "end_turn" });
        }

        public IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(List<ChatMessage> messages, List<ToolDefinition>? tools, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private Session Open()
    {
        var resolution = ResolvedConfig.ForTesting(new MockLlmProvider()) is var r
            ? r with { Model = r.Model! with { ContextWindow = 200_000 }, Catalog = r.Catalog with { Colleagues = true } }
            : null!;
        var session = new Session(_project);
        SessionFactory.Wire(session, resolution, new SharedServices { GlobalInstructionsDir = _config },
            new SessionPorts { Observer = _seen, ToolObserver = new BufferedJobPanel() }, AgentMode.Single);
        return session;
    }

    [Fact]
    public void The_verbs_freeze_and_unfreeze()
    {
        var session = Open();

        session.ColleaguesCommand("freeze charting");
        Assert.True(Library.Find("Charting")!.Frozen);
        Assert.Contains(_seen.Notices, n => n.Contains("Charting is frozen", StringComparison.Ordinal));

        session.ColleaguesCommand("unfreeze Charting");
        Assert.False(Library.Find("Charting")!.Frozen);
        Assert.Contains(_seen.Notices, n => n.Contains("learns again, from now on", StringComparison.Ordinal));

        session.ColleaguesCommand("freeze Nobody");
        Assert.Contains(_seen.Notices, n => n.Contains("no colleague called 'Nobody'", StringComparison.Ordinal));
    }

    [Fact]
    public void The_draft_freezes_through_the_store()
    {
        Round("one");
        var draft = ColleagueDraft.For(Library.Find("Charting")!);
        draft.Frozen = true;

        Assert.True(draft.IsDirty);
        draft.Apply(Library);

        Assert.True(Library.Find("Charting")!.Frozen);
        Assert.Equal(1, Library.Find("Charting")!.FrozenAfter);
    }

    [Fact]
    public void The_library_says_frozen_and_what_was_not_learned()
    {
        Round("one");
        Library.Freeze("Charting");
        Round("two");
        Library.Unfreeze("Charting");
        Library.Freeze("Charting");

        var facts = Assert.Single(LibraryView.Build(Library)).Facts;

        Assert.True(facts.Frozen);
        Assert.Equal(1, facts.Unlearned);
        Assert.Contains("frozen", LibraryView.FactsLine(facts));
        Assert.Contains("1 round lived frozen, not learned from", LibraryView.FactsLine(facts));
    }

    [Theory]
    [InlineData(false, "idle")]
    [InlineData(true, "idle · frozen")]
    public void The_card_word_says_frozen(bool frozen, string word) =>
        Assert.Equal(word, CxAgent.UI.AgentRows.ColleagueWord(CxAgent.UI.AgentRowState.Done,
            new ColleagueCard([], 1, Learning: false, Task: null) { Frozen = frozen }));

    [Fact]
    public void The_web_form_has_the_switch()
    {
        var form = WebConfigForms.Colleagues(ProviderConfigLoader.Parse("""
            { "providers": { "big": { "kind": "ollama", "model": "qwen", "baseUrl": "http://localhost:1/v1", "contextWindow": 200000 } },
              "defaultProvider": "big" }
            """, new Dictionary<string, string>()), Library, "Charting");

        Assert.Contains("\"frozen\"", form.ToJsonString());

        var draft = WebConfigForms.SetColleague(ColleagueDraft.For(Library.Find("Charting")!), new JsonObject { ["frozen"] = "true" });
        Assert.True(draft.Frozen);
    }
}
