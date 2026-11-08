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
public sealed record BringOrder(string Name, string Task, string? Model, bool Background, string ParentAgentId)
{
    /// <summary>Told the child the moment it is built, before it runs — how the lead draws a bring as a live worker row.</summary>
    public Action<SubAgent>? OnChild { get; init; }
}

/// <summary>What a front end needs to open a colleague's one-to-one: where, the conversation to return to, and the setup.</summary>
/// <param name="Folder">Its most recent workplace that still exists — or the session's own folder.</param>
/// <param name="Resume">Its saved one-to-one, to return to; null the first time.</param>
/// <param name="Setup">What makes the session its one-to-one — <see cref="Sessions.Session.BecomeOneToOne"/>.</param>
/// <summary>A one-to-one's conversation as it stands when a turn ends — what <see cref="ColleagueDesk.CaptureOneToOne"/> keeps from.</summary>
/// <param name="Colleague">Whose one-to-one it is.</param>
/// <param name="SessionId">The session it is in now.</param>
/// <param name="Folder">The folder it was opened in.</param>
/// <param name="Messages">Its conversation.</param>
public sealed record OneToOneExchange(string Colleague, string SessionId, string Folder, IReadOnlyList<ChatMessage> Messages);

public sealed record OneToOneOpening(string Folder, Storage.SessionSnapshot? Resume, Sessions.OneToOne Setup);

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

    /// <summary>Whether this desk's session is a colleague's one-to-one.</summary>
    public bool OneToOne { get; init; }

    /// <summary>Where sessions are saved — where a colleague's one-to-one is found again by its tag.</summary>
    public Storage.FolderSessionStore? Sessions { get; init; }

    /// <summary>The port a background worker's report reaches the lead through — what agent_send itself is given.</summary>
    public IAgentDelivery? Delivery { get; init; }

    /// <summary>Tells the user something — a distilling that failed.</summary>
    public Action<string>? Notify { get; init; }

    /// <summary>Tells the user something that is news, not trouble — what a colleague learned.</summary>
    public Action<string>? Tell { get; init; }

    /// <summary>What a browser may reach while the session is served to one — read by an incarnation's workplace reads
    /// and its recall at each decision.</summary>
    public Web.ServedFolders? Served { get; init; }
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
        _distiller.Failed += (name, why) =>
        {
            Distilled(name);
            _parts.Notify?.Invoke($"Could not distil {name}'s latest work: {why}");
        };
        _distiller.Started += name =>
        {
            lock (_distilling) if (!_distilling.Contains(name)) _distilling.Add(name);
            StateChanged?.Invoke();
        };
        _distiller.Distilled += OnDistilled;
        // OFF THE LIST AS ITS OWN ROUNDS ARE DONE: waiting for the whole drain named the first of several colleagues as
        // distilling — and its card as learning — until the last was done.
        _distiller.Finished += Distilled;
        _workers.SendBegan += OnBegan;
        _workers.SendEnded += OnEnded;
        _workers.Badges = BadgeFor;

        // ROUNDS LEFT FROM BEFORE — a process that ended mid-drain, a drain that failed — are taken up now.
        Drain();
    }

    /// <summary>The library this desk keeps colleagues in.</summary>
    public ColleagueStore Library => _parts.Library;

    /// <summary>Colleagues being distilled now, by this desk's drains.</summary>
    public IReadOnlyList<string> Distilling
    {
        get { lock (_distilling) return [.. _distilling]; }
    }

    private readonly List<string> _distilling = [];

    /// <summary>Raised when who is distilling changed — for a status line or a panel to redraw.</summary>
    public event Action? StateChanged;

    /// <summary>
    /// Colleagues this desk appended rounds for since their last distilling — the only ones whose distilling this
    /// session announces: a round another session lived is that session's news, even when this one's drain distils it.
    /// </summary>
    private readonly ConcurrentDictionary<string, long> _owned = new(StringComparer.OrdinalIgnoreCase);

    private void Distilled(string name)
    {
        bool removed;
        lock (_distilling) removed = _distilling.Remove(name);
        if (removed) StateChanged?.Invoke();
    }

    private void OnDistilled(DistillChange change)
    {
        // WHAT THIS SESSION'S OWN DISTILLING MADE IS NOT NEWS FROM ELSEWHERE: the incarnation here learned it. Counted as
        // known, or each round of its own was followed by "your experience has grown … from sessions in …".
        var origin = OriginPrefix + change.Colleague;
        if (_given.TryGetValue(origin, out var given) && change.Version > given) _given[origin] = change.Version;

        // THE ROUND THIS DESK APPENDED, BY NUMBER: still waiting, it is not this change's; distilled before it — with
        // nothing to learn, which raises nothing, or by another process — its claim goes, and this change is another
        // session's news. A flag kept the claim, and the next distilling of someone else's round was said here.
        if (!_owned.TryGetValue(change.Colleague, out var mine) || mine > change.Through) return;
        _owned.TryRemove(change.Colleague, out _);
        if (mine < change.From) return;
        if (change.Nothing)
        {
            _parts.Tell?.Invoke($"{change.Colleague}: nothing new to keep from that task.");
            return;
        }
        var record = _parts.Library.Find(change.Colleague);
        var parts = new List<string>();
        if (change.Core) parts.Add("core updated");
        parts.AddRange(change.Updated.Select(k => $"{FolderName(record, k)} updated"));
        parts.AddRange(change.Added.Select(k => $"{FolderName(record, k)} added"));
        _parts.Tell?.Invoke($"{change.Colleague} learned from its work: {string.Join(", ", parts)} (v{change.Version}).");
    }

    /// <summary>A codebase as a person knows it — its folder's own name — rather than its key with a hash.</summary>
    private static string FolderName(ColleagueRecord? record, string key) =>
        record?.Workplaces.FirstOrDefault(w => w.Codebase == key) is { } w
            ? Path.GetFileName(Path.TrimEndingDirectorySeparator(w.Folder))
            : System.Text.RegularExpressions.Regex.Replace(key, "-[0-9a-f]{8}$", "");

    /// <summary>Points the desk at a re-wired session's spawner and models. The workers' store does not change.</summary>
    public void Rewire(ColleagueDeskParts parts) => _parts = parts;

    /// <summary>Every colleague, the most recently worked first.</summary>
    public IReadOnlyList<ColleagueRecord> List() => _parts.Library.List();

    /// <summary>
    /// The panel's Colleagues section, or null when the library is empty — then neither front end shows the section.
    /// One here is "working" while a run holds it, else "distilling" while its rounds are, else "idle".
    /// </summary>
    public ColleaguePanel? Panel()
    {
        // A ONE-TO-ONE'S DESK SHOWS NOTHING: Bring and Open there would offer the colleague to itself.
        if (_parts.OneToOne) return null;
        var all = List();
        if (all.Count == 0) return null;
        var distilling = Distilling;
        var rows = all.Select(r =>
        {
            var handle = _workers.NameOfOrigin(OriginPrefix + r.Name);
            var busy = distilling.Contains(r.Name, StringComparer.OrdinalIgnoreCase);
            var state = handle is not null && _workers.IsBusy(handle) ? "working" : busy ? "distilling" : handle is null ? "" : "idle";
            return new ColleaguePanelRow(r.Name, r.Description, handle, state)
            {
                AgentId = handle is null ? null : _workers.Find(handle)?.Agent.Agent.Id,
            };
        }).ToList();
        return new ColleaguePanel(rows, distilling);
    }

    /// <summary>
    /// The Tasks panel's card for the worker <paramref name="handle"/>, or null when it is not a colleague's.
    ///
    /// <para>THE TASK IS WHAT STARTED ITS RUN, as the store recorded it when the run began — a bring's, a send's or a
    /// tab's — never read from its conversation: the app's own warnings arrive there as user-role messages (a live card
    /// read "task: You have called grep with the same arguments 3 times…"), and the list is the worker's own, appended
    /// to and compacted by its loop while a panel reads on the UI's thread.</para>
    /// </summary>
    public ColleagueCard? CardOf(string handle)
    {
        if (_workers.Find(handle) is not { } kept || Incarnation(kept.Agent.Agent.Id) is not { } found) return null;
        if (_parts.Library.Find(found.Name) is not { } record) return null;
        var task = _workers.AskedOf(handle)?.Trim().Split('\n', 2)[0].Trim();
        return new ColleagueCard(ColleagueLine.Of(record).Knows, record.ExperienceVersion,
            Distilling.Contains(record.Name, StringComparer.OrdinalIgnoreCase), task);
    }

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
    /// <summary>
    /// Why <paramref name="order"/> cannot be kept, or null when it can — asked by the colleague tool BEFORE it puts the
    /// question to the user, so nobody is asked to approve what would be refused anyway.
    /// </summary>
    public string? KeepRefusal(KeepOrder order) => Checked(order, out _, out _);

    /// <summary>
    /// Why nothing is brought in or kept in a one-to-one — by the command, the library's Bring, or a browser. A worker
    /// there would report into the conversation, and its work would be distilled as the colleague's own.
    /// </summary>
    private const string InOneToOne =
        "This is a one-to-one: nothing is brought in or kept here. Bring colleagues in, or keep workers, in a working session.";

    private string? Checked(KeepOrder order, out StoredAgent? kept, out string name)
    {
        kept = null;
        name = "";
        if (_parts.OneToOne) return InOneToOne;
        var handle = _workers.HandleFor(order.Worker.Trim());
        kept = _workers.Find(handle);
        if (kept is null) return $"There is no worker called '{order.Worker.Trim()}' in this session.";
        if (_workers.OriginOf(handle) is { } origin && origin.StartsWith(OriginPrefix, StringComparison.Ordinal))
            return $"{handle} is already the colleague {origin[OriginPrefix.Length..]}.";
        // NOT WHILE IT WORKS: the run under way would be split between the kept round and the next one.
        if (_workers.IsBusy(handle)) return $"{handle} is working; keep it when it has finished.";

        name = (order.Name ?? NameFrom(kept)).Trim();
        if (ColleagueStore.NameRefusal(name) is { } bad) return bad;
        if (_parts.Library.Find(name) is { } taken) return $"There is already a colleague called '{taken.Name}'. Give this one another name.";
        return null;
    }

    public string Keep(KeepOrder order)
    {
        if (Checked(order, out var found, out var name) is { } refused) return refused;
        var kept = found!;
        var handle = kept.Name;

        var now = DateTimeOffset.UtcNow;
        var record = new ColleagueRecord
        {
            Name = name,
            Type = kept.TypeName,
            Instance = kept.Agent.Instance,
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

        // ALL OR NOTHING: a colleague with no past, and a worker not marked as it, would be refused a second keep under
        // the same name — so a keep that fails after the folder exists takes the folder away again.
        try
        {
            var messages = kept.Agent.Agent.Context.Messages.Where(m => m.Role != "system").ToList();
            Append(name, new PastRound(0, _parts.SessionId() ?? "", _parts.Folder, kept.At, now, messages));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            try { Directory.Delete(_parts.Library.FolderOf(name), recursive: true); }
            catch (Exception gone) when (gone is IOException or UnauthorizedAccessException) { }
            return $"Could not keep {handle}: {e.Message}";
        }

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
        if (_parts.OneToOne) return InOneToOne;
        var name = order.Name.Trim();
        if (_parts.Library.Find(name) is not { } record)
            return $"There is no colleague called '{name}'. {Known()}";

        // WHAT IT HAS NOT YET LEARNED FROM is taken up now, so this incarnation's next one starts from it.
        Drain();

        var origin = OriginPrefix + record.Name;
        if (_parts.Spawner is not { } spawner) return "This session cannot start workers.";
        if (_workers.NameOfOrigin(origin) is { } live)
        {
            if (order.Task.Trim().Length == 0) return $"{record.Name} is already here as {live} — give it work with agent_send.";

            // ALREADY HERE, AND A TASK FOR IT: the task goes to it, as agent_send would take it. Answered "use
            // agent_send" instead, a lead went on without sending it, and the work was lost (user, 2026-11-08).
            return await new AgentReachTools(_workers, spawner.ConcurrencySlot, spawner.CanBackground ? _parts.Delivery : null)
                .SendAsync(new SendRequest(To: live, Prompt: order.Task, Background: order.Background,
                    ParentAgentId: order.ParentAgentId, FromUser: false), ct);
        }

        // ONE BRING AT A TIME PER COLLEAGUE: two at once — the lead's and the user's, or two tool calls in one turn —
        // would both pass the check above and leave two incarnations of one colleague in one session.
        if (!_bringing.TryAdd(origin, 0)) return $"{record.Name} is already being brought in.";
        try
        {
            return await BringAsync(order, record, spawner, ct);
        }
        finally
        {
            _bringing.TryRemove(origin, out _);
        }
    }

    /// <summary>Colleagues being brought in now, by origin.</summary>
    private readonly ConcurrentDictionary<string, byte> _bringing = new(StringComparer.Ordinal);

    private async Task<string> BringAsync(BringOrder order, ColleagueRecord record, ISubAgentSpawner spawner, CancellationToken ct)
    {
        var origin = OriginPrefix + record.Name;

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
        // THE ENTRY THAT PASSED THE CHECK, NAMED EVEN WHEN IT IS THE SESSION'S OWN: left out, the spawner would take the
        // type's own routing, which may be another, smaller model than the one the preload was sized for.
        var instance = choice.Instance;
        SubAgent? child = null;
        // NO TASK, NO RUN: it joins idle, and work reaches it as it reaches any worker.
        var envelope = await spawner.SpawnAsync(new SpawnOrder
        {
            Prompt = order.Task,
            Run = order.Task.Trim().Length > 0,
            Description = record.Name,
            Type = record.Type,
            Instance = instance,
            Origin = origin,
            Handle = ColleagueStore.Slug(record.Name),
            Background = order.Background,
            ParentAgentId = order.ParentAgentId,
        }, c =>
        {
            child = c;
            // THE CALLER'S OWN HOOK TOO — the lead's, which draws the spawn as a worker row live.
            order.OnChild?.Invoke(c);
        }, ct) ?? "This session cannot start workers.";

        // AN ORDINARY WORKER IF THE DOOR REFUSED, AND MARKED AS ONE: left marked, its rounds would reach a colleague it is
        // not, and a keep of it would be refused as one it already is.
        var refused = child?.AugmentRefusal;
        var handle = SubAgentEnvelope.NameOf(envelope);
        if (refused is not null && handle is not null) _workers.SetOrigin(handle, null);

        // A SPAWN THE AGENT TOOL DID NOT MAKE, WRITTEN DOWN AS ONE, so a resume rebuilds it as it rebuilds every worker.
        if (child is not null && handle is not null)
            _parts.NoteWorker(new WorkerMark(child.Agent.Id, refused is null ? origin : null)
            {
                Name = handle, Type = record.Type, Instance = instance, Description = record.Name,
            });

        if (refused is not null)
            note = (note is null ? "" : note + " ") + $"It came in without its experience: {refused}.";
        return note is null ? envelope : note + "\n" + envelope;
    }

    /// <summary>The colleague whose one-to-one <paramref name="snapshot"/> is, or null for an ordinary session.</summary>
    public static string? OneToOneOf(Storage.SessionSnapshot? snapshot) =>
        snapshot?.Tag is { } tag && tag.StartsWith(OriginPrefix, StringComparison.Ordinal) ? tag[OriginPrefix.Length..] : null;

    /// <summary>The refusal where a one-to-one is resumed by id with no library to open it through — a headless run, a
    /// browser's resume of a saved session.</summary>
    public static string OneToOneElsewhere(string colleague) =>
        $"That is a one-to-one with {colleague}: open it with /colleagues open {colleague}, or Open in the library.";

    /// <summary>
    /// The tag <paramref name="name"/>'s one-to-one is saved under, or null for no such colleague — what a front end looks
    /// for among its open sessions BEFORE asking for an opening, which refuses a conversation open anywhere, its own
    /// window included.
    /// </summary>
    public string? OneToOneTag(string name) => _parts.Library.Find(name.Trim()) is { } record ? OriginPrefix + record.Name : null;

    /// <summary>
    /// What it takes to open <paramref name="name"/>'s one-to-one, or null with why not — ONE ANSWER FOR BOTH FRONT ENDS,
    /// which each only open a session with it: the terminal in a tab, the browser as a session.
    ///
    /// <para>THE SAME CONVERSATION EVERY TIME: its saved one-to-one, found by its tag, is returned to rather than a new one
    /// begun — what it was taught there is in its context as well as its experience.</para>
    /// </summary>
    public OneToOneOpening? OneToOne(string name, out string? refusal)
    {
        refusal = null;
        if (_parts.Library.Find(name.Trim()) is not { } record)
        {
            refusal = $"There is no colleague called '{name.Trim()}'. {Known()}";
            return null;
        }
        var tag = OriginPrefix + record.Name;
        var saved = _parts.Sessions?.ListTagged(tag).FirstOrDefault();

        // ONE WRITER PER CONVERSATION, as /sessions has it: open in another process, resuming it here would carry on a
        // second copy beside the first.
        if (saved is not null && _parts.Sessions!.HolderOf(saved.Uid) is { } held)
        {
            refusal = $"{record.Name}'s one-to-one is {Storage.SessionLock.HeldMessage(held)}.";
            return null;
        }
        var resume = saved is not null ? _parts.Sessions!.LoadById(saved.Uid) : null;

        // ITS MOST RECENT WORKPLACE THAT STILL EXISTS — where it can read what it knows — else the session's own folder.
        var folder = resume?.WorkingDir is { } was && Directory.Exists(was) ? was
            : record.Workplaces.OrderByDescending(w => w.Last).Select(w => w.Folder).FirstOrDefault(Directory.Exists) ?? _parts.Folder;

        var window = (record.Instance is { } own ? _parts.Models(own)?.Window : null) ?? _parts.Models(null)?.Window ?? ExperienceBudget.MinimumWindow;
        var preload = ExperienceBudget.Preload(_parts.Library.ReadExperience(record.Name),
            new ExperienceFocus(Codebase.Key(folder), record.Workplaces, ExperienceBudget.For(window)));
        // NO SILENT READS IN A ONE-TO-ONE: the user is there to answer a prompt, and the briefing does not claim them.
        var briefing = IncarnationBriefing.For(record, preload, new IncarnationSetting(ReadsWorkplaces: false, [])) + "\n\n# Talking with the user\n\n"
            + "You are talking with the user directly — not working for a lead agent on a task. Answer what they ask from "
            + "what you know, and say plainly when you do not know. What they teach or correct here becomes part of your "
            + "experience. You can read files to check what you say; you cannot change anything.";
        return new OneToOneOpening(folder, resume, new Sessions.OneToOne(record.Name, tag, briefing, $"with {record.Name}"));
    }

    /// <summary>
    /// The last message of each one-to-one already kept as a round, by colleague — where the next round starts.
    ///
    /// <para>BY COLLEAGUE, NOT SESSION ID: a resume and a re-wire each give the session a new id, and a mark under the old
    /// one was missed, so the whole conversation was appended again and distilled a second time.</para>
    /// </summary>
    private readonly ConcurrentDictionary<string, ChatMessage?> _oneToOneKept = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Marks everything a one-to-one holds as already kept — called as it is wired, which is always between turns: what a
    /// resume restored was kept by the process that lived it, and a re-wire's copy of the conversation holds nothing new.
    /// </summary>
    public void OneToOneWired(string name, IReadOnlyList<ChatMessage> messages) =>
        _oneToOneKept[name] = messages.LastOrDefault(m => m.Role != "system");

    /// <summary>
    /// Keeps a one-to-one's latest exchange as a round of its colleague's past, and distils it. Everything after the last
    /// message kept before — found by the message itself, as an incarnation's round is.
    /// </summary>
    public void CaptureOneToOne(OneToOneExchange exchange)
    {
        var name = exchange.Colleague;
        try
        {
            var all = exchange.Messages.Where(m => m.Role != "system").ToList();
            var at = _oneToOneKept.TryGetValue(name, out var last) && last is not null
                ? all.FindLastIndex(m => ReferenceEquals(m, last)) : -1;
            var round = all.Skip(at + 1).ToList();
            if (round.Count == 0 || _parts.Library.Find(name) is null) return;
            _oneToOneKept[name] = round[^1];
            Append(name, new PastRound(0, exchange.SessionId, exchange.Folder,
                round[0].Timestamp == default ? DateTimeOffset.UtcNow : round[0].Timestamp, DateTimeOffset.UtcNow, round));
            Drain();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _parts.Notify?.Invoke($"Could not keep {name}'s latest one-to-one exchange: {e.Message}");
        }
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

        // ITS WORKPLACES AS THEY ARE NOW: one moved or deleted is said in the briefing, never read — a folder that is no
        // longer there is not one the colleague knows the contents of.
        var here = record.Workplaces.Where(w => Directory.Exists(w.Folder)).Select(w => w.Folder).ToList();
        var gone = record.Workplaces.Where(w => !Directory.Exists(w.Folder)).Select(w => w.Folder).ToList();
        return new ChildAugment
        {
            Context = IncarnationBriefing.For(record, preload, new IncarnationSetting(ReadsWorkplaces: here.Count > 0, gone)),
            ExtraTools = [new RecallTool(new RecallScope(_parts.Library, record.Name, _parts.Served))],
            ReadAlso = new Permissions.ReadAlso(here, _parts.Served),
        };
    }

    private void OnBegan(string agentId)
    {
        if (Incarnation(agentId) is not { } found) return;
        _rounds[agentId] = new RoundStart(found.Kept.Agent.Agent.Context.Messages.LastOrDefault(m => m.Role != "system"), DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// What a colleague is told at the start of its next run when its experience grew in other sessions while it worked
    /// here: its starting experience stays fixed for the session, so where to read the newer one is part of the line.
    /// </summary>
    public static string GrowthLine(IReadOnlyList<string> elsewhere) =>
        $"{GrowthNote} (from sessions in {string.Join(", ", elsewhere)}). "
        + "What you were given at the start is still yours; what changed is in your colleague's library — recall reads the current version.]";

    /// <summary>The note a colleague is given when its experience grew elsewhere — the desk's own words, not a task.</summary>
    private const string GrowthNote = "[Your experience has grown since you started here";

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
                AlsoRead = OtherWorkplacesRead(round, record, _parts.Folder),
            });
            TellOfGrowth(found, record);
            Drain();
        }
        // EVERYTHING BUT A CANCELLATION: this runs inside the worker's own release, and an exception escaping it would skip
        // the listeners after this one — the run's own archive among them.
        catch (Exception e) when (e is not OperationCanceledException)
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
        found.Kept.Agent.Agent.Mailbox.TryEnqueue(GrowthLine(elsewhere),
            out _, wakes: false);
    }

    private void Append(string name, PastRound round)
    {
        _owned[name] = _parts.Library.Append(name, round);
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

                // NOTHING IS DISTILLING ONCE THE DRAIN ENDS, whatever it ended on.
                bool any;
                lock (_distilling) { any = _distilling.Count > 0; _distilling.Clear(); }
                if (any) StateChanged?.Invoke();
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

    /// <summary>
    /// Folders of the colleague's other workplaces whose files the round's tool calls named. A path no file can have —
    /// one a model wrote with a NUL in it — names no workplace and is passed over.
    /// </summary>
    internal static IReadOnlyList<string> OtherWorkplacesRead(List<ChatMessage> round, ColleagueRecord record, string folder)
    {
        var here = Codebase.Key(folder);
        var others = record.Workplaces.Where(w => w.Codebase != here).ToList();
        if (others.Count == 0) return [];
        var read = new HashSet<string>(StringComparer.Ordinal);
        foreach (var call in round.SelectMany(m => m.ToolCalls ?? []))
        {
            if (call.Arguments.ValueKind != JsonValueKind.Object || !call.Arguments.TryGetProperty("path", out var p)
                || p.ValueKind != JsonValueKind.String || p.GetString() is not { Length: > 0 } path) continue;
            string full;
            try { full = Path.GetFullPath(path, folder); }
            catch (ArgumentException) { continue; }
            if (full.Contains('\0')) continue;
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
        // THE LETTERS A NAME MAY HOLD, and only those — ASCII, as NameRefusal allows — so "Café menu" becomes "Caf menu"
        // rather than a refusal of a name the user never typed.
        foreach (var ch in kept.Description ?? kept.Name)
            sb.Append(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' ? ch : ' ');
        var name = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (name.Length > 40) name = name[..40].TrimEnd();
        return name.Length > 0 ? name : kept.Name;
    }

    /// <summary>A colleague's origin as a badge — the one way every surface names an incarnation.</summary>
    public static WorkerBadge? BadgeFor(string origin) =>
        origin.StartsWith(OriginPrefix, StringComparison.Ordinal) ? new WorkerBadge("Colleague", origin[OriginPrefix.Length..], "◆") : null;

    public void Dispose()
    {
        if (_workers.Badges == (Func<string, WorkerBadge?>)BadgeFor) _workers.Badges = null;
        _workers.SendBegan -= OnBegan;
        _workers.SendEnded -= OnEnded;
    }

    private readonly record struct RoundStart(ChatMessage? Last, DateTimeOffset At);

    private sealed record Found(StoredAgent Kept, string Name);
}
