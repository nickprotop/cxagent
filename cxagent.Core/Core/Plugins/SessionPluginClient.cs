using CxAgent.Core.Sessions;

namespace CxAgent.Core.Plugins;

/// <summary>
/// One plugin's handle on one session.
///
/// <para>ONE PER PLUGIN PER SESSION, because that is the unit revocation works on: unwiring a plugin
/// in one session must not disturb the same plugin in another, which is the per-session failure this
/// project keeps having.</para>
/// </summary>
public sealed class SessionPluginClient(Session session, string pluginName, PluginSubmitQueue queue)
    : IPluginClient
{
    // SEVERED IS A ONE-WAY LATCH, read on every call. A plugin holds this reference for as long as it
    // likes; what stops it acting is this flag, not the registry letting go.
    private volatile bool _severed;

    public async Task<SubmitResult> Submit(string goal, bool wantResult = false, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_severed, this);

        if (string.IsNullOrWhiteSpace(goal))
            return new SubmitResult(false, null, "a goal was empty — nothing was submitted.");

        // BUSY MEANS QUEUE, NOT REFUSE. The session takes it when its turn ends, the same shape the
        // deferred unwire uses. A full queue is the only refusal, and it names itself.
        if (session.IsBusy)
        {
            return queue.TryEnqueue(pluginName, goal, out var refusal)
                ? new SubmitResult(true, null, null)
                : new SubmitResult(false, null, refusal);
        }

        var outcome = session.Submit(goal, origin: TurnOriginator.Plugin(pluginName));

        return outcome switch
        {
            Session.SubmitOutcome.Started started when wantResult
                => new SubmitResult(true, await started.Result.WaitAsync(ct), null),
            Session.SubmitOutcome.Started => new SubmitResult(true, null, null),
            Session.SubmitOutcome.Queued => new SubmitResult(true, null, null),

            // A COMMAND IS NOT A TURN. Text beginning with '/' runs a command and starts nothing, so
            // there is no answer to wait for even when the caller asked.
            Session.SubmitOutcome.Handled handled
                => new SubmitResult(true, null, $"the goal ran as a command ({handled.Status})."),

            Session.SubmitOutcome.NoAgent
                => new SubmitResult(false, null, "the session has no usable model configured."),

            _ => new SubmitResult(false, null, "the session did not accept the goal."),
        };
    }

    /// <summary>
    /// Cuts the plugin off and forgets what it queued, answering how many goals were dropped.
    ///
    /// <para>DROPPED RATHER THAN DRAINED: a goal from a plugin that is no longer wired must never run.
    /// The count is what the unwire message reports, so a user is told work was discarded rather than
    /// wondering why it never happened.</para>
    /// </summary>
    public int Sever()
    {
        _severed = true;
        return queue.DropFrom(pluginName);
    }
}
