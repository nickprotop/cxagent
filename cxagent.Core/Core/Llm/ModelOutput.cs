namespace CxAgent.Core.Llm;

/// <summary>
/// Reading what a model emitted: separating its reasoning from its answer.
///
/// <para>THIS IS AGENT BEHAVIOUR, and it lives here for that reason. It was written inside a DAG job
/// plugin because that is where the first worker loop happened to be, and the single-agent loop then
/// reached across into a plugin it otherwise has nothing to do with. An agent that cannot parse its
/// own model's output is not self-contained.</para>
/// </summary>
public static class ModelOutput
{
    /// <summary>
    /// The text INSIDE the reasoning block — the complement of <see cref="StripReasoning"/>.
    ///
    /// <para>A reasoning model spends most of a long turn here and emits nothing else, so this is
    /// the only evidence available that it is working rather than wedged. It is shown live and
    /// discarded; it never enters the conversation, because a model that sees its own thinking
    /// replayed as content starts treating it as commitment.</para>
    ///
    /// <para>Handles the UNBALANCED case deliberately: mid-stream the opening tag has arrived and
    /// the closing one has not, which is exactly when this is wanted.</para>
    /// </summary>
    public static string ExtractReasoning(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var open = text.IndexOf("<think", StringComparison.OrdinalIgnoreCase);
        if (open < 0) return string.Empty;

        var contentStart = text.IndexOf('>', open);
        if (contentStart < 0) return string.Empty;          // tag itself still arriving
        contentStart++;

        var close = text.IndexOf("</think>", contentStart, StringComparison.OrdinalIgnoreCase);
        return close < 0
            ? text[contentStart..]                          // still thinking
            : text[contentStart..close];
    }

    /// <summary>
    /// Removes a reasoning model's <c>&lt;think&gt;…&lt;/think&gt;</c> block from generated text.
    ///
    /// <para>Seen live: a worker's finished body read literally "&lt;/think&gt;" — the reasoning
    /// tags were never stripped anywhere, so they reached the transcript AND the job's Output. The
    /// transcript is cosmetic; the output is not. <c>JobDigest</c> feeds that same text to the
    /// ORCHESTRATOR, so a downstream job consuming {{reviewer.content}} was being handed a model's
    /// private deliberation as if it were the answer.</para>
    ///
    /// <para>Handles the unbalanced case deliberately: a stream cut mid-thought, or a model that
    /// emits only the closing tag, still yields clean text rather than passing the fragment through.
    /// Text with no tags at all is returned untouched — this must not disturb the normal case.</para>
    /// </summary>
    public static string StripReasoning(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        if (!text.Contains("<think", StringComparison.OrdinalIgnoreCase)
            && !text.Contains("</think>", StringComparison.OrdinalIgnoreCase))
            return text;

        // Balanced blocks first.
        var cleaned = System.Text.RegularExpressions.Regex.Replace(
            text, "<think[^>]*>.*?</think>", string.Empty,
            System.Text.RegularExpressions.RegexOptions.Singleline
            | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // An unbalanced OPEN tag means everything after it is thought — drop the remainder.
        var open = cleaned.IndexOf("<think", StringComparison.OrdinalIgnoreCase);
        if (open >= 0) cleaned = cleaned[..open];

        // An unbalanced CLOSE tag means everything before it was thought — keep the remainder.
        var close = cleaned.LastIndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        if (close >= 0) cleaned = cleaned[(close + "</think>".Length)..];

        return cleaned.Trim();
    }
}
