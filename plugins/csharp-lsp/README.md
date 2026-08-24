# csharp-lsp

C# code navigation, backed by a language server. Three tools:

| tool | answers |
| --- | --- |
| `csharp_definition` | where the symbol at a position is declared |
| `csharp_references` | every place it is used |
| `csharp_diagnostics` | what the server currently reports for a file |

Definitions and references cross project boundaries: a reference in a test project resolves into the
project under test, provided the server has indexed both.

## Install

`install.sh` does this for you. By hand:

1. Download **`csharp-lsp.zip`** from the [latest release](https://github.com/nickprotop/cxagent/releases/latest).
2. Unpack both files — `csharp-lsp.dll` and `csharp-lsp.plugin.json` — into your plugins folder:
   - Linux / macOS: `~/.config/cxagent/plugins/`
   - Windows: `%APPDATA%\cxagent\plugins\`

The sidecar must travel with the DLL. It is read before the assembly is loaded — it is what the
approval prompt shows and what cxagent reports without running anything — so a plugin missing it
cannot load and cannot be described.

## You also need a language server

The plugin speaks LSP; it does not implement C#. Install one:

```bash
dotnet tool install -g csharp-ls     # the default, needs no configuration
```

OmniSharp works too and needs `-lsp`, or it speaks its own protocol instead.

## Enable it

cxagent reports the plugin at startup as present-but-unconfigured. Then either:

**For one session** — nothing to edit:

```
/plugin load csharp-lsp.dll
```

**To keep it** — in `config.json`:

```json
"plugins": {
  "csharp-lsp": { "file": "csharp-lsp.dll" }
}
```

Either way cxagent asks once whether to trust the binary, showing a hash of its contents. Restart
after editing config: it is read at startup.

## Configuration

Everything is optional. With no `settings` at all the plugin uses `csharp-ls` and says so.

| setting | default | |
| --- | --- | --- |
| `server` | `csharp-ls` | the language server command |
| `args` | none | arguments for it |

```json
"plugins": {
  "csharp-lsp": {
    "file": "csharp-lsp.dll",
    "settings": { "server": "/opt/omnisharp/OmniSharp", "args": ["-lsp"] }
  }
}
```

The same works inline, for trying one without editing anything:

```
/plugin load csharp-lsp.dll { "server": "/opt/omnisharp/OmniSharp", "args": ["-lsp"] }
```

## Notes

**Positions are 1-based** — line 1 is the first line, character 1 the first column — matching how a
human reads a file. The plugin converts to the server's own convention internally.

**The server takes seconds to index** on first use. A definition lookup immediately after startup may
find nothing while a later one succeeds; that is the server warming up, not the plugin failing.

**It is scoped to C#**, and that is one line: the plugin tells the server the document's language,
and it says `csharp`. Pointing it at `gopls` would not fail loudly — the server would parse Go as C#
and quietly return nothing. A general LSP plugin derives that from the file extension; this one
deliberately does not.

**Where you put the plugins folder matters.** Some language servers scan a workspace for projects and
will index a plugin binary sitting inside it — OmniSharp hangs on `.cxagent/plugins` in a repo with
no solution file. The global config folder avoids that entirely.
