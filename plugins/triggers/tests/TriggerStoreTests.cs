using Xunit;

namespace CxAgent.Plugins.Triggers.Tests;

/// <summary>
/// The static every session's plugin instance shares — and the session scoping that keeps one
/// session's wake out of another's client.
/// </summary>
[Collection("TriggerStore")]
public class TriggerStoreTests : IDisposable
{
    private readonly string _a = "session-" + Guid.NewGuid().ToString("N");
    private readonly string _b = "session-" + Guid.NewGuid().ToString("N");

    public void Dispose()
    {
        TriggerStore.SweepSession(_a);
        TriggerStore.SweepSession(_b);
    }

    private static When Wake(string after)
    {
        Assert.True(When.TryParse(after, null, null, out var when, out _));
        return when!;
    }

    /// <summary>
    /// A SMALL INTEGER, SCOPED PER SESSION, because a human retypes it from a listing at
    /// /triggers-cancel 3. Numbering restarts per session so a user never sees another
    /// conversation's numbers in their own list.
    /// </summary>
    [Fact]
    public void Ids_start_at_one_and_count_up_within_a_session()
    {
        Assert.Equal(1, TriggerStore.Add(_a, Wake("20m"), "first").Id);
        Assert.Equal(2, TriggerStore.Add(_a, Wake("30m"), "second").Id);
    }

    /// <summary>
    /// WHICH MAKES THE KEY A PAIR, NOT AN ID. The static is process-wide and holds every session's,
    /// so two sessions each holding trigger 1 collide the moment anything keys on the number alone.
    /// </summary>
    [Fact]
    public void Two_sessions_each_get_their_own_number_one()
    {
        var first = TriggerStore.Add(_a, Wake("20m"), "mine");
        var second = TriggerStore.Add(_b, Wake("20m"), "theirs");

        Assert.Equal(1, first.Id);
        Assert.Equal(1, second.Id);
        Assert.Equal("mine", TriggerStore.For(_a).Single().Prompt);
        Assert.Equal("theirs", TriggerStore.For(_b).Single().Prompt);
    }

    [Fact]
    public void Cancelling_removes_only_that_sessions_trigger()
    {
        TriggerStore.Add(_a, Wake("20m"), "mine");
        TriggerStore.Add(_b, Wake("20m"), "theirs");

        Assert.True(TriggerStore.Cancel(_a, 1));

        Assert.Empty(TriggerStore.For(_a));
        Assert.Single(TriggerStore.For(_b));
    }

    [Fact]
    public void Cancelling_a_number_nobody_holds_answers_false()
    {
        Assert.False(TriggerStore.Cancel(_a, 99));
    }

    /// <summary>
    /// UPDATE KEEPS THE ID, which is what makes it different from cancel-and-recreate: a recreated
    /// trigger loses the number a user already has in front of them.
    /// </summary>
    [Fact]
    public void Updating_replaces_the_schedule_and_prompt_but_keeps_the_id()
    {
        TriggerStore.Add(_a, Wake("20m"), "old");

        var updated = TriggerStore.Update(_a, 1, Wake("2h"), "new");

        Assert.NotNull(updated);
        Assert.Equal(1, updated!.Id);
        Assert.Equal("new", updated.Prompt);
        Assert.Single(TriggerStore.For(_a));
    }

    /// <summary>
    /// SWEPT BY SESSION, NOT EMPTIED. One plugin instance unwiring must not cancel another session's
    /// triggers — the static is process-wide and holds every session's.
    /// </summary>
    [Fact]
    public void Sweeping_one_session_leaves_the_others_running()
    {
        TriggerStore.Add(_a, Wake("20m"), "mine");
        TriggerStore.Add(_b, Wake("20m"), "theirs");

        TriggerStore.SweepSession(_a);

        Assert.Empty(TriggerStore.For(_a));
        Assert.Single(TriggerStore.For(_b));
    }

    [Fact]
    public void Only_triggers_whose_moment_has_come_are_due()
    {
        TriggerStore.Add(_a, Wake("1s"), "soon");
        TriggerStore.Add(_a, Wake("1d"), "later");

        var due = TriggerStore.Due(DateTimeOffset.Now.AddMinutes(1))
            .Where(t => t.SessionId == _a).ToList();

        Assert.Single(due);
        Assert.Equal("soon", due[0].Prompt);
    }

    /// <summary>
    /// A ONE-SHOT IS SPENT WHEN IT FIRES; a cron line is not. Rescheduling is what keeps a recurring
    /// trigger in the list without minting a new id for it.
    /// </summary>
    [Fact]
    public void A_one_shot_is_gone_after_firing_and_a_cron_line_is_not()
    {
        var once = TriggerStore.Add(_a, Wake("1s"), "once");
        Assert.True(When.TryParse(null, null, "* * * * *", out var cron, out _));
        var repeating = TriggerStore.Add(_a, cron!, "repeating");

        TriggerStore.Reschedule(once, DateTimeOffset.Now.AddMinutes(1));
        TriggerStore.Reschedule(repeating, DateTimeOffset.Now.AddMinutes(1));

        var left = TriggerStore.For(_a);
        Assert.Single(left);
        Assert.Equal("repeating", left[0].Prompt);
    }
}

[CollectionDefinition("TriggerStore", DisableParallelization = true)]
public class TriggerStoreCollection;
