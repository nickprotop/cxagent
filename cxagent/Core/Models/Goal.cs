namespace CxAgent.Core.Models;

// Draft is the copilot-v1.1 pre-execution phase (scheduler inert). v1 goals start Active.
public enum GoalState { Draft, Active, Paused, Completed, Failed, Cancelled }

public record Goal
{
    public required string Id { get; init; }           // ULID
    public required string Description { get; init; }
    public GoalState State { get; set; } = GoalState.Active;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string ProviderId { get; init; } = "";
    public List<ChatMessage> Conversation { get; init; } = new();
}
