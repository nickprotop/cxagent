using CxAgent.Core.Llm;

namespace CxAgent.UI;

/// <summary>
/// The Settings dialog's working copy: ONE load at open, edited in memory by every page, persisted
/// by exactly one Save. Nothing here touches disk.
///
/// This replaces three independent editors (F5 wizard, F7 roles, F8 providers) that each loaded,
/// saved, and re-wired the live session on their own. A single working copy with a single Save
/// means <see cref="TryCompose"/> can answer "did anything actually change" ONCE, for the whole
/// dialog — and that answer is the guard against a needless WireRunner call, which tears down the
/// outgoing runner's schedulers and would kill a running goal's scheduler. Opening the dialog and
/// dismissing it without touching anything must be a true no-op.
/// </summary>
public sealed class SettingsSession
{
    /// <summary>The working copy. Roles used to be tracked separately and folded back in at
    /// Snapshot/TryCompose time; they are gone, so this is the whole of it.</summary>
    public ProviderSettings Working { get; private set; }

    public bool Dirty { get; private set; }

    /// <summary>
    /// <paramref name="loaded"/> is the on-disk catalog, or null when config.json is ABSENT (genuine
    /// first run, nothing to lose) — that state becomes an empty catalog with the built-in roles
    /// seeded, the same starting point <see cref="RoleEditor.FromSettings"/> gives any settings
    /// object whose Roles list is empty.
    ///
    /// <para>Use <see cref="ForLoad"/> instead when you hold a <see cref="ConfigLoad"/>. This
    /// constructor cannot tell ABSENT from INVALID — both arrive as null, and they have opposite
    /// safety properties: absent is safe to build on, invalid means the user's providers, roles and
    /// bindings are still on disk and must not be overwritten with a blank baseline.</para>
    /// </summary>
    public SettingsSession(ProviderSettings? loaded)
    {
        Working = loaded ?? ProviderCatalogEditor.EmptyCatalog();
    }

    /// <summary>
    /// Builds a session from a <see cref="ConfigLoad"/>, REFUSING an invalid one.
    ///
    /// <para>This is the guard that used to live in three separate dialog handlers as
    /// "if (load.IsInvalid) refuse". Those handlers are gone, and `SettingsEntry.Classify` now routes
    /// an invalid load to the repair wizard instead — but that is ROUTING, one call away from the
    /// damage, and the damage is unchanged: an invalid load has Settings == null, the plain
    /// constructor turns null into <c>EmptyCatalog()</c>, and Save writes <c>settings.Roles</c> with
    /// no Count &gt; 0 gate (deliberately — see the ROLES INVARIANT in ProviderConfigWriter). So a
    /// session built from an invalid load, if one ever reached Save, would persist an empty roles
    /// list over the user's real one. That is the exact defect ProviderCatalogEditor once shipped.</para>
    ///
    /// <para>Refusing HERE makes the guard a property of the type rather than of the caller, so a
    /// future entry point (a `/settings` command, a restored deep link) cannot reintroduce it by
    /// forgetting to classify first.</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">The config exists but did not load.</exception>
    public static SettingsSession ForLoad(ConfigLoad load) =>
        load.IsInvalid
            ? throw new InvalidOperationException(
                "Refusing to open settings on a config that exists but did not load — saving would "
                + "overwrite the providers, roles and bindings still on disk. Errors: " + load.ErrorText)
            : new SettingsSession(load.Settings);

    /// <summary>Applies the result of a pure catalog transform (AddOrReplace/RemoveInstance/SetDefault).
    /// Dirty only on an actual value change — record equality — so retyping the same value doesn't
    /// turn a Cancel-equivalent session into a writer.</summary>
    public void UpdateCatalog(ProviderSettings updated)
    {
        if (updated == Working) return;
        Working = updated;
        Dirty = true;
    }

    public void UpdateOrchestrator(OrchestratorSettings o)
    {
        if (o == Working.Orchestrator) return;
        Working = Working with { Orchestrator = o };
        Dirty = true;
    }

    /// <summary>Folds the live role registry back into <see cref="Working"/>. This is both the
    /// baseline handed to the setup wizard as `existing` (so a wizard merge lands on THIS session's
    /// unsaved edits, not on disk) and the input Save composes from.</summary>
    public ProviderSettings Snapshot() => Working;

    /// <summary>Absorbs a merged catalog produced by the wizard (built from <see cref="Snapshot"/>
    /// as its baseline). Re-deriving Roles from the merged settings is safe precisely because that
    /// baseline already had this session's roles folded in — the wizard preserves what it didn't
    /// touch.</summary>
    public void AbsorbWizardResult(ProviderSettings merged)
    {
        Working = merged;
        Dirty = true;
    }

    /// <summary>Null when nothing changed — the `if (!dirty) return null` idiom both retired editors
    /// used, so Save writes nothing and WireRunner is never called for a no-op dialog visit.</summary>
    public ProviderSettings? TryCompose() => Dirty ? Snapshot() : null;
}
