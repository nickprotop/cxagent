using System.Text.Json;
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

    [Theory]
    [InlineData("reasoning_content")]
    [InlineData("reasoning")]
    public void ParseStreamDelta_ReadsReasoningIntoThinking(string field)
    {
        var d = OpenAiWire.ParseStreamDelta($$$"""{"choices":[{"delta":{"{{{field}}}":"Let me see"}}]}""");
        Assert.Equal("Let me see", d.Thinking);
        Assert.Null(d.Text);
    }

    /// <summary>
    /// An EMPTY reasoning field is no thinking. "" on the stream chunk means "thinking, text hidden",
    /// which no OpenAI-compatible server does; a server that sends an empty field beside every
    /// content delta would otherwise open a hidden-thinking block on each one.
    /// </summary>
    [Theory]
    [InlineData("\"reasoning_content\":\"\"")]
    [InlineData("\"reasoning_content\":null")]
    public void ParseStreamDelta_EmptyReasoning_IsNoThinking(string field)
    {
        var d = OpenAiWire.ParseStreamDelta($$$"""{"choices":[{"delta":{"content":"hi",{{{field}}}}}]}""");
        Assert.Null(d.Thinking);
        Assert.Equal("hi", d.Text);
    }

    [Fact]
    public void ParseResponse_ReadsReasoningContent()
    {
        using var doc = JsonDocument.Parse(
            """{"choices":[{"message":{"content":"42","reasoning_content":"6*7"},"finish_reason":"stop"}]}""");
        var r = OpenAiWire.ParseResponse(doc.RootElement);
        Assert.Equal("42", r.Text);
        Assert.Equal("6*7", r.Thinking);
    }

    private static List<ChatMessage> AfterAToolResult() =>
    [
        new() { Role = "system", Content = "You are cxagent." },
        new() { Role = "user", Content = "fix the build" },
        new()
        {
            Role = "assistant", Content = "",
            ToolCalls = [new ToolCall { Id = "c1", Name = "read_file", Arguments = JsonDocument.Parse("{}").RootElement }],
        },
        new() { Role = "tool", ToolCallId = "c1", Content = "file text" },
        new() { Role = "user", Content = "# Your task list\n- [>] edit it", IsTaskList = true },
    ];

    /// <summary>
    /// A TASK LIST AFTER A TOOL RESULT RIDES THAT RESULT. Sent as a user message, a local model read
    /// each copy as the user prodding it — "The user is reminding me of my task list" opened half its
    /// turns — and narrated "let me continue" instead of working.
    /// </summary>
    [Fact]
    public void ATaskListAfterAToolResult_IsSentInsideIt()
    {
        var msgs = OpenAiWire.BuildRequestBody("m", AfterAToolResult(), null, stream: false)["messages"]!.AsArray();

        Assert.Equal(4, msgs.Count);
        var tool = msgs[^1]!;
        Assert.Equal("tool", tool["role"]!.GetValue<string>());
        Assert.Equal("file text\n\n# Your task list\n- [>] edit it", tool["content"]!.GetValue<string>());
    }

    /// <summary>
    /// ANYWHERE ELSE IT STAYS A USER MESSAGE: after the model's own reply there is no tool result to
    /// carry it, and folding it into what the user typed would put words in their mouth.
    /// </summary>
    [Fact]
    public void ATaskListAfterAReply_StaysItsOwnMessage()
    {
        List<ChatMessage> conversation =
        [
            new() { Role = "system", Content = "You are cxagent." },
            new() { Role = "user", Content = "plan it" },
            new() { Role = "assistant", Content = "planned" },
            new() { Role = "user", Content = "# Your task list", IsTaskList = true },
        ];

        var msgs = OpenAiWire.BuildRequestBody("m", conversation, null, stream: false)["messages"]!.AsArray();

        Assert.Equal(4, msgs.Count);
        Assert.Equal("user", msgs[^1]!["role"]!.GetValue<string>());
        Assert.Equal("planned", msgs[^2]!["content"]!.GetValue<string>());
    }
}
