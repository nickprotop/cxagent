namespace CxAgent.Core.Llm;

/// <summary>Whether Anthropic returns thinking text (<c>summarized</c>) or empty blocks (<c>omitted</c>).</summary>
public enum ThinkingDisplay { Summarized, Omitted }

/// <summary>The level names, one vocabulary across providers.</summary>
public static class ThinkingLevels
{
    public const string Off = "off", On = "on", Low = "low", Medium = "medium", High = "high",
        XHigh = "xhigh", Max = "max";

    /// <summary>
    /// Resolved per request to the lowest level the model accepts. The auto-mode classifier asks
    /// for it: its yes/no runs on every tool call, and naming a concrete level up front would need
    /// the model's capabilities at construction, which is a network call on the startup path.
    /// </summary>
    public const string Lowest = "lowest";

    /// <summary>Every name config and <c>/think</c> accept, in offering order.</summary>
    public static readonly IReadOnlyList<string> Known = [Off, On, Low, Medium, High, XHigh, Max];

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
