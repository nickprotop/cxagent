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

/// <summary>A verdict and, when the model gave one, why.</summary>
public sealed record ClassifierDecision(ClassifierVerdict Verdict, string? Reason);

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
