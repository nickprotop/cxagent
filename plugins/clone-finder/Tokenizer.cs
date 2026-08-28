namespace CxAgent.Plugins.CloneFinder;

/// <summary>A normalised token: its text after folding, and the 1-based source line it came from
/// so a match can be reported back to a location.</summary>
public record Token(string Text, int Line);

/// <summary>Normalises source into a token stream comparable across renamed copies of the same
/// code, without knowing what language it is looking at.</summary>
public static class Tokenizer
{
    // A union across C-family languages, not per-language: the plugin has no way to know what
    // language a given file is, and a word being a keyword somewhere it isn't only makes the
    // comparison stricter (fewer identifiers folded together), never wrong.
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "if", "else", "for", "foreach", "while", "do", "switch", "case", "default", "break",
        "continue", "return", "goto", "throw", "try", "catch", "finally", "yield",
        "class", "struct", "interface", "enum", "record", "namespace", "using", "import",
        "package", "module", "def", "function", "func", "fn", "lambda",
        "public", "private", "protected", "internal", "static", "final", "const", "let", "var",
        "readonly", "volatile", "virtual", "override", "abstract", "sealed", "extern", "async",
        "await", "new", "delete", "this", "self", "super", "base",
        "int", "long", "short", "byte", "float", "double", "decimal", "bool", "boolean", "char",
        "string", "void", "object", "var", "auto", "null", "nil", "none", "true", "false",
        "typedef", "template", "typename", "operator", "friend", "explicit", "implicit",
        "in", "out", "ref", "is", "as", "instanceof", "typeof", "sizeof", "nameof",
        "true", "false", "null",
    };

    /// <summary>A single forward scan with a small state machine — comments and whitespace never
    /// reach the token stream, identifiers fold to `_`, and everything else (keywords, operators,
    /// punctuation, literals) survives verbatim so the comparison stays sensitive to what actually
    /// differs between two blocks.</summary>
    public static IReadOnlyList<Token> Normalise(string source)
    {
        var tokens = new List<Token>();
        int i = 0;
        int line = 1;
        int n = source.Length;

        while (i < n)
        {
            char c = source[i];

            if (c == '\n')
            {
                line++;
                i++;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            // Line comment: // or #. Consumed to end of line (not including the newline itself,
            // which the next loop iteration handles so the line counter stays correct).
            if ((c == '/' && i + 1 < n && source[i + 1] == '/') || c == '#')
            {
                while (i < n && source[i] != '\n') i++;
                continue;
            }

            // Block comment: /* ... */. Spans lines, so newlines inside it must still bump the
            // line counter or every token after a multi-line comment reports the wrong location.
            if (c == '/' && i + 1 < n && source[i + 1] == '*')
            {
                i += 2;
                while (i < n && !(source[i] == '*' && i + 1 < n && source[i + 1] == '/'))
                {
                    if (source[i] == '\n') line++;
                    i++;
                }
                i = Math.Min(i + 2, n);
                continue;
            }

            // String/char literal: kept verbatim (including its quotes) so two different literals
            // stay distinguishable, and escape-aware so an escaped quote doesn't end the literal
            // early and desynchronise everything that follows.
            if (c == '"' || c == '\'')
            {
                char quote = c;
                int start = i;
                int startLine = line;
                i++;
                while (i < n && source[i] != quote)
                {
                    if (source[i] == '\\' && i + 1 < n)
                    {
                        i += 2;
                    }
                    else
                    {
                        if (source[i] == '\n') line++;
                        i++;
                    }
                }
                i = Math.Min(i + 1, n);
                tokens.Add(new Token(source[start..i], startLine));
                continue;
            }

            // Numeric literal: kept verbatim, digits plus an interior '.' so "3.14" stays one
            // token rather than splitting on the punctuation branch below.
            if (char.IsDigit(c))
            {
                int start = i;
                while (i < n && (char.IsLetterOrDigit(source[i]) || source[i] == '.')) i++;
                tokens.Add(new Token(source[start..i], line));
                continue;
            }

            // Identifier or keyword: a run of letters/digits/underscore. Keywords are the one
            // exception to folding — folding `for` would let every loop match every other
            // statement of the same shape, and the keyword set is what tells them apart from a
            // renamed identifier that folds to `_`.
            if (char.IsLetter(c) || c == '_')
            {
                int start = i;
                while (i < n && (char.IsLetterOrDigit(source[i]) || source[i] == '_')) i++;
                string word = source[start..i];
                tokens.Add(new Token(Keywords.Contains(word) ? word : "_", line));
                continue;
            }

            // Everything else — operators and punctuation — is emitted one character at a time,
            // verbatim: multi-character operators like `+=` or `==` still compare equal between
            // two clones because both sides emit the same run of single-char tokens in the same
            // order.
            tokens.Add(new Token(c.ToString(), line));
            i++;
        }

        return tokens;
    }
}
