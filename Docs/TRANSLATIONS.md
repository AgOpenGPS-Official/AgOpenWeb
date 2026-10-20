# Translations

The web UI is written in English and translated in the browser. Translators work on
Weblate; nobody edits the language files by hand.

## Files

| File | What it is |
|------|------------|
| `Shared/AgOpenWeb.RemoteServer/wwwroot/i18n/en.json` | The English source strings. Generated — do not edit. |
| `Shared/AgOpenWeb.RemoteServer/wwwroot/i18n/<code>.json` | One language (`de.json`, `pt-BR.json`, `zh-Hans.json`). Written by Weblate. |
| `Shared/AgOpenWeb.RemoteServer/wwwroot/i18n.js` | The loader. |
| `Tools/i18n-extract.py` | Regenerates `en.json` from `index.html` and `app.js`. |
| `Tools/i18n-limits.py` | Length limits for labels (Weblate) and the CI check that translations fit. |
| `Tools/i18n-limits.json` | Each label's limit in characters, and the screen that sets it. |
| `Tools/i18n-fit-baseline.json` | Translations known not to fit, waiting for the translator. |

**The key of every string is its English text.** `en.json` maps each string to itself;
a language file maps the same keys to translations. A missing or empty entry shows
English.

A language appears under *App Settings → Language* once its file exists. Choosing it
switches the UI at once, with no reload.

## Adding or changing UI text

Text in `index.html` needs nothing: write it in English. This covers element text and
the `title`, `placeholder` and `aria-label` attributes.

Text in `app.js`:

```js
el.textContent = tr('Save');                         // a plain string
showToast(tr('Select a track first'));
el.textContent = tr('Pass {n}', { n: pass });        // a string with values
el.textContent = size + ' · ' + tr('all', {});       // joined into a longer string
ctx.fillText(I18n.text(tr('Heading')), x, y);        // drawn on a canvas
```

- Use `{name}` placeholders, not `+`, so translators can reorder the sentence.
- Text inside HTML that the script builds (`'<div>Save</div>'`) is picked up without `tr()`.
- `tr('Save')` returns the English string; it is translated when it reaches the page.
  Program logic therefore always sees English.
- Put `translate="no"` on an element whose content must not be translated (a list of
  field names, the log viewer).

Then regenerate the source file and commit it:

```bash
Tools/i18n-extract.py
```

CI runs `Tools/i18n-extract.py --check` and fails if `en.json` is out of date.

Changing the wording of an English string creates a new key, so its translations start
again (Weblate's translation memory will suggest the old ones).

## Translate the meaning

Use the word your language has for what the control does, not a word-for-word copy of
the English. The English names stay as AgOpenGPS users know them, even where they say
little on their own ("Svenn Arrow", "Offset Fix"); a translation is free to say what they
do instead: German has "Richtungspfeil" and "Feld verschieben". Write questions about the
English as a comment on the string in Weblate, not in the translation.

## Length: translations fit the English layout

The layout is made for the English text and doesn't change for a language: a button
doesn't wrap and a row doesn't push its neighbours aside because a translation is longer.
A translation that doesn't fit has to be shortened (a shorter word or an abbreviation).

- **Weblate shows a limit.** Each label and button has a `max-length` flag, so
  translators see "at most N characters" and get a warning when they go over. Find the
  ones over their limit with the search `check:max-length`. The check warns only; it isn't
  enforced, so an approved translation over its limit still reaches the app.
- **CI measures the pixels.** The `translations-fit` job opens every screen in English
  and puts each translation in place of its label: the text must stay on one line in its
  box, and nothing else may move, shrink or leave the screen. Characters only approximate
  width, so this is the check that counts. A translation that fails and isn't in
  `Tools/i18n-fit-baseline.json` fails the job, naming the screen and the limit.
- **The baseline** lists translations approved before the limits existed. When the
  translator shortens one, CI warns that it fits now: take it out of the file.

Help text and other sentences (more than one line, or longer than 40 characters) have no
limit; their boxes grow. Neither does text the check can't reach (toasts, list rows,
later wizard steps).

After adding or moving labels, measure the limits again and send them to Weblate (the
app running headless, as for any check in the running app):

```bash
Tools/i18n-limits.py            # rewrites Tools/i18n-limits.json
Tools/i18n-limits.py --push     # max-length flags on Weblate (needs a Weblate token)
Tools/i18n-limits.py --check    # the CI check, locally
```

## Not translated yet

- Messages composed by the host (C#): status and error text, wizard step titles, fix
  quality names. They pass through the same lookup, so they will translate once their
  English text is added to `en.json`.
- Unit symbols (`cm`, `km/h`, `ha`).

## Weblate setup (maintainers)

Add a component to the AgOpenGPS project:

| Setting | Value |
|---------|-------|
| Repository | this repository, branch `develop` |
| File format | JSON file |
| File mask | `Shared/AgOpenWeb.RemoteServer/wwwroot/i18n/*.json` |
| Monolingual base language file | `Shared/AgOpenWeb.RemoteServer/wwwroot/i18n/en.json` |
| Edit base file | off (it is generated) |
| Language code style | BCP style (`pt-BR`, `zh-Hans`) |
| Licence | Apache-2.0 |

Have Weblate send its changes as pull requests, and add the Weblate webhook to this
repository so new English strings reach translators on every push.
