using CxAgent.Core.Colleagues;
using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The colleagues the lead's prompt lists: taken once, fixed for the conversation like memory's index, retaken when the
/// cache is rebuilt anyway — and what changed in between said at the front of the next message.
/// </summary>
public sealed class ColleagueIndexTests : IDisposable
{
    private readonly string _config = Directory.CreateTempSubdirectory("colleague-index-config-").FullName;
    private readonly string _project = Directory.CreateTempSubdirectory("colleague-index-proj-").FullName;

    public void Dispose()
    {
        foreach (var d in new[] { _config, _project })
            try { Directory.Delete(d, recursive: true); } catch (IOException) { }
    }

    private ColleagueStore Library => new(_config);

    private void Colleague(string name, string description)
    {
        Library.Create(new ColleagueRecord { Name = name, Created = DateTimeOffset.UtcNow, Description = description });
        Library.Update(name, r => r with { Workplaces = [new Workplace("cxgpu-12345678", "/home/dev/source/cxgpu", 1, DateTimeOffset.UtcNow)] });
    }

    private Session Open(CapturingProvider provider, bool on = true)
    {
        var resolution = ResolvedConfig.ForTesting(provider) is var r
            ? r with { Model = r.Model! with { ContextWindow = 200_000 }, Catalog = r.Catalog with { Colleagues = on } }
            : null!;
        var session = new Session(_project);
        SessionFactory.Wire(session, resolution, new SharedServices { GlobalInstructionsDir = _config },
            new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() }, AgentMode.Single);
        return session;
    }

    private static string System(CapturingProvider p, int i) => p.Requests[i].First(m => m.Role == "system").Content;

    private static string LastUser(CapturingProvider p, int i) => p.Requests[i].Last(m => m.Role == "user").Content;

    [Fact]
    public void Render_lists_each_and_counts_the_rest_past_the_cap()
    {
        var lines = Enumerable.Range(1, 100)
            .Select(i => new ColleagueLine($"Colleague{i}", "knows a thing or two about charts", ["cxgpu"])).ToList();

        var text = ColleagueIndex.Render(lines, 500);

        Assert.StartsWith("- Colleague1 — knows a thing or two about charts · knows: cxgpu\n", text);
        Assert.True(text.Length < 600);
        Assert.Contains("more — the colleague tool lists them all.", text);
    }

    [Fact]
    public async Task Empty_library_adds_no_section()
    {
        var on = new CapturingProvider();
        var off = new CapturingProvider();
        await Open(on).SendAndWait("one");
        await Open(off, on: false).SendAndWait("one");

        Assert.Equal(System(off, 0), System(on, 0));
        Assert.DoesNotContain("# Colleagues", System(on, 0));
    }

    [Fact]
    public async Task The_lead_lists_its_colleagues()
    {
        Colleague("Charting", "lays out GPU charts");
        var provider = new CapturingProvider();

        await Open(provider).SendAndWait("one");

        Assert.Contains("# Colleagues", System(provider, 0));
        Assert.Contains("- Charting — lays out GPU charts · knows: cxgpu", System(provider, 0));
        // THE GUIDANCE IS A ROLE, NOT A CHECKLIST (user, 2026-11-08): teammates, asked before guessing or repeating.
        Assert.Contains("teammates who have worked where you have not", System(provider, 0));
        Assert.Contains("ask before you guess", System(provider, 0));
    }

    [Fact]
    public async Task A_session_without_the_tool_gets_no_section()
    {
        Colleague("Charting", "lays out GPU charts");
        var provider = new CapturingProvider();
        var resolution = ResolvedConfig.ForTesting(provider) is var r
            ? r with
            {
                Model = r.Model! with { ContextWindow = 200_000 },
                Catalog = r.Catalog with { Colleagues = true, Tools = new ToolSelection([Tool.Inherited, Tool.Not.Colleague]) },
            }
            : null!;
        var session = new Session(_project);
        SessionFactory.Wire(session, resolution, new SharedServices { GlobalInstructionsDir = _config },
            new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() }, AgentMode.Single);

        await session.SendAndWait("one");

        Assert.DoesNotContain("# Colleagues", System(provider, 0));
    }

    [Fact]
    public void A_child_gets_no_colleagues_section()
    {
        string Prompt(bool child) => SystemPrompt.Build(new SystemPromptContext(
            WorkingDirectory: "/tmp/x", IsGitRepo: false, Platform: "linux", Today: new DateOnly(2026, 11, 8), ModelId: "m")
        {
            IsSubAgent = child,
            Colleagues = [new ColleagueLine("Charting", "lays out charts", ["cxgpu"])],
        });

        Assert.DoesNotContain("# Colleagues", Prompt(child: true));
        Assert.Contains("# Colleagues", Prompt(child: false));
    }

    [Fact]
    public async Task Keeping_mid_session_leaves_the_prompt_byte_identical_and_says_so_next()
    {
        Colleague("Charting", "lays out GPU charts");
        var provider = new CapturingProvider();
        var session = Open(provider);
        await session.SendAndWait("one");

        Colleague("Invoices", "knows the invoice schema");
        await session.SendAndWait("two");
        await session.SendAndWait("three");

        Assert.Equal(System(provider, 0), System(provider, 1));
        Assert.StartsWith("[Colleagues changed", LastUser(provider, 1));
        Assert.Contains("'Invoices' was kept — knows the invoice schema", LastUser(provider, 1));
        Assert.Equal("three", LastUser(provider, 2));   // said once
    }

    [Fact]
    public async Task Clear_retakes_the_list()
    {
        var provider = new CapturingProvider();
        var session = Open(provider);
        await session.SendAndWait("one");

        Colleague("Charting", "lays out GPU charts");
        session.ClearContext();
        await session.SendAndWait("two");

        Assert.Contains("- Charting — lays out GPU charts", System(provider, 1));
        Assert.Equal("two", LastUser(provider, 1));
    }

    private sealed class CapturingProvider : ILlmProvider
    {
        public List<List<ChatMessage>> Requests { get; } = [];
        public string ProviderId => "capturing";
        public string DisplayName => "Capturing";
        public string ModelId => "test-model";
        public bool SupportsToolCalling => true;
        public bool SupportsStreaming => false;

        public Task<LlmResponse> ChatAsync(List<ChatMessage> messages, List<ToolDefinition>? tools, CancellationToken ct)
        {
            Requests.Add([.. messages]);
            return Task.FromResult(new LlmResponse { Text = "ok", StopReason = "end_turn" });
        }

        public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(List<ChatMessage> messages, List<ToolDefinition>? tools,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            var r = await ChatAsync(messages, tools, ct);
            yield return new LlmStreamChunk(r.Text, null, true, null, r.StopReason);
        }
    }
}
