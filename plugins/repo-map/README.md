# repo-map

A map of a codebase, for a model that has not read it.

A model finding its way around a repository lists folders and opens files, and every file opened only
to learn it was the wrong one is context spent. One call to `repo_map` returns the declarations that
matter most, each with its line number, so the next read is the right one.

## The one tool

`repo_map(path, focus, max_tokens)`. Every argument is optional.

```
412 source files under ., 318 declaring something. Most-used first. Each line is `line: declaration`.

cxagent.Core/Core/Agents/Agent.cs
  12: public sealed partial class Agent
    40: public Agent(AgentSetup setup)
    88: public async Task<TurnResult> RunTurnAsync(string prompt, CancellationToken ct)
cxagent.Core/Core/Jobs/JobRegistry.cs
  9: public sealed class JobRegistry
    31: public static JobRegistry CreateWithBuiltins()
...

Next by rank, declarations not shown:
  cxagent.Core/Core/Sessions/Session.cs
  ...
…and 271 more files. Pass focus, map a subfolder, or raise max_tokens (now 2048) to see them.
```

- **`path`**: the folder to map, relative to the caller's own folder. The default is the whole folder.
- **`focus`**: file paths, folders or symbol names, comma-separated (`src/Agent.cs,RunTurn`). What
  they use and what uses them come first.
- **`max_tokens`**: roughly how large the map may be. The default is 2048 and the most is 16384. What
  does not fit is still listed by path while the budget lasts, and the map always says how much it
  left out.

## How files are ranked

A file that mentions a name another file declares is taken to use that file. The files are then
ranked with PageRank over that graph. A file many others use ranks high, and a file used by
something many others use ranks higher still. So the types everything is built from come first and
the leaves come last.

The matching is by name, because there is no compiler to resolve references. A name declared in
several files spreads its weight across all of them, and names too common to mean anything, such as
`ToString`, `Run` and `Dispose`, are not counted. This is wrong in detail and right in aggregate,
which is all a map needs.

Tests, fixtures and bundled code rank below the project's own: files under `test/`, `tests/`,
`spec/`, `testdata/`, `fixtures/`, `__tests__/`, `vendor/` or `third_party/`, and files named like
tests (`*_test.go`, `*_spec.rb`, `*.test.ts`, `*.spec.ts`, `test_*.py`, `FooTest.java`,
`AgentTests.cs`). They are still
mapped and listed, only lower.

With a focus, the ranking restarts at the focused files instead of anywhere at all, and the files
that use them get a share of the restarts too. A focus on a test file works like any other.

## What it reads

**Languages:** C#, Java, Kotlin, Scala, Swift, Dart, C, C++, Objective-C, PHP, JavaScript,
TypeScript, Go, Rust, Python, Ruby and shell, recognised by file extension.

Declarations are found by reading the source as text, with no language server and no native parser,
so the plugin works the moment it is installed, in any repository. It finds:

- types (classes, structs, interfaces, records, enums, traits, typedefs, Dart's mixins and extensions,
  Scala's objects and Objective-C's `@interface` and `@implementation`);
- functions, methods and constructors, operators and indexers included;
- properties: C#'s, Objective-C's `@property`, Dart's getters, and the `var`, `val` and `let` of Swift,
  Kotlin, Scala and Dart;
- what a JavaScript or TypeScript module exports with `export const`, and a Dart library's top-level
  `final` and `const` values, shown up to their `=`.

It shows only what is declared at the top of a file or inside a type. Anything inside a function
body is left out. A line it misreads costs one declaration and never the map.

**Which files.** `git ls-files` decides: the repository's tracked files, plus untracked files that
are not ignored. Outside a repository it walks the folder instead, skipping dot-folders and the usual
build output (`bin`, `obj`, `node_modules`, `dist`, `build`, `target`, `vendor`). Files over 1 MB, `*.min.js` and
Blade templates (`*.blade.php`) are skipped, as are empty files, and a file is read only if it really is inside the folder: symlinks are never followed.

## What it asks, and what it does not need

It needs no language server. It reads source under the folder it is pointed at and refuses a folder
outside the caller's own, including one reached through a symlink. It writes nothing and opens no
connection. The one process it starts is `git ls-files`, and that has exited before the call returns.

You are asked twice, as for any plugin. The first prompt asks whether to load the plugin, and is
about trusting the binary. The second asks whether `repo_map` may be used in this folder; "Always
allow" answers that for good. The tool itself adds no prompt of its own.

## Configuring it

```json
"plugins": {
  "repo-map": { "file": "repo-map.dll" }
}
```

The file name alone is enough: plugins are found one folder down, in `plugins/repo-map/`. Writing
`repo-map/repo-map.dll` also finds it, but makes the whole plugins folder its load set, so
installing any other plugin changes repo-map's hash and asks for approval again.

It has no settings.
