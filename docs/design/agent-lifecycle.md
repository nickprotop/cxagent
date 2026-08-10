# Agent Lifecycle

**Why this exists:** sub-agents are built on top of `Agent`, and every earlier attempt in this
codebase failed by building on an assumed shape rather than the real one — which is what led to
deleting large amounts of code. This is the traced-from-source account of what an agent owns, what
flows through it, and what it returns. Every claim here was read out of the code, not recalled.

Verified against `Core/Agent/Agent.cs` and `Core/Agent/AgentHost.cs`.

---

## 1. What an agent IS

One class, `Core/Agent/Agent.cs`. Constructed as many times as there are agents. It imports only
`Core.*` and `Helpers` — **nothing from `UI`**, proven by the compiler after the kernel move.

```csharp
public Agent(ILlmProvider provider, PluginRegistry plugins, TokenLedger ledger,
    IChatSink sink, IJobPanel jobs, LogFileManager? logs, int maxTurns,
    int? compressAbove = null, AgentContext? context = null,
    string? globalInstructionsDir = null, McpToolset? mcp = null, string? briefing = null)
```

### What it OWNS (per agent, never shared)

| Thing | Where | Note |
|---|---|---|
| **Id** | `Id { get; } = UlidGenerator.NewId()` | Minted at construction, stable for life. Keys the log directory and job rows. |
| **Context** | `_context = context ?? new AgentContext()` | **Omitting it gives a fresh one.** This is the self-containment guarantee. |
| **Briefing** | `_briefing` | Constructor-only. What this agent was created to do. |
| **Turn counter** | `_turn` | Monotonic across the agent's life, numbers the log files. |
| **Session verdicts** | `_lastBuild`, `_lastTest` | Outlive the prompt that produced them. |

### What it is HANDED (may be shared, and today is)

| Thing | Shared? | Note |
|---|---|---|
| `ILlmProvider` | yes | Stateless w.r.t. conversation. |
| `PluginRegistry` | yes | Tool implementations; the permission gate is inside it. |
| `TokenLedger` | **yes — one per session** | Constructed in `AgentHost`, never in `Agent`. Thread-safe (`Interlocked`). Per-agent ledgers are deliberately deferred. |
| `IChatSink` / `IJobPanel` | **per agent in principle** | Constructor parameters. A sub-agent is created by passing different implementations. This is the whole UI seam. |
| `McpToolset` | yes | One fleet per session. |

---

## 2. The context is ONE list, not two parts

This is the most misunderstood part, so it is stated exactly.

`AgentContext` holds **one** `List<ChatMessage>`. `SendAsync` takes the LIVE list and mutates it in
place — it does not assemble a request from "system prompt + conversation":

```csharp
var messages = _context.Messages;          // the live list, NOT a copy
messages.Add(new ChatMessage { Role = "user", Content = prompt, ... });
```

The system prompt is **reconciled at index 0**, not prepended per turn:

```csharp
var existing = messages.FirstOrDefault(m => m.Role == "system");
if (existing is null)  messages.Insert(0, ...);            // first turn only
else if (text differs) messages[index] = existing with ...; // replaced IN PLACE
```

**Why it matters:** because index 0 is replaced only when its text actually differs, an unchanged
environment produces a **byte-identical prefix** for the whole session. That helps only on endpoints
with IMPLICIT prefix caching — the Anthropic wire emits no `cache_control` breakpoints, so nothing is
cached there however stable the prefix is. The stability is worth having either way, and is pinned by
a test asserting seven turns produce the same system message. Assembling the
request from parts each turn would destroy it.

`PinnedHeadCount` reads the same position, so compaction summarises from index 1 and can never
summarise the system prompt away.

### What the system prompt is built from

Everything is either fixed for the agent's life or read from something the USER controls:

| Input | Varies? |
|---|---|
| Platform | fixed |
| **Working directory, git-ness** | **re-read EVERY prompt from a PROCESS-GLOBAL** (`Directory.GetCurrentDirectory()`). Not per-agent: `Agent` has no cwd parameter, so concurrent agents share one. |
| `Today` | **frozen** (`_startedOn`), NOT `DateTime.Now` per turn |
| `ModelId` | `_ = ctx.ModelId;` — deliberately discarded |
| Briefing | fixed at construction |
| Project instructions (`CXAGENT.md` and friends) | fresh per prompt — user-controlled |
| MCP server instructions | fresh per prompt — user-controlled |

**So the cache prefix changes when the user edits an instruction file or an MCP server connects, and
at no other time.**

Order of assembly: general prompt → project instructions → briefing. The briefing is LAST because
what an agent was created to do is the most specific instruction there is.

---

## 3. What flows through a turn

```
SendAsync(prompt, ct)
  │
  ├─ messages.Add(user prompt)                    → the agent's own context
  ├─ reconcile system message at index 0
  ├─ build tools = built-ins + MCP definitions    → rebuilt EVERY prompt
  │
  └─ for (turn = 0; ; turn++)
       ├─ MaybeCompressAsync                       → compresses ITS OWN context
       ├─ StreamTurnAsync ─┬─ _sink.BeginAssistantTurn()
       │                   ├─ _sink.AppendAssistant(id, bodyDelta)      ← STREAMED
       │                   ├─ _sink.AppendReasoning(id, thinkingDelta)  ← STREAMED
       │                   └─ _sink.EndAssistantTurn(id)
       ├─ _ledger.Record(usage)                    → the SHARED session ledger
       ├─ _context.RecordUsage(inputTokens, chars) → its OWN occupancy
       ├─ TurnCompleted?.Invoke(toolCallCount)
       │
       ├─ no tool calls → return text              ← EXIT
       └─ tool calls    → InvokeAndShowAsync each
                           ├─ Job { AgentId, DisplayName, State } → _jobs.SetJobs
                           ├─ MCP first, then built-ins
                           └─ messages.Add(Role="tool", ToolCallId=…)  → its own context
```

### Reasoning IS streamed

Both body and reasoning append incrementally in the same loop, with separate high-water marks
(`shown`, `shownReasoning`) because a reasoning block spans many deltas and the two interleave.

The agent emits **kinds**, never markup: `AppendAssistant` for body, `AppendReasoning` for thinking.
Colour and escaping are the sink's. Reasoning goes into the BODY rather than a header, deliberately —
a self-overwriting header discards thinking as fast as it arrives.

**Cost, stated:** body content clears the transcript's thinking spinner. Accepted, because streamed
reasoning is better evidence the model is alive than a spinner — it says WHAT it is doing.

---

## 4. What `SendAsync` RETURNS

`Task<string>` — the final assistant text, reasoning stripped. **Three exit points, all returning
the answer:**

1. **Normal completion** — no tool calls this turn. `return text`.
2. **Turn cap** — `_maxTurns` reached; a salvaged summary is produced and returned. "The salvaged
   summary IS the answer on this path."
3. **Stuck detection** — the same tool called with the same arguments repeatedly; `return text`.

### THE RETURN IS NOT HOW TEXT REACHES THE SCREEN

This is the single most important fact for sub-agents.

The answer is **already on screen** before `SendAsync` returns — it streamed through `_sink` during
the turn. The return value is a *separate copy* for the caller.

**Proof:** `AgentHost.SendAsync` currently DISCARDS the return value entirely, and the transcript
still renders correctly (verified on a live drive). The two channels are independent.

```
_sink   → what the USER sees      (streamed, live, per token)
return  → what the CALLER gets    (one string, at the end)
```

For a sub-agent these go to two different places: the child's sink renders into the child's own
(buffered) transcript, and the returned string becomes the parent's tool result.

---

## 5. `AgentHost` — what it adds

`AgentHost` is the UI's side of ONE agent. It owns for the session:

- the `TokenLedger` (constructed here, shared)
- the `AgentContext` (constructed here, handed to the agent)
- the resume store, the token budget, the context window
- **one `Agent`, built once** — not per prompt. The agent owns its id, so a per-prompt agent
  fragmented the logs and restarted turn numbering.

It republishes agent state as events the UI binds to: `TokensUpdated`, `ContextUsedUpdated`,
`ContextCompressed`, `ContextEstimatedUpdated`, `TurnCompleted`. **All four subscriptions in
`AppBootstrap` already marshal through `EnqueueOnUIThread`**, which is the pattern sub-agents on the
thread pool will use.

### There is no third list

`AgentHost.SendAsync(prompt, ct)` used to take a `List<ChatMessage> conversation`, append the prompt
and the answer to it, and **nothing ever read it**. It was a leftover from before the agent owned its
context. Deleted.

Two E2E tests asserted on that list — meaning they passed whether or not the model was ever reached.
They now assert on the agent's context.

**The lists that exist, and only these:**

| List | Who reads it | Cleared by |
|---|---|---|
| `AgentContext.Messages` | **the model**, every turn | `/clear` → `Context.Clear()` |
| `mainWindow.Chat` (the control) | **the user** | its own rendering |

`/clear` clears the context. That is the whole operation.

---

## 6. Cancellation

Per-turn `CancellationTokenSource`, linked to the session's. Escape cancels the running turn; the
session, its context and its MCP servers survive.

The token reaches the provider stream, the tool loop, and `ProcessRunner`, which kills the **entire
process tree** on cancellation. A child that has detached or is designed to outlive its parent (a
.NET `testhost`, for instance) is not in that tree — a documented limit of `entireProcessTree`, not a
defect.

A sub-agent inherits a token the same way, so stopping one is already expressible.

---

## 7. What this means for sub-agents

**Already true, needing nothing:**

- own context, own system prompt, own briefing, own id, own compaction — pinned by tests
- own sinks — constructor parameters; a sub-agent is different IMPLEMENTATIONS, not a different path
- the return value — one string, independent of display
- the parent channel — a `tool` message, exactly like `read_file` output. **`ToolCallId` must be set:
  it is the only field marking a message as a tool result, and a null silently turns it into an
  ordinary user turn**
- the row — `Job` already carries `AgentId`, so a spawn renders a row for free and the id is what a
  later expand-the-row swap keys on
- cancellation

**Still missing:**

- a factory, and a spawn tool. `WorkerToolset.Specs` is a static table keyed on the `WorkerTool`
  enum, so dispatch has no access to a factory — that is the one genuinely new seam
- requester identity on `PermissionRequest` — `(Kind, Display, AlwaysRule)`, no agent id
- a waiting-on-permission `JobState` and a gate→UI event
- live elapsed time on a running row

**Deliberately deferred:** per-agent ledgers and cost attribution.
