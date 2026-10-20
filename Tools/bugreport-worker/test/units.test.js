// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { generateKeyPairSync, verify } from 'node:crypto';
import { listEntries, readEntry, publicSystemInfo } from '../src/zip.js';
import { appJwt, pemToPkcs8 } from '../src/github.js';
import { planPrune, issueOf } from '../src/prune.js';
import { cleanMeta, issueFor } from '../src/reports.js';
import { verifyAccess } from '../src/dumps.js';

const fixture = name => new Uint8Array(readFileSync(new URL('./fixtures/' + name, import.meta.url)));

test('zip: lists entries and inflates system_info.txt', async () => {
  const bytes = fixture('dump.zip');
  const entries = listEntries(bytes);
  assert.deepEqual(entries.map(e => e.name), ['system_info.txt', 'logs.txt']);
  const sys = await readEntry(bytes, entries[0], 64 * 1024);
  assert.match(new TextDecoder().decode(sys), /^App Version: 1\.0\.0\.0\r\n/);
  assert.equal(new TextDecoder().decode(await readEntry(bytes, entries[1], 1024)), 'log line\n');
});

test('zip: over the size limit or not a zip reads as null', async () => {
  const bytes = fixture('dump.zip');
  assert.equal(await readEntry(bytes, listEntries(bytes)[0], 100), null);
  assert.equal(listEntries(new TextEncoder().encode('just some text, not a zip at all')), null);
  assert.equal(listEntries(bytes.subarray(0, bytes.length - 30)), null);
});

test('zip: the machine name never reaches the public issue', () => {
  const lines = publicSystemInfo('﻿App Version: 1.0\nMachine: FARM-PC\nOS: Windows\nWorking Set: 1MB\n');
  assert.deepEqual(lines, ['App Version: 1.0', 'OS: Windows']);
});

test('github: JWT from a PKCS#1 key (as GitHub hands it out) verifies', async () => {
  const { privateKey, publicKey } = generateKeyPairSync('rsa', { modulusLength: 2048 });
  const pem = privateKey.export({ type: 'pkcs1', format: 'pem' });
  assert.match(pem, /BEGIN RSA PRIVATE KEY/);
  const jwt = await appJwt(12345, pem, 1_800_000_000);
  const [h, b, s] = jwt.split('.');
  const claims = JSON.parse(Buffer.from(b, 'base64url'));
  assert.deepEqual(claims, { iat: 1_800_000_000 - 60, exp: 1_800_000_000 + 540, iss: '12345' });
  assert.ok(verify('sha256', Buffer.from(h + '.' + b), publicKey, Buffer.from(s, 'base64url')));
  // A PKCS#8 key passes through unchanged.
  const p8 = privateKey.export({ type: 'pkcs8', format: 'pem' });
  assert.deepEqual(Buffer.from(pemToPkcs8(p8)), privateKey.export({ type: 'pkcs8', format: 'der' }));
});

const GB = 1e9;
const obj = (key, gb, day, issue = null) => ({ key, size: gb * GB, uploaded: new Date(2026, 0, day), issue });

test('prune: nothing below 9 GB', () => {
  const r = planPrune([obj('a', 4, 1), obj('b', 4.9, 2)], new Set(), 9 * GB, 8 * GB);
  assert.equal(r.victims.length, 0);
});

test('prune: closed issues first by age, then the rest by age, until below 8 GB', () => {
  const objects = [
    obj('open-old', 2, 1, 10),   // open, oldest
    obj('closed-new', 1, 9, 11), // closed
    obj('none', 2, 2),           // still waiting for its issue
    obj('closed-old', 1, 3, 12), // closed
    obj('open-new', 3.5, 8, 13),
  ];
  const r = planPrune(objects, new Set([11, 12]), 9 * GB, 8 * GB);
  assert.equal(r.total, 9.5 * GB);
  assert.deepEqual(r.victims.map(v => v.key), ['closed-old', 'closed-new']);
  assert.equal(r.remaining, 7.5 * GB);
  const deeper = planPrune(objects, new Set([11, 12]), 9 * GB, 4 * GB);
  assert.deepEqual(deeper.victims.map(v => v.key), ['closed-old', 'closed-new', 'open-old', 'none']);
});

test('prune: issue number read from R2 metadata', () => {
  assert.equal(issueOf({ customMetadata: { issue: '412' } }), 412);
  assert.equal(issueOf({ customMetadata: {} }), null);
  assert.equal(issueOf({ customMetadata: { issue: 'x' } }), null);
});

test('reports: metadata is cut to size and a bad GitHub name dropped', () => {
  const m = cleanMeta(JSON.stringify({ title: '  Line\n jumps  ', description: 'x'.repeat(30000), github: '@crf767' }));
  assert.equal(m.title, 'Line jumps');
  assert.equal(m.description.length, 20000);
  assert.equal(m.github, 'crf767');
  assert.equal(cleanMeta(JSON.stringify({ github: 'not a name!' })).github, '');
  assert.equal(cleanMeta('{nope'), null);
});

test('reports: the issue carries the description, versions and the maintainer link', () => {
  const env = { DUMPS_URL: 'https://dumps.example' };
  const meta = cleanMeta(JSON.stringify({ title: '', description: 'Speed shows 0', platform: 'Windows', github: 'crf767' }));
  const i = issueFor(env, meta, ['App Version: 1.0'], 'dumps/2026/10/20/' + 'a'.repeat(32) + '.zip');
  assert.equal(i.title, 'Bug report (untitled)');
  assert.deepEqual(i.labels, ['bug', 'auto-report']);
  assert.match(i.body, /^Speed shows 0\n/);
  assert.match(i.body, /- App Version: 1\.0\n- Platform: Windows\n- Dump: https:\/\/dumps\.example\/v1\/dumps\/2026\/10\/20\/a{32}\.zip \(maintainers only\)/);
  assert.match(i.body, /by @crf767\.$/);
  assert.doesNotMatch(i.body, /Language/);
});

test('dumps: Access assertion checked for signature, audience, expiry and issuer', async () => {
  const { privateKey, publicKey } = generateKeyPairSync('rsa', { modulusLength: 2048 });
  const jwk = { ...publicKey.export({ format: 'jwk' }), kid: 'k1', alg: 'RS256' };
  const team = 'agopenweb', aud = 'aud-123';
  const sign = async claims => {
    const { createSign } = await import('node:crypto');
    const h = Buffer.from(JSON.stringify({ alg: 'RS256', kid: 'k1' })).toString('base64url');
    const b = Buffer.from(JSON.stringify(claims)).toString('base64url');
    return h + '.' + b + '.' + createSign('sha256').update(h + '.' + b).sign(privateKey, 'base64url');
  };
  const good = { aud: [aud], exp: 2000, iss: `https://${team}.cloudflareaccess.com`, email: 'chris@example' };
  assert.equal((await verifyAccess(await sign(good), team, aud, 1000, [jwk])).email, 'chris@example');
  assert.equal(await verifyAccess(await sign({ ...good, aud: ['other'] }), team, aud, 1000, [jwk]), null);
  assert.equal(await verifyAccess(await sign({ ...good, exp: 999 }), team, aud, 1000, [jwk]), null);
  assert.equal(await verifyAccess(await sign({ ...good, iss: 'https://evil.cloudflareaccess.com' }), team, aud, 1000, [jwk]), null);
  const t = (await sign(good)).split('.');
  const forged = t[0] + '.' + Buffer.from(JSON.stringify({ ...good, email: 'x' })).toString('base64url') + '.' + t[2];
  assert.equal(await verifyAccess(forged, team, aud, 1000, [jwk]), null);
  assert.equal(await verifyAccess('', team, aud, 1000, [jwk]), null);
});
