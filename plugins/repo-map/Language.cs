namespace CxAgent.Plugins.RepoMap;

/// <summary>How a language's declarations are found: by the braces that open their bodies, or by
/// the indentation that does.</summary>
public enum Shape { Braces, Indent }

/// <summary>Which declaration rules apply. Several languages share one: C#, Java, C++ and their
/// relatives declare a method as "words, then a name, then a parameter list", and one rule reads
/// all of them.</summary>
public enum Family { CLike, Script, Go, Rust, Python, Ruby, Shell }

/// <summary>
/// What the stripper and the outliner need to know about one language — its comments, its strings,
/// and which declaration rules read it.
///
/// <para>A TABLE, NOT A PARSER PER LANGUAGE. A map needs declarations and nothing else, and the
/// syntax that hides a declaration is the same few things everywhere: a comment, a string, a body.
/// Getting those three right per language is what lets one set of rules read a dozen languages; a
/// real parser for each would be a dependency this plugin exists to avoid.</para>
/// </summary>
public sealed record Language(string Name, Family Family)
{
    public Shape Shape => Family is Family.Python or Family.Ruby ? Shape.Indent : Shape.Braces;

    /// <summary><c>//</c> line comments and <c>/* */</c> block comments.</summary>
    public bool SlashComments { get; init; }

    /// <summary><c>#</c> line comments.
    ///
    /// <para>NOT FOR C OR C#: there <c>#</c> starts a preprocessor line, which the outliner skips
    /// whole — treating it as a comment would also blank a <c>#define</c> the next line continues.</para></summary>
    public bool HashComments { get; init; }

    /// <summary>Backtick strings that may span lines — JavaScript's templates, Go's raw strings.</summary>
    public bool Backticks { get; init; }

    /// <summary>Python's <c>"""</c> and <c>'''</c>, and C#'s raw <c>"""</c> strings.</summary>
    public bool TripleQuotes { get; init; }

    /// <summary>C#'s <c>@"…"</c>, where a quote is escaped by doubling it and a backslash is a character.</summary>
    public bool VerbatimStrings { get; init; }

    /// <summary>
    /// Whether <c>'</c> may open something other than a string.
    ///
    /// <para>RUST'S LIFETIMES. <c>&amp;'a str</c> has no closing quote, and reading it as a string
    /// would blank everything to the end of the line — the parameter list, and the brace that opens
    /// the function's body with it.</para>
    /// </summary>
    public bool Lifetimes { get; init; }

    /// <summary>
    /// Whether a body's <c>{</c> is always on its declaration's line.
    ///
    /// <para>NOT FOR C#, C OR PHP, where the brace on a line of its own is a common style. Go
    /// requires it on the same line, and the JavaScript and JVM languages' formatters put it there; and
    /// knowing it lets a declaration with no body end at its line even without a semicolon.</para>
    /// </summary>
    public bool SameLineBraces { get; init; }

    /// <summary>
    /// Whether an ordinary quoted string may run across lines — Rust's and PHP's do, and a help text
    /// or an SQL query written that way is common.
    ///
    /// <para>NOT THE DEFAULT. Where a string cannot span lines, ending one at the newline keeps a
    /// quote this cannot read local to its line; where it can, ending it there reads the rest of
    /// the string as code, and its braces hide every declaration after it.</para>
    ///
    /// <para>NOT RUBY, though its strings span lines too: a regex literal with a quote in it
    /// (sinatra's <c>/="(?:[^"\\]|\\.)*"/</c>), <c>%r{…}</c> and heredocs are syntax this does not read,
    /// and one odd quote among them would then swallow the rest of the file instead of a line.</para>
    /// </summary>
    public bool SpanningStrings { get; init; }

    /// <summary>C#'s <c>{ get; }</c> properties, which carry no parameter list for the method rule to find.</summary>
    public bool Properties { get; init; }

    /// <summary>
    /// JavaScript's <c>/…/</c> regex literals.
    ///
    /// <para>READ AS CODE, ONE WITH A QUOTE OR A BACKTICK IN IT OPENS A STRING THAT IS NOT THERE. A
    /// backtick's template string spans lines, so everything up to the next backtick in the file —
    /// declarations included — would be blanked.</para>
    /// </summary>
    public bool RegexLiterals { get; init; }

    /// <summary>
    /// The file's language, or null when it is not one this plugin reads.
    ///
    /// <para>BY EXTENSION ONLY. Sniffing content would cost a read of every file in a large tree to
    /// save a handful of extensionless scripts.</para>
    /// </summary>
    public static Language? For(string path)
    {
        var name = Path.GetFileName(path);
        // MINIFIED BUNDLES ARE BUILD OUTPUT that happens to be committed: one line, thousands of
        // generated names, nothing a reader declared.
        if (name.EndsWith(".min.js", StringComparison.OrdinalIgnoreCase)) return null;
        // A BLADE VIEW IS A TEMPLATE: HTML with {{ }} tags, ending in .php so the framework finds it.
        if (name.EndsWith(".blade.php", StringComparison.OrdinalIgnoreCase)) return null;

        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".cs" => CSharp,
            ".java" or ".kt" or ".kts" or ".swift" or ".dart" => Jvm,
            ".scala" => Scala,
            ".c" or ".h" or ".cpp" or ".hpp" or ".cc" or ".hh" or ".cxx" or ".hxx" or ".m" or ".mm" => C,
            ".php" => Php,
            ".js" or ".jsx" or ".mjs" or ".cjs" or ".ts" or ".tsx" or ".mts" or ".cts" => Script,
            ".go" => Go,
            ".rs" => Rust,
            ".py" or ".pyi" => Python,
            ".rb" => Ruby,
            ".sh" or ".bash" or ".zsh" => Shell,
            _ => null,
        };
    }

    public static readonly Language CSharp = new("C#", Family.CLike)
    { SlashComments = true, TripleQuotes = true, VerbatimStrings = true, Properties = true };

    public static readonly Language Jvm = new("JVM", Family.CLike) { SlashComments = true, TripleQuotes = true, SameLineBraces = true };

    /// <summary>
    /// SCALA HAS ITS OWN RULES, unlike the other JVM languages: its generics are in brackets, a def
    /// may have no parameter list or several, and a body follows <c>=</c> — none of which the
    /// "words, a name, a parameter list" rule reads.
    /// </summary>
    public static readonly Language Scala = new("Scala", Family.CLike) { SlashComments = true, TripleQuotes = true, SameLineBraces = true };

    public static readonly Language C = new("C", Family.CLike) { SlashComments = true };

    public static readonly Language Php = new("PHP", Family.CLike) { SlashComments = true, HashComments = true, SpanningStrings = true };

    public static readonly Language Script = new("JavaScript", Family.Script) { SlashComments = true, Backticks = true, SameLineBraces = true, RegexLiterals = true };

    public static readonly Language Go = new("Go", Family.Go) { SlashComments = true, Backticks = true, SameLineBraces = true };

    public static readonly Language Rust = new("Rust", Family.Rust) { SlashComments = true, Lifetimes = true, SpanningStrings = true };

    public static readonly Language Python = new("Python", Family.Python) { HashComments = true, TripleQuotes = true };

    public static readonly Language Ruby = new("Ruby", Family.Ruby) { HashComments = true };

    public static readonly Language Shell = new("Shell", Family.Shell) { HashComments = true };
}
