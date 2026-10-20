# Bug report service (Cloudflare Workers + R2)

Receives bug reports sent from the app, stores the dump in R2 and opens the GitHub issue.
Design: `Plans/BUG_REPORT_SUBMISSION_PLAN.md`.

Two Workers share one R2 bucket:

| Worker | Config | Does |
|--------|--------|------|
| `agopenweb-bugreports` | `wrangler.toml` | `POST /v1/reports` from the app; daily cron: opens issues that failed earlier, prunes storage |
| `agopenweb-dumps` | `wrangler.dumps.toml` | `GET /v1/dumps/…` for maintainers, behind Cloudflare Access |

## Tests

```bash
npm install
npm test                 # node --test: zip checks, GitHub JWT, pruning, the Worker end to end
npx wrangler dev         # local run on :8787 with a local R2 bucket
```

## First-time setup

1. **Log in:** `npx wrangler login`.
2. **Bucket:** `npx wrangler r2 bucket create agopenweb-bugreports`, then the 365-day backstop:
   `npx wrangler r2 bucket lifecycle add agopenweb-bugreports expire-old dumps/ --expire-days 365`.
3. **GitHub App** (organisation settings → Developer settings → GitHub Apps → New):
   - Name `AgOpenWeb Bug Reports`, homepage the repo URL, **Webhook: off**.
   - Repository permissions: **Issues: Read and write** (Metadata: read is added automatically).
   - Where it can be installed: only this account. Create it, note the **App ID**, generate a
     **private key** (a `.pem` download), then **Install App** on `AgOpenWeb` only.
   - Create the `auto-report` label on the repo.
4. **Secrets:** `npx wrangler secret put GITHUB_APP_ID`, then
   `npx wrangler secret put GITHUB_APP_KEY < path/to/key.pem` (the PKCS#1 file GitHub
   gives is fine).
5. **Deploy:** `npm run deploy`. Put the dumps Worker's address in `DUMPS_URL` in
   `wrangler.toml` and deploy again.
6. **Access on the dumps Worker** (dashboard → Workers → `agopenweb-dumps` → Settings →
   Domains & Routes → workers.dev → enable Cloudflare Access). Edit the Access application's
   policy to allow the maintainers' emails. Copy the team name (`<team>.cloudflareaccess.com`)
   and the application's **AUD** tag into `ACCESS_TEAM` and `ACCESS_AUD` in
   `wrangler.dumps.toml`, and deploy. Until both are set the dumps Worker refuses everything.
7. **Optional:** a tracking issue for pruning summaries; put its number in `TRACKING_ISSUE`.

## Limits (vars in `wrangler.toml`)

- `MAX_BYTES` 50 MB per dump; `DAILY_CAP` 100 reports a day in all; `PER_IP_DAILY` 10 per
  connection a day; the `RATE_LIMIT` binding allows 2 a minute per connection.
- `PRUNE_AT_BYTES` 9 GB / `PRUNE_TO_BYTES` 8 GB: at 9 GB the cron deletes down to below
  8 GB, dumps of closed issues first, then the rest oldest first; an open issue whose dump
  goes gets a comment saying so.
