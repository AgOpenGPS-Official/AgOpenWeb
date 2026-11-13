#!/usr/bin/env python3
# AgOpenWeb
# Copyright (C) 2026 AgOpenWeb Contributors
# SPDX-License-Identifier: Apache-2.0
"""Combine the job harness reports (field, path, rig, turn intervention, imperial) into one.

    combine-job-reports.py OUT.md LABEL=REPORT.md [LABEL=REPORT.md ...]

Each report starts with "## Findings (each kind once, most serious first)": numbered entries
"N. **Severity** `CODE` ×k: text" with "   Seen in: J1, J2" under them (or a table, "| Code |
Severity | ...", in the web client and working day reports). The jobs share one
engine, so one cause shows up in several reports under the same code; the combined report lists
each code once, with every harness and job it was seen in and the first harness's text, most
serious first. The full reports stay as their own artifacts.
"""
import re
import sys

ORDER = ["Safety", "DataLoss", "Bug", "Inconsistency", "Improvement", "Harness"]
# Some harnesses put the kind after the severity: "**Bug** (app defect) `CODE`" (dump replay).
ENTRY = re.compile(r"^\d+\. \*\*(?P<sev>[A-Za-z]+)\*\* (?:\([^)]*\) )?`(?P<code>[A-Z0-9-]+)` ×(?P<n>\d+): (?P<text>.*)$")
# The web client and working day harnesses list them as a table instead: "| Code | Severity | ..."
# with the text in the last column ("First seen as" or "Finding") and, in the web client's,
# the scenarios it was seen in ("Scenarios"). Cells escape "|" as "\|".
CELL_SPLIT = re.compile(r"(?<!\\)\|")


def _cells(line):
    return [c.strip().replace("\\|", "|") for c in CELL_SPLIT.split(line.strip())[1:-1]]


def _table_row(header, line):
    cells = _cells(line)
    if len(cells) != len(header):
        return None
    col = dict(zip(header, cells))
    code = col.get("Code", "").strip("`")
    if not re.fullmatch(r"[A-Z][A-Z0-9-]*", code):  # not the "|---|" line
        return None
    seen = col.get("Scenarios", "")
    n = len([s for s in seen.split(",") if s.strip()]) or 1
    return (col.get("Severity", ""), code, n, cells[-1], seen)


def findings(path):
    """[(severity, code, count, text, seen)] from a report's findings list."""
    try:
        lines = open(path, encoding="utf-8").read().splitlines()
    except OSError:
        return None
    # A harness report lists them under "## Findings"; the combined report has no such heading
    # and is all findings.
    out, inside = [], not any(l.startswith("## Findings") for l in lines)
    header = None
    for i, line in enumerate(lines):
        if line.startswith("## Findings"):
            inside = True
            continue
        if inside and line.startswith("## ") and not line.startswith("## Findings"):
            break
        if inside and line.startswith("| Code |"):
            header = _cells(line)
            continue
        if inside and header and line.startswith("|"):
            row = _table_row(header, line)
            if row:
                out.append(row)
            continue
        m = ENTRY.match(line) if inside else None
        if m:
            seen = ""
            if i + 1 < len(lines) and lines[i + 1].strip().startswith("Seen in:"):
                seen = lines[i + 1].strip()[len("Seen in:"):].strip()
            out.append((m["sev"], m["code"], int(m["n"]), m["text"], seen))
    return out


def main(argv):
    if len(argv) < 3:
        print(__doc__)
        return 2
    out_path, pairs = argv[1], [a.split("=", 1) for a in argv[2:]]
    codes, missing = {}, []
    for label, path in pairs:
        f = findings(path)
        if f is None:
            missing.append(label)
            continue
        for sev, code, n, text, seen in f:
            c = codes.setdefault(code, {"sev": sev, "text": text, "where": [], "n": 0})
            c["n"] += n
            c["where"].append(f"{label}: {seen}" if seen else label)
            if sev in ORDER and c["sev"] in ORDER and ORDER.index(sev) < ORDER.index(c["sev"]):
                c["sev"] = sev  # the most serious any harness gave it

    def rank(item):
        sev = item[1]["sev"]
        return (ORDER.index(sev) if sev in ORDER else len(ORDER), -len(item[1]["where"]), item[0])

    lines = ["# Job scenarios: the combined findings", "",
             "The field, path, rig, turn intervention and imperial job harnesses run on one engine, so a cause "
             "shows up in several of them under the same code. Each code once, most serious first, with every "
             "harness and job it was seen in; the text is the first harness's. The full reports are the "
             "`job-scenarios-report-*` artifacts.", ""]
    if missing:
        lines += [f"No report from: {', '.join(missing)} (the run failed or was cut short; see its job).", ""]
    if not codes:
        lines.append("No findings.")
    for k, (code, c) in enumerate(sorted(codes.items(), key=rank), 1):
        lines.append(f"{k}. **{c['sev']}** `{code}` ×{c['n']}: {c['text']}")
        lines.append(f"   Seen in: {'; '.join(c['where'])}")
    open(out_path, "w", encoding="utf-8").write("\n".join(lines) + "\n")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
