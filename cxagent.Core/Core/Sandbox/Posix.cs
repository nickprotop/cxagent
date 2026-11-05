using System.Runtime.InteropServices;

namespace CxAgent.Core.Sandbox;

/// <summary>The one thing the sandbox asks of the C library: whose sockets to look for.</summary>
internal static class Posix
{
    /// <summary>The user this process runs as — the number in /run/user/&lt;uid&gt; and /tmp/tmux-&lt;uid&gt;.</summary>
    // DllImport, NOT LibraryImport: the generated form needs unsafe code enabled for the whole library, for one call
    // that takes nothing and returns a number.
    [DllImport("libc", EntryPoint = "getuid")]
    internal static extern uint UserId();
}
