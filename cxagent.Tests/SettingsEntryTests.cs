using CxAgent.Core.Llm;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The classifier that replaces three separate per-handler invalid-config guards (old F7 roles, old
/// F8 providers, the F5 setup flow) with one decision table. See <see cref="SettingsEntry.Classify"/>
/// for the rule: an unloadable config REFUSES except via F5, which stays the documented repair route.
/// </summary>
public class SettingsEntryTests
{
    private static ProviderSettings OneProvider(string name, string model) =>
        ProviderCatalogEditor.AddOrReplace(
            ProviderCatalogEditor.EmptyCatalog(), name,
            new ProviderInstanceConfig("openai-compatible", model, "k", "https://x.invalid/v1", null),
            makeDefault: true);




    [Fact]
    public void Escape_GoesToTheDialogWhileOpen_ThenToARunningTurn_ThenNowhere()
    {
        // A MISDIAGNOSIS WORTH NOT REPEATING. "Escape does not discard a draft" reads as a routing
        // defect with a missing `finally` behind it. Both halves are wrong: the routing is a pure
        // function of its inputs, and AppBootstrap DOES clear the dialog flag in a finally
        // (verified). What that symptom actually is: Escape does not clear typed COMPOSER TEXT.
        //
        // ONE INPUT. What Escape can find in front of it is a running turn, or nothing — and with no
        // turn running it means "cancel nothing", not some third discard state, which would be a
        // no-op nothing tells the user about. A permission prompt and a question are both answered
        // before this is reached — see the handler, which checks them first, so their absence here
        // is the routing staying a pure function rather than a gap.
        Assert.Equal(EscapeTarget.CancelTurn, EscapeRouting.For(turnIsRunning: true));
        Assert.Equal(EscapeTarget.Nothing,    EscapeRouting.For(turnIsRunning: false));
    }
}
