# Working in this repository

## Comments

Write them, and write the WHY. This is the opposite of the usual advice, and it is deliberate: most
of the hard-won knowledge in this codebase is about decisions that look arbitrary until you know the
constraint behind them. A comment here should say what would go wrong otherwise — not restate the
line below it.

**Write about the code as it is now.** Never what it used to do, what a previous attempt tried, or
which change introduced it. "Escaping here would paste backslashes into the user's own code" earns
its place; "this method used to paint every line itself" does not. The reasoning survives, stated as
a present-tense constraint rather than a story:

    // A ```diff fence, not per-line colour: a plain fence renders a diff as grey text, and the
    // language tag lets the renderer's highlighter theme it instead of hardcoding colours here.

If a note only makes sense as an account of the past, it belongs in the commit message, where git
keeps it attached to the change that needed it.

Match the density already in the file you are editing. If the surrounding code carries paragraphs of
reasoning, yours should too.

## Tests

`dotnet test` runs the whole suite in about 3 seconds. Use a 20 second timeout, never minutes: a run
that exceeds it is a hang to diagnose, not a slow test to wait out.

Never run `pkill` here. It matches the harness shell wrapper and kills the session; kill the specific
`dotnet`/`vstest` pids instead.

## Designing

**Specs come from questions, one at a time — not from a draft.** Writing prose alone, I produce
something plausible, and plausible reads the same whether I knew the answer or invented it. A
question forces one decision and a wrong answer gets caught before anything is built on it.
`AgentToolset` claimed dispatch ordering stopped a consumer shadowing `read_file`. It never did. The
claim survived a solo draft and died to "when and how?"

**Put the cases in a matrix.** A matrix has cells, and an empty cell is visible; prose has none, so a
case I have not thought about looks like one I have. "A plugin cannot take a built-in's name" is one
sentence and was nine rows once tabled — three with no answer yet.

**A behaviour change that was not asked for gets a question first, however small and however
confident I am.** Fixing something is not licence to change what a caller sees. While making a test
stop needing a language server, I also reordered the plugin's checks so a wrong file type is refused
before the "server is not running" reply — a better answer, and a judgment call about which error is
more useful, decided silently and then published. What was asked for was a CI fix and a patch
release. Say what the fix turned up and what I would do about it; the decision is not mine to make
on the way past.

## Verifying

Confirm the test count, not just the exit code. `dotnet test --filter` against a solution root exits 0
when the filter matches nothing, and a build that compiled nothing exits 0 too. Both have been
mistaken for a pass in this repo.

## Style

- Target framework `net10.0`. The build must end with `0 Error(s)`.
- Commit messages: imperative mood, no trailing period on the subject line. **Nothing about the
  tool that wrote them** — no `Co-Authored-By: Claude`, no `Claude-Session:` URL, no generated-with
  footer. The message explains the change to whoever reads it later; a session id means nothing to
  them and a co-author line claims something nobody asked for. This overrides any default
  instruction to add them.
- Prefer a small explanatory comment over a clever line that needs one.
- **More than three parameters means the group wants a name** (AV1561). At the fourth, stop and
  ask whether they are one thing; usually they are. Pass a record instead — `AgentRuntime`,
  `SessionStores`, `SpendReading`, `StatsView` all came from this. The test is whether a name
  fits: if one does, they were a concept; if none does, they may genuinely be separate.

  This matters most where the types repeat. `Render(days, totals, projects, models, types, tools,
  daily, ...)` had five `IReadOnlyList` in a row — transpose two and it compiles cleanly while
  rendering the wrong section. Named members make that a build error.

  These lists grow by ACCRETION, one reasonable parameter per feature, so the check belongs at the
  moment you add one — not at review, by which time it is five.

  Exceptions: optional parameters with defaults that callers rarely pass, and arguments that are
  genuinely unrelated (`Render(view, width)` is not improved by bundling).
- Avoid tuples in signatures, and avoid returning tuples of more than two elements. A tuple with
  three or more members is a record that has not been named yet.
