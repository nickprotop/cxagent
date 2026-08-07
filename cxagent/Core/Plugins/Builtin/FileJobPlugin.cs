using CxAgent.Core.Models;

namespace CxAgent.Core.Plugins.Builtin;

/// <summary>File operations: read/write/append/delete/copy/move, plus read-only list/search.</summary>
public class FileJobPlugin : IJobPlugin
{
    private static readonly HashSet<string> Actions =
        new() { "read", "write", "append", "delete", "copy", "move", "list", "search", "replace" };

    public string TypeName => "file";
    public string DisplayName => "File Operation";

    public JobSchema GetSchema() => new(TypeName, DisplayName, new[]
    {
        new JobParamSpec("action", "string", Required: true,
            "read|write|append|delete|copy|move|list|search|replace"),
        new JobParamSpec("path", "string", Required: true, "Target file path"),
        // Says where content comes from, not just what it is. "Content for write/append" left the
        // model to infer that it must author the text, so with an upstream job to write it reached
        // for the reference syntax that no longer exists — rejected at compile time, costing a whole
        // repair round on a plan that was one omitted param away from correct. The param's OWN
        // description is the text closest to that decision; a rule stated only in the shared
        // guidance block loses to it.
        new JobParamSpec("content", "string", Required: false,
            "Text to write. Calling this as a TOOL: always supply it. In a PLANNED `file` job you "
            + "may omit it when the job has exactly one `depends_on` entry, and that dependency's "
            + "output becomes the file's contents."),
        new JobParamSpec("dest", "string", Required: false, "Destination for copy/move"),
        new JobParamSpec("offset", "integer", Required: false,
            "Read only: 1-based line to start at. Use with 'limit' to page through a file whose "
            + "content was elided for being too large."),
        new JobParamSpec("limit", "integer", Required: false,
            "Read/list/search: maximum results to return. Omit for a sensible default."),
        // LIST and SEARCH exist so the two commonest read-only questions -- "what files are here"
        // and "where does this string appear" -- stop being shell commands. Through run_shell they
        // are `find` and `grep`, which raise a permission prompt for an operation that reads
        // nothing the role is not already allowed to read; live drives stalled repeatedly on
        // exactly those approvals.
        new JobParamSpec("pattern", "string", Required: false,
            "list: a glob such as \"**/*.cs\" (default \"*\"). search: the text to find. "
            + "replace: the exact existing text to replace. Calling this as a TOOL: read the file "
            + "first and copy the text from what you just read. PLANNING a `file` job: only when you "
            + "already have that text verbatim — if knowing it would require reading the file, do "
            + "not plan the replace yet. See the job-type rule for what to do instead; it differs by "
            + "mode, and naming a job type you were not offered would be worse than saying nothing."),
        // REPLACE, because `write` is whole-file only. Changing one function in a 500-line file
        // meant reproducing all 500 lines from a model's memory of them -- every unchanged line an
        // opportunity to silently alter something nobody asked to change.
        new JobParamSpec("regex", "boolean", Required: false,
            "search: treat `pattern` as a regular expression instead of literal text. Default false "
            + "— a pattern containing . or ( means something different under each mode."),
        new JobParamSpec("glob", "string", Required: false,
            "search: restrict to files matching this glob, e.g. \"*.cs\". Default all files."),
        // Says the indentation is handled. Without it a model spends turns trying to reproduce a
        // file's exact leading whitespace — which it cannot see reliably in a tool result — and,
        // measured live, mistrusts its own correct edit afterwards and reverts it.
        new JobParamSpec("replacement", "string", Required: false,
            "replace: the text to substitute for `pattern`. The pattern must appear EXACTLY once "
            + "in the file, or nothing is written. INDENTATION IS HANDLED FOR YOU: write the "
            + "replacement at whatever indentation is natural and it is shifted onto the file's own "
            + "(tabs or spaces, matching the line being replaced), keeping any nesting inside it. "
            + "The result echoes the exact text written, so there is no need to re-read the file "
            + "or inspect it with a shell command to confirm."),
    });

    public JobValidation Validate(JobParameters parameters)
    {
        var action = parameters.Get("action", "");
        var path = parameters.Get("path", "");
        var errors = new List<string>();
        if (!Actions.Contains(action)) errors.Add($"'action' must be one of {string.Join("|", Actions)}.");
        if (string.IsNullOrWhiteSpace(path)) errors.Add("'path' is required.");
        if (action is "write" or "append" && parameters.Get<string?>("content", null) is null)
            errors.Add($"'content' is required for action '{action}'.");
        if (action == "replace")
        {
            if (string.IsNullOrEmpty(parameters.Get("pattern", "")))
                errors.Add("'pattern' is required for action 'replace'.");
            if (parameters.Get<string?>("replacement", null) is null)
                errors.Add("'replacement' is required for action 'replace'.");
        }
        if (action is "copy" or "move" && string.IsNullOrWhiteSpace(parameters.Get("dest", "")))
            errors.Add($"'dest' is required for action '{action}'.");
        return errors.Count == 0 ? JobValidation.Valid() : JobValidation.Invalid(errors.ToArray());
    }

    /// <summary>
    /// Reads a file, optionally a line window of it.
    ///
    /// <para>Exists because of a LOOP seen on a five-way fan-out: a worker asked for a 36 KB source
    /// file, <see cref="WorkerToolset.MaxToolResultChars"/> elided the middle, and the worker — with
    /// no way to ask for the missing part — re-issued the SAME call and got the SAME cut, until the
    /// turn cap killed it. The cap is right (an unbounded read is re-sent on every subsequent
    /// ChatAsync call for the rest of the tool loop); what was missing was a way to NAVIGATE it.</para>
    ///
    /// <para>Always reports <c>total_lines</c>, so a worker whose window was elided can compute the
    /// next one rather than guess. Without it the tool is still a loop: the model has no idea whether
    /// it is a tenth of the way through or nearly done.</para>
    /// </summary>
    private static async Task ReadAsync(string path, JobParameters parameters,
        Dictionary<string, object?> output, CancellationToken ct)
    {
        // A DIRECTORY, not a file. File.ReadAllTextAsync throws "Access to the path ... is denied",
        // which reads as a PERMISSIONS problem — and a model that believes it lacks access does not
        // retry with the right action, it hunts for another route in. Seen live: ten consecutive
        // discovery jobs, two of them this exact failure, and the goal never reached its edit.
        if (Directory.Exists(path))
            throw new InvalidOperationException(
                $"'{path}' is a directory, not a file. Use action 'list' to see what is in it, or "
                + "'search' to find text inside it.");

        var offset = parameters.Get<int?>("offset", null);
        var limit = parameters.Get<int?>("limit", null);

        // Whole-file read stays a single ReadAllTextAsync: it is the common case, and streaming
        // lines would change the exact bytes returned (a file with no trailing newline would gain
        // one) for callers that never asked for a window.
        if (offset is null && limit is null)
        {
            var text = await File.ReadAllTextAsync(path, ct);
            output["content"] = text;
            output["total_lines"] = CountLines(text);
            return;
        }

        // Clamp rather than reject. A model that asks for line 0 (or a negative offset, or reads
        // past EOF) has made an off-by-one, not a fatal error — returning an error string would
        // spend a whole turn on something the obvious interpretation handles. Reading past the end
        // yields an empty window plus total_lines, which is exactly the signal to stop.
        var all = await File.ReadAllLinesAsync(path, ct);
        var start = Math.Clamp((offset ?? 1) - 1, 0, all.Length);
        var count = Math.Clamp(limit ?? (all.Length - start), 0, all.Length - start);

        output["content"] = string.Join('\n', all.Skip(start).Take(count));
        output["total_lines"] = all.Length;
        output["offset"] = start + 1;
        output["lines_returned"] = count;
    }

    /// <summary>Line count of raw text, matching ReadAllLinesAsync's convention so `total_lines`
    /// means the same thing on both paths — otherwise a worker that reads a whole file, then pages
    /// with an offset, would see the count change under it.</summary>
    private static int CountLines(string text)
    {
        if (text.Length == 0) return 0;
        var lines = text.Split('\n').Length;
        return text.EndsWith('\n') ? lines - 1 : lines;
    }

    /// <summary>
    /// Lists files under a directory. Read-only, and deliberately NOT a shell command: through
    /// run_shell this is `find`, which raises a permission prompt for an operation that reads
    /// nothing the role could not already read. Live drives stalled repeatedly on exactly those
    /// approvals, and a worker blocked on a prompt is a worker doing nothing.
    /// </summary>
    private static void ListInto(string path, JobParameters parameters,
        Dictionary<string, object?> output)
    {
        var pattern = parameters.Get("pattern", "*");
        var limit = parameters.Get<int?>("limit", null) ?? 200;

        // The directory itself, or the directory holding a file that was passed by mistake — a
        // model that lists "src/Foo.cs" meant "the folder Foo.cs is in", and erroring teaches it
        // nothing it can act on.
        var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path;

        var matches = Directory.EnumerateFiles(dir, NormalizeGlob(pattern), SearchOption.AllDirectories)
            .Take(limit + 1)
            .ToList();

        var truncated = matches.Count > limit;
        if (truncated) matches.RemoveAt(matches.Count - 1);

        output["content"] = string.Join('\n', matches);
        output["count"] = matches.Count;
        // Says so rather than silently returning a partial answer the model reads as complete.
        if (truncated) output["truncated"] = true;
    }

    /// <summary>
    /// Normalises a caller's glob into the single-segment pattern <c>Directory.EnumerateFiles</c>
    /// accepts, since both call sites already search recursively via SearchOption.AllDirectories.
    ///
    /// <para><c>**/*.cs</c> is what any developer writes and what a model reaches for first. .NET
    /// does not understand it — it treats <c>**</c> as a literal directory name and throws "Could
    /// not find a part of the path", which reads as "your path is wrong" rather than "that syntax is
    /// unsupported". Seen live: a search of a 1,294-file tree failed twice on it, and the agent fell
    /// back to `find` through run_shell, paying a permission prompt for a read it was already
    /// entitled to make.</para>
    ///
    /// <para>Leading <c>./</c> is stripped for the same reason: harmless to a shell, fatal here.</para>
    /// </summary>
    private static string NormalizeGlob(string glob)
    {
        var g = glob.Trim();
        if (g.Length == 0) return "*";

        // Recursion is the search mode, not part of the pattern: drop any leading **/ or ./ segments.
        while (g.StartsWith("**/", StringComparison.Ordinal) || g.StartsWith("**\\", StringComparison.Ordinal))
            g = g[3..];
        while (g.StartsWith("./", StringComparison.Ordinal) || g.StartsWith(".\\", StringComparison.Ordinal))
            g = g[2..];

        // A ** left anywhere else (src/**/*.cs) cannot be expressed in one segment. Keep the file
        // part, which is the half that actually selects, rather than throwing over the directory
        // half that AllDirectories already covers.
        var star2 = g.LastIndexOf("**", StringComparison.Ordinal);
        if (star2 >= 0)
        {
            var tail = g[(star2 + 2)..].TrimStart('/', '\\');
            g = tail.Length > 0 ? tail : "*";
        }

        return g.Length == 0 ? "*" : g;
    }

    /// <summary>
    /// Finds a literal string in files under a directory, reporting file:line:text. The `grep` half
    /// of the same argument as <see cref="ListInto"/>.
    ///
    /// <para>LITERAL, not regex: a model that means a literal string and gets regex semantics
    /// silently searches for something else, and the failure looks like "not found" rather than
    /// like a mistake.</para>
    /// </summary>
    private static async Task SearchIntoAsync(string path, JobParameters parameters,
        Dictionary<string, object?> output, CancellationToken ct)
    {
        var needle = parameters.Get("pattern", "");
        if (string.IsNullOrEmpty(needle))
        {
            output["content"] = "";
            output["count"] = 0;
            return;
        }

        var limit = parameters.Get<int?>("limit", null) ?? 100;
        var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path;
        var glob = parameters.Get("glob", "*");

        // REGEX for content, opt-in. "TODO|FIXME", "class \\w+Decoder", "^\\s*public" are ordinary
        // questions, and literal-only forces several round trips to answer one of them -- each a
        // paid turn. Opt-in because a pattern containing . or ( means something different under
        // each mode, and silently choosing regex would change what an existing literal search finds.
        //
        // A one-second match timeout: a catastrophically backtracking pattern is a plausible thing
        // for a model to write, and without a bound it hangs the job rather than failing it.
        var useRegex = parameters.Get("regex", false);
        System.Text.RegularExpressions.Regex? rx = null;
        if (useRegex)
        {
            try
            {
                rx = new System.Text.RegularExpressions.Regex(needle,
                    System.Text.RegularExpressions.RegexOptions.Compiled,
                    TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException ex)
            {
                // Name the fix: a bad pattern is a typo, not a reason to abandon the search.
                throw new InvalidOperationException(
                    $"'pattern' is not a valid regular expression: {ex.Message}. "
                    + "Omit `regex` to search for it as literal text.");
            }
        }

        var hits = new List<string>();
        foreach (var file in Directory.EnumerateFiles(dir, NormalizeGlob(glob), SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            if (hits.Count >= limit) break;

            string[] lines;
            // A binary or unreadable file is not an error worth failing the whole search over.
            try { lines = await File.ReadAllLinesAsync(file, ct); }
            catch (Exception) { continue; }

            for (var i = 0; i < lines.Length && hits.Count < limit; i++)
            {
                bool match;
                // A timeout is a property of THIS pattern on THIS line, not a failure of the search:
                // skip the line and keep going rather than losing every hit found so far.
                try { match = rx is null ? lines[i].Contains(needle, StringComparison.Ordinal) : rx.IsMatch(lines[i]); }
                catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { continue; }

                if (match) hits.Add($"{file}:{i + 1}:{lines[i].Trim()}");
            }
        }

        output["content"] = string.Join('\n', hits);
        output["count"] = hits.Count;
        if (hits.Count >= limit) output["truncated"] = true;
    }

    /// <summary>
    /// Replaces one exact occurrence of <c>pattern</c> with <c>replacement</c>.
    ///
    /// <para><c>write</c> is whole-file only, so changing one function in a 500-line file meant
    /// reproducing all 500 lines from the model's memory of them — and every unchanged line it
    /// retypes is a chance to silently alter something nobody asked to change.</para>
    ///
    /// <para>EXACTLY ONCE, or nothing is written. An ambiguous match means the model does not know
    /// which occurrence it is editing, and picking one silently is how the wrong line gets changed
    /// in a file nobody is watching. Zero matches usually means the model is editing from memory
    /// rather than from what the file says.</para>
    /// </summary>
    private static async Task ReplaceAsync(string path, JobParameters parameters,
        Dictionary<string, object?> output, CancellationToken ct)
    {
        var pattern = parameters.Get<string>("pattern");
        var replacement = parameters.Get<string>("replacement");
        var text = await File.ReadAllTextAsync(path, ct);

        var (first, matchLength) = FindSingleMatch(text, pattern, path);

        // RE-INDENT TO THE FILE. Matching already ignores whitespace, so a model that writes
        // standard 4-space C# into a tab-indented file gets its match — and then, because the
        // replacement was inserted VERBATIM, silently breaks the indentation it just matched past.
        // The tool was lenient about what it accepted and literal about what it wrote, which is the
        // worst combination: the edit lands and the file is subtly wrong.
        //
        // Measured live on a real bug hunt: a correct one-line fix turned "\t\t\tif (open)" into
        // "\t\tif (open)". The agent spotted it with `cat -A`, REVERTED its own correct fix, and
        // spent the rest of the run building a heredoc patch through run_shell — abandoning a tool
        // that had worked, over indentation it had no way to know.
        var original = replacement;
        replacement = ReindentToMatch(text, first, matchLength, replacement);

        // PRESERVE THE BOM. File.WriteAllTextAsync writes UTF-8 without one regardless of what the
        // file had, so editing one line of a BOM'd file silently rewrote its first three bytes —
        // caught live: HexEncoder.cs went from EF BB BF to 2F 2F on a two-line insertion. In a C#
        // repo that is a spurious diff on every file an implementer touches, and the kind of change
        // nobody attributes to the agent that made it.
        var hadBom = await StartsWithBomAsync(path, ct);
        var encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: hadBom);

        await File.WriteAllTextAsync(path,
            text[..first] + replacement + text[(first + matchLength)..], encoding, ct);

        // SHOW WHAT LANDED. The old result said only "replaced 1 occurrence", so a model with any
        // doubt about its edit had exactly one way to check: shell out. Measured live — an agent
        // followed a correct replace with `cat -A`, mistrusted what it saw, reverted its own fix and
        // spent the rest of the run patching through run_shell. Echoing the written line closes that
        // loop in-band, and saying the indentation was adjusted explains the difference it would
        // otherwise discover and misread as damage.
        var note = ReferenceEquals(original, replacement)
            ? ""
            : " (indentation adjusted to match the file)";

        output["content"] = $"replaced 1 occurrence in {path}{note}\n"
            + $"the file now reads:\n{QuoteWritten(replacement)}";
        output["bytes_before"] = text.Length;
    }

    /// <summary>
    /// Shifts <paramref name="replacement"/> onto the indentation the REPLACED TEXT actually had.
    ///
    /// <para>Only the leading whitespace of each line is touched, and only when the replacement's own
    /// indentation differs from the file's. An edit that already carries the file's exact indentation
    /// is returned unchanged — this is a repair, not a reformat, and rewriting a correct edit would
    /// be its own kind of surprise.</para>
    ///
    /// <para>RELATIVE STRUCTURE SURVIVES: every line is shifted by the same amount, computed from the
    /// FIRST line, so a nested line one level deeper stays one level deeper. Flattening a block would
    /// trade a small indentation bug for a much worse one.</para>
    /// </summary>
    private static string ReindentToMatch(string text, int start, int length, string replacement)
    {
        // The file's indentation at the edit site: the whitespace opening the line `start` sits on,
        // which is the indentation the replaced text itself had.
        var lineStart = text.LastIndexOf('\n', Math.Max(0, Math.Min(start, text.Length - 1))) + 1;
        var fileIndent = LeadingWhitespace(text, lineStart);

        var lines = replacement.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0) return replacement;

        // The replacement's own base — the frame everything in it is relative to.
        //
        // NORMALLY the first line's indent. But when the first line is SHALLOWER than the body below
        // it, the model ANCHORED: it copied a match anchor (a comment, a brace) at whatever column
        // the tool result showed, then wrote the body in its own frame. The anchor's column then
        // describes nothing, and using it as the base makes every line under it look like nesting to
        // ADD — measured live on exactly this shape, three tabs became eight. The body's own minimum
        // is the real base there, and the anchor line rides along with it.
        var firstIndent = LeadingWhitespace(lines[0], 0);
        var bodyIndent = lines.Length > 1 ? MinimumIndent(lines[1..]) : firstIndent;
        var anchored = lines.Length > 1 && firstIndent.Length < bodyIndent.Length;

        var ownIndent = anchored ? bodyIndent : firstIndent;
        if (ownIndent == fileIndent) return replacement;   // already right: leave it exactly alone

        // REFUSE TO GUESS when the shift is not uniform. Every line must sit at or below the
        // replacement's own base, so that moving the base moves the whole block rigidly and the
        // shape the model wrote is preserved exactly. A line ABOVE the base cannot be expressed as
        // base + nesting, and re-indenting it means inventing a second interpretation of what the
        // model meant.
        //
        // Aider reached the same rule from the other direction and states it as code: it collapses
        // the per-line whitespace deltas and bails with `if len(add) != 1: return` — non-uniform
        // indent is REJECTED rather than repaired. Its benchmark is also the reason to attempt this
        // at all (disabling flexible patching: 9x more editing errors), so the pairing is the whole
        // lesson: be generous about the offset, refuse anything that is not one.
        //
        // Returning the replacement untouched is the honest failure: the model's own text is written
        // and it can see the result in the echo, rather than the tool silently reshaping code on a
        // guess.
        foreach (var line in lines)
        {
            if (line.Trim().Length == 0) continue;
            if (!LeadingWhitespace(line, 0).StartsWith(ownIndent, StringComparison.Ordinal))
                return replacement;
        }

        var sb = new System.Text.StringBuilder(replacement.Length + lines.Length * fileIndent.Length);
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) sb.Append('\n');

            var line = lines[i];
            if (line.Trim().Length == 0) { sb.Append(line); continue; }   // blank lines keep as-is

            var indent = LeadingWhitespace(line, 0);

            // The part of this line's indentation BEYOND the replacement's own base — its nesting.
            // TRANSLATED into the file's indent character, not copied: keeping the model's four
            // spaces verbatim under a tab base produces "\t\t    Go();", mixing both in one line,
            // which is a worse artefact than the original bug and one every C# linter flags.
            var extra = indent.StartsWith(ownIndent, StringComparison.Ordinal)
                ? indent[ownIndent.Length..]
                : "";

            sb.Append(fileIndent).Append(TranslateIndent(extra, fileIndent)).Append(line[indent.Length..]);
        }

        return sb.ToString();
    }

    /// <summary>
    /// The written text, capped, for echoing back in the tool result. Bounded because a large
    /// replacement would otherwise crowd out the rest of the worker's context to confirm something
    /// it already knows it sent.
    /// </summary>
    private static string QuoteWritten(string written)
    {
        var lines = written.Replace("\r\n", "\n").Split('\n');
        if (lines.Length <= 12) return written;
        return string.Join('\n', lines.Take(6))
             + $"\n… {lines.Length - 12} more lines …\n"
             + string.Join('\n', lines.Skip(lines.Length - 6));
    }

    /// <summary>
    /// Rewrites one level of nesting in the FILE's indent character.
    ///
    /// <para>A replacement's inner indentation is whatever the model wrote — usually four spaces. Under
    /// a tab-indented file, appending it verbatim yields "\t\t    Go();", mixing tabs and spaces on a
    /// single line: a worse artefact than the bug being fixed, and one every C# linter flags. Four
    /// spaces of nesting under a tab file must become one tab.</para>
    ///
    /// <para>Space-indented files keep spaces, so nothing changes for the common case.</para>
    /// </summary>
    private static string TranslateIndent(string extra, string fileIndent)
    {
        if (extra.Length == 0) return extra;

        // Only translate when the file indents with TABS and the nesting uses spaces (or vice
        // versa). Same-character nesting is already right.
        var fileUsesTabs = fileIndent.Contains('\t');
        var extraUsesTabs = extra.Contains('\t');
        if (fileUsesTabs == extraUsesTabs) return extra;

        if (fileUsesTabs)
        {
            // Spaces -> tabs. Four is the near-universal step in the C# these files are written in,
            // and any remainder is dropped rather than left as stray spaces after a tab.
            var levels = extra.Count(c => c == ' ') / 4;
            return new string('\t', Math.Max(1, levels));
        }

        // Tabs -> spaces, the mirror case: a tab of nesting in a space-indented file.
        return new string(' ', extra.Count(c => c == '\t') * 4);
    }

    /// <summary>The shallowest indentation across non-blank lines. Blank lines carry none and would
    /// otherwise force the result to "" and flatten everything.</summary>
    private static string MinimumIndent(string[] lines)
    {
        string? min = null;
        foreach (var line in lines)
        {
            if (line.Trim().Length == 0) continue;
            var indent = LeadingWhitespace(line, 0);
            if (min is null || indent.Length < min.Length) min = indent;
        }
        return min ?? "";
    }

    /// <summary>
    /// The closest few lines in the file to the pattern's first line, with whitespace MADE VISIBLE.
    ///
    /// <para>"not found, even ignoring indentation" tells a model that it failed but not what the
    /// file actually says, so its next move is to guess again from the same memory that just missed.
    /// crush does the same thing for the same reason — its diagnoseMismatch renders tabs as → and
    /// spaces as · and points at the closest window — because whitespace is precisely the thing a
    /// model cannot see in ordinary tool output and precisely the thing it got wrong.</para>
    /// </summary>
    private static string NearestLines(string text, string pattern)
    {
        var needle = pattern.Replace("\r\n", "\n").Split('\n')
                            .FirstOrDefault(l => l.Trim().Length > 0)?.Trim();
        if (string.IsNullOrEmpty(needle)) return "";

        var lines = text.Replace("\r\n", "\n").Split('\n');
        var hits = new List<string>();
        for (var i = 0; i < lines.Length && hits.Count < 3; i++)
            if (lines[i].Trim() == needle) hits.Add($"  line {i + 1}: {VisualizeWhitespace(lines[i])}");

        return hits.Count == 0 ? ""
            : "\n\nThe file has these lines with matching text but different whitespace "
              + "(→ = tab, · = space):\n" + string.Join('\n', hits);
    }

    /// <summary>Renders leading tabs and spaces as visible glyphs.</summary>
    private static string VisualizeWhitespace(string line)
    {
        var indent = LeadingWhitespace(line, 0);
        var shown = new string(indent.Select(c => c == '\t' ? '→' : '·').ToArray());
        return shown + line[indent.Length..];
    }

    /// <summary>The run of spaces and tabs starting at <paramref name="from"/>.</summary>
    private static string LeadingWhitespace(string s, int from)
    {
        var i = from;
        while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) i++;
        return s[from..i];
    }

    /// <summary>Whether the file begins with a UTF-8 BOM. Read from the BYTES, not from
    /// ReadAllTextAsync — that strips the BOM silently, so the text alone cannot tell you whether
    /// there was one.</summary>
    private static async Task<bool> StartsWithBomAsync(string path, CancellationToken ct)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            var head = new byte[3];
            var read = await stream.ReadAsync(head, ct);
            return read == 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF;
        }
        catch (Exception) { return false; }   // unreadable head: write the common form
    }

    /// <summary>
    /// Locates <paramref name="pattern"/> in <paramref name="text"/>, exactly if possible and
    /// otherwise ignoring how each line is INDENTED. Returns where it starts and how long the real
    /// match is, since a whitespace-insensitive match can be a different length than the pattern.
    ///
    /// <para>Exact-only was too brittle to use. A live drive read HexEncoder.cs, planned a replace,
    /// and failed on both files: the source is indented with TABS and the pattern almost certainly
    /// came back with spaces. The model cannot see whitespace it did not look at closely, and
    /// "reproduce the indentation exactly, from memory" is not a thing to ask of it — the whole
    /// point of `replace` was to stop it reproducing text from memory.</para>
    ///
    /// <para>WHITESPACE ONLY, and only within a line: indentation, and runs of spaces between
    /// tokens. Every non-space character must still match in order. That is enough for the two ways
    /// a model reliably differs from a file it is copying — tabs it did not look at, and house style
    /// it does not share (MimeKit writes <c>EstimateOutputLength (int)</c> with a space before the
    /// paren; standard C# does not, and a model writes standard C#) — while still being unable to
    /// quietly edit a DIFFERENT piece of code, which is the risk that made exact-match the first
    /// choice.</para>
    /// </summary>
    /// <summary>
    /// A line with ALL whitespace removed, so two lines that differ only in spacing compare equal.
    /// Non-space characters and their order are untouched.
    ///
    /// <para>All of it, not collapsed-to-one-space: the failure this exists for is
    /// <c>Estimate (int n)</c> versus <c>Estimate(int n)</c>, where the file has a space and the
    /// pattern has none. Collapsing runs still leaves those different.</para>
    ///
    /// <para>What this gives up: <c>a b</c> and <c>ab</c> now compare equal, so a pattern could in
    /// principle match a line whose tokens run together differently. In a language where that
    /// changes meaning it would matter; in C# it takes a deliberately perverse pattern, and the
    /// alternative — measured twice on live drives — is a tool too brittle to use at all.</para>
    /// </summary>
    private static string Squash(string line)
    {
        var sb = new System.Text.StringBuilder(line.Length);
        foreach (var c in line)
            if (!char.IsWhiteSpace(c)) sb.Append(c);
        return sb.ToString();
    }

    private static (int Start, int Length) FindSingleMatch(string text, string pattern, string path)
    {
        var matches = FindAllMatches(text, pattern);

        if (matches.Count == 0)
            throw new InvalidOperationException(
                $"'pattern' was not found in {path}, even ignoring indentation. Read the file first "
                + "and copy the text from what it actually says, rather than reproducing it from "
                + "memory."
                + NearestLines(text, pattern));

        if (matches.Count > 1)
            throw new InvalidOperationException(
                $"'pattern' appears {matches.Count} times in {path}, so which one to change is "
                + "ambiguous. Include enough surrounding lines to make it unique. Nothing was "
                + "written.");

        return matches[0];
    }

    /// <summary>
    /// Every place <paramref name="pattern"/> matches, under the SAME whitespace-insensitive rule
    /// used to locate the edit.
    ///
    /// <para>Uniqueness and location must be decided by ONE matcher. They were not: the edit was
    /// located ignoring whitespace, then uniqueness was re-checked by searching for the literal
    /// matched text. Two occurrences differing only in spacing — `if (x) {` and `if (x)  {`, the
    /// commonest kind of near-duplicate in real source — therefore read as unique, and the tool
    /// silently edited the first. Nothing warned, and the diff looked deliberate.</para>
    /// </summary>
    private static List<(int Start, int Length)> FindAllMatches(string text, string pattern)
    {
        var found = new List<(int, int)>();

        // ONE pass, whitespace-insensitive, over whole lines. Not "exact matches first, else fall
        // back": an exact hit and a spacing-variant hit are equally plausible targets to a caller
        // who could not know the file's exact spacing — that is why the insensitive rule exists at
        // all — so short-circuiting on the exact one hides the ambiguity instead of reporting it.
        var patternLines = pattern.Replace("\r\n", "\n").Split('\n');
        var textLines = text.Replace("\r\n", "\n").Split('\n');

        for (var i = 0; i + patternLines.Length <= textLines.Length; i++)
        {
            var all = true;
            for (var j = 0; j < patternLines.Length && all; j++)
                all = Squash(textLines[i + j]) == Squash(patternLines[j]);
            if (!all) continue;

            // Character offset of line i in the ORIGINAL text, so the slice is exact.
            var start = 0;
            for (var k = 0; k < i; k++) start += textLines[k].Length + 1;
            var length = 0;
            for (var k = 0; k < patternLines.Length; k++) length += textLines[i + k].Length + 1;

            found.Add((start, Math.Min(length - 1, text.Length - start)));
        }

        return found;
    }

    public async Task<JobResult> ExecuteAsync(JobParameters parameters, IJobContext context, CancellationToken ct)
    {
        var action = parameters.Get<string>("action");
        var path = parameters.Get<string>("path");
        var start = DateTimeOffset.UtcNow;
        try
        {
            var output = new Dictionary<string, object?>();
            switch (action)
            {
                case "read":
                    await ReadAsync(path, parameters, output, ct);
                    break;
                case "list":
                    ListInto(path, parameters, output);
                    break;
                case "search":
                    await SearchIntoAsync(path, parameters, output, ct);
                    break;
                case "replace":
                    await ReplaceAsync(path, parameters, output, ct);
                    break;
                case "write":
                    await File.WriteAllTextAsync(path, parameters.Get<string>("content"), ct);
                    break;
                case "append":
                    await File.AppendAllTextAsync(path, parameters.Get<string>("content"), ct);
                    break;
                case "delete":
                    File.Delete(path);
                    break;
                case "copy":
                    File.Copy(path, parameters.Get<string>("dest"), overwrite: true);
                    break;
                case "move":
                    File.Move(path, parameters.Get<string>("dest"), overwrite: true);
                    break;
            }
            context.Log($"file {action}: {path}");
            return new JobResult { Success = true, ExitCode = 0, Output = output, Duration = DateTimeOffset.UtcNow - start };
        }
        catch (Exception ex)
        {
            return new JobResult { Success = false, ExitCode = -1, ErrorMessage = ex.Message, Duration = DateTimeOffset.UtcNow - start };
        }
    }
}
