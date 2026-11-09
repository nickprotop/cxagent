# Installing and running cxagent

How to install it, what the first run asks, where the configuration lives, the command line, and how
to remove it. [COMMANDS.md](COMMANDS.md) covers what you type once it is running, and
[CONFIG.md](CONFIG.md) every key of the configuration.

## Installing

Linux and macOS, no .NET required:

```bash
curl -fsSL https://raw.githubusercontent.com/nickprotop/cxagent/master/install.sh | bash
cxagent
```

### Windows, the .NET tool, or building from source

**Windows** (PowerShell)
```powershell
irm https://raw.githubusercontent.com/nickprotop/cxagent/master/install.ps1 | iex
```

**As a .NET tool** (requires the .NET 10 SDK or runtime)
```bash
dotnet tool install -g cxagent
cxagent
```

Updates with `dotnet tool update -g cxagent`. Unlike the binaries above this one is
framework-dependent: it uses the .NET you already have rather than carrying its own.

**Build from source** (requires .NET 10 SDK)
```bash
git clone https://github.com/nickprotop/cxagent.git
cd cxagent
./build-and-install.sh
```

## The first run

**You do not need to write any configuration to start.** On first run (with no config file, or none
that names a working provider) the configuration dialog opens on a choice of where your model runs
(Anthropic, OpenRouter, OpenAI, Ollama, llama.cpp, or any OpenAI-compatible endpoint), lists the
models that endpoint serves, and writes the file for you. It is never stored in the repository.
`F8` opens the same dialog afterwards; [Configuration](#configuration) says what a change does and
where the file lives.


## Configuration

**`F8` opens the configuration dialog** (general settings, providers, MCP servers and sub-agent
types), and a saved change applies with no restart: the general settings to tabs opened after it, the
rest at once. Changes the model cannot see (a rotated key, the auto-mode
reviewer, where an agent type runs) apply in place and keep every tab's prompt cache; one that would
re-send a conversation (a tab's model changing) asks first whether to apply it now, after compressing,
at that tab's next clear or compaction, or only to new tabs.

The file lands in `~/.config/cxagent/config.json` on Linux and macOS (`$XDG_CONFIG_HOME` when set),
and `%APPDATA%\cxagent\config.json` on Windows. The same directory holds your session
history and logs.

Written by hand, `~/.config/cxagent/config.json`:

```json
{
  "providers": {
    "local": { "kind": "ollama", "model": "qwen3:32b", "baseUrl": "http://localhost:11434" }
  },
  "defaultProvider": "local"
}
```

Provider kinds: `ollama`, `openai-compatible` (requires `baseUrl`), `anthropic`.

Set `contextWindow` on a provider when you know it: it is the denominator for the occupancy
readout and the trigger for compaction. Left unset, cxagent asks the endpoint at startup.

Optional blocks: `agents` for sub-agent types, `mcp` for MCP servers, `plugins` for tools loaded from
disk, `orchestrator` for caps.

**[CONFIG.md](CONFIG.md) is the full reference**: every block, where the file lives on each OS, what
else cxagent keeps in that directory, and how `AGENTS.md` / `CXAGENT.md` / `CLAUDE.md` are resolved.
[`config.sample.json`](config.sample.json) documents every key inline, including what each one's
absence means.

**[ROADMAP.md](ROADMAP.md)** is what is next.


## Command line

```
cxagent                          # start in the current folder
cxagent -c                       # continue the most recent session here
cxagent -r <id>                  # continue that one, by id
cxagent --resume                 # start with this folder's sessions listed, to pick from
cxagent --sessions [all]         # list earlier sessions and exit
cxagent --model <instance>       # open on a specific entry from `providers`
cxagent --mode <single|fan-out>  # start in that mode
cxagent --config-dir <dir>       # use this directory instead of the usual one
cxagent --theme <name>           # start in a named theme
cxagent --mock                   # a fake provider, to try the UI
cxagent -p "<goal>"              # run one goal without the TUI; see SCRIPTING.md
cxagent -v, --version
cxagent -h, --help               # the options, also shown when an argument is wrong
```

The options follow Claude Code's names where it has one, so a script written for `claude -p` runs
with `cxagent` swapped in.

**`--config-dir`** points cxagent at a different config directory: the one holding `config.json`,
the session database and the logs. The path is used AS the directory, with no `cxagent` folder
appended, unlike `XDG_CONFIG_HOME`. It is the quickest way to try something against a clean setup
without touching your own:

```bash
cxagent --config-dir /tmp/scratch
```

**`--theme`** starts in a named theme. Names are case-insensitive, and one that matches nothing falls
back to cxagent's own rather than refusing to start: a colour scheme is not worth failing over.
`--help` lists what is installed. A `theme` key in `config.json` does the same thing for every run;
the argument wins when both are present.


## Uninstall

However you installed it:

```bash
# the install script (Linux/macOS)
curl -fsSL https://raw.githubusercontent.com/nickprotop/cxagent/master/uninstall.sh | bash

# the .NET tool
dotnet tool uninstall -g cxagent
```

```powershell
irm https://raw.githubusercontent.com/nickprotop/cxagent/master/uninstall.ps1 | iex
```

Your configuration and session history are left alone. They live in
`~/.config/cxagent` on Linux and macOS (`$XDG_CONFIG_HOME/cxagent` when that is set), and
`%APPDATA%\cxagent` on Windows. Delete that directory to remove them too.
