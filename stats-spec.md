# `/stats` — the local usage view

Every figure below already exists in memory at the moment a turn ends. Almost none of it survives the
session. This is a plan to keep it and read it back.

---

## What is already stored

`agent_sessions`, written by `SaveTurn` on **every turn** — not at exit, because a crash is exactly
when exit does not happen:

```sql
agent_id TEXT PRIMARY KEY,
context_json TEXT NOT NULL,
input_tokens INTEGER NOT NULL DEFAULT 0,
output_tokens INTEGER NOT NULL DEFAULT 0,
finished INTEGER NOT NULL DEFAULT 0,
updated_at TEXT NOT NULL,
working_dir TEXT
```

So daily totals, per-project totals, session counts and the ↑/↓ split are **already derivable from
disk**. That is the whole of item 1 and it needs no schema change.

What is missing is everything that makes a fan-out session legible — which is precisely what tonight's
runs showed is worth seeing.

---

## The constraint that shapes this

**A CHILD NEVER REACHES THE STORE.** `SubAgentFactory` builds children directly as `Agent`, never
through `AgentHost`, and the factory's own doc says why:

> *"`AgentHost` … owns the session store, so a child built through it would write a row under its own
> id that `OfferResumeAsync` then offers at next launch as a crashed session the user never ran."*

That rule stays. A per-child row in `agent_sessions` would be a resume candidate, and the fix for that
would be a `kind` column consulted by every read — a discriminator on a table whose entire purpose is
"what can I resume".

**So child statistics go in their own table**, written by the parent, which is the only party that
sees a child begin and end. This is not a workaround: the parent genuinely is the owner of the fact
"I spawned a planner and it cost 41k".

---

## Schema

### `agent_sessions` — three columns added

| Column | Why it cannot be derived |
|---|---|
| `sub_agent_tokens INTEGER DEFAULT 0` | `Ledger.SubAgentTokens`. Without it, a session's parent/worker split is unrecoverable — and tonight that was **86% to workers**, the single most interesting number of the evening. |
| `model_id TEXT` | Which model spent it. `ByModel` exists live and is dropped at exit; a history that cannot say which model ran is a history of nothing actionable. |
| `mode TEXT` | `single` / `fan-out`. Decides whether "0 spawns" means *chose not to* or *could not*. |

Added via the existing `AddColumnIfMissing`, which is already there for exactly this — `CREATE TABLE
IF NOT EXISTS` does nothing to a table that exists, so an in-place upgrade silently keeps the old
shape and every INSERT naming a new column fails.

### `agent_runs` — new, one row per spawned child

```sql
CREATE TABLE IF NOT EXISTS agent_runs (
    run_id          TEXT PRIMARY KEY,   -- the child's Agent.Id
    parent_agent_id TEXT NOT NULL,      -- FK in spirit; joins to agent_sessions
    type_name       TEXT NOT NULL,      -- explore | planner | review | general …
    model_id        TEXT,               -- a type may name its own provider
    input_tokens    INTEGER NOT NULL DEFAULT 0,
    output_tokens   INTEGER NOT NULL DEFAULT 0,
    turns           INTEGER NOT NULL DEFAULT 0,
    tool_calls      INTEGER NOT NULL DEFAULT 0,
    outcome         TEXT NOT NULL,      -- completed | failed | cancelled | capped
    started_at      TEXT NOT NULL,
    duration_ms     INTEGER NOT NULL,
    working_dir     TEXT);

CREATE INDEX IF NOT EXISTS idx_runs_by_parent ON agent_runs(parent_agent_id);
CREATE INDEX IF NOT EXISTS idx_runs_by_type   ON agent_runs(type_name, started_at);
```

**Written once, when the child finishes** — not per turn. A child is short-lived and its row is a
summary, not a resumable state. That also means no orphan rows: a child that never completes never
writes, and the parent's own `sub_agent_tokens` still accounts for what it burned.

**Every field is in scope at the write site.** `Agent.cs` already holds all of it where the finished
row is built:

| Field | Source | Status |
|---|---|---|
| `run_id` | `childId` | exists |
| `type_name`, `model_id` | `SubAgent.TypeName` / `.ModelId` | **added tonight** |
| `input_tokens`, `output_tokens` | `spawned.Agent.Spend` | **added tonight** |
| `turns` | `childTurns` | **added tonight** |
| `duration_ms` | `DateTimeOffset.UtcNow - started` | exists |
| `outcome` | `failed` + `SendResult.Outcome` | exists |
| `tool_calls` | `child.Jobs.Jobs.Count` | exists |

Only `tool_calls` needs no new tracking at all — the child's `BufferedJobPanel` already holds every
row it drew, which is what keeps them out of the parent's transcript.

---

### `tool_calls` — new, one row per tool invocation

The largest untapped source in the app. **Every tool call already builds a `Job`** carrying
`CreatedAt`, `StartedAt`, `CompletedAt`, `State`, `Result.Success`, `Result.ExitCode` and
`RetryCount` — a complete per-call record, rendered to a row and then dropped when the session ends.

```sql
CREATE TABLE IF NOT EXISTS tool_calls (
    call_id      TEXT PRIMARY KEY,      -- job.Id
    agent_id     TEXT NOT NULL,         -- parent OR child; joins either table
    tool_name    TEXT NOT NULL,         -- read_file, shell, spawn_agent, mcp:context7:…
    plugin_type  TEXT,                  -- file | shell | http | llm_agent | mcp
    outcome      TEXT NOT NULL,         -- succeeded | failed | cancelled | denied
    duration_ms  INTEGER NOT NULL,
    result_chars INTEGER NOT NULL,      -- what it put INTO the context
    started_at   TEXT NOT NULL);

CREATE INDEX IF NOT EXISTS idx_calls_by_agent ON tool_calls(agent_id);
CREATE INDEX IF NOT EXISTS idx_calls_by_tool  ON tool_calls(tool_name, started_at);
```

`result_chars` is the one that earns its place: it is **what a tool cost the context**, and the whole
premise of delegation is moving large results out of the parent. Tonight that was 751,152 chars of
child reading returning 57,502 — measurable only because the logs happened to be on disk.

`agent_id` deliberately points at either a session or a child. A child's calls are the interesting
ones (that is where the reading happens), and forcing them into a parent-only column would lose it.

### `compactions` — new, one row per compaction

`ContextCompressed` fires with `(before, after)` and nothing keeps it. Compaction is the most
expensive automatic thing the app does and there is currently **no way to know how often it happens**.

```sql
CREATE TABLE IF NOT EXISTS compactions (
    agent_id       TEXT NOT NULL,
    at             TEXT NOT NULL,
    before_tokens  INTEGER NOT NULL,
    after_tokens   INTEGER NOT NULL,
    trigger        TEXT NOT NULL);      -- pressure | manual
```

`trigger` separates "the app decided" from "the user typed `/compress`" — a threshold that fires too
eagerly and one that never fires are both invisible without it.

### `permissions` — new, one row per decision

Five kinds (`Shell`, `FileRead`, `FileWrite`, `Http`, `Mcp`), each decided per call and discarded.

```sql
CREATE TABLE IF NOT EXISTS permissions (
    agent_id   TEXT NOT NULL,
    at         TEXT NOT NULL,
    kind       TEXT NOT NULL,
    decision   TEXT NOT NULL,           -- allowed | denied | rule | silent
    requester  TEXT);                   -- which agent asked — parent or a child's label
```

`requester` exists because the permission prompt already carries it (added so a prompt could say
which agent was asking). "How often does a worker ask for something the parent would not have?" is
a safety question, not a curiosity.

### Two columns more on `agent_sessions`

| Column | Why |
|---|---|
| `turns INTEGER` | Sessions are compared by cost today; cost per turn is the more honest measure of a long session against a short expensive one. |
| `started_at TEXT` | `updated_at` alone cannot give session duration, and duration is the axis every "how did I spend my week" view wants. |

---

## What that makes possible

The point of storing more is that the interesting views are **joins**, not counters:

- **Cost of a tool, not just a session** — `tool_calls` grouped by `tool_name`: which tool puts most
  into context, which fails most, which is slowest. `read_file` returning 16k chars 40 times is a
  compaction waiting to happen, and nothing surfaces that today.
- **Delegation efficiency, measured** — child `input+output` from `agent_runs` against the
  `result_chars` of the parent's `spawn_agent` call in `tool_calls`. That ratio (31× for tonight's
  web-research child) is the number that says whether a spawn paid for itself, and it needs both
  tables.
- **Does compaction correlate with failure?** — `compactions` against the `outcome` of calls that
  follow. Compaction rewrites the conversation; whether models get worse afterwards is answerable
  from history and unanswerable from one session.
- **Which agent type earns its keep** — `agent_runs` by `type_name`: average turns, failure rate,
  tokens per run. `planner` at 6× compression versus `explore` at 31× is a real difference in what
  the types are for.
- **A session's actual timeline** — `tool_calls.started_at` + `duration_ms` reconstructs where the
  wall-clock went: thinking, tool execution, or waiting on a child.

---

## `/stats` — what it answers

Four sections, each a query, each a question tonight actually raised.

```
Today                    3 sessions · 1,966,676 tokens · ↑1.9M ↓22.1k
This week               11 sessions · 4,102,331 tokens
```

```
By project
  ~/source/cxgpu         2,180,404   5 sessions
  ~/source/cxagent         921,927   4 sessions
```

```
By model
  qwen3.6-35b-a3b        1,966,676   ↑1.9M ↓22.1k
```

```
By agent type            runs   tokens     avg turns   failed
  explore                   4  1,204,553        9.2        0
  planner                   2    462,834        7.5        0
  general                   1     12,004        2.0        0
```

The last block is the one that does not exist anywhere today and is the reason for `agent_runs`.
"Is `planner` worth spawning?" is answerable only across runs — a single session's row cannot say
whether 41k is typical or an outlier.

### Deliberately absent

**"Processing time saved."** There is no counterfactual run, so any figure would be invented, and an
invented number beside real token counts devalues the real ones. Elapsed time per session and per
child is real and is stored; *saved* is not.

**Cost in currency.** Local models cost electricity, not dollars, and a per-token price for a hosted
provider is config the app does not have. Tokens are the honest unit.

---

## Order of work

| # | Change | Note |
|---|---|---|
| 1 | Five columns on `agent_sessions` via `AddColumnIfMissing` | no reads change |
| 2 | `SaveTurn` writes them | one call site, all values in scope |
| 3 | `agent_runs` + `SaveRun` | one row per child, written at completion |
| 4 | Parent writes the child's row where the finished job row is built | `Agent.cs`, beside the existing `job.ProgressBody` account |
| 5 | `tool_calls` + `SaveToolCall` | the big one — every call, parent and child |
| 6 | `compactions`, `permissions` | two small writers on existing events |
| 7 | `StatsQuery` — pure functions over the store, no UI | testable without a terminal |
| 8 | `/stats` rendering it | `SessionCommands`, like `/mcp` |

**Steps 1–6 before 7–8, and 1–2 first of all.** Every session run before a column exists is history
that can never answer its question — there is no backfill for a number nobody recorded. The reading
half can arrive whenever; the writing half is the part with a deadline.

## Volume, and why it is not a problem

`tool_calls` is the only table that grows quickly — tonight's five sessions would be roughly 250 rows.
A heavy year is on the order of 10⁵ rows of ~120 bytes: **single-digit megabytes**, with two indexes
on a database that today holds a handful of rows. No retention policy needed at this scale; if one is
ever wanted, `DELETE FROM tool_calls WHERE started_at < ?` is the whole of it.

The one real cost is **write frequency**: a row per tool call rather than per turn. `SaveTurn` already
writes a full `context_json` every turn — often tens of kilobytes — so a 120-byte row per call is
noise beside what the store does now. It must keep the same discipline as everything else here:
**best-effort, failures swallowed**. Statistics must never be able to fail a session.

## Note on the kernel split

`agent_runs` is session storage, and `isolated-kernel.md` puts session storage on the host side of
the seam (item 2: `SqliteSessionStore` → `ISessionStore`). Adding a second table now means one more
method on that interface later — `SaveRun` alongside `SaveTurn` and `MarkFinished`. Worth knowing;
not worth blocking on, since the alternative is a stats feature that waits for a refactor.
