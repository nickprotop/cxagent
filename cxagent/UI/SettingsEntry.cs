using CxAgent.Core.Llm;
using CxAgent.Core.Storage;

namespace CxAgent.UI;

/// <summary>
/// Where a settings entry point (F5/F7/F8) should go, once the on-disk config has been loaded.
/// </summary>
public enum SettingsRoute
{
    /// <summary>Open the consolidated Settings dialog on the requested page.</summary>
    OpenDialog,
    /// <summary>Run the first-run/repair setup wizard.</summary>
    RunWizard,
}

/// <summary>
/// The single classifier behind every settings entry point. Before this task the same decision was
/// made three separate times (old F7 roles, old F8 providers, the F5 setup flow) and two of the three
/// guards were byte-for-byte identical while the third was deliberately different — a state of affairs
/// that invited them to drift. Now stated once.
/// </summary>
public static class SettingsEntry
{
    // There is exactly ONE entry point now (F5), so an invalid or absent config always routes to the
    // repair wizard.
    //
    // This USED to take a `viaF5` flag, because F7/F8 opened editors that had to REFUSE on an invalid
    // load: ProviderCatalogEditor's EmptyCatalog() fallback has an empty Roles list, and its Done
    // persisted that emptiness over the user's real providers and roles. Those editors are deleted
    // and those keys are retired, so the refuse branch had no reachable caller — dead code guarding
    // against a class that no longer exists.
    //
    // Deleting it is safe in the direction that matters: refusing on the ONLY surface that can repair
    // a broken config would strand the user with no in-app fix. The wizard still warns that setup
    // starts fresh (AppBootstrap's IsInvalid message), so the destructive step is announced, not
    // silent. SettingsRoute.Refuse was this route's only producer, so it is deleted too rather than
    // left as an unreachable enum member nobody can explain later.
    public static SettingsRoute Classify(ConfigLoad load) =>
        load.IsInvalid || load.IsAbsent ? SettingsRoute.RunWizard
        : SettingsRoute.OpenDialog;

    /// <summary>
    /// The currently persisted configuration, or null when there is nothing safe to build on — the
    /// file does not exist (genuine first run) or the loader rejects it. Every editor entry point (F5
    /// wizard, F7 roles, F8 providers) loads through here rather than closing over startup state, so
    /// each one edits what is on disk NOW: without it, F5 replaced the whole catalog and destroyed
    /// every other instance and role binding the user had.
    /// </summary>
    internal static ConfigLoad LoadSettings(AppPaths paths, IReadOnlyDictionary<string, string> env)
    {
        // Checked BEFORE loading, because the loader reports a missing file as a ProviderConfigException
        // too — the same exception TYPE it uses for a file that exists but fails validation. The two
        // outcomes have opposite safety properties, so they are told apart by the filesystem rather
        // than by matching on an error message, which would break the moment that wording changed.
        if (!File.Exists(Path.Combine(paths.ConfigDir, "config.json")))
            return default;   // Absent: Settings null, Errors null.

        try
        {
            return ConfigLoad.Ok(ProviderConfigLoader.LoadAndValidate(paths, env));
        }
        catch (ProviderConfigException ex)
        {
            // The file EXISTS and still holds the user's providers, roles and bindings — it just does
            // not validate (a hand-edited defaultProvider that no longer resolves, say). Treating this
            // as "absent" is what makes an editor start from an empty catalog and then PERSIST that
            // emptiness over the real config: ProviderConfigWriter writes settings.Roles with no
            // Count > 0 gate (deliberately — see the ROLES INVARIANT), so an empty list is written as
            // "this user has no roles" rather than skipped. Same role-wipe defect this classifier
            // guards against, through a different door.
            return ConfigLoad.Invalid(ex.Errors);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable for an environmental reason. The contents are UNKNOWN, so this is equally not
            // safe to overwrite — a transient permissions or IO failure must not cost the user config.
            return ConfigLoad.Invalid(new[] { ex.Message });
        }
    }
}

/// <summary>
/// The outcome of reading config.json, keeping the three cases apart. <c>Absent</c> (genuine first
/// run — nothing to lose, editors may start from empty) must NOT be collapsed with <c>Invalid</c>
/// (a file that exists but does not load — its contents are still on disk and must not be
/// overwritten with a blank baseline).
/// </summary>
public readonly record struct ConfigLoad(ProviderSettings? Settings, IReadOnlyList<string>? Errors)
{
    public static ConfigLoad Ok(ProviderSettings s) => new(s, null);
    public static ConfigLoad Invalid(IReadOnlyList<string> errors) => new(null, errors);

    /// <summary>No config.json at all: a first run. Safe to build on an empty baseline.</summary>
    public bool IsAbsent => Settings is null && Errors is null;

    /// <summary>The file exists but did not load. Refuse to write over it.</summary>
    public bool IsInvalid => Errors is not null;

    public string ErrorText => string.Join("; ", Errors ?? Array.Empty<string>());
}

/// <summary>
/// Where an Escape keypress should go. Extracted from AppBootstrap's global-shortcut lambda so the
/// routing is testable: the live behaviour is otherwise only verifiable by hand, and the P14 drive
/// mis-diagnosed it precisely because there was nothing to assert against.
///
/// <para>Escape has THREE possible consumers and the order is not obvious. A portal (NavigationView's
/// hamburger overlay on a narrow terminal) swallows keys before anything here runs
/// (InputCoordinator.cs:115-118). Otherwise this global shortcut fires at InputCoordinator.cs:131,
/// BEFORE active-window routing at :150 — so an open dialog can never see Escape itself, and routing
/// it here is the only way Cancel is reachable at all.</para>
///
/// <para>What it does NOT do, stated because a drive expected otherwise: Escape has never cleared
/// typed composer text, in any version of this app. <c>DiscardDraft</c> resolves a pending COPILOT
/// approval (AgentHost.cs — a TaskCompletionSource), which is a different thing that happens to
/// share the key.</para>
/// </summary>
public enum EscapeTarget
{
    /// <summary>A Settings dialog is open: cancel it, discarding its unsaved working copy.</summary>
    CancelDialog,
    /// <summary>
    /// A turn is running: cancel it, leaving the session alive.
    ///
    /// <para>What Claude Code's Escape does, and what this app had no way to do at all — the only
    /// cancellation wired up was Ctrl+Q and /exit, which take the whole process down. A long shell
    /// command or a model that has started down the wrong path could not be stopped without losing
    /// the conversation.</para>
    /// </summary>
    CancelTurn,

    /// <summary>Nothing to cancel: Escape does nothing.</summary>
    Nothing,
}

public static class EscapeRouting
{
    /// <summary>
    /// THE DIALOG WINS while one is open. `dialogIsOpen` MUST become false the moment the dialog
    /// closes — AppBootstrap clears it in a `finally`, because a value left set here points Escape
    /// at a dead dialog for the rest of the session and no unit test above this line would notice.
    ///
    /// <para>Then a running turn. Escape is the one key a user presses when something is going
    /// wrong, and the ordering matters: cancelling a turn while a modal is open would leave them
    /// looking at a dialog they cannot dismiss.</para>
    ///
    /// <para>Otherwise NOTHING. This used to return DiscardPendingApproval unconditionally, which
    /// had been a no-op since the copilot draft gate was deleted — so Escape did nothing at all
    /// whenever no dialog was open, silently.</para>
    /// </summary>
    public static EscapeTarget For(bool dialogIsOpen, bool turnIsRunning = false) =>
        dialogIsOpen ? EscapeTarget.CancelDialog
        : turnIsRunning ? EscapeTarget.CancelTurn
        : EscapeTarget.Nothing;
}
