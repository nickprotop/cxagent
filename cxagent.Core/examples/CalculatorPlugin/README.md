# CalculatorPlugin — a managed plugin, end to end

Two tools, four methods, and a permission prompt for adding two numbers.

```bash
dotnet build cxagent.Core/examples/CalculatorPlugin
```

That produces `calculator.dll` and `calculator.plugin.json`. Copy both into your plugins folder and
name it in config:

```json
"plugins": { "calculator": { "file": "calculator.dll", "enabled": true } }
```

cxagent asks once whether to trust the binary, then the model can call `calc_add`,
`calc_multiply` and `calc_divide`.

## Why a calculator

Because nothing about the domain competes for your attention. Every line in `CalculatorPlugin.cs` is
plugin machinery — there is no other kind of line in the file — so reading it teaches you the
contract rather than someone else's problem.

## Why addition asks permission

`calc_add` declares `"gated": true`; `calc_multiply` does not; `calc_divide` declares
`"gated": "dynamic"` and decides per call. One run shows you all three paths.

The third is the one worth watching. `calc_divide` asks only when the divisor is zero — same tool,
same schema, a different answer depending on the arguments. That case is why the callback exists: a
boolean fixed before the call would have to interrupt on every division or none of them.

Gating arithmetic is ridiculous, and that is deliberate. A gate on something genuinely dangerous
teaches you what the danger was. A gate on `2 + 2` can only teach you the mechanism, which is the
part that transfers to the plugin you actually write.

Two things worth noticing when the prompt appears:

**"Always" is offered, and the rule names the plugin.** It stores `plugin calculator tool calc_add`,
not a bare tool name — so it cannot be inherited by some other plugin that later declares
`calc_add`. Whether to grant it is yours: you already approved this binary at load.

**It is not the same question as the load prompt.** That one asked whether to trust the binary at
all, once, showing a hash of its contents. This one asks about a call. Answering the first never
exempts you from the second.

## Settings

`calculator.plugin.json` has no settings of its own, but the plugin reads one:

```json
"plugins": { "calculator": { "file": "calculator.dll", "settings": { "precision": 4 } } }
```

```
no settings          1 + 2 -> 3.00
"precision": 4       1 + 2 -> 3.0000
"precision": "four"  1 + 2 -> 3.00     ← a typo falls back, it does not crash
```

That last line is the part worth copying. `Settings` is whatever JSON the user typed; cxagent checks
that it parses and nothing else, because it cannot know what your plugin expects. Read defensively
about shape, not just absence.

## What each method is for

`Load` returns the manifest — by parsing the sidecar rather than restating it, so there is one JSON
to keep true instead of two that must agree.

`Start` is where a real plugin spawns its backend. A calculator has none, so it is one line; the
shape still matters, because a plugin that starts something lazily on first call is a tool that
fails its first call and works on its second.

`Invoke` does the work. The permission prompt already happened — `"gated": true` in the manifest is
what caused it, not anything in this method.

`Stop` closes what `Start` opened, and must tolerate a backend that is already gone: it runs on the
way down, including after a crash.

## See also

- [`../CalculatorAbiPlugin`](../CalculatorAbiPlugin) — the same calculator in C, across the ABI
- [the plugin guide](../../docs/plugins.md) — sidecars, naming, permission, spawning
