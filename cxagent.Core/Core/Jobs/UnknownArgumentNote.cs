namespace CxAgent.Core.Jobs;

/// <summary>
/// Names the arguments that were SENT BUT IGNORED, appended to a validation failure.
///
/// <para>WHY. Unrecognised arguments are copied in and silently dropped — nothing ever compares the
/// names a caller sent against the names a tool accepts. So `read_file {"file_path": "/x/y.cs"}`
/// reports <c>'path' is required</c>, a message that contradicts what the caller just sent: it DID
/// supply a path, under the wrong name. A misspelled argument and a forgotten one are
/// indistinguishable in that output, and they need opposite fixes — rename versus supply. Faced with
/// a message asserting an absence it can see is untrue, a model's cheapest move is to resend the
/// same shape.</para>
///
/// <para>ONLY ON FAILURE. A call that succeeded while carrying a stray key is not worth a lecture
/// appended to a good result; that would put noise on the common path to fix the rare one.</para>
///
/// <para>The suggestion is offered only when it is nearly certain (see <see cref="Suggest"/>). A
/// wrong guess is worse than none, because the caller will follow it.</para>
/// </summary>
public static class UnknownArgumentNote
{
    /// <summary>
    /// The note for <paramref name="sent"/> against <paramref name="accepted"/>, or "" when every
    /// name was recognised. <paramref name="subject"/> names the tool or job type in the message.
    /// </summary>
    public static string For(IEnumerable<string> sent, IReadOnlyList<string> accepted, string subject)
    {
        // `action` is pinned by the toolset rather than supplied by the caller, and a planned job
        // legitimately carries it. Reporting it as unrecognised would send the caller hunting for a
        // mistake it did not make.
        var known = new HashSet<string>(accepted, StringComparer.OrdinalIgnoreCase) { "action" };
        var unknown = sent.Where(n => !known.Contains(n)).Distinct(StringComparer.OrdinalIgnoreCase)
                          .ToList();
        if (unknown.Count == 0) return "";

        var described = unknown.Select(u =>
            Suggest(u, accepted) is { } hit ? $"'{u}' (did you mean '{hit}'?)" : $"'{u}'");

        return $" Unrecognised arguments were ignored: {string.Join(", ", described)}. "
             + $"{subject} accepts: {string.Join(", ", accepted)}.";
    }

    /// <summary>
    /// The accepted name a misspelling most likely meant, or null when nothing is close enough.
    ///
    /// <para>Containment after stripping case and separators, which covers the whole observed family
    /// — <c>file_path</c>/<c>filePath</c>/<c>filepath</c> for <c>path</c>, <c>maxRetries</c> for
    /// <c>max_retries</c>. Deliberately NOT edit distance: it is happy to call <c>path</c> a near
    /// miss for <c>pattern</c> (distance 3), and confidently renaming one real parameter to a
    /// different real parameter is the one failure mode worse than staying silent.</para>
    ///
    /// <para>Ambiguity yields null for the same reason: if a name contains two accepted names, which
    /// one was meant is exactly what is unknown.</para>
    /// </summary>
    private static string? Suggest(string unknown, IReadOnlyList<string> accepted)
    {
        var needle = Squash(unknown);
        if (needle.Length == 0) return null;

        var hits = accepted
            .Where(a => Squash(a) is { Length: > 0 } s && (needle.Contains(s) || s.Contains(needle)))
            .ToList();

        return hits.Count == 1 ? hits[0] : null;
    }

    private static string Squash(string s) =>
        new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
