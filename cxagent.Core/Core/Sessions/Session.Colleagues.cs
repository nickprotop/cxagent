using System.Text;
using CxAgent.Core.Colleagues;
using CxAgent.Core.Commands;

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
            var brought = TakeBrought();
            if (_colleaguesKnown is not { } known || Colleagues is not { } desk) return brought;
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
            if (parts.Count == 0) return brought;

            _colleaguesKnown = now.ToDictionary(c => c.Name, Fingerprint, StringComparer.Ordinal);
            var changed = "[Colleagues changed since this conversation began: " + string.Join("; ", parts)
                          + ". The colleague tool lists them as they are now.]";
            return brought is null ? changed : brought + "\n" + changed;
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

    /// <summary>Colleagues the user brought in since the lead last heard, for the front of its next message.</summary>
    private readonly List<string> _brought = [];

    private string? TakeBrought()
    {
        lock (_brought)
        {
            if (_brought.Count == 0) return null;
            var said = string.Join("\n", _brought);
            _brought.Clear();
            return said;
        }
    }

    private const string ColleaguesOff = "Colleagues are off. Turn them on in F8 › General, or with colleagues.enabled in config.json.";

    /// <summary><c>/colleagues</c>, with or without a verb — the same in every front end.</summary>
    public CommandStatus ColleaguesCommand(string? arguments)
    {
        if (Colleagues is not { } desk)
        {
            Say(new Message(ColleaguesOff, Severity.Info));
            return CommandStatus.Reported;
        }
        var text = (arguments ?? "").Trim();
        var (verb, rest) = text.Split(' ', 2) is [var v, var r] ? (v, r.Trim()) : (text, "");
        // THE LIBRARY IS FILES ON DISK: one that cannot be read is said, never thrown into the front end's command loop.
        try
        {
            return RunColleagues(desk, verb, rest);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return Told($"The colleagues' library could not be read: {e.Message}");
        }
    }

    private CommandStatus RunColleagues(ColleagueDesk desk, string verb, string rest)
    {
        switch (verb.ToLowerInvariant())
        {
            case "":
                return ListColleagues(desk);
            case "keep":
                if (rest.Length == 0) return Told("Name the worker: /colleagues keep <worker> [name]. /agents lists them.");
                var (worker, name) = rest.Split(' ', 2) is [var w, var n] ? (w, Unquoted(n.Trim())) : (rest, (string?)null);
                return Told(desk.Keep(new KeepOrder(worker, name is { Length: > 0 } ? name : null)));
            case "bring":
                // NOT AWAITED, the spawn runs on; BringColleagueAsync says its own outcome, failures included.
                _ = BringColleagueAsync(rest);
                return CommandStatus.Reported;
            default:
                return Told($"/colleagues knows keep and bring, not '{verb}'. /colleagues alone lists them.");
        }
    }

    /// <summary>
    /// <c>/colleagues bring &lt;name&gt; &lt;task&gt;</c>: brings one in as the lead's worker, in the background, and tells
    /// the lead with its next message — the worker's report reaches it like any other.
    ///
    /// <para>THE NAME MAY HOLD SPACES, so the longest colleague name the text starts with wins, and a single word is
    /// tried last.</para>
    /// </summary>
    public async Task<CommandStatus> BringColleagueAsync(string rest)
    {
        try
        {
            return await BringColleagueCoreAsync(rest);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return Told($"Could not bring the colleague in: {e.Message}");
        }
    }

    private async Task<CommandStatus> BringColleagueCoreAsync(string rest)
    {
        if (Colleagues is not { } desk) return Told(ColleaguesOff);

        // A QUOTED NAME is taken as quoted — "UI Charting" or 'UI Charting' — the way a shell has taught people to type
        // a name with spaces; read unquoted, the quote became part of the name and nothing matched.
        if (rest.Length > 1 && rest[0] is '"' or '\'' && rest.IndexOf(rest[0], 1) is var close and > 1)
            return await BringNamedAsync(desk, rest[1..close].Trim(), rest[(close + 1)..].Trim());

        // THE LONGEST COLLEAGUE NAME THE TEXT IS, OR STARTS WITH — the whole text included, so a name typed with no task is
        // known as that name and asked for its task, rather than cut at its first space into a name nobody has.
        var typed = rest.Trim();
        var match = desk.List().Select(c => c.Name)
            .Where(n => typed.Equals(n, StringComparison.OrdinalIgnoreCase) || typed.StartsWith(n + " ", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(n => n.Length).FirstOrDefault();
        var (name, task) = match is not null
            ? (match, typed[match.Length..].Trim())
            : typed.Split(' ', 2) is [var first, var after] ? (first, after.Trim()) : (typed, "");
        return await BringNamedAsync(desk, name, task);
    }

    private async Task<CommandStatus> BringNamedAsync(ColleagueDesk desk, string name, string task)
    {
        if (name.Length == 0) return Told("Say who and what: /colleagues bring <name> <task>.");
        if (task.Length == 0)
        {
            var known = desk.Library.Find(name)?.Name ?? name;
            return Told($"What should {known} do? /colleagues bring {known} <task> — the task is its first message.");
        }
        if (SessionId is not { } lead) return Told("This session has no agent to report to yet.");

        var answer = await desk.BringAsync(new BringOrder(name, task, null, Background: true, lead), CancellationToken.None);
        if (Agents.SubAgentEnvelope.NameOf(answer) is { } handle)
        {
            lock (_brought)
                _brought.Add($"[The user brought the colleague {name} in as the worker {handle}, on: {task}. Its report "
                             + "reaches you when it finishes; agent_send reaches it meanwhile.]");
            return Told($"{name} is working as {handle}.");
        }
        return Told(answer);
    }

    private CommandStatus ListColleagues(ColleagueDesk desk)
    {
        var all = desk.List();
        var sb = new StringBuilder();
        if (all.Count == 0) sb.Append("No colleagues yet. `/colleagues keep <worker> [name]` keeps a worker of this session as one.\n");
        foreach (var line in all.Select(ColleagueLine.Of))
            sb.Append("- **").Append(line.Name).Append("**").Append(line.Description.Length > 0 ? " — " + line.Description : "")
              .Append(line.Knows.Count > 0 ? " · knows: " + string.Join(", ", line.Knows) : "").Append('\n');
        sb.Append("\nIn `").Append(desk.Library.Root).Append("`. `/colleagues bring <name> <task>` brings one in.");
        // THE PLAIN LINE TOO, so a notice reads "Charting — lays out charts" wherever bold is not drawn.
        Say(new Message(sb.ToString().Replace("**", ""), Severity.Info));
        return CommandStatus.Reported;
    }

    /// <summary>The text without one pair of surrounding quotes, double or single.</summary>
    private static string Unquoted(string text) =>
        text.Length > 1 && text[0] is '"' or '\'' && text[^1] == text[0] ? text[1..^1].Trim() : text;

    private CommandStatus Told(string text)
    {
        Say(new Message(text, Severity.Info));
        return CommandStatus.Reported;
    }
}
