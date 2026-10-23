using CxAgent.Core.Jobs;
using Xunit;

namespace CxAgent.Tests;

/// <summary>Exactly once, whoever asks and whenever it ends.</summary>
public class ResultHandoffTests
{
    private sealed class Counter
    {
        public int Sent, Left;
        public bool SendAnswer = true, LeaveAnswer = true;
        public bool Send(string _) { Interlocked.Increment(ref Sent); return SendAnswer; }
        public bool Leave(string _) { Interlocked.Increment(ref Left); return LeaveAnswer; }
    }

    [Fact]
    public void Complete_WithNobodyWaiting_SendsOnce()
    {
        var c = new Counter(); var h = new ResultHandoff(c.Leave);
        h.Complete("r", c.Send);
        Assert.Equal(1, c.Sent);
        Assert.Equal(new HandoffRead("r", true, true), h.Read());
    }

    /// <summary>
    /// A SENT RESULT STILL WAITING TO BE READ IS TAKEN BACK BY A RECLAIM, once: the reader returns it, so the
    /// message must not arrive as well — and a second reclaim has nothing left to take.
    /// </summary>
    [Fact]
    public void Reclaim_TakesBackASentResult_Once()
    {
        var c = new Counter(); var h = new ResultHandoff(c.Leave);
        var withdrawn = new List<string>();
        h.Complete("r", c.Send, withdraw: text => { withdrawn.Add(text); return true; });

        Assert.True(h.Reclaim());
        Assert.Equal(["r"], withdrawn);
        Assert.False(h.Reclaim());
        Assert.Single(withdrawn);
    }

    /// <summary>
    /// A WITHDRAWAL THAT FAILS LEAVES THE RESULT SENT: it was read already, or never queued where the
    /// withdrawal looks. The reader then repeats it as it always has — the worst case is a duplicate, never a
    /// report nobody gets.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AFailedWithdrawal_LeavesTheResultSent(bool throws)
    {
        var c = new Counter(); var h = new ResultHandoff(c.Leave);
        h.Complete("r", c.Send, withdraw: _ => throws ? throw new InvalidOperationException() : false);

        Assert.False(h.Reclaim());
        Assert.Equal(new HandoffRead("r", true, true), h.Read());
    }

    [Fact]
    public void Reclaim_WithNoWithdrawal_OrBeforeTheEnd_TakesNothing()
    {
        var c = new Counter(); var h = new ResultHandoff(c.Leave);
        Assert.False(h.Reclaim());
        h.Complete("r", c.Send);
        Assert.False(h.Reclaim());
        Assert.Equal(new HandoffRead("r", true, true), h.Read());
    }

    /// <summary>
    /// ONLY A SENT RESULT IS RECLAIMED. An undelivered one is Read's to take, and one handed to a waiter
    /// was never sent — withdrawing either would pull a message that does not exist.
    /// </summary>
    [Fact]
    public void Reclaim_LeavesAnUndeliveredResult_ToRead()
    {
        var c = new Counter { SendAnswer = false }; var h = new ResultHandoff(c.Leave);
        var asked = 0;
        h.Complete("r", c.Send, withdraw: _ => { asked++; return true; });

        Assert.False(h.Reclaim());
        Assert.Equal(0, asked);
        Assert.Equal(new HandoffRead("r", true, false), h.Read());
    }

    [Fact]
    public void Read_BeforeTheEnd_SaysNotEnded() =>
        Assert.Equal(new HandoffRead(null, false, false), new ResultHandoff(_ => true).Read());

    [Fact]
    public async Task AWaiter_TakesTheResult_AndNothingIsSent()
    {
        var c = new Counter(); var h = new ResultHandoff(c.Leave);
        var wait = h.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        h.Complete("r", c.Send);
        Assert.Equal(new HandoffRead("r", true, false), await wait);
        Assert.Equal(0, c.Sent);
        Assert.Equal(0, c.Left);
    }

    [Fact]
    public async Task TwoWaiters_BothGetIt_AndNothingIsSent()
    {
        var c = new Counter(); var h = new ResultHandoff(c.Leave);
        var a = h.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        var b = h.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        h.Complete("r", c.Send);
        Assert.Equal("r", (await a).Result);
        Assert.Equal("r", (await b).Result);
        Assert.Equal(0, c.Sent);
    }

    [Fact]
    public async Task AWaitThatTimesOut_LeavesTheResultToBeSent()
    {
        var c = new Counter(); var h = new ResultHandoff(c.Leave);
        Assert.Equal(new HandoffRead(null, false, false),
            await h.WaitAsync(TimeSpan.FromMilliseconds(20), CancellationToken.None));
        h.Complete("r", c.Send);
        Assert.Equal(1, c.Sent);
    }

    [Fact]
    public async Task AWaitCancelledBeforeTheEnd_LeavesTheResultToBeSent()
    {
        var c = new Counter(); var h = new ResultHandoff(c.Leave);
        using var cts = new CancellationTokenSource();
        var wait = h.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        h.Complete("r", c.Send);
        Assert.Equal(1, c.Sent);
        Assert.Equal(0, c.Left);
    }

    /// <summary>
    /// ESCAPE LANDS AS THE RUN ENDS. Whichever way the await resolves, the parent gets the result
    /// exactly once — as the wait's answer, or as a left note.
    /// </summary>
    [Fact]
    public async Task AWaitCancelledAtTheEnd_LosesNothing_AndDuplicatesNothing()
    {
        for (var i = 0; i < 500; i++)
        {
            var c = new Counter(); var h = new ResultHandoff(c.Leave);
            using var cts = new CancellationTokenSource();
            var wait = h.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);
            await Task.WhenAll(Task.Run(() => h.Complete("r", c.Send)), Task.Run(cts.Cancel));

            var answered = false;
            try { answered = (await wait).Result == "r"; }
            catch (OperationCanceledException) { }

            Assert.Equal(1, (answered ? 1 : 0) + c.Sent + c.Left);
        }
    }

    [Fact]
    public void ARefusedSend_IsUndelivered_AndReadTakesIt()
    {
        var c = new Counter { SendAnswer = false }; var h = new ResultHandoff(c.Leave);
        h.Complete("r", c.Send);
        Assert.True(h.Undelivered);
        Assert.Equal(new HandoffRead("r", true, false), h.Read());
        Assert.False(h.Undelivered);
        Assert.Equal(new HandoffRead("r", true, true), h.Read());
    }

    [Fact]
    public void ASendThatThrows_IsUndelivered()
    {
        var h = new ResultHandoff(_ => true);
        h.Complete("r", _ => throw new InvalidOperationException("session gone"));
        Assert.True(h.Undelivered);
    }

    [Fact]
    public async Task Silenced_NeverSends_NorLeaves_ButReadReturnsIt()
    {
        var c = new Counter(); var h = new ResultHandoff(c.Leave);
        h.Silence();
        h.Complete("r", c.Send);
        Assert.Equal(0, c.Sent);
        Assert.Equal(new HandoffRead("r", true, false), h.Read());
        Assert.Equal("r", (await h.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None)).Result);
        Assert.Equal(0, c.Left);
    }

    [Fact]
    public void ASecondComplete_IsIgnored()
    {
        var c = new Counter(); var h = new ResultHandoff(c.Leave);
        h.Complete("first", c.Send);
        h.Complete("second", c.Send);
        Assert.Equal(1, c.Sent);
        Assert.Equal("first", h.Result);
    }

    /// <summary>
    /// SILENCED, BUT SOMEONE WAS ALREADY WAITING. The waiter asked before the conversation began
    /// closing, so it is answered — promptly, not after its timeout — and nothing is sent or left.
    /// </summary>
    [Fact]
    public async Task Silenced_WithAWaiter_AnswersTheWaiter_AndSendsNothing()
    {
        var c = new Counter(); var h = new ResultHandoff(c.Leave);
        var wait = h.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        h.Silence();
        h.Complete("r", c.Send);
        Assert.Same(wait, await Task.WhenAny(wait, Task.Delay(TimeSpan.FromSeconds(2))));
        Assert.Equal(new HandoffRead("r", true, false), await wait);
        Assert.Equal(0, c.Sent);
        Assert.Equal(0, c.Left);
        Assert.True(h.Read().AlreadySent);
    }

    /// <summary>
    /// SILENCED, AND THE ONE WAITER HANDED IT IS CANCELLED. A silenced result is never left; it waits for
    /// a Read, which takes it exactly once. The cancel races the waiter's own continuation, so this
    /// repeats until the cancel has won at least once — and checks the invariant whichever won.
    /// </summary>
    [Fact]
    public async Task Silenced_ThenTheWaiterIsCancelled_LeavesNothing_AndReadTakesItOnce() =>
        await CancelTheOnlyWaiter(silenceAfterComplete: false);

    /// <summary>
    /// SILENCED AFTER THE RESULT REACHED THE WAITERS. If they are then all cancelled, the leave that
    /// would follow is a note into a closing conversation — so there is none.
    /// </summary>
    [Fact]
    public async Task SilencedWhileWithTheWaiters_ThenTheyAreCancelled_LeavesNothing() =>
        await CancelTheOnlyWaiter(silenceAfterComplete: true);

    private static async Task CancelTheOnlyWaiter(bool silenceAfterComplete)
    {
        var cancelWon = 0;
        for (var i = 0; i < 200 && cancelWon < 5; i++)
        {
            var c = new Counter(); var h = new ResultHandoff(c.Leave);
            using var cts = new CancellationTokenSource();
            var wait = h.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);
            if (!silenceAfterComplete) h.Silence();
            h.Complete("r", c.Send);
            if (silenceAfterComplete) h.Silence();
            cts.Cancel();

            HandoffRead? answer = null;
            try { answer = await wait; }
            catch (OperationCanceledException) { cancelWon++; }

            Assert.Equal(0, c.Sent);
            Assert.Equal(0, c.Left);
            if (answer is null)
            {
                Assert.Equal(new HandoffRead("r", true, false), h.Read());
                Assert.True(h.Read().AlreadySent);
            }
            else
            {
                Assert.Equal(new HandoffRead("r", true, false), answer);
                Assert.True(h.Read().AlreadySent);
            }
        }
        Assert.True(cancelWon > 0, "the cancel never beat the waiter's continuation");
    }

    /// <summary>
    /// ONE OF TWO WAITERS IS CANCELLED AS THE RUN ENDS. The other receives it, so the cancelled one must
    /// not also leave it: exactly one of answered, sent, left.
    /// </summary>
    [Fact]
    public async Task OneOfTwoWaitersCancelledAtTheEnd_TheOtherHasIt_AndNothingIsLeft()
    {
        for (var i = 0; i < 500; i++)
        {
            var c = new Counter(); var h = new ResultHandoff(c.Leave);
            using var cts = new CancellationTokenSource();
            var cancelled = h.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);
            var other = h.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            await Task.WhenAll(Task.Run(() => h.Complete("r", c.Send)), Task.Run(cts.Cancel));

            var otherAnswered = (await other).Result == "r";
            var cancelledAnswered = false;
            try { cancelledAnswered = (await cancelled).Result == "r"; }
            catch (OperationCanceledException) { }

            if (otherAnswered)
            {
                Assert.Equal(0, c.Left);
                Assert.Equal(0, c.Sent);
            }
            Assert.Equal(1, (otherAnswered || cancelledAnswered ? 1 : 0) + c.Sent + c.Left);
        }
    }

    /// <summary>
    /// RANDOM INTERLEAVINGS: waits with random timeouts and cancels, reads, and the end, on many threads.
    /// Invariant: consumed exactly once. Waiters that all received it are one parent turn, so they count
    /// as one consumption.
    /// </summary>
    [Fact]
    public async Task TheHandoff_DeliversExactlyOnce_UnderRandomInterleaving() => await Soak(rounds: 300, seed: 20261016);

    [Fact(Skip = "soak — remove this Skip, then run --filter Category=Soak"), Trait("Category", "Soak")]
    public async Task TheHandoff_Soak() => await Soak(rounds: 2000, seed: 7);

    private static async Task Soak(int rounds, int seed)
    {
        var rng = new Random(seed);
        for (var round = 0; round < rounds; round++)
        {
            var c = new Counter(); var h = new ResultHandoff(c.Leave);
            var viaWaiters = 0;
            var actors = new List<Task>();
            var waiters = rng.Next(0, 4);
            for (var w = 0; w < waiters; w++)
            {
                var timeout = TimeSpan.FromMilliseconds(rng.Next(0, 4));
                var cts = new CancellationTokenSource();
                if (rng.Next(2) == 0) cts.CancelAfter(rng.Next(0, 4));
                actors.Add(Task.Run(async () =>
                {
                    try
                    {
                        if (await h.WaitAsync(timeout, cts.Token) is { Ended: true, AlreadySent: false })
                            Interlocked.Exchange(ref viaWaiters, 1);
                    }
                    catch (OperationCanceledException) { }
                }));
            }
            actors.Add(Task.Run(async () => { await Task.Delay(rng.Next(0, 3)); h.Complete("r", c.Send); }));
            await Task.WhenAll(actors);
            var viaRead = h.Read() is { Ended: true, AlreadySent: false } ? 1 : 0;

            Assert.Equal(1, Math.Max(viaWaiters, viaRead) + c.Sent + c.Left);
        }
    }
}
