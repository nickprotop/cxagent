using CxAgent.Core.Permissions;

namespace CxAgent.Core.Agents;

/// <summary>
/// The folder a sub-agent works in when a spawn names one: its absolute path, and its name relative to
/// the parent's folder — the form shown and stored.
/// </summary>
public sealed record ChildRoot(string Absolute, string Relative)
{
    /// <summary>
    /// Resolves a spawn's <c>root</c> against the parent's folder.
    ///
    /// <para>INSIDE THE PARENT'S FOLDER OR REFUSED, judged on where the path LANDS — symlinks followed,
    /// <c>..</c> collapsed — not on how it is spelled. A child's folder is its permission boundary, so a
    /// spelling that reached outside would hand a child a trusted folder the user never trusted.</para>
    ///
    /// <para>NOT INSIDE A FOLDER WHOSE FILES CAN RUN CODE (<c>.git</c>, <c>.vscode</c>, …). Rooted
    /// there, a child's ordinary writes land in hooks and task files; the folder is refused rather
    /// than every write in it asked about.</para>
    ///
    /// <para>MUST EXIST: a typo must not create folders, and a worktree is made before it is used.</para>
    /// </summary>
    /// <param name="asked">The spawn's <c>root</c>; blank or <c>.</c> is the parent's own folder.</param>
    /// <param name="policy">The parent's policy, whose boundary and resolver decide; null when the
    /// session runs without one, and then <paramref name="parentFolder"/> is the boundary.</param>
    /// <param name="parentFolder">The parent's working folder.</param>
    public static ChildRootResult Resolve(string? asked, PermissionPolicy? policy, string? parentFolder)
    {
        if (string.IsNullOrWhiteSpace(asked) || asked.Trim() is "." or "./") return ChildRootResult.ParentsOwn;

        var boundaryText = policy?.Boundary ?? parentFolder;
        if (boundaryText is null)
            return ChildRootResult.Refused($"root '{asked}' is outside this agent's folder (none).");
        // THE REAL FOLDER, in the form the resolved root below is in — see PermissionPolicy.RealFolder.
        var boundary = PermissionPolicy.RealFolder(boundaryText);

        // A LEADING ~ IS THE HOME FOLDER, as a shell reads it. Taken literally it would name a folder
        // called "~" under the parent's — which does not exist, so the refusal would say "not a folder"
        // about a path whose author meant somewhere else entirely.
        var text = asked.Trim();
        if (text == "~" || text.StartsWith("~/", StringComparison.Ordinal))
            text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), text.Length > 1 ? text[2..] : "");

        var resolved = policy is not null ? policy.Resolve(text) : PermissionPolicy.ResolveAgainst(text, boundary);
        if (resolved is null || !PathResolver.Contains(boundary, resolved))
            return ChildRootResult.Refused($"root '{asked}' is outside this agent's folder ({boundary}).");

        if (!Directory.Exists(resolved)) return ChildRootResult.Refused($"root '{asked}' is not a folder.");
        if (resolved == boundary) return ChildRootResult.ParentsOwn;

        var relative = Path.GetRelativePath(boundary, resolved).Replace('\\', '/');
        if (relative.Split('/').FirstOrDefault(PermissionPolicy.IsExecutableConfigFolder) is { } codeDir)
            return ChildRootResult.Refused(
                $"root '{asked}' is inside {codeDir}, whose files can run code; choose a folder outside it.");

        // THE NAME IS WRITTEN INTO THE ENVELOPE'S root="…" ATTRIBUTE, which is read back by scanning to
        // the next quote, so a name carrying one would end the attribute early.
        if (relative.IndexOfAny(['"', '<', '>', '\n', '\r']) >= 0)
            return ChildRootResult.Refused($"root '{asked}' has a character a folder name here cannot carry.");

        return new ChildRootResult(new ChildRoot(resolved, relative), null);
    }
}

/// <summary>
/// What a spawn's <c>root</c> came to: a folder of its own, the parent's own folder (both null), or a
/// refusal the model reads.
/// </summary>
public sealed record ChildRootResult(ChildRoot? Root, string? Refusal)
{
    /// <summary>No folder of its own: the child works where the parent does, as it always has.</summary>
    public static ChildRootResult ParentsOwn { get; } = new(null, null);

    /// <summary>A refusal, worded for the model.</summary>
    public static ChildRootResult Refused(string why) => new(null, why);
}
