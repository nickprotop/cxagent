namespace CxAgent.Core.Llm.Providers;

/// <summary>
/// Everything an OpenAI-compatible endpoint needs, as one value.
///
/// <para>A RECORD RATHER THAN EIGHT PARAMETERS. The constructor had reached eight — four required
/// strings followed by four optionals — which is the shape where a caller passing arguments
/// positionally can transpose two same-typed ones and still compile. Named members make that a build
/// error, and the repository's own rule (CXAGENT.md, AV1561) says more than three parameters means
/// the group wants a name.</para>
///
/// <para>The bundling was done when <see cref="CacheControl"/> was added rather than adding a ninth
/// parameter — the rule's first real test, and grandfathering an exception into it would have made
/// the very method the rule was written about worse.</para>
/// </summary>
public sealed record OpenAiProviderOptions
{
    /// <summary>Config's name for this instance — what spend is attributed to.</summary>
    public required string ProviderId { get; init; }

    /// <summary>Human-readable label, e.g. "openai-compatible gpt-4o-mini".</summary>
    public required string DisplayName { get; init; }

    public required string Model { get; init; }

    /// <summary>The endpoint root. A trailing slash is trimmed by the constructor.</summary>
    public required string BaseUrl { get; init; }

    /// <summary>Null for an endpoint that needs no key — a local llama.cpp, typically.</summary>
    public string? ApiKey { get; init; }

    /// <summary>Extra request headers, such as OpenRouter's attribution pair.</summary>
    public IReadOnlyDictionary<string, string>? ExtraHeaders { get; init; }

    /// <summary>An injected client, or null for the shared one. Tests inject; production does not.</summary>
    public HttpClient? Client { get; init; }

    public RetryPolicy? Retry { get; init; }

    /// <summary>
    /// Emit a <c>cache_control</c> breakpoint on the system prompt.
    ///
    /// <para>OFF UNLESS CONFIG ASKS, because writing to a cache is BILLED on the providers that need
    /// this: Anthropic charges 1.25x normal input for a five-minute entry and 2x for an hour, Gemini
    /// charges input plus storage. It pays back only when the prefix is reused before it expires, so
    /// a single question and then walking away costs MORE than not caching.</para>
    ///
    /// <para>Providers that cache automatically (OpenAI, DeepSeek's own API) and endpoints that
    /// ignore the field entirely (llama.cpp) are unaffected either way.</para>
    /// </summary>
    public bool CacheControl { get; init; }
}
