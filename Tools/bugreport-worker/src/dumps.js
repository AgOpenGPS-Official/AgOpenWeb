// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

// The maintainer half of the bug report service: GET /v1/dumps/<yyyy>/<mm>/<dd>/<id>.zip
// returns a stored dump. The Worker's workers.dev address sits behind Cloudflare Access, and
// the Worker checks Access's signed assertion itself too, so a misconfigured Access policy
// fails closed instead of opening the bucket.

const KEY = /^\/v1\/(dumps\/\d{4}\/\d{2}\/\d{2}\/[0-9a-f]{32}\.zip)$/;

const b64urlBytes = s => Uint8Array.from(atob(s.replace(/-/g, '+').replace(/_/g, '/')), c => c.charCodeAt(0));

let certs = null; // { keys, fetched } for this isolate

async function accessKeys(team, refresh) {
  if (!refresh && certs && Date.now() - certs.fetched < 3600_000) return certs.keys;
  const res = await fetch(`https://${team}.cloudflareaccess.com/cdn-cgi/access/certs`);
  certs = { keys: (await res.json()).keys, fetched: Date.now() };
  return certs.keys;
}

/** The Access JWT's claims when it is valid for this application, else null. */
export async function verifyAccess(token, team, aud, now = Date.now() / 1000, keys = null) {
  const parts = (token || '').split('.');
  if (parts.length !== 3) return null;
  let head, claims;
  try {
    head = JSON.parse(new TextDecoder().decode(b64urlBytes(parts[0])));
    claims = JSON.parse(new TextDecoder().decode(b64urlBytes(parts[1])));
  } catch { return null; }
  if (head.alg !== 'RS256') return null;
  for (const refresh of [false, true]) {
    const jwk = (keys || await accessKeys(team, refresh)).find(k => k.kid === head.kid);
    if (!jwk) { if (keys) return null; continue; }
    const key = await crypto.subtle.importKey('jwk', jwk,
      { name: 'RSASSA-PKCS1-v1_5', hash: 'SHA-256' }, false, ['verify']);
    const ok = await crypto.subtle.verify('RSASSA-PKCS1-v1_5', key, b64urlBytes(parts[2]),
      new TextEncoder().encode(parts[0] + '.' + parts[1]));
    const audOk = Array.isArray(claims.aud) ? claims.aud.includes(aud) : claims.aud === aud;
    return ok && audOk && claims.exp > now && claims.iss === `https://${team}.cloudflareaccess.com`
      ? claims : null;
  }
  return null;
}

export default {
  async fetch(request, env) {
    if (!env.ACCESS_TEAM || !env.ACCESS_AUD) return new Response('Access not configured', { status: 503 });
    const claims = await verifyAccess(request.headers.get('Cf-Access-Jwt-Assertion'), env.ACCESS_TEAM, env.ACCESS_AUD);
    if (!claims) return new Response('Forbidden', { status: 403 });
    const m = KEY.exec(new URL(request.url).pathname);
    if (!m || request.method !== 'GET') return new Response('Not found', { status: 404 });
    const obj = await env.DUMPS.get(m[1]);
    if (!obj) return new Response('This dump has been removed.', { status: 404 });
    const issue = obj.customMetadata?.issue;
    const name = issue ? `bugreport-issue-${issue}.zip` : m[1].split('/').pop();
    console.log('dump', m[1], 'to', claims.email || claims.common_name || 'service token');
    return new Response(obj.body, {
      headers: { 'Content-Type': 'application/zip', 'Content-Disposition': `attachment; filename="${name}"` },
    });
  },
};
