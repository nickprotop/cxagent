using System.Text.Json.Nodes;
using CxAgent.Core.Commands;
using CxAgent.Core.Models;
using CxAgent.Core.Storage;
using CxAgent.Web;
using Xunit;

namespace CxAgent.Tests;

/// <summary>What a browser tab is sent about a session: every event numbered once, kept for replay, and a reconnect
/// that resumes exactly where it left off.</summary>
public sealed class SessionEventLogTests
{
    private static readonly ChatMessageId Turn = new(7);

    private static SessionEventLog Streamed(int tokens)
    {
        var log = new SessionEventLog("S");
        log.UserTurnAdded(new ChatMessageId(6), "hi");
        log.AssistantTurnBegan(Turn);
        for (var i = 0; i < tokens; i++) log.AssistantTextAppended(Turn, $"t{i} ");
        log.AssistantTurnEnded(Turn);
        return log;
    }

    private static long Seq(JsonObject e) => e["seq"]!.GetValue<long>();

    [Fact]
    public void Events_are_numbered_from_one_in_order()
    {
        var events = Streamed(3).Since(0).Events;
        Assert.Equal([1L, 2, 3, 4, 5, 6], events.Select(Seq));
        Assert.Equal(["user", "turnBegan", "text", "text", "text", "turnEnded"], events.Select(e => e["kind"]!.GetValue<string>()));
        Assert.All(events, e => Assert.Equal("S", e["sessionId"]!.GetValue<string>()));
    }

    [Fact]
    public void Reconnect_since_seq_replays_only_missed_events()
    {
        var log = Streamed(20);
        var missed = log.Since(5);
        Assert.False(missed.Reset);
        Assert.Equal(Enumerable.Range(6, 18).Select(i => (long)i), missed.Events.Select(Seq));
    }

    [Fact]
    public void A_subscriber_gets_live_events_after_its_replay_without_a_gap_or_a_repeat()
    {
        var log = Streamed(2);
        var seen = new List<long>();
        var replay = log.Subscribe(0, e => seen.Add(Seq(e)), out var _);
        seen.InsertRange(0, replay.Events.Select(Seq));
        log.AssistantTurnBegan(new ChatMessageId(9));
        log.AssistantTextAppended(new ChatMessageId(9), "more");
        Assert.Equal(Enumerable.Range(1, seen.Count).Select(i => (long)i), seen);
    }

    [Fact]
    public void An_unsubscribed_tab_hears_nothing_more()
    {
        var log = Streamed(1);
        var seen = 0;
        log.Subscribe(log.LastSeq, _ => seen++, out var handle);
        handle.Dispose();
        log.Said(new Message("after"));
        Assert.Equal(0, seen);
    }

    [Fact]
    public void A_since_beyond_this_log_starts_over()
    {
        // A TAB FROM A PREVIOUS SERVER asks for "after 812" of a log that has only just begun; it must redraw, not
        // wait for the numbers to catch up while dropping everything below them.
        var log = Streamed(2);
        var since = log.Since(812);
        Assert.True(since.Reset);
        Assert.Equal(log.LastSeq, since.Events.Count);
    }

    [Fact]
    public void A_reconnect_older_than_the_window_starts_over()
    {
        var log = new SessionEventLog("S", capacity: 10);
        for (var i = 0; i < 30; i++) log.Said(new Message($"m{i}"));
        var since = log.Since(3);
        Assert.True(since.Reset);
        Assert.Equal(10, since.Events.Count);
        Assert.Equal(30, Seq(since.Events[^1]));
    }

    [Fact]
    public void Tool_events_carry_the_contained_mark_and_cap_output()
    {
        var log = new SessionEventLog("S");
        var job = new Job { Id = "J1", AgentId = "A", JobType = "shell", DisplayName = "run_shell · ls", State = JobState.Running };
        log.ToolsChanged([job]);
        log.ToolOutputAppended("J1", new string('x', 70_000));
        log.ToolOutputAppended("J1", "more");
        job.State = JobState.Succeeded;
        job.Result = new JobResult { Success = true, Output = { ["sandbox"] = "contained" } };
        log.ToolUpdated(job);

        var events = log.Since(0).Events;
        Assert.Equal("toolStarted", events[0]["kind"]!.GetValue<string>());
        Assert.Equal("run_shell · ls", events[0]["name"]!.GetValue<string>());
        var output = string.Concat(events.Where(e => e["kind"]!.GetValue<string>() == "toolOutput").Select(e => e["text"]!.GetValue<string>()));
        Assert.True(output.Length <= SessionEventLog.MaxToolOutput + 200);
        var ended = events.Single(e => e["kind"]!.GetValue<string>() == "toolEnded");
        Assert.Equal("contained", ended["sandbox"]!.GetValue<string>());
    }

    [Fact]
    public void A_worker_row_carries_its_type_model_and_live_progress()
    {
        var log = new SessionEventLog("S");
        var job = new Job { Id = "W1", AgentId = "A", JobType = "llm_agent", DisplayName = "planner · find tests", State = JobState.Running,
            RunsOn = "deepseek:deepseek-v3.2", ProgressMessage = "starting…" };
        log.ToolsChanged([job]);
        job.ProgressMessage = "2 turns · 14% ctx · 6s";
        job.ProgressBody = "task: find tests";
        log.ToolProgressed(job);
        log.ToolProgressed(job);   // unchanged: nothing new

        var events = log.Since(0).Events;
        Assert.Equal("llm_agent", events[0]["jobType"]!.GetValue<string>());
        Assert.Equal("deepseek:deepseek-v3.2", events[0]["runsOn"]!.GetValue<string>());
        var progress = events.Where(e => e["kind"]!.GetValue<string>() == "toolProgress").ToList();
        Assert.Single(progress);
        Assert.Equal("2 turns · 14% ctx · 6s", progress[0]["progress"]!.GetValue<string>());
        Assert.Equal("task: find tests", progress[0]["body"]!.GetValue<string>());
    }

    [Fact]
    public void A_tool_that_streamed_nothing_ends_with_its_result_and_its_own_duration()
    {
        // A REPLAY ARRIVES IN ONE BURST, so a duration the page times itself reads 0.0s; and most tools never stream,
        // so without the result on the end their row opens on "(no output)".
        var log = new SessionEventLog("S");
        var start = DateTimeOffset.UtcNow.AddSeconds(-20);
        var job = new Job { Id = "J1", AgentId = "A", JobType = "read_file", DisplayName = "read_file · a.txt", State = JobState.Running, StartedAt = start };
        log.ToolsChanged([job]);
        job.State = JobState.Succeeded;
        job.CompletedAt = start.AddSeconds(20);
        job.Result = new JobResult { Success = true, Output = { ["content"] = "hello" } };
        log.ToolUpdated(job);

        var ended = log.Since(0).Events.Single(e => e["kind"]!.GetValue<string>() == "toolEnded");
        Assert.Equal("hello", ended["output"]!.GetValue<string>());
        Assert.Equal(20_000, ended["ms"]!.GetValue<long>());
    }

    [Fact]
    public void A_tool_that_streamed_its_output_does_not_send_it_twice()
    {
        var log = new SessionEventLog("S");
        var job = new Job { Id = "J1", AgentId = "A", JobType = "shell", DisplayName = "run_shell · ls", State = JobState.Running };
        log.ToolsChanged([job]);
        log.ToolOutputAppended("J1", "a\nb\n");
        job.State = JobState.Succeeded;
        job.Result = new JobResult { Success = true, Output = { ["content"] = "a\nb\n" } };
        log.ToolUpdated(job);

        var ended = log.Since(0).Events.Single(e => e["kind"]!.GetValue<string>() == "toolEnded");
        Assert.Null(ended["output"]);
    }

    [Fact]
    public void A_worker_event_keeps_its_type_apart_from_the_message_type()
    {
        var log = new SessionEventLog("S");
        log.Worker(new CxAgent.Web.WorkerShown("W1", "agent-1", "planner", "general", "find tests"));
        var e = log.Since(0).Events.Single();
        Assert.Equal("event", e["type"]!.GetValue<string>());
        Assert.Equal("general", e["workerType"]!.GetValue<string>());
    }

    [Fact]
    public void A_worker_row_that_ends_says_its_last_progress()
    {
        // A BACKGROUND WORKER'S ROW ENDS AT LAUNCH, its line set to "started in background" with no progress call
        // between; without it on the end the card keeps whatever it said before — "starting…".
        var log = new SessionEventLog("S");
        var job = new Job { Id = "W1", AgentId = "A", JobType = "llm_agent", DisplayName = "planner", State = JobState.Running,
            ProgressMessage = "starting…" };
        log.ToolsChanged([job]);
        job.ProgressMessage = "started in background · planner";
        job.State = JobState.Succeeded;
        log.ToolUpdated(job);

        var ended = log.Since(0).Events.Single(e => e["kind"]!.GetValue<string>() == "toolEnded");
        Assert.Equal("started in background · planner", ended["progress"]!.GetValue<string>());
    }

    [Fact]
    public void A_row_ends_once_however_often_its_end_is_reported_and_again_after_it_reopens()
    {
        // A VIEW ATTACHING LATE is replayed the row's start with the job as it is now — finished — and then the update
        // that finished it. A worker's row reopened by more work runs and ends again, which is a second end.
        var log = new SessionEventLog("S");
        var job = new Job { Id = "W1", AgentId = "A", JobType = "llm_agent", DisplayName = "w", State = JobState.Succeeded };
        log.ToolsChanged([job]);
        log.ToolUpdated(job);
        job.State = JobState.Running;
        log.ToolUpdated(job);
        job.State = JobState.Succeeded;
        log.ToolUpdated(job);

        Assert.Equal(2, log.Since(0).Events.Count(e => e["kind"]!.GetValue<string>() == "toolEnded"));
    }

    [Fact]
    public void The_log_counts_turns_and_tool_calls()
    {
        var log = Streamed(1);
        var job = new Job { Id = "J", AgentId = "A", JobType = "shell", DisplayName = "x", State = JobState.Running };
        log.ToolsChanged([job]);
        job.State = JobState.Succeeded;
        log.ToolUpdated(job);
        Assert.Equal(1, log.Turns);
        Assert.Equal(1, log.ToolCalls);
    }

    private static AppPaths SavedSession(out string agentId)
    {
        // ONE TURN WITH ONE TOOL CALL, as history.jsonl records it.
        var paths = new AppPaths(Directory.CreateTempSubdirectory("replay-").FullName);
        agentId = "A1";
        var dir = new SessionFolder(paths, agentId).Dir;
        Directory.CreateDirectory(dir);
        new Core.Storage.HistoryRecorder(Path.Combine(dir, "history.jsonl")).Record(
        [
            new ChatMessage { Role = "user", Content = "ls /" },
            new ChatMessage { Role = "assistant", Content = "", ToolCalls = [new ToolCall { Id = "c1", Name = "run_shell",
                Arguments = System.Text.Json.JsonDocument.Parse("""{"command":"ls /"}""").RootElement.Clone() }] },
            new ChatMessage { Role = "tool", ToolCallId = "c1", Content = "bin boot" },
            new ChatMessage { Role = "assistant", Content = "two folders" },
        ]);
        return paths;
    }

    [Fact]
    public void A_replayed_past_is_not_counted_with_this_run()
    {
        // THE COUNTS ARE THIS RUN'S, as the terminal's are for a resumed session.
        var log = new SessionEventLog("S");
        log.Replay(Core.Export.ExportSource.Load(SavedSession(out var agent), agent)!);
        Assert.Equal((0, 0), (log.Turns, log.ToolCalls));

        log.UserTurnAdded(new ChatMessageId(9), "again");
        Assert.Equal(1, log.Turns);
    }

    [Fact]
    public void Live_turns_that_arrive_during_a_replay_are_still_counted()
    {
        // A LONG REPLAY AND A LIVE TURN ON ANOTHER THREAD: the replay's events are not this run's, the live one is.
        var paths = new AppPaths(Directory.CreateTempSubdirectory("replay-").FullName);
        var dir = new SessionFolder(paths, "A1").Dir;
        Directory.CreateDirectory(dir);
        new Core.Storage.HistoryRecorder(Path.Combine(dir, "history.jsonl")).Record(
            [.. Enumerable.Range(0, 3000).SelectMany(i => new[] { new ChatMessage { Role = "user", Content = $"u{i}" }, new ChatMessage { Role = "assistant", Content = $"a{i}" } })]);
        var log = new SessionEventLog("S");
        var past = Core.Export.ExportSource.Load(paths, "A1")!;

        var live = Task.Run(() => { for (var i = 0; i < 300; i++) log.UserTurnAdded(new ChatMessageId(i + 1), "live"); });
        log.Replay(past);
        live.Wait();

        Assert.Equal(300, log.Turns);
    }

    [Fact]
    public void Replaying_a_saved_session_comes_before_anything_live()
    {
        var log = new SessionEventLog("S");
        log.Replay(Core.Export.ExportSource.Load(SavedSession(out var agent), agent)!);
        log.Said(new Message("live"));

        var kinds = log.Since(0).Events.Select(e => e["kind"]!.GetValue<string>()).ToList();
        Assert.Equal(["user", "turnBegan", "turnEnded", "toolStarted", "toolEnded", "turnBegan", "text", "turnEnded", "said"], kinds);
    }

    [Fact]
    public void A_replayed_worker_is_announced_for_its_row()
    {
        var paths = new AppPaths(Directory.CreateTempSubdirectory("replay-").FullName);
        var dir = new SessionFolder(paths, "A1").Dir;
        Directory.CreateDirectory(dir);
        new Core.Storage.HistoryRecorder(Path.Combine(dir, "history.jsonl")).Record(
        [
            new ChatMessage { Role = "user", Content = "count" },
            new ChatMessage { Role = "assistant", Content = "", ToolCalls = [new ToolCall { Id = "c1", Name = "agent",
                Arguments = System.Text.Json.JsonDocument.Parse("""{"type":"general","prompt":"count files"}""").RootElement.Clone() }] },
            new ChatMessage { Role = "tool", ToolCallId = "c1", Content = """<agent id="CHILD" name="counter" state="completed">2</agent>""" },
        ]);
        var log = new SessionEventLog("S");
        log.Replay(Core.Export.ExportSource.Load(paths, "A1")!);

        var events = log.Since(0).Events;
        var kinds = events.Select(e => e["kind"]!.GetValue<string>()).ToList();
        Assert.True(kinds.IndexOf("toolStarted") < kinds.IndexOf("worker") && kinds.IndexOf("worker") < kinds.IndexOf("toolEnded"));
        var worker = events.Single(e => e["kind"]!.GetValue<string>() == "worker");
        Assert.Equal(("CHILD", "counter", "general"), (worker["agentId"]!.GetValue<string>(), worker["name"]!.GetValue<string>(),
            worker["workerType"]!.GetValue<string>()));
    }

    [Fact]
    public void A_clear_drops_what_was_kept_and_is_itself_the_first_event_a_late_tab_sees()
    {
        var log = Streamed(3);
        log.Cleared();
        log.UserTurnAdded(new ChatMessageId(9), "after");

        var all = log.Since(0).Events;
        Assert.Equal(["cleared", "user"], all.Select(e => e["kind"]!.GetValue<string>()));

        // A TAB THAT SAW THE OLD CONVERSATION is told to redraw from the clear on.
        var back = log.Since(3);
        Assert.True(back.Reset);
        Assert.Equal("cleared", back.Events[0]["kind"]!.GetValue<string>());
    }

    private static CxAgent.Core.Sessions.ToolCallReport Call(string id, string outcome, long ms = 42, int chars = 1200) =>
        new(id, "child-1", "read_file", "file", outcome, ms, chars, DateTimeOffset.UtcNow) { Target = "Agent.cs" };

    /// <summary>A WORKER'S FINISHED CALL is told to the page as the terminal's row: its mark, tool, target, time and
    /// size already formatted, so the two tables cannot read one call two ways.</summary>
    [Fact]
    public void A_workers_finished_call_is_the_terminals_row()
    {
        var log = new SessionEventLog("S");
        log.WorkerCall(Call("c1", "succeeded", ms: 1234, chars: 1200));
        var e = log.Since(0).Events.Single();
        Assert.Equal("workerCall", e["kind"]!.GetValue<string>());
        Assert.Equal("child-1", e["agent"]!.GetValue<string>());
        Assert.Equal("✓", e["mark"]!.GetValue<string>());
        Assert.Equal("ok", e["tone"]!.GetValue<string>());
        Assert.Equal("read_file", e["tool"]!.GetValue<string>());
        Assert.Equal("Agent.cs", e["target"]!.GetValue<string>());
        Assert.Equal("1,234", e["ms"]!.GetValue<string>());
        Assert.Equal("1.2 kB", e["out"]!.GetValue<string>());
        Assert.Equal("1 call · across 1 tool · 1.2s in tools", e["summary"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("succeeded", "✓", "ok")]
    [InlineData("failed", "✗", "bad")]
    [InlineData("denied", "⊘", "skip")]
    [InlineData("cancelled", "–", "skip")]
    public void Each_outcome_has_the_terminals_mark(string outcome, string mark, string tone)
    {
        var log = new SessionEventLog("S");
        log.WorkerCall(Call("c1", outcome));
        var e = log.Since(0).Events.Single();
        Assert.Equal(mark, e["mark"]!.GetValue<string>());
        Assert.Equal(tone, e["tone"]!.GetValue<string>());
    }

    /// <summary>THE SUMMARY COUNTS THE WORKER'S CALLS SO FAR, the bad ones named, as the terminal's line over its
    /// table does.</summary>
    [Fact]
    public void The_summary_counts_a_workers_calls_so_far()
    {
        var log = new SessionEventLog("S");
        log.WorkerCall(Call("c1", "succeeded"));
        log.WorkerCall(Call("c2", "failed"));
        var last = log.Since(0).Events.Last();
        Assert.Equal("2 calls · across 1 tool · 0.1s in tools · 1 failed", last["summary"]!.GetValue<string>());
    }
}
