# NTRIP: main and fallback service, a sturdier client, and a report that explains itself

**Status:** approved 2026-10-29 (#463). Phase 1 built, in review; the other phases are not started.
**Prompted by:** #443 (NTRIP "not working": the reporter and the maintainers had to guess whether it was the login, the mount point or the position), #285 and #385 (silent stops and retries), and a rebuild of the NTRIP client in AgIO (AgOpenGPS) that was tested against recorded casters and a caster of our own. This plan brings what that rebuild learned to AgOpenWeb, in AgOpenWeb's own architecture.
**Related:** `Plans/RTCM_FORWARDING_PLAN.md` (the forwarding half, already in), `Plans/Completed/NTRIP_PROFILES_PLAN.md` (profiles per field), `Plans/BUG_REPORT_SUBMISSION_PLAN.md` (what a report carries), #278 (NTRIP on/off), #397 (observers), #444 (position sent to the caster).

## What happens today

`NtripClientService` is one connection to one caster. Profiles (`NtripProfile`) are one caster each, optionally tied to fields; a loaded field switches the connection to its profile, else to the default one.

- **One service, no way out.** If the caster is down or the login stops working during a job, corrections stop until someone notices and picks another profile by hand.
- **The status is one sentence.** `NtripStatus` is a free string ("Rejected: Mount point not found", "receive error: ..."). The panel cannot say *what to do*, and the web client cannot tell a wrong password from a caster that is down.
- **The failure is only in the log.** Why the last attempts failed, in which order, what the caster answered: the 5-second `[NTRIP]` line has the RTCM side (`Plans/RTCM_FORWARDING_PLAN.md`), but not the connection side. A report from a user who "has no corrections" cannot be told apart from a wrong password without asking.
- **Waits are implicit.** The wait for the caster's reply is bounded only by the 30 s stall watchdog; the retry state is a set of `Interlocked` flags (`_reconnectInProgress`, `_failureStreak`, `_generation`) around a `Teardown`.
- **Request and docs disagree.** `CLAUDE.md` and `Plans/ARCHITECTURE.md` say the request carries `Ntrip-Version: Ntrip/2.0`; `SendNtripRequestAsync` sends HTTP/1.1 with `Connection: keep-alive` and no version header. Casters that only serve NTRIP 2.0 to clients that ask for it see an NTRIP 1 client.
- **Test and the real connection disagree.** The editor's Test button already sends `Ntrip-Version: Ntrip/2.0` (`NtripConnectionTester.cs:38`); the client does not (`NtripClientService.cs:313`). A passing Test can be followed by a different result on the real connection. Running Test on the same session code removes the difference.
- **The TCP connect has no timeout of its own.** `tcp.ConnectAsync` (`NtripClientService.cs:175`) waits for the operating system's connect timeout.
- **A wrong login or mount point ends the client, so a threshold is never reached.** A `Rejected` reply goes to `Teardown` (`NtripClientService.cs:444`) and nothing retries; "after N failed attempts" would never fire for the failures that most need a fallback (decision 4).
- **Classification exists once already.** Replies are sorted into `Accepted`, `Rejected` and `RejectedRetry` (`NtripResponse.cs`). The outcome categories extend that enum; there is one classification, not two.
- **Profiles are for fields only.** Nothing lets a farm with two services (own base station plus a network service) say "use this one, and if it fails, that one".

What is **already good** and stays as it is: `RtcmFramer`, `ChunkedDecoder`, `RtcmQueue` (pacing, unicast to the GPS module), `RtcmStreamStats`, `NtripRtcmReport`, GGA from the last fix or the field origin (#444), the network-change wake-up (`INetworkWatcher`), the header cap (F2), the Network IO RTCM table. The rebuild's framer/queue were ported *from* these; nothing there is replaced.

## What the AgIO rebuild learned (and is offered back)

| Learned | Why it matters here |
|---|---|
| The connection is a small state machine with named outcomes (accepted, wrong login, mount point not found, caster busy, no network, not NTRIP, silent), not a string. | The panel can say what to do; retries give up on what retrying cannot fix and keep trying what it can. |
| Every wait has a timeout of its own (connect, reply, first data, data gap). | No state waits on the 30 s watchdog to notice. |
| NTRIP 2.0 first with a fall back to 1.0 on a caster that does not understand it, one time per round; `ICY 200 OK`, chunked bodies, a source table in place of an answer. | Fewer "works in AgIO / RTKLIB, not here". |
| A **main** service and a **fallback** service, a switch after N failed attempts or by hand, and **never back by itself**. | A job does not stop because one caster does; the operator decides when to return, because the switch can cost RTK. |
| The same login on the same caster is not tried twice at once (a second connection can push the first one off). | The fallback must not kick the main out when both are the same account. |
| The status carries *checks* (connected, logged in, mount point found, corrections arriving, base station, correction age) with a plain sentence for each. | A farmer reads "Login refused: check user name and password"; a maintainer reads the same checks in the report. |
| A **support report** text (no password, ever) assembled from the same status. | The Bug Report dump explains an NTRIP problem without a second round of questions. |

## Decisions

1. **Two exclusive modes, one switch.** `Connections.NtripMode`:
   - **Main + Fallback** (default). One profile is in use: the **default profile** (the existing `IsDefault`; "Use this profile" in the list sets it). A profile holds a **main** service and an optional **fallback** service. Loading a field does **nothing** to NTRIP. Profiles' field associations are ignored but kept.
   - **Profile per field** (the existing behaviour, unchanged). A loaded field uses its associated profile, else the default; closing a field returns to the default. A profile in this mode is its **main** service only; the fallback is not used, and the editor says so.

   Switching the mode deletes and rewrites nothing; switching back finds the profiles as they were. Per-field *with* fallback is out of scope (decision 4 below).
2. **The profile file grows, it does not change shape.** `NtripProfile` keeps `Id`, `Name`, `CasterHost/Port`, `MountPoint`, `Username`, `Password`, `AssociatedFields`, `AutoConnectOnFieldLoad`, `IsDefault`, `FilePath` (these are the **main** service, so every existing file loads as it is) and gains `Fallback` (null = none): `Enabled`, `CasterHost/Port`, `MountPoint`, `Username`, `Password`, `AutoSwitch` (default false), `SwitchAfterFailures` (default 2), plus `Version` (Auto/1.0/2.0) and `RawTcp` per service where the rebuild has them. Unknown fields in old files default; no migration code.
3. **The fallback is another NTRIP service.** A serial radio fallback (AgIO has one) does not exist here: AgOpenWeb has no serial path to the receiver, and a radio wired to the receiver already feeds it without the app.
4. **Switching rules.** Automatic: after `SwitchAfterFailures` failed attempts in a row on the main service (a failed attempt is any outcome that is not "corrections arrived"), if `AutoSwitch` is on. **An outcome that retrying cannot fix** (wrong login, mount point not found, access denied) **counts as having reached the threshold at once**: with Auto-switch on the switch happens on the first such reply, not after N attempts that will never come. Manual: a button at any time, in both directions. **Never back by itself:** while on the fallback the main service is probed in the background only if it is a different account on a different caster (decision 5), and a *"The main service is back. Switch back?"* prompt asks the operator. A switch raises one notice that says GPS may lose RTK and the position may shift a little. In manual mode (`AutoSwitch` off, the default) the client reports the failure with advice and waits for the operator.
5. **Same account, same caster: no second connection.** If main and fallback share caster and login, the main is not probed while the fallback is in use. This holds when the two use **different mount points of one subscription** (a common setup: a VRS mount point plus the nearest base): the main is never probed, so "The main service is back" never appears. That is intended, and the card says so while the fallback is in use: *"The main service is not tried in the background because it uses the same login. Switch back yourself when you want it."* with the **Switch to main** button next to it.
6. **The rebuilt connection layer replaces the connection half of `NtripClientService`, not the forwarding half.** `INtripClientService` keeps its members (`IsActive`, `IsConnected`, `ConnectAsync`, `DisconnectAsync`, `RtcmDestination`, `GetRtcmStreamSnapshot`, `TotalBytesReceived`, events) so the VM, `NtripFieldCloseTests` and the dump keep working; it gains `Status` (decision 7) and `SwitchTo(main|fallback)`. The framer, queue and stats are used as they are. The outcome categories extend `NtripReply` (`NtripResponse.cs`), so the client, the Test button and the status share one classification.
7. **One status object, read by everyone.** `NtripStatus` (immutable snapshot): state, `Using` (main/fallback), headline and advice (English, through `tr()` on the client), the checks, the last outcome category, attempts, seconds to the next retry, the last N events. The Network IO card, the status bar text (`State.Connections.NtripStatus`, kept for what reads it), the bug report and the log are all produced from it, so they cannot disagree.
8. **Reports never hold a password.** The support text names host, port and mount point (the maintainers need them) and the user name is blanked like `RedactSecrets` blanks `NtripUsername`; passwords are never written. A test feeds a password through every path (status, events, report, log lines) and fails if it appears.
9. **Strings.** Every sentence the user can read is English in `app.js` through `tr()` (host side: a code and arguments; the client composes the sentence), so `Tools/i18n-extract.py` and `Tools/i18n-limits.py` see it and Weblate can translate it. The rebuild's sentences are short on purpose; each must fit its label.
10. **Cross-platform parity and threading as the rules say.** All of it is in `Shared/`. The client runs on its own I/O tasks and posts to the host-loop dispatcher; it never touches the cycle worker or `*State` directly (`UpdateNtripConnectionProperties` stays the only writer).
11. **When to retry with NTRIP 1.0** (Version = Auto only; a service set to 1.0 or 2.0 is never second-guessed). The client goes back to the 1.0 request, once, only when the caster did not understand the 2.0 one:
    - the reply is not an NTRIP or HTTP reply at all (not `ICY`, `HTTP/x.y`, `SOURCETABLE` or `ERROR`), and
    - `HTTP 400 Bad Request` or `HTTP 505 Version Not Supported`.

    **Never** on `401`, `403` or `404`, on `ERROR - Bad Password`, or on a source table (that means a wrong mount point, and the answer would be the same in 1.0); never on a timeout or a close with no reply (some casters close on a bad login; that is a failure with its own category, and it is retried as such). The version that **delivered corrections** is remembered for that service (caster, port, mount point, in memory; forgotten when the profile is edited or the host restarts), so a reconnect starts with it and does not repeat the probe. Each case above, and each "never" case, has its own fake-caster test.

## Design

```
caster(s) ──► NtripSession (state machine, timeouts, retry policy)
                  │ whole bytes
                  ▼
        CorrectionSourceManager (main/fallback, switch rules, notices)
                  │                         │
                  ▼                         ▼
   existing: ChunkedDecoder → RtcmFramer → RtcmQueue → UDP 2233        NtripStatus ─► VM ─► Status frame ─► Network IO card
                                                                             │
                                                                             ├─► log "[NTRIP] ..." lines
                                                                             └─► ntrip_session.txt in the Bug Report dump
```

New files (`Shared/AgOpenWeb.Services/Ntrip/`, Apache header, `CultureInfo.InvariantCulture` throughout; `CA1305` is already an error for all production code): `NtripSession`, `NtripOutcome` (categories), `RetryPolicy`, `NtripRequest` (builds the request, Auto/1.0/2.0), `NtripHeaderScanner` (ICY/HTTP/source table), `CorrectionSourceManager`, `NtripStatus`, `CorrectionChecks`, `NtripSupportReport`. `NtripResponse` is extended, not replaced. `NtripConnectionTester` (the editor's Test button) runs the same session code for one attempt, so Test and the real thing cannot disagree.

Wire: `Contracts.cs` (`NtripStatusDto`, `NtripProfileDto.Fallback...`), `SceneProjector`, `WireCodec` + `transport.js` in the same order, `app.js` (card, editor, prompt). Commands: `ntrip.mode|main|field`, `ntrip.switch|main|fallback`, `ntrip.answer|yes|no` (the switch-back prompt, existing prompt frame), `ntrip.save` extended with the fallback fields. All need control (none are in `ObserverIds`); observers see the card and the prompt, change nothing. Passwords in the profiles frame stay masked as `ntrip.test` already treats them (#397).

Bug report: `ntrip_session.txt` next to `ntrip_rtcm.txt` (status, checks, the last events with their categories and timestamps, switch history, versions tried, what the caster answered on its first line, the mode and which profile is in use); one line in `system_info.txt` ("NTRIP: fallback in use since 10:42 (main: login refused)") so the issue body the Worker builds from it already says where to look. The Worker is not changed. `logs.txt` already carries `[NTRIP]` lines; the new ones keep the prefix and add the outcome category, never a URL with credentials.

## Phases (one PR each)

### Phase 0: this plan
Reviewed by the maintainers on a docs-only PR. Answers to the open questions go in this file.

### Phase 1: the connection layer, no visible change
- `NtripSession`, `RetryPolicy`, `NtripRequest`, `NtripHeaderScanner`, outcome categories; `NtripClientService` uses them; single service, same UI. `Ntrip-Version: Ntrip/2.0` is sent with the fall back to 1.0 (decision 2 below).
- The Test button runs the same session code, so it cannot pass where the real connection fails. Every wait gets its own timeout, the TCP connect included.
- `NtripStatus` filled and logged; `State.Connections.NtripStatus` fed from its headline.
- Tests (NUnit, `AgOpenWeb.Services.Tests`): a fake caster on 127.0.0.1 for each outcome and for each rule of decision 11 (accepted 2.0, accepted 1.0 after a 2.0 refusal, no 1.0 retry after 401, 404, a source table or a timeout, the remembered version, ICY, chunked, wrong login, wrong mount point with a source table, busy, silent, closes after 200, not RTCM), a fake clock for the retry policy and every timeout, `NtripHandshakeTests` and `NtripHeaderBufferCapTests` kept green.
- **Outcome:** same behaviour as today for a caster that works; a failure now has a name. The first thing in the PR is an audit table: each defect found in the rebuild against `NtripClientService` as it is, "still applies" or "already fine", so the maintainers see what is a change and what is not.
- **Built as** (differences from the sketch above, all to keep it smaller):
  - `NtripSession` is the connection and the retry loop in one; there is no separate `RetryPolicy` class. The backoff is the one the client already had (1, 2, 4, 8, 15 s), without jitter, and a wrong login or mount point still ends the session at once, so nothing waits on attempts that cannot help (see decision 4).
  - The status type is `NtripSessionStatus` (a record), so it is not confused with the `NtripStatus` string property of the state classes. The headline, light and checks come with the card in Phase 4.
  - `NtripReply` stays, derived from the new `NtripOutcome`, so `NtripResponse.Classify` is the one classification. Its messages keep the wording the client had.
  - The no-data limit is 30 s, as the stall watchdog it replaces; the connect limit is 10 s and the reply limit 15 s. `ConnectAsync` now returns when the session has started and no longer throws for an unreachable caster: the session reports it and retries.
  - The position goes in the NTRIP 2.0 request (`Ntrip-GGA`) or, for NTRIP 1.0, right after the answer, then on the interval; before, the first one went out 5 s after connecting.

### Phase 2: the report (straight after Phase 1)
Phases 1 and 2 together answer #443: a failure has a name and a maintainer can read it from the dump. They go to testers before any fallback UI exists.
- `ntrip_session.txt`, the `system_info.txt` line, `NtripSupportReport`; the redaction test (decision 8) over status, events, log lines, the dump and the profile frame.
- A dump made in each failure state of Phase 1 is read as a maintainer: the cause must be readable from `ntrip_session.txt` alone.

### Phase 3: profiles with a fallback, and the mode
- `NtripProfile.Fallback`, `Connections.NtripMode` (`config.set|conn.ntripMode`), `CorrectionSourceManager`, `SwitchTo`, the notices.
- `MainViewModel.Ntrip.cs`: `HandleNtripProfileForFieldAsync`, `RestoreDefaultNtripProfileAsync` and `ReconnectNtripIfConnectedAsync` act in per-field mode only; in main-and-fallback mode the default profile is connected at start and on edit.
- Tests: the existing `NtripFieldCloseTests` unchanged for per-field mode; new ones for the other mode (field load and close do nothing); switch after N failures; an outcome retrying cannot fix switches at once with Auto-switch on and not with it off; no auto-return; the same-account rule, including two mount points of one login (no probe, no "main is back"); mode change deletes nothing.

### Phase 4: the Network IO card and the editor
- Card: one headline and the checks, the service in use, **Switch to fallback / main**, a mode switch with one sentence on each mode, the switch-back prompt, the same-login sentence (decision 5), the notice. Editor: Main and Fallback tabs, *Test* per tab, the auto-switch toggle and count, the warning that the fallback is ignored in per-field mode. Touch sizes as the other Network IO controls.
- Verified in the headless app with the fake caster and in a browser (Playwright), once on each head's web view where it differs.

### Phase 5: docs
`CLAUDE.md` (the NTRIP sections), `Plans/ARCHITECTURE.md` (NTRIP data flow), `Docs/INTERFACE_GUIDE.md` (Network IO), this plan to `Plans/Completed/`, `RTCM_FORWARDING_PLAN.md` "Still to check" list unchanged.

## Verification

- **Unit and fake caster** as in Phases 1 and 3; time through `IClock`, no sleeps.
- **Headless** with `AGOPENWEB_DATA`: the fake caster stops answering, the card shows the advice, the fallback takes over after the count, the prompt appears when the main returns, a second browser (observer) sees both and can change neither.
- **A real caster** of ours for the happy path and a wrong password (the AgIO rebuild's recordings are local only and are not committed; fixtures here are written by hand or anonymised first).
- **Bench:** AiO v26 with an F9P: corrections keep reaching the board across a switch; the RTCM queue is cleared at a switch so an old caster's epoch is never sent after the new caster's.
- **Hot path:** the status snapshot is built when something changed, not per tick; `HotPathBudgetTests` stay green.
- **Field:** a tablet with Android and Windows on a Wi-Fi unit that loses its link for a minute: no stuck state, no retry storm (#385).

## Decisions taken with the proposer (2026-10-29)

These were open questions in the first draft; the maintainers can still overrule them in review.

1. **Existing installs get the new default.** Main + Fallback for everyone. Profiles that have field associations keep them (nothing is deleted); in Main + Fallback mode they are ignored. A user who relied on a profile per field switches the mode once in Network IO. The first start after the update shows one notice that says so.
2. **The request is NTRIP 2.0 first, with a fall back to 1.0** (`Ntrip-Version: Ntrip/2.0`, and a caster that does not understand it gets the 1.0 request once per round), as AgIO does. `CLAUDE.md` and `Plans/ARCHITECTURE.md` are corrected to describe that exactly, in Phase 5.
3. **Fallback is NTRIP only.** No serial or radio source.
4. **The two modes stay exclusive:** fallback off (Profile per field) or fallback on (Main + Fallback). Per-field with a fallback is not planned.
5. **Auto-switch is off by default.** The default is *report and let the operator choose*: when the main service fails the card says why and offers **Switch to fallback**; with Auto-switch on, the switch happens after `SwitchAfterFailures` (default 2) failed attempts. The "main is back, switch back?" prompt appears in both settings.
6. **Code origin** is not an issue for this plan; the new files carry the Apache header like all others.
7. **The recorder** (a local tool that recorded real caster sessions so tests could replay them) is not part of this plan. Fixtures here are written by hand or anonymised first.

## Out of scope

- Serial or USB connection to a receiver; a radio source.
- Caster selection from a source table beyond what Test already shows (the rebuild has a mount point picker; it can follow separately).
- Changes to the RTCM forwarding, the queue or the Worker.
- Changes to AgIO (AgOpenGPS): the rebuild there is its own pull request.
