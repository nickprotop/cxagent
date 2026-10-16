using System.Text.Json.Nodes;
using CxAgent.Core.Config;
using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Agent types in config.json: a built-in may be told where it runs, how long, and with which tools —
/// never what it is told; a type of the user's own is theirs entirely. A saved change reaches running
/// sessions in place.
/// </summary>
public class AgentTypeEditsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "agent-edits-" + Guid.NewGuid().ToString("N"));
    public AgentTypeEditsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    private static JsonObject Doc() => JsonNode.Parse("""
        { "providers": {}, "agents": { "researcher": { "briefing": "You research.", "note": "mine" } } }
        """)!.AsObject();

    [Fact]
    public void ABuiltinsOverride_TakesProviderTurnsAndTools()
    {
        var root = Doc();
        AgentEdits.SetOverride(root, "explore", new AgentTypeFields { Provider = "local", MaxTurns = 30, Tools = ["inherited", "-write_file"] });

        var explore = root["agents"]!["explore"]!;
        Assert.Equal("local", (string?)explore["provider"]);
        Assert.Equal(30, (int?)explore["maxTurns"]);
        Assert.Equal(2, explore["tools"]!.AsArray().Count);
    }

    // THE SHIPPED BRIEFING IS A CONTRACT WITH THE CODE: refused here, as the loader ignores it.
    [Fact]
    public void ABuiltinsBriefing_IsRefused() =>
        Assert.Throws<ConfigEditException>(() =>
            AgentEdits.SetOverride(Doc(), "planner", new AgentTypeFields { Briefing = "do it my way" }));

    [Fact]
    public void ResetBuiltin_RemovesTheOverride()
    {
        var root = Doc();
        AgentEdits.SetOverride(root, "explore", new AgentTypeFields { MaxTurns = 30 });
        AgentEdits.ResetBuiltin(root, "explore");
        Assert.Null(root["agents"]!["explore"]);
    }

    [Fact]
    public void AnOwnType_NeedsABriefing() =>
        Assert.Throws<ConfigEditException>(() => AgentEdits.AddType(Doc(), "reviewer2", new AgentTypeFields { Description = "x" }));

    [Fact]
    public void AnOwnType_CannotTakeABuiltinsName() =>
        Assert.Throws<ConfigEditException>(() => AgentEdits.AddType(Doc(), "planner", new AgentTypeFields { Briefing = "b" }));

    [Fact]
    public void RenameAndRemove_KeepUnknownKeys()
    {
        var root = Doc();
        AgentEdits.RenameType(root, "researcher", "reader");
        Assert.Equal("mine", (string?)root["agents"]!["reader"]!["note"]);
        AgentEdits.RemoveType(root, "reader");
        Assert.Null(root["agents"]!["reader"]);
    }

    // THE FEWEST TERMS: what a base leaves out, said once each.
    [Fact]
    public void ToolTerms_WriteTheBaseAndWhatItLeavesOut()
    {
        Assert.Equal(["inherited", "-write_file", "-run_shell"],
            ToolTerms.Write(new ToolTerms("inherited", ["write_file", "run_shell"], [])));
        Assert.Equal(["all"], ToolTerms.Write(new ToolTerms("all", [], [])));
    }

    [Fact]
    public void ToolTerms_ReadWhatWasWritten()
    {
        var read = ToolTerms.Read(["inherited", "-grep", "+agent"]);
        Assert.Equal("inherited", read.Base);
        Assert.Contains("grep", read.Removed);
        Assert.Contains("agent", read.Added);
    }

    // ---- in place ------------------------------------------------------------------------------------

    private static ResolvedConfig WithTypes(ILlmProvider provider, Dictionary<string, AgentTypeConfig> types)
    {
        var registry = ProviderRegistry.FromProviders(new Dictionary<string, ILlmProvider> { ["local"] = provider }, "local");
        return new ResolvedConfig(registry.Use("local"), new ProviderCatalog(Instances: registry, AgentTypes: types), []);
    }

    // A NEW TYPE IS ANNOUNCED IN THE NEXT MESSAGE, once — the spawn tool's description is part of the
    // cached prompt and stays as it was.
    [Fact]
    public async Task ANewType_IsAnnouncedOnce_InTheNextMessage_AndTheHostStays()
    {
        var model = new MockLlmProvider();
        for (var i = 0; i < 3; i++) model.EnqueueResponse(new LlmResponse { Text = "ok", StopReason = "end_turn" });
        var manager = SessionManager.Create(new AppPaths(_dir));
        using var _m = manager;
        var session = manager.Open(_dir, WithTypes(model, []),
            new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() }, AgentMode.FanOut);
        await Assert.IsType<Session.SubmitOutcome.Started>(session.Submit("first")).Turn;
        var host = session.Host;

        manager.ApplyConfig(WithTypes(model, new() { ["researcher"] = new("You research.", Description: "reads outside the repo") }),
            new ConfigChange(new HashSet<string>(), new Dictionary<string, string>(), true, "agent types"), CacheChoice.ApplyNow);

        await Assert.IsType<Session.SubmitOutcome.Started>(session.Submit("second")).Turn;
        var second = model.LastMessages!.Last(m => m.Role == "user").Content!;
        await Assert.IsType<Session.SubmitOutcome.Started>(session.Submit("third")).Turn;
        var third = model.LastMessages!.Last(m => m.Role == "user").Content!;

        Assert.Same(host, session.Host);
        Assert.Contains("researcher", second);
        Assert.DoesNotContain("researcher", third);
    }
}
