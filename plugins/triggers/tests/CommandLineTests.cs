using Xunit;

namespace CxAgent.Plugins.Triggers.Tests;

/// <summary>The `&lt;when&gt; &lt;prompt&gt;` grammar a person types, and the listing they read.</summary>
public class CommandLineTests
{
    [Fact]
    public void The_first_token_is_the_schedule_and_the_rest_is_the_prompt()
    {
        Assert.True(CommandLine.TrySplit("20m check whether the deploy finished",
            out var when, out var prompt, out _));

        Assert.Equal("20m", when);
        Assert.Equal("check whether the deploy finished", prompt);
    }

    /// <summary>
    /// THE QUOTES ARE LOAD-BEARING, because a cron line is the one form containing spaces. Without
    /// them there is no boundary between schedule and prompt.
    /// </summary>
    [Fact]
    public void A_quoted_cron_line_is_one_token()
    {
        Assert.True(CommandLine.TrySplit("\"0 9 * * 1-5\" post the standup reminder",
            out var when, out var prompt, out _));

        Assert.Equal("0 9 * * 1-5", when);
        Assert.Equal("post the standup reminder", prompt);
    }

    [Fact]
    public void A_line_with_no_prompt_is_refused()
    {
        Assert.False(CommandLine.TrySplit("20m", out _, out _, out var refusal));
        Assert.NotNull(refusal);
    }

    [Fact]
    public void An_empty_line_is_refused_naming_the_shape()
    {
        Assert.False(CommandLine.TrySplit("   ", out _, out _, out var refusal));
        Assert.Contains("when", refusal!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("20m", WhenKind.After)]
    [InlineData("2h", WhenKind.After)]
    [InlineData("0 9 * * 1-5", WhenKind.Every)]
    public void The_three_shapes_are_told_apart_without_being_named(string text, WhenKind expected)
    {
        Assert.True(CommandLine.TryReadWhen(text, out var when, out var refusal), refusal);
        Assert.Equal(expected, when!.Kind);
    }

    // A COMPUTED FUTURE MOMENT, NOT A LITERAL: `When.TryParse` refuses an "at" already in the
    // past, so a fixed date like "2026-09-10 09:00" goes from passing to refused the instant the
    // wall clock crosses it — a test that starts failing on its own merely by the calendar turning.
    [Fact]
    public void An_at_shape_is_told_apart_without_being_named()
    {
        var text = DateTimeOffset.Now.AddDays(1).ToString("yyyy-MM-dd HH:mm");

        Assert.True(CommandLine.TryReadWhen(text, out var when, out var refusal), refusal);
        Assert.Equal(WhenKind.At, when!.Kind);
    }

    /// <summary>
    /// AN AMBIGUOUS TOKEN IS REFUSED NAMING ALL THREE FORMS, never picked between — the refusal is
    /// immediate and on screen, which is the difference that makes sniffing safe here.
    /// </summary>
    [Fact]
    public void Something_that_is_none_of_the_three_is_refused_showing_each()
    {
        Assert.False(CommandLine.TryReadWhen("sometime", out _, out var refusal));

        Assert.Contains("20m", refusal!);
        Assert.Contains("09:00", refusal!);
        Assert.Contains("cron", refusal!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_empty_listing_says_so()
    {
        Assert.Contains("no triggers", CommandLine.Render([]),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// ONE LINE PER TRIGGER, AND ONLY THE FIRST LINE OF A PROMPT — enough to decide what to cancel
    /// without printing a paragraph per row.
    /// </summary>
    [Fact]
    public void A_listing_shows_one_line_each_with_the_prompts_first_line()
    {
        When.TryParse("20m", null, null, out var when, out _);
        var trigger = new Trigger(3, "s", when!, "check the deploy\nand then the logs",
            DateTimeOffset.Now.AddMinutes(20));

        var rendered = CommandLine.Render([trigger]);

        Assert.Contains("3", rendered);
        Assert.Contains("check the deploy", rendered);
        Assert.DoesNotContain("and then the logs", rendered);
    }
}
