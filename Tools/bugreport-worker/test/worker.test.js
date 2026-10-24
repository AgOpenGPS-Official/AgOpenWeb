// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

// The reports Worker end to end against an in-memory R2 bucket and a fake GitHub.
import { test, beforeEach } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { generateKeyPairSync } from 'node:crypto';
import worker from '../src/reports.js';

const dump = readFileSync(new URL('./fixtures/dump.zip', import.meta.url));
const notDump = readFileSync(new URL('./fixtures/not-a-dump.zip', import.meta.url));
const emailDump = readFileSync(new URL('./fixtures/dump-email.zip', import.meta.url)); // + contact.txt
const attachedDump = readFileSync(new URL('./fixtures/dump-attachments.zip', import.meta.url)); // + 2 attachments

class FakeBucket {
  constructor() { this.objects = new Map(); }
  async put(key, value, opts = {}) {
    const bytes = typeof value === 'string' ? new TextEncoder().encode(value) : new Uint8Array(value);
    this.objects.set(key, { key, bytes, size: bytes.length, uploaded: opts.uploaded || new Date(), customMetadata: opts.customMetadata || {} });
  }
  async get(key) {
    const o = this.objects.get(key);
    return o && { ...o, body: o.bytes, arrayBuffer: async () => o.bytes, json: async () => JSON.parse(new TextDecoder().decode(o.bytes)) };
  }
  async delete(keys) { for (const k of [].concat(keys)) this.objects.delete(k); }
  async list({ prefix = '', limit = 1000 } = {}) {
    const objects = [...this.objects.values()].filter(o => o.key.startsWith(prefix)).sort((a, b) => a.key < b.key ? -1 : 1);
    return { objects: objects.slice(0, limit), truncated: false };
  }
}

const { privateKey } = generateKeyPairSync('rsa', { modulusLength: 2048 });
let env, github;

beforeEach(() => {
  env = {
    DUMPS: new FakeBucket(), GITHUB_REPO: 'org/repo', GITHUB_APP_ID: '1',
    GITHUB_APP_KEY: privateKey.export({ type: 'pkcs1', format: 'pem' }),
    DUMPS_URL: 'https://dumps.example', PRUNE_AT_BYTES: '1000', PRUNE_TO_BYTES: '500',
  };
  github = { up: true, issues: [], comments: [], closed: [], users: ['crf767'] };
  globalThis.fetch = async (url, init = {}) => {
    const u = new URL(url);
    const reply = (status, body) => new Response(JSON.stringify(body), { status });
    if (!github.up) return reply(503, { message: 'down' });
    if (u.pathname === '/repos/org/repo/installation') return reply(200, { id: 7 });
    if (u.pathname === '/app/installations/7/access_tokens') return reply(201, { token: 't', expires_at: new Date(Date.now() + 3600e3).toISOString() });
    if (u.pathname === '/repos/org/repo/issues' && init.method === 'POST') {
      const n = 400 + github.issues.length;
      github.issues.push({ number: n, ...JSON.parse(init.body) });
      return reply(201, { number: n, html_url: `https://github.com/org/repo/issues/${n}` });
    }
    const user = /^\/users\/([^/]+)$/.exec(u.pathname);
    if (user) return github.users.includes(user[1]) ? reply(200, { login: user[1] }) : reply(404, { message: 'Not Found' });
    if (u.pathname === '/repos/org/repo/issues') return reply(200, github.closed.map(number => ({ number })));
    const c = /^\/repos\/org\/repo\/issues\/(\d+)\/comments$/.exec(u.pathname);
    if (c) { github.comments.push({ issue: +c[1], ...JSON.parse(init.body) }); return reply(201, {}); }
    return reply(404, {});
  };
});

const META = {
  title: 'GPS_Out shows no speed',
  description: 'Steps: start the sim, open GPS_Out, the speed field stays empty.',
  github: 'crf767', platform: 'Windows',
};

function post(bytes, meta = META) {
  const form = new FormData();
  form.set('meta', JSON.stringify(meta));
  form.set('dump', new Blob([bytes], { type: 'application/zip' }), 'bugreport.zip');
  return worker.fetch(new Request('https://w.example/v1/reports', {
    method: 'POST', body: form, headers: { 'CF-Connecting-IP': '203.0.113.5' },
  }), env);
}

const dumps = () => [...env.DUMPS.objects.values()].filter(o => o.key.startsWith('dumps/'));

test('a dump is stored and its issue opened', async () => {
  const res = await post(dump);
  assert.equal(res.status, 201);
  assert.deepEqual(await res.json(), { issue: 400, url: 'https://github.com/org/repo/issues/400' });
  const [stored] = dumps();
  assert.deepEqual(stored.bytes, new Uint8Array(dump));
  assert.equal(stored.customMetadata.issue, '400');
  const issue = github.issues[0];
  assert.equal(issue.title, 'GPS_Out shows no speed');
  assert.match(issue.body, /App Version: 1\.0\.0\.0/);
  assert.doesNotMatch(issue.body, /FARM-PC/);
  assert.ok(issue.body.includes('https://dumps.example/v1/' + stored.key));
  assert.match(issue.body, /by @crf767\./);
});

test('a report needs a real title, a description and a way to reply', async () => {
  const turnedAway = async (meta, bytes = dump) => {
    const res = await post(bytes, { ...META, ...meta });
    assert.equal(res.status, 400);
    return (await res.json()).error;
  };
  assert.match(await turnedAway({ title: 'turn on' }), /Title too short/);
  assert.match(await turnedAway({ description: 'it does not work' }), /Description too short/);
  assert.match(await turnedAway({ github: '' }), /GitHub username or an email/);
  assert.match(await turnedAway({ github: 'not a name' }), /Not a GitHub username/);
  assert.match(await turnedAway({ github: 'nobody-here' }), /No GitHub user @nobody-here/);
  assert.equal(env.DUMPS.objects.size, 0);
  assert.equal(github.issues.length, 0);
});

test('an email in the dump is enough, and it never reaches the issue', async () => {
  const res = await post(emailDump, { ...META, github: '' });
  assert.equal(res.status, 201);
  const issue = github.issues[0];
  assert.doesNotMatch(issue.body, /grower@example\.com/);
  assert.match(issue.body, /Sent from the app\.$/);
});

test('the issue counts the attachments but names none of them', async () => {
  assert.equal((await post(attachedDump)).status, 201);
  const body = github.issues[0].body;
  assert.match(body, /Attachments: 2 \(in the dump\)/);
  assert.doesNotMatch(body, /screen shot|field-notes/);
  assert.equal((await post(dump)).status, 201);
  assert.doesNotMatch(github.issues[1].body, /Attachments:/);
});

test('GitHub unable to look the user up does not block the report', async () => {
  const realFetch = globalThis.fetch;
  globalThis.fetch = async (url, init) => /\/users\//.test(url) ? new Response('{}', { status: 502 }) : realFetch(url, init);
  assert.equal((await post(dump)).status, 201);
});

test('anything but an AgOpenWeb dump is turned away and nothing stored', async () => {
  assert.equal((await post(notDump)).status, 400);
  assert.equal((await post(new TextEncoder().encode('hello'))).status, 400);
  assert.equal(env.DUMPS.objects.size, 0);
  assert.equal(github.issues.length, 0);
});

test('caps: per connection per day, and for everyone per day', async () => {
  env.PER_IP_DAILY = '2';
  assert.equal((await post(dump)).status, 201);
  assert.equal((await post(dump)).status, 201);
  assert.equal((await post(dump)).status, 429);
  env.PER_IP_DAILY = '10'; env.DAILY_CAP = '2';
  assert.equal((await post(dump)).status, 429);
  env.RATE_LIMIT = { limit: async () => ({ success: false }) };
  env.DAILY_CAP = '100';
  assert.equal((await post(dump)).status, 429);
});

test('GitHub down: stored, 202, and the cron opens the issue later', async () => {
  github.up = false;
  const res = await post(dump);
  assert.equal(res.status, 202);
  assert.equal(dumps()[0].customMetadata.issue, undefined);
  github.up = true;
  await run(worker.scheduled);
  assert.equal(github.issues.length, 1);
  assert.equal(dumps()[0].customMetadata.issue, '400');
  assert.equal([...env.DUMPS.objects.keys()].filter(k => k.startsWith('pending/')).length, 0);
});

async function run(handler) {
  const waits = [];
  await handler({}, env, { waitUntil: p => waits.push(p) });
  await Promise.all(waits);
}

test('cron prunes closed-issue dumps first and tells open issues it removed theirs', async () => {
  const put = (key, size, day, issue) => env.DUMPS.put(`dumps/2026/01/0${day}/${key}.zip`, new Uint8Array(size),
    { uploaded: new Date(2026, 0, day), customMetadata: issue ? { issue: String(issue) } : {} });
  await put('open-old', 300, 1, 10);
  await put('closed', 300, 5, 11);
  await put('open-new', 300, 6, 12);
  await put('none', 200, 2);
  github.closed = [11];
  await run(worker.scheduled); // 1100 bytes ≥ 1000: delete to below 500
  assert.deepEqual(dumps().map(o => o.key), ['dumps/2026/01/06/open-new.zip']);
  assert.deepEqual(github.comments.map(c => c.issue), [10]);
  await run(worker.scheduled); // 300 bytes: nothing more
  assert.equal(dumps().length, 1);
});
