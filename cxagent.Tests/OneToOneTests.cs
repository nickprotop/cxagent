using CxAgent.Core.Colleagues;
using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// A colleague's one-to-one: a session of its own, the colleague as its agent, read-only, kept out of every list a
/// session is offered from, every turn a round — and the same conversation each time it is opened.
/// </summary>
public sealed class OneToOneTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("one-to-one-").FullName;
    private readonly string _config;
    private readonly string _project;
    private readonly AppPaths _paths;

    public OneToOneTests()
    {
        _config = Path.Combine(_root, "cfg");
        _project = Path.Combine(_root, "cxgpu");
        Directory.CreateDirectory(_config);
        Directory.CreateDirectory(_project);
        _paths = new AppPaths(_config);
        _paths.EnsureCreated();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private ColleagueStore Library => new(_config);
    private FolderSessionStore Store => new(_paths);

    private void Charting()
    {
        Library.Create(new ColleagueRecord { Name = "Charting", Type = "general", Created = DateTimeOffset.UtcNow });
        Library.Update("Charting", r => r with { Workplaces = [new Workplace(Codebase.Key(_project), _project, 1, DateTimeOffset.UtcNow)] });
        Library.WriteCore("Charting", "## Lessons\nPanes first.");
    }

    private Session Wire(Session session, ILlmProvider model)
    {
        var resolution = ResolvedConfig.ForTesting(model) is var r
            ? r with { Model = r.Model! with { ContextWindow = 200_000 }, Catalog = r.Catalog with { Colleagues = true } }
            : null!;
        SessionFactory.Wire(session, resolution,
            new SharedServices { GlobalInstructionsDir = _config, Resume = Store, Logs = new LogFileManager(_paths) },
            new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() }, AgentMode.Single);
        return session;
    }

    /// <summary>A lead session, to ask its desk for the one-to-one, then the one-to-one itself as a front end opens it.</summary>
    private (Session OneToOne, OneToOneOpening Opening) Opened(ILlmProvider model)
    {
        var lead = Wire(new Session(_project), model);
        var opening = lead.Colleagues!.OneToOne("Charting", out var refusal)!;
        Assert.Null(refusal);
        var session = new Session(opening.Folder);
        session.BecomeOneToOne(opening.Setup);
        if (opening.Resume is { } resume) session.PendResume(resume);
        return (Wire(session, model), opening);
    }

    [Fact]
    public void A_tagged_session_survives_every_turn_and_is_never_offered()
    {
        Store.SaveTurn(new FolderSessionStore.ResumeTurn("ONE", [new ChatMessage { Role = "user", Content = "a" }], 1, 1, _project) { Tag = "colleague:Charting" });
        Store.SaveTurn(new FolderSessionStore.ResumeTurn("ONE", [new ChatMessage { Role = "user", Content = "a" }, new ChatMessage { Role = "assistant", Content = "b" }], 2, 2, _project) { Tag = "colleague:Charting" });
        Store.SaveTurn(new FolderSessionStore.ResumeTurn("PLAIN", [new ChatMessage { Role = "user", Content = "c" }], 1, 1, _project));

        Assert.Equal(["PLAIN"], Store.List(all: true).Select(i => i.Uid));
        Assert.Equal(["PLAIN"], Core.Commands.SessionsCommand.Offered(Store.List(_project)).Select(i => i.Uid));
        Assert.Equal("PLAIN", Store.LoadLatestUnfinished(_project)?.AgentId);
        Assert.Equal("PLAIN", Store.LoadLatestUncontinued(_project)?.AgentId);
        Assert.Equal(["ONE"], Store.ListTagged("colleague:Charting").Select(i => i.Uid));
    }

    [Fact]
    public void Its_tools_are_read_only()
    {
        Charting();

        var (session, _) = Opened(new MockLlmProvider());

        var tools = session.OfferedToolNames();
        Assert.Contains(Tool.ReadFile, tools);
        Assert.Contains(Tool.Grep, tools);
        foreach (var never in new[] { Tool.WriteFile, Tool.ReplaceInFile, Tool.RunShell, Tool.Agent, Tool.Colleague, Tool.HttpRequest })
            Assert.DoesNotContain(never, tools);
    }

    [Fact]
    public async Task Its_briefing_heads_the_prompt_and_every_turn_is_a_round()
    {
        Charting();
        var model = new MockLlmProvider();
        model.EnqueueResponse(new LlmResponse { Text = "Panes first, because the legend follows the series.", StopReason = "end_turn" });
        model.EnqueueResponse(new LlmResponse { Text = "NO CHANGE", StopReason = "end_turn" });   // the distiller
        var (session, _) = Opened(model);

        await session.SendAndWait("why do you lay charts out in panes?");
        await session.Colleagues!.Idle();

        var past = Library.Past("Charting");
        var round = Assert.Single(past);
        Assert.Contains(round.Messages, m => m.Content == "why do you lay charts out in panes?");
        Assert.Contains(round.Messages, m => m.Content.StartsWith("Panes first", StringComparison.Ordinal));
    }

    [Fact]
    public void The_briefing_says_it_is_talking_with_the_user()
    {
        Charting();
        var lead = Wire(new Session(_project), new MockLlmProvider());

        var opening = lead.Colleagues!.OneToOne("Charting", out _)!;

        Assert.Contains("You are Charting", opening.Setup.Briefing);
        Assert.Contains("Panes first.", opening.Setup.Briefing);
        Assert.Contains("talking with the user directly", opening.Setup.Briefing);
        Assert.Equal("with Charting", opening.Setup.Title);
        Assert.Equal(_project, opening.Folder);
    }

    [Fact]
    public async Task Open_again_returns_to_the_same_conversation()
    {
        Charting();
        var model = new MockLlmProvider();
        model.EnqueueResponse(new LlmResponse { Text = "hello", StopReason = "end_turn" });
        model.EnqueueResponse(new LlmResponse { Text = "NO CHANGE", StopReason = "end_turn" });
        var (first, opening) = Opened(model);
        Assert.Null(opening.Resume);
        await first.SendAndWait("hi");
        await first.Colleagues!.Idle();
        var uid = first.SessionId;

        var again = Wire(new Session(_project), model).Colleagues!.OneToOne("Charting", out _)!;

        Assert.Equal(uid, again.Resume?.AgentId);
    }

    [Fact]
    public void An_unknown_colleague_is_refused()
    {
        var lead = Wire(new Session(_project), new MockLlmProvider());

        Assert.Null(lead.Colleagues!.OneToOne("Nobody", out var refusal));
        Assert.Contains("no colleague called 'Nobody'", refusal);
    }

    [Fact]
    public void The_command_table_offers_open()
    {
        Assert.Contains(Core.Commands.SessionCommands.ArgumentsFor("/colleagues "), a => a.Name.StartsWith("open", StringComparison.Ordinal));
    }
}
