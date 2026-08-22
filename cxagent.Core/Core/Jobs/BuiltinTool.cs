namespace CxAgent.Core.Llm;

/// <summary>
/// What a worker may do. Every worker gets all of these UNLESS A SELECTION NARROWS THEM.
///
/// <para>NOT GRANTED PER ROLE — a worker's NAME does not decide which of these it may use. Deciding
/// capability from identity means the orchestrator must choose the identity before it knows what the
/// work will need, which it cannot: a "reviewer" handed a write-shaped job describes writing instead
/// of writing, and that shape produces new variants faster than they can be guarded.</para>
///
/// <para>A SELECTION IS THE OPPOSITE OF A ROLE, not a revival of one. It is written by a person — in
/// config, in code, or for one turn — who has decided what THIS deployment should offer, and it is
/// applied to the assembled list rather than baked into a name. See
/// <see cref="Jobs.ToolSelection"/>.</para>
///
/// <para>The enum exists because tools are real and <see cref="Jobs.ToolBindings"/> maps each one
/// to an executor action. Safety lives in the permission gate, which asks the user before anything
/// outside the working folder is touched and does not care what the job is called.</para>
/// </summary>
public enum BuiltinTool
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
