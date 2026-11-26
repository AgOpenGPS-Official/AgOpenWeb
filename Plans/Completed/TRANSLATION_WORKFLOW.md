# Translation Workflow — Options for Team Review

**Status:** Decision pending. Translation PRs are paused until we pick a direction (see `CONTRIBUTING.md`).

**Problem:** We want translators to contribute in one place and have their work flow to the right codebases without anyone hand-maintaining diverging `.resx` files. AgOpenGPS has used [Weblate](https://hosted.weblate.org/engage/agopengps/) for over a year. AgOpenWeb is currently accumulating translations via ad-hoc PRs (last one: #282, closed pending this decision).

## Current state

| Project | File location | Format | Key convention | Key count |
|---|---|---|---|---|
| AgOpenGPS | `AgOpenGPS.Core/Translations/gStr.*.resx` | .resx | `gs*` prefix (`gsABline`, `gsAckermann`) | 467 |
| AgOpenWeb | `Shared/AgOpenWeb.Views/Localization/Strings.*.resx` | .resx | Plain (`ABLine`, `Ackermann`) | 383 |

**Key overlap: 0.** Value overlap: **102 English strings** appear verbatim in both (≈27% of AgOpenWeb's strings).

Most existing AgOpenWeb locales are severely incomplete — 28 keys each out of 383, versus AgOpen's locales which are broadly complete.

## Goals

1. **One translator action → both projects benefit**, where possible.
2. **Zero hand-edited `.resx` files** — translation workflow is the only writer.
3. **Low friction to translators** — web UI, no git knowledge required.
4. **Reasonable implementation cost** — we're a small team.

## Options

### Option A — Rename AgOpenWeb keys to match AgOpen convention

Rewrite every AgOpenWeb key to use AgOpen's `gs*` convention (or some shared scheme). For the 102 strings where English text matches, keys converge and translators do them once. For the remaining 278 AgOpenWeb-only strings, we invent keys in the same convention.

**Effort:** ~380 string-key edits, plus every `{loc:Localize X}` binding across all AXAML files and any code references. Mechanical but large.

**Risk:** Localization keys are string-based — rename errors show up at runtime as raw key names, not compile errors. Every locale file (23 of them) edited in parallel. Needs automated tooling + validation.

**Translator experience:** Best. Weblate sees shared source strings across both projects; translator does each string once.

**Ongoing cost:** Must maintain the shared convention forever. Adding a new AgOpenWeb string means inventing a key that won't conflict with AgOpen's namespace.

**Auto-sharing:** ~100% of English-text-matching strings.

---

### Option B — Keep separate translation sources, rely on Translation Memory

AgOpen and AgOpenWeb stay as distinct translation targets. Weblate's built-in **Translation Memory** indexes every saved translation and auto-suggests matching entries whenever the same English source string appears anywhere else — **bidirectionally**. A translator clicks *Accept* instead of retyping.

There are two valid layouts; pick based on admin preference:

| Layout | TM behavior | Admin / branding |
|---|---|---|
| **Same project, separate components** | Within-project TM — automatic, no config | One project URL for both; shared maintainers, badges, stats |
| **Separate projects** | Cross-project TM on `hosted.weblate.org` is on by default for public projects (one shared pool across every public Weblate project on the instance, not just ours) | Independent settings/maintainers/access per project; each gets its own URL, badge, stats page |

TM flows both ways regardless of layout:

- AgOpenWeb translator approves a string AgOpen doesn't yet have → AgOpen translators get it as a suggestion next time they hit the same English source.
- AgOpen already has translations for 102 of our English strings → AgOpenWeb translators get them pre-suggested from day one.

**Effort:** Zero code changes. Create one Weblate target (component or project) pointing at `Shared/AgOpenWeb.Views/Localization/` with the standard `.resx` file filter.

**Risk:** Minimal. Existing `.resx` files keep their structure.

**Translator experience:** Decent. They navigate to AgOpenWeb's target; familiar strings auto-suggest from AgOpen's existing translations (and vice versa). No retyping for matching strings.

**Ongoing cost:** Zero beyond Weblate hosting.

**Caveat with separate projects on `hosted.weblate.org`:** the cross-project TM pool includes *all* public projects on the instance, not just AgOpen + AgOpenWeb. Expect occasional noisy suggestions from unrelated apps — translators can ignore them. Within a single project the TM pool is scoped cleanly to our two components only.

**Auto-sharing:** ~27% of strings get exact-match suggestions (the 102 English value matches). Fuzzy matches surface additional near-duplicates ranked by similarity. Translator still clicks *Accept* per string — TM is suggestion-only, not auto-apply, so a bad translation can't silently propagate.

---

### Option C — Partial key rename (match the 102 where possible)

Rename only the 102 AgOpenWeb keys whose English values match AgOpen. Those become genuinely shared-source; the remaining 278 AgOpenWeb-only keys stay on the current naming.

**Effort:** Medium — 102 key renames across resx + AXAML bindings. Smaller scope than Option A.

**Risk:** Same runtime-failure risk as Option A, in a smaller scope.

**Translator experience:** Mixed. 102 strings translate once across both projects; 278 need per-component work (same as Option B).

**Ongoing cost:** Annoying — two naming conventions in one repo, unclear which to use for new strings. Tends to drift back toward the old convention over time.

**Auto-sharing:** 27%, fully automatic (no *Accept* click).

## Recommendation

**Start with Option B.** It's the cheapest, lowest-risk path to a working Weblate setup. Translation Memory covers the 27% automatic-match case. If after a few months translators are complaining about the ~73% of AgOpenWeb-only strings that don't benefit from AgOpen's work, revisit Option A as a followup — at that point we'll know whether the ceiling is actually worth the churn.

**Option C is the worst of both worlds** — still has the rename risk and ongoing cost, without eliminating the duplicate effort on the non-matching strings.

**Hard block on all options:** someone needs to own the Weblate setup (create the component, bot token, badge, CONTRIBUTING.md update). Roughly a half-day of config work.

## Open questions

1. Do we want to make Weblate the *only* translation channel, or keep a PR escape hatch for languages Weblate doesn't support well? AgOpen's README suggests Weblate-only — seems fine.
2. What about existing AgOpenWeb locales with 28/383 keys? They stay as-is and fill in through Weblate over time; Weblate doesn't need them to be complete to start.
3. Any strings we should keep untranslated on principle (brand names, unit labels like "ft/ha")? Not urgent — can mark strings non-translatable in Weblate whenever.

## References

- [AgOpenGPS Weblate project](https://hosted.weblate.org/engage/agopengps/)
- [Weblate docs: Translation Memory](https://docs.weblate.org/en/latest/user/translating.html#translation-memory)
- [Weblate docs: `.resx` format support](https://docs.weblate.org/en/latest/formats.html#resourcedictionary-files)
- AgOpen README's `## Translation` section — model for our own README update once a choice is made
- Closed PR #282 — context for why this note exists
