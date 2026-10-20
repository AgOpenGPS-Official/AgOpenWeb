#!/usr/bin/env python3
# AgOpenWeb
# Copyright (C) 2024-2026 AgOpenWeb Contributors
# SPDX-License-Identifier: Apache-2.0
"""Keep translated labels inside the English layout: character limits for Weblate, and a
pixel check for CI.

Opens every panel, tab, dialog and flyout of the running web client in English. For each
text that is a key in i18n/en.json and shows as a label (one line, at most 40
characters), it puts other text in its place and sees whether that still fits: the label
stays on one line inside its box, and nothing else on the screen moves up or down,
changes height, is squeezed narrower or pushed off the screen (sliding sideways is fine,
as is a panel growing within the screen). Sentences (several lines, or longer than 40
characters) are left out: their boxes grow. Text the sweep can't reach (toasts, list
rows, later wizard steps) is left out too.

  (default)   measure: sample text of growing length gives each label's room in
              characters, never less than the English; written to Tools/i18n-limits.json
  --push      give each English source string on Weblate the flag max-length:<limit> from
              that file (and take it off strings without one). The check warns translators;
              it blocks only if added to the component's enforced checks.
              Token: $WEBLATE_TOKEN or ~/.weblate-token.
  --check     every language with translations: each label's translation must fit, in
              pixels. Translations listed in Tools/i18n-fit-baseline.json (known, waiting
              for the translator) are reported but don't fail; a listed one that fits now
              gets a warning to take it out. CI runs this with Roboto as the system font
              (the Android tablets'), so make the baseline there or in the same setup
              (see the translations-fit job): --write-baseline rewrites it.

Characters only approximate width, so a translation within its limit can still fail
--check; the limits were measured with macOS's wider system font, which keeps that rare.

Needs the app running (headless, a scratch data dir) and Playwright:
  AGOPENWEB_DATA=/tmp/aow Platforms/AgOpenWeb.Desktop/bin/Debug/net10.0/AgOpenWeb.Desktop --headless
  pip install playwright && playwright install chromium

Usage:
  Tools/i18n-limits.py [--url http://localhost:5174/] [--size 1280x800] [--out FILE]
  Tools/i18n-limits.py --push [FILE]
  Tools/i18n-limits.py --check | --write-baseline
"""
import argparse
import json
import os
import re
import sys
import time
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
I18N = ROOT / "Shared/AgOpenWeb.RemoteServer/wwwroot/i18n"
LIMITS = ROOT / "Tools/i18n-limits.json"            # what --push sends to Weblate
BASELINE = ROOT / "Tools/i18n-fit-baseline.json"   # translations known not to fit
WEBLATE = "https://hosted.weblate.org/api/"
SOURCE_UNITS = WEBLATE + "translations/agopengps/agopenweb/en/units/"
LABEL_MAX = 40      # longer English text is a sentence
MARGIN = 0          # characters kept spare: none, the sample measures about as wide as German
SAMPLE = ("Lorem ipsum dolor sit amet consectetur adipiscing elit sed do eiusmod tempor "
          "incididunt ut labore et dolore magna aliqua Ut enim ad minim veniam quis nostrud "
          "exercitation ullamco laboris nisi ut aliquip ex ea commodo consequat")

# Every screen to measure: [name, js that shows it, root selector or null].
SURFACES_JS = r"""
() => {
  const out = [['main screen', 'lnCloseAll(); closeDialog();', null]];
  for (const p of LN_NAV_PANELS) {
    const open = `closeDialog(); lnCloseAll(); document.getElementById('${p}').classList.add('open');`;
    out.push([p, open, '#' + p]);
    document.querySelectorAll(`#${p} .cfg-tab`).forEach((b, i) => out.push([`${p} / tab ${i + 1}`,
      open + `document.querySelectorAll('#${p} .cfg-tab')[${i}].dispatchEvent(new PointerEvent('pointerdown', {bubbles: true}));`,
      '#' + p]));
  }
  for (const c of document.querySelectorAll('#dialoghost .dlg-card[id]'))
    out.push([c.id, `lnCloseAll(); openDialog('${c.id}');`, '#' + c.id]);
  out.push(['steer wizard', 'lnCloseAll(); closeDialog(); openSteerWizard();', '#wizard-overlay']);
  for (const f of document.querySelectorAll('.bn-flyout[id]'))
    out.push([f.id, `lnCloseAll(); closeDialog(); document.getElementById('${f.id}').classList.add('open');`, '#' + f.id]);
  return out;
}"""

MEASURE_JS = r"""
([rootSel, keys, sample, labelMax, trans]) => {
  const root = rootSel ? document.querySelector(rootSel) : document.body;
  const keySet = new Set(keys);
  const norm = s => s.replace(/\s+/g, ' ').trim();
  const visible = el => { for (let e = el; e; e = e.parentElement) { const cs = getComputedStyle(e);
      if (cs.display === 'none' || cs.visibility === 'hidden') return false; }
    const r = el.getBoundingClientRect(); return r.width > 0 && r.height > 0; };
  const lines = node => { const r = document.createRange(); r.selectNodeContents(node); const tops = new Set();
    let right = -Infinity; for (const q of r.getClientRects()) if (q.width > 0) { tops.add(Math.round(q.top / 4)); right = Math.max(right, q.right); }
    return { n: tops.size, right }; };
  const all = [...root.querySelectorAll('*')].filter(e => e.getBoundingClientRect().width > 0);
  const out = [];
  const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
  const nodes = []; for (let n = walker.nextNode(); n; n = walker.nextNode()) nodes.push(n);
  for (const node of nodes) {
    const el = node.parentElement;
    if (!el || el.closest('[translate="no"]') || ['SCRIPT', 'STYLE', 'OPTION', 'TEXTAREA'].includes(el.tagName)) continue;
    const src = node.nodeValue;
    const tail = src.match(/\s*\(?\s*$/)[0];
    const key = norm(src.slice(0, src.length - tail.length));
    if (!keySet.has(key) || !visible(el)) continue;
    const before = lines(node);
    if (before.n !== 1 || key.length > labelMax) { out.push({ key, label: false }); continue; }
    let box = el; while (getComputedStyle(box).display === 'inline') box = box.parentElement;
    // Everything but the label itself; its own containers may resize sideways with it, and
    // empty spacers may shrink, but nothing may move up or down or change height.
    const others = all.filter(e => e !== el && !el.contains(e));
    const sideways = others.map(e => e.contains(el) || (!e.textContent.trim() && !e.querySelector('img, input, select, canvas')));
    const snap = others.map(e => e.getBoundingClientRect());
    const lead = src.match(/^\s*/)[0];
    const fitsText = text => {
      node.nodeValue = lead + text + tail;
      const l = lines(node), b = box.getBoundingClientRect(), cs = getComputedStyle(box);
      if (l.n > 1 || l.right > b.right - parseFloat(cs.paddingRight) - parseFloat(cs.borderRightWidth) + 1) return false;
      if (box.scrollWidth > box.clientWidth + 1 && cs.overflowX !== 'visible') return false;
      for (let i = 0; i < others.length; i++) { const r = others[i].getBoundingClientRect(), s = snap[i];
        if (Math.abs(r.top - s.top) > 0.5 || Math.abs(r.height - s.height) > 0.5 || (!sideways[i] && r.width < s.width - 0.5)
            || (r.right > innerWidth + 0.5 && r.right > s.right + 0.5)) return false; }
      return true;
    };
    const where = el.id ? '#' + el.id : el.tagName.toLowerCase() + (el.classList[0] ? '.' + el.classList[0] : '');
    if (trans) {     // --check: does the translation itself fit?
      if (trans[key]) out.push({ key, label: true, fits: fitsText(trans[key]), where });
      node.nodeValue = src;
      continue;
    }
    let lo = 1, hi = sample.length;
    while (lo < hi) { const mid = (lo + hi + 1) >> 1; if (fitsText(sample.slice(0, mid))) lo = mid; else hi = mid - 1; }
    node.nodeValue = src;
    out.push({ key, label: true, fit: lo, english: key.length, where });
  }
  return out;
}"""


def weblate(url, token, data=None):
    req = urllib.request.Request(url, method="PATCH" if data is not None else "GET",
                                 data=json.dumps(data).encode() if data is not None else None,
                                 headers={"Authorization": "Token " + token,
                                          "Content-Type": "application/json"})
    with urllib.request.urlopen(req) as r:
        return json.load(r)


def push(path):
    token = os.environ.get("WEBLATE_TOKEN") or Path("~/.weblate-token").expanduser().read_text().strip()
    limits = json.loads(Path(path).read_text(encoding="utf-8"))
    units, url = [], SOURCE_UNITS
    while url:
        page = weblate(url, token)
        units += page["results"]
        url = page["next"]
    changed = 0
    for u in units:
        flags = [f for f in re.split(r"\s*,\s*", u["extra_flags"]) if f and not f.startswith("max-length:")]
        if u["context"] in limits:
            flags.append(f"max-length:{limits[u['context']]['limit']}")
        new = ", ".join(flags)
        if new != u["extra_flags"]:
            weblate(u["url"], token, {"extra_flags": new})
            changed += 1
            time.sleep(0.2)
    missing = [k for k in limits if k not in {u["context"] for u in units}]
    print(f"{len(units)} source strings on Weblate, {changed} updated"
          + (f"; not on Weblate yet: {missing}" if missing else ""))
    return 0


def sweep(url, size, keys, trans=None):
    """Every measurement from every screen, as (screen name, result)."""
    from playwright.sync_api import sync_playwright
    w, h = (int(v) for v in size.split("x"))
    with sync_playwright() as pw:
        browser = pw.chromium.launch()
        page = browser.new_page(viewport={"width": w, "height": h})
        page.goto(url)
        page.wait_for_function("typeof iHoldControl!=='undefined' && iHoldControl===true", timeout=30000)
        page.evaluate("transport.send('app.setLanguage|en')")
        page.wait_for_function("I18n.language === 'en'")
        page.wait_for_timeout(1000)
        for name, js, root in page.evaluate(SURFACES_JS):
            page.evaluate(js)
            page.wait_for_timeout(300)
            for r in page.evaluate(MEASURE_JS, [root, keys, SAMPLE, LABEL_MAX, trans]):
                yield name, r
        page.evaluate("lnCloseAll(); closeDialog();")
        browser.close()


def measure(a, keys):
    found = {}   # key -> {"limit", "fit", "places"} or {"label": False}
    for name, r in sweep(a.url, a.size, keys):
        cur = found.get(r["key"])
        if not r["label"]:
            found[r["key"]] = {"label": False}      # a sentence anywhere: no limit
            continue
        if cur is not None and not cur.get("label", True):
            continue
        limit = max(r["fit"] - MARGIN, r["english"])
        place = f"{name} {r['where']}"
        if cur is None or limit < cur["limit"]:   # the tightest place comes first
            found[r["key"]] = {"limit": limit, "fit": r["fit"], "places": [place] + (cur or {}).get("places", [])}
        else:
            cur["places"].append(place)

    limits = {k: v for k, v in found.items() if v.get("label", True)}
    Path(a.out).write_text(json.dumps(limits, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")

    sentences = sum(1 for v in found.values() if not v.get("label", True))
    print(f"en.json keys: {len(keys)}  reached on screen: {len(found)}  "
          f"labels with a limit: {len(limits)}  sentences (no limit): {sentences}  "
          f"not reached: {len(keys) - len(found)}")
    tight = sum(1 for k, v in limits.items() if v["limit"] == len(k))
    print(f"labels whose limit is the English length (no room to spare): {tight}")
    for f in sorted(I18N.glob("*.json")):
        if f.stem == "en":
            continue
        tr = {k: v for k, v in json.loads(f.read_text(encoding="utf-8")).items() if v}
        over = [k for k, v in limits.items() if k in tr and len(tr[k]) > v["limit"]]
        if tr:
            print(f"  {f.stem}: {len(tr)} translated, {sum(1 for k in limits if k in tr)} with a limit, {len(over)} over it")
    return 0


def check(a, keys):
    """Each language's labels in the English layout: a translation that doesn't fit fails,
    unless it is in the baseline (known, waiting for the translator)."""
    limits = json.loads(LIMITS.read_text(encoding="utf-8")) if LIMITS.exists() else {}
    baseline = json.loads(BASELINE.read_text(encoding="utf-8")) if BASELINE.exists() else {}
    annotate = os.environ.get("GITHUB_ACTIONS") == "true"
    new_baseline, bad = {}, 0
    for f in sorted(I18N.glob("*.json")):
        trans = {k: v for k, v in json.loads(f.read_text(encoding="utf-8")).items() if v}
        if f.stem == "en" or not trans:
            continue
        lang = f.stem
        checked, failed = set(), {}
        for name, r in sweep(a.url, a.size, keys, trans):
            if not r["label"]:
                continue        # a sentence: its box grows
            checked.add(r["key"])
            if not r["fits"]:
                failed.setdefault(r["key"], []).append(f"{name} {r['where']}")
        known = set(baseline.get(lang, []))
        new = sorted(set(failed) - known)
        fixed = sorted(k for k in known & checked if k not in failed)
        print(f"{lang}: {len(checked)} labels checked, {len(failed)} don't fit "
              f"({len(failed) - len(new)} known, {len(new)} new), {len(fixed)} known now fit")
        for k in new:
            lim = limits.get(k, {}).get("limit")
            msg = (f"{lang}: \"{trans[k]}\" (for \"{k}\") doesn't fit on {failed[k][0]}"
                   + (f"; at most {lim} characters" if lim else ""))
            print(("::error::" if annotate else "  NEW ") + msg)
        for k in fixed:
            msg = f"{lang}: \"{k}\" fits now; take it out of {BASELINE.relative_to(ROOT)}"
            print(("::warning::" if annotate else "  FIXED ") + msg)
        bad += len(new)
        if failed:
            new_baseline[lang] = sorted(failed)
    if a.write_baseline:
        BASELINE.write_text(json.dumps(new_baseline, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
        print(f"wrote {BASELINE.relative_to(ROOT)}")
        return 0
    if bad:
        print(f"\n{bad} translation(s) change the layout. Shorten them on Weblate (Docs/TRANSLATIONS.md).")
        return 1
    return 0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--url", default="http://localhost:5174/")
    ap.add_argument("--size", default="1280x800")
    ap.add_argument("--out", default=str(LIMITS))
    ap.add_argument("--push", metavar="LIMITS_JSON", nargs="?", const=str(LIMITS))
    ap.add_argument("--check", action="store_true")
    ap.add_argument("--write-baseline", action="store_true")
    a = ap.parse_args()
    if a.push:
        return push(a.push)
    keys = list(json.loads((I18N / "en.json").read_text(encoding="utf-8")))
    return check(a, keys) if a.check or a.write_baseline else measure(a, keys)


if __name__ == "__main__":
    sys.exit(main())
