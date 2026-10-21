using System.Text;
using System.Text.RegularExpressions;

namespace CxAgent.Plugins.RepoMap;

/// <summary>A type a file declares, or something callable inside one or at its top level.</summary>
///
/// <para>A NAMESPACE IS NEITHER. It opens a scope the outliner must track, but listing it would add
/// a line to every file and indent everything beneath it, telling a reader nothing the path does
/// not.</para>
public enum SymbolKind { Type, Member, Namespace }

/// <summary>One declaration: its name, what it says, and where.</summary>
/// <param name="Name">What other code calls it by — what the ranker looks for elsewhere.</param>
/// <param name="Signature">The declaration as written, without its body.</param>
/// <param name="Line">1-based, so a reader can open the file at it.</param>
/// <param name="Depth">How many types or namespaces it sits inside, for indenting the map.</param>
public sealed record Symbol(string Name, string Signature, int Line, int Depth, SymbolKind Kind);

/// <summary>
/// The declarations in one file.
///
/// <para>A DECLARATION IS SHOWN ONLY WHERE IT CAN BE CALLED FROM OUTSIDE ITS BODY: at the top of a
/// file, or inside a type or namespace. Everything inside a function body — local functions,
/// lambdas, the statements — is hidden, because the map answers "what does this file offer", and a
/// helper nested in a method offers nothing to anyone else.</para>
///
/// <para>A HEURISTIC, AND IT FAILS QUIETLY. Where it misreads a line it drops that declaration or
/// shows one line that is not one; it never throws, because one odd file must not cost the map of
/// the rest of the repository.</para>
/// </summary>
public static class Outliner
{
    /// <summary>Longest signature quoted. A parameter list longer than this is cut, and says so.</summary>
    private const int MaxSignature = 160;

    /// <summary>How many lines a signature may continue over before it is cut — a parameter list per
    /// line is common, a declaration longer than this is rare.</summary>
    private const int MaxSignatureLines = 8;

    public static IReadOnlyList<Symbol> Outline(Stripped text, Language language)
    {
        try
        {
            return language.Shape == Shape.Indent ? ByIndent(text, language) : ByBraces(text, language);
        }
        catch (Exception)
        {
            // See the class remarks: a file this cannot read contributes nothing, and the map goes on.
            return [];
        }
    }

    private enum Frame { Container, Body }

    private static List<Symbol> ByBraces(Stripped text, Language language)
    {
        var symbols = new List<Symbol>();
        var frames = new Stack<(Frame Kind, string Name, bool Namespace)>();
        var parens = 0;

        // A declaration whose body has not opened yet. Its "{" may be on a later line (Allman
        // style), and until it opens, that brace belongs to it.
        Frame? pending = null;
        string pendingName = "";
        var pendingNamespace = false;

        for (var i = 0; i < text.Code.Length; i++)
        {
            var line = text.Code[i];
            var trimmed = line.Trim();

            var inContainer = frames.All(f => f.Kind == Frame.Container);
            if (parens == 0 && pending is null && inContainer && trimmed.Length > 0 && !Skipped(trimmed))
            {
                var inType = frames.Any(f => !f.Namespace);
                var typeName = inType ? frames.First(f => !f.Namespace).Name : null;
                var found = Declaration(trimmed, language, inType, typeName);
                if (found is { } d)
                {
                    if (d.Kind != SymbolKind.Namespace)
                        symbols.Add(new Symbol(d.Name, Signature(text, i, language), i + 1,
                            frames.Count(f => !f.Namespace), d.Kind));
                    pending = d.Opens;
                    pendingName = d.Name;
                    pendingNamespace = d.Kind == SymbolKind.Namespace;
                }
            }

            foreach (var c in line)
            {
                switch (c)
                {
                    case '(' or '[': parens++; break;
                    case ')' or ']': parens = Math.Max(0, parens - 1); break;
                    case '{':
                        frames.Push((pending ?? Frame.Body, pending is null ? "" : pendingName,
                            pending is not null && pendingNamespace));
                        pending = null;
                        break;
                    case '}':
                        if (frames.Count > 0) frames.Pop();
                        break;
                    // A DECLARATION THAT ENDS WITHOUT A BODY — an interface method, a forward
                    // declaration, `namespace X;` — gives up its claim on the next brace.
                    case ';' when parens == 0:
                        pending = null;
                        break;
                }
            }

            // WHERE THE BODY'S BRACE IS WRITTEN ON THE DECLARATION'S OWN LINE, a declaration that
            // ends its line without one has no body — Go's interface methods, TypeScript's without a
            // semicolon — and must not claim the next declaration's brace as its own.
            if (language.SameLineBraces && parens == 0) pending = null;
        }

        return symbols;
    }

    private static List<Symbol> ByIndent(Stripped text, Language language)
    {
        var symbols = new List<Symbol>();
        // Every def and class opens a frame, shown or not: a def inside a def is hidden only if
        // the outer one is remembered.
        var frames = new Stack<(int Indent, Frame Kind)>();
        var parens = 0;

        for (var i = 0; i < text.Code.Length; i++)
        {
            var line = text.Code[i];
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            if (parens == 0)
            {
                var indent = Indent(line);
                while (frames.Count > 0 && frames.Peek().Indent >= indent) frames.Pop();

                var match = language.Family == Family.Python ? PythonDecl.Match(trimmed) : RubyDecl.Match(trimmed);
                if (match.Success)
                {
                    var isType = match.Groups["type"].Success;
                    if (frames.All(f => f.Kind == Frame.Container))
                        symbols.Add(new Symbol(match.Groups["name"].Value, Signature(text, i, language), i + 1,
                            frames.Count, isType ? SymbolKind.Type : SymbolKind.Member));
                    frames.Push((indent, isType ? Frame.Container : Frame.Body));
                }
            }

            foreach (var c in line)
            {
                if (c is '(' or '[' or '{') parens++;
                else if (c is ')' or ']' or '}') parens = Math.Max(0, parens - 1);
            }
        }

        return symbols;
    }

    private static int Indent(string line)
    {
        var width = 0;
        foreach (var c in line)
        {
            if (c == ' ') width++;
            else if (c == '\t') width += 4;
            else break;
        }
        return width;
    }

    private static readonly Regex PythonDecl = new(
        @"^(?:async\s+)?(?:def\s+(?<name>\w+)|(?<type>class)\s+(?<name>\w+))", RegexOptions.Compiled);

    private static readonly Regex RubyDecl = new(
        @"^(?:def\s+(?:self\.)?(?<name>[\w?!=]+)|(?<type>class|module)\s+(?<name>[\w:]+))", RegexOptions.Compiled);

    /// <summary>
    /// Lines no declaration starts on.
    ///
    /// <para>A PREPROCESSOR LINE: <c>#define F(x)</c> would otherwise read as a function called F.
    /// An attribute or decorator is not skipped here but stripped by <see cref="Declaration"/>, since
    /// <c>@Inject public Svc(Repo repo)</c> is a declaration with one in front.</para>
    ///
    /// <para>AND A LINE THAT CONTINUES AN EXPRESSION. One starting with an operator — <c>? a(…)</c>,
    /// <c>&amp;&amp; b(…)</c>, <c>.Select(…)</c> — is the middle of something an earlier line began.</para>
    /// </summary>
    private static bool Skipped(string trimmed) =>
        trimmed[0] is '#' or '}' or ')' or ']' or '?' or ':' or '.' or '+' or '-' or '/' or '%' or '|' or '&' or '=' or ',' or '!' or '<' or '>';

    private sealed record Found(string Name, SymbolKind Kind, Frame Opens);

    private static Found? Declaration(string line, Language language, bool inType, string? typeName)
    {
        line = LeadingAnnotations.Replace(line, "");
        if (line.Length == 0) return null;

        return language.Family switch
        {
            Family.Go => GoDeclaration(line, inType),
            Family.Rust => RustDeclaration(line),
            Family.Shell => ShellDeclaration(line, inType),
            Family.Script => ScriptDeclaration(line, inType),
            _ => CLikeDeclaration(line, language, inType, typeName),
        };
    }

    /// <summary>
    /// <c>[Attr]</c>, <c>@Decorator(…)</c> and C++'s <c>template&lt;…&gt;</c> in front of a
    /// declaration on the same line. Stripped so the rules below see the declaration itself.
    /// </summary>
    private static readonly Regex LeadingAnnotations = new(
        @"^(?:(?:\[[^\]]*\]|@[\w.]+(?:\([^)]*\))?|template\s*<[^>]*>)\s*)+", RegexOptions.Compiled);

    private static readonly Regex TypeDecl = new(
        @"^(?:(?:public|private|protected|internal|static|sealed|abstract|partial|readonly|unsafe|file|ref|data|open|final|export|default|declare|inline|value|inner|companion|annotation|fileprivate|override|virtual|new|strictfp|non-sealed|enum)\s+)*" +
        @"(?<kw>class|struct|interface|enum|record|namespace|module|object|trait|protocol|extension|union)\b(?:\s+(?:class|struct))?\s+(?<name>[A-Za-z_$][\w$]*)",
        RegexOptions.Compiled);

    /// <summary>A C# property: no parameter list, so the method rule cannot see it.</summary>
    private static readonly Regex PropertyDecl = new(
        @"^(?:[\w<>\[\],.?]+\s+)+(?<name>[A-Za-z_]\w*)\s*(?:\{\s*(?:get|set|init|private|protected|internal)\b|=>)",
        RegexOptions.Compiled);

    /// <summary>Words that come before a parenthesis without naming anything being declared.</summary>
    private static readonly HashSet<string> NotNames = new(StringComparer.Ordinal)
    {
        "if", "for", "foreach", "while", "switch", "catch", "using", "lock", "return", "new", "throw",
        "sizeof", "typeof", "nameof", "await", "yield", "else", "do", "case", "when", "fixed", "checked",
        "unchecked", "default", "super", "this", "base", "delete", "function", "with", "synchronized",
        "elif", "match", "assert", "print", "defer", "go", "select", "from", "in", "is", "as", "not",
    };

    /// <summary>
    /// Words that may stand in front of a call without making it a declaration. A line in a type
    /// body that starts with one is a statement that strayed there — a field initialiser's call, a
    /// <c>return</c> in an expression body that continued a line.
    /// </summary>
    private static readonly HashSet<string> StatementWords = new(StringComparer.Ordinal)
    {
        "return", "await", "throw", "new", "yield", "else", "case", "goto", "var", "let", "const",
    };

    private static Found? CLikeDeclaration(string line, Language language, bool inType, string? typeName)
    {
        // `struct foo bar = {…}` and `X y = new X(…)` assign; a declaration does not.
        var paren = line.IndexOf('(');
        var assign = AssignmentAt(line);

        var type = TypeDecl.Match(line);
        if (type.Success && assign < 0)
        {
            // A FORWARD DECLARATION (`class Foo;`) says a type exists somewhere else; the map
            // shows it where it is defined.
            if (Regex.IsMatch(line, @"^(?:\w+\s+)*(?:class|struct|union|enum)\s+\w+\s*;$")) return null;
            return TypeFound(type);
        }

        if (paren > 0 && (assign < 0 || assign > paren))
        {
            var head = line[..paren].TrimEnd();
            var name = NameAtEnd(head, out var before);
            if (name is null || NotNames.Contains(name)) return null;

            var pre = before.Trim();
            var words = pre.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 0 && StatementWords.Contains(words[0])) return null;

            // NOTHING IN FRONT OF THE NAME: only a constructor, inside its own type, is declared
            // that way in these languages. At file scope it is a call — a macro, a test framework's
            // `describe(` — and in a type body it is a constructor or nothing.
            if (words.Length == 0 && !(inType && (name == typeName || language == Language.Php || head.StartsWith('~'))))
                return null;

            // A NAME REACHED THROUGH A DOT is a call (`Console.WriteLine(`) unless a return type
            // stands in front of it — then it is C#'s explicit interface implementation
            // (`void IDisposable.Dispose(`). C++'s `Type::method` has no dot and needs no rule.
            if (head.Length > name.Length && head[head.Length - name.Length - 1] == '.'
                && !(words.Length >= 2 && IsTypeWord(words[^2])))
                return null;

            return new Found(name, SymbolKind.Member, Frame.Body);
        }

        if (language.Properties && inType && assign < 0 && PropertyDecl.Match(line) is { Success: true } prop)
        {
            var first = line.Split(' ', 2)[0];
            if (StatementWords.Contains(first)) return null;
            return new Found(prop.Groups["name"].Value, SymbolKind.Member, Frame.Body);
        }

        return null;
    }

    /// <summary>An enum's body is its values, not members anyone calls, so it opens no container.</summary>
    private static Found TypeFound(Match type) => type.Groups["kw"].Value switch
    {
        "namespace" or "module" => new Found(type.Groups["name"].Value, SymbolKind.Namespace, Frame.Container),
        "enum" => new Found(type.Groups["name"].Value, SymbolKind.Type, Frame.Body),
        _ => new Found(type.Groups["name"].Value, SymbolKind.Type, Frame.Container),
    };

    /// <summary>A word that can be a return type or a modifier: an identifier, perhaps generic, an array or nullable.</summary>
    private static bool IsTypeWord(string word) => Regex.IsMatch(word, @"^[A-Za-z_][\w.]*(?:<.*>)?[\[\]?*&]*$");

    /// <summary>
    /// The position of an assignment's <c>=</c>, ignoring <c>==</c>, <c>=&gt;</c>, <c>&lt;=</c> and friends.
    ///
    /// <para>ONLY OUTSIDE BRACKETS. A default argument (<c>int size = 3</c>) or an enum's value
    /// (<c>{ A = 1 }</c>) is part of a declaration; an <c>=</c> at the top of the line is what makes
    /// <c>static struct node empty = { 0 };</c> a variable rather than a type.</para>
    /// </summary>
    private static int AssignmentAt(string line)
    {
        var depth = 0;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] is '(' or '[' or '{') { depth++; continue; }
            if (line[i] is ')' or ']' or '}') { depth--; continue; }
            if (line[i] != '=' || depth > 0) continue;
            var prev = i > 0 ? line[i - 1] : ' ';
            var next = i + 1 < line.Length ? line[i + 1] : ' ';
            if (next is '=' or '>' || prev is '=' or '!' or '<' or '>' or '+' or '-' or '*' or '/' or '%' or '&' or '|' or '^' or '?' or ':')
                continue;
            return i;
        }
        return -1;
    }

    /// <summary>
    /// The identifier a declaration head ends in, past any generic argument list
    /// (<c>Task&lt;T&gt; Run&lt;T&gt;</c> ends in <c>Run</c>), and the text in front of it.
    /// </summary>
    private static string? NameAtEnd(string head, out string before)
    {
        before = "";
        var end = head.Length;
        if (end > 0 && head[end - 1] == '>')
        {
            var depth = 0;
            for (var i = end - 1; i >= 0; i--)
            {
                if (head[i] == '>') depth++;
                else if (head[i] == '<' && --depth == 0) { end = i; break; }
            }
            head = head[..end].TrimEnd();
            end = head.Length;
        }

        var start = end;
        while (start > 0 && (char.IsLetterOrDigit(head[start - 1]) || head[start - 1] is '_' or '$' or '~' or '#'))
            start--;
        if (start == end || char.IsDigit(head[start])) return null;

        before = head[..start];
        return head[start..end].TrimStart('~', '#');
    }

    private static readonly Regex GoFunc = new(@"^func\s+(?:\([^)]*\)\s*)?(?<name>\w+)\s*[\[(]", RegexOptions.Compiled);
    private static readonly Regex GoType = new(@"^type\s+(?<name>\w+)(?:\[[^\]]*\])?\s+(?<kw>struct|interface)?", RegexOptions.Compiled);
    private static readonly Regex GoMethod = new(@"^(?<name>[A-Za-z_]\w*)\s*\(", RegexOptions.Compiled);

    private static Found? GoDeclaration(string line, bool inType)
    {
        if (GoFunc.Match(line) is { Success: true } f) return new Found(f.Groups["name"].Value, SymbolKind.Member, Frame.Body);
        if (GoType.Match(line) is { Success: true } t)
            return new Found(t.Groups["name"].Value, SymbolKind.Type,
                t.Groups["kw"].Value == "interface" ? Frame.Container : Frame.Body);
        // An interface's methods are written as `Name(args) result`, with nothing in front.
        if (inType && GoMethod.Match(line) is { Success: true } m) return new Found(m.Groups["name"].Value, SymbolKind.Member, Frame.Body);
        return null;
    }

    private static readonly Regex RustFn = new(
        @"^(?:pub(?:\([^)]*\))?\s+)?(?:(?:const|async|unsafe|default|extern(?:\s+""[^""]*"")?)\s+)*fn\s+(?<name>\w+)", RegexOptions.Compiled);
    private static readonly Regex RustType = new(
        @"^(?:pub(?:\([^)]*\))?\s+)?(?:unsafe\s+)?(?<kw>struct|enum|union|trait|mod|type)\s+(?<name>\w+)", RegexOptions.Compiled);
    private static readonly Regex RustImpl = new(
        @"^(?:unsafe\s+)?impl\b(?:\s*<.*?>)?\s+(?<what>[^{]+)", RegexOptions.Compiled);
    private static readonly Regex RustMacro = new(@"^macro_rules!\s*(?<name>\w+)", RegexOptions.Compiled);

    private static Found? RustDeclaration(string line)
    {
        if (RustFn.Match(line) is { Success: true } f) return new Found(f.Groups["name"].Value, SymbolKind.Member, Frame.Body);
        if (RustType.Match(line) is { Success: true } t)
            return t.Groups["kw"].Value switch
            {
                "mod" => new Found(t.Groups["name"].Value, SymbolKind.Namespace, Frame.Container),
                "trait" => new Found(t.Groups["name"].Value, SymbolKind.Type, Frame.Container),
                _ => new Found(t.Groups["name"].Value, SymbolKind.Type, Frame.Body),
            };
        if (RustImpl.Match(line) is { Success: true } im)
        {
            // `impl Trait for Type` is about Type; `impl<T> Type<T>` too. The name is what the
            // impl block adds methods TO.
            var what = im.Groups["what"].Value;
            var forAt = what.IndexOf(" for ", StringComparison.Ordinal);
            if (forAt >= 0) what = what[(forAt + 5)..];
            var name = Regex.Match(what, @"[A-Za-z_]\w*").Value;
            return name.Length == 0 ? null : new Found(name, SymbolKind.Type, Frame.Container);
        }
        if (RustMacro.Match(line) is { Success: true } m) return new Found(m.Groups["name"].Value, SymbolKind.Member, Frame.Body);
        return null;
    }

    private static readonly Regex ScriptFunction = new(
        @"^(?:export\s+)?(?:default\s+)?(?:declare\s+)?(?:async\s+)?function\s*\*?\s*(?<name>[\w$]+)", RegexOptions.Compiled);
    private static readonly Regex ScriptArrow = new(
        @"^(?:export\s+)?(?:declare\s+)?(?:const|let|var)\s+(?<name>[\w$]+)\s*(?::[^=]+)?=\s*(?:async\s+)?(?:function\b|(?:\([^)]*\)|[\w$]+)\s*(?::[^=]+?)?=>|\($)",
        RegexOptions.Compiled);
    private static readonly Regex ScriptTypeAlias = new(
        @"^(?:export\s+)?(?:declare\s+)?type\s+(?<name>[\w$]+)", RegexOptions.Compiled);
    private static readonly Regex ScriptMethod = new(
        @"^(?:(?:public|private|protected|static|async|readonly|abstract|override|get|set|declare)\s+)*\*?(?<name>#?[\w$]+)\s*(?:<[^>]*>)?\s*\(", RegexOptions.Compiled);

    private static Found? ScriptDeclaration(string line, bool inType)
    {
        if (ScriptFunction.Match(line) is { Success: true } f) return new Found(f.Groups["name"].Value, SymbolKind.Member, Frame.Body);
        if (ScriptArrow.Match(line) is { Success: true } a) return new Found(a.Groups["name"].Value, SymbolKind.Member, Frame.Body);
        if (ScriptTypeAlias.Match(line) is { Success: true } ta) return new Found(ta.Groups["name"].Value, SymbolKind.Type, Frame.Body);

        var type = TypeDecl.Match(line);
        if (type.Success) return TypeFound(type);

        // A CLASS BODY'S METHODS HAVE NOTHING IN FRONT OF THEIR NAME — `render() {` — so the rule
        // that refuses a bare call at file scope must allow one here. A class body holds no
        // statements, so a bare name and a parenthesis there can only be a method.
        if (inType && AssignmentAt(line) is var eq && ScriptMethod.Match(line) is { Success: true } m
            && (eq < 0 || eq > m.Index + m.Length) && !NotNames.Contains(m.Groups["name"].Value.TrimStart('#')))
            return new Found(m.Groups["name"].Value.TrimStart('#'), SymbolKind.Member, Frame.Body);

        return null;
    }

    private static readonly Regex ShellFunction = new(
        @"^(?:function\s+(?<name>[\w.:-]+)|(?<name>[\w.:-]+)\s*\(\s*\))", RegexOptions.Compiled);

    private static Found? ShellDeclaration(string line, bool inType) =>
        !inType && ShellFunction.Match(line) is { Success: true } f
            ? new Found(f.Groups["name"].Value, SymbolKind.Member, Frame.Body)
            : null;

    /// <summary>
    /// The declaration on line <paramref name="first"/>, as written, up to where its body starts.
    ///
    /// <para>CONTINUED OVER LINES WHILE ITS PARENTHESES ARE OPEN. A parameter list one per line is
    /// the norm in several of these languages, and the first line alone — <c>public Agent(</c> —
    /// tells a reader nothing about what it takes.</para>
    /// </summary>
    private static string Signature(Stripped text, int first, Language language)
    {
        var code = new StringBuilder();
        var display = new StringBuilder();
        var depth = 0;
        for (var i = first; i < text.Code.Length && i < first + MaxSignatureLines; i++)
        {
            if (i > first) { code.Append(' '); display.Append(' '); }
            code.Append(text.Code[i].Trim());
            display.Append(Trimmed(text.Display[i], text.Code[i]));
            foreach (var c in text.Code[i])
                if (c == '(') depth++;
                else if (c == ')') depth--;
            if (depth <= 0) break;
        }

        var cut = BodyStart(code.ToString(), language);
        var shown = display.ToString()[..Math.Min(cut, display.Length)];
        // A parameter list begun on the line after its "(" joins as "( string": the space was the line break.
        shown = Regex.Replace(Regex.Replace(shown, @"\s+", " "), @"([(\[<]) | ([)\]>])", "$1$2").Trim().TrimEnd(':').TrimEnd();
        return shown.Length <= MaxSignature ? shown : shown[..MaxSignature].TrimEnd() + "…";
    }

    /// <summary>The display line trimmed by the same amount as its code line, so the two stay aligned.</summary>
    private static string Trimmed(string display, string code)
    {
        var start = code.Length - code.TrimStart().Length;
        var length = code.Trim().Length;
        return start + length <= display.Length ? display.Substring(start, length) : display.Trim();
    }

    /// <summary>
    /// Where a declaration's body begins: its <c>{</c>, or for C# an expression body's <c>=&gt;</c>,
    /// outside any parentheses.
    ///
    /// <para>NOT THE ARROW IN JAVASCRIPT, where <c>const f = (a) =&gt; {</c> is the declaration and
    /// the arrow is part of what it says. Python and Ruby have no braces to stop at; their
    /// signature is the line, and the trailing colon is trimmed by the caller.</para>
    /// </summary>
    private static int BodyStart(string code, Language language)
    {
        var depth = 0;
        for (var i = 0; i < code.Length; i++)
        {
            var c = code[i];
            if (c is '(' or '[') depth++;
            else if (c is ')' or ']') depth--;
            else if (depth == 0 && c == '{' && language.Shape == Shape.Braces) return i;
            else if (depth == 0 && c == '=' && i + 1 < code.Length && code[i + 1] == '>' && language == Language.CSharp) return i;
            else if (depth == 0 && c == ';' && language.Shape == Shape.Braces) return i;
        }
        return code.Length;
    }
}
