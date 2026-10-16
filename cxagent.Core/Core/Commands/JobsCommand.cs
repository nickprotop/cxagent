using CxAgent.Core.Execution;
using CxAgent.Core.Jobs;

namespace CxAgent.Core.Commands;

/// <summary>
/// Stopping a background command by pid, typed rather than called — the half of <c>/tasks stop</c> that
/// takes a number.
///
/// <para>READS <see cref="DetachedProcessRegistry.Default"/>, THE SAME INSTANCE <see cref="TaskTools"/>
/// reads for <c>task_list</c>, so a pid the user just read off <c>/tasks</c> is the pid this stops.</para>
/// </summary>
public sealed class JobsCommand(DetachedProcessRegistry registry)
{
    /// <summary>
    /// Stops one background command, if the session may.
    ///
    /// <para>THE SESSION AGENT MAY KILL ANY JOB — this IS the session, typing rather than calling a
    /// tool, so the caller identity is always the session's own and there is no sub-agent kill rule
    /// to apply here; that rule lives in <see cref="TaskTools"/>, which a sub-agent actually
    /// calls.</para>
    /// </summary>
    public string Kill(string pidText)
    {
        if (!int.TryParse(pidText.Trim(), out var pid))
            return $"'{pidText}' is not a pid. `/jobs` to see the ones running.";

        var entry = registry.Live.FirstOrDefault(j => j.Process.Pid == pid);
        if (entry is null)
            return $"no background command with pid {pid}.";

        entry.Process.Kill();
        return $"pid {pid} stopped.";
    }
}
