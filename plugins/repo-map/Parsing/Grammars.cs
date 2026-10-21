using System.Reflection;

namespace CxAgent.Plugins.RepoMap.Parsing;

/// <summary>One Tree-sitter grammar this plugin reads with.</summary>
/// <param name="Id">The binding's language id, which names the native library: <c>c-sharp</c> is <c>libtree-sitter-c-sharp</c>.</param>
/// <param name="Name">How a note in the map's header names the language.</param>
/// <param name="Queries">The embedded query files, in the order they are joined.</param>
public sealed record Grammar(string Id, string Name, IReadOnlyList<string> Queries);

/// <summary>
/// Which grammar reads a file, and the query that finds its declarations and references.
///
/// <para>A LANGUAGE WITHOUT ONE IS READ AS TEXT. Kotlin, Swift, Dart and Objective-C have no grammar
/// in the pinned package, and <see cref="For"/> returning null is the normal path for them, not a
/// failure.</para>
///
/// <para>QUERIES ARE JOINED, NOT COPIED. TypeScript's upstream query builds on JavaScript's, so a
/// TypeScript grammar runs both files; JavaScript-only and JSX-only patterns live in files of their
/// own, because a pattern naming a node a grammar lacks fails the whole query.</para>
/// </summary>
public static class Grammars
{
    public static readonly Grammar C = new("c", "C", ["c.scm"]);
    public static readonly Grammar Cpp = new("cpp", "C++", ["cpp.scm"]);
    public static readonly Grammar CSharp = new("c-sharp", "C#", ["c-sharp.scm"]);
    public static readonly Grammar Java = new("java", "Java", ["java.scm"]);
    public static readonly Grammar Scala = new("scala", "Scala", ["scala.scm"]);
    public static readonly Grammar JavaScript = new("javascript", "JavaScript", ["javascript.scm", "javascript-only.scm", "jsx.scm"]);
    public static readonly Grammar TypeScript = new("typescript", "TypeScript", ["javascript.scm", "typescript.scm"]);
    public static readonly Grammar Tsx = new("tsx", "TSX", ["javascript.scm", "typescript.scm", "jsx.scm"]);
    public static readonly Grammar Go = new("go", "Go", ["go.scm"]);
    public static readonly Grammar Rust = new("rust", "Rust", ["rust.scm"]);
    public static readonly Grammar Python = new("python", "Python", ["python.scm"]);
    public static readonly Grammar Ruby = new("ruby", "Ruby", ["ruby.scm"]);
    public static readonly Grammar Php = new("php", "PHP", ["php.scm"]);
    public static readonly Grammar Bash = new("bash", "Shell", ["bash.scm"]);

    public static IReadOnlyList<Grammar> All { get; } =
        [C, Cpp, CSharp, Java, Scala, JavaScript, TypeScript, Tsx, Go, Rust, Python, Ruby, Php, Bash];

    /// <summary>
    /// The grammar for a file, or null for the text reader. By extension only, as <see cref="Language.For"/> is.
    ///
    /// <para>A .h IS READ AS C++. The C++ grammar parses a C header; the C grammar fails on a C++ one,
    /// and the extension cannot say which a header is.</para>
    /// </summary>
    public static Grammar? For(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".c" => C,
        ".h" or ".cpp" or ".hpp" or ".cc" or ".hh" or ".cxx" or ".hxx" => Cpp,
        ".cs" => CSharp,
        ".java" => Java,
        ".scala" => Scala,
        ".js" or ".jsx" or ".mjs" or ".cjs" => JavaScript,
        ".ts" or ".mts" or ".cts" => TypeScript,
        ".tsx" => Tsx,
        ".go" => Go,
        ".rs" => Rust,
        ".py" or ".pyi" => Python,
        ".rb" => Ruby,
        ".php" => Php,
        ".sh" or ".bash" or ".zsh" => Bash,
        _ => null,
    };

    /// <summary>A grammar's query: its files, joined in order.</summary>
    public static string QueryText(Grammar grammar) =>
        string.Join("\n", grammar.Queries.Select(Resource));

    private static string Resource(string file)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"RepoMap.Queries.{file}")
            ?? throw new InvalidOperationException($"the query {file} is not embedded in repo-map.dll");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
