using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using CxAgent.Core.Agents;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Storage;

namespace CxAgent.Core.Colleagues;

/// <summary>A worker worth keeping as a colleague, and why it is offered — one line, never a blocking question.</summary>
/// <param name="Handle">The worker.</param>
/// <param name="Why">What it has been through: "you steered it 2 times", and the like.</param>
public sealed record KeepSuggestion(string Handle, string Why)
{
    /// <summary>The line both front ends show above Keep and Not this one.</summary>
    public string Line => $"{Handle} — {Why}. Keep it as a colleague? It would learn from its work and could be brought into any session.";
}

/// <summary>A colleague to distil again: from which round — its last when null — and on which model for this run.</summary>
/// <param name="Name">The colleague, by any name it has had.</param>
/// <param name="From">The first round distilled again, or null for its last.</param>
/// <param name="Model">A provider entry for this run, or null for its own distilling model.</param>
public sealed record DistillOrder(string Name, long? From, string? Model);

/// <summary>What distilling again asks: the order resolved — its name and round filled in — or null with why not.</summary>
public sealed record DistillAsk(DistillOrder? Order, string Text);

/// <summary>What forgetting a colleague asks: its name, or null when there is nothing to forget, and the words.</summary>
public sealed record ForgetAsk(string? Name, string Text);

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

    /// <summary>
    /// Bring its Talk worker — the conversation the user holds with it in this session — rather than the one that answers
    /// asks. It joins idle, keeps its conversation, and is told it is talking with the user.
    /// </summary>
    public bool Talk { get; init; }
}

/// <summary>A colleague's Talk worker here — its handle — or why there is none.</summary>
public sealed record TalkOpening(string? Handle, string? Refusal);

/// <summary>The worker a <c>/colleagues ask</c> or <c>talk</c> leads to — its handle, whose tab a front end opens — or why there is none.</summary>
public sealed record ColleagueDoor(string? Handle, string? Refusal);

/// <summary>What <see cref="ColleagueDesk.ReadsOf"/> sorts: one round's messages, whose colleague, and the folder it ran in.</summary>
/// <param name="Round">The round's messages, the tool calls and their results among them.</param>
/// <param name="Record">The colleague whose round it is — its workplaces are what "another workplace" means.</param>
/// <param name="Folder">The session's folder — its codebase is the one the round is about.</param>
public sealed record RoundReadsRequest(IReadOnlyList<ChatMessage> Round, ColleagueRecord Record, string Folder)
{
    /// <summary>Where the worker ran, when it was given a folder of its own — its relative paths mean that folder.</summary>
    public string? ResolveFrom { get; init; }

    /// <summary>cxagent's own folder, whose logs a backgrounded command writes and a worker reads: never "elsewhere".</summary>
    public string? Ignore { get; init; }

    /// <summary>The home folder a <c>~</c> stands for — the user's own unless a test says otherwise.</summary>
    public string Home { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}

/// <summary>Where a round read beyond its own codebase.</summary>
/// <param name="Workplaces">Other workplaces of the colleague's, by their recorded folder.</param>
/// <param name="Elsewhere">Folders outside every workplace, by their codebase's root.</param>
public sealed record RoundReads(IReadOnlyList<string> Workplaces, IReadOnlyList<string> Elsewhere);

/// <summary>A one-to-one's conversation as it stands when a turn ends — what <see cref="ColleagueDesk.CaptureOneToOne"/> keeps from.</summary>
/// <param name="Colleague">Whose one-to-one it is.</param>
/// <param name="SessionId">The session it is in now.</param>
/// <param name="Folder">The folder it was opened in.</param>
/// <param name="Messages">Its conversation.</param>
public sealed record OneToOneExchange(string Colleague, string SessionId, string Folder, IReadOnlyList<ChatMessage> Messages);

/// <summary>What a front end needs to open a colleague's one-to-one: where, the conversation to return to, and the setup.</summary>
/// <param name="Folder">Its most recent workplace that still exists — or the session's own folder.</param>
/// <param name="Resume">Its saved one-to-one, to return to; null the first time.</param>
/// <param name="Setup">What makes the session its one-to-one — <see cref="Sessions.Session.BecomeOneToOne"/>.</param>
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

    /// <summary>The session's gate — what a colleague's recall asks through, as the caller, while the session is served.</summary>
    public Permissions.IPermissionGate? Gate { get; init; }

    /// <summary>Whether a worker kept coming back to is offered as a colleague — config's <c>colleagues.suggest</c>.</summary>
    public bool Suggest { get; init; } = true;

    /// <summary>Where each distilling call is recorded for <c>/stats</c> — the usage history; null records nothing.</summary>
    public Action<Storage.RunRecord>? SaveRun { get; init; }

    /// <summary>Books a distilling call on the session's own spend — its ledger, its panel, its row in the usage history —
    /// under the model's label; null books nothing.</summary>
    public Action<Llm.LlmUsage, string>? Spent { get; init; }
}

/// <summary>
/// A session's desk for colleagues: keeping one of its workers, bringing a colleague in, catching every round an
/// incarnation works and handing it to the distiller.
///
/// <para>EVERYTHING THROUGH THE WORKER CODE'S OWN PATHS. A colleague is spawned by an order, marked by an origin, equipped
/// through the factory's door and revived from its history like any worker; this class only prepares those and listens
/// to the store's begin and end announcements. The worker code never learns the word.</para>
/// </summary>
public sealed partial class ColleagueDesk : IDisposable
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
        _distiller.Spent += Record;
        _workers.SendBegan += OnBegan;
        _workers.SendEnded += OnEnded;
        _workers.SendEnded += Weigh;
        _workers.Forgotten += OnWorkerForgotten;
        _workers.Preparing = PrepareAsync;
        _workers.AskInstead = AskWorkerForAsync;
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
        var key = KeyOf(change.Colleague);
        if (_given.TryGetValue(key, out var given) && change.Version > given) _given[key] = change.Version;

        // THE ROUND THIS DESK APPENDED, BY NUMBER: still waiting, it is not this change's; distilled before it — with
        // nothing to learn, which raises nothing, or by another process — its claim goes, and this change is another
        // session's news. A flag kept the claim, and the next distilling of someone else's round was said here.
        if (!_owned.TryGetValue(key, out var mine) || mine > change.Through) return;
        _owned.TryRemove(key, out _);
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

    /// <summary>
    /// One distilling call as a run in the usage history, under the colleague's name — whichever session's drain made it,
    /// since that is the machine that paid. A history that cannot be written loses the row, never the distilling.
    /// </summary>
    private void Record(DistillSpend call)
    {
        // UNDER THE LABEL THE MODEL WAS CHOSEN BY — its provider entry, then the model — as every other figure names it;
        // the bare model id would split one model into two lines of the per-model spend.
        var label = call.ModelId is { } id && _labels.TryGetValue(id, out var known) ? known : call.ModelId;
        try
        {
            _parts.Spent?.Invoke(call.Usage ?? new Llm.LlmUsage { InputTokens = call.InputTokens, OutputTokens = call.OutputTokens },
                label ?? "");
        }
        catch (Exception e) when (e is not OutOfMemoryException) { }
        try
        {
            _parts.SaveRun?.Invoke(new Storage.RunRecord(Guid.NewGuid().ToString("N"), _parts.SessionId() ?? "",
                Storage.StatsQuery.DistillPrefix + call.Colleague, label, call.InputTokens, call.OutputTokens,
                Turns: 1, ToolCalls: 0, Outcome: "completed", call.StartedAt, call.DurationMs, WorkingDir: null));
        }
        catch (Exception e) when (e is not OutOfMemoryException) { }
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
            // HERE IF EITHER OF ITS WORKERS IS: one here only to talk with the user is not to be brought in again.
            var handle = HandleOf(r) ?? TalkOf(r);
            var busy = distilling.Contains(r.Name, StringComparer.OrdinalIgnoreCase);
            var state = handle is not null && _workers.IsBusy(handle) ? "working" : busy ? "distilling" : handle is null ? "" : "idle";
            if (r.Frozen) state = state.Length == 0 ? "frozen" : state + " · frozen";
            return new ColleaguePanelRow(r.Name, r.Description, handle, state)
            {
                AgentId = handle is null ? null : _workers.Find(handle)?.Agent.Agent.Id,
            };
        }).ToList();
        return new ColleaguePanel(rows, distilling);
    }

    /// <summary>
    /// Its worker in this session, under its name now or one it had — A WORKER BROUGHT IN BEFORE A RENAME ELSEWHERE still
    /// carries the old name in its origin, and is still this colleague.
    /// </summary>
    private string? HandleOf(ColleagueRecord record) =>
        ColleagueRenaming.Origins(record).Select(_workers.NameOfOrigin).FirstOrDefault(h => h is not null);

    /// <summary>Its Talk worker here, by any name it has had, or null.</summary>
    private string? TalkOf(ColleagueRecord record) =>
        ColleagueRenaming.Origins(record).Select(_workers.ConversationOf).FirstOrDefault(h => h is not null);

    /// <summary>Every worker of it here — its Ask worker and its Talk worker, whichever are.</summary>
    private IEnumerable<string> HandlesOf(ColleagueRecord record) =>
        new[] { HandleOf(record), TalkOf(record) }.OfType<string>();

    /// <summary>Its Talk worker in this session, by its name now, or null.</summary>
    public string? TalkHere(string name) => _workers.ConversationOf(OriginPrefix + name);

    /// <summary>
    /// Renames a colleague and answers the line to show — see <see cref="ColleagueRenaming"/>. Its incarnation here takes
    /// the new name at once, recorded for a resume, so its card, its rounds and what is said when it learns all follow.
    /// </summary>
    public string Rename(string name, string newName)
    {
        var was = _parts.Library.Find(name.Trim());
        if (ColleagueRenaming.Rename(_parts.Library, _parts.Sessions, new RenameOrder(name, newName)) is { } why) return why;
        var record = _parts.Library.Find(newName.Trim())!;
        foreach (var handle in HandlesOf(record))
            if (_workers.Find(handle) is { } kept) Reorigin(kept, record.Name);
        StateChanged?.Invoke();
        return $"{was?.Name ?? name.Trim()} is now called {record.Name}. Sessions that knew it by its old name still bring it back.";
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
            Distilling.Contains(record.Name, StringComparer.OrdinalIgnoreCase), task)
        {
            Frozen = record.Frozen,
        };
    }

    /// <summary>What every front end says when a colleague is frozen — one text, so none words it differently.</summary>
    public static string FrozenLine(string name) =>
        $"{name} is frozen: it works and its rounds are kept, but it learns nothing until unfrozen.";

    /// <summary>What every front end says when a colleague is unfrozen.</summary>
    public static string UnfrozenLine(string name) =>
        $"{name} learns again, from now on — what it did while frozen stays in its past, never in its experience.";

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
        // KEPT, IT IS THE COLLEAGUE'S TALK WORKER HERE: it holds a conversation, and has neither the experience nor the
        // recall a worker brought as a colleague is built with — its next ask goes to an Ask worker brought for it.
        _workers.KeepConversation(handle);
        _given[KeyOf(name)] = 0;
        Present(name);
        // IN THE SESSION'S HISTORY, so a resume brings it back as the colleague and not as the worker it was.
        _parts.NoteWorker(new WorkerMark(kept.Agent.Agent.Id, marked));
        _parts.NoteWorker(new WorkerMark(kept.Agent.Agent.Id, marked) { Talk = true });
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
        // ITS ASK WORKER HERE TAKES THE TASK; its Talk worker is a conversation of its own, opened beside it.
        if (!order.Talk && _workers.NameOfOrigin(origin) is { } live)
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
        var bringing = order.Talk ? origin + TalkSuffix : origin;
        if (!_bringing.TryAdd(bringing, 0)) return $"{record.Name} is already being brought in.";
        try
        {
            return await BringAsync(order, record, spawner, ct);
        }
        finally
        {
            _bringing.TryRemove(bringing, out _);
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

        _windows[KeyOf(record.Name)] = choice.Window!.Value;
        // THE ENTRY THAT PASSED THE CHECK, NAMED EVEN WHEN IT IS THE SESSION'S OWN: left out, the spawner would take the
        // type's own routing, which may be another, smaller model than the one the preload was sized for.
        var instance = choice.Instance;
        SubAgent? child = null;
        // NO TASK, NO RUN: it joins idle, and work reaches it as it reaches any worker.
        var envelope = await spawner.SpawnAsync(new SpawnOrder
        {
            Prompt = order.Talk ? "" : order.Task,
            Run = !order.Talk && order.Task.Trim().Length > 0,
            Description = order.Talk ? TalkDescription(record.Name) : record.Name,
            Type = record.Type,
            Instance = instance,
            Origin = origin,
            Handle = ColleagueStore.Slug(record.Name) + (order.Talk ? TalkSuffix : ""),
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
        // A TOOL OF ITS OWN LEFT OUT IS SAID: another tool in this session holds the name, and the colleague is told it
        // has what it now has not.
        if (child is { ToolsLeftOut.Count: > 0 })
            _parts.Notify?.Invoke($"{record.Name}: {string.Join(", ", child.ToolsLeftOut)} left out — another tool in this session holds that name.");
        var refused = child?.AugmentRefusal;
        var handle = SubAgentEnvelope.NameOf(envelope);
        if (refused is not null && handle is not null) _workers.SetOrigin(handle, null);

        // A SPAWN THE AGENT TOOL DID NOT MAKE, WRITTEN DOWN AS ONE, so a resume rebuilds it as it rebuilds every worker.
        if (child is not null && handle is not null)
            _parts.NoteWorker(new WorkerMark(child.Agent.Id, refused is null ? origin : null)
            {
                Name = handle, Type = record.Type, Instance = instance,
                Description = order.Talk ? TalkDescription(record.Name) : record.Name,
            });
        // WHICH OF ITS TWO IT IS, written down for a resume — see WorkerMark.Talk and WorkerMark.Asks.
        if (child is not null && handle is not null && refused is null)
        {
            _parts.NoteWorker(new WorkerMark(child.Agent.Id, origin) { Talk = order.Talk, Asks = !order.Talk });
            if (order.Talk)
            {
                _workers.KeepConversation(handle);
                child.Agent.ReplaceCallerContext(Briefing(record, talk: true));
                _talkGiven[handle] = record.ExperienceVersion;
            }
        }

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

    /// <summary>The handle the colleague named <paramref name="name"/> answers to in this session, or null when it is not here.</summary>
    public string? HandleHere(string name) => _workers.NameOfOrigin(OriginPrefix + name);

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
        // UNDER ANY NAME IT HAS HAD: a one-to-one begun before a rename is saved under the name it had then.
        var saved = _parts.Sessions is { } sessions
            ? ColleagueRenaming.Origins(record).SelectMany(sessions.ListTagged).FirstOrDefault()
            : null;

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
            // A ONE-TO-ONE READS ACROSS FOLDERS TOO, and what it learned there belongs where it was read.
            var reads = _parts.Library.Find(name) is { } record
                ? ReadsOf(new RoundReadsRequest(round, record, exchange.Folder) { Ignore = Path.GetDirectoryName(_parts.Library.Root) })
                : new RoundReads([], []);
            Append(name, new PastRound(0, exchange.SessionId, exchange.Folder,
                round[0].Timestamp == default ? DateTimeOffset.UtcNow : round[0].Timestamp, DateTimeOffset.UtcNow, round)
            {
                AlsoRead = reads.Workplaces,
                ReadElsewhere = reads.Elsewhere,
            });
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

        Present(record.Name);
        var here = record.Workplaces.Where(w => Directory.Exists(w.Folder)).Select(w => w.Folder).ToList();
        return new ChildAugment
        {
            Context = Briefing(record),
            ExtraTools = [new RecallTool(RecallScopeFor(record.Name))],
            ReadAlso = new Permissions.ReadAlso(here, _parts.Served),
        };
    }

    /// <summary>
    /// Who a colleague is and what it knows NOW — its briefing with its experience as the library holds it, sized to its
    /// window. Read when it is brought in, and again at the start of every ask.
    /// </summary>
    private string Briefing(ColleagueRecord record, bool talk = false)
    {
        var window = _windows.TryGetValue(KeyOf(record.Name), out var w) ? w
            : (record.Instance is { } own ? _parts.Models(own)?.Window : null) ?? _parts.Models(null)?.Window ?? ExperienceBudget.MinimumWindow;
        var preload = ExperienceBudget.Preload(_parts.Library.ReadExperience(record.Name),
            new ExperienceFocus(Codebase.Key(_parts.Folder), record.Workplaces, ExperienceBudget.For(window)));
        _given[KeyOf(record.Name)] = record.ExperienceVersion;

        // ITS WORKPLACES AS THEY ARE NOW: one moved or deleted is said in the briefing, never read — a folder that is no
        // longer there is not one the colleague knows the contents of.
        var here = record.Workplaces.Where(p => Directory.Exists(p.Folder)).Select(p => p.Folder).ToList();
        var gone = record.Workplaces.Where(p => !Directory.Exists(p.Folder)).Select(p => p.Folder).ToList();
        return IncarnationBriefing.For(record, preload, new IncarnationSetting(ReadsWorkplaces: here.Count > 0, gone) { TalkingWithUser = talk });
    }

    // A TALK WORKER'S HANDLE AND ROW: the colleague's own, marked as the conversation.
    private const string TalkSuffix = "-talk";

    private static string TalkDescription(string name) => $"{name} · talk";

    // THE EXPERIENCE VERSION EACH TALK WORKER LAST HEARD OF — briefed with, or told of since. See PrepareTalk.
    private readonly ConcurrentDictionary<string, long> _talkGiven = new(StringComparer.Ordinal);

    /// <summary>
    /// Opens a colleague's Talk worker here — the conversation the user holds with it in this session — or answers the
    /// one already here. Its handle, or why not.
    /// </summary>
    public async Task<TalkOpening> OpenTalkAsync(string name, string parentAgentId, CancellationToken ct)
    {
        if (_parts.Library.Find(name.Trim()) is not { } record)
            return new TalkOpening(null, $"There is no colleague called '{name.Trim()}'. {Known()}");
        if (_workers.ConversationOf(OriginPrefix + record.Name) is { } here) return new TalkOpening(here, null);
        var said = await BringAsync(new BringOrder(record.Name, "", null, Background: true, parentAgentId) { Talk = true }, ct);
        return _workers.ConversationOf(OriginPrefix + record.Name) is { } opened
            ? new TalkOpening(opened, null)
            : new TalkOpening(null, said);
    }

    /// <summary>
    /// Readies a colleague's incarnation for a new ask (user, 2026-11-11: "a colleague is its distilled knowledge; after a
    /// round is distilled, that is its starting point"). Waits until the library shows none of its rounds undistilled,
    /// then starts it over from its briefing as the library holds it now. Not for any other worker.
    ///
    /// <para>NOTHING CARRIED OVER: what it read, ran and answered in earlier asks is in its past, and its recall reads it
    /// back when it needs it; a conversation carried from ask to ask kept a wrong answer alive and grew every ask's cost.</para>
    /// </summary>
    private async Task PrepareAsync(string handle, CancellationToken ct)
    {
        if (_workers.Find(handle) is not { } kept || Incarnation(kept.Agent.Agent.Id) is not { } found) return;
        // A TALK IS A CONVERSATION: it goes on, and never waits — see PrepareTalk.
        if (IsTalk(handle))
        {
            PrepareTalk(kept, found.Name);
            return;
        }
        await WaitToLearnAsync(found.Name, handle, ct);
        if (_parts.Library.Find(found.Name) is not { } record) return;
        kept.Agent.Agent.StartOver();
        kept.Agent.Agent.ReplaceCallerContext(Briefing(record));
        // IN ITS TAB, WHERE THE EARLIER ASKS STILL SHOW: this one starts from what it knows, not from them.
        kept.Agent.Feed.Said(new Commands.Message(FreshLine(record.ExperienceVersion), Commands.Severity.Info));
    }

    /// <summary>The divider an ask's tab shows where a fresh ask begins.</summary>
    public static string FreshLine(long version) => $"— a fresh ask, from what it has learned (v{version}) —";

    /// <summary>
    /// The Ask worker for the colleague whose Talk worker is <paramref name="talk"/> — brought in idle if none is here — or
    /// null when it is no colleague's.
    /// </summary>
    private async Task<string?> AskWorkerForAsync(string talk, CancellationToken ct)
    {
        if (_workers.OriginOf(talk) is not { } origin || !origin.StartsWith(OriginPrefix, StringComparison.Ordinal)) return null;
        if (_workers.NameOfOrigin(origin) is { } asks) return asks;
        if (_parts.SessionId() is not { } lead) return null;
        await BringAsync(new BringOrder(origin[OriginPrefix.Length..], "", null, Background: true, lead), ct);
        return _workers.NameOfOrigin(origin);
    }

    /// <summary>Whether <paramref name="handle"/> is a colleague's Talk worker, which keeps its conversation; every other
    /// incarnation answers asks, each fresh from its knowledge.</summary>
    public bool IsTalk(string handle) => _workers.KeepsConversation(handle);

    /// <summary>
    /// Readies a Talk worker for the user's next message: briefed as talking with the user, once — a worker brought back
    /// or kept here was not — and told in one line when its experience grew since: its conversation goes on from where
    /// it was, so the newer version is a recall away rather than in its prompt.
    /// </summary>
    private void PrepareTalk(StoredAgent kept, string name)
    {
        if (_parts.Library.Find(name) is not { } record) return;
        if (!_talkGiven.TryGetValue(kept.Name, out var given))
        {
            kept.Agent.Agent.ReplaceCallerContext(Briefing(record, talk: true));
            _talkGiven[kept.Name] = record.ExperienceVersion;
            return;
        }
        if (record.ExperienceVersion <= given) return;
        _talkGiven[kept.Name] = record.ExperienceVersion;
        kept.Agent.Agent.Mailbox.TryEnqueue(VersionLine(record.ExperienceVersion), out _, wakes: false);
    }

    /// <summary>
    /// Starts a colleague's Talk conversation over (user, 2026-11-11): its context goes back to its briefing, with the
    /// knowledge as the library holds it now, and its tab says where the new conversation begins. Null when it did;
    /// otherwise why not. NOTHING IS LOST, so nothing is asked first: every exchange is already a round, distilled, and
    /// its past for recall.
    /// </summary>
    public string? NewConversation(string handle)
    {
        if (_workers.Find(handle) is not { } kept || Incarnation(kept.Agent.Agent.Id) is not { } found)
            return $"'{handle}' is not a colleague here.";
        // AN ASK HAS NO CONVERSATION: each one starts over already, so there is nothing to clear.
        if (!IsTalk(handle)) return $"{found.Name}'s asks each starts fresh from what it has learned — there is no conversation to clear.";
        if (_workers.IsBusy(handle)) return $"{found.Name} is answering — let it finish, or stop it, then start a new conversation.";
        if (_parts.Library.Find(found.Name) is not { } record) return $"There is no colleague called '{found.Name}'. {Known()}";
        kept.Agent.Agent.StartOver();
        kept.Agent.Agent.ReplaceCallerContext(Briefing(record, talk: true));
        _talkGiven[handle] = record.ExperienceVersion;
        kept.Agent.Feed.Said(new Commands.Message(NewTalkLine(record.ExperienceVersion), Commands.Severity.Info));
        return null;
    }

    /// <summary>The divider a Talk tab shows where a new conversation begins.</summary>
    public static string NewTalkLine(long version) => $"— a new conversation, from what it has learned (v{version}) —";

    /// <summary>What a Talk worker is told when its experience grew during the conversation.</summary>
    public static string VersionLine(long version) =>
        $"[Your experience is now v{version}: what you learned since this conversation began is in it, and recall reads it.]";

    // WAITS IN PROGRESS, by handle — what Don't wait cancels.
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _waits = new(StringComparer.Ordinal);

    /// <summary>How often a waiting ask looks at the library again.</summary>
    internal TimeSpan LearnPoll { get; set; } = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// Waits until the library shows none of <paramref name="name"/>'s rounds undistilled — read from its record, so a
    /// distil in another process counts — or until nothing is distilling them: a failed distil ends the wait, and the ask
    /// starts from the last good version. Don't wait (<see cref="SkipWait"/>) ends it at once.
    /// </summary>
    private async Task WaitToLearnAsync(string name, string handle, CancellationToken ct)
    {
        if (_parts.Library.Undistilled(name).Count == 0) return;
        using var skip = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _waits[handle] = skip;
        _workers.BeginWaitingToLearn(handle);
        StateChanged?.Invoke();
        try
        {
            var kicked = false;
            while (_parts.Library.Undistilled(name).Count > 0)
            {
                // NOTHING DISTILLING HERE AND NOTHING ELSEWHERE: start a drain once; if that ends with rounds still
                // waiting, the distil failed, and the ask goes on from what was learned before.
                if (_drains.IsCompleted && !LockHeldElsewhere())
                {
                    if (kicked) break;
                    kicked = true;
                    Drain();
                }
                await Task.Delay(LearnPoll, skip.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }   // Don't wait
        finally
        {
            _waits.TryRemove(handle, out _);
            _workers.EndWaitingToLearn(handle);
            StateChanged?.Invoke();
        }
    }

    /// <summary>Don't wait: the ask <paramref name="handle"/> is waiting with starts now, from what was learned before.</summary>
    public bool SkipWait(string handle)
    {
        if (!_waits.TryGetValue(handle, out var waiting)) return false;
        try { waiting.Cancel(); } catch (ObjectDisposedException) { return false; }
        return true;
    }

    // ANOTHER PROCESS DISTILLING: the lock is taken and let go at once, only to see whether it is free.
    private bool LockHeldElsewhere()
    {
        try
        {
            using var held = DistillingLock.TryTake(_parts.Library.Root);
            return held is null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    private void OnBegan(string agentId)
    {
        if (Incarnation(agentId) is not { } found) return;
        _rounds[agentId] = new RoundStart(found.Kept.Agent.Agent.Context.Messages.LastOrDefault(m => m.Role != "system"), DateTimeOffset.UtcNow);
    }

    /// <summary>A colleague's recall: its library, the served folders, and the session's gate to ask through.</summary>
    internal RecallScope RecallScopeFor(string colleague) => new(_parts.Library, colleague, _parts.Served)
    {
        Ask = _parts.Gate is { } gate
            ? async (request, context, ct) => (await new Jobs.CallAsker(gate, null).AskAsync(request, context, ct)).Allowed
            : null,
    };


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

            // FORGOTTEN — HERE OR IN ANOTHER SESSION — IT CARRIES ON AS AN ORDINARY WORKER: nothing is kept, no folder is
            // made, and the plain origin is recorded so a resume does not bring back a colleague nobody has.
            if (_parts.Library.Find(found.Name) is not { } record)
            {
                Unmark(found.Kept);
                return;
            }
            // FROM WHERE THE WORKER RAN — its own folder when it was given one — as its paths were resolved when it worked.
            var reads = ReadsOf(new RoundReadsRequest(round, record, _parts.Folder)
            {
                ResolveFrom = found.Kept.Agent.Agent.Setup.Identity.WorkingDir,
                Ignore = Path.GetDirectoryName(_parts.Library.Root),
            });
            Append(found.Name, new PastRound(0, _parts.SessionId() ?? "", _parts.Folder, start.At, DateTimeOffset.UtcNow, round)
            {
                AlsoRead = reads.Workplaces,
                ReadElsewhere = reads.Elsewhere,
            });
            Drain();
        }
        // EVERYTHING BUT A CANCELLATION: this runs inside the worker's own release, and an exception escaping it would skip
        // the listeners after this one — the run's own archive among them.
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _parts.Notify?.Invoke($"Could not keep {found.Name}'s latest round: {e.Message}");
        }
    }


    private void Append(string name, PastRound round)
    {
        _owned[KeyOf(name)] = _parts.Library.Append(name, round);

        // A SCRATCH FOLDER IS NO WORKPLACE: the round is kept in the past all the same — see Codebase.IsProject.
        if (!Codebase.IsProject(_parts.Folder)) return;
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
        return choice is not null && ExperienceBudget.WindowRefusal(choice.Window, choice.Label) is null ? Labelled(choice) : null;
    };

    /// <summary>
    /// The chosen model's provider, its label remembered for the spend its calls report. BY MODEL ID, because that is all
    /// a call's spend carries back; two entries serving one model id name the same model, and either label is true.
    /// </summary>
    private ILlmProvider Labelled(ModelChoice choice)
    {
        _labels[choice.Provider.ModelId] = choice.Label;
        return choice.Provider;
    }

    /// <summary>Each distilling model's label by its model id — see <see cref="Labelled"/>.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _labels = new(StringComparer.Ordinal);

    /// <summary>
    /// The folders a round read beyond the session's codebase: other workplaces it knows — distilled into their codebases
    /// too — and folders outside every one, named to the distiller so what was learned there is not filed under the
    /// session's codebase.
    ///
    /// <para>BY CODEBASE, NOT FOLDER NAME: a worktree and its main checkout are one codebase. Paths are resolved as the
    /// policy resolves them, from the folder the worker ran in, with <c>~</c> as home; a shell command's paths are found
    /// by the same parser the permission check uses. A path no file can have — one a model wrote with a NUL in it — is
    /// passed over. Folders outside every workplace are named by their codebase's root, at most five.</para>
    /// </summary>
    internal static RoundReads ReadsOf(RoundReadsRequest request)
    {
        var folder = Permissions.PermissionPolicy.RealFolder(request.Folder);
        var from = request.ResolveFrom is { } r ? Permissions.PermissionPolicy.RealFolder(r) : folder;
        var ignore = request.Ignore is { } i ? Permissions.PermissionPolicy.RealFolder(i) : null;
        var here = Codebase.Key(folder);
        var known = request.Record.Workplaces.Where(w => w.Codebase != here)
            .GroupBy(w => w.Codebase).ToDictionary(g => g.Key, g => g.First().Folder, StringComparer.Ordinal);
        // READS THAT HAPPENED: a failed call read nothing, so its folder taught nothing.
        var failed = request.Round.Where(m => m.ToolCallId is { } id && m.Content.TrimStart().StartsWith("error", StringComparison.OrdinalIgnoreCase))
            .Select(m => m.ToolCallId!).ToHashSet(StringComparer.Ordinal);
        var workplaces = new List<string>();
        var elsewhere = new List<string>();
        foreach (var path in request.Round.SelectMany(m => m.ToolCalls ?? [])
                     .Where(call => Reads.Contains(call.Name) && (call.Id is null || !failed.Contains(call.Id)))
                     .SelectMany(call => PathsOf(call, from, request.Home)))
        {
            if (path.Contains('\0') || Permissions.PathResolver.Contains(folder, path) || Permissions.PathResolver.Contains(from, path)
                || (ignore is not null && Permissions.PathResolver.Contains(ignore, path))) continue;
            var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            if (dir is null) continue;
            string key, root;
            try { (key, root) = (Codebase.Key(dir), Codebase.Root(dir)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { continue; }
            if (key == here) continue;
            if (known.TryGetValue(key, out var workplace)) { if (!workplaces.Contains(workplace)) workplaces.Add(workplace); }
            else if (!elsewhere.Contains(root) && elsewhere.Count < 5) elsewhere.Add(root);
        }
        return new RoundReads(workplaces, elsewhere);
    }

    /// <summary>A <c>~/</c> at the start of a word — a home-relative path as a shell would expand it.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"(?<=^|[\s;&|(=])~/")]
    private static partial System.Text.RegularExpressions.Regex TildeAtWord();

    /// <summary>The tools whose paths are reads — a write teaches where something was put, not what is there.</summary>
    private static readonly HashSet<string> Reads = new([Jobs.Tool.ReadFile, Jobs.Tool.Glob, Jobs.Tool.Grep, Jobs.Tool.RunShell], StringComparer.Ordinal);

    /// <summary>The paths one tool call names, resolved: a file tool's <c>path</c>, or what a shell command touches.</summary>
    private static IEnumerable<string> PathsOf(ToolCall call, string folder, string home)
    {
        if (call.Arguments.ValueKind != JsonValueKind.Object) return [];
        if (call.Name == Jobs.Tool.RunShell)
        {
            if (!call.Arguments.TryGetProperty("command", out var c) || c.ValueKind != JsonValueKind.String) return [];
            // ~ EXPANDED HERE, AND ONLY HERE: the permission check's parser rightly leaves it alone — a second resolver
            // there is how the boundary and the prompt come to disagree — but this decides only where knowledge is filed,
            // and "cd ~/source/lib && git show …" is the common way a library's source is read.
            var command = TildeAtWord().Replace(c.GetString() ?? "", Path.TrimEndingDirectorySeparator(home) + "/");
            var subjects = Permissions.CommandSubjects.Of(command, new Permissions.PathResolver(folder));
            return subjects.ChangesTo is { Length: > 0 } cd
                ? subjects.Paths.Append(Path.GetFullPath(cd, folder))
                : subjects.Paths;
        }
        if (!call.Arguments.TryGetProperty("path", out var p) || p.ValueKind != JsonValueKind.String
            || p.GetString() is not { Length: > 0 } raw || raw.Contains('\0')) return [];
        var spelled = raw.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(home, raw[2..]) : raw;
        try { return [Permissions.PermissionPolicy.ResolveAgainst(spelled, folder) ?? Path.GetFullPath(spelled, folder)]; }
        catch (ArgumentException) { return []; }
    }

    private Found? Incarnation(string agentId)
    {
        if (_workers.FindByAgentId(agentId) is not { } kept) return null;
        if (_workers.OriginOf(kept.Name) is not { } origin || !origin.StartsWith(OriginPrefix, StringComparison.Ordinal)) return null;
        // BY ITS NAME NOW: a worker brought in before a rename elsewhere still carries the old one, and its rounds, its
        // growth and what is said when it learns all go by the colleague's name as the library has it.
        var name = origin[OriginPrefix.Length..];
        if (_parts.Library.Find(name) is not { } record) return new Found(kept, name);
        // RENAMED ELSEWHERE — F8, a browser, another process — the worker takes the name now, as a rename here gives it.
        if (!string.Equals(record.Name, name, StringComparison.Ordinal)) Reorigin(kept, record.Name);
        return new Found(kept, record.Name);
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

    /// <summary>
    /// Raised once for a worker the user or the agent keeps coming back to — for a front end to offer Keep and Not this one
    /// under its answer. See <see cref="Weigh"/>.
    /// </summary>
    public event Action<KeepSuggestion>? KeepSuggested;

    /// <summary>How many steers by the user make a worker worth offering — conservative, the spec's floor.</summary>
    public const int SuggestAfterSteers = 2;

    /// <summary>How many follow-ups from the agent make a worker worth offering.</summary>
    public const int SuggestAfterFollowUps = 3;

    /// <summary>How many rounds of work in this session — one session, one area — make a worker worth offering.</summary>
    public const int SuggestAfterRounds = 4;

    /// <summary>Workers already offered, or turned down with Not this one — each is asked about once.</summary>
    private readonly ConcurrentDictionary<string, byte> _suggested = new(StringComparer.Ordinal);

    /// <summary>Rounds each worker has ended here, by agent id.</summary>
    private readonly ConcurrentDictionary<string, int> _stretches = new(StringComparer.Ordinal);

    /// <summary>
    /// Keep, pressed under a suggestion: the worker kept under a name taken from what it was doing — as typing
    /// <c>/colleagues keep</c> with no name does. Answers the line to show.
    /// </summary>
    public string KeepOffered(string handle)
    {
        _suggested[handle] = 0;
        var said = Keep(new KeepOrder(handle, null));
        OfferAnswered?.Invoke(handle);
        return said;
    }

    /// <summary>
    /// Not this one: the worker is never offered again — recorded in the session's history, so a resume does not offer
    /// it either.
    /// </summary>
    public void Dismiss(string handle)
    {
        _suggested[handle] = 0;
        _workers.Decline(handle);
        if (_workers.Find(handle) is { } kept)
            _parts.NoteWorker(new WorkerMark(kept.Agent.Agent.Id, _workers.OriginOf(handle)) { Declined = true });
        OfferAnswered?.Invoke(handle);
    }

    /// <summary>
    /// An offer was answered — Keep or Not this one, in either front end — so every front end showing it takes its
    /// buttons away. One answer per offer: the other front end's buttons would otherwise keep a worker turned down.
    /// </summary>
    public event Action<string>? OfferAnswered;

    /// <summary>
    /// Freezes or unfreezes a colleague and answers the line to show; the panel and cards redraw to say so. Null record:
    /// no such colleague.
    /// </summary>
    public string Freeze(string name, bool freeze)
    {
        var record = freeze ? _parts.Library.Freeze(name) : _parts.Library.Unfreeze(name);
        if (record is null) return $"There is no colleague called '{name}'. /colleagues lists them.";
        StateChanged?.Invoke();
        return freeze ? FrozenLine(record.Name) : UnfrozenLine(record.Name);
    }

    /// <summary>
    /// At the end of each stretch of a worker's work, whether it has earned an offer: steered twice by the user, followed
    /// up three times by the agent, or four rounds here (spec §9, thresholds conservative). ONCE PER WORKER, and never for
    /// a colleague's own worker, in a one-to-one, or with suggestions or colleagues off.
    /// </summary>
    private void Weigh(string agentId, string? outcome)
    {
        if (!_parts.Suggest || _parts.OneToOne || KeepSuggested is null) return;
        if (_workers.FindByAgentId(agentId) is not { } kept || _workers.OriginOf(kept.Name) is not null || _workers.Declined(kept.Name)) return;
        var rounds = _stretches.AddOrUpdate(agentId, 1, (_, n) => n + 1);
        if (_suggested.ContainsKey(kept.Name)) return;
        var sends = _workers.SendsTo(kept.Name);
        var why = sends.User >= SuggestAfterSteers ? $"you steered it {sends.User} times"
            : sends.Lead >= SuggestAfterFollowUps ? $"the agent sent it {sends.Lead} follow-ups"
            : rounds >= SuggestAfterRounds ? $"it has done {rounds} rounds of work here"
            : null;
        if (why is null || !_suggested.TryAdd(kept.Name, 0)) return;
        var offer = new KeepSuggestion(kept.Name, why);
        if (SuggestDelay <= TimeSpan.Zero) Offer(offer);
        // A MOMENT LATER, NOT NOW: the end of a stretch is announced inside the worker's release, before whoever asked
        // has shown its answer — raised now, the offer drew above the answer it is about.
        else _ = Task.Delay(SuggestDelay).ContinueWith(_ => Offer(offer), TaskScheduler.Default);
    }

    /// <summary>How long after a stretch ends its offer is made — see <see cref="Weigh"/>.</summary>
    internal TimeSpan SuggestDelay { get; set; } = TimeSpan.FromSeconds(1);

    private void Offer(KeepSuggestion offer)
    {
        // A LISTENER'S FAILURE IS ITS OWN: nothing that offers may take down what ended the stretch.
        try { KeepSuggested?.Invoke(offer); }
        catch (Exception e) when (e is not OperationCanceledException) { }
    }

    /// <summary>This desk's mark in a colleague's <c>live</c> folder — one per session, whatever its id becomes.</summary>
    private readonly string _holder = Guid.NewGuid().ToString("N");

    /// <summary>The colleagues this desk has marked live.</summary>
    private readonly ConcurrentDictionary<string, byte> _present = new(StringComparer.OrdinalIgnoreCase);

    private void Present(string name)
    {
        _present[name] = 0;
        _parts.Library.MarkPresent(name, _holder);
    }

    /// <summary>A worker let go here: a colleague with no incarnation left here is no longer live in this session.</summary>
    private void OnWorkerForgotten(string agentId)
    {
        foreach (var name in _present.Keys)
            if (_parts.Library.Find(name) is not { } record || !HandlesOf(record).Any())
            {
                _present.TryRemove(name, out _);
                _parts.Library.MarkAbsent(name, _holder);
            }
    }

    /// <summary>
    /// The question distilling again asks — ONE TEXT FOR BOTH FRONT ENDS — naming the rounds, the model and the
    /// experience it folds into; or, with no order, why there is nothing to ask. Its last round when none is named.
    /// </summary>
    public DistillAsk AskDistill(DistillOrder order)
    {
        if (_parts.Library.Find(order.Name.Trim()) is not { } record)
            return new DistillAsk(null, $"There is no colleague called '{order.Name.Trim()}'. {Known()}");
        // FROZEN MEANS ITS EXPERIENCE STAYS AS IT IS (user, 2026-11-10): distilling again would change it.
        if (record.Frozen)
            return new DistillAsk(null, $"{record.Name} is frozen, so its experience stays as it is — unfreeze it first: /colleagues unfreeze {record.Name}.");
        var rounds = _parts.Library.RoundCount(record.Name);
        if (rounds == 0) return new DistillAsk(null, $"{record.Name} has no rounds to distil yet.");
        var from = order.From ?? rounds;
        if (from < 1 || from > rounds)
            return new DistillAsk(null, $"{record.Name} has rounds 1 to {rounds}; there is no round {from}.");
        // THE MODEL THAT WOULD RUN IT, checked before asking — a named one, or its own distilling model: a yes to a run
        // that then refuses its model asks for nothing.
        var own = record.DistillInstance ?? record.Instance;
        var choice = order.Model is { } entry ? _parts.Models(entry)
            : (own is not null ? _parts.Models(own) : null) ?? _parts.Models(null);
        if (choice is null)
            return new DistillAsk(null, order.Model is { } missing
                ? $"There is no provider entry called '{missing}'."
                : $"{record.Name}'s distilling model is not configured here.");
        if (ExperienceBudget.WindowRefusal(choice.Window, choice.Label) is { } small) return new DistillAsk(null, small);
        var which = from == rounds ? $"round {from}" : $"rounds {from}–{rounds}";
        var on = order.Model is { } m ? $"on {m}" : "on its own distilling model";
        return new DistillAsk(new DistillOrder(record.Name, from, order.Model),
            $"Distil {which} of {record.Name} again, {on}? What it learns is folded into its current experience "
            + $"(v{record.ExperienceVersion}), and each round costs a call to the model.");
    }

    /// <summary>
    /// How a distil-again ended, said where it was asked — ONE LINE FOR BOTH FRONT ENDS: the experience's new version, or
    /// that there was nothing new to keep. Without it the question kept saying "distilling…" after the work was done.
    /// </summary>
    /// <param name="name">The colleague.</param>
    /// <param name="before">Its experience version when the distil began.</param>
    public string DistilledAgainLine(string name, long before) =>
        _parts.Library.Find(name.Trim()) is not { } record
            ? $"{name.Trim()} distilled again."
            : record.ExperienceVersion > before
                ? $"{record.Name} distilled again — its experience is now v{record.ExperienceVersion}."
                : $"{record.Name} distilled again — nothing new to keep, experience still v{record.ExperienceVersion}.";

    /// <summary>
    /// Distils the asked rounds again, as the user said yes to — see <see cref="Distiller.RedistilAsync"/> — and answers
    /// why it could not, or null when it did: what it learned is said like any distilling's, through the session.
    /// </summary>
    public async Task<string?> DistillAgainAsync(DistillOrder order)
    {
        var ask = AskDistill(order);
        if (ask.Order is not { } ready) return ask.Text;
        var provider = ready.Model is { } entry && _parts.Models(entry) is { } named ? Labelled(named) : null;
        Func<string?, ILlmProvider?> providerFor = provider is not null ? _ => provider : DistillModel(ready.Name);
        // SAID HERE AS THIS SESSION'S OWN: the change it makes is announced as "learned from its work", as a round lived
        // here is — the claim is the last round the rerun reaches.
        _owned[KeyOf(ready.Name)] = _parts.Library.RoundCount(ready.Name);
        try
        {
            return await _distiller.RedistilAsync(new RedistilRequest(ready.Name, ready.From!.Value, providerFor), CancellationToken.None);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return $"Could not distil {ready.Name} again: {e.Message}";
        }
    }

    /// <summary>A worker of a colleague that is gone, made an ordinary one — in memory, and recorded for a resume.</summary>
    private void Unmark(StoredAgent kept)
    {
        // NOT OFFERED BACK AS A COLLEAGUE: the user just forgot the one it was, and its sends were counted while it was.
        _suggested[kept.Name] = 0;
        _workers.SetOrigin(kept.Name, null);
        _parts.NoteWorker(new WorkerMark(kept.Agent.Agent.Id, null));
    }

    /// <summary>
    /// Lets a colleague go from this session — ONE ANSWER FOR EVERY DOOR: the command, its card, its tab and the lead's
    /// tool. Its worker is let go as any worker is, so it is not resumed and its tab closes; what it learned here is
    /// already the library's, filed as each of its tasks ended. It can be brought again.
    ///
    /// <para>NOT WHILE IT WORKS: a task ended under it would be a stop nobody chose. Stopping is its own step.</para>
    /// </summary>
    public string Release(string name)
    {
        // ONE RELEASE AT A TIME: each press of a tab's Release runs on its own, and releases that overlapped all found the
        // workers still here — each let them go and each said so.
        lock (_releasing) return ReleaseLocked(name);
    }

    private readonly object _releasing = new();

    private string ReleaseLocked(string name)
    {
        if (_parts.Library.Find(name.Trim()) is not { } record) return $"There is no colleague called '{name.Trim()}'. {Known()}";
        var handles = HandlesOf(record).ToList();
        if (handles.Count == 0) return $"{record.Name} is not in this session.";
        // ITS ASK WORKER AND ITS TALK WORKER BOTH GO — and neither while one works: a release that let one go and left the
        // other would be half a release.
        if (handles.FirstOrDefault(_workers.IsBusy) is { } working)
            return $"{record.Name} is working — stop it first (/tasks stop {working}), then release it.";
        foreach (var handle in handles)
            switch (_workers.Forget(handle))
            {
                case Agents.ForgetOutcome.Working:
                    return $"{record.Name} is working — stop it first (/tasks stop {handle}), then release it.";
                case Agents.ForgetOutcome.ReportOwed:
                    return $"{record.Name} still owes its report to the agent that set it working — release it once that is delivered.";
            }
        return $"{record.Name} has left this session. Its rounds here are kept; /colleagues bring {record.Name} brings it back.";
    }

    /// <summary>
    /// The question forgetting asks — ONE TEXT FOR BOTH FRONT ENDS — naming what goes and where it is live; or, with no
    /// name, why there is nothing to ask.
    /// </summary>
    public ForgetAsk AskForget(string name)
    {
        if (_parts.Library.Find(name.Trim()) is not { } record)
            return new ForgetAsk(null, $"There is no colleague called '{name.Trim()}'. {Known()}");
        var rounds = _parts.Library.RoundCount(record.Name);
        var folders = record.Workplaces.OrderByDescending(w => w.Last).Select(w => w.Folder).ToList();
        var sb = new StringBuilder();
        sb.Append($"Forget {record.Name} for good? Its past ({rounds} round{(rounds == 1 ? "" : "s")}), its experience ")
          .Append($"(v{record.ExperienceVersion})");
        if (folders.Count > 0) sb.Append($" and what it learned in the folders it worked in ({string.Join(", ", folders)}) — the folders themselves are untouched —");
        sb.Append(" are deleted, and its one-to-one with them.");
        var live = _parts.Library.LiveSessions(record.Name);
        if (live > 0) sb.Append($" It is live in {live} session{(live == 1 ? "" : "s")} now; there it carries on as an ordinary worker.");
        return new ForgetAsk(record.Name, sb.ToString());
    }

    /// <summary>
    /// Forgets a colleague, as the user has said yes to, and answers the line to show. Sessions keep their own histories.
    ///
    /// <para>IN THIS ORDER, so a refusal changes nothing: its one-to-one open anywhere refuses (it would carry on keeping
    /// rounds for nobody); then the distilling lock, so no distiller writes into what is being deleted; then the folder,
    /// moved aside in one step; and only once it is gone, its one-to-one sessions and its workers here.</para>
    /// </summary>
    public string Forget(string name)
    {
        if (_parts.Library.Find(name.Trim()) is not { } record) return $"There is no colleague called '{name.Trim()}'.";
        if (OneToOneOpen(record) is { } open) return open;
        var key = KeyOf(record.Name);

        DistillingLock? lockHeld;
        try { lockHeld = DistillingLock.TakeWithin(_parts.Library.Root, DistillingLock.BriefWait); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return $"Could not forget {record.Name}: {e.Message}"; }
        if (lockHeld is null) return "A colleague is being distilled — try again in a moment.";
        using (lockHeld)
        {
            // ASKED AGAIN UNDER THE LOCK: a one-to-one opened since the first look would lose its session beneath it.
            if (OneToOneOpen(record) is { } since) return since;
            try { _parts.Library.Retire(record.Name); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return $"Could not forget {record.Name} — something holds its folder ({e.Message}). Nothing was deleted; try again.";
            }
        }

        var notes = new List<string>();
        if (_parts.Sessions is { } store)
            foreach (var tag in ColleagueRenaming.Origins(record))
                foreach (var saved in store.ListTagged(tag))
                    try { store.Delete(saved.Uid); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { notes.Add($"its one-to-one could not be deleted: {e.Message}"); }
        foreach (var handle in HandlesOf(record).ToList())
            if (_workers.Find(handle) is { } kept)
            {
                Unmark(kept);
                notes.Add($"{handle} carries on here as an ordinary worker");
            }
        _present.TryRemove(record.Name, out _);
        _given.TryRemove(key, out _);
        _windows.TryRemove(key, out _);
        _owned.TryRemove(key, out _);
        StateChanged?.Invoke();
        return $"{record.Name} is forgotten." + (notes.Count > 0 ? " " + string.Join("; ", notes) + "." : "");
    }

    /// <summary>Why its one-to-one, open in some process, stops a forget — or null when none is open.</summary>
    private string? OneToOneOpen(ColleagueRecord record)
    {
        if (_parts.Sessions is not { } sessions) return null;
        foreach (var tag in ColleagueRenaming.Origins(record))
            foreach (var saved in sessions.ListTagged(tag))
                if (sessions.HolderOf(saved.Uid) is { } held)
                    return $"{record.Name}'s one-to-one is {Storage.SessionLock.HeldMessage(held)} — close it first.";
        return null;
    }

    /// <summary>
    /// What the desk keys a colleague's state by: its folder, which a rename never moves — so what was given, what was
    /// sized and which round is this session's survive a rename made anywhere, here or not.
    /// </summary>
    private string KeyOf(string name) => _parts.Library.FolderOf(name);

    /// <summary>A worker of a renamed colleague, given its name now — in memory, and recorded for a resume.</summary>
    private void Reorigin(StoredAgent kept, string name)
    {
        var origin = OriginPrefix + name;
        _workers.SetOrigin(kept.Name, origin);
        _parts.NoteWorker(new WorkerMark(kept.Agent.Agent.Id, origin));
    }

    public void Dispose()
    {
        if (_workers.Badges == (Func<string, WorkerBadge?>)BadgeFor) _workers.Badges = null;
        _workers.SendBegan -= OnBegan;
        _workers.SendEnded -= OnEnded;
        _workers.SendEnded -= Weigh;
        _workers.Forgotten -= OnWorkerForgotten;
        foreach (var name in _present.Keys) _parts.Library.MarkAbsent(name, _holder);
    }

    private readonly record struct RoundStart(ChatMessage? Last, DateTimeOffset At);

    private sealed record Found(StoredAgent Kept, string Name);
}
