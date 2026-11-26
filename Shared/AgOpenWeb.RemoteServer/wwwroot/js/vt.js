// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0
//
// The VT painter and VT cards.

// ---- VT painter ----
// The virtual terminal's draw list (screens, soft keys, list options) painted in Canvas 2D: no
// ISOBUS knowledge, the items in order. From SharpIsoTerm's reference painter
// (painter/vtpaint.js, bc508cf, MIT); keep the two in step when the draw list changes.
//   images.add(id, w, h, rgba) · vtPaintMask(ctx, screen, images) · vtPaintKey(ctx, keys, i, images)
//   vtPaintItems(ctx, items, images) · vtHit(items, x, y)

class VtImages {
  constructor() { this.byId = new Map(); }
  add(id, w, h, rgba) {
    const c = document.createElement("canvas");
    c.width = w;
    c.height = h;
    c.getContext("2d").putImageData(new ImageData(new Uint8ClampedArray(rgba), w, h), 0, 0);
    this.byId.set(id, c);
  }
  get(id) { return this.byId.get(id); }
}

// The 16 pixel line art pattern as dash lengths, most significant bit first.
function vtDash(pattern) {
  const runs = [];
  let bit = (pattern >> 15) & 1, run = 0;
  if (!bit) runs.push(0); // a dash list starts with a drawn run
  for (let i = 15; i >= 0; i--) {
    const b = (pattern >> i) & 1;
    if (b === bit) run++;
    else { runs.push(run); bit = b; run = 1; }
  }
  runs.push(run);
  return runs;
}

function vtStroke(ctx, it) {
  if (!it.line || !it.lw) return false;
  ctx.strokeStyle = it.line;
  ctx.lineWidth = it.lw;
  ctx.setLineDash(it.dash !== undefined ? vtDash(it.dash) : []);
  return true;
}

function vtFill(ctx, it, images) {
  if (it.pat) {
    const img = images.get(it.pat);
    if (!img) return false;
    ctx.fillStyle = ctx.createPattern(img, "repeat");
    return true;
  }
  if (!it.fill) return false;
  ctx.fillStyle = it.fill;
  return true;
}

// Text in a box of fixed font cells: a monospace face stretched to the cell.
function vtText(ctx, it) {
  const style = it.style || 0;
  let c = it.c || "#000000", bg = it.bg;
  if (style & 16 && bg) [c, bg] = [bg, c]; // inverted
  if (bg) { ctx.fillStyle = bg; ctx.fillRect(it.x, it.y, it.w, it.h); }
  const fw = it.fw || 8, fh = it.fh || 8;
  const perLine = Math.max(1, Math.floor(it.w / fw));
  let lines = [];
  for (const para of it.s.split(/\r\n|\n|\r/)) {
    if (!it.wrap || para.length <= perLine) { lines.push(para); continue; }
    let rest = para;
    while (rest.length > perLine) {
      let cut = rest.lastIndexOf(" ", perLine);
      if (cut <= 0) cut = perLine;
      lines.push(rest.slice(0, cut));
      rest = rest.slice(cut).replace(/^ /, "");
    }
    lines.push(rest);
  }
  lines = lines.slice(0, Math.max(1, Math.floor(it.h / fh)));
  const total = lines.length * fh;
  let y = it.vj === 1 ? it.y + (it.h - total) / 2 : it.vj === 2 ? it.y + it.h - total : it.y;
  ctx.save();
  ctx.beginPath();
  ctx.rect(it.x, it.y, it.w, it.h);
  ctx.clip();
  ctx.font = `${style & 8 ? "italic " : ""}${style & 1 ? "bold " : ""}${fh}px ui-monospace, Menlo, Consolas, monospace`;
  ctx.textBaseline = "alphabetic";
  const advance = ctx.measureText("M").width || fh / 2;
  const sx = fw / advance;
  ctx.fillStyle = c;
  ctx.strokeStyle = c;
  ctx.lineWidth = Math.max(1, fh / 12);
  for (const line of lines) {
    const width = line.length * fw;
    const x = it.hj === 1 ? it.x + (it.w - width) / 2 : it.hj === 2 ? it.x + it.w - width : it.x;
    ctx.save();
    ctx.translate(x, y + fh * 0.8);
    ctx.scale(sx, 1);
    ctx.fillText(line, 0, 0);
    ctx.restore();
    if (style & 4) { ctx.beginPath(); ctx.moveTo(x, y + fh - 1); ctx.lineTo(x + width, y + fh - 1); ctx.stroke(); }
    if (style & 2) { ctx.beginPath(); ctx.moveTo(x, y + fh / 2); ctx.lineTo(x + width, y + fh / 2); ctx.stroke(); }
    y += fh;
  }
  ctx.restore();
}

function vtItem(ctx, it, images) {
  switch (it.t) {
    case "clip":
      ctx.save();
      ctx.beginPath();
      ctx.rect(it.x, it.y, it.w, it.h);
      ctx.clip();
      break;
    case "unclip":
      ctx.restore();
      break;
    case "rect": {
      if (vtFill(ctx, it, images)) ctx.fillRect(it.x, it.y, it.w, it.h);
      if (!vtStroke(ctx, it)) break;
      // The line lies inside the rectangle.
      const h = it.lw / 2, x0 = it.x + h, y0 = it.y + h, x1 = it.x + it.w - h, y1 = it.y + it.h - h;
      const sides = it.sides === undefined ? 15 : it.sides;
      if (sides === 15) { ctx.strokeRect(x0, y0, x1 - x0, y1 - y0); break; }
      ctx.beginPath();
      if (sides & 1) { ctx.moveTo(x0, y0); ctx.lineTo(x1, y0); }
      if (sides & 2) { ctx.moveTo(x1, y0); ctx.lineTo(x1, y1); }
      if (sides & 4) { ctx.moveTo(x0, y1); ctx.lineTo(x1, y1); }
      if (sides & 8) { ctx.moveTo(x0, y0); ctx.lineTo(x0, y1); }
      ctx.stroke();
      break;
    }
    case "line":
      if (!vtStroke(ctx, it)) break;
      ctx.beginPath();
      ctx.moveTo(it.x1, it.y1);
      ctx.lineTo(it.x2, it.y2);
      ctx.stroke();
      break;
    case "ellipse": {
      // Angles count anticlockwise from 3 o'clock; the canvas's y axis points down.
      const rx = it.w / 2, ry = it.h / 2, cx = it.x + rx, cy = it.y + ry;
      const kind = it.kind || 0;
      const full = !kind || it.a0 === it.a1;
      const a0 = full ? 0 : (-it.a0 * Math.PI) / 180, a1 = full ? 2 * Math.PI : (-it.a1 * Math.PI) / 180;
      ctx.beginPath();
      if (kind === 3 && !full) ctx.moveTo(cx, cy);
      ctx.ellipse(cx, cy, Math.max(0, rx - (it.lw || 0) / 2), Math.max(0, ry - (it.lw || 0) / 2), 0, a0, a1, !full);
      if (kind !== 1) ctx.closePath();
      if (kind !== 1 && vtFill(ctx, it, images)) ctx.fill();
      if (vtStroke(ctx, it)) ctx.stroke();
      break;
    }
    case "poly": {
      const p = it.pts;
      if (p.length < 4) break;
      ctx.beginPath();
      ctx.moveTo(p[0], p[1]);
      for (let i = 2; i < p.length; i += 2) ctx.lineTo(p[i], p[i + 1]);
      if (it.closed) ctx.closePath();
      if (it.closed && vtFill(ctx, it, images)) ctx.fill("evenodd");
      if (vtStroke(ctx, it)) ctx.stroke();
      break;
    }
    case "text":
      vtText(ctx, it);
      break;
    case "image": {
      const img = images.get(it.img);
      if (img) { ctx.imageSmoothingEnabled = false; ctx.drawImage(img, it.x, it.y, it.w, it.h); }
      break;
    }
    case "hit":
      if (!it.on) { ctx.fillStyle = "rgba(128,128,128,0.5)"; ctx.fillRect(it.x, it.y, it.w, it.h); }
      if (it.sel) {
        // The focus: a frame around the field, solid while it is open for input.
        ctx.setLineDash(it.open ? [] : [4, 3]);
        ctx.strokeStyle = "#ff8c00";
        ctx.lineWidth = 2;
        ctx.strokeRect(it.x + 1, it.y + 1, it.w - 2, it.h - 2);
        ctx.setLineDash([]);
      }
      break;
    case "box":
      ctx.setLineDash([4, 3]);
      ctx.strokeStyle = "#ff00ff";
      ctx.lineWidth = 1;
      ctx.strokeRect(it.x + 0.5, it.y + 0.5, it.w - 1, it.h - 1);
      ctx.setLineDash([]);
      ctx.fillStyle = "#ff00ff";
      ctx.font = "10px sans-serif";
      ctx.fillText(it.label, it.x + 3, it.y + 12);
      break;
  }
}

function vtPaintItems(ctx, items, images) {
  ctx.save();
  for (const it of items) vtItem(ctx, it, images);
  ctx.restore();
}

function vtPaintMask(ctx, screen, images) {
  ctx.fillStyle = screen.bg || "#000000";
  ctx.fillRect(0, 0, screen.w, screen.h);
  ctx.save();
  ctx.beginPath();
  ctx.rect(0, 0, screen.w, screen.h);
  ctx.clip();
  vtPaintItems(ctx, screen.items, images);
  ctx.restore();
}

// Soft key i of the screen's key mask; an empty place (null) shows the mask's background.
function vtPaintKey(ctx, keys, i, images) {
  const key = keys.keys[i];
  ctx.fillStyle = key ? key.bg : keys.bg;
  ctx.fillRect(0, 0, keys.w, keys.h);
  if (!key) return;
  ctx.save();
  ctx.beginPath();
  ctx.rect(0, 0, keys.w, keys.h);
  ctx.clip();
  vtPaintItems(ctx, key.items, images);
  ctx.restore();
}

// The topmost touchable item at x, y (mask pixels), or null.
function vtHit(items, x, y) {
  for (let i = items.length - 1; i >= 0; i--) {
    const it = items[i];
    if (it.t === "hit" && x >= it.x && x < it.x + it.w && y >= it.y && y < it.y + it.h) return it;
  }
  return null;
}

// Virtual terminal cards: ISOBUS implements' own screens, one card per terminal (the host runs
// one to four, as displays in one cab; each implement chooses its terminal in its own menu). A
// terminal flattens its implement's object pool into draw lists (screen, soft keys, input
// editors) that the VT painter above paints; its card shows them and sends the operator's
// touches back (vt.* commands naming the terminal, which need control). The host repeats a held
// key and ends it if control goes. Hidden unless the host has terminals (its first link message
// says so, with how many).
const Vt = (() => {
  const navBtn = document.getElementById('ln-vt'), tpl = document.getElementById('vt-card-tpl');
  const SIZES = [320, 400, 480, 600];
  const PAGE = 12; // soft keys shown at once (the terminal's PhysicalKeys): two columns of six, 1-6 beside the mask
  let link = null, session = -1, images = new VtImages(); // picture ids are unique across terminals
  let audioCtx = null;
  const terms = []; // one per terminal: its card and what it shows

  // The cards to show: the link's count (one while there is no link, for the gateway editor).
  // The cards added after this browser's Connect open with the card it was pressed on.
  let openAdded = false;
  function setCount(n) {
    while (terms.length < n) {
      const term = makeTerminal(terms.length);
      terms.push(term);
      if (openAdded) term.setOpen(true);
    }
    while (terms.length > n) terms.pop().remove();
  }

  function message(m, t) {
    if (t < 0) { // the link: for every terminal
      if (m.type !== 'link') return;
      if (m.session !== session) { // new terminals: their pictures and screens start over
        session = m.session; images = new VtImages();
        for (const term of terms) term.reset();
      }
      link = m;
      navBtn.style.display = '';
      setCount(m.state === 'off' ? 1 : Math.max(1, Math.min(4, m.count || 1)));
      if (m.state !== 'off') openAdded = false;
      render();
      return;
    }
    if (terms[t]) terms[t].message(m);
  }

  function image(id, w, h, rgba) { images.add(id, w, h, rgba); }

  function render() {
    navBtn.classList.toggle('active', terms.some(term => term.open()));
    for (const term of terms) term.render();
  }

  // The nav button: closes every card when any is open, else opens them all.
  navBtn.addEventListener('pointerdown', e => {
    e.preventDefault(); e.stopPropagation();
    const any = terms.some(term => term.open());
    for (const term of terms) term.setOpen(!any);
  });

  // Control Audio Signal: the implement's beeps, as tones (activations 0 stops them).
  function playAudio(m, term) {
    clearTimeout(term.audioTimer);
    if (!m.activations) return;
    try { audioCtx = audioCtx || new (window.AudioContext || window.webkitAudioContext)(); } catch (_) { return; }
    let n = Math.min(m.activations, 20);
    const beep = () => {
      const osc = audioCtx.createOscillator(), g = audioCtx.createGain();
      osc.frequency.value = Math.max(200, Math.min(m.frequency || 1000, 8000));
      g.gain.value = 0.2;
      osc.connect(g); g.connect(audioCtx.destination);
      osc.start(); osc.stop(audioCtx.currentTime + Math.max(20, m.on || 200) / 1000);
      if (--n > 0) term.audioTimer = setTimeout(beep, Math.max(20, m.on || 200) + Math.max(20, m.off || 100));
    };
    beep();
  }

  function makeTerminal(index) {
    const card = tpl.content.firstElementChild.cloneNode(true);
    card.id = 'vt-card-' + index;
    // Each card a little below and right of the one before, so every header can be reached.
    card.style.top = 'calc(' + (70 + index * 36) + 'px + var(--sat))';
    card.style.left = 'calc(' + (80 + index * 36) + 'px + var(--sal))';
    tpl.parentNode.insertBefore(card, tpl); // beside the chart cards
    wireCardDrag(card);
    const q = sel => card.querySelector(sel);
    const maskCv = q('.vt-mask'), mctx = maskCv.getContext('2d');
    const keysEl = q('.vt-keys'), msgEl = q('.vt-msg'), editEl = q('.vt-edit'), wsSel = q('.vt-ws');
    const dot = q('.vt-dot'), nameEl = q('.vt-name'), identEl = q('.vt-ident');
    const alarmEl = q('.vt-alarm'), alarmText = q('.vt-alarm-text');
    const sizeKey = 'vt.size' + (index ? '.' + index : '');
    let screen = null, edit = null, workingSets = null, alarms = [];
    let keyPage = 0, showGateway = false, selecting = null, sizeIx = 1, identTimer = null;
    try { sizeIx = SIZES.indexOf(+localStorage.getItem(sizeKey)); if (sizeIx < 0) sizeIx = 1; } catch (_) {}
    card.style.setProperty('--vt', SIZES[sizeIx] + 'px');
    // In the card pane the mask is the largest square that fits beside its soft key columns
    // (a third of the mask wide) under the header.
    const pc = CardPane.track(card, {
      name: () => tr('VT {n}', { n: index + 1 }),
      isOpen: () => card.style.display !== 'none',
      fit(w, h) {
        if (w == null) { card.style.setProperty('--vt', SIZES[sizeIx] + 'px'); return; }
        const hdr = card.querySelector('.chart-hdr').offsetHeight || 30;
        card.style.setProperty('--vt', Math.max(120, Math.floor(Math.min((w - 12) * 3 / 4, h - hdr - 12))) + 'px');
      },
    });
    const send = (cmd, arg) => transport.send('vt.' + cmd + '|' + index + (arg !== undefined ? ',' + arg : ''));

    const term = {
      audioTimer: null,
      open: () => card.style.display !== 'none',
      setOpen(on) {
        card.style.display = on ? '' : 'none';
        navBtn.classList.toggle('active', terms.some(x => x.open()));
        CardPane.shown(pc, on);
        if (on) this.render();
      },
      reset() { screen = null; edit = null; workingSets = null; alarms = []; },
      remove() { clearTimeout(term.audioTimer); clearTimeout(identTimer); CardPane.untrack(pc); card.remove(); },
      message(m) {
        switch (m.type) {
          case 'screen': screen = m; break;
          case 'edit': onEdit(m); break;
          case 'workingSets': workingSets = m; break;
          case 'alarms': onAlarms(m.list || []); break;
          case 'audio': playAudio(m, term); return;
          case 'identify': identify(); break;
          default: return; // volume and later additions: nothing to show yet
        }
        if (term.open()) term.render();
        else navBtn.classList.toggle('active', terms.some(x => x.open()));
      },
      render,
    };

    // A new alarm reaches the operator with the card closed: the card opens, and an alarm
    // from an implement that isn't shown offers to show it.
    function onAlarms(list) {
      const before = new Set(alarms.map(a => a.ws + ':' + a.mask));
      alarms = list;
      if (list.some(a => !before.has(a.ws + ':' + a.mask)) && !term.open()) term.setOpen(true);
    }

    // Identify VT: an implement asks every terminal to show its number while the operator picks
    // one in its menu. The card opens and shows its number for 3 s.
    function identify() {
      identEl.textContent = tr('VT {n}', { n: index + 1 });
      identEl.classList.add('show');
      clearTimeout(identTimer);
      identTimer = setTimeout(() => identEl.classList.remove('show'), 3000);
      if (!term.open()) term.setOpen(true);
    }

    function onEdit(m) {
      edit = m.o === null ? null : m;
      // A boolean toggles without an editor, when this browser asked for it.
      if (edit && edit.kind === 'bool' && selecting === edit.o && iHoldControl) {
        send('number', edit.o + ',' + (edit.value ? 0 : 1));
        edit = null;
      }
      selecting = null;
    }

    function wsLabel(ws) { return tr('Implement') + ' ' + ws.address + (ws.state === 'transferring' ? ' (' + ws.progress + '%)' : ''); }

    function renderHeader() {
      nameEl.textContent = tr('VT {n}', { n: index + 1 }); // the same with one terminal or four
      const st = link ? link.state : 'off';
      dot.className = 'vt-dot ' + (st === 'connected' ? 'ok' : st === 'connecting' || st === 'busy' ? 'wait' : st === 'failed' ? 'bad' : '');
      dot.title = st === 'connected' ? (link.name || link.gateway) + (link.rtt != null ? ' · ' + link.rtt + ' ms' : '') + (link.bus ? ' · ' + link.bus : '')
        : st === 'busy' ? tr('In use by {who}', { who: link.why })
        : st === 'connecting' ? (link.adapter ? tr('Waiting for the CAN adapter on {port}', { port: link.adapter }) : tr('Connecting to {gw}', { gw: link.gatewayName || link.gateway })) + (link.why ? ' (' + link.why + ')' : '') : '';
    }

    function render() {
      renderHeader();
      if (!term.open()) return;
      // Implement picker: shown with two or more implements on this terminal.
      const list = (workingSets && workingSets.list || []).filter(w => w.selectable !== false);
      wsSel.style.display = list.length > 1 ? '' : 'none';
      if (list.length > 1) {
        wsSel.innerHTML = '';
        for (const w of list) {
          const o = document.createElement('option');
          o.value = w.name; o.textContent = wsLabel(w); o.title = w.name;
          o.selected = workingSets.active === w.name;
          wsSel.appendChild(o);
        }
      }
      // Alarm from an implement that isn't shown.
      const away = alarms.find(a => !screen || a.ws !== screen.ws);
      alarmEl.classList.toggle('show', !!away);
      if (away) { alarmText.textContent = tr('Alarm') + ': ' + tr('Implement') + ' ' + away.address; alarmEl.dataset.ws = away.ws; }

      const msg = statusMessage();
      msgEl.style.display = msg ? '' : 'none';
      if (msg) { if (msgEl.dataset.msg !== msg.key) { msgEl.dataset.msg = msg.key; msgEl.innerHTML = ''; msg.build(msgEl); } }
      else msgEl.dataset.msg = '';
      paint();
      renderEdit();
    }

    // What covers the mask instead of a screen, or null when there's a screen to show. The
    // gateway editor is on the first card only: one connection serves every terminal.
    function statusMessage() {
      const text = (t, sub) => el => {
        const d = document.createElement('div'); d.textContent = t; el.appendChild(d);
        if (sub) { const s = document.createElement('div'); s.className = 'vt-sub'; s.textContent = sub; el.appendChild(s); }
      };
      if (index === 0 && (showGateway || !link || link.state === 'off')) return { key: 'gw:' + (link ? [link.session, link.state, link.gateway, link.gatewayName, link.adapter, link.count].concat(link.ports || [], (link.gateways || []).map(g => g.name + '@' + g.address)).join('|') : ''), build: buildGateway }; // rebuilt when what it shows changes
      if (!link || link.state === 'off') return { key: 'off', build: text(tr('No gateway')) };
      if (link.state === 'failed') return { key: 'failed' + link.why, build: text(tr('The terminal could not start.'), link.why) };
      if (link.state === 'busy')
        return { key: 'busy' + link.why, build: text(tr('{gw} is in use by {who}', { gw: link.gatewayName || link.gateway, who: link.why }),
          tr('This card connects when the other terminal lets it go.')) };
      if (link.state !== 'connected')
        return { key: 'conn' + link.why, build: link.adapter
          ? text(tr('Waiting for the CAN adapter on {port}', { port: link.adapter }), link.why)
          : text(tr('Connecting to {gw}', { gw: link.gatewayName || link.gateway }), link.why) };
      if (!screen || !screen.ws || !screen.kind) {
        const ws = workingSets && workingSets.list && workingSets.list[0];
        if (ws && ws.state === 'transferring') return { key: 'up' + ws.progress, build: text(tr('Loading the implement\'s screens: {p}%', { p: ws.progress })) };
        if (ws && ws.state === 'parsing') return { key: 'parse', build: text(tr('Loading the implement\'s screens')) };
        if (ws && ws.state === 'failed') return { key: 'bad', build: text(tr('The implement\'s screens were refused.')) };
        return { key: 'none', build: text(tr('No implement'), terms.length > 1 ? tr('Choose this VT in the implement\'s own settings.') : link.name || link.gateway) };
      }
      return null;
    }

    // The gear: how the terminals reach the bus (a gateway on the network, or an slcan adapter
    // plugged into this device) and how many there are.
    function buildGateway(el) {
      const d = document.createElement('div'); d.textContent = tr('ISOBUS connection'); el.appendChild(d);
      const kind = document.createElement('select'); kind.className = 'vt-ws';
      for (const [v, label] of [['gateway', tr('Gateway on the network')], ['adapter', tr('CAN adapter on this device')]]) {
        const o = document.createElement('option'); o.value = v; o.textContent = label; kind.appendChild(o);
      }
      kind.value = link && link.adapter ? 'adapter' : 'gateway';
      if (link && link.adapters === false && !link.adapter) kind.style.display = 'none'; // no USB adapters here (iOS)
      el.appendChild(kind);
      // Gateway: one of those announcing themselves on the network, by name, or an address typed.
      const found = (link && link.gateways || []).slice();
      const gwRow = document.createElement('div'); gwRow.className = 'vt-row';
      const pick = document.createElement('select'); pick.className = 'vt-ws';
      for (const g of found) {
        const o = document.createElement('option'); o.value = g.address; o.textContent = g.name; o.dataset.name = g.name;
        pick.appendChild(o);
      }
      const other = document.createElement('option'); other.value = ''; other.textContent = tr('Other address…');
      pick.appendChild(other);
      const mine = link && link.gateway ? found.find(g => (link.gatewayName && g.name === link.gatewayName) || g.address === link.gateway) : found[0];
      pick.value = mine ? mine.address : '';
      const gwAgain = document.createElement('button'); gwAgain.className = 'fo-btn'; gwAgain.textContent = tr('Refresh');
      gwAgain.addEventListener('pointerdown', e => { e.preventDefault(); transport.send('vt.ports'); });
      gwRow.append(pick, gwAgain);
      const inp = document.createElement('input');
      inp.placeholder = '192.168.1.10'; inp.value = link && link.gateway && !mine ? link.gateway : '';
      inp.autocapitalize = 'off'; inp.spellcheck = false;
      // Adapter: its serial port, from the host's list (refreshed when the gear opens).
      const portRow = document.createElement('div'); portRow.className = 'vt-row';
      const port = document.createElement('select'); port.className = 'vt-ws';
      const ports = (link && link.ports || []).slice();
      if (link && link.adapter && !ports.includes(link.adapter)) ports.unshift(link.adapter);
      for (const p of ports) {
        const o = document.createElement('option'); o.value = p; o.textContent = p; o.selected = link && p === link.adapter;
        port.appendChild(o);
      }
      const again = document.createElement('button'); again.className = 'fo-btn'; again.textContent = tr('Refresh');
      again.addEventListener('pointerdown', e => { e.preventDefault(); transport.send('vt.ports'); });
      portRow.append(port, again);
      const sub = document.createElement('div'); sub.className = 'vt-sub';
      const show = () => {
        const adapter = kind.value === 'adapter', typed = !pick.value;
        gwRow.style.display = adapter ? 'none' : '';
        inp.style.display = adapter || !typed ? 'none' : '';
        portRow.style.display = adapter ? '' : 'none';
        sub.textContent = adapter
          ? (ports.length ? tr('An slcan adapter (CANable) on the implement bus, 250 kbit/s.') : tr('No adapter found: plug it in, then Refresh.'))
          : !typed ? tr('A gateway on this network, on the implement bus.')
          : found.length ? tr('Address of the AOW link gateway on the implement bus (port 2323).')
          : tr('No gateway found on this network. Type its address (port 2323), or Refresh.');
      };
      kind.addEventListener('change', show);
      pick.addEventListener('change', show);
      el.append(gwRow, inp, portRow, sub);
      show();
      // How many terminals: one card each, as displays in one cab.
      const countRow = document.createElement('div'); countRow.className = 'vt-row';
      const countLabel = document.createElement('span'); countLabel.textContent = tr('Terminals');
      const count = document.createElement('select'); count.className = 'vt-ws';
      const have = link && link.count || 1;
      for (let n = 1; n <= 4; n++) {
        const o = document.createElement('option'); o.value = n; o.textContent = n; o.selected = n === have;
        count.appendChild(o);
      }
      countRow.append(countLabel, count);
      el.appendChild(countRow);
      const row = document.createElement('div'); row.className = 'vt-row';
      const ok = document.createElement('button'); ok.className = 'fo-btn'; ok.textContent = tr('Connect');
      ok.addEventListener('pointerdown', e => {
        e.preventDefault();
        if (+count.value > 1) openAdded = true;
        if (+count.value !== have) transport.send('vt.count|' + count.value);
        const picked = pick.value ? pick.selectedOptions[0].dataset.name : '';
        const address = pick.value || inp.value.trim();
        if (kind.value === 'adapter') { if (port.value) transport.send('vt.adapter|' + port.value); }
        else if (+count.value === have || address !== (link && link.gateway || '') || picked !== (link && link.gatewayName || '') || (link && link.adapter))
          transport.send('vt.gateway|' + address + (picked ? '\t' + picked : ''));
        showGateway = false; msgEl.dataset.msg = ''; render();
      });
      row.appendChild(ok);
      if (link && link.state !== 'off') {
        // Disconnect: every terminal off, until Connect.
        const off = document.createElement('button'); off.className = 'fo-btn'; off.textContent = tr('Disconnect');
        off.addEventListener('pointerdown', e => { e.preventDefault(); transport.send('vt.gateway|'); showGateway = false; msgEl.dataset.msg = ''; render(); });
        row.appendChild(off);
        const cancel = document.createElement('button'); cancel.className = 'fo-btn'; cancel.textContent = tr('Cancel');
        cancel.addEventListener('pointerdown', e => { e.preventDefault(); showGateway = false; render(); });
        row.appendChild(cancel);
      }
      el.appendChild(row);
    }

    function paint() {
      if (!screen || !screen.kind) { keysEl.innerHTML = ''; return; }
      maskCv.width = screen.w; maskCv.height = screen.h;
      mctx.setTransform(1, 0, 0, 1, 0, 0);
      vtPaintMask(mctx, screen, images);
      // Soft keys: a page of PAGE at a time, with a pager when the mask holds more.
      const keys = screen.keys ? screen.keys.keys : [];
      const pages = Math.max(1, Math.ceil(keys.length / PAGE));
      if (keyPage >= pages) keyPage = 0;
      const want = PAGE + (pages > 1 ? 1 : 0);
      if (keysEl.childElementCount !== want || keysEl.dataset.pages !== String(pages)) {
        keysEl.innerHTML = ''; keysEl.dataset.pages = pages;
        for (let i = 0; i < PAGE; i++) { const c = document.createElement('canvas'); c.dataset.slot = i; keysEl.appendChild(c); wireKey(c); }
        if (pages > 1) {
          const b = document.createElement('button'); b.textContent = '⇣';
          b.addEventListener('pointerdown', e => { e.preventDefault(); keyPage = (keyPage + 1) % pages; paint(); });
          keysEl.appendChild(b);
        }
      }
      for (let i = 0; i < PAGE; i++) {
        const c = keysEl.children[i], k = keyPage * PAGE + i;
        const kw = screen.keys ? screen.keys.w : 80, kh = screen.keys ? screen.keys.h : 80;
        c.width = kw; c.height = kh;
        const kc = c.getContext('2d');
        kc.setTransform(1, 0, 0, 1, 0, 0);
        if (screen.keys && k < keys.length) vtPaintKey(kc, screen.keys, k, images);
        else { kc.fillStyle = screen.keys ? screen.keys.bg : '#808080'; kc.fillRect(0, 0, kw, kh); }
      }
    }

    // Press, then release or abort: pointer up over the same thing releases; leaving it or a
    // cancelled touch aborts. A press must always end, so a lost pointer aborts too.
    function trackPress(el, kind, o, still) {
      send('key', kind + ',' + o + ',p');
      let done = false;
      const end = state => {
        if (done) return; done = true;
        send('key', kind + ',' + o + ',' + state);
        el.removeEventListener('pointerup', up); el.removeEventListener('pointercancel', cancel);
        el.removeEventListener('pointermove', move); el.removeEventListener('lostpointercapture', cancel);
      };
      const up = e => end(still(e) ? 'r' : 'a');
      const cancel = () => end('a');
      const move = e => { if (!still(e)) end('a'); };
      el.addEventListener('pointerup', up); el.addEventListener('pointercancel', cancel);
      el.addEventListener('pointermove', move); el.addEventListener('lostpointercapture', cancel);
    }

    function wireKey(c) {
      c.addEventListener('pointerdown', e => {
        e.preventDefault();
        if (!iHoldControl || !screen || !screen.keys) return;
        const key = screen.keys.keys[keyPage * PAGE + (+c.dataset.slot)];
        if (!key) return;
        try { c.setPointerCapture(e.pointerId); } catch (_) {}
        const r = c.getBoundingClientRect();
        trackPress(c, 's', key.o, ev => ev.clientX >= r.left && ev.clientX < r.right && ev.clientY >= r.top && ev.clientY < r.bottom);
      });
    }

    function maskPoint(e) {
      const r = maskCv.getBoundingClientRect();
      return { x: (e.clientX - r.left) * screen.w / r.width, y: (e.clientY - r.top) * screen.h / r.height };
    }

    maskCv.addEventListener('pointerdown', e => {
      e.preventDefault();
      if (!iHoldControl || !screen || !screen.kind) return;
      const p = maskPoint(e);
      const hit = vtHit(screen.items, p.x, p.y);
      if (!hit || !hit.on) return;
      if (hit.kind === 'input') { selecting = hit.o; send('select', hit.o); return; }
      if (hit.kind !== 'button') return;
      try { maskCv.setPointerCapture(e.pointerId); } catch (_) {}
      trackPress(maskCv, 'b', hit.o, ev => {
        const q = maskPoint(ev);
        return q.x >= hit.x && q.x < hit.x + hit.w && q.y >= hit.y && q.y < hit.y + hit.h;
      });
    });

    // The editor for an open input field (only for the browser holding control).
    function renderEdit() {
      const show = !!edit && iHoldControl && term.open() && !(msgEl.style.display !== 'none');
      editEl.classList.toggle('show', show);
      if (!show) { editEl.dataset.o = ''; editEl.innerHTML = ''; return; }
      const key = edit.ws + ':' + edit.o + ':' + edit.kind + ':' + edit.value;
      if (editEl.dataset.o === key) return;
      editEl.dataset.o = key; editEl.innerHTML = '';
      const row = document.createElement('div'); row.className = 'vt-row';
      const btn = (label, fn) => {
        const b = document.createElement('button'); b.className = 'fo-btn'; b.textContent = label;
        b.addEventListener('pointerdown', ev => { ev.preventDefault(); fn(); }); row.appendChild(b);
      };
      const esc = () => send('escape');
      if (edit.kind === 'number') {
        const inp = document.createElement('input');
        inp.inputMode = 'decimal'; inp.value = edit.text;
        const lo = (edit.min + edit.offset) * edit.scale, hi = (edit.max + edit.offset) * edit.scale;
        const sub = document.createElement('div'); sub.className = 'vt-sub';
        sub.textContent = Math.min(lo, hi).toFixed(edit.decimals) + ' … ' + Math.max(lo, hi).toFixed(edit.decimals);
        const enter = () => {
          const shown = parseFloat(inp.value.replace(',', '.'));
          if (!isFinite(shown) || !edit.scale) return;
          const raw = Math.round(shown / edit.scale) - edit.offset;
          if (raw < edit.min || raw > edit.max) { sub.textContent = tr('Out of range') + ': ' + sub.textContent; return; }
          send('number', edit.o + ',' + raw);
        };
        inp.addEventListener('keydown', ev => { if (ev.key === 'Enter') enter(); else if (ev.key === 'Escape') esc(); });
        editEl.append(inp, sub);
        btn(tr('OK'), enter); btn(tr('Cancel'), esc);
        editEl.appendChild(row);
        setTimeout(() => { inp.focus(); inp.select(); }, 0);
      } else if (edit.kind === 'string') {
        const inp = document.createElement('input');
        inp.value = edit.value; inp.maxLength = edit.length; inp.autocapitalize = 'off'; inp.spellcheck = false;
        const enter = () => send('string', edit.o + ',' + inp.value);
        inp.addEventListener('keydown', ev => { if (ev.key === 'Enter') enter(); else if (ev.key === 'Escape') esc(); });
        editEl.appendChild(inp);
        btn(tr('OK'), enter); btn(tr('Cancel'), esc);
        editEl.appendChild(row);
        setTimeout(() => inp.focus(), 0);
      } else if (edit.kind === 'list') {
        const opts = document.createElement('div'); opts.className = 'vt-opts';
        edit.options.forEach((items, i) => {
          if (!items) return;
          const c = document.createElement('canvas');
          c.width = edit.w; c.height = edit.h;
          if (i === edit.value) c.classList.add('sel');
          const cx = c.getContext('2d');
          cx.fillStyle = '#ffffff'; cx.fillRect(0, 0, edit.w, edit.h);
          vtPaintItems(cx, items, images);
          c.addEventListener('pointerdown', ev => { ev.preventDefault(); send('number', edit.o + ',' + i); });
          opts.appendChild(c);
        });
        editEl.appendChild(opts);
        btn(tr('Cancel'), esc);
        editEl.appendChild(row);
      } else { // bool opened by the implement or another browser
        btn(edit.value ? tr('Off') : tr('On'), () => send('number', edit.o + ',' + (edit.value ? 0 : 1)));
        btn(tr('Cancel'), esc);
        editEl.appendChild(row);
      }
    }

    if (index) q('.vt-gear').style.display = 'none'; // the connection is set on VT 1
    q('.vt-x').addEventListener('pointerdown', e => { e.preventDefault(); term.setOpen(false); });
    q('.vt-gear').addEventListener('pointerdown', e => {
      e.preventDefault();
      if (!iHoldControl) return;
      showGateway = !showGateway; msgEl.dataset.msg = ''; render();
      if (showGateway) transport.send('vt.ports');
    });
    q('.vt-size').addEventListener('pointerdown', e => {
      e.preventDefault();
      sizeIx = (sizeIx + 1) % SIZES.length;
      card.style.setProperty('--vt', SIZES[sizeIx] + 'px');
      try { localStorage.setItem(sizeKey, SIZES[sizeIx]); } catch (_) {}
    });
    wsSel.addEventListener('change', () => { if (iHoldControl) send('activate', wsSel.value); });
    q('.vt-alarm-show').addEventListener('pointerdown', e => {
      e.preventDefault();
      if (iHoldControl && alarmEl.dataset.ws) send('activate', alarmEl.dataset.ws);
    });

    return term;
  }

  return { message, image, render };
})();
