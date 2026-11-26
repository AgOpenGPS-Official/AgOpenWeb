#!/usr/bin/env python3
# AgOpenWeb
# Copyright (C) 2026 AgOpenWeb Contributors
# SPDX-License-Identifier: Apache-2.0
"""Compare two harness reports: a fix against its base.

    compare-reports.py BASE.md FIX.md [OUT.md]

Every harness report (and the combined job report) lists its findings as
"N. **Severity** `CODE` ×k: text" with "   Seen in: ..." under them. This lists, by code, what the
fix made go away, what it added, and what both have, with each side's count, places and text
(the text carries the numbers: m² missed, metres, seconds). New findings come first: they are
what stops a merge. Writes to OUT.md, or prints when none is given.
"""
import importlib.util
import os
import sys

sys.dont_write_bytecode = True  # no __pycache__ beside the tools
_spec = importlib.util.spec_from_file_location(
    "combine", os.path.join(os.path.dirname(os.path.abspath(__file__)), "combine-job-reports.py"))
_combine = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_combine)
findings, ORDER = _combine.findings, _combine.ORDER


def by_code(path):
    f = findings(path)
    if f is None:
        sys.exit(f"No report at {path}")
    out = {}
    for sev, code, n, text, seen in f:
        out[code] = {"sev": sev, "n": n, "text": text, "seen": seen}
    return out


def rank(code, entry):
    return (ORDER.index(entry["sev"]) if entry["sev"] in ORDER else len(ORDER), code)


def main(argv):
    if len(argv) < 3:
        print(__doc__)
        return 2
    base, fix = by_code(argv[1]), by_code(argv[2])
    new = sorted((c for c in fix if c not in base), key=lambda c: rank(c, fix[c]))
    gone = sorted((c for c in base if c not in fix), key=lambda c: rank(c, base[c]))
    both = sorted((c for c in fix if c in base), key=lambda c: rank(c, fix[c]))

    def line(e):
        return f"**{e['sev']}** ×{e['n']}: {e['text']}" + (f" (seen in: {e['seen']})" if e["seen"] else "")

    lines = ["# Fix against base", "",
             f"Base: `{argv[1]}`  ", f"Fix: `{argv[2]}`", "",
             f"{len(new)} new, {len(gone)} gone, {len(both)} in both.", ""]
    lines += ["## New with the fix", ""] + ([f"- `{c}` {line(fix[c])}" for c in new] or ["None."]) + [""]
    lines += ["## Gone with the fix", ""] + ([f"- `{c}` {line(base[c])}" for c in gone] or ["None."]) + [""]
    lines += ["## In both", ""]
    if not both:
        lines.append("None.")
    for c in both:
        b, f = base[c], fix[c]
        if (b["sev"], b["n"], b["text"], b["seen"]) == (f["sev"], f["n"], f["text"], f["seen"]):
            lines.append(f"- `{c}` unchanged: {line(f)}")
        else:
            lines += [f"- `{c}`", f"  - base: {line(b)}", f"  - fix:  {line(f)}"]
    text = "\n".join(lines) + "\n"
    if len(argv) > 3:
        open(argv[3], "w", encoding="utf-8").write(text)
    else:
        sys.stdout.write(text)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
