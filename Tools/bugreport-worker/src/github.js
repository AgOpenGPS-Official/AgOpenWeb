// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

// GitHub as the AgOpenWeb bug report App: an RS256 JWT signed with the App's key buys an
// installation token (an hour long) for the one repo the App is installed on.

const API = 'https://api.github.com';

const b64url = bytes => btoa(String.fromCharCode(...bytes))
  .replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
const utf8 = s => new TextEncoder().encode(s);

function derLength(n) {
  if (n < 0x80) return [n];
  const out = [];
  for (; n > 0; n >>= 8) out.unshift(n & 0xff);
  return [0x80 | out.length, ...out];
}

function der(tag, body) { return Uint8Array.from([tag, ...derLength(body.length), ...body]); }

/**
 * The key GitHub hands out is PKCS#1 ("BEGIN RSA PRIVATE KEY"); WebCrypto takes PKCS#8.
 * Either is accepted: PKCS#1 is wrapped in the PKCS#8 envelope for rsaEncryption.
 */
export function pemToPkcs8(pem) {
  const pkcs1 = pem.includes('BEGIN RSA PRIVATE KEY');
  const b64 = pem.replace(/-----[^-]+-----/g, '').replace(/\s+/g, '');
  const raw = Uint8Array.from(atob(b64), c => c.charCodeAt(0));
  if (!pkcs1) return raw;
  const version = [0x02, 0x01, 0x00];
  const rsaAlg = [0x30, 0x0d, 0x06, 0x09, 0x2a, 0x86, 0x48, 0x86, 0xf7, 0x0d, 0x01, 0x01, 0x01, 0x05, 0x00];
  return der(0x30, [...version, ...rsaAlg, ...der(0x04, raw)]);
}

export async function appJwt(appId, pem, now = Math.floor(Date.now() / 1000)) {
  const key = await crypto.subtle.importKey('pkcs8', pemToPkcs8(pem),
    { name: 'RSASSA-PKCS1-v1_5', hash: 'SHA-256' }, false, ['sign']);
  // iat a minute back for clock drift; GitHub allows at most 10 minutes.
  const head = b64url(utf8(JSON.stringify({ alg: 'RS256', typ: 'JWT' })));
  const body = b64url(utf8(JSON.stringify({ iat: now - 60, exp: now + 540, iss: String(appId) })));
  const sig = await crypto.subtle.sign('RSASSA-PKCS1-v1_5', key, utf8(head + '.' + body));
  return head + '.' + body + '.' + b64url(new Uint8Array(sig));
}

async function call(path, token, init = {}) {
  const res = await fetch(API + path, {
    ...init,
    headers: {
      Accept: 'application/vnd.github+json',
      Authorization: 'Bearer ' + token,
      'X-GitHub-Api-Version': '2022-11-28',
      'User-Agent': 'agopenweb-bugreports',
      ...(init.body ? { 'Content-Type': 'application/json' } : {}),
    },
  });
  if (!res.ok) throw new Error(`GitHub ${init.method || 'GET'} ${path}: ${res.status} ${(await res.text()).slice(0, 300)}`);
  return res.status === 204 ? null : res.json();
}

let cached = null; // { token, expires } for as long as this isolate lives

export async function installationToken(env) {
  if (cached && cached.expires - Date.now() > 5 * 60_000) return cached.token;
  const jwt = await appJwt(env.GITHUB_APP_ID, env.GITHUB_APP_KEY);
  const inst = await call(`/repos/${env.GITHUB_REPO}/installation`, jwt);
  const t = await call(`/app/installations/${inst.id}/access_tokens`, jwt, { method: 'POST' });
  cached = { token: t.token, expires: Date.parse(t.expires_at) };
  return cached.token;
}

export async function createIssue(env, { title, body, labels }) {
  const token = await installationToken(env);
  return call(`/repos/${env.GITHUB_REPO}/issues`, token,
    { method: 'POST', body: JSON.stringify({ title, body, labels }) });
}

/** Whether a GitHub account by this name exists; throws when GitHub can't say. */
export async function userExists(env, name) {
  const token = await installationToken(env);
  try { await call(`/users/${encodeURIComponent(name)}`, token); return true; }
  catch (e) { if (/: 404 /.test(e.message)) return false; throw e; }
}

export async function comment(env, number, body) {
  const token = await installationToken(env);
  return call(`/repos/${env.GITHUB_REPO}/issues/${number}/comments`, token,
    { method: 'POST', body: JSON.stringify({ body }) });
}

/** Numbers of the closed auto-report issues (pages of 100). */
export async function closedReportIssues(env) {
  const token = await installationToken(env);
  const closed = new Set();
  for (let page = 1; ; page++) {
    const batch = await call(`/repos/${env.GITHUB_REPO}/issues?state=closed&labels=auto-report&per_page=100&page=${page}`, token);
    for (const i of batch) closed.add(i.number);
    if (batch.length < 100) return closed;
  }
}
