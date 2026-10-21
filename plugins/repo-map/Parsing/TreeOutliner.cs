using System.Text;
using TS = TreeSitter;

namespace CxAgent.Plugins.RepoMap.Parsing;

/// <summary>
/// One file's declarations and references, from its syntax tree.
///
/// <para>THE GRAMMAR'S OWN QUERY SAYS WHAT A DECLARATION IS. Each grammar's upstream tags.scm marks
/// definitions and references with the same capture names everywhere (<c>@definition.class</c>,
/// <c>@reference.call</c>), so the code below knows a handful of node names that recur across
/// grammars and nothing about any one language.</para>
///
/// <para>WHAT THE MAP SHOWS IS WHAT THE TEXT READER SHOWS: a type, or a member at a file's or a
/// type's top level, never anything inside a body; the declaration as written up to its body or its
/// value; the line it starts on. A namespace is tracked and never listed.</para>
/// </summary>
public static class TreeOutliner
{
    /// <summary>How long one file's parse may take before that file is read as text instead.</summary>
    public static readonly TimeSpan ParseBudget = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Nodes that wrap a declaration without being one: <c>export</c>, <c>const</c>,
    /// <c>template &lt;…&gt;</c>, Go's <c>type</c>. The declaration is shown with them, because
    /// <c>export const x</c> is what a reader searches for, not <c>x</c>.
    /// </summary>
    private static readonly HashSet<string> Wrappers = new(StringComparer.Ordinal)
    {
        "export_statement", "lexical_declaration", "variable_declaration", "ambient_declaration",
        "template_declaration", "type_declaration",
    };

    /// <summary>
    /// Where a C or C++ declarator belongs. Their queries capture <c>count(const char *text)</c>, and
    /// the declaration a reader knows is <c>static int count(const char *text)</c> around it.
    /// </summary>
    private static readonly HashSet<string> DeclaratorOwners = new(StringComparer.Ordinal)
    {
        "function_definition", "declaration", "field_declaration",
    };

    /// <summary>Where a declaration's body starts when the grammar does not call it <c>body</c>.</summary>
    private static readonly HashSet<string> BodyTypes = new(StringComparer.Ordinal)
    {
        "accessor_list", "arrow_expression_clause", "field_initializer_list", "enum_member_declaration_list",
        "template_body", "declaration_list", "class_body", "field_declaration_list", "compound_statement",
    };

    /// <summary>Declarations whose value is not part of them: <c>export const config = { … }</c> is <c>export const config</c>.</summary>
    private static readonly HashSet<string> ValueHolders = new(StringComparer.Ordinal) { "variable_declarator", "assignment" };

    /// <summary>A value that IS the declaration — <c>const f = (a) =&gt;</c> says what f takes — so it is read on to its body.</summary>
    private static readonly HashSet<string> Functions = new(StringComparer.Ordinal)
    {
        "arrow_function", "function_expression", "function", "generator_function",
    };

    /// <summary>Attached to a declaration without being what it says: shown only when on the same line.</summary>
    private static readonly HashSet<string> Attributes = new(StringComparer.Ordinal)
    {
        "attribute_list", "attribute_item", "decorator", "annotation", "marker_annotation",
    };

    /// <summary>
    /// Never searched for a body or a value: a default parameter's <c>{}</c> or lambda is not where
    /// the declaration's own body starts.
    /// </summary>
    private static readonly HashSet<string> Opaque = new(StringComparer.Ordinal)
    {
        "parameters", "formal_parameters", "parameter_list", "argument_list", "arguments", "type_arguments",
        "type_parameters", "template_parameter_list", "method_parameters", "lambda_parameters",
        "type_parameter_list", "attribute_list", "attribute_item", "decorator", "annotation", "marker_annotation",
    };

    private static readonly HashSet<string> Comments = new(StringComparer.Ordinal) { "comment", "line_comment", "block_comment" };

    /// <summary>A node's identity: the binding makes a new object per visit, so nodes are compared by where and what they are.</summary>
    private readonly record struct Span(int Start, int End, string Type)
    {
        public static Span Of(TS.Node node) => new(node.StartIndex, node.EndIndex, node.Type);
    }

    /// <summary>One definition the query found: its name, the node the query captured, and the node the map quotes.</summary>
    private sealed record Definition(TS.Node Name, TS.Node Node, TS.Node Head, SymbolKind Kind);

    /// <summary>The file's outline, or null when its parse ran past <see cref="ParseBudget"/>.</summary>
    public static FileOutline? Outline(string path, string text, Reader reader)
    {
        using var tree = reader.Parser.Parse(text);
        if (tree is null) return null;

        var definitions = new List<Definition>();
        var references = new List<TS.Node>();
        using (var cursor = reader.Query.Execute(tree.RootNode))
            foreach (var match in cursor.Matches)
            {
                TS.Node? name = null, node = null;
                string? role = null;
                foreach (var capture in match.Captures)
                    if (capture.Name == "name") name = capture.Node;
                    else if (capture.Name.StartsWith("definition.", StringComparison.Ordinal)
                             || capture.Name.StartsWith("reference.", StringComparison.Ordinal))
                        (role, node) = (capture.Name, capture.Node);

                if (name is null || node is null || role is null) continue;
                if (role.StartsWith("definition.", StringComparison.Ordinal))
                    definitions.Add(new Definition(name, node, Head(node), KindOf(role["definition.".Length..])));
                else
                    references.Add(name);
            }

        return new FileOutline(path, Symbols(text, definitions), new Dictionary<string, int>(StringComparer.Ordinal));
    }

    /// <summary>
    /// What kind of thing a capture says it found. The suffixes are the tags convention's, plus the few
    /// a grammar adds (Scala's <c>object</c>, Rust's <c>macro</c>); an unknown one is a member, which is
    /// what most of them are.
    /// </summary>
    private static SymbolKind KindOf(string what) => what switch
    {
        "class" or "interface" or "type" or "struct" or "enum" or "trait" or "object" or "union" => SymbolKind.Type,
        // A NAMESPACE IS TRACKED, NEVER LISTED — see SymbolKind.
        "module" or "namespace" or "package" => SymbolKind.Namespace,
        _ => SymbolKind.Member,
    };

    private static List<Symbol> Symbols(string text, List<Definition> found)
    {
        // THE SAME NAME CAPTURED TWICE — two patterns matching one declaration — is one declaration.
        var definitions = found
            .GroupBy(d => d.Name.StartIndex)
            .Select(g => g.First())
            .OrderBy(d => d.Head.StartIndex)
            .ToList();

        // A PROTOTYPE WHERE ITS DEFINITION IS: the definition says the same and has the body.
        var defined = definitions.Where(d => d.Head.Type == "function_definition").Select(d => d.Name.Text).ToHashSet(StringComparer.Ordinal);
        definitions.RemoveAll(d => d.Head.Type == "declaration" && defined.Contains(d.Name.Text));

        // BY DEFINITION, NOT BY NODE: a type is both the node its query captured and the wrapper it is
        // quoted from (export class App, Go's type Handler interface), and the members inside it are
        // one level deeper, not two.
        var members = new HashSet<Span>();
        var types = new Dictionary<Span, Definition>();
        foreach (var d in definitions)
        {
            if (d.Kind == SymbolKind.Member) { members.Add(Span.Of(d.Node)); members.Add(Span.Of(d.Head)); }
            if (d.Kind == SymbolKind.Type) { types[Span.Of(d.Node)] = d; types[Span.Of(d.Head)] = d; }
        }

        var symbols = new List<Symbol>();
        foreach (var d in definitions)
        {
            if (d.Kind == SymbolKind.Namespace) continue;
            var name = d.Name.Text;
            if (name.Length == 0) continue;

            var enclosing = new HashSet<Definition>();
            var hidden = false;
            for (var up = d.Head.Parent; up is not null && !hidden; up = up.Parent)
            {
                var span = Span.Of(up);
                // INSIDE A FUNCTION'S BODY: that function's business, never the file's.
                if (members.Contains(span)) hidden = true;
                else if (types.TryGetValue(span, out var outer) && outer != d)
                {
                    // ONE DECLARATION SEEN TWICE — typedef struct X { } X — is listed as the outer one.
                    if (outer.Name.Text == name && d.Kind == SymbolKind.Type) hidden = true;
                    enclosing.Add(outer);
                }
            }
            if (hidden) continue;
            var depth = enclosing.Count;

            if (Quote(text, d.Head) is not { } quoted) continue;
            symbols.Add(new Symbol(name, quoted.Signature, quoted.Line, depth, d.Kind));
        }
        return symbols;
    }

    /// <summary>
    /// The node a declaration is quoted from: a C declarator's whole declaration, then out through the
    /// wrappers that belong to it alone.
    ///
    /// <para>ALONE: <c>const a = 1, b = 2</c> wraps two declarations, and quoting the wrapper for each
    /// would show both of them as <c>a</c>'s.</para>
    /// </summary>
    private static TS.Node Head(TS.Node node)
    {
        if (node.Type.EndsWith("_declarator", StringComparison.Ordinal))
        {
            var up = node.Parent;
            while (up is not null && up.Type.EndsWith("_declarator", StringComparison.Ordinal)) up = up.Parent;
            if (up is not null && DeclaratorOwners.Contains(up.Type)) node = up;
        }
        while (node.Parent is { } parent && Wrappers.Contains(parent.Type)
               && parent.NamedChildren.Count(c => !Opaque.Contains(c.Type) && !Comments.Contains(c.Type)) == 1)
            node = parent;
        return node;
    }

    private sealed record Quoted(string Signature, int Line);

    /// <summary>
    /// The declaration as the map shows it, and the line it starts on.
    ///
    /// <para>FROM ITS FIRST LINE THAT IS NOT ONLY ATTRIBUTES: <c>[Serializable]</c> or
    /// <c>@Override</c> on a line of its own is not what a reader looks for, but <c>@Inject public
    /// Svc(…)</c> on one line is quoted whole, as written.</para>
    /// </summary>
    private static Quoted? Quote(string text, TS.Node head)
    {
        if (FirstToken(head) is not { } first) return null;
        var lineStart = first.StartIndex == 0 ? 0 : text.LastIndexOf('\n', first.StartIndex - 1) + 1;
        var start = Math.Max(head.StartIndex, lineStart);
        var end = CutIn(head) is >= 0 and var cut ? cut : head.EndIndex;
        if (end <= start) return null;

        var raw = new StringBuilder();
        var at = start;
        foreach (var comment in CommentsIn(head).Where(c => c.StartIndex >= start && c.EndIndex <= end))
        {
            raw.Append(text, at, comment.StartIndex - at).Append(' ');
            at = comment.EndIndex;
        }
        raw.Append(text, at, end - at);

        // WHAT IS LEFT BEFORE THE BODY OR THE VALUE: the "=" of a value cut away, a statement's ";",
        // the "{" of a body on the same line, Python's ":".
        var shown = Signatures.Normalise(raw.ToString()).TrimEnd(' ', ';', '=', '{', ',', ':');
        return shown.Length == 0 ? null : new Quoted(Signatures.Cut(shown), first.StartPosition.Row + 1);
    }

    /// <summary>The first token of a node that is not inside an attribute or a comment.</summary>
    private static TS.Node? FirstToken(TS.Node node)
    {
        if (Attributes.Contains(node.Type) || Comments.Contains(node.Type)) return null;
        if (node.Children.Count == 0) return node;
        foreach (var child in node.Children)
            if (FirstToken(child) is { } token) return token;
        return null;
    }

    /// <summary>
    /// Where the declaration stops: the start of its body, or of a value that is not itself a function;
    /// -1 when nothing inside it does, and the whole node is the declaration.
    /// </summary>
    private static int CutIn(TS.Node node)
    {
        var fields = node.Fields;
        for (var i = 0; i < fields.Count; i++)
        {
            var (field, child) = (fields[i].Key, fields[i].Value);
            // "{" OR RUBY'S "end": a body that is empty has no body node, only the token that closes it.
            if (field == "body" || child.Type is "{" or "end" || BodyTypes.Contains(child.Type)) return child.StartIndex;
            if (field is "value" or "right" && ValueHolders.Contains(node.Type) && !Functions.Contains(child.Type))
                return child.StartIndex;
            if (Opaque.Contains(child.Type) || child.Children.Count == 0) continue;
            if (CutIn(child) is >= 0 and var cut) return cut;
        }
        return -1;
    }

    private static IEnumerable<TS.Node> CommentsIn(TS.Node node)
    {
        foreach (var child in node.Children)
        {
            if (Comments.Contains(child.Type)) yield return child;
            else foreach (var inner in CommentsIn(child)) yield return inner;
        }
    }
}
