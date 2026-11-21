// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using AgOpenWeb.Services.Threading;

namespace AgOpenWeb.WebClientScenarios.Tests;

/// <summary>
/// Who stood still, for a finding's evidence: the deadman trips when a page sends no presence for
/// 1.5 s, and that is the page stalling, the test process (the host) stalling, or neither (then
/// it is the app's). C12 on 21 Nov 2026 lost control 2.7 s into the work with the link up, once in
/// nine nights, and nothing in the report said which. Three watches, each a timer asked to run
/// every <see cref="TickMs"/> ms and noting when it ran more than <see cref="GapMs"/> ms late:
/// <list type="bullet">
/// <item><b>page</b>: a timer in every page the harness opens (an init script; the page says so on
/// its console when it runs again). The page's own note waits for 5 s (transport.js).</item>
/// <item><b>process</b>: a thread in the test process: the whole process (GC, the runner's CPU).</item>
/// <item><b>host loop</b>: an empty item posted to the host loop: the VM's thread busy.</item>
/// </list>
/// Times are the harness clock (<see cref="ModuleListener.NowMs"/>), the one the module's PGN 254
/// and the control changes are kept in; a page's gap is placed by the wall clock it shares.
/// </summary>
public static class StallWatch
{
    public const int TickMs = 100, GapMs = 250;

    /// <summary>The page's watch, run before the page's own scripts.</summary>
    public static readonly string PageScript = $@"(() => {{
  let at = Date.now();
  setInterval(() => {{
    const now = Date.now(), gap = now - at - {TickMs};
    at = now;
    if (gap > {GapMs}) console.debug('[aow-stall] ' + gap + ' ' + now + ' ' + (document.hidden ? 'hidden' : 'shown'));
  }}, {TickMs});
}})();";

    public readonly record struct Stall(long EndMs, long GapMs, string Where);

    private static readonly List<Stall> Stalls = new();
    private static readonly long WallAtZero = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - ModuleListener.NowMs;
    private static int _started;

    private static void Add(Stall s) { lock (Stalls) { Stalls.Add(s); if (Stalls.Count > 20000) Stalls.RemoveRange(0, 10000); } }

    /// <summary>A line from a page's console: true when it was the watch's (and is kept).</summary>
    public static bool FromPage(string screen, string text)
    {
        if (!text.StartsWith("[aow-stall] ", StringComparison.Ordinal)) return false;
        var p = text.Split(' ');
        if (p.Length >= 4 && long.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long gap)
            && long.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long wall))
            Add(new Stall(wall - WallAtZero, gap, "page " + screen + " (" + p[3] + ")"));
        return true;
    }

    /// <summary>Starts the process and host-loop watches (once a run).</summary>
    public static void Start(HostLoopDispatcher hostLoop)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        var process = new Thread(() =>
        {
            long at = ModuleListener.NowMs;
            while (true)
            {
                Thread.Sleep(TickMs);
                long now = ModuleListener.NowMs, gap = now - at - TickMs;
                at = now;
                if (gap > GapMs) Add(new Stall(now, gap, "test process"));
            }
        }) { IsBackground = true, Name = "stall-watch" };
        process.Start();
        _ = Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(TickMs);
                long asked = ModuleListener.NowMs;
                try { await hostLoop.InvokeAsync(() => { }); } catch { return; } // the loop is gone: the run is over
                long late = ModuleListener.NowMs - asked;
                if (late > GapMs) Add(new Stall(ModuleListener.NowMs, late, "host loop"));
            }
        });
    }

    /// <summary>The stalls since <paramref name="fromMs"/>, one a line, for the evidence.</summary>
    public static string Text(long fromMs)
    {
        List<Stall> list;
        lock (Stalls) list = Stalls.Where(s => s.EndMs >= fromMs).OrderBy(s => s.EndMs).ToList();
        var inv = CultureInfo.InvariantCulture;
        var lines = new List<string>
        {
            "# Timers that ran late, from the scenario's start (harness ms). A timer asked for every "
                + TickMs.ToString(inv) + " ms; listed when it ran more than " + GapMs.ToString(inv) + " ms late.",
            "# page <screen>: a timer in that page; test process: a thread in the test process; host loop: an item on the VM's thread.",
            "# Each line: when it ran again, how late, and where. The deadman trips after 1500 ms without presence.",
        };
        if (list.Count == 0) lines.Add("# none");
        lines.AddRange(list.Select(s => string.Create(inv, $"{s.EndMs - fromMs,8} ms  {s.GapMs,6} ms late  {s.Where}")));
        return string.Join("\n", lines) + "\n";
    }
}
