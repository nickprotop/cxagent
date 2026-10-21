# Vendored: TreeSitter.DotNet

Source: https://github.com/mariusgreuel/tree-sitter-dotnet-bindings, commit `8cae484`, `src/*.cs`,
MIT (LICENSE beside this file). The native libraries come from the TreeSitter.DotNet 1.3.0 package,
which that commit builds.

Compiled into repo-map.dll rather than referenced: a plugin is loaded with Assembly.LoadFrom from its
own install folder, and the package's loader finds natives only beside the host. Each change below
is marked `CXAGENT CHANGE n OF 3` in the source.

1. `NativeLibrary.cs` — a `[ModuleInitializer]` registers a `DllImport` resolver for `tree-sitter`.
2. `NativeLibrary.cs` — the loader tries `<assembly folder>/runtimes/<rid>/native/` first.
3. `Parser.cs`, `Native.cs` — `Parse(string, TimeSpan)`: a parse that gives up after a time limit.

Otherwise unchanged, warnings included: one nullability warning in `NativeLibrary.cs` is upstream's.
