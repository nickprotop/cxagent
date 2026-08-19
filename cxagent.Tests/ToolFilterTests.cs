using System.Text.Json;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Permissions;
using CxAgent.Core.Plugins;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The filter, at both sites.
///
/// <para>`Agent` decides what the model is TOLD (the assembly at :767) and what it may RUN (the
/// dispatch chain at :1753). A selection that moves one and not the other produces a tool that is
/// offered and refused, or hidden and callable — so both are tested here rather than trusting that
/// one implies the other.</para>
/// </summary>
public class ToolFilterTests
{
    private sealed class EchoTool : IAgentTool
    {
        public ToolDefinition Definition { get; } = new(
            "echo_tool", "echoes", JsonSerializer.SerializeToElement(new { type = "object" }));

        public PermissionRequest? Gate(JobParameters call) => null;

        public Task<JobResult> ExecuteAsync(JobParameters call, IJobContext context, CancellationToken ct) =>
            Task.FromResult(new JobResult { Success = true, Output = { ["content"] = "echoed" } });
    }

    /// <summary>An MCP server that is simply connected, so its tools are in the assembled list.</summary>
    private sealed class FakeServer(string name, params CxAgent.Core.Mcp.McpToolDef[] tools)
        : CxAgent.Core.Mcp.IMcpServer
    {
        public string Name { get; } = name;
        public string? Instructions => null;
        public string? Error => null;
        public IReadOnlyList<CxAgent.Core.Mcp.McpToolDef> Tools { get; } = tools;

        public Task<string> CallToolAsync(string tool, JsonElement args, CancellationToken ct) =>
            Task.FromResult("ok");
    }

    private static async Task<IReadOnlyList<string>> OfferedNames(Agent agent, MockLlmProvider provider)
    {
        provider.EnqueueResponse(new LlmResponse
        {
            Text = "done",
            StopReason = "end_turn",
            Usage = new LlmUsage { InputTokens = 1, OutputTokens = 1 },
        });
        await agent.SendAsync("go", CancellationToken.None);
        return [.. (provider.LastTools ?? []).Select(t => t.Name)];
    }

    [Fact]
    public async Task AWithheldBuiltinIsNotOffered()
    {
        var provider = new MockLlmProvider();
        var agent = new Agent(provider, PluginRegistry.CreateWithBuiltins(), new TokenLedger(),
            new BufferedChatSink(), new BufferedJobPanel(), logs: null, maxTurns: 5,
            toolSelection: new ToolSelection([Tool.Inherited, Tool.Not.RunShell]));

        var offered = await OfferedNames(agent, provider);

        Assert.DoesNotContain(Tool.RunShell, offered);
        Assert.Contains(Tool.ReadFile, offered);
    }

    [Fact]
    public async Task AWithheldBuiltinIsRefusedIfCalledAnyway()
    {
        // THE SECOND SITE. Fails if only the offer site was filtered — a model can emit a call for a
        // tool it was never shown, from habit or from a summary, and the dispatch guard is what
        // refuses it rather than letting an un-offered tool run.
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(LlmResponse.WithToolCall(Tool.RunShell, new { command = "echo hi" }));
        provider.EnqueueResponse(new LlmResponse
        {
            Text = "done",
            StopReason = "end_turn",
            Usage = new LlmUsage { InputTokens = 1, OutputTokens = 1 },
        });

        var agent = new Agent(provider, PluginRegistry.CreateWithBuiltins(), new TokenLedger(),
            new BufferedChatSink(), new BufferedJobPanel(), logs: null, maxTurns: 5,
            toolSelection: new ToolSelection([Tool.Inherited, Tool.Not.RunShell]));

        await agent.SendAsync("go", CancellationToken.None);

        var results = (provider.LastMessages ?? []).Where(m => m.Role == "tool").Select(m => m.Content).ToList();
        Assert.Contains(results, c => c is not null && c.Contains("not available"));
    }

    [Fact]
    public async Task AWithheldInjectedToolIsNotOffered()
    {
        var provider = new MockLlmProvider();
        var agent = new Agent(provider, PluginRegistry.CreateWithBuiltins(), new TokenLedger(),
            new BufferedChatSink(), new BufferedJobPanel(), logs: null, maxTurns: 5,
            agentTools: [new EchoTool()],
            toolSelection: new ToolSelection([Tool.Inherited, "-echo_tool"]));

        Assert.DoesNotContain("echo_tool", await OfferedNames(agent, provider));
    }

    [Fact]
    public async Task AnExactSetKeepsOnlyWhatItNames()
    {
        var provider = new MockLlmProvider();
        var agent = new Agent(provider, PluginRegistry.CreateWithBuiltins(), new TokenLedger(),
            new BufferedChatSink(), new BufferedJobPanel(), logs: null, maxTurns: 5,
            toolSelection: new ToolSelection([Tool.ReadFile, Tool.Grep]));

        var offered = await OfferedNames(agent, provider);

        Assert.Equal([Tool.ReadFile, Tool.Grep], offered.Where(n => n != Tool.TodoWrite).ToList());

        // NO TOOL IS EXEMPT: todowrite is withheld too, because the exact set did not name it.
        Assert.DoesNotContain(Tool.TodoWrite, offered);
    }

    [Fact]
    public async Task ASelectionCannotGrantASpawnToolWithNoSpawner()
    {
        // S0 IS ABSOLUTE. The agent has no spawner, so `agent` is not in the assembled list at all —
        // and a + term can only ever match something that is.
        var provider = new MockLlmProvider();
        var agent = new Agent(provider, PluginRegistry.CreateWithBuiltins(), new TokenLedger(),
            new BufferedChatSink(), new BufferedJobPanel(), logs: null, maxTurns: 5,
            toolSelection: new ToolSelection([Tool.Inherited, Tool.Also.Agent]));

        Assert.DoesNotContain(Tool.Agent, await OfferedNames(agent, provider));
    }

    [Fact]
    public async Task AnMcpToolIsOfferedEvenUnderAnExactSetThatOmitsIt()
    {
        // MCP BYPASSES SELECTION — `enabled` per server is its control. Pinned so nobody "fixes"
        // this into the filter and reintroduces the delta-timing bug: MCP names arrive after config
        // is read, so a selection that governed them would withhold late arrivals forever.
        var mcp = new CxAgent.Core.Mcp.McpToolset([new FakeServer("files",
            new CxAgent.Core.Mcp.McpToolDef("read", "Reads.",
                JsonSerializer.SerializeToElement(new { type = "object" })))]);

        var provider = new MockLlmProvider();
        var agent = new Agent(provider, PluginRegistry.CreateWithBuiltins(), new TokenLedger(),
            new BufferedChatSink(), new BufferedJobPanel(), logs: null, maxTurns: 5,
            mcp: mcp,
            toolSelection: new ToolSelection([Tool.ReadFile]));

        var offered = await OfferedNames(agent, provider);

        Assert.Contains("files_read", offered);   // survives an exact set that never named it
        Assert.Contains(Tool.ReadFile, offered);
        Assert.DoesNotContain(Tool.RunShell, offered);
    }

    [Fact]
    public async Task NoSelectionChangesNothing()
    {
        // The default must be free: every existing caller passes nothing and sees today's list.
        var provider = new MockLlmProvider();
        var agent = new Agent(provider, PluginRegistry.CreateWithBuiltins(), new TokenLedger(),
            new BufferedChatSink(), new BufferedJobPanel(), logs: null, maxTurns: 5);

        var offered = await OfferedNames(agent, provider);

        Assert.Contains(Tool.RunShell, offered);
        Assert.Contains(Tool.TodoWrite, offered);
    }
}
