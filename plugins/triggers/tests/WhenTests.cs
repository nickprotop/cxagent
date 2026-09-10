using Xunit;

namespace CxAgent.Plugins.Triggers.Tests;

/// <summary>
/// The three grammars, and the refusals. A wrong guess here schedules something nobody asked for and
/// is noticed when it fires — which is exactly when it is least welcome.
/// </summary>
public class WhenTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("30s", 30)]
    [InlineData("90m", 90 * 60)]
    [InlineData("2h", 2 * 60 * 60)]
    [InlineData("1d", 24 * 60 * 60)]
    public void A_duration_is_an_integer_and_one_unit(string text, int expectedSeconds)
    {
        Assert.True(When.TryParseDuration(text, out var value));
        Assert.Equal(expectedSeconds, (int)value.TotalSeconds);
    }

    /// <summary>
    /// NO COMPOUND FORMS. Accepting "1h30m" means owning a parser for every ordering somebody might
    /// write, and the refusal is immediate and on screen where a wrong parse would not be.
    /// </summary>
    [Theory]
    [InlineData("1h30m")]
    [InlineData("")]
    [InlineData("soon")]
    [InlineData("30")]
    [InlineData("m30")]
    [InlineData("-5m")]
    public void Anything_else_is_not_a_duration(string text)
    {
        Assert.False(When.TryParseDuration(text, out _));
    }

    [Fact]
    public void Exactly_one_field_must_be_set()
    {
        Assert.False(When.TryParse(null, null, null, out _, out var none));
        Assert.Contains("exactly one", none!, StringComparison.OrdinalIgnoreCase);

        Assert.False(When.TryParse("20m", "2026-09-10 09:00", null, out _, out var two));
        Assert.Contains("exactly one", two!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_after_fires_that_far_from_now()
    {
        Assert.True(When.TryParse("20m", null, null, out var when, out _));

        Assert.Equal(Now.AddMinutes(20), when!.NextAfter(Now));
        Assert.False(when.Repeats);
    }

    /// <summary>
    /// LOCAL TIME, AND NAMING THAT IS THE POINT. A person writing "09:00" means their own morning,
    /// and a scheduler that quietly read it as UTC would fire correctly-by-the-spec at the wrong hour
    /// of somebody's day.
    /// </summary>
    [Fact]
    public void An_at_is_read_as_local_time_unless_it_carries_an_offset()
    {
        // A LITERAL FAR IN THE FUTURE, DELIBERATELY: the past-check below runs against the real
        // clock at test time, not against `Now` above, so a same-day literal goes stale the moment
        // the wall clock passes it.
        Assert.True(When.TryParse(null, "2099-09-10 09:00", null, out var local, out _));
        Assert.Equal(TimeSpan.Zero, local!.At!.Value.Offset - local.At.Value.Offset);

        Assert.True(When.TryParse(null, "2099-09-10T09:00:00+02:00", null, out var explicitOffset, out _));
        Assert.Equal(TimeSpan.FromHours(2), explicitOffset!.At!.Value.Offset);
    }

    /// <summary>
    /// A ONE-SHOT ALREADY PAST HAS NO NEXT FIRE, and saying so at the CALL is what makes it useful:
    /// "that was 40 minutes ago" helps immediately and helps not at all tomorrow.
    /// </summary>
    [Fact]
    public void A_time_already_past_is_refused_at_the_call()
    {
        Assert.False(When.TryParse(null, "2020-01-01 09:00", null, out _, out var refusal));
        Assert.Contains("past", refusal!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_cron_line_repeats_and_answers_its_next_occurrence()
    {
        Assert.True(When.TryParse(null, null, "0 9 * * *", out var when, out _));

        Assert.True(when!.Repeats);
        var next = when.NextAfter(Now)!.Value;
        Assert.Equal(9, next.Hour);
        Assert.True(next > Now);
    }

    /// <summary>
    /// A BAD EXPRESSION IS REFUSED WHEN IT IS WRITTEN, never when it fires. A scheduler that stored
    /// an unparseable line and discovered it at 09:00 has turned a typo into silence.
    /// </summary>
    [Fact]
    public void A_malformed_cron_line_is_refused_with_its_error()
    {
        Assert.False(When.TryParse(null, null, "not a cron line", out _, out var refusal));
        Assert.NotNull(refusal);
    }

    [Fact]
    public void A_description_says_which_kind_it_is()
    {
        When.TryParse("20m", null, null, out var after, out _);
        When.TryParse(null, null, "0 9 * * 1-5", out var every, out _);

        Assert.Contains("20m", after!.Describe());
        Assert.Contains("0 9 * * 1-5", every!.Describe());
    }
}
