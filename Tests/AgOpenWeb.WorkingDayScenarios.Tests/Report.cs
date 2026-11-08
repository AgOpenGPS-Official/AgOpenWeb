// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;

namespace AgOpenWeb.WorkingDayScenarios.Tests;

public enum Severity { Safety, Major, Minor }

public sealed record Finding(string Code, Severity Severity, string Title, string Detail);

/// <summary>
/// Turns the day into findings with stable codes (judged by fixed rules, below) and a Markdown
/// report: the findings first, most serious first, each marked New or Already reported
/// (<c>KnownFindings.txt</c>), then the day's timeline, the jobs, the baselines, the heap census,
/// the process every half hour, the browsers, the disk and what AgOpenWeb logged.
/// </summary>
public static class Report
{
    private static string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ the rules

    public static List<Finding> Judge(Day d)
    {
        var list = new List<Finding>();
        var ref0 = d.Baselines.FirstOrDefault(b => b.Code.StartsWith("B-J", StringComparison.Ordinal));
        var end = d.Baselines.LastOrDefault(b => b.Code == "B-END");
        // What the phone in the pocket left (MAP-FROZEN-BY-A-PHONE) is judged there: the heap's
        // growth over the day is counted from the first baseline after the phone read again,
        // when that leaves an hour of the day to count.
        var refHeap = ref0;
        if (d.PhoneWokeAt is { } woke && ref0 != null && ref0.Clock < woke
            && d.Baselines.FirstOrDefault(b => b.Clock >= woke && b.Code.StartsWith("B-J", StringComparison.Ordinal)) is { } afterPhone
            && end != null && (end.Clock - afterPhone.Clock).TotalHours >= 1)
            refHeap = afterPhone;

        // Memory in the same state (no field open, browsers connected) hours apart.
        if (ref0 != null && end != null)
        {
            double hh = Math.Max(0.5, (end.Clock - refHeap!.Clock).TotalHours);
            double dh = end.P.HeapMB - refHeap.P.HeapMB;
            if (dh > 8 && dh > 0.15 * refHeap.P.HeapMB)
                list.Add(new Finding("HEAP-GROWS-OVER-THE-DAY", Severity.Major,
                    F($"The managed heap grew {dh:0.0} MB in {hh:0.0} h with nothing open ({dh / hh:0.0} MB an hour)"),
                    F($"After a full collection, with no field open and the browsers connected: {refHeap.P.HeapMB:0.0} MB at {refHeap.Clock:HH:mm} ({refHeap.What}), {end.P.HeapMB:0.0} MB at {end.Clock:HH:mm} ({end.What}). ")
                    + (refHeap == ref0 && d.PhoneStalledAt != null && d.PhoneStalledAt > ref0.Clock ? "The phone in the pocket (`MAP-FROZEN-BY-A-PHONE`) is inside this span. " : "")
                    + CensusLine(d)));
            double hours = Math.Max(0.5, (end.Clock - ref0.Clock).TotalHours);
            double dn = end.P.NativeMB - ref0.P.NativeMB;
            if (dn > 40)
                list.Add(new Finding("NATIVE-MEMORY-GROWS", Severity.Minor,
                    F($"Native memory (resident, not the GC's) grew {dn:0} MB in {hours:0.0} h"),
                    F($"Anonymous resident memory minus what the GC holds: {ref0.P.NativeMB:0} -> {end.P.NativeMB:0} MB (resident {ref0.P.RssMB:0} -> {end.P.RssMB:0} MB, GC committed {ref0.P.CommittedMB:0} -> {end.P.CommittedMB:0} MB), same state.")));
            int dt = end.P.Threads - ref0.P.Threads;
            if (dt > 8)
                list.Add(new Finding("THREADS-GROW", Severity.Minor, F($"{dt} more threads at the end of the day than after the first field"),
                    F($"{ref0.P.Threads} -> {end.P.Threads} OS threads ({ref0.P.PoolThreads} -> {end.P.PoolThreads} in the thread pool), same state.")));
            long dtm = end.P.Timers - ref0.P.Timers;
            if (dtm > 10)
                list.Add(new Finding("TIMERS-GROW", Severity.Minor, F($"{dtm} more timers alive at the end of the day than after the first field"),
                    F($"System.Threading timers: {ref0.P.Timers} -> {end.P.Timers}, same state (each runs its callback until disposed). ") + TimerLine(d)));
            int df = end.P.Files - ref0.P.Files;
            if (df > 3)
                list.Add(new Finding("FILES-LEFT-OPEN", Severity.Minor, F($"{df} more files held open at the end of the day than after the first field"),
                    F($"{ref0.P.Files} -> {end.P.Files} open files. ") + string.Join(", ", d.OpenFilesAtEnd.Take(10))));
            int ds = end.P.Sockets - ref0.P.Sockets;
            if (ds > 3)
                list.Add(new Finding("SOCKETS-LEFT-OPEN", Severity.Minor, F($"{ds} more sockets open at the end of the day than after the first field"),
                    F($"{ref0.P.Sockets} -> {end.P.Sockets} sockets, the same two browsers connected (the tablet and phone reconnected during the day).")));
        }

        // An hour parked with the field open: nothing is worked, nothing should grow.
        if (d.LunchStart != null && d.LunchEnd != null)
        {
            double hours = Math.Max(0.1, (d.LunchEndClock - d.LunchStartClock).TotalHours);
            double dh = d.LunchEnd.HeapMB - d.LunchStart.HeapMB;
            if (dh > 3)
                list.Add(new Finding("HEAP-GROWS-WHILE-PARKED", Severity.Major,
                    F($"The managed heap grew {dh:0.0} MB during lunch with nothing worked ({dh / hours:0.0} MB an hour)"),
                    F($"Parked with the field open, GPS at 10 Hz, NTRIP flowing, no browser: {d.LunchStart.HeapMB:0.0} -> {d.LunchEnd.HeapMB:0.0} MB after full collections.")));
            var grown = FilesGrown(d, "lunch-start", "lunch-end").Where(g => g.Delta > 20_000).ToList();
            if (grown.Count > 0)
                list.Add(new Finding("FILE-GROWS-WHILE-PARKED", Severity.Minor,
                    F($"{grown.Count} file(s) grew during lunch with nothing worked"),
                    string.Join("; ", grown.Take(6).Select(g => F($"{g.Path} +{g.Delta / 1024.0:0} KB ({g.Delta / 1024.0 / hours:0} KB an hour)")))));
        }

        // The same field, the same work, morning and afternoon.
        var j1 = d.Jobs.FirstOrDefault(j => j.Job.Code == "J1");
        var j3 = d.Jobs.FirstOrDefault(j => j.Job.Code == "J3");
        if (j1 != null && j3 != null && j1.Cycle.Count > 1000 && j3.Cycle.Count > 1000)
        {
            double r50 = j3.Cycle.P(50) / j1.Cycle.P(50), r95 = j3.Cycle.P(95) / j1.Cycle.P(95);
            if ((r50 > 1.3 || r95 > 1.3) && j3.Cycle.P(95) - j1.Cycle.P(95) > 0.3)
                list.Add(new Finding("GPS-CYCLE-SLOWER-LATER", Severity.Major,
                    F($"The GPS cycle on Olival took {r95:0.0}x as long in the afternoon as in the morning"),
                    F($"Same field, same AB line, same machine: J1 at {j1.Start:HH:mm} p50 {j1.Cycle.P(50):0.000} ms, p95 {j1.Cycle.P(95):0.000} ms; J3 at {j3.Start:HH:mm} p50 {j3.Cycle.P(50):0.000} ms, p95 {j3.Cycle.P(95):0.000} ms.")));
            double t99 = j3.Tick.P(99) / j1.Tick.P(99);
            if (t99 > 1.3 && j3.Tick.P(99) - j1.Tick.P(99) > 0.2)
                list.Add(new Finding("CONTROL-TICK-SLOWER-LATER", Severity.Major,
                    F($"The 100 Hz control tick on Olival took {t99:0.0}x as long (p99) in the afternoon as in the morning"),
                    F($"J1 p99 {j1.Tick.P(99):0.000} ms, J3 p99 {j3.Tick.P(99):0.000} ms.")));
        }

        if (d.CycleDay.Over100Ms > 0)
            list.Add(new Finding("GPS-CYCLE-OVER-100-MS", Severity.Major,
                F($"{d.CycleDay.Over100Ms} GPS cycle(s) took longer than the 100 ms between two fixes (longest {d.CycleDay.MaxMs:0} ms; {d.SlowCyclesWithGc} with a garbage collection in it)"),
                "The next fix waits: guidance and the steer angle are a fix late. " + Moments(d, "GPS cycle", 100)));
        long slowTicks = d.TickDay.CountOver(10);
        if (slowTicks > 0)
            list.Add(new Finding("CONTROL-TICK-OVER-10-MS", Severity.Minor,
                F($"{slowTicks} control tick(s) took longer than the 10 ms tick period (longest {d.TickDay.MaxMs:0.0} ms; {d.SlowTicksWithGc} with a garbage collection in it)"),
                "The section and steer loop runs every 10 ms (`SteerMachineLoopService`); a longer tick delays the PGNs to the modules and the section timing. " + Moments(d, "control tick", 10)));

        var slowHost = d.Samples.Where(s => s.HostLoopMaxMs > 250).ToList();
        var heldLoop = d.Said.Where(k => k.Key.Contains("held the loop", StringComparison.Ordinal)).ToList();
        if (slowHost.Count > 0 || heldLoop.Count > 0)
            list.Add(new Finding("HOST-LOOP-SLOW", Severity.Minor,
                F($"The host loop (where commands and the GPS results run) answered late {slowHost.Count} time(s) in the day"),
                string.Join(", ", slowHost.Take(6).Select(s => F($"{s.Clock:HH:mm} {s.HostLoopMaxMs:0} ms ({s.Phase})")))
                + (heldLoop.Count > 0 ? " Logged: " + string.Join(" | ", heldLoop.Take(4).Select(k => F($"{k.Value.Count}x {k.Value.Text}"))) : "")));

        // The browsers.
        if (d.PhoneStalledAt != null && d.PhoneStallMaxGapMs > 3000)
        {
            var after = d.Baselines.FirstOrDefault(b => b.Clock >= (d.PhoneWokeAt ?? DateTime.MaxValue) && b.Code.StartsWith("B-", StringComparison.Ordinal));
            var before = d.Baselines.LastOrDefault(b => b.Clock <= d.PhoneStalledAt);
            var reconnect = d.Tablet.Seeds.FirstOrDefault(s => s.Note != null);
            list.Add(new Finding("MAP-FROZEN-BY-A-PHONE", Severity.Major,
                F($"While a phone with the page open was in a pocket, every browser's map stopped (the tablet waited {d.PhoneStallMaxGapMs / 1000:0} s for an update)"),
                F($"At {d.PhoneStalledAt:HH:mm} the observer phone stopped reading (its connection open, as a phone with the screen off or carried out of Wi-Fi range). ")
                + (d.MapFrozeAt != null ? F($"{Math.Max(0, d.MapFrozeAfterWallS):0} s of wall time later the tablet stopped getting map updates. ") : "")
                + "The host sends every frame to every browser and waits for each send (`WebSocketHub.BroadcastAsync` awaits all clients, `MapBroadcaster.RunAsync` awaits each broadcast), so once that phone's socket buffers were full the 10 Hz broadcast loop stopped for everyone"
                + (d.PhoneWokeAt != null ? F($" until the phone read again at {d.PhoneWokeAt:HH:mm}.") : " for the rest of the day.")
                + (reconnect != null ? F($" The tablet reconnecting meanwhile {(double.IsNaN(reconnect.FirstTickMs) ? "got no map update at all" : F($"waited {reconnect.FirstTickMs / 1000:0} s for its first map update"))} (seed {reconnect.Bytes / 1024.0:0} KB).") : "")
                + " The coverage the broadcast loop drains from `CoverageMapService` at 10 Hz piles up while it is stuck"
                + (before != null && after != null ? F($" (heap {before.P.HeapMB:0.0} MB at `{before.Code}` before, {after.P.HeapMB:0.0} MB at `{after.Code}` after, the same state)") : "")
                + "; the deadman runs in the same loop (`DEADMAN-BLOCKED-BY-A-PHONE`)."));
        }
        if (d.Deadman is { } dm && (double.IsNaN(dm.OffMs) || dm.OffMs > 3000))
            list.Add(new Finding("DEADMAN-DID-NOT-STOP-AUTOSTEER", Severity.Safety,
                double.IsNaN(dm.OffMs)
                    ? "When the operator's tablet went silent with AutoSteer on, AutoSteer stayed engaged"
                    : F($"When the operator's tablet went silent with AutoSteer on, AutoSteer stayed engaged {dm.OffMs / 1000:0.0} s"),
                F($"At {dm.At:HH:mm} the tablet's Wi-Fi dropped without a word (its connection open, no presence heartbeat), the phone reading: the deadman (1.5 s, `ControlAuthority.SweepStale`) should disengage AutoSteer and switch the sections off. ")
                + (double.IsNaN(dm.OffMs) ? "AutoSteer was still engaged after 8 s of wall time." : "Reason given: " + (dm.Reason ?? "none") + ".")));
        if (d.DeadmanPhoneAway is { } dp && (double.IsNaN(dp.OffMs) || dp.OffMs > 3000))
            list.Add(new Finding("DEADMAN-BLOCKED-BY-A-PHONE", Severity.Safety,
                double.IsNaN(dp.OffMs)
                    ? "With a phone in a pocket, AutoSteer stayed engaged when the operator's tablet went silent"
                    : F($"With a phone in a pocket, AutoSteer stayed engaged {dp.OffMs / 1000:0.0} s when the operator's tablet went silent"),
                F($"At {dp.At:HH:mm} the tablet's Wi-Fi dropped without a word with AutoSteer on, while the observer phone was in the pocket (`MAP-FROZEN-BY-A-PHONE`). ")
                + (d.Deadman is { } d1 && !double.IsNaN(d1.OffMs) && d1.OffMs <= 3000 ? F($"With the phone reading, the same drop disengaged AutoSteer in {d1.OffMs / 1000:0.0} s. ") : "")
                + "The deadman's sweep (`ControlAuthority.SweepStale`) runs at the top of `MapBroadcaster.RunAsync`'s loop, which was stuck in a broadcast waiting on the phone's send, so the stale holder was never revoked: "
                + (double.IsNaN(dp.OffMs) ? "AutoSteer was still engaged after 8 s of wall time." : "reason given: " + (dp.Reason ?? "none") + ".")));

        if (d.PhoneSlowSeed is { } slowSeed && d.PhoneLoadTabletMaxGapMs > 3000)
            list.Add(new Finding("MAP-PAUSED-WHILE-A-BROWSER-LOADS", Severity.Major,
                F($"While the phone loaded the page over a {Day.SlowWifiBytesPerSecond * 8 / 1e6:0} Mbit/s Wi-Fi, every browser's map stopped (the tablet waited {d.PhoneLoadTabletMaxGapMs / 1000:0.0} s for an update)"),
                F($"At {d.PhoneSlowAt:HH:mm} the phone opened the page with {d.Jobs.LastOrDefault(j => j.Start <= d.PhoneSlowAt)?.Job.Field ?? "a field"} open: a seed of {slowSeed.Bytes / 1048576.0:0.0} MB ({slowSeed.CoverageBytes / 1048576.0:0.0} MB of it the coverage snapshot), loaded in {slowSeed.LoadedMs / 1000:0.0} s. ")
                + "`WebSocketHub.HandleAsync` sends the seed on the new connection's send gate, the coverage snapshot as one frame (12 bytes for every worked display cell); the broadcast loop's next frame to that connection waits for the gate and `BroadcastAsync` waits for every connection, so the tablet's map, the other browsers' and the deadman's sweep (`DEADMAN-BLOCKED-BY-A-PHONE`) wait for the whole snapshot."));

        foreach (var (name, b) in new[] { ("tablet", d.Tablet), ("phone", d.Phone) })
        {
            if (b == null) continue;
            var judged = b.Seeds.Where(s => s.Note == null && s != d.PhoneSlowSeed).ToList();
            var missing = judged.Count(s => double.IsNaN(s.FirstTickMs));
            var slow = judged.Where(s => s.FirstTickMs > 5000).ToList();
            if (missing > 0 || slow.Count > 0)
                list.Add(new Finding("RECONNECT-NO-MAP", Severity.Minor,
                    F($"The {name} connected {judged.Count} time(s); {missing} time(s) it got no map update, {slow.Count} time(s) the first one took over 5 s"),
                    string.Join("; ", judged.Select((s, i) => F($"connection {i + 1}: seed {s.Bytes / 1024.0:0} KB, first Tick after {s.FirstTickMs / 1000:0.0} s")))));
        }

        // NTRIP all day.
        var ntripOff = d.Samples.Where(s => !s.NtripOpen).ToList();
        if (ntripOff.Count > 0)
            list.Add(new Finding("NTRIP-DROPPED", Severity.Minor,
                F($"NTRIP was not connected at {ntripOff.Count} of {d.Samples.Count} five-minute looks"),
                string.Join(", ", ntripOff.Take(8).Select(s => F($"{s.Clock:HH:mm}")))));

        // What AgOpenWeb itself logged as broken.
        var exc = d.Said.Where(k => k.Key.StartsWith("Exception", StringComparison.Ordinal) || k.Key.StartsWith("Hang", StringComparison.Ordinal)).ToList();
        if (exc.Count > 0)
            list.Add(new Finding("EXCEPTIONS-IN-THE-DAY", Severity.Major, F($"{exc.Sum(e => e.Value.Count)} exception(s) on the host loop, {exc.Count} kind(s)"),
                string.Join(" | ", exc.Take(5).Select(e => F($"{e.Value.Count}x from {e.Value.First:HH:mm}: {Short(e.Value.Text, 220)}")))));
        var err = d.Said.Where(k => k.Key.StartsWith("Error", StringComparison.Ordinal)).ToList();
        if (err.Count > 0)
            list.Add(new Finding("ERRORS-LOGGED", Severity.Minor, F($"{err.Sum(e => e.Value.Count)} error(s) logged, {err.Count} kind(s)"),
                string.Join(" | ", err.Take(5).Select(e => F($"{e.Value.Count}x from {e.Value.First:HH:mm}: {Short(e.Value.Text, 220)}")))));

        // The next morning, and the backend after exit.
        if (!double.IsNaN(d.NextBootSeconds) && d.NextBootSeconds > Math.Max(3, 2 * d.BootSeconds))
            list.Add(new Finding("NEXT-START-SLOW", Severity.Minor,
                F($"AgOpenWeb took {d.NextBootSeconds:0.0} s to start on the day's data ({d.BootSeconds:0.0} s fresh)"), ""));
        if (d.BeforeStart != null && d.AfterExit != null && d.AfterExit.HeapMB - d.BeforeStart.HeapMB > 30)
            list.Add(new Finding("BACKEND-NOT-FREED-AFTER-EXIT", Severity.Minor,
                F($"{d.AfterExit.HeapMB - d.BeforeStart.HeapMB:0} MB of the day stayed on the heap after AgOpenWeb shut down"),
                F($"Heap {d.BeforeStart.HeapMB:0.0} MB before the start, {d.AfterExit.HeapMB:0.0} MB after the shut-down in the same process. On Android and iOS the process outlives the backend. ")
                + (d.Censuses.FirstOrDefault(c => c.Label == "after-exit") is { Error: null } gone
                    ? (gone.Paths.Count == 0 ? "The heap census found none of MainViewModel, CoverageMapService or RemoteServerHost alive."
                        : string.Join(" ", gone.Paths.Select(kv => $"`{HeapCensus.Short(kv.Key)}` is still alive, held by `{Short(kv.Value, 600)}`.")))
                    : "")));
        return list.OrderBy(f => f.Severity).ThenBy(f => f.Code, StringComparer.Ordinal).ToList();
    }

    /// <summary>The slowest moments of one kind, with what had just happened before each.</summary>
    private static string Moments(Day d, string what, double over)
    {
        var m = d.SlowMoments.Where(x => x.What == what && x.Ms > over).OrderByDescending(x => x.Ms).Take(8).ToList();
        return m.Count == 0 ? "" : "The slowest: " + string.Join("; ", m.Select(x => F($"{x.Clock:HH:mm:ss} {x.Ms:0} ms (after {x.Before}{(x.Gc.Length > 0 ? "; " + x.Gc : "")})"))) + ".";
    }

    private static string CensusLine(Day d)
    {
        var first = d.Censuses.FirstOrDefault(c => c.Label.StartsWith("after-J", StringComparison.Ordinal));
        var last = d.Censuses.FirstOrDefault(c => c.Label == "end");
        if (first?.Error is { } e1) return "Heap census failed: " + e1;
        if (last?.Error is { } e2) return "Heap census failed: " + e2;
        if (first == null || last == null) return "";
        var g = HeapCensus.Growth(first, last, 3);
        return "Grew most (heap census): " + string.Join("; ", g.Select(x =>
            F($"{Short(HeapCensus.Short(x.Type), 80)} {x.DCount:+#,0;-#,0;+0} objects, +{x.DBytes / 1048576.0:0.0} MB")
            + (last.Paths.TryGetValue(x.Type, out var p) ? ", held at `" + Short(p, 160) + "`" : ""))) + ".";
    }

    /// <summary>The timers that grew between the two censuses, by owner.</summary>
    private static string TimerLine(Day d)
    {
        var first = d.Censuses.FirstOrDefault(c => c.Label.StartsWith("after-J", StringComparison.Ordinal) && c.Error == null);
        var last = d.Censuses.FirstOrDefault(c => c.Label == "end" && c.Error == null);
        if (first == null || last == null) return "";
        var grew = last.Timers.Select(kv => (Owner: kv.Key, D: kv.Value - first.Timers.GetValueOrDefault(kv.Key)))
            .Where(x => x.D > 0).OrderByDescending(x => x.D).Take(6).ToList();
        return grew.Count == 0 ? "" : "More live timers by owner (heap census): " + string.Join(", ", grew.Select(x => F($"`{Short(x.Owner, 90)}` +{x.D}"))) + ".";
    }

    private static List<(string Path, long Delta)> FilesGrown(Day d, string a, string b)
    {
        if (!d.FileSizes.TryGetValue(a, out var fa) || !d.FileSizes.TryGetValue(b, out var fb)) return new();
        return fb.Select(kv => (kv.Key, kv.Value - fa.GetValueOrDefault(kv.Key))).Where(x => x.Item2 > 0)
            .OrderByDescending(x => x.Item2).ToList();
    }

    private static string Short(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    private static HashSet<string> Known()
    {
        var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "KnownFindings.txt");
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return set;
        foreach (var line in File.ReadLines(path))
        {
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith('#')) continue;
            set.Add(t.Split(' ', '\t')[0]);
        }
        return set;
    }

    // ------------------------------------------------------------------ the report

    public static string Markdown(Day d)
    {
        var findings = Judge(d);
        var known = Known();
        var sb = new StringBuilder();
        string commit = Environment.GetEnvironmentVariable("GITHUB_SHA") ?? Environment.GetEnvironmentVariable("AGOPENWEB_DAY_COMMIT") ?? "";
        sb.AppendLine("# Working day scenarios");
        sb.AppendLine();
        sb.AppendLine(F($"An {Day.Hours:0.#}-hour day from {Day.DayStart:HH:mm} on the day's clock, {Day.PaceMs} ms of wall time per 0.1 s fix ({100.0 / Day.PaceMs:0.#}x), {d.WallSeconds / 60:0} minutes of wall time{(commit.Length > 0 ? ", commit " + commit[..Math.Min(7, commit.Length)] : "")}. {Day.Tractor.Name}, {Day.Sprayer.Name}, {Day.Sys.Label}."));
        if (d.MaxBehindMs > 2000)
            sb.AppendLine().AppendLine(F($"_The harness fell behind its pace by up to {d.MaxBehindMs / 1000:0.0} s of wall time (the machine could not keep up)._"));
        if (d.HarnessProblems.Count > 0)
        {
            sb.AppendLine().AppendLine("**The harness failed:**");
            foreach (var h in d.HarnessProblems) sb.AppendLine("- " + h);
        }

        sb.AppendLine().AppendLine("## Findings").AppendLine();
        if (findings.Count == 0) sb.AppendLine("None.");
        else
        {
            sb.AppendLine("| Code | Severity | | Finding |").AppendLine("|---|---|---|---|");
            foreach (var f in findings)
                sb.AppendLine($"| `{f.Code}` | {f.Severity} | {(known.Contains(f.Code) ? "Already reported" : "New")} | {f.Title} |");
            sb.AppendLine();
            foreach (var f in findings)
            {
                sb.AppendLine($"**`{f.Code}`** ({f.Severity}). {f.Title}.");
                if (f.Detail.Length > 0) sb.AppendLine().AppendLine(f.Detail);
                sb.AppendLine();
            }
        }

        sb.AppendLine("## The day").AppendLine();
        sb.AppendLine("| Clock | Wall | Event | What |").AppendLine("|---|---|---|---|");
        foreach (var e in d.Events)
            sb.AppendLine(F($"| {e.Clock:HH:mm:ss} | {e.WallSeconds / 60:0.0} min | `{e.Code}` | {e.Text.Replace("|", "/", StringComparison.Ordinal)} |"));

        sb.AppendLine().AppendLine("## Fields").AppendLine();
        sb.AppendLine("| Job | Field | From | To | Worked | U-turns | Driving | Parked | Opened in | GPS cycle p50 / p95 / p99 / max | Tick p99 / max | End |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var j in d.Jobs)
            sb.AppendLine(F($"| {j.Job.Code} | {j.Job.Field}{(j.Job.Reopen ? " (again)" : "")} | {j.Start:HH:mm} | {j.End:HH:mm} | {j.AreaHa:0.00} ha | {j.Turns}{(j.ManualTurns > 0 ? F($" ({j.ManualTurns} by hand)") : "")} | {j.DrivenMinutes:0} min | {j.ParkedMinutes:0} min | {j.OpenSeconds:0.0} s | {j.Cycle.P(50):0.000} / {j.Cycle.P(95):0.000} / {j.Cycle.P(99):0.000} / {j.Cycle.MaxMs:0.0} ms | {j.Tick.P(99):0.000} / {j.Tick.MaxMs:0.0} ms | {(j.Done ? "done" : j.CutShort ? "end of the day" : j.Note)} |"));
        sb.AppendLine(F($"| day | | | | | | | | | {d.CycleDay.P(50):0.000} / {d.CycleDay.P(95):0.000} / {d.CycleDay.P(99):0.000} / {d.CycleDay.MaxMs:0.0} ms ({d.CycleDay.Count:N0} cycles) | {d.TickDay.P(99):0.000} / {d.TickDay.MaxMs:0.0} ms ({d.TickDay.Count:N0} ticks) | |"));

        sb.AppendLine().AppendLine("## Baselines (no field open, the tractor parked, after a full collection)").AppendLine();
        sb.AppendLine("| | Clock | When | Heap | GC committed | Resident | Native | Threads | Pool | Timers | Open fds | Sockets | Files | Data folder |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var b in d.Baselines)
            sb.AppendLine(F($"| `{b.Code}` | {b.Clock:HH:mm} | {b.What} | {b.P.HeapMB:0.0} MB | {b.P.CommittedMB:0} MB | {b.P.RssMB:0} MB | {b.P.NativeMB:0} MB | {b.P.Threads} | {b.P.PoolThreads} | {b.P.Timers} | {b.P.Fds} | {b.P.Sockets} | {b.P.Files} | {b.DataMB:0.0} MB |"));
        if (d.LunchStart != null && d.LunchEnd != null)
            sb.AppendLine().AppendLine(F($"Lunch ({d.LunchStartClock:HH:mm}-{d.LunchEndClock:HH:mm}, parked, field open, no browser): heap {d.LunchStart.HeapMB:0.0} -> {d.LunchEnd.HeapMB:0.0} MB, resident {d.LunchStart.RssMB:0} -> {d.LunchEnd.RssMB:0} MB, threads {d.LunchStart.Threads} -> {d.LunchEnd.Threads}, timers {d.LunchStart.Timers} -> {d.LunchEnd.Timers}."));
        if (d.Measures.Count > 0)
        {
            sb.AppendLine();
            foreach (var m in d.Measures) sb.AppendLine("- " + m);
        }

        if (d.Censuses.Count > 0)
        {
            sb.AppendLine().AppendLine("## Heap census").AppendLine();
            foreach (var c in d.Censuses)
                sb.AppendLine(c.Error == null ? F($"- `{c.Label}`: {c.Objects:N0} objects, {c.Bytes / 1048576.0:0.0} MB ({c.Seconds:0.0} s)") : $"- `{c.Label}`: failed: {c.Error}");
            var first = d.Censuses.FirstOrDefault(c => c.Label.StartsWith("after-J", StringComparison.Ordinal) && c.Error == null);
            var last = d.Censuses.FirstOrDefault(c => c.Label == "end" && c.Error == null);
            if (first != null && last != null)
            {
                sb.AppendLine().AppendLine($"Types that grew from `{first.Label}` to `{last.Label}` (the same state), most bytes first; where the biggest one is held (the path from a GC root, from the last AgOpenWeb object on it):").AppendLine();
                sb.AppendLine("| Type | More objects | More bytes | Objects at the end | Held at |").AppendLine("|---|---|---|---|---|");
                foreach (var g in HeapCensus.Growth(first, last, 20))
                {
                    string held = last.Paths.TryGetValue(g.Type, out var path) ? "`" + Short(path, 200) + "`" : "";
                    sb.AppendLine(F($"| `{Short(HeapCensus.Short(g.Type), 110)}` | {g.DCount:N0} | {g.DBytes / 1024.0:N0} KB | {g.Count:N0} | {held} |"));
                }
            }
            if (first != null && last != null && last.Timers.Count > 0)
            {
                sb.AppendLine().AppendLine($"Live timers by owner (the type of the callback's target, else of its state), `{first.Label}` and `{last.Label}`:").AppendLine();
                sb.AppendLine("| Owner | " + first.Label + " | " + last.Label + " |").AppendLine("|---|---|---|");
                foreach (var owner in first.Timers.Keys.Union(last.Timers.Keys).OrderByDescending(k => last.Timers.GetValueOrDefault(k)).ThenBy(k => k, StringComparer.Ordinal))
                    sb.AppendLine(F($"| `{Short(owner, 110)}` | {first.Timers.GetValueOrDefault(owner)} | {last.Timers.GetValueOrDefault(owner)} |"));
            }
            if (d.Censuses.FirstOrDefault(c => c.Label == "after-exit" && c.Error == null) is { } gone)
            {
                var app = gone.Types.Values.Where(t => t.Type.StartsWith("AgOpenWeb.", StringComparison.Ordinal)).ToList();
                sb.AppendLine().AppendLine(F($"After AgOpenWeb shut down (`after-exit`): {gone.Objects:N0} live objects, {gone.Bytes / 1048576.0:0.0} MB; {app.Sum(t => t.Count):N0} of them AgOpenWeb's own ({app.Count} types). The biggest types:")).AppendLine();
                sb.AppendLine("| Type | Objects | Bytes |").AppendLine("|---|---|---|");
                foreach (var t in gone.Types.Values.OrderByDescending(t => t.Bytes).Take(8))
                    sb.AppendLine(F($"| `{Short(HeapCensus.Short(t.Type), 110)}` | {t.Count:N0} | {t.Bytes / 1024.0:N0} KB |"));
                foreach (var (type, path) in gone.Paths)
                    sb.AppendLine().AppendLine($"`{HeapCensus.Short(type)}` is still alive, held by: `{path}`");
            }
        }

        sb.AppendLine().AppendLine("## Every half hour").AppendLine();
        sb.AppendLine("Each row: the process at that moment (after a full collection) and the half hour before it (GPS cycle and control tick percentiles, the host loop's slowest answer, the tablet's frames).").AppendLine();
        sb.AppendLine("| Clock | Doing | Heap | Resident | Threads | Timers | fds | GPS cycle p50 / p95 / max | Tick p99 / max | Host loop | Tablet ticks/s, longest gap | Data | NTRIP | Errors / warnings |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var grp in d.Samples.GroupBy(s => (s.Clock - Day.DayStart).Ticks / TimeSpan.FromMinutes(30).Ticks))
        {
            var rows = grp.ToList(); var s = rows[^1];
            double p50 = rows.Average(r => double.IsNaN(r.CycleP50) ? 0 : r.CycleP50);
            sb.AppendLine(F($"| {s.Clock:HH:mm} | {s.Phase} | {s.P.HeapMB:0.0} MB | {s.P.RssMB:0} MB | {s.P.Threads} | {s.P.Timers} | {s.P.Fds} | {p50:0.000} / {rows.Max(r => Nz(r.CycleP95)):0.000} / {rows.Max(r => r.CycleMax):0.0} ms | {rows.Max(r => Nz(r.TickP99)):0.000} / {rows.Max(r => r.TickMax):0.0} ms | {rows.Max(r => r.HostLoopMaxMs):0} ms | {Nz(s.TabletTicksPerSec):0.0}, {rows.Max(r => Nz(r.TabletMaxGapMs)) / 1000:0.0} s | {s.DataMB:0.0} MB | {(rows.All(r => r.NtripOpen) ? "on" : "dropped")} | {rows.Sum(r => r.Errors)} / {rows.Sum(r => r.Warnings)} |"));
        }

        if (d.SlowMoments.Count > 0)
        {
            sb.AppendLine().AppendLine("## The slowest moments").AppendLine();
            sb.AppendLine("GPS cycles over 20 ms and control ticks over 10 ms, slowest first, with the day's last event before each.").AppendLine();
            sb.AppendLine("| Clock | What | Took | After | In it |").AppendLine("|---|---|---|---|---|");
            foreach (var m in d.SlowMoments.OrderByDescending(x => x.Ms).Take(25))
                sb.AppendLine(F($"| {m.Clock:HH:mm:ss.f} | {m.What} | {m.Ms:0.0} ms | {m.Before} | {m.Gc} |"));
        }

        sb.AppendLine().AppendLine("## Browsers").AppendLine();
        foreach (var b in new[] { d.Tablet, d.Phone })
        {
            if (b == null) continue;
            sb.AppendLine(F($"- **{b.Name}**: {b.Connects} connection(s), {b.Bytes / 1048576.0:0.0} MB received. By frame: ")
                + string.Join(", ", Enumerable.Range(0, 64).Where(i => b.FramesByType[i] > 0).OrderByDescending(i => b.BytesByType[i])
                    .Select(i => F($"{FrameName(i)} {b.FramesByType[i]:N0} ({b.BytesByType[i] / 1024.0:N0} KB)"))));
            foreach (var (s, i) in b.Seeds.Select((s, i) => (s, i)))
                sb.AppendLine(F($"  - connection {i + 1}: seed {s.Frames} frames, {s.Bytes / 1024.0:0} KB (")
                    + string.Join(", ", Enumerable.Range(0, 64).Where(t => s.ByType[t] >= 10240).OrderByDescending(t => s.ByType[t]).Select(t => F($"{FrameName(t)} {s.ByType[t] / 1024.0:0} KB")))
                    + F($"), loaded in {s.LoadedMs:0} ms, first Tick after {s.FirstTickMs:0} ms") + (s.Note != null ? ", " + s.Note : "")
                    + (s == d.PhoneSlowSeed ? F($", over a {Day.SlowWifiBytesPerSecond * 8 / 1e6:0} Mbit/s Wi-Fi") : ""));
        }

        sb.AppendLine().AppendLine("## Disk").AppendLine();
        var cols = d.Disk.Keys.ToList();
        var kinds = d.Disk.Values.SelectMany(v => v.Keys).Distinct().OrderBy(k => k, StringComparer.Ordinal).ToList();
        sb.AppendLine("| Files | " + string.Join(" | ", cols.Select(c => "`" + c + "`")) + " |");
        sb.AppendLine("|---|" + string.Concat(cols.Select(_ => "---|")));
        foreach (var k in kinds)
            sb.AppendLine("| `" + k + "` | " + string.Join(" | ", cols.Select(c => d.Disk[c].TryGetValue(k, out var v) ? F($"{v.Files} / {v.Bytes / 1024.0:N0} KB") : "")) + " |");

        sb.AppendLine().AppendLine("## What AgOpenWeb logged, and what the operator saw").AppendLine();
        if (d.Said.Count == 0) sb.AppendLine("Nothing.");
        foreach (var kv in d.Said.OrderByDescending(k => k.Value.Count).Take(40))
            sb.AppendLine(F($"- {kv.Value.Count}x from {kv.Value.First:HH:mm}: {Short(kv.Value.Text, 300).Replace("\n", " ", StringComparison.Ordinal)}"));
        return sb.ToString();
    }

    private static double Nz(double v) => double.IsNaN(v) ? 0 : v;

    private static string FrameName(int t) => t switch
    {
        1 => "Scene", 2 => "Tick", 3 => "CoverageInit", 4 => "CoverageCells", 5 => "Status", 6 => "ControlState", 7 => "Hello",
        8 => "Config", 9 => "Profiles", 10 => "Wizard", 11 => "NtripProfiles", 12 => "FieldOps", 13 => "AgShare", 14 => "AppInfo",
        15 => "FieldTools", 16 => "RecordedPath", 17 => "Boundary", 18 => "Sound", 19 => "Pong", 20 => "CoverageEdge", 21 => "ViewPrefs",
        22 => "Prompt", 23 => "Toast", 24 => "DrivePick", 25 => "HardwareMessage", 26 => "Vt", 27 => "VtImage", 28 => "NtripMountpoints",
        29 => "WebCards", _ => "type " + t.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>Every five-minute sample, for a spreadsheet or a plot.</summary>
    public static string Csv(Day d)
    {
        var sb = new StringBuilder("clock,wall_s,phase,heap_mb,rss_mb,anon_mb,threads,pool,timers,fds,sockets,files,gen2,alloc_mb,cpu_s,cycle_p50,cycle_p95,cycle_p99,cycle_max,cycles,tick_p99,tick_max,host_max_ms,tablet_tps,tablet_gap_ms,tablet_tick_bytes,data_mb,data_files,ntrip_open,ntrip_bytes,ntrip_events,errors,warnings\n");
        foreach (var s in d.Samples)
            sb.AppendLine(F($"{s.Clock:HH:mm:ss},{s.WallSeconds:0},{s.Phase},{s.P.HeapMB:0.00},{s.P.RssMB:0.0},{s.P.AnonMB:0.0},{s.P.Threads},{s.P.PoolThreads},{s.P.Timers},{s.P.Fds},{s.P.Sockets},{s.P.Files},{s.P.Gen2},{s.P.AllocatedMB:0},{s.P.CpuSeconds:0.0},{s.CycleP50:0.0000},{s.CycleP95:0.0000},{s.CycleP99:0.0000},{s.CycleMax:0.000},{s.Cycles},{s.TickP99:0.0000},{s.TickMax:0.000},{s.HostLoopMaxMs:0.0},{s.TabletTicksPerSec:0.00},{s.TabletMaxGapMs:0},{s.TabletTickBytes:0},{s.DataMB:0.00},{s.DataFiles},{(s.NtripOpen ? 1 : 0)},{s.NtripBytes},{s.NtripEvents},{s.Errors},{s.Warnings}"));
        return sb.ToString();
    }
}
