#!/usr/bin/env python3
# AgOpenWeb
# Copyright (C) 2026 AgOpenWeb Contributors
# SPDX-License-Identifier: Apache-2.0
"""The Vehicle & Tool settings sweep in shards (sweep-shards.txt beside this script).

    sweep-shards.py check SettingsSweep.cs [SettingsSweep.cs ...]   every row in one shard, none in two
    sweep-shards.py only                                             AGOPENWEB_SCENARIO_ONLY for runner-compare.sh
    sweep-shards.py matrix SettingsSweep.cs                          check, then the workflow's matrix (JSON)

A row runs in a shard when its key or its group's name contains one of the shard's "|"-separated
selector strings: SettingsSweep.RunAsync's own rule. The base side of a comparison runs its own
commit's harness, so shards can only use what that harness has. A row in no shard would never run
and one in two would run twice, so check fails on either; each file is the sweep table at one
side's commit (git show <ref>:<path>). The matrix is the two operators, then the shards.
"""
import json
import os
import re
import sys

sys.dont_write_bytecode = True
LIST = os.path.join(os.path.dirname(os.path.abspath(__file__)), "sweep-shards.txt")


def shards():
    out = []
    for line in open(LIST, encoding="utf-8"):
        line = line.strip()
        if line and not line.startswith("#"):
            name, _, selector = line.partition("=")
            out.append((name, selector))
    return out


def rows(path):
    """[(key, group)] from SettingsSweep.Cases, groups named as SweepCase.Group names them."""
    src = open(path, encoding="utf-8").read()
    start = src.index("public static readonly SweepCase[] Cases")
    body = src[start:src.index("};", start)]
    out = []
    for m in re.finditer(r'^\s*new\((.*?)\),\s*(?://[^\n]*)?$', body, re.S | re.M):
        strings = re.findall(r'"((?:[^"\\]|\\.)*)"', m.group(1))
        probe = re.search(r'Probe\.(\w+)', m.group(1)).group(1)
        key, setup = strings[0], strings[4:]
        out.append((key, probe + (" (" + ", ".join(setup) + ")" if setup else "")))
    written = len(re.findall(r'^\s*new\(', body, re.M))
    if len(out) != written:
        sys.exit(f"{path}: read {len(out)} of the table's {written} rows; sweep-shards.py needs updating")
    return out


def check(paths):
    bad = 0
    for path in paths:
        table = rows(path)
        where = {}
        for name, selector in shards():
            parts = [o.lower() for o in selector.split("|")]
            for i, (key, group) in enumerate(table):
                if any(o in key.lower() or o in group.lower() or o in "sweep" for o in parts):
                    where.setdefault(i, []).append(name)
        for i, (key, group) in enumerate(table):
            if i not in where:
                print(f"!! {path}: {key} ({group}) is in no shard: it would not run", file=sys.stderr); bad = 1
            elif len(where[i]) > 1:
                print(f"!! {path}: {key} ({group}) is in {', '.join(where[i])}: it would run twice", file=sys.stderr); bad = 1
        if not bad:
            print(f"{path}: {len(table)} rows, each in one of {len(shards())} shards", file=sys.stderr)
    return bad


def main(argv):
    cmd = argv[1] if len(argv) > 1 else ""
    if cmd == "only":
        print(",".join(f"{n}={s}" for n, s in shards()))
        return 0
    if cmd == "check" and len(argv) > 2:
        return check(argv[2:])
    if cmd == "matrix" and len(argv) > 2:
        if check(argv[2:]):
            return 1
        parts = [{"name": "careful", "who": "careful", "only": ""}, {"name": "careless", "who": "careless", "only": ""}]
        parts += [{"name": n, "who": "sweep", "only": s} for n, s in shards()]
        print(json.dumps({"part": parts}, separators=(",", ":")))
        return 0
    sys.exit(__doc__)


if __name__ == "__main__":
    sys.exit(main(sys.argv))
