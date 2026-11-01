// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0
//
// Web cards.

// ---- Web cards ----
// Add-ons' own web pages in cards (Plans/ADDON_WEB_CARDS_PLAN.md), apart from the VT cards. The
// bookmarks (name + URL) are the host's, the same on every browser; which cards are open, their
// page, size and place are this browser's (localStorage). {host} in a URL is the AgOpenWeb host
// as this browser reaches it. The page loads from the add-on itself, on its own origin. A browser
// without control sees the page under a cover that takes the taps (#397).
const WebCards = (() => {
  const tpl = document.getElementById('web-card-tpl'), navBtn = document.getElementById('ln-web');
  const SIZES = [[480, 320], [640, 420], [800, 520], [1000, 640]], MAX = 4, KEY = 'web.cards';
  let bookmarks = [], cards = [], shown = false;

  const resolve = url => url.split('{host}').join(location.hostname);
  function save() {
    try {
      localStorage.setItem(KEY, JSON.stringify({ shown, cards: cards.map(c => c.state) }));
    } catch (_) {}
  }

  function makeCard(state) {
    const card = tpl.content.firstElementChild.cloneNode(true);
    const n = cards.length;
    card.style.top = state.y != null ? state.y + 'px' : 'calc(' + (70 + n * 36) + 'px + var(--sat))';
    card.style.left = state.x != null ? state.x + 'px' : 'calc(' + (80 + n * 36) + 'px + var(--sal))';
    tpl.parentNode.insertBefore(card, tpl);
    wireCardDrag(card);
    raiseCard(card);
    const q = s => card.querySelector(s);
    const pick = q('.web-pick'), frame = q('iframe'), cover = q('.web-cover'), msg = q('.web-msg'), editEl = q('.web-edit');
    const c = { card, state, editing: false, editIx: null };
    const applySize = () => {
      const [w, h] = SIZES[state.size] || SIZES[1];
      card.style.setProperty('--ww', w + 'px'); card.style.setProperty('--wh', h + 'px');
    };
    applySize();
    // In the card pane the page fills the card's share of it.
    c.pc = CardPane.track(card, {
      name: () => (state.url && pick.selectedOptions[0] ? pick.selectedOptions[0].textContent : tr('Web')),
      isOpen: () => cards.includes(c),
      fit(w, h) {
        if (w == null) { applySize(); return; }
        const hdr = card.querySelector('.chart-hdr').offsetHeight || 30;
        const hint = card.querySelector('.web-hint').offsetHeight;
        card.style.setProperty('--ww', Math.floor(w - 2) + 'px'); card.style.setProperty('--wh', Math.floor(h - hdr - hint - 2) + 'px');
      },
    });

    c.render = () => {
      // The picker: the bookmarks, the card's page chosen.
      const opts = [['', tr('Choose a page')]].concat(bookmarks.map(b => [b.url, b.name]));
      const sig = JSON.stringify(opts);
      if (pick.dataset.sig !== sig) {
        pick.dataset.sig = sig; pick.innerHTML = '';
        for (const [v, t] of opts) { const o = document.createElement('option'); o.value = v; o.textContent = t; pick.appendChild(o); }
      }
      pick.value = bookmarks.some(b => b.url === state.url) ? state.url : '';
      const src = state.url ? resolve(state.url) : '';
      if (frame.dataset.src !== src) { frame.dataset.src = src; frame.src = src || 'about:blank'; }
      msg.textContent = !bookmarks.length ? tr('No pages yet. Add one with ⚙.') : tr('Choose a page above.');
      msg.style.display = state.url ? 'none' : '';
      cover.classList.toggle('show', !iHoldControl);
      q('.web-add').style.display = cards.length < MAX ? '' : 'none';
      q('.web-ext').style.display = state.url ? '' : 'none';
      q('.web-hint').hidden = !state.url;
      if (c.editing && !iHoldControl) c.editing = false;
      editEl.classList.toggle('show', c.editing);
      if (c.editing) renderEdit(c, editEl);
    };

    pick.addEventListener('change', () => { state.url = pick.value; save(); c.render(); CardPane.renamed(); });
    // Another card, a little below and right of this one, so both headers can be reached.
    q('.web-add').addEventListener('pointerdown', e => {
      e.preventDefault();
      const r = zoomRect(card);
      add({ url: '', size: state.size, x: r.left / uiZoom + 36, y: r.top / uiZoom + 36 });
    });
    q('.web-ext').addEventListener('pointerdown', e => { e.preventDefault(); if (state.url) window.open(resolve(state.url), '_blank'); });
    q('.web-size').addEventListener('pointerdown', e => {
      e.preventDefault(); state.size = ((state.size | 0) + 1) % SIZES.length; applySize(); save();
    });
    q('.web-gear').addEventListener('pointerdown', e => {
      e.preventDefault();
      if (!iHoldControl) return; // the bookmarks change for every browser
      c.editing = !c.editing; c.editIx = null; c.render();
    });
    q('.web-x').addEventListener('pointerdown', e => {
      e.preventDefault();
      card.remove(); cards = cards.filter(x => x !== c);
      CardPane.untrack(c.pc);
      if (!cards.length) shown = false;
      save(); render();
    });
    // Where the card was dragged to (wireCardDrag sets left/top in the card's own pixels).
    q('.chart-hdr').addEventListener('pointerup', () => {
      const x = parseFloat(card.style.left), y = parseFloat(card.style.top);
      if (Number.isFinite(x) && Number.isFinite(y) && (x !== state.x || y !== state.y)) { state.x = x; state.y = y; save(); }
    });
    return c;
  }

  // The bookmark editor over the card's page: the list, and a form for a new or changed one.
  function renderEdit(c, el) {
    el.innerHTML = '';
    const btn = (text, fn) => {
      const b = document.createElement('button'); b.textContent = text;
      b.addEventListener('pointerdown', e => { e.preventDefault(); e.stopPropagation(); fn(); });
      return b;
    };
    bookmarks.forEach((b, i) => {
      const row = document.createElement('div'); row.className = 'web-row';
      const t = document.createElement('span'); t.textContent = b.name + ' ';
      const u = document.createElement('small'); u.textContent = b.url; t.appendChild(u);
      row.append(t,
        btn(tr('Edit'), () => { c.editIx = i; c.render(); }),
        btn(tr('Delete'), () => showConfirm(tr('Delete Bookmark'), tr('Delete "{name}"?', { name: b.name }),
          () => transport.send('webcard.delete|' + i))));
      el.appendChild(row);
    });
    const editing = c.editIx != null ? bookmarks[c.editIx] : null;
    const name = document.createElement('input'); name.placeholder = tr('Name'); name.maxLength = 40;
    const url = document.createElement('input'); url.placeholder = 'http://{host}/'; url.maxLength = 500;
    url.autocapitalize = 'off'; url.spellcheck = false; url.setAttribute('translate', 'no');
    if (editing) { name.value = editing.name; url.value = editing.url; }
    const sub = document.createElement('div'); sub.className = 'web-sub';
    sub.textContent = tr('{host} is this AgOpenWeb\'s own address, for an add-on on the same computer.');
    const row = document.createElement('div'); row.className = 'web-row';
    row.append(
      btn(editing ? tr('Save') : tr('Add'), () => {
        if (!name.value.trim() || !url.value.trim()) return;
        transport.send('webcard.save|' + (editing ? c.editIx : -1) + '\t' + name.value.trim().replace(/\t/g, ' ') + '\t' + url.value.trim());
        c.editIx = null; name.value = ''; url.value = '';
        document.activeElement?.blur(); // the list redraws when the host sends it back
      }),
      btn(editing ? tr('Cancel') : tr('Done'), () => { if (editing) c.editIx = null; else c.editing = false; c.render(); }));
    el.append(name, url, sub, row);
  }

  function add(state) {
    if (cards.length >= MAX) return;
    const c = makeCard(state);
    cards.push(c); shown = true;
    save(); render();
    CardPane.shown(c.pc, true);
  }

  function render() {
    navBtn.classList.toggle('active', shown && cards.length > 0);
    for (const c of cards) c.render();
  }

  function setBookmarks(list) {
    bookmarks = list;
    // An editor open on a bookmark that moved or went: back to the list.
    for (const c of cards) if (c.editIx != null && c.editIx >= list.length) c.editIx = null;
    for (const c of cards) if (!c.editing || document.activeElement?.closest('.web-edit') !== c.card.querySelector('.web-edit')) c.render();
    CardPane.renamed(); // the tabs show the pages' names
  }

  // The nav button: hides every web card when any is open, else brings back the last ones.
  navBtn.addEventListener('pointerdown', e => {
    e.preventDefault(); e.stopPropagation();
    if (shown && cards.length) {
      shown = false;
      save(); // kept for the next tap
      for (const c of cards) { c.card.remove(); CardPane.untrack(c.pc); }
      cards = [];
      render();
      return;
    }
    let last = [];
    try { last = (JSON.parse(localStorage.getItem(KEY) || '{}').cards) || []; } catch (_) {}
    if (!last.length) last = [{ url: '', size: 1 }];
    for (const s of last.slice(0, MAX)) add(s);
  });

  // Cards that were open when this browser last left.
  try {
    const st = JSON.parse(localStorage.getItem(KEY) || '{}');
    if (st.shown) for (const s of (st.cards || []).slice(0, MAX)) add(s);
  } catch (_) {}

  return { setBookmarks, render };
})();
