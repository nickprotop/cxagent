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
            foreach (var (name, tokens) in ledger.ByModel.OrderByDescending(m => m.Value))
                models.Add(new JsonObject
                {
                    ["name"] = name,
                    ["tokens"] = tokens,
                    ["cost"] = costs.TryGetValue(name, out var c) ? (double)c : null,
                });
        }

        var workers = new JsonArray();
        foreach (var row in AgentRows.For(session.SubAgents, now))
            workers.Add(new JsonObject
            {
                ["agentId"] = row.AgentId,
                ["name"] = row.Kept.Name,
                ["type"] = row.Kept.TypeName,
                ["task"] = row.Kept.Description,
                ["state"] = row.State.ToString(),
                ["running"] = row.Running,
                ["background"] = !row.Foreground,
                ["seconds"] = (int)row.Elapsed.TotalSeconds,
                ["badge"] = Badge(row.Badge),
            });

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
            ["context"] = new JsonObject { ["used"] = session.ContextUsed, ["window"] = res?.ContextWindow },
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
