#!/usr/bin/env python3
# AgOpenWeb
# Copyright (C) 2026 AgOpenWeb Contributors
# SPDX-License-Identifier: Apache-2.0
"""The lines of a commit a reviewer has to read: every added line that is not a copy of a
removed one (Plans/MAINVIEWMODEL_REFACTOR_PLAN.md, migration guard 1, "moves are moves").

    Tools/move-check.py <commit> [<commit> ...]     each commit against its parent
    Tools/move-check.py <base>..<head>               each commit in the range, oldest first

A line counts as moved when the same text, ignoring leading and trailing whitespace, was
removed anywhere in the same commit, as many times as it was added. Blank lines and lines
that are only braces are left out. What is printed is what changed beyond the move: read it
with `git show --color-moved=dimmed-zebra <commit>` beside it.
"""
import collections
import re
import subprocess
import sys

TRIVIAL = re.compile(r"^[{}();,\s]*$")


def git(*args):
    return subprocess.run(["git", *args], check=True, capture_output=True, text=True).stdout


def commits(spec):
    if ".." in spec:
        return git("rev-list", "--reverse", spec).split()
    return [git("rev-parse", spec).strip()]


def check(commit):
    diff = git("show", "--format=", "--unified=0", "--no-color", commit)
    added, removed = [], collections.Counter()
    path, line_no = None, 0
    for line in diff.splitlines():
        if line.startswith("+++ "):
            path = line[6:] if line.startswith("+++ b/") else line[4:]
        elif line.startswith("@@"):
            m = re.search(r"\+(\d+)", line)
            line_no = int(m.group(1)) if m else 0
        elif line.startswith("+"):
            text = line[1:]
            if not TRIVIAL.match(text):
                added.append((path, line_no, text))
            line_no += 1
        elif line.startswith("-") and not line.startswith("--- "):
            if not TRIVIAL.match(line[1:]):
                removed[line[1:].strip()] += 1
    new = []
    for path, no, text in added:
        key = text.strip()
        if removed[key] > 0:
            removed[key] -= 1
        else:
            new.append((path, no, text))
    total = len(added)
    title = git("show", "-s", "--format=%h %s", commit).strip()
    print(f"== {title}")
    print(f"   {total} added lines, {total - len(new)} moved, {len(new)} new")
    last = None
    for path, no, text in new:
        if path != last:
            print(f"   {path}")
            last = path
        print(f"   {no:6} | {text}")
    print()
    return len(new)


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    for spec in sys.argv[1:]:
        for c in commits(spec):
            check(c)
    return 0


if __name__ == "__main__":
    sys.exit(main())
