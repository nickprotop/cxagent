using CxAgent.Core.Llm;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// <c>/model</c> — which configured instance the session talks to.
///
/// <para>The registry is real rather than mocked: it is what the command reads, and building one is
/// two lines. What is NOT here is the switch itself, which needs a window and a live agent.</para>
/// </summary>
public class ModelCommandTests
{
    private static ProviderRegistry Registry(params (string Name, string Model, int? Window)[] instances)
    {
        var providers = instances.ToDictionary(
            i => i.Name,
            i => (ILlmProvider)new MockLlmProvider(i.Model),
            StringComparer.Ordinal);

        var windows = instances.ToDictionary(i => i.Name, i => i.Window, StringComparer.Ordinal);

        return ProviderRegistry.FromProviders(providers, instances[0].Name, windows);
    }

    [Fact]
    public void BareCommand_ListsEveryInstanceWithItsModel()
    {
        var reply = ModelCommand.Decide("",
            Registry(("local", "qwen3", 213_000), ("claude", "claude-x", 200_000)), "local").Reply;

        Assert.Contains("local:qwen3", reply);
        Assert.Contains("claude:claude-x", reply);
    }

    /// <summary>
    /// THE WINDOW IS SHOWN because it is the thing that changes behaviour. Switching to a smaller
    /// model does not fail — the turn loop compacts before it sends — but a conversation that fitted
    /// now has to be summarised, and that is worth knowing before choosing.
    /// </summary>
    [Fact]
    public void BareCommand_ShowsTheContextWindow()
    {
        var reply = ModelCommand.Decide("", Registry(("local", "qwen3", 213_000)), "local").Reply;

        Assert.Contains("213k", reply);
    }

    [Fact]
    public void BareCommand_MarksTheOneInUse()
    {
        var reply = ModelCommand.Decide("",
            Registry(("local", "qwen3", 1000), ("claude", "claude-x", 1000)), "claude").Reply;

        Assert.Contains("in use", reply);
    }

    [Fact]
    public void ANameSwitches()
    {
        var result = ModelCommand.Decide("claude",
            Registry(("local", "qwen3", 1000), ("claude", "claude-x", 1000)), "local");

        Assert.Equal("claude", result.SwitchTo);
    }

    /// <summary>Instance names are short and user-chosen, so a prefix is the common way to type one.</summary>
    [Fact]
    public void AUniquePrefixSwitches()
    {
        var result = ModelCommand.Decide("cla",
            Registry(("local", "qwen3", 1000), ("claude", "claude-x", 1000)), "local");

        Assert.Equal("claude", result.SwitchTo);
    }

    /// <summary>An exact name wins over a prefix, so `claude` reaches claude and not claude-fast.</summary>
    [Fact]
    public void AnExactNameBeatsALongerOneItPrefixes()
    {
        var result = ModelCommand.Decide("claude",
            Registry(("claude", "a", 1000), ("claude-fast", "b", 1000)), "claude-fast");

        Assert.Equal("claude", result.SwitchTo);
    }

    /// <summary>
    /// AMBIGUITY IS REPORTED, NEVER RESOLVED — the same rule /sessions follows. Picking one silently
    /// is how a user spends a conversation on a model they did not choose.
    /// </summary>
    [Fact]
    public void AnAmbiguousPrefixNamesTheCandidates()
    {
        var result = ModelCommand.Decide("cl",
            Registry(("local", "a", 1000), ("claude", "b", 1000), ("cloud", "c", 1000)), "local");

        Assert.Null(result.SwitchTo);
        Assert.Contains("claude", result.Reply);
        Assert.Contains("cloud", result.Reply);
    }

    [Fact]
    public void AnUnknownNameListsWhatIsConfigured()
    {
        var result = ModelCommand.Decide("gpt", Registry(("local", "qwen3", 1000)), "local");

        Assert.Null(result.SwitchTo);
        Assert.Contains("local", result.Reply);
    }

    /// <summary>
    /// SWITCHING TO WHAT IS ALREADY IN USE IS NOT A SWITCH. Re-wiring would rebuild the agent for no
    /// change, and the user would have no way to tell that had happened.
    /// </summary>
    [Fact]
    public void SwitchingToTheCurrentOne_DoesNothing()
    {
        var result = ModelCommand.Decide("local", Registry(("local", "qwen3", 1000)), "local");

        Assert.Null(result.SwitchTo);
        Assert.Contains("Already using", result.Reply);
    }

    [Fact]
    public void WithNoProvidersConfigured_SaysSo()
    {
        var result = ModelCommand.Decide("", registry: null, current: null);

        Assert.Null(result.SwitchTo);
        Assert.Contains("No providers configured", result.Reply);
    }

    // --- what the switch says afterwards ---

    /// <summary>
    /// instance:model, EVERYWHERE THE UI NAMES A MODEL. The instance alone is ambiguous — two
    /// entries can serve the same model — and the model alone leaves "which of my providers is
    /// this?" unanswerable. It is also the exact string `/model &lt;name&gt;` takes.
    /// </summary>
    [Fact]
    public void Switched_NamesTheModelAndItsWindow()
    {
        var text = ModelCommand.Switched("claude", "claude-x", 200_000, 213_000, used: 1_000);

        Assert.Contains("claude:claude-x", text);
        Assert.Contains("200k", text);
        Assert.Contains("conversation is kept", text);
    }

    /// <summary>
    /// THE WARNING THAT EARNS ITS PLACE: a window smaller than what the conversation already occupies.
    /// Nothing breaks — the next turn compacts — but being told afterwards reads as a fault rather
    /// than a consequence of the choice just made.
    /// </summary>
    [Fact]
    public void Switched_WarnsWhenTheConversationAlreadyFillsTheNewWindow()
    {
        var text = ModelCommand.Switched("small", "tiny", 32_000, 213_000, used: 30_000);

        Assert.Contains("summarise", text);
        Assert.Contains("30k", text);
    }

    /// <summary>A roomy switch says nothing alarming — a line that always warns is one nobody reads.</summary>
    [Fact]
    public void Switched_DoesNotWarnWhenThereIsRoom()
    {
        var text = ModelCommand.Switched("big", "large", 200_000, 32_000, used: 1_000);

        Assert.DoesNotContain("summarise", text);
    }

    /// <summary>A smaller window is worth mentioning even when the conversation still fits.</summary>
    [Fact]
    public void Switched_NotesASmallerWindow()
    {
        var text = ModelCommand.Switched("small", "tiny", 32_000, 213_000, used: 100);

        Assert.Contains("Smaller window", text);
    }

    /// <summary>An unknown window changes how compaction behaves, so it is stated.</summary>
    [Fact]
    public void Switched_SaysWhenTheWindowIsUnknown()
    {
        var text = ModelCommand.Switched("odd", "mystery", null, 200_000, used: 100);

        Assert.Contains("fixed threshold", text);
    }

    // --- the palette ---

}
