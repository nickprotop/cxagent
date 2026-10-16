using System.Diagnostics;
using CxAgent.Core.Agents;
using CxAgent.Core.Commands;
using CxAgent.Core.Execution;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Permissions;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using CxAgent.UI;

namespace CxAgent.Headless;

public sealed record GoalInputSource(bool Redirected, TextReader Reader);

public sealed record HeadlessSignals(CancellationToken Cancel, Func<bool> Terminated);

public sealed record HeadlessRequest(CommandLineOptions Options, AppPaths Paths, string WorkingDir,
    GoalInputSource Input, HeadlessIo Io, HeadlessSignals Signals)
{
    /// <summary>A scripted model for tests; the binary always resolves config.</summary>
    public ResolvedConfig? ResolutionForTest { get; init; }
}

/// <summary>
/// One goal, no TUI: resolve, open or resume, submit, wait, write, exit.
///
/// <para>THE SAME STORES AS THE TUI, through <see cref="Startup"/>, so a session this makes opens
/// in the TUI and a TUI session continues here.</para>
/// </summary>
public static class HeadlessRun
{
    public static async Task<int> RunAsync(HeadlessRequest r)
    {
        var h = r.Options.Headless!;
        var output = HeadlessOutput.For(h, r.Io);

        int Usage(string message)
        {
            r.Io.Err.WriteLine($"cxagent: {message}");
            return 2;
        }

        var resolution = r.ResolutionForTest
            ?? Startup.Resolve(r.Paths, Startup.Env(), r.Options.UseMock, r.Options.Instance);
        if (resolution is null) return Usage($"no provider called '{r.Options.Instance}' in config.");
        if (!resolution.HasProvider)
            return Usage("no model is configured. Run cxagent once to set one up.");

        if (h.MaxTurns is { } turns)
            resolution = resolution with
            {
                Catalog = resolution.Catalog with
                {
                    Orchestrator = (resolution.Orchestrator ?? OrchestratorSettings.Unbounded) with { MaxTurns = turns },
                },
            };

        // STDIN IS READ OFF THIS THREAD AND ABANDONED ON A SIGNAL. A redirected stdin that never
        // closes — `ssh host cxagent -p …` without -n, a CI step with an idle pipe — would otherwise
        // block here before anything ran, with Ctrl+C and SIGTERM already taken by the handlers that
        // let a turn save first. The abandoned read dies with the process.
        ComposedGoal goal;
        try
        {
            goal = await Task.Run(() => HeadlessGoal.Compose(new GoalInput(h.Goal, r.Input.Redirected, r.Input.Reader)))
                .WaitAsync(r.Signals.Cancel);
        }
        catch (OperationCanceledException)
        {
            r.Io.Err.WriteLine("cxagent: stopped while reading piped input.");
            return HeadlessEnds.ExitCode(r.Signals.Terminated() ? HeadlessEnd.Terminated : HeadlessEnd.Interrupted);
        }
        if (goal.Error is { } goalError) return Usage(goalError);

        var stores = Startup.Stores(r.Paths);
        var target = Startup.FindResumeTarget(stores.Sessions, r.WorkingDir, r.Options.Resume);
        if (target.Problem is { } problem) return Usage(problem);

        // EVERY USAGE ERROR BEFORE ANYTHING IS BUILT OR RESUMED. A mistyped --plugin on a -c run must
        // exit with the conversation it named still the one -c finds next time.
        var folders = PluginDiscovery.SearchFolders(resolution.PluginPaths, r.WorkingDir, r.Paths.ConfigDir);
        var plugins = HeadlessPlugins.Select(new PluginSelection(resolution.Plugins, h, folders,
            stores.Rules, r.WorkingDir));
        if (plugins.Error is { } pluginError) return Usage(pluginError);

        var bypass = h.Permissions == HeadlessPermissions.Bypass;
        if (bypass)
            r.Io.Err.WriteLine("cxagent: --dangerously-skip-permissions: every prompt is approved, "
                + "except file-tool writes into .git, .vscode, .claude and .idea.");

        var prompt = new HeadlessPrompt(bypass);
        foreach (var hash in plugins.RunApprovedHashes) prompt.ApprovePluginForRun(hash);
        var gate = PermissionDecider.WithPrompt(stores.Rules,
            notice: (_, message) => output.Said(message), prompt.Answer);

        var logs = new LogFileManager(r.Paths);
        var services = Startup.Services(new StartupServicesRequest(r.Paths, stores, gate, logs, resolution));
        var manager = services.Manager;
        var session = new Session(r.WorkingDir);

        // ONE TEARDOWN, HOWEVER THE RUN ENDS — an exception from MCP or a plugin's load included, so
        // MCP children never outlive the process and the session is always closed cleanly.
        try
        {
            return await RunSessionAsync(new OpenedRun(r, goal.Text!, output, gate, stores, services, session,
                resolution, target, plugins, folders) { Piped = goal.Piped });
        }
        finally
        {
            await TeardownAsync(manager, session, services);
        }
    }

    // EVERYTHING THE OPENED RUN NEEDS, named, so the second half of the run is its own method
    // without a dozen positional arguments.
    private sealed record OpenedRun(HeadlessRequest Request, string Goal, HeadlessOutput Output,
        PermissionDecider Gate, StartupStores Stores, StartupServices Services, Session Session,
        ResolvedConfig Resolution, ResumeTarget Target, PluginChoice Plugins, IReadOnlyList<string> Folders)
    {
        /// <summary>The piped block apart from the goal, for a custom command to put under its expansion.</summary>
        public string? Piped { get; init; }
    }

    private static async Task<int> RunSessionAsync(OpenedRun run)
    {
        var (r, goal, output, gate, stores, services, session, resolution, target, plugins, folders) = run;
        var h = r.Options.Headless!;
        var manager = services.Manager;

        // ARMED, NOT YET RETIRED: the source session is marked superseded only once this run's turn
        // has started, so nothing that fails before then costs the user the conversation.
        if (target.Snapshot is { } snapshot) session.PendResume(snapshot);

        var edits = h.Permissions switch
        {
            HeadlessPermissions.AcceptEdits or HeadlessPermissions.Bypass => EditMode.AcceptEdits,
            HeadlessPermissions.Auto => EditMode.Auto,
            _ => EditMode.AlwaysAsk,
        };
        var policy = new PermissionPolicy(r.WorkingDir, stores.Rules, edits)
        {
            SessionId = session.Id,
            LogDir = r.Paths.LogsDir,
            TrustOverride = h.TrustsFolder,
        };
        policy.Classifier = PermissionDecider.ClassifierFor(
            resolution.ClassifierInstance, resolution.Providers, resolution.ClassifierTimeoutSeconds);

        if (!policy.FolderTrusted)
            output.Said(new Message("this folder is not trusted: reads and writes that would ask are "
                + "refused. --trust trusts it for this run.", Severity.Warning));

        // NO Ask: ask_user is then not offered at all, so the model never plans around asking
        // someone who is not there.
        manager.Open(session, resolution, new SessionPorts
        {
            Observer = output,
            ToolObserver = output,
            Policy = policy,
        }, new WorkingMode(r.Options.Mode, edits));

        // AFTER OPEN, which restores a resumed session's saved level: the flag is this run's own
        // choice and overrides it. Refused up front, as a usage error, because a run that quietly
        // went at another level would answer differently from what the caller asked for.
        if (h.Thinking is { } level)
        {
            var offered = await session.ThinkingOptionsAsync();

            // NO ANSWER IS NOT A WRONG LEVEL: the model was unreachable or hung, and the same command
            // may well work in a minute — a failure (1), where a level it does not offer is usage (2).
            if (session.KnownThinkingOptions is null)
            {
                r.Io.Err.WriteLine("cxagent: --thinking: the model did not answer when asked which levels "
                    + "it offers, so the run did not start.");
                return 1;
            }
            if (!offered.Any)
            {
                r.Io.Err.WriteLine($"cxagent: --thinking: {offered.Why}.");
                return 2;
            }
            if (!offered.Levels.Contains(level))
            {
                r.Io.Err.WriteLine($"cxagent: --thinking: '{level}' is not offered by this model. "
                    + $"Levels: {string.Join(", ", offered.Levels)}.");
                return 2;
            }
            // SILENTLY, as a resume restores one: "Thinking set to high" only repeats the flag back
            // to whoever passed it, and in the JSON modes it would land on stderr.
            session.RestoreThinking(level);
        }

        gate.OnDecision = report =>
        {
            stores.History.SavePermission(new PermissionRecord(
                report.SessionId ?? session.Id, DateTimeOffset.UtcNow, report.Kind.ToString(),
                report.Decision, report.Requester, report.Root ?? r.WorkingDir, report.Subject, report.Flagged));
            if (report.Decision is "denied" or "auto-denied" or "auto-refused") output.Denied(report);
        };
        session.ChildSpawned += child => output.ChildSpawned(child.JobId, child.Child.Agent.Id);

        // ONE PER MODEL TURN. The event's argument is that turn's tool-call count, which is not what
        // num_turns means. Boxed so the handler's increment and the read below share one value.
        var turnCount = new System.Runtime.CompilerServices.StrongBox<int>();
        session.TurnCompleted += (_, _) => Interlocked.Increment(ref turnCount.Value);

        try
        {
            await services.Mcp.ReloadAsync(resolution.McpServers, r.Signals.Cancel);
        }
        catch (OperationCanceledException) { }

        var skippedMcp = services.Mcp.Statuses()
            .Where(s => s.Enabled && s.Error is not null)
            .Select(s => s.NeedsAuth ? $"{s.Name} (needs login)" : $"{s.Name} ({s.Error})")
            .ToList();

        // WHAT WAS CHOSEN IS NOT WHAT LOADED. A plugin that fails its own load is skipped, with why,
        // so a consumer reading `plugins` never relies on tools that are not there.
        IReadOnlyList<PluginDiscovery.PluginNotLoaded> notLoaded = [];
        if (plugins.Load.Count > 0)
            notLoaded = await PluginDiscovery.LoadConfiguredAsync(session, plugins.Load, folders, r.Paths.ConfigDir,
                message => output.Said(new Message(message, Severity.Warning)), r.Signals.Cancel,
                allowClients: false);
        var failedNames = notLoaded.Select(n => n.Name).ToHashSet(StringComparer.Ordinal);

        output.Start(new RunStart(session.SessionId, r.WorkingDir,
            resolution.InstanceName is { } i ? $"{i}:{resolution.Provider?.ModelId}" : resolution.Provider?.ModelId ?? "",
            PermissionModeName(h.Permissions), session.OfferedToolNames(),
            services.Mcp.Statuses().Select(s => (s.Name, s.Error is null ? "connected" : "failed")).ToList(),
            [.. plugins.Load.Keys.Where(n => !failedNames.Contains(n))],
            new RunSkipped([.. plugins.Skipped, .. notLoaded.Select(n => n.ToString())], skippedMcp), policy.FolderTrusted));

        var clock = Stopwatch.StartNew();
        // SubmitRaw, NOT Submit: a goal is always a goal. "/clear" goes to the model as text.
        //
        // EXCEPT A CUSTOM COMMAND, which IS a goal — a saved one — so -p "/review 7" runs it as the
        // TUI would. FindTyped answers null for a built-in or an unknown name, which keep going as
        // text. Piped input goes under the expansion, never into $ARGUMENTS.
        var submitted = h.Goal is { } typed && session.FindTyped(typed) is { } entry
            ? session.RunCommand(new TypedCommand(typed, entry) { Attachment = run.Piped })
            : session.SubmitRaw(goal);
        if (submitted is not Session.SubmitOutcome.Started started)
        {
            r.Io.Err.WriteLine("cxagent: the session could not start a turn.");
            return 2;
        }

        // THE CONVERSATION THIS CONTINUES IS RETIRED NOW, once its successor exists.
        if (target.Snapshot is { } continued) stores.Sessions.MarkSuperseded(continued.AgentId);

        using var stop = r.Signals.Cancel.Register(() => session.CancelTurn());
        await started.Turn;
        var outcome = await started.Outcome;
        var text = await started.Result;

        var end = HeadlessEnds.From(outcome, r.Signals.Terminated());
        var ledger = session.Ledger;
        var killed = DetachedProcessRegistry.Default.Live
            .Select(l => l.Job is { } job ? $"{l.Process.Pid} {job.Command}" : $"{l.Process.Pid}").ToList();

        output.Finish(new RunResult(end, text,
            end == HeadlessEnd.Answered ? [] : [output.LastError ?? ErrorFor(end)],
            session.SessionId, Volatile.Read(ref turnCount.Value), clock.ElapsedMilliseconds,
            new RunSpend(ledger?.InputTokens ?? 0, ledger?.OutputTokens ?? 0, ledger?.CachedInputTokens ?? 0,
                ledger?.TotalCost, resolution.InstanceName ?? resolution.Provider?.ModelId ?? ""),
            killed));

        return HeadlessEnds.ExitCode(end);
    }

    private static string ErrorFor(HeadlessEnd end) => end switch
    {
        HeadlessEnd.TurnLimit => "stopped at the turn limit before finishing.",
        HeadlessEnd.Interrupted => "interrupted.",
        HeadlessEnd.Terminated => "terminated.",
        _ => "the run failed; see stderr for the provider's message.",
    };

    private static string PermissionModeName(HeadlessPermissions p) => p switch
    {
        HeadlessPermissions.AcceptEdits => "acceptEdits",
        HeadlessPermissions.Auto => "auto",
        HeadlessPermissions.Bypass => "bypassPermissions",
        _ => "default",
    };

    // CLOSED CLEANLY, so -c can find it, then the manager's dispose reaps background jobs and
    // plugin children, and MCP servers stop.
    private static async Task TeardownAsync(SessionManager manager, Session session, StartupServices services)
    {
        session.MarkFinished();
        manager.Close(session);
        manager.Dispose();
        try { await services.Mcp.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception) { }
    }
}
