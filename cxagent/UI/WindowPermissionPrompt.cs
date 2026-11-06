using CxAgent.Core.Permissions;
using SharpConsoleUI;

namespace CxAgent.UI;

/// <summary>
/// The terminal's permission gate.
///
/// <para>WHAT IS LEFT AFTER THE MOVE. The decision pipeline — silent policy, stored rules, the auto classifier and
/// its fail-closed behaviour, the trust floor, persistence — is <see cref="PermissionDecider"/> in Core. Asking is
/// <see cref="Hosting.PromptHub"/>'s: the terminal and any browser race to answer, one prompt per session at a
/// time, and <see cref="TuiPromptResponder"/> is how this window shows them. What stays here is where the gate's
/// own notices land, and what Escape means.</para>
/// </summary>
public static class WindowPermissionPrompt
{
    /// <summary>A gate that asks through the hub.</summary>
    public static PermissionDecider Gate(MainWindow mw,
        PermissionRulesStore store, ITranscriptWriter? transcript, Hosting.PromptHub prompts) =>
        // THE Message OVERLOAD, so the gate's own notices arrive with their severity intact rather
        // than as a pre-coloured sentence Core had to compose.
        PermissionDecider.WithPrompt(store,

            // INTO THE ASKING SESSION'S TRANSCRIPT. One gate serves every session, so a notice that
            // went to "the transcript" went to whichever the writer resolved — the active tab, and
            // before that a control captured at startup. A denial belongs in the history of the
            // conversation that was denied: filed anywhere else it accuses the wrong session and
            // leaves the right one with no record of what it was refused.
            //
            // NULL SESSION FALLS BACK TO THE ACTIVE TAB, which is the no-policy refusal — there is
            // no session to file it against, and in front of the user is the only place left.
            transcript is null ? null : (sessionId, message) =>
            {
                if (sessionId is not null && mw.TabForSessionId(sessionId) is { } tab)
                    ChatTranscriptSink.Post(tab.Chat, ChatTranscriptSink.Row(message));
                else
                    transcript.Write(message);
            },
            prompts.AskPermissionAsync);

    /// <summary>
    /// What Escape does on this prompt.
    ///
    /// <para>"NO" FOR THE MODEL'S OWN CALL, so the run continues and the model adapts to the refusal —
    /// see MainWindow.TryDenyPermission. A COMMAND'S LINE STOPS THE COMMAND: the user pressing Escape
    /// on a <c>!`…`</c> line they just set off means "don't send this", and denying the one line
    /// would still send the rest. Cancelling the turn resolves this prompt as Deny through its
    /// cancellation registration and hands the typed command back to the composer.</para>
    /// </summary>
    /// <param name="stopTurn">Cancels the asking session's turn; null when there is none to find.</param>
    public static Action EscapeAction(PermissionRequest request, Action deny, Action? stopTurn) =>
        request.RaisedByCommand && stopTurn is not null ? stopTurn : deny;
}
