using CxAgent.Core.Models;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The sibling of <see cref="ObserverFanOutTests"/>, and necessary for the same reason: fanning out
/// only what a session SAYS would give a second front end a conversation with no working in it —
/// every tool row and progress line arrives through <see cref="IToolObserver"/>.
/// </summary>
public class ToolObserverFanOutTests
{
    private sealed class Spy(bool throws = false) : IToolObserver
    {
        public List<string> Seen { get; } = [];

        private void Note(string what)
        {
            Seen.Add(what);
            if (throws) throw new InvalidOperationException("subscriber is unhappy");
        }

        public void ToolsChanged(IReadOnlyList<Job> jobs) => Note($"changed:{jobs.Count}");
        public void ToolUpdated(Job job) => Note($"updated:{job.Id}");
        public void ToolProgressed(Job job) => Note($"progressed:{job.Id}");
        public void ToolResourcesSampled(string jobId, ResourceSnapshot snapshot) => Note($"sampled:{jobId}");
        public void ToolOutputAppended(string jobId, string delta) => Note($"output:{delta}");
    }

    [Fact]
    public void EverySubscriberSeesEveryEvent()
    {
        var fan = new ToolObserverFanOut();
        var a = new Spy();
        var b = new Spy();
        fan.Add(a);
        fan.Add(b);

        fan.ToolsChanged([]);
        fan.ToolOutputAppended("j1", "hello");

        Assert.Equal(["changed:0", "output:hello"], a.Seen);
        Assert.Equal(["changed:0", "output:hello"], b.Seen);
    }

    /// <summary>A THROW HERE UNWINDS INTO A RUNNING TOOL, not into a render loop — delivery is a
    /// plain call from the job executor's own thread, so isolation matters more here than it does
    /// for the session observer.</summary>
    [Fact]
    public void AThrowingSubscriberDoesNotStopTheEvent()
    {
        var fan = new ToolObserverFanOut();
        var bad = new Spy(throws: true);
        var good = new Spy();
        fan.Add(bad);
        fan.Add(good);

        var ex = Record.Exception(() => fan.ToolOutputAppended("j1", "x"));

        Assert.Null(ex);
        Assert.Equal(["output:x"], good.Seen);
    }

    [Fact]
    public void DisposingTheTokenStopsDelivery()
    {
        var fan = new ToolObserverFanOut();
        var spy = new Spy();
        var token = fan.Add(spy);

        fan.ToolOutputAppended("j1", "before");
        token.Dispose();
        fan.ToolOutputAppended("j1", "after");

        Assert.Equal(["output:before"], spy.Seen);
        Assert.Equal(0, fan.Count);
    }

    [Fact]
    public void NoSubscribersIsNotAnError()
    {
        var fan = new ToolObserverFanOut();

        Assert.Null(Record.Exception(() => fan.ToolProgressed(new Job { Id = "j", AgentId = "a", JobType = "shell", DisplayName = "shell" })));
    }
}
