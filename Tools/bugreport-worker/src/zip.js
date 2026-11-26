// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

// Just enough zip reading to check that an upload is an AgOpenWeb dump and to read its
// system_info.txt: the central directory names, and one entry's bytes (stored or deflated).
// Dumps come from .NET's ZipArchive, which writes zip64 only past 4 GB, so plain zip is all
// we see.

const EOCD = 0x06054b50, CENTRAL = 0x02014b50, LOCAL = 0x04034b50;

/** Central directory entries, or null when the bytes aren't a readable zip. */
export function listEntries(bytes) {
  if (bytes.length < 22) return null;
  const v = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  if (v.getUint32(0, true) !== LOCAL) return null;
  let eocd = -1;
  for (let i = bytes.length - 22, stop = Math.max(0, bytes.length - 22 - 65535); i >= stop; i--)
    if (v.getUint32(i, true) === EOCD) { eocd = i; break; }
  if (eocd < 0) return null;
  const count = v.getUint16(eocd + 10, true);
  let p = v.getUint32(eocd + 16, true);
  const names = new TextDecoder();
  const out = [];
  for (let n = 0; n < count; n++) {
    if (p + 46 > bytes.length || v.getUint32(p, true) !== CENTRAL) return null;
    const nameLen = v.getUint16(p + 28, true), extraLen = v.getUint16(p + 30, true),
      commentLen = v.getUint16(p + 32, true);
    if (p + 46 + nameLen > bytes.length) return null;
    out.push({
      name: names.decode(bytes.subarray(p + 46, p + 46 + nameLen)),
      method: v.getUint16(p + 10, true),
      compressedSize: v.getUint32(p + 20, true),
      size: v.getUint32(p + 24, true),
      offset: v.getUint32(p + 42, true),
    });
    p += 46 + nameLen + extraLen + commentLen;
  }
  return out;
}

/** One entry's bytes, or null if it can't be read or is larger than maxBytes. */
export async function readEntry(bytes, entry, maxBytes) {
  if (entry.size > maxBytes || entry.compressedSize > maxBytes) return null;
  const v = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  const p = entry.offset;
  if (p + 30 > bytes.length || v.getUint32(p, true) !== LOCAL) return null;
  const start = p + 30 + v.getUint16(p + 26, true) + v.getUint16(p + 28, true);
  const data = bytes.subarray(start, start + entry.compressedSize);
  if (data.length !== entry.compressedSize) return null;
  if (entry.method === 0) return data;
  if (entry.method !== 8) return null;
  try {
    const stream = new Blob([data]).stream().pipeThrough(new DecompressionStream('deflate-raw'));
    const out = new Uint8Array(await new Response(stream).arrayBuffer());
    return out.length > maxBytes ? null : out;
  } catch { return null; }
}

/** The lines of system_info.txt safe to show on a public issue (not the machine name). */
export function publicSystemInfo(text) {
  const keep = ['App Version', 'Build Info', 'OS', 'Runtime'];
  return text.replace(/^﻿/, '').split(/\r?\n/)
    .filter(l => keep.some(k => l.startsWith(k + ':')))
    .map(l => l.slice(0, 200));
}
