# System Prompt — the opencode reference, and what we take from it

**Status:** reference. Records what opencode's prompt does, what we adapted (`SystemPrompt.cs`), what
applies to us NOW but is not yet written, and what waits for features we do not have.

**Source read:** `sst/opencode`, `packages/opencode/src/session/prompt/*.txt` and
`packages/opencode/src/session/system.ts`, cloned and read 2026-08-10.

---

## 1. How opencode assembles a system prompt

Three parts, concatenated per request:

1. **A model-family prompt** — one of nine `.txt` files, chosen by substring match on the model id
   (`system.ts:27` `provider()`): `muse-spark`→meta, `gpt-4`/`o1`/`o3`→beast, `gpt`+`codex`→codex,
   `gpt`→gpt, `gemini-`→gemini, `claude`→anthropic, `trinity`→trinity, `kimi`→kimi, else default.
2. **A runtime `<env>` block** (`system.ts:60` `environment()`) — model id, working directory,
   workspace root, is-git-repo, platform, today's date, plus any configured project references.
3. **Optional blocks** — skills (`system.ts` `skills()`), MCP server descriptions, plan-mode text.

Sizes: default 8.5 KB, anthropic 8.2, gpt 9.3, gemini 15.4, beast 11.1, copilot-gpt-5 14.2.

### The sections in `default.txt`

| Section | Substance |
|---|---|
| (preamble) | Identity; never guess URLs; where to get help and file issues |
| Tone and style | Terse. Fewer than 4 lines unless asked. No preamble/postamble. No emoji. Worked examples (`user: what is 2+2?` → `assistant: 4`) |
| Proactiveness | Act when asked; do not surprise; answer the question before jumping to action |
| Following conventions | Mimic existing style; **never assume a library is available — check**; read neighbouring files and imports first |
| Code style | "DO NOT ADD ***ANY*** COMMENTS unless asked" |
| Doing tasks | Search extensively; implement; **verify with tests, NEVER assume the test framework**; run lint/typecheck when provided; never commit unless asked |
| Tool usage policy | Prefer the Task tool for search (context economy — **needs subagents, see §4**); **batch independent tool calls in one message** (we have this) |
| Code References | Cite as `file_path:line_number` |

### Why nine variants

They encode measured, model-specific quirks — not restyling. Verified by diff (500–800 differing
tokens between families):

- **kimi**: "default to taking action with tools… do not just describe the solution in text."
- **gpt**: "Parallelize tool calls whenever possible… Use `multi_tool_use.parallel`… Never chain
  together bash commands with separators like `echo "====";` as this renders to the user poorly."

Those read as things learned by driving those models and watching them fail.

---

## 2. What we already adapted

`cxagent/Core/Llm/SystemPrompt.cs`, covered by `SystemPromptTests`:

- `<env>` block — working directory, is-git-repo, platform, today.
- Doing the work — use the tools; text in a message changes nothing; search before assuming.
- Following conventions — match the surrounding code; never assume a library is present.
- **Verifying** — do not assume the build/test command; a command that exits 0 has not necessarily
  verified anything; a test run reporting zero tests is not a pass; a filter matching nothing exits
  0; a build that compiled nothing exits 0; say so plainly when you cannot verify.
- Answering — concise; `file_path:line_number`.
- Batching — independent tool calls belong in one turn; three reads is one round trip.
- **No invented URLs** for `http_request` — use one the user gave, or one read from a file.
- **No committing unless asked** — we hand the model `run_shell`, so nothing else stops it.
- **The `/` commands** — named so the model can suggest `/compress`, and so a typed `/help` is
  recognised as something the app handles rather than answered as prose.

The Verifying section exists because a live drive produced a false success: the model ran
`dotnet test --filter …` from a solution root, the filter matched zero tests, it read `exit_code: 0`
as proof and reported "all tests build and pass cleanly" over a file with a compile error.

---

## 3. Applies to us NOW

### Done (2026-08-10)

3.1 **`http_request` guardrail** — was unmentioned while we shipped the tool
(`Core/Llm/WorkerTool.cs:22`). Now: never invent a URL.

3.2 **The `/` commands** — four exist (`UI/SessionCommands.cs:27-30`) and the model knew of none.

3.3 **Batching independent tool calls** — every turn is a round trip to a local model.

3.4 **Commit discipline** — "do not commit unless the user asks".

3.6 **Explain a shell command before running it.** opencode: "When you run a non-trivial bash
command, you should explain what the command does and why you are running it." It matters MORE here:
`run_shell` goes through a permission prompt that shows the command truncated and carries no reason,
so the user approves a string they cannot fully read. Observed on the ConsoleEx drive.

### Still open

3.5 **Comments policy.** opencode forbids comments unless asked. **We want the opposite**: this
codebase's convention is heavy explanatory comments carrying the reasoning behind a decision. An
agent editing here should match that, and a generic "follow the conventions" may not be enough
against a model trained on the common advice. Not yet written because it is a claim about THIS repo
rather than about agents generally — it belongs in a per-project instruction file (an AGENTS.md
equivalent), which we do not read yet.

---

## 4. Waits for features we do not have

- **Subagent delegation — THE NEXT PHASE.** opencode's line is "When doing file search, prefer to
  use the Task tool in order to reduce context usage." That is a context-economy instruction: a
  search that reads twenty files into a subagent's context and returns three lines costs the parent
  three lines, not twenty. It needs three things from the prompt, and they should land with the
  feature rather than after it:
    1. **When to delegate** — broad search and survey work, where the finding is small and the
       reading is large. Not a single known file: `read_file` is cheaper than a whole agent.
    2. **What the subagent gets** — opencode's kimi prompt is explicit that "a newly created
       subagent does not automatically see your current context", so the delegating prompt must
       carry everything needed. Ours will be too: `Agent` owns its own `AgentContext` by
       construction (Plan 1), so a sub-agent starts empty by design.
    3. **What comes back** — a result, not a transcript. The parent's context is the thing being
       protected; returning the subagent's turns would defeat the delegation.
- **Skills / MCP** — `system.ts` appends a block per available skill and MCP server. Nothing to
  describe until those exist.
- **Plan mode** — `plan.txt`, `plan-mode.txt`, `plan-reminder-anthropic.txt`. We deleted planning
  deliberately (Plan 1); this returns only if a read-only mode is ever wanted.
- **Project references** — the `<available_references>` block for extra readable directories.

---

## 5. Per-model variants — the position

`SystemPrompt.Build` takes the model id and ignores it, so a variant is one branch rather than a
refactor. We ship one prompt because the app can reach three provider kinds
(`anthropic`, `ollama`, `openai-compatible`) and only one has been driven.

**The rule: a variant is earned by a measured failure, not anticipated.** opencode's GPT line about
`multi_tool_use.parallel` and bash `;` chaining is clearly the residue of watching GPT do the wrong
thing. Writing a claude variant now would be inventing quirks. `SystemPromptTests
.Build_IsTheSamePromptForEveryModel_ForNow` pins this as a decision rather than an oversight.

---

## 6. Proactiveness — considered, not copied

opencode's section reads: *"You are allowed to be proactive, but only when the user asks… if the user
asks you HOW to approach something, answer their question first, and not immediately jump into taking
actions."*

**Not added, and the reason is specific to this project.** Our prompt says `USE THEM: text in a
message changes nothing` — which exists because the measured failure here was the model DESCRIBING
edits instead of making them: "a perfect edit, right tabs, right house style… and then nowhere to put
it, so it emitted the edit as prose". Their balance-seeking language pulls directly against that, and
on a local model the weaker instruction tends to win.

The half worth having is their point 3 — *"do not add a code explanation summary unless requested;
after working on a file, just stop"* — and our Answering section already covers it ("skip preamble",
"be concise").

If a drive ever shows the agent acting when it should have answered, revisit. Right now the evidence
points the other way.

---

## 7. AGENTS.md — a feature, not a prompt line

opencode reads project instructions and prepends them (`session/instruction.ts:55-130`): a global
`~/.config/opencode/AGENTS.md` plus `findUp` from the working directory for `AGENTS.md`, `CLAUDE.md`,
`CONTEXT.md`. First project-level match wins, deliberately — "so we don't stack AGENTS.md/CLAUDE.md
from every ancestor".

**Why it is not in our prompt yet:** it is not prompt text, it is a file-discovery feature. And it is
the right home for the one instruction we know we want and cannot put in a universal prompt: this
codebase wants HEAVY explanatory comments, where opencode's prompt says "DO NOT ADD ***ANY***
COMMENTS". That is a claim about THIS repo — writing it into the universal prompt would be wrong for
anyone pointing cxagent at a different tree.

Small when we do it: read one file, prepend it below the system prompt, say in the prompt that
project instructions may follow and take precedence. `findUp` with first-match-wins is the part worth
copying exactly — stacking every ancestor's file is how the context fills with stale advice.

---

## 8. The model id stays OUT of the prompt

opencode's `<env>` opens with *"You are powered by the model named ${model.api.id}. The exact model ID
is ${model.providerID}/${model.api.id}"*. We take the model id in `SystemPromptContext` and discard it
(`_ = ctx.ModelId`).

**The system message is the prompt-cache prefix.** It sits at position 0 and is re-sent verbatim every
turn; cached reads are roughly a tenth of the input price, and any change to that prefix throws the
cache away for the rest of the conversation — the same argument `AgentContext`'s own doc makes about
why the context must not be rebuilt per goal. Putting a value in there that varies between models, for
no gain to the model's reasoning, is paying that cost for nothing.

The date is the other field that could drift, and does not: the system message is built ONCE (the
agent inserts it only when no system message is present) and is pinned above compression, so a session
running past midnight keeps its original prefix. `Build_IsDeterministic_ForTheSameEnvironment` and
`Build_NeverPutsTheModelIdInThePrompt` hold both properties.

---

## 9. Deliberately not copied

- **~6 KB of capability description** for WebFetch/Task/skills/MCP we do not have — describing tools
  the model cannot call is worse than silence.
- **The verbosity block.** One directive plus SIX worked examples (`user: what is 2+2?` →
  `assistant: 4`; `is 11 a prime number?` → `Yes`), ~1,900 chars — the largest single chunk of their
  prompt, roughly 22% of it. Our one-line "be concise" carries most of the effect at 1% of the cost.
  The prompt is re-sent in full every turn, so this is a recurring charge against a local window, not
  a one-off. Revisit only if measured verbosity justifies it.
- **The issue-reporting URL and product self-description.** opencode is a product with users to
  route; this is not.
