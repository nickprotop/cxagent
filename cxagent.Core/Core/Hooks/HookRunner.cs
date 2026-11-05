using System.Text.Json.Nodes;

namespace CxAgent.Core.Hooks;

/// <summary>Something a hook run has to tell the user: a failure, or a hook's own <c>systemMessage</c>.</summary>
public sealed record HookNotice(string Text, bool IsError);

/// <summary>
/// What every hook an event reached decided, combined into one answer.
/// </summary>
/// <param name="Decision"><c>deny</c>, <c>defer</c>, <c>ask</c>, <c>allow</c> or <c>block</c>; null when no hook decided.</param>
/// <param name="Reason">The deciding hook's reason.</param>
/// <param name="UpdatedInput">The tool's arguments after every rewrite, in cxagent's names; null when none rewrote.</param>
/// <param name="Context">Text for the model, from every hook that added some.</param>
/// <param name="DecidedBy">The deciding hook's name.</param>
public sealed record HookVerdict(string? Decision, string? Reason, JsonObject? UpdatedInput,
    IReadOnlyList<string> Context, string? DecidedBy)
{
    /// <summary>Set when a hook asked the whole turn to stop (<c>continue: false</c>), with its reason.</summary>
    public string? Halt { get; init; }

    /// <summary>The hooks that ran.</summary>
    public IReadOnlyList<string> Fired { get; init; } = [];

    public static HookVerdict None { get; } = new(null, null, null, [], null);

    /// <summary>Whether the action should not happen: a deny or a block.</summary>
    public bool Stops => Decision is "deny" or "block";
}

/// <summary>What a session's runner is built from.</summary>
/// <param name="ProjectTrusted">Whether the folder is trusted NOW — asked at every event, never cached.</param>
internal sealed record HookRunnerDeps(HookSnapshot Snapshot, HookHandlers Handlers, Func<bool> ProjectTrusted, Action<HookNotice> Notice)
{
    /// <summary>Runs one command hook. A seam for tests; production runs a real process.</summary>
    public Func<HookRun, CancellationToken, Task<HookReply>> Run { get; init; } = HookProcess.RunAsync;

    /// <summary>Where command hooks run: the repository's top, or the working folder outside one.</summary>
    public string ProjectDir { get; init; } = Environment.CurrentDirectory;
}

/// <summary>
/// One session's hooks: finds the ones an event reaches, runs them, and combines what they answer.
///
/// <para>TRUST IS ASKED AT EVERY EVENT. A folder trusted mid-session runs its hooks from that moment, and
/// one whose trust is revoked stops at once; a decision cached at startup would honour a choice the user
/// has since taken back.</para>
///
/// <para>DENY BEATS DEFER BEATS ASK BEATS ALLOW. Several hooks may answer one call, and the strictest
/// wins: a guard is only a guard if a permissive hook beside it cannot out-vote it.</para>
/// </summary>
public sealed class HookRunner
{
    private readonly HookRunnerDeps _deps;
    private volatile HookSnapshot _snapshot;

    internal HookRunner(HookRunnerDeps deps)
    {
        _deps = deps;
        _snapshot = deps.Snapshot;
    }

    /// <summary>The hooks as they stand: what F8 and <c>/hooks</c> show.</summary>
    public HookSnapshot Snapshot => _snapshot;

    /// <summary>The library's handlers.</summary>
    public HookHandlers Handlers => _deps.Handlers;

    /// <summary>Whether the repository's hooks may run right now.</summary>
    public bool ProjectTrusted => _deps.ProjectTrusted();

    /// <summary>
    /// Who the session is, for the common fields of every hook's input. Set by whoever builds the session;
    /// an agent fills in its own identity on top (<see cref="HookEventContext.Agent"/>).
    /// </summary>
    public Func<HookEventContext> Session { get; set; } =
        () => new("", "", Environment.CurrentDirectory, "default", "main");

    private readonly List<string> _startContext = [];

    /// <summary>Holds what SessionStart hooks said for the model, until the first prompt carries it.</summary>
    internal void HoldStartContext(IEnumerable<string> context)
    {
        lock (_startContext) _startContext.AddRange(context);
    }

    /// <summary>Takes the held SessionStart context — once, so it is said at the start and not with every prompt.</summary>
    internal IReadOnlyList<string> TakeStartContext()
    {
        lock (_startContext)
        {
            var taken = _startContext.ToList();
            _startContext.Clear();
            return taken;
        }
    }

    /// <summary>Swaps in a reviewed set of hooks — F8's save, or the user accepting a changed file.</summary>
    public void Replace(HookSnapshot reviewed) => _snapshot = reviewed;

    /// <summary>Whether anything at all listens to this event, so callers can skip building its input.</summary>
    public bool Listens(string eventName) =>
        _deps.Handlers.Any(eventName) || _snapshot.Hooks.Any(h => h.Event == eventName && h.Runs);

    /// <summary>
    /// Runs every hook this event reaches and combines their answers.
    /// </summary>
    /// <param name="matchOn">What a matcher is tested against: the tool name on tool events, the event's own
    /// subtype elsewhere (a session start's source); null where the event takes no matcher.</param>
    public async Task<HookVerdict> RunAsync(string eventName, string? matchOn, JsonObject input, CancellationToken ct)
    {
        var trusted = _deps.ProjectTrusted();
        var hooks = _snapshot.Hooks
            .Where(h => h.Event == eventName && h.Runs)
            .Where(h => h.Source is HookSource.User or HookSource.Library || trusted)
            .Where(h => matchOn is null || HookNames.Matches(h.Matcher, matchOn))
            .ToList();

        var replies = await Task.WhenAll(hooks.Select(h => _deps.Run(new HookRun(h, input, _deps.ProjectDir), ct)));

        var acc = new Accumulator(eventName, matchOn);
        // SOURCE ORDER, not completion order: rewrites apply user, project, local, and the last one stands,
        // so a run that finished in a different order must not change which rewrite won.
        foreach (var (hook, reply) in hooks.Zip(replies).OrderBy(p => p.First.Source))
            acc.Take(hook, reply, _deps.Notice);

        foreach (var handle in _deps.Handlers.For(eventName))
        {
            var json = (JsonObject)input.DeepClone();
            if (acc.Rewrite is not null) json["tool_input"] = HookNames.WithAliases(matchOn ?? "", acc.Rewrite);
            HookAnswer answer;
            try { answer = await handle(new HookInput(eventName, json), ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _deps.Notice(new($"hook handler failed: {ex.Message}", IsError: true));
                continue;
            }
            acc.TakeAnswer("library handler", answer);
        }

        return acc.Result([.. hooks.Select(h => h.DisplayName)]);
    }

    /// <summary>Combines answers in the order they are taken.</summary>
    private sealed class Accumulator(string eventName, string? toolName)
    {
        private string? _decision;
        private string? _reason;
        private string? _decidedBy;
        private string? _halt;
        private readonly List<string> _context = [];

        public JsonObject? Rewrite { get; private set; }

        public void Take(HookDefinition hook, HookReply reply, Action<HookNotice> notice)
        {
            var name = hook.DisplayName;
            switch (reply.Kind)
            {
                case HookOutcomeKind.Failed:
                    // A FAILED GUARD THAT WAS TOLD TO HOLD, holds — but only where holding means something.
                    if (hook.OnFailureBlock && HookEvents.CanBlock(eventName))
                        Decide(BlockWord, $"hook '{name}' failed ({reply.Reason})", name);
                    else
                        notice(new($"hook '{name}' failed: {reply.Reason}", IsError: true));
                    return;

                case HookOutcomeKind.Block:
                    Decide(BlockWord, string.IsNullOrWhiteSpace(reply.Reason) ? $"blocked by hook '{name}'" : reply.Reason, name);
                    return;
            }

            if (reply.Decision is { } d) Decide(Normalise(d), reply.DecisionReason, name);
            if (reply.UpdatedInput is { } updated && toolName is not null)
                Rewrite = HookNames.ToCxagentInput(toolName, updated);
            if (reply.AdditionalContext is { Length: > 0 } ctx) _context.Add(ctx);
            // PLAIN STDOUT IS CONTEXT where the contract says so: a prompt and a session's start. Elsewhere it
            // is the hook talking to itself and goes nowhere.
            else if (reply.Json is null && eventName is HookEvents.UserPromptSubmit or HookEvents.SessionStart
                     && reply.Stdout.Trim() is { Length: > 0 } text)
                _context.Add(text);
            if (reply.SystemMessage is { Length: > 0 } msg) notice(new($"hook '{name}': {msg}", IsError: false));
            if (!reply.Continue) _halt ??= reply.StopReason ?? $"stopped by hook '{name}'";
        }

        public void TakeAnswer(string name, HookAnswer answer)
        {
            if (answer.Decision is { } d) Decide(Normalise(d), answer.Reason, name);
            if (answer.Rewrite is { } r) Rewrite = (JsonObject)r.DeepClone();
            if (answer.Context is { Length: > 0 } c) _context.Add(c);
        }

        /// <summary>A block on a tool call is a denial of it; elsewhere it stays a block.</summary>
        private string BlockWord => eventName is HookEvents.PreToolUse or HookEvents.PermissionRequest ? "deny" : "block";

        private string Normalise(string d) => d switch
        {
            "block" => BlockWord,
            // THE CONTRACT'S OLDER WORDS for the same two answers, still found in shared scripts.
            "approve" => "allow",
            _ => d,
        };

        private static int Rank(string? d) => d switch
        {
            "deny" or "block" => 4,
            "defer" => 3,
            "ask" => 2,
            "allow" => 1,
            _ => 0,
        };

        private void Decide(string decision, string? reason, string by)
        {
            if (Rank(decision) == 0 || Rank(decision) <= Rank(_decision)) return;
            _decision = decision;
            _reason = reason;
            _decidedBy = by;
        }

        public HookVerdict Result(IReadOnlyList<string> fired) =>
            new(_decision, _reason, Rewrite, _context, _decidedBy) { Halt = _halt, Fired = fired };
    }
}
