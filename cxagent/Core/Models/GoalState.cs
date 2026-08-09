namespace CxAgent.Core.Models;

/// <summary>
/// Lifecycle of a run as the chat sinks report it. Draft is inert (nothing has started);
/// everything else is a state the single-agent loop can reach and the UI renders.
/// </summary>
public enum GoalState { Draft, Active, Paused, Completed, Failed, Cancelled }
