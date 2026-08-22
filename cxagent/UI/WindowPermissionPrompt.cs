using CxAgent.Core.Permissions;
using SharpConsoleUI;

namespace CxAgent.UI;

/// <summary>
/// The terminal's way of asking, handed to the gate as a function.
///
/// <para>WHAT IS LEFT AFTER THE MOVE. The decision pipeline — silent policy, stored rules, the auto
/// classifier and its fail-closed behaviour, the trust floor, persistence — is
/// <see cref="PermissionDecider"/> in Core now. This is the part that genuinely needs a window: show
/// a control, wait for a click, put the composer back. Everything else a second front end would have
/// had to reimplement, and reimplementing a fail-closed classifier is how one quietly stops failing
/// closed.</para>
/// </summary>
public static class WindowPermissionPrompt
{
    /// <summary>A gate that asks through this window.</summary>
    public static PermissionDecider Gate(ConsoleWindowSystem system, MainWindow mw,
        PermissionRulesStore store, ITranscriptWriter? transcript) =>
        // THE Message OVERLOAD, so the gate's own notices arrive with their severity intact rather
        // than as a pre-coloured sentence Core had to compose.
        PermissionDecider.WithPrompt(store,
            transcript is null ? null : transcript.Write,
            (request, offerTrust, ct) =>
            {
                var prompt = new PermissionPromptControl(request, offerTrust);
                var content = prompt.BuildContent();   // built ONCE — see PermissionDecider's note
                // THE DENY ACTION GOES WITH IT. The window is handed the built content, not this
                // control, so it cannot answer the prompt on its own — this is the one operation
                // Escape needs, and TryCancel is already the safe idempotent Deny the cancellation
                // registration below uses.
                system.EnqueueOnUIThread(() => mw.ShowPermissionPrompt(content, prompt.TryCancel));
                return AwaitAndRestore(prompt, ct,
                    () => system.EnqueueOnUIThread(() => mw.RestoreComposer(content)));
            });

    // Cancellation is registered on the CONTROL's own Completion (via TryCancel), not a gate-local
    // TCS: a cancelled goal must make prompt.Completion itself resolve, or this method's `finally`
    // never runs, RestoreComposer is never called, the composer stays swapped out forever, and
    // MainWindow._activePrompt stays set — silently no-opping every later ShowPermissionPrompt call
    // (MainWindow's idempotence guard). That is a permanent soft-lock: no goal can ever be submitted
    // again. The registration is disposed once Completion resolves either way (real click or
    // cancellation), so it never fires late against a control nobody is looking at. TryCancel is a
    // safe no-op if a real click already resolved it, or wins the race and a later click finds the
    // TCS already completed (PermissionPromptControl.Completion's own doc: a second resolution is
    // silently ignored, same as a double-click).
    private static async Task<PermissionChoice> AwaitAndRestore(
        PermissionPromptControl prompt, CancellationToken ct, Action restore)
    {
        using var reg = ct.Register(() => prompt.TryCancel());
        try
        {
            return await prompt.Completion;
        }
        finally
        {
            restore();
        }
    }
}
