namespace CxAgent.Core.Llm;

/// <summary>
/// What a worker may do. Every worker gets all of these UNLESS A SELECTION NARROWS THEM.
///
/// <para>The selection is not the role system returning. A role decided capability from IDENTITY,
/// before the work was known, which is the part that failed. A selection is written by a person —
/// in config, in code, or for one turn — who has decided what THIS deployment should offer, and it
/// is applied to the assembled list rather than baked into a name. See
/// <see cref="Plugins.ToolSelection"/>.</para>
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
    WebFetch,
}
