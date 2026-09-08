namespace CxAgent.Core.Plugins;

/// <summary>One plugin-submitted goal, waiting for the session to go idle.</summary>
public sealed record QueuedSubmit(string PluginName, string Goal);

/// <summary>
/// Goals a plugin submitted while its session was busy.
///
/// <para>QUEUED RATHER THAN REFUSED, because a refusal pushes retry logic into every plugin and makes
/// a submit's success depend on timing the plugin cannot observe. This is the same shape the deferred
/// unwire already uses: the session takes it when its turn ends.</para>
///
/// <para>BOUNDED, because a plugin submitting in a loop must hit a limit rather than grow a list. The
/// depth is small on purpose — a plugin with more than a handful of goals outstanding is malfunctioning,
/// and a queue deep enough to hide that is worse than a refusal.</para>
/// </summary>
public sealed class PluginSubmitQueue
{
    /// <summary>How many goals may wait at once, across all plugins.</summary>
    public const int MaxDepth = 8;

    private readonly Queue<QueuedSubmit> _pending = new();

    // A PLAIN object, matching PermissionRulesStore and the rest of Core. This codebase does not use
    // System.Threading.Lock anywhere; a lone exception here would be a style divergence with no
    // benefit at this contention level.
    private readonly object _lock = new();

    /// <summary>Queues a goal, or refuses it with a reason the plugin can report.</summary>
    public bool TryEnqueue(string pluginName, string goal, out string? refusal)
    {
        lock (_lock)
        {
            if (_pending.Count >= MaxDepth)
            {
                refusal = $"the session's submit queue is full ({MaxDepth} waiting) — "
                        + "a goal was not accepted rather than queued without bound.";
                return false;
            }

            _pending.Enqueue(new QueuedSubmit(pluginName, goal));
            refusal = null;
            return true;
        }
    }

    /// <summary>Takes the next goal, or null when none waits.</summary>
    public QueuedSubmit? DrainOne()
    {
        lock (_lock) return _pending.Count == 0 ? null : _pending.Dequeue();
    }

    /// <summary>
    /// Forgets everything one plugin queued, answering how many.
    ///
    /// <para>CALLED AT SEVER: a goal from a plugin that is no longer wired must never run, and the
    /// count is what the unwire message reports.</para>
    /// </summary>
    public int DropFrom(string pluginName)
    {
        lock (_lock)
        {
            var kept = _pending.Where(p => !string.Equals(p.PluginName, pluginName, StringComparison.Ordinal)).ToList();
            var dropped = _pending.Count - kept.Count;
            _pending.Clear();
            foreach (var item in kept) _pending.Enqueue(item);
            return dropped;
        }
    }
}
