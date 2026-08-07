namespace CxAgent.Core.Permissions;

public enum PermissionKind { Shell, FileRead, FileWrite, Http }

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
