namespace CxAgent.Core.Sessions;

/// <summary>
/// What makes a session a colleague's one-to-one: the colleague as the session's own agent, briefed as itself, with
/// read-only tools, saved under a tag that keeps it out of every list a session is offered from.
/// </summary>
/// <param name="Colleague">Whose one-to-one.</param>
/// <param name="Tag">What its saved header is tagged — how Open finds it again.</param>
/// <param name="Briefing">Who it is and what it knows: the highest-authority text in its prompt.</param>
/// <param name="Title">Its tab's and its row's name — "with Charting".</param>
public sealed record OneToOne(string Colleague, string Tag, string Briefing, string Title)
{
    /// <summary>
    /// ITS TOOLS: reading, and nothing else. It is there to be asked and taught, not to change anything — a write or a
    /// shell command from a conversation with no project of its own would land wherever its workplace happens to be.
    /// </summary>
    public static readonly Jobs.ToolSelection Tools = new([Jobs.Tool.ReadFile, Jobs.Tool.Glob, Jobs.Tool.Grep, Colleagues.RecallTool.ToolName]);
}
