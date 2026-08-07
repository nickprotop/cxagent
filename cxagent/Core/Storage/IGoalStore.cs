using CxAgent.Core.Models;

namespace CxAgent.Core.Storage;

/// <summary>Durable store for goals, jobs, and conversation. Implementations must
/// enforce foreign keys (ON DELETE CASCADE) and be safe to open concurrently (WAL).</summary>
public interface IGoalStore
{
    Task SaveGoalAsync(Goal goal);                                    // upsert
    Task<Goal?> GetGoalAsync(string goalId);                          // Conversation hydrated separately
    Task<List<Goal>> ListGoalsAsync(int limit = 50, int offset = 0);
    Task<List<Goal>> ListGoalsByStateAsync(params GoalState[] states);

    Task SaveJobAsync(Job job);                                       // upsert
    Task<List<Job>> GetJobsForGoalAsync(string goalId);

    Task SaveChatMessageAsync(string goalId, ChatMessage message);    // append
    Task<List<ChatMessage>> GetConversationAsync(string goalId);      // drops dangling tool-results

    Task DeleteGoalAsync(string goalId);                              // FK cascade + log dir removal (via LogFileManager, Task 4)
}
