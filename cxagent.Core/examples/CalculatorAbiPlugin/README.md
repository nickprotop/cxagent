# CalculatorAbiPlugin — the same calculator, in C

One file, seven exported functions, no JSON library.

```bash
cc -shared -fPIC -o calculator.so calculator_abi.c
```

Copy `calculator.so` and `calculator.plugin.json` into your plugins folder and name the `.so` in
config, exactly as you would a managed plugin.

## Write an ABI plugin when your language cannot be loaded managed

C, Rust, Go, C++. If you are writing C#, write a [managed plugin](../CalculatorPlugin) — an ABI
plugin in .NET needs NativeAOT, which strips the reflection `System.Text.Json` relies on, so every
payload then needs a hand-written `JsonTypeInfo`. It is more code to reach a place the host would
have loaded directly.

What you get for the boundary: a crash in here fails the call instead of taking cxagent down.

## No JSON library, on purpose

Every string this plugin returns is a literal or one `snprintf`. Arguments are read with `strstr`
and `atof` — enough for two numbers, and wrong the moment an argument is nested.

A plugin answering real questions wants a parser. One teaching the boundary wants you to see the
exact bytes crossing it, which is why there is no ceremony between you and them.

## The two rules that bite

**Every returned string must be heap-allocated.** The host frees every pointer you return, through
your own `cxagent_plugin_free`. Return a string literal and you hand `free()` a pointer it does not
own — and the crash lands later, inside the host, with a stack that never mentions your file.

**Never return NULL.** The host cannot tell "out of memory" from "not implemented", so the contract
is that a pointer always comes back. This example keeps a static sentinel for the allocation-failure
path, and `cxagent_plugin_free` checks for it — `free()` on static storage is undefined behaviour.

## Settings, without a parser

`cxagent_plugin_start` receives the context JSON — working directory and your settings block — and
this example pulls one integer out with `strstr` and `atoi`:

```
no settings       1 + 2 -> 3.00
"precision": 4    1 + 2 -> 3.0000
```

Enough to show the mechanism, and wrong the moment a settings block nests an object containing the
word `precision`. A real plugin parses. The point is that settings is where your configuration comes
from — hardcode a path or a flag and one binary serves one setup, read it here and it serves many.

## `pluginContract` appears twice

`cxagent_plugin_abi_version()` answers before anything else is read. The manifest carries
`"pluginContract": 2` as well, so a host holding a JSON blob can check it without a live library.

Omit the manifest field and it reads as 0 — the load is refused for an unsupported version your file
never mentions. Worth knowing before you spend an evening on it.

## See also

- [`../CalculatorPlugin`](../CalculatorPlugin) — the same calculator, managed
- [`cxagent_plugin.h`](../../Core/Plugins/Abi/cxagent_plugin.h) — the seven functions and their ownership rules
- [`Abi/README.md`](../../Core/Plugins/Abi/README.md) — the JSON envelopes
