using System.Text.Json.Nodes;
using CxAgent.Core.Commands;
using CxAgent.Core.Models;
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
    public void Seeding_from_a_transcript_replays_it_first()
    {
        var file = Path.Combine(Directory.CreateTempSubdirectory("seed-").FullName, "transcript.jsonl");
        File.WriteAllLines(file,
        [
            """{"s":"x","q":1,"at":"2026-11-06T05:10:36Z","k":"user","r":"User","b":"ls /"}""",
            """{"s":"x","q":4,"at":"2026-11-06T05:10:37Z","k":"assistant","r":"Assistant","b":"bin boot"}""",
            """{"s":"x","q":-1,"at":"2026-11-06T05:10:30Z","k":"system","r":"Info","b":"6 plugins loaded"}""",
            "not json",
        ]);
        var log = new SessionEventLog("S");
        log.Seed(file);
        log.Said(new Message("live"));

        var kinds = log.Since(0).Events.Select(e => e["kind"]!.GetValue<string>()).ToList();
        Assert.Equal(["user", "turnBegan", "text", "turnEnded", "said", "said"], kinds);
        Assert.Equal("bin boot", log.Since(0).Events[2]["text"]!.GetValue<string>());
    }
}
