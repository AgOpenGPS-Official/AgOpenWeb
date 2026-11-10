// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.Json;

namespace AgOpenWeb.WebClientScenarios.Tests;

/// <summary>A control as the screen shows it, and whether a finger can reach it.</summary>
public sealed record Control(string Group, string Name, string Title, double X, double Y, double W, double H, string How)
{
    public bool Reachable => How == "on screen" || How == "after scrolling its bar";
    public double MinSide => Math.Min(W, H);
    public string Box => string.Create(CultureInfo.InvariantCulture, $"{W:0}×{H:0} px");
}

public sealed record Readout(string Name, string Text, bool OnScreen, bool Cut);

public sealed record LayoutSnapshot(int PageWidth, int PageHeight, List<Control> Controls, List<Readout> Readouts, string Renderer, int Frames);

/// <summary>
/// The layout checks, run inside the page: every button of the main bars (status bar, left
/// nav, right nav, bottom nav, section bar, camera pad), its size, and whether a finger can land
/// on it: its centre and four points around it (at a quarter and three quarters of its width and
/// height) are tried, and it is reachable when one of them is on the screen and not under
/// something else. What only passes over the screen for a moment (the toast, the map-tap hint) is
/// looked through, not counted as covering. A control off the screen counts as reachable when
/// the bar holding it scrolls (a finger can scroll it there); in a bar that doesn't scroll, it
/// can't be reached at all. A covered control is reported with what covers its centre (its id,
/// or its text and the nearest element with an id around it). The same check runs over the
/// controls of an open menu panel.
/// </summary>
public static class Layout
{
    /// <summary>A finger on a moving cab's screen needs at least this much (CSS px, the smaller side).
    /// Apple asks for 44 pt, Material for 48 dp on a phone held still.</summary>
    public const double MinTouchPx = 40;

    public static readonly (string Group, string Selector)[] Bars =
    {
        ("status bar", "#statusbar"), ("left nav", "#leftnav"), ("right nav", "#rightnav"),
        ("bottom nav", "#bottomnav"), ("section bar", "#sectionbar"), ("camera pad", "#campad"),
    };

    /// <summary>The readouts an operator glances at: role, speed, fix, field and job line.</summary>
    public static readonly (string Name, string Selector)[] Readouts =
    {
        ("role", "#sb-roletext"), ("speed", "#sb-speed"), ("heading", "#sb-hdg"), ("GPS fix", "#sb-fix"),
        ("field / job line", "#sb-rot"), ("cross-track", "#hud-top"),
    };

    // The reach test, shared by the bars and the panels. Installed once per page.
    private const string Helpers = @"
    if (!window.__web) window.__web = (() => {
      const shown = el => {
        if (!el) return false;
        const cs = getComputedStyle(el);
        if (cs.display === 'none' || cs.visibility === 'hidden' || +cs.opacity === 0) return false;
        const r = el.getBoundingClientRect(); return r.width > 0 && r.height > 0;
      };
      const name = el => el.id ? '#' + el.id
        : (el.getAttribute('data-cmd') ? '[' + el.getAttribute('data-cmd') + ']'
        : (el.title || el.getAttribute('aria-label') || el.textContent || el.placeholder || el.name || el.tagName).trim().replace(/\s+/g, ' ').slice(0, 32));
      const scroller = el => {
        for (let p = el.parentElement; p && p !== document.body; p = p.parentElement) {
          const cs = getComputedStyle(p);
          if ((/(auto|scroll)/.test(cs.overflowY) && p.scrollHeight > p.clientHeight + 1)
           || (/(auto|scroll)/.test(cs.overflowX) && p.scrollWidth > p.clientWidth + 1)) return p;
        }
        return null;
      };
      // Overlays that come and go by themselves: looked through, not counted as covering.
      const TRANSIENT = '#toast, #maptap-hint';
      const cover = h => {
        const n = name(h);
        const around = h.id ? null : (h.parentElement && h.parentElement.closest('[id]'));
        return around ? n + ' (in #' + around.id + ')' : n;
      };
      // The top element at a point, transient overlays looked through.
      const top = (x, y) => document.elementsFromPoint(x, y).find(h => !h.closest(TRANSIENT)) || null;
      const at = el => {
        const r = el.getBoundingClientRect();
        const pts = [[0.5, 0.5], [0.25, 0.25], [0.75, 0.25], [0.25, 0.75], [0.75, 0.75]]
          .map(([fx, fy]) => [r.left + r.width * fx, r.top + r.height * fy])
          .filter(([x, y]) => x >= 0 && y >= 0 && x < innerWidth && y < innerHeight);
        if (pts.length === 0) return { on: false };
        let by = '';
        for (const [x, y] of pts) {
          const h = top(x, y);
          if (h && (h === el || el.contains(h))) return { on: true, hit: true, by: '' };
          if (!by && h) by = cover(h);
        }
        return { on: true, hit: false, by };
      };
      const reach = el => {
        let a = at(el);
        if (a.on && a.hit) return 'on screen';
        const sc = scroller(el);
        if (sc) {
          const saved = [];
          for (let p = el.parentElement; p; p = p.parentElement) saved.push([p, p.scrollTop, p.scrollLeft]);
          const r = el.getBoundingClientRect(), pr = sc.getBoundingClientRect();
          sc.scrollTop += (r.top + r.height / 2) - (pr.top + pr.height / 2);
          sc.scrollLeft += (r.left + r.width / 2) - (pr.left + pr.width / 2);
          const b = at(el);
          for (const [p, t, l] of saved) { p.scrollTop = t; p.scrollLeft = l; }
          if (b.on && b.hit) return 'after scrolling its bar';
          return b.on ? 'covered by ' + b.by + ' even after scrolling its bar' : 'off the screen even after scrolling its bar';
        }
        return a.on ? 'covered by ' + a.by : 'off the screen (its bar does not scroll)';
      };
      const describe = (group, el) => {
        const r = el.getBoundingClientRect();
        return { group, name: name(el), title: (el.title || el.textContent || '').trim().replace(/\s+/g, ' ').slice(0, 40),
                 x: r.left, y: r.top, w: r.width, h: r.height, how: reach(el) };
      };
      return { shown, name, reach, describe };
    })();";

    public static async Task<LayoutSnapshot> MeasureAsync(Screen s)
    {
        var bars = Bars.Select(b => new[] { b.Group, b.Selector }).ToArray();
        var reads = Readouts.Select(b => new[] { b.Name, b.Selector }).ToArray();
        var e = await s.EvalAsync<JsonElement>(@"([bars, reads]) => {" + Helpers + @"
          const W = window.__web, controls = [];
          for (const [g, sel] of bars) {
            const root = document.querySelector(sel);
            if (!W.shown(root)) continue;
            for (const el of root.querySelectorAll('button')) if (W.shown(el)) controls.push(W.describe(g, el));
          }
          const readouts = [];
          for (const [n, sel] of reads) {
            const el = document.querySelector(sel);
            if (!W.shown(el)) { readouts.push({ name: n, text: '', on: false, cut: false, missing: true }); continue; }
            const r = el.getBoundingClientRect();
            const on = r.left >= -1 && r.top >= -1 && r.right <= innerWidth + 1 && r.bottom <= innerHeight + 1;
            const cut = el.scrollWidth > el.clientWidth + 1 && getComputedStyle(el).overflowX !== 'visible';
            readouts.push({ name: n, text: el.textContent.trim().replace(/\s+/g, ' ').slice(0, 60), on, cut });
          }
          const de = document.documentElement;
          return { pw: Math.max(de.scrollWidth, document.body.scrollWidth), ph: Math.max(de.scrollHeight, document.body.scrollHeight),
                   controls, readouts, renderer: (typeof ckStatus !== 'undefined' ? String(ckStatus) : ''),
                   frames: (typeof fps !== 'undefined' ? Math.round(fps) : -1) };
        }", new object[] { bars, reads });
        return new LayoutSnapshot(
            e.GetProperty("pw").GetInt32(), e.GetProperty("ph").GetInt32(),
            Controls(e.GetProperty("controls")),
            e.GetProperty("readouts").EnumerateArray().Select(r => new Readout(r.GetProperty("name").GetString()!,
                r.GetProperty("text").GetString() ?? "", r.GetProperty("on").GetBoolean(), r.GetProperty("cut").GetBoolean())).ToList(),
            e.GetProperty("renderer").GetString() ?? "", e.GetProperty("frames").GetInt32());
    }

    /// <summary>The menu panel open now (its id) and the reach of every control in it, or null when none is open.</summary>
    public static async Task<(string Id, double X, double Y, double W, double H, List<Control> Controls)?> OpenPanelAsync(Screen s)
    {
        var e = await s.EvalAsync<JsonElement>(@"() => {" + Helpers + @"
          const W = window.__web;
          const ids = (typeof LN_NAV_PANELS !== 'undefined' ? LN_NAV_PANELS : []).filter(id => document.getElementById(id).classList.contains('open'));
          if (ids.length === 0) return null;
          const p = document.getElementById(ids[ids.length - 1]);
          const r = p.getBoundingClientRect(), controls = [];
          for (const el of p.querySelectorAll('button, input, select, textarea')) if (W.shown(el)) controls.push(W.describe('panel', el));
          return { id: p.id, x: r.left, y: r.top, w: r.width, h: r.height, controls };
        }");
        if (e.ValueKind != JsonValueKind.Object) return null;
        return (e.GetProperty("id").GetString()!, e.GetProperty("x").GetDouble(), e.GetProperty("y").GetDouble(),
                e.GetProperty("w").GetDouble(), e.GetProperty("h").GetDouble(), Controls(e.GetProperty("controls")));
    }

    private static List<Control> Controls(JsonElement arr) => arr.EnumerateArray().Select(c => new Control(
        c.GetProperty("group").GetString()!, c.GetProperty("name").GetString()!, c.GetProperty("title").GetString() ?? "",
        c.GetProperty("x").GetDouble(), c.GetProperty("y").GetDouble(), c.GetProperty("w").GetDouble(), c.GetProperty("h").GetDouble(),
        c.GetProperty("how").GetString()!)).ToList();
}
