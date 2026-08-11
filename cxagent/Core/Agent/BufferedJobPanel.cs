using CxAgent.Core.Models;

namespace CxAgent.Core.Agent;

/// <summary>
/// A sub-agent's job rows, captured rather than rendered.
///
/// <para>THE OTHER HALF OF THE ISOLATION, and the half that is easy to forget. Handing a child a
/// buffered <see cref="IChatSink"/> while leaving it the parent's <see cref="IJobPanel"/> looks like
/// isolation and is not: every tool the child calls would draw its own row in the parent's
/// transcript, interleaved with the parent's own rows and with nothing saying which agent caused
/// which. The child's ONE row — the spawn call itself — is what the user is meant to see.</para>
///
/// <para>Rows are kept BY ID because a <see cref="Job"/> is mutated in place: the loop hands the same
/// instance to <see cref="SetJobs"/> while running and to <see cref="UpdateJob"/> when it finishes,
/// so appending to a list would record one object twice and show both entries in their final state.
/// The same reason the real panel keys by id.</para>
/// </summary>
public sealed class BufferedJobPanel : IJobPanel
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Job> _jobs = [];

    /// <summary>What the child did, latest state per job — the record behind its row.</summary>
    public IReadOnlyCollection<Job> Jobs { get { lock (_gate) return [.. _jobs.Values]; } }

    public void SetJobs(IReadOnlyList<Job> jobs)
    {
        lock (_gate)
            foreach (var job in jobs) _jobs[job.Id] = job;
    }

    public void UpdateJob(Job job)
    {
        lock (_gate) _jobs[job.Id] = job;
    }

    // A progress tick for a row nobody is watching. The job it describes is already stored above,
    // and its final state is what a later inspection wants.
    public void UpdateProgress(Job job) { }

    // Live samples for a row nobody is watching. The finished job carries its own duration, which is
    // what a later inspection actually wants.
    public void UpdateResources(string jobId, ResourceSnapshot snapshot) { }

    // A worker's streaming text. The child's own IChatSink already captures everything it says, so
    // keeping a second copy here would double the buffer to no end.
    public void AppendText(string jobId, string delta) { }
}
