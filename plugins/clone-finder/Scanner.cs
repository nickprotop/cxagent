using System.Text;
using System.Text.RegularExpressions;

namespace CxAgent.Plugins.CloneFinder;

/// <summary>Everything one scan needs, carried as one concept: the detector's floors ride along
/// with the path so the tool layer hands a single request through Scanner, Detector and Report
/// instead of re-threading four loose parameters past each other (AV1561 — transposing two ints
/// in that list compiles cleanly and scans with the wrong floor). Defaults are jscpd's shipped
/// pair, settled by benchmarking: either floor alone lets through what the other exists to stop.</summary>
public record ScanRequest(string Path, int MinLines = 6, int MinTokens = 50,
    string? Exclude = null, int MaxResults = 20);

/// <summary>Chooses which files a scan reads. Three exclude layers COMPOSE — built-ins, the
/// repository's .gitignore, the caller's globs — because no single one suffices: .gitignore
/// misses a committed vendor/ tree, a built-in list cannot know what this repository ignores,
/// and only the caller knows what this particular run should skip. Tests are scanned: duplicated
/// test setup is real duplication, and excluding it by default would be a strong opinion applied
/// silently.</summary>
public static class Scanner
{
    /// <summary>Skipped whether or not any .gitignore mentions them: build output and vendored
    /// dependency trees are wall-to-wall generated duplicates, and one un-ignored bin/ would
    /// drown every real finding. Whole directory names, so src/distances/ is untouched.</summary>
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.Ordinal)
    {
        "bin", "obj", "node_modules", ".git", "dist", "vendor",
    };

    /// <summary>Only code goes to the tokeniser. The set is the languages its normalisation is
    /// built for — C-family syntax plus the script languages whose `#` comments it already
    /// strips; feeding it markdown or JSON would report prose paragraphs and config stanzas as
    /// clones of each other.</summary>
    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".c", ".h", ".cpp", ".hpp", ".cc", ".hh", ".cxx", ".hxx",
        ".java", ".kt", ".kts", ".scala", ".go", ".rs", ".swift", ".m", ".mm",
        ".js", ".jsx", ".mjs", ".cjs", ".ts", ".tsx",
        ".py", ".rb", ".php", ".sh", ".ps1",
    };

    /// <summary>Every file the request selects, as absolute paths in a stable ordinal order so
    /// the same tree always yields the same report.</summary>
    public static IReadOnlyList<string> Files(ScanRequest request)
    {
        string root = System.IO.Path.GetFullPath(request.Path);

        // Caller globs share the .gitignore rule machinery (same anchoring, same wildcards) so
        // "Migrations/**" means the same thing in both places; they are anchored at the scan
        // root because that is the only directory the caller can see.
        var rules = new List<GitignoreRule>();
        foreach (string pattern in (request.Exclude ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var rule = GitignoreRule.Parse(pattern, root);
            if (rule is not null) rules.Add(rule);
        }
        // Caller rules come after the ancestors' so that, under last-match-wins, an explicit
        // exclude for this run beats a repository-level re-include.
        rules.InsertRange(0, AncestorRules(root));

        var files = new List<string>();
        Walk(root, rules, files);
        files.Sort(StringComparer.Ordinal);
        return files;
    }

    /// <summary>.gitignore files ABOVE the scan root still govern it: scanning one project
    /// inside a solution must honour the repository's ignore file, which lives at the repo root,
    /// not in the subdirectory being scanned. The climb stops at the directory holding .git —
    /// past the repository boundary an unrelated ignore file has no authority here — or at the
    /// filesystem root when there is no repository at all. Outermost first, so a deeper file
    /// wins under last-match-wins.</summary>
    private static List<GitignoreRule> AncestorRules(string root)
    {
        var directories = new List<string>();
        for (string? dir = root; dir is not null; dir = System.IO.Path.GetDirectoryName(dir))
        {
            directories.Add(dir);
            if (Directory.Exists(System.IO.Path.Combine(dir, ".git")) ||
                File.Exists(System.IO.Path.Combine(dir, ".git"))) break;
        }
        directories.Reverse();

        var rules = new List<GitignoreRule>();
        foreach (string dir in directories) rules.AddRange(LoadGitignore(dir));
        return rules;
    }

    private static void Walk(string directory, List<GitignoreRule> inherited, List<string> files)
    {
        var local = LoadGitignore(directory);
        // The scan root's own .gitignore is already in the inherited set (AncestorRules includes
        // the root itself), so skip it here or its rules would apply twice — harmless for
        // ignores, wrong for negations.
        var rules = local.Count == 0 || inherited.Any(r => r.Base == directory)
            ? inherited
            : [.. inherited, .. local];

        foreach (string sub in Directory.EnumerateDirectories(directory))
        {
            string name = System.IO.Path.GetFileName(sub);
            if (ExcludedDirectories.Contains(name)) continue;
            // An ignored directory is pruned whole rather than consulted per file. A negation
            // that re-includes a child of an ignored directory is therefore not honoured — the
            // trade git itself makes, and the alternative is testing every file below against
            // rules that almost always say no.
            if (Ignored(sub, isDirectory: true, rules)) continue;
            Walk(sub, rules, files);
        }

        foreach (string file in Directory.EnumerateFiles(directory))
        {
            if (!CodeExtensions.Contains(System.IO.Path.GetExtension(file))) continue;
            if (Ignored(file, isDirectory: false, rules)) continue;
            files.Add(file);
        }
    }

    /// <summary>Last match wins, as in git: a later negation can rescue what an earlier pattern
    /// ignored, so every rule must be consulted rather than stopping at the first hit.</summary>
    private static bool Ignored(string path, bool isDirectory, List<GitignoreRule> rules)
    {
        bool ignored = false;
        foreach (var rule in rules)
        {
            if (rule.DirectoryOnly && !isDirectory) continue;
            if (rule.Matches(path)) ignored = !rule.Negated;
        }
        return ignored;
    }

    private static List<GitignoreRule> LoadGitignore(string directory)
    {
        string path = System.IO.Path.Combine(directory, ".gitignore");
        if (!File.Exists(path)) return [];

        var rules = new List<GitignoreRule>();
        foreach (string line in File.ReadAllLines(path))
        {
            var rule = GitignoreRule.Parse(line, directory);
            if (rule is not null) rules.Add(rule);
        }
        return rules;
    }
}

/// <summary>One .gitignore pattern, compiled once and matched against paths relative to the
/// directory whose .gitignore declared it — a pattern's meaning depends on where it was written,
/// so the base directory travels with the rule.</summary>
internal sealed class GitignoreRule
{
    private readonly Regex _regex;
    public string Base { get; }
    public bool Negated { get; }
    public bool DirectoryOnly { get; }

    private GitignoreRule(Regex regex, string baseDirectory, bool negated, bool directoryOnly)
    {
        _regex = regex;
        Base = baseDirectory;
        Negated = negated;
        DirectoryOnly = directoryOnly;
    }

    public static GitignoreRule? Parse(string line, string baseDirectory)
    {
        string pattern = line.TrimEnd();
        if (pattern.Length == 0 || pattern.StartsWith('#')) return null;

        bool negated = pattern.StartsWith('!');
        if (negated) pattern = pattern[1..];

        bool directoryOnly = pattern.EndsWith('/');
        if (directoryOnly) pattern = pattern.TrimEnd('/');
        if (pattern.Length == 0) return null;

        // A slash anywhere in the body anchors the pattern to its .gitignore's directory;
        // without one, "*.g.cs" means "at any depth", which the (^|/) alternative provides.
        bool anchored = pattern.Contains('/');
        if (anchored) pattern = pattern.TrimStart('/');

        string body = Translate(pattern);
        string prefix = anchored ? "^" : "(?:^|/)";
        return new GitignoreRule(
            new Regex(prefix + body + "$", RegexOptions.Compiled | RegexOptions.CultureInvariant),
            baseDirectory, negated, directoryOnly);
    }

    public bool Matches(string path)
    {
        string relative = System.IO.Path.GetRelativePath(Base, path).Replace('\\', '/');
        // Outside the base entirely (a ".." step) means the rule has nothing to say.
        if (relative.StartsWith("..", StringComparison.Ordinal)) return false;
        return _regex.IsMatch(relative);
    }

    /// <summary>Gitignore glob to regex. `**` crosses directory separators, `*` and `?` do not —
    /// collapsing that distinction would let "*.cs" anchored at the root match every file in the
    /// tree. Character classes pass through (with `!` negation becoming `^`) because real ignore
    /// files lean on them: the stock Visual Studio template is full of `[Bb]in/`.</summary>
    private static string Translate(string pattern)
    {
        var regex = new StringBuilder();
        int i = 0;
        while (i < pattern.Length)
        {
            char c = pattern[i];
            if (c == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                // "**/" swallows zero or more whole directories; any other "**" — trailing
                // (the pattern's '/' before it was consumed as a literal) or mid-segment — is
                // "anything, slashes included".
                if (i + 2 < pattern.Length && pattern[i + 2] == '/') { regex.Append("(?:.*/)?"); i += 3; }
                else { regex.Append(".*"); i += 2; }
            }
            else if (c == '*') { regex.Append("[^/]*"); i++; }
            else if (c == '?') { regex.Append("[^/]"); i++; }
            else if (c == '[')
            {
                int end = pattern.IndexOf(']', i + 1);
                if (end < 0) { regex.Append(Regex.Escape("[")); i++; continue; }
                string cls = pattern[(i + 1)..end];
                if (cls.StartsWith('!')) cls = "^" + cls[1..];
                regex.Append('[').Append(cls).Append(']');
                i = end + 1;
            }
            else { regex.Append(Regex.Escape(c.ToString())); i++; }
        }
        return regex.ToString();
    }
}
