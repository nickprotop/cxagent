using CxAgent.Core.Llm;
using CxAgent.Core.Plugins;

namespace CxAgent.Core.Agent;

/// <summary>
/// Assembles a session's runtime: the ledger, the plugin registry, the agent-type catalog, the
/// sub-agent seam and the host itself.
///
/// <para>THIS USED TO LIVE IN THE UI. AppBootstrap.WireRunner built all of it, and six of its nine
/// constructions were pure Core types — so "the layer above a session" was UI plus session
/// assembly, and a headless host had to reimplement 136 lines to start one. TwoLiveSessionsTests
/// proved it by omission: it hand-assembled a host in 23 lines and silently skipped the type
/// catalog, the spawner, MCP and the ask-user hook.</para>
///
/// <para>A STATIC FACTORY RATHER THAN A METHOD ON Session, because Session deliberately takes only
/// its working directory — that is what lets it be constructed BEFORE the permission gate, which
/// needs its root string. A Wire() method on Session would need the gate, and the ordering cycle
/// would be back.</para>
/// </summary>
public static class SessionFactory
{
    /// <param name="session">The session to wire. Its host is replaced, and the outgoing one disposed.</param>
    /// <param name="resolution">Which provider, instance, window, agent types and limits to use.</param>
    /// <param name="shared">Process-wide services — see <see cref="SharedServices"/>.</param>
    /// <param name="ports">This session's own connections — see <see cref="SessionPorts"/>.</param>
    /// <param name="mode">Single or fan-out.</param>
    public static void Wire(Session session, ProviderResolution resolution,
        SharedServices shared, SessionPorts ports, WorkingMode mode)
    {
        // Rebuilt from THIS resolution's roles so an F7 rebinding takes effect in this session.
        // The new AgentHost below reads this field, not a startup copy.
        // THE SESSION'S POLICY GOES WITH THE REGISTRY. The gate is shared across the process and
        // the registry is per session, so this is where "which session is asking" is attached — see
        // SessionPorts.Policy.
        var plugins = shared.Gate is null
            ? PluginRegistry.CreateWithBuiltins()
            : PluginRegistry.CreateWithBuiltins(resolution.Providers, shared.Gate, ports.Policy);

        // CONSUMED ONCE, READ TWICE. Taking the session's pending resume clears it, so a later
        // F5 re-wire cannot resurrect a session the user already resumed. But BOTH the ledger's
        // seed and the host's context come from it, and taking it inline in the argument list
        // (as this used to) while also reading it for the ledger would hand the second reader a
        // null — seeding the ledger and silently discarding the entire restored conversation,
        // with every test still green. One local, both uses.
        var resumeSnapshot = session.TakePendingResume();

        // THE LEDGER IS THE COMPOSITION ROOT'S NOW (D7), not AgentHost's. Constructed here so
        // "which ledger does this agent get?" has an answer — the question per-model attribution
        // and sub-agent factories both have to ask.
        //
        // IN WireRunner, NOT AT THE TOP OF AppBootstrap, and the distinction is behavioural.
        // This method re-runs on every F5 provider change and that RESETS the spend to zero.
        // Hoisting it to startup would make the ledger survive the re-wire and report one
        // session's spend across two providers as though it were one model's.
        // CONSUMED ONCE, like the pending resume: a later re-wire must start fresh rather than
        // inherit a ledger from a switch two provider changes ago.
        var carried = session.TakeCarriedLedger();

        var ledger = carried
            ?? (resumeSnapshot is null
                ? new TokenLedger()
                : new TokenLedger(resumeSnapshot.InputTokens, resumeSnapshot.OutputTokens));

        // THE SUB-AGENT SEAM, assembled here because this is the only place that holds all of
        // it: the provider, the plugin registry, the ledger just built above, the context window
        // and the orchestrator settings. That is exactly what the ledger hoist was for — those
        // last two are private on AgentHost and were unreachable from any factory before it.
        var orchestrator = resolution.Orchestrator ?? OrchestratorSettings.Unbounded;
        // THE TYPE CATALOG. Built per re-wire, like everything else here: an F5 provider change
        // must re-resolve every type's instance against the NEW registry, or a type would keep a
        // provider the session no longer uses.
        var agentTypes = new AgentTypeCatalog(resolution.AgentTypes, resolution.Providers);

        var subAgents = new SubAgentSpawner(new SubAgentFactory(new SubAgentFactory.SubAgentRuntime
        {
            Provider = resolution.Provider!,
            InstanceName = resolution.InstanceName,
            Plugins = plugins,

            // THE PARENT'S LEDGER (D7): a child's spend is the session's spend.
            Ledger = ledger,
            Logs = shared.Logs,

            // THE SAME CEILING THE PARENT GETS, resolved once. Two expressions for one number is
            // how a configured 0 came to mean "unbounded" for the session and "the default" for
            // its children.
            MaxTurns = AgentHost.CeilingFor(orchestrator.MaxTurns),

            // THE CONSTANT, never the literal — two copies of this number desynchronise the
            // moment either moves, and a child that never compresses dies on an overflow.
            CompressAbove = orchestrator.EffectiveCompressThreshold(resolution.ContextWindow)
                ?? OrchestratorSettings.DefaultCompressThreshold,
            ContextWindow = resolution.ContextWindow,

            GlobalInstructionsDir = shared.GlobalInstructionsDir,
            Mcp = shared.Mcp,

            // THE SESSION'S OWN RULE, injected rather than copied. A type on a different instance
            // has a different window, so the threshold must be re-derived from it — and a second
            // copy of "80% of the window" in the factory would desynchronise the moment either
            // moved.
            ThresholdFor = w => orchestrator.EffectiveCompressThreshold(w)
                ?? OrchestratorSettings.DefaultCompressThreshold,

            // UNCAPPED UNLESS THE USER SAID OTHERWISE. Null is the common case and means every
            // spawn the model emits runs — the barrier still holds them all inside the turn.
            MaxConcurrentAgents = resolution.MaxConcurrentAgents,
            WorkingDir = session.WorkingDirectory,
        }),
            agentTypes);

        var host = new AgentHost(
            new AgentHost.AgentRuntime
            {
                Provider = resolution.Provider!,
                InstanceName = resolution.InstanceName,
                Plugins = plugins,

                // THE SAME workingDir THE PERMISSION GATE USES, captured once at startup.
                // Sessions and permission rules are both scoped to the project they belong to,
                // and they must agree on what "this project" means.
                WorkingDir = session.WorkingDirectory,

                // THE SESSION OWNS WHAT WAS TYPED MID-TURN, and this is the only line that connects
                // it to the turn loop. A method group rather than a lambda over the text: the agent
                // must read it at the barrier, not at wiring time, or it would capture whatever was
                // pending when the session was wired — which is nothing, forever.
                TakePendingSteer = session.TakePendingSteer,

                // OUR config folder, so a user-level CXAGENT.md applies wherever they work.
                GlobalInstructionsDir = shared.GlobalInstructionsDir,

                // The real window (when config told us one), so auto-compression derives its
                // threshold from actual headroom instead of the fixed constant. Null on
                // --mock/no-provider and whenever contextWindow is not configured.
                ContextWindow = resolution.ContextWindow,

                // Passing this is what makes the cap real: the host defaults to unbounded, so
                // omitting it silently disabled the turn cap in production while every unit
                // test still passed.
                Orchestrator = resolution.Orchestrator,

                // The toolset, but NOT the servers: ownership stays with the session. Handing
                // those over would let an F5 re-wire dispose them, killing every server on a
                // provider change and leaving the new host with a toolset over dead pipes.
                Mcp = shared.Mcp,

                Spawner = subAgents,
                Mode = mode,

                // HOW THE MODEL ASKS. The window owns the composer swap, so this is the one
                // place that can put a question where the permission gate already asks. A
                // sub-agent never gets it — Agent refuses regardless of what is passed here.
                //
                // WRAPPED, NOT PASSED DIRECTLY: AgentRuntime.AskUser is a bare Func for a type
                // that has no reason to depend on the named AskUser delegate; SessionPorts.Ask
                // is that named delegate for readability at its own call sites. The two shapes
                // are identical but a named delegate has no implicit conversion to Func.
                AskUser = ports.Ask is null ? null : (questions, ct) => ports.Ask(questions, ct),
            },
            ports.Observer,
            ports.Tools,
            new AgentHost.SessionStores
            {
                // Every completed turn lands here, so a crash leaves something to resume from.
                Resume = shared.Resume,

                // And here, for /stats — a separate archive that outlives the session.
                History = shared.History,
                Logs = shared.Logs,
            },
            resume: resumeSnapshot,

            // Built above from the same snapshot `resume` came from, so a resumed session gets
            // its spend back exactly as it did when AgentHost made this itself.
            ledger: ledger);

        // THROUGH THE SESSION, which disposes the host it replaces and records the provider and
        // instance alongside it — three facts that must move together, and used to be three
        // assignments a re-wire had to remember.
        // SO THE SESSION CAN ANSWER FOR ITSELF — /model's instances and /mode's edit modes are both
        // questions about this session, and it needs the catalog it was wired against to answer them.
        session.NotePolicy(ports.Policy);
        session.NoteCatalog(resolution.Providers, resolution.ClassifierInstance is { Length: > 0 });

        session.ReplaceHost(host, resolution.Provider!, resolution.InstanceName, plugins);
    }
}
