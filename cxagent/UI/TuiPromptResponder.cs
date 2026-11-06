using System.Collections.Concurrent;
using CxAgent.Core.Commands;
using CxAgent.Hosting;
using SharpConsoleUI;

namespace CxAgent.UI;

/// <summary>
/// The terminal's side of <see cref="PromptHub"/>: every prompt shown with the controls the terminal has always
/// used, and taken down again when a browser answered it first.
///
/// <para>EVERY SESSION. The terminal is the window every session in this process belongs to, so it shows every
/// prompt — the same as before a browser could answer any of them.</para>
///
/// <para>AN ANSWER FROM HERE GOES THROUGH THE HUB like anyone's. A click that lands after a browser already answered
/// loses quietly; the line the transcript gets says who did answer.</para>
/// </summary>
public sealed class TuiPromptResponder(ConsoleWindowSystem system, MainWindow mw, PromptHub hub) : IPromptResponder
{
    /// <summary>What an answer given in the terminal is filed as.</summary>
    public const string Terminal = "terminal";

    private readonly ConcurrentDictionary<string, string> _sessions = new();

    /// <inheritdoc/>
    public bool Serves(string sessionId) => true;

    /// <inheritdoc/>
    public void Show(PendingPrompt prompt, CancellationToken resolved)
    {
        _sessions[prompt.Id] = prompt.SessionId;
        switch (prompt)
        {
            case PermissionPending p:
                ShowPermission(p, resolved);
                break;
            case QuestionPending q:
                ShowQuestion(q, resolved);
                break;
            case TrustPending t:
                ShowTrust(t, resolved);
                break;
        }
    }

    /// <inheritdoc/>
    public void Resolved(string promptId, Resolution resolution)
    {
        if (!_sessions.TryRemove(promptId, out var sessionId)) return;
        if (resolution.Answer is null || resolution.AnsweredBy == Terminal) return;

        // SAID IN THE SESSION'S OWN TAB, so someone watching the terminal sees why the prompt vanished.
        if (mw.TabForSessionId(sessionId) is { } tab)
            system.EnqueueOnUIThread(
                () => ChatTranscriptSink.Post(tab.Chat, ChatTranscriptSink.Row(
                    new Message($"Answered on the web by {Md.Escape(resolution.AnsweredBy)}: {Describe(resolution.Answer)}"))),
                "tuiPromptResponder.Resolved");
    }

    private static string Describe(PromptAnswer answer) => answer switch
    {
        PermissionAnswer { Choice: var c } => c switch
        {
            Core.Permissions.PermissionChoice.Once => "allowed once",
            Core.Permissions.PermissionChoice.Always => "always allowed",
            Core.Permissions.PermissionChoice.TrustFolder => "trusted the folder",
            _ => "denied",
        },
        QuestionAnswer { Answers.Cancelled: true } => "skipped",
        QuestionAnswer => "answered",
        TrustAnswer { Trusted: true } => "trusted the folder",
        _ => "not trusted",
    };

    private void ShowPermission(PermissionPending p, CancellationToken resolved)
    {
        var request = p.Request;
        var prompt = new PermissionPromptControl(request, p.OfferTrust);
        var content = prompt.BuildContent();   // built ONCE — see PermissionDecider's note

        // ESCAPE STOPS THE TURN for a command's own prompt, and denies anything else — see EscapeAction.
        var stopTurn = request.Policy?.SessionId is { } sessionId
            ? () => { mw.TabForSessionId(sessionId)?.Session?.CancelTurn(); }
            : (Action?)null;
        var escape = WindowPermissionPrompt.EscapeAction(request, prompt.TryCancel, stopTurn);

        // THE ASKING TAB IS NAMED IN THE SAME UI-THREAD STEP THAT SHOWS THE PROMPT. The window keeps one "next
        // prompt goes here" slot; named from another thread, a second session's prompt arriving in between would
        // take it, and both prompts would land in whichever tab spoke last.
        system.EnqueueOnUIThread(() =>
        {
            mw.NotePromptTabBySessionId(p.SessionId);
            mw.ShowPermissionPrompt(content, escape, request.RequesterAgentId);
        }, "tuiPromptResponder.Permission");

        _ = Task.Run(async () =>
        {
            // ANSWERED ELSEWHERE takes the prompt down; the deny that completes it then loses the race and is dropped.
            using var _ = resolved.Register(prompt.TryCancel);
            var choice = await prompt.Completion.ConfigureAwait(false);
            system.EnqueueOnUIThread(() => mw.RestoreComposer(content), "tuiPromptResponder.RestoreComposer");
            hub.Answer(p.Id, new PermissionAnswer(choice), Terminal);
        });
    }

    private void ShowQuestion(QuestionPending q, CancellationToken resolved)
    {
        // A SESSION THIS WINDOW HAS NO TAB FOR is not one it can ask about; a browser still can.
        if (mw.TabForSessionId(q.SessionId)?.Session is not { } session) return;
        _ = Task.Run(async () =>
        {
            var answers = await mw.AskQuestionAsync(session, q.Questions, resolved).ConfigureAwait(false);
            hub.Answer(q.Id, new QuestionAnswer(answers), Terminal);
        });
    }

    private void ShowTrust(TrustPending t, CancellationToken resolved)
    {
        var prompt = new TrustQuestionControl(t.Folder);
        var content = prompt.BuildContent();   // built ONCE — see PermissionPromptControl's contract
        // ON THE ASKING SESSION'S TAB — named in the same step, as for a permission. Without it a second tab's trust
        // question went into whichever tab last asked something.
        system.EnqueueOnUIThread(() =>
        {
            mw.NotePromptTabBySessionId(t.SessionId);
            mw.ShowPermissionPrompt(content);
        }, "tuiPromptResponder.Trust");
        _ = Task.Run(async () =>
        {
            using var _ = resolved.Register(prompt.Dismiss);
            try
            {
                hub.Answer(t.Id, new TrustAnswer(await prompt.Completion.ConfigureAwait(false)), Terminal);
            }
            catch (OperationCanceledException)
            {
                // TAKEN DOWN BECAUSE IT WAS ANSWERED ELSEWHERE — nothing to answer from here.
            }
            finally
            {
                system.EnqueueOnUIThread(() => mw.RestoreComposer(content), "tuiPromptResponder.RestoreComposer");
            }
        });
    }
}
