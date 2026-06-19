#!/usr/bin/env python3
"""Embed the shipped README into the `description` field of the Steam Workshop manifest.

Reads release/ColinsPatchKit/content/README.md (the concise, Workshop-facing README),
converts its Markdown to Steam Workshop BBCode, and writes the result into the
`description` field of release/ColinsPatchKit/workshop.json (all other manifest fields
are left untouched). Re-run after editing that README.

Conversion rules (Markdown -> Steam BBCode):
  - the top-level H1 title is dropped (Steam renders its own title from `title`)
  - the table-of-contents list is dropped (Steam descriptions have no in-page anchors)
  - H2/H3/H4 -> [h1]/[h2]/[h3]; **bold** -> [b]; *italic* -> [i]
  - [text](url) -> [url=url]text[/url]; the install table -> a Steam [table]
  - inline `code` -> plain text (Steam has no inline-mono tag)
  - ![alt](path) -> [img]<raw GitHub URL>[/img]

Caveat: Steam Workshop often only renders [img] tags that point at its own CDN, so
external (GitHub raw) images may not display. Set IMAGE_MODE = "omit" to drop them,
or "link" to render each as a text link instead.

Usage: python3 release/update_workshop_json.py
"""

import json
import re
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
README = REPO_ROOT / "release" / "ColinsPatchKit" / "content" / "README.md"
MANIFEST = REPO_ROOT / "release" / "ColinsPatchKit" / "workshop.json"
RAW_BASE = "https://raw.githubusercontent.com/colinking/sts2-patch-kit/main/"

# "img" -> [img] with raw URL | "link" -> text link | "omit" -> drop entirely
IMAGE_MODE = "img"

# Steam caps a Workshop item description at 8000 characters.
MAX_DESCRIPTION_LEN = 8000

IMAGE_RE = re.compile(r"^!\[([^\]]*)\]\(([^)]+)\)\s*$")
HEADING_RE = re.compile(r"^(#{1,6})\s+(.*)$")
LIST_ITEM_RE = re.compile(r"^(\s*)-\s+(.*)$")
TOC_LINK_RE = re.compile(r"^\[[^\]]+\]\(#[^)]*\)$")  # a bare anchor link (table of contents)


def convert_inline(text: str) -> str:
    text = re.sub(r"\*\*(.+?)\*\*", r"[b]\1[/b]", text)            # bold
    text = re.sub(r"(?<!\*)\*(?!\*)(.+?)(?<!\*)\*(?!\*)", r"[i]\1[/i]", text)  # italic
    text = re.sub(r"\[([^\]]+)\]\(([^)]+)\)", r"[url=\2]\1[/url]", text)       # links
    text = re.sub(r"`([^`]+)`", r"\1", text)                       # inline code -> plain
    return text.strip()


def image_bbcode(alt: str, path: str) -> str | None:
    url = path if path.startswith("http") else RAW_BASE.rstrip("/") + "/" + path.lstrip("/")
    if IMAGE_MODE == "omit":
        return None
    if IMAGE_MODE == "link":
        return f"[url={url}]{alt or 'screenshot'}[/url]"
    return f"[img]{url}[/img]"


def markdown_to_bbcode(md: str) -> str:
    lines = md.splitlines()
    blocks: list[str] = []
    para: list[str] = []
    list_items: list[str] = []
    table_rows: list[str] = []
    seen_title = False

    def flush_para():
        if para:
            blocks.append(convert_inline(" ".join(para)))
            para.clear()

    def flush_list():
        if list_items:
            body = "".join(f"[*]{convert_inline(it)}\n" for it in list_items)
            blocks.append(f"[list]\n{body}[/list]")
            list_items.clear()

    def flush_table():
        if table_rows:
            rows = [[c.strip() for c in r.strip().strip("|").split("|")] for r in table_rows]
            rows = [r for r in rows if not all(set(c) <= {"-", ":", " "} for c in r)]  # drop separator
            out = ["[table]"]
            for idx, cells in enumerate(rows):
                tag = "th" if idx == 0 else "td"
                cells_bb = "".join(f"[{tag}]{convert_inline(c)}[/{tag}]" for c in cells)
                out.append(f"[tr]{cells_bb}[/tr]")
            out.append("[/table]")
            blocks.append("\n".join(out))
            table_rows.clear()

    def flush_all():
        flush_para()
        flush_list()
        flush_table()

    for raw in lines:
        line = raw.rstrip()

        if line.startswith("|"):  # table row
            flush_para()
            flush_list()
            table_rows.append(line)
            continue
        flush_table()

        if not line.strip():  # blank line ends a paragraph/list
            flush_para()
            flush_list()
            continue

        heading = HEADING_RE.match(line)
        if heading:
            flush_all()
            level, text = len(heading.group(1)), heading.group(2).strip()
            if level == 1:
                seen_title = True  # drop the single top-level title
                continue
            tag = {2: "h1", 3: "h2", 4: "h3"}.get(level, "h3")
            blocks.append(f"[{tag}]{convert_inline(text)}[/{tag}]")
            continue

        img = IMAGE_RE.match(line)
        if img:
            flush_all()
            bb = image_bbcode(img.group(1), img.group(2))
            if bb:
                blocks.append(bb)
            continue

        item = LIST_ITEM_RE.match(line)
        if item:
            flush_para()
            content = item.group(2).strip()
            if TOC_LINK_RE.match(content):  # table-of-contents entry -> drop
                continue
            list_items.append(content)
            continue

        # continuation of the current list item or paragraph (Markdown soft-wrap)
        if list_items:
            list_items[-1] += " " + line.strip()
        else:
            para.append(line.strip())

    flush_all()
    blocks = [b for b in blocks if b.strip()]

    # Join blocks with a blank line, except a header hugs the block beneath it
    # (Steam already renders space below a header, so the extra newline is redundant).
    out = ""
    for idx, block in enumerate(blocks):
        if idx:
            prev = blocks[idx - 1]
            if prev.startswith("[img]") or re.match(r"^\[h[1-3]\]", prev):
                out += "\n"  # images and headers hug the block beneath them
            else:
                out += "\n\n"  # blank line before everything else (incl. before images)
        out += block
    return out


def main() -> None:
    description = markdown_to_bbcode(README.read_text(encoding="utf-8"))

    if len(description) > MAX_DESCRIPTION_LEN:
        raise SystemExit(
            f"Description is {len(description)} chars, over Steam's "
            f"{MAX_DESCRIPTION_LEN}-char limit by {len(description) - MAX_DESCRIPTION_LEN}. "
            f"Manifest not updated; trim {README.relative_to(REPO_ROOT)} and re-run."
        )

    # Preserve the BOM and field order of the existing manifest.
    manifest = json.loads(MANIFEST.read_text(encoding="utf-8-sig"))
    manifest["description"] = description
    MANIFEST.write_text(
        json.dumps(manifest, indent=2, ensure_ascii=False) + "\n",
        encoding="utf-8-sig",
    )
    print(
        f"Wrote {len(description)} chars of BBCode into {MANIFEST.relative_to(REPO_ROOT)} "
        f"({MAX_DESCRIPTION_LEN - len(description)} under Steam's {MAX_DESCRIPTION_LEN}-char limit)"
    )


if __name__ == "__main__":
    main()
