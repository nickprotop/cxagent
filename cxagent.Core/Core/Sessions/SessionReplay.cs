using System.Text.Json;
using CxAgent.Core.Agents;
using CxAgent.Core.Commands;
using CxAgent.Core.Export;
using CxAgent.Core.Jobs;
using CxAgent.Core.Models;
using CxAgent.Core.Storage;

namespace CxAgent.Core.Sessions;

/// <summary>A sub-agent a replayed session had spawned: who it was, and the row its spawn drew.</summary>
/// <param name="JobId">The replayed row of the call that spawned it.</param>
/// <param name="AgentId">Its own agent id — its folder's name.</param>
/// <param name="Name">The handle the model addressed it by.</param>
/// <param name="Type">Its agent type.</param>
/// <param name="Task">The first line of its brief.</param>
/// <param name="Dir">Its folder, under the agent that spawned it.</param>
public sealed record PastWorker(string JobId, string AgentId, string Name, string Type, string? Task, string Dir);

/// <summary>What a replay showed.</summary>
/// <param name="Turns">The user turns shown.</param>
/// <param name="Hidden">Earlier user turns left out by the cap.</param>
/// <param name="Workers">The sub-agents the shown part spawned, oldest first.</param>
public sealed record ReplayShown(int Turns, int Hidden, IReadOnlyList<PastWorker> Workers);

/// <summary>
/// Shows a saved session's past to a front end as the ordinary events a live session raises: the user's turns, the
/// answers, the tool calls with their results, and the workers it spawned.
///
/// <para>FROM ITS HISTORY, NOT ITS TRANSCRIPT. The transcript holds only what was said, so a replay from it has no
/// tool calls and no workers; and it lives in one agent's folder, while a resume mints a new agent whose folder
/// starts empty, so every resume cut the past at the last one. history.jsonl records every message and begins by
/// naming the folder it continues — the chain the export already walks.</para>
///
/// <para>AS EVENTS, so a front end draws the past with the code that draws the present, and the terminal and the
/// browser cannot show a past session differently.</para>
///
/// <para>CAPPED BY TURNS, the newest kept: a session that ran for days would otherwise paint every one before the
/// user can type. The whole conversation stays in /export, and the replay says how many it left out.</para>
/// </summary>
public static class SessionReplay
{
    /// <summary>The user turns a replay shows by default.</summary>
    public const int DefaultTurns = 100;

    // NEGATIVE, AND FAR FROM ZERO: a live session numbers its messages upward from one, and a notice's seq counts
    // down from minus one; a replayed id must collide with neither.
    private const long FirstId = -1_000_000_000;

    /// <summary>
    /// Replays the session whose newest agent is <paramref name="agentId"/> into <paramref name="chat"/> and
    /// <paramref name="tools"/>. Nothing is shown for a session with no saved folder.
    /// </summary>
    /// <param name="worker">
    /// Told of each worker as its row starts and before it ends — where a live session announces one — for a front end
    /// that gives workers a view of their own.
    /// </param>
    /// <param name="called">
    /// Told of each call as it ends, as <see cref="Agent.ToolCallFinished"/> tells a live watcher — for a front end
    /// that draws its tool rows from those reports rather than from the rows themselves.
    /// </param>
    public static ReplayShown Play(AppPaths paths, string agentId, ISessionObserver chat, IToolObserver tools,
        int maxTurns = DefaultTurns, Action<PastWorker>? worker = null, Action<ToolCallReport>? called = null)
    {
        if (ExportSource.Load(paths, agentId) is not { } source) return new ReplayShown(0, 0, []);
        return Play(source, chat, tools, new PlayOptions(maxTurns, worker, called));
    }

    private sealed record PlayOptions(int MaxTurns, Action<PastWorker>? Worker, Action<ToolCallReport>? Called);

    private static ReplayShown Play(ExportSource source, ISessionObserver chat, IToolObserver tools, PlayOptions options)
    {
        var maxTurns = options.MaxTurns;
        var entries = source.Entries;
        var turnStarts = entries.Select((e, i) => (e, i)).Where(p => IsUserTurn(p.e)).Select(p => p.i).ToList();
        var hidden = Math.Max(0, turnStarts.Count - maxTurns);
        var from = hidden > 0 ? turnStarts[hidden] : 0;

        if (hidden > 0)
            chat.Said(new Message($"{hidden} earlier turn{(hidden == 1 ? "" : "s")} not shown — /export has the whole conversation."));

        // A RESULT IS MATCHED TO ITS CALL BY ID, wherever it landed: a background worker's report can arrive turns
        // after the call that started it.
        var results = new Dictionary<string, ExportEntry>(StringComparer.Ordinal);
        foreach (var e in entries)
            if (e.Message is { Role: "tool", ToolCallId: { } callId }) results.TryAdd(callId, e);

        var id = FirstId;
        var workers = new List<PastWorker>();
        for (var i = from; i < entries.Count; i++)
        {
            var entry = entries[i];
            switch (entry.Kind)
            {
                // NOT AT THE TOP OF A COPY: nothing has been said there to compact. Histories written before the recorder
                // knew a resumed session's replaced system prompt from a compaction carry that line there.
                case HistoryLine.Kinds.Compacted when i > 0 && entries[i - 1].Dir != entry.Dir:
                    continue;
                case HistoryLine.Kinds.Compacted:
                    chat.Said(new Message("The conversation was compacted here; the agent remembers a summary of what came before."));
                    continue;
                case HistoryLine.Kinds.Cleared:
                    chat.Said(new Message("The conversation was cleared here."));
                    continue;
                case ExportEntry.Note when entry.Message?.Content is { Length: > 0 } note:
                    chat.Said(new Message(note));
                    continue;
                case HistoryLine.Kinds.Message:
                    break;
                default:
                    continue;
            }
            if (entry.Message is not { } m) continue;

            if (IsUserTurn(entry))
            {
                chat.UserTurnAdded(new ChatMessageId(id--), m.Content);
                continue;
            }
            if (m.Role != "assistant" || m.Content.StartsWith(SessionCompressor.SummaryMarker, StringComparison.Ordinal))
                continue;

            var turn = new ChatMessageId(id--);
            chat.AssistantTurnBegan(turn);
            if (entry.Thinking is { Count: > 0 } thinking)
            {
                var text = string.Join("\n\n", thinking);
                chat.AssistantThinkingAppended(turn, text);
                chat.AssistantThinkingEnded(turn, new Llm.ThinkingSummary(TimeSpan.Zero,
                    text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length, Hidden: false, Redacted: false) { Untimed = true });
            }
            if (m.Content.Length > 0) chat.AssistantTextAppended(turn, m.Content);
            chat.AssistantTurnEnded(turn);

            foreach (var call in m.ToolCalls ?? [])
            {
                var result = call.Id is { } cid ? results.GetValueOrDefault(cid) : null;
                // STARTED, THEN ENDED, as a live call is: a row handed over already finished is ended by the first
                // call and again by the second.
                var job = Started(call, source.Header, at: entry.At, ref id);
                tools.ToolsChanged([job]);
                if (call.Name == Tool.Agent && WorkerOf(call, job, result) is { } past)
                {
                    workers.Add(past);
                    options.Worker?.Invoke(past);
                }
                Finish(job, call, result);
                tools.ToolUpdated(job);
                options.Called?.Invoke(new ToolCallReport(job.Id, job.AgentId, call.Name, job.JobType,
                    job.State == JobState.Succeeded ? "succeeded" : "cancelled", 0,
                    (result?.Message?.Content ?? "").Length, entry.At) { Target = job.DisplayName, Untimed = true });
            }
        }
        return new ReplayShown(turnStarts.Count - hidden, hidden, workers);
    }

    /// <summary>
    /// A message the user typed. Tool results and the task list also carry the user role, and so do a worker's
    /// report and cxagent's own nudges; none of those is a turn of the user's.
    /// </summary>
    private static bool IsUserTurn(ExportEntry e) =>
        e is { Kind: HistoryLine.Kinds.Message, Message: { Role: "user", ToolCallId: null, IsTaskList: false } }
        && e.From is null or Incoming.You;

    private static Job Started(ToolCall call, ExportHeader session, DateTimeOffset at, ref long id) =>
        new()
        {
            // UNIQUE ACROSS THE CHAIN: a provider's call ids are only unique within one conversation, and a resumed
            // one is several.
            Id = $"past:{id--}:{call.Id}",

            // THE SESSION'S AGENT, whichever copy made the call: a front end counts a round's calls by agent, and
            // the calls the session makes next are its newest agent's.
            AgentId = session.AgentId,
            JobType = Agent.BuiltinJobType(call.Name) ?? "file",
            Replayed = true,
            DisplayName = Agent.DescribeCall(call, session.WorkingDir),
            State = JobState.Running,
            CreatedAt = at,
        };

    private static void Finish(Job job, ToolCall call, ExportEntry? result)
    {
        // A CALL WITH NO RESULT never finished: the process ended under it, or it was stopped.
        if (result?.Message is not { } answer)
        {
            job.State = JobState.Cancelled;
            job.Result = new JobResult { Success = false, Output = new Dictionary<string, object?> { ["content"] = "(no result was recorded)" } };
            return;
        }

        // SUCCEEDED, AS LIVE: a finished call's answer is the reader's to judge, and live rows do not sniff it for
        // failure words either. What history does not keep — a denial, an exit code — a replay cannot show.
        //
        // NO TIMES. History writes a call and its result together, before the next request, so their stamps say
        // nothing about how long it ran; a row shows no duration rather than a wrong one.
        job.State = JobState.Succeeded;
        job.Result = new JobResult
        {
            Success = true,
            Output = new Dictionary<string, object?> { ["content"] = answer.Content },
        };
        if (call.Name == Tool.Agent) job.ProgressMessage = SubAgentEnvelope.StateOf(answer.Content);
    }

    private static PastWorker? WorkerOf(ToolCall call, Job job, ExportEntry? result)
    {
        if (result?.Message?.Content is not { } envelope || SubAgentEnvelope.IdOf(envelope) is not { } childId) return null;
        var name = SubAgentEnvelope.NameOf(envelope) ?? childId;
        var type = Arg(call, "type") is { Length: > 0 } t ? t : AgentTypeCatalog.DefaultTypeName;
        var task = Arg(call, "prompt")?.Split('\n', 2)[0].Trim();
        return new PastWorker(job.Id, childId, name, type, task, Path.Combine(result.Dir, childId));
    }

    private static string? Arg(ToolCall call, string name) =>
        call.Arguments.ValueKind == JsonValueKind.Object && call.Arguments.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
