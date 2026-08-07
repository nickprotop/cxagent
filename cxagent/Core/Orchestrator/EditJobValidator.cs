namespace CxAgent.Core.Orchestrator;

/// <summary>
/// Rejects the one plan shape that can never work: a <c>file replace</c> whose <c>pattern</c> must
/// have come from a <c>file read</c> of the same path.
///
/// <para>The orchestrator can only author params from plan-time knowledge — the goal text, and
/// DIGESTS of prior job outputs. A replace needs the target file's exact bytes. Those are
/// incompatible: a pattern reconstructed from a digest will not match, no matter how good the
/// orchestrator is. Measured across four consecutive drives, each of which failed with "'pattern'
/// was not found"; the matcher was relaxed twice (ignore indentation, then ignore all whitespace)
/// and the task still never completed, because the mismatch is not lexical. The author of the
/// pattern had not seen the file.</para>
///
/// <para>The worker path already has the right shape — <c>replace_in_file</c> called from a context
/// that just called <c>read_file</c>, with the real bytes in hand. It was simply never selected,
/// because nothing told the orchestrator to select it.</para>
///
/// <para>This removes no capability. A replace whose text IS known at plan time — the user pasted
/// it, or a previous drive authored it — has no read dependency and is untouched. Only the shape
/// that is always wrong is refused, which also makes this instrumentation: every rejection is a
/// count of how often the guidance failed.</para>
///
/// <para>Called from BOTH PlanCompiler and ConsultJobCompiler. Every defect class in this codebase
/// — D7, D8, D11, D12, and the missing param validation found the same night — was a rule added to
/// one compiler and not its twin.</para>
/// </summary>
public static class EditJobValidator
{
    /// <summary>A job as the compilers see it mid-build: type, action, path, and the plan-local ids
    /// it depends on.</summary>
    public readonly record struct JobFact(
        string LocalId, string PluginType, string? Action, string? Path, IReadOnlyList<string> DependsOn);

    /// <summary>
    /// Checks every job in the batch. <paramref name="error"/> names the offending pair and the
    /// remedy — a rejection that only says "no" sends the orchestrator hunting for another route in,
    /// which is how a read-a-directory error once produced ten consecutive discovery jobs.
    /// </summary>
    /// <param name="fanOut">Whether llm_agent is registered. The REMEDY differs by mode, and naming
    /// a job type the orchestrator was never shown is worse than saying nothing: in single-agent
    /// mode the fix is to wait for the read to finish and plan the edit at the next consult, when
    /// the file's text is in the digest.</param>
    public static bool TryValidate(IReadOnlyList<JobFact> jobs, out string? error, bool fanOut = false)
    {
        error = null;

        // Path is compared as written, not resolved: both jobs come from the same plan and the same
        // orchestrator, so they spell it the same way. Resolving would need a filesystem this
        // validator has no business touching at compile time.
        var readsByPath = jobs
            .Where(j => j.PluginType == "file" && j.Action is "read" or "search")
            .ToLookup(j => j.Path ?? "", StringComparer.Ordinal);

        foreach (var job in jobs)
        {
            if (job.PluginType != "file" || job.Action != "replace") continue;

            var producer = readsByPath[job.Path ?? ""]
                .FirstOrDefault(r => job.DependsOn.Contains(r.LocalId, StringComparer.Ordinal));
            if (producer.LocalId is null) continue;

            error = $"Job '{job.LocalId}' replaces text in {job.Path}, and depends on job "
                  + $"'{producer.LocalId}' which reads that same file — so its 'pattern' would have "
                  + "to be reconstructed from a digest, and a digest cannot reproduce exact bytes. "
                  + (fanOut
                      ? "Replace BOTH jobs with ONE llm_agent job that reads and edits "
                        + $"{job.Path} in a single context, telling it what to change and how to verify it."
                      : $"Drop the replace for now. Let '{producer.LocalId}' run; its text reaches you "
                        + "in the next consult, and you can plan the edit THEN by copying the pattern "
                        + "from what the file actually says.");
            return false;
        }

        return true;
    }
}
