// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

namespace AgOpenWeb.FieldScenarios.Tests;

/// <summary>How the careful operator makes the field's boundary (one way per field, so all
/// of them get used).</summary>
public enum BoundaryMethod { DrawOnMap, DriveAround, KmlNewField, KmlIntoOpenField }

/// <summary>
/// The operator who does everything right, in the order the screens suggest: create the
/// field, make the boundary and the obstacles, the headland, every kind of track, tram lines,
/// flags, a job with some coverage, then close and open again and check that everything came
/// back. Every step is a correct action, so any refusal, exception or broken rule is a finding.
/// </summary>
public sealed class GoodOperator : Operator
{
    private readonly ScenarioField _f;
    private readonly BoundaryMethod _method;
    private readonly string _name;

    public GoodOperator(Scenario s, ScenarioField field, BoundaryMethod method, string fieldName) : base(s)
    {
        _f = field;
        _method = method;
        _name = fieldName;
    }

    public async Task Run()
    {
        var inside = _f.InsidePoint();
        int obstaclesExpected = 0;

        // ---------------------------------------------------------------- 1. field + boundary
        switch (_method)
        {
            case BoundaryMethod.DrawOnMap:
                await S.Step($"create field '{_name}' standing in it", async () => await NewField(_name, inside));
                await S.Step("draw the outer boundary on the map (" + _f.Outer.Count + " taps)", () => DrawBoundary(_f.Outer));
                break;
            case BoundaryMethod.DriveAround:
                await S.Step($"create field '{_name}' standing in it", async () => await NewField(_name, inside));
                await S.Step("record the outer boundary by driving round the field", () => RecordBoundary(_f.Outer, inner: false));
                break;
            case BoundaryMethod.KmlNewField:
                await S.Step("put the KML (outer + obstacles) in the Import folder", () => WriteImportFile(_name + ".kml", _f.ToKml(true, _name)));
                await S.Step($"create field '{_name}' from the KML", async () =>
                {
                    await R.PlaceAsync(inside.Lat, inside.Lon, 0);
                    await Cmd("field.fromKml", _name + ".kml\t" + _name);
                    if (!await R.WaitUntilAsync(() => R.Vm.CurrentFieldName == _name && !R.State.UI.IsBusy))
                        await OpenField(_name);
                });
                obstaclesExpected = _f.Inner.Count;
                break;
            case BoundaryMethod.KmlIntoOpenField:
                await S.Step($"create field '{_name}' standing in it", async () => await NewField(_name, inside));
                await S.Step("put the KML (outer + obstacles) in the Import folder", () => WriteImportFile(_name + "-limits.kml", _f.ToKml(true, _name)));
                await S.Step("Boundary menu > Import KML into the open field", () => Cmd("boundary.importKmlFile", _name + "-limits.kml"));
                obstaclesExpected = _f.Inner.Count;
                break;
        }
        var snap = S.Last;
        S.Check(snap.IsFieldOpen && snap.FieldName == _name, Severity.Bug, $"field '{_name}' is not open after creating it (open: '{snap.FieldName}')");
        S.Check(snap.Outer != null, Severity.Bug, "the field has no outer boundary after making it");
        if (snap.Outer is { Count: >= 3 })
        {
            double want = await SourceAreaM2(_f.Outer);
            double got = Poly.Area(snap.Outer);
            double tol = _method == BoundaryMethod.DriveAround ? 0.03 : 0.002;
            S.Check(Math.Abs(got - want) <= want * tol, Severity.Bug,
                F($"boundary area {got / 10000:F3} ha, the field is {want / 10000:F3} ha ({(got - want) / want * 100:F2} %)"));
        }

        // ---------------------------------------------------------------- 2. obstacles
        if (_method is BoundaryMethod.DrawOnMap or BoundaryMethod.DriveAround)
        {
            var big = _f.BigObstacles().Take(2).ToList();
            foreach (var (ring, k) in big.Select((r, k) => (r, k + 1)))
            {
                var path = Geo.Grow(ring, 0.0);
                await S.Step($"drive round obstacle {k} to record it as an inner boundary ({Geo.PerimeterM(ring):F0} m)",
                    () => RecordBoundary(path, inner: true));
                obstaclesExpected++;
            }
        }
        S.Check(S.Last.Inner.Count == obstaclesExpected, Severity.Bug,
            F($"expected {obstaclesExpected} inner boundaries, the field has {S.Last.Inner.Count}"));
        if (S.Last.Inner.Count > 0)
        {
            await S.Step("Boundary menu: select the first obstacle and make it drive-through", async () =>
            {
                await Cmd("boundary.refresh");
                await Cmd("boundary.select", "1");
                await Cmd("boundary.driveThru");
            });
            S.Check(S.Last.InnerFlags.Count > 0 && S.Last.InnerFlags[0].DriveThrough != _f.Inner.FirstOrDefault().DriveThrough || _method is BoundaryMethod.DrawOnMap or BoundaryMethod.DriveAround,
                Severity.Inconsistency, "drive-through of inner 1 did not change");
        }

        // ---------------------------------------------------------------- 3. headland
        await S.Step("Field Builder > Headland: whole boundary, 12 m", () => Cmd("headland.wholeBoundary", "12"));
        S.Check(S.Last.Headland != null, Severity.Bug, "no headland after 'whole boundary 12 m'");
        if (S.Last.Headland is { Count: >= 3 } && S.Last.Outer is { Count: >= 3 })
        {
            double min = S.Last.Headland.Min(p => Poly.DistToRing(p, S.Last.Outer));
            S.Check(Math.Abs(min - 12) < 0.6, Severity.Bug, F($"headland lies {min:F2} m from the boundary at its closest; asked 12 m"));
        }
        await S.Step("change the headland offset to 16 m", () => Cmd("headland.setOffset", "0,16"));
        if (S.Last.Headland is { Count: >= 3 } && S.Last.Outer is { Count: >= 3 })
        {
            double min = S.Last.Headland.Min(p => Poly.DistToRing(p, S.Last.Outer));
            S.Check(Math.Abs(min - 16) < 0.6, Severity.Bug, F($"headland lies {min:F2} m from the boundary after the change to 16 m"));
        }
        await S.Step("rename the headland segment", () => Cmd("headland.rename", "0,Main headland"));
        S.Check(S.Last.HeadlandSegments.FirstOrDefault()?.Name == "Main headland", Severity.Inconsistency, "headland segment was not renamed");

        // ---------------------------------------------------------------- 4. tracks
        int before = await TrackCount();
        foreach (var t in _f.Tracks.Where(t => t.Points.Count == 2))
        {
            await S.Step($"Quick AB > Lat/Lon A-B: planned line '{t.Name}'",
                () => Cmd("track.latLonAB", LL(t.Points[0]) + "," + LL(t.Points[1])));
            S.Check(await TrackCount() == before + 1, Severity.Bug, $"line '{t.Name}' was not added");
            before = await TrackCount();
            var added = S.Last.Tracks.LastOrDefault();
            if (added != null && added.Points.Count == 2)
            {
                var (ae, an) = await R.ToFieldAsync(t.Points[0].Lat, t.Points[0].Lon);
                var (be, bn) = await R.ToFieldAsync(t.Points[1].Lat, t.Points[1].Lon);
                double wantH = Math.Atan2(be - ae, bn - an);
                double gotH = Math.Atan2(added.Points[1].E - added.Points[0].E, added.Points[1].N - added.Points[0].N);
                double dh = Math.Abs(Math.IEEERemainder(wantH - gotH, 2 * Math.PI)) * 180 / Math.PI;
                S.Check(dh < 0.1, Severity.Bug, F($"line '{t.Name}' heading is {dh:F2}° off what was typed"));
            }
        }

        var (segA, segB, hdg) = InsideSegment(inside, 70);
        await S.Step(F($"stand in the field facing {hdg:F0}° and make an A+ line at the tractor"), async () =>
        {
            await R.PlaceAsync(segA.Lat, segA.Lon, hdg);
            await Cmd("track.refSide", "right");
            await Cmd("track.aPlus");
        });
        S.Check(await TrackCount() == before + 1, Severity.Bug, "A+ at the tractor did not add a line");
        if (S.Last.Tracks.LastOrDefault() is { Points.Count: 2 } ap)
        {
            double gotH = Math.Atan2(ap.Points[1].E - ap.Points[0].E, ap.Points[1].N - ap.Points[0].N) * 180 / Math.PI;
            double vmH = await R.ReadAsync(() => R.State.Vehicle.Heading);
            // The line follows the app's heading; that heading may lag the driven one by a
            // degree after the harness moves the tractor across the field in one jump.
            double dh = Math.Abs(Math.IEEERemainder(gotH - vmH, 360));
            double dd = Math.Abs(Math.IEEERemainder(gotH - hdg, 360));
            S.Check(dh < 0.2 && dd < 1.5, Severity.Bug, F($"A+ line heading {gotH:F1}° but the tractor drove in facing {hdg:F1}° (the app's heading now {vmH:F1}°)"));
        }
        before = await TrackCount();

        await S.Step("A+ with a typed heading of 120°", () => Cmd("track.aPlus", "120"));
        S.Check(await TrackCount() == before + 1, Severity.Bug, "A+ with heading 120 did not add a line");
        before = await TrackCount();

        await S.Step("Quick AB > Lat/Lon + heading 33.5°", () => Cmd("track.latLonHeading", LL(segA) + ",33.5"));
        S.Check(await TrackCount() == before + 1, Severity.Bug, "Lat/Lon + heading did not add a line");
        before = await TrackCount();

        // Lines at 0° (north-south passes), both ways the operator can ask for them.
        foreach (var (what, cmd, arg) in new[]
        {
            ("Quick AB > Lat/Lon + heading 0° (north-south passes)", "track.latLonHeading", LL(segA) + ",0"),
            ("A+ with a typed heading of 0°", "track.aPlus", "0"),
        })
        {
            await S.Step(what, () => Cmd(cmd, arg));
            S.Check(await TrackCount() == before + 1, Severity.Bug, what + ": no line was added");
            if (S.Last.Tracks.LastOrDefault() is { Points.Count: 2 } z)
            {
                double zh = Math.Atan2(z.Points[1].E - z.Points[0].E, z.Points[1].N - z.Points[0].N) * 180 / Math.PI;
                S.Check(Math.Abs(Math.IEEERemainder(zh, 180)) < 0.05, Severity.Bug, F($"{what}: the line runs at {zh:F2}°, not north-south"));
            }
            before = await TrackCount();
        }

        await S.Step("Draw straight line: tap A, tap B on the map", async () =>
        {
            await Cmd("track.drawStraight");
            await Cmd("track.drawPoint", await En(segA));
            await Cmd("track.drawPoint", await En(segB));
        });
        S.Check(await TrackCount() == before + 1, Severity.Bug, "Draw straight did not add a line");
        before = await TrackCount();

        var arc = Arc(inside, 35, 6);
        await S.Step("Draw curve: tap 6 points, Finish", async () =>
        {
            await Cmd("track.drawCurve");
            foreach (var p in arc) await Cmd("track.drawPoint", await En(p));
            await Cmd("track.drawFinish");
        });
        S.Check(await TrackCount() == before + 1, Severity.Bug, "Draw curve did not add a curve");
        if (S.Last.Tracks.LastOrDefault() is { } dc) S.Check(dc.Points.Count >= 6, Severity.Bug, F($"drawn curve has {dc.Points.Count} points for 6 taps"));
        before = await TrackCount();

        await S.Step("Drive AB: Set A, drive 70 m, Set B", async () =>
        {
            await R.PlaceAsync(segA.Lat, segA.Lon, hdg);
            await Cmd("track.refSide", "left");
            await Cmd("track.driveAB");
            await Cmd("track.setABGps");
            await R.DriveAsync(new[] { segA, segB });
            await Cmd("track.setABGps");
        });
        S.Check(await TrackCount() == before + 1, Severity.Bug, "Drive AB did not add a line");
        if (S.Last.Tracks.LastOrDefault() is { Points.Count: 2 } da)
        {
            double gotH = Math.Atan2(da.Points[1].E - da.Points[0].E, da.Points[1].N - da.Points[0].N) * 180 / Math.PI;
            double dh = Math.Abs(Math.IEEERemainder(gotH - hdg, 360));
            S.Check(dh < 1.0, Severity.Bug, F($"Drive-AB line heading {gotH:F1}°, driven {hdg:F1}°"));
        }
        before = await TrackCount();

        var sPath = SCurve(segA, hdg, 70);
        await S.Step("Record curve: Set start, drive an S of 70 m, Set end", async () =>
        {
            await R.PlaceAsync(sPath[0].Lat, sPath[0].Lon, hdg);
            await Cmd("track.refSide", "right");
            await Cmd("track.recordCurve");
            await R.DriveAsync(sPath, 0.5);
            await Cmd("track.finishCurve");
        });
        S.Check(await TrackCount() == before + 1, Severity.Bug, "Record curve did not add a curve");
        before = await TrackCount();

        var nearEdge = NearEdgePoint();
        await S.Step("stand near the boundary and make a line along the nearest edge", async () =>
        {
            await R.PlaceAsync(nearEdge.P.Lat, nearEdge.P.Lon, nearEdge.EdgeHeading);
            await Cmd("track.createFromBoundary");
        });
        S.Check(await TrackCount() == before + 1, Severity.Bug, "'boundary edge' did not add a line");
        before = await TrackCount();

        await S.Step("Boundary curve (whole boundary)", () => Cmd("track.boundaryCurve"));
        S.Check(await TrackCount() == before + 1, Severity.Bug, "'boundary curve' did not add a curve");
        before = await TrackCount();

        int i1 = 0, i2 = Math.Max(2, _f.Outer.Count / 3);
        await S.Step($"Bnd. Curve: tap boundary vertices {i1} and {i2}", async () =>
            await Cmd("track.boundaryCurveSeg", await En(_f.Outer[i1]) + "," + await En(_f.Outer[i2])));
        S.Check(await TrackCount() == before + 1, Severity.Bug, "'Bnd. Curve' A-B did not add a curve");
        before = await TrackCount();

        await S.Step("All edges: a line along every boundary edge", () => Cmd("track.allEdges"));
        S.Check(await TrackCount() > before, Severity.Bug, "'all edges' added nothing");
        int edges = await TrackCount() - before;
        before = await TrackCount();

        await S.Step("Water pivot from a typed centre", () => Cmd("track.latLonPivot", LL(inside)));
        S.Check(await TrackCount() == before + 1, Severity.Bug, "pivot from lat/lon did not add a track");
        before = await TrackCount();

        // ---------------------------------------------------------------- 5. track management
        await S.Step("Tracks: rename track 0 to 'Main line'", () => Cmd("track.rename", "0,Main line"));
        S.Check(S.Last.Tracks.FirstOrDefault()?.Name == "Main line", Severity.Inconsistency, "track 0 was not renamed");
        var t0 = S.Last.Tracks.FirstOrDefault();
        await S.Step("Tracks: swap A/B of track 0", () => Cmd("track.swapAB", "0"));
        if (t0 is { Points.Count: 2 } && S.Last.Tracks.FirstOrDefault() is { Points.Count: 2 } t0b)
            S.Check(Math.Abs(t0.Points[0].E - t0b.Points[1].E) < 0.01 && Math.Abs(t0.Points[0].N - t0b.Points[1].N) < 0.01, Severity.Bug, "swap A/B did not swap the points");
        await S.Step("Tracks: hide track 1, then show it again", async () =>
        {
            await Cmd("track.setVisible", "1,0");
            await Cmd("track.setVisible", "1,1");
        });
        await S.Step("Tracks: select track 0 (steer on it)", () => Cmd("track.select", "0"));
        S.Check(S.Last.SelectedTrack == "Main line", Severity.Bug, $"selected track is '{S.Last.SelectedTrack}', wanted 'Main line'");
        await S.Step("nudge right twice, then reset the nudge", async () =>
        {
            await Cmd("track.nudgeRight");
            await Cmd("track.nudgeRight");
            await Cmd("track.resetNudge");
        });
        S.Check(Math.Abs(S.Last.Tracks.FirstOrDefault()?.Nudge ?? 0) < 0.001, Severity.Bug, "nudge did not go back to 0 after Reset");
        await S.Step("extend A, shrink B of the selected line", async () =>
        {
            await Cmd("track.extendA");
            await Cmd("track.shrinkB");
        });
        var curveIdx = S.Last.Tracks.FindIndex(t => t.Type == "Curve");
        if (curveIdx >= 0)
            await S.Step($"edit the curve on the map: move its first point 2 m east and save", async () =>
            {
                var c = S.Last.Tracks[curveIdx];
                var pts = c.Points.Select((p, k) => k == 0 ? F($"{p.E + 2:F3},{p.N:F3}") : F($"{p.E:F3},{p.N:F3}"));
                await Cmd("track.editSave", curveIdx.ToString(Inv) + ";" + string.Join(";", pts));
            });
        if (curveIdx >= 0 && S.Last.Tracks.Count > curveIdx)
        {
            var c = S.Last.Tracks[curveIdx];
            S.Check(c.Type == "Curve", Severity.Inconsistency, $"after the on-map edit track {curveIdx} is a {c.Type}");
        }
        int countBeforeDelete = await TrackCount();
        await S.Step("delete the last track (the pivot)", () => Cmd("track.delete", (countBeforeDelete - 1).ToString(Inv)));
        S.Check(await TrackCount() == countBeforeDelete - 1, Severity.Bug, "track was not deleted");

        // ---------------------------------------------------------------- 6. tram lines
        await S.Step("select 'Main line' and add a tram system on it", async () =>
        {
            await Cmd("track.select", "0");
            await Cmd("tram.add");
        });
        S.Check(S.Last.TramSystems.Count == 1 && S.Last.TramSystems[0].RefTrack == "Main line", Severity.Bug,
            $"tram system should reference 'Main line', has '{S.Last.TramSystems.FirstOrDefault()?.RefTrack}'");
        await S.Step("tram: width 24 m (3 boom widths), 1 pass, on", async () =>
        {
            await Cmd("tram.set", "0,width,24");
            await Cmd("tram.set", "0,passes,1");
            await Cmd("tram.set", "0,enabled,1");
        });
        S.Check(S.Last.TramLines.Count > 0, Severity.Bug, "no tram lines built for a 24 m tram system on the main line");
        await S.Step("add a second tram system on the boundary, edge mode, 2 passes", async () =>
        {
            await Cmd("tram.add");
            await Cmd("tram.set", "1,ref,(Boundary)");
            await Cmd("tram.set", "1,mode,1");
            await Cmd("tram.set", "1,passes,2");
        });
        S.Check(S.Last.TramSystems.Count == 2, Severity.Bug, F($"{S.Last.TramSystems.Count} tram systems, wanted 2"));
        await S.Step("tram display on", () => Cmd("config.set", "tram.display:true"));

        // ---------------------------------------------------------------- 7. flags
        var (fe, fn) = await R.ToFieldAsync(inside.Lat, inside.Lon);
        await S.Step("place a flag on the map, rename it 'Stone', colour it red", async () =>
        {
            await Cmd("flag.placeAt", F($"{fe:F3},{fn:F3}"));
            await Cmd("flag.rename", "0,Stone");
            await Cmd("flag.setColor", "0,Red");
        });
        S.Check(S.Last.Flags.Count == 1 && S.Last.Flags[0].Name == "Stone", Severity.Inconsistency, "flag not placed/renamed");
        await S.Step("place a flag at the tractor", () => Cmd("flag.placeHere"));
        S.Check(S.Last.Flags.Count == 2, Severity.Bug, F($"{S.Last.Flags.Count} flags after 'flag here', wanted 2"));

        // ---------------------------------------------------------------- 8. contour
        await S.Step("contour on, drive 60 m, contour off", async () =>
        {
            await Cmd("contour.toggle");
            await R.DriveAsync(new[] { segA, segB });
            await Cmd("contour.toggle");
        });
        S.Check(!S.Last.IsContourOn, Severity.Inconsistency, "contour mode still on after toggling it off");

        // ---------------------------------------------------------------- 9. a job with coverage
        await S.Step("start a job 'Spraying' on this field", () => Cmd("field.startJob", _name + "\tSpraying\tscenario\tSpraying " + _name));
        await S.Step("sections on (master), drive a pass of 70 m, sections off", async () =>
        {
            await R.PlaceAsync(segA.Lat, segA.Lon, hdg);
            await Cmd("section.master");
            await R.DriveAsync(new[] { segA, segB });
            await Cmd("section.master");
        });

        // ---------------------------------------------------------------- 10. close + open
        var beforeClose = await Snap();
        await S.Step("close the field (save coverage)", CloseField);
        S.Check(!S.Last.IsFieldOpen, Severity.Bug, "field still open after Close");
        await S.Step($"open '{_name}' again", async () => await OpenField(_name));
        CompareRoundTrip(beforeClose, S.Last, "close + open");

        // ---------------------------------------------------------------- 11. from existing
        string copy = _name + " copy";
        await S.Step($"New field from '{_name}' (flags, headland, lines; no coverage)", async () =>
        {
            await CloseField();
            await Cmd("field.fromExisting", _name + "\t" + copy + "\t1\t0\t1\t1");
            await R.WaitUntilAsync(() => R.Vm.CurrentFieldName == copy && !R.State.UI.IsBusy);
        });
        S.Check(S.Last.FieldName == copy, Severity.Bug, $"'{copy}' is not open after From Existing (open: '{S.Last.FieldName}')");
        if (S.Last.FieldName == copy)
        {
            var c = S.Last;
            S.Check(c.Outer?.Count == beforeClose.Outer?.Count, Severity.Bug, F($"copy has {c.Outer?.Count ?? 0} boundary points, the source {beforeClose.Outer?.Count ?? 0}"));
            S.Check(c.Inner.Count == beforeClose.Inner.Count, Severity.Bug, F($"copy has {c.Inner.Count} inner boundaries, the source {beforeClose.Inner.Count}"));
            S.Check((c.Headland != null) == (beforeClose.Headland != null), Severity.Bug, "copy lost the headland");
            S.Check(c.HeadlandSegments.Count == beforeClose.HeadlandSegments.Count, Severity.Inconsistency, F($"copy has {c.HeadlandSegments.Count} headland segments, the source {beforeClose.HeadlandSegments.Count}"));
            S.Check(c.Tracks.Count == beforeClose.Tracks.Count, Severity.Bug, F($"copy has {c.Tracks.Count} tracks, the source {beforeClose.Tracks.Count}"));
            S.Check(c.Flags.Count == beforeClose.Flags.Count, Severity.Bug, F($"copy has {c.Flags.Count} flags, the source {beforeClose.Flags.Count}"));
            S.Check(c.TramSystems.Count == beforeClose.TramSystems.Count, Severity.Inconsistency, F($"copy has {c.TramSystems.Count} tram systems, the source {beforeClose.TramSystems.Count}"));
        }
        await S.Step("close the copy", CloseField);

        // ---------------------------------------------------------------- 12. Drive In
        await S.Step("stand in the field with nothing open and press Drive In", async () =>
        {
            await R.PlaceAsync(inside.Lat, inside.Lon, 0);
            await Cmd("field.driveIn");
            await R.SettleAsync();
            if (!await R.ReadAsync(() => R.Vm.IsFieldOpen))
                await Cmd("field.driveInOpen", _name); // two fields here (the copy): pick ours from the list
            await R.WaitUntilAsync(() => R.Vm.IsFieldOpen && !R.State.UI.IsBusy);
        });
        S.Check(S.Last.FieldName is var fnm && (fnm == _name || fnm == copy), Severity.Bug, $"Drive In opened '{S.Last.FieldName}'");

        await S.Step("close the field at the end of the day", CloseField);
        _ = edges;
    }

    // ------------------------------------------------------------------ geometry for the story

    private async Task<double> SourceAreaM2(IReadOnlyList<(double Lat, double Lon)> ring)
    {
        var en = new List<(double E, double N)>();
        foreach (var p in ring) en.Add(await R.ToFieldAsync(p.Lat, p.Lon));
        return Poly.Area(en);
    }

    private bool Clear((double Lat, double Lon) p) =>
        Geo.Inside(p, _f.Outer) && !_f.Inner.Any(i => Geo.Inside(p, Geo.Grow(i.Ring, 3)));

    /// <summary>A straight run through <paramref name="mid"/> inside the field and clear of obstacles.</summary>
    private ((double Lat, double Lon) A, (double Lat, double Lon) B, double Heading) InsideSegment((double Lat, double Lon) mid, double len)
    {
        for (double l = len; l >= 15; l -= 5)
            foreach (double h in new[] { 30.0, 60, 0, 90, 120, 150, 10, 45, 75, 105, 135, 165 })
            {
                double r = h * Math.PI / 180;
                var a = Geo.Offset(mid, -Math.Sin(r) * l / 2, -Math.Cos(r) * l / 2);
                var b = Geo.Offset(mid, Math.Sin(r) * l / 2, Math.Cos(r) * l / 2);
                bool ok = true;
                for (int k = 0; k <= 20 && ok; k++)
                {
                    double t = k / 20.0;
                    ok = Clear((a.Lat + (b.Lat - a.Lat) * t, a.Lon + (b.Lon - a.Lon) * t));
                }
                if (ok) return (a, b, h);
            }
        return (Geo.Offset(mid, 0, -5), Geo.Offset(mid, 0, 5), 0);
    }

    private List<(double Lat, double Lon)> Arc((double Lat, double Lon) c, double radius, int n)
    {
        for (double r = radius; r >= 8; r -= 4)
        {
            var pts = Enumerable.Range(0, n).Select(k =>
            {
                double a = (-60 + 120.0 * k / (n - 1)) * Math.PI / 180;
                return Geo.Offset(c, Math.Sin(a) * r, Math.Cos(a) * r - r * 0.5);
            }).ToList();
            if (pts.All(Clear)) return pts;
        }
        return Enumerable.Range(0, n).Select(k => Geo.Offset(c, k * 2.0, k * 1.0)).ToList();
    }

    private List<(double Lat, double Lon)> SCurve((double Lat, double Lon) start, double headingDeg, double len)
    {
        var pts = new List<(double Lat, double Lon)>();
        double h = headingDeg * Math.PI / 180;
        for (int k = 0; k <= 14; k++)
        {
            double along = len * k / 14.0;
            double side = 4 * Math.Sin(2 * Math.PI * k / 14.0);
            pts.Add(Geo.Offset(start, Math.Sin(h) * along + Math.Cos(h) * side, Math.Cos(h) * along - Math.Sin(h) * side));
        }
        return pts.All(Clear) ? pts : pts.Take(2).ToList();
    }

    /// <summary>A point 10 m inside the longest boundary edge, and that edge's heading.</summary>
    private ((double Lat, double Lon) P, double EdgeHeading) NearEdgePoint()
    {
        int best = 0;
        double bestLen = 0;
        for (int i = 0; i < _f.Outer.Count; i++)
        {
            double l = Geo.DistanceM(_f.Outer[i], _f.Outer[(i + 1) % _f.Outer.Count]);
            if (l > bestLen) { bestLen = l; best = i; }
        }
        var a = _f.Outer[best];
        var b = _f.Outer[(best + 1) % _f.Outer.Count];
        double h = Geo.BearingDeg(a, b);
        var mid = ((a.Lat + b.Lat) / 2, (a.Lon + b.Lon) / 2);
        foreach (double side in new[] { 90.0, -90 })
        {
            double r = (h + side) * Math.PI / 180;
            var p = Geo.Offset(mid, Math.Sin(r) * 10, Math.Cos(r) * 10);
            if (Geo.Inside(p, _f.Outer)) return (p, h);
        }
        return (mid, h);
    }
}
