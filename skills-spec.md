# Skills

Instructions the model loads when it needs them, instead of paying for them on every turn.

Built the way Claude Code and opencode build them — both were read in source before this was written,
and where they agree the reasoning is theirs, not ours.

---

## 0. Decisions

| # | Decision | Where |
|---|---|---|
| S1 | **A skill is a directory with a `SKILL.md`**: YAML frontmatter (`name`, `description`), markdown body. Both references use exactly this, and published skills exist in it. | §1 |
| S2 | **The CATALOG is in the system prompt; the BODY is not.** Names and descriptions cost a few hundred characters permanently. Twenty skills of 3k each would be 60k of permanent prefix. | §2 |
| S3 | **Loading is a TOOL.** `load_skill {name}` → the body as a `Role="tool"` message. Structurally identical to `spawn_agent`: same `ToolDefinition` array, same `??` chain, same result shape. | §3 |
| S4 | **Every agent has a catalog — parent and child alike.** The main agent is the ordinary case, not an exception. opencode's `load(agent)` takes any agent. | §2 |
| S5 | **A child does not inherit a loaded skill.** It has a fresh context and its own catalog; it loads what its own task needs. | §2 |
| S6 | **No unload.** Neither reference has one, and here it would be wrong: the body is a tool result, so removing it means editing history — the thing that breaks tool-call pairing and 400s a session. | §4 |
| S7 | **No command loads a skill.** The model decides. `/skills` LISTS what was found, which is the `/mcp` case: a malformed file silently does not exist and nothing else would tell you. | §5 |
| S8 | **Discovery runs per turn inside the prompt build, exactly as `ProjectInstructions` does** — no cache, no snapshot, no rebuild policy. Unchanged files render byte-identical text, the message is replaced only when it differs, so the prefix holds. Editing a skill costs one prefix and takes effect next turn: their edit, their cost. | §2, §3 |
| S9 | **Prose first; reference FILES followed almost immediately.** Real skills linked `references/*.md` relative to a directory the model could not see, so the loader now lists a skill's other files by absolute path and the model reads them through the ordinary permission gate. Only SCRIPTS remain deferred. | §6 |
| S10 | **The UI says which skills are loaded.** A skill shaping behaviour with nothing on screen saying so is an invisible state change, and it changes again silently at compaction. | §5 |
| S11 | **A CHILD'S ROW SAYS WHAT IT LOADED**, in its expanded body beside `type` and `model`. A worker is the case where this matters most: its context is invisible, so a loaded skill is the one thing shaping its behaviour that the parent cannot see. | §5 |

---

## 1. What a skill is

```
skills/
  rtl-aware-development/
    SKILL.md
```

```markdown
---
name: rtl-aware-development
description: Use when implementing or reviewing RTL/LTR behaviour — CSS, menus,
  scrolling, mixed-direction text.
---

# RTL-Aware Development

Treat direction as independent from language…
```

**THE DESCRIPTION IS THE ENTIRE INTERFACE.** It is the only thing the model sees before deciding, so
it is written as *"Use when…"* rather than as a title. Both references phrase every one that way, and
it matches what this project already measured: four `<example>` blocks moved the delegation rate where
three abstract rules did not. A description that says WHEN beats one that says WHAT.

**Two locations, resolved like instructions already are** (`ProjectInstructions.cs:39` walks
`CXAGENT.md` / `AGENTS.md` / `CLAUDE.md`, `:58` reads a global one):

| Location | Meaning |
|---|---|
| `AppPaths.ConfigDir/skills/` | true of the user wherever they work |
| `<repo>/.cxagent/skills/` → `<repo>/.agents/skills/` | true of this project, first match wins |

**HIDDEN AND NAMESPACED, BECAUSE THAT IS WHAT BOTH REFERENCES ACTUALLY DO.** Claude Code reads
`.claude/skills/<name>/SKILL.md`; opencode registers BOTH `<config dir>/skill` and
`<config dir>/skills` (`config/plugin/skill.ts:25`, singular and plural, so a user guessing either is
right) under every `.opencode` found walking up from cwd. A bare `skills/` at a repo root has no
precedent in either.

**IT IS A TOOL-LOADING PATH, NOT A DOCUMENT.** `AGENTS.md` is unhidden because humans and several
different agents READ it; a skills directory is configuration that an app LOADS FROM, which is why
both references hide it. Those are different things and the analogy between them does not hold.

**TWO NAMES, FIRST MATCH WINS — `ProjectFileNames` one level up** (`ProjectInstructions.cs:39`).
Specific first, neutral second, degrades cleanly when neither exists:

| Order | Directory | Why |
|---|---|---|
| 1 | `.cxagent/skills/` | this app's own, unambiguous |
| 2 | `.agents/skills/` | vendor-neutral, matching the plural `AGENTS.md` this app already reads |

**AT THE DIRECTORY LEVEL, NOT THE SKILL LEVEL.** If `.cxagent/skills/` wins, `.agents/skills/` is not
consulted at all — not even for a name the winner lacks. Same rule as `ProjectFileNames`, where a
repo carrying `CXAGENT.md` never reads its `AGENTS.md`. Merging would make a skill's origin depend on
which names happened to collide, which is the unpredictable version.

**"EXISTS" MEANS HOLDS AT LEAST ONE VALID SKILL, NOT `Directory.Exists`.** An empty `.cxagent/skills/`
must not shadow a populated `.agents/skills/` — this repo already contains an empty
`cxagent/.agents/skills/`, so the abandoned-directory case is not hypothetical. A directory that
silently disables everything below it is the worst failure this design could ship.

**BUT THAT RULE AND "MALFORMED FILES ARE REPORTED" CONTRADICT EACH OTHER, AND THE SPEC RESOLVES IT
HERE.** Take `.cxagent/skills/` holding exactly one skill with broken frontmatter. It has no VALID
skill, so it does not "exist", so `.agents/skills/` wins — and the broken file sits in a directory
discovery decided was not there. Reported, or not? If not, the user loses their skill AND the message
explaining why, which is the exact `/mcp` failure this section opened by citing.

**SKIPPED FILES ARE COLLECTED FROM EVERY CANDIDATE DIRECTORY, INCLUDING LOSERS.** Shadowing decides
which directory SUPPLIES SKILLS; it does not decide which directory may report problems. So
`/skills` says *"`.agents/skills/` is in use; `.cxagent/skills/` was skipped — `rtl/SKILL.md` has no
`description`"*, and the user learns both facts. Diagnostics are not subject to first-match-wins.

**`.claude/skills/` IS NOT READ, and a symlink is the answer.** `ProjectInstructions` already drew
this line: it honours a project's `CLAUDE.md` (which describes the project) while refusing
`~/.claude/CLAUDE.md` (another product's configuration). Skills sit further across it — a real
`doc-control/SKILL.md` on this machine carries `allowed-tools: Read, Write, Edit`, naming tools by
Claude Code's names, not this app's. Loading that silently would mean honouring a tool grant written
for a different application. A user who wants them says so explicitly:
`ln -s .claude/skills .cxagent/skills`.

The global side has no such doubt: `AppPaths.ConfigDir` (`AppPaths.cs:9`) resolves per-OS, is already
where this app's own files live, and needs no fallback because nobody else writes there.

**SHADOWING IS A DELIBERATE DEPARTURE FROM THE INSTRUCTION WALK, NOT A COPY OF IT.** Read
`ProjectInstructions.cs:101-118` before assuming otherwise — its comment is *"ONE NAME, EVERY LEVEL
THAT HAS IT"*. First-match-wins there applies across NAMES; the winning name then collects **every
ancestor directory**, rendered root-first so the nearest wins on conflict (`:116`), and the global
file is included ALONGSIDE rather than shadowed (`:96-97`). That is accumulation with precedence.

**Skills shadow instead, because they are documents rather than prose.** Two `AGENTS.md` files
stack — house style plus package specifics, both true at once. Two `SKILL.md` files with the same
name are two versions of one document, and concatenating them produces a document that contradicts
itself. So: nearest complete definition wins, and the loser is not merged in.

**DISCOVERY WALKS UP FROM CWD AND STOPS AT THE REPO ROOT — the same boundary `ProjectInstructions`
now uses** (`cac2f2e`): start at the working directory, walk up while looking for a `.git` entry, stop
at the directory holding it. **No `.git` anywhere means cwd only, nothing above it.**

**THE BOUNDARY IS THE POINT, NOT THE WALK.** Before `cac2f2e` that walk ran to the FILESYSTEM root,
which for skills would be materially worse than for instructions: a `skills/` directory in `~`, in
`/home`, or in `/` would have supplied loadable documents to every session, from directories that on
a shared machine other people can write. Instructions are text the model reads; a skill is text the
model reads AND acts on, so the same exposure carries further.

**`.git` AS FILE OR DIRECTORY.** A submodule and a linked worktree mark their root with a `.git`
FILE holding a `gitdir:` pointer; testing only for the directory walks straight past them. **This
predicate already exists TWICE** — `IsGitRepo` at `Agent.cs:439`, whose comment calls the file case
*"exactly the checkout style this project is developed in"*, and the walk bound added by `cac2f2e`.
Step 1 reuses it rather than writing a third copy.

**NEAREST WINS, and it composes with §1's directory-level shadowing in one rule:** walk up from cwd,
and the first ancestor that holds a directory with at least one valid skill supplies the catalog.
`.cxagent/skills` is checked before `.agents/skills` at each level, so a package-level
`.cxagent/skills` beats a repo-root one, and a repo-root `.agents/skills` is used when no level has a
`.cxagent/skills`. Skills SHADOW rather than stack (§1), so exactly one directory ever wins.

**A file that does not parse is SKIPPED AND REPORTED, never guessed at.** Missing frontmatter,
missing `name`, missing `description`: the skill does not exist as far as the model is concerned, and
`/skills` says why. This is the `/mcp` lesson — *a server that silently never appears is
indistinguishable from one you never configured.*

**THE NAME COMES FROM THE DIRECTORY; FRONTMATTER `name` IS CHECKED, NOT TRUSTED.** Otherwise two
directories can declare the same `name`, and a directory can disagree with the file inside it — both
undefined and both confusing. Tying identity to the directory makes duplicates impossible by
construction; a mismatched `name:` is reported by `/skills` rather than silently honoured.

**UNKNOWN FRONTMATTER KEYS ARE IGNORED, NOT FATAL — AND THIS IS WHAT MAKES THE SYMLINK ADVICE REAL.**
A real `.claude/skills/doc-control/SKILL.md` on this machine carries `argument-hint` and
`allowed-tools`. Strict parsing would make `ln -s .claude/skills .cxagent/skills` import NOTHING,
quietly breaking the escape hatch recommended above. So unknown keys are skipped over.

**BUT IGNORING `allowed-tools` IS A DECISION WITH TEETH, so say it out loud:** that key is another
application's tool grant. This design does not honour it and does not pretend to — cxagent's own
permission gate governs every call a skill provokes, exactly as if the model had chosen the tool
itself. A user symlinking Claude Code's skills gets their PROSE, not their permissions.

---

## 2. The catalog

Rendered into the system prompt for **every** agent, exactly where `# MCP servers` is
(`SystemPrompt.cs:388`) and by the same rules: sorted for prefix stability, omitted entirely when
empty.

```
# Skills

Skills provide specialised instructions for specific tasks. Use the load_skill
tool when a task matches one of these descriptions.

<available_skills>
  <skill>
    <name>rtl-aware-development</name>
    <description>Use when implementing or reviewing RTL/LTR behaviour…</description>
  </skill>
</available_skills>
```

**SORTED, ALWAYS.** `AppendMcpInstructions` already does `.OrderBy(kv => kv.Key, StringComparer.Ordinal)`
with the comment *"stable order, or the prefix churns"*. Same reason, same treatment: a catalog that
reshuffles between runs invalidates the prompt cache for nothing.

**RENDERED WHERE `ProjectInstructions` IS, NOT WHERE `McpInstructions` IS.** MCP prose arrives as an
init-only property because a session KNOWS its servers before the prompt is built. Skills are read
from disk at build time, so they follow the instruction path instead: discovered inside the per-turn
build and appended (`Agent.cs:464`), which is what gives S8 its "edit takes effect next turn" for
free.

**Either the catalog is an init-only `IReadOnlyList<SkillInfo>` on `SystemPromptContext` populated
from that same call, or it is appended alongside `ProjectInstructions.Render(...)`.** The first keeps
prompt ORDER under `SystemPrompt`'s control, which matters because the catalog belongs beside
`# MCP servers` rather than after the project's instructions. Step 2 takes it.

**BUT NOT A `Dictionary<string,string>`, WHICH IS WHERE THE ANALOGY BREAKS.** `McpInstructions` is
name → prose, and prose is all the prompt needs. A skill has a name, a description, a directory AND a
body; the prompt uses two of those, the loader needs all four. Declaring the prompt side as a
dictionary would fork discovery into two representations that must be kept in step. It is
`IReadOnlyList<SkillInfo>` — one record type, one snapshot, **sorted by `Name`**, which buys the same
prefix stability `.OrderBy(…, StringComparer.Ordinal)` buys MCP. The prompt renders the two fields it
wants and ignores the rest.

**PARENT AND CHILD BOTH GET ONE (S4).** A child's is built the same way. What a child does NOT get is
whatever the parent loaded: it has a fresh `AgentContext`, so a body in the parent's message list is
invisible to it. It loads its own (S5).

**An `AgentType` may narrow a child's catalog** — the way it already narrows provider, window and
turns. Absent, the child sees everything. Worth having because a `planner` that can load a
`deployment` skill is a planner with a distraction; not worth building until a skill exists that
someone wants withheld.

**THE SEAM IS THE SNAPSHOT PASSED TO THE CHILD (§3), so build it in the right shape now.** Since the
catalog is threaded through `SubAgentFactory` explicitly, narrowing later means passing a FILTERED
snapshot at that one call site — a smaller change than passing an unfiltered one and adding a filter
at every read. Deferring the feature is free; deferring the shape is not.

---

## 3. Loading

```
load_skill { name: "rtl-aware-development" }
  → Role="tool":  { name, directory, content }
```

**A TOOL, NOT AN INJECTION, and that is what makes it cheap here.** It joins the definition array at
`Agent.cs:512` and resolves in the `??` chain at `:1374` — spawner, then MCP, then `WorkerToolset` —
returning a string that becomes a `Role="tool"` message with a `ToolCallId`. The loop does not learn
a new concept. Same observation that made sub-agents small: *an agent is a tool*, and so is a skill.

**IT IS AN AGENT-OWNED BRANCH IN THE `??` CHAIN, NOT AN `IJobPlugin` — AND THIS IS FORCED, NOT
PREFERRED.** The tool must read the agent's own message list to answer the double-load question
below, and a plugin cannot: `IJobContext` (`Core/Plugins/IJobPlugin.cs:19-106`) carries progress, log
and telemetry callbacks plus `Requester` — whose own doc comment says *"A LABEL, NOT AN ID"*, a human
phrase for the permission prompt. **No agent identity, no message access.** With five children
running concurrently through one shared plugin instance, a plugin could not even tell which of them
was asking.

**SO IT SITS BESIDE THE SPAWNER**, which is the exact precedent: `_spawner!.TryInvokeAsync(call,
OnChildSpawned, ct, Id)` at `Agent.cs:1374` is agent-owned, is passed the agent's `Id`, and returns
null for names it does not own. `_skills!.TryInvokeAsync(call, Context.Messages, ct)` slots in
identically, ahead of the MCP branch. S3's *"structurally identical to `spawn_agent`"* becomes
literally true rather than approximately.

**AND S4 COSTS NOTHING AFTER ALL — but not for the reason an earlier draft gave.** It is not that
`_plugins` is shared; it is that **discovery is not injected at all**. Every agent builds its own
system prompt from its own `TryGetWorkingDirectory()` (`Agent.cs:1955`, which is
`Directory.GetCurrentDirectory()` — process-wide, so parent and child resolve the same directory),
and discovery runs there. A child finds its own catalog because it runs the same code, not because
anything was handed to it. Nothing threads through `SubAgentFactory`.

**WHAT LIVES WHERE — AND THE ANSWER IS "NOTHING SHARED", WHICH IS WHY FAN-OUT IS A NON-EVENT:**

| State | Lives where | Why |
|---|---|---|
| Discovery results | **nowhere** — re-read per turn, per agent, during its own prompt build | no shared object exists, so there is nothing to race on and nothing to swap |
| "What have I loaded?" | **nowhere** — derived from the agent's own messages | state that cannot drift because it is not stored |

**A SHARED MUTABLE LOADED-SET IS THE BUG THIS DESIGN AVOIDS BY CONSTRUCTION.** It is the natural
place to reach for and it fails twice: concurrent mutation from several children, and — worse because
it is silent — child A's load making the tool answer *"already loaded"* to child B, which then
proceeds with a skill it never received. A wrong answer, delivered calmly, to an agent that cannot
detect it. Deriving both halves from per-agent state removes the object that bug lives on.

**THERE IS NO REBUILD POLICY, BECAUSE THERE IS NO CACHE — S8 IS ALREADY SATISFIED BY THE EXISTING
PROMPT PATH.** `ProjectInstructions.Find` is called at `Agent.cs:464` INSIDE the per-turn prompt
build, and the comment above it settles this question for skills too:

> *"The instruction files are read again each time, so editing AGENTS.md mid-session takes effect on
> the next prompt. That is the user's call to make: they edited the file, and an agent that silently
> ignores it until a restart is behaving as though it knows better."*

**THE CACHE IS PROTECTED BY COMPARISON, NOT BY AVOIDING THE READ.** The system message is REPLACED
only when the rendered text actually differs (`:420-430`), so unchanged skills produce a
byte-identical prefix and the cached reads keep hitting. Editing one costs exactly one prefix — the
thing the user asked for by editing it.

**SO NO SNAPSHOT, NO SWAP, NO `volatile`, NO WATCHER, AND NO REFRESH COMMAND.** An earlier draft
specified all of them, on cache reasoning that does not apply to a path which re-reads and compares.
`/skills` is purely diagnostic (§5). A skill edited mid-session takes effect on the next turn, which
is both what a user expects and what already happens for `AGENTS.md`.

**This mirrors the ledger exactly** — per-agent tallies are per-agent, and the shared aggregate is
written under interlock. Same shape, same reason.

**AN UNKNOWN NAME IS AN ERROR STRING, NOT AN EXCEPTION** — and it names the valid ones, exactly as
`spawn_agent` does for an unknown type. A refusal the model can act on beats a turn that ends.

**LOADING THE SAME SKILL TWICE RETURNS A SHORT ACKNOWLEDGEMENT, NOT THE BODY AGAIN.** A model that
forgets what it loaded — likely, since the body drifts far up the context — would otherwise pay for
the whole document a second time and put two copies in the window. So the second call answers
*"already loaded earlier in this conversation"* and nothing more.

**IT MUST STILL RETURN A RESULT, though — never nothing.** Every call needs its `tool` message or the
turn orphans; "already loaded" is a legitimate answer, silence is a broken session.

**AFTER COMPACTION THE ANSWER MUST FLIP BACK.** If the body was summarised away, "already loaded" is
a lie that leaves the model with nothing — so the answer must reflect whether the body is STILL IN
THE WINDOW, not whether a load ever happened.

**SO KEEP NO STATE AT ALL — DERIVE IT FROM THE CONVERSATION.** Scan `Context.Messages` for a marked
skill body. Present means "already loaded"; absent means load it.

**THE MARKER CARRIES THE NAME, AND THE SCAN NEVER JOINS ACROSS MESSAGES.** A tool result records
only `ToolCallId` and `Content` (`Core/Models/ChatMessage.cs:5-15`) — **the tool's NAME is not on it**,
it lives on the `ToolCall` in the preceding assistant message. So "find the call, take its id, find
the matching result" is a two-pass join, and it breaks in exactly the case this scan exists for:
compaction can remove one half and leave the other. Instead the body itself announces what it is:

```
[skill: rtl-aware-development]
# RTL-Aware Development
…
```

One `Content.StartsWith` over messages with a non-null `ToolCallId`. No join, no straddle
sensitivity — and it is the SAME recogniser §4's compaction notice needs, which sees a bare message
list with no join available either. One rule serves both.

**FILTER ON `ToolCallId is not null`, NEVER ON `Role == "tool"`.** `ChatMessage`'s own comment says
the wire builders overwrite `Role` — OpenAI sets `"tool"`, Anthropic emits a `"user"` turn carrying a
tool_result block — and that `ToolCallId` "is the ONLY marker of one". This is a one-line mistake
that costs an hour.

**THE MARKER IS NOT AN INVENTION — IT IS `SummaryMarker` AGAIN** (`SessionCompressor.cs:422`), which
exists so a summary is recognisable AS one on the next compaction, and whose own comment says it is
matched on the marker "rather than on position: the head's shape changes as the conversation grows,
and a positional guess would silently pick up an ordinary assistant message". A skill body faces the
identical problem — the message list is spliced under it — and takes the identical answer.

| Why this beats a tracked set | |
|---|---|
| Flips back automatically | after summarise, after `Truncate`, after `/clear` — no hook to forget |
| Per-agent for free | each agent scans its own messages; nothing shared, nothing to race |
| Cannot drift | the window IS the state, so the answer cannot disagree with reality |

**THE OBVIOUS IMPLEMENTATION — A PER-AGENT SET CLEARED ON A COMPACTION HOOK — IS WRONG TWICE.** It
misses the `Truncate` fallback path (§4), which also removes bodies and raises no summarise event;
and it OVER-clears, because compaction removes only the older half — a body loaded two turns ago
survives at `:162` and must stay "loaded". Getting both right is exactly the bookkeeping the scan
makes unnecessary.

**THIS IS WHY THE TOOL IS AGENT-OWNED** (above). The scan needs `Context.Messages`, the agent holds
them, and nothing else in the dispatch path can reach them.

**THE ACK MUST NOT COUNT AS A LOAD.** The marker goes on bodies only, or the second call's
*"already loaded"* result would itself satisfy the next scan.

**`directory` COMES BACK, AND SO DOES A FILE LISTING (S9).** The directory alone proved not to be
enough. A real skill says *"see references/anti-patterns.md"* — a path relative to a directory the
model cannot see — so the result also names what is IN that directory, absolute, ready to hand to the
file tool. The existing permission gate covers those reads and no new one is needed.

---

## 4. Lifetime — say this plainly, because it surprises people

A loaded skill is **a tool result in the message list**. Therefore:

- **It is re-sent on every subsequent turn**, like every other tool result. Loading a 3k skill on
  turn 2 of a 40-turn session costs 3k × 38 turns of context.
- **It survives until compaction**, which folds it into a summary or drops it. Nothing special-cases
  its REMOVAL — but it is no longer anonymous on the way out: it carries the §3 marker, so compaction
  can name what it removed (below) and the double-load scan can tell whether it is still there. The
  marker is the only special-casing, and it exists to make the loss VISIBLE rather than to prevent it.
- **It cannot be unloaded** (S6), and the reason is narrower than "editing history is unsafe" —
  because this codebase edits history routinely and safely. `SessionCompressor` splices the live
  conversation on every compaction (`RemoveRange` at `:162`). What makes that safe is `SafeCut`
  (`:62-64`), which walks the boundary BACKWARD off any message carrying a `ToolCallId` so a cut
  never lands between a call and its result: **pairs are removed together.** An unload would do the
  opposite — delete one `tool` message and leave the `assistant` message that called it holding an
  unanswered call. That is the orphan that 400s a session permanently, that
  `ContextOverflow.IsOverflow` does not match, and that only `/clear` recovers from.

  **So removal is not the hazard; HALF a pair is.** An unload could in principle be made safe by
  removing the call as well — which is a bigger, more invasive edit for a feature neither reference
  implements, and is why the answer is still no.

**SO THE SAVING IS NARROWER THAN "ON DEMAND" SUGGESTS.** You avoid paying for every skill
permanently; you pay for the one you loaded, permanently, from the moment you load it. That is still
much better — and it is an argument for keeping skill bodies focused rather than exhaustive.

**BUT THE BODY IS NOT CAPPED, unlike `ProjectInstructions`' 8,000 chars.** That cap guards the SYSTEM
PROMPT — text every turn carries whether or not anyone wanted it. A skill body is a tool result: paid
only when the model chose to load it, and removable by compaction. Truncating a document the model
deliberately asked for, mid-sentence, to save a cost it already accepted, trades a real loss for an
imaginary saving. **The DESCRIPTION is the field that rides in the prefix**, so that is where a length
limit belongs if one is ever needed.

**THE CATALOG OUTLIVES THE BODY**, and that asymmetry is the design working: the cheap thing is in
the system message and survives compaction, the expensive thing is a tool result and does not. A
model that needs a skill again after compaction can see it is available and reload it.

**WHAT COMPACTION DOES TO A LOADED SKILL, EXACTLY.** `SessionCompressor` removes the older half of
the conversation and inserts one `assistant` summary in its place, so a skill body inside that range
is GONE — replaced by whatever the summariser chose to say about it. On this path the failure mode is
"the skill quietly stops applying", not "the session breaks", because `SafeCut` walks the boundary
off any `ToolCallId` before the splice.

**AND ON THE FALLBACK PATH TOO, SINCE `f236b9f`.** That commit is a prerequisite of this design
rather than part of it. Before it, the `Truncate` fallback taken when `SummariseAsync` throws
(`SessionCompressor.cs:184`) did bare arithmetic with no walk-back, so a boundary landing on a tool
result deleted the `assistant` message that CALLED it and kept the answer — the orphan that 400s a
session permanently. It could strand any tool pair, with no skills involved; a provider blip during
compaction was the whole trigger.

**IT IS RECORDED HERE BECAUSE THIS SECTION ONCE ASSERTED "COMPACTION CANNOT ORPHAN" AS UNCONDITIONAL,
AND IT WAS NOT.** The claim held for the summarise path and was false for the fallback. It is true now
on both, which is what lets the rest of this section reason about skill bodies as things that get
REMOVED rather than things that break sessions. A skill body is a large tool result and made the
straddle likelier, so the feature would have found this eventually — better found first.

**THE QUIET STOP IS THE REAL PROBLEM, AND IT HAS A CHEAP FIX.** Nothing otherwise tells the model its
skill is gone. Worse, the summary may well mention it — *"loaded the rtl-aware-development skill"* —
leaving a model that believes a skill is in force with none of its actual content, still citing a
document it can no longer read.

**COMPACTION ALREADY KNOWS WHAT IT REMOVED, so it says so.** `oldTurns` (`SessionCompressor.cs:137`)
is the exact range about to be deleted. Scan it for marked skill bodies and insert one deterministic
line:

```
Skill bodies removed by compaction: rtl-aware-development. Reload with load_skill if still needed.
```

**OUTSIDE THE SUMMARY'S BRACKET, NOT INSIDE IT.** `FormatSummary` wraps the text as
`[earlier conversation, summarised: …]` (`:418-419`), and the NEXT compaction pulls that bracket back
out via `ExtractPreviousSummary` (`:430`) and feeds it to the summariser to merge. A notice inside
the bracket is therefore text a model will paraphrase or drop — destroying the one property that
makes this mitigation worth having. It goes after the closing `]`, in the same inserted message, as
deterministic text nothing ever rewrites.

**AND THE `Truncate` FALLBACK MUST INSERT ITS OWN NOTICE.** `f236b9f` stopped that path ORPHANING,
which is a different problem from the one here: it still REMOVES bodies, and it inserts no summary at
all (`:184`), so there is nothing to append to and the loss would go unannounced on the one path
taken when things are already going wrong. It emits the bare notice alone, with no summary around it.
The decline path (`:159`) needs nothing: it removes nothing, so nothing was lost.

**WHY THIS MITIGATION AND NOT THE OTHERS.** It costs no prompt-cache churn — the notice is an
ordinary `assistant` message, not the cached system prefix — it needs no cooperation from the
summariser model, and it cancels the lie directly rather than hoping the summary never tells it. The
rejected alternatives:
  * **Re-advertise in the catalog with a marker** — needs per-agent, per-compaction prompt rendering,
    which breaks §2's shared immutable snapshot and S8's "only the user's edits churn the prefix".
  * **Teach the summariser to omit skill loads** — depends on a model following an instruction, to
    fix a problem caused by a model following an instruction.

**IT ALSO CARRIES THE SAME MARKER THE DOUBLE-LOAD CHECK NEEDS** (§3), so one mechanism serves both:
a skill body is recognisable in the message list, whether you are asking "was it removed?" or "is it
still here?".

**It is the same split the briefing already has** — a child's briefing is in its system message and
survives; the parent's prompt to that child is a user turn and does not.

---

## 5. What the user sees

**`/skills`** — a listing, not an invocation (S7). Which skills were found, from where, and **which
files were skipped and why**. That last part is the whole reason the command exists: a `SKILL.md`
with broken frontmatter is invisible to the model, and nothing else in the app would ever mention it.
**IT IS NOT A REFRESH, AND MUST NOT IMPLY IT IS.** Discovery runs per turn (§3), so an edited skill
is already live on the next turn — a command that looked like the way to apply changes would teach a
user a ritual they do not need. It reports; that is all it does.

**IT NAMES THE WINNING DIRECTORY, AND HAS A FORM FOR WHEN THERE IS NONE.** Shadowing means one
directory supplies everything, so the listing says which — and when every candidate holds only
malformed files there is no winner, and *"`.cxagent/skills/` is in use"* would be a lie. That case
reads as `no skills loaded — 2 files skipped`, with the reasons under it. It is the case a user hits
on their FIRST attempt at writing a skill, so it is the one the command must handle gracefully.

**A row when one loads.** It is a tool call, so it already gets one — it needs a plugin type so it
reads as `▸ ✔ Skill  rtl-aware-development · done` rather than a generic tool row, the same way
`llm_agent` types a worker row today.

**A CHILD'S ROW SAYS WHAT IT LOADED (S11).** `Report` (`Agent.cs:1252`) already builds an expanded
body in two parts — standing `facts` (`type`, `model`, `task`, then the live counters) and the last
six tool calls under them. Skills go in `facts`, after `model`:

```
  type: explore
  model: qwen3.6-35b-a3b
  task: Explore AuthFailureDetector
  skills: rtl-aware-development
  2 turns · 14% ctx · 51s

  ▸ ✔ Read  AuthFailureDetector.cs
  ▸ ✔ Grep  "detect("
```

**THIS IS THE CASE WHERE THE HINT EARNS THE MOST.** A worker's context is invisible by design — the
isolation is the point — so its row is the only account of it anyone gets. Everything else in `facts`
is a standing fact the parent chose; a loaded skill is the child's own decision, and the one thing
shaping its answer that the parent has no other way to learn. By the time the row finishes, the
child's context is gone.

**IT IS NOT A STANDING FACT, THOUGH, AND THE LINE MUST NOT PRETEND OTHERWISE.** `type` and `model`
are fixed before the child starts; skills ACCUMULATE mid-run. So the line appears only once something
is loaded rather than sitting empty, and it grows — the same reason `occupancy` renders as `""` until
usage is first reported instead of showing a misleading `0% ctx`.

**THE LOAD IS ALREADY VISIBLE — AND THAT IS THE ARGUMENT FOR THE LINE, NOT AGAINST IT.** `recent`
(`Agent.cs:1289`) reads `child.Jobs.Jobs`, the child's own buffered panel, so a `load_skill` call
shows up there as an ordinary row the moment it happens. But it is one of the **last six**, and it
scrolls away: a child that loads a skill on turn one and then makes forty calls has no trace of it
left by the time anyone reads the row. The `facts` line is what makes it PERSIST — the same
distinction the block comment already draws, standing facts above, "what it is doing" below.

**THE LIVE ROW READS IT FROM `child.Agent`; THE FINISHED ROW READS A CAPTURED COPY.** `facts` today
reads `child.TypeName` and `child.ModelId` — `SubAgent` properties known before the child starts.
Loaded skills are not: they accumulate inside the child's own agent, so the live row reads them
through `child.Agent`, matching `child.Agent.Context.UsedFraction` at `:1266`.

**THE FINISHED ROW CANNOT DO THE SAME, AND SAYING IT COULD WAS A CONTRADICTION.** This section argues
the child's context is gone by the time the row closes — which is the whole reason the line matters.
A finished row that reads live state would depend on the thing it claims has vanished. So `Report`
CAPTURES the names into the enclosing closure on each tick, exactly as `childTurns` is already
captured (`:1235`), and the completion path reads the captured value. That also makes step 4's gate
genuinely exercisable: clear the child's context, assert the finished row still names the skill.

**The one-second tick then delivers it free.** `Report` is timer-driven, not only
`TurnCompleted`-driven, so a child that loads a skill four minutes into a single turn shows it
immediately without a new signal.

**Several skills: name them while they fit, then count.** `Clip` (`Agent.cs:1937`) is already the
house tool for this. Two names read fine; five need `rtl-aware-development, deployment +3`.

**THE FINISHED ROW NEEDS ITS OWN EDIT — it does not inherit this one.** The completion path builds a
SECOND list, `account` (`Agent.cs:1485`), which re-derives `type`, `model` and `task` and then adds
the token totals; `ProgressBody` is REPLACED, not appended to. So `facts` and `account` must both
learn the line, and a step-4 gate that only checks the live row will pass while the finished row
silently drops it. This is the surface that matters most — a child that has exited is exactly when
*"why did it answer that way?"* gets asked.

**AND `ChildRunReport` (`Agent.cs:1489`) TAKES THE NAMES TOO.** Built in the same block and persisted
to history, its stated purpose is the question *"is planner worth spawning"* — which one session
cannot answer. Skills belong there for the same reason the token totals do: *"planner runs that
loaded the deployment skill cost 3× the ones that did not"* is exactly the cross-session question the
record exists to serve, and the row that scrolls away cannot serve it. One field, added while the
data is already in hand.

**A session-panel line while one is active (S10)**, beside `Agent types` and `MCP`:

```
Skills
  4 available                    ← muted, like MCP's counts
  rtl-aware-development          ← valued: this one is LOADED
```

**THE TWO LINES MUST LOOK DIFFERENT**, or a reader sees five names and cannot tell which are in
force. The MCP section's `Value(...)`/`Muted(...)` convention already draws exactly this distinction.

**THE PANEL RE-DERIVES THE LOADED SET ON A UI CADENCE, and that is a deliberate acceptance.** §3
chose derivation over stored state to prevent drift, but it priced the scan per TOOL CALL, and
`Refresh` runs far more often. A `Content.StartsWith` over a few hundred messages is genuinely cheap
— but this is a written decision, not an assumption: if the panel ever becomes hot, the answer is to
cache at the two points that can change it (a load, a compaction), never to re-introduce a set that
can disagree with the window.

This is the one genuinely new surface. A skill loaded ten turns ago is still shaping behaviour with
nothing on screen saying so — and at compaction it silently stops, which is a behaviour change with
no visible cause.

**THE PANEL IS THE PARENT'S, AND SAYS SO.** With S11 giving each child its own line, a panel that
pooled both would be answering a question nobody asked: a child's skills live and die inside a row
that already reports them, and a child is gone by the next turn while the panel persists. Same split
the spend readout settled — the status bar is the parent, the panel is the aggregate — except here it
resolves the other way, because a skill is not a quantity to total. Attributing one to the wrong
agent is worse than omitting it.

---

## 6. Files — built. Scripts — still deferred (S9)

Most published skills carry more than prose: reference documents, templates, sometimes scripts. Both
reference implementations support it.

**REFERENCE FILES ARRIVED FASTER THAN THIS SECTION PREDICTED, because real skills forced it.** Two
skills added to this repository — `xunit` and `modern-csharp` — link their references the way
markdown does, `[references/patterns.md](references/patterns.md)`, under a heading reading *"Load
References"*. That is a path relative to a directory the MODEL CANNOT SEE: it was being instructed to
load files it had no way to locate. Prose-only was not a stable resting point once a real skill
existed.

**So `load_skill` lists the skill's other files by ABSOLUTE path**, and the model reads the one it
needs with the ordinary file tool. Named rather than inlined: inlining every reference would hand
back the permanent prefix the catalog/body split exists to avoid, and most references go unread on
most tasks. No second capability channel, and the gate that governs every other read governs these —
a project skill's files are inside the working boundary and read silently, a global skill's are in
the config directory and prompt.

**SCRIPTS ARE STILL DEFERRED, and the reason is unchanged.** A skill that ships something meant to be
RUN is code from disk the user did not write. `PermissionGatedPlugin` gates *shell access*, not
*where a command came from* — so a document adds no attack surface a file read did not already have,
while executable content raises a question this design has not answered.

---

## 7. Steps

Each ends with something that works.

| # | Step | Gate |
|---|---|---|
| 1 | Discovery + parse: `.cxagent/skills` → `.agents/skills` → `ConfigDir/skills`, read frontmatter, report what was skipped | Tests over a temp tree: valid; missing frontmatter; missing `description`; project shadows global; **`.cxagent` wins over `.agents` when both hold skills**; **an EMPTY `.cxagent/skills` does NOT shadow a populated `.agents/skills`** (§1); **a LOSER directory's malformed file is still reported** — the contradiction §1 exists to resolve; **both directories malformed → no winner, and `/skills` says so without claiming one is "in use"**; **unknown frontmatter keys (`allowed-tools`) parse rather than skip**; **run from a SUBDIRECTORY of a repo, finding the root's skills**; **run from a subdirectory with NO `.git` anywhere, finding only cwd's** — the `cac2f2e` boundary, and the case that keeps a scratch directory under `~` from loading the home folder's skills; **a `.git` FILE bounds the walk as well as a `.git` directory**; **a symlinked directory resolves**, since that is the `.claude/skills` answer |
| 2 | Catalog in the system prompt, sorted, omitted when empty | Rendered-prompt tests over all four combinations: single/fan-out × parent/child. **With NO skills the heading does not appear at all** — `AppendMcpInstructions` early-returns for exactly this reason (`SystemPrompt.cs:395`), and a suite that only ever runs WITH skills never notices a leaked heading charging a cache miss to users without the feature. **Two builds from unchanged files are byte-identical** — `Directory.EnumerateDirectories` order is filesystem-dependent and unsorted, so the sort is load-bearing and this two-line test is all that stands between S8 and a prefix that churns for free |
| 3 | `load_skill` tool | Loads by name; unknown name errors and lists valid ones; the body lands as a `tool` message with a matching id. **A child can load one too** — the case S4 promises, which now depends on the snapshot being threaded through `SubAgentFactory` and so must be tested rather than assumed. **A second call for the same name returns the ack, not the body** — and **after a compaction that removed the body, the SAME call returns the body again** (§3's flip-back, the subtlest thing here and the easiest to ship broken). **The ack does not itself satisfy the scan** |
| 4 | UI: typed row, `/skills`, panel line, **both child rows** | The row reads as a skill; `/skills` names a skipped file and its reason; **the FINISHED row shows the skill after the live row is gone** — `account` is a second list and a live-row-only test passes while it silently drops the line |
| 5 | Live drive | Does a model with a matching task load the skill unprompted? Record the answer either way — that is the measurement, and it is about ONE model on ONE endpoint. |

**Step 5's question is the honest one.** This design assumes a model that reaches for optional
affordances. Whether any given model does is not something the design can decide, and not a reason to
build it differently — both references ship this shape for model families we cannot test.
