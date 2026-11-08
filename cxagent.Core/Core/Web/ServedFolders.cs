namespace CxAgent.Core.Web;

/// <summary>
/// The folders a browser may reach while this process serves its sessions to one — <c>cxagent web</c>, or <c>/web</c>
/// from the terminal — or null while it serves none.
///
/// <para>ONE LIVE VALUE, SET BY THE HOST AND READ AT EACH DECISION: a colleague brought in before <c>/web</c> started is
/// held to it from the moment it does, because nothing is copied at spawn time.</para>
/// </summary>
public sealed class ServedFolders
{
    private volatile IReadOnlyList<string>? _roots;

    /// <summary>The served folders, each as it really is on disk; null when nothing is served.</summary>
    public IReadOnlyList<string>? Roots
    {
        get => _roots;
        set => _roots = value?.Select(Permissions.PermissionPolicy.RealFolder).ToList();
    }
}
