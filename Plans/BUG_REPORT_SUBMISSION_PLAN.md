# In-App Bug Report Submission (Cloudflare Workers + R2)

## Context

The Bug Report dialog (main menu → Bug Report Dump) already builds the zip on the host
(`app.bugReport` in `RemoteServerWiring.Helpers.cs` → `DebugDumpService.CreateDump`, saved
under `Documents/BugReports/`). Getting it to us takes three more steps from the reporter:
**Download**, **Report on GitHub** (a prefilled issue form), then dragging the zip into the
form. Reports that arrive this way are good (#364), but every step loses reporters, and on a
tablet in a cab the drag-and-drop step is the hardest.

This is Phase 2 of `Plans/Completed/ONE_CLICK_BUG_REPORT_PLAN.md`, with one change: that plan
put a GitHub token in the app. Anything in the app can be pulled out of the APK, so the app
holds no secret. A small service in between receives the report, stores the zip and opens the
issue with its own credentials.

**Goal:** one button, **Send Report**, that delivers the description and the dump to the
maintainers and opens a GitHub issue, and that works when the tractor has no signal by
sending later.

## Why Cloudflare Workers + R2

- **Free at our volume.** Workers' free plan: 100,000 requests a day. R2: 10 GB stored,
  about 1 million writes and 10 million reads a month, no download (egress) fees. A dump is
  about 1 MB (the #364 dump was 865 KB), so 10 GB holds thousands of reports. R2 needs a
  payment card on the account even inside the free allowance.
- **One small thing to run.** A single Worker (JavaScript) with an R2 bucket bound to it, a
  daily cron trigger, and a free `*.workers.dev` address. No servers, no IAM.
- **Request bodies up to 100 MB** on the free plan, so the app uploads the zip straight to the
  Worker in one request; no pre-signed URLs. Waiting on the upload or on GitHub doesn't count
  towards the 10 ms CPU limit.

Check the limits on Cloudflare's pricing page before building; they're from mid-2026.

## Architecture

```
App host ──POST /v1/reports (zip + metadata)──▶ Worker ──put──▶ R2 bucket "agopenweb-bugreports"
   │                                              │
   │◀── { issue: 412, url } ──────────────────────┤──GitHub App──▶ issue #412 (label bug, auto-report)
                                                  │
Maintainer / triage Action ──GET /v1/dumps/<id>──▶ Worker (Cloudflare Access) ──get──▶ R2
Cron (daily) ─────────────────────────────────▶ Worker: prune storage (below)
```

The host sends the report, not the browser: the zip is on the host already, the host is the
one place that can keep an outbox across restarts, and the web client may be a phone on the
LAN that leaves.

## The Worker

Lives in this repo as `Tools/bugreport-worker/` (`wrangler.toml`, `src/index.js`, tests).
Deployed by hand with `wrangler deploy` at first; a GitHub Action with a Cloudflare API token
later if it changes often.

### `POST /v1/reports`

- Body: the zip (`Content-Type: application/zip`). Metadata in headers or a small JSON part:
  title, description, app version + git hash, platform, language, GitHub username.
- Checks, in order, all cheap:
  1. Rate limit per IP (Workers rate-limiting binding): e.g. 5 an hour, 20 a day.
  2. Size: reject over 50 MB (`Content-Length`, then the bytes actually read).
  3. It is a zip (`PK\x03\x04`) and its central directory lists `system_info.txt`, so the
     endpoint takes AgOpenWeb dumps and nothing else.
  4. A global daily cap (a counter in KV or a Durable Object; or simply count today's
     objects) so a flood can't fill the bucket or the issue list.
  5. Enough to act on (#407, "turn on" with no description and nobody to ask): a title of at
     least 15 characters, a description of at least 40, and a way to reply: a GitHub
     username that exists (`GET /users/<name>`; a GitHub outage doesn't block the report)
     or an email address in the dump's `contact.txt`. The email never goes in the
     metadata or the issue, only in the maintainer-only dump. The app checks the same
     before it enables Send.
- Stores the zip as `dumps/<yyyy>/<mm>/<id>.zip`, `id` a random 128-bit value, with custom
  metadata: issue number (filled after step below), version, platform, upload time.
- Opens the issue as a **GitHub App** (installed on this repo only, permission Issues: write):
  the Worker signs a JWT with the App's private key (WebCrypto, RS256), swaps it for an
  installation token, and creates the issue:
  - Title: the reporter's title, or "Bug report (untitled)".
  - Body: the description, version, platform, receiver/sentence type and a few lines from
    `system_info.txt`; `@username` if given (so the reporter gets GitHub notifications);
    the maintainer link to the dump.
  - Labels: `bug`, `auto-report` (so spam can be found and closed in bulk).
- Replies `{ issue, url }`. If GitHub fails, the zip stays stored and the Worker replies
  `{ id }` with a 202; the cron retries opening the issue.
- Secrets (`wrangler secret put`): the GitHub App id, installation id and private key.

### `GET /v1/dumps/<id>`

Maintainer-only: behind **Cloudflare Access** (free for up to 50 users; maintainers sign in
with GitHub or email). The triage Action (Phase 3 of the earlier plan) uses an Access
service token. Nothing else in the bucket is reachable from outside.

### Storage pruning (cron, daily)

R2's lifecycle rules can delete by age but not by bucket size, so the Worker's scheduled
handler does it:

1. List the bucket (paginated `list()`, 1,000 per page) and sum the object sizes. At a few
   thousand objects this is a handful of list calls, well inside the free allowance.
2. Below **9 GB**: stop.
3. At or above 9 GB: delete until the total is below **8 GB** (a 1 GB margin, so it doesn't
   run every day once full), oldest first, in this order:
   1. dumps whose issue is **closed** (looked up with the stored issue number; one GitHub
      search call per page of closed issues, not one per dump),
   2. then any dump by age.

   Dumps for open issues go last, because those are the ones still being worked on.
4. Comment once on each affected **open** issue: "The dump for this report was removed to
   free storage", so nobody chases a dead link.
5. Log what it deleted (Workers logs), and post a summary to a fixed tracking issue when it
   deleted anything, so we notice if we're near the limit every week.

Also an R2 lifecycle rule as a backstop: delete anything older than 365 days, whatever the
cron does.

Thresholds are Worker variables (`PRUNE_AT_BYTES`, `PRUNE_TO_BYTES`), so moving to a paid
plan is a config change.

## The app

### Dialog (`#bugreport` in `index.html` / `app.js`)

- New primary button **Send Report**, beside the existing ones. **Download** and
  **Report on GitHub** stay as the fallback (no internet on the device at all, or the
  service is down for good).
- Fields "GitHub username" and "or email"; one of the two is required (see check 5). A
  line says the email goes only to the maintainers, never into the public issue, and is
  deleted with the report. The description box prompts *What were you doing? What
  happened? What did you expect?*, and a button above the form opens the AgOpenGPS docs
  (https://docs.agopengps.com/) for questions that aren't bugs.
- **Screenshots or files** (#411 follow-up): after Create, *Add Screenshot or File* posts each
  picked file to the host (`POST /bugreports/attach?zip=&name=&client=`, the body is the file;
  `/bugreports/detach` removes one). The host adds it to the dump under `attachments/`
  (`BugReportAttachments`: at most 10 files, 20 MB each, 25 MB in all; names made safe and
  numbered when they repeat). Only the screen holding control may change a report (#397).
  Attachments are maintainer-only like the rest of the dump; the issue says only
  "Attachments: N (in the dump)". Android's WebView has no picker of its own, so the shell
  answers `onShowFileChooser` with the system one.
- A line saying what is sent, before the button: the description, settings (passwords
  already blanked by `DebugDumpService.RedactSecrets`), the open field with its location and
  boundary, the last 5 minutes of GPS positions and a screenshot. It goes to the AgOpenWeb
  maintainers; the issue shows only the description and version.
- Status after sending: "Sent as #412" with a link (via `openExternal`), or "Queued: will
  send when online".

### Host (`Shared/`)

- `BugReportSender` service (Services): POSTs the zip with `HttpClient`, timeout and retry.
- Outbox: `Documents/BugReports/outbox/` holds zips waiting to send, each with a small JSON
  sidecar (metadata, attempts). On a failure the report stays there; the sender retries
  with backoff (1 min, 5 min, 30 min, then hourly) and on app start. After success the zip
  moves back to `Documents/BugReports/` (the reporter keeps their copy) and the sidecar
  records the issue number.
- New command `app.bugReport.send|<file>` routed in `RemoteServerWiring.Helpers.cs`; status
  through the existing `BugReportStatus`.
- Endpoint URL as a constant (`https://agopenweb-bugreports.<account>.workers.dev/v1/reports`),
  overridable in settings for testing.
- All on every head, no platform code (cross-platform parity). Android already holds the
  INTERNET permission.

### Strings

New UI text in English with `tr()`, then `Tools/i18n-extract.py`; the labels go through the
length limits (`Tools/i18n-limits.py`) like every other button.

## Decisions (2026-10-20)

1. **Dumps are maintainer-only**, behind Cloudflare Access. Today reporters attach dumps to
   public issues themselves; with a one-tap send they make no deliberate choice about
   publishing their farm's location, so the issue carries only the description and version.
2. **A GitHub App** opens the issues, not a personal token: not tied to one person, and its
   tokens last an hour.
3. **Chris's personal Cloudflare account** for now. Moving to an organisation account later
   means redeploying the Worker there, copying the bucket and changing the app's endpoint
   constant (the outbox resends anything still queued).

## Phases

1. **Worker + R2 + GitHub App.** POST stores and opens issues; GET behind Access. Tested with
   `wrangler dev` (local R2) and `curl` with a real dump against a test repo, then this one.
2. **App: Send Report + outbox.** NUnit tests for the outbox (queued on failure, retried,
   moved on success) with a fake HTTP handler; checked in the headless app against
   `wrangler dev`.
3. **Pruning cron.** Unit tests for the delete order and the 9 → 8 GB thresholds (vitest
   with fake list results); one run against a bucket with the thresholds set low.
4. **Triage Action** (Phase 3 of the earlier plan): on `auto-report` issues, fetch the dump
   with the Access service token and post an analysis.
5. **Docs.** `.github/ISSUE_TEMPLATE/bug_report.yml` and the README point to Send Report
   first; the manual path stays documented.

## Verification

- Send from each head (Desktop, Android tablet, iPad): an issue opens with the right
  version and platform, the dump downloads through Access and matches the local copy.
- Airplane mode: the report queues, the dialog says so, and it sends when the network
  returns, including after an app restart.
- Abuse: the 6th report in an hour from one IP gets 429; a non-zip and a zip without
  `system_info.txt` get 400; nothing is stored for either.
- Pruning: with thresholds at 50 MB / 40 MB, closed-issue dumps go first, open-issue dumps
  last, and each affected open issue gets one comment.
