// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

// Storage pruning: R2 lifecycle rules delete by age only, so the daily cron sums the bucket
// and, at PRUNE_AT_BYTES or more, deletes down to below PRUNE_TO_BYTES. Dumps whose issue is
// closed go first, oldest first; then the rest (open issues, and any still waiting for one)
// by age, so the reports being worked on stay longest.

/**
 * @param objects [{ key, size, uploaded: Date, issue: number|null }]
 * @param closed  Set of closed issue numbers
 * @returns { total, remaining, victims } victims in deletion order
 */
export function planPrune(objects, closed, atBytes, toBytes) {
  const total = objects.reduce((s, o) => s + o.size, 0);
  if (total < atBytes) return { total, remaining: total, victims: [] };
  const isClosed = o => o.issue != null && closed.has(o.issue);
  const order = [...objects].sort((a, b) =>
    (isClosed(b) - isClosed(a)) || (a.uploaded - b.uploaded));
  const victims = [];
  let remaining = total;
  for (const o of order) {
    if (remaining < toBytes) break;
    victims.push(o);
    remaining -= o.size;
  }
  return { total, remaining, victims };
}

/** The issue number stored with a dump, or null. */
export function issueOf(obj) {
  const n = Number(obj.customMetadata?.issue);
  return Number.isInteger(n) && n > 0 ? n : null;
}
