// A SMALL HIGHLIGHTER, WRITTEN HERE: keywords, strings, comments and numbers for the languages a coding agent shows
// most. Not a parser — a reader's aid. Every token is escaped as it is emitted, so the output is safe to set as HTML
// whatever the code contains.

const KEYWORDS = {
  csharp: "abstract as async await base bool break case catch class const continue default delegate do else enum event explicit false finally for foreach get if implicit in init int interface internal is lock long namespace new null object operator out override params private protected public readonly record ref required return sealed set static string struct switch this throw true try typeof using var virtual void when where while with yield",
  js: "async await break case catch class const continue default delete do else export extends false finally for from function if import in instanceof let new null of return static super switch this throw true try typeof undefined var void while yield",
  ts: "abstract as async await break case catch class const continue declare default delete do else enum export extends false finally for from function if implements import in instanceof interface keyof let new null of private protected public readonly return static super switch this throw true try type typeof undefined var void while yield",
  python: "and as assert async await break class continue def del elif else except False finally for from global if import in is lambda None nonlocal not or pass raise return self True try while with yield",
  bash: "case do done elif else esac export fi for function if in local read return select then until while",
  json: "true false null",
};
KEYWORDS.cs = KEYWORDS.csharp;
KEYWORDS.javascript = KEYWORDS.js;
KEYWORDS.typescript = KEYWORDS.ts;
KEYWORDS.py = KEYWORDS.python;
KEYWORDS.sh = KEYWORDS.bash;
KEYWORDS.shell = KEYWORDS.bash;

const HASH_COMMENTS = new Set(["python", "py", "bash", "sh", "shell"]);

export function escapeHtml(text) {
  return text.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;").replace(/'/g, "&#39;");
}

function span(cls, text) {
  return '<span class="' + cls + '">' + escapeHtml(text) + "</span>";
}

export function highlight(code, lang) {
  lang = (lang || "").toLowerCase();
  if (lang === "diff") {
    return code.split("\n").map(function (line) {
      if (line.startsWith("+")) return span("tok-add", line);
      if (line.startsWith("-")) return span("tok-del", line);
      if (line.startsWith("@@")) return span("tok-hunk", line);
      return escapeHtml(line);
    }).join("\n");
  }
  const words = KEYWORDS[lang];
  if (!words) return escapeHtml(code);
  const keywords = new Set(words.split(" "));
  const comment = HASH_COMMENTS.has(lang) ? "#[^\\n]*" : "\\/\\/[^\\n]*|\\/\\*[\\s\\S]*?\\*\\/";
  const pattern = new RegExp(
    "(" + comment + ")|(\"(?:\\\\.|[^\"\\\\])*\"|'(?:\\\\.|[^'\\\\])*'|`(?:\\\\.|[^`\\\\])*`)|(\\b\\d[\\d_.]*\\b)|([A-Za-z_][A-Za-z0-9_]*)",
    "g");
  let out = "";
  let last = 0;
  let m;
  while ((m = pattern.exec(code)) !== null) {
    out += escapeHtml(code.slice(last, m.index));
    if (m[1]) out += span("tok-c", m[1]);
    else if (m[2]) out += span("tok-s", m[2]);
    else if (m[3]) out += span("tok-n", m[3]);
    else if (keywords.has(m[4])) out += span("tok-k", m[4]);
    else out += escapeHtml(m[4]);
    last = pattern.lastIndex;
  }
  return out + escapeHtml(code.slice(last));
}
