// MARKDOWN FOR MODEL TEXT — ESCAPE FIRST, THEN FORMAT.
//
// The text this renders was written by a model, which was told things by whatever it read: a web page, a README, a
// file in the repository. So every character is escaped before any markdown is applied, and the only HTML in the
// output is the handful of tags written below. A link's target is kept only when it is http, https or mailto;
// anything else shows as text. Code goes through highlight.js, which escapes as it highlights. The page's content
// policy (no inline script) is the backstop, not the defence.

const SAFE_URL = /^(https?:\/\/|mailto:)/i;

// NAMES MODELS WRITE that highlight.js knows by another one.
const ALIASES = { "c#": "csharp", "cs": "csharp", "sh": "bash", "zsh": "bash", "shell": "bash", "console": "bash",
  "terminal": "bash", "ps1": "powershell", "js": "javascript", "ts": "typescript", "py": "python", "rs": "rust",
  "yml": "yaml", "md": "markdown", "html": "xml", "htm": "xml", "svg": "xml", "csproj": "xml", "golang": "go",
  "kt": "kotlin", "rb": "ruby", "jsonc": "json", "toml": "ini", "dotenv": "bash" };

export function escapeHtml(text) {
  return text.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;").replace(/'/g, "&#39;");
}

function highlighted(code, lang) {
  const hl = globalThis.hljs;
  const name = ALIASES[lang] || lang;
  if (hl && name && hl.getLanguage(name)) {
    try { return { html: hl.highlight(code, { language: name, ignoreIllegals: true }).value, name }; }
    catch { /* a grammar that throws shows its code plain */ }
  }
  return { html: escapeHtml(code), name: name || "text" };
}

function codeBlock(code, lang) {
  const { html, name } = highlighted(code, lang);
  const label = escapeHtml(lang || "text");
  return '<div class="code"><div class="code-bar"><span class="code-lang">' + label
    + '</span><button type="button" class="copy">Copy</button></div><pre><code class="hljs language-'
    + escapeHtml(name).replace(/[^a-z0-9_+-]/gi, "") + '">' + html + "</code></pre></div>";
}

function inline(text) {
  // CODE SPANS FIRST, set aside so nothing inside them is formatted. Placeholders use a character escaping never
  // produces, so model text cannot forge one.
  const kept = [];
  const keep = (html) => { kept.push(html); return "\u0000" + (kept.length - 1) + "\u0000"; };
  let s = escapeHtml(text).replace(/`([^`\n]+)`/g, (_, code) => keep("<code>" + code + "</code>"));
  s = s.replace(/\[([^\]]+)\]\(([^)\s]+)\)/g, (whole, label, url) => {
    // THE URL IS ALREADY ESCAPED, so a quote in it cannot close the attribute; the scheme decides if it is a link.
    if (!SAFE_URL.test(url.replace(/&amp;/g, "&"))) return label;
    return keep('<a href="' + url + '" rel="noopener noreferrer" target="_blank">' + label + "</a>");
  });
  s = s.replace(/(^|[\s(])(https?:\/\/[^\s<)]+[^\s<).,;:!?'"])/g,
    (_, lead, url) => lead + keep('<a href="' + url + '" rel="noopener noreferrer" target="_blank">' + url + "</a>"));
  s = s.replace(/\*\*([^*]+)\*\*/g, "<strong>$1</strong>");
  s = s.replace(/__([^_]+)__/g, "<strong>$1</strong>");
  s = s.replace(/(^|[^*])\*([^*\s][^*]*)\*/g, "$1<em>$2</em>");
  s = s.replace(/(^|\W)_([^_\s][^_]*)_(?=\W|$)/g, "$1<em>$2</em>");
  s = s.replace(/~~([^~]+)~~/g, "<del>$1</del>");
  return s.replace(/\u0000(\d+)\u0000/g, (_, i) => kept[Number(i)]);
}

function tableRow(line, cell) {
  const cells = line.trim().replace(/^\|/, "").replace(/\|$/, "").split("|");
  return "<tr>" + cells.map((c) => "<" + cell + ">" + inline(c.trim()) + "</" + cell + ">").join("") + "</tr>";
}

const FENCE = /^(\s*)(`{3,}|~{3,})\s*([^\s`]*)/;
const LIST = /^(\s*)([-*+]|\d+[.)])\s+/;

export function render(text) {
  const lines = String(text).replace(/\r\n?/g, "\n").split("\n");
  const out = [];
  let i = 0;
  while (i < lines.length) {
    const line = lines[i];
    let m;
    if (line.trim() === "") { i++; continue; }

    // A FENCE AT ANY INDENT — models put them inside list items. Its code is unindented by the fence's own indent, and
    // an unclosed one (the reply is still streaming) is code to the end.
    if ((m = FENCE.exec(line))) {
      const indent = m[1].length, marker = m[2], lang = m[3].toLowerCase();
      const code = [];
      i++;
      while (i < lines.length && !(lines[i].trim().startsWith(marker) && lines[i].trim().replace(/[`~]/g, "") === "")) {
        code.push(lines[i].slice(Math.min(indent, lines[i].length - lines[i].trimStart().length)));
        i++;
      }
      i++;
      out.push(codeBlock(code.join("\n"), lang));
      continue;
    }
    if ((m = /^(#{1,6})\s+(.*)$/.exec(line))) {
      out.push("<h" + m[1].length + ">" + inline(m[2].replace(/\s#+\s*$/, "")) + "</h" + m[1].length + ">");
      i++;
      continue;
    }
    if (/^\s*([-*_])(\s*\1){2,}\s*$/.test(line)) { out.push("<hr>"); i++; continue; }
    if (/^\s*\|.*\|\s*$/.test(line) && i + 1 < lines.length && /^\s*\|?\s*:?-{2,}/.test(lines[i + 1])) {
      let html = "<table><thead>" + tableRow(line, "th") + "</thead><tbody>";
      i += 2;
      while (i < lines.length && /^\s*\|.*\|\s*$/.test(lines[i])) html += tableRow(lines[i++], "td");
      out.push(html + "</tbody></table>");
      continue;
    }
    if ((m = LIST.exec(line))) {
      const ordered = /\d/.test(m[2]);
      const items = [];
      while (i < lines.length && (m = LIST.exec(lines[i]))) {
        const item = [lines[i].slice(m[0].length)];
        i++;
        // A LIST ITEM'S OWN CONTINUATION — indented lines and fences under it — stays with it.
        while (i < lines.length && lines[i].trim() !== "" && !LIST.test(lines[i]) && !FENCE.test(lines[i])
          && /^\s+/.test(lines[i])) item.push(lines[i++].trim());
        if (i < lines.length && FENCE.test(lines[i]) && /^\s+/.test(lines[i])) {
          const start = i;
          const fence = FENCE.exec(lines[i]);
          i++;
          while (i < lines.length && !(lines[i].trim().startsWith(fence[2]) && lines[i].trim().replace(/[`~]/g, "") === "")) i++;
          i++;
          items.push(inline(item.join(" ")) + render(lines.slice(start, i).join("\n")));
          continue;
        }
        items.push(inline(item.join(" ")));
      }
      const tag = ordered ? "ol" : "ul";
      out.push("<" + tag + ">" + items.map((it) => "<li>" + it + "</li>").join("") + "</" + tag + ">");
      continue;
    }
    if (/^\s*>\s?/.test(line)) {
      const quoted = [];
      while (i < lines.length && /^\s*>\s?/.test(lines[i])) quoted.push(lines[i++].replace(/^\s*>\s?/, ""));
      out.push("<blockquote>" + render(quoted.join("\n")) + "</blockquote>");
      continue;
    }
    const para = [];
    while (i < lines.length && lines[i].trim() !== "" && !FENCE.test(lines[i]) && !LIST.test(lines[i])
      && !/^(#{1,6}\s|\s*>)/.test(lines[i])) para.push(lines[i++]);
    if (para.length === 0) para.push(lines[i++]);
    out.push("<p>" + inline(para.join("\n")).replace(/\n/g, "<br>") + "</p>");
  }
  return out.join("");
}
