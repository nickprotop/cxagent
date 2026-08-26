#!/usr/bin/env python3
"""Render the repo's own markdown into styled site pages.

ONE SOURCE, NOT TWO. The documents rendered here live in the repository and are edited there; the
site renders them rather than carrying a copy, because a copy is a second thing to keep true and
the two would diverge on the first edit that forgot one.

STANDARD LIBRARY ONLY -- no pip install in CI. This is a deliberately small renderer covering the
constructs these three documents actually use, not a general GitHub-flavoured markdown
implementation. It is not fit for arbitrary markdown, and the link check below is what stops it
failing silently on something it does not understand.
"""

import argparse
import html
import re
import shutil
import sys
from collections import OrderedDict
from pathlib import Path

GITHUB_BLOB = "https://github.com/nickprotop/cxagent/blob/master"

# WHAT RENDERS, AND WHERE IT LANDS. Anything not here links to GitHub instead: api.md and tools.md
# are for embedders already reading source, and the maintainer documents are not written for a
# visitor. See the design doc, "Rendering three documents".
RENDERED = OrderedDict([
    ("COMMANDS.md", "commands"),
    ("CONFIG.md", "config"),
    ("cxagent.Core/docs/plugins.md", "plugins"),
    ("docs/screenshots/README.md", "walkthrough"),
])


# WHAT COUNTS AS AN ASSET rather than a document to link out to. Deliberately short: anything not
# listed here is treated as a link, which is the safer default -- a wrong link is visible, a file
# silently copied into the site is not.
ASSET_SUFFIXES = {".png", ".jpg", ".jpeg", ".gif", ".svg", ".webp"}


def slug(text: str) -> str:
    """GitHub's heading-anchor rule: lowercase, punctuation dropped, spaces to hyphens.

    MUST MATCH GITHUB'S, because the links being rewritten were written against GitHub's anchors --
    `CONFIG.md#agents-sub-agent-types` resolves there and must resolve here too.
    """
    text = re.sub(r"`([^`]*)`", r"\1", text)
    text = re.sub(r"\*\*([^*]*)\*\*", r"\1", text)
    text = text.strip().lower()
    text = re.sub(r"[^\w\s-]", "", text)
    # GitHub does NOT collapse runs of whitespace into one hyphen: an em-dash between two spaced
    # words leaves two spaces once stripped, and each becomes its own hyphen ("agents — sub-agent
    # types" -> "agents--sub-agent-types", double hyphen). Collapsing here would make headings with
    # punctuation-then-space resolve to an anchor GitHub itself does not produce.
    return re.sub(r"[\s_]", "-", text)


def headings_of(markdown: str) -> set:
    return {slug(m.group(2)) for m in re.finditer(r"^(#{1,6})\s+(.*)$", markdown, re.M)}


def inline(text: str) -> str:
    """Inline constructs, escaped first so a document cannot inject markup into the site."""
    out = html.escape(text)
    out = re.sub(r"`([^`]+)`", r"<code>\1</code>", out)
    out = re.sub(r"\*\*([^*]+)\*\*", r"<strong>\1</strong>", out)
    out = re.sub(r"(?<!\*)\*([^*]+)\*(?!\*)", r"<em>\1</em>", out)
    return out


def render_body(markdown: str, resolve) -> str:
    """Block constructs. `resolve` maps a link target to a URL, or raises for a bad one."""
    lines = markdown.split("\n")
    out, i = [], 0

    while i < len(lines):
        line = lines[i]

        if line.startswith("```"):
            lang = line[3:].strip()
            i += 1
            block = []
            while i < len(lines) and not lines[i].startswith("```"):
                block.append(lines[i])
                i += 1
            i += 1
            cls = f' class="language-{html.escape(lang)}"' if lang else ""
            out.append(f"<pre><code{cls}>{html.escape(chr(10).join(block))}</code></pre>")
            continue

        # A THEMATIC BREAK, which these documents use to separate one capture's section from the
        # next. Matched before the paragraph branch, or a run of hyphens on its own line renders as
        # the literal text "---".
        if re.fullmatch(r"\s*(-{3,}|\*{3,}|_{3,})\s*", line):
            out.append("<hr>")
            i += 1
            continue

        heading = re.match(r"^(#{1,6})\s+(.*)$", line)
        if heading:
            level = len(heading.group(1))
            text = heading.group(2)
            out.append(f'<h{level} id="{slug(text)}">{link_up(inline(text), resolve)}</h{level}>')
            i += 1
            continue

        if line.startswith("|") and i + 1 < len(lines) and re.match(r"^\|[\s:|-]+\|$", lines[i + 1]):
            header = [c.strip() for c in line.strip("|").split("|")]
            i += 2
            rows = []
            while i < len(lines) and lines[i].startswith("|"):
                rows.append([c.strip() for c in lines[i].strip("|").split("|")])
                i += 1
            head = "".join(f"<th>{link_up(inline(c), resolve)}</th>" for c in header)
            body = "".join(
                "<tr>" + "".join(f"<td>{link_up(inline(c), resolve)}</td>" for c in r) + "</tr>"
                for r in rows)
            out.append(f"<table><thead><tr>{head}</tr></thead><tbody>{body}</tbody></table>")
            continue

        if re.match(r"^\s*[-*]\s+", line):
            items = []
            while i < len(lines) and re.match(r"^\s*[-*]\s+", lines[i]):
                items.append(re.sub(r"^\s*[-*]\s+", "", lines[i]))
                i += 1
            body = "".join(f"<li>{link_up(inline(t), resolve)}</li>" for t in items)
            out.append(f"<ul>{body}</ul>")
            continue

        if line.startswith(">"):
            quote = []
            while i < len(lines) and lines[i].startswith(">"):
                quote.append(lines[i].lstrip("> "))
                i += 1
            out.append(f"<blockquote>{link_up(inline(' '.join(quote)), resolve)}</blockquote>")
            continue

        if not line.strip():
            i += 1
            continue

        para = []
        while i < len(lines) and lines[i].strip() and not lines[i].startswith(("#", "|", ">", "```")) \
                and not re.match(r"^\s*[-*]\s+", lines[i]):
            para.append(lines[i])
            i += 1
        out.append(f"<p>{link_up(inline(' '.join(para)), resolve)}</p>")

    return "\n".join(out)


def link_up(text: str, resolve) -> str:
    """Images first, then links.

    THE ORDER IS NOT A STYLE CHOICE. The link pattern matches the tail of an image too -- the `!` is
    the only difference -- so running links first turns `![alt](x.png)` into a stray `!` followed by
    an anchor wrapping the alt text, and the image never renders at all.
    """
    def image(match):
        alt, target = match.group(1), match.group(2)
        # LAZY, because the walkthrough carries seventeen full-window terminal captures and a reader
        # arriving at the top should not wait for the ones they may never scroll to.
        return (f'<img src="{html.escape(resolve(target))}" alt="{alt}" loading="lazy">')

    def one(match):
        label, target = match.group(1), match.group(2)
        return f'<a href="{html.escape(resolve(target))}">{label}</a>'

    text = re.sub(r"!\[([^\]]*)\]\(([^)]+)\)", image, text)
    return re.sub(r"\[([^\]]+)\]\(([^)]+)\)", one, text)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", required=True, type=Path)
    parser.add_argument("--out", required=True, type=Path)
    args = parser.parse_args()

    sources = {}
    for rel in RENDERED:
        path = args.root / rel
        if not path.is_file():
            print(f"error: no document at '{path}'.", file=sys.stderr)
            return 1
        sources[rel] = path.read_text()

    anchors = {rel: headings_of(text) for rel, text in sources.items()}
    # KEYED ON THE RESOLVED PATH, NOT THE BASENAME. Two rendered documents can share a filename --
    # docs/screenshots/README.md is one -- and a basename key would make one of them claim every
    # link to the other, silently pointing a reader at the wrong page.
    by_path = {(args.root / rel).resolve(): rel for rel in RENDERED}

    problems = []
    assets: list[Path] = []

    def resolver(current: str):
        def resolve(target: str) -> str:
            if target.startswith(("http://", "https://", "#", "mailto:")):
                return target

            path_part, _, anchor = target.partition("#")
            # Resolved against the LINKING document's directory, the way a reader on GitHub would
            # follow it -- so ../README.md from cxagent.Core/docs is the repository's own README.
            resolved = ((args.root / Path(current).parent / path_part).resolve()
                        if path_part else (args.root / current).resolve())

            if resolved in by_path:
                rel = by_path[resolved]
                if anchor and anchor not in anchors[rel]:
                    problems.append(
                        f"{current} links to {target}, but '{Path(rel).name}' has no heading "
                        f"'{anchor}'.")
                    return target
                # SIBLING, NOT ROOT-ABSOLUTE. These pages are written into _site/docs/, and the
                # site is served from a project subpath (/cxagent/), where a leading "/" resolves
                # to the domain root and 404s. A bare name resolves next to the linking page.
                target_page = f"{RENDERED[rel]}.html"
                return f"{target_page}#{anchor}" if anchor else target_page

            # NOT RENDERED: leave for GitHub rather than 404 inside the site. The target is
            # resolved against the linking document's own directory, the way a reader on GitHub
            # would follow it.
            # AN ASSET IS COPIED, NOT LINKED OUT. A .png resolved to a GitHub blob URL renders
            # GitHub's page around the image rather than the image, so a walkthrough of seventeen
            # captures would show seventeen framed web pages. Assets are copied beside the rendered
            # page instead, and referenced relative to it.
            if Path(path_part).suffix.lower() in ASSET_SUFFIXES:
                source = (args.root / Path(current).parent / path_part).resolve()
                if not source.is_file():
                    problems.append(f"{current} references {target}, which does not exist.")
                    return target
                assets.append(source)
                return f"assets/{source.name}"

            joined = (args.root / Path(current).parent / path_part).resolve()
            try:
                repo_rel = joined.relative_to(args.root.resolve())
            except ValueError:
                problems.append(f"{current} links outside the repository: {target}")
                return target

            if not joined.exists():
                problems.append(f"{current} links to {target}, which does not exist.")
                return target

            return f"{GITHUB_BLOB}/{repo_rel}" + (f"#{anchor}" if anchor else "")
        return resolve

    args.out.mkdir(parents=True, exist_ok=True)

    (args.out / "docs").mkdir(exist_ok=True)

    pages = {}
    for rel, name in RENDERED.items():
        pages[name] = render_body(sources[rel], resolver(rel))

    # EVERY DOCUMENT IS CHECKED BEFORE ANY IS WRITTEN. A partial render leaves a site whose pages
    # disagree about which links work.
    if problems:
        for problem in problems:
            print(f"error: {problem}", file=sys.stderr)
        return 1

    # AFTER THE LINK CHECK, so a failed build copies nothing. Deduplicated by name: the same capture
    # referenced from two documents is one file, and two different files sharing a name would
    # silently overwrite each other -- which is why that case is refused rather than resolved.
    if assets:
        by_name: dict[str, Path] = {}
        for source in assets:
            existing = by_name.get(source.name)
            if existing is not None and existing != source:
                print(f"error: two different files are both called '{source.name}': "
                      f"{existing} and {source}.", file=sys.stderr)
                return 1
            by_name[source.name] = source

        asset_dir = args.out / "docs" / "assets"
        asset_dir.mkdir(parents=True, exist_ok=True)
        for name, source in sorted(by_name.items()):
            shutil.copyfile(source, asset_dir / name)
        print(f"copied {len(by_name)} asset(s)")

    template = (Path(__file__).parent / "doc-template.html").read_text()
    for rel, name in RENDERED.items():
        title = re.search(r"^#\s+(.*)$", sources[rel], re.M)
        (args.out / "docs" / f"{name}.html").write_text(
            template
            .replace("{{title}}", html.escape(title.group(1) if title else name))
            .replace("{{body}}", pages[name])
            .replace("{{source}}", f"{GITHUB_BLOB}/{rel}"))
        print(f"rendered {rel} -> docs/{name}.html")

    return 0


if __name__ == "__main__":
    sys.exit(main())
