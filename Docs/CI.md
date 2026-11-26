# CI and releases


GitHub Actions workflows, split by purpose:

- **`build-and-release.yml`** ("CI") — on every push / PR to `main` or `develop`: runs the test suite plus a
  compile-check of each platform head (Desktop, Android, iOS). No packaging, no releases.
- **`build-deploy-bundles.yml`** — the single packaging + release publisher. Runs the
  `deploy/{linux,windows,macos}/package.sh` scripts + builds the signed Android APK on clean runners:
  - on a **`v*` tag** (or manual dispatch with a tag) → publishes one complete Release, marked
    prerelease so it never shows as "Latest" (the beta is the build users run), with every
    artifact: Linux daemon (x64/arm64) + desktop launcher (x64/arm64) tarballs, Windows zip
    (launcher + service installer), macOS `.dmg`, and the Android APK; its notes end with every
    PR merged since the previous version tag (GitHub's generated notes, `.github/release.yml`);
  - on a **daily schedule** → refreshes a rolling `beta` prerelease with the same artifacts, only
    when `develop` has moved on since the beta was built (otherwise the run builds nothing);
  - on a manual dispatch, by its `publish` input: `beta` (default) refreshes the rolling
    beta from `develop` right away (title carries build time + commit); `release` publishes
    the Release named by `release_tag`; `none` builds + uploads artifacts only (dry run).

- **Scenario harnesses, nightly** — eleven workflows, each running its harness on `develop` (when it has a
  commit from the last 25 hours) or by hand, keeping its findings report as an artifact and in the run's
  summary; PR CI only builds them. GitHub's own schedule started them 5–8 hours late every day, so the bug
  report Worker starts them (`Tools/bugreport-worker`, `NIGHTLY_CRON` 05:07 UTC, 23:07 US Central, `NIGHTLY_WORKFLOWS`
  in its `wrangler.toml`: a change there needs `wrangler deploy`) as a `workflow_dispatch` with `nightly=true`, which keeps the 25-hour check;
  side by side, each on its own runner: `field-scenarios.yml`, `steer-wizard-scenarios.yml` (also fails when its safety watch saw the
  wheels move when they shouldn't), `vehicle-tool-scenarios.yml`, `machine-module-scenarios.yml`,
  `gps-ntrip-scenarios.yml`, `job-scenarios.yml` (the five job harnesses, up to about 80 min, one combined report), `isobus-scenarios.yml`,
  `web-client-scenarios.yml`, `update-scenarios.yml`, `working-day-scenarios.yml`, `dump-replay-scenarios.yml`.
  The slow ones that rarely change (the Vehicle & Tool settings sweep, working day, update, dump replay) run only when a
  commit of the last 25 hours touched what they cover, and every Sunday whatever changed (`.github/scripts/nightly-fresh.sh`,
  the paths in each workflow; add a path there when a harness starts covering more). Each harness's `README.md` says what it covers.

To cut a release (about weekly, so each one stays small): bump `sys/version.h` and the three heads' display versions, then push a tag — `git tag v26.7.x && git push origin v26.7.x`. A summary on top of the generated list goes in with `gh release edit`.
