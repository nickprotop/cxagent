using System.Text;
using System.Text.Json;
using CxAgent.Core.Agents;
using CxAgent.Core.Export;
using CxAgent.Core.Models;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The markdown an export writes: a timeline of the conversation, with as much of what the model saw as the
/// options ask for.
/// </summary>
public class ExportMarkdownTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("export-md-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static readonly DateTimeOffset T0 = new(2026, 10, 18, 10, 42, 0, TimeSpan.Zero);
    private static DateTimeOffset At(int minute) => T0.AddMinutes(minute);

    private static ExportOptions Readable => new(ExportScope.Whole, 1, ExportOptions.Readable);
    private static ExportOptions Full => new(ExportScope.Whole, 1, ExportOptions.Full);

    private static ChatMessage User(string text, int minute = 0) => new() { Role = "user", Content = text, Timestamp = At(minute) };
    private static ChatMessage Assistant(string text, int minute = 0, params ToolCall[] calls) =>
        new() { Role = "assistant", Content = text, Timestamp = At(minute), ToolCalls = calls.Length > 0 ? [.. calls] : null };
    private static ChatMessage Result(string id, string text, int minute = 0) =>
        new() { Role = "tool", Content = text, ToolCallId = id, Timestamp = At(minute) };
    private static ToolCall Call(string id, string name, object args) =>
        new() { Id = id, Name = name, Arguments = JsonSerializer.SerializeToElement(args) };

    private ExportEntry E(ChatMessage m, Incoming? from = null, IReadOnlyList<string>? thinking = null, string kind = HistoryLine.Kinds.Message) =>
        new(m.Timestamp, kind, m, thinking, from, _dir);

    private ExportSource Source(params ExportEntry[] entries) => new(
        new ExportHeader("AGENT", "Fix the login redirect", "/src/shop", At(0), At(25), 42_100, 3_200, "local:qwen"),
        entries, true, _dir);

    private static string Render(ExportSource s, ExportOptions o) => ExportMarkdown.Render(s, o).Markdown;

    [Fact]
    public void Readable_HasTheHeader_AndTheSpeakers_AndNoThinking()
    {
        var md = Render(Source(
            E(User("why does it redirect?"), Incoming.You),
            E(Assistant("because of returnUrl", 1), thinking: ["let me look"])), Readable);

        Assert.StartsWith("# Fix the login redirect\n", md);
        Assert.Contains("`/src/shop`", md);
        Assert.Contains("42.1k in / 3.2k out", md);
        Assert.Contains("## You · ", md);
        Assert.Contains("## cxagent · ", md);
        Assert.Contains("because of returnUrl", md);
        Assert.DoesNotContain("let me look", md);
    }

    [Fact]
    public void ToolCalls_AreOneLineEach_WithAFailureSaid()
    {
        var md = Render(Source(
            E(User("go"), Incoming.You),
            E(Assistant("", 1,
                Call("1", "read_file", new { path = "/src/shop/src/a.cs" }),
                Call("2", "run_shell", new { command = "dotnet test" }),
                Call("3", "run_shell", new { command = "ls" }),
                Call("4", "write_file", new { path = "/etc/x", content = "x" }))),
            E(Result("1", "class A {}", 1)),
            E(Result("2", "error: command exited with code 1\nexit_code: 1\nstdout: fail", 1)),
            E(Result("3", "a\nexit_code: 0", 1)),
            E(Result("4", "error: denied by the user", 1))), Readable);

        Assert.Contains("> `read_file src/a.cs`", md);
        Assert.Contains("> `run_shell dotnet test` → exit 1", md);
        Assert.Contains("> `run_shell ls`\n", md);
        Assert.Contains("→ error", md);
        Assert.DoesNotContain("class A {}", md);
    }

    [Fact]
    public void AReplacement_IsADiff()
    {
        var md = Render(Source(
            E(Assistant("", 1, Call("1", "replace_in_file", new
            {
                path = "src/Auth/LoginController.cs",
                pattern = "        return Redirect(\"/\");",
                replacement = "        return LocalRedirect(returnUrl ?? \"/\");",
            }))),
            E(Result("1", "replaced 1 occurrence in src/Auth/LoginController.cs", 1))), Readable);

        Assert.Contains("```diff\n", md);
        Assert.Contains("-        return Redirect(\"/\");", md);
        Assert.Contains("+        return LocalRedirect(returnUrl ?? \"/\");", md);
    }

    [Fact]
    public void Full_AddsResults_Thinking_TheSystemPrompt_AndInjectedText()
    {
        var md = Render(Source(
            E(new ChatMessage { Role = "system", Content = "You are cxagent.", Timestamp = At(0) }),
            E(User("go"), Incoming.You),
            E(Assistant("", 1, Call("1", "read_file", new { path = "src/a.cs" })), thinking: ["read it first"]),
            E(Result("1", "class A {}", 1)),
            E(new ChatMessage { Role = "user", Content = "- [ ] step", IsTaskList = true, Timestamp = At(1) })), Full);

        Assert.Contains("## System prompt", md);
        Assert.Contains("You are cxagent.", md);
        Assert.Contains("```cs\nclass A {}\n```", md);
        Assert.Contains("<summary>Thinking</summary>", md);
        Assert.Contains("read it first", md);
        Assert.Contains("task list (injected)", md);
    }

    [Fact]
    public void Readable_LeavesOutTheSystemPrompt_AndTheTaskList()
    {
        var md = Render(Source(
            E(new ChatMessage { Role = "system", Content = "You are cxagent.", Timestamp = At(0) }),
            E(User("go"), Incoming.You),
            E(new ChatMessage { Role = "user", Content = "- [ ] step", IsTaskList = true, Timestamp = At(1) })), Readable);

        Assert.DoesNotContain("You are cxagent.", md);
        Assert.DoesNotContain("- [ ] step", md);
    }

    [Fact]
    public void Compaction_IsAlwaysMarked_ItsSummaryOnlyWhenAsked()
    {
        var summary = Assistant(CxAgent.Core.Sessions.SessionCompressor.SummaryMarker + "they fixed the redirect]", 5);
        ExportSource S() => Source(
            E(User("go"), Incoming.You),
            new ExportEntry(At(5), HistoryLine.Kinds.Compacted, null, null, null, _dir),
            E(summary),
            E(new ChatMessage { Role = "user", Content = "", Timestamp = At(6) }, kind: HistoryLine.Kinds.Cleared));

        var readable = Render(S(), Readable);
        var full = Render(S(), Full);

        Assert.Contains("Context compacted here", readable);
        Assert.DoesNotContain("they fixed the redirect", readable);
        Assert.Contains("they fixed the redirect", full);
        Assert.Contains("Conversation cleared", readable);
    }

    /// <summary>
    /// THE EXPORT IS A TIMELINE (decision 15): each of a sub-agent's stretches sits where it began — the brief after
    /// the spawn, a later message after the send, a follow-up typed in its tab at the time it was typed.
    /// </summary>
    [Fact]
    public void ASubAgentsStretches_SitWhereTheyHappened()
    {
        var kid = new HistoryRecorder(Path.Combine(_dir, "KID", "history.jsonl"));
        kid.NoteIncoming(Incoming.Brief);
        kid.Record([User("find the auth flow", 1), Assistant("it is in LoginController", 2)]);
        kid.NoteIncoming(Incoming.Parent);
        kid.Record([User("find the auth flow", 1), Assistant("it is in LoginController", 2),
            User("and the logout?", 16), Assistant("LogoutController", 17)]);
        kid.NoteIncoming(Incoming.You);
        kid.Record([User("find the auth flow", 1), Assistant("it is in LoginController", 2),
            User("and the logout?", 16), Assistant("LogoutController", 17), User("thanks, and tests?", 20), Assistant("none", 21)]);

        const string envelope = "<sub_agent id=\"KID\" name=\"explore-auth-flow\" state=\"completed\">\nit is in LoginController\n</sub_agent>";
        var md = Render(Source(
            E(User("go"), Incoming.You),
            E(Assistant("", 0, Call("s", "agent", new { description = "explore auth flow", prompt = "find the auth flow", type = "explore" }))),
            E(Result("s", envelope, 3)),
            E(Assistant("found it", 4)),
            E(User("ask it more", 15), Incoming.You),
            E(Assistant("", 15, Call("a", "agent_send", new { to = "explore-auth-flow", prompt = "and the logout?" }))),
            E(Result("a", envelope.Replace("LoginController", "LogoutController"), 18)),
            E(Assistant("logout too", 19)),
            E(User("done?", 22), Incoming.You)), Readable);

        int Pos(string s) => md.IndexOf(s, StringComparison.Ordinal);
        Assert.True(Pos("· brief ·") > Pos("`agent`"), "the brief's stretch comes after its spawn");
        Assert.True(Pos("· brief ·") < Pos("found it"));
        Assert.True(Pos("> `agent_send` explore-auth-flow — and the logout?") >= 0, "the send has its own line");
        Assert.True(Pos("· stretch 2 ·") > Pos("`agent_send`") && Pos("· stretch 2 ·") < Pos("logout too"));
        Assert.True(Pos("· from you ·") > Pos("logout too") && Pos("· from you ·") < Pos("done?"));
        Assert.Contains("<b>explore-auth-flow</b> (explore)", md);
        Assert.Contains("LogoutController", md);
        Assert.Equal(1, ExportMarkdown.Render(Source(
            E(Assistant("", 0, Call("s", "agent", new { description = "d", prompt = "p" }))),
            E(Result("s", envelope, 3))), Readable).Stats.SubAgents);
    }

    [Fact]
    public void WithoutSubAgents_OnlyTheReportIsShown()
    {
        new HistoryRecorder(Path.Combine(_dir, "KID", "history.jsonl")).Record([User("brief", 1), Assistant("inner work", 2)]);
        var md = Render(Source(
            E(Assistant("", 0, Call("s", "agent", new { description = "d", prompt = "p" }))),
            E(Result("s", "<sub_agent id=\"KID\" name=\"kid\" state=\"completed\">\nthe report\n</sub_agent>", 3))),
            Readable with { Parts = ExportOptions.Readable & ~ExportParts.SubAgents });

        Assert.Contains("the report", md);
        Assert.DoesNotContain("inner work", md);
    }

    [Fact]
    public void AChildWithNoHistory_ShowsItsReport_AndSaysSo()
    {
        var md = Render(Source(
            E(Assistant("", 0, Call("s", "agent", new { description = "d", prompt = "p" }))),
            E(Result("s", "<sub_agent id=\"GONE\" name=\"kid\" state=\"completed\">\nthe report\n</sub_agent>", 3))), Readable);

        Assert.Contains("the report", md);
        Assert.Contains("conversation not recorded", md);
    }

    [Fact]
    public void Scope_LastTurns_StartAtTheUsersMessages()
    {
        var s = Source(
            E(User("first", 0), Incoming.You), E(Assistant("a1", 1)),
            E(User("report arrived", 2), Incoming.Delivery), E(Assistant("a2", 3)),
            E(User("second", 4), Incoming.You), E(Assistant("a3", 5)),
            E(User("third", 6), Incoming.You), E(Assistant("a4", 7)));

        var last = Render(s, Readable with { Scope = ExportScope.LastTurn });
        Assert.Contains("third", last);
        Assert.DoesNotContain("second", last);

        var two = Render(s, Readable with { Scope = ExportScope.LastTurns, Turns = 2 });
        Assert.Contains("second", two);
        Assert.DoesNotContain("first", two);

        var many = Render(s, Readable with { Scope = ExportScope.LastTurns, Turns = 9 });
        Assert.Contains("first", many);
    }

    /// <summary>AN AGENT'S OWN EXPORT COUNTS STRETCHES: anything it received starts one.</summary>
    [Fact]
    public void AnAgentsLastTurn_IsItsLastStretch()
    {
        var s = new ExportSource(new ExportHeader("KID", "Parent session", null, At(0), At(9), 0, 0, null, "explore-auth", "explore"),
            [E(User("brief", 0), Incoming.Brief), E(Assistant("r1", 1)), E(User("more", 5), Incoming.Parent), E(Assistant("r2", 6))],
            true, _dir);

        var md = Render(s, Readable with { Scope = ExportScope.LastTurn });

        Assert.StartsWith("# explore-auth (explore)", md);
        Assert.Contains("sub-agent of “Parent session”", md);
        Assert.Contains("r2", md);
        Assert.DoesNotContain("r1", md);
        Assert.Contains("## From cxagent · ", md);
    }

    /// <summary>
    /// A NUDGE IS cxagent TALKING TO THE MODEL, NOT YOU: it starts no turn, Readable leaves it out, and Full shows it as
    /// injected text.
    /// </summary>
    [Fact]
    public void Nudges_AreInjectedText_NotTheUser()
    {
        var s = Source(
            E(User("the real question", 0), Incoming.You), E(Assistant("partial", 1)),
            E(User("Your reply was cut off at the output limit.", 2), Incoming.Nudge), E(Assistant("the rest", 3)));

        var last = Render(s, Readable with { Scope = ExportScope.LastTurn });
        Assert.Contains("the real question", last);
        Assert.DoesNotContain("cut off", last);

        var full = Render(s, Full);
        Assert.Contains("nudge (injected)", full);
        Assert.Contains("cut off", full);
    }

    /// <summary>
    /// A CHILD SPAWNED BEFORE A RE-WIRE lives under the older folder; a later send's result, written in the newer one,
    /// must not call its conversation unrecorded when its stretches are on the page.
    /// </summary>
    [Fact]
    public void AChildFoundOnce_IsNotCalledUnrecordedElsewhere()
    {
        var older = Path.Combine(_dir, "OLDAGENT");
        new HistoryRecorder(Path.Combine(older, "KID", "history.jsonl")).Record([User("brief", 1), Assistant("work", 2)]);
        const string envelope = "<sub_agent id=\"KID\" name=\"kid\" state=\"completed\">\nreport\n</sub_agent>";
        var md = Render(Source(
            new ExportEntry(At(0), HistoryLine.Kinds.Message, Assistant("", 0, Call("s", "agent", new { description = "d", prompt = "p" })), null, null, older),
            new ExportEntry(At(3), HistoryLine.Kinds.Message, Result("s", envelope, 3), null, null, older),
            E(Assistant("", 5, Call("a", "agent_send", new { to = "kid", prompt = "more" }))),
            E(Result("a", envelope, 6))), Readable);

        Assert.DoesNotContain("conversation not recorded", md);
    }

    /// <summary>A CHILD IS READ ONCE PER EXPORT, not once per option the dialog's preview renders.</summary>
    [Fact]
    public void AChildsHistory_IsReadOnce_AcrossRenders()
    {
        var path = Path.Combine(_dir, "KID", "history.jsonl");
        new HistoryRecorder(path).Record([User("brief", 1), Assistant("first version", 2)]);
        var s = Source(E(Assistant("", 0, Call("s", "agent", new { description = "d", prompt = "p" }))),
            E(Result("s", "<sub_agent id=\"KID\" name=\"kid\" state=\"completed\">\nr\n</sub_agent>", 3)));

        Assert.Contains("first version", Render(s, Readable));
        File.Delete(path);
        Assert.Contains("first version", Render(s, Full));
    }

    /// <summary>AN AGENT'S OWN EXPORT SAYS HOW IT STANDS AND WHAT IT COST, as a session's header does.</summary>
    [Fact]
    public void AnAgentsHeader_HasItsStateAndSpend()
    {
        var s = new ExportSource(new ExportHeader("KID", "Parent", null, At(0), At(9), 1_500, 300, null, "kid", "explore", "done"),
            [E(User("brief", 0), Incoming.Brief), E(Assistant("r", 1))], true, _dir);

        var md = Render(s, Readable);

        Assert.Contains("done", md.Split('\n')[1]);
        Assert.Contains("1.5k in / 300 out", md);
    }

    [Fact]
    public void Stats_CountWhatWasWritten()
    {
        var result = ExportMarkdown.Render(Source(E(User("q"), Incoming.You), E(Assistant("a", 1))), Readable);

        Assert.Equal(Encoding.UTF8.GetByteCount(result.Markdown), result.Stats.Bytes);
        Assert.Equal(2, result.Stats.Messages);
    }

    [Fact]
    public void Presets_HaveNames_AndAnythingElseIsCustom()
    {
        Assert.Equal("Readable", new ExportOptions(ExportScope.Whole, 1, ExportOptions.Readable).PresetName);
        Assert.Equal("Full", new ExportOptions(ExportScope.Whole, 1, ExportOptions.Full).PresetName);
        Assert.Equal("Custom", new ExportOptions(ExportScope.Whole, 1, ExportOptions.Readable | ExportParts.Thinking).PresetName);
    }
}

public class EditDiffTests
{
    private static ToolCall Call(string name, object args) =>
        new() { Id = "1", Name = name, Arguments = JsonSerializer.SerializeToElement(args) };

    [Fact]
    public void AWrite_IsAdditions_CappedWithACount()
    {
        var content = string.Join("\n", Enumerable.Range(1, 100).Select(i => $"line {i}"));
        var diff = EditDiff.For(Call("write_file", new { path = "a.txt", content }))!;

        Assert.Equal(60, diff.Split('\n').Count(l => l.StartsWith("+line")));
        Assert.Contains("… 40 more lines", diff);
        Assert.Contains("+++ a.txt", diff);
    }

    [Fact]
    public void OtherTools_HaveNoDiff() => Assert.Null(EditDiff.For(Call("read_file", new { path = "a" })));
}
