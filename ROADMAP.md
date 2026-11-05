# cxagent — Roadmap

What is next. See [README](README.md) for what cxagent does and
[cxagent.Core](cxagent.Core/README.md) for the library.

## Next

- **Checkpoints and rewind** — roll the working tree back to before a turn. Today there is no undo
  inside cxagent; git is the safety net, and an edit the model made has to be reverted by hand.
  Not built yet because most edits an agent makes go through the shell (`sed`, a Python script, a
  formatter), not the edit tools, so a rewind that tracks only those would promise an undo it cannot
  deliver; and git already undoes what is in the folder.
- **Hooks in plugins** — a plugin folder bringing its own `hooks/hooks.json`, approved with the plugin.
- **Sandbox on macOS** — Linux commands run inside bubblewrap; macOS would need a Seatbelt profile.
  Built when someone asks for it.
- **Network allowlists in the sandbox** — today the network is on or off; letting a contained command
  reach only a package registry or a git host needs a proxy it is forced through.
- **A web front end** — the reason the extraction was worth doing.
- **Skill scripts** — a skill that runs, not just one that is read.
