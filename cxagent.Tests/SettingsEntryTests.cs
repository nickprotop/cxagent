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
        // The P14 drive reported "Escape no longer discards a draft" as a defect and blamed a missing
        // `finally`. Both halves were wrong, and this note stays so the next reader does not repeat
        // the diagnosis: the routing is a pure function of its inputs, and AppBootstrap DOES clear
        // the dialog flag in a finally (verified). What the drive actually observed is that Escape
        // does not clear typed COMPOSER TEXT -- which it never has, in any version.
        //
        // The old third state, DiscardPendingApproval, had been a NO-OP since the copilot draft gate
        // was deleted: Escape did nothing whenever no dialog was open, and nothing said so. It is now
        // "cancel the running turn", which is what a user pressing Escape actually wants.
        // ONE INPUT NOW. The dialog branch went with the Settings dialog itself; what Escape can
        // find in front of it is a running turn, or nothing. A permission prompt and a question are
        // both answered before this is reached — see the handler, which checks them first, so their
        // absence here is the routing staying a pure function rather than a gap.
        Assert.Equal(EscapeTarget.CancelTurn, EscapeRouting.For(turnIsRunning: true));
        Assert.Equal(EscapeTarget.Nothing,    EscapeRouting.For(turnIsRunning: false));
    }
}
