using CxAgent.Core.Commands;
using CxAgent.Core.Llm;

namespace CxAgent.Core.Sessions;

/// <summary>
/// How hard this session's model thinks: chosen with <c>/think</c> or F8, pending until the next
/// message, saved with the session, and checked against what the model offers.
/// </summary>
public sealed partial class Session
{
    private string? _pendingThinking;
    private bool _hasPending;
    private bool _thinkingChecked;

    private IThinkingProvider? Thinker => Provider as IThinkingProvider;

    /// <summary>The level requests go at now; null means the model's own default.</summary>
    public string? ThinkingLevel => Thinker?.ThinkingLevel;

    /// <summary>
    /// A level chosen while a turn ran, waiting for the next message; null when none is. "default"
    /// when the choice was the model's own default.
    /// </summary>
    public string? PendingThinking => _hasPending ? _pendingThinking ?? "default" : null;

    /// <summary>What the model offers, once it has said; null while it has not.</summary>
    public ThinkingOptions? KnownThinkingOptions => Thinker?.KnownThinkingOptions;

    /// <summary>What the model offers, asked once (bounded, cached) when not yet known.</summary>
    public Task<ThinkingOptions> ThinkingOptionsAsync() =>
        Thinker?.GetThinkingOptionsAsync()
        ?? Task.FromResult(ThinkingOptions.None("this model has no thinking levels"));

    /// <summary>
    /// Writes the instance's default level to config — set by a front end that owns config.json.
    /// Returns false when the write failed, which the session says.
    /// </summary>
    public Func<string?, bool>? SaveThinkingDefault { get; set; }

    /// <summary>
    /// Sets the level (null = the model's default), now when idle and at the next message when a
    /// turn is running. <paramref name="save"/> also makes it the default for new sessions.
    /// </summary>
    public CommandStatus SetThinking(string? level, bool save = false)
    {
        var options = KnownOptionsOrNotYet();
        if (!options.Any)
        {
            Say(new Message($"No thinking levels: {options.Why}."));
            return CommandStatus.Reported;
        }
        if (level is not null && !options.Levels.Contains(level))
        {
            Say(new Message($"'{level}' is not offered by {ModelName}. Levels: {string.Join(", ", options.Levels)}."));
            return CommandStatus.Refused;
        }
        if (save && SaveThinkingDefault?.Invoke(level) == false)
            Say(new Message("The level was not saved to config.", Severity.Warning));

        // NEVER INSIDE A TURN. Anthropic silently turns thinking off for a request whose level
        // differs from the rest of its tool loop, so a change made while the model works waits for
        // the next message — and says so on the mode line meanwhile.
        if (IsBusy)
        {
            _pendingThinking = level;
            _hasPending = true;
            Announce(SessionChangeKind.Thinking);
            return CommandStatus.Changed;
        }

        Apply(level);
        return CommandStatus.Changed;
    }

    /// <summary>F8: the next offered level, wrapping.</summary>
    public CommandStatus CycleThinking()
    {
        var options = KnownOptionsOrNotYet();
        if (!options.Any)
        {
            Say(new Message($"No thinking levels: {options.Why}."));
            return CommandStatus.Reported;
        }

        var from = _hasPending ? _pendingThinking : ThinkingLevel;
        var at = from is null ? -1 : options.Levels.ToList().IndexOf(from);
        return SetThinking(options.Levels[(at + 1) % options.Levels.Count]);
    }

    /// <summary>From a resumed session's header: applied silently — the user chose it before.</summary>
    public void RestoreThinking(string? level)
    {
        if (level is not null) SwapThinking(level);
    }

    /// <summary>
    /// Runs as a turn starts: the level chosen during the last turn, then — once the model has said
    /// what it offers — a check that a configured level is one of them. The provider already sends
    /// the model's default for a level it does not offer; this says so, once, so the user knows why.
    /// </summary>
    private void ApplyPendingThinking()
    {
        if (_hasPending)
        {
            _hasPending = false;
            Apply(_pendingThinking);
        }

        if (_thinkingChecked || ThinkingLevel is not { } level || level == ThinkingLevels.Lowest) return;

        // NOT YET IS NOT A VERDICT: checked at a later turn, once the model has answered.
        if (KnownThinkingOptions is not { } options) return;
        _thinkingChecked = true;

        if (options.Any && !options.Levels.Contains(level))
        {
            Say(new Message($"Config asks for thinking '{level}', which {ModelName} does not offer — "
                + "using the model's default.", Severity.Warning));
            SwapThinking(null);
        }
    }

    private void Apply(string? level)
    {
        CacheNote();
        SwapThinking(level);
        Say(new Message(level is null ? "Thinking set to the model's default." : $"Thinking set to {level}."));
    }

    /// <summary>
    /// Moves the agent, the children's default and the session's own copies to a provider at
    /// <paramref name="level"/> — the same set <c>/model</c> moves, since a level is a different
    /// provider object over the same model.
    /// </summary>
    private void SwapThinking(string? level)
    {
        if (Thinker is not { } thinker || Resolution?.Model is not { } model) return;
        var next = model with { Provider = thinker.WithThinking(level) };
        Host?.SwapProvider(next);
        _spawner?.SwapDefaultProvider(next.Provider, next.ContextWindow, next.InstanceName);
        Provider = next.Provider;
        Resolution = Resolution.WithModel(next);
        Announce(SessionChangeKind.Thinking);
    }

    /// <summary>
    /// SAID BEFORE IT COSTS. The level is rendered into Anthropic's prompt, so a change re-sends the
    /// whole conversation uncached once. Over 10,000 tokens that is worth a line; below it, noise.
    /// </summary>
    private void CacheNote()
    {
        if (Provider is not Llm.Providers.AnthropicProvider { CacheControl: true }) return;
        if (Host?.Context.Used is not { } used || used <= 10_000) return;
        Say(new Message($"The next request rebuilds the prompt cache (~{used / 1000}K tokens re-sent at full price)."));
    }

    /// <summary>
    /// What the model has said it offers, or a "not yet" with no levels.
    ///
    /// <para>NEVER WAITS ON THE PROBE. Every caller can run on the UI thread, and a probe started
    /// there carries that thread's context: blocking it on the probe would wait for a continuation
    /// that needs the very thread being blocked, and freeze the window for good. The probe is
    /// started when the session is wired, and answers within seconds; until then the user is told
    /// to wait rather than made to.</para>
    /// </summary>
    private ThinkingOptions KnownOptionsOrNotYet() =>
        KnownThinkingOptions ?? ThinkingOptions.None("the model has not said yet which levels it offers — try again in a moment");

    /// <summary><c>/think</c>: the levels, or a level set for this session (and saved, with --save).</summary>
    public CommandStatus Think(string argument)
    {
        var decision = ThinkCommand.Decide(argument, KnownOptionsOrNotYet(), ThinkingLevel);
        if (decision.Reply.Text.Length > 0) Say(decision.Reply);
        if (decision.SetTo is null && !decision.Clear) return CommandStatus.Reported;
        return SetThinking(decision.Clear ? null : decision.SetTo, decision.Save);
    }

    /// <summary>The palette's values for <c>/think</c>: what the model offers, then the default.
    /// Empty until the model has said — the palette must never wait on a probe.</summary>
    private IReadOnlyList<CompletionValue> ThinkingValues() =>
        KnownThinkingOptions is { Any: true } options
            ? [.. options.Levels.Select(l => new CompletionValue(l, l == ThinkingLevel ? "in use" : "")),
               new CompletionValue(ThinkCommand.Default, ThinkingLevel is null ? "in use" : "the model's own")]
            : [];

    private string ModelName => Resolution?.Model?.Label ?? Provider?.ModelId ?? "this model";
}
