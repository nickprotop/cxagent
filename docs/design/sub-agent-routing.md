# Sub-agent resolution and routing

How a `task` call becomes a child agent running on a particular endpoint, and which decisions are
made where. Written down because the instance/model distinction has now been rediscovered in four
separate places, each time as a bug.

## The one rule

**A type routes to an INSTANCE, never to a model.**

A `providers` entry is a name bound to *one endpoint and one model*. Two entries can serve the same
model with different endpoints, context windows and compression thresholds:

```json
"providers": {
  "local": { "baseUrl": "http://localhost:8771/v1", "model": "qwen3", "contextWindow": 213000 },
  "small": { "baseUrl": "http://localhost:8771/v1", "model": "qwen3", "contextWindow": 32000 }
}
```

`local` and `small` are the same server and the same model. They are still a real choice, because
the window differs — and the window is the thing that decides when a child compresses. This is why
"which model does this type use" is the wrong question: the answer is identical for both, and
useless.

Corollary, and the reason the rule is load-bearing rather than pedantic:

- **Spend and history key by instance** (`instance:model`). Attributing a child's tokens to a bare
  model merges two entries whose cost profile may differ entirely.
- **Model behaviour keys by model.** A future model-specific system prompt variant branches on
  family quirks, which belong to the model. `SystemPromptContext.ModelId` is bare on purpose.

## The path, end to end

### 1. Config states the intent

```json
"agents": {
  "cheap":   { "briefing": "Answers quick questions.", "provider": "small", "maxTurns": 20 },
  "builder": { "provider": "big" }
}
```

Two shapes, and the difference matters. `cheap` is a name cxagent does not ship, so config authors all
of it. `builder` is one of the five **built-in** types (`BuiltinAgentTypes.cs`): its briefing and
description come from the program, and config may only say where it runs and what it may spend. A
`briefing` under a built-in name is ignored, with a warning — see *Failure modes* below.

`ProviderConfig` validates `provider` against the configured instance names. A miss is a **warning,
not an error** (`ProviderConfig.cs`): the type survives and runs on the parent's instance. Rationale
— a typo in one type's routing should not prevent the app starting, and the user is told.

`AgentTypeConfig(Briefing, Provider, MaxTurns)` is *what the file said*, with `Provider` still a
string.

### 2. The catalog resolves it, once

`AgentTypeCatalog` turns each config entry into an `AgentType`, and this is the only place the
instance name is still in hand:

```csharp
if (cfg.Provider is { } instance && providers is not null
    && providers.TryGet(instance, out var resolved))
{
    provider = resolved;
    instanceName = instance;
    providers.InstanceWindows.TryGetValue(instance, out window);
}
```

Three things are resolved **together or not at all**: the provider, its window, and its name. That
grouping is the invariant — see *Failure modes* below.

A second miss here (registry and config disagree) degrades to the parent's rather than throwing,
because throwing would happen at spawn time, minutes into a session.

**There is always a `general`**, seeded before config is read so a user's own `general` overrides it
rather than being rejected. It has no provider, so it inherits the parent's — which is what makes a
bare spawn ordinary rather than a special case.

**The catalog is rebuilt on every re-wire.** `WireRunner` constructs it per F5/F7 provider change, so
a type re-resolves its instance against the *new* registry. Otherwise a type would keep pointing at a
provider the session no longer uses.

### 3. The model names a type

`SubAgentSpawner.Handle` reads `type` from the tool call:

```csharp
var type = _types.Resolve(requested);
if (type is null)
    return $"error: unknown agent type '{requested?.Trim()}'. Valid: {_types.Names}.";
```

- Blank or absent → `general`.
- Unknown → **refused**, not defaulted. The model will invent `researcher`. Substituting `general`
  would mean the user's briefing did not apply and nobody was told.

The tool description lists the catalog, one line per type, with `[runs on <instance>]` appended only
when a type routes elsewhere — a type is usually routed for a reason (bigger window, stronger model,
cheaper one) and that is a fact the parent should choose by.

### 4. The factory derives a runtime

```csharp
var runtime = type?.Provider is not null
    ? _runtime.With(type.Provider, type.ContextWindow, type.InstanceName)
    : _runtime;
```

`With` returns a **new record**, never a mutation — two children spawned in one turn must not be able
to see each other's provider. It moves four things as a unit:

| Field | Why it travels with the provider |
|---|---|
| `Provider` | the endpoint itself |
| `ContextWindow` | belongs to the instance, not the session |
| `CompressAbove` | derived from the window via `ThresholdFor` |
| `InstanceName` | spend attribution; a child recorded under the parent's name is spend charged to an endpoint it never called |

`MaxTurns` is the exception — `type?.MaxTurns ?? _runtime.MaxTurns`, resolved separately because null
inherits and 0 means unbounded, a rule `Agent` already implements.

### 5. What the child does and does not get

Passed: its own buffered sink and job panel, a **fresh `AgentContext(window)`**, a log directory
nested under the parent's, the type's briefing (which beats the caller's), `isSubAgent: true`.

Deliberately **not** passed:

- **No session store.** `Agent` has no such parameter. Only `AgentHost` persists.
- **No spawner.** A child built without one *structurally cannot nest*. "No sub-agents of
  sub-agents" is not a rule the child is asked to follow — it is a tool it was never given.

Shared with the parent: the **`TokenLedger`** (deliberately — one session, one bill, split by
`SubAgentTokens`) and the job registry.

## Failure modes this design forecloses

**Per-field fallback.** Pairing type A's provider with the session's window is the canonical bug: a
child on a 32k endpoint carrying a 213k window sees `IsUnderPressure` as permanently false, never
compacts, and dies on an overflow. Hence "together or not at all".

**Mutating the shared runtime.** Would let two children spawned in one turn see each other's
provider. Hence a new record per child.

**Attributing by model.** Two instances of one model collapse into a single row, and a routing
decision reads as no routing at all. This bit `SubAgent.ModelId` (fixed), the catalog line (fixed),
and 36 rows of the history database (backfilled).

**Silently defaulting an unknown type.** The user's briefing does not apply and nobody is told.

**A briefing that drifts from the code depending on it.** The planner is told to write the file whose
path the spawner supplies; the spawner reports whether it appeared; the builder is told to refuse work
that arrives without one. While the text lived in `config.json` those three could disagree silently,
and every fix reached only whoever re-copied the sample — measured: two briefing corrections made in
one session shipped to exactly one machine. The five now live in `BuiltinAgentTypes.cs`, versioned and
tested with the code that relies on them, and a `briefing` under a built-in name is **ignored with a
warning** rather than honoured or dropped in silence. Config keeps what is genuinely per-user:
`provider` and `maxTurns`.

**Trusting a child's word for what it wrote.** The planner's `PLAN WRITTEN: <path>` line was a claim,
and twice on live drives a planner that never called `write_file` ended with it anyway — once after
announcing it would "proceed by making some assumptions". Both times the parent believed it, could not
find the file, and wrote its own plan from the chat text for a builder to follow. The spawner now
**names the path itself**, puts it in the child's context, and appends what is actually on disk to the
result: `plan file: <path>`, or a refusal telling the parent there is no plan and not to write one from
the text above. The path is derived per spawn from the call's own label, so two planners in a session
cannot overwrite each other.

The marker itself is **retired**. It survived one round as a belt-and-braces second signal and earned
its removal immediately: the planner briefing still told the child to invent `./plans/<short-name>.md`
while the spawner checked the path it had supplied, so a planner that followed its briefing correctly
produced a plan the check then declared missing — a false negative on a working run, which is worse
than the false positive it replaced. One authority, and it is the one the parent can verify.

Which types write a plan is now a **declared property** (`AgentTypeDefinition.WritesAPlanFile`) rather
than `Briefing.Contains("PLAN WRITTEN:")`. Sniffing the prose coupled the mechanism to a sentence: the
builder's briefing mentioned the marker in order to say what it refuses, so it sat one careless edit
away from being handed a plan path it never writes.

## Where a path resolves

A sibling of the same rule, and the reason it is here: **the agent's working directory is data, not
the process's.**

`IJobContext.WorkingDirectory` carries it to every tool call. `FileJobExecutor` resolves `path` and
`dest` against it once, before any action runs; `ShellJobExecutor` uses it when the model omits
`working_dir`; `PermissionPolicy.RequestsFor` takes it as `root` so the gate resolves the **same
string against the same base** the executor will.

That last one is the safety property. The gate and the executor resolving differently is not a
near-miss — it is a check that passes on one file while another is written:

```
model: write "src/foo.cs"
gate:     /home/nick/session-b/src/foo.cs   → inside root, allowed
executor: /home/nick/session-a/src/foo.cs   → written
```

Every layer behaves correctly and the edit lands in a checkout the user never approved. Null root
means the process's directory, which is what every caller did before this existed.

## Where each fact lives

| Question | Answer from |
|---|---|
| Which types exist? | `AgentTypeCatalog.All` — rebuilt per re-wire |
| Which instance does a type use? | `AgentType.Provider` + `.InstanceName`, resolved once |
| What window does the child get? | `AgentType.ContextWindow`, travelling with the provider |
| Who paid for the child? | `TokenLedger`, keyed `instance:model` |
| What ran, historically? | `runs` table, `model_id` = `instance:model` |
| Which prompt variant? | `SystemPromptContext.ModelId` — bare model, reserved seam |
