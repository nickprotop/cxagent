using CxAgent.Core.Agents;
using Xunit;

namespace CxAgent.Tests;

/// <summary>Order, bound, and that draining empties.</summary>
public class AgentMailboxTests
{
    [Fact]
    public void Messages_come_back_in_the_order_they_were_sent()
    {
        var box = new AgentMailbox();
        box.TryEnqueue("first", out _);
        box.TryEnqueue("second", out _);

        Assert.Equal(new[] { "first", "second" }, box.Drain().ToArray());
    }

    /// <summary>
    /// ALL OF IT AT ONCE: two corrections sent together are one thought, and delivering them a turn
    /// apart would let the agent act on half of it.
    /// </summary>
    [Fact]
    public void Draining_empties_the_mailbox()
    {
        var box = new AgentMailbox();
        box.TryEnqueue("only", out _);

        Assert.Single(box.Drain());
        Assert.Empty(box.Drain());
    }

    [Fact]
    public void An_empty_mailbox_drains_to_nothing_rather_than_throwing()
    {
        Assert.Empty(new AgentMailbox().Drain());
    }

    [Fact]
    public void A_full_mailbox_refuses_with_a_reason_the_caller_can_report()
    {
        var box = new AgentMailbox();
        for (var i = 0; i < AgentMailbox.MaxDepth; i++)
            Assert.True(box.TryEnqueue($"m{i}", out _));

        Assert.False(box.TryEnqueue("one too many", out var refusal));
        Assert.Contains("full", refusal!);
    }

    [Fact]
    public void Space_frees_up_once_it_is_drained()
    {
        var box = new AgentMailbox();
        for (var i = 0; i < AgentMailbox.MaxDepth; i++) box.TryEnqueue($"m{i}", out _);
        box.Drain();

        Assert.True(box.TryEnqueue("room now", out _));
    }

    /// <summary>
    /// A WITHDRAWAL TAKES THE ONE MESSAGE IT NAMES, by the very string that was sent: an equal text from
    /// somebody else is a different message, and taking it would lose that sender's words.
    /// </summary>
    [Fact]
    public void Withdrawing_takes_only_that_message_and_keeps_the_rest_in_order()
    {
        var box = new AgentMailbox();
        var report = string.Concat("the ", "report");
        box.TryEnqueue("before", out _);
        box.TryEnqueue(report, out _);
        box.TryEnqueue("after", out _);

        Assert.True(box.TryWithdraw(report));
        Assert.Equal(new[] { "before", "after" }, box.Drain().ToArray());
    }

    [Fact]
    public void An_equal_text_that_was_not_the_one_sent_is_not_withdrawn()
    {
        var box = new AgentMailbox();
        box.TryEnqueue(string.Concat("the ", "report"), out _);

        Assert.False(box.TryWithdraw(string.Concat("the ", "report")));
        Assert.Single(box.Drain());
    }

    [Fact]
    public void Withdrawing_what_was_already_drained_says_so()
    {
        var box = new AgentMailbox();
        var report = string.Concat("the ", "report");
        box.TryEnqueue(report, out _);
        box.Drain();

        Assert.False(box.TryWithdraw(report));
    }
}
