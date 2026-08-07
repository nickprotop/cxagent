namespace CxAgent.Core.Llm;

/// <summary>
/// What a worker may do. Every worker gets all of these.
///
/// <para>These used to be granted per ROLE — implementer and debugger got the full set, planner and
/// reviewer a read-only subset. That system is gone: it encoded capability as identity, and the
/// orchestrator had to choose the identity before it knew what the work would need. It could not.
/// A reviewer handed a write-shaped job described writing instead of writing; the same shape kept
/// producing new variants faster than they could be guarded.</para>
///
/// <para>The enum stays because tools are real and <see cref="Plugins.WorkerToolset"/> maps each one
/// to a plugin action. What is gone is the idea that a worker's NAME decides which it may use.
/// Safety lives in the permission gate, which asks the user before anything outside the working
/// folder is touched and does not care what the job is called.</para>
/// </summary>
public enum WorkerTool
{
    ReadFile,
    WriteFile,
    RunShell,
    HttpRequest,
    ListFiles,
    SearchFiles,
    ReplaceInFile,
}
