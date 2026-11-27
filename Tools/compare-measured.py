#!/usr/bin/env python3
# AgOpenWeb
# Copyright (C) 2026 AgOpenWeb Contributors
# SPDX-License-Identifier: Apache-2.0
"""The measured values of two harness reports side by side: every table row whose value differs.

    compare-measured.py BASE.md FIX.md [BASE2.md]

Findings are compared by compare-reports.py; this is the other half of a refactor round's pass
mark (Plans/MAINVIEWMODEL_REFACTOR_PLAN.md): the numbers the reports measure (area sprayed, misses,
turns, timings, positions) within the spread the base showed. Rows are matched by the heading they
sit under and their first cell. With BASE2 (a second run of the base) a row is marked when the fix
lies outside what the two base runs showed, and the base's own differences are listed as the
spread; without it every difference is listed for reading.
"""
import re
import sys


def tables(path):
    rows, heading = {}, ""
    for line in open(path, encoding="utf-8"):
        line = line.rstrip("\n")
        if line.startswith("#"):
            heading = line.lstrip("#").strip()
            continue
        if not line.startswith("|") or re.match(r"^\|[-| :]+\|$", line):
            continue
        cells = [c.strip() for c in line.strip("|").split("|")]
        if len(cells) < 2 or cells[0] in ("Measured", "What", ""):
            continue
        rows[(heading, cells[0])] = " | ".join(cells[1:])
    return rows


def numbers(text):
    return [float(n) for n in re.findall(r"-?\d+(?:\.\d+)?", text.replace(",", ""))]


def outside(fix, a, b):
    """Is any number of the fix's value outside the range of the two base values?"""
    nf, na, nb = numbers(fix), numbers(a), numbers(b)
    if not (len(nf) == len(na) == len(nb)):
        return fix not in (a, b)
    return any(f < min(x, y) - 1e-9 or f > max(x, y) + 1e-9 for f, x, y in zip(nf, na, nb))


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        return 2
    base, fix = tables(sys.argv[1]), tables(sys.argv[2])
    base2 = tables(sys.argv[3]) if len(sys.argv) > 3 else None
    out_of_spread = 0
    for key in sorted(set(base) | set(fix)):
        b, f = base.get(key), fix.get(key)
        if b == f and (base2 is None or base2.get(key) == b):
            continue
        heading, name = key
        if base2 is None:
            print(f"{heading}\n  {name}\n    base: {b}\n    fix:  {f}")
            continue
        b2 = base2.get(key)
        mark = "" if None not in (b, f, b2) and not outside(f, b, b2) else "  OUTSIDE THE BASE'S SPREAD"
        out_of_spread += bool(mark)
        print(f"{heading}\n  {name}{mark}\n    base: {b}\n    base2: {b2}\n    fix:  {f}")
    if base2 is not None:
        print(f"\n{out_of_spread} value(s) outside the base's spread")
    return 1 if out_of_spread else 0


if __name__ == "__main__":
    sys.exit(main())
