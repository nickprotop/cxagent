# CxAgent.Plugins.Templates

`dotnet new` templates for [cxagent](https://github.com/nickprotop/cxagent) plugins.

```
dotnet new install CxAgent.Plugins.Templates
dotnet new cxagent-plugin -n weather
```

`cxagent-plugin` writes a C# plugin ready to build and test:

- a tool with a per-call permission gate;
- its sidecar, `<name>.plugin.json`;
- a test project that loads it and calls it through
  [CxAgent.Plugins.Testing](https://www.nuget.org/packages/CxAgent.Plugins.Testing).

It pins this release's contract and testing packages. `cxagent plugin new <name>` writes the same
project without installing a template, and `cxagent plugin new <name> --lang c` writes one in C.

- [The plugin SDK](https://github.com/nickprotop/cxagent/blob/master/cxagent.Core/sdk/README.md): a tutorial from `new` to a packed release
- [Writing plugins](https://github.com/nickprotop/cxagent/blob/master/cxagent.Core/docs/plugins.md): the sidecar, gates, settings and commands
