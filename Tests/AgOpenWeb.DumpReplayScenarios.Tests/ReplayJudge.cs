// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.DumpReplayScenarios.Tests;

/// <summary>
/// After a replay: what this build did against what the reporter's build did on the same GPS
/// data (then / now), and the rules any run must keep (the field job's checks that a replay can
/// measure, and the GPS stage's). A difference between then and now is not a finding by itself:
/// it is often the fix. A finding is a rule the replay itself breaks.
/// </summary>
public static class ReplayJudge
{
    private static string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    public static void Judge(DumpReplay R)
    {
        var S = R.S;
        var d = R.Dump;
        var rows = d.Rows;
        var now = R.Now;
        int a = R.StartRow, b = rows.Count;

        // ---- the run itself: exceptions, errors, a hung host loop, VM changes off the loop.
        if (R.Exceptions.Count > 0)
            S.Problem(Severity.Bug, Kind.AppDefect, "EXCEPTION", "an exception on the host loop: " + First(R.Exceptions[0]));
        foreach (var g in R.Errors.GroupBy(Category).Take(3))
            S.Problem(Severity.Bug, Kind.AppDefect, "ERROR-LOGGED", F($"{g.Count()} error(s) logged, first: {First(g.First())}"));
        if (R.Hung) S.Problem(Severity.Bug, Kind.AppDefect, "HOST-LOOP-HANG", "the host loop did not answer within 30 s");
        if (R.OffLoop.Count > 0) S.Problem(Severity.Inconsistency, Kind.AppDefect, "STATE-OFF-LOOP", R.OffLoop[0]);

        // ---- the pose: the replay must put the pivot where the reporter's build did.
        var pos = new List<double>();
        var hdgMoving = new List<double>();
        for (int i = a; i < b; i++)
        {
            if (now[i] is not { } n) continue;
            pos.Add(Math.Sqrt((n.E - rows[i].E) * (n.E - rows[i].E) + (n.N - rows[i].N) * (n.N - rows[i].N)));
            if (rows[i].Speed > 0.5) hdgMoving.Add(AngleDiff(n.Heading, rows[i].Heading));
        }
        if (pos.Count == 0) { S.Harness("REPLAY-NO-CYCLES", "the pipeline gave no cycle for any row"); return; }
        R.S.Row.Metrics.Add(("Pivot: now against then (median / 95% / max)", F($"{Pct(pos, 50):0.000} / {Pct(pos, 95):0.000} / {pos.Max():0.000} m")));
        if (hdgMoving.Count > 0)
            R.S.Row.Metrics.Add(("Heading while moving: now against then (median / 95% / max)", F($"{Pct(hdgMoving, 50):0.0} / {Pct(hdgMoving, 95):0.0} / {hdgMoving.Max():0.0}°")));
        // A pivot more than 10 cm from the reporter's on most rows: the replay does not feed what
        // the reporter's receiver gave (or the GPS stage changed since: the heading line says which).
        if (Pct(pos, 50) > 0.10)
            S.Problem(Severity.Inconsistency, Kind.HarnessArtifact, "REPLAY-POSE-DIFFERS",
                F($"the replayed pivot is {Pct(pos, 50):0.00} m (median) from the reporter's: the GPS stage changed since the report, or the replay does not rebuild the fix exactly"));

        // ---- guidance, where both builds had a line and no turn ran. The cross-track error's
        // sign and the pass numbering may differ between builds; the line is the same when the
        // distance from it is.
        int div = Divergence(R);
        R.DivergedRow = div;
        var steer = new List<double>();
        int lineSame = 0, lineAll = 0;
        for (int i = a; i < Math.Min(b, div); i++)
        {
            var r = rows[i];
            if (now[i] is not { } n || !r.HasGuidance || !n.HasGuidance || r.YtExecuting || n.YtExecuting) continue;
            lineAll++;
            if (Math.Abs(Math.Abs(n.Xte) - Math.Abs(r.Xte)) < 0.5) lineSame++;
            if (r.AutoSteer && n.Engaged) steer.Add(Math.Abs(n.Steer - r.Steer));
        }
        if (lineAll > 0) R.S.Row.Metrics.Add(("On the same line as then (distance from it within 0.5 m)", F($"{100.0 * lineSame / lineAll:0}% of {lineAll} rows on a line")));
        if (steer.Count > 0) R.S.Row.Metrics.Add(("Steer angle now against then, AutoSteer on (median / 95% / max)", F($"{Pct(steer, 50):0.0} / {Pct(steer, 95):0.0} / {steer.Max():0.0}°")));
        if (div < b)
        {
            var n = now[div]!;
            string what = n.HasGuidance
                ? F($"guides on another line than the reporter's build did (distance from the line now {Math.Abs(n.Xte):0.0} m, then {Math.Abs(rows[div].Xte):0.0} m)")
                : F($"has no line where the reporter's build guided ({Math.Abs(rows[div].Xte):0.0} m from its line)");
            string rest = ", and the replay goes on with the reporter's own path: the guidance comparisons stop there (the take-over shows what this build does)";
            // The reporter changing pass just before (a lateral move, a re-pick, a U-turn) is a
            // place the two builds may part for a reason the dump does not hold: a note. Anywhere
            // else this build left the reporter's line on its own.
            if (PassChangeBefore(R, div, 5.0) is { } change)
                S.Note(F($"from t {rows[div].T:0.0} s this build ") + what + ", " + change + rest);
            else if (!n.HasGuidance)
                S.Problem(Severity.Bug, Kind.AppDefect, "GUIDANCE-LOST",
                    F($"from t {rows[div].T:0.0} s this build ") + what + " for 3 s, with no pass change by the reporter in the 5 s before" + rest, "replay", rows[div].T);
            else
                S.Problem(Severity.Inconsistency, Kind.AppDefect, "GUIDANCE-DIVERGED",
                    F($"from t {rows[div].T:0.0} s this build ") + what + " for 3 s, with no pass change by the reporter in the 5 s before" + rest, "replay", rows[div].T);
            R.S.Row.ThenNow.Add(("Replay compares guidance until", "", F($"t {rows[div].T:0} s of {rows[^1].T:0} s")));
        }

        // ---- rules on the replayed run.
        StandstillHeading(R);
        PassAndXteJumps(R);
        for (int i = a; i < b; i++)
            if (now[i] is { } n && (!double.IsFinite(n.Steer) || !double.IsFinite(n.Xte) || !double.IsFinite(n.Heading)))
            { S.Problem(Severity.Safety, Kind.AppDefect, "NOT-A-NUMBER", F($"t {rows[i].T:0.0} s: steer {n.Steer}, cross-track {n.Xte}, heading {n.Heading}"), "replay", rows[i].T); break; }

        Alarms(S, R.Sounds, "replay", rows[a].T);

        // ---- AutoSteer: dropped by the app, then and now.
        int dropsNow = 0; string? firstDrop = null; double firstDropT = 0;
        for (int i = a + 1; i < b; i++)
        {
            if (now[i] is not { } n || now[i - 1] is not { } p) continue;
            if (p.Engaged && !n.Engaged && rows[i].AutoSteer)
            {
                dropsNow++;
                if (firstDrop == null) { firstDrop = F($"t {rows[i].T:0.0} s: {n.Disengage ?? "no reason given"}"); firstDropT = rows[i].T; }
            }
        }
        int dropsThen = d.Log.Count(l => l.Text.Contains("[AutoSteer] Off", StringComparison.Ordinal) && !l.Text.Contains("button", StringComparison.OrdinalIgnoreCase) && d.LogT(l) is { } t && t >= rows[a].T && t <= rows[^1].T);
        // This build dropping AutoSteer where the reporter's stayed engaged, on the same fixes:
        // the operator loses steering this build would not have taken from them before.
        if (dropsNow > 0)
            S.Problem(Severity.Bug, Kind.AppDefect, "AUTOSTEER-DROPPED",
                F($"AutoSteer went off by itself {dropsNow}x in this build while the reporter's stayed engaged on the same fixes; first {firstDrop}"), "replay", firstDropT);

        // ---- U-turns: episodes and headland approaches, then and now.
        // A turn already running when the GPS log starts can't be replayed: not counted, then or now.
        var turnsThen = Episodes(i => rows[i].YtExecuting, a, b).Where(e => e.From > a).ToList();
        var turnsNow = Episodes(i => now[i]?.YtExecuting == true, a, b).Where(e => e.From > a).ToList();
        if (rows[a].YtExecuting) S.Note("a U-turn was already running when the GPS log starts: not replayed, not counted");
        var appr = Approaches(R);
        int turnedThen = appr.Count(x => x.Then), turnedNow = appr.Count(x => x.Now);
        foreach (var x in appr)
            S.Step(F($"headland approach t {rows[x.Row].T:0.0} s on pass {rows[x.Row].PathsAway}: then {(x.Then ? "U-turn" : "no U-turn")}, now {(x.Now ? "U-turn" : "no U-turn")}{(x.Engaged ? "" : " (AutoSteer off in the replay)")}{(x.Decided ? "" : F($" (undecided: the GPS log ends {rows[^1].T - rows[x.Row].T:0} s after it)"))}{(x.Row < R.DivergedRow ? "" : " (after the lines parted: not judged)")}"));
        var missedNow = appr.Where(x => !x.Now && x.Engaged && x.Decided && R.UTurnsUsed && x.Row < R.DivergedRow).ToList();
        if (missedNow.Count > 0)
            S.Problem(Severity.Bug, Kind.AppDefect, "UTURN-MISSED",
                F($"AgOpenWeb made no U-turn when the reporter's tractor reached the headland line with AutoSteer and U-turns on (t {rows[missedNow[0].Row].T:0.0} s, pass {rows[missedNow[0].Row].PathsAway}); then: {(missedNow[0].Then ? "a U-turn was made" : "no U-turn either")}"),
                "replay", rows[Math.Max(a, missedNow[0].Row - 100)].T, rows[Math.Min(b - 1, missedNow[0].Row + 150)].T);

        R.S.Row.ThenNow.Add(("U-turns run", turnsThen.Count.ToString(CultureInfo.InvariantCulture), turnsNow.Count.ToString(CultureInfo.InvariantCulture)));
        if (appr.Count > 0)
            R.S.Row.ThenNow.Add(("Headland approaches with a U-turn", F($"{turnedThen} of {appr.Count}"), F($"{turnedNow} of {appr.Count}")));
        R.S.Row.ThenNow.Add(("AutoSteer off by itself", dropsThen.ToString(CultureInfo.InvariantCulture), dropsNow.ToString(CultureInfo.InvariantCulture)));
        string Verdict(int turns, int turned, int drops) =>
            F($"{turns} U-turn{(turns == 1 ? "" : "s")}") + (appr.Count > 0 ? F($", {appr.Count - turned} approach{(appr.Count - turned == 1 ? "" : "es")} without one") : "") + F($", {drops} AutoSteer drop{(drops == 1 ? "" : "s")}");
        R.S.Row.ThenNow.Insert(0, ("Verdict", Verdict(turnsThen.Count, turnedThen, dropsThen), Verdict(turnsNow.Count, turnedNow, dropsNow)));
    }

    /// <summary>#157: the heading must hold while the machine stands still and the receiver's
    /// own heading holds.</summary>
    private static void StandstillHeading(DumpReplay R)
    {
        var rows = R.Dump.Rows; var now = R.Now;
        int worstRow = -1; double worst = 0, worstThen = 0;
        int i = R.StartRow;
        while (i < rows.Count)
        {
            if (rows[i].Speed >= 0.1 || now[i] is null) { i++; continue; }
            int j = i;
            while (j + 1 < rows.Count && rows[j + 1].Speed < 0.1 && now[j + 1] != null) j++;
            if (j - i >= 20)
            {
                // Skip the first second: the machine is still slowing down.
                int s0 = Math.Min(j, i + 10);
                double h0 = now[s0]!.Heading, t0 = rows[s0].Heading, sh0 = rows[s0].SentenceHeading;
                for (int k = s0; k <= j; k++)
                {
                    double sentenceMove = rows[k].HasSentenceColumns && (rows[k].ImuValid || rows[k].Sentence is "PAOGI" or "SIM") ? AngleDiff(rows[k].SentenceHeading, sh0) : 0;
                    if (sentenceMove > 2) continue;
                    double dn = AngleDiff(now[k]!.Heading, h0);
                    if (dn > worst) { worst = dn; worstRow = k; worstThen = AngleDiff(rows[k].Heading, t0); }
                }
            }
            i = j + 1;
        }
        if (worstRow >= 0 && worst > 10)
            R.S.Problem(Severity.Bug, Kind.AppDefect, "HEADING-MOVES-AT-STANDSTILL",
                F($"standing still from about t {rows[worstRow].T:0.0} s, the heading moved {worst:0} degrees while the receiver's own heading held (then: {worstThen:0} degrees)"), "replay", rows[worstRow].T);
    }

    /// <summary>The pass number and the cross-track error must not jump while the machine moves
    /// a metre on a line (no turn, no track change).</summary>
    private static void PassAndXteJumps(DumpReplay R)
    {
        var rows = R.Dump.Rows; var now = R.Now;
        double lastTurnEnd = double.NegativeInfinity;
        for (int i = R.StartRow + 1; i < rows.Count; i++)
        {
            if (now[i] is not { } n || now[i - 1] is not { } p) continue;
            if (p.YtExecuting && !n.YtExecuting) lastTurnEnd = rows[i].T;
            if (n.YtExecuting || p.YtExecuting || rows[i].T - lastTurnEnd < 3) continue;
            if (!n.HasGuidance || !p.HasGuidance || !n.GuidanceFresh || !p.GuidanceFresh) continue;
            // Turning round (a U-turn the reporter drove by hand, a turn at a field end): the
            // distance from the line changes sign with the direction of travel.
            int back = i;
            while (back > R.StartRow && rows[i].T - rows[back].T < 5) back--;
            if (AngleDiff(rows[i].Heading, rows[back].Heading) > 60) continue;
            double moved = Math.Sqrt((n.E - p.E) * (n.E - p.E) + (n.N - p.N) * (n.N - p.N));
            if (moved > 1.0) continue;
            if (Math.Abs(n.PathsAway - p.PathsAway) >= 2)
            {
                bool then = Math.Abs(rows[i].PathsAway - rows[i - 1].PathsAway) >= 2;
                R.S.Problem(Severity.Bug, Kind.AppDefect, "PASS-JUMP",
                    F($"t {rows[i].T:0.0} s: the pass number jumped {p.PathsAway} -> {n.PathsAway} while the machine moved {moved:0.00} m on the line (then: {(then ? "the same" : F($"{rows[i - 1].PathsAway} -> {rows[i].PathsAway}"))})"), "replay", rows[i].T);
            }
            // The sign of the distance from the line turns with the direction of travel across it
            // (square to the line it flips): the size is what must not jump.
            else if (n.PathsAway == p.PathsAway && Math.Abs(Math.Abs(n.Xte) - Math.Abs(p.Xte)) > 1.5)
                R.S.Problem(Severity.Bug, Kind.AppDefect, "XTE-JUMP",
                    F($"t {rows[i].T:0.0} s: the cross-track error jumped {p.Xte:0.00} -> {n.Xte:0.00} m on pass {n.PathsAway} while the machine moved {moved:0.00} m"), "replay", rows[i].T);
        }
    }

    /// <summary>
    /// The first row from which this build has left the reporter's line for 3 s: rows where the
    /// reporter's build guided with AutoSteer on and no turn, and this build either has no line
    /// or one at another distance (more than 1 m apart; no turn now). A row where the two agree
    /// ends the run, and so does a U-turn of the reporter's; a row that can't be compared (the
    /// reporter's AutoSteer off, no line then, a turn now) leaves it as it is. From there the
    /// reporter's tractor, steered by their build, is no test of this one. <c>rows.Count</c> if never.
    /// </summary>
    public static int Divergence(DumpReplay R)
    {
        var rows = R.Dump.Rows; var now = R.Now;
        int run = 0, from = -1;
        for (int i = R.StartRow; i < rows.Count; i++)
        {
            var r = rows[i];
            if (r.YtExecuting || r.YtTriggered) { run = 0; continue; }
            if (now[i] is not { } n || !r.AutoSteer || !r.HasGuidance || (n.HasGuidance && n.YtExecuting)) continue;
            bool apart = !n.HasGuidance || Math.Abs(Math.Abs(n.Xte) - Math.Abs(r.Xte)) > 1.0;
            if (!apart) { run = 0; continue; }
            if (run++ == 0) from = i;
            if (run >= 30) return from;
        }
        return rows.Count;
    }

    /// <summary>The reporter's build changed pass in the <paramref name="seconds"/> before row
    /// <paramref name="i"/> (a lateral move, a re-pick of the line, a U-turn): what it was, or null.</summary>
    public static string? PassChangeBefore(DumpReplay R, int i, double seconds)
    {
        var rows = R.Dump.Rows;
        for (int k = i; k > R.StartRow && rows[i].T - rows[k].T <= seconds; k--)
        {
            if (rows[k].YtExecuting || rows[k].YtTriggered) return F($"just after a U-turn of the reporter's (t {rows[k].T:0.0} s)");
            if (rows[k].HasGuidance && rows[k - 1].HasGuidance && rows[k].PathsAway != rows[k - 1].PathsAway)
                return F($"just after the reporter's build changed pass {rows[k - 1].PathsAway} -> {rows[k].PathsAway} (t {rows[k].T:0.0} s: a lateral move or a re-pick the dump does not record)");
        }
        return null;
    }

    /// <summary>The alarms the operator heard, and an alarm played again and again (#570: a missed
    /// turn re-planned and alarmed on every cycle): the same alarm 4 times or more within 2 s.</summary>
    public static void Alarms(Scenario s, List<(double T, SoundEffect Effect)> sounds, string phase, double from)
    {
        List<(double T, SoundEffect Effect)> list;
        lock (sounds) list = sounds.Where(x => x.T >= from).ToList();
        var alarms = list.Where(x => x.Effect is SoundEffect.UTurnTooClose or SoundEffect.BoundaryAlarm or SoundEffect.YouTurnApproach or SoundEffect.RtkLost).ToList();
        s.Row.Metrics.Add(("Alarms heard (" + phase + ")", alarms.Count == 0 ? "none"
            : string.Join(", ", alarms.GroupBy(x => x.Effect).OrderBy(g => g.Key.ToString(), StringComparer.Ordinal).Select(g => F($"{g.Key} {g.Count()}x")))));
        foreach (var g in alarms.GroupBy(x => x.Effect))
        {
            var t = g.Select(x => x.T).OrderBy(x => x).ToList();
            for (int i = 0; i + 3 < t.Count; i++)
                if (t[i + 3] - t[i] <= 2.0)
                {
                    int n = t.Count(x => x >= t[i] && x <= t[i] + 2.0);
                    s.Problem(Severity.Bug, Kind.AppDefect, "ALARM-REPEATED", F($"{phase} t {t[i]:0.0} s: the {g.Key} alarm played {n} times within 2 s"), phase, t[i], t[i] + 2.0);
                    break;
                }
        }
    }

    /// <summary>A headland approach. <c>Decided</c>: the GPS log goes on long enough after it (15 s)
    /// to tell whether a U-turn came, or one did.</summary>
    public sealed record Approach(int Row, bool Then, bool Now, bool Engaged, bool Decided);

    /// <summary>Where the reporter's tractor crossed from the cultivated area into the headland,
    /// heading out, on a line: did a U-turn trigger around it (10 s before to 15 s after)?</summary>
    public static List<Approach> Approaches(DumpReplay R)
    {
        var list = new List<Approach>();
        var rows = R.Dump.Rows; var now = R.Now;
        var work = R.Work ?? (R.Outer.Count > 2 ? R.Outer : null);
        if (work == null) return list;
        bool prevIn = false; int lastAt = -1000;
        for (int i = R.StartRow; i < rows.Count; i++)
        {
            bool inside = Ring.Inside((rows[i].E, rows[i].N), work);
            if (i > R.StartRow && prevIn && !inside && rows[i].HasGuidance && !rows[i].Reverse && rows[i].Speed > 0.5 && i - lastAt > 100)
            {
                lastAt = i;
                int from = Math.Max(R.StartRow, i - 100), to = Math.Min(rows.Count - 1, i + 150);
                bool then = false, nowT = false;
                for (int k = from; k <= to; k++)
                {
                    then |= rows[k].YtTriggered || rows[k].YtExecuting;
                    nowT |= now[k] is { } n && (n.YtTriggered || n.YtExecuting);
                }
                list.Add(new Approach(i, then, nowT, now[i]?.Engaged == true, then || nowT || i + 150 < rows.Count));
            }
            prevIn = inside;
        }
        return list;
    }

    public static List<(int From, int To)> Episodes(Func<int, bool> on, int a, int b)
    {
        var list = new List<(int, int)>();
        int start = -1;
        for (int i = a; i < b; i++)
        {
            if (on(i) && start < 0) start = i;
            if (!on(i) && start >= 0) { list.Add((start, i - 1)); start = -1; }
        }
        if (start >= 0) list.Add((start, b - 1));
        return list;
    }

    public static double AngleDiff(double a, double b)
    {
        double d = Math.Abs(((a - b) % 360 + 540) % 360 - 180);
        return d;
    }

    public static double Pct(List<double> v, double p)
    {
        if (v.Count == 0) return 0;
        var s = v.OrderBy(x => x).ToList();
        int i = (int)Math.Clamp(Math.Round(p / 100.0 * (s.Count - 1)), 0, s.Count - 1);
        return s[i];
    }

    private static string First(string s) { int i = s.IndexOf('\n'); return (i < 0 ? s : s[..i]).Trim(); }
    private static string Category(string s) { int i = s.IndexOf(':'); return i < 0 ? s : s[..i]; }
}
