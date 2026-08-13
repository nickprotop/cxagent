namespace CxAgent.Core.Permissions;

/// <summary>
/// What is being asked for. <c>Mcp</c> is a call into a third-party MCP server — code we did not
/// write, running locally with the user's credentials, whose arguments follow a schema we cannot
/// interpret.
///
/// <para>PERSISTED BY NAME (<c>JsonStringEnumConverter</c>), so adding a value is a one-way change to
/// permissions.json: an older binary reading a file containing an <c>"Mcp"</c> rule throws, treats
/// the whole file as empty — losing every rule and all folder trust — and clobbers it on the next
/// save. Acceptable, but a downgrade hazard worth knowing about.</para>
/// </summary>
public enum PermissionKind { Shell, FileRead, FileWrite, Http, Mcp }

/// <summary>Per-folder trust-on-first-use state. Unknown (never asked) behaves as Untrusted for
/// the silent class — an unanswered question must never behave like a yes.</summary>
public enum TrustState { Unknown, Trusted, Untrusted }

/// <summary>One thing the user is being asked to allow. Display is what the prompt shows
/// (verbatim command / RESOLVED path / origin — plus env/working_dir for shell, when present);
/// AlwaysRule is EXACTLY what "Always" would persist, pre-computed here so the button text and
/// the stored rule can never diverge. NULL AlwaysRule = this request cannot be truthfully
/// generalised (e.g. a shell job carrying a custom env — Decisions §3): no Always button is
/// offered, and no stored rule ever matches it.
///
/// <para>SUBJECT IS THE THING ITSELF, when Display is not. For a shell command Display is decorated
/// for a reader — "<c>ls (in /repo)</c>" — and anything that PARSES the command needs it undecorated
/// or it sees a command called "ls (in". Defaults to Display, so every other kind is unaffected and
/// no existing construction site changes.</para></summary>
public sealed record PermissionRequest(PermissionKind Kind, string Display, string? AlwaysRule,
    string? Subject = null)
{
    /// <summary>
    /// The undecorated thing this request is about — the command, the path, the origin.
    ///
    /// <para>Falls back to <see cref="Display"/>, which is right for every kind whose display IS the
    /// subject. Only shell decorates, and only shell sets this.</para>
    /// </summary>
    public string What => Subject ?? Display;

    /// <summary>
    /// WHO IS ASKING — null for the session's own agent, a short label for a sub-agent.
    ///
    /// <para>OBSERVED ON A LIVE DRIVE. A child spawned to analyse a test failure asked to run shell
    /// commands repeatedly, and the prompt was INDISTINGUISHABLE from the parent asking: same
    /// heading, same command, nothing saying a delegated agent wanted this. With one foreground
    /// child that is merely unhelpful — the user knows what they started. It stops being cosmetic
    /// the moment two children run at once, because prompts follow the COMPOSER, not the view: you
    /// can be reading one child's transcript and approving another child's write, with nothing on
    /// screen to tell you so.</para>
    ///
    /// <para>AN INIT-ONLY PROPERTY, not a fourth positional field, so the ~15 construction sites in
    /// PermissionPolicy and the tests are untouched — the same reason McpInstructions was added that
    /// way. A caller that knows who is asking says so; nobody else changes.</para>
    /// </summary>
    public string? Requester { get; init; }
}
