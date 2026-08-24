using System.Text.Json;
using CxAgent.Core.Agents;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Permissions;
using CxAgent.Core.Jobs;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// A plugin loads at a turn boundary, after the agent already exists — so its tools cannot arrive
/// through a field a constructor captures once. These tests exercise the seam a later registry
/// (not built yet) will fill: a source the agent CONSULTS fresh each turn, the same way it already
/// consults <c>_skills</c> rather than a snapshot of the catalog taken at construction.
/// </summary>
public class DynamicToolSourceTests
{
    private sealed class MutableToolSource
    {
        private readonly List<IAgentTool> _tools = [];
        public IReadOnlyList<IAgentTool> Get() => _tools.ToList();
        public void Add(IAgentTool tool) => _tools.Add(tool);
        public void Clear() => _tools.Clear();
    }

    private sealed class RecordingTool : IAgentTool
    {
        private readonly string _name;
        public int Calls { get; private set; }

        public ToolDefinition Definition { get; }

        public RecordingTool(string name)
        {
            _name = name;
            Definition = new(name, "a tool contributed after construction",
                JsonSerializer.SerializeToElement(new { type = "object", properties = new { } }));
        }

        public PermissionRequest? Gate(JobParameters call) => null;

        public Task<JobResult> ExecuteAsync(JobParameters call, IJobContext context, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new JobResult
            {
                Success = true,
                Output = { ["content"] = "ran: " + _name },
            });
        }
    }

    private static LlmResponse Done(string text) =>
        new() { Text = text, StopReason = "end_turn", Usage = new LlmUsage { InputTokens = 10, OutputTokens = 2 } };

    [Fact]
    public async Task AToolAddedAfterConstructionIsOfferedOnTheNextTurn()
    {
        var source = new MutableToolSource();
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(Done("first turn, nothing to call yet"));

        var agent = new Agent(provider, JobRegistry.CreateWithBuiltins(), new TokenLedger(),
            new BufferedChatSink(), new BufferedJobPanel(), logs: null, maxTurns: 5,
            dynamicTools: source.Get);

        await agent.SendAsync("go", CancellationToken.None);

        // NOTHING WAS OFFERED YET: the source was empty at the time definitions were built.
        Assert.DoesNotContain(agent.LastOfferedToolNamesForTest, n => n == "late_tool");

        // ADDED BETWEEN TURNS, the way a plugin loading at a turn boundary would.
        source.Add(new RecordingTool("late_tool"));

        provider.EnqueueResponse(Done("second turn"));
        await agent.SendAsync("go again", CancellationToken.None);

        Assert.Contains("late_tool", agent.LastOfferedToolNamesForTest);
    }

    [Fact]
    public async Task AToolAddedAfterConstructionDispatches()
    {
        var source = new MutableToolSource();
        var tool = new RecordingTool("late_tool");

        var provider = new MockLlmProvider();
        // Registered before the agent even sees its first prompt, so this turn's definitions
        // already include it — the point under test is DISPATCH, not the offer timing above.
        source.Add(tool);
        provider.EnqueueResponse(LlmResponse.WithToolCall("late_tool", new { }));
        provider.EnqueueResponse(Done("finished"));

        var agent = new Agent(provider, JobRegistry.CreateWithBuiltins(), new TokenLedger(),
            new BufferedChatSink(), new BufferedJobPanel(), logs: null, maxTurns: 5,
            dynamicTools: source.Get);

        await agent.SendAsync("go", CancellationToken.None);

        Assert.Equal(1, tool.Calls);
    }

    [Fact]
    public async Task AToolRemovedBetweenTurnsIsNoLongerOffered()
    {
        var source = new MutableToolSource();
        var tool = new RecordingTool("late_tool");
        source.Add(tool);

        var provider = new MockLlmProvider();
        provider.EnqueueResponse(Done("first turn"));

        var agent = new Agent(provider, JobRegistry.CreateWithBuiltins(), new TokenLedger(),
            new BufferedChatSink(), new BufferedJobPanel(), logs: null, maxTurns: 5,
            dynamicTools: source.Get);

        await agent.SendAsync("go", CancellationToken.None);
        Assert.Contains("late_tool", agent.LastOfferedToolNamesForTest);

        // REMOVED, THE WAY A PLUGIN UNLOADED AT A TURN BOUNDARY WOULD BE. No call is in flight for
        // it — the source is read once per turn, not mid-turn — so this is safe between requests.
        source.Clear();

        provider.EnqueueResponse(Done("second turn"));
        await agent.SendAsync("go again", CancellationToken.None);

        Assert.DoesNotContain(agent.LastOfferedToolNamesForTest, n => n == "late_tool");
    }
}
