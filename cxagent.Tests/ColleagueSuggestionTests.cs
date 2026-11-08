using CxAgent.Core.Agents;
using CxAgent.Core.Colleagues;
using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Suggesting a keep (spec §9): a worker the user or the agent keeps coming back to is offered as a colleague, once, in a
/// line that never blocks — after the user steered it twice, the agent followed it up three times, or it ran four rounds
/// in this session. Never for a colleague's own worker, never in a one-to-one, never with the setting off.
/// </summary>
public sealed class ColleagueSuggestionTests : IDisposable
{
    private readonly string _config = Directory.CreateTempSubdirectory("suggest-config-").FullName;
    private readonly string _project = Directory.CreateTempSubdirectory("suggest-proj-").FullName;
    private readonly SubAgentStore _workers = new();
    private readonly List<KeepSuggestion> _suggested = [];

    public void Dispose()
    {
        foreach (var d in new[] { _config, _project })
            try { Directory.Delete(d, recursive: true); } catch (IOException) { }
    }

    private async Task<ColleagueDesk> Desk(bool suggest = true, bool oneToOne = false)
    {
        var desk = new ColleagueDesk(new ColleagueDeskParts
        {
            Library = new ColleagueStore(_config),
            Workers = _workers,
            Folder = _project,
            SessionId = () => "session",
            NoteWorker = _ => { },
            Models = _ => new ModelChoice(new MockLlmProvider(), "main:m", 200_000, "main"),
            Suggest = suggest,
            OneToOne = oneToOne,
        });
        desk.KeepSuggested += _suggested.Add;
        await desk.Idle();
        return desk;
    }

    private string Worker(ILlmProvider? provider = null)
    {
        var child = new SubAgentFactory(new SubAgentFactory.SubAgentRuntime
        {
            Provider = provider ?? new MockLlmProvider(), Ledger = new TokenLedger(), MaxTurns = 5, ContextWindow = 200_000,
            Tools = new AgentTools(JobRegistry.CreateWithBuiltins()),
        }).Create();
        return _workers.Keep(child, "chart layout");
    }

    /// <summary>One stretch of the worker's work: begun and ended, as a send or a spawn announces it.</summary>
    private void Stretch(string handle)
    {
        Assert.True(_workers.TryClaim(handle));
        _workers.AnnounceBegin(handle, "go on");
        _workers.EndSend(handle);
    }

    [Fact]
    public async Task Two_user_steers_suggest_keeping_once()
    {
        using var desk = await Desk();
        var handle = Worker();

        _workers.NoteSent(handle, fromUser: true);
        Stretch(handle);
        Assert.Empty(_suggested);

        _workers.NoteSent(handle, fromUser: true);
        Stretch(handle);
        _workers.NoteSent(handle, fromUser: true);
        Stretch(handle);

        var one = Assert.Single(_suggested);
        Assert.Equal(handle, one.Handle);
        Assert.Contains("you steered it 2 times", one.Why);
    }

    [Fact]
    public async Task Three_lead_follow_ups_suggest()
    {
        using var desk = await Desk();
        var handle = Worker();

        for (var i = 0; i < 3; i++)
        {
            _workers.NoteSent(handle, fromUser: false);
            Stretch(handle);
        }

        Assert.Contains("the agent sent it 3 follow-ups", Assert.Single(_suggested).Why);
    }

    [Fact]
    public async Task Four_rounds_suggest()
    {
        using var desk = await Desk();
        var handle = Worker();

        for (var i = 0; i < 4; i++) Stretch(handle);

        Assert.Contains("4 rounds", Assert.Single(_suggested).Why);
    }

    [Fact]
    public async Task A_colleague_s_worker_is_never_suggested()
    {
        using var desk = await Desk();
        var handle = Worker();
        _workers.SetOrigin(handle, "colleague:Charting");

        for (var i = 0; i < 5; i++) { _workers.NoteSent(handle, fromUser: true); Stretch(handle); }

        Assert.Empty(_suggested);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Off_or_in_a_one_to_one_means_never(bool suggest, bool oneToOne)
    {
        using var desk = await Desk(suggest, oneToOne);
        var handle = Worker();

        for (var i = 0; i < 5; i++) { _workers.NoteSent(handle, fromUser: true); Stretch(handle); }

        Assert.Empty(_suggested);
    }

    [Fact]
    public async Task Not_this_one_is_never_asked_again()
    {
        using var desk = await Desk();
        var handle = Worker();
        desk.Dismiss(handle);

        for (var i = 0; i < 5; i++) { _workers.NoteSent(handle, fromUser: true); Stretch(handle); }

        Assert.Empty(_suggested);
    }

    /// <summary>
    /// Every accepted send counts, by who sent it: the user's own foreground send, and a message left for a worker still
    /// running. A refused send counts for nothing.
    /// </summary>
    [Fact]
    public async Task Sends_are_counted_on_every_path()
    {
        var model = new MockLlmProvider();
        model.EnqueueResponse(new LlmResponse { Text = "done", StopReason = "end_turn" });
        var handle = Worker(model);
        var tools = new AgentReachTools(_workers);

        await tools.SendAsync(new SendRequest(handle, "steer", Background: false, ParentAgentId: null, FromUser: true), CancellationToken.None);
        Assert.Equal(new WorkerSends(1, 0), _workers.SendsTo(handle));

        // RUNNING: the send is left in its mailbox, and still counts.
        Assert.True(_workers.TryClaim(handle));
        await tools.SendAsync(new SendRequest(handle, "and this", Background: false, ParentAgentId: "lead", FromUser: false), CancellationToken.None);
        _workers.Release(handle);
        Assert.Equal(new WorkerSends(1, 1), _workers.SendsTo(handle));

        await tools.SendAsync(new SendRequest("nobody", "x", Background: false, ParentAgentId: null, FromUser: true), CancellationToken.None);
        Assert.Equal(new WorkerSends(0, 0), _workers.SendsTo("nobody"));
    }

    [Fact]
    public void Config_reads_the_switch()
    {
        var off = ProviderConfigLoader.Parse("""{ "providers": {}, "colleagues": { "suggest": false } }""", new Dictionary<string, string>());
        var bad = ProviderConfigLoader.Parse("""{ "providers": {}, "colleagues": { "suggest": "no" } }""", new Dictionary<string, string>());

        Assert.False(off.ColleagueSuggestions);
        Assert.Null(bad.ColleagueSuggestions);
        Assert.Contains(bad.Warnings, w => w.Contains("colleagues.suggest", StringComparison.Ordinal));
    }

    [Fact]
    public void Both_config_pages_set_the_switch_and_write_it()
    {
        var settings = ProviderConfigLoader.Parse("""{ "providers": {} }""", new Dictionary<string, string>());
        var form = CxAgent.Web.WebConfigForms.Colleagues(settings, new ColleagueStore(_config), null).ToJsonString();
        Assert.Contains("colleagueSuggest", form);

        var draft = CxAgent.UI.Config.GeneralDraft.From(settings);
        CxAgent.Web.WebConfigForms.SetGeneral(draft, new System.Text.Json.Nodes.JsonObject { ["colleagueSuggest"] = "false" });
        Assert.False(draft.ColleagueSuggestions);
        Assert.True(draft.IsDirty);

        var root = new System.Text.Json.Nodes.JsonObject();
        draft.Apply(root);
        Assert.False(root["colleagues"]!["suggest"]!.GetValue<bool>());
    }
}
