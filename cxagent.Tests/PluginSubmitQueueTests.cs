using CxAgent.Core.Plugins;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The bounded queue a plugin's mid-turn submit lands in until its session goes idle.
///
/// <para>THE RULE UNDER TEST IS FIFO ORDER, A HARD DEPTH LIMIT, AND PER-PLUGIN SEVER: goals drain in
/// the order they were submitted, a plugin looping on submit is refused once the queue is full rather
/// than growing it without bound, and dropping one plugin's queued goals must never touch another's.</para>
/// </summary>
public class PluginSubmitQueueTests
{
    [Fact]
    public void A_queue_holds_goals_in_order()
    {
        var queue = new PluginSubmitQueue();
        Assert.True(queue.TryEnqueue("a", "first", out _));
        Assert.True(queue.TryEnqueue("a", "second", out _));

        Assert.Equal("first", queue.DrainOne()?.Goal);
        Assert.Equal("second", queue.DrainOne()?.Goal);
        Assert.Null(queue.DrainOne());
    }

    [Fact]
    public void A_plugin_submitting_in_a_loop_is_refused_rather_than_growing_the_queue()
    {
        var queue = new PluginSubmitQueue();
        for (var i = 0; i < PluginSubmitQueue.MaxDepth; i++)
            Assert.True(queue.TryEnqueue("a", $"goal {i}", out _));

        Assert.False(queue.TryEnqueue("a", "one too many", out var refusal));
        Assert.Contains("queue is full", refusal);
    }

    [Fact]
    public void Severing_a_plugin_drops_only_that_plugins_queued_goals()
    {
        var queue = new PluginSubmitQueue();
        queue.TryEnqueue("a", "a's goal", out _);
        queue.TryEnqueue("b", "b's goal", out _);

        Assert.Equal(1, queue.DropFrom("a"));
        Assert.Equal("b's goal", queue.DrainOne()?.Goal);
        Assert.Null(queue.DrainOne());
    }
}
