using CxAgent.Core.Commands;
using CxAgent.Core.Llm;
using CxAgent.Core.Jobs;
using CxAgent.Core.Storage;

namespace CxAgent.Core.Sessions;

/// <summary>
/// What a session does when a user types a slash command.
///
/// <para>SPLIT FROM THE SESSION'S STATE, not from its behaviour: this is the same type, and every
/// method here reads the fields declared in Session.cs. The division is what a reader is looking
/// for — "how does a session run a turn" and "what does /model do" are different questions, and one
/// file answering both was 1,239 lines.</para>
///
/// <para>EACH ONE DOES, SAYS AND ANNOUNCES. A command that returned text for a caller to print would
/// put the wording in the front end, where a second one reproduces it differently — so these say
/// their own results through the session's observer and return only whether they handled it.</para>
/// </summary>
public sealed partial class Session
{
    /// <summary>
    /// Says what skills this session can reach, and which SKILL.md files were skipped.
    ///
    /// <para>DISCOVERED NOW, from the working directory — the same read the agent does each turn, so
    /// what this reports is what the model is seeing rather than a copy that could disagree.</para>
    /// </summary>
    public CommandStatus ListSkills()
    {
        Say(new Commands.SkillsCommand(
            () => Skills.SkillCatalog.Find(WorkingDirectory, Services?.GlobalInstructionsDir ?? ""),
            // WHAT THE AGENT WAS ACTUALLY OFFERED. Listing loadable skills to a user whose agent
            // cannot load one is the same defect as the todo header naming a withheld todowrite.
            CxAgent.Core.Jobs.ToolSelection.Offers(ToolSelection, CxAgent.Core.Jobs.Tool.Skill)).Render());

        return CommandStatus.Reported;
    }

    /// <summary>
    /// Says what MCP servers this session has, or the detail of one.
    ///
    /// <para>READ-ONLY BY CONSTRUCTION. Reloading re-reads config the host supplied and logging in
    /// opens a browser; both are the host's and arrive as verbs. What is left needs only the
    /// toolset and the statuses, which are here.</para>
    /// </summary>
    public CommandStatus DescribeMcp(string arguments)
    {
        if (Services?.Mcp is not { } toolset || Services?.McpStatuses is not { } statuses)
        {
            Say(new Message("No MCP servers are configured here.", Severity.Warning));
            return CommandStatus.Reported;
        }

        Say(Commands.SessionCommands.DescribeMcp(statuses(), arguments, [.. toolset.Names()]));
        return CommandStatus.Reported;
    }

    /// <summary>
    /// Says what commands this process has.
    ///
    /// <para>THE TABLE IS CORE'S, so this needs nothing a front end holds. A front end with keys
    /// of its own registers over the command and prepends them; one with nothing to add gets a
    /// working /help for free.</para>
    /// </summary>
    public CommandStatus ShowHelp()
    {
        Say(Commands.SessionCommands.HelpLines(Manager?.Commands));
        return CommandStatus.Reported;
    }

    /// <summary>
    /// Says what this process has spent, over the window <paramref name="arguments"/> asks for.
    ///
    /// <para>THE STORE IS THE MANAGER'S, so it is passed rather than held: usage outlives any one
    /// session and a session that owned it would be claiming a total it did not earn alone.</para>
    ///
    /// <para>REPORTED, NEVER CLEARED HERE. Clearing needs a confirmation, and a confirmation needs
    /// somebody to ask — see the registry, where a front end overrides this command with one that
    /// can.</para>
    /// </summary>
    /// <summary>
    /// Lists the sessions in this folder, and restores one when asked.
    ///
    /// <para>THE SESSION SCOPES IT. Every row is filtered by <see cref="WorkingDirectory"/> unless
    /// the user says <c>all</c> — which is why this is a session's answer rather than the manager's,
    /// even though the manager owns the store. The composition root did this by capturing both and
    /// reaching across.</para>
    ///
    /// <para>"all" IS READ BEFORE THE DECISION, because it changes which rows EXIST rather than what
    /// is done with them — and <c>resume 3</c> has to mean the row the user is looking at. Reading it
    /// afterwards would renumber the list under the number they just typed.</para>
    ///
    /// <para>REPORTED, NEVER SWALLOWED. An empty list from a locked database says "you have no
    /// earlier sessions", which is a lie a user cannot detect — the same reason
    /// <see cref="SayUsage"/> reports its own read failures.</para>
    /// </summary>
    public CommandStatus ListSessions(string arguments)
    {
        // THE STORE AND THE MANAGER ARE THIS SESSION'S OWN. They were parameters, which meant a
        // caller had to hold Core's resume database and the manager that owns it in order to ask a
        // session what it already knows.
        if (Services?.Resume is not { } store || Manager is not { } manager)
        {
            Say(new Message("No session history is available here.", Severity.Warning));
            return CommandStatus.Reported;
        }

        try
        {
            var all = Commands.SessionCommands.ArgumentWords($"/sessions {arguments}")
                .Any(w => w.Equals("all", StringComparison.OrdinalIgnoreCase));

            var rows = store.List(all ? null : WorkingDirectory, all);
            var result = Commands.SessionsCommand.Decide(
                arguments, rows, Storage.SqliteSessionStore.DefaultRetention, all);

            if (result.ResumeUid is null)
            {
                Say(result.Reply);
                return CommandStatus.Reported;
            }

            if (store.LoadByUid(result.ResumeUid) is { Session: { } snapshot })
                manager.Resume(this, snapshot);
            else
                Say(new Message("That session could not be read back.", Severity.Warning));
        }
        catch (Exception ex)
        {
            // THE SEVERITY IS THE TONE, and stating it is not optional here: Say defaults to Info,
            // so a forgotten argument files a failure as an aside. A front end maps Error to
            // whatever red it actually uses, which a colour baked into the text cannot.
            //
            // ESCAPED, because exception text is arbitrary — a path with an underscore in it
            // renders as italics otherwise.
            Say(new Message($"Could not read sessions: {Commands.Md.Escape(ex.Message)}", Severity.Error));
        }

        return CommandStatus.Reported;
    }

    /// <summary>Says that a resume cannot be applied because nothing can rebuild this session's host.
    /// A headless arrangement with no rewire hook, which is a real configuration rather than an
    /// error — but it must be SAID, because a silent no-op reads as success.</summary>
    internal void SayCannotResume() =>
        Say(new Message("This session cannot restore an earlier conversation — nothing here can rebuild "
          + "it. Use --resume at startup instead.", Severity.Warning));

    public CommandStatus SayUsage(string arguments)
    {
        // NOT A DASHBOARD. Rendering usage for "/stats clear" answers a request to DELETE with the
        // thing the user asked to be rid of, and nothing says the deletion did not happen —
        // ParseDays simply fails to read "clear" as a number and falls back to the default window.
        // A front end that can confirm registers this verb and takes it before this runs.
        if (Commands.StatsCommand.IsClear(arguments))
        {
            Say(new Message("Clearing usage history needs a confirmation this application does "
                + "not provide.", Severity.Warning));
            return CommandStatus.Reported;
        }

        if (Services?.History is not { } history)
        {
            Say(new Message("No usage history is available here.", Severity.Warning));
            return CommandStatus.Reported;
        }

        try
        {
            Say(Commands.StatsCommand.Render(history, arguments));
        }
        catch (Exception ex)
        {
            // REPORTED, like every other read of this store: an empty dashboard would say "you have
            // spent nothing", which is a lie a user cannot detect. Severity and the escaping are
            // stated here for the same reasons as ListSessions above.
            Say(new Message($"Could not read usage history: {Commands.Md.Escape(ex.Message)}", Severity.Error));
        }

        return CommandStatus.Reported;
    }

    /// <summary>
    /// The briefing for a turn that explores this folder and writes down what it found.
    ///
    /// <para>SESSION WORK, and the only command here that spends tokens. It reads which instruction
    /// file to write, says so when the answer is not the obvious one — an existing CLAUDE.md is read
    /// but never written — and sends a briefing the user did not type.</para>
    ///
    /// <para>ECHOED AS "/init", NOT AS THE BRIEFING. The user typed three words; the model receives
    /// several paragraphs about what to explore. Putting those on the transcript as the user's own
    /// message attributes words to them they never wrote, on every later read of the log.</para>
    ///
    /// <para>REFUSED MID-TURN like every other operation that needs the agent — a second SendAsync
    /// on one agent appends to a live conversation from two loops.</para>
    /// </summary>
    /// <returns>
    /// The briefing to send, or null when there is no host to send it to.
    ///
    /// <para>RETURNED RATHER THAN SENT, and this is the one command that cannot own its own turn.
    /// A turn needs the caller's cancellation scope — the one Escape holds — and its queue, so a
    /// session that called SendAsync itself would start a turn nothing could stop or steer. The
    /// session decides WHAT to send and says what it noticed; the caller runs it the same way it
    /// runs a typed goal.</para>
    /// </returns>
    public string? InitialisePrompt()
    {
        if (Host is null || RefusedWhileBusy()) return null;

        var target = Commands.InitCommand.Resolve(WorkingDirectory);
        if (target.Note is { } note) Say(note);

        return Commands.InitCommand.Prompt(target);
    }

    /// <summary>
    /// Runs <c>/init</c>: sends the briefing and shows <c>/init</c>.
    ///
    /// </summary>
    public SubmitOutcome Initialise() =>
        InitialisePrompt() is { } briefing
            // SUBMITRAW, NOT SUBMIT. The briefing is already-resolved prose and the echo is the
            // literal text "/init" — routing it back through dispatch would re-enter the command
            // that produced it.
            ? SubmitRaw(briefing, echo: "/init")
            : new SubmitOutcome.NoAgent();

    /// <summary>
    /// Says what sub-agent types this session can spawn, or one type's full briefing.
    ///
    /// <para>FROM THE CATALOG THIS SESSION WAS WIRED WITH — the shipped types merged with config,
    /// which is what it can actually spawn. Building one from the resolution instead, as the caller
    /// did, listed only the config-declared names.</para>
    /// </summary>
    public CommandStatus ListAgentTypes(string arguments)
    {
        // NO CATALOG, NOTHING TO LIST — and Unknown rather than a reply, because a session wired
        // without agent types genuinely does not service this command.
        if (_agentTypes is not { } catalog) return CommandStatus.Unknown;

        Say(new Commands.AgentsCommand(catalog,
            CxAgent.Core.Jobs.ToolSelection.Offers(
                ToolSelection, CxAgent.Core.Jobs.Tool.Agent)).Render(arguments));
        return CommandStatus.Reported;
    }

    /// <summary>Says what changed in this session's working folder, per <c>git diff</c>.</summary>
    public CommandStatus ShowDiff(string arguments)
    {
        Say(Commands.DiffCommand.Render(arguments, WorkingDirectory));
        return CommandStatus.Reported;
    }

    /// <summary>
    /// Empties this session's conversation.
    ///
    /// <para>THE SESSION'S HALF ONLY. It drops the messages and says so; what a front end does about
    /// its own scrollback is the front end's decision — clearing it is one reasonable answer and
    /// drawing a divider is another, and a session with an opinion about that is a session that
    /// cannot be driven by a log writer or a web page. The <see cref="Changed"/> signal is how a
    /// surface learns there is something to redraw.</para>
    ///
    /// <para>REFUSED MID-TURN like every other mutation: emptying the list a running turn is
    /// appending to leaves that turn writing into a conversation nobody will read, and its next
    /// request carries tool results whose calls are gone.</para>
    /// </summary>
    public CommandStatus ClearContext()
    {
        if (Host is null || RefusedWhileBusy()) return CommandStatus.Refused;

        Host.Context.Clear();

        // ANNOUNCED BEFORE IT IS SAID, and this is the general rule rather than a quirk of clearing:
        // a watcher reacts to the signal by redrawing, and a message written first lands on a
        // surface the redraw is about to replace. Said last, it survives whatever the reaction did.
        //
        // Found here because clearing is the reaction that destroys most — this front end wipes its
        // scrollback, so the sentence written before the announcement vanished and the user was left
        // with an empty screen and no word about why.
        Announce(SessionChangeKind.ContextCleared);
        Say("Conversation cleared.");
        return CommandStatus.Changed;
    }

    /// <summary>
    /// Compacts this session's context now, if a turn is not already running.
    ///
    /// <para>REFUSED MID-TURN RATHER THAN QUEUED, and the difference from an ordinary prompt is
    /// real: a prompt is still valid when the turn ends, but this is a measurement-and-rewrite of a
    /// context that is actively changing — running it later is a DIFFERENT operation from the one
    /// that was asked for. Nothing is lost by refusing: the turn loop already compacts on measured
    /// pressure, so this costs a keystroke rather than a compaction.</para>
    ///
    /// <para>FIRE AND FORGET, returning the task for a caller that wants to await it. The refusal
    /// path returns null, having already said why.</para>
    /// </summary>
    public Task<SessionCompressor.CompressResult>? CompressNow(CancellationToken ct)
    {
        if (Host is null || RefusedWhileBusy()) return null;

        return Host.CompressNowAsync(ct);
    }

    /// <summary>
    /// Points this session at a different model, keeping the conversation.
    ///
    /// <para>SESSION WORK, and it belongs here rather than in a caller: it reads this
    /// session's state and mutates this session's state. What the UI keeps is reporting the change,
    /// which is the only part that needs a window.</para>
    ///
    /// <para>FALSE WHEN THERE IS NO HOST — a switch before the first wire has nothing to point
    /// anywhere, and the caller says so rather than this throwing.</para>
    /// </summary>
    /// <summary>
    /// Switch to the instance the user named — resolved from this session's own catalog.
    ///
    /// <para>THE BY-NAME HALF, and the one that was missing. <c>/model openrouter</c> went through
    /// <c>ConfigResolver.ResolveInstance</c> in the composition root: a config.json read, a
    /// re-validation, a registry rebuild and a window probe, to answer a question the catalog in this
    /// session's hand could already answer. The result was then stripped to its <c>.Model</c> and the
    /// rest thrown away.</para>
    ///
    /// <para>NO PATHS, NO ENVIRONMENT, SO NO RULE BROKEN. Reading configuration is the process's job
    /// and a session has neither — which is exactly why this could not live here before. Deriving
    /// from the catalog needs neither, so the answer comes from what this session was handed at wire
    /// time. That also makes it consistent with what <c>/model</c> OFFERS: the completion list is
    /// already built from this same catalog, so a name it suggests is now a name this resolves.</para>
    ///
    /// <para>THE OTHER OVERLOAD IS NOT REACHABLE THROUGH THIS ONE. A caller holding a provider the
    /// catalog never knew about — a test double, a headless embedder — has no name to pass, which is
    /// why both exist rather than one wrapping the other.</para>
    /// </summary>
    /// <summary>
    /// Switches model from what the user typed at <c>/model</c> — decided here, not by the caller.
    ///
    /// <para>BOTH INPUTS ARE THE SESSION'S. <c>ModelCommand.Decide</c> needs the catalog and the
    /// instance in use, and this holds each — so a caller doing it would be reaching across for two
    /// values it had already been handed.</para>
    ///
    /// <para>THE REPLY IS SAID ONLY WHEN NOTHING MOVED — a listing, an unknown name, an
    /// already-on-that-model. A real switch is announced by <c>SwitchModel</c>, which knows
    /// what the window and the spend became; saying anything here would report it twice.</para>
    /// </summary>
    /// <remarks>
    /// FROM INPUT, because it parses what a user typed — an empty argument lists the models, a name
    /// switches, an unknown name says so. <see cref="Use(string?)"/> takes a name that has already
    /// been decided on, which is a different job; the pair mirrors <see cref="SetMode(string)"/> and
    /// <see cref="SetMode(WorkingMode)"/>.
    /// </remarks>
    public CommandStatus UseFromInput(string argument)
    {
        var decision = Commands.ModelCommand.Decide(argument, _catalog, InstanceName);

        if (decision.SwitchTo is null)
        {
            if (decision.Reply.Text.Length > 0) Say(decision.Reply);
            return CommandStatus.Reported;
        }

        return Use(decision.SwitchTo);
    }

    public CommandStatus Use(string? instanceName)
    {
        // THE NULL IS THE MESSAGE. Use returns null for a name the catalog does not know, and the
        // overload below already owns saying so — one speaker per outcome, as its own comment says.
        return Use(_catalog?.Use(instanceName), instanceName);
    }

    /// <summary>
    /// Switches to this exact model — for a caller holding one the catalog never knew, such as a test
    /// double or a provider an embedder built itself.
    ///
    /// <para>TWO ENTRY POINTS, NOT FOUR. There was also <c>Use(ActiveModel?, string?)</c>, a pure
    /// alias for this with no callers: a name and a model are genuinely different inputs — one can
    /// fail to resolve, the other cannot — and everything else was spelling.</para>
    /// </summary>
    public CommandStatus Use(ActiveModel? next, string? requestedName = null)
    {
        if (RefusedWhileBusy()) return CommandStatus.Refused;

        // THE FAILURE IS SAID HERE TOO, not by whoever resolved. A caller that reported it printed a
        // second, vaguer sentence on top of whatever this had already said — visible on screen as
        // "Could not switch to openrouter." directly under "A turn is running". One speaker per
        // outcome, and this is the speaker.
        if (next is null)
        {
            Say(new Message($"Could not start {requestedName ?? "that model"} — it did not resolve to a "
              + "usable provider. Check its entry in config.json.", Severity.Error));
            return CommandStatus.Refused;
        }

        if (Host is null) return CommandStatus.Refused;

        // READ BEFORE THE SWAP, because the announcement below compares what the context WAS on the
        // model being left against the window it is moving to. Reading them here keeps that ordering
        // out of a caller, which would have to know about it and could get it wrong.
        var previousWindow = Host.Context.Window;
        var used = Host.Context.Used;

        Host.SwapProvider(next);

        // AND THE CHILDREN'S DEFAULT. A child with no provider of its own inherits it from the
        // spawner, which holds the model captured at wire time — leave it and every sub-agent keeps
        // talking to the model the session started on, so the usage archive records the old instance
        // for every run after a switch while the switch notice promises the opposite.
        //
        // FUTURE CHILDREN ONLY. One already running keeps its provider: it holds its own dialogue
        // with that model, and moving it mid-flight would split that dialogue across two endpoints.
        _spawner?.SwapDefaultProvider(next.Provider, next.ContextWindow, next.InstanceName);

        // THE SESSION'S OWN COPIES FOLLOW. /model's completions and the panel read InstanceName from
        // here, so leaving these behind would offer the user the model they just left.
        Provider = next.Provider;
        InstanceName = next.InstanceName;

        // THE CATALOG IS UNTOUCHED, and unreachable from here: this method takes an ActiveModel, so
        // there is no configuration in scope to replace by accident. That is the whole reason for
        // the split — a single swap that moves the agent and the host but not the spawner leaves
        // nothing naming the set that has to move together.
        Resolution = Resolution?.WithModel(next) ?? new ResolvedConfig(next, Llm.ProviderCatalog.Empty, []);

        Announce(SessionChangeKind.Model);
        Say(ModelSwitchNotice.For(next.InstanceName ?? next.Provider.ProviderId, next.Provider.ModelId,
            next.ContextWindow, previousWindow, used));

        return CommandStatus.Changed;
    }

    /// <summary>
    /// Arms the next wire to continue THIS conversation on another instance — the <c>/model</c>
    /// handoff.
    ///
    /// <para>TWO THINGS CARRY, AND THEY CARRY TOGETHER. The conversation, so a switch is not a
    /// restart; and the LEDGER, which a re-wire otherwise starts fresh. Fresh is right when the
    /// provider is being reconfigured and wrong here: a user switching model mid-conversation
    /// expects <c>/stats</c> to show the whole session, and the ledger already tallies by
    /// instance:model — which is exactly the question a switch creates.</para>
    ///
    /// <para>ONE CALL RATHER THAN TWO, because arming one without the other is silently wrong in
    /// both directions: the conversation without the ledger loses the session's spend, and the
    /// ledger without the conversation attributes the old spend to a fresh chat.</para>
    /// </summary>
    /// <returns>False when there is no host to carry from — nothing is armed.</returns>
    public bool CarryToNextWire()
    {
        if (Host is null) return false;

        PendResume(new SessionSnapshot(
            Host.SessionId, Host.Context.Snapshot(),
            Host.Ledger.InputTokens, Host.Ledger.OutputTokens, DateTimeOffset.UtcNow));
        CarryLedger(Host.Ledger);
        return true;
    }

    /// <summary>The label every spend readout uses: <c>instance:model</c>, or the bare model when no
    /// instance is named. Two entries can serve the same model with different endpoints and windows,
    /// so the model alone cannot say where the tokens went.</summary>
    public string? SpendLabel => Provider is null
        ? null
        : InstanceName is { Length: > 0 } instance
            ? $"{instance}:{Provider.ModelId}"
            : Provider.ModelId;
}
