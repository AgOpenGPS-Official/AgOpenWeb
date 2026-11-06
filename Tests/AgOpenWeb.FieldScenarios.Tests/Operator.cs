// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace AgOpenWeb.FieldScenarios.Tests;

/// <summary>
/// What an operator can do at the web client, as the commands the client sends. Both the
/// careful and the careless operator use these; they differ in which ones they pick, in
/// what order, and with what values.
/// </summary>
public abstract class Operator
{
    protected readonly Scenario S;
    protected readonly FieldScenarioRig R;
    protected static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    protected static string F(FormattableString s) => s.ToString(Inv);

    protected Operator(Scenario s)
    {
        S = s;
        R = s.Rig;
    }

    protected Task Cmd(string cmd, string arg = "") => R.SendAsync(cmd, arg);

    /// <summary>Lat/lon → the "e,n;e,n;…" a map drawing sends.</summary>
    protected async Task<string> MapPoints(IEnumerable<(double Lat, double Lon)> ring)
    {
        var parts = new List<string>();
        foreach (var p in ring)
        {
            var (e, n) = await R.ToFieldAsync(p.Lat, p.Lon);
            parts.Add(F($"{e:F3},{n:F3}"));
        }
        return string.Join(";", parts);
    }

    protected async Task<string> En((double Lat, double Lon) p)
    {
        var (e, n) = await R.ToFieldAsync(p.Lat, p.Lon);
        return F($"{e:F3},{n:F3}");
    }

    protected static string LL((double Lat, double Lon) p) => F($"{p.Lat:F9},{p.Lon:F9}");

    /// <summary>Tool as Renato's sprayer: 8 sections of 1 m.</summary>
    public async Task SetUpSprayer()
    {
        await Cmd("config.set", "tool.numSections:8");
        for (int i = 0; i < 8; i++) await Cmd("config.set", F($"tool.sectionWidth:{i},100"));
    }

    protected async Task<bool> NewField(string name, (double Lat, double Lon) at, double heading = 0)
    {
        await R.PlaceAsync(at.Lat, at.Lon, heading);
        await Cmd("field.new", name);
        return await R.WaitUntilAsync(() => R.Vm.IsFieldOpen && R.Vm.CurrentFieldName == name && !R.State.UI.IsBusy);
    }

    protected async Task DrawBoundary(IEnumerable<(double Lat, double Lon)> ring) =>
        await Cmd("boundary.fromMapPoints", await MapPoints(ring));

    /// <summary>Drive round a ring with the boundary player: Drive around → Record → drive → Stop.</summary>
    protected async Task RecordBoundary(IReadOnlyList<(double Lat, double Lon)> ring, bool inner, double stepM = 1.0, bool atPivot = true)
    {
        // A tractor turns a corner on a curve; jumping from one heading to the next in one
        // fix would swing the recorded pivot sideways and draw a knot at every corner.
        var path = Rounded(ring, 3.0);
        await R.PlaceAsync(path[0].Lat, path[0].Lon, Geo.BearingDeg(path[0], path[1]));
        await Cmd(inner ? "boundary.driveAroundInner" : "boundary.driveAround");
        // The tractor drives on the line itself, so record at the antenna, not at the
        // implement's edge (which is the default and would put the line 4 m to one side).
        if (atPivot && !await R.ReadAsync(() => R.Vm.IsDrawAtPivot)) await Cmd("boundary.toggleAntennaTool");
        await Cmd("boundary.toggleRecording");
        var closed = path.Append(path[0]).ToList();
        await R.DriveAsync(closed, stepM);
        await Cmd("boundary.stop");
        await Cmd("boundary.refresh");
    }

    /// <summary>The ring with each corner replaced by a curve that starts and ends up to
    /// <paramref name="cutM"/> metres from it (less on short edges).</summary>
    protected static List<(double Lat, double Lon)> Rounded(IReadOnlyList<(double Lat, double Lon)> ring, double cutM)
    {
        var o = ring[0];
        double k = Math.Cos(o.Lat * Math.PI / 180) * 6371000.0 * Math.PI / 180, kn = 6371000.0 * Math.PI / 180;
        var p = ring.Select(q => ((q.Lon - o.Lon) * k, (q.Lat - o.Lat) * kn)).ToList();
        var outp = new List<(double E, double N)>();
        int n = p.Count;
        for (int i = 0; i < n; i++)
        {
            var a = p[(i - 1 + n) % n];
            var b = p[i];
            var c = p[(i + 1) % n];
            double lab = Math.Sqrt(Math.Pow(b.Item1 - a.Item1, 2) + Math.Pow(b.Item2 - a.Item2, 2));
            double lbc = Math.Sqrt(Math.Pow(c.Item1 - b.Item1, 2) + Math.Pow(c.Item2 - b.Item2, 2));
            double t1 = lab < 1e-9 ? 0 : Math.Min(cutM, lab / 3) / lab;
            double t2 = lbc < 1e-9 ? 0 : Math.Min(cutM, lbc / 3) / lbc;
            var s0 = (b.Item1 + (a.Item1 - b.Item1) * t1, b.Item2 + (a.Item2 - b.Item2) * t1);
            var s1 = (b.Item1 + (c.Item1 - b.Item1) * t2, b.Item2 + (c.Item2 - b.Item2) * t2);
            for (int j = 0; j <= 6; j++)
            {
                double t = j / 6.0, u = 1 - t;
                outp.Add((u * u * s0.Item1 + 2 * u * t * b.Item1 + t * t * s1.Item1,
                          u * u * s0.Item2 + 2 * u * t * b.Item2 + t * t * s1.Item2));
            }
        }
        return outp.Select(q => (o.Lat + q.N / kn, o.Lon + q.E / k)).ToList();
    }

    protected async Task WriteImportFile(string fileName, string content)
    {
        Directory.CreateDirectory(R.ImportDir);
        await File.WriteAllTextAsync(Path.Combine(R.ImportDir, fileName), content);
        await Cmd("import.scan");
    }

    protected async Task<bool> OpenField(string name)
    {
        await Cmd("field.openOnly", name);
        return await R.WaitUntilAsync(() => R.Vm.IsFieldOpen && R.Vm.CurrentFieldName == name && !R.State.UI.IsBusy);
    }

    /// <summary>Close the field; answer the unsaved-coverage question (save) if it comes.</summary>
    protected async Task CloseField()
    {
        await Cmd("field.close");
        await R.SettleAsync();
        if (await R.ReadAsync(() => R.Vm.IsPromptPending)) await R.AnswerPromptAsync(true);
        if (await R.ReadAsync(() => R.Vm.IsFieldOpen)) await Cmd("coverage.saveJob");
        await R.WaitUntilAsync(() => !R.Vm.IsFieldOpen && !R.State.UI.IsBusy);
    }

    protected Task<FieldSnapshot> Snap() => R.ReadAsync(() => FieldSnapshot.Take(R));

    protected async Task<int> TrackCount() => await R.ReadAsync(() => R.Vm.SavedTracks.Count);

    /// <summary>Compare what the field had before a close with what it has after it is opened
    /// again: everything the operator built must come back as it was.</summary>
    protected void CompareRoundTrip(FieldSnapshot a, FieldSnapshot b, string what)
    {
        void Diff(bool same, Severity sev, string msg) => S.Check(same, sev, what + ": " + msg);

        Diff(a.Outer?.Count == b.Outer?.Count, Severity.Bug, F($"outer boundary had {a.Outer?.Count ?? 0} points, now {b.Outer?.Count ?? 0}"));
        if (a.Outer is { Count: >= 3 } && b.Outer is { Count: >= 3 })
        {
            double aa = Poly.Area(a.Outer), ba = Poly.Area(b.Outer);
            Diff(Math.Abs(aa - ba) <= Math.Max(1, aa * 1e-4), Severity.Bug, F($"outer area was {aa:F1} m², now {ba:F1} m²"));
        }
        Diff(a.Inner.Count == b.Inner.Count, Severity.Bug, F($"inner boundaries were {a.Inner.Count}, now {b.Inner.Count}"));
        Diff(a.InnerFlags.SequenceEqual(b.InnerFlags), Severity.Bug, "inner boundary drive-through / hard flags changed");
        Diff((a.Headland == null) == (b.Headland == null), Severity.Bug, F($"headland was {(a.Headland == null ? "absent" : "present")}, now {(b.Headland == null ? "absent" : "present")}"));
        if (a.Headland is { Count: >= 3 } && b.Headland is { Count: >= 3 })
        {
            double aa = Poly.Area(a.Headland), ba = Poly.Area(b.Headland);
            Diff(Math.Abs(aa - ba) <= Math.Max(1, aa * 1e-3), Severity.Bug, F($"headland area was {aa:F1} m², now {ba:F1} m²"));
        }
        Diff(a.HeadlandSegments.Count == b.HeadlandSegments.Count, Severity.Bug, F($"headland segments were {a.HeadlandSegments.Count}, now {b.HeadlandSegments.Count}"));
        Diff(a.HeadlandSegments.Select(x => x.Name + "/" + x.Offset.ToString("F2", Inv)).SequenceEqual(b.HeadlandSegments.Select(x => x.Name + "/" + x.Offset.ToString("F2", Inv))),
            Severity.Bug, "headland segment names or offsets changed: [" + string.Join(", ", a.HeadlandSegments.Select(x => x.Name + " " + x.Offset.ToString("F1", Inv))) + "] -> [" + string.Join(", ", b.HeadlandSegments.Select(x => x.Name + " " + x.Offset.ToString("F1", Inv))) + "]");
        Diff(a.Tracks.Count == b.Tracks.Count, Severity.Bug, F($"tracks were {a.Tracks.Count}, now {b.Tracks.Count}: lost [{string.Join(", ", a.Tracks.Select(t => t.Name).Except(b.Tracks.Select(t => t.Name)))}], new [{string.Join(", ", b.Tracks.Select(t => t.Name).Except(a.Tracks.Select(t => t.Name)))}]"));
        // By position: names repeat (two A+ lines made in the same minute share a name).
        for (int i = 0; i < Math.Min(a.Tracks.Count, b.Tracks.Count); i++)
        {
            var t = a.Tracks[i];
            var u = b.Tracks[i];
            if (t.Name != u.Name)
            {
                Diff(false, Severity.Bug, F($"track {i} was '{t.Name}', now '{u.Name}' (order or name changed)"));
                continue;
            }
            Diff(t.Type == u.Type, Severity.Bug, $"track '{t.Name}' type {t.Type} -> {u.Type}");
            Diff(t.Points.Count == u.Points.Count, Severity.Bug, F($"track '{t.Name}' had {t.Points.Count} points, now {u.Points.Count}"));
            if (t.Points.Count > 0 && u.Points.Count > 0)
            {
                double d0 = Math.Sqrt(Math.Pow(t.Points[0].E - u.Points[0].E, 2) + Math.Pow(t.Points[0].N - u.Points[0].N, 2));
                double d1 = Math.Sqrt(Math.Pow(t.Points[^1].E - u.Points[^1].E, 2) + Math.Pow(t.Points[^1].N - u.Points[^1].N, 2));
                Diff(d0 < 0.02 && d1 < 0.02, Severity.Bug, F($"track '{t.Name}' moved: A by {d0:F3} m, B by {d1:F3} m"));
            }
            // The selected track's stored nudge is the pass the tractor is on (resume there), so
            // it changes with driving; the others must keep theirs.
            if (t.Name != a.SelectedTrack)
                Diff(Math.Abs(t.Nudge - u.Nudge) < 0.01, Severity.Inconsistency, F($"track '{t.Name}' nudge {t.Nudge:F2} -> {u.Nudge:F2}"));
            Diff(t.Visible == u.Visible, Severity.Inconsistency, $"track '{t.Name}' visibility {t.Visible} -> {u.Visible}");
        }
        Diff(a.TramSystems.Count == b.TramSystems.Count, Severity.Bug, F($"tram systems were {a.TramSystems.Count}, now {b.TramSystems.Count}"));
        Diff(a.TramSystems.SequenceEqual(b.TramSystems), Severity.Bug, "tram system settings changed: " + string.Join("; ", a.TramSystems.Except(b.TramSystems)) + " -> " + string.Join("; ", b.TramSystems.Except(a.TramSystems)));
        Diff(a.TramLines.Count == b.TramLines.Count, Severity.Inconsistency, F($"tram lines were {a.TramLines.Count}, now {b.TramLines.Count}"));
        Diff(a.Flags.Count == b.Flags.Count, Severity.Bug, F($"flags were {a.Flags.Count}, now {b.Flags.Count}"));
        Diff(a.Flags.Select(x => x.Name).SequenceEqual(b.Flags.Select(x => x.Name)), Severity.Inconsistency, "flag names changed: [" + string.Join(", ", a.Flags.Select(x => x.Name)) + "] -> [" + string.Join(", ", b.Flags.Select(x => x.Name)) + "]");
    }
}
