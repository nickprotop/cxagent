using CxAgent.Core.Agents;
using Xunit;

namespace CxAgent.Tests;

/// <summary>Naming and the busy claim — the two rules the reach tools depend on.</summary>
public class SubAgentStoreTests
{
    [Fact]
    public void A_description_becomes_a_slug()
    {
        Assert.Equal("review-the-auth-code", SubAgentStore.Slug("Review the auth code", []));
    }

    [Fact]
    public void A_colliding_name_is_suffixed_rather_than_replacing_what_is_there()
    {
        Assert.Equal("review-auth-2", SubAgentStore.Slug("review auth", ["review-auth"]));
    }

    [Fact]
    public void A_second_collision_counts_on()
    {
        Assert.Equal("review-auth-3",
            SubAgentStore.Slug("review auth", ["review-auth", "review-auth-2"]));
    }

    [Fact]
    public void An_empty_description_still_yields_a_usable_name()
    {
        Assert.False(string.IsNullOrWhiteSpace(SubAgentStore.Slug(null, [])));
    }

    [Fact]
    public void Punctuation_folds_to_single_hyphens_rather_than_runs()
    {
        Assert.Equal("fix-the-parser", SubAgentStore.Slug("Fix — the  parser!!", []));
    }

    [Fact]
    public void A_long_description_is_capped_so_the_handle_stays_retypable()
    {
        var name = SubAgentStore.Slug(new string('a', 200), []);

        Assert.True(name.Length <= 40, $"name was {name.Length} characters");
    }

    [Fact]
    public void Busy_is_false_for_an_agent_nobody_is_sending_to()
    {
        Assert.False(new SubAgentStore().IsBusy("anything"));
    }

    [Fact]
    public void A_second_send_to_a_busy_agent_is_refused_and_the_claim_is_released()
    {
        var store = new SubAgentStore();

        Assert.True(store.TryBeginSend("a"));
        Assert.False(store.TryBeginSend("a"));

        store.EndSend("a");
        Assert.True(store.TryBeginSend("a"));
    }

    [Fact]
    public void Finding_a_name_nobody_kept_answers_null()
    {
        Assert.Null(new SubAgentStore().Find("ghost"));
    }

    [Fact]
    public void An_empty_store_lists_nothing()
    {
        Assert.Empty(new SubAgentStore().All());
    }
}
