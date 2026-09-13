using CxAgent.Core.Execution;
using CxAgent.Core.Jobs;

namespace CxAgent.Core.Commands;

/// <summary>
/// What is backgrounded right now, and a way to stop one without asking the model to.
///
/// <para>READS <see cref="DetachedProcessRegistry.Default"/> — THE SAME INSTANCE <see
/// cref="BackgroundJobTools"/> reads for <c>job_list</c>. A second registry, or a second row format,
/// is exactly the drift this design exists to rule out: a user typing <c>/jobs</c> must see what the
/// model sees for <c>job_list</c>, not a copy that can disagree about a pid or an age.</para>
///
/// <para>NOT BUILT ON <see cref="BackgroundJobTools"/> ITSELF. That class answers in prose meant for
/// a model to read back — "no background commands are running.", a numbered list ending in a slot
/// count — and bending it to hand back rows instead would contort a tool-shaped API to do a
/// command's job. <see cref="BackgroundJobTools.Age"/> is the one piece worth sharing, since two
/// clocks computing "how long has this run" independently is how a row ends up disagreeing with
/// itself between the two surfaces.</para>
/// </summary>
public sealed class JobsCommand(DetachedProcessRegistry registry, string sessionId)
{
    /// <summary>The listing, or a line saying there is nothing to list.</summary>
    public string Render()
    {
        var live = registry.Live;
        if (live.Count == 0)
            return "no background commands are running.";

        return string.Join("\n", live.Select(entry =>
        {
            var job = entry.Job;
            var owner = job is null ? "unowned"
                : job.AgentId == sessionId ? "session"
                : job.AgentId;
            var age = job is null ? "?" : BackgroundJobTools.Age(job.Started);
            var command = job?.Command ?? "(unknown — started outside the shell tool)";
            return $"pid {entry.Process.Pid}  ·  {owner}  ·  {age}  ·  {command}";
        }));
    }

    /// <summary>
    /// Stops one background command, if the session may.
    ///
    /// <para>THE SESSION AGENT MAY KILL ANY JOB — this IS the session, typing rather than calling a
    /// tool, so the caller identity is always the session's own and there is no sub-agent kill rule
    /// to apply here; that rule lives in <see cref="BackgroundJobTools"/>, which a sub-agent actually
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
