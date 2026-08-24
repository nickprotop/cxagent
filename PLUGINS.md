# Plugins

**Status:** design
**Date:** 2026-08-24

A plugin adds capability to a session without recompiling cxagent, and without being written in C#.
The first family is LSP: a plugin per language server, each exposing the server's operations as
tools the model can call, with the protocol machinery entirely inside the plugin and Core knowing
nothing about LSP at all.

> **Writing a plugin?** Start at
> [`cxagent.Core/Core/Plugins/README.md`](cxagent.Core/Core/Plugins/README.md) — sidecars, naming,
> permission, settings, and two working calculator examples.
>
> **This document is the design**, and its audience is anyone about to CHANGE the plugin system: why
> loading is refused mid-turn, why a plugin cannot override a built-in, what the collision matrix
> decides and where each row is knowable, what v1 deliberately left out. Read it before proposing a
> change; the constraints here were expensive to arrive at and most of them are not obvious from the
> code that enforces them.

---

## What a plugin is

**A bundle of tools, each carrying the executor that runs it.**

A tool alone would have nothing to dispatch into. An executor alone is worse: `JobRegistry` maps a
type name to an executor and jobs dispatch by that name, so a bare executor is a job type anything
could route into — including a built-in tool whose name it shares. Bundling removes the question:
the executor exists to serve the tools shipped beside it, and no other tool can reach it.

**The one-executor-many-tools SHAPE is already how Core works** — though not the bundling, which is
new. `ToolBindings` maps five tools onto one `file` executor, distinguished by a pinned action:

    read_file        -> file/read
    glob             -> file/list
    grep             -> file/search
    write_file       -> file/write
    replace_in_file  -> file/replace

An LSP plugin has the same shape for the same reason: one executor holding the client and the
server's lifetime, several tools pinned to operations on it. One connection, many callable things.

**But Core's bindings are deliberately DECOUPLED from their executors, not bundled**, and the
difference matters. A binding names its executor by string through `JobRegistry`; nothing stops
another binding naming `file`, and a binding whose executor is unregistered is simply skipped. The
"no other tool can reach it" property does not exist in Core today.

So the bundling rule is this document's own, resting on the argument above rather than on precedent:
a job type is a namespace anything can route into, and a plugin contributing one unattached would be
contributing a namespace rather than a capability. The table shows the shape is workable. It does
not show that Core already enforces the bundling.

## What a plugin is not

**It cannot read the transcript.** Not the user's messages, not the model's replies, not other
tools' results. A plugin sees the arguments of its own calls and nothing else.

This removes passive reading, which is worth removing: a plugin with transcript access exfiltrates
everything typed into the session, and no load-time prompt conveys that meaningfully.

**It is not a barrier against a hostile plugin, and must not be described as one.** The model
composes tool arguments from the conversation, so a plugin already receives a model-filtered view of
it — and a prompt injection that persuades the model to pass more is not something this restriction
can stop. What stops a hostile plugin is not loading it. This narrows the accident and the casual
case; the load gate is the actual boundary.

**It cannot replace anything.** Not a built-in tool, not a command, not an executor type, not a
provider. Registration is additive only — see *Overriding is forbidden* below.

**It cannot contribute an executor on its own.** Only tools, with executors attached.

---

## Scope: one instance per session

Every plugin is instantiated per session, uniformly. There is no per-plugin choice.

A per-plugin scope declaration would mean Core maintains two lifecycle models and every plugin
author picks one — the kind of decision that produces failures nobody can reproduce, because the
behaviour depends on a field in someone else's manifest.

The cost is real and accepted: two sessions in one folder run two language servers, indexing the
same repository twice. That is the smaller of the two problems in that situation — two agents
editing one working tree is the larger one, and it is not this document's to solve. A plugin that
wants to share process-level resources between its own instances may do so internally; Core's model
stays one thing.

This also matches how the rest of the system is built. `AgentToolset` is already per-session, and a
session already owns its sub-agents and tears them down with it.

---

## Lifecycle

    Load     Core hands the plugin its context; the plugin describes itself
    Start    the plugin may spawn processes, open connections, index
    ...      tools dispatch
    Stop     the plugin shuts down; its children exit
    Unwire   its tools are deregistered; nothing can reach it again

**UNWIRE, NOT UNLOAD, and the word is the honest one.** A managed plugin's assembly cannot be
removed from the process without loading it into an `AssemblyLoadContext` of its own, and even then
only if nothing outlives it holding a reference. Calling this step "Unload" would put a promise in
the contract that Core cannot keep, and someone would eventually rely on the memory coming back.

What unwiring guarantees is what callers actually need: the tools are gone from the registry, the
model is never offered them again, no further call can reach the plugin, and its child processes are
dead. The code staying resident costs memory and nothing else.

An ABI plugin unwires more completely — its host is a process and killing it reclaims everything.
That asymmetry favours ABI for once, and is worth knowing when choosing a loader for a plugin that
will be cycled often.

**Stop runs when a session closes.** `SessionManager.Close` is the hook — it disposes the agent host
and forgets the session, and plugin Stop belongs beside that.

**But that is not every exit path, and the difference leaks processes.** `Close` runs on the normal
unwind. An unhandled exception appends `crash.log` and reaches nothing; a killed window reaches
nothing. A language server is a child process, so every crash strands one.

So Stop-on-close is necessary and not sufficient, and **orphan reaping is part of v1, not a later
refinement**. Whatever cannot be closed on the way down must be collectable on the way up: a pid
record written where the next run can find it, and reaped at startup.

**The record covers every process a plugin spawns, not only an ABI host.** An ABI plugin's host is
the obvious case, but v1's flagship is a MANAGED LSP plugin spawning a language server in-process —
no host, and the child that most needs reaping. A design that records only host pids leaks exactly
the process the section exists to collect, in exactly the case it was written for.

So the obligation is on Core and stated in the contract: **a plugin declares the processes it
spawns, Core records them, and Core reaps them.** Not the plugin's own bookkeeping, because a plugin
that crashed is a plugin that cannot clean up after itself — which is the entire scenario. The
context a plugin is handed therefore includes the means to register a child process, and doing so is
not optional for a plugin that spawns one.

**Nothing in Core does this today, and MCP has the identical leak.** `ProcessRunner` kills its own
tree only within a call; `McpClient` kills its child only in `DisposeAsync` — both live-teardown
only, and `McpClient`'s own comment concedes the gap: *"An orphaned subprocess is the one failure in
this feature that outlives the app."*

That is worth stating for two reasons. The machinery here is new rather than a wiring-up of
something existing, so it carries its own cost. And MCP is its obvious second customer: a pid record
general enough for plugin children would collect an orphaned MCP server for free, which is a
standing bug this design happens to be positioned to fix.

**Stop has a timeout, and the remedy differs by loader.** An ABI plugin's host is a process: after
the timeout it is killed and the failure logged. A managed plugin is in-process and there is nothing
to kill — a hung Stop can only be abandoned, the session closed around it, and the hang logged
loudly enough to name the plugin. That asymmetry is real and is the price of loading managed code
in-process.

---

## Loading is refused mid-turn

Adding or removing a plugin during a running turn is refused, and the refusal is said out loud.

A turn is: send the tool definitions, the model picks one, execute, send the result, the model picks
again. A plugin loading between the send and the pick means the model is choosing from a list that
no longer describes reality. Unloading is worse: a call may already be in flight for a tool that no
longer exists.

Refused rather than queued, and it is worth being exact about why, because the nearest analogy does
not transfer. `/compress` refuses rather than queues because running it later is a genuinely
DIFFERENT operation — it measures and rewrites a context that is still changing. A plugin load
deferred to the next turn boundary is the SAME operation, so that argument is not available here.

The reason is the tool-list invariant alone, and it is sufficient. `Session.SetMode` and
`Session.CompressNow` also refuse while busy and say so, so the BEHAVIOUR is consistent and nobody
has to learn a new rule — but consistency of behaviour is why refusing reads naturally, not why it
is correct.

A plugin may be loaded or unwired at any turn boundary, not only at session open. That is the point
of the mutable registry: adding a language server for the file you just opened should not require
restarting the session.

**A runtime load always prompts.** The load gate is the only boundary Core can enforce, so it cannot
be bypassed by a flag. Config declares what MAY load; the user still approves each binary the first
time its hash is seen. A configuration that could pre-approve an arbitrary binary would dissolve the
boundary this design rests on.

**Unwiring must reap.** A plugin unwired mid-session leaves a host process that startup reaping will
not see, because startup is not coming. So the pid record is cleared and the host killed at unwire,
not only at exit — the same obligation as Stop, at a different moment.

**Unwire is one ordered operation, and it contains Stop.** Both this section and the lifecycle above
claim obligations around child processes and timeouts; they are the same obligations, sequenced
once:

    1. deregister    the tools leave the registry — nothing new can reach the plugin
    2. drain         outstanding calls finish, under the Stop timeout
    3. Stop          the plugin shuts down; its children exit
    4. reap          the host is killed if it outlived Stop; the pid record is cleared

Deregistering FIRST is what makes draining finite: a plugin still reachable while draining can be
handed new work forever. Draining before Stop is what keeps a running job from failing for a reason
nobody could trace back to a plugin command — an executor's job can outlive the turn that started
it, so refusing mid-turn does not by itself mean nothing is in flight.

Session close runs the same four steps. There is no separate teardown path: closing a session is
unwiring every plugin it loaded, and a plugin cannot tell the difference.

`Session.IsBusy` is the gate. The session sets it as a turn begins and clears it in a `finally`
however the turn ends, cancellation included, so it cannot latch.

Core already holds this invariant for exactly this reason, and plugins simply fall under it —
`RefusedWhileBusy` states it: *"The tool list is fixed once a request begins — deliberately, so a
tool cannot appear or vanish between two turns of one request and leave the model chasing something
that is no longer there."* This section is not a new rule; it is that rule reaching a new source of
tools.

---

## Permission

### The load gate is the only boundary Core can enforce

When a plugin's tools are first exposed, the user approves the plugin. Once, at load, not per call.

After that, Core enforces nothing inside the plugin. A native library doing its own `open()` cannot
be intercepted; a managed plugin can call anything the process can. **Pretending otherwise would be
security theatre, and worse than being honest** — a user who believes there is a sandbox behaves
differently from one who knows there is not.

So the trust boundary is the load, and it is a real trust decision of the same kind as running any
binary. After it, the plugin author's integrity and the user's judgement are what remain.

This is the same shape as folder trust, and for the same reason: the meaningful consent is at the
boundary, not on every operation behind it.

### Therefore the load prompt has to be good

It is the only place Core can tell the truth, so naming the plugin is not enough. The prompt states
where the plugin came from and what it declares it does:

    lsp-rust wants to run a process and read files in this folder.
    /home/nick/.config/cxagent/plugins/csharp-lsp-rust.so

A user approving a plugin blind is the weak point of this design — not the absence of per-call
prompts.

### A host with no gate asks nothing, and that is the embedder's decision

`Session.LoadPlugin` asks only when a gate is wired. Headless hosts and tests have none, so a plugin
loads there unasked — the same rule every other permission path in this codebase follows, since a
prompt with nobody to answer it is a hang rather than a safeguard.

It is worth stating plainly because it qualifies the claim above. The load gate is the only boundary
Core CAN enforce, and in a host that wires no gate there is no boundary at all: the embedder has
taken permissions on themselves, for plugins exactly as for file writes and shell commands. A
library cannot do better than refuse to invent consent nobody is present to give.

### Identity is a content hash, not a filename

A grant names *this binary*, not this path. Replace the file with different code and the grant does
not carry over: the plugin re-asks.

This is the folder-trust birth-time argument applied to a file. A grant keyed on a name would let
anything that later occupies that name inherit consent.

**The hash covers everything loaded, not one file.** A managed plugin with dependency assemblies is
a directory, and hashing only its entry point leaves a swapped dependency changing the code without
changing the identity — the grant would carry over to something the user never approved. So the
identity is over the plugin's whole load set, ordered deterministically.

**Every plugin update therefore re-prompts.** That is correct — an update is exactly the moment a
user would want to be asked — and mildly annoying in practice. Trusting a publisher rather than a
binary would fix it and requires signing, which requires a key story, which is a subsystem. Out of
scope, deliberately.

### The plugin provides its own policy; Core enforces it

A plugin may declare that some of its operations need approval. It supplies the policy; Core does
the asking, through the same prompt machinery every other permission uses.

The direction matters. The plugin does not render its own consent UI and does not implement its own
prompt — it declares "this operation asks, and here is what to show". That gives three things:

- one consent vocabulary, so a user never learns a second one
- a plugin cannot quietly skip a gate it declared, because Core holds the rule
- persistence for free: an "always allow" lands in `permissions.json` under the same folder scope,
  and `/trust`, the rules list and the store's pruning all apply unchanged

A plugin that declares no gated operations gets none. LSP declares none — reading symbols is
reading, and the load gate covered it. A plugin that pushes to a remote, spends money, or writes
outside the workspace would declare operations that ask.

A plugin declaring nothing is not certified safe by that silence. The load gate is what stands
behind it.

**Two flags, because a plugin's tools are not uniformly dangerous.** `gated` decides whether a call
asks at all. `alwaysAskable` — default true — decides whether that prompt offers "Always", and the
stored rule names the plugin as well as the tool (`plugin csharp-lsp tool csharp_rename`) so it
cannot be inherited by a later plugin that happens to declare the same tool name.

The second flag exists because one boolean per plugin forces its safest and sharpest tools to share
an answer. A language server's `definition` is a read a user should be able to stop being asked
about; its `rename` rewrites files across a repository. Nothing Core can inspect — a name, a schema —
separates them, and the author is the only party who can say which is which.

Withholding "Always" is not a security boundary and is not treated as one: a plugin wanting standing
grants simply declares itself always-askable, and the user approved the binary at load regardless.
It is the author marking their own sharp edges. Refusing "Always" everywhere was tried and is worse
than it sounds — a trusted plugin that interrupts on every call is one users route around by turning
gating off wholesale, trading a scoped grant for none at all.

---

## Overriding is forbidden

A plugin may add. It may never replace: not a built-in tool, not a command, not an executor type.

**Where plugin tools sit in dispatch is part of this rule, not a detail below it.** The chain in
`Agent.InvokeAndShowAsync` resolves in order:

    spawn -> skills -> todos -> ask_user -> MCP -> injected tools -> ToolBindings (built-ins)

`ToolBindings.InvokeAsync` answers "no such tool" rather than returning null, so it TERMINATES the
chain — it is the built-ins' dispatcher and the last link, and anything placed after it is
unreachable code that looks correct.

**Plugin tools resolve immediately before that terminator**, in the injected tools' position — they
are the same kind of thing: contributed rather than built in, present or absent per session.

**Position does NOT protect the built-ins, and it must not be claimed to.** A link before the
terminator is consulted BEFORE the built-ins, so a plugin tool named `read_file` would win dispatch,
not lose it. Being behind the terminator makes a link unreachable, which is a different thing from
being protected by it.

What actually prevents shadowing is the duplicate-name refusal below: a plugin whose tool name is
already taken does not load at all. That is the whole of the protection, and no ordering substitutes
for it.

*(`AgentToolset`'s own doc comment claims it is "consulted only after every built-in has declined,
so a consumer cannot shadow `read_file`". That is not true of built-ins dispatched through
`ToolBindings`, which it precedes. The claim is wrong in the source and must not be inherited here.)*

**A second contributor needs new plumbing, not this slot.** `_agentTools` is a readonly field built
once at construction from an immutable set, and it resolves duplicates last-wins — which this
document forbids for plugins. Plugin tools therefore need a registry that can be mutated at a turn
boundary and that refuses collisions, sitting in the same chain position rather than inside the
existing set.

**A duplicate name refuses the load.** `AgentToolset` resolves collisions by last-registration-wins,
which is right for a front end contributing over Core and wrong for a plugin: silently winning a
name is exactly what this section forbids. A plugin whose tool name is already taken — by a
built-in, by an injected tool, or by another plugin — fails to load and says which name collided.
Refusing the whole plugin rather than dropping the one tool, because a plugin that half-loaded is a
plugin whose behaviour nobody can predict from its manifest.

**MCP is the one collision that cannot be refused at load, and it resolves the other way.** An MCP
server is free to advertise any name and connects after config is read, so a name may become
contended *after* a plugin loaded cleanly. MCP resolves earlier in the chain, so the MCP tool wins
and the plugin's is unreachable. That is not a decision this document is free to make differently —
it is what the chain does — so the obligation is to say it: a plugin tool shadowed by a late MCP
server is reported, not silently dead. A user who hits it has two working controls, `enabled` on the
server or `-name` in the selection, and neither is discoverable if nothing says what happened.

Last-registration-wins is right *within* a trust domain — Core seeds a command, the application
overrides it, and both are the same program. A plugin is not. A plugin that could re-register
`read_file` would silently intercept every file read in the session, with neither the model nor the
user seeing anything change, and the load gate's promise ("this plugin provides tools") would be a
lie.

**Two levels, and the first is a gate rather than a preference.**

`enabled` on a plugin is the OVERALL GATE. False and the plugin does not load: no process spawned,
no tools registered, no load prompt, nothing to select from. It is not a filter over a loaded
plugin's tools — it decides whether any of this happens at all.

Selection sits BELOW that gate and only ever narrows what a loaded plugin already contributed. A
tool that is not offered because `enabled` is false is not withheld; it does not exist this run.

The ordering matters because it decides what a `+` can reach. `+lsp_rename` at any depth cannot
re-include a tool from a disabled plugin, and that is not a special case in the grammar — it falls
out of `ToolSelection`'s own floor rule: *"S0 IS THE ONLY FLOOR, and it is enforced by construction
rather than by a check: Apply can only return elements of what it was handed."* A disabled plugin
puts nothing into what Apply is handed.

**Plugin tools are selectable, and use the grammar that already exists.** Its terms are already
sufficient for everything this needs: `inherited` to start from what this level would have, `all` to
start from everything it could have, `name` to include, `-name` to exclude, `+name` to re-include
past an earlier exclusion, and a list with no keyword as an exact set.

    "tools": ["inherited", "-write_file"]           a built-in removed
    "tools": ["inherited", "-lsp_rename"]           one plugin tool withheld
    "tools": ["read_file", "grep", "lsp_definition"] an exact set, mixed freely

A plugin tool is a tool. Nothing in the grammar needs to know where it came from, and a sub-agent
narrowing its own tools should be able to withhold `lsp_rename` exactly as it withholds
`write_file`.

**Naming a tool that has not loaded is harmless in the delta forms, and not in an exact set.**
`Apply` intersects the chosen names against what is offered, so `-lsp_rename` removes nothing when
the plugin is absent and starts working the moment it loads — the grammar's stated safety property:
*"a typo in a removal removes nothing. The dangerous direction is the one the delta form makes
safe."*

An exact set has no such protection. `["read_file", "lsp_definition"]` written against a plugin that
fails to load silently yields one tool instead of two, and nothing says so. That is not new — it is
the exact-set form's existing trade — but a plugin-aware config is exactly where someone would write
one, so it is worth knowing that `["inherited", "-write_file"]` degrades safely and an exact set
does not.

**MCP tools are excluded from selection and plugin tools are not, and the reason is control rather
than timing.** `ToolSelection` states the exclusion plainly: *"MCP TOOLS ARE NOT SELECTABLE and never
reach this type: `enabled` per server is their control. They are third-party code whose names are
composed at runtime, and a selection that never governs them cannot get their late arrival wrong."*

Late arrival is explicitly NOT the obstacle — the same type lists late-connecting MCP servers among
the reasons it holds terms rather than resolved names, and resolves against the offered set exactly
once, late. A plugin's tools arriving after config is read is therefore the case the type was built
for, not an argument against selecting them.

What differs is that a plugin has a second control and MCP does not. An MCP server is governed only
by `enabled`, so giving its tools a selection too would be a second lever on the same thing. A
plugin is governed by `enabled` at the gate AND by selection below it, and those answer different
questions: whether it runs at all, and which of what it contributed this agent is offered.

**So removal needs no new vocabulary.** Disabling a built-in is `-write_file`. Withholding a
plugin's tool from one agent is `-lsp_rename`. Turning the whole plugin off is `enabled: false`.
Three existing mechanisms, each governing what it already governs.

The security property is unaffected: selection decides what is OFFERED from what loaded, and a
plugin still cannot take a name that is already taken.

---

## Name collisions

A name can be claimed by a built-in, an injected tool, a plugin or an MCP server, and the layers
below differ in WHEN a clash becomes knowable — which is what decides the response. Refusing costs
nothing before anything is running and costs a user their question once a turn is in flight.

| # | collision | knowable at | response | |
| --- | --- | --- | --- | --- |
| 1 | injected x injected | session open | withdraw both, report | shipped |
| 2 | plugin x plugin | config read | refuse to start | needs plugins |
| 3 | plugin x injected | session open | withdraw the plugin's, report | needs plugins |
| 4 | plugin/injected x an ENABLED built-in | config read (plugin) / open (injected) | refuse / withdraw | injected half shipped |
| 5 | plugin/injected x a DISABLED built-in | config read | not a collision — the name is free | injected half shipped |
| 6 | as 5, but S2 re-enables the built-in | session open | built-in wins, withdraw + report | shipped |
| 7 | as 5, but S3 re-enables it for one request | dispatch | built-in wins, say so in the transcript | shipped |
| 8 | plugin/injected x an MCP tool | never — MCP connects late | MCP wins, report | injected half shipped |
| 9 | a plugin LOADED at runtime clashing with any of the above | at load | refuse the load | needs plugins |

**The injected-tool rows are already built.** They were reachable without plugins, and building them
first proved the matrix against real code rather than leaving it a design: `AgentToolset` withdraws
a duplicated name and reports it at session open, dispatch skips an injected tool a live built-in
outranks and says why per affected call, a disabled built-in frees its name, and an MCP server
winning a contested name is reported rather than leaving the injected tool silently unreachable.
`AgentToolset`'s `strict` flag is the opt-in refusal for an embedder who would rather not start
degraded.

Every plugin row follows the rule its injected twin already follows. What the plugin rows add is the
CONFIG layer — a clash between two declared things, catchable before anything runs — which has
nothing to validate until plugins are declared in config.

**Three moments, not two.**

*Config read* is where a statically certain clash lives — both sides declared in a file, nothing
running, nobody waiting. It refuses to start and names the file and the key, exactly as an unknown
provider kind does today.

*Session open, and plugin load* is where the tool set is first complete: the injected tools are in
hand, plugins have described themselves, and no turn is in flight. Most of the matrix resolves here.
The response is to withdraw the loser and report it rather than to refuse the session — the clash is
usually the embedder's wiring, withdrawing costs only their own tools, and refusing would deny a
user their whole session over something a sentence explains. `AgentToolset`'s strict mode is the
opt-in for a consumer who would rather fail loudly.

*Dispatch* is the last resort and governs only what a request can create. A per-request `+read_file`
re-enabling a built-in an injected tool had taken is a clash that existed nowhere until that
selection was written. The built-in wins — the model was promised that name — and the transcript
says why, because a silent skip is the surprise worth avoiding.

**Name your tools after what makes them yours.** The matrix says what happens when two plugins claim
one name; it does not make the clash pleasant. Row 2 refuses the second plugin's whole load, and the
user's only remedy is editing a plugin they did not write. A tool called `lsp_definition` claims a
name every language-server plugin has equal claim to, so installing a C# one and a Rust one is a
configuration that cannot work. `csharp_definition` and `rust_definition` both fit, and the model
picks between them by reading the names — which is also how it knows which one to call in a mixed
repository.

The rule generalises past LSP: a plugin's tool names should say what it does AND what it does it to.
Prefer the specific name even when yours is the only such plugin today, because the collision arrives
when someone installs the second one, and by then your name is in their config.

**A turn is never refused for a collision.** The temptation is real: no surprises, nothing ambiguous
runs. But the user's question is not the thing at fault, and refusing it costs them their prompt for
a wiring detail they often cannot fix. Worse, a clash from S2 would refuse EVERY turn — a session
alive but unable to answer anything, fixable only by editing config and restarting. Saying what
happened achieves the same honesty at none of that cost.

**Row 8 cannot be refused at any layer**, and it is worth seeing why. An MCP server connects after
config is read and is free to advertise any name, so a clash can appear mid-session with no earlier
moment to catch it. Refusing turns would let a connecting server kill a session. MCP resolves
earlier in the dispatch chain, so it wins; the obligation is to report that the plugin's tool is
unreachable, not to prevent it.

---

## Failure

**A managed plugin that throws** fails that tool call. The plugin stays loaded: one bad call is not
grounds to tear down a language server that is otherwise working. Repeated failure may be, but that
is policy, not contract.

**A native plugin that faults** cannot be caught. There is no `try` that reaches a segmentation
fault, so isolation has to be architectural: **ABI plugins run out of process, one host each.**

One host per plugin rather than one for all of them, so a faulting `lsp-rust` does not take
`lsp-python` with it. That is the same isolation principle the per-session decision rests on.

**These multiply, and the number is the product.** Per-session instances times one host each means
three sessions with two native plugins is six host processes, each with its own language server
beneath it. That is the cost of the two isolation decisions taken together, and it is accepted for
the same reason each was: a shared host reintroduces exactly the coupling the split exists to
remove. It is stated here so nobody meets it as a surprise.

When a host dies, Core sees a closed pipe, the tool call fails, and the user is told which plugin
died — the session survives.

Managed plugins load in-process because a .NET exception is catchable, and making them pay for a
problem they do not have would be a cost with no return.

**This asymmetry is the design, not an accident**, and it is the first thing a reader will ask
about.

### Why this is affordable

Because the boundary is JSON, out-of-process is a transport change rather than a contract change.
The marshalling is already serialisation; moving it across a pipe does not alter what is serialised.

And the machinery is one an LSP plugin needs anyway — a language server is already a child process
speaking a protocol over a pipe. The plugin host and the language-server client are the same shape,
so the second one is nearly free once the first exists.

---

## The boundary is JSON

Both loaders, both directions, one contract.

`ToolDefinition` already carries `JsonElement InputSchema`, so a tool's description is JSON before
it reaches a plugin at all. A struct-based ABI would mean marshalling C structs into managed objects
into JSON — two translations to arrive where the data started.

JSON also solves versioning, which is the stronger reason. A struct ABI is frozen: add a field and
every compiled plugin breaks. With JSON an old plugin omits a new field and Core defaults it, and a
new plugin sends a field an old Core ignores. That is the difference between a contract that can
evolve and one that cannot.

---

## Hook points

Core exposes a registry per extensible kind. A plugin's description may name any of them.

    tools         v1  — a tool and its executor, bundled
    permission    v1  — a plugin's own declared gates, on its own operations
    commands          — CommandRegistry already exists and is Core's
    completions       — ValueSources is already a named-source lookup
    providers         — an LLM provider as a plugin
    observers         — notification only, no veto

**v1 honours `tools` and `permission`; the rest are refused by name.**

`permission` is in v1 because the Permission section above specifies plugin-declared gates as
operative, and a registry refused by name would contradict it. Note the scope: a plugin declares
gates on ITS OWN operations. It does not contribute a policy that governs anything else — that
would be a plugin deciding whether `write_file` may run, which is the overriding this document
forbids in a more dangerous form. A plugin declaring `commands` against a build
that does not service them is told so; it is not silently ignored.

That is the rule this codebase settled for commands and it transfers directly — a thing exists where
a handler is registered, and a declared-but-unserviced name is a lie told to whoever reads the
manifest. `CommandRegistry` holds the same property in substance: a command declared in the table
and serviced by nobody is offered in help and the palette, and answers "not available in this
application" when a user reaches for it.

### Two kinds of hook, not to be conflated

**Notifications** are one-way: "this happened." Many listeners, no veto, nobody's answer matters.
Core's existing events — `ToolCallFinished`, `TurnCompleted`, `ChildSpawned` — are these, and they
exist for a front end to render.

**Providers** are contended: "answer this." The answer changes behaviour, so ordering and precedence
are real questions. Every registry above except `observers` is this kind, which is why *Overriding
is forbidden* is a section and not a footnote.

---

## The system prompt

A plugin may contribute a block of instructions, and this is a hook like any other — declared in the
manifest, refusable, and governed by the same rules as everything else it contributes.

**Why per-tool descriptions are not enough**, and Core already answers this for the identical case.
MCP servers contribute an instructions block for a stated reason: the text *"describes how to use
that server, which its individual tool descriptions cannot."*

A plugin is the same shape. `lsp_definition`, `lsp_references` and `lsp_rename` share a workspace, an
index that must be warm, and a position encoding — facts about the plugin rather than about any one
tool, and repeating them in three descriptions is both wasteful and a place for them to drift.

**It is attributed, never anonymous.** The block is rendered under the plugin's name, exactly as MCP
servers are:

    # Plugins

    From the 'lsp-rust' plugin:
    <the plugin's text>

Anonymous text merged into the system prompt would let a plugin issue instructions that read as the
application's own. Attribution is not decoration: it is the difference between guidance the model
can weigh and an instruction it cannot tell from Core's.

**It is bounded, and it is not a channel.** A plugin's text is capped and the cap is enforced rather
than trusted, because prompt space is a shared and costly resource — every turn re-sends it. A
plugin whose block is unbounded taxes every request in the session.

**It follows the tools it describes.** A plugin whose tools are all withheld by selection contributes
no instructions: text describing tools the model cannot call is a description of capability it does
not have.

Core already builds prompt text this way at runtime. The `ask_user` guidance is emitted only when
selection allows that tool, spawn guidance only when the agent may spawn, and `ToolBindings` drops a
tool's cross-references to tools that are not offered. The rule here is that behaviour applied to a
plugin's block.

**It is a departure from how MCP instructions work, deliberately.** An MCP server's block is
appended unconditionally, because MCP tools are not selectable and so cannot be absent from a server
that connected. A plugin's tools CAN be absent while the plugin is loaded, so its block has to
follow them or it describes a capability that was withheld.

**Loading or unwiring costs the prefix cache, and that is the user's call to make.**

Two surfaces move, not one. The `tools` array a request carries is rebuilt from the live set every
turn, so it changes the moment a plugin's tools appear or leave. The instructions block above
changes with them. Both sit ahead of the conversation, so either invalidates the provider's prefix
cache from that point in the session.

The cost is real — cache hit rate is the dominant performance fact of a long tool loop, where every
turn re-sends the whole conversation — but it is not a reason to prevent, delay or batch the change.
A user loading a language server mid-session has decided the capability is worth more than the
cache, and that is exactly the kind of decision this design leaves to them: the same principle as
the load gate, `enabled` and unwire. Core's job is to make the cost visible if asked, not to
second-guess a deliberate action.

Nothing here should therefore try to be clever about *when* a load takes effect. The turn boundary
is the rule, and the rule is about correctness — the model must not be offered a list that changes
underneath it — not about the cache.

---

## Configuration

`plugins` in `config.json`, keyed by name, with `AgentConfig` support — the same shape `mcp` already
uses, so nobody learns a second vocabulary:

```json
{
  "pluginPaths": ["~/.config/cxagent/plugins", ".cxagent/plugins"],
  "plugins": {
    "lsp-rust": {
      "file": "csharp-lsp-rust.so",
      "enabled": true,
      "settings": { "server": "rust-analyzer", "args": ["--log-level", "warn"] }
    }
  }
}
```

**The search paths are a sibling of `plugins`, not a key inside it.** A settings key living among
name-keyed entries collides with a plugin of that name — `mcp`, the cited precedent, is uniformly
name-to-object and this follows it.

**The key is a name; the value points at a filename, not a path.** The file is resolved against the
search folders, so a config is portable between machines. A path would not be.

**Two locations, project wins.** User-global and project-local, resolved the way project
instructions already walk up. A project overrides a globally installed plugin rather than colliding
with it.

**Discovery is the application's.** Core accepts "here is a plugin at this path" and does not care
how it was found. Enumerating folders, presenting a picker and deciding what to load is orchestration
— Core is the infrastructure underneath it. This is the same split as commands: Core owns the
table and the dispatch; the front end contributes what only it can service.

---

## What a plugin is handed at Load

- **the working directory** — LSP roots its server here
- **its own settings** — the `settings` object from config, verbatim
- **a logger** — a plugin that cannot say why it failed is undebuggable
- **a cancellation token** — one whose lifetime is the PLUGIN INSTANCE's, cancelled at Stop

  Not "the session's", because there is no such token: a session holds a per-turn scope, replaced
  each lap. Handing a plugin a turn's token would cancel a language server mid-index because a user
  pressed Escape on an unrelated question. The plugin's token is created at Load and cancelled at
  Stop, so long-lived work survives turns and still dies with the session. A per-CALL token is
  handed to the executor separately, and that one IS the turn's.

And not:

- the transcript, in any form
- the model, or the ability to start a turn
- the permission store — a plugin declares policy, it does not read or write grants

---

## What this leaves room for

Nothing below is v1. It is recorded because the shape above was chosen partly to keep it reachable,
and a later reader should know which seams are load-bearing for reasons that have not arrived yet.

**Automatic proposal.** An application that can tell a folder is a Rust project, or a .NET one,
could match that against the plugins it knows and enable one — or, better, propose it. That work
lands entirely on the application side: sensing, matching and proposing are discovery and selection,
which is why they were put there. Core keeps accepting "here is a plugin at this path" and never
learns what a language is.

Two rules above are what make this safe rather than merely convenient:

- **Enabling is not approving.** Config declares what MAY load; the load gate still asks, per
  binary hash. An application may therefore propose as freely as it likes — the worst outcome is a
  prompt the user declines.
- **A proposal arriving mid-turn is refused, not queued.** Detection that fires while the model is
  working cannot surprise anyone, because the same boundary that governs a typed command governs it.

**Kinds beyond tools.** `commands`, `completions`, `providers` and `observers` are refused by name
in v1 rather than absent from the format, so a plugin declaring one gets an answer instead of
silence — and adding a kind later is honouring a declaration the manifest could already carry.

**A second customer for reaping.** The pid record exists for plugin children, but MCP servers have
the identical crash-leak today and no collection at all. A record general enough for one would
collect the other.

---

## The v1 cut

**Both loaders ship in v1.** Managed in-process and ABI out-of-process, against one contract.

Not because LSP needs both — a managed plugin would carry it — but because the second loader is what
proves the contract is a contract rather than an interface with a serialisation step bolted on. A
design that has only ever been exercised by C# will have absorbed C# assumptions nobody noticed:
exceptions crossing the boundary, a type surviving a round trip because both ends shared its
definition, a nullable that was never actually serialised. Those are discovered by writing the
second loader, and they are cheap to fix while the first plugin is the only one that exists.

Deferring the ABI would mean the contract is settled by one loader and then bent to fit the other,
which is how a single plugin system becomes two.

So v1 is:

- the plugin contract, and a describe format that can carry kinds that do not exist yet
- the managed loader, in-process
- the ABI loader, out-of-process, one host per plugin
- lifecycle, with Stop on every exit path and a timeout
- the load gate: origin, declared capability, content-hash identity
- config: `plugins`, search folders, load by filename, project over global
- `tools` and `permission` honoured; every other registry declared-but-refused, by name
- runtime load AND unwire, both at turn boundaries — not load-only

  Unwire earns its place in v1 rather than after it because a registry that only grows is not the
  mutable registry this design is for: a user who loads the wrong language server, or one that is
  misbehaving, should not have to end the session to be rid of it. It also forces the two pieces of
  machinery that are easiest to omit and worst to retrofit — reaping a host at unwire rather than
  only at startup, and waiting out a call that outlived its turn. Both are cheap while the first
  plugin is the only one, and both are the kind of thing a later addition discovers through a leaked
  process nobody can account for.
- built-in removal through config, since a plugin can never shadow one
- one LSP plugin, as the thing that proves the rest

**The LSP plugin is the acceptance test.** A contract with no consumer is a guess; the first plugin
is what turns it into a fact, which is why it belongs in v1 rather than after it.

**The LSP plugin is written twice: managed first, then as ABI.** In that order, and the order is the
point.

Managed first isolates the failure surface. A language server has plenty of its own difficulty —
process lifetime, protocol framing, initialisation handshake, position encodings — and meeting all
of that through an untried IPC boundary means every failure has two candidate causes. Managed
first, the plugin either works or it does not, and the answer is about LSP.

Then the same plugin, already known to work, is rewritten against the ABI. Any behaviour that
changes is attributable to the boundary, because nothing else did. That is a controlled experiment
rather than two unknowns at once, and it is the cheapest way to find the C# assumptions the contract
absorbed without anyone noticing.

It also means the ABI loader is proved by a plugin whose correct output is already known, which is a
much better test than a plugin being written for the first time.
