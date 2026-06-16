// Binary wire codec for the map feed. Replaces SignalR/JSON with compact
// little-endian frames (BinaryWriter is little-endian on every platform; the
// client decodes with DataView littleEndian=true). One frame = [u8 type][payload].
//
// This is the transport's encoder ONLY — it does not change the DTOs the
// projector produces or the JS objects the renderer consumes (transport.js
// decodes back to the same shapes). The seam in REMOTE_WEB_UI_SPLIT.md §Transport.
//
// Geometry points are f32 (sub-cm at field scale, half the bytes); vehicle pose
// E/N stay f64 (live position precision). Coverage cells — the bandwidth hog —
// go out as raw i32 triples (~12 B/cell vs ~30 B as JSON).

using System.Text;

namespace AgValoniaGPS.RemoteServer;

public static class WireCodec
{
    public const byte Scene = 1, Tick = 2, CoverageInit = 3, CoverageCells = 4, Status = 5,
        ControlState = 6, Hello = 7, Config = 8, Profiles = 9;

    public static byte[] EncodeProfiles(ProfilesDto p)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Profiles);
        WriteStr(w, p.ActiveVehicle);
        WriteStr(w, p.ActiveTool);
        w.Write(p.Vehicles.Count);
        foreach (var e in p.Vehicles) { WriteStr(w, e.Name); WriteStr(w, e.Preview); }
        w.Write(p.Tools.Count);
        foreach (var e in p.Tools) { WriteStr(w, e.Name); WriteStr(w, e.Preview); }
        return ms.ToArray();
    }

    public static byte[] EncodeConfig(ConfigDto c)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Config);
        var v = c.Vehicle;
        WriteStr(w, v.Name);
        w.Write(v.Type);               // i32
        w.Write(v.HitchType);          // i32
        w.Write(v.HitchLength);        // f64
        w.Write(v.Wheelbase);
        w.Write(v.TrackWidth);
        w.Write(v.AntennaPivot);
        w.Write(v.AntennaHeight);
        w.Write(v.AntennaOffset);
        var g = c.Gps;
        w.Write((byte)(g.IsDualGps ? 1 : 0));
        w.Write(g.DualHeadingOffset);  // f64
        w.Write(g.DualReverseDistance);
        w.Write((byte)(g.AutoDualFix ? 1 : 0));
        w.Write(g.DualSwitchSpeed);
        w.Write(g.MinGpsStep);
        w.Write(g.FixToFixDistance);
        w.Write(g.HeadingFusionWeight);
        w.Write((byte)(g.ReverseDetection ? 1 : 0));
        w.Write((byte)(g.RtkLostAlarm ? 1 : 0));
        w.Write(g.RtkLostAction);      // i32
        var r = c.Roll;
        w.Write(r.RollZero);           // f64
        w.Write(r.RollFilter);
        w.Write((byte)(r.IsRollInvert ? 1 : 0));
        // Tool/Implement tab.
        var t = c.Tool;
        w.Write(t.Type);               // i32
        w.Write(t.HitchType);
        w.Write(t.HitchLength);        // f64
        w.Write(t.TrailingHitchLength);
        w.Write(t.TankTrailingHitchLength);
        w.Write(t.Length);
        w.Write(t.LookAheadOn);
        w.Write(t.LookAheadOff);
        w.Write(t.TurnOffDelay);
        w.Write(t.Offset);
        w.Write(t.Overlap);
        w.Write(t.TrailingToolToPivotLength);
        w.Write((byte)(t.IsSectionsNotZones ? 1 : 0));
        w.Write(t.NumSections);        // i32
        w.Write(t.DefaultSectionWidth);// f64
        w.Write(t.SectionWidths.Count);
        foreach (var sw in t.SectionWidths) w.Write(sw); // f64
        w.Write(t.Zones);              // i32
        w.Write(t.ZoneRanges.Count);
        foreach (var z in t.ZoneRanges) w.Write(z);      // i32
        w.Write((byte)(t.IsMultiColoredSections ? 1 : 0));
        w.Write(t.SectionColors.Count);
        foreach (var col in t.SectionColors) w.Write(col); // i32 (0xRRGGBB)
        w.Write(t.SingleCoverageColor);// i32
        w.Write((byte)(t.IsSectionOffWhenOut ? 1 : 0));
        w.Write((byte)(t.IsHeadlandSectionControl ? 1 : 0));
        w.Write(t.MinCoverage);        // i32
        w.Write(t.SlowSpeedCutoff);    // f64
        w.Write(t.CoverageMargin);
        w.Write((byte)(t.IsWorkSwitchEnabled ? 1 : 0));
        w.Write((byte)(t.IsWorkSwitchActiveLow ? 1 : 0));
        w.Write((byte)(t.IsWorkSwitchManualSections ? 1 : 0));
        w.Write((byte)(t.IsSteerSwitchEnabled ? 1 : 0));
        w.Write((byte)(t.IsSteerSwitchManualSections ? 1 : 0));
        w.Write(t.TotalWidth);         // f64
        // U-Turn tab.
        var u = c.Uturn;
        w.Write(u.Style);              // i32
        w.Write(u.Extension);          // f64
        w.Write(u.Smoothing);          // i32
        w.Write(u.Radius);             // f64
        w.Write(u.DistanceFromBoundary);
        // Tram tab.
        var tr = c.Tram;
        w.Write(tr.Passes);            // i32
        w.Write((byte)(tr.Display ? 1 : 0));
        w.Write(tr.Line);              // i32
        // Machine Control tab.
        var m = c.Machine;
        w.Write((byte)(m.HydraulicLiftEnabled ? 1 : 0));
        w.Write(m.RaiseTime);          // i32
        w.Write(m.LookAhead);          // f64
        w.Write(m.LowerTime);          // i32
        w.Write((byte)(m.InvertRelay ? 1 : 0));
        w.Write(m.User1); w.Write(m.User2); w.Write(m.User3); w.Write(m.User4); // i32
        w.Write(m.PinAssignments.Count);
        foreach (var p in m.PinAssignments) w.Write(p);  // i32 (PinFunction)
        return ms.ToArray();
    }

    public static byte[] EncodeScene(SceneDto s)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Scene);
        w.Write(s.Version);          // i64
        w.Write(s.OriginLat);        // f64
        w.Write(s.OriginLon);        // f64
        w.Write((byte)(s.HasField ? 1 : 0));
        WriteStr(w, s.FieldName);

        w.Write(s.Boundaries.Count);
        foreach (var ring in s.Boundaries) WritePts(w, ring);

        w.Write(s.Tracks.Count);
        foreach (var t in s.Tracks)
        {
            WriteStr(w, t.Id);
            WriteStr(w, t.Name);
            w.Write(t.Type);         // i32
            WritePts(w, t.Points);
        }

        WriteOptPts(w, s.Headland);
        WriteOptPts(w, s.GuidanceLine);

        w.Write(s.ToolSections.Count);
        foreach (var sec in s.ToolSections) { w.Write((float)sec.Left); w.Write((float)sec.Right); }

        WriteOptPts(w, s.UTurnPath);
        WriteOptPts(w, s.NextTrack);

        w.Write(s.Flags.Count);
        foreach (var fl in s.Flags) { w.Write((float)fl.E); w.Write((float)fl.N); WriteStr(w, fl.ColorHex); }

        if (s.Imagery is { } im)
        {
            w.Write((byte)1);
            w.Write(im.MinE); w.Write(im.MinN); w.Write(im.MaxE); w.Write(im.MaxN);
            w.Write(im.Version);
        }
        else w.Write((byte)0);

        return ms.ToArray();
    }

    public static byte[] EncodeTick(TickDto t)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Tick);
        w.Write(t.SceneVersion);     // i64
        w.Write(t.Pose.E);           // f64
        w.Write(t.Pose.N);           // f64
        w.Write((float)t.Pose.Heading);
        w.Write((float)t.Pose.Speed);
        w.Write((byte)t.Fix);
        w.Write(t.Sections.Length);
        foreach (var st in t.Sections) w.Write(st); // already a 0/1/2 display-state byte
        w.Write((float)t.CrossTrackError);
        w.Write((byte)(t.GuidanceActive ? 1 : 0));
        WriteStr(w, t.LineLabel);
        WriteStr(w, t.ActiveTrackName ?? ""); // empty string == null on the client
        w.Write(t.ToolE);            // f64
        w.Write(t.ToolN);            // f64
        w.Write((float)t.ToolHeading);
        w.Write((byte)(t.ToolReady ? 1 : 0));
        // Operational state (right-nav toolbar).
        w.Write((byte)(t.IsAutoSteerEngaged ? 1 : 0));
        w.Write((byte)(t.AutoSteerAvailable ? 1 : 0));
        w.Write((byte)(t.IsContourMode ? 1 : 0));
        w.Write((byte)(t.IsSectionAutoMaster ? 1 : 0));
        w.Write((byte)(t.IsSectionManualAll ? 1 : 0));
        w.Write((byte)(t.IsYouTurnEnabled ? 1 : 0));
        w.Write((byte)(t.TurnIsLeft ? 1 : 0));
        w.Write((float)t.DistanceToTrigger);
        w.Write((byte)(t.IsActiveTrackClosed ? 1 : 0));
        w.Write((float)t.Roll);
        // Bottom-nav field-tools (Phase 8).
        w.Write((byte)(t.HeadlandOn ? 1 : 0));
        w.Write((byte)(t.SectionInHeadland ? 1 : 0));
        w.Write((byte)(t.AutoTrack ? 1 : 0));
        w.Write((byte)t.SkipRows);
        w.Write((byte)(t.SkipRowsOn ? 1 : 0));
        w.Write((byte)t.TramMode);
        return ms.ToArray();
    }

    public static byte[] EncodeStatus(StatusDto s)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Status);
        w.Write(s.FixQuality);       // i32
        WriteStr(w, s.FixQualityText);
        w.Write((float)s.Age);
        w.Write(s.SatelliteCount);   // i32
        w.Write((byte)(s.IsMetric ? 1 : 0));
        w.Write((byte)(s.GpsOk ? 1 : 0));
        w.Write((byte)(s.ImuOk ? 1 : 0));
        w.Write((byte)(s.AutoSteerOk ? 1 : 0));
        w.Write((byte)(s.MachineOk ? 1 : 0));
        WriteStr(w, s.ImuIp);
        WriteStr(w, s.AutoSteerIp);
        WriteStr(w, s.MachineIp);
        w.Write((byte)(s.GpsConfigured ? 1 : 0));
        w.Write((byte)(s.ImuConfigured ? 1 : 0));
        w.Write((byte)(s.AutoSteerConfigured ? 1 : 0));
        w.Write((byte)(s.MachineConfigured ? 1 : 0));
        WriteStr(w, s.JobName);
        w.Write(s.WorkedAreaSqM);     // f64
        // GPS-detail card (Phase 5).
        w.Write(s.Latitude);          // f64
        w.Write(s.Longitude);         // f64
        w.Write((float)s.Altitude);
        w.Write((float)s.Hdop);
        // Simulator panel (Phase 6).
        w.Write((byte)(s.SimEnabled ? 1 : 0));
        w.Write((float)s.SimSpeedKph);
        w.Write((float)s.SimSteerAngle);
        w.Write((byte)(s.Sim10x ? 1 : 0));
        return ms.ToArray();
    }

    // Sent once per connection so the client learns its own id (to compare against
    // ControlState.HolderId and know whether it holds actuation authority).
    public static byte[] EncodeHello(string clientId)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Hello);
        WriteStr(w, clientId);
        return ms.ToArray();
    }

    public static byte[] EncodeControlState(ControlStateDto s)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(ControlState);
        w.Write((byte)(s.Held ? 1 : 0));
        WriteStr(w, s.HolderId);
        WriteStr(w, s.HolderName);
        return ms.ToArray();
    }

    public static byte[] EncodeCoverageInit(CoverageInitDto c)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(CoverageInit);
        w.Write(c.CellSize);         // f64
        w.Write(c.OriginE);          // f64
        w.Write(c.OriginN);          // f64
        w.Write(c.Width);            // i32
        w.Write(c.Height);           // i32
        return ms.ToArray();
    }

    public static byte[] EncodeCoverageCells(CoverageCellsDto c)
    {
        var cells = c.Cells;
        // [u8 type][i32 intCount][i32 × intCount] — block-copied for speed.
        var buf = new byte[1 + 4 + cells.Length * 4];
        buf[0] = CoverageCells;
        BitConverter.TryWriteBytes(buf.AsSpan(1, 4), cells.Length);
        Buffer.BlockCopy(cells, 0, buf, 5, cells.Length * 4); // int[] → bytes, little-endian
        return buf;
    }

    private static void WritePts(BinaryWriter w, IReadOnlyList<Vec2Dto> pts)
    {
        w.Write(pts.Count);
        foreach (var p in pts) { w.Write((float)p.E); w.Write((float)p.N); }
    }

    private static void WriteOptPts(BinaryWriter w, IReadOnlyList<Vec2Dto>? pts)
    {
        if (pts is null) { w.Write((byte)0); return; }
        w.Write((byte)1);
        WritePts(w, pts);
    }

    private static void WriteStr(BinaryWriter w, string? s)
    {
        var b = Encoding.UTF8.GetBytes(s ?? "");
        w.Write(b.Length);           // i32 byte length
        w.Write(b);
    }
}
