// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

namespace AgOpenWeb.FieldScenarios.Tests;

/// <summary>
/// The operator who does everything wrong: the wrong order (headland before a boundary, tram
/// lines with no field), impossible values (NaN, negative widths, A = B), shapes that cannot be
/// a field (a bow-tie, three points in a row, a boundary 1 m²), half-finished actions (cancel a
/// drawing, close the field while recording), and things that pull the rug out from under
/// other things (rename or delete the track a tram system hangs on, redraw the boundary
/// under an existing headland). Each mischief is one step; the invariants say whether the app
/// kept its data sound, and <see cref="Scenario.ExpectFeedback"/> whether it told the operator.
///
/// Some of these values cannot be typed in the web client as it is (its inputs check numbers);
/// they are still sent, because the host is the last line: any browser on the LAN can send any
/// command, and a client bug can too. The report marks which ones the client already stops.
/// </summary>
public sealed class BadOperator : Operator
{
    private readonly ScenarioField _f;
    private int _n;

    public BadOperator(Scenario s, ScenarioField field) : base(s) { _f = field; }

    private Task<FieldSnapshot> Bad(string what, Func<Task> act) => S.Step(what, act, Expect.Refused);

    private async Task BadSilentCheck(string what, Func<Task> act)
    {
        await Bad(what, act);
        S.ExpectFeedback();
    }

    /// <summary>A fresh small field with a correct boundary to misbehave on.</summary>
    private async Task Fresh(string label, bool headland = false, bool tracks = false)
    {
        string name = $"Bad {++_n:00} {label}";
        await S.Step($"(set-up) new field '{name}' with its boundary", async () =>
        {
            if (await R.ReadAsync(() => R.Vm.IsFieldOpen)) await CloseField();
            await NewField(name, _f.InsidePoint());
            await DrawBoundary(_f.Outer);
            if (headland) await Cmd("headland.wholeBoundary", "12");
            if (tracks)
            {
                var t = _f.Tracks.First(x => x.Points.Count == 2);
                await Cmd("track.latLonAB", LL(t.Points[0]) + "," + LL(t.Points[1]));
                await Cmd("track.select", "0");
            }
        }, Expect.Any);
    }

    public async Task Run()
    {
        await NoFieldOpen();
        await FieldNames();
        await BoundaryShapes();
        await BoundaryRecording();
        await HeadlandMischief();
        await TrackMischief();
        await TramMischief();
        await PullTheRug();
        await FieldAndJobMischief();
        // Last: two fields whose names differ only in capitals break the field list for the
        // rest of the session (see the finding), so nothing may come after it.
        await CaseOnlyNames();
        if (await R.ReadAsync(() => R.Vm.IsFieldOpen)) await S.Step("(end) close the field", CloseField, Expect.Any);
    }

    // ------------------------------------------------------------------ 0. nothing open
    private async Task NoFieldOpen()
    {
        await S.Step("(set-up) make sure no field is open", async () =>
        {
            if (await R.ReadAsync(() => R.Vm.IsFieldOpen)) await CloseField();
            await R.PlaceAsync(_f.InsidePoint().Lat, _f.InsidePoint().Lon, 0);
        }, Expect.Any);
        var t = _f.Tracks.First(x => x.Points.Count == 2);
        await BadSilentCheck("no field: headland whole boundary 12 m", () => Cmd("headland.wholeBoundary", "12"));
        await BadSilentCheck("no field: add a tram system", () => Cmd("tram.add"));
        await BadSilentCheck("no field: A+ line", () => Cmd("track.aPlus"));
        await BadSilentCheck("no field: Lat/Lon AB line", () => Cmd("track.latLonAB", LL(t.Points[0]) + "," + LL(t.Points[1])));
        await BadSilentCheck("no field: draw a boundary on the map", async () => await DrawBoundary(_f.Outer));
        await BadSilentCheck("no field: drive around (record boundary)", () => Cmd("boundary.driveAround"));
        await BadSilentCheck("no field: place a flag on the map", () => Cmd("flag.placeAt", "10,10"));
        await Bad("no field: contour on and drive 60 m", async () =>
        {
            var p = _f.InsidePoint();
            await R.PlaceAsync(p.Lat, p.Lon, 90);
            await Cmd("contour.toggle");
            await R.DriveAsync(new[] { p, Geo.Offset(p, 60, 0) });
        });
        S.ExpectFeedback();
        if (S.Last.IsContourOn) await S.Step("(undo) contour off", () => Cmd("contour.toggle"), Expect.Any);
        await BadSilentCheck("no field: open a field that does not exist", () => Cmd("field.openOnly", "No such field"));
        await BadSilentCheck("no field: start a job on a field that does not exist", () => Cmd("field.startJob", "No such field\tSpraying\t\tJob"));
        // What the mischief left behind must not leak into the next field.
        string name = $"Bad {++_n:00} after no-field mischief";
        await S.Step($"open a new field '{name}': it must start empty", async () => await NewField(name, _f.InsidePoint()), Expect.Works);
        S.Check(S.Last.TramSystems.Count == 0, Severity.Bug, $"a tram system added with no field open turned up in the new field '{name}' ({S.Last.TramSystems.Count})");
        S.Check(S.Last.Tracks.Count == 0, Severity.Bug, $"a line made with no field open turned up in the new field '{name}' ({S.Last.Tracks.Count})");
        S.Check(S.Last.Outer == null, Severity.Bug, "a boundary drawn with no field open turned up in the new field");
        S.Check(S.Last.Headland == null, Severity.Bug, "a headland made with no field open turned up in the new field");
        S.Check(S.Last.Flags.Count == 0, Severity.Bug, F($"a flag placed with no field open turned up in the new field '{name}' ({S.Last.Flags.Count})"));
        var dir = S.Last.FieldDirectory;
        await S.Step("(set-up) close it", CloseField, Expect.Any);
        var contours = dir == null ? null : Path.Combine(dir, "contours.geojson");
        S.Check(contours == null || !File.Exists(contours) || !File.ReadAllText(contours).Contains("LineString", StringComparison.Ordinal), Severity.Bug,
            $"a contour strip driven with no field open was saved into the next field '{name}' (contours.geojson)");
    }

    // ------------------------------------------------------------------ 1. names
    private async Task FieldNames()
    {
        var at = _f.InsidePoint();
        await R.PlaceAsync(at.Lat, at.Lon, 0);
        async Task TryName(string label, string name)
        {
            int before = Directory.Exists(R.FieldsDir) ? Directory.GetDirectories(R.FieldsDir).Length : 0;
            await Bad($"new field named {label}", async () =>
            {
                await Cmd("field.new", name);
                await R.SettleAsync();
            });
            S.ExpectFeedback();
            int after = Directory.Exists(R.FieldsDir) ? Directory.GetDirectories(R.FieldsDir).Length : 0;
            var escaped = Directory.GetDirectories(R.DataRoot, "*", SearchOption.TopDirectoryOnly)
                .Concat(Directory.GetDirectories(Path.Combine(R.DataRoot, "AgOpenWeb")))
                .Where(d => Path.GetFileName(d).Contains("escape", StringComparison.OrdinalIgnoreCase)).ToList();
            S.Check(escaped.Count == 0, Severity.Bug, $"a field folder was made outside Fields: {string.Join(", ", escaped)}");
            if (after > before && await R.ReadAsync(() => R.Vm.IsFieldOpen)) await CloseField();
        }
        await TryName("'' (empty)", "");
        await TryName("'   ' (spaces)", "   ");
        await TryName("'../escape'", "../escape");
        await TryName("'a/b'", "a/b");
        await TryName("'CON' (Windows keeps it)", "CON");
        await TryName("300 × 'x'", new string('x', 300));
        await TryName("'Tab\\there' (a tab splits the job command)", "Tab\there");
        await S.Step("(set-up) a field called 'Vinha'", async () => { await NewField("Vinha", at); await CloseField(); }, Expect.Any);
        await Bad("new field 'Vinha' again (it exists)", () => Cmd("field.new", "Vinha"));
        S.ExpectFeedback();
        if (await R.ReadAsync(() => R.Vm.IsFieldOpen)) await CloseField();
    }

    // ------------------------------------------------------------------ 9. capitals
    private async Task CaseOnlyNames()
    {
        var at = _f.InsidePoint();
        if (await R.ReadAsync(() => R.Vm.IsFieldOpen)) await CloseField();
        // A name that differs only in capitals: one folder on Windows and macOS, two on Linux.
        await Bad("new field 'vinha' ('Vinha' exists; same name, small letters)", async () =>
        {
            await R.PlaceAsync(at.Lat, at.Lon, 0);
            await Cmd("field.new", "vinha");
            await R.SettleAsync();
        });
        bool both = Directory.Exists(Path.Combine(R.FieldsDir, "Vinha")) && Directory.Exists(Path.Combine(R.FieldsDir, "vinha"))
                    && Directory.GetDirectories(R.FieldsDir).Count(d => string.Equals(Path.GetFileName(d), "vinha", StringComparison.OrdinalIgnoreCase)) == 2;
        S.Check(!both, Severity.Inconsistency, "'Vinha' and 'vinha' became two fields (the app compares field names ignoring capitals everywhere else; on Windows and macOS they are one folder)");
        if (await R.ReadAsync(() => R.Vm.IsFieldOpen)) await CloseField();
        var target = Directory.GetDirectories(R.FieldsDir).Select(Path.GetFileName).FirstOrDefault(n => n!.StartsWith("Bad 02", StringComparison.Ordinal)) ?? "Olival";
        await S.Step($"then open another field, '{target}', from the field list", async () => await OpenField(target!), Expect.Works);
        S.Check(S.Last.FieldName == target, Severity.Bug,
            $"with 'Vinha' and 'vinha' both on disk, opening '{target}' from the field list failed: Fields & Jobs throws while building its list, so no field can be opened, started, resumed or deleted there");
    }

    // ------------------------------------------------------------------ 2. boundary shapes
    private async Task BoundaryShapes()
    {
        await Fresh("shapes");
        var c = _f.InsidePoint();
        var (ce, cn) = await R.ToFieldAsync(c.Lat, c.Lon);
        string P(params (double E, double N)[] pts) => string.Join(";", pts.Select(p => F($"{ce + p.E:F3},{cn + p.N:F3}")));

        await BadSilentCheck("draw a boundary with 2 points", () => Cmd("boundary.fromMapPoints", P((0, 0), (50, 0))));
        await BadSilentCheck("draw a boundary of 3 points in a straight row", () => Cmd("boundary.fromMapPoints", P((0, 0), (50, 0), (100, 0))));
        await BadSilentCheck("draw a bow-tie (the edges cross)", () => Cmd("boundary.fromMapPoints", P((0, 0), (100, 100), (100, 0), (0, 100))));
        await BadSilentCheck("tap the same point 5 times", () => Cmd("boundary.fromMapPoints", P((0, 0), (0, 0), (0, 0), (0, 0), (0, 0))));
        await BadSilentCheck("draw a boundary of 1 m²", () => Cmd("boundary.fromMapPoints", P((0, 0), (1, 0), (1, 1), (0, 1))));
        await BadSilentCheck("draw a boundary with a NaN point", () => Cmd("boundary.fromMapPoints", "NaN,NaN;" + P((0, 0), (100, 0), (100, 100))));
        S.Check(S.LastStep!.Messages.All(m => !m.Contains(".NET", StringComparison.Ordinal)), Severity.Improvement,
            "a boundary point that is not a number is refused with the JSON serializer's own error text (\".NET number values such as positive and negative infinity cannot be written as valid JSON…\") instead of a message for the operator");
        await BadSilentCheck("draw a boundary with an Infinity point", () => Cmd("boundary.fromMapPoints", "Infinity,0;" + P((0, 0), (100, 0), (100, 100))));
        await BadSilentCheck("draw a boundary 9000 km away", () => Cmd("boundary.fromMapPoints", "9000000,0;9000100,0;9000100,100;9000000,100"));
        await BadSilentCheck("draw a boundary with garbage between the points", () => Cmd("boundary.fromMapPoints", "abc;" + P((0, 0), (100, 0)) + ";x,y;" + P((100, 100))));
        await S.Step("(set-up) the real boundary again", () => DrawBoundary(_f.Outer), Expect.Any);
        await BadSilentCheck("Boundary menu: select boundary 99 and delete it", async () => { await Cmd("boundary.select", "99"); await Cmd("boundary.delete"); });
        await BadSilentCheck("Boundary menu: select boundary -1 and toggle drive-through", async () => { await Cmd("boundary.select", "-1"); await Cmd("boundary.driveThru"); });
        await BadSilentCheck("Boundary menu: select 'abc'", () => Cmd("boundary.select", "abc"));
        await BadSilentCheck("Import KML: a file that is not there", () => Cmd("boundary.importKmlFile", "nothing-here.kml"));
        await Bad("Import KML: a file that is not KML", async () =>
        {
            await WriteImportFile("broken.kml", "this is not <xml");
            await Cmd("boundary.importKmlFile", "broken.kml");
        });
        S.ExpectFeedback();
        await Bad("Import KML: a bow-tie polygon", async () =>
        {
            var p = _f.InsidePoint();
            var a = Geo.Offset(p, -50, -50); var b = Geo.Offset(p, 50, 50); var cc = Geo.Offset(p, 50, -50); var d = Geo.Offset(p, -50, 50);
            var kml = new ScenarioField { Name = "bowtie", Outer = new() { a, b, cc, d } }.ToKml(false);
            await WriteImportFile("bowtie.kml", kml);
            await Cmd("boundary.importKmlFile", "bowtie.kml");
        });
        S.ExpectFeedback();
        await Bad("Import KML: an obstacle that lies outside the field", async () =>
        {
            var far = Geo.Offset(_f.Outer[0], 200, 200);
            var ring = new List<(double, double)> { far, Geo.Offset(far, 20, 0), Geo.Offset(far, 20, 20), Geo.Offset(far, 0, 20) };
            var sf = new ScenarioField { Name = "outside", Outer = _f.Outer, Inner = new() { (false, ring) } };
            await WriteImportFile("outside.kml", sf.ToKml(true));
            await Cmd("boundary.importKmlFile", "outside.kml");
        });
        S.ExpectFeedback();
    }

    // ------------------------------------------------------------------ 3. recording
    private async Task BoundaryRecording()
    {
        await Fresh("recording");
        var c = _f.InsidePoint();
        await BadSilentCheck("Drive around, then Stop at once (no points)", async () => { await Cmd("boundary.driveAround"); await Cmd("boundary.stop"); });
        await BadSilentCheck("Drive around, Record, drive 3 m, Stop", async () =>
        {
            await R.PlaceAsync(c.Lat, c.Lon, 0);
            await Cmd("boundary.driveAround");
            await Cmd("boundary.toggleRecording");
            await R.DriveAsync(new[] { c, Geo.Offset(c, 0, 3) });
            await Cmd("boundary.stop");
        });
        await Bad("press Record without Drive around first, drive a square, Stop", async () =>
        {
            await R.PlaceAsync(c.Lat, c.Lon, 0);
            await Cmd("boundary.toggleRecording");
            await R.DriveAsync(new[] { c, Geo.Offset(c, 0, 30), Geo.Offset(c, 30, 30), Geo.Offset(c, 30, 0), c });
            await Cmd("boundary.stop");
        });
        S.Check(S.Last.Outer is { Count: > 10 } o && Poly.Area(o) > 10000, Severity.Bug,
            F($"Record without Drive around replaced the field's boundary with a 30 m square (outer now {S.Last.Outer?.Count ?? 0} points, {(S.Last.Outer is { } oo ? Poly.Area(oo) : 0):F0} m²)"));
        await S.Step("(set-up) the real boundary again", () => DrawBoundary(_f.Outer), Expect.Any);
        await BadSilentCheck("Add point / Undo / Clear with no recording going on", async () =>
        {
            await Cmd("boundary.addPoint");
            await Cmd("boundary.undo");
            await Cmd("boundary.clear");
        });
        await Bad("drive round an 'obstacle' that is outside the field and save it", async () =>
        {
            var far = Geo.Offset(_f.Outer[0], 0, 0);
            var dir = Geo.BearingDeg(_f.Centroid, _f.Outer[0]) * Math.PI / 180;
            var o = Geo.Offset(far, Math.Sin(dir) * 60, Math.Cos(dir) * 60);
            var ring = new List<(double Lat, double Lon)> { o, Geo.Offset(o, 25, 0), Geo.Offset(o, 25, 25), Geo.Offset(o, 0, 25) };
            await RecordBoundary(ring, inner: true);
        });
        S.ExpectFeedback();
        await Bad("drive round an obstacle across the field edge", async () =>
        {
            var e = _f.Outer[0];
            var ring = new List<(double Lat, double Lon)> { Geo.Offset(e, -15, -15), Geo.Offset(e, 15, -15), Geo.Offset(e, 15, 15), Geo.Offset(e, -15, 15) };
            await RecordBoundary(ring, inner: true);
        });
        S.ExpectFeedback();
        await Bad("record the outer boundary at the implement edge (default) while driving on the line, clockwise and anticlockwise", async () =>
        {
            if (await R.ReadAsync(() => R.Vm.IsDrawAtPivot)) await Cmd("boundary.toggleAntennaTool");
            await RecordBoundary(_f.Outer, inner: false, atPivot: false);
        });
        double want = 0;
        {
            var en = new List<(double E, double N)>();
            foreach (var p in _f.Outer) en.Add(await R.ToFieldAsync(p.Lat, p.Lon));
            want = Poly.Area(en);
        }
        if (S.Last.Outer is { Count: >= 3 } rec)
            S.Check(false, Severity.Improvement, F($"recorded at the implement edge: {Poly.Area(rec) / 10000:F2} ha for a {want / 10000:F2} ha field — the app does not say on which side, or how far, the line is drawn while the tractor drives on the edge"));
        await S.Step("(set-up) the real boundary again", () => DrawBoundary(_f.Outer), Expect.Any);
        await Bad("start recording, then close the field mid-way", async () =>
        {
            await R.PlaceAsync(c.Lat, c.Lon, 0);
            await Cmd("boundary.driveAround");
            await Cmd("boundary.toggleRecording");
            await R.DriveAsync(new[] { c, Geo.Offset(c, 0, 20), Geo.Offset(c, 20, 20) });
            await CloseField();
        });
        S.Check(!S.Last.IsBoundaryRecording, Severity.Bug, "boundary recording is still running after the field was closed");
        string next = $"Bad {++_n:00} after recording";
        await Bad($"open another field '{next}', drive a square, press Stop", async () =>
        {
            await NewField(next, c);
            await R.DriveAsync(new[] { c, Geo.Offset(c, 0, 25), Geo.Offset(c, 25, 25), Geo.Offset(c, 25, 0), c });
            await Cmd("boundary.stop");
        });
        S.Check(S.Last.Outer == null, Severity.Bug,
            F($"a boundary recorded partly in the previous field was saved into '{next}' ({S.Last.Outer?.Count ?? 0} points)"));
    }

    // ------------------------------------------------------------------ 4. headland
    private async Task HeadlandMischief()
    {
        await S.Step("(set-up) close", CloseField, Expect.Any);
        string name = $"Bad {++_n:00} headland first";
        await S.Step($"(set-up) new field '{name}' without a boundary", async () => await NewField(name, _f.InsidePoint()), Expect.Any);
        await BadSilentCheck("headland before any boundary exists", () => Cmd("headland.wholeBoundary", "12"));
        await BadSilentCheck("headland line from map points with no boundary", () => Cmd("headland.fromMapPoints", "line,12,0,0,50,50"));

        await Fresh("headland");
        await Bad("headland whole boundary with offset 0 (uses a default)", () => Cmd("headland.wholeBoundary", "0"));
        if (S.Last.HeadlandSegments.LastOrDefault() is { } zs)
            S.Check(false, Severity.Improvement, F($"offset 0 was turned into {zs.Offset:F1} m without saying so"));
        await Bad("headland whole boundary with offset -5", () => Cmd("headland.wholeBoundary", "-5"));
        await S.Step("(set-up) delete all headland", () => Cmd("headland.deleteAll"), Expect.Any);
        await Bad("headland whole boundary 120 m (meant 12)", () => Cmd("headland.wholeBoundary", "120"));
        S.ExpectFeedback();
        await S.Step("(set-up) delete all headland", () => Cmd("headland.deleteAll"), Expect.Any);
        await Bad("headland whole boundary 5000 m (bigger than the field)", () => Cmd("headland.wholeBoundary", "5000"));
        S.ExpectFeedback();
        await S.Step("(set-up) delete all headland", () => Cmd("headland.deleteAll"), Expect.Any);
        await Bad("headland whole boundary 'NaN'", () => Cmd("headland.wholeBoundary", "NaN"));
        S.ExpectFeedback();
        await S.Step("(set-up) delete all headland", () => Cmd("headland.deleteAll"), Expect.Any);
        await S.Step("(set-up) headland whole boundary 12 m", () => Cmd("headland.wholeBoundary", "12"), Expect.Any);
        await Bad("whole boundary headland again (a second one on top, 20 m)", () => Cmd("headland.wholeBoundary", "20"));
        await BadSilentCheck("set offset of segment 0 to -3", () => Cmd("headland.setOffset", "0,-3"));
        await BadSilentCheck("set offset of segment 7 (there is none)", () => Cmd("headland.setOffset", "7,10"));
        await Bad("set offset of segment 0 to 'NaN'", () => Cmd("headland.setOffset", "0,NaN"));
        S.ExpectFeedback();
        await Bad("set offset of segment 0 to 'Infinity'", () => Cmd("headland.setOffset", "0,Infinity"));
        await S.Step("(set-up) delete all headland, whole boundary 12 m", async () => { await Cmd("headland.deleteAll"); await Cmd("headland.wholeBoundary", "12"); }, Expect.Any);
        await BadSilentCheck("headland line with both taps on the same spot", () => Cmd("headland.fromMapPoints", "line,12,0,0,0,0"));
        await Bad("headland curve from 2 taps 5 km outside the field", () => Cmd("headland.fromMapPoints", "curve,12,5000,5000,-5000,5000"));
        await BadSilentCheck("delete headland segment -1, then 99", async () => { await Cmd("headland.delete", "-1"); await Cmd("headland.delete", "99"); });
        await BadSilentCheck("rename headland segment 0 to '   '", () => Cmd("headland.rename", "0,   "));
        await Bad("edit headland segment 0 on the map with NaN points", () => Cmd("headland.editSave", "0,NaN,NaN,NaN,NaN"));
        S.ExpectFeedback();
        await S.Step("delete all headland, then close and open the field", async () =>
        {
            await Cmd("headland.deleteAll");
            var name2 = await R.ReadAsync(() => R.Vm.CurrentFieldName);
            await CloseField();
            await OpenField(name2);
        }, Expect.Works);
        S.Check(S.Last.Headland == null, Severity.Bug, "the deleted headland came back after close + open");
    }

    // ------------------------------------------------------------------ 5. tracks
    private async Task TrackMischief()
    {
        await Fresh("tracks", tracks: true);
        var c = _f.InsidePoint();
        await BadSilentCheck("Lat/Lon AB with A = B", () => Cmd("track.latLonAB", LL(c) + "," + LL(c)));
        await BadSilentCheck("Lat/Lon AB with latitude 91", () => Cmd("track.latLonAB", "91,0," + LL(c)));
        await BadSilentCheck("Lat/Lon AB with NaN", () => Cmd("track.latLonAB", "NaN,NaN," + LL(c)));
        await BadSilentCheck("Lat/Lon AB on the other side of the world", () => Cmd("track.latLonAB", "-39.42,172.46,-39.43,172.47"));
        await Bad("Lat/Lon + heading 720°", () => Cmd("track.latLonHeading", LL(c) + ",720"));
        await BadSilentCheck("Lat/Lon + heading NaN", () => Cmd("track.latLonHeading", LL(c) + ",NaN"));
        await BadSilentCheck("Lat/Lon + heading 1e308", () => Cmd("track.latLonHeading", LL(c) + ",1e308"));
        var off = Geo.Offset(c, 20, 20);
        await S.Step("(set-up) drive 30 m away from the field's origin", () => R.PlaceAsync(off.Lat, off.Lon, 45), Expect.Any);
        await BadSilentCheck("A+ with heading 'NaN'", () => Cmd("track.aPlus", "NaN"));
        await BadSilentCheck("A+ with heading 'Infinity'", () => Cmd("track.aPlus", "Infinity"));
        int n0 = (await Snap()).Tracks.Count;
        await Bad("Draw straight: tap the same spot twice", async () =>
        {
            await Cmd("track.drawStraight");
            await Cmd("track.drawPoint", "5,5");
            await Cmd("track.drawPoint", "5,5");
        });
        S.Check(S.Last.Tracks.Count == n0, Severity.Bug,
            F($"Draw straight with A and B on the same spot made a line anyway ('{S.Last.Tracks.LastOrDefault()?.Name}'): its heading is atan2(0,0) = 0°, a north-south line the operator never drew (Lat/Lon AB refuses A and B closer than 1 m)"));
        await Cmd("track.drawCancel");
        await BadSilentCheck("Draw curve: 1 tap, Finish", async () =>
        {
            await Cmd("track.drawCurve");
            await Cmd("track.drawPoint", "5,5");
            await Cmd("track.drawFinish");
        });
        await Cmd("track.drawCancel");
        await BadSilentCheck("Draw curve: Finish with no taps", async () => { await Cmd("track.drawCurve"); await Cmd("track.drawFinish"); });
        await Cmd("track.drawCancel");
        await BadSilentCheck("Draw curve: 3 taps on one spot, Finish", async () =>
        {
            await Cmd("track.drawCurve");
            for (int i = 0; i < 3; i++) await Cmd("track.drawPoint", "7,7");
            await Cmd("track.drawFinish");
        });
        await Cmd("track.drawCancel");
        int before = (await Snap()).Tracks.Count;
        await Bad("Draw curve: 2 taps, Cancel, then tap the map twice more", async () =>
        {
            await Cmd("track.drawCurve");
            await Cmd("track.drawPoint", "10,10");
            await Cmd("track.drawPoint", "20,20");
            await Cmd("track.drawCancel");
            await Cmd("track.drawPoint", "30,30");
            await Cmd("track.drawPoint", "40,40");
        });
        S.Check(S.Last.Tracks.Count == before, Severity.Bug, F($"taps after Cancel still made a track ({before} -> {S.Last.Tracks.Count})"));
        n0 = (await Snap()).Tracks.Count;
        await Bad("Drive AB: Set A and Set B without moving (a double tap)", async () =>
        {
            await R.PlaceAsync(off.Lat, off.Lon, 45);
            await Cmd("track.driveAB");
            await Cmd("track.setABGps");
            await Cmd("track.setABGps");
        });
        S.Check(S.Last.Tracks.Count == n0, Severity.Bug,
            F($"Drive AB with Set A and Set B at the same spot made a line anyway ('{S.Last.Tracks.LastOrDefault()?.Name}', heading 0°, north-south, whatever way the tractor faces)"));
        await Cmd("track.drawCancel");
        await BadSilentCheck("Record curve: start and finish without moving", async () =>
        {
            await Cmd("track.recordCurve");
            await Cmd("track.finishCurve");
        });
        await Cmd("track.drawCancel");
        await BadSilentCheck("Bnd. Curve with A = B", () => Cmd("track.boundaryCurveSeg", "0,0,0,0"));
        await BadSilentCheck("Bnd. Curve with both taps 3 km away", () => Cmd("track.boundaryCurveSeg", "3000,3000,3100,3000"));
        await Bad("All edges twice", async () => { await Cmd("track.allEdges"); await Cmd("track.allEdges"); });
        await BadSilentCheck("water pivot 2 km outside the field", () => Cmd("track.latLonPivot", LL(Geo.Offset(c, 2000, 0))));
        await BadSilentCheck("rename track 99", () => Cmd("track.rename", "99,x"));
        await BadSilentCheck("rename track 0 to '' (nothing)", () => Cmd("track.rename", "0,"));
        await Bad("rename track 1 to the name of track 0", async () =>
        {
            var n0 = (await Snap()).Tracks[0].Name;
            await Cmd("track.rename", "1," + n0);
        });
        S.ExpectFeedback();
        await BadSilentCheck("delete track -1, 99 and 'abc'", async () =>
        {
            await Cmd("track.delete", "-1");
            await Cmd("track.delete", "99");
            await Cmd("track.delete", "abc");
        });
        await BadSilentCheck("select track 99, activate track -5", async () => { await Cmd("track.select", "99"); await Cmd("track.activate", "-5"); });
        await BadSilentCheck("show/hide: '0,2', 'abc,1', '99,0'", async () =>
        {
            await Cmd("track.setVisible", "0,2");
            await Cmd("track.setVisible", "abc,1");
            await Cmd("track.setVisible", "99,0");
        });
        await BadSilentCheck("swap A/B of track 99", () => Cmd("track.swapAB", "99"));
        await BadSilentCheck("edit track 0 on the map with a NaN point", () => Cmd("track.editSave", "0;NaN,NaN;10,10"));
        await BadSilentCheck("edit track 0 on the map: both points on one spot", () => Cmd("track.editSave", "0;10,10;10,10"));
        await BadSilentCheck("edit track 99 on the map", () => Cmd("track.editSave", "99;0,0;10,10"));
        await S.Step("(set-up) show track 0 again and select it", async () => { await Cmd("track.setVisible", "0,1"); await Cmd("track.select", "0"); }, Expect.Any);
        S.Check(S.Last.SelectedTrack != null, Severity.Improvement, "(harness) no track selected for the nudge / extend tests");
        await Bad("nudge right 300 times", async () => { for (int i = 0; i < 300; i++) R.Server.CommandHandler!("track.nudgeRight", ""); await R.SettleAsync(); });
        await Cmd("track.resetNudge");
        var len0 = (await Snap()).Tracks[0].Points is var p0 && p0.Count == 2 ? Math.Sqrt(Math.Pow(p0[1].E - p0[0].E, 2) + Math.Pow(p0[1].N - p0[0].N, 2)) : 0;
        await Bad("shrink B 200 times (5 m each)", async () => { for (int i = 0; i < 200; i++) R.Server.CommandHandler!("track.shrinkB", ""); await R.SettleAsync(); });
        if (S.Last.Tracks.Count > 0 && S.Last.Tracks[0].Points is { Count: 2 } p1)
        {
            double h0 = Math.Atan2(p0[1].E - p0[0].E, p0[1].N - p0[0].N);
            double h1 = Math.Atan2(p1[1].E - p1[0].E, p1[1].N - p1[0].N);
            double len1 = Math.Sqrt(Math.Pow(p1[1].E - p1[0].E, 2) + Math.Pow(p1[1].N - p1[0].N, 2));
            S.Check(Math.Abs(Math.IEEERemainder(h1 - h0, 2 * Math.PI)) < 0.01, Severity.Bug,
                F($"shrinking B past A turned the line round: heading {h0 * 180 / Math.PI:F1}° -> {h1 * 180 / Math.PI:F1}°, length {len0:F0} -> {len1:F0} m"));
        }
        await Bad("extend A 400 times (5 m each)", async () => { for (int i = 0; i < 400; i++) R.Server.CommandHandler!("track.extendA", ""); await R.SettleAsync(); });
        await Bad("smooth the AB line 5 times", async () => { for (int i = 0; i < 5; i++) await Cmd("track.smooth"); });
        await Bad("A+ right after creating a field (tractor exactly at the field's origin)", async () =>
        {
            await CloseField();
            await NewField($"Bad {++_n:00} at origin", c, 30);
            await Cmd("track.aPlus");
        });
        S.Check(S.Last.Tracks.Count == 1, Severity.Bug, "A+ standing on the spot where the field was created made no line (that spot is E 0, N 0, which the app reads as 'no GPS')");
        await Bad("flag here right after creating a field", () => Cmd("flag.placeHere"));
        S.Check(S.Last.Flags.Count == 1, Severity.Bug, "'flag here' standing on the spot where the field was created was refused as 'no GPS position'");
    }

    // ------------------------------------------------------------------ 6. tram
    private async Task TramMischief()
    {
        await Fresh("tram", tracks: true);
        await S.Step("(set-up) a tram system on track 0", () => Cmd("tram.add"), Expect.Any);
        await BadSilentCheck("tram width 0", () => Cmd("tram.set", "0,width,0"));
        await BadSilentCheck("tram width -5", () => Cmd("tram.set", "0,width,-5"));
        await BadSilentCheck("tram width NaN", () => Cmd("tram.set", "0,width,NaN"));
        await BadSilentCheck("tram width Infinity", () => Cmd("tram.set", "0,width,Infinity"));
        await BadSilentCheck("tram width 0.1 m (the sprayer is 8 m)", () => Cmd("tram.set", "0,width,0.1"));
        await S.Step("(set-up) tram width 24", () => Cmd("tram.set", "0,width,24"), Expect.Any);
        await BadSilentCheck("tram offset NaN", () => Cmd("tram.set", "0,offset,NaN"));
        await S.Step("(set-up) tram offset 0", () => Cmd("tram.set", "0,offset,0"), Expect.Any);
        await BadSilentCheck("tram offset 1e6 m", () => Cmd("tram.set", "0,offset,1000000"));
        await S.Step("(set-up) tram offset 0", () => Cmd("tram.set", "0,offset,0"), Expect.Any);
        await BadSilentCheck("tram passes -3", () => Cmd("tram.set", "0,passes,-3"));
        await BadSilentCheck("tram passes 100000", () => Cmd("tram.set", "0,passes,100000"));
        await S.Step("(set-up) tram passes 1", () => Cmd("tram.set", "0,passes,1"), Expect.Any);
        await BadSilentCheck("tram passes 'abc'", () => Cmd("tram.set", "0,passes,abc"));
        await BadSilentCheck("tram reference a track that does not exist", () => Cmd("tram.set", "0,ref,No such line"));
        await S.Step("(set-up) tram reference track 0 again", async () => { var n0 = (await Snap()).Tracks[0].Name; await Cmd("tram.set", "0,ref," + n0); }, Expect.Any);
        await BadSilentCheck("tram set an unknown setting", () => Cmd("tram.set", "0,colour,blue"));
        await BadSilentCheck("tram set on system 99", () => Cmd("tram.set", "99,width,24"));
        await BadSilentCheck("tram delete -1 and 99", async () => { await Cmd("tram.delete", "-1"); await Cmd("tram.delete", "99"); });
        await Bad("tram mode '7', direction '9'", async () => { await Cmd("tram.set", "0,mode,7"); await Cmd("tram.set", "0,dir,9"); });
        await Bad("add 40 tram systems", async () => { for (int i = 0; i < 40; i++) await Cmd("tram.add"); });
        await S.Step("(set-up) delete them again", async () => { for (int i = 40; i >= 1; i--) await Cmd("tram.delete", i.ToString(Inv)); }, Expect.Any);
        await Bad("turn the tram system off", () => Cmd("tram.set", "0,enabled,0"));
        await S.Step("(set-up) turn it on", () => Cmd("tram.set", "0,enabled,1"), Expect.Any);
        await BadSilentCheck("config: tram.width -1 and NaN", async () => { await Cmd("config.set", "tram.width:-1"); await Cmd("config.set", "tram.width:NaN"); });
        await BadSilentCheck("config: tram.passes -4", () => Cmd("config.set", "tram.passes:-4"));
    }

    // ------------------------------------------------------------------ 7. pull the rug
    private async Task PullTheRug()
    {
        await Fresh("rug", headland: true, tracks: true);
        await S.Step("(set-up) tram system on track 0, flag, second line", async () =>
        {
            await Cmd("tram.add");
            await Cmd("tram.set", "0,width,24");
            var c = _f.InsidePoint();
            await Cmd("track.latLonHeading", LL(c) + ",45");
            await Cmd("flag.placeAt", "1,1");
        }, Expect.Any);
        var n0 = S.Last.Tracks[0].Name;
        int linesBefore = S.Last.TramLines.Count;
        await Bad($"rename track '{n0}' that the tram system follows", () => Cmd("track.rename", "0,Renamed line"));
        S.Check(S.Last.TramLines.Count == linesBefore, Severity.Bug, F($"tram lines {linesBefore} -> {S.Last.TramLines.Count} after renaming their track"));
        S.Check(S.Last.TramSystems.All(t => t.RefTrack == null || S.Last.Tracks.Any(x => x.Name == t.RefTrack)), Severity.Bug,
            "renaming a track cuts the tram system off it (systems point to tracks by name): the tram lines disappear without a word");
        await S.Step("(set-up) rename it back", () => Cmd("track.rename", "0," + n0), Expect.Any);
        await Bad($"delete track '{n0}' that the tram system follows", () => Cmd("track.delete", "0"));
        S.ExpectFeedback();
        await Bad("redraw the outer boundary much smaller (headland, tram, obstacles stay)", async () =>
        {
            var c = _f.Centroid;
            var small = _f.Outer.Select(p => (c.Lat + (p.Lat - c.Lat) * 0.5, c.Lon + (p.Lon - c.Lon) * 0.5)).ToList();
            await DrawBoundary(small);
        });
        S.ExpectFeedback();
        await Bad("delete the outer boundary (select 0, delete) with headland and tracks", async () =>
        {
            await Cmd("boundary.refresh");
            await Cmd("boundary.select", "0");
            await Cmd("boundary.delete");
            await R.SettleAsync();
            if (await R.ReadAsync(() => R.Vm.IsPromptPending)) await R.AnswerPromptAsync(true);
        });
        await Bad("delete all tracks while the tram system and the steered line use them", async () => await Cmd("track.deleteAll"));
        await Bad("close and open the field after all that", async () =>
        {
            var name = await R.ReadAsync(() => R.Vm.CurrentFieldName);
            await CloseField();
            await OpenField(name);
        });
    }

    // ------------------------------------------------------------------ 8. fields and jobs
    private async Task FieldAndJobMischief()
    {
        await Fresh("fields");
        var name = S.Last.FieldName;
        await Bad("delete the field that is open", () => Cmd("field.deleteField", name));
        S.ExpectFeedback();
        S.Check(S.Last.IsFieldOpen && Directory.Exists(Path.Combine(R.FieldsDir, name)), Severity.Bug, "the open field was deleted under the operator");
        await BadSilentCheck("delete a field that does not exist", () => Cmd("field.deleteField", "No such field"));
        await BadSilentCheck("resume a job that does not exist", () => Cmd("field.resumeJob", name + "\tNo such job"));
        await BadSilentCheck("From Existing: source does not exist", () => Cmd("field.fromExisting", "No such field\tCopy X\t1\t1\t1\t1"));
        await BadSilentCheck("From Existing: new name = an existing field", () => Cmd("field.fromExisting", name + "\tVinha\t1\t1\t1\t1"));
        await BadSilentCheck("From Existing: too few parts", () => Cmd("field.fromExisting", name));
        await BadSilentCheck("From KML: file not there", () => Cmd("field.fromKml", "nothing.kml\tKml X"));
        await Bad("open two fields at once (second before the first has loaded)", async () =>
        {
            await CloseField();
            R.Server.CommandHandler!("field.openOnly", "Vinha");
            R.Server.CommandHandler!("field.openOnly", name);
            await R.SettleAsync();
            await Task.Delay(500);
            await R.SettleAsync();
        });
        S.Check(S.Last.FieldName == name, Severity.Inconsistency, $"after opening 'Vinha' then '{name}' at once, '{S.Last.FieldName}' is open");
        await Bad("close twice", async () => { await Cmd("field.close"); await Cmd("field.close"); });
        await Bad("start a job on a field while another is open", async () =>
        {
            await OpenField(name);
            await Cmd("field.startJob", "Vinha\tSpraying\t\tJob V");
            await R.SettleAsync();
        });
        await Bad("the field folder disappears while the field is open (USB stick pulled), then add a line", async () =>
        {
            await CloseField();
            await NewField($"Bad {++_n:00} folder gone", _f.InsidePoint());
            await DrawBoundary(_f.Outer);
            var dir = await R.ReadAsync(() => R.State.Field.ActiveField?.DirectoryPath);
            if (dir != null) Directory.Delete(dir, true);
            await Cmd("track.aPlus", "10");
            await Cmd("headland.wholeBoundary", "12");
            await CloseField();
        });
    }
}
