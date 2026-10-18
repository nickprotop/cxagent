# cxagent — Roadmap

What is next. See [README](README.md) for what cxagent does and
[cxagent.Core](cxagent.Core/README.md) for the library.

## Next

- **Checkpoints and rewind** — roll the working tree back to before a turn. Today there is no undo
  inside cxagent; git is the safety net, and an edit the model made has to be reverted by hand.
- **Image input** — paste or drop a screenshot into the composer and have the model see it. The
  system clipboard reaches the app on every desktop now, which is half of it.
- **Web search** — a tool that finds pages. `web_fetch` reads a URL the model already has; nothing
  finds one it does not.
- **Hooks** — commands run on events the user chooses: before or after a tool call, when a turn
  ends, when a session starts. Today the only way in is a plugin, which is code.
- **Several edits in one call** — `replace_in_file` changes one exact passage; a multi-edit or patch
  tool would let a refactor across a file be one call and one permission, not one per passage.
- **A worktree per sub-agent** — background agents that write share the one checkout, so two of
  them on the same files is the user's to avoid. A git worktree each would let them work apart and
  be merged back.
- **Sandboxed shell** — commands run as the user, gated by permission rules but not contained; the
  same thing stands between this loop and running tools in parallel.
- **Publishing a plugin from outside this repository** — the catalog and the picker that installs
  from it ship (`/plugin browse`, F2); adding an entry still takes a change to
  [`plugins.json`](plugins/plugins.json) here.
- **A web front end** — the reason the extraction was worth doing.
- **Skill scripts** — a skill that runs, not just one that is read.
