namespace CxAgent.Core.Permissions;

/// <summary>
/// What the auto-mode classifier concluded about one action.
///
/// <para>THREE VALUES, AND "I DO NOT KNOW" IS ONE OF THEM. A two-valued verdict forces every
/// uncertainty into one of the two confident answers; making <see cref="Ask"/> first-class is what
/// lets the model decline to decide instead of guessing.</para>
///
/// <para><see cref="Ask"/> IS ALSO EVERY FAILURE. A timeout, a transport error, an unparseable body,
/// an empty completion, a verdict nobody recognises — all ask. Only an explicit ALLOW permits a
/// silent action, and only an explicit DENY refuses one.</para>
/// </summary>
public enum ClassifierVerdict
{
    Ask,
    Allow,
    Deny,
}

/// <summary>
/// A verdict and, when the model gave one, why.
/// </summary>
/// <param name="Verdict">What the classifier concluded.</param>
/// <param name="Reason">Why, when stage two gave one.</param>
/// <param name="Flagged">
/// WHETHER TRIAGE SENT THIS ACTION TO STAGE TWO — the fact the triage-flag-rate counter needs, and
/// the reason it lives on the decision rather than as a side-effect counter read separately. A
/// classifier instance is shared across concurrent job threads (see PermissionDecider's docs on
/// <c>maxParallel</c>), so a mutable "last flagged" flag on the classifier itself would race between
/// requests; this rides with the exact decision it describes, which is race-free by construction and
/// survives a cache hit correctly (a cached decision WAS flagged when it was computed, and returning
/// it later must still say so).
/// </param>
public sealed record ClassifierDecision(ClassifierVerdict Verdict, string? Reason, bool Flagged = false);

public static class VerdictParser
{
    /// <summary>
    /// Reads a verdict from a completion. Anything not recognised is <see cref="ClassifierVerdict.Ask"/>.
    ///
    /// <para>ORDINAL, AND THE WHOLE FIRST TOKEN. Not Contains, not a JSON parse: "ALLOW, but only if
    /// you are sure" and <c>{"verdict":"allow"}</c> are a model that did not answer the question
    /// asked. What IS accepted is a bare verdict, optionally followed by ": reason" — the shape the
    /// instruction asks for.</para>
    /// </summary>
    public static ClassifierDecision Parse(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return new(ClassifierVerdict.Ask, null);

        var colon = trimmed.IndexOf(':');
        var head = (colon < 0 ? trimmed : trimmed[..colon]).Trim();
        var reason = colon < 0 || colon == trimmed.Length - 1
            ? null
            : trimmed[(colon + 1)..].Trim() is { Length: > 0 } r ? r : null;

        return head switch
        {
            "ALLOW" => new(ClassifierVerdict.Allow, reason),
            "DENY" => new(ClassifierVerdict.Deny, reason),
            _ => new(ClassifierVerdict.Ask, head == "ASK" ? reason : null),
        };
    }
}
