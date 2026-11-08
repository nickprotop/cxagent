using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using CxAgent.Core.Agents;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Storage;

namespace CxAgent.Core.Colleagues;

/// <summary>Keep a session's worker as a colleague.</summary>
/// <param name="Worker">Its handle or agent id.</param>
/// <param name="Name">The colleague's name, or null to take one from what the worker was doing.</param>
public sealed record KeepOrder(string Worker, string? Name);

/// <summary>Bring a colleague into the session as a worker.</summary>
/// <param name="Name">The colleague.</param>
/// <param name="Task">What it is asked to do — its first message here.</param>
/// <param name="Model">A provider entry to run on instead of its own, or null.</param>
/// <param name="Background">Whether it runs in the background where it can.</param>
/// <param name="ParentAgentId">The session agent's id, so its report finds its way back.</param>
public sealed record BringOrder(string Name, string Task, string? Model, bool Background, string ParentAgentId);

/// <summary>A model a colleague can run or be distilled on.</summary>
/// <param name="Provider">The provider.</param>
/// <param name="Label">How a person knows it — <c>entry:model</c>.</param>
/// <param name="Window">Its context window, or null when not known.</param>
/// <param name="Instance">Its provider entry name, or null for an unnamed one.</param>
public sealed record ModelChoice(ILlmProvider Provider, string Label, int? Window, string? Instance);

/// <summary>
/// What a session lends its desk. Set on every wire: a re-wire replaces the spawner and the models, and the desk must
/// reach the new ones.
/// </summary>
public sealed record ColleagueDeskParts
{
    /// <summary>The library.</summary>
    public required ColleagueStore Library { get; init; }

    /// <summary>The session's workers — the same store for the session's whole life.</summary>
    public required SubAgentStore Workers { get; init; }

    /// <summary>How the session spawns, or null where it cannot.</summary>
    public ISubAgentSpawner? Spawner { get; init; }

    /// <summary>The session's folder.</summary>
    public required string Folder { get; init; }

    /// <summary>The session's id as it is now — a resume or a re-wire mints another.</summary>
    public required Func<string?> SessionId { get; init; }

    /// <summary>Writes a line about a worker into the session agent's history.</summary>
    public required Action<WorkerMark> NoteWorker { get; init; }

    /// <summary>A model by provider entry name, null asking for the session's own; null when there is no such entry.</summary>
    public required Func<string?, ModelChoice?> Models { get; init; }

    /// <summary>Tells the user something — a distilling that failed.</summary>
    public Action<string>? Notify { get; init; }
}

/// <summary>
/// A session's desk for colleagues: keeping one of its workers, bringing a colleague in, catching every round an
/// incarnation works and handing it to the distiller.
///
/// <para>EVERYTHING THROUGH THE WORKER CODE'S OWN PATHS. A colleague is spawned by an order, marked by an origin, equipped
/// through the factory's door and revived from its history like any worker; this class only prepares those and listens
/// to the store's begin and end announcements. The worker code never learns the word.</para>
/// </summary>
public sealed class ColleagueDesk : IDisposable
{
    /// <summary>The origin prefix a colleague's incarnation carries.</summary>
    public const string OriginPrefix = "colleague:";

    private ColleagueDeskParts _parts;
    private readonly SubAgentStore _workers;
    private readonly Distiller _distiller;

    /// <summary>Runs under way, by agent id: the last message its context held, and when it began.</summary>
    private readonly ConcurrentDictionary<string, RoundStart> _rounds = new(StringComparer.Ordinal);

    /// <summary>The experience version each incarnation here started from (or last learned of), by origin.</summary>
    private readonly ConcurrentDictionary<string, long> _given = new(StringComparer.Ordinal);

    /// <summary>The window each incarnation brought in here runs on, by origin — what its preload is sized to.</summary>
    private readonly ConcurrentDictionary<string, int> _windows = new(StringComparer.Ordinal);

    /// <summary>Workplaces already counted for this session, so a session counts once however many rounds it has.</summary>
    private readonly ConcurrentDictionary<string, byte> _counted = new(StringComparer.Ordinal);

    private readonly object _drainGate = new();
    private Task _drains = Task.CompletedTask;

    public ColleagueDesk(ColleagueDeskParts parts)
    {
        _parts = parts;
        _workers = parts.Workers;
        _distiller = new Distiller(parts.Library);
        _distiller.Failed += (name, why) => _parts.Notify?.Invoke($"Could not distil {name}'s latest work: {why}");
        _workers.SendBegan += OnBegan;
        _workers.SendEnded += OnEnded;

        // ROUNDS LEFT FROM BEFORE — a process that ended mid-drain, a drain that failed — are taken up now.
        Drain();
    }

    /// <summary>The library this desk keeps colleagues in.</summary>
    public ColleagueStore Library => _parts.Library;

    /// <summary>Points the desk at a re-wired session's spawner and models. The workers' store does not change.</summary>
    public void Rewire(ColleagueDeskParts parts) => _parts = parts;

    /// <summary>Every colleague, the most recently worked first.</summary>
    public IReadOnlyList<ColleagueRecord> List() => _parts.Library.List();

    /// <summary>Completes once every distilling this desk started has finished — for a caller that must wait.</summary>
    public Task Idle()
    {
        lock (_drainGate) return _drains;
    }

    /// <summary>
    /// Keeps a worker of this session as a colleague, and answers the line to show. The worker becomes the colleague's
    /// incarnation here at once: its conversation so far is the first round of its past, distilled now, and its later
    /// runs are rounds.
    /// </summary>
    public string Keep(KeepOrder order)
    {
        var handle = _workers.HandleFor(order.Worker.Trim());
        if (_workers.Find(handle) is not { } kept) return $"There is no worker called '{order.Worker.Trim()}' in this session.";
        if (_workers.OriginOf(handle) is { } origin && origin.StartsWith(OriginPrefix, StringComparison.Ordinal))
            return $"{handle} is already the colleague {origin[OriginPrefix.Length..]}.";
        // NOT WHILE IT WORKS: the run under way would be split between the kept round and the next one.
        if (_workers.IsBusy(handle)) return $"{handle} is working; keep it when it has finished.";

        var name = (order.Name ?? NameFrom(kept)).Trim();
        if (ColleagueStore.NameRefusal(name) is { } bad) return bad;
        if (_parts.Library.Find(name) is { } taken) return $"There is already a colleague called '{taken.Name}'. Give this one another name.";

        var now = DateTimeOffset.UtcNow;
        var record = new ColleagueRecord
        {
            Name = name,
            Type = kept.TypeName,
            Instance = InstanceOf(kept.Agent.ModelId),
            Created = now,
            Description = kept.Description ?? "",
        };
        try
        {
            _parts.Library.Create(record);
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return $"Could not keep {handle}: {e.Message}";
        }

        var messages = kept.Agent.Agent.Context.Messages.Where(m => m.Role != "system").ToList();
        Append(name, new PastRound(0, _parts.SessionId() ?? "", _parts.Folder, kept.At, now, messages));

        var marked = OriginPrefix + name;
        _workers.SetOrigin(handle, marked);
        _given[marked] = 0;
        // IN THE SESSION'S HISTORY, so a resume brings it back as the colleague and not as the worker it was.
        _parts.NoteWorker(new WorkerMark(kept.Agent.Agent.Id, marked));
        Drain();
        return $"Kept {handle} as the colleague {name}. Its experience is being distilled from its work so far; from now on "
               + "every task it finishes is added.";
    }

    /// <summary>Brings a colleague into this session as a worker, and answers the spawn's envelope or why not.</summary>
    public async Task<string> BringAsync(BringOrder order, CancellationToken ct)
    {
        var name = order.Name.Trim();
        if (_parts.Library.Find(name) is not { } record)
            return $"There is no colleague called '{name}'. {Known()}";

        // WHAT IT HAS NOT YET LEARNED FROM is taken up now, so this incarnation's next one starts from it.
        Drain();

        var origin = OriginPrefix + record.Name;
        if (_workers.NameOfOrigin(origin) is { } live)
            return $"{record.Name} is already here as {live} — give it work with agent_send.";
        if (_parts.Spawner is not { } spawner) return "This session cannot start workers.";

        // ITS OWN MODEL, OR THE ONE ASKED FOR — and the session's, said, when its own is not configured here.
        string? note = null;
        ModelChoice? choice;
        if (order.Model is { Length: > 0 } asked)
        {
            choice = _parts.Models(asked);
            if (choice is null) return $"There is no provider entry '{asked}' here.";
        }
        else
        {
            choice = record.Instance is { } own ? _parts.Models(own) : _parts.Models(null);
            if (choice is null)
            {
                choice = _parts.Models(null);
                note = $"{record.Name}'s own model, {record.Instance}, is not configured here; it runs on the session's.";
            }
        }
        if (choice is null) return "There is no model to run it on.";
        if (ExperienceBudget.WindowRefusal(choice.Window, choice.Label) is { } small) return small;

        _windows[origin] = choice.Window!.Value;
        var session = _parts.Models(null);
        var instance = choice.Instance is { } i && i != session?.Instance ? i : null;
        SubAgent? child = null;
        var envelope = await spawner.SpawnAsync(new SpawnOrder
        {
            Prompt = order.Task,
            Description = record.Name,
            Type = record.Type,
            Instance = instance,
            Origin = origin,
            Handle = ColleagueStore.Slug(record.Name),
            Background = order.Background,
            ParentAgentId = order.ParentAgentId,
        }, c => child = c, ct) ?? "This session cannot start workers.";

        // A SPAWN THE AGENT TOOL DID NOT MAKE, WRITTEN DOWN AS ONE, so a resume rebuilds it as it rebuilds every worker.
        if (child is not null && SubAgentEnvelope.NameOf(envelope) is { } handle)
            _parts.NoteWorker(new WorkerMark(child.Agent.Id, origin)
            {
                Name = handle, Type = record.Type, Instance = instance, Description = record.Name,
            });

        if (child?.AugmentRefusal is { } refused)
            note = (note is null ? "" : note + " ") + $"It came in without its experience: {refused}.";
        return note is null ? envelope : note + "\n" + envelope;
    }

    /// <summary>
    /// The factory's door, answered: for a colleague's origin, who it is and its experience, sized to its window.
    /// Null for an origin that is not a colleague's; throws for a colleague that no longer exists, so the factory
    /// builds the worker plain and says why.
    /// </summary>
    public ChildAugment? Augment(string origin)
    {
        if (!origin.StartsWith(OriginPrefix, StringComparison.Ordinal)) return null;
        var name = origin[OriginPrefix.Length..];
        var record = _parts.Library.Find(name)
            ?? throw new InvalidOperationException($"there is no colleague called {name} any more");

        var window = _windows.TryGetValue(origin, out var w) ? w
            : (record.Instance is { } own ? _parts.Models(own)?.Window : null) ?? _parts.Models(null)?.Window ?? ExperienceBudget.MinimumWindow;
        var preload = ExperienceBudget.Preload(_parts.Library.ReadExperience(record.Name),
            new ExperienceFocus(Codebase.Key(_parts.Folder), record.Workplaces, ExperienceBudget.For(window)));
        _given[origin] = record.ExperienceVersion;
        return new ChildAugment { Context = IncarnationBriefing.For(record, preload) };
    }

    private void OnBegan(string agentId)
    {
        if (Incarnation(agentId) is not { } found) return;
        _rounds[agentId] = new RoundStart(found.Kept.Agent.Agent.Context.Messages.LastOrDefault(m => m.Role != "system"), DateTimeOffset.UtcNow);
    }

    private void OnEnded(string agentId, string? outcome)
    {
        if (!_rounds.TryRemove(agentId, out var start) || Incarnation(agentId) is not { } found) return;

        // ONE ROUND'S FAILURE COSTS THAT ROUND: this runs on the store's end announcement, inside the worker's own
        // release, and must never throw into it.
        try
        {
            // EVERYTHING AFTER THE LAST MESSAGE IT HELD WHEN THE RUN BEGAN — found by the message itself, not a count: the
            // agent writes its system prompt in at the front when a run starts, which moves every index. Not found, a
            // compaction replaced it mid-round, and what remains — the summary and what followed — is the round.
            var messages = found.Kept.Agent.Agent.Context.Messages.Where(m => m.Role != "system").ToList();
            var at = start.Last is null ? -1 : messages.FindLastIndex(m => ReferenceEquals(m, start.Last));
            var round = messages.Skip(at + 1).ToList();
            if (round.Count == 0) return;

            var record = _parts.Library.Find(found.Name);
            if (record is null) return;
            Append(found.Name, new PastRound(0, _parts.SessionId() ?? "", _parts.Folder, start.At, DateTimeOffset.UtcNow, round)
            {
                AlsoRead = OtherWorkplacesRead(round, record),
            });
            TellOfGrowth(found, record);
            Drain();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            _parts.Notify?.Invoke($"Could not keep {found.Name}'s latest round: {e.Message}");
        }
    }

    /// <summary>
    /// LEARNING ELSEWHERE ARRIVES AT THE END: an incarnation's starting experience heads its cached prompt and never
    /// changes, so growth from other sessions reaches it as one line in its mail, read at the start of its next run.
    /// </summary>
    private void TellOfGrowth(Found found, ColleagueRecord record)
    {
        var origin = OriginPrefix + found.Name;
        var given = _given.GetOrAdd(origin, record.ExperienceVersion);
        if (record.ExperienceVersion <= given) return;
        var elsewhere = record.GrownIn.Where(f => !string.Equals(f, _parts.Folder, StringComparison.Ordinal)).ToList();
        _given[origin] = record.ExperienceVersion;
        if (elsewhere.Count == 0) return;
        found.Kept.Agent.Agent.Mailbox.TryEnqueue(
            $"[Your experience has grown since you started here (from sessions in {string.Join(", ", elsewhere)}). "
            + "What you were given at the start is still yours; what changed is in your colleague's library.]",
            out _, wakes: false);
    }

    private void Append(string name, PastRound round)
    {
        _parts.Library.Append(name, round);
        var key = Codebase.Key(_parts.Folder);
        var firstHere = _counted.TryAdd(name + "\u0001" + key, 0);
        _parts.Library.Update(name, r =>
        {
            var at = r.Workplaces.FirstOrDefault(w => w.Codebase == key);
            var updated = at is null
                ? new Workplace(key, _parts.Folder, 1, round.Ended)
                : at with { Folder = _parts.Folder, Sessions = at.Sessions + (firstHere ? 1 : 0), Last = round.Ended };
            return r with { Workplaces = [.. r.Workplaces.Where(w => w.Codebase != key), updated] };
        });
    }

    /// <summary>How long a drain that found the lock held waits before looking again.</summary>
    internal TimeSpan BusyRetry { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Set while a drain is queued and not yet started, so a burst of rounds queues one drain, not one each.</summary>
    private int _drainQueued;

    /// <summary>
    /// Distils every colleague's waiting rounds in the background — see <see cref="Distiller.DrainAllAsync"/>.
    ///
    /// <para>A HELD LOCK IS WAITED OUT, for as long as any round is still waiting: the holder drains everything it
    /// finds, but it may have looked just before this round was appended, and giving up would leave the round until
    /// that colleague's next one — which may never come.</para>
    /// </summary>
    private void Drain()
    {
        if (Interlocked.Exchange(ref _drainQueued, 1) == 1) return;
        lock (_drainGate)
        {
            _drains = _drains.ContinueWith(async _ =>
            {
                Interlocked.Exchange(ref _drainQueued, 0);
                while (await _distiller.DrainAllAsync(DistillModel, CancellationToken.None) == DistillOutcome.Busy && AnyWaiting())
                    await Task.Delay(BusyRetry);
            }, TaskScheduler.Default).Unwrap();
        }
    }

    private bool AnyWaiting()
    {
        try { return _parts.Library.List().Any(r => _parts.Library.Undistilled(r.Name).Count > 0); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>The distilling model for a colleague: its own setting, else its model, else the session's — 128k or more.</summary>
    private Func<string?, ILlmProvider?> DistillModel(string name) => _ =>
    {
        var record = _parts.Library.Find(name);
        var choice = (record?.DistillInstance ?? record?.Instance) is { } entry ? _parts.Models(entry) ?? _parts.Models(null) : _parts.Models(null);
        return choice is not null && ExperienceBudget.WindowRefusal(choice.Window, choice.Label) is null ? choice.Provider : null;
    };

    /// <summary>Folders of the colleague's other workplaces whose files the round's tool calls named.</summary>
    private IReadOnlyList<string> OtherWorkplacesRead(List<ChatMessage> round, ColleagueRecord record)
    {
        var here = Codebase.Key(_parts.Folder);
        var others = record.Workplaces.Where(w => w.Codebase != here).ToList();
        if (others.Count == 0) return [];
        var read = new HashSet<string>(StringComparer.Ordinal);
        foreach (var call in round.SelectMany(m => m.ToolCalls ?? []))
        {
            if (call.Arguments.ValueKind != JsonValueKind.Object || !call.Arguments.TryGetProperty("path", out var p)
                || p.ValueKind != JsonValueKind.String || p.GetString() is not { Length: > 0 } path) continue;
            var full = Path.GetFullPath(path, _parts.Folder);
            foreach (var w in others)
                if (full.StartsWith(Path.TrimEndingDirectorySeparator(w.Folder) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    read.Add(w.Folder);
        }
        return [.. read];
    }

    private Found? Incarnation(string agentId)
    {
        if (_workers.FindByAgentId(agentId) is not { } kept) return null;
        return _workers.OriginOf(kept.Name) is { } origin && origin.StartsWith(OriginPrefix, StringComparison.Ordinal)
            ? new Found(kept, origin[OriginPrefix.Length..])
            : null;
    }

    private string Known()
    {
        var names = _parts.Library.List().Select(r => r.Name).ToList();
        return names.Count == 0 ? "There are no colleagues yet." : $"Colleagues: {string.Join(", ", names)}.";
    }

    /// <summary>A name from what the worker was doing: its description, cut to what a name may hold.</summary>
    private static string NameFrom(StoredAgent kept)
    {
        var sb = new StringBuilder();
        foreach (var ch in kept.Description ?? kept.Name)
            sb.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : ' ');
        var name = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (name.Length > 40) name = name[..40].TrimEnd();
        return name.Length > 0 ? name : kept.Name;
    }

    /// <summary>The provider entry in an <c>entry:model</c> label, or null for a bare model.</summary>
    private static string? InstanceOf(string? modelId) =>
        modelId is { } id && id.IndexOf(':') is var colon and > 0 ? id[..colon] : null;

    public void Dispose()
    {
        _workers.SendBegan -= OnBegan;
        _workers.SendEnded -= OnEnded;
    }

    private readonly record struct RoundStart(ChatMessage? Last, DateTimeOffset At);

    private sealed record Found(StoredAgent Kept, string Name);
}
