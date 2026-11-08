namespace CxAgent.Core.Agents;

/// <summary>
/// One spawn, as the session asks for it — what the agent tool reads out of a model's call, and what a module (the
/// colleagues desk) asks for directly.
///
/// <para>TWO FIELDS ONLY AN ORDER CARRIES: <see cref="Origin"/> and <see cref="Handle"/>. The agent tool never sets them,
/// whatever its call's arguments say, so a model cannot make an ordinary worker claim to be something it is not.</para>
/// </summary>
public sealed record SpawnOrder
{
    /// <summary>What the worker is asked to do — its first message.</summary>
    public required string Prompt { get; init; }

    /// <summary>The label a person sees on its row; also what an unasked-for handle is slugged from.</summary>
    public string? Description { get; init; }

    /// <summary>The agent type, or null for the general one.</summary>
    public string? Type { get; init; }

    /// <summary>The provider entry to run on, from the type's list, or null for its own.</summary>
    public string? Provider { get; init; }

    /// <summary>
    /// A provider entry to run on whatever the type offers, or null to follow <see cref="Provider"/> and the type. AN
    /// ORDER'S ONLY, like the origin: a module that chose a model for a worker — a colleague's own — needs it honoured
    /// even on a type that offers no choice. The agent tool never sets it; what the module lets its own caller choose —
    /// the colleague tool's model override — is that module's rule, checked there.
    /// </summary>
    public string? Instance { get; init; }

    /// <summary>The folder to work in, relative to the session's, or null for the session's own.</summary>
    public string? Root { get; init; }

    /// <summary>What the parent knew and passes down, below the briefing.</summary>
    public string? Context { get; init; }

    /// <summary>Whether it runs in the background where it can.</summary>
    public bool Background { get; init; } = true;

    /// <summary>Where the worker came from, opaque to the worker code — see <see cref="SubAgentFactory.ChildRequest.Origin"/>.</summary>
    public string? Origin { get; init; }

    /// <summary>The handle wanted for it, or null to slug one from the description.</summary>
    public string? Handle { get; init; }

    /// <summary>The spawning agent's id, so its logs nest and its report finds its way back.</summary>
    public string? ParentAgentId { get; init; }

    /// <summary>The parent's per-request tool selection, which the child inherits.</summary>
    public Jobs.ToolSelection? TurnTools { get; init; }
}
