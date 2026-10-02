#!/usr/bin/env python3
"""Regenerate the web client's English source strings (wwwroot/i18n/en.json).

The key of every entry is the English text itself (see Docs/TRANSLATIONS.md). Strings
come from:
  * index.html — text and title / placeholder / aria-label attributes, outside
    <script>, <style> and translate="no" elements;
  * app.js — tr('…') calls, and text inside HTML that the script builds (">text<").

Usage:
  Tools/i18n-extract.py           rewrite en.json
  Tools/i18n-extract.py --check   exit 1 if en.json is out of date (CI / tests)
"""
import json
import re
import sys
from html.parser import HTMLParser
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
WWW = ROOT / "Shared/AgOpenWeb.RemoteServer/wwwroot"
OUT = WWW / "i18n/en.json"
ATTRS = ("title", "placeholder", "aria-label")
VOID = {"area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta",
        "source", "track", "wbr"}


def norm(s):
    """As the loader does: collapse whitespace; "Wheelbase (" before a unit label is "Wheelbase"."""
    return re.sub(r"\s+", " ", re.sub(r"\s*\(?\s*$", "", s)).strip()


def translatable(s):
    """Two letters in a row somewhere, so "—", "0.0" and "✕" are left alone."""
    return re.search(r"[^\W\d_]{2}", s) is not None


class HtmlStrings(HTMLParser):
    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.found = set()
        self.skip = []      # open tags inside which text is not collected

    def handle_starttag(self, tag, attrs):
        a = dict(attrs)
        skipping = bool(self.skip)
        if tag in ("script", "style") or a.get("translate") == "no" or skipping:
            if tag not in VOID:
                self.skip.append(tag)
            if skipping or a.get("translate") == "no":
                return
        for name in ATTRS:
            if a.get(name):
                self.add(a[name])

    def handle_endtag(self, tag):
        if self.skip and self.skip[-1] == tag:
            self.skip.pop()

    def handle_data(self, data):
        if not self.skip:
            self.add(data)

    def add(self, s):
        s = norm(s)
        if translatable(s):
            self.found.add(s)


STR = r"""'((?:[^'\\\n]|\\.)*)'|"((?:[^"\\\n]|\\.)*)"|`((?:[^`\\]|\\.)*)`"""


def unescape_js(s):
    return re.sub(r"\\(.)", lambda m: {"n": "\n", "t": "\t"}.get(m.group(1), m.group(1)), s)


def js_strings(src):
    found = set()

    def add(s):
        s = norm(unescape_js(s))
        if translatable(s) and "${" not in s:
            found.add(s)

    # tr('…') — explicit, may carry {placeholders}.
    for m in re.finditer(r"(?<![\w.$])tr\(\s*(?:" + STR + ")", src):
        add(next(g for g in m.groups() if g is not None))

    # ">text<" inside any string literal (HTML the script builds).
    for m in re.finditer(STR, src):
        lit = next(g for g in m.groups() if g is not None)
        if re.search(r"</?[a-zA-Z]", lit):
            for text in re.findall(r">([^<>]+)<", lit):
                add(text)
            for name in ATTRS:
                for text in re.findall(name + r'="([^"]+)"', lit):
                    add(text)

    return found


def collect():
    html = HtmlStrings()
    html.feed((WWW / "index.html").read_text(encoding="utf-8"))
    strings = html.found | js_strings((WWW / "app.js").read_text(encoding="utf-8"))
    return {s: s for s in sorted(strings, key=lambda s: (s.lower(), s))}


def main():
    text = json.dumps(collect(), ensure_ascii=False, indent=2) + "\n"
    if "--check" in sys.argv:
        if not OUT.exists() or OUT.read_text(encoding="utf-8") != text:
            print("i18n/en.json is out of date — run Tools/i18n-extract.py", file=sys.stderr)
            return 1
        return 0
    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text(text, encoding="utf-8")
    print(f"{OUT.relative_to(ROOT)}: {len(json.loads(text))} strings")
    return 0


if __name__ == "__main__":
    sys.exit(main())
