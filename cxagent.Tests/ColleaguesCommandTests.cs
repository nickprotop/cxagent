using CxAgent.Core.Colleagues;
using CxAgent.Core.Commands;
using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// <c>/colleagues</c>: the user's half of the lead's colleague tool, in Core so the terminal and the browser run the
/// same thing — listing, keeping (the user typing it is the yes) and bringing one in.
/// </summary>
public sealed class ColleaguesCommandTests : IDisposable
{
    private readonly string _config = Directory.CreateTempSubdirectory("colleagues-cmd-config-").FullName;
    private readonly string _project = Directory.CreateTempSubdirectory("colleagues-cmd-proj-").FullName;
    private readonly BufferedChatSink _seen = new();

    public void Dispose()
    {
        foreach (var d in new[] { _config, _project })
            try { Directory.Delete(d, recursive: true); } catch (IOException) { }
    }

    private ColleagueStore Library => new(_config);

    private Session Open(bool on = true)
    {
        var resolution = ResolvedConfig.ForTesting(new MockLlmProvider()) is var r
            ? r with { Model = r.Model! with { ContextWindow = 200_000 }, Catalog = r.Catalog with { Colleagues = on } }
            : null!;
        var session = new Session(_project);
        SessionFactory.Wire(session, resolution, new SharedServices { GlobalInstructionsDir = _config },
            new SessionPorts { Observer = _seen, ToolObserver = new BufferedJobPanel() }, AgentMode.Single);
        return session;
    }

    private static string Worker(Session session)
    {
        var child = new SubAgentFactory(new SubAgentFactory.SubAgentRuntime
        {
            Provider = new MockLlmProvider(), Ledger = new TokenLedger(), MaxTurns = 5,
            Tools = new AgentTools(JobRegistry.CreateWithBuiltins()),
        }).Create();
        child.Agent.Context.Replace([new ChatMessage { Role = "user", Content = "chart it" }, new ChatMessage { Role = "assistant", Content = "charted" }]);
        return session.SubAgents.Keep(child, "chart layout");
    }

    private static Func<string, IReadOnlyList<CommandArgument>> Values(Session session) =>
        set => [.. session.Values(set).Select(v => new CommandArgument(v.Name, v.Summary))];

    [Fact]
    public void Colleagues_lists_or_says_none_yet()
    {
        var session = Open();
        session.ColleaguesCommand(null);
        Assert.Contains(_seen.Notices, n => n.Contains("No colleagues yet", StringComparison.Ordinal));

        Library.Create(new ColleagueRecord { Name = "Charting", Type = "general", Created = DateTimeOffset.UtcNow, Description = "lays out charts" });
        session.ColleaguesCommand("");
        Assert.Contains(_seen.Notices, n => n.Contains("Charting — lays out charts", StringComparison.Ordinal));
    }

    [Fact]
    public void Keep_verb_keeps_without_asking()
    {
        var session = Open();
        var handle = Worker(session);

        session.ColleaguesCommand($"keep {handle} UI Charting");

        Assert.NotNull(Library.Find("UI Charting"));
        Assert.Contains(_seen.Notices, n => n.Contains("Kept", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Bring_verb_brings_a_colleague_in_and_tells_the_lead_next()
    {
        Library.Create(new ColleagueRecord { Name = "UI Charting", Type = "general", Created = DateTimeOffset.UtcNow });
        var session = Open();

        await session.BringColleagueAsync("UI Charting draw the sales chart");

        // IN THE BACKGROUND: the receipt is back before the worker has read its task, so the task is checked in what
        // the lead is told rather than in the worker's context.
        Assert.NotNull(session.SubAgents.Find("ui-charting"));
        var note = session.TakeColleagueNote();
        Assert.StartsWith("[The user brought the colleague UI Charting in as the worker ui-charting", note);
        Assert.Contains("on: draw the sales chart", note);
    }

    /// <summary>A name with spaces typed in quotes, as a shell would want it: a live use read the quote as part of the name.</summary>
    [Theory]
    [InlineData("\"UI Charting\" draw the sales chart")]
    [InlineData("'UI Charting' draw the sales chart")]
    public async Task Bring_takes_a_quoted_name(string typed)
    {
        Library.Create(new ColleagueRecord { Name = "UI Charting", Type = "general", Created = DateTimeOffset.UtcNow });
        var session = Open();

        await session.BringColleagueAsync(typed);

        Assert.NotNull(session.SubAgents.Find("ui-charting"));
        Assert.Contains("on: draw the sales chart", session.TakeColleagueNote());
    }

    /// <summary>
    /// The whole name and no task — once answered "no colleague called 'Explore'", now a colleague joining idle (user,
    /// 2026-11-08).
    /// </summary>
    [Theory]
    [InlineData("Explore SharpConsoleUI API")]
    [InlineData("explore sharpconsoleui api  ")]
    [InlineData("\"Explore SharpConsoleUI API\"")]
    public async Task Bring_with_a_name_and_no_task_joins_idle(string typed)
    {
        Library.Create(new ColleagueRecord { Name = "Explore SharpConsoleUI API", Type = "general", Created = DateTimeOffset.UtcNow });
        var session = Open();

        await session.BringColleagueAsync(typed);

        Assert.Contains(_seen.Notices, n => n.Contains("Explore SharpConsoleUI API joined as explore-sharpconsoleui-api, idle", StringComparison.Ordinal));
        Assert.NotNull(session.SubAgents.Find("explore-sharpconsoleui-api"));
    }

    [Fact]
    public async Task Bring_matches_a_name_in_any_case()
    {
        Library.Create(new ColleagueRecord { Name = "Explore SharpConsoleUI API", Type = "general", Created = DateTimeOffset.UtcNow });
        var session = Open();

        await session.BringColleagueAsync("explore sharpconsoleui api look at the dialogs");

        Assert.NotNull(session.SubAgents.Find("explore-sharpconsoleui-api"));
    }

    [Fact]
    public void Keep_takes_a_quoted_name()
    {
        var session = Open();
        var handle = Worker(session);

        session.ColleaguesCommand($"keep {handle} \"UI Charting\"");

        Assert.Equal("UI Charting", Library.Find("UI Charting")?.Name);
    }

    [Fact]
    public void Completion_offers_worker_handles_after_keep_and_colleagues_after_bring()
    {
        Library.Create(new ColleagueRecord { Name = "Charting", Type = "general", Created = DateTimeOffset.UtcNow });
        var session = Open();
        var handle = Worker(session);

        Assert.Contains(SessionCommands.ArgumentsFor("/colleagues keep ", Values(session)), a => a.Name == handle);
        Assert.Contains(SessionCommands.ArgumentsFor("/colleagues bring ", Values(session)), a => a.Name == "Charting");
        Assert.Contains(SessionCommands.ArgumentsFor("/colleagues ", Values(session)), a => a.Name.StartsWith("keep", StringComparison.Ordinal));
    }

    [Fact]
    public void Off_says_how_to_turn_it_on()
    {
        var session = Open(on: false);

        session.ColleaguesCommand("keep x");

        Assert.Contains(_seen.Notices, n => n.Contains("colleagues.enabled", StringComparison.Ordinal));
    }

    [Fact]
    public void The_browser_may_run_it() =>
        Assert.Contains("/colleagues", CxAgent.Web.WebCommands.Allowed);
}
