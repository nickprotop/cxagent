using CxAgent.Core.Web;

namespace CxAgent.Core.Permissions;

/// <summary>
/// Folders beyond a policy's boundary whose files may be READ without asking — a colleague's workplaces, the codebases
/// it knows. See <see cref="PermissionPolicy.WithReadAlso"/>.
/// </summary>
/// <param name="Folders">The folders, as recorded; each counts only while it exists and is trusted.</param>
/// <param name="Served">What a browser may reach while the session is served to one, or null for a policy no browser
/// sees — then a read outside those folders asks.</param>
public sealed record ReadAlso(IReadOnlyList<string> Folders, ServedFolders? Served);
