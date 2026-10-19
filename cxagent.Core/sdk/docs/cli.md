# The plugin command line

*`cxagent plugin` makes, checks, runs, packs and verifies a plugin — with no session and no model.*

```
cxagent plugin new <name> --lang csharp|c [--dir <folder>]
cxagent plugin check <folder> [--json]
cxagent plugin call <folder> <tool> ['<json>'] [options]
cxagent plugin call <folder> --command <name> [arguments…] [--json]
cxagent plugin pack <folder> [--native <lib>=<rid> …] [--out <folder>] [--description t]
                    [--publisher t] [--license t] [--repository url] [--info file.json|file.yaml] [--json]
cxagent plugin verify <entry.json | plugins.json> [--https-only] [--json]
```

A **folder** here is what a user installs: one `<name>.plugin.json` and the library of the same stem
beside it (`.dll`, `.so` or `.dylib`), plus whatever that library needs. How to use these while you
test is in [testing.md](testing.md); the way to the catalog is in [publishing.md](publishing.md).

## `cxagent plugin new <name> --lang csharp|c [--dir <folder>]`

Writes a working plugin project from the template cxagent carries: a sidecar, one tool with a
per-call gate, the plugin, a README and a test project (C: the C SDK in `cxp/`, a CMake build and a
CTest). It prints the next commands to run.

- `<name>` becomes the folder, the config key, the file stem and every tool's prefix: lowercase
  letters, digits and `-`, starting with a letter, at most 40.
- `--dir` writes somewhere other than `./<name>`.
- **It never writes over anything**: a folder that exists and holds anything is refused.

The same C# template is a `dotnet new` template too: `dotnet new install CxAgent.Plugins.Templates`,
then `dotnet new cxagent-plugin -n <name>`.

## `cxagent plugin check <folder> [--json]`

Every load-time check, without a session:

- the sidecar parses;
- its contract is one this cxagent loads, and its features fit that contract (`"client"` needs 3,
  `"kind"` needs 4);
- the kind fits the file;
- no tool takes a built-in name.

Then it **loads** the plugin, as a session would, without starting it. That load compares what
the code returns with the sidecar. A native plugin's load also covers the plugin host's handshake,
`describe` and version.

It reports every problem, not the first, one line each, naming the field:

```
$ cxagent plugin check weather/
kind: "native", but weather.dll is a .NET assembly.
tools[0].name: 'read_file' is a built-in tool name; a plugin cannot take it.
```

`--json` gives `{"ok": false, "problems": [{"field": "kind", "message": "…"}]}`.

## `cxagent plugin call <folder> <tool> ['<json>'] [options]`

The real lifecycle once: **Load, Start, the gate, Invoke, Stop.**

```
$ cxagent plugin call weather/ search '{"city":"Athens"}' --answer allow
progress 50% fetching
asks: look up the weather for Athens
answered: allow
{
  "success": true,
  "exitCode": 0,
  "errorMessage": null,
  "output": {
    "content": "21°C, clear in Athens"
  }
}
```

**The gate is the session's own decision.** If the call would ask the user, `call` prints the
question. Without `--answer` it stops there, as a session waits at its prompt:
`stopped: a user would be asked — pass --answer allow or --answer deny`. `--answer deny` refuses it
the same way, and the tool never runs.

**Reports print as they happen.** Progress, log lines and streamed text appear while the call runs,
and the result follows once it returns. A submit from the plugin's client prints as
`would start a turn: <goal>`. Nothing reaches a model.

| Option | |
|---|---|
| `'<json>'` | the tool's arguments; must be a JSON object. Default `{}` |
| `--settings '<json>'` / `--settings-file f` | the plugin's `settings` block, as in config.json (one or the other) |
| `--cwd d` | the folder the plugin works in. Default: the current one |
| `--answer allow\|deny` | how the permission question is answered |
| `--timeout s` | stop the call after `s` seconds; the plugin is then stopped and its processes reaped. Default 60 |
| `--command name [args…]` | run a command the plugin declares instead of a tool; everything after the name is its argument |
| `--json` | one JSON object (below), and nothing printed live. Anywhere on the line — after `--command name` too |

### `--json`, for `check` and `call`

Every outcome is one object on stdout, so a script can always parse it:

| outcome | object |
|---|---|
| `check` | `{"ok": bool, "problems": [{"field", "message"}]}` |
| a call | `{"gate": {"display", "alwaysAskable", "subject"} \| null, "answer": "allow" \| "deny" \| null, "stoppedAtGate": bool, "reports": ["progress 50% half", "log [Info] working", "stream text"], "submits": [goal], "result": {"success", "exitCode", "errorMessage", "output"} \| null}` |
| a command | `{"message": string \| null, "status": "reported" \| "changed" \| "refused"}` |
| load failed | `{"error": "load", "problems": [{"field", "message"}]}` — `check`'s shape |
| timed out | `{"error": "timeout", "seconds": n}` |

A usage error (exit 2) writes only to stderr.

## `cxagent plugin pack <folder> [options]`

Writes what a release ships: a zip per build, each zip's sha256, and `<name>.catalog.json`, the
entry that describes them. It runs `check` first and **writes nothing** when `check` finds a
problem — a zip of a plugin that will not load is something every user's install would refuse.

| Option | |
|---|---|
| `--native <lib>=<rid>` | a native plugin's library for one platform, repeated per platform; the rid is one of `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`, `win-x64`, `win-arm64`. Each makes its own `<name>-<rid>.zip`. A managed plugin needs none and makes one `<name>.zip` |
| `--out <folder>` | where the zips and the entry go. Default: the current folder |
| `--description`, `--publisher`, `--license`, `--repository` | the entry's text the sidecar does not hold |
| `--info file.json\|file.yaml` | the same four, from a file |
| `--json` | `{"zips": [{"file", "sha256", "platform"}], "entry": path, "unfilled": [field]}` instead of the summary |

Each text field comes from its option, else the `--info` file, else the project's `Description`,
`Authors`, `PackageLicenseExpression` and `RepositoryUrl`. One still unknown is written as
`<fill in: …>` and listed under "still to fill in"; `verify` refuses it until you do. Each zip's
`url` is left as `<upload URL>`, and the summary ends with the steps left: upload, fill in the URLs,
verify, submit. A plugin with no `README.md` beside it is packed anyway, with a note: the plugin
manager shows the catalog description instead.

## `cxagent plugin verify <entry.json | plugins.json> [--https-only] [--json]`

Checks what a user's install would get. For each entry it downloads every zip, checks its sha256,
unpacks it and holds the sidecar and files up against every field the entry claims. **No plugin code
runs.** Given the whole catalog, it checks every entry.

- `--https-only` refuses a zip at a local path, as the submission bot and CI do. Leave it off to
  check an entry that points at zips on disk before you upload them.
- `--json` gives `{"ok": bool, "problems": [{"entry", "field", "message"}]}`.

## Exit codes

Every `cxagent plugin` command shares them:

- **0**: success. For `call`, this includes stopping at an unanswered or denied gate: a session
  would stop there too.
- **1**: the plugin is at fault. That means a problem `check` found, a load or Start that failed, a
  result whose `success` is false, a tool or command the plugin does not declare (the message names
  the ones it does), a refused command, a command that threw, or a call that overran its timeout.
- **2**: the command line is wrong — a folder that does not exist included.
- **130**: interrupted with Ctrl+C. The plugin is stopped and its processes reaped first.
