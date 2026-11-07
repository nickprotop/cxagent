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
        return new JsonObject
        {
            ["type"] = "panel",
            ["sessionId"] = session.Id,
            ["context"] = new JsonObject { ["used"] = session.ContextUsed, ["window"] = res?.ContextWindow },
            ["session"] = new JsonObject
            {
                ["since"] = log.Started.ToString("O"),
                ["turns"] = log.Turns,
                ["toolCalls"] = log.ToolCalls,
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
                ["git"] = GitBranch(session.WorkingDirectory),
                ["skills"] = session.LoadedSkills.Count,
                ["permissions"] = manager.Rules?.RulesFor(session.WorkingDirectory).Rules.Count ?? 0,
                ["turnCap"] = Core.Agents.AgentHost.SessionCeilingFor(res?.Orchestrator?.MaxTurns),
                ["mcp"] = new JsonArray([.. (manager.Shared.McpStatuses?.Invoke() ?? [])
                    .Select(m => (JsonNode)new JsonObject { ["name"] = m.Name, ["tools"] = m.ToolCount, ["failed"] = m.Error is not null })]),
            },
            ["model"] = res?.InstanceName,
            ["mode"] = session.Mode.Edits.ToString(),
            ["sandbox"] = new JsonObject
            {
                ["status"] = sandbox?.StatusIfKnown?.Kind.ToString(),
                ["enabled"] = sandbox?.Settings.Enabled ?? false,
            },
            ["choices"] = new JsonObject
            {
                ["models"] = new JsonArray([.. manager.Values(Core.Commands.ValueSources.Providers).Select(v => (JsonNode)new JsonObject { ["name"] = v.Name, ["summary"] = v.Summary })]),
                ["modes"] = new JsonArray([.. manager.Values(Core.Commands.ValueSources.EditModes).Select(v => (JsonNode)new JsonObject { ["name"] = v.Name, ["summary"] = v.Summary })]),
            },
        };
    }

    /// <summary>
    /// The branch the folder's repository is on, read from <c>.git/HEAD</c> — no git process per panel refresh. Null
    /// outside a repository, and the short commit when HEAD is detached.
    /// </summary>
    internal static string? GitBranch(string folder)
    {
        try
        {
            for (var dir = new DirectoryInfo(folder); dir is not null; dir = dir.Parent)
            {
                var git = Path.Combine(dir.FullName, ".git");
                string? head = null;
                if (Directory.Exists(git)) head = Path.Combine(git, "HEAD");
                else if (File.Exists(git) && File.ReadAllText(git).Trim() is var pointer && pointer.StartsWith("gitdir:", StringComparison.Ordinal))
                    head = Path.Combine(Path.GetFullPath(pointer[7..].Trim(), dir.FullName), "HEAD");
                if (head is null) continue;
                var text = File.ReadAllText(head).Trim();
                return text.StartsWith("ref: refs/heads/", StringComparison.Ordinal) ? text[16..] : text[..Math.Min(8, text.Length)];
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
        return null;
    }
}
