using CxAgent.Core.Llm;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

public class SettingsSessionTests
{
    private static ProviderSettings OneProvider(string name, string model) =>
        ProviderCatalogEditor.AddOrReplace(
            ProviderCatalogEditor.EmptyCatalog(), name,
            new ProviderInstanceConfig("openai-compatible", model, "k", "https://x.invalid/v1", null),
            makeDefault: true);

    /// <summary>Two provider instances, "first"/"second", with the built-in reviewer role bound to
    /// ("first", "m1") — the fixture CatalogEdit_ThenCompose_PreservesEveryRoleAndBinding needs to
    /// prove a provider-only edit doesn't disturb.</summary>

    [Fact]
    public void AnUntouchedSession_ComposesToNothing()
    {
        // TryCompose == null is what makes "Save with no edits" write nothing and re-wire nothing —
        // the `if (!dirty) return null` idiom both retired editors used.
        Assert.Null(new SettingsSession(OneProvider("first", "m1")).TryCompose());
    }

    [Fact]
    public void OrchestratorEdits_RideTheSameCompose()
    {
        var session = new SettingsSession(OneProvider("first", "m1"));
        session.UpdateOrchestrator(session.Working.Orchestrator with { MaxTurns = 4 });
        Assert.Equal(4, session.TryCompose()!.Orchestrator.MaxTurns);
    }

    [Fact]
    public void SettingTheSameValue_DoesNotMakeTheSessionDirty()
    {
        var session = new SettingsSession(OneProvider("first", "m1"));
        session.UpdateOrchestrator(session.Working.Orchestrator);   // no-op edit
        Assert.Null(session.TryCompose());
    }

    [Fact]
    public void ForLoad_RefusesAnInvalidConfig_RatherThanStartingFromAnEmptyCatalog()
    {
        // THE GUARD. An invalid load has Settings == null; the plain constructor turns null into
        // EmptyCatalog(); ProviderConfigWriter writes settings.Roles with no Count > 0 gate; so a
        // session built from an invalid load would, on Save, persist an empty roles list over the
        // user's real one. That is the defect ProviderCatalogEditor actually shipped once.
        //
        // SettingsEntry.Classify routes invalid -> repair wizard, so nothing reaches here TODAY --
        // but that is routing, one forgotten call away from the damage. This makes the refusal a
        // property of the TYPE.
        var invalid = ConfigLoad.Invalid(new[] { "bad defaultProvider" });

        var ex = Assert.Throws<InvalidOperationException>(() => SettingsSession.ForLoad(invalid));
        Assert.Contains("bad defaultProvider", ex.Message);   // the user is told WHY, not just "no"
    }

    [Fact]
    public void ForLoad_AllowsAnAbsentConfig_BecauseThereIsNothingToLose()
    {
        // ABSENT and INVALID both arrive as Settings == null and must NOT be collapsed. A genuine
        // first run has nothing on disk to overwrite, so building from an empty catalog is correct
        // there -- refusing would leave a new user unable to open settings at all.
        var session = SettingsSession.ForLoad(default);   // ConfigLoad default == absent

        Assert.NotNull(session.Working);
    }

    [Fact]
    public void ForLoad_PassesAValidConfigThrough_Unchanged()
    {
        var ok = ConfigLoad.Ok(OneProvider("first", "m1"));

        var session = SettingsSession.ForLoad(ok);

        Assert.Contains("first", session.Working.Providers.Keys);
        Assert.Null(session.TryCompose());   // and it starts CLEAN -- open+close is a true no-op
    }

}
