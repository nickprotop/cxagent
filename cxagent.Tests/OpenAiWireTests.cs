using System.Text.Json.Nodes;
using CxAgent.Core.Llm.Providers;
using CxAgent.Core.Models;
using Xunit;

namespace CxAgent.Tests;

public class OpenAiWireTests
{
    private static List<ChatMessage> Conversation() =>
    [
        new() { Role = "system", Content = "You are cxagent." },
        new() { Role = "user", Content = "hello" },
    ];

    /// <summary>
    /// OFF BY DEFAULT, AND THE BODY IS UNCHANGED. This is the regression that matters: every
    /// existing user, and every local endpoint, must send exactly what they sent before.
    /// </summary>
    [Fact]
    public void WithoutCacheControl_TheSystemMessageIsAPlainString()
    {
        var body = OpenAiWire.BuildRequestBody("m", Conversation(), null, stream: false);

        var system = body["messages"]!.AsArray()[0]!;
        Assert.Equal("You are cxagent.", system["content"]!.GetValue<string>());
    }

    /// <summary>
    /// WITH IT, the system message becomes a content array carrying the breakpoint. Anthropic and
    /// Google cache NOTHING without this — measured through OpenRouter, the same 7,002-token prefix
    /// gave 0 cached without it and 7,002 with it.
    /// </summary>
    [Fact]
    public void WithCacheControl_TheSystemMessageCarriesABreakpoint()
    {
        var body = OpenAiWire.BuildRequestBody("m", Conversation(), null, stream: false,
            cacheSystemPrompt: true);

        var parts = body["messages"]!.AsArray()[0]!["content"]!.AsArray();
        var part = parts[0]!;

        Assert.Equal("text", part["type"]!.GetValue<string>());
        Assert.Equal("You are cxagent.", part["text"]!.GetValue<string>());
        Assert.Equal("ephemeral", part["cache_control"]!["type"]!.GetValue<string>());
    }

    /// <summary>
    /// ONLY THE SYSTEM MESSAGE. A breakpoint on a user turn would pay to store a prefix that changes
    /// every turn — the write cost with none of the reuse.
    /// </summary>
    [Fact]
    public void WithCacheControl_UserMessagesKeepPlainContent()
    {
        var body = OpenAiWire.BuildRequestBody("m", Conversation(), null, stream: false,
            cacheSystemPrompt: true);

        var user = body["messages"]!.AsArray()[1]!;
        Assert.Equal("hello", user["content"]!.GetValue<string>());
    }

    /// <summary>
    /// ONLY THE FIRST SYSTEM MESSAGE. A GUARD, not a live case: Agent.cs uses
    /// FirstOrDefault(m => m.Role == "system") and replaces in place, so a conversation carries
    /// exactly one. This pins the behaviour against a future change that stacks two — where a second
    /// breakpoint would quietly double the write cost.
    /// </summary>
    [Fact]
    public void WithCacheControl_OnlyTheFirstSystemMessageIsMarked()
    {
        List<ChatMessage> two =
        [
            new() { Role = "system", Content = "first" },
            new() { Role = "system", Content = "second" },
        ];

        var body = OpenAiWire.BuildRequestBody("m", two, null, stream: false, cacheSystemPrompt: true);
        var msgs = body["messages"]!.AsArray();

        Assert.NotNull(msgs[0]!["content"]!.AsArray());
        Assert.Equal("second", msgs[1]!["content"]!.GetValue<string>());
    }
}
