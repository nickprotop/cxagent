using System.Text.Json;
using CxAgent.Core.Agents;
using CxAgent.Core.Hooks;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using Xunit;

namespace CxAgent.Tests;

/// <summary>An agent hook's verifier: a read-only agent that may look around before it answers.</summary>
public class HookAgentRunnerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cxagent-hook-agent").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task The_verifier_reads_then_answers()
    {
        File.WriteAllText(Path.Combine(_dir, "result.txt"), "2 failed");
        var provider = new ScriptedProvider(
            new LlmResponse { Text = "", StopReason = "tool_use", ToolCalls = [new ToolCall { Name = "read_file", Id = "r1",
                Arguments = JsonSerializer.SerializeToElement(new { path = "result.txt" }) }] },
            new LlmResponse { Text = """{"ok": false, "reason": "2 failed"}""", StopReason = "end_turn" });
        var text = await HookAgentRunner.RunAsync(new HookAgentRequest(provider, "Did the tests pass?", _dir), CancellationToken.None);
        Assert.Contains("\"ok\": false", text);
        Assert.Contains("2 failed", provider.ResultOf(1, "r1"));
    }

    [Fact]
    public async Task The_verifier_cannot_write()
    {
        var provider = new ScriptedProvider(
            new LlmResponse { Text = "", StopReason = "tool_use", ToolCalls = [new ToolCall { Name = "write_file", Id = "w1",
                Arguments = JsonSerializer.SerializeToElement(new { path = "x.txt", content = "x" }) }] },
            new LlmResponse { Text = """{"ok": true}""", StopReason = "end_turn" });
        await HookAgentRunner.RunAsync(new HookAgentRequest(provider, "check", _dir), CancellationToken.None);
        Assert.False(File.Exists(Path.Combine(_dir, "x.txt")));
    }
}
