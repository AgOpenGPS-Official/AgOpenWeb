// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0
//
// The card pane (split layout) and its drawer.

// ---- Card pane (#456, Plans/ADD_ON_CARDS_PLAN.md) ----
// The VT and web cards float over the map, or (split layout) sit in a pane on the left or right
// with the map in the rest. In the pane the open cards go two to a tab, in the order they were
// opened, each sized to its share of the pane; with none open the map has the whole window. The
// layout and the pane's width are this browser's. A card moved into or out of the pane is moved in
// the page, which reloads a web page, so that happens only when the layout changes: a tab switch
// hides and shows cards.
//
// The pane is a drawer (§3): Closed (its handle on the window's edge, with the count of the open
// cards), Split (a third to a half of the window) or Full (over the map, which stops drawing; a
// guidance strip along the top keeps the speed, the distance from the line, the sections and the
// steer state, and above walking pace the light bar or steer bar too). Only the operator moves
// it: a drag of the handle moves the edge freely and snaps on release (under a third → Closed,
// past a half → Full), a tap toggles Closed and the last open position. Above walking pace it
// can't be pulled to Full. It appears Closed when the first card opens (nothing flies out
// unasked) and never closes by itself.
const CardPane = (() => {
  const KEY = 'cards.pane', MIN = 1 / 3, MAX = 1 / 2, GAP = 6, DIV = 16; // DIV: the handle strip
  const WALKING_KMH = 2;
  let layout = 'float', frac = MIN, pos = 'closed', openPos = 'split', tab = 0;
  try {
    const st = JSON.parse(localStorage.getItem(KEY) || '{}');
    if (st.layout === 'left' || st.layout === 'right') layout = st.layout;
    if (st.frac >= MIN && st.frac <= MAX) frac = st.frac;
    if (st.openPos === 'full') openPos = 'full';
  } catch (_) {}
  const save = () => { try { localStorage.setItem(KEY, JSON.stringify({ layout, frac, openPos })); } catch (_) {} };
  const moving = () => (lastTick ? lastTick.speed : 0) * 3.6 > WALKING_KMH;

  const pane = document.createElement('div'); pane.id = 'cardpane';
  const strip = document.createElement('div'); strip.className = 'cp-strip';
  const tabsEl = document.createElement('div'); tabsEl.className = 'cp-tabs';
  const slots = document.createElement('div'); slots.className = 'cp-slots';
  const handle = document.createElement('div'); handle.className = 'cp-handle';
  // The grab tab: as wide as the side nav and just under it, a chevron pointing the way a tap
  // moves the drawer, the count of the open cards while Closed. Sized for a finger.
  const grab = document.createElement('div'); grab.className = 'cp-grab';
  grab.innerHTML = '<svg viewBox="0 0 24 24" width="30" height="30" aria-hidden="true"><path d="M9 5l7 7-7 7" fill="none" stroke="currentColor" stroke-width="3" stroke-linecap="round" stroke-linejoin="round"/></svg>';
  const count = document.createElement('span'); count.className = 'cp-count'; grab.appendChild(count);
  handle.appendChild(grab);
  pane.append(strip, tabsEl, slots, handle);
  document.body.insertBefore(pane, document.getElementById('vt-card-tpl'));
  // Nothing in the pane reaches the map (the window-level pan, tap and wheel handlers).
  for (const t of ['pointerdown', 'wheel']) pane.addEventListener(t, e => { if (!e.ctrlKey) e.stopPropagation(); });

  // Every card: { card, fit(w, h) in its own pixels, name(), isOpen() }; order = when opened.
  let cards = [], order = [], open = [], curW = 0;
  const split = () => layout !== 'float';
  const home = c => { c.card.classList.remove('in-pane', 'cp-hide'); c.card.style.order = ''; document.body.insertBefore(c.card, document.getElementById('vt-card-tpl')); c.fit(null); };
  const into = c => { c.card.classList.add('in-pane'); if (c.card.parentNode !== slots) slots.appendChild(c.card); };
  // The pane's paddings (zoomed px): the window's safe-area inset on its outer side, the handle on the inner.
  const pads = () => { const s = getComputedStyle(pane); return { l: parseFloat(s.paddingLeft) || 0, r: parseFloat(s.paddingRight) || 0, t: parseFloat(s.paddingTop) || 0, b: parseFloat(s.paddingBottom) || 0 }; };
  const handleW = () => { const p = pads(); return Math.round((layout === 'left' ? p.l + DIV : p.r + DIV) * uiZoom); };
  // The pane's width in window px for its position; a live drag passes its own.
  const widthFor = () => pos === 'closed' ? handleW() : pos === 'full' ? innerWidth : Math.round(innerWidth * frac);

  function layoutNow(liveW) {
    order = order.filter(c => cards.includes(c) && c.isOpen());
    const had = open.length;
    open = split() ? order : [];
    if (!had && open.length) pos = 'closed'; // the drawer appears as its tab; the operator pulls it
    pane.classList.toggle('show', open.length > 0);
    pane.classList.toggle('right', layout === 'right');
    if (!open.length) { curW = 0; setMapArea(0, 0); return; }
    const w = liveW != null ? liveW : widthFor();
    curW = w;
    const full = liveW == null && pos === 'full', closed = liveW == null && pos === 'closed';
    pane.classList.toggle('full', full);
    pane.classList.toggle('closed', closed);
    // Full: both insets, so nothing on the map (the navs, the bar) sits over the pane.
    if (full) setMapArea(innerWidth, innerWidth);
    else setMapArea(layout === 'left' ? w : 0, layout === 'right' ? w : 0);
    pane.style.width = (w / uiZoom) + 'px';
    count.textContent = String(open.length);
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
    // Each card's share of the pane, in its own (zoomed) pixels: stacked, or side by side in Full.
    const inTab = open.slice(tab * 2, tab * 2 + 2), n = inTab.length;
    const p = pads();
    // The tab under the side nav (the left nav's top row when the right nav isn't shown).
    const ln = document.getElementById('leftnav').getBoundingClientRect();
    const rn = document.getElementById('rightnav').getBoundingClientRect();
    const under = (layout === 'left' ? ln.bottom : rn.height ? rn.bottom : ln.top) / uiZoom + 8;
    // Shorter when the screen leaves less room under the nav (a 10" tablet), never under 64 px;
    // with no room at all (a big UI size, where the nav itself scrolls) it sits at the bottom.
    const room = innerHeight / uiZoom - p.b - 8 - under, gh = Math.max(64, Math.min(120, room));
    grab.style.height = gh + 'px';
    grab.style.top = Math.round(room >= 64 ? under : innerHeight / uiZoom - p.b - 8 - gh) + 'px';
    // The chevron points the way a tap moves the drawer: open it from Closed, else close it.
    grab.classList.toggle('flip', (layout === 'right') === closed);
    const iw = w / uiZoom - p.l - p.r, ih = innerHeight / uiZoom - p.t - p.b
      - (tabs > 1 ? tabsEl.offsetHeight : 0) - (full ? strip.offsetHeight : 0);
    const sw = full ? (iw - GAP * (n + 1)) / n : iw - 2 * GAP;
    const sh = full ? ih - 2 * GAP : (ih - GAP * (n + 1)) / n;
    for (const c of cards) {
      const k = inTab.indexOf(c);
      c.card.classList.toggle('cp-hide', k < 0);
      if (k >= 0) { c.card.style.order = String(k); c.fit(sw, sh); }
    }
  }

  // While the handle is dragged: where it will snap. Two guide lines across the window at a
  // third and a half (the Full one only when it can be reached), the one for the zone the edge
  // is in lit with its label, and a halo round the tab in the zone's colour, since a finger
  // covers the tab itself; a short buzz on entering a zone where the device has one.
  const guides = document.createElement('div'); guides.id = 'cp-guides';
  guides.innerHTML = '<div class="cp-guide" data-zone="split"><span></span></div><div class="cp-guide" data-zone="full"><span></span></div>';
  document.body.appendChild(guides);
  const [gSplit, gFull] = guides.children;
  let zone = null;
  function showGuides(on) {
    guides.classList.toggle('show', on);
    if (!on) { zone = null; pane.classList.remove('drag-closed', 'drag-split', 'drag-full'); return; }
    gSplit.querySelector('span').textContent = tr('Split');
    gFull.querySelector('span').textContent = tr('Full');
    const side = layout === 'left' ? 'left' : 'right';
    for (const [g, f] of [[gSplit, MIN], [gFull, MAX]]) {
      g.style.left = g.style.right = '';
      g.style[side] = (innerWidth * f / uiZoom) + 'px';
    }
    gFull.style.display = moving() ? 'none' : '';
  }
  function dragZone(w) {
    const f = w / innerWidth, z = f < MIN ? 'closed' : f > MAX ? 'full' : 'split';
    if (z === zone) return;
    if (zone && navigator.vibrate) navigator.vibrate(12);
    zone = z;
    for (const k of ['closed', 'split', 'full']) pane.classList.toggle('drag-' + k, k === z);
    gSplit.classList.toggle('lit', z === 'split');
    gFull.classList.toggle('lit', z === 'full');
  }

  // The handle: a drag moves the pane's edge and snaps on release, a tap toggles Closed.
  let drag = null;
  handle.addEventListener('pointerdown', e => {
    e.preventDefault(); drag = { x: e.clientX, w: curW, moved: false };
    try { handle.setPointerCapture(e.pointerId); } catch (_) {}
  });
  handle.addEventListener('pointermove', e => {
    if (!drag) return;
    if (Math.abs(e.clientX - drag.x) > 6 && !drag.moved) { drag.moved = true; showGuides(true); dragZone(curW); }
    if (!drag.moved) return;
    const dx = (e.clientX - drag.x) * (layout === 'left' ? 1 : -1);
    let w = Math.min(innerWidth, Math.max(handleW(), drag.w + dx));
    if (moving()) w = Math.min(w, Math.round(innerWidth * MAX)); // not to Full at speed
    dragZone(w);
    if (Math.abs(w - curW) >= 2) layoutNow(w);
  });
  const toggle = () => { pos = pos === 'closed' ? (openPos === 'full' && moving() ? 'split' : openPos) : 'closed'; };
  const end = () => {
    if (!drag) return;
    const d = drag; drag = null;
    showGuides(false);
    if (!d.moved) toggle();
    else {
      const f = curW / innerWidth;
      if (f < MIN) pos = 'closed';
      else if (f > MAX) pos = openPos = 'full';
      else { pos = openPos = 'split'; frac = f; }
    }
    save(); layoutNow();
  };
  handle.addEventListener('pointerup', end); handle.addEventListener('pointercancel', end);
  for (const t of ['resize', 'uizoom']) addEventListener(t, () => { if (split()) layoutNow(); });

  // ---- The guidance strip (Full) ----
  // What the map's HUD showed, in one row: speed, the distance from the line with the steer
  // arrow, the AutoSteer state, the sections; above walking pace a second row with the light
  // bar's cells or the steer bar (the same rules as lightbarSk / steerBarSk).
  strip.innerHTML = '<span class="cp-speed"><b></b> <small></small></span>'
    + '<span class="cp-xte"><span class="cp-arrow"></span><span class="cp-dist"></span></span>'
    + '<span class="cp-steer"><i class="cp-dot"></i></span><span class="cp-secs"></span>'
    + '<div class="cp-bar"></div>';
  const [speedB, speedU, arrow, dist, dot, secs, bar] = ['.cp-speed b', '.cp-speed small', '.cp-arrow', '.cp-dist', '.cp-dot', '.cp-secs', '.cp-bar']
    .map(s => strip.querySelector(s));
  const LB_SIDE = 7;
  let secCount = -1, barKind = '', titled = false;
  function renderStrip() {
    if (!titled) { titled = true; strip.querySelector('.cp-steer').title = tr('AutoSteer'); }
    const mps = lastTick ? lastTick.speed : 0;
    speedB.textContent = toDisplayUnit(mps * 3.6, 'kmh').toFixed(1);
    speedU.textContent = unitLabel('kmh');
    const cfg = config && config.autosteer, guided = !!(tick && tick.guidanceActive);
    if (!guided) { arrow.textContent = ''; dist.textContent = '—'; }
    else {
      const xte = tick.crossTrackError || 0, onLine = Math.abs(xte) < 0.05;
      arrow.textContent = onLine ? '●' : xte > 0 ? '◀' : '▶'; // the way to steer
      arrow.style.color = !onLine && xte > 0 ? LB_LEFT : LB_RIGHT;
      dist.textContent = fmtUnit(Math.abs(xte) * 100, 'cm', 0);
    }
    const op = tick && tick.op;
    dot.style.background = op && op.autoSteer ? (op.moduleNotSteering ? '#e74c3c' : '#2ecc71') : '#6b7280';
    const sec = tick && tick.sections, master = !!(op && (op.sectionAuto || op.sectionManual) && sec && sec.length);
    secs.style.display = master ? '' : 'none';
    if (master) {
      if (sec.length !== secCount) { secCount = sec.length; secs.innerHTML = '<i></i>'.repeat(sec.length); }
      const cells = secs.children;
      for (let i = 0; i < cells.length; i++) cells[i].style.background = SECTION_COLORS[sec[i]] || SECTION_COLORS[0];
    }
    const barOn = moving() && guided && !!(cfg && cfg.guidanceBarOn && cfg.lightbarEnabled);
    pane.classList.toggle('moving', barOn);
    if (!barOn) return;
    const kind = cfg.steerBarEnabled ? 'steer' : 'light';
    if (kind !== barKind) {
      barKind = kind;
      bar.innerHTML = kind === 'light' ? '<i></i>'.repeat(LB_SIDE) + '<span class="cp-mid"></span>' + '<i></i>'.repeat(LB_SIDE)
        : '<div class="cp-sbar"><i></i></div>';
    }
    if (kind === 'light') {
      const cpp = Math.max(1, (cfg.cmPerPixel | 0) || 5), val = tick.crossTrackError || 0;
      const lit = Math.min(Math.round(Math.abs(val) / (cpp / 100)), LB_SIDE), steerLeft = val > 0;
      const cells = bar.querySelectorAll('i'); // left cells first, lit outwards from the middle
      for (let i = 0; i < LB_SIDE; i++) {
        const on = steerLeft && i >= LB_SIDE - lit, onR = !steerLeft && i < lit;
        cells[i].style.background = on ? LB_LEFT : '#1c2230';
        cells[LB_SIDE + i].style.background = onR ? LB_RIGHT : '#1c2230';
      }
    } else {
      const auto = !!(op && op.autoSteer), err = tick.steerAngleError || 0;
      const fill = bar.querySelector('.cp-sbar i');
      if (Math.abs(err) < (auto ? 0.5 : 0.2)) { fill.style.width = '0'; return; }
      const len = Math.min(Math.abs(err), 12) / 12 * 50; // % of the bar, from its centre
      fill.style.width = len + '%';
      fill.style.left = err > 0 ? '50%' : (50 - len) + '%'; // on the side away from the turn
      fill.style.opacity = auto ? '0.5' : '1';
    }
  }

  return {
    get layout() { return layout; },
    get position() { return pos; },
    // Full covers the map: the frame loop doesn't draw it.
    get coversMap() { return split() && pos === 'full' && open.length > 0; },
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
    // A card opened (on its tab, shown when the drawer is open) or closed.
    shown(c, on) {
      order = order.filter(x => x !== c);
      if (on) order.push(c);
      if (!split()) return;
      if (on) tab = Math.floor(order.filter(x => x.isOpen()).indexOf(c) / 2);
      layoutNow();
    },
    // A card's name changed (its tab's label).
    renamed() { if (split()) layoutNow(); },
    // Each frame: the guidance strip while Full.
    render() { if (pane.classList.contains('full')) renderStrip(); },
  };
})();
