using System.Text.RegularExpressions;

namespace CxAgent.Core.Llm;

/// <summary>
/// Whether a provider failure was the endpoint refusing a request for being too long.
///
/// <para>WHY THIS EXISTS. Compression normally fires PREDICTIVELY — occupancy against the configured
/// window, before the call. That check can be wrong in both directions: the window is a config value
/// and may not match what the endpoint actually serves, a local llama.cpp splits <c>n_ctx</c> across
/// slots so the served window is a fraction of the advertised one, and a provider that reports no
/// usage leaves the check with nothing to act on. The refusal itself cannot be wrong: it is the
/// endpoint saying, in its own words, that this did not fit.</para>
///
/// <para>Ported from opencode (<c>packages/llm/src/provider-error.ts</c> and
/// <c>packages/opencode/src/provider/error.ts</c>), which collects these strings across every vendor
/// it supports — that breadth is the value here, and it is not worth rediscovering one 400 at a time.
/// Three signals, matching theirs: a known message, HTTP 413, or an error code of
/// <c>context_length_exceeded</c> in the body.</para>
/// </summary>
public static class ContextOverflow
{
    /// <summary>
    /// Vendor wordings for "your input was too long". From opencode's list, plus llama.cpp's own
    /// phrasing — the endpoint this project runs against locally.
    /// </summary>
    private static readonly Regex[] Patterns =
    [
        new(@"prompt is too long", RegexOptions.IgnoreCase),
        new(@"request_too_large", RegexOptions.IgnoreCase),
        new(@"input is too long for requested model", RegexOptions.IgnoreCase),
        new(@"exceeds the context window", RegexOptions.IgnoreCase),
        new(@"exceeds (?:the )?(?:model'?s )?maximum context length", RegexOptions.IgnoreCase),
        new(@"input token count.*exceeds the maximum", RegexOptions.IgnoreCase),
        new(@"tokens in request more than max tokens allowed", RegexOptions.IgnoreCase),
        new(@"maximum prompt length is \d+", RegexOptions.IgnoreCase),
        new(@"reduce the length of the messages", RegexOptions.IgnoreCase),
        new(@"maximum context length is \d+ tokens", RegexOptions.IgnoreCase),
        new(@"exceeds (?:the )?maximum allowed input length", RegexOptions.IgnoreCase),
        new(@"is longer than the model'?s context length", RegexOptions.IgnoreCase),
        new(@"exceeds the limit of \d+", RegexOptions.IgnoreCase),
        new(@"exceeds the available context size", RegexOptions.IgnoreCase),
        new(@"greater than the context length", RegexOptions.IgnoreCase),
        new(@"context window exceeds limit", RegexOptions.IgnoreCase),
        new(@"exceeded model token limit", RegexOptions.IgnoreCase),
        new(@"context[_ ]length[_ ]exceeded", RegexOptions.IgnoreCase),
        new(@"request entity too large", RegexOptions.IgnoreCase),
        new(@"context length is only \d+ tokens", RegexOptions.IgnoreCase),
        new(@"input length.*exceeds.*context length", RegexOptions.IgnoreCase),
        new(@"prompt too long; exceeded (?:max )?context length", RegexOptions.IgnoreCase),
        new(@"too large for model with \d+ maximum context length", RegexOptions.IgnoreCase),
        new(@"but the configured context size is", RegexOptions.IgnoreCase),
        new(@"model_context_window_exceeded", RegexOptions.IgnoreCase),
        new(@"too many tokens", RegexOptions.IgnoreCase),
        new(@"token limit exceeded", RegexOptions.IgnoreCase),
    ];

    /// <summary>
    /// Failures that MENTION tokens or limits but are not overflows.
    ///
    /// <para>Checked first and unconditionally. A rate limit is a wait, not a compaction — throwing
    /// away history in response to one destroys the session's memory to solve a problem that
    /// resolves itself in seconds. Its message frequently says "tokens per minute", which is exactly
    /// why a token-mentioning match alone is not enough.</para>
    /// </summary>
    private static readonly Regex[] Exclusions =
    [
        new(@"^(throttling error|service unavailable):", RegexOptions.IgnoreCase),
        new(@"rate limit", RegexOptions.IgnoreCase),
        new(@"too many requests", RegexOptions.IgnoreCase),
    ];

    /// <summary>
    /// True when this failure is the endpoint refusing the request for length.
    /// </summary>
    /// <param name="message">The exception message, as the provider worded it.</param>
    /// <param name="httpStatus">The status, when there was one. 413 is the refusal in status form.</param>
    /// <param name="vendorBody">
    /// The raw response body, when captured. Searched as well as the message because a wire can
    /// surface a bland message over a body naming <c>context_length_exceeded</c>.
    /// </param>
    public static bool IsOverflow(string? message, int? httpStatus, string? vendorBody = null)
    {
        var text = string.Join('\n', new[] { message, vendorBody }.Where(s => !string.IsNullOrWhiteSpace(s)));

        // A RATE LIMIT IS NEVER AN OVERFLOW, whatever else the text says, and whatever the status.
        foreach (var exclusion in Exclusions)
            if (exclusion.IsMatch(text)) return false;

        if (httpStatus == 413) return true;

        foreach (var pattern in Patterns)
            if (pattern.IsMatch(text)) return true;

        return false;
    }
}
