#!/usr/bin/env python3
# AgOpenWeb
# Copyright (C) 2024-2026 AgOpenWeb Contributors
# SPDX-License-Identifier: Apache-2.0
"""Keep every source file under the project's licence, Apache 2.0 (LICENSE.md).

Each .cs, .js, .py and .sh file starts with this header (after a #! line, if any),
in the file's own comment syntax:

    // AgOpenWeb
    // Copyright (C) <year>[-<year>] AgOpenWeb Contributors
    // SPDX-License-Identifier: Apache-2.0

and no tracked text file names another licence (GPL, MIT, BSD, ...). Third-party
code (wwwroot/vendor), the AgOpenGPS reference copy (AgOpen_Snapshot) and Plans/
are left alone. See Plans/LICENSE_HEADERS_PLAN.md.

Usage:
  Tools/license-headers.py           add or rewrite headers (old GPL headers included)
  Tools/license-headers.py --check   exit 1 if a file's header is missing, changed or
                                     names another licence (CI / tests)
"""
import os
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
HOLDER = "AgOpenWeb Contributors"
SPDX = "SPDX-License-Identifier: Apache-2.0"
NEW_YEARS = "2024-2026"

COMMENT = {".cs": "//", ".js": "//", ".py": "#", ".sh": "#"}
# Text files scanned for another licence's name, besides the ones that carry a header.
SCANNED = {".md", ".html", ".css", ".axaml", ".xml", ".csproj", ".props", ".targets",
           ".yml", ".yaml", ".json", ".txt", ".ps1", ".iss", ".plist", ".service"}
SKIP = ("AgOpen_Snapshot/", "Plans/", "LICENSE.md", "Tools/license-headers.py")

COPYRIGHT = re.compile(r"Copyright \(C\) (\d{4}(?:-\d{4})?) " + re.escape(HOLDER) + "$")
FOREIGN = re.compile(
    r"General Public License|\b[AL]?GPL(?:-?v?\d(?:\.\d)?)?\b|Mozilla Public License"
    r"|\bMIT License\b|\bBSD[- ]\d-Clause\b|Creative Commons|\bUnlicense\b"
    r"|SPDX-License-Identifier:(?! Apache-2\.0\b(?!\s+(?:OR|AND|WITH)\b))")


def tracked():
    out = subprocess.check_output(["git", "ls-files", "-z"], cwd=ROOT)
    for name in out.decode().split("\0"):
        if not name or name.startswith(SKIP) or "/vendor/" in name:
            continue
        ext = os.path.splitext(name)[1].lower()
        if ext in COMMENT or ext in SCANNED:
            yield name, ext


def header(c, years):
    return [f"{c} AgOpenWeb", f"{c} Copyright (C) {years} {HOLDER}", f"{c} {SPDX}"]


def split_shebang(lines):
    return (lines[:1], lines[1:]) if lines and lines[0].startswith("#!") else ([], lines)


def fixed(text, c):
    """The file with the header in place; any older AgOpenWeb header (GPL, short or
    copyright-only) is replaced, keeping its years."""
    shebang, lines = split_shebang(text.split("\n"))
    years = NEW_YEARS
    if lines and lines[0] == f"{c} AgOpenWeb" and len(lines) > 1:
        m = COPYRIGHT.match(lines[1][len(c) + 1:])
        if m:
            years = m.group(1)
            rest = lines[2:]
            if rest and rest[0] == f"{c} {SPDX}":
                rest = rest[1:]
            # Old GPL notice: the comment lines up to its last line.
            for end in ("along with this program", "Licensed under GNU GPL v3", "any later version"):
                i = next((k for k, l in enumerate(rest[:20]) if end in l), None)
                if i is not None:
                    rest = rest[i + 1:]
                    break
            if rest and rest[0] == c:
                rest = rest[1:]
            lines = rest
    while lines and lines[0] == "":
        lines = lines[1:]
    return "\n".join(shebang + header(c, years) + ([""] + lines if lines else [""]))


def problems(name, ext, text):
    c = COMMENT.get(ext)
    if c:
        _, lines = split_shebang(text.split("\n"))
        start = 2 if text.startswith("#!") else 1
        top = lines[:3] + [""] * 3
        if top[0] != f"{c} AgOpenWeb" or not COPYRIGHT.match(top[1][len(c) + 1:]) \
                or top[2] != f"{c} {SPDX}":
            yield start, "missing or changed licence header; expected:\n    " + \
                "\n    ".join(header(c, "<years>"))
    for n, line in enumerate(text.split("\n"), 1):
        if FOREIGN.search(line):
            yield n, f"names a licence other than Apache 2.0: {line.strip()[:100]}"


def main():
    check = "--check" in sys.argv[1:]
    annotate = os.environ.get("GITHUB_ACTIONS") == "true"
    bad = changed = 0
    for name, ext in tracked():
        path = ROOT / name
        try:
            text = path.read_text(encoding="utf-8")
        except (UnicodeDecodeError, FileNotFoundError):
            continue
        if not check and ext in COMMENT:
            new = fixed(text, COMMENT[ext])
            if new != text:
                path.write_text(new, encoding="utf-8")
                changed += 1
                text = new
        for line, msg in problems(name, ext, text):
            bad += 1
            if annotate:
                print(f"::error file={name},line={line}::{msg.splitlines()[0]}")
            print(f"{name}:{line}: {msg}")
    if not check:
        print(f"{changed} file(s) updated")
    if bad:
        print(f"\n{bad} licence problem(s). Source files carry the Apache 2.0 header above "
              "(Tools/license-headers.py adds it); see CONTRIBUTING.md.")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
