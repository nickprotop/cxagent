using System.Text.Json;
using System.Text.Json.Nodes;
using CxAgent.Core.Models;
using CxAgent.Core.Storage;
using CxAgent.Web;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// A saved session's past, shown again as the events a live one raises — what both front ends draw when a session is
/// restored.
/// </summary>
public sealed class SessionReplayTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("replay-").FullName;
    private readonly AppPaths _paths;

    public SessionReplayTests() => _paths = new AppPaths(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private HistoryRecorder History(string agentId, string? under = null)
    {
        var dir = under is null ? new SessionFolder(_paths, agentId).Dir : Path.Combine(new SessionFolder(_paths, under).Dir, agentId);
        Directory.CreateDirectory(dir);
        return new HistoryRecorder(Path.Combine(dir, "history.jsonl"));
    }

    private static ChatMessage User(string text) => new() { Role = "user", Content = text };
    private static ChatMessage Said(string text) => new() { Role = "assistant", Content = text };

    private static ChatMessage Calls(string id, string name, string args) => new()
    {
        Role = "assistant",
        Content = "",
        ToolCalls = [new ToolCall { Id = id, Name = name, Arguments = JsonDocument.Parse(args).RootElement.Clone() }],
    };

    private static ChatMessage Result(string id, string text) => new() { Role = "tool", ToolCallId = id, Content = text };

    private static string Kind(JsonObject e) => e["kind"]!.GetValue<string>();

    private (SessionEventLog Log, ReplayShown Shown) Play(string agentId, int maxTurns = SessionReplay.DefaultTurns)
    {
        var log = new SessionEventLog("S");
        var shown = SessionReplay.Play(_paths, agentId, log, log, maxTurns);
        return (log, shown);
    }

    [Fact]
    public void A_replay_shows_the_turns_the_answers_and_each_tool_call_with_its_result()
    {
        History("A1").Record([new ChatMessage { Role = "system", Content = "sys" }, User("list"),
            Calls("c1", "run_shell", """{"command":"ls"}"""), Result("c1", "a.txt"), Said("one file")]);

        var (log, shown) = Play("A1");
        var events = log.Since(0).Events;

        Assert.Equal(["user", "turnBegan", "turnEnded", "toolStarted", "toolEnded", "turnBegan", "text", "turnEnded"],
            events.Select(Kind));
        Assert.Equal("list", events[0]["text"]!.GetValue<string>());
        Assert.Equal("shell", events[3]["jobType"]!.GetValue<string>());
        Assert.Contains("a.txt", events[4]["output"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("one file", events[6]["text"]!.GetValue<string>());
        Assert.Equal(1, shown.Turns);
    }

    [Fact]
    public void A_replay_follows_every_resume_back_to_the_start()
    {
        // A RESUME MINTS A NEW AGENT, whose folder starts with a line naming the one it continues; replaying only the
        // newest folder showed the past as beginning at the last resume.
        var first = new List<ChatMessage> { User("first"), Said("one") };
        History("A1").Record(first);
        var second = History("A2");
        second.ContinuesFrom("A1", first);
        second.Record([.. first, User("second"), Said("two")]);

        var (log, _) = Play("A2");

        Assert.Equal(["first", "second"], log.Since(0).Events.Where(e => Kind(e) == "user").Select(e => e["text"]!.GetValue<string>()));
        Assert.Equal(["one", "two"], log.Since(0).Events.Where(e => Kind(e) == "text").Select(e => e["text"]!.GetValue<string>()));
    }

    [Fact]
    public void The_cap_keeps_the_newest_turns_and_says_how_many_it_left_out()
    {
        History("A1").Record([.. Enumerable.Range(1, 5).SelectMany(i => new[] { User($"u{i}"), Said($"a{i}") })]);

        var (log, shown) = Play("A1", maxTurns: 2);
        var events = log.Since(0).Events;

        Assert.Equal("said", Kind(events[0]));
        Assert.StartsWith("3 earlier turns not shown", events[0]["text"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(["u4", "u5"], events.Where(e => Kind(e) == "user").Select(e => e["text"]!.GetValue<string>()));
        Assert.Equal((2, 3), (shown.Turns, shown.Hidden));
    }

    [Fact]
    public void A_task_list_and_a_tool_result_are_not_turns_of_the_user()
    {
        History("A1").Record([User("go"), new ChatMessage { Role = "user", Content = "[tasks]", IsTaskList = true }, Said("ok")]);

        var (log, shown) = Play("A1");

        Assert.Single(log.Since(0).Events, e => Kind(e) == "user");
        Assert.Equal(1, shown.Turns);
    }

    [Fact]
    public void A_spawned_worker_comes_back_with_its_name_type_task_and_folder()
    {
        History("A1").Record([User("count"),
            Calls("c1", "agent", """{"type":"general","prompt":"count the files\nthen report"}"""),
            Result("c1", """<agent id="CHILD" name="counter" state="completed">2</agent>"""), Said("2 files")]);
        History("CHILD", under: "A1").Record([User("count the files")]);

        var (log, shown) = Play("A1");

        var worker = Assert.Single(shown.Workers);
        Assert.Equal(("CHILD", "counter", "general", "count the files"), (worker.AgentId, worker.Name, worker.Type, worker.Task));
        Assert.Equal(Path.Combine(new SessionFolder(_paths, "A1").Dir, "CHILD"), worker.Dir);
        var started = log.Since(0).Events.Single(e => Kind(e) == "toolStarted");
        Assert.Equal("llm_agent", started["jobType"]!.GetValue<string>());
        Assert.Equal(worker.JobId, started["job"]!.GetValue<string>());
    }

    [Fact]
    public void A_call_that_never_got_its_result_ends_cancelled()
    {
        History("A1").Record([User("go"), Calls("c1", "run_shell", """{"command":"sleep 99"}""")]);

        var (log, _) = Play("A1");

        var ended = log.Since(0).Events.Single(e => Kind(e) == "toolEnded");
        Assert.Equal("Cancelled", ended["state"]!.GetValue<string>());
    }

    [Fact]
    public void A_session_with_no_folder_shows_nothing()
    {
        var (log, shown) = Play("NOPE");
        Assert.Empty(log.Since(0).Events);
        Assert.Equal(0, shown.Turns);
    }

    [Fact]
    public void A_replay_claims_no_times_its_history_does_not_keep()
    {
        // HISTORY WRITES A CALL AND ITS RESULT TOGETHER, and keeps thinking's text but not its length: a restored
        // session read "Thought for 0s" and "0.1s" for calls that ran for minutes.
        var recorder = History("A1");
        recorder.Record([User("go"), Calls("c1", "run_shell", """{"command":"ls"}"""), Result("c1", "x"), Said("done")]);

        var log = new SessionEventLog("S");
        var reports = new List<Core.Sessions.ToolCallReport>();
        SessionReplay.Play(_paths, "A1", log, log, called: reports.Add);

        var ended = log.Since(0).Events.Single(e => Kind(e) == "toolEnded");
        Assert.Null(ended["ms"]);
        var report = Assert.Single(reports);
        Assert.True(report.Untimed);
        Assert.Equal(("A1", "succeeded"), (report.AgentId, report.Outcome));
    }

    [Fact]
    public void Replayed_thinking_says_what_was_thought_not_for_how_long()
    {
        var log = new SessionEventLog("S");
        var turn = new Core.Sessions.ChatMessageId(1);
        log.AssistantTurnBegan(turn);
        log.AssistantThinkingEnded(turn, new Core.Llm.ThinkingSummary(TimeSpan.Zero, 5, false, false) { Untimed = true });

        Assert.Null(log.Since(0).Events.Single(e => Kind(e) == "thinkingEnded")["seconds"]);
    }

    [Fact]
    public void A_call_made_by_an_earlier_copy_is_reported_under_the_session_s_newest_agent()
    {
        // ROUNDS ARE COUNTED BY AGENT, and the calls the session makes next are its newest agent's.
        var first = new List<ChatMessage> { User("first"), Calls("c1", "run_shell", """{"command":"ls"}"""), Result("c1", "x") };
        History("A1").Record(first);
        History("A2").ContinuesFrom("A1", first);

        var reports = new List<Core.Sessions.ToolCallReport>();
        SessionReplay.Play(_paths, "A2", new SessionEventLog("S"), new SessionEventLog("T"), called: reports.Add);

        Assert.Equal("A2", Assert.Single(reports).AgentId);
    }

    [Fact]
    public void A_compaction_mark_that_opens_a_resumed_copy_is_not_shown()
    {
        // HISTORIES WRITTEN BEFORE THE RECORDER KNEW BETTER mark a resume's replaced system prompt as a compaction, at
        // the very top of the new copy — where no real compaction can be, since nothing has been said there yet.
        var first = new List<ChatMessage> { new() { Role = "system", Content = "old" }, User("first"), Said("one") };
        History("A1").Record(first);
        var path = Path.Combine(new SessionFolder(_paths, "A2").Dir, "history.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path,
        [
            """{"q":1,"at":"2026-11-07T07:16:20Z","k":"continues","prev":"A1"}""",
            """{"q":2,"at":"2026-11-07T07:16:20Z","k":"compacted"}""",
            """{"q":3,"at":"2026-11-07T07:16:20Z","k":"message","m":{"Role":"user","Content":"second"},"from":"You"}""",
        ]);

        var (log, _) = Play("A2");

        Assert.DoesNotContain(log.Since(0).Events, e => Kind(e) == "said");
        Assert.Equal(["first", "second"], log.Since(0).Events.Where(e => Kind(e) == "user").Select(e => e["text"]!.GetValue<string>()));
    }
}
