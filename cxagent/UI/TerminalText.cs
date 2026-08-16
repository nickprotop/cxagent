using System.Text;

namespace CxAgent.UI;

/// <summary>
/// Strips terminal control sequences from text on its way to a RENDERED surface.
///
/// <para>WHY THIS EXISTS. The agent runs commands, and a command's output is arbitrary bytes — most
/// pointedly when the agent has just built a program and runs it to check its work. A live session
/// ran a freshly built <c>cxgpu --gpu-usage --color</c>, and its 24-bit colour codes went into the
/// transcript and smeared it: the terminal was never actually hijacked, because SharpConsoleUI's own
/// <c>TextSanitizer</c> replaces the ESC rune before it reaches a cell — but replacing ONE BYTE of a
/// sixteen-byte sequence leaves the other fifteen on screen as literal text. The guard held and the
/// result was still unreadable.</para>
///
/// <para>SO WHOLE SEQUENCES GO, not the escape byte. That is the difference between this and a
/// per-rune filter, and it is the entire point.</para>
///
/// <para>DISPLAY ONLY — and that boundary is deliberate. The same text reaches three places and only
/// one of them is a renderer:</para>
/// <list type="bullet">
///   <item>the TRANSCRIPT, which cannot render escapes and is what this protects;</item>
///   <item>the MODEL's tool result, left INTACT — an agent building a colour feature verifies it by
///   counting escape codes in its own output, which a session did, and blinding it there would make
///   that class of work impossible to check;</item>
///   <item>the job LOG FILE, left intact — it is a faithful record, and <c>less -R</c> renders it.</item>
/// </list>
/// </summary>
public static class TerminalText
{
    // NAMED, not literal. These were real control bytes in the source: legal C#, invisible in every
    // editor and diff, and one careless copy-paste from silently becoming something else.
    private const char Esc = '\u001b';
    private const char Bel = '\u0007';

    /// <summary>
    /// The text with terminal control sequences removed, safe to hand to a markup parser.
    ///
    /// <para>Newlines and tabs SURVIVE: they are content, not control. Carriage return and backspace
    /// do not — both rewrite what is already on the line, which is corruption in a surface that has
    /// no cursor to move.</para>
    /// </summary>
    public static string Strip(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;

        // Nothing to do is the common case — a scan is far cheaper than building a new string.
        if (text.IndexOf(Esc) < 0 && text.IndexOf('\r') < 0 && text.IndexOf('\b') < 0)
            return text;

        var sb = new StringBuilder(text.Length);
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];

            if (c == Esc && i + 1 < text.Length)
            {
                var next = text[i + 1];

                // CSI: ESC [ ... <final byte 0x40-0x7E>. Colour, cursor movement, erase — the form
                // that caused this. Parameter and intermediate bytes run 0x20-0x3F.
                if (next == '[')
                {
                    i += 2;
                    while (i < text.Length && text[i] >= ' ' && text[i] <= '?') i++;
                    if (i < text.Length) i++;          // the final byte
                    continue;
                }

                // OSC: ESC ] ... terminated by BEL or ST (ESC \). Window titles, and the hyperlink
                // form — which carries a URL, so leaving its payload behind would print a naked
                // address into the transcript.
                if (next == ']')
                {
                    i += 2;
                    while (i < text.Length)
                    {
                        if (text[i] == Bel) { i++; break; }                       // BEL
                        if (text[i] == Esc && i + 1 < text.Length && text[i + 1] == '\\')
                        { i += 2; break; }                                             // ST
                        i++;
                    }
                    continue;
                }

                // Two- and three-character forms: ESC ( B, ESC = , ESC > and friends. The byte after
                // ESC decides how many follow; ( ) * + take one more (the charset).
                i += 2;
                if (next is '(' or ')' or '*' or '+' && i < text.Length) i++;
                continue;
            }

            // A LONE ESC AT THE END is a truncated sequence — output cut mid-escape by the capture
            // cap. Drop it rather than printing a replacement glyph for half a code nobody sent.
            if (c == Esc) { i++; continue; }

            // CR and BS rewrite the line rather than adding to it. A progress bar that redraws with
            // \r arrives here as every frame concatenated; dropping the CR at least leaves the
            // frames legible instead of overwriting each other into nonsense.
            if (c is '\r' or '\b') { i++; continue; }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }
}
