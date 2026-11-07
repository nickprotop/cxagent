using System.Text.Json;
using CxAgent.Core.Agents;
using CxAgent.Core.Export;
using CxAgent.Core.Jobs;
using CxAgent.Core.Models;
using CxAgent.Core.Storage;

namespace CxAgent.Core.Sessions;

/// <summary>
/// Brings a restored session's workers back: each one the session spawned, rebuilt from its own history, kept under
/// the handle the model knows it by, with its past in its feed for a tab to show.
///
/// <para>WORKERS LIVED ONLY IN MEMORY. A restored session remembered spawning them — their handles and their reports
/// are in its context — but the store was empty, so the panels listed none, no tab could open one, and the model's
/// next agent_send to one was told it did not exist.</para>
///
/// <para>THE SAME ID, so everything that already names the worker still finds it: the envelopes in the parent's
/// history, a card's "open", and the export, which looks a child up by id beside the call that reached it.</para>
///
/// <para>IDLE, whatever it was doing when the process ended: a background run that was cut off is not resumed, and
/// its report never arrived — the model can send it the work again.</para>
/// </summary>
internal static class WorkerRevival
{
    /// <summary>
    /// Revives the workers of the session whose newest agent is <paramref name="sessionAgentId"/>, and answers how
    /// many came back.
    /// </summary>
    public static int Revive(AppPaths paths, string sessionAgentId, SubAgentFactory factory, AgentTypeCatalog types,
        SubAgentStore store)
    {
        if (ExportSource.Load(paths, sessionAgentId) is not { } session) return 0;

        // THE SESSION'S COPIES, oldest first: a worker spawned before a resume has its folder under the copy that
        // spawned it, and one brought back by an earlier restore has another under each copy it worked in since.
        var copies = session.Entries.Select(e => e.Dir).Distinct(StringComparer.Ordinal).ToList();
        var revived = 0;
        foreach (var spawn in Spawns(session))
        {
            if (store.FindByAgentId(spawn.AgentId) is not null || store.Find(spawn.Name) is not null) continue;

            var entries = copies.Select(dir => Path.Combine(dir, spawn.AgentId))
                .Select(dir => ExportSource.LoadDir(dir, new ExportHeader(spawn.AgentId, null, session.Header.WorkingDir, null, null, 0, 0)))
                .Where(s => s is not null)
                .SelectMany(s => s!.Entries)
                .ToList();
            if (entries.Count == 0) continue;

            // ITS TYPE AS IT IS CONFIGURED NOW, on the type's own provider: a type since removed or disabled leaves the
            // worker in the past, where its rows still show it.
            if (types.Resolve(spawn.Type) is not { } type || type.Pick(null).Routing is not { } routing) continue;
            var child = factory.Create(new SubAgentFactory.ChildRequest
            {
                Id = spawn.AgentId,
                Label = spawn.Description,
                ParentAgentId = sessionAgentId,
                Type = type with { Routing = routing },
            });

            var context = ContextOf(entries);
            child.Agent.Context.Replace(context);
            child.Agent.History?.Adopt(child.Agent.Context.Messages);

            // ITS PAST, FOR ITS TAB: a view attaching to the feed is played what the feed has seen.
            if (child.Feed is { } feed)
                SessionReplay.PlayWorker(new ExportSource(new ExportHeader(spawn.AgentId, null, session.Header.WorkingDir,
                    null, null, 0, 0), entries, true, entries[^1].Dir), feed);

            // ITS LAST END AS ITS HISTORY HAS IT: the state of the last report it sent, over its last run — from the
            // last message it was sent to the last it wrote. Not its whole record, which spans every run it had.
            var lastRun = entries.LastOrDefault(e => e.Message is { Role: "user", ToolCallId: null, IsTaskList: false })?.At ?? entries[0].At;
            if (store.Restore(spawn.Name, child, spawn.Description, new AgentEnd(spawn.LastState, lastRun, entries[^1].At)))
                revived++;
        }
        return revived;
    }

    /// <summary>
    /// The conversation a worker's history ends with: everything since its last compaction or clear — the summary a
    /// compaction left included — under its first system prompt.
    /// </summary>
    internal static List<ChatMessage> ContextOf(IReadOnlyList<ExportEntry> entries)
    {
        var start = 0;
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            var e = entries[i];
            if (e.Kind == HistoryLine.Kinds.Cleared) { start = i + 1; break; }
            if (e.Message is { Role: "assistant" } m && m.Content.StartsWith(SessionCompressor.SummaryMarker, StringComparison.Ordinal))
            {
                start = i;
                break;
            }
        }

        var messages = entries.Skip(start).Where(e => e.Kind == HistoryLine.Kinds.Message && e.Message is not null)
            .Select(e => e.Message!).ToList();
        if (messages.FirstOrDefault()?.Role != "system"
            && entries.FirstOrDefault(e => e.Message?.Role == "system")?.Message is { } system)
            messages.Insert(0, system);
        return messages;
    }

    private sealed record Spawn(string AgentId, string Name, string Type, string? Description)
    {
        public string? LastState { get; set; }
    }

    /// <summary>Every worker the session's own agent spawned, once each, in the order they were spawned, with the state
    /// its last report gave.</summary>
    private static List<Spawn> Spawns(ExportSource session)
    {
        var results = new Dictionary<string, ExportEntry>(StringComparer.Ordinal);
        foreach (var e in session.Entries)
            if (e.Message is { Role: "tool", ToolCallId: { } id }) results.TryAdd(id, e);

        var spawns = new Dictionary<string, Spawn>(StringComparer.Ordinal);
        foreach (var e in session.Entries)
        {
            foreach (var call in e.Message?.ToolCalls ?? [])
            {
                if (call.Name is not (Tool.Agent or Tool.AgentSend) || call.Id is null
                    || !results.TryGetValue(call.Id, out var result)
                    || SubAgentEnvelope.IdOf(result.Message!.Content) is not { } childId)
                    continue;

                // A SPAWN NAMES IT; EVERY REPORT SINCE — a send's, too — says how it last ended.
                if (call.Name == Tool.Agent && !spawns.ContainsKey(childId))
                    spawns[childId] = new Spawn(childId, SubAgentEnvelope.NameOf(result.Message.Content) ?? childId,
                        Arg(call, "type") is { Length: > 0 } t ? t : AgentTypeCatalog.DefaultTypeName,
                        Arg(call, "description"));
                if (spawns.TryGetValue(childId, out var spawn)) spawn.LastState = SubAgentEnvelope.StateOf(result.Message.Content);
            }
        }
        return [.. spawns.Values];
    }

    private static string? Arg(ToolCall call, string name) =>
        call.Arguments.ValueKind == JsonValueKind.Object && call.Arguments.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
