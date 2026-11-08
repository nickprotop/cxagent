namespace CxAgent.Core.Agents;

/// <summary>
/// What a module may add to a child it recognises by origin — the factory's one door, answered by
/// <see cref="SubAgentFactory.SubAgentRuntime.Augment"/>.
///
/// <para>A CLOSED LIST, ON PURPOSE. The worker code is the solid part of cxagent; a module that could hand the factory
/// anything — a provider, a policy, a whole setup — could reshape every child it touches, and a bug there would be a
/// bug in every worker. Here it can only add what is named below, each one checked where the factory applies it.</para>
///
/// <para>Slice 2 of the colleagues work adds <c>ExtraTools</c> (added only; a name a child already has is refused, so
/// nothing shadows a built-in) and <c>ReadAlso</c> (folders a file read may reach, through the policy's own
/// derivation) — added when something first uses them, not before.</para>
/// </summary>
public sealed record ChildAugment
{
    /// <summary>
    /// Text placed before the request's caller context — who the child is and what it already knows.
    ///
    /// <para>FROM THE HOOK, NOT THE REQUEST, so a revived child gets it too: revival rebuilds a child's system prompt from
    /// its type on every turn and keeps only its messages, so anything a spawn put in the prompt would be lost on
    /// resume unless the same door supplies it again.</para>
    /// </summary>
    public string? Context { get; init; }

    /// <summary>
    /// Tools the module adds — a colleague's <c>recall</c>. ADD ONLY: a name a built-in or another injected tool already
    /// holds is left out (see <see cref="SubAgent.ToolsLeftOut"/>), so nothing can shadow what the worker already has.
    /// Offered whatever the worker's type narrowed its tools to, as they are the module's, not the type's.
    /// </summary>
    public IReadOnlyList<Jobs.IAgentTool> ExtraTools { get; init; } = [];

    /// <summary>
    /// Folders whose files the worker may read without asking — a colleague's workplaces — through its policy's own
    /// derivation (<see cref="Permissions.PermissionPolicy.WithReadAlso"/>): reads only, while each is trusted.
    /// </summary>
    public Permissions.ReadAlso? ReadAlso { get; init; }
}
