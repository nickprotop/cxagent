using CxAgent.Core.Models;

namespace CxAgent.Core.Plugins.Builtin;

/// <summary>File operations: read/write/append/delete/copy/move, plus read-only list/search.</summary>
public class FileJobPlugin : IJobPlugin
{
    private static readonly HashSet<string> Actions =
        new() { "read", "write", "append", "delete", "copy", "move", "list", "search", "replace",
                "create" };

    public string TypeName => "file";
    public string DisplayName => "File Operation";

    public JobSchema GetSchema() => new(TypeName, DisplayName, new[]
    {
        new JobParamSpec("action", "string", Required: true,
            "read|write|append|delete|copy|move|list|search|replace|create"),
        // "Target file path" for EVERY action, including list and search, where it is a DIRECTORY.
        // One schema serves six actions, so the one description has to say where they differ — and
        // the omission is not theoretical: an agent put a glob here ({"path": "**/*"}) because the
        // only required param was called a file path while the tool description talked about
        // patterns. It found nothing, fell back to `ls -R`, and read a bin/ directory as the
        // project.
        new JobParamSpec("path", "string", Required: true,
            "The file to act on. For list and search it is the DIRECTORY to search under, optional "
            + "and defaulting to the working directory — the glob or search text goes in `pattern`, "
            + "never here."),
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
        // LIST AND SEARCH DEFAULT IT to the working directory, so demanding it here would reject the
        // call the `glob` and `grep` tools now advertise as valid — pattern required, path an
        // optional narrowing.
        if (string.IsNullOrWhiteSpace(path) && action is not ("list" or "search"))
            errors.Add("'path' is required.");
        if (action is "write" or "append" or "create" && parameters.Get<string?>("content", null) is null)
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
        // READ THROUGH THE SERVICE, so a BOM'd file's text reads the same here as it does to the
        // matcher — the BOM is stripped either way and never reaches the model as a stray U+FEFF at
        // the start of the first line.
        var snapshot = await FileMutation.ReadAsync(path, ct);

        // MISSING IS NOT EMPTY. The service reports absence rather than throwing, which is right for
        // a writer — a write to a new path is ordinary. For a READ it is not: returning "" would tell
        // the model the file exists and is empty, and it would reason from that. The framework's own
        // message names the path, which is what a model needs to fix a typo.
        if (!snapshot.Existed)
            throw new FileNotFoundException($"Could not find file '{path}'.", path);

        var text = snapshot.Text;

        if (offset is null && limit is null)
        {
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
    /// <summary>
    /// Removes the files the REPOSITORY says are ignored, by asking git rather than guessing.
    ///
    /// <para>WHY GIT AND NOT A LIST. A hardcoded set of names — bin, obj, node_modules — is wrong in
    /// both directions at once. It misses whatever THIS repo generates (build/, out/, .next/,
    /// vendor/, a generated Generated/), and it overrides repos that commit dist/ on purpose,
    /// silently, with nothing in the result to say a filter ran. git already knows the answer,
    /// including nested .gitignore files down the tree, the global excludes file, and negations
    /// like "!keep.log" that a name list cannot express at all.</para>
    ///
    /// <para>ONE PROCESS FOR THE WHOLE BATCH, via <c>git check-ignore --stdin</c>. Measured on this
    /// machine: 17ms for 73 paths, 15ms for 281 — flat, because the cost is the spawn and not the
    /// paths. Against a tool call that has already walked the filesystem that is not worth
    /// avoiding, and it is the reason this is not one invocation per file.</para>
    ///
    /// <para>NO REPO, NO FILTERING. Outside a git checkout there is no authority to consult and
    /// nothing is dropped: a caller who asked to list a directory gets that directory. Same when git
    /// is missing or fails — the results pass through unfiltered rather than a guess standing in for
    /// an answer. The failure mode is noise, which is visible; the alternative is hiding a file
    /// nobody was told about.</para>
    ///
    /// <para>.git ITSELF IS ALWAYS DROPPED. It is not "ignored" (git tracks nothing inside it) so
    /// check-ignore says nothing about it, yet a search that walks it reports hits in every historic
    /// version of every file — content nobody can edit.</para>
    /// </summary>
    private static List<string> WithoutIgnored(List<string> files, string dir)
    {
        if (files.Count == 0) return files;

        var kept = files.Where(f => !InsideGitDir(f)).ToList();
        if (kept.Count == 0) return kept;

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git")
            {
                WorkingDirectory = dir,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("check-ignore");
            psi.ArgumentList.Add("--stdin");

            using var git = System.Diagnostics.Process.Start(psi);
            if (git is null) return kept;

            foreach (var f in kept) git.StandardInput.WriteLine(f);
            git.StandardInput.Close();

            var ignored = new HashSet<string>(StringComparer.Ordinal);
            while (git.StandardOutput.ReadLine() is { } line)
                if (line.Length > 0) ignored.Add(line);

            // Bounded, because a hung git must not hang the tool. check-ignore is a local index
            // lookup; a second is already pathological.
            if (!git.WaitForExit(1000)) { try { git.Kill(entireProcessTree: true); } catch { } return kept; }

            // Exit 0 = something matched, 1 = nothing matched, anything else = git could not answer
            // (not a repo, bad invocation) and its opinion is not usable.
            if (git.ExitCode > 1) return kept;

            return ignored.Count == 0 ? kept : kept.Where(f => !ignored.Contains(f)).ToList();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
            or IOException or UnauthorizedAccessException)
        {
            // No git on PATH, or it could not be run. Unfiltered beats invented.
            return kept;
        }
    }

    /// <summary>Segment-wise, never a substring: a directory named "src/.github" is not ".git".</summary>
    private static bool InsideGitDir(string file)
    {
        foreach (var part in file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            if (string.Equals(part, ".git", StringComparison.Ordinal)) return true;
        return false;
    }

    private static void ListInto(string path, JobParameters parameters,
        Dictionary<string, object?> output)
    {
        var pattern = parameters.Get("pattern", "*");
        var limit = parameters.Get<int?>("limit", null) ?? 200;

        // The directory itself, or the directory holding a file that was passed by mistake — a
        // model that lists "src/Foo.cs" meant "the folder Foo.cs is in", and erroring teaches it
        // nothing it can act on.
        var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path;

        // FILTERED BEFORE THE CAP, not after. An ignored file that consumed one of the limit slots
        // would shrink the answer without appearing in it — the cap would report "truncated" while
        // returning fewer real files than asked for.
        var matches = WithoutIgnored(
            Directory.EnumerateFiles(dir, NormalizeGlob(pattern), SearchOption.AllDirectories).ToList(),
            dir);
        var truncated = matches.Count > limit;
        if (truncated) matches = matches.Take(limit).ToList();

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
        foreach (var file in WithoutIgnored(
            Directory.EnumerateFiles(dir, NormalizeGlob(glob), SearchOption.AllDirectories).ToList(), dir))
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

                if (match)
                    hits.Add($"{file}:{i + 1}:{EnclosingScope(lines, i)}{lines[i].Trim()}");
            }
        }

        output["content"] = string.Join('\n', hits);
        output["count"] = hits.Count;
        if (hits.Count >= limit) output["truncated"] = true;
    }

    /// <summary>
    /// The declaration a matched line sits inside, as a "[in Foo]" prefix — or "" when none is
    /// found within a reasonable distance.
    ///
    /// <para>WHY. "file:1196:text" tells a model WHERE a hit is only if it already knows the file's
    /// shape; a NAME tells it what the hit is part of. Measured across three drives on a 1,587-line
    /// file: the model searched for the flag, got a list of line numbers, and never opened the
    /// function that one of them was inside — while correctly describing the bug from the two
    /// endpoints it could name. The bug lived in a third function that neither endpoint mentions and
    /// that only a line number pointed at. A name in the search result is the cheapest possible way
    /// to say "this is in WrapCellLine".</para>
    ///
    /// <para>Scans upward for the nearest line that looks like a declaration and is indented less
    /// than the hit — the same rule a reader's eye uses. Bounded, because an unbounded scan on a
    /// match near the end of a large file would walk the whole thing for every hit.</para>
    /// </summary>
    private static string EnclosingScope(string[] lines, int hit)
    {
        const int MaxScan = 400;
        var hitIndent = LeadingWhitespace(lines[hit], 0).Length;

        for (var i = hit - 1; i >= 0 && hit - i <= MaxScan; i--)
        {
            var line = lines[i];
            if (line.Trim().Length == 0) continue;

            var indent = LeadingWhitespace(line, 0).Length;
            if (indent >= hitIndent) continue;          // same block or deeper: not a parent

            var name = DeclarationName(line);
            if (name is not null) return $"[in {name}] ";
        }
        return "";
    }

    /// <summary>
    /// The declared name on a line, or null if it does not look like a declaration.
    ///
    /// <para>Deliberately language-agnostic and shallow: it recognises the shape "…keyword Name(" or
    /// "…keyword Name" across the C-family, Python, Go, Rust, JS/TS. A wrong guess costs a slightly
    /// misleading label on one search hit, so the bar for including a keyword is low, but anything
    /// that would need real parsing is left out.</para>
    /// </summary>
    private static string? DeclarationName(string line)
    {
        var t = line.Trim();
        if (t.StartsWith("//") || t.StartsWith("*") || t.StartsWith("#")) return null;

        ReadOnlySpan<string> keywords =
        [
            "class ", "struct ", "interface ", "record ", "enum ", "namespace ",
            "def ", "func ", "fn ", "function ", "impl ", "trait ", "module ",
        ];
        foreach (var kw in keywords)
        {
            var at = t.IndexOf(kw, StringComparison.Ordinal);
            if (at < 0) continue;
            var rest = t[(at + kw.Length)..].TrimStart();
            var name = TakeIdentifier(rest);
            if (name.Length > 0) return name;
        }

        // NOT A DECLARATION: an assignment, a `new` expression, or a statement. `var frameIsLink =
        // new Stack<bool>();` ends in ')' and contains '(', so the signature rule below accepted it
        // and labelled every hit in the method "[in Stack]" — a confidently wrong name, which is
        // worse than none because the model has no reason to doubt it. Verified on the real file:
        // this exact line produced that label.
        if (t.Contains('=', StringComparison.Ordinal) && !t.Contains("=>", StringComparison.Ordinal))
            return null;
        if (t.EndsWith(";", StringComparison.Ordinal)) return null;

        // A method/function signature without a leading keyword: "…Name(" with a body or an
        // expression arrow. Requires the paren so a bare call or a field does not qualify.
        var paren = t.IndexOf('(');
        if (paren > 0 && (t.EndsWith("{") || t.EndsWith(")") || t.Contains("=>", StringComparison.Ordinal)))
        {
            var before = t[..paren];
            var lastSpace = before.LastIndexOfAny([' ', '\t', '.', '*', '&']);
            var name = TakeIdentifier(before[(lastSpace + 1)..]);
            if (name.Length > 1 && char.IsLetter(name[0])) return name;
        }

        return null;
    }

    private static string TakeIdentifier(string s)
    {
        var i = 0;
        while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
        return s[..i];
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

        // IDENTICAL IS NOT AN EDIT. This wrote the file and reported "replaced 1 occurrence
        // (indentation adjusted to match the file)" — success, with a note implying something
        // changed, for an operation that changed nothing. A model reading that has been told its
        // edit landed and will move on; the reason it sent the same text twice (a mis-copied
        // replacement, a rewrite it thought it had made) goes unexamined. opencode refuses the same
        // case for the same reason.
        if (string.Equals(pattern, replacement, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "'pattern' and 'replacement' are identical, so this would change nothing. If you "
                + "meant to edit the file, send the text you want it to say instead. Nothing was "
                + "written.");

        // READ THROUGH THE SERVICE, so the conventions this edit must restore come from the SAME
        // read its content is computed against — and so the write below can tell whether the file
        // moved underneath it.
        var snapshot = await FileMutation.ReadAsync(path, ct);
        var text = snapshot.Text;

        var match = FindSingleMatch(text, pattern, path);
        var (first, matchLength) = (match.Start, match.Length);

        // SHIFT ONTO THE FILE'S INDENTATION. Extracted to IndentShift, a pure function over three
        // strings — see its doc for why. The matched span is extended back to the START OF ITS LINE,
        // because the span begins at the pattern's first character and so does not itself contain
        // the indentation being matched against.
        //
        // A match that does not begin a line is a FRAGMENT (`a + b` inside `int t = a + b;`) and has
        // no indentation to correct; prepending one would splice whitespace into a statement.
        // NO CORRECTION AT THE CALL SITE. The matcher normalises a whole-line span to start at the
        // line, so the slice already contains the file's indentation whichever pass found it — and
        // the pattern and replacement go through exactly as the model sent them, so IndentShift can
        // cancel each side's own base independently. That is the entire mechanism.
        //
        // Every previous attempt failed by reconstructing one side here: extending the pattern with
        // the file's leading whitespace while leaving the replacement raw made the two describe
        // different things, and the indent was added on top of indentation already present.
        //
        // A fragment (`a + b` inside `int t = a + b;`) has no indentation to correct at all.
        var original = replacement;
        if (match.WholeLines)
            replacement = IndentShift.Apply(
                text.Substring(first, matchLength), pattern, replacement);

        // PRESERVE THE BOM. File.WriteAllTextAsync writes UTF-8 without one regardless of what the
        // file had, so editing one line of a BOM'd file silently rewrote its first three bytes —
        // caught live: HexEncoder.cs went from EF BB BF to 2F 2F on a two-line insertion. In a C#
        // repo that is a spurious diff on every file an implementer touches, and the kind of change
        // nobody attributes to the agent that made it.
        // CONDITIONALLY, because this is a read-modify-write and the file may have moved underneath
        // it. The per-path lock covers agents inside this process; it says nothing about the user's
        // editor, a formatter or a git checkout. Applying an edit computed from content that no
        // longer exists succeeds, reports success, and silently reverts whatever happened in
        // between. The service restores the BOM and line endings from the snapshot this edit was
        // computed against — see FileMutation for why both have to come from the file rather than
        // from the replacement.
        var result = text[..first] + replacement + text[(first + matchLength)..];
        await FileMutation.WriteIfUnchangedAsync(path, result, snapshot, ct);

        // SHOW WHAT LANDED. The old result said only "replaced 1 occurrence", so a model with any
        // doubt about its edit had exactly one way to check: shell out. Measured live — an agent
        // followed a correct replace with `cat -A`, mistrusted what it saw, reverted its own fix and
        // spent the rest of the run patching through run_shell. Echoing the written line closes that
        // loop in-band, and saying the indentation was adjusted explains the difference it would
        // otherwise discover and misread as damage.
        // BY VALUE, not by reference. IndentShift returns a fresh string from string.Join even when
        // it changed nothing, so a reference check reported "indentation adjusted" on every edit —
        // including the ones where the model got it exactly right. A note that always appears is a
        // note nobody reads, which costs the one case where it matters.
        var note = string.Equals(original, replacement, StringComparison.Ordinal)
            ? ""
            : " (indentation adjusted to match the file)";

        output["content"] = $"replaced 1 occurrence in {path}{note}\n"
            + $"the file now reads:\n{QuoteWritten(replacement)}";
        output["bytes_before"] = text.Length;
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

    /// <summary>
    /// Writes a file, keeping the UTF-8 BOM it already had.
    ///
    /// <para>THE SAME BUG REPLACE ALREADY FIXED, arriving through the other door. That path preserves
    /// the BOM because a live drive turned HexEncoder.cs from EF BB BF into 2F 2F on a two-line
    /// insertion — a spurious diff on every touched file in a C# repo, and one nobody attributes to
    /// the agent that made it. The fix went into ReplaceAsync only, so overwriting the SAME file with
    /// write_file stripped it again: identical symptom, different tool, and which one the model
    /// happened to pick decided whether the repo stayed clean.</para>
    ///
    /// <para>WHAT THE FILE HAD, not what the content carries. A model reproducing a file it read
    /// cannot see the BOM (ReadAllTextAsync strips it), so the content it sends never has one and
    /// asking the content would always answer "no". The bytes on disk are the only witness.</para>
    ///
    /// <para>Returns whether the file already existed, which the caller reports: "created" and
    /// "overwrote" are different events and a model that clobbered a file it meant to create has no
    /// other way to find out.</para>
    /// </summary>
    private static async Task<bool> WritePreservingBomAsync(string path, string content,
        bool append, CancellationToken ct)
    {
        var existed = File.Exists(path);

        // A NEW FILE GETS NO BOM, and an appended one keeps whatever it had — appending must never
        // insert a BOM into the middle of a file, which is what encoding a fresh UTF8Encoding(true)
        // onto an append stream would do.
        var hadBom = existed && await StartsWithBomAsync(path, ct);
        var encoding = new System.Text.UTF8Encoding(
            encoderShouldEmitUTF8Identifier: hadBom && !append);

        // THE FILE'S LINE ENDINGS TOO, the same reasoning as the BOM and as ReplaceAsync's. A model
        // reproducing a file it read sends bare \n, because a tool result cannot show it otherwise;
        // writing that over a CRLF file rewrites every line in the diff. A file that does not exist
        // yet has no convention to keep, so its content goes through untouched.
        if (existed && await UsesCrlfAsync(path, ct))
            content = content.Replace("\r\n", "\n", StringComparison.Ordinal)
                             .Replace("\n", "\r\n", StringComparison.Ordinal);

        EnsureParentDirectory(path);
        if (append)
            await File.AppendAllTextAsync(path, content, encoding, ct);
        else
            await File.WriteAllTextAsync(path, content, encoding, ct);

        return existed;
    }


    /// <summary>Whether the file already uses CRLF, read from disk because the content a model sends
    /// never does — see the callers for why that matters.</summary>
    private static async Task<bool> UsesCrlfAsync(string path, CancellationToken ct)
    {
        try
        {
            var text = await File.ReadAllTextAsync(path, ct);
            return text.Contains("\r\n", StringComparison.Ordinal);
        }
        catch (Exception) { return false; }
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
    /// The single place <paramref name="pattern"/> occurs, or a throw naming what went wrong.
    ///
    /// <para>Locating is <see cref="PatternMatcher"/>'s job; this decides what to do about the
    /// count. EXACTLY ONE, or nothing is written: an ambiguous match means the model does not know
    /// which occurrence it is editing, and picking one silently is how the wrong line gets changed
    /// in a file nobody is watching.</para>
    /// </summary>
    private static PatternMatch FindSingleMatch(string text, string pattern, string path)
    {
        // WHITESPACE IS NOT A TARGET. The matcher squashes whitespace deliberately, so a pattern
        // made only of it describes every blank line at once and nothing in particular — and a model
        // that sent one meant something it did not manage to express. Refused with a sentence it can
        // act on, rather than a count of matches it never intended.
        if (pattern.Trim().Length == 0)
            throw new InvalidOperationException(
                "'pattern' is only whitespace, which matches nothing in particular — indentation and "
                + "blank lines are deliberately ignored when matching. Send the text of the lines to "
                + "change. Nothing was written.");

        var matches = PatternMatcher.FindAll(text, pattern);

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
                + "written."
                // WHERE THEY ARE, not just how many. The not-found case already shows candidate
                // lines (NearestLines); this one said "appears 7 times" and stopped, leaving the
                // model to invent seven disambiguating patterns against a file it must now re-read.
                //
                // This is also the answer to replaceAll, which was declined deliberately: a flag
                // that rewrites every match at once is the same edit with the review removed, and
                // the tolerant matcher makes its blast radius genuinely hard to predict — `count`
                // matches inside string literals and comments too. Showing the sites keeps each edit
                // individually aimed and reviewable, which is the property worth keeping, while
                // removing most of what made refusing expensive.
                + MatchSites(text, matches));

        return matches[0];
    }

    /// <summary>
    /// Where an ambiguous pattern matched — line numbers and the line itself, capped.
    ///
    /// <para>Capped at five because the point is to let the model AIM, not to reproduce the file: a
    /// pattern matching forty times is one the model should reconsider rather than disambiguate, and
    /// forty lines of tool result spends context to say so.</para>
    /// </summary>
    private static string MatchSites(string text, IReadOnlyList<PatternMatch> matches)
    {
        var normalised = text.Replace("\r\n", "\n");
        var shown = new List<string>();

        foreach (var m in matches.Take(5))
        {
            // The line a match starts on: count the newlines before it.
            var upto = Math.Min(m.Start, normalised.Length);
            var line = normalised.AsSpan(0, upto).Count('\n') + 1;
            var lineText = normalised.Split('\n').ElementAtOrDefault(line - 1)?.Trim() ?? "";
            shown.Add($"  line {line}: {(lineText.Length > 100 ? lineText[..100] + "…" : lineText)}");
        }

        if (matches.Count > shown.Count) shown.Add($"  … and {matches.Count - shown.Count} more");

        return "\n\nIt matches at:\n" + string.Join('\n', shown);
    }

    /// <summary>
    /// A path as the model wrote it, made absolute against the agent's folder.
    ///
    /// <para>An ALREADY-ABSOLUTE path is returned untouched — GetFullPath ignores the base for one,
    /// and normalising it is still worth doing so `/tmp/x/../y` and `/tmp/y` are one path to every
    /// layer that compares them, the permission gate included.</para>
    ///
    /// <para>NO ROOT MEANS THE PROCESS'S, which is what happened everywhere before this existed —
    /// so a caller without an opinion (a test, a headless job) keeps the old behaviour rather than
    /// being handed an empty base.</para>
    /// </summary>
    private static string Resolve(string path, IJobContext context)
    {
        var resolved = context.WorkingDirectory is { Length: > 0 } root
            ? Path.GetFullPath(path, root)
            : Path.GetFullPath(path);

        // A DROPPED LEADING SLASH, NAMED AS ITSELF. A model that means /tmp/x/App.cs and sends
        // "tmp/x/App.cs" has written a RELATIVE path that looks absolute, and resolving it against
        // /tmp/x is correct — it yields /tmp/x/tmp/x/App.cs, which does not exist.
        //
        // The framework's message for that is "Could not find a part of the path
        // '/tmp/x/tmp/x/App.cs'". Everything needed to see the mistake is in there, and a model
        // reading it concludes the FILE is missing rather than that its path was malformed: it then
        // globs for the file, gets the same treatment, and spends its run hunting something it
        // already had the path to.
        //
        // MEASURED: 450 such errors across three consecutive drives, none before them. One planner
        // burned all eleven of its turns this way and returned without writing anything, which read
        // as the agent ignoring its briefing rather than as a path bug.
        //
        // So say it. The check is cheap and only fires on a path that genuinely repeats the root.
        if (context.WorkingDirectory is { Length: > 0 } wd && !File.Exists(resolved) && !Directory.Exists(resolved))
        {
            var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(wd));
            var doubled = normalizedRoot + Path.DirectorySeparatorChar
                        + normalizedRoot.TrimStart(Path.DirectorySeparatorChar);

            if (resolved.StartsWith(doubled, StringComparison.Ordinal))
                throw new FileNotFoundException(
                    $"'{path}' looks like an absolute path with its leading separator missing, so it "
                    + $"resolved to '{resolved}'. Send '{Path.DirectorySeparatorChar}{path}' for the "
                    + "absolute path, or a path relative to the working directory.");
        }

        return resolved;
    }

    /// <summary>
    /// Creates the directory a write is about to land in, when it does not already exist.
    ///
    /// <para>WITHOUT THIS, WRITING TO A NEW SUBDIRECTORY THROWS. File.WriteAllTextAsync does not
    /// create parent directories, so `write_file` to `plans/design.md` in a repo with no `plans/`
    /// fails with DirectoryNotFoundException — and the model has no way to tell that apart from a
    /// permission problem or a bad path.</para>
    ///
    /// <para>OBSERVED, not theorised. In a live drive the agent wrote a plan to `./plans/x.md`, the
    /// write failed, it ran `mkdir -p plans` on the next turn to fix it, and then never retried the
    /// write — leaving an empty directory and a downstream agent pointed at a file that did not
    /// exist. Two turns spent, and the failure survived both.</para>
    ///
    /// <para>THE BOUNDARY IS UNAFFECTED. The path was resolved and permission-checked before this
    /// runs, so creating its parent cannot reach anywhere the write itself could not. A path with no
    /// directory part (a bare filename) yields null or empty here and is left alone.</para>
    /// </summary>
    private static void EnsureParentDirectory(string path)
    {
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
            Directory.CreateDirectory(parent);
    }


    public async Task<JobResult> ExecuteAsync(JobParameters parameters, IJobContext context, CancellationToken ct)
    {
        var action = parameters.Get<string>("action");

        // RESOLVED AGAINST THE AGENT'S FOLDER, ONCE, before any action sees it — which is what the
        // system prompt already tells the model happens ("Relative paths resolve from the working
        // directory"). Until the context carried a root that was true only by coincidence: an
        // unqualified `src/foo.cs` went to the framework, which resolved it against the PROCESS
        // directory, and the two agreed because nothing ever moved the process.
        //
        // THE DANGEROUS CASE IS NOT A FAILED READ, it is a successful write. With a second session
        // rooted elsewhere, the gate checks `src/foo.cs` against THIS session's root and allows it,
        // then the framework resolves it against the other's — so the edit lands in a checkout the
        // user never approved, with every layer behaving correctly on the way.
        //
        // Doing it HERE covers every action including both `dest` reads below; doing it per-action
        // is the same per-field-fallback mistake the sub-agent runtime is careful to avoid.
        var start = DateTimeOffset.UtcNow;
        try
        {
            // RESOLVED INSIDE THE TRY, so a bad path becomes a failed RESULT rather than an escaping
            // exception. It sat outside, which meant the one error most worth explaining to a model
            // — a path it can fix — was the one that bypassed the plugin's own error handling.
            // PATH DEFAULTS TO THE WORKING DIRECTORY FOR THE SEARCHING ACTIONS, which is what makes
            // it optional on the `glob` and `grep` tools. Everything else needs a real target and
            // still fails without one — a `write` with no path is a mistake, not a search of ".".
            //
            // THE TWO-ARGUMENT OVERLOAD, because the one-argument form is Values[key] and THROWS on
            // an absent key. Making `path` optional on glob/grep without changing this line meant
            // every call that took the schema at its word — `grep {"pattern":"X"}`, exactly what the
            // tool now advertises — came back as "The given key 'path' was not present in the
            // dictionary", 174 times in one session. The schema said optional; the code demanded it.
            var requested = parameters.Get<string?>("path", null);
            if (string.IsNullOrWhiteSpace(requested) && action is "list" or "search")
                requested = ".";

            // Every other action requires one, and Validate has already said so with a message the
            // model can act on — this only stops a null reaching Resolve if that guard ever moves.
            if (string.IsNullOrWhiteSpace(requested))
                return new JobResult
                {
                    Success = false,
                    ErrorMessage = $"'path' is required for action '{action}'.",
                };
            var path = Resolve(requested, context);

            var output = new Dictionary<string, object?>();

            // SERIALISED PER FILE for anything that MUTATES one. Reads and searches are left free:
            // they cannot corrupt anything, and locking them would serialise the common case to
            // guard against nothing.
            //
            // AROUND THE WHOLE ACTION, not just the write. `replace` is read-modify-write, so a lock
            // held only across the write leaves exactly the race worth closing — both agents read
            // the same text, both match, and the second computes its edit from a version that no
            // longer exists by the time it writes.
            //
            // The lock is on the RESOLVED path. copy and move touch a second file, and taking two
            // locks is how deadlock arrives (A→B in one agent, B→A in another); the source is the
            // one being read and is the one that matters here.
            // SERIALISED PER FILE for anything that MUTATES one, through FileMutation — the lock
            // table is a process-wide fact about paths, so it belongs with the writer rather than
            // here. Reads and searches are left free: they cannot corrupt anything, and locking them
            // would serialise the common case to guard against nothing.
            var mutates = action is not ("read" or "list" or "search");
            var gate = mutates ? FileMutation.LockHandleFor(path) : null;
            if (gate is not null) await gate.WaitAsync(ct);
            try
            {
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
                    case "create":
                    {
                        // CREATE MEANS NEW. Refused rather than falling back to a write: the caller
                        // said "this file should not exist yet", and quietly overwriting turns a
                        // stated expectation into a silent replacement.
                        var made = await FileMutation.CreateNewAsync(
                            path, parameters.Get<string>("content"), ct);
                        if (!made)
                            return new JobResult
                            {
                                Success = false,
                                // NO TOOL NAMED. A plugin cannot see the selection, and this string
                                // is read at the moment of a failure — the worst time to send the
                                // model after a tool it may not have been offered. What happened and
                                // why is the useful part; which tool to reach for next is a question
                                // the offered list already answers.
                                ErrorMessage = $"{path} already exists, and 'create' will not replace "
                                             + "a file. Read it first: if replacing it is what you "
                                             + "meant, do that explicitly. Nothing was written.",
                            };
                        output["created"] = true;
                        output["content"] = $"created {path}";
                        break;
                    }
                    case "write":
                    {
                        var existed = await WritePreservingBomAsync(
                            path, parameters.Get<string>("content"), append: false, ct);
                        // CREATED OR OVERWROTE, said out loud. The result used to carry neither, so an
                        // agent that meant to create a file and silently replaced one had nothing in the
                        // tool result to notice it by.
                        output["created"] = !existed;
                        output["content"] = existed ? $"overwrote {path}" : $"created {path}";
                        break;
                    }
                    case "append":
                    {
                        var existed = await WritePreservingBomAsync(
                            path, parameters.Get<string>("content"), append: true, ct);
                        output["created"] = !existed;
                        output["content"] = existed ? $"appended to {path}" : $"created {path}";
                        break;
                    }
                    case "delete":
                        File.Delete(path);
                        break;
                    case "copy":
                        File.Copy(path, Resolve(parameters.Get<string>("dest"), context), overwrite: true);
                        break;
                    case "move":
                        File.Move(path, Resolve(parameters.Get<string>("dest"), context), overwrite: true);
                        break;
                }
            }
            finally
            {
                gate?.Release();
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
