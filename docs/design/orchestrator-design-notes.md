# P8 design notes — orchestrator as a fluent, adapting path

> Moved here from `.superpowers/sdd/p8-design-notes.md` on 2026-08-14, when that plan's scratch
> workspace was cleared. Kept because it records findings from live drives that are not
> reconstructable from git history.

Rewritten 2026-08-04 after P7/P7b's live drives produced concrete failure evidence. The earlier
round-based sketch is superseded; what it got right is carried forward and marked as such.

---

## The shape the user asked for

> "each job on finish expose to orchestrator, orchestrator re runs, can modify the jobs,
> add/remove/update. the orchestrator is a fluent path adapting on jobs."

Per-JOB adaptation, not per-round. The orchestrator is consulted when a job finishes, sees what it
produced, and may extend, edit, or prune the rest of the plan before more work happens.

On isolation:

> "jobs does they cross talking? or via orchestrator? I think they must be isolated, so those bugs
> you chase now will not be present."

**Correct, and the evidence is this session's bug list.** On data movement:

> "the idea of passing {{}} instead of real tokens up and down is nice."

Together these give the rule the whole design turns on:

> **CONTROL flows through the orchestrator. DATA flows by REFERENCE between jobs, and only
> materialises where it is actually consumed.**

---

## Why isolation matters — every bug this session had one root cause

Four failures chased in one afternoon:

| Observed | Root cause |
|---|---|
| A file job wrote the literal `{{review}}`; goal reported SUCCESS | no substitution existed |
| Next run wrote `{output-2}` | model invented syntax; nothing had documented one |
| `{{read_app_paths.content}}` failed | model PARAPHRASED its own job id instead of copying it |
| Final job never wrote the file at all | model read "write a summary to <path>" as agent work, not a file job |

Every one is the orchestrator being asked to **predict the entire data-flow graph up front, in one
shot, before seeing a single byte of output.** `{{job.key}}` is a workaround for not being able to
look. Per-job adaptation removes the need to predict: plan `review-A`, SEE its output, then plan the
write job knowing what actually exists.

**Jobs must never call each other.** A job takes parameters, produces a `JobResult`, and ends. It has
no channel to another job. Anything else reintroduces exactly the coupling above, with less
visibility.

---

## The reference/materialisation rule (the user's `{{}}` point, made precise)

Passing a 4,586-byte review THROUGH the orchestrator costs those tokens twice — once reported, once
re-emitted into the next job's params. So:

- **Between jobs:** references stay symbolic. `{{review.content}}` is resolved by `JobExecutor` at
  execution time (P7b Task 3), never by the orchestrator. Large payloads move job→job without ever
  entering the orchestrator's context.
- **To the orchestrator:** it sees a DIGEST — id, name, type, role, state, duration, exit code, and a
  bounded head+tail of output with a VISIBLE `[... N bytes elided ...]` marker, plus the log path.
  Errors are NEVER truncated. It can pull more with `get_job_output(job_id, offset, limit)`.
- **When re-planning:** the orchestrator emits `{{job.key}}` references, NOT the content it just
  read. It saw 400 bytes of a 4KB review; the next job gets all 4KB, by reference, without the
  orchestrator reproducing a single byte.

This is how Claude Code behaves: a tool result is summarised into context, the full artifact stays on
disk, and a follow-up reads it if needed.

**So P7b's `{{job.key}}` is NOT superseded by P8 — it is what makes P8 cheap.** Keep it, keep its key
conventions identical (`content`, `stdout`, `exit_code`), and let both features read the same
`JobResult.Output`.

---

## Consulted per job, but allowed to say nothing

The obvious risk: five jobs become five orchestrator turns, each carrying accumulated context, and a
parallel fan-out serialises behind the consult.

Design answer — **a cheap "continue" is the default**. On each completion the orchestrator may reply:

- `continue` — no change. Cheap, no re-planning, execution proceeds. Expected to be the common case.
- `modify` — add / remove / update jobs via `DagModifier.TryApply` (already exists, P6).
- `finish_goal` — terminal, with a summary.

Full re-planning cost is paid only when something actually surprises it.

**Parallelism must survive.** A consult about job A must not block jobs B and C that are still
running. The consult is about the DAG's future, not a barrier. See open question 1.

---

## Carried forward from the round-based sketch (still valid)

- **Extend the LIVE dag** via `DagModifier.TryApply` — stable job ids, one continuous panel view,
  prior outputs stay addressable. More important now: per-job adaptation means many small edits.
- **Termination, all three:** an explicit `finish_goal` tool, a max-consults cap in config.json, and
  `GoalTokenBudget`. Hitting a cap ends the goal and SAYS SO — never reported as success.
- **Feedback shape:** bounded digest + visible elision marker + log path + `get_job_output`. Errors
  never truncated.
- **User control:** runs automatically, each decision shown in chat, cancellable. No per-step
  confirmation.
- **Mid-run introspection** (`list_jobs`, `get_job_status`, `get_job_output`) — a tool-surface task,
  not plumbing: `Job.Progress`/`ProgressMessage`, continuously-written logs, `ResourceSnapshot` and
  the full `JobDag` all already exist and are wired.

---

## Hard constraints — earned, not theoretical

1. **QUIESCENCE IS NOT OWNERSHIP.** `DagScheduler.WaitForQuiescenceAsync` is a point-in-time sample,
   not a lease. P6 Task 11 round 2 proved it: capturing a scheduler was not enough, because the swap
   logic still disposed it once quiescent. P8 re-enters live DAGs constantly — same hazard,
   amplified. `GoalRunner` now tracks every scheduler in `_allSchedulers` and disposes only in
   `Dispose()`. Do not reintroduce eager disposal.
2. **`DriveAsync` FORBIDS OVERLAPPING DRIVES** (throws when `_inFlight > 0` or anything is Queued).
   Each adaptation must wait for quiescence or serialise. A fire-and-forget re-entry faults into an
   unobserved Task — that was P6's C2.
3. **ANY value that parses but is never consumed WILL rot.** Hit four times: the orchestrator budget,
   the resource sink, `llmAgent.routing`, and a job-level `role` that `PlanCompiler` silently
   dropped. Whatever P8 adds to config or schema needs a test asserting it REACHES the runtime.
4. **Every new provider call path must record into the same `TokenLedger`.** `JobDiagnoser` recorded
   nowhere until P6's I3; per-job consultation multiplies calls, so an unmetered path makes the cap
   fiction. The consult itself is a provider call — meter it.
5. **Errors must never be silent.** P6's C1 was a silent no-op that survived a green suite. Every path
   that declines to act (cap hit, budget exhausted, orchestrator returned nothing) must report.
6. **THE MODEL CANNOT USE WHAT IT ISN'T TOLD ABOUT — and it will invent something plausible in the
   gap.** Hit three times: plugin types (P6 T0), the role enum (P7 T10), reference syntax (P7b T4).
   Every P8 tool needs a WORKED EXAMPLE in its schema, not a prose rule. Proven this session: prose
   alone left `role` unset on 3 of 4 jobs; adding one correct job object fixed it.
7. **TREAT A FAILURE AS OUR BUG FIRST.** Explicit user instruction, and right twice in one afternoon:
   `role` omissions were blamed on the local model, then found to be (a) `PlanCompiler` discarding
   the field and (b) weak schema wording. Both ours.

---

## DECIDED WITH THE USER (2026-08-04) — these are settled, not open

1. **Siblings keep running; edits QUEUE and apply at quiescence.** A consult about A fires
   immediately and its digest states what is still in flight, but any `DagModifier` edit is held
   until nothing is running. Preserves the fan-out AND respects constraint 2 — the no-overlapping-
   drives contract that P6 spent two review rounds on.

2. **Consults BATCH near-simultaneous completions.** One consult carries every job finished since the
   last, so a 3-way fan-out costs one call with three digests rather than three calls. Cheaper, and
   the orchestrator sees them together so it can reason ACROSS them (e.g. "both reviews agree").

3. **Observe freely; CANCEL AND SKIP ARE ALLOWED — but they go through the same queue as edits.**
   The user pushed back on observe-only with "isnt claude do this?" and was right: Claude Code does
   kill a hung background process (run_in_background + KillShell). The distinction is not capability
   but blast radius — killing a shell leaves nothing mid-transaction, whereas cancelling a cxagent
   job is a STATE MUTATION on a live DAG, which is exactly constraint 2's hazard.
   Resolution: cancel/skip is a first-class orchestrator capability, routed through the SAME
   quiescence queue as DAG edits, not a special case that bypasses the contract. For the case that
   matters — a job hung on a network call — this is near-identical in effect, because a stuck job IS
   the quiescence boundary: nothing else is transitioning. The queue only delays a cancel while other
   jobs are actively finishing, which is precisely when a mid-drive mutation must not land.
   STILL DEFERRED, deliberately: whether the orchestrator may cancel a job that is MAKING PROGRESS
   (reporting via ReportProgress, burning CPU) versus one visibly stuck. Killing a slow-but-working
   job is a different judgement from killing a hung one; decide it after observing real behaviour.

4. **Loop guard: BOTH caps.** A per-job edit cap (the orchestrator may modify any given job at most N
   times before it is left Failed and reported — mirrors the existing MaxRetries convention so there
   is one mental model for "this job has had enough attempts"), AND a global per-goal consult cap as
   the backstop for loops the per-job cap cannot see, such as two jobs alternately editing each
   other's successors. Hitting either ends the goal and SAYS SO — never reported as success
   (constraint 5).

## Still open — decide in the plan

- **Does the digest include the job's PARAMETERS?** Useful for the orchestrator to spot its own
  mistake (e.g. the paraphrased-id failure), but costs tokens on every consult.
- **What counts as "near-simultaneous" for batching?** A time window, a quiescence boundary, or
  simply "everything that finished since the last consult"? The last is simplest and needs no timer.

---

## An observed failure P8 should fix by construction

The last fan-out drive (a82b279, all three roles correctly `reviewer`) still did not write its file:
the model planned a final `llm_agent` job that GENERATED the summary, and no `file` job to write it.
It read "write one combined summary to `<path>`" as the agent's task.

No prompt wording fully prevents this — it is a planning-shape error made before any output exists.
Per-job adaptation fixes it structurally: the orchestrator sees the summary job finish, sees the
requested path still has nothing at it, and appends the write job then. That is the clearest single
argument for this design over better up-front prompting.

---

## Prerequisite status

- I3 (diagnosis spend unmetered): FIXED in d402060, confirmed live (1,739 → 5,467 tokens).
- Scheduler re-entry safety: FIXED in cfc6b9e.
- `{{job.key}}` references: LANDED in P7b (5996025, 6491199, 0fbd5b3, 02543b3) and drive-verified
  end to end — the data-movement half of this design already works.

All P8 prerequisites are met.
