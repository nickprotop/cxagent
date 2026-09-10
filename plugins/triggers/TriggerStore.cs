namespace CxAgent.Plugins.Triggers;

/// <summary>One pending trigger. Its id is small and per-session, because a human retypes it.</summary>
public sealed record Trigger(int Id, string SessionId, When When, string Prompt,
    DateTimeOffset NextFire);

/// <summary>
/// Every session's pending triggers, in one process-wide static.
///
/// <para>STATIC BECAUSE THE PLUGIN IS PER-SESSION AND THE TIMER IS NOT. A plugin gets its own
/// instance per session, and one timer serving every session is cheaper and simpler than one per
/// instance — but it means everything here is shared, which is why nothing is keyed on an id alone.
/// </para>
///
/// <para>THE KEY IS (SessionId, Id). Two sessions each holding trigger 1 collide the moment anything
/// keys on the number, and a wake fired into the wrong session's client is the reach rule broken by
/// the back door — with nothing to report it.</para>
///
/// <para>IN MEMORY, AND NOT BY CHOICE. A session is a folder since phase one and a half, and
/// IPluginContext carries no path to it, so there is nowhere to persist that would be deleted with
/// the conversation. Phase three owns durability.</para>
/// </summary>
public static class TriggerStore
{
    private static readonly List<Trigger> Pending = [];
    private static readonly Dictionary<string, int> NextId = new(StringComparer.Ordinal);

    // A PLAIN object, matching the rest of this codebase, which does not use System.Threading.Lock.
    private static readonly object Gate = new();

    /// <summary>Adds a trigger and answers it, with the id its session will show.</summary>
    public static Trigger Add(string sessionId, When when, string prompt)
    {
        lock (Gate)
        {
            var id = NextId.TryGetValue(sessionId, out var n) ? n : 1;
            NextId[sessionId] = id + 1;

            var trigger = new Trigger(id, sessionId, when, prompt,
                when.NextAfter(DateTimeOffset.Now) ?? DateTimeOffset.MaxValue);
            Pending.Add(trigger);
            return trigger;
        }
    }

    /// <summary>This session's triggers, soonest first.</summary>
    public static IReadOnlyList<Trigger> For(string sessionId)
    {
        lock (Gate)
            return Pending.Where(t => t.SessionId == sessionId)
                .OrderBy(t => t.NextFire).ToList();
    }

    /// <summary>Removes one, or answers false when this session holds no such id.</summary>
    public static bool Cancel(string sessionId, int id)
    {
        lock (Gate)
            return Pending.RemoveAll(t => t.SessionId == sessionId && t.Id == id) > 0;
    }

    /// <summary>
    /// Replaces a trigger's schedule and prompt, keeping its id.
    ///
    /// <para>A FULL REPLACEMENT MINUS THE ID, which is what keeps the exactly-one-of rule universal.
    /// Under merge semantics an update with only a prompt presents ZERO when-fields and one with a
    /// new time on a recurring trigger presents TWO — so the validator would refuse both, and the
    /// only way out is an exception that says "exactly one, except here".</para>
    /// </summary>
    public static Trigger? Update(string sessionId, int id, When when, string prompt)
    {
        lock (Gate)
        {
            var index = Pending.FindIndex(t => t.SessionId == sessionId && t.Id == id);
            if (index < 0) return null;

            var updated = Pending[index] with
            {
                When = when,
                Prompt = prompt,
                NextFire = when.NextAfter(DateTimeOffset.Now) ?? DateTimeOffset.MaxValue,
            };
            Pending[index] = updated;
            return updated;
        }
    }

    /// <summary>
    /// Drops one session's triggers, leaving every other session's running.
    ///
    /// <para>CALLED FROM SEVER, NOT FROM Stop. Unwire runs deregister → drain → SEVER → Stop → reap,
    /// and Stop is timeout-bounded and abandoned if it overruns — so work that only stops there is
    /// work that may never stop.</para>
    /// </summary>
    public static void SweepSession(string sessionId)
    {
        lock (Gate)
        {
            Pending.RemoveAll(t => t.SessionId == sessionId);
            NextId.Remove(sessionId);
        }
    }

    /// <summary>Everything whose moment has come, across every session.</summary>
    public static IReadOnlyList<Trigger> Due(DateTimeOffset now)
    {
        lock (Gate)
            return Pending.Where(t => t.NextFire <= now).ToList();
    }

    /// <summary>
    /// Advances a fired trigger, or removes it when it was a one-shot.
    ///
    /// <para>MATCHED BY IDENTITY, not by reference: the record may have been replaced by an update
    /// between the fire and this call, and rescheduling a stale copy would resurrect the old prompt.
    /// </para>
    ///
    /// <para>AND SKIPPED ENTIRELY WHEN THE MATCH IS NO LONGER THE SAME SCHEDULE. (SessionId, Id)
    /// survives an Update — only the id is kept — so finding a row is not enough to know it is still
    /// the trigger that fired: Update may have replaced When and Prompt while this fire's submit was
    /// still awaiting. Advancing THAT row from THIS fire's `now` would push a schedule Update never
    /// meant off by one cycle, and Update already computed NextFire against the current clock when it
    /// ran — there is nothing here to correct.</para>
    /// </summary>
    public static void Reschedule(Trigger fired, DateTimeOffset now)
    {
        lock (Gate)
        {
            var index = Pending.FindIndex(
                t => t.SessionId == fired.SessionId && t.Id == fired.Id);
            if (index < 0) return;

            var current = Pending[index];
            if (current.When != fired.When || current.Prompt != fired.Prompt) return;

            // REPEATS DECIDES, NOT NextAfter's RESULT: an "after" one-shot's NextAfter(now) is
            // always now + delay, never null — null only ever comes from a spent "at". Reading
            // Repeats is the only way that is true for all three kinds at once.
            if (!current.When.Repeats)
            {
                Pending.RemoveAt(index);
                return;
            }

            var next = current.When.NextAfter(now);
            if (next is null)
            {
                Pending.RemoveAt(index);
                return;
            }

            Pending[index] = current with { NextFire = next.Value };
        }
    }
}
