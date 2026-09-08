# experiment-abi

The native twin of `../experiment-managed` — the same single capability, contract 3's
`IPluginClient`, exercised across the ABI boundary instead of in-process.

**NOT IN THE CATALOG.** `plugins.json` does not list this plugin. It exists to be driven, not used.

## The one tool

`experiment_submit(goal)` — queues `goal`, which `cxagent_plugin_poll` hands to the host on its next
tick. The host performs the actual `IPluginClient.Submit` call on the managed side
(`AbiPlugin.OnPluginSubmitted`); this plugin only ever sees the goal go out, never an answer.

**No `want_result`.** `AbiPlugin.cs` refuses `wantResult:true` on a polled submit by design — an ABI
plugin may only fire-and-forget (see `cxagent_plugin.h`, "WHY WANTRESULT IS REFUSED"). The managed
twin can wait for an answer because it holds the client directly; this plugin cannot, so its tool
does not offer to.

**One pending goal at a time.** A second `experiment_submit` before the first is polled replaces it,
reported back to the caller rather than silently dropped.

## Building

```
gcc -shared -fPIC -o experiment_abi.so experiment_abi.c
```

Linux only — the `.so` extension and this repo's `AbiFixtures/build.sh` both assume it.
