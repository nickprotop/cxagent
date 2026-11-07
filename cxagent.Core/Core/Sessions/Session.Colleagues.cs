using CxAgent.Core.Colleagues;

namespace CxAgent.Core.Sessions;

/// <summary>The session's desk for colleagues — keeping its workers, bringing colleagues in.</summary>
public sealed partial class Session
{
    private readonly object _colleagueGate = new();
    private IReadOnlyList<ColleagueLine>? _colleagueIndex;

    /// <summary>Each colleague as the lead last knew it, by name — the snapshot, then each change it was told of.</summary>
    private Dictionary<string, string>? _colleaguesKnown;

    /// <summary>
    /// The colleagues the lead's prompt lists: taken the first time an agent asks, then the same list until the
    /// conversation is cleared, compacted or another resumed — memory's rule, for memory's reason: it heads the cache.
    /// </summary>
    internal IReadOnlyList<ColleagueLine> ColleagueIndex()
    {
        lock (_colleagueGate)
        {
            if (_colleagueIndex is null)
            {
                var now = Colleagues?.List() ?? [];
                _colleagueIndex = [.. now.Select(ColleagueLine.Of)];
                _colleaguesKnown = now.ToDictionary(c => c.Name, Fingerprint, StringComparer.Ordinal);
            }
            return _colleagueIndex;
        }
    }

    /// <summary>
    /// What changed in the library since the lead last knew it, as one line for the front of the next message — or null.
    /// AT THE END OF THE CONVERSATION, NOT IN THE LIST, so the cached prefix stays whole.
    /// </summary>
    internal string? TakeColleagueNote()
    {
        lock (_colleagueGate)
        {
            if (_colleaguesKnown is not { } known || Colleagues is not { } desk) return null;
            var now = desk.List();
            var parts = new List<string>();
            foreach (var c in now)
            {
                if (!known.TryGetValue(c.Name, out var was))
                    parts.Add($"'{c.Name}' was kept — {Described(c)}");
                else if (was != Fingerprint(c))
                    parts.Add($"'{c.Name}' has grown — {Described(c)}");
            }
            var names = now.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
            parts.AddRange(known.Keys.Where(k => !names.Contains(k)).Order(StringComparer.Ordinal).Select(k => $"'{k}' was forgotten"));
            if (parts.Count == 0) return null;

            _colleaguesKnown = now.ToDictionary(c => c.Name, Fingerprint, StringComparer.Ordinal);
            return "[Colleagues changed since this conversation began: " + string.Join("; ", parts)
                   + ". The colleague tool lists them as they are now.]";
        }
    }

    /// <summary>The conversation is fresh — the cache is rebuilt anyway — so the list may catch up.</summary>
    internal void RefreshColleagueIndex()
    {
        lock (_colleagueGate)
        {
            _colleagueIndex = null;
            _colleaguesKnown = null;
        }
    }

    private static string Fingerprint(ColleagueRecord c) =>
        c.Description + "\n" + string.Join("\n", c.Workplaces.Select(w => w.Codebase).Order(StringComparer.Ordinal));

    private static string Described(ColleagueRecord c) => c.Description.Length > 0 ? c.Description : "no description yet";

    /// <summary>This session's desk for colleagues, or null when colleagues are off.</summary>
    public ColleagueDesk? Colleagues { get; private set; }

    /// <summary>
    /// Points the session at its colleagues — on every wire, so turning them off in config takes effect, and a re-wire's
    /// new spawner and models reach the desk. ONE DESK FOR THE SESSION'S LIFE while the library stays the same: it
    /// listens to the session's workers, whose store outlives every re-wire, and a second desk would hear each round
    /// twice.
    /// </summary>
    internal void UseColleagues(ColleagueDeskParts? parts)
    {
        if (parts is null)
        {
            Colleagues?.Dispose();
            Colleagues = null;
            return;
        }
        if (Colleagues is { } desk && desk.Library.Root == parts.Library.Root)
        {
            desk.Rewire(parts);
            return;
        }
        Colleagues?.Dispose();
        Colleagues = new ColleagueDesk(parts);
    }
}
