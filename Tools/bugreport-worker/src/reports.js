// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

// The public half of the bug report service (Plans/BUG_REPORT_SUBMISSION_PLAN.md):
// POST /v1/reports takes a dump from the app, stores it in R2 and opens the GitHub issue.
// The daily cron opens issues that failed earlier and prunes storage. Dumps are read back
// only through the dumps Worker, behind Cloudflare Access.

import { listEntries, readEntry, publicSystemInfo } from './zip.js';
import { createIssue, comment, closedReportIssues, userExists } from './github.js';
import { planPrune, issueOf } from './prune.js';

const json = (status, body) => new Response(JSON.stringify(body), {
  status, headers: { 'Content-Type': 'application/json' },
});
const int = (v, d) => (Number.isFinite(Number(v)) && v !== '' && v != null ? Number(v) : d);
const clip = (s, n) => (typeof s === 'string' ? s.slice(0, n) : '');

async function ipHash(env, ip) {
  const d = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(ip + '|' + env.GITHUB_APP_ID));
  return [...new Uint8Array(d).slice(0, 8)].map(b => b.toString(16).padStart(2, '0')).join('');
}

function newKey(now) {
  const r = crypto.getRandomValues(new Uint8Array(16));
  const hex = [...r].map(b => b.toString(16).padStart(2, '0')).join('');
  const day = now.toISOString().slice(0, 10).replace(/-/g, '/');
  return `dumps/${day}/${hex}.zip`;
}

async function listAll(bucket, prefix) {
  const out = [];
  let cursor;
  do {
    const page = await bucket.list({ prefix, cursor, include: ['customMetadata'] });
    out.push(...page.objects);
    cursor = page.truncated ? page.cursor : undefined;
  } while (cursor);
  return out;
}

// What a report must carry before it becomes an issue (the app's Send button checks the
// same): enough words to act on, and a way to reach the reporter. The contact is a GitHub
// username (mentioned on the issue, so replies notify them) or an email address, which the
// app writes into the dump as contact.txt and so stays with the maintainers.
export const MIN_TITLE = 15, MIN_DESCRIPTION = 40;
const EMAIL = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;
const GITHUB_USER = /^[A-Za-z0-9](?:[A-Za-z0-9]|-(?=[A-Za-z0-9])){0,38}$/;
const chars = s => [...s].length;

/** Report metadata as the app sends it, cut to sizes GitHub and R2 accept. */
export function cleanMeta(raw) {
  let m;
  try { m = JSON.parse(raw || '{}'); } catch { return null; }
  if (!m || typeof m !== 'object') return null;
  const github = clip(m.github, 100).trim().replace(/^@/, '');
  return {
    title: clip(m.title, 200).replace(/\s+/g, ' ').trim(),
    description: clip(m.description, 20000).trim(),
    version: clip(m.version, 100),
    platform: clip(m.platform, 50),
    language: clip(m.language, 20),
    github: GITHUB_USER.test(github) ? github : '',
    githubInvalid: github !== '' && !GITHUB_USER.test(github),
  };
}

/** Why a report can't be taken as it is, or null. contact = the dump's contact.txt text. */
export function whatsMissing(meta, contact) {
  if (chars(meta.title) < MIN_TITLE) return `Title too short: at least ${MIN_TITLE} characters.`;
  if (chars(meta.description) < MIN_DESCRIPTION)
    return `Description too short: at least ${MIN_DESCRIPTION} characters.`;
  if (meta.githubInvalid) return 'Not a GitHub username.';
  const email = (contact || '').split(/\r?\n/).some(l => EMAIL.test(l.replace(/^\uFEFF/, '').replace(/^Email:\s*/i, '').trim()));
  if (!meta.github && !email) return 'Add a GitHub username or an email address so we can reply.';
  return null;
}

export function issueFor(env, meta, sysInfo, key, attachments = 0) {
  const link = `${env.DUMPS_URL}/v1/${key}`;
  const facts = [
    ...sysInfo,
    meta.platform ? `Platform: ${meta.platform}` : null,
    meta.language ? `Language: ${meta.language}` : null,
    // The count only: names and contents stay in the maintainer-only dump.
    attachments ? `Attachments: ${attachments} (in the dump)` : null,
    `Dump: ${link} (maintainers only)`,
  ].filter(Boolean);
  const lines = [
    meta.description || '*No description given.*',
    '',
    '---',
    '',
    ...facts.map(l => '- ' + l),
    '',
    meta.github ? `Sent from the app by @${meta.github}.` : 'Sent from the app.',
  ];
  return {
    title: meta.title || 'Bug report (untitled)',
    body: lines.join('\n'),
    labels: ['bug', 'auto-report'],
  };
}

async function report(request, env) {
  const ip = request.headers.get('CF-Connecting-IP') || 'unknown';
  if (env.RATE_LIMIT) {
    const { success } = await env.RATE_LIMIT.limit({ key: ip });
    if (!success) return json(429, { error: 'Too many reports; try again in a minute.' });
  }
  const maxBytes = int(env.MAX_BYTES, 50 * 1024 * 1024);
  if (int(request.headers.get('Content-Length'), 0) > maxBytes + 1024 * 1024)
    return json(413, { error: 'Report too large.' });

  let form;
  try { form = await request.formData(); } catch { return json(400, { error: 'Expected multipart form data.' }); }
  const meta = cleanMeta(form.get('meta'));
  const file = form.get('dump');
  if (!meta || !file || typeof file === 'string') return json(400, { error: 'Missing meta or dump.' });
  if (file.size > maxBytes) return json(413, { error: 'Report too large.' });

  const bytes = new Uint8Array(await file.arrayBuffer());
  const entries = listEntries(bytes);
  const sys = entries?.find(e => e.name === 'system_info.txt');
  if (!sys) return json(400, { error: 'Not an AgOpenWeb bug report.' });
  const sysBytes = await readEntry(bytes, sys, 64 * 1024);
  const sysInfo = sysBytes ? publicSystemInfo(new TextDecoder().decode(sysBytes)) : [];
  const contactEntry = entries.find(e => e.name === 'contact.txt');
  const contactBytes = contactEntry ? await readEntry(bytes, contactEntry, 4 * 1024) : null;
  const missing = whatsMissing(meta, contactBytes ? new TextDecoder().decode(contactBytes) : '');
  if (missing) return json(400, { error: missing });
  // A made-up username would leave the reporter unreachable. GitHub trouble doesn't block
  // the report: only a clear "no such user" does.
  if (meta.github) {
    const known = await userExists(env, meta.github).catch(e => { console.error('user check', e.message); return true; });
    if (!known) return json(400, { error: `No GitHub user @${meta.github}.` });
  }

  const now = new Date();
  const who = await ipHash(env, ip);
  const today = await listAll(env.DUMPS, `dumps/${now.toISOString().slice(0, 10).replace(/-/g, '/')}/`);
  if (today.length >= int(env.DAILY_CAP, 100))
    return json(429, { error: 'The report service is full for today; try again tomorrow.' });
  if (today.filter(o => o.customMetadata?.ip === who).length >= int(env.PER_IP_DAILY, 10))
    return json(429, { error: 'Too many reports from this connection today.' });

  const key = newKey(now);
  const custom = { ip: who, version: meta.version, platform: meta.platform };
  await env.DUMPS.put(key, bytes, {
    httpMetadata: { contentType: 'application/zip' }, customMetadata: custom,
  });

  const attachments = entries.filter(e => e.name.startsWith('attachments/') && !e.name.endsWith('/')).length;
  const issue = issueFor(env, meta, sysInfo, key, attachments);
  try {
    const made = await createIssue(env, issue);
    await env.DUMPS.put(key, bytes, {
      httpMetadata: { contentType: 'application/zip' },
      customMetadata: { ...custom, issue: String(made.number) },
    });
    return json(201, { issue: made.number, url: made.html_url });
  } catch (e) {
    // Stored but no issue: the cron opens it later. The app counts this as sent.
    console.error('issue for', key, e.message);
    await env.DUMPS.put('pending/' + key.slice('dumps/'.length) + '.json', JSON.stringify(issue));
    return json(202, { id: key.slice('dumps/'.length) });
  }
}

async function openPending(env) {
  for (const p of (await env.DUMPS.list({ prefix: 'pending/', limit: 10 })).objects) {
    const key = 'dumps/' + p.key.slice('pending/'.length, -'.json'.length);
    const [issue, dump] = [await (await env.DUMPS.get(p.key))?.json(), await env.DUMPS.get(key)];
    if (!issue || !dump) { await env.DUMPS.delete(p.key); continue; }
    try {
      const made = await createIssue(env, issue);
      await env.DUMPS.put(key, await dump.arrayBuffer(), {
        httpMetadata: { contentType: 'application/zip' },
        customMetadata: { ...dump.customMetadata, issue: String(made.number) },
      });
      await env.DUMPS.delete(p.key);
    } catch (e) { console.error('pending', p.key, e.message); return; }
  }
}

async function prune(env) {
  const objects = (await listAll(env.DUMPS, 'dumps/')).map(o => ({
    key: o.key, size: o.size, uploaded: o.uploaded, issue: issueOf(o),
  }));
  const at = int(env.PRUNE_AT_BYTES, 9e9), to = int(env.PRUNE_TO_BYTES, 8e9);
  const total = objects.reduce((s, o) => s + o.size, 0);
  if (total < at) { console.log(`storage ${total} bytes, below ${at}`); return; }
  const closed = await closedReportIssues(env);
  const { remaining, victims } = planPrune(objects, closed, at, to);
  for (let i = 0; i < victims.length; i += 1000)
    await env.DUMPS.delete(victims.slice(i, i + 1000).map(v => v.key));
  for (const v of victims)
    if (v.issue != null && !closed.has(v.issue))
      await comment(env, v.issue, 'The dump for this report was removed to free storage.').catch(e => console.error(e.message));
  const summary = `Storage pruning: ${victims.length} dumps deleted, ${total} → ${remaining} bytes ` +
    `(${victims.filter(v => closed.has(v.issue)).length} for closed issues).`;
  console.log(summary);
  if (env.TRACKING_ISSUE) await comment(env, Number(env.TRACKING_ISSUE), summary).catch(e => console.error(e.message));
}

export default {
  async fetch(request, env) {
    const { pathname } = new URL(request.url);
    if (pathname === '/v1/reports' && request.method === 'POST') return report(request, env);
    if (pathname === '/v1/health') return json(200, { ok: true });
    return json(404, { error: 'Not found.' });
  },
  async scheduled(_event, env, ctx) {
    ctx.waitUntil((async () => { await openPending(env); await prune(env); })());
  },
};
