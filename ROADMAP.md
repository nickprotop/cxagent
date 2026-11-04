# cxagent — Roadmap

What is next. See [README](README.md) for what cxagent does and
[cxagent.Core](cxagent.Core/README.md) for the library.

## Next

- **Checkpoints and rewind** — roll the working tree back to before a turn. Today there is no undo
  inside cxagent; git is the safety net, and an edit the model made has to be reverted by hand.
- **Hooks** — commands run on events the user chooses: before or after a tool call, when a turn
  ends, when a session starts. Today the only way in is a plugin, which is code.
- **Sandboxed shell** — commands run as the user, gated by permission rules but not contained; the
  same thing stands between this loop and running tools in parallel.
- **A web front end** — the reason the extraction was worth doing.
- **Skill scripts** — a skill that runs, not just one that is read.
