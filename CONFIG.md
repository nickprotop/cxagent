# Configuration

One file: `config.json`, in cxagent's config directory.

| OS | Path |
|---|---|
| Linux | `$XDG_CONFIG_HOME/cxagent/`, or `~/.config/cxagent/` |
| macOS | `~/Library/Application Support/cxagent/` |
| Windows | `%APPDATA%\cxagent\` |

The directory is forced to `0700` and `config.json` to `0600` on every start — it holds API keys, and
a readable directory exposes the file listing even when the file itself is locked down. An install
made before that was enforced is repaired on the next launch rather than staying loose.

There is no config file until you make one. [`config.sample.json`](config.sample.json) documents every
key inline, including what each one's absence means. F5 in the app edits providers without touching
the file by hand.

---

## `providers` — required

Named instances. The name is yours; `kind` is what cxagent knows how to talk to.

```json
"providers": {
  "local": {
    "kind": "openai-compatible",
    "model": "qwen3.6-35b-a3b-ud-iq4_xs.gguf",
    "baseUrl": "http://127.0.0.1:8771/v1",
    "contextWindow": 212992
  }
},
"defaultProvider": "local"
```

**`contextWindow` is worth setting even though it is optional.** Without it the app cannot tell you
how full the context is — a percentage needs a denominator, and inventing one would put a confident
figure on a guess. It is also what auto-compaction measures pressure against: unset, the threshold
falls back to a fixed constant that may be far from your model's real headroom.

**`apiKey` supports `${ENV_VAR}`**, so a config file can be committed or shared without carrying a
secret. A key written literally is still forced to `0600`, but the variable form is better.

**`maxConcurrentAgents` bounds how many sub-agents may call this endpoint at once.** Absent or `0`
means unlimited, which is the default — the same convention `maxTurns` uses.

Uncapped is deliberate: cxagent cannot discover what an endpoint tolerates, and a limit chosen
without evidence throttles everyone to guard against a problem they may not have. Set it when you
know your endpoint's shape. What uncapped costs, so the choice is informed:

- **A hosted API** answers concurrent requests and pushes back with 429s — and the retry layer then
  multiplies the traffic that caused them.
- **A single-threaded local server** (the common `llama.cpp` case) queues them at the socket, so the
  children hold open connections and running timeouts while executing one at a time. The parallelism
  becomes serial execution with more ways to fail.
- **A local server splits its context across slots.** With N concurrent streams each child actually
  gets `n_ctx / N` while its own accounting believes it has the whole window — so compaction fires
  far too late and children die on overflow rather than compacting.

The real bound on runaway cost is `orchestrator.goalTokenBudget`, not this: a child is refused
outright once the session's budget is already spent, whatever the concurrency.

**`defaultProvider` is optional** and names one of the instances above. Without it, a session with
more than one configured provider has no way to choose.

---

## `agents` — sub-agent types

Types the model can name when it spawns a worker. Every one is optional; a session with no `agents`
block still has `general`.

```json
"agents": {
  "explore": {
    "briefing": "You search and report. Find what was asked for, give exact paths as
                 file_path:line_number, and say what you actually saw rather than what you
                 expect to be true. Do not edit anything.",
    "maxTurns": 30
  }
}
```

| Key | Meaning |
|---|---|
| `briefing` | The child's highest-authority instruction. Config is the only legitimate author of this — a parent cannot set it. |
| `provider` | An instance name from `providers`. Omit to inherit the session's. |
| `maxTurns` | Absent inherits the session ceiling; `0` is unbounded. |

**A type's provider brings its own context window.** They resolve together, deliberately: a child
given one provider and another's window never sees pressure, never compacts, and dies on an overflow
rather than degrading.

**`maxTurns` is a backstop, not a budget.** Seen live: an `explore` child spent all 30 of its turns
hunting a JSON schema that is not published anywhere, then filled its last few re-reading local
files. The cap stopped it, the parent was told `capped`, and the session carried on — but the fix was
the briefing, not the number. A give-up clause would have returned a useful answer at turn 15.
`/stats` counts capped runs apart from failures for exactly this reason.

Unknown type names are refused rather than silently treated as `general`: substituting the default
would mean the briefing did not apply and nobody was told.

---

## `mcp` — servers

```json
"mcp": {
  "context7": { "command": ["npx", "-y", "@upstash/context7-mcp"], "enabled": true },
  "remote":   { "url": "https://example.com/mcp", "headers": { "Authorization": "Bearer ${TOKEN}" } }
}
```

`command` is argv — no shell, so no pipes, globs or redirection. An HTTP server uses `url` instead,
and may carry `headers`. Optional per server: `enabled` (default true), `cwd`, `env`, `timeoutMs`.

A server's own usage instructions, when it sends any, are appended to the system prompt **attributed
to it by name**, so the model can tell that advice apart from cxagent's own.

`/mcp reload` re-reads this block and reconnects — adding a server does not need a restart.
`/mcp login <server>` runs OAuth for a server that returned 401 and stores the token at `0600`,
never in the config file.

---

## `orchestrator` — caps

```json
"orchestrator": { "goalTokenBudget": null, "maxWorkerTurns": 500 }
```

`goalTokenBudget` is a ceiling for the whole session, `null` for none. `maxWorkerTurns` is the
default turn ceiling an agent type inherits when it names none.

---

## Instruction files — `AGENTS.md` and friends

Separate from `config.json`, and answering a different question: config is *how this app is wired*,
instructions are *what an agent should know here*. They ride in the system message.

**Two files apply, in this order:**

1. **The global one** — `CXAGENT.md` in cxagent's config directory. What is true of you wherever you
   work.
2. **The nearest project one** — searched by walking up from the working directory until a match is
   found or the filesystem root is reached.

**Project file names, first match wins:**

| Name | Why in this order |
|---|---|
| `CXAGENT.md` | So a repo can address **this** agent specifically. Some instructions are only true of cxagent — its permission model, its process — and a shared file is the wrong place for them. |
| `AGENTS.md` | The vendor-neutral convention, and what a repo will already have if it has anything. |
| `CLAUDE.md` | Last, so a repo carrying only that one is still honoured. |

**Only the nearest match is used, not every ancestor.** Stacking one file per directory from here to
`/` would let a file three levels up silently govern a session, and the reader has no way to know
which files contributed.

**The global file is `CXAGENT.md` and only that.** No `AGENTS.md` at that path — no other agent reads
cxagent's config directory, so the shared name buys nothing and invites confusion. No global
`CLAUDE.md` either: a `CLAUDE.md` describes a *project*, and reading one from a home directory
applies project instructions to every project. opencode does read `~/.claude/CLAUDE.md`; this
deliberately does not.

**Capped at 8,000 characters, and the cut is marked in the text.** This rides in the prompt-cache
prefix that is re-sent on every turn, so a large instruction file is not a one-off cost but a
permanent tax on the window. Instructions that stop mid-sentence with no explanation read as a bug in
the agent, so the truncation says so.

---

## What else lives in that directory

| File | What it is |
|---|---|
| `config.json` | This file. `0600`. |
| `cxagent.db` | The resume buffer — one row per agent, replaced every turn, worthless once a session ends cleanly. Pruned on startup. |
| `history.db` | Usage history for `/stats`. An archive: append-only, never pruned. `/stats clear` empties it. |
| `logs/<agent-id>/` | Per-turn context dumps and per-tool output. A sub-agent's logs nest **inside** its parent's directory, so the tree mirrors who spawned whom. |
| `CXAGENT.md` | Your global instructions, if you wrote any. |

Deleting `cxagent.db` costs a crash recovery and nothing else. Deleting `history.db` costs `/stats`.
