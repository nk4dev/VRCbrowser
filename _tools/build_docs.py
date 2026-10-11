#!/usr/bin/env python3
"""Builds docs/index.html (Japanese) and docs/en.html (English) from the package README.

    python3 _tools/build_docs.py path/to/README.md

The README is the single source: its English half and its Japanese half become one page each.
Only the Markdown the README uses is understood (headings, paragraphs, flat lists, tables, bold, code, links).
"""
import html
import re
import sys
from pathlib import Path

SITE = "https://apps.nknighta.me/VRCbrowser/"
REPO = "https://github.com/nk4dev/VRCbrowser"
ROOT = Path(__file__).resolve().parent.parent

PAGES = {
    "ja": {
        "file": "index.html", "lang": "ja", "locale": "ja_JP", "alt_locale": "en_US",
        "title": "VRCbrowser ドキュメント",
        "description": "VRChat ワールド用のインワールド・ページビューア VRCbrowser の公式ドキュメント。導入、使い方、対応している HTML / CSS / JavaScript。",
        "lead": "VRChat ワールド用のインワールド・ページビューアです。URL を入力すると、テキスト / Markdown / 簡単な HTML / 画像をキャンバスに表示します。",
        "install": "VCC に追加", "contents": "目次", "other": "English", "other_file": "en.html", "other_lang": "en",
        "og_alt": "VRCbrowser — VRChat ワールド内で Web ページを表示",
    },
    "en": {
        "file": "en.html", "lang": "en", "locale": "en_US", "alt_locale": "ja_JP",
        "title": "VRCbrowser Documentation",
        "description": "Official documentation for VRCbrowser, an in-world page viewer for VRChat worlds: installation, usage, and the supported HTML, CSS and JavaScript.",
        "lead": "An in-world page viewer for VRChat worlds. Enter a URL and it shows text, Markdown, simple HTML or an image on a canvas.",
        "install": "Add to VCC", "contents": "Contents", "other": "日本語", "other_file": "./", "other_lang": "ja",
        "og_alt": "VRCbrowser — an in-world page viewer for VRChat worlds",
    },
}


def inline(text):
    codes = []

    def keep(match):
        codes.append("<code>" + html.escape(match.group(1)) + "</code>")
        return "\x00%d\x00" % (len(codes) - 1)

    text = re.sub(r"`([^`]+)`", keep, text)
    text = html.escape(text, quote=False)

    def link(match):
        url = match.group(2)
        if url == "LICENSE.md":
            url = REPO + "/blob/main/LICENSE.md"
        return '<a href="%s">%s</a>' % (html.escape(url), match.group(1))

    text = re.sub(r"\[([^\]]+)\]\(([^)]+)\)", link, text)
    text = re.sub(r"\*\*(.+?)\*\*", r"<strong>\1</strong>", text)
    return re.sub(r"\x00(\d+)\x00", lambda m: codes[int(m.group(1))], text)


def convert(lines, shift):
    """Markdown lines -> (html, [(level, id, text)]). shift: how many levels headings are raised."""
    out, toc = [], []
    paragraph, items, list_tag, table = [], [], None, []

    def flush():
        nonlocal paragraph, items, list_tag, table
        if paragraph:
            out.append("<p>" + inline(" ".join(paragraph)) + "</p>")
            paragraph = []
        if items:
            out.append("<%s>" % list_tag)
            out.extend("<li>" + "<br>".join(inline(part) for part in item) + "</li>" for item in items)
            out.append("</%s>" % list_tag)
            items, list_tag = [], None
        if table:
            rows = [[cell.strip() for cell in row.strip().strip("|").split("|")] for row in table]
            out.append('<div class="table"><table><thead><tr>' + "".join("<th>%s</th>" % inline(c) for c in rows[0]) + "</tr></thead><tbody>")
            for row in rows[2:]:
                out.append("<tr>" + "".join("<td>%s</td>" % inline(c) for c in row) + "</tr>")
            out.append("</tbody></table></div>")
            table = []

    for line in lines:
        if not line.strip():
            flush()
            continue
        heading = re.match(r"(#{1,6}) +(.*)", line)
        if heading:
            flush()
            level = max(2, len(heading.group(1)) - shift)
            text = heading.group(2).strip()
            ident = "s%d" % (len(toc) + 1)
            toc.append((level, ident, text))
            out.append('<h%d id="%s">%s</h%d>' % (level, ident, inline(text), level))
            continue
        if line.startswith("|"):
            if not table:
                flush()
            table.append(line)
            continue
        item = re.match(r"(-|\d+\.) +(.*)", line)
        if item:
            tag = "ul" if item.group(1) == "-" else "ol"
            if paragraph or table or (list_tag and list_tag != tag):
                flush()
            list_tag = tag
            items.append([item.group(2)])
            continue
        if items and line.startswith(" "):
            items[-1].append(line.strip())  # continuation line of the list item
            continue
        if items or table:
            flush()
        paragraph.append(line.strip())
    flush()
    return "\n".join(out), toc


def split_readme(text):
    lines = text.replace("\r\n", "\n").split("\n")
    rule = lines.index("---")
    english, japanese = lines[:rule], lines[rule + 1:]
    # English: drop the title, the one-line summary and the language link (the page has its own header)
    english = english[next(i for i, line in enumerate(english) if line.startswith("## ")):]
    # Japanese: drop "## 日本語" and its summary; its sections are one level deeper
    japanese = japanese[next(i for i, line in enumerate(japanese) if line.startswith("### ")):]
    return english, japanese


def page(key, body, toc):
    p = PAGES[key]
    url = SITE + "docs/" + ("" if p["file"] == "index.html" else p["file"])
    nav = "\n".join('<li class="l%d"><a href="#%s">%s</a></li>' % (level, ident, inline(text)) for level, ident, text in toc if level <= 3)
    alternates = "\n".join('<link rel="alternate" hreflang="%s" href="%sdocs/%s">' % (PAGES[k]["lang"], SITE, "" if PAGES[k]["file"] == "index.html" else PAGES[k]["file"]) for k in PAGES)
    return TEMPLATE.format(
        lang=p["lang"], title=html.escape(p["title"]), description=html.escape(p["description"]), url=url, site=SITE, repo=REPO,
        locale=p["locale"], alt_locale=p["alt_locale"], og_alt=html.escape(p["og_alt"]), alternates=alternates,
        lead=html.escape(p["lead"]), install=p["install"], contents=p["contents"], other=p["other"], other_file=p["other_file"], other_lang=p["other_lang"], nav=nav, body=body)


TEMPLATE = """<!DOCTYPE html>
<html lang="{lang}">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>{title}</title>
<meta name="description" content="{description}">
<link rel="canonical" href="{url}">
{alternates}
<meta property="og:type" content="article">
<meta property="og:site_name" content="VRCbrowser">
<meta property="og:title" content="{title}">
<meta property="og:description" content="{description}">
<meta property="og:url" content="{url}">
<meta property="og:locale" content="{locale}">
<meta property="og:locale:alternate" content="{alt_locale}">
<meta property="og:image" content="{site}og-image.png">
<meta property="og:image:type" content="image/png">
<meta property="og:image:width" content="1200">
<meta property="og:image:height" content="630">
<meta property="og:image:alt" content="{og_alt}">
<meta name="twitter:card" content="summary_large_image">
<meta name="twitter:title" content="{title}">
<meta name="twitter:description" content="{description}">
<meta name="twitter:image" content="{site}og-image.png">
<meta name="theme-color" content="#2463eb">
<link rel="icon" href="../favicon.svg" type="image/svg+xml">
<link rel="stylesheet" href="docs.css">
</head>
<body>
<header class="top">
  <a class="brand" href="{url}">VRCbrowser</a>
  <nav class="links">
    <a href="../install.html">{install}</a>
    <a href="{repo}">GitHub</a>
    <a href="{other_file}" hreflang="{other_lang}">{other}</a>
  </nav>
</header>
<div class="layout">
<aside class="toc">
  <h2>{contents}</h2>
  <ul>
{nav}
  </ul>
</aside>
<main>
  <h1>{title}</h1>
  <p class="lead">{lead}</p>
  <p><a class="button" href="../install.html">{install}</a></p>
{body}
</main>
</div>
</body>
</html>
"""


def main():
    readme = Path(sys.argv[1]).read_text(encoding="utf-8")
    english, japanese = split_readme(readme)
    for key, lines, shift in (("en", english, 0), ("ja", japanese, 1)):
        body, toc = convert(lines, shift)
        target = ROOT / "docs" / PAGES[key]["file"]
        target.write_text(page(key, body, toc), encoding="utf-8")
        print(target.relative_to(ROOT), len(toc), "sections")


if __name__ == "__main__":
    main()
