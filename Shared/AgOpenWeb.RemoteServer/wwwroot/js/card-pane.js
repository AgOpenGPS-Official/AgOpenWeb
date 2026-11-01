// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0
//
// The card pane (split layout).

// ---- Card pane (#456, Plans/CARD_PANE_SPLIT_PLAN.md) ----
// The VT and web cards float over the map, or (split layout) sit in a pane on the left or right
// with the map in the rest. In the pane the open cards go two to a tab, in the order they were
// opened, each sized to its share of the pane; with none open the map has the whole window. The
// layout and the pane's width are this browser's. A card moved into or out of the pane is moved in
// the page, which reloads a web page, so that happens only when the layout changes: a tab switch
// hides and shows cards.
const CardPane = (() => {
  const KEY = 'cards.pane', MIN = 1 / 3, MAX = 1 / 2, GAP = 6, DIV = 16; // DIV: the divider strip
  let layout = 'float', frac = MIN, tab = 0;
  try { const st = JSON.parse(localStorage.getItem(KEY) || '{}'); if (st.layout === 'left' || st.layout === 'right') layout = st.layout; if (st.frac >= MIN && st.frac <= MAX) frac = st.frac; } catch (_) {}
  const save = () => { try { localStorage.setItem(KEY, JSON.stringify({ layout, frac })); } catch (_) {} };

  const pane = document.createElement('div'); pane.id = 'cardpane';
  const tabsEl = document.createElement('div'); tabsEl.className = 'cp-tabs';
  const slots = document.createElement('div'); slots.className = 'cp-slots';
  const divider = document.createElement('div'); divider.className = 'cp-divider';
  pane.append(tabsEl, slots, divider);
  document.body.insertBefore(pane, document.getElementById('vt-card-tpl'));
  // Nothing in the pane reaches the map (the window-level pan, tap and wheel handlers).
  for (const t of ['pointerdown', 'wheel']) pane.addEventListener(t, e => { if (!e.ctrlKey) e.stopPropagation(); });

  // Every card: { card, fit(w, h) in its own pixels, name(), isOpen() }; order = when opened.
  let cards = [], order = [];
  const split = () => layout !== 'float';
  const home = c => { c.card.classList.remove('in-pane', 'cp-hide'); c.card.style.order = ''; document.body.insertBefore(c.card, document.getElementById('vt-card-tpl')); c.fit(null); };
  const into = c => { c.card.classList.add('in-pane'); if (c.card.parentNode !== slots) slots.appendChild(c.card); };

  function layoutNow() {
    order = order.filter(c => cards.includes(c) && c.isOpen());
    const open = split() ? order : [];
    pane.classList.toggle('show', open.length > 0);
    pane.classList.toggle('right', layout === 'right');
    if (!open.length) { setMapArea(0, 0); return; }
    const w = Math.round(innerWidth * frac);
    setMapArea(layout === 'left' ? w : 0, layout === 'right' ? w : 0);
    pane.style.width = (w / uiZoom) + 'px';
    const tabs = Math.ceil(open.length / 2);
    tab = Math.min(tab, tabs - 1);
    tabsEl.innerHTML = '';
    tabsEl.style.display = tabs > 1 ? '' : 'none';
    for (let t = 0; t < tabs; t++) {
      const b = document.createElement('button');
      b.className = 'cp-tab' + (t === tab ? ' active' : '');
      b.textContent = open.slice(t * 2, t * 2 + 2).map(c => c.name()).join(' · ');
      b.addEventListener('pointerdown', e => { e.preventDefault(); tab = t; layoutNow(); });
      tabsEl.appendChild(b);
    }
    // Each card's share of the pane, in its own (zoomed) pixels.
    const inTab = open.slice(tab * 2, tab * 2 + 2);
    const sw = w / uiZoom - DIV - 2 * GAP;
    const sh = (innerHeight / uiZoom - (tabs > 1 ? tabsEl.offsetHeight : 0) - GAP * (inTab.length + 1)) / inTab.length;
    for (const c of cards) {
      const k = inTab.indexOf(c);
      c.card.classList.toggle('cp-hide', k < 0);
      if (k >= 0) { c.card.style.order = String(k); c.fit(sw, sh); }
    }
  }

  // Width: dragging the divider, a third to a half of the window.
  let drag = null;
  divider.addEventListener('pointerdown', e => {
    e.preventDefault(); drag = { x: e.clientX, w: innerWidth * frac };
    try { divider.setPointerCapture(e.pointerId); } catch (_) {}
  });
  divider.addEventListener('pointermove', e => {
    if (!drag) return;
    const dx = (e.clientX - drag.x) * (layout === 'left' ? 1 : -1);
    const f = Math.min(MAX, Math.max(MIN, (drag.w + dx) / innerWidth));
    if (Math.abs(f - frac) * innerWidth >= 2) { frac = f; layoutNow(); }
  });
  const end = () => { if (drag) { drag = null; save(); } };
  divider.addEventListener('pointerup', end); divider.addEventListener('pointercancel', end);
  for (const t of ['resize', 'uizoom']) addEventListener(t, () => { if (split()) layoutNow(); });

  return {
    get layout() { return layout; },
    setLayout(l) {
      if (l === layout) return;
      layout = l; save();
      for (const c of cards) if (split()) into(c); else home(c);
      layoutNow();
    },
    // A card made: into the pane in the split layout. fit(null) means floating again.
    track(card, opts) {
      const c = Object.assign({ card }, opts);
      cards.push(c);
      if (split()) into(c);
      return c;
    },
    untrack(c) { cards = cards.filter(x => x !== c); order = order.filter(x => x !== c); if (split()) layoutNow(); },
    // A card opened (shown at once, on its tab) or closed.
    shown(c, on) {
      order = order.filter(x => x !== c);
      if (on) order.push(c);
      if (!split()) return;
      if (on) tab = Math.floor(order.filter(x => x.isOpen()).indexOf(c) / 2);
      layoutNow();
    },
    // A card's name changed (its tab's label).
    renamed() { if (split()) layoutNow(); },
  };
})();
