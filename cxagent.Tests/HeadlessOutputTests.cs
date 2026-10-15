using System.Text.Json;
using CxAgent.Core.Commands;
using CxAgent.Core.Models;
using CxAgent.Core.Permissions;
using CxAgent.Core.Sessions;
using CxAgent.Headless;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

public class HeadlessOutputTests
{
    private static RunResult Result(HeadlessEnd end, string? text) => new(end, text,
        end == HeadlessEnd.Answered ? [] : ["it broke"], "SESSION1", 2, 1500,
        new RunSpend(100, 20, 80, null, "local:m"), []);

    private sealed record Made(HeadlessOutput Output, StringWriter Out, StringWriter Err);

    private static Made Make(OutputFormat format, bool verbose = false, bool partial = false)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var options = new HeadlessOptions { Output = format, Verbose = verbose, IncludePartialMessages = partial };
        return new Made(HeadlessOutput.For(options, new HeadlessIo(o, e)), o, e);
    }

    [Fact]
    public void Text_PrintsOnlyTheAnswer()
    {
        var (output, o, e) = Make(OutputFormat.Text);
        output.AssistantTurnBegan(new ChatMessageId(1));
        output.AssistantTextAppended(new ChatMessageId(1), "the ");
        output.AssistantTextAppended(new ChatMessageId(1), "answer");
        output.AssistantTurnEnded(new ChatMessageId(1));
        output.Finish(Result(HeadlessEnd.Answered, "the answer"));

        Assert.Equal("the answer" + Environment.NewLine, o.ToString());
        Assert.Equal("", e.ToString());
    }

    [Fact]
    public void Text_Verbose_PutsProgressOnStderr()
    {
        var (output, o, e) = Make(OutputFormat.Text, verbose: true);
        output.Said(new Message("a notice", Severity.Warning));
        output.Finish(Result(HeadlessEnd.Answered, "x"));

        Assert.Contains("a notice", e.ToString());
        Assert.DoesNotContain("a notice", o.ToString());
    }

    /// <summary>A warning — the folder is not trusted, a server needs a login — is not progress: it
    /// explains the run, so it reaches stderr without --verbose.</summary>
    [Fact]
    public void Text_Warnings_GoToStderr_EvenWithoutVerbose()
    {
        var (output, o, e) = Make(OutputFormat.Text);
        output.Said(new Message("this folder is not trusted", Severity.Warning));
        output.Said(new Message("an info line", Severity.Info));

        Assert.Contains("this folder is not trusted", e.ToString());
        Assert.DoesNotContain("an info line", e.ToString());
        Assert.Equal("", o.ToString());
    }

    /// <summary>The job's display name already starts with the tool; the row names it once.</summary>
    [Fact]
    public void Text_Verbose_ToolRows_NameTheToolOnce()
    {
        var (output, _, e) = Make(OutputFormat.Text, verbose: true);
        output.ToolUpdated(new Job
        {
            Id = "J1", AgentId = "A", JobType = "file", DisplayName = "read_file · README.md",
            PlanLocalId = "read_file", State = JobState.Succeeded, CreatedAt = DateTimeOffset.UtcNow,
        });

        Assert.Contains("read_file · README.md · succeeded", e.ToString());
        Assert.DoesNotContain("read_file · read_file", e.ToString());
    }

    [Fact]
    public void Text_Errors_GoToStderr_EvenWithoutVerbose()
    {
        var (output, _, e) = Make(OutputFormat.Text);
        output.Finish(Result(HeadlessEnd.Failed, null));

        Assert.Contains("it broke", e.ToString());
    }

    [Fact]
    public void Json_IsOneResultObject_InClaudeCodesShape()
    {
        var (output, o, _) = Make(OutputFormat.Json);
        output.Denied(new PermissionDecisionReport(PermissionKind.Shell, "denied", null, "git push"));
        output.Finish(Result(HeadlessEnd.Answered, "the answer"));

        using var doc = JsonDocument.Parse(o.ToString());
        var r = doc.RootElement;
        Assert.Equal("result", r.GetProperty("type").GetString());
        Assert.Equal("success", r.GetProperty("subtype").GetString());
        Assert.False(r.GetProperty("is_error").GetBoolean());
        Assert.Equal("the answer", r.GetProperty("result").GetString());
        Assert.Equal("SESSION1", r.GetProperty("session_id").GetString());
        Assert.Equal(2, r.GetProperty("num_turns").GetInt32());
        Assert.Equal(100, r.GetProperty("usage").GetProperty("input_tokens").GetInt32());
        Assert.Equal(80, r.GetProperty("usage").GetProperty("cache_read_input_tokens").GetInt32());
        Assert.Equal("run_shell", r.GetProperty("permission_denials")[0].GetProperty("tool_name").GetString());
        Assert.Equal(0, r.GetProperty("cxagent").GetProperty("exitCode").GetInt32());
    }

    /// <summary>The spec's `cxagent` block: the run's own facts, beside Claude Code's fields.</summary>
    [Fact]
    public void Json_TheCxagentBlock_CarriesTrustModeSkippedAndWarnings()
    {
        var (output, o, _) = Make(OutputFormat.Json);
        output.Start(new RunStart("SESSION1", "/w", "local:m", "bypassPermissions", [], [], [],
            new RunSkipped(["csharp-lsp (not approved)"], ["github (needs login)"]), true));
        output.Said(new Message("a server needs a login", Severity.Warning));
        output.Finish(Result(HeadlessEnd.Answered, "x"));

        using var doc = JsonDocument.Parse(o.ToString());
        var c = doc.RootElement.GetProperty("cxagent");
        Assert.True(c.GetProperty("trusted").GetBoolean());
        Assert.Equal("bypassPermissions", c.GetProperty("permissionMode").GetString());
        Assert.Equal("csharp-lsp (not approved)", c.GetProperty("skipped").GetProperty("plugins")[0].GetString());
        Assert.Equal("github (needs login)", c.GetProperty("skipped").GetProperty("mcp")[0].GetString());
        Assert.Equal("a server needs a login", c.GetProperty("warnings")[0].GetString());
    }

    [Fact]
    public void Json_AnError_HasErrors_AndNoResult()
    {
        var (output, o, _) = Make(OutputFormat.Json);
        output.Finish(Result(HeadlessEnd.TurnLimit, null));

        using var doc = JsonDocument.Parse(o.ToString());
        Assert.Equal("error_max_turns", doc.RootElement.GetProperty("subtype").GetString());
        Assert.True(doc.RootElement.GetProperty("is_error").GetBoolean());
        Assert.False(doc.RootElement.TryGetProperty("result", out _));
        Assert.Equal(1, doc.RootElement.GetProperty("errors").GetArrayLength());
    }

    [Fact]
    public void StreamJson_IsOneObjectPerLine_InitFirst_ResultLast()
    {
        var (output, o, _) = Make(OutputFormat.StreamJson);
        output.Start(new RunStart("SESSION1", "/w", "local:m", "default", ["read_file"], [], [], RunSkipped.None, true));
        output.AssistantTurnBegan(new ChatMessageId(1));
        output.AssistantTextAppended(new ChatMessageId(1), "hi");
        output.AssistantTurnEnded(new ChatMessageId(1));
        output.Finish(Result(HeadlessEnd.Answered, "hi"));

        var lines = o.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement).ToList();
        Assert.Equal("system", lines[0].GetProperty("type").GetString());
        Assert.Equal("init", lines[0].GetProperty("subtype").GetString());
        Assert.Contains(lines, l => l.GetProperty("type").GetString() == "assistant");
        Assert.Equal("result", lines[^1].GetProperty("type").GetString());
    }

    private static readonly RunStart Started =
        new("SESSION1", "/w", "local:m", "default", ["read_file"], [], [], RunSkipped.None, true);

    private static List<JsonElement> LinesOf(StringWriter o) => o.ToString()
        .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l).RootElement).ToList();

    [Fact]
    public void StreamJson_ThinkingBlock_IsAnAssistantThinkingContent_BeforeTheText()
    {
        var (output, o, _) = Make(OutputFormat.StreamJson);
        output.Start(Started);
        var id = new ChatMessageId(1);
        output.AssistantTurnBegan(id);
        output.AssistantThinkingAppended(id, "check prices.py");
        output.AssistantThinkingEnded(id, new(TimeSpan.FromSeconds(2), 2, false, false));
        output.AssistantTextAppended(id, "done");
        output.AssistantTurnEnded(id);
        var lines = LinesOf(o);
        var thinking = lines.Single(l => l.GetProperty("type").GetString() == "assistant"
            && l.GetProperty("message").GetProperty("content")[0].GetProperty("type").GetString() == "thinking");
        Assert.Equal("check prices.py", thinking.GetProperty("message").GetProperty("content")[0].GetProperty("thinking").GetString());
        Assert.True(lines.IndexOf(thinking) < lines.FindIndex(l => l.ToString().Contains("\"done\"")));
    }

    [Fact]
    public void StreamJson_Partial_EmitsThinkingDeltas()
    {
        var (output, o, _) = Make(OutputFormat.StreamJson, partial: true);
        output.Start(Started);
        var id = new ChatMessageId(1);
        output.AssistantTurnBegan(id);
        output.AssistantThinkingAppended(id, "x");

        Assert.Contains("thinking_delta", o.ToString());
    }

    [Fact]
    public void StreamJson_HiddenThinking_HasEmptyText()
    {
        var (output, o, _) = Make(OutputFormat.StreamJson);
        output.Start(Started);
        var id = new ChatMessageId(1);
        output.AssistantTurnBegan(id);
        output.AssistantThinkingEnded(id, new(TimeSpan.FromSeconds(3), 0, true, false));

        var thinking = LinesOf(o).Single(l => l.GetProperty("type").GetString() == "assistant");
        Assert.Equal("", thinking.GetProperty("message").GetProperty("content")[0].GetProperty("thinking").GetString());
    }

    [Fact]
    public void Text_Verbose_PutsAThinkingLineOnStderr_NotStdout()
    {
        var (output, o, e) = Make(OutputFormat.Text, verbose: true);
        var id = new ChatMessageId(1);
        output.AssistantTurnBegan(id);
        output.AssistantThinkingAppended(id, "first line\nmore");
        output.AssistantThinkingEnded(id, new(TimeSpan.FromSeconds(4), 3, false, false));

        Assert.Contains("◆ thinking (4s): first line…", e.ToString());
        Assert.DoesNotContain("first line", o.ToString());
    }

    [Fact]
    public void Text_Verbose_HiddenThinking_SaysThought()
    {
        var (output, _, e) = Make(OutputFormat.Text, verbose: true);
        var id = new ChatMessageId(1);
        output.AssistantTurnBegan(id);
        output.AssistantThinkingEnded(id, new(TimeSpan.FromSeconds(0.2), 0, true, false));

        Assert.Contains("◆ thought (1s)", e.ToString());
    }

    [Fact]
    public void Text_NotVerbose_SaysNothingAboutThinking()
    {
        var (output, _, e) = Make(OutputFormat.Text);
        var id = new ChatMessageId(1);
        output.AssistantTurnBegan(id);
        output.AssistantThinkingAppended(id, "private");
        output.AssistantThinkingEnded(id, new(TimeSpan.FromSeconds(4), 1, false, false));

        Assert.DoesNotContain("thinking", e.ToString());
        Assert.DoesNotContain("thought", e.ToString());
    }

    [Fact]
    public void StreamJson_ToolCalls_AreToolUse_ThenToolResult()
    {
        var (output, o, _) = Make(OutputFormat.StreamJson);
        var job = new Job
        {
            Id = "J1", AgentId = "A", JobType = "file", DisplayName = "read README.md",
            PlanLocalId = "read_file", State = JobState.Running, CreatedAt = DateTimeOffset.UtcNow,
        };
        output.ToolsChanged([job]);
        output.ToolUpdated(job with
        {
            State = JobState.Succeeded,
            Result = new JobResult { Success = true, Output = new() { ["content"] = "hello" } },
        });

        var lines = o.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement).ToList();
        var use = lines.Single(l => l.GetProperty("type").GetString() == "assistant")
            .GetProperty("message").GetProperty("content")[0];
        Assert.Equal("tool_use", use.GetProperty("type").GetString());
        Assert.Equal("read_file", use.GetProperty("name").GetString());
        var result = lines.Single(l => l.GetProperty("type").GetString() == "user")
            .GetProperty("message").GetProperty("content")[0];
        Assert.Equal("J1", result.GetProperty("tool_use_id").GetString());
        Assert.Equal("hello", result.GetProperty("content").GetString());
    }

    [Fact]
    public void StreamJson_PartialMessages_EmitTextDeltas_OnlyWhenAsked()
    {
        var (without, o1, _) = Make(OutputFormat.StreamJson);
        without.AssistantTurnBegan(new ChatMessageId(1));
        without.AssistantTextAppended(new ChatMessageId(1), "hi");
        Assert.DoesNotContain("stream_event", o1.ToString());

        var (with, o2, _) = Make(OutputFormat.StreamJson, partial: true);
        with.AssistantTurnBegan(new ChatMessageId(1));
        with.AssistantTextAppended(new ChatMessageId(1), "hi");
        Assert.Contains("text_delta", o2.ToString());
    }

    [Fact]
    public void TheLastErrorSaid_IsKept_ForTheResult()
    {
        var (output, _, _) = Make(OutputFormat.Json);
        output.Said(new Message("connection refused", Severity.Error));

        Assert.Equal("connection refused", output.LastError);
    }

    /// <summary>A reader that hung up must not fail the run: writing stops, the run goes on.</summary>
    [Fact]
    public void AClosedStdout_StopsWritingQuietly()
    {
        var output = HeadlessOutput.For(new HeadlessOptions { Output = OutputFormat.StreamJson },
            new HeadlessIo(new ClosedWriter(), new StringWriter()));

        output.Start(new RunStart("S", "/w", "m", "default", [], [], [], RunSkipped.None, true));
        output.Finish(Result(HeadlessEnd.Answered, "x"));

        Assert.True(output.OutputClosed);
    }

    private sealed class ClosedWriter : StringWriter
    {
        public override void Write(char value) => throw new IOException("Broken pipe");
        public override void Write(string? value) => throw new IOException("Broken pipe");
        public override void WriteLine(string? value) => throw new IOException("Broken pipe");
    }
}
