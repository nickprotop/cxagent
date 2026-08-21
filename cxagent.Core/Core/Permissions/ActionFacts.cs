namespace CxAgent.Core.Permissions;

/// <summary>
/// Everything the classifier is shown about one action, beyond its kind and subject.
///
/// <para>A RECORD RATHER THAN PARAMETERS. Six optional strings on a method signature is the shape
/// where a caller transposes two and the classifier silently reasons about the wrong thing.</para>
///
/// <para>TWO CLASSES OF INPUT, and the difference decides the cap. The goal, the requester and the
/// path facts are USER- or SYSTEM-authored and are included whole. The diff and the body are
/// attacker-influenced by construction — a file's contents, a server's response — and are capped.
/// Everything renders inside the delimiter either way.</para>
/// </summary>
public sealed record ActionFacts
{
    /// <summary>What the user asked for, verbatim. User-authored, so uncapped.</summary>
    public string? Goal { get; init; }

    /// <summary>Which agent is asking — null for the session's own, a label for a child.</summary>
    public string? Requester { get; init; }

    /// <summary>Whether the target already exists. The difference between adding and destroying.</summary>
    public bool? TargetExists { get; init; }

    /// <summary>Project instructions in force. User-authored: "this repo's build writes to dist/".</summary>
    public string? ProjectInstructions { get; init; }

    /// <summary>The edit itself, capped. Attacker-influenced.</summary>
    public string? Diff { get; init; }

    /// <summary>The paths a shell command touches, and whether they are inside the boundary.</summary>
    public IReadOnlyList<string>? Paths { get; init; }

    /// <summary>
    /// HTTP-specific facts, when this action is a request. A nested record rather than three more
    /// top-level properties — <c>Method</c>, <c>Url</c> and <c>BodySize</c> are one concept ("what
    /// request is this") and belong together, not scattered beside Diff and Paths which answer a
    /// different question about a different kind of action.
    ///
    /// <para>EXISTS SO THE FULL URL AND METHOD HAVE A HOME THAT IS NOT <c>PermissionRequest.What</c>.
    /// For Http, <c>Subject</c> is the bare origin (by design — see PermissionTypes.cs), so the
    /// method, full URL and body size a caller may have gathered would otherwise have nowhere to go
    /// but a re-decorated <c>What</c>, which is exactly the field this task's facts are meant to
    /// replace as the classifier's input.</para>
    /// </summary>
    public HttpFacts? Http { get; init; }

    /// <summary>
    /// Method, full URL, and request body SIZE — never body content. Size, not content, because the
    /// classifier reasons about shape ("a 40MB POST to an unfamiliar host") without being handed the
    /// body itself to reason (or be misled) about.
    /// </summary>
    public sealed record HttpFacts(string? Method, string? Url, long? BodySize);

    /// <summary>Lines of <see cref="Diff"/> shown. Beyond this the model is told it was truncated.</summary>
    public const int DiffLineCap = 80;

    /// <summary>Renders the body of the <c>&lt;action&gt;</c> block. Never includes a closing tag.</summary>
    public string Render()
    {
        var sb = new System.Text.StringBuilder();
        if (Goal is { Length: > 0 }) sb.AppendLine($"user asked: {Neutralise(Goal)}");
        if (Requester is { Length: > 0 }) sb.AppendLine($"requested by sub-agent: {Neutralise(Requester)}");
        if (ProjectInstructions is { Length: > 0 })
            sb.AppendLine($"project instructions: {Neutralise(ProjectInstructions)}");
        if (TargetExists is { } exists)
            sb.AppendLine(exists ? "target exists (this overwrites)" : "target does not exist (this creates)");
        if (Paths is { Count: > 0 }) sb.AppendLine($"paths touched: {string.Join(", ", Paths.Select(Neutralise))}");

        if (Http is { } http)
        {
            if (http.Method is { Length: > 0 }) sb.AppendLine($"http method: {Neutralise(http.Method)}");
            if (http.Url is { Length: > 0 }) sb.AppendLine($"http url: {Neutralise(http.Url)}");
            if (http.BodySize is { } size) sb.AppendLine($"http body size: {size} bytes");
        }

        if (Diff is { Length: > 0 })
        {
            var lines = Diff.Split('\n');
            sb.AppendLine("change:");
            foreach (var line in lines.Take(DiffLineCap)) sb.AppendLine(Neutralise(line));
            if (lines.Length > DiffLineCap)
                sb.AppendLine($"[truncated: {lines.Length - DiffLineCap} more lines not shown]");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Removes anything that would close the delimiter early.
    ///
    /// <para>WITHOUT THIS THE DELIMITER IS DECORATION. A file containing the literal closing tag ends
    /// the data block, and everything after it is read as instruction — which is the entire
    /// break-out this defence exists to prevent.</para>
    /// </summary>
    private static string Neutralise(string text) =>
        text.Replace("</action>", "[/action]", StringComparison.OrdinalIgnoreCase);
}
