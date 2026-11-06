// MARKDOWN FOR MODEL TEXT — ESCAPE FIRST, THEN FORMAT.
//
// The text this renders was written by a model, which was told things by whatever it read: a web page, a README, a
// file in the repository. So every character is escaped before any markdown is applied, and the only HTML in the
// output is the handful of tags written below. A link's target is kept only when it is http, https or mailto;
// anything else shows as text. The page's content policy (no inline script) is the backstop, not the defence.

import { escapeHtml, highlight } from "./highlight.js";

const SAFE_URL = /^(https?:\/\/|mailto:)/i;

function inline(text) {
  // CODE SPANS FIRST, set aside so nothing inside them is formatted. Placeholders use a character escaping never
  // produces, so model text cannot forge one.
  const codes = [];
  let s = escapeHtml(text).replace(/`([^`]+)`/g, function (_, code) {
    codes.push("<code>" + code + "</code>");
    return "\u0000" + (codes.length - 1) + "\u0000";
  });
  s = s.replace(/\[([^\]]+)\]\(([^)\s]+)\)/g, function (whole, label, url) {
    // THE URL IS ALREADY ESCAPED, so a quote in it cannot close the attribute; the scheme decides if it is a link.
    const raw = url.replace(/&amp;/g, "&");
    if (!SAFE_URL.test(raw)) return label;
    return '<a href="' + url + '" rel="noopener noreferrer" target="_blank">' + label + "</a>";
  });
  s = s.replace(/\*\*([^*]+)\*\*/g, "<strong>$1</strong>");
  s = s.replace(/(^|[^*])\*([^*\s][^*]*)\*/g, "$1<em>$2</em>");
  s = s.replace(/(^|\W)_([^_\s][^_]*)_(?=\W|$)/g, "$1<em>$2</em>");
  return s.replace(/\u0000(\d+)\u0000/g, function (_, i) { return codes[Number(i)]; });
}

function tableRow(line, cell) {
  const cells = line.trim().replace(/^\|/, "").replace(/\|$/, "").split("|");
  return "<tr>" + cells.map(function (c) { return "<" + cell + ">" + inline(c.trim()) + "</" + cell + ">"; }).join("") + "</tr>";
}

function blocks(text) {
  const lines = text.split("\n");
  const out = [];
  let i = 0;
  while (i < lines.length) {
    const line = lines[i];
    let m;
    if (line.trim() === "") { i++; continue; }
    if ((m = /^(#{1,6})\s+(.*)$/.exec(line))) {
      out.push("<h" + m[1].length + ">" + inline(m[2]) + "</h" + m[1].length + ">");
      i++;
      continue;
    }
    if (/^\s*\|.*\|\s*$/.test(line) && i + 1 < lines.length && /^\s*\|?\s*:?-{2,}/.test(lines[i + 1])) {
      let html = "<table><thead>" + tableRow(line, "th") + "</thead><tbody>";
      i += 2;
      while (i < lines.length && /^\s*\|.*\|\s*$/.test(lines[i])) html += tableRow(lines[i++], "td");
      out.push(html + "</tbody></table>");
      continue;
    }
    if (/^\s*([-*+]|\d+\.)\s+/.test(line)) {
      const ordered = /^\s*\d+\./.test(line);
      let html = ordered ? "<ol>" : "<ul>";
      while (i < lines.length && /^\s*([-*+]|\d+\.)\s+/.test(lines[i]))
        html += "<li>" + inline(lines[i++].replace(/^\s*([-*+]|\d+\.)\s+/, "")) + "</li>";
      out.push(html + (ordered ? "</ol>" : "</ul>"));
      continue;
    }
    if (/^>\s?/.test(line)) {
      const quoted = [];
      while (i < lines.length && /^>\s?/.test(lines[i])) quoted.push(lines[i++].replace(/^>\s?/, ""));
      out.push("<blockquote>" + inline(quoted.join(" ")) + "</blockquote>");
      continue;
    }
    const para = [];
    while (i < lines.length && lines[i].trim() !== "" && !/^(#{1,6}\s|>|\s*([-*+]|\d+\.)\s)/.test(lines[i])) para.push(lines[i++]);
    if (para.length === 0) para.push(lines[i++]);
    out.push("<p>" + inline(para.join("\n")).replace(/\n/g, "<br>") + "</p>");
  }
  return out.join("");
}

export function render(text) {
  // FENCES FIRST: what is inside a fence is code, never markdown.
  const parts = String(text).split(/^```/m);
  let html = "";
  for (let i = 0; i < parts.length; i++) {
    if (i % 2 === 0) {
      html += blocks(parts[i]);
    } else {
      const newline = parts[i].indexOf("\n");
      const lang = newline < 0 ? "" : parts[i].slice(0, newline).trim().replace(/[^A-Za-z0-9_+-]/g, "");
      const code = newline < 0 ? parts[i] : parts[i].slice(newline + 1);
      html += '<pre><code class="lang-' + (lang || "text") + '">' + highlight(code.replace(/\n$/, ""), lang) + "</code></pre>";
    }
  }
  return html;
}
