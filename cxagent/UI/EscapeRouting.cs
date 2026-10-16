namespace CxAgent.UI;

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
/// typed composer text, in any version of this app.</para>
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

    /// <summary>btw mode, the aside answering: stop that answer, and only that.</summary>
    StopAside,

    /// <summary>btw mode, the aside idle: the composer is the main agent's again.</summary>
    LeaveAside,
}

/// <summary>Whether the composer is in btw mode, and if so whether the aside is answering.</summary>
public enum AsideState { None, Idle, Answering }

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
    //
    // BTW MODE COMES BEFORE THE TURN. A user in btw mode is talking to the aside; an Esc that
    // cancelled the main run behind it would destroy work they were not even looking at. Still the
    // chat tab's key only, for the same reason a turn is.
    public static EscapeTarget For(bool turnIsRunning, bool chatTabIsActive, AsideState aside = AsideState.None) =>
        !chatTabIsActive ? EscapeTarget.Nothing
        : aside is AsideState.Answering ? EscapeTarget.StopAside
        : aside is AsideState.Idle ? EscapeTarget.LeaveAside
        : turnIsRunning ? EscapeTarget.CancelTurn
        : EscapeTarget.Nothing;
}
