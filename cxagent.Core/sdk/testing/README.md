# CxAgent.Plugins.Testing

Check and call a [cxagent](https://github.com/nickprotop/cxagent) plugin from a test, with no
session and no model. It uses the loaders, the permission gate and the lifecycle a session uses, so
a plugin that passes here loads and asks the way it will for a user. It is the same engine as
`cxagent plugin check` and `cxagent plugin call`.

Native plugins work too: the package carries the plugin host, so a test needs no cxagent installed.

```xml
<PackageReference Include="CxAgent.Plugins.Testing" Version="0.9.*" />
```

```csharp
using CxAgent.Plugins.Testing;

[Fact]
public async Task SearchAsksAndAnswers()
{
    Assert.Empty(await PluginHarness.CheckAsync(PluginDir));          // what check reports

    await using var plugin = await PluginHarness.LoadAsync(PluginDir);
    var call = await plugin.CallAsync("search", new { city = "Athens" }, answer: GateAnswer.Allow);

    Assert.Equal("look up the weather for Athens", call.Gate?.Display);
    Assert.True(call.Result!.Success);
    Assert.Contains(call.Progress, p => p.Percent == 50);
}
```

`PluginDir` is a plugin folder: its `<name>.plugin.json` and the library beside it, as a user
installs it.

- [Testing a plugin](https://github.com/nickprotop/cxagent/blob/master/cxagent.Core/docs/plugin-testing.md): every option of the harness and the CLI
- [The plugin SDK](https://github.com/nickprotop/cxagent/blob/master/cxagent.Core/sdk/README.md): tutorials, in C# and in C
