using System.Text.Json;
using CxAgent.Core.Colleagues;
using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The lead's <c>colleague</c> tool: listing, bringing one in, and keeping a worker — which only the user's yes does.
/// </summary>
public sealed class ColleagueToolTests : IDisposable
{
    private readonly string _config = Directory.CreateTempSubdirectory("colleague-tool-config-").FullName;
    private readonly string _project = Directory.CreateTempSubdirectory("colleague-tool-proj-").FullName;

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
            new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() }, AgentMode.Single);
        return session;
    }

    private static ToolCall Call(object args) => new()
    {
        Name = "colleague", Id = "c1", Arguments = JsonSerializer.SerializeToElement(args),
    };

    private static Func<IReadOnlyList<UserQuestion>, CancellationToken, Task<QuestionAnswers>> Answering(string answer, List<UserQuestion>? asked = null) =>
        (questions, _) =>
        {
            asked?.AddRange(questions);
            return Task.FromResult(new QuestionAnswers([answer]));
        };

    private string Worker(Session session)
    {
        var provider = new MockLlmProvider();
        var child = new SubAgentFactory(new SubAgentFactory.SubAgentRuntime
        {
            Provider = provider, Ledger = new TokenLedger(), MaxTurns = 5,
            Tools = new AgentTools(JobRegistry.CreateWithBuiltins()),
        }).Create();
        child.Agent.Context.Replace([new ChatMessage { Role = "user", Content = "chart it" }, new ChatMessage { Role = "assistant", Content = "charted" }]);
        return session.SubAgents.Keep(child, "chart layout");
    }

    [Fact]
    public async Task List_names_each_colleague_with_its_description()
    {
        Library.Create(new ColleagueRecord { Name = "Charting", Type = "general", Created = DateTimeOffset.UtcNow, Description = "lays out charts" });
        var tool = new ColleagueTool(Open().Colleagues!, null, () => "lead");

        var answer = await tool.TryInvokeAsync(Call(new { action = "list" }), CancellationToken.None);

        Assert.Contains("Charting — lays out charts", answer);
    }

    [Fact]
    public async Task Keep_asks_and_keeps_on_yes()
    {
        var session = Open();
        var handle = Worker(session);
        var asked = new List<UserQuestion>();
        var tool = new ColleagueTool(session.Colleagues!, Answering("Keep", asked), () => "lead");

        var answer = await tool.TryInvokeAsync(Call(new { action = "keep", worker = handle, name = "Charting" }), CancellationToken.None);

        var question = Assert.Single(asked);
        Assert.Contains($"Keep {handle} as a colleague named Charting?", question.Question);
        Assert.Equal(["Keep", "Don't keep"], question.Choices.Select(c => c.Label));
        Assert.Contains("Kept", answer);
        Assert.NotNull(Library.Find("Charting"));
    }

    /// <summary>A live drive's lead left the name out; the question read "named (a name from its work)".</summary>
    [Fact]
    public async Task Keep_without_a_name_asks_plainly()
    {
        var session = Open();
        var handle = Worker(session);
        var asked = new List<UserQuestion>();
        var tool = new ColleagueTool(session.Colleagues!, Answering("Don't keep", asked), () => "lead");

        await tool.TryInvokeAsync(Call(new { action = "keep", worker = handle }), CancellationToken.None);

        Assert.StartsWith($"Keep {handle} as a colleague? It is named from its work.", Assert.Single(asked).Question);
    }

    /// <summary>
    /// A live session's lead asked to keep the colleague it was already talking to; the user was asked a question with
    /// no right answer. What keeping would refuse is refused before anyone is asked.
    /// </summary>
    [Fact]
    public async Task Keep_of_what_cannot_be_kept_is_refused_without_asking()
    {
        var session = Open();
        var handle = Worker(session);
        session.Colleagues!.Keep(new KeepOrder(handle, "Charting"));
        var asked = new List<UserQuestion>();
        var tool = new ColleagueTool(session.Colleagues, Answering("Keep", asked), () => "lead");

        var already = await tool.TryInvokeAsync(Call(new { action = "keep", worker = handle, name = "Charting" }), CancellationToken.None);
        var nobody = await tool.TryInvokeAsync(Call(new { action = "keep", worker = "nobody" }), CancellationToken.None);

        Assert.Empty(asked);
        Assert.Contains("already the colleague Charting", already);
        Assert.Contains("no worker called 'nobody'", nobody);
    }

    [Fact]
    public async Task Keep_does_nothing_on_no()
    {
        var session = Open();
        var handle = Worker(session);
        var tool = new ColleagueTool(session.Colleagues!, Answering("Don't keep"), () => "lead");

        var answer = await tool.TryInvokeAsync(Call(new { action = "keep", worker = handle, name = "Charting" }), CancellationToken.None);

        Assert.Equal("The user did not keep it.", answer);
        Assert.Null(Library.Find("Charting"));
    }

    [Fact]
    public async Task Keep_without_a_user_is_refused()
    {
        var session = Open();
        var handle = Worker(session);
        var tool = new ColleagueTool(session.Colleagues!, null, () => "lead");

        var answer = await tool.TryInvokeAsync(Call(new { action = "keep", worker = handle }), CancellationToken.None);

        Assert.Contains("needs the user", answer);
        Assert.Empty(Library.List());
    }

    [Fact]
    public async Task Bring_returns_the_spawn_envelope()
    {
        Library.Create(new ColleagueRecord { Name = "Charting", Type = "general", Created = DateTimeOffset.UtcNow });
        var session = Open();
        var tool = new ColleagueTool(session.Colleagues!, null, () => session.SessionId!);

        var answer = await tool.TryInvokeAsync(Call(new { action = "bring", name = "Charting", task = "chart the sales" }), CancellationToken.None);

        Assert.Equal("charting", SubAgentEnvelope.NameOf(answer));
    }

    /// <summary>
    /// A live lead brought "a colleague" with a task and no name, twice, and was refused twice with nothing to go on. It
    /// is pointed at the list to choose from — never given one picked for it (user, 2026-11-08).
    /// </summary>
    [Fact]
    public async Task Bring_without_a_name_points_to_the_list_to_choose_from()
    {
        Library.Create(new ColleagueRecord { Name = "Charting", Type = "general", Created = DateTimeOffset.UtcNow });
        var session = Open();
        var tool = new ColleagueTool(session.Colleagues!, null, () => session.SessionId!);

        var answer = await tool.TryInvokeAsync(Call(new { action = "bring", task = "chart the sales" }), CancellationToken.None);

        Assert.Contains("action list", answer);
        Assert.Contains("whose experience fits", answer);
        Assert.Empty(session.SubAgents.All());
    }

    [Fact]
    public async Task Another_tool_s_call_is_not_answered()
    {
        var tool = new ColleagueTool(Open().Colleagues!, null, () => "lead");

        Assert.Null(await tool.TryInvokeAsync(new ToolCall { Name = "memory", Arguments = JsonSerializer.SerializeToElement(new { }) }, CancellationToken.None));
    }

    [Fact]
    public void The_lead_is_offered_the_tool_with_an_empty_library_and_a_child_never()
    {
        var session = Open();
        Assert.Contains("colleague", session.OfferedToolNames());

        var child = new SubAgentFactory(new SubAgentFactory.SubAgentRuntime
        {
            Provider = new MockLlmProvider(), Ledger = new TokenLedger(), MaxTurns = 5,
            Tools = new AgentTools(JobRegistry.CreateWithBuiltins()) { Colleagues = session.Colleagues },
        }).Create();
        Assert.DoesNotContain("colleague", child.Agent.OfferedToolNames());
    }

    [Fact]
    public void Off_means_no_tool()
    {
        Assert.DoesNotContain("colleague", Open(on: false).OfferedToolNames());
    }
}
