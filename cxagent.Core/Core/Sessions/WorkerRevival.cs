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
/// history, a card's "open", and the export, which gathers a child by id from every copy of its session.</para>
///
/// <para>IDLE, whatever it was doing when the process ended: a background run that was cut off is not resumed, and
/// its report never arrived — the model can send it the work again.</para>
/// </summary>
/// <summary>Where revived workers are built and kept.</summary>
/// <param name="Factory">Builds each one, as a spawn would.</param>
/// <param name="Types">The agent types as configured now.</param>
/// <param name="Store">The session's store, which keeps them.</param>
internal sealed record Revival(SubAgentFactory Factory, AgentTypeCatalog Types, SubAgentStore Store);

/// <summary>What a restore could not bring back, and what came back other than it was — each with why.</summary>
/// <param name="Left">Workers that stay in the past.</param>
/// <param name="Plain">Workers back with their conversation but not as what they were: their origin's module is gone
/// or failed.</param>
internal sealed record RevivalReport(IReadOnlyList<string> Left, IReadOnlyList<string> Plain);

internal static class WorkerRevival
{
    /// <summary>
    /// Revives the workers of <paramref name="session"/> — its history, loaded once for the whole restore — under its
    /// newest agent <paramref name="parentAgentId"/>, and answers the ones that could not come back, each with why.
    /// </summary>
    public static RevivalReport Revive(ExportSource session, string parentAgentId, Revival into)
    {
        var plain = new List<string>();
        // THE SESSION'S COPIES, oldest first: a worker spawned before a resume has its folder under the copy that
        // spawned it, and one brought back by an earlier restore has another under each copy it worked in since.
        var copies = session.Entries.Select(e => e.Dir).Distinct(StringComparer.Ordinal).ToList();
        var left = new List<string>();
        foreach (var spawn in Spawns(session))
        {
            // ONE WORKER'S DAMAGED RECORD COSTS THAT WORKER, not the restore: this runs inside the wire, after the
            // host has been replaced, and a throw would leave the session half restored.
            try
            {
                switch (Revived(spawn, copies, session, parentAgentId, into))
                {
                    case { Plain: true } back: plain.Add($"{spawn.Name} ({back.Why})"); break;
                    case { } why: left.Add($"{spawn.Name} ({why.Why})"); break;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException
                or ArgumentException or System.Text.Json.JsonException)
            {
                left.Add($"{spawn.Name} ({e.Message})");
            }
        }
        return new RevivalReport(left, plain);
    }

    /// <summary>Why a worker did not come back as it was: left in the past, or back plain.</summary>
    private sealed record Outcome(string Why, bool Plain = false)
    {
        public static implicit operator Outcome(string why) => new(why);
    }

    /// <summary>Brings one worker back, or says why it stays in the past or came back plain.</summary>
    private static Outcome? Revived(Spawn spawn, List<string> copies, ExportSource session, string parentAgentId, Revival into)
    {
        var (factory, types, store) = (into.Factory, into.Types, into.Store);

        // ALREADY HERE — this run's own, or one the user dismissed — is not taken again, and is checked before anything
        // is built for it.
        if (store.FindByAgentId(spawn.AgentId) is not null) return null;
        if (store.Holds(spawn.Name)) return "its name is taken";

        var entries = copies.Select(dir => Path.Combine(dir, spawn.AgentId))
            .Select(dir => ExportSource.LoadDir(dir, new ExportHeader(spawn.AgentId, null, session.Header.WorkingDir, null, null, 0, 0)))
            .Where(s => s is not null)
            .SelectMany(s => s!.Entries)
            .ToList();
        // A WORKER THAT JOINED IDLE AND NEVER RAN has no record of its own, and none is missing: its spawn line is all
        // there is, and it comes back as it was — idle, with an empty conversation.
        if (entries.Count == 0 && !spawn.Marked) return "its record is missing";

        // AS IT WAS SPAWNED: its type as configured now, on the provider the spawn asked for, in the folder the spawn
        // confined it to — a worker revived unconfined could write where it never could. A type since removed or
        // disabled, a provider no longer offered, or a folder no longer allowed leaves it in the past, where its rows
        // still show it.
        if (types.Resolve(spawn.Type) is not { } type) return $"no type '{spawn.Type}'";
        TypeRouting routing;
        if (spawn.Instance is { } instance)
        {
            if (factory.InstanceRouting(instance) is not { } entry) return $"no provider entry '{instance}'";
            routing = entry;
        }
        else
        {
            var pick = type.Pick(spawn.Provider);
            if (pick.Routing is not { } picked) return pick.Refusal ?? "no provider";
            routing = picked;
        }
        var root = ChildRoot.Resolve(spawn.Root, factory.Policy, factory.WorkingDir);
        if (root.Refusal is { } outside) return outside;

        var child = factory.Create(new SubAgentFactory.ChildRequest
        {
            Id = spawn.AgentId,
            Label = spawn.Description,
            ParentAgentId = parentAgentId,
            Type = type with { Routing = routing },
            Root = root.Root,
            Origin = spawn.Origin,
        });

        // NEVER RAN, NO END: one that joined idle comes back idle. An end with no word reads as a failure.
        if (entries.Count == 0)
        {
            if (!store.Restore(spawn.Name, child, spawn.Description, null)) return "its name is taken";
        }
        else
        {
            child.Agent.Context.Replace(ContextOf(entries));
            child.Agent.History?.Adopt(child.Agent.Context.Messages);

            // ITS PAST, FOR ITS TAB: a view attaching to the feed is played what the feed has seen.
            if (child.Feed is { } feed)
                SessionReplay.PlayWorker(new ExportSource(new ExportHeader(spawn.AgentId, null, session.Header.WorkingDir,
                    null, null, 0, 0), entries, true, entries[^1].Dir), feed);

            // ITS LAST END AS ITS HISTORY HAS IT: the state of the last report it sent, over its last run — from the last
            // message it was sent to the last it wrote. Not its whole record, which spans every run it had.
            var lastRun = entries.LastOrDefault(e => e.Message is { Role: "user", ToolCallId: null, IsTaskList: false })?.At ?? entries[0].At;
            if (!store.Restore(spawn.Name, child, spawn.Description, new AgentEnd(spawn.LastState ?? OwnEnding(entries), lastRun, entries[^1].At)))
                return "its name is taken";
        }

        // BACK, BUT NOT AS IT WAS: an origin nothing could equip again — no module now (the feature is off), or one that
        // failed — leaves an ordinary worker with its conversation, and the session says so rather than letting the user
        // believe it is what it was.
        if (spawn.Origin is not null && (child.AugmentRefusal is not null || !factory.CanAugment))
        {
            // REFUSED, IT IS AN ORDINARY WORKER NOW, and its mark goes. Merely switched off, the mark stays: turned back on,
            // the next resume equips it again, and with nothing answering origins the mark is never read meanwhile.
            if (child.AugmentRefusal is not null) store.SetOrigin(spawn.Name, null);
            return new Outcome(child.AugmentRefusal ?? "what it came from is switched off here", Plain: true);
        }
        return null;
    }

    /// <summary>
    /// The conversation a worker's history ends with: everything since its last compaction or clear, the summary a
    /// compaction left included, and not ending on calls it never answered.
    ///
    /// <para>NO SYSTEM PROMPT: the agent writes its own on every turn, from its type as configured now, so an old one
    /// kept here would only be replaced.</para>
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

        var messages = entries.Skip(start)
            .Where(e => e.Kind == HistoryLine.Kinds.Message && e.Message is { Role: not "system" })
            .Select(e => e.Message!).ToList();

        // NOT ENDING ON CALLS IT NEVER ANSWERED: a worker cut off mid-call left calls without results, and a provider
        // refuses a request that ends that way — its first send back would fail. The calls and any results they did
        // get go; what it said before them stays.
        var last = messages.FindLastIndex(m => m.ToolCalls is { Count: > 0 });
        if (last >= 0)
        {
            var answered = messages.Skip(last + 1).Select(m => m.ToolCallId).OfType<string>().ToHashSet(StringComparer.Ordinal);
            if (messages[last].ToolCalls!.Any(c => c.Id is null || !answered.Contains(c.Id)))
                messages.RemoveRange(last, messages.Count - last);
        }
        return messages;
    }

    private sealed record Spawn(string AgentId, string Name, string Type, string? Description)
    {
        /// <summary>Where it came from, as the last origin line about it said, or null for an ordinary worker.</summary>
        public string? Origin { get; set; }

        /// <summary>Spawned by an origin line rather than the agent tool — which may never have run.</summary>
        public bool Marked { get; init; }

        /// <summary>The provider the spawn asked for, or null for its type's own.</summary>
        public string? Provider { get; init; }

        /// <summary>The folder the spawn confined it to, or null for its parent's.</summary>
        public string? Root { get; init; }

        /// <summary>The provider entry an order put it on, whatever its type offers.</summary>
        public string? Instance { get; init; }

        public string? LastState { get; set; }
    }

    /// <summary>
    /// How a worker's last run ended, read from its own history when no report says: "completed" when it ended on an
    /// answer — what a completed run is — and null otherwise.
    ///
    /// <para>FOR A WORKER NO REPORT DESCRIBES: a colleague the colleague tool brought, and every run asked from its tab,
    /// reach the parent's history as no agent or agent_send result. Without this every such worker came back from a
    /// restart as "last task failed", a run that had answered among them.</para>
    /// </summary>
    private static string? OwnEnding(IReadOnlyList<ExportEntry> entries) =>
        entries.LastOrDefault(e => e.Message is not null)?.Message is { Role: "assistant", ToolCalls: null or { Count: 0 } } last
            && !string.IsNullOrWhiteSpace(last.Content)
            ? SubAgentEnvelope.WordFor(SendOutcome.Completed)
            : null;

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
            // AN ORIGIN LINE, IN ITS PLACE IN THE FILE: one carrying a name is a spawn the agent tool did not make; a bare
            // one re-marks a worker already spawned, and the last one about a worker is what it is now.
            if (e is { Kind: HistoryLine.Kinds.Origin, Worker: { } mark })
            {
                if (mark.Name is { Length: > 0 } name && !spawns.ContainsKey(mark.AgentId))
                    spawns[mark.AgentId] = new Spawn(mark.AgentId, name,
                        mark.Type is { Length: > 0 } t ? t : AgentTypeCatalog.DefaultTypeName, mark.Description)
                    {
                        Provider = mark.Provider,
                        Root = mark.Root,
                        Instance = mark.Instance,
                        Marked = true,
                    };
                if (spawns.TryGetValue(mark.AgentId, out var marked)) marked.Origin = mark.Origin;
                continue;
            }

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
                        Arg(call, "description"))
                    {
                        Provider = Arg(call, "provider"),
                        Root = Arg(call, "root"),
                    };
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
