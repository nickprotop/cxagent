# clone-finder

Duplication found without reading the files.

Hunting copy-paste by reading is the most expensive way a model can do it: every candidate file
read is context spent, and the copies worth finding are exactly the ones nobody remembers writing
twice. This scans the whole tree in one call and returns locations, not files.

## The one tool

`find_clones(path)` — point it at a directory, get the duplicated blocks ranked biggest first.

```
14 duplicated blocks. Top 14 by size (lines x places):

33L x4     src/Orders/Export.cs:120-152
           src/Invoices/Export.cs:88-120
           src/Reports/Export.cs:41-73
           + 1 more places
             | foreach (var row in rows)
             | writer.WriteField(row.Id);
```

Each finding names every place the block lives (`path:start-end`, relative to the scanned
directory) and quotes a couple of lines to recognise it by — enough to decide whether to open
anything at all.

## What counts as a clone

A block is reported when it clears **both** floors: `min_lines` (default 6) and `min_tokens`
(default 50). Either alone lets through what the other exists to stop — a line floor alone reports
any run of short statements that happen to align, a token floor alone reports a dense one-liner
pasted twice.

Matching is over normalised tokens: identifiers fold together, so a renamed copy still matches;
literals, keywords and structure stay distinct, so different code does not.

**Do not lower `min_tokens` to be thorough.** That floods the report with short repeated
statements — six lines of assertions clears the line floor, and the token floor is what keeps
them out.

## What it scans

Source files it knows how to tokenise — C-family languages plus the usual script languages.
Build output (`bin/`, `obj/`, `node_modules/`, `dist/`, `vendor/`) and everything `.gitignore`
ignores are skipped without being asked; `exclude` adds this run's own globs on top, e.g.
`**/tests/**`.

Tests are scanned by default: duplicated test setup is real duplication, and excluding it silently
would be a strong opinion applied on the quiet.

## A hit is a candidate, not a verdict

The detector reports that blocks match; whether they should stop matching is a judgment about the
code. Three test fixtures that align are not always worth merging.

## What it does not need

No permission prompt. The tool reads source under the directory you point it at, writes nothing,
starts no process and opens no connection.

Approval is still asked once for the plugin itself, as for any plugin: that prompt is about
trusting the binary, not about what the tool does.
