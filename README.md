# CXAgent

<div align="center">

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Platform-Linux%20|%20macOS%20|%20Windows-orange.svg)]()

</div>

**A terminal AI coding agent built on [SharpConsoleUI](https://github.com/nickprotop/ConsoleEx).**

<div align="center">

### If you find CXAgent useful, please consider giving it a star!

It helps others discover the project and motivates continued development.

[![GitHub stars](https://img.shields.io/github/stars/nickprotop/cxagent?style=for-the-badge&logo=github&color=yellow)](https://github.com/nickprotop/cxagent/stargazers)

</div>

Give it a goal in plain language. It reads your files, works out what to change, and changes them —
in one context, with the real bytes in front of it. Anything outside your working folder asks first.

Bring your own model: Ollama, any OpenAI-compatible endpoint, or Anthropic.

**Say it. Watch it work.**

## Quick Start

**Option 1: One-line install** (Linux/macOS, no .NET required)
```bash
curl -fsSL https://raw.githubusercontent.com/nickprotop/cxagent/master/install.sh | bash
cxagent
```

**Windows** (PowerShell)
```powershell
irm https://raw.githubusercontent.com/nickprotop/cxagent/master/install.ps1 | iex
```

**Option 2: Build from source** (requires .NET 10 SDK)
```bash
git clone https://github.com/nickprotop/cxagent.git
cd cxagent
./build-and-install.sh
```

On first run a setup wizard asks for a provider and model. Configuration is written to
`~/.config/cxagent/config.json` — it is never stored in the repository.

## What it does

Run it in the folder you want to work in, and type what you want:

```
add an overflow guard to EstimateOutputLength in HexEncoder.cs
```

It reads the file, finds the method, and edits it in place — matching the surrounding indentation
and style, because it is looking at the actual text rather than reconstructing it from memory.

### Tools

| Tool | Purpose |
|------|---------|
| `read_file` | Read a file, or a line window of it (`offset`/`limit`) |
| `write_file` | Write a whole file |
| `replace_in_file` | Replace an exact passage, leaving the rest untouched |
| `list_files` | List files under a path, by glob |
| `search_files` | Find text in files, literal or regex |
| `run_shell` | Run a command |
| `http_request` | Fetch a URL |

### Permissions

Reading and writing inside the working folder is free. Anything else — a path outside it, a shell
command — stops and asks, with **Allow once**, **Always allow**, or **Deny**. "Always" is remembered
per folder, because a folder is a project.

### Fan-out mode

`cxagent --fan-out` plans a DAG of jobs instead, running several workers in parallel with the
dependency graph as the record. Useful when work genuinely decomposes — reviewing many files at once.
Single-agent is the default: for read-then-edit work, one context holding the real bytes is more
reliable than coordinating several that do not.

## Configuration

`~/.config/cxagent/config.json`:

```json
{
  "providers": {
    "local": { "kind": "ollama", "model": "qwen3:32b", "baseUrl": "http://localhost:11434" }
  },
  "defaultProvider": "local"
}
```

Provider kinds: `ollama`, `openai-compatible` (requires `baseUrl`), `anthropic`.

Optional `orchestrator` block:

| Key | Default | Meaning |
|-----|---------|---------|
| `maxWorkerTurns` | 200 | Cap on tool-loop round-trips for one goal |
| `goalTokenBudget` | unset | Stop a goal past this many tokens |
| `copilot` | false | Ask before running a freshly planned goal |

## Keys

| Key | Action |
|-----|--------|
| `F2` | New goal |
| `F4` | Chat |
| `F1` | Help |
| `F5` | Settings |
| `F6` | Diagnose a failed job |
| `Ctrl+Q` | Quit |

## Uninstall

```bash
curl -fsSL https://raw.githubusercontent.com/nickprotop/cxagent/master/uninstall.sh | bash
```

```powershell
irm https://raw.githubusercontent.com/nickprotop/cxagent/master/uninstall.ps1 | iex
```

## The cx family

[cxfiles](https://github.com/nickprotop/cxfiles) · [cxpost](https://github.com/nickprotop/cxpost) ·
[cxlog](https://github.com/nickprotop/cxlog) · [cxnet](https://github.com/nickprotop/cxnet) ·
[cxgpu](https://github.com/nickprotop/cxgpu) · [cxshell](https://github.com/nickprotop/cxshell)

## License

MIT — see [LICENSE](LICENSE).
