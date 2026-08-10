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
    public void AnUnloadableConfig_RoutesToTheRepairWizard_NotIntoTheDialog()
    {
        // The role-wipe guard, restated for a ONE-ENTRY-POINT world. It used to be "refuse via
        // F7/F8, repair via F5", but F7/F8 are retired: the dialog is now reachable only through
        // F5, so the refuse branch had no caller. What still must hold is that an unloadable config
        // never reaches the DIALOG -- the dialog would build a session from a null/empty baseline
        // and Save would persist that emptiness over the providers, roles and bindings still on
        // disk (ProviderConfigWriter writes Roles with no Count > 0 gate -- see the ROLES INVARIANT).
        var invalid = ConfigLoad.Invalid(new[] { "bad defaultProvider" });
        Assert.Equal(SettingsRoute.RunWizard, SettingsEntry.Classify(invalid));
        Assert.NotEqual(SettingsRoute.OpenDialog, SettingsEntry.Classify(invalid));
    }

    [Fact]
    public void AnAbsentConfig_RunsFirstRunSetup()
    {
        // Genuine first run: nothing on disk to lose, and an empty dialog would be a bad first
        // impression -- the wizard teaches. (This is the user's own ruling on onboarding.)
        Assert.Equal(SettingsRoute.RunWizard, SettingsEntry.Classify(default));
    }

    [Fact]
    public void AValidConfig_OpensTheDialog()
    {
        var ok = ConfigLoad.Ok(OneProvider("first", "m1"));
        Assert.Equal(SettingsRoute.OpenDialog, SettingsEntry.Classify(ok));
    }

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
        Assert.Equal(EscapeTarget.CancelDialog, EscapeRouting.For(dialogIsOpen: true));
        Assert.Equal(EscapeTarget.CancelTurn,   EscapeRouting.For(dialogIsOpen: false, turnIsRunning: true));
        Assert.Equal(EscapeTarget.Nothing,      EscapeRouting.For(dialogIsOpen: false, turnIsRunning: false));
    }

    /// <summary>
    /// THE DIALOG WINS even mid-turn. Escape is the key someone presses when something is going
    /// wrong, and cancelling the turn underneath an open modal would leave them looking at a dialog
    /// they cannot dismiss.
    /// </summary>
    [Fact]
    public void Escape_WithBothADialogAndARunningTurn_CancelsTheDialog()
    {
        Assert.Equal(EscapeTarget.CancelDialog,
            EscapeRouting.For(dialogIsOpen: true, turnIsRunning: true));
    }
}
