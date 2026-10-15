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
        var options = OptionsNow();
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

    /// <summary>
    /// F8: the next offered level, wrapping. Never waits on the model: before it has said what it
    /// offers there is nothing to step through, and a key must not freeze the window for a probe.
    /// </summary>
    public CommandStatus CycleThinking()
    {
        var options = KnownThinkingOptions;
        if (options is not { Any: true })
        {
            Say(new Message($"No thinking levels: {(options ?? ThinkingOptions.None("the model has not said yet")).Why}."));
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
    /// Runs as a turn starts: the level chosen during the last turn, then — once — a check that a
    /// configured level is one the model offers. A refused level would be a 400 on every request;
    /// this says so once and uses the model's default instead.
    /// </summary>
    private void ApplyPendingThinking()
    {
        if (_hasPending)
        {
            _hasPending = false;
            Apply(_pendingThinking);
        }

        if (_thinkingChecked || ThinkingLevel is not { } level || level == ThinkingLevels.Lowest) return;
        _thinkingChecked = true;

        var options = OptionsNow();
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
    /// The options, waiting for the probe if it has not answered. ON THE POOL: the probe awaits
    /// HTTP, and blocking on it from the UI thread would deadlock on that thread's own context.
    /// Bounded by the probe's three seconds, and cached once it answers.
    /// </summary>
    private ThinkingOptions OptionsNow() =>
        KnownThinkingOptions ?? Task.Run(ThinkingOptionsAsync).GetAwaiter().GetResult();

    private string ModelName => Resolution?.Model?.Label ?? Provider?.ModelId ?? "this model";
}
