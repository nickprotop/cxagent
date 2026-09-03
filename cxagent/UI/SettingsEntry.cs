using CxAgent.Core.Llm;
using CxAgent.Core.Storage;

namespace CxAgent.UI;

/// <summary>
/// Reading config.json for the one caller that still edits it.
///
/// <para>ONE CALLER, THE WIZARD. There are deliberately no in-app editors for config beside it:
/// config is not applied in place, so an editor would write a file and ask for a restart — a job a
/// text editor does better, over a file the user can open directly. What an editor CANNOT replace
/// is the FIRST run, where there is no file to open and no schema to guess, which is why the wizard
/// exists and why this classifier exists with it.</para>
/// </summary>
public static class SettingsEntry
{
    /// <summary>
    /// The currently persisted configuration, or null when there is nothing safe to build on — the
    /// file does not exist (genuine first run) or the loader rejects it. The wizard loads through
    /// here rather than closing over startup state, so it appends to what is on disk NOW: without
    /// it, a wizard run replaced the whole catalog and destroyed every other instance and role
    /// binding the user had.
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
    /// <para>Otherwise NOTHING, said explicitly. A catch-all target that no handler acts on is the
    /// same as this in behaviour but not in readability: Escape then does nothing whenever no dialog
    /// is open, and nothing in the code says so.</para>
    /// </summary>
    // TWO INPUTS. A prompt or a question is answered before this is reached — see the handler, which
    // checks those first — so what is left is a running turn, and whether Escape is the chat tab's to
    // spend. It is not: a shell tab runs the user's own programs and a file tab holds a buffer, and
    // both want the key for themselves. Pressing Escape at a vim inside a terminal tab must not kill
    // the run behind it.
    //
    // NOTHING BECOMES UNREACHABLE. The waiting bar shows a turn running from any tab and F4 returns
    // to chat, so the way to cancel is one key away and visible from where the user is standing.
    public static EscapeTarget For(bool turnIsRunning, bool chatTabIsActive) =>
        turnIsRunning && chatTabIsActive ? EscapeTarget.CancelTurn : EscapeTarget.Nothing;
}
