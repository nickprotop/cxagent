using CxAgent.Core.Commands;
using SharpConsoleUI;

namespace CxAgent.UI;

/// <summary>
/// Runs a task nobody awaits, and says so when it throws.
///
/// <para>WHY THIS EXISTS: a button handler and a command handler are both synchronous — they return
/// bool, and the work behind them is async — so every one of them starts a task and discards it. A
/// discarded task that throws takes its exception with it: no crash, no message, nothing in the
/// transcript. The user presses a button and the application does not react, which is
/// indistinguishable from the button being dead.</para>
///
/// <para>THAT IS NOT A HYPOTHETICAL. It cost a whole debugging session: "Update does nothing" was
/// reported and reproduced, and every candidate cause — a stale catalog hash, a shadowed install, a
/// missing checksum — was investigated and eliminated from the outside, because the one thing that
/// would have named the fault was being swallowed here.</para>
///
/// <para>THE MESSAGE GOES TO THE TRANSCRIPT, NOT A TOAST. A failure a user must act on outlives six
/// seconds, and an exception's own words are usually longer than a toast's fixed width — which cuts
/// off exactly the half that says why.</para>
/// </summary>
internal static class FireAndForget
{
    /// <summary>
    /// Starts <paramref name="work"/> and hands a throw to <paramref name="say"/>.
    ///
    /// <para>A CALLBACK RATHER THAN A WINDOW, because a dialog reports differently from a transcript:
    /// the plugin manager toasts and writes panel notes, and pushing a transcript row from behind a
    /// modal would put the message where nobody is looking.</para>
    /// </summary>
    /// <param name="work">The task to run unawaited.</param>
    /// <param name="what">What the user asked for, named as they would — "update csharp-lsp".</param>
    /// <param name="say">Where the failure is said. Called on the UI thread.</param>
    /// <param name="system">Used to reach the UI thread; the throw arrives on whatever thread failed.</param>
    public static void Run(Task work, string what, Action<string> say, ConsoleWindowSystem system)
    {
        _ = Report(work, what, say, system);

        static async Task Report(Task work, string what, Action<string> say, ConsoleWindowSystem system)
        {
            try
            {
                await work;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                // THE FIRST FRAME, NOT THE WHOLE TRACE. A NullReferenceException's Message names
                // nothing at all; the file and line that threw is the smallest thing that turns it
                // into something a reader can act on, and a full trace would not fit a toast.
                var where = ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim() ?? "";
                system.EnqueueOnUIThread(
                    () => say($"could not {what}: {ex.GetType().Name} — {ex.Message} {where}"));
            }
        }
    }

    /// <summary>Starts <paramref name="work"/> and reports a throw as <paramref name="what"/> failing.</summary>
    /// <param name="work">The task to run unawaited.</param>
    /// <param name="what">What the user asked for, named as they would name it — "update csharp-lsp".</param>
    /// <param name="window">Where the failure is said.</param>
    /// <param name="system">Used to reach the UI thread; the throw arrives on whatever thread failed.</param>
    public static void Run(Task work, string what, MainWindow window, ConsoleWindowSystem system)
    {
        _ = Report(work, what, window, system);

        static async Task Report(Task work, string what, MainWindow window, ConsoleWindowSystem system)
        {
            try
            {
                await work;
            }
            catch (OperationCanceledException)
            {
                // A CANCELLED OPERATION IS NOT A FAILURE. The user closed a dialog or answered a
                // prompt with no; saying "could not" about their own decision reads as a fault.
            }
            catch (Exception ex)
            {
                // THE TYPE AS WELL AS THE MESSAGE. A NullReferenceException's Message says nothing
                // at all on its own, and it is exactly the exception a silent discard hides best.
                var where = ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim() ?? "";
                system.EnqueueOnUIThread(() => ChatTranscriptSink.Post(window.Chat,
                    ChatTranscriptSink.Row(new Message(
                        $"could not {what}: {ex.GetType().Name} — {ex.Message} {where}",
                        Severity.Error))));
            }
        }
    }
}
