using System.Text.Json.Nodes;
using CxAgent.Core.Jobs;
using CxAgent.Core.Sessions;
using CxAgent.UI;

namespace CxAgent.Web;

/// <summary>
/// One session as the page's side panel shows it — the terminal's F3 panel and its F7 tasks — built fresh from what the
/// session, its ledger and the background-command board say now.
///
/// <para>A SNAPSHOT, NOT A STREAM. These figures change on many separate events (tokens, turns, workers, commands, the
/// model); one builder read every second or so, sent only when it differs, is simpler than following each, and a tab
/// that joins late gets the whole of it at once.</para>
/// </summary>
internal static class WebPanel
{
    /// <summary>A colleague's Tasks card for the page, with its state word; null for none.</summary>
    internal static JsonObject? ColleagueCard(CxAgent.Core.Colleagues.ColleagueCard? card, string state) =>
        card is null ? null : new JsonObject
        {
            ["knows"] = new JsonArray([.. card.Knows.Select(k => (JsonNode?)JsonValue.Create(k))]),
            ["version"] = card.Version,
            ["learning"] = card.Learning,
            ["frozen"] = card.Frozen,
            ["task"] = card.Task,
            ["state"] = state,
        };

    // AS MANY POINTS AS THE WIDEST PANE DRAWS; the rest stay in the session's file.
    private const int TurnsSent = 120;

    /// <summary>The newest points of a session's per-turn history, as the page's sparklines take them.</summary>
    private static JsonArray Turns(IReadOnlyList<Core.Sessions.TurnPoint>? points) =>
        [.. (points ?? []).TakeLast(TurnsSent).Select(p => (JsonNode)new JsonObject
        {
            ["tokens"] = p.Tokens, ["workers"] = p.WorkerTokens, ["cache"] = p.Cache,
            ["context"] = p.Window is > 0 and var w ? (double)p.Context / w : null,
        })];

    /// <summary>The panel for <paramref name="session"/>, whose browser log is <paramref name="log"/>.</summary>
    public static JsonObject Build(Session session, SessionEventLog log, SessionManager manager)
    {
        var res = session.Resolution;
        var ledger = session.Ledger;
        var now = DateTimeOffset.UtcNow;

        var models = new JsonArray();
        if (ledger is not null)
        {
            var costs = ledger.CostByInstance;
            var caches = ledger.CacheByInstance;
            foreach (var (name, tokens) in ledger.ByModel.OrderByDescending(m => m.Value))
                models.Add(new JsonObject
                {
                    ["name"] = name,
                    ["tokens"] = tokens,
                    ["cost"] = costs.TryGetValue(name, out var c) ? (double)c : null,
                    // THE TERMINAL'S WORDS where no figure exists — see CostWords.
                    ["costNote"] = costs.ContainsKey(name) ? null : Core.Llm.CostWords.NotReported,
                    // ABSENT, NOT ZERO, for an instance that never reported — as the terminal's row.
                    ["cache"] = caches.TryGetValue(name, out var k) ? k.Rate : null,
                });
        }

        var workers = new JsonArray();
        var rows = AgentRows.For(session.SubAgents, now).Select(r => (Row: r, Folded: false)).ToList();
        // A COLLEAGUE'S TALK WORKER FOLDED INTO ITS CARD STILL HAS A TAB, and the tab reads its own state from this list:
        // sent, marked folded, for the tab — the cards and the counts leave it out.
        foreach (var row in rows.Select(r => r.Row).ToList())
            if (row.TalkAgentId is { } talkId && talkId != row.AgentId && session.SubAgents.FindByAgentId(talkId) is { } talker)
                rows.Add(((AgentRows.Describe(session.SubAgents, talker, now) ?? AgentRows.Idle(talker)) with
                    { Badge = row.Badge, Talking = true, TalkAgentId = talkId }, true));
        foreach (var (row, folded) in rows)
        {
            // A COLLEAGUE CARRIES ITS CARD AND NO TYPE — the facts F7's card draws, in the terminal's own words.
            // THE TASK OF THE RUN SHOWN, as on the terminal's card: the conversation's while it is at work.
            var card = row.Badge is null ? null : session.Colleagues?.CardOf(row.Acting.Name);
            workers.Add(new JsonObject
            {
                ["folded"] = folded,
                ["agentId"] = row.AgentId,
                // WHAT A STOP OR A MOVE ON THE CARD REACHES: the worker whose run it shows — a colleague's Talk worker
                // while its conversation is at work, though the card is its Ask worker's.
                ["actingAgentId"] = row.Acting.Agent.Agent.Id,
                // WHICH DOOR, AND WHETHER THE OTHER IS AT WORK: an open tab re-reads both each panel, as the terminal's tab
                // re-reads them each tick — a worker kept from its tab becomes a Talk worker then, and one forgotten loses
                // its badge; the desk refuses Release while either door is busy, so the tab does not offer it then.
                ["door"] = DoorOf(session, row.Kept),
                ["otherBusy"] = AgentRows.OtherDoorBusy(session.SubAgents, row.Kept, now),
                // THE ASK WORKER'S OWN STATE when the card shows another's run — what its tab reads, so an Ask tab does not
                // take on the conversation's state, its Stop and its hidden Release.
                ["own"] = row.Own is { } own && card is not null ? new JsonObject
                {
                    ["state"] = own.State.ToString(),
                    ["running"] = own.Running,
                    ["background"] = !own.Foreground,
                    ["seconds"] = (int)own.Elapsed.TotalSeconds,
                    ["colleague"] = ColleagueCard(session.Colleagues?.CardOf(row.Kept.Name) ?? card, AgentRows.ColleagueWord(own, card)),
                } : null,
                ["name"] = row.Kept.Name,
                ["type"] = row.Badge is null ? row.Kept.TypeName : null,
                ["colleague"] = card is null ? null : ColleagueCard(card, AgentRows.ColleagueWord(row, card)),
                // ITS TALK WORKER, the card's second door — and whether this card IS one (a kept worker's conversation).
                ["talkAgentId"] = row.TalkAgentId,
                ["talk"] = session.SubAgents.KeepsConversation(row.Kept.Name),
                ["task"] = row.Kept.Description,
                ["state"] = row.State.ToString(),
                ["running"] = row.Running,
                ["background"] = !row.Foreground,
                ["seconds"] = (int)row.Elapsed.TotalSeconds,
                ["badge"] = Badge(row.Badge),
            });
        }

        var commands = new JsonArray();
        foreach (var entry in BackgroundJobBoard.Default.For(session.Owns))
        {
            var view = entry.View();
            commands.Add(new JsonObject
            {
                ["pid"] = view.Job.Pid,
                ["command"] = view.Job.Command,
                ["state"] = view.State.ToString(),
                ["exitCode"] = entry.Finished ? view.ExitCode : null,
                ["seconds"] = (int)view.Elapsed.TotalSeconds,
            });
        }

        var sandbox = session.Policy?.Sandbox;

        // WHAT THE MODEL OFFERS, ONCE IT HAS SAID. Asking is bounded and cached by the session, so the first panel
        // starts the question and a later one carries the answer; the picker stays hidden until then.
        var offered = session.KnownThinkingOptions;
        if (offered is null) _ = session.ThinkingOptionsAsync();
        return new JsonObject
        {
            ["type"] = "panel",
            ["sessionId"] = session.Id,
            // WHERE AUTO-COMPRESS STARTS, resolved as the host resolves it — the gauge's tick, as on the terminal's panel.
            ["context"] = new JsonObject
            {
                ["used"] = session.ContextUsed, ["window"] = res?.ContextWindow,
                ["compressAt"] = res is null ? null
                    : (res.Orchestrator ?? Core.Llm.OrchestratorSettings.Unbounded).EffectiveCompressThreshold(res.ContextWindow),
            },
            // THE PER-TURN HISTORY THE TERMINAL'S SPARKLINES DRAW — the newest points, enough for the widest pane.
            ["turns"] = Turns(session.Turns?.Points),
            ["session"] = new JsonObject
            {
                // THE SESSION'S OWN TALLY, the one the terminal's panel shows — not this log's, which started when the
                // first browser tab opened and counted user messages, not turns.
                ["since"] = session.Tally.Started.ToString("O"),
                ["turns"] = session.Tally.Turns,
                ["toolCalls"] = session.Tally.ToolCalls,
                ["working"] = session.IsBusy,
            },
            ["tokens"] = new JsonObject
            {
                ["models"] = models,
                ["total"] = ledger?.TotalTokens ?? 0,
                ["input"] = ledger?.InputTokens ?? 0,
                ["output"] = ledger?.OutputTokens ?? 0,
                ["workers"] = ledger?.SubAgentTokens ?? 0,
                ["cacheHitRate"] = ledger?.CacheHitRate,
                ["cost"] = ledger?.TotalCost is { } cost ? (double)cost : null,
                // WHAT THE TOTAL COVERS, or that there is none — the terminal's session line, in the same words.
                ["costNote"] = ledger is null ? null
                    : Core.Llm.CostWords.SessionNote(ledger.TotalCost is not null,
                        ledger.ByModel.Count(m => m.Value > 0 && ledger.CostByInstance.ContainsKey(m.Key)), ledger.ByModel.Count(m => m.Value > 0)),
            },
            ["workers"] = workers,
            ["commands"] = commands,
            ["here"] = new JsonObject
            {
                ["folder"] = session.WorkingDirectory,
                ["git"] = CxAgent.UI.GitTree.Branch(session.WorkingDirectory),
                // THE TREE'S SHAPE, as the terminal's panel shows it under the branch — GitTree's cached reading, so a
                // panel built every second runs git at most once per five seconds.
                ["gitTree"] = CxAgent.UI.GitTree.Branch(session.WorkingDirectory) is null ? null : CxAgent.UI.GitTree.Cached(session.WorkingDirectory),
                ["skills"] = session.LoadedSkills.Count,
                ["permissions"] = manager.Rules?.RulesFor(session.WorkingDirectory).Rules.Count ?? 0,
                ["turnCap"] = Core.Agents.AgentHost.SessionCeilingFor(res?.Orchestrator?.MaxTurns),
                ["mcp"] = new JsonArray([.. (manager.Shared.McpStatuses?.Invoke() ?? [])
                    .Select(m => (JsonNode)new JsonObject { ["name"] = m.Name, ["tools"] = m.ToolCount, ["failed"] = m.Error is not null })]),
            },
            // THE NOTES AS THE FILES HAVE THEM, for the Memory section's rows; null when memory is off, so the page
            // leaves the section out rather than saying "none" about a feature that is not on.
            ["memory"] = session.Memory is { } memory
                ? new JsonArray([.. memory.List().Select(n => (JsonNode)new JsonObject
                {
                    ["name"] = n.Name,
                    ["scope"] = n.Scope == Core.Memory.MemoryScope.System ? "system" : "project",
                    ["description"] = n.Description,
                })])
                : null,
            // THE SAME ROWS THE TERMINAL'S SECTION DRAWS; null with no colleagues, so the pane leaves it out.
            ["colleagues"] = Colleagues(session.Colleagues?.Panel()),
            // WHO IS BEING DISTILLED, in the terminal status bar's words — apart from the colleagues section, which a
            // one-to-one leaves out: there every exchange is distilled, and the page said nothing of it.
            ["distilling"] = Core.Colleagues.ColleagueDesk.DistillingText(session.Colleagues?.Distilling ?? []),
            ["model"] = res?.InstanceName,
            ["thinking"] = new JsonObject
            {
                ["level"] = session.ThinkingLevel,
                ["pending"] = session.PendingThinking,
                ["levels"] = new JsonArray([.. (offered?.Levels ?? []).Select(l => (JsonNode)l)]),
            },

            // BTW MODE: what is typed goes to the aside until it is left, so the page has to say so.
            ["aside"] = session.InAside,
            ["mode"] = session.Mode.Edits.ToString(),
            ["sandbox"] = new JsonObject
            {
                ["status"] = sandbox?.StatusIfKnown?.Kind.ToString(),
                ["enabled"] = sandbox?.Settings.Enabled ?? false,
            },
            // THE SESSION ANSWERS FOR THESE, not the manager: models and edit modes are per session (its config, its
            // agent), and the manager returns an empty list for any set it does not own.
            ["choices"] = new JsonObject
            {
                ["models"] = new JsonArray([.. session.Values(Core.Commands.ValueSources.Providers).Select(v => (JsonNode)new JsonObject { ["name"] = v.Name, ["summary"] = v.Summary })]),
                ["modes"] = new JsonArray([.. session.Values(Core.Commands.ValueSources.EditModes).Select(v => (JsonNode)new JsonObject { ["name"] = v.Name, ["summary"] = v.Summary })]),
            },
        };
    }

    /// <summary>Which of a colleague's two tabs a worker is — "talk" or "ask" — or null for any other worker.</summary>
    internal static string? DoorOf(Session session, Core.Agents.StoredAgent stored) =>
        AgentTab.Door(session.SubAgents, stored) is { Length: > 0 } door ? door.TrimStart(' ', '·') : null;

    /// <summary>A worker's badge as the page reads it — the same three facts the terminal shows — or null.</summary>
    internal static JsonObject? Badge(Core.Agents.WorkerBadge? badge) =>
        badge is null ? null : new JsonObject { ["kind"] = badge.Kind, ["name"] = badge.Name, ["glyph"] = badge.Glyph };

    /// <summary>The panel's colleagues as the page reads them, or null when there are none to show.</summary>
    internal static JsonObject? Colleagues(Core.Colleagues.ColleaguePanel? panel) =>
        panel is null ? null : new JsonObject
        {
            ["rows"] = new JsonArray([.. panel.Rows.Select(r => (JsonNode)new JsonObject
            {
                ["name"] = r.Name, ["description"] = r.Description, ["handle"] = r.Handle, ["agentId"] = r.AgentId, ["state"] = r.State,
            })]),
            ["distilling"] = new JsonArray([.. panel.Distilling.Select(n => (JsonNode)JsonValue.Create(n)!)]),
        };
}
