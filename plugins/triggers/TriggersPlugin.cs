using System.Globalization;
using CxAgent.Core.Jobs;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;

namespace CxAgent.Plugins.Triggers;

/// <summary>
/// Work that starts without anybody typing — on a clock, or when a process exits.
///
/// <para>THE FIRST PRODUCTION CONSUMER OF CONTRACT 3. It declares the client, so
/// <see cref="IPluginContext.Client"/> is non-null and a fire can submit into the session that
/// created the trigger. Everything else it needs already shipped: commands on both loaders, a
/// <see cref="IPluginContext.Lifetime"/> that actually cancels so a timer dies with its session, and
/// a sever that outranks a turn the plugin started.</para>
///
/// <para>A TRIGGER DIES WITH THE PROCESS, and that is not a choice this plugin gets to make. A
/// session is a folder since phase one and a half, and IPluginContext carries no path to it — so
/// there is nowhere to write that would be deleted with the conversation. Phase three owns
/// durability, because a daemon has to answer missed-fire policy anyway.</para>
/// </summary>
public sealed class TriggersPlugin : IPlugin, IPluginClientConsumer, IPluginCommandHandler,
    IPluginGateSource
{
    private IPluginContext? _context;

    /// <summary>
    /// THE SIDECAR IS THE MANIFEST, parsed and returned rather than restated — see CalculatorPlugin
    /// for the same shape. One JSON, true by construction.
    /// </summary>
    public Task<PluginManifest> Load(IPluginContext context, CancellationToken ct)
    {
        _context = context;

        // THE HOST'S CONTRACT IS CHECKED FROM THIS SIDE TOO. A host OLDER than contract 3 cannot
        // refuse what it has never heard of: it reads the manifest with its own rules and a field it
        // does not recognise becomes whatever its parser falls back to. Throwing here fails the load
        // cleanly and says why, which is the only honest outcome.
        if (context.HostContract < 3)
            throw new InvalidOperationException(
                $"triggers needs plugin contract 3; this host speaks {context.HostContract}. "
                + "It submits into its session, which contract 2 has no way to express.");

        // BESIDE THIS ASSEMBLY, not AppContext.BaseDirectory: that is the host's folder, and a
        // plugin is loaded from wherever it was installed.
        var here = Path.GetDirectoryName(typeof(TriggersPlugin).Assembly.Location)!;
        var sidecar = Path.Combine(here, "triggers.plugin.json");

        var parsed = PluginManifest.Parse(File.ReadAllText(sidecar));
        var manifest = parsed.Manifest
            ?? throw new InvalidOperationException(
                $"triggers.plugin.json could not be read: {string.Join("; ", parsed.Errors)}");

        return Task.FromResult(manifest);
    }

    /// <summary>
    /// Opens what it needs and RETURNS, holding nothing.
    ///
    /// <para>Session.LoadPlugin awaits this before it reports the load, so a plugin that held its
    /// timer by not returning would hang the session that asked for it. The timer lives on a task
    /// this plugin owns, ended by Lifetime cancelling at Stop.</para>
    /// </summary>
    public Task Start(CancellationToken ct)
    {
        // A TASK THIS PLUGIN OWNS, ENDED BY Lifetime — not by Stop. Unwire disposes the context and
        // cancels Lifetime BEFORE Stop is awaited, so a timer registered against this token has
        // already ended by the time Stop runs. A plugin that instead tried to cancel its own timers
        // inside Stop would be racing a Stop that is timeout-bounded and abandoned if it overruns.
        _ = Tick(_context!.Lifetime);
        return Task.CompletedTask;

        async Task Tick(CancellationToken lifetime)
        {
            // A SECOND IS THE RESOLUTION A CRON LINE NEEDS AND NO MORE. Anything finer burns wakeups
            // for a schedule whose smallest unit is a minute.
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            try
            {
                while (await timer.WaitForNextTickAsync(lifetime))
                    await FireDue(DateTimeOffset.Now);
            }
            catch (OperationCanceledException)
            {
                // The session ended. Its triggers go with it.
            }
            finally
            {
                if (_context?.SessionId is { } id) TriggerStore.SweepSession(id);
            }
        }
    }

    /// <summary>
    /// Fires everything due, and reschedules what repeats.
    ///
    /// <para>INTERNAL AND TAKING A CLOCK, so a test can advance time rather than sleep. The timer
    /// calls this with DateTimeOffset.Now.</para>
    /// </summary>
    internal async Task FireDue(DateTimeOffset now)
    {
        foreach (var trigger in TriggerStore.Due(now))
        {
            // ONLY THIS SESSION'S. The store is process-wide and this instance belongs to one
            // session; firing another's would put a stranger's goal into this client.
            if (trigger.SessionId != _context?.SessionId) continue;

            try
            {
                // wantResult: false — a wake needs no answer, so the scheduler does not ask for one.
                if (_context.Client is { } client)
                    await client.Submit(trigger.Prompt, wantResult: false, _context.Lifetime);
            }
            catch (ObjectDisposedException)
            {
                // THE SEVERED CLIENT IS THE CUE TO DROP THE WAKE, and it only arrives if something
                // catches it. This runs on a timer nobody awaits: an uncaught throw here disappears
                // and the trigger simply stops working with no message — the same class of silent
                // failure a discarded task produced in the plugin manager.
                TriggerStore.SweepSession(trigger.SessionId);
                return;
            }
            catch (Exception ex)
            {
                _context?.Logger.Log($"trigger {trigger.Id} failed to submit: {ex.Message}");
            }

            TriggerStore.Reschedule(trigger, now);
        }
    }

    public async Task<JobResult> Invoke(string toolName, JobParameters call, IJobContext context,
        CancellationToken ct)
    {
        var session = _context?.SessionId;
        if (session is null)
            return Fail("this host does not scope plugins by session, so triggers cannot be kept apart.");

        return toolName switch
        {
            "trigger_wake" => Wake(session, call),
            "trigger_list" => Listing(session),
            "trigger_update" => UpdateOne(session, call),
            "trigger_cancel" => CancelOne(session, call),
            "trigger_on_exit" => await OnExit(session, call),
            _ => Fail($"unknown tool '{toolName}'"),
        };
    }

    private static JobResult Wake(string session, JobParameters call)
    {
        var prompt = call.Get<string>("prompt");
        if (!When.TryParse(call.Get<string?>("after", null), call.Get<string?>("at", null),
                call.Get<string?>("every", null), out var when, out var refusal))
            return Fail(refusal!);

        var trigger = TriggerStore.Add(session, when!, prompt);
        return Say($"trigger {trigger.Id} set: fires {when!.Describe()}.");
    }

    private static JobResult Listing(string session) =>
        Say(CommandLine.Render(TriggerStore.For(session)));

    private static JobResult UpdateOne(string session, JobParameters call)
    {
        var id = call.Get<int>("id");
        var prompt = call.Get<string>("prompt");
        if (!When.TryParse(call.Get<string?>("after", null), call.Get<string?>("at", null),
                call.Get<string?>("every", null), out var when, out var refusal))
            return Fail(refusal!);

        var updated = TriggerStore.Update(session, id, when!, prompt);
        if (updated is null)
            return Fail($"no trigger {id} in this session.");

        return Say($"trigger {updated.Id} updated: fires {updated.When.Describe()}.");
    }

    private static JobResult CancelOne(string session, JobParameters call)
    {
        var id = call.Get<int>("id");
        if (!TriggerStore.Cancel(session, id))
            return Fail($"no trigger {id} in this session.");

        return Say($"trigger {id} cancelled.");
    }

    /// <summary>
    /// Starts the watch and RETURNS — the tool call must not block on a process that may run for
    /// half an hour. The wait itself continues on a task this plugin owns, registered against
    /// <see cref="IPluginContext.Lifetime"/> so Stop's cancellation reaches it the same way a
    /// trigger's timer does.
    /// </summary>
    private Task<JobResult> OnExit(string session, JobParameters call)
    {
        var command = call.Get<string>("command");
        var timeoutText = call.Get<string>("timeout");
        var prompt = call.Get<string>("prompt");

        if (!When.TryParseDuration(timeoutText, out var timeout))
            return Task.FromResult(Fail(
                $"'{timeoutText}' is not a duration: an integer and one unit suffix, s/m/h/d — "
                + "\"30m\", \"2h\". No compound forms."));

        // NOT AWAITED BY THE TOOL CALL. The watch runs on its own task, ended by Lifetime exactly as
        // the clock's Tick is — see Start's comment on why Stop must not be the thing that cancels it.
        _ = Watch(command, timeout, prompt, _context!.Lifetime);

        return Task.FromResult(Say($"watching `{command}`; you'll be woken in up to "
            + $"{timeoutText} when it exits."));
    }

    private async Task Watch(string command, TimeSpan timeout, string prompt, CancellationToken lifetime)
    {
        var outcome = await ProcessWatch.Run(command, timeout, _context!.RegisterChildProcess, lifetime);
        var composed = ProcessWatch.Compose(prompt, outcome);

        try
        {
            if (_context.Client is { } client)
                await client.Submit(composed, wantResult: false, lifetime);
        }
        catch (ObjectDisposedException)
        {
            // THE SEVERED CLIENT IS THE CUE TO DROP THE WAKE, exactly as FireDue treats it — this
            // runs on a task nobody awaits, so an uncaught throw here disappears silently.
        }
        catch (Exception ex)
        {
            _context.Logger.Log($"trigger_on_exit for '{command}' failed to submit: {ex.Message}");
        }
    }

    /// <summary>
    /// The gate a dynamic tool is asked for, once, when the tool is CALLED.
    ///
    /// <para>NAMING THE COMMAND IN THE PROMPT IS WHAT THIS BUYS. `gated: "dynamic"` hands this the
    /// ARGUMENTS, so the prompt says "run `gh run watch 12345` in the background until it exits"
    /// rather than "run trigger_on_exit" — a decision someone can actually make, against one nobody
    /// can.</para>
    ///
    /// <para>AND THE SUBJECT IS THE COMMAND, not the sentence around it: PermissionRequest.What is
    /// Subject ?? Display, and ActionClassifier reads What.</para>
    ///
    /// <para>ONCE AT CREATION IS ALL THERE IS. The gate fires when trigger_on_exit is CALLED, not
    /// when the process exits, so the user approves the command once and the firing carries no
    /// further question. That is not an optimisation — it is what a tool gate already does.</para>
    /// </summary>
    public PluginGate? Gate(string toolName, JobParameters call)
    {
        if (toolName != "trigger_on_exit") return null;

        var command = call.Get<string?>("command", null);
        if (string.IsNullOrWhiteSpace(command)) return null;

        return new PluginGate($"run `{command}` in the background until it exits")
        {
            Subject = command,
        };
    }

    private static JobResult Fail(string why) =>
        new() { Success = false, ErrorMessage = why };

    private static JobResult Say(string text) =>
        new() { Success = true, Output = new Dictionary<string, object?> { ["content"] = text } };

    /// <summary>
    /// The four commands a person types.
    ///
    /// <para>NAMED WITHOUT THE SLASH, which is what the manifest declares and what Core hands back —
    /// Core adds the slash on both sides, so a manifest cannot smuggle a namespace into a name.</para>
    /// </summary>
    public Task<CommandResult> RunCommand(string name, string arguments, CancellationToken ct)
    {
        var session = _context?.SessionId;
        if (session is null)
            return Task.FromResult(new CommandResult(
                "this host does not scope plugins by session.", PluginCommandOutcome.Refused));

        return Task.FromResult(name switch
        {
            "triggers-list" => new CommandResult(
                CommandLine.Render(TriggerStore.For(session)), PluginCommandOutcome.Reported),
            "triggers-add" => Add(session, arguments),
            "triggers-update" => UpdateFrom(session, arguments),
            "triggers-cancel" => CancelFrom(session, arguments),
            _ => new CommandResult($"'{name}' is not a triggers command.",
                PluginCommandOutcome.Refused),
        });
    }

    private static CommandResult Add(string session, string arguments)
    {
        if (!CommandLine.TrySplit(arguments, out var whenText, out var prompt, out var splitRefusal))
            return new CommandResult(splitRefusal, PluginCommandOutcome.Refused);

        if (!CommandLine.TryReadWhen(whenText!, out var when, out var whenRefusal))
            return new CommandResult(whenRefusal, PluginCommandOutcome.Refused);

        var trigger = TriggerStore.Add(session, when!, prompt!);
        return new CommandResult($"trigger {trigger.Id} set: fires {when!.Describe()}.",
            PluginCommandOutcome.Changed);
    }

    private static CommandResult UpdateFrom(string session, string arguments)
    {
        // <id> <when> <prompt> — an id token first, then the same grammar TrySplit already knows,
        // so a quoted cron line still gets its one boundary.
        var text = arguments.Trim();
        var idSplit = text.IndexOf(' ');
        if (idSplit < 0)
            return new CommandResult(
                "say the id, when and what: `<id> <when> <prompt>` — for example "
                + "`3 30m check the deploy again`.", PluginCommandOutcome.Refused);

        var idText = text[..idSplit];
        if (!int.TryParse(idText, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            return new CommandResult($"'{idText}' is not a trigger id. Use the number from "
                + "/triggers-list.", PluginCommandOutcome.Refused);

        if (!CommandLine.TrySplit(text[idSplit..], out var whenText, out var prompt,
                out var splitRefusal))
            return new CommandResult(splitRefusal, PluginCommandOutcome.Refused);

        if (!CommandLine.TryReadWhen(whenText!, out var when, out var whenRefusal))
            return new CommandResult(whenRefusal, PluginCommandOutcome.Refused);

        var updated = TriggerStore.Update(session, id, when!, prompt!);
        if (updated is null)
            return new CommandResult($"no trigger {id} in this session.",
                PluginCommandOutcome.Refused);

        return new CommandResult($"trigger {updated.Id} updated: fires {updated.When.Describe()}.",
            PluginCommandOutcome.Changed);
    }

    private static CommandResult CancelFrom(string session, string arguments)
    {
        var text = arguments.Trim();
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            return new CommandResult($"'{text}' is not a trigger id. Use the number from "
                + "/triggers-list.", PluginCommandOutcome.Refused);

        if (!TriggerStore.Cancel(session, id))
            return new CommandResult($"no trigger {id} in this session.",
                PluginCommandOutcome.Refused);

        return new CommandResult($"trigger {id} cancelled.", PluginCommandOutcome.Changed);
    }

    /// <summary>
    /// A BACKSTOP FOR A TIMER THAT NEVER STARTED. Tick's finally block is the sweep that matters in
    /// the normal path — it runs at sever, before Stop is even awaited — but Session.LoadPlugin calls
    /// Start inside a try/catch (Session.cs) and unwires the plugin on a throw. If Start threw, or
    /// this instance never reached the point of registering its timer, Tick's finally never ran and
    /// this session's triggers would sit in the process-wide store forever: unreachable, since FireDue
    /// only fires a trigger whose SessionId matches the live instance, but never freed either.
    ///
    /// <para>SweepSession is idempotent, so sweeping here even when Tick already did costs nothing.
    /// Reading _context here is safe: SessionId is captured at construction (PluginResolver.cs), not
    /// looked up live, so it survives past Unwire disposing the context. Stop is timeout-bounded and
    /// abandoned if it overruns, which is exactly why the timer's own finally — not this — is the path
    /// that normally does the work.</para>
    /// </summary>
    public Task Stop(CancellationToken ct)
    {
        if (_context?.SessionId is { } id) TriggerStore.SweepSession(id);
        return Task.CompletedTask;
    }
}
