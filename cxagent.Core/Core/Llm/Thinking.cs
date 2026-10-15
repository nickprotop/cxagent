namespace CxAgent.Core.Llm;

/// <summary>Whether Anthropic returns thinking text (<c>summarized</c>) or empty blocks (<c>omitted</c>).</summary>
public enum ThinkingDisplay { Summarized, Omitted }

/// <summary>The level names, one vocabulary across providers.</summary>
public static class ThinkingLevels
{
    public const string Off = "off", On = "on", Minimal = "minimal", Low = "low", Medium = "medium",
        High = "high", XHigh = "xhigh", Max = "max";

    /// <summary>
    /// Resolved per request to the lowest level the model accepts. The auto-mode classifier asks
    /// for it: its yes/no runs on every tool call, and naming a concrete level up front would need
    /// the model's capabilities at construction, which is a network call on the startup path.
    /// </summary>
    public const string Lowest = "lowest";

    /// <summary>
    /// The model's own level, written where a level is stored — the session header. Not a level a
    /// request sends: it is the absence of one, NAMED so that a stored "the user chose the model's
    /// default" cannot be read as "nothing was stored" and replaced by the config's level.
    /// </summary>
    public const string Default = "default";

    /// <summary>Every name config and <c>/think</c> accept, in offering order.</summary>
    public static readonly IReadOnlyList<string> Known = [Off, On, Minimal, Low, Medium, High, XHigh, Max];

    /// <summary>Whether <paramref name="level"/> is one of <see cref="Known"/>.</summary>
    /// <param name="level">The name to check; null is not known.</param>
    public static bool IsKnown(string? level) =>
        level is not null && Known.Contains(level, StringComparer.Ordinal);
}

/// <summary>What one model offers. Empty means no levels: the indicator hides and F8 says why.</summary>
/// <param name="Levels">Offered levels, lowest first.</param>
/// <param name="Why">When <see cref="Levels"/> is empty, one sentence saying why, for F8 and <c>/think</c>.</param>
public sealed record ThinkingOptions(IReadOnlyList<string> Levels, string? Why = null)
{
    /// <summary>An offering with no levels.</summary>
    /// <param name="why">One sentence saying why the model offers none.</param>
    public static ThinkingOptions None(string why) => new([], why);

    /// <summary>Whether any level is offered.</summary>
    public bool Any => Levels.Count > 0;
}

/// <summary>How a finished thinking block is summarised in its folded header.</summary>
/// <param name="Duration">How long the model thought.</param>
/// <param name="Words">Word count of the thinking text.</param>
/// <param name="Hidden">The provider withheld the text (display omitted).</param>
/// <param name="Redacted">The block was safety-redacted.</param>
public sealed record ThinkingSummary(TimeSpan Duration, int Words, bool Hidden, bool Redacted);

/// <summary>
/// A provider whose thinking can be set per session.
///
/// <para>A CLONE, NOT A SETTER. Registry providers are shared by every session, sub-agent and the
/// classifier that names the instance; a level set on the shared object would leak into all of
/// them. <see cref="WithThinking"/> returns a provider at that level sharing the HTTP client and the
/// capability cache, so a level costs nothing and touches no one else.</para>
/// </summary>
public interface IThinkingProvider
{
    /// <summary>The level requests are sent at; null means the model's own default (nothing sent).</summary>
    string? ThinkingLevel { get; }

    /// <summary>The options once known, or null while the capability probe has not answered.</summary>
    ThinkingOptions? KnownThinkingOptions { get; }

    /// <summary>Probes once (bounded, cached only on success) and returns what the model offers.</summary>
    /// <param name="ct">Cancels the probe.</param>
    Task<ThinkingOptions> GetThinkingOptionsAsync(CancellationToken ct = default);

    /// <summary>This provider at <paramref name="level"/> (null = model default).</summary>
    /// <param name="level">The level to send requests at.</param>
    ILlmProvider WithThinking(string? level);
}

/// <summary>
/// One thinking block exactly as a provider returned it, to be sent back unchanged.
///
/// <para>OPAQUE, AND ONLY THE PROVIDER'S. Anthropic signs each block and refuses one that was
/// edited; within a tool loop it requires them back, so the model keeps the reasoning behind the
/// calls it is making. Nothing here reads or changes one — it is carried, then returned.</para>
/// </summary>
/// <param name="Redacted">A safety-redacted block: no readable text, only <paramref name="Data"/>.</param>
/// <param name="Thinking">The block's text as returned (empty when the provider hid it).</param>
/// <param name="Signature">The provider's signature over the block.</param>
/// <param name="Data">A redacted block's encrypted content.</param>
public sealed record ThinkingBlock(bool Redacted, string? Thinking, string? Signature, string? Data);

/// <summary>Which way an OpenAI-compatible endpoint is told how hard to think.</summary>
public enum ReasoningDialect
{
    /// <summary>llama.cpp: <c>chat_template_kwargs.enable_thinking</c>, when its template takes it.</summary>
    LlamaCpp,

    /// <summary>OpenRouter: a <c>reasoning</c> object, from what its model list says the model takes.</summary>
    OpenRouter,

    /// <summary>OpenAI: <c>reasoning_effort</c>, for a model the configuration says reasons.</summary>
    OpenAi,
}

/// <summary>The dialect an endpoint speaks, from its host.</summary>
public static class ReasoningDialects
{
    /// <summary>
    /// OpenRouter and OpenAI by their hosts; anything else is treated as llama.cpp, whose probe
    /// answers "none" for a server that is not one — so a gateway gets no levels rather than a guess.
    /// </summary>
    public static ReasoningDialect For(string? baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? uri.Host.ToLowerInvariant() switch
        {
            "openrouter.ai" => ReasoningDialect.OpenRouter,
            "api.openai.com" => ReasoningDialect.OpenAi,
            _ => ReasoningDialect.LlamaCpp,
        } : ReasoningDialect.LlamaCpp;
}
