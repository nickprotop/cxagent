# Working in this repository

## Comments

Write them, and write the WHY. This is the opposite of the usual advice, and it is deliberate: most
of the hard-won knowledge in this codebase is about decisions that look arbitrary until you know what
was tried and measured. A comment here should say what failed, or what number came off a real run —
not restate the line below it.

Match the density already in the file you are editing. If the surrounding code carries paragraphs of
reasoning, yours should too.

## Tests

`dotnet test` runs the whole suite in about 3 seconds. Use a 20 second timeout, never minutes: a run
that exceeds it is a hang to diagnose, not a slow test to wait out.

Never run `pkill` here. It matches the harness shell wrapper and kills the session; kill the specific
`dotnet`/`vstest` pids instead.

## Verifying

Confirm the test count, not just the exit code. `dotnet test --filter` against a solution root exits 0
when the filter matches nothing, and a build that compiled nothing exits 0 too. Both have been
mistaken for a pass in this repo.

## Style

- Target framework `net10.0`. The build must end with `0 Error(s)`.
- Commit messages: imperative mood, no trailing period on the subject line.
- Prefer a small explanatory comment over a clever line that needs one.
