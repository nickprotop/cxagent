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
2. **The nearest project one** — searched by walking up from the working directory, stopping at the
   repository root (the directory holding `.git`). Outside a repository only the working directory
   itself is read: with no worktree there is no boundary that means anything, and climbing would let
   a scratch folder under your home directory pick up that directory's own files.

**Project file names, first match wins:**

| Name | Why in this order |
|---|---|
| `CXAGENT.md` | So a repo can address **this** agent specifically. Some instructions are only true of cxagent — its permission model, its process — and a shared file is the wrong place for them. |
| `AGENTS.md` | The vendor-neutral convention, and what a repo will already have if it has anything. |
| `CLAUDE.md` | Last, so a repo carrying only that one is still honoured. |

**One NAME, but every level inside the repo that has it.** The first name that matches anywhere wins,
and then every copy of *that* name from the repository root down to here is used, root first — so the
nearest file is rendered last and wins on a conflict. The monorepo case is why: a root file carries
the house style, a package file carries what is specific to that package, and both are true at once.
What is never mixed is the *names* — a repo with `AGENTS.md` at its root and `CLAUDE.md` in a
subdirectory gets the `AGENTS.md`.

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

## Skills

Instructions the model loads **when it needs them**, instead of paying for them on every turn.

The split is the whole point: a skill's **name and description** ride in the system prompt
permanently and cost a few hundred characters, while its **body** is fetched by a tool only when the
model decides a task matches. Twenty skills of 3k each would be 60k of permanent prefix; their
catalog is a rounding error.

**A skill is a directory with a `SKILL.md`:**

```
.cxagent/skills/
  double-entry-posting/
    SKILL.md
```

```markdown
---
name: double-entry-posting
description: Use when adding, reviewing or fixing any ledger posting or balance
  logic in this repo. Covers house rules that are not obvious from the code.
---

# Double-entry posting

Every posting must sum to exactly zero. A transaction that does not balance is
rejected, never auto-corrected.
```

**The description is the entire interface.** It is the only thing the model sees before deciding, so
write it as *"Use when…"* rather than as a title. A description that says WHEN beats one that says
WHAT — the model is matching a task against it, not reading a table of contents.

**Where they live, first directory with a valid skill wins:**

| Location | Meaning |
|---|---|
| `<repo>/.cxagent/skills/` | This project, addressed to this agent |
| `<repo>/.agents/skills/` | This project, vendor-neutral — the plural of the `AGENTS.md` convention |
| `<config dir>/skills/` | You, wherever you work |

Searched by walking up from the working directory to the repository root, nearest first, so a
package's own skills outrank the repo's. Same boundary as the instruction files, and it matters more
here: a skill is text the model reads **and acts on**, and the directories above your home folder are
writable by other people on a shared machine.

**Unlike instructions, skills shadow rather than stack.** Two `AGENTS.md` files combine sensibly —
house style plus package specifics. Two `SKILL.md` files with the same name are two *versions* of one
document, and merging them produces a document that contradicts itself. Exactly one directory
supplies the catalog.

**"Has a skill" means at least one that parses**, not merely that the directory exists — an abandoned
empty `.cxagent/skills/` must not silently switch off a populated `.agents/skills/` beside it.

**`.claude/skills/` is not read.** Those files carry `allowed-tools`, a tool grant written for a
different application with different tools; honouring it silently would mean obeying permissions you
never granted here. If you want them, say so explicitly:

```sh
ln -s .claude/skills .cxagent/skills
```

Their prose then loads normally. Unknown frontmatter keys — `allowed-tools`, `argument-hint` — are
ignored rather than rejected, which is what makes that symlink work. cxagent's own permission gate
governs every call a skill provokes, exactly as if the model had chosen the tool itself.

**A skill can ship other files.** Reference documents, templates, examples — anything beside the
`SKILL.md`:

```
.agents/skills/xunit/
  SKILL.md
  manifest.json
  references/
    patterns.md
    anti-patterns.md
```

They are **listed by absolute path when the skill loads**, and the model reads the one it needs with
the ordinary file tool. Not inlined: inlining every reference would hand back the permanent prefix
the catalog/body split exists to avoid, and most references go unread on most tasks. Write the body
to point at them by name — *"see references/anti-patterns.md for what not to do"* — and the model
matches that against the listing.

The same permission gate governs those reads as governs every other one. A **project** skill's files
are inside the working boundary and read without asking; a **global** skill's live in the config
directory, outside it, so they prompt — those files are not part of the repository you are working
in. Twenty files are named before the list is cut short, since the listing rides in the window for
the rest of the session.

**A malformed `SKILL.md` is skipped and reported, never guessed at.** Missing frontmatter, missing
`description`: the skill does not exist as far as the model is concerned, and `/skills` says which
file and why — including files in a directory that lost the shadowing contest, since a broken file in
a shadowed directory is exactly the one nothing else would explain.

**The name comes from the folder.** A `name:` in the frontmatter is checked against it and reported
when it disagrees, never obeyed — otherwise two directories could declare the same skill.

**Re-read every turn.** Edit a skill and it takes effect on the next one, with no restart and no
refresh command. The prompt cache is protected by comparison rather than by caching the read: an
unchanged file renders byte-identical text and the system message is not replaced, so editing one
costs a single prefix — which is what you asked for by editing it.

**Once loaded, a skill stays until the conversation is compacted.** Its body is an ordinary tool
result, so it is re-sent every turn like any other and cannot be unloaded — an unload would leave the
call that fetched it unanswered, which breaks the session outright. When compaction does remove it,
the model is told which skills went and that it may reload them; without that it would keep citing a
document it can no longer read. `/skills` shows what exists; the session panel shows what is
currently in force.

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
