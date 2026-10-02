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
| Licence | GPL-3.0 |

Have Weblate send its changes as pull requests, and add the Weblate webhook to this
repository so new English strings reach translators on every push.
