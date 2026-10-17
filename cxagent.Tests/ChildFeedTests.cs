using CxAgent.Core.Commands;
using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// A child's observer: its buffers hear everything, and a view attached late is replayed what it missed and
/// then follows live — each event exactly once, however the attach races the child.
/// </summary>
public class ChildFeedTests
{
    /// <summary>Records what it was told as short strings, from any thread.</summary>
    internal sealed class Spy(bool throws = false) : ISessionObserver, IToolObserver
    {
        private readonly object _gate = new();
        private readonly List<string> _seen = [];

        public string[] Seen { get { lock (_gate) return [.. _seen]; } }
        public ChildView View => new(this, this, r => Note($"call:{r.ToolName}"));

        private void Note(string what)
        {
            lock (_gate) _seen.Add(what);
            if (throws) throw new InvalidOperationException("watcher is unhappy");
        }

        public void UserTurnAdded(ChatMessageId id, string text) => Note($"user:{text}");
        public void AssistantTurnBegan(ChatMessageId id) => Note($"began:{id.Value}");
        public void AssistantTextAppended(ChatMessageId id, string token) => Note($"text:{id.Value}:{token}");
        public void AssistantThinkingAppended(ChatMessageId id, string text) => Note($"think:{id.Value}:{text}");
        public void AssistantThinkingEnded(ChatMessageId id, ThinkingSummary summary) => Note($"thought:{id.Value}");
        public void AssistantTurnEnded(ChatMessageId id) => Note($"ended:{id.Value}");
        public void AssistantLabelled(ChatMessageId id, string header) => Note($"label:{header}");
        public void Said(Message message) => Note($"said:{message.Text}");
        public void ToolsChanged(IReadOnlyList<Job> jobs) => Note($"tools:{jobs.Count}");
        public void ToolUpdated(Job job) => Note($"tool:{job.Id}");
        public void ToolProgressed(Job job) => Note($"progress:{job.Id}");
        public void ToolResourcesSampled(string jobId, ResourceSnapshot snapshot) => Note($"sample:{jobId}");
        public void ToolOutputAppended(string jobId, string delta) => Note($"out:{jobId}:{delta}");
    }

    private static ChildFeed Feed(out BufferedChatSink sink)
    {
        sink = new BufferedChatSink();
        return new ChildFeed(sink, new BufferedJobPanel());
    }

    [Fact]
    public void Unwatched_TheBufferStillRecordsEverything()
    {
        var feed = Feed(out var sink);

        feed.AssistantTextAppended(new ChatMessageId(1), "hi");
        feed.Said(new Message("note"));

        Assert.Equal("hi", sink.Body);
        Assert.Contains("note", sink.Notices);
        Assert.Equal(0, feed.Watchers);
    }

    /// <summary>
    /// REVIEW FOCUS 3: the tab opened mid-answer. The replayed "hel" and the live "lo" carry the same id, so a
    /// transcript sink appends both to one message.
    /// </summary>
    [Fact]
    public void Attach_ReplaysWhatHappenedBefore_ThenFollowsLive()
    {
        var feed = Feed(out _);
        feed.UserTurnAdded(new ChatMessageId(1), "go");
        feed.AssistantTurnBegan(new ChatMessageId(2));
        feed.AssistantTextAppended(new ChatMessageId(2), "hel");

        var spy = new Spy();
        feed.Attach(spy.View);
        feed.AssistantTextAppended(new ChatMessageId(2), "lo");
        feed.AssistantTurnEnded(new ChatMessageId(2));

        Assert.Equal(["user:go", "began:2", "text:2:hel", "text:2:lo", "ended:2"], spy.Seen);
    }

    /// <summary>A run of tokens is one entry in the log, so a long answer replays as one append.</summary>
    [Fact]
    public void ALaterWatcher_SeesARunOfTokensAsOneAppend()
    {
        var feed = Feed(out _);
        foreach (var token in new[] { "a", "b", "c" }) feed.AssistantTextAppended(new ChatMessageId(1), token);
        feed.AssistantThinkingAppended(new ChatMessageId(1), "x");
        feed.AssistantThinkingAppended(new ChatMessageId(1), "y");

        var spy = new Spy();
        feed.Attach(spy.View);

        Assert.Equal(["text:1:abc", "think:1:xy"], spy.Seen);
    }

    /// <summary>
    /// THE RACE THE FEED EXISTS FOR. A child publishing on its own thread while the tab attaches: every event
    /// reaches the view exactly once and in order — none lost between replay and attach, none shown twice.
    /// </summary>
    [Fact]
    public async Task AnEventRacingTheAttach_AppearsExactlyOnce()
    {
        var feed = Feed(out _);
        const int count = 20_000;
        using var started = new ManualResetEventSlim();

        var writer = Task.Run(() =>
        {
            for (var i = 0; i < count; i++)
            {
                if (i == 100) started.Set();
                feed.UserTurnAdded(new ChatMessageId(i), i.ToString());
            }
        });
        started.Wait();
        var spy = new Spy();
        feed.Attach(spy.View);
        await writer;

        Assert.Equal(Enumerable.Range(0, count).Select(i => $"user:{i}"), spy.Seen);
    }

    [Fact]
    public void Detaching_StopsDelivery_AndTheBufferCarriesOn()
    {
        var feed = Feed(out var sink);
        var spy = new Spy();
        var attached = feed.Attach(spy.View);

        attached.Dispose();
        feed.AssistantTextAppended(new ChatMessageId(1), "after");

        Assert.Empty(spy.Seen);
        Assert.Equal("after", sink.Body);
        Assert.Equal(0, feed.Watchers);
    }

    [Fact]
    public void AThrowingWatcher_CostsItselfTheEvent_NotTheBufferOrOthers()
    {
        var feed = Feed(out var sink);
        feed.Attach(new Spy(throws: true).View);
        var calm = new Spy();
        feed.Attach(calm.View);

        feed.AssistantTextAppended(new ChatMessageId(1), "still");

        Assert.Equal("still", sink.Body);
        Assert.Equal(["text:1:still"], calm.Seen);
    }

    /// <summary>The transcript's tool rows are drawn from finished calls, so a late view needs them too.</summary>
    [Fact]
    public void AFinishedCall_IsReplayedToALaterView()
    {
        var feed = Feed(out _);
        feed.CallFinished(new ToolCallReport("c1", "child", "read_file", null, "ok", 3, 10, DateTimeOffset.UtcNow));

        var spy = new Spy();
        feed.Attach(spy.View);

        Assert.Equal(["call:read_file"], spy.Seen);
    }

    /// <summary>A transient tick is delivered live but not kept: a late view has nothing to tick yet.</summary>
    [Fact]
    public void ProgressTicks_AreLiveOnly_AndDoNotBreakATextRun()
    {
        var feed = Feed(out _);
        var job = new Job { Id = "j1", AgentId = "child", JobType = "shell", DisplayName = "ls" };
        feed.AssistantTextAppended(new ChatMessageId(1), "a");
        feed.ToolProgressed(job);
        feed.AssistantTextAppended(new ChatMessageId(1), "b");

        var spy = new Spy();
        feed.Attach(spy.View);

        Assert.Equal(["text:1:ab"], spy.Seen);
    }

    /// <summary>A factory child is observed through its feed: what it answered is replayed to a view.</summary>
    [Fact]
    public async Task AFactoryChild_IsObservedThroughItsFeed()
    {
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(new LlmResponse { Text = "found it", StopReason = "end_turn" });
        var child = new SubAgentFactory(new SubAgentFactory.SubAgentRuntime
        {
            Provider = provider, Executors = JobRegistry.CreateWithBuiltins(), Ledger = new TokenLedger(),
            MaxTurns = 5, CompressAbove = 40_000, ContextWindow = 200_000,
        }).Create();

        await child.Agent.SendAsync("look", CancellationToken.None);
        var spy = new Spy();
        child.Feed.Attach(spy.View);

        var text = string.Concat(spy.Seen.Where(s => s.StartsWith("text:", StringComparison.Ordinal))
                                         .Select(s => s[(s.IndexOf(':', 5) + 1)..]));
        Assert.Contains("found it", text, StringComparison.Ordinal);
        Assert.Contains("found it", child.Sink.Body, StringComparison.Ordinal);
    }
}
