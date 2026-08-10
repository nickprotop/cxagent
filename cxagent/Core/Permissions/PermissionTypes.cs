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
/// offered, and no stored rule ever matches it.</summary>
public sealed record PermissionRequest(PermissionKind Kind, string Display, string? AlwaysRule);
