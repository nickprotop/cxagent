using CxAgent.Core.Commands;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// One observer over several, so a session can be watched by more than one front end.
///
/// <para>WHAT IS WORTH PINNING IS THE ISOLATION, not the forwarding. Forwarding is obvious and fails
/// loudly; a subscriber whose exception unwinds into the turn loop fails a TURN over a bug in
/// something merely watching it, and does so only when that subscriber is unhappy — which is never
/// while the only implementation is a local sink that works.</para>
/// </summary>
public class ObserverFanOutTests
{
    /// <summary>Records what it was told, and can be made to fail on demand.</summary>
    private sealed class Spy(bool throws = false) : ISessionObserver
    {
        public List<string> Seen { get; } = [];

        private void Note(string what)
        {
            Seen.Add(what);
            if (throws) throw new InvalidOperationException("subscriber is unhappy");
        }

        public void UserTurnAdded(ChatMessageId id, string text) => Note($"user:{text}");
        public void AssistantTurnBegan(ChatMessageId id) => Note("began");
        public void AssistantTextAppended(ChatMessageId id, string token) => Note($"text:{token}");
        public void AssistantReasoningAppended(ChatMessageId id, string text) => Note($"reason:{text}");
        public void AssistantTurnEnded(ChatMessageId id) => Note("ended");
        public void AssistantLabelled(ChatMessageId id, string header) => Note($"label:{header}");
        public void Said(Message message) => Note($"said:{message.Text}");
    }

    private static readonly ChatMessageId Id = new(1);

    [Fact]
    public void EverySubscriberSeesEveryEvent()
    {
        var fan = new ObserverFanOut();
        var a = new Spy();
        var b = new Spy();
        fan.Add(a);
        fan.Add(b);

        fan.AssistantTurnBegan(Id);
        fan.AssistantTextAppended(Id, "hello");
        fan.AssistantTurnEnded(Id);

        Assert.Equal(["began", "text:hello", "ended"], a.Seen);
        Assert.Equal(["began", "text:hello", "ended"], b.Seen);
    }

    /// <summary>
    /// A THROWING SUBSCRIBER COSTS ITSELF ONE EVENT AND NOTHING ELSE. This is the whole reason the
    /// type exists rather than a list and a foreach at each call site: an exception escaping here
    /// unwinds into the agent's own flow and fails a turn over a rendering bug in a watcher.
    /// </summary>
    [Fact]
    public void AThrowingSubscriberDoesNotStopTheEvent()
    {
        var fan = new ObserverFanOut();
        var bad = new Spy(throws: true);
        var good = new Spy();
        fan.Add(bad);
        fan.Add(good);

        var ex = Record.Exception(() => fan.AssistantTurnBegan(Id));

        Assert.Null(ex);
        Assert.Equal(["began"], good.Seen);
    }

    /// <summary>AND IT KEEPS RECEIVING. One bad event is not a reason to drop a subscriber: a front
    /// end that failed to render one token is still the front end somebody is looking at.</summary>
    [Fact]
    public void AThrowingSubscriberIsNotDropped()
    {
        var fan = new ObserverFanOut();
        var bad = new Spy(throws: true);
        fan.Add(bad);

        fan.AssistantTurnBegan(Id);
        fan.AssistantTurnEnded(Id);

        Assert.Equal(["began", "ended"], bad.Seen);
    }

    /// <summary>DELIVERY ORDER IS REGISTRATION ORDER, and a throw must not change it — otherwise a
    /// subscriber's own health would decide whether the ones after it are reached.</summary>
    [Fact]
    public void OrderSurvivesAThrow()
    {
        var fan = new ObserverFanOut();
        var first = new Spy();
        var bad = new Spy(throws: true);
        var last = new Spy();
        fan.Add(first);
        fan.Add(bad);
        fan.Add(last);

        fan.Said(new Message("hi"));

        Assert.Equal(["said:hi"], first.Seen);
        Assert.Equal(["said:hi"], last.Seen);
    }

    [Fact]
    public void DisposingTheTokenStopsDelivery()
    {
        var fan = new ObserverFanOut();
        var spy = new Spy();
        var token = fan.Add(spy);

        fan.AssistantTurnBegan(Id);
        token.Dispose();
        fan.AssistantTurnEnded(Id);

        Assert.Equal(["began"], spy.Seen);
        Assert.Equal(0, fan.Count);
    }

    /// <summary>
    /// THE SAME OBSERVER MAY ATTACH TWICE — a reconnect racing its own teardown — and disposing one
    /// registration must take out exactly that one. Removing "the one that equals this" would take
    /// both, silencing a front end that is still there.
    /// </summary>
    [Fact]
    public void OneRegistrationOfTwoIsRemoved()
    {
        var fan = new ObserverFanOut();
        var spy = new Spy();
        var first = fan.Add(spy);
        fan.Add(spy);

        first.Dispose();
        fan.AssistantTurnBegan(Id);

        Assert.Equal(1, fan.Count);
        Assert.Equal(["began"], spy.Seen);   // still receiving, once
    }

    /// <summary>A DOUBLE DISPOSE IS A NO-OP, not the removal of the other registration.</summary>
    [Fact]
    public void DisposingTwiceRemovesOnlyOne()
    {
        var fan = new ObserverFanOut();
        var spy = new Spy();
        var first = fan.Add(spy);
        fan.Add(spy);

        first.Dispose();
        first.Dispose();

        Assert.Equal(1, fan.Count);
    }

    [Fact]
    public void NoSubscribersIsNotAnError()
    {
        var fan = new ObserverFanOut();

        var ex = Record.Exception(() =>
        {
            fan.AssistantTurnBegan(Id);
            fan.Said(new Message("nobody is listening"));
        });

        Assert.Null(ex);
    }

    /// <summary>
    /// ATTACHING DURING DELIVERY MUST NOT THROW. A front end connects while a turn is streaming, and
    /// delivery iterating a live list rather than a snapshot would fault mid-turn — the failure mode
    /// this copies on write to avoid, and one that only appears under exactly this timing.
    /// </summary>
    [Fact]
    public void AttachingWhileDeliveringIsSafe()
    {
        var fan = new ObserverFanOut();
        var joiner = new Spy();

        // Subscriber that attaches another the moment it is called.
        fan.Add(new Attacher(fan, joiner));

        var ex = Record.Exception(() => fan.AssistantTurnBegan(Id));

        Assert.Null(ex);
        Assert.Equal(2, fan.Count);
        Assert.Empty(joiner.Seen);   // joined after the snapshot for this event was taken
    }

    private sealed class Attacher(ObserverFanOut fan, ISessionObserver joiner) : ISessionObserver
    {
        public void UserTurnAdded(ChatMessageId id, string text) { }
        public void AssistantTurnBegan(ChatMessageId id) => fan.Add(joiner);
        public void AssistantTextAppended(ChatMessageId id, string token) { }
        public void AssistantReasoningAppended(ChatMessageId id, string text) { }
        public void AssistantTurnEnded(ChatMessageId id) { }
        public void AssistantLabelled(ChatMessageId id, string header) { }
        public void Said(Message message) { }
    }

    /// <summary>
    /// Concurrent attach and detach while events flow. Not a proof of thread safety, but it fails
    /// reliably against a plain List, which is the mistake being guarded.
    /// </summary>
    [Fact]
    public async Task ConcurrentAttachDetachAndDeliveryDoNotFault()
    {
        var fan = new ObserverFanOut();
        var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));

        var churn = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                var token = fan.Add(new Spy());
                token.Dispose();
            }
        });

        var deliver = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
                fan.AssistantTextAppended(Id, "x");
        });

        await Task.WhenAll(churn, deliver);

        Assert.Equal(0, fan.Count);
    }
    /// <summary>
    /// A RE-WIRE SWAPS THE PORTS' SUBSCRIBER WITHOUT EVER DOUBLE-SUBSCRIBING.
    ///
    /// <para>THE WINDOW BETWEEN ADD AND REMOVE IS THE BUG. A re-wire (`/model`, resume, a setup
    /// flow) replaces the front end's sink, and if the new one joins before the old one leaves, an
    /// event arriving in between reaches a transcript control TWICE — invisible in a transcript that
    /// appends. Narrow today because a re-wire refuses while a turn is running; under a daemon, a
    /// re-wire racing an in-flight event is the ordinary case.</para>
    ///
    /// <para>The shape is what makes it safe: `ResubscribePorts` takes the fan-out and the observer
    /// and does the `Add` ITSELF. Handed ready-made subscription handles instead, the caller has to
    /// create them to pass them, and C# evaluates arguments before the call — so both would join
    /// before the method's first line, and the ordering could not be honoured from inside it.</para>
    ///
    /// <para><b>THIS TEST PINS THE END STATE, NOT THE WINDOW</b> — stated plainly because the
    /// distinction matters and the test cannot make it. The double-subscription lasts only between
    /// the `Add` and the `Dispose`, so a single-threaded test observes the same final fan-out either
    /// way: verified by reintroducing the add-before-remove ordering, and these assertions still
    /// passed. Catching the window itself needs an event raised from another thread DURING the swap,
    /// which is a race a test can only make probable, never certain. What is enforced here is that
    /// one subscriber remains and it is the new one; what prevents the window is the SIGNATURE,
    /// which is why the argument-evaluation reasoning is written into the method's own remarks
    /// rather than left to this test.</para>
    /// </summary>
    [Fact]
    public void ReSubscribingPortsReplacesTheSubscriberRatherThanAddingBesideIt()
    {
        var session = new Session(Path.GetTempPath());
        var fan = new ObserverFanOut();
        var tools = new ToolObserverFanOut();

        var outgoing = new Spy();
        session.ResubscribePorts(new Session.PortSubscription(fan, outgoing, tools, null));

        var incoming = new Spy();
        session.ResubscribePorts(new Session.PortSubscription(fan, incoming, tools, null));

        fan.Said(new Message("hello"));

        Assert.Empty(outgoing.Seen);                  // the outgoing sink is gone
        Assert.Equal(["said:hello"], incoming.Seen);  // and the incoming one hears it exactly once
        Assert.Equal(1, fan.Count);
    }

    /// <summary>
    /// AND A SUBSCRIBER THAT JOINED SEPARATELY SURVIVES THE RE-WIRE.
    ///
    /// <para>This is the whole reason the fan-outs are KEPT across a wire rather than rebuilt:
    /// minting fresh ones would silently drop every subscriber that had joined since the last wire —
    /// an attached client, a transcript store — failing by going quiet rather than by throwing.</para>
    /// </summary>
    [Fact]
    public void ReSubscribingPortsLeavesOtherSubscribersInPlace()
    {
        var session = new Session(Path.GetTempPath());
        var fan = new ObserverFanOut();
        var tools = new ToolObserverFanOut();

        var attached = new Spy();   // an attached client, not the ports'
        fan.Add(attached);

        session.ResubscribePorts(new Session.PortSubscription(fan, new Spy(), tools, null));
        session.ResubscribePorts(new Session.PortSubscription(fan, new Spy(), tools, null));

        fan.Said(new Message("hello"));

        Assert.Equal(["said:hello"], attached.Seen);
    }

}
