using System.Text.Json;
using CxAgent.Core.Sessions;
using CxAgent.Core.Llm;
using CxAgent.Core.Mcp;
using CxAgent.Core.Models;
using CxAgent.Core.Jobs;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// THE SYSTEM PROMPT MUST NOT MOVE UNDER A RUNNING CONVERSATION.
///
/// <para>Everything before the first changed byte is served from the provider's prefix cache. Rewrite
/// the system message and the WHOLE context re-processes cold — measured on a 116-turn drive, a
/// 134-character change at turn 82 forced a full reprocess of 67,367 tokens, and on that endpoint an
/// identical prompt costs 43ms warm against 1,420ms cold.</para>
///
/// <para>A user editing AGENTS.md asked for that and can see why they paid. Nobody asks for an MCP
/// server's handshake to land on turn 82, which is exactly what used to happen.</para>
/// </summary>
public class McpPromptStabilityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cxagent-mcp-" + Guid.NewGuid().ToString("N"));
    public McpPromptStabilityTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    private sealed class FakeServer(string name, string? instructions) : IMcpServer
    {
        public string Name { get; } = name;
        public string? Instructions { get; } = instructions;
        public string? Error => null;
        public IReadOnlyList<McpToolDef> Tools { get; } =
            [new McpToolDef("read", "Reads a thing.", JsonDocument.Parse("{}").RootElement)];

        public Task<string> CallToolAsync(string tool, JsonElement args, CancellationToken ct)
            => Task.FromResult("ok");
    }

    private static LlmResponse Done(string text) =>
        new() { Text = text, ToolCalls = [], StopReason = "end_turn", Usage = new LlmUsage() };

    private static string SystemTextOf(MockLlmProvider p) =>
        p.LastMessages!.First(m => m.Role == "system").Content;

    /// <summary>
    /// A SERVER THAT CONNECTS MID-SESSION DOES NOT REWRITE THE PROMPT. This is the regression: the
    /// instructions used to be re-read every turn, so a late handshake invalidated the cache prefix
    /// for the rest of the conversation.
    /// </summary>
    [Fact]
    public async Task AServerConnectingMidSession_DoesNotChangeTheSystemPrompt()
    {
        var provider = new MockLlmProvider();
        for (var i = 0; i < 4; i++) provider.EnqueueResponse(Done("ok"));

        var mcp = new McpToolset([new FakeServer("files", instructions: null)]);
        var agent = new Agent(provider, JobRegistry.CreateWithBuiltins(), new TokenLedger(),
            new RecordingSink(), new NullJobPanel(), logs: null, maxTurns: 5,
            workingDir: _dir, mcp: mcp);

        await agent.SendAsync("first", CancellationToken.None);
        var before = SystemTextOf(provider);

        // context7 finishes its handshake and now has guidance to contribute.
        mcp.Replace([new FakeServer("files", instructions: null),
                     new FakeServer("context7", "Use this for library documentation.")]);

        await agent.SendAsync("second", CancellationToken.None);

        Assert.Equal(before, SystemTextOf(provider));
        Assert.DoesNotContain("library documentation", SystemTextOf(provider), StringComparison.Ordinal);
    }

    /// <summary>
    /// TOOL DEFINITIONS STAY LIVE. Pinning the PROSE must not pin the tools — a server that connects
    /// late still offers what it can do; it just cannot rewrite the paragraph above it.
    /// </summary>
    [Fact]
    public async Task AServerConnectingMidSession_StillOffersItsTools()
    {
        var provider = new MockLlmProvider();
        for (var i = 0; i < 4; i++) provider.EnqueueResponse(Done("ok"));

        var mcp = new McpToolset([]);
        var agent = new Agent(provider, JobRegistry.CreateWithBuiltins(), new TokenLedger(),
            new RecordingSink(), new NullJobPanel(), logs: null, maxTurns: 5,
            workingDir: _dir, mcp: mcp);

        await agent.SendAsync("first", CancellationToken.None);
        Assert.DoesNotContain(provider.LastTools ?? [], t => t.Name.Contains("context7"));

        mcp.Replace([new FakeServer("context7", "Use this for library documentation.")]);
        await agent.SendAsync("second", CancellationToken.None);

        Assert.Contains(provider.LastTools ?? [], t => t.Name.Contains("context7"));
    }

    /// <summary>
    /// GUIDANCE PRESENT AT THE START STILL REACHES THE MODEL — pinning must not mean dropping. The
    /// fix is about WHEN the value is fixed, not about removing the feature.
    /// </summary>
    [Fact]
    public async Task InstructionsPresentAtTheFirstPrompt_AreIncluded()
    {
        var provider = new MockLlmProvider();
        for (var i = 0; i < 2; i++) provider.EnqueueResponse(Done("ok"));

        var mcp = new McpToolset([new FakeServer("context7", "Use this for library documentation.")]);
        var agent = new Agent(provider, JobRegistry.CreateWithBuiltins(), new TokenLedger(),
            new RecordingSink(), new NullJobPanel(), logs: null, maxTurns: 5,
            workingDir: _dir, mcp: mcp);

        await agent.SendAsync("first", CancellationToken.None);

        Assert.Contains("library documentation", SystemTextOf(provider), StringComparison.Ordinal);
    }
}
