// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

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

namespace AgOpenWeb.RemoteServer;

public static class WireCodec
{
    public const byte Scene = 1, Tick = 2, CoverageInit = 3, CoverageCells = 4, Status = 5,
        ControlState = 6, Hello = 7, Config = 8, Profiles = 9, Wizard = 10, NtripProfiles = 11,
        FieldOps = 12, AgShare = 13, AppInfo = 14, FieldTools = 15, RecordedPath = 16, Boundary = 17,
        Sound = 18, Pong = 19, CoverageEdge = 20, ViewPrefs = 21, Prompt = 22, Toast = 23, DrivePick = 24, HardwareMessage = 25,
        Vt = 26, VtImage = 27, NtripMountpoints = 28;

    /// <summary>One-shot alert: tells the client to play sound effect
    /// <paramref name="effectId"/> (the <c>SoundEffect</c> enum value). Pushed
    /// event-driven, not part of the periodic tick.</summary>
    public static byte[] EncodeSound(byte effectId)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Sound);
        w.Write(effectId);
        return ms.ToArray();
    }

    /// <summary>The host's pending confirm/error dialog (#109). Re-sent whenever it
    /// changes and in every client's seed; Kind 0 clears it.</summary>
    public static byte[] EncodePrompt(PromptDto p)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Prompt);
        w.Write(p.Seq);                   // i32
        w.Write((byte)p.Kind);
        WriteStr(w, p.Title);
        WriteStr(w, p.Message);
        WriteStr(w, p.ConfirmLabel);
        WriteStr(w, p.CancelLabel);
        WriteStr(w, p.CheckboxLabel);
        w.Write((byte)(p.CheckboxChecked ? 1 : 0));
        return ms.ToArray();
    }

    /// <summary>One-shot notification text (a refusal or failure, #109). Pushed
    /// event-driven, like <see cref="EncodeSound"/>; not part of the seed.</summary>
    /// <summary>Module hardware message (PGN 221, #110): text, seconds, warning.</summary>
    public static byte[] EncodeHardwareMessage(string text, int seconds, bool warning)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(HardwareMessage);
        WriteStr(w, text);
        w.Write(seconds);
        w.Write((byte)(warning ? 1 : 0));
        return ms.ToArray();
    }

    /// <summary>A virtual terminal message for a VT card: the terminal it is about (0 to 3; 255
    /// for the host's own <c>link</c> state), then one JSON object, relayed as the terminal wrote
    /// it (screen, edit, workingSets, alarms, audio, ...).</summary>
    public static byte[] EncodeVt(int terminal, string json)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Vt);
        w.Write((byte)(terminal is >= 0 and < 255 ? terminal : 255));
        WriteStr(w, json);
        return ms.ToArray();
    }

    /// <summary>A virtual terminal picture: its id, size, and the pixels as RGBA, rows top
    /// first. Sent once, before the first screen that uses it.</summary>
    public static byte[] EncodeVtImage(uint id, int width, int height, ReadOnlySpan<byte> rgba)
    {
        var frame = new byte[13 + rgba.Length];
        frame[0] = VtImage;
        BitConverter.TryWriteBytes(frame.AsSpan(1), id);
        BitConverter.TryWriteBytes(frame.AsSpan(5), width);
        BitConverter.TryWriteBytes(frame.AsSpan(9), height);
        rgba.CopyTo(frame.AsSpan(13));
        return frame;
    }

    public static byte[] EncodeToast(string message)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Toast);
        WriteStr(w, message);
        return ms.ToArray();
    }

    /// <summary>One-shot Drive In pick list (#109): the fields within 0.5 km when Drive
    /// In found more than one (AgOpenGPS FormDrivePicker). Not part of the seed.</summary>
    public static byte[] EncodeDrivePick(IReadOnlyList<FieldEntryDto> fields)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(DrivePick);
        w.Write(fields.Count);
        foreach (var f in fields)
        {
            WriteStr(w, f.Name);
            w.Write(f.DistanceKm);        // f64
            w.Write(f.AreaHa);            // f64
        }
        return ms.ToArray();
    }

    public static byte[] EncodeBoundary(BoundaryDto b)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Boundary);
        w.Write(b.Items.Count);
        foreach (var it in b.Items)
        {
            w.Write(it.Index);            // i32
            WriteStr(w, it.BoundaryType);
            w.Write(it.AreaHa);            // f64 (hectares; the client formats ha/ac)
            w.Write((byte)(it.DriveThru ? 1 : 0));
            w.Write((byte)(it.Hard ? 1 : 0));
        }
        w.Write(b.SelectedIndex);         // i32
        w.Write((byte)(b.PlayerVisible ? 1 : 0));
        w.Write((byte)(b.IsRecording ? 1 : 0));
        w.Write((byte)(b.IsPaused ? 1 : 0));
        w.Write(b.PointCount);            // i32
        w.Write(b.AreaHa);                // f64
        w.Write(b.OffsetCm);              // f64
        w.Write((byte)(b.DrawRightSide ? 1 : 0));
        w.Write((byte)(b.DrawAtPivot ? 1 : 0));
        w.Write((byte)(b.SectionControlOn ? 1 : 0));
        w.Write(b.RecordingPoints.Count);
        foreach (var v in b.RecordingPoints) w.Write((float)v);
        return ms.ToArray();
    }

    public static byte[] EncodeFieldTools(FieldToolsDto f)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(FieldTools);
        w.Write(f.ImportFields.Count);
        foreach (var s in f.ImportFields) WriteStr(w, s);
        return ms.ToArray();
    }

    public static byte[] EncodeRecordedPath(RecordedPathDto r)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(RecordedPath);
        w.Write(r.RecFiles.Count);
        foreach (var s in r.RecFiles) WriteStr(w, s);
        w.Write((byte)(r.IsRecording ? 1 : 0));
        w.Write((byte)(r.IsPlaying ? 1 : 0));
        w.Write((byte)(r.HasUnsaved ? 1 : 0));
        WriteStr(w, r.RecordedPathInfo);
        WriteStr(w, r.ResumeModeLabel);
        WriteStr(w, r.RecordedPathName);
        w.Write(r.RecordingPoints.Count);
        foreach (var v in r.RecordingPoints) w.Write((float)v); // field-local m, f32
        return ms.ToArray();
    }

    public static byte[] EncodeAppInfo(AppInfoDto a)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(AppInfo);
        WriteStr(w, a.Version);
        WriteStr(w, a.GitHash);
        WriteStr(w, a.CurrentLanguage);
        w.Write(a.Languages.Count);
        foreach (var l in a.Languages) { WriteStr(w, l.Code); WriteStr(w, l.Name); }
        w.Write(a.Directories.Count);
        foreach (var d in a.Directories) { WriteStr(w, d.Name); WriteStr(w, d.Path); w.Write((byte)(d.Exists ? 1 : 0)); }
        w.Write(a.Hotkeys.Count);
        foreach (var hk in a.Hotkeys) { WriteStr(w, hk.Action); WriteStr(w, hk.Key); WriteStr(w, hk.Label); }
        w.Write(a.Logs.Count);
        foreach (var lg in a.Logs) { WriteStr(w, lg.Time); w.Write(lg.Level); WriteStr(w, lg.Message); }
        WriteStr(w, a.BugReportStatus);
        WriteStr(w, a.BugReportSendStatus);
        return ms.ToArray();
    }

    public static byte[] EncodeAgShare(AgShareDto a)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(AgShare);
        WriteStr(w, a.ServerUrl);
        WriteStr(w, a.ApiKey);
        w.Write((byte)(a.Enabled ? 1 : 0));
        WriteStr(w, a.Status);
        w.Write((byte)(a.Busy ? 1 : 0));
        w.Write(a.LocalFields.Count);
        foreach (var f in a.LocalFields) { WriteStr(w, f.Name); w.Write((byte)(f.HasBoundary ? 1 : 0)); }
        w.Write(a.CloudFields.Count);
        foreach (var f in a.CloudFields) { WriteStr(w, f.Id); WriteStr(w, f.Name); w.Write(f.AreaHa); }
        return ms.ToArray();
    }

    public static byte[] EncodeFieldOps(FieldOpsDto f)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(FieldOps);
        w.Write(f.Fields.Count);
        foreach (var e in f.Fields)
        {
            WriteStr(w, e.Name);
            w.Write((byte)(e.HasDistance ? 1 : 0));
            w.Write(e.DistanceKm);   // f64
            w.Write(e.AreaHa);       // f64
        }
        w.Write(f.Jobs.Count);
        foreach (var j in f.Jobs)
        {
            WriteStr(w, j.FieldName);
            WriteStr(w, j.TaskName);
            WriteStr(w, j.WorkType);
            w.Write(j.Status);       // i32
            WriteStr(w, j.LastOpened);
            WriteStr(w, j.Notes);
        }
        w.Write(f.WorkTypeSuggestions.Count);
        foreach (var s in f.WorkTypeSuggestions) WriteStr(w, s);
        w.Write(f.IsoXmlFiles.Count);
        foreach (var s in f.IsoXmlFiles) WriteStr(w, s);
        w.Write(f.KmlFiles.Count);
        foreach (var s in f.KmlFiles) WriteStr(w, s);
        WriteStr(w, f.ActiveFieldName);
        return ms.ToArray();
    }

    // The mount points last asked for in the profile editor: [i32 seq] str for, str status,
    // [i32 n][ per mount point: str mount, name, format, systems, country, f32 km (NaN = unknown), u8 needs position ].
    public static byte[] EncodeNtripMountpoints(NtripMountpointsDto d)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(NtripMountpoints);
        w.Write(d.Seq);
        WriteStr(w, d.For);
        WriteStr(w, d.Status);
        w.Write(d.Items.Count);
        foreach (var m in d.Items)
        {
            WriteStr(w, m.Mount);
            WriteStr(w, m.Name);
            WriteStr(w, m.Format);
            WriteStr(w, m.Systems);
            WriteStr(w, m.Country);
            w.Write((float)m.DistanceKm);
            w.Write((byte)(m.NeedsPosition ? 1 : 0));
        }
        return ms.ToArray();
    }

    public static byte[] EncodeNtripProfiles(NtripProfilesDto p)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(NtripProfiles);
        w.Write(p.Profiles.Count);
        foreach (var e in p.Profiles)
        {
            WriteStr(w, e.Id);
            WriteStr(w, e.Name);
            WriteStr(w, e.CasterHost);
            w.Write(e.CasterPort);          // i32
            WriteStr(w, e.MountPoint);
            WriteStr(w, e.Username);
            WriteStr(w, e.Password);
            w.Write((byte)(e.AutoConnectOnFieldLoad ? 1 : 0));
            w.Write((byte)(e.IsDefault ? 1 : 0));
            w.Write(e.AssociatedFields.Count);
            foreach (var f in e.AssociatedFields) WriteStr(w, f);
            // The fallback service (append-only): [u8 present] then str host, i32 port, str mount,
            // str user, str password (masked), u8 auto-switch, i32 failed attempts before switching.
            var fb = e.Fallback;
            w.Write((byte)(fb != null ? 1 : 0));
            if (fb != null)
            {
                WriteStr(w, fb.CasterHost);
                w.Write(fb.CasterPort);
                WriteStr(w, fb.MountPoint);
                WriteStr(w, fb.Username);
                WriteStr(w, fb.Password);
                w.Write((byte)(fb.AutoSwitch ? 1 : 0));
                w.Write(fb.SwitchAfterFailures);
            }
        }
        w.Write(p.AvailableFields.Count);
        foreach (var f in p.AvailableFields) WriteStr(w, f);
        return ms.ToArray();
    }

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
        w.Write(v.ImuPivot);
        w.Write(v.ImuHeight);
        w.Write(v.ImuOffset);
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
        w.Write(tr.Width);             // f64 — tram width, m (#110)
        w.Write((byte)(tr.OuterInverted ? 1 : 0)); // #451 — swap outer/inner
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
        // Screen & Alerts (Display) tab.
        var d = c.Display;
        void DB(bool b) => w.Write((byte)(b ? 1 : 0));
        DB(d.GridVisible); DB(d.FieldTextureVisible); DB(d.FieldTextureMoveable); DB(d.SvennArrowVisible);
        DB(d.HeadlandDistanceVisible); DB(d.LineSmoothEnabled); DB(d.AutoDayNight); DB(d.HardwareMessagesEnabled);
        DB(d.ExtraGuidelines);
        w.Write(d.ExtraGuidelinesCount); // i32
        WriteStr(w, d.ResolutionLabel);
        DB(d.UTurnButtonVisible); DB(d.LateralButtonVisible);
        DB(d.AutoSteerSound); DB(d.UTurnSound); DB(d.HydraulicSound); DB(d.SectionsSound);
        DB(d.KeyboardEnabled); DB(d.StartFullscreen); DB(d.ElevationLogEnabled);
        w.Write(d.DisplayResolutionMultiplier); // f64 — web imagery LOD (quality)
        DB(d.IsDayMode); // day/night theme — web switches its full palette + map colours
        w.Write(d.UiScale); // i32 — UI size, percent
        DB(d.ImageryVisible); // the field's aerial imagery on the map (#500)

        // AutoSteer config tab (full 9-tab surface). Append-only; field order mirrors
        // AutoSteerConfigDto exactly so transport.js decodes it positionally.
        var a = c.AutoSteer;
        // Tab 1 — Pure Pursuit / Stanley
        w.Write(a.SteerResponseHold); w.Write(a.IntegralGain); DB(a.IsStanleyMode);
        w.Write(a.StanleyAggressiveness); w.Write(a.StanleyOvershootReduction);
        // Tab 2 — Steering Sensor
        w.Write(a.WasOffset); w.Write(a.CountsPerDegree); w.Write(a.Ackermann); w.Write(a.MaxSteerAngle);
        // Tab 3 — Deadzone / Timing
        w.Write(a.DeadzoneHeading); w.Write(a.DeadzoneDelay); w.Write(a.SpeedFactor); w.Write(a.AcquireFactor);
        // Tab 4 — Gain / PWM
        w.Write(a.ProportionalGain); w.Write(a.MaxPwm); w.Write(a.MinPwm);
        // Tab 5 — Turn Sensors
        DB(a.TurnSensorEnabled); DB(a.PressureSensorEnabled); DB(a.CurrentSensorEnabled);
        w.Write(a.TurnSensorCounts); w.Write(a.PressureTripPoint); w.Write(a.CurrentTripPoint);
        // Tab 6 — Hardware Config
        DB(a.DanfossEnabled); DB(a.InvertWas); DB(a.InvertMotor); DB(a.InvertRelays);
        w.Write(a.MotorDriver); w.Write(a.AdConverter); w.Write(a.ImuAxisSwap); w.Write(a.ExternalEnable);
        // Tab 7 — Algorithm
        w.Write(a.UTurnCompensation); w.Write(a.SideHillCompensation); DB(a.SteerInReverse);
        // Tab 8 — Speed Limits
        DB(a.ManualTurnsEnabled); w.Write(a.ManualTurnsSpeed); w.Write(a.MinSteerSpeed); w.Write(a.MaxSteerSpeed);
        // Tab 9 — Display
        w.Write(a.LineWidth); w.Write(a.NudgeDistance); w.Write(a.NextGuidanceTime); w.Write(a.CmPerPixel);
        DB(a.LightbarEnabled); DB(a.SteerBarEnabled); DB(a.GuidanceBarOn);
        w.Write(a.RefNudgeDistance); // i32 — Ref Nudge panel step, cm
        return ms.ToArray();
    }

    public static byte[] EncodeWizard(WizardDto w)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(Wizard);
        bw.Write(w.StepIndex);          // i32
        bw.Write(w.TotalSteps);
        WriteStr(bw, w.StepKind);
        WriteStr(bw, w.Title);
        WriteStr(bw, w.Description);
        void B(bool b) => bw.Write((byte)(b ? 1 : 0));
        B(w.CanBack); B(w.CanNext); B(w.CanSkip); B(w.IsLast);
        WriteStr(bw, w.Validation);
        bw.Write((float)w.StatusWas);
        bw.Write((float)w.StatusRoll);
        WriteStr(bw, w.StatusGps);
        bw.Write((float)w.StatusSpeed);
        bw.Write(w.StatusPwm);          // i32
        B(w.StatusConnected);
        bw.Write(w.HardwareLevel);      // i32
        bw.Write((float)w.LiveAngle);
        bw.Write((float)w.LiveRoll);
        bw.Write((float)w.LiveError);
        WriteStr(bw, w.TestPhase);
        WriteStr(bw, w.TestResult);
        bw.Write((float)w.TestProgress);
        B(w.TestActive);
        B(w.RtkFixed);
        WriteStr(bw, w.FixLabel);
        bw.Write((float)w.Diameter);
        WriteStr(bw, w.RecordHint);
        B(w.CanRecord);
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
        for (int i = 0; i < s.Boundaries.Count; i++)
        {
            w.Write((byte)(i < s.BoundaryInner.Count && s.BoundaryInner[i] ? 1 : 0));
            WritePts(w, s.Boundaries[i]);
        }

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
        foreach (var fl in s.Flags) { w.Write((float)fl.E); w.Write((float)fl.N); WriteStr(w, fl.ColorHex); WriteStr(w, fl.Name); }

        if (s.Imagery is { } im)
        {
            w.Write((byte)1);
            w.Write(im.MinE); w.Write(im.MinN); w.Write(im.MaxE); w.Write(im.MaxN);
            w.Write(im.Version);
        }
        else w.Write((byte)0);

        // Tracks-manager list (ALL tracks incl. hidden) — appended at the end of the
        // Scene frame per the wire rule. No points; index maps back to the field list.
        w.Write(s.TrackList.Count);
        foreach (var ti in s.TrackList)
        {
            w.Write(ti.Index);
            WriteStr(w, ti.Name);
            WriteStr(w, ti.Type); // display label (Line/Curve/Path/Contour)
            w.Write((byte)(ti.Active ? 1 : 0));
            w.Write((byte)(ti.Visible ? 1 : 0));
        }

        // Field Builder Headland-tab segment list — appended at the end of the Scene frame
        // per the wire rule. No points; index maps back to the VM's HeadlandSegments list.
        w.Write(s.HeadlandSegs.Count);
        foreach (var hs in s.HeadlandSegs)
        {
            w.Write(hs.Index);
            WriteStr(w, hs.Name);
            WriteStr(w, hs.Type); // Line / Curve / Boundary
            w.Write(hs.Offset);   // f64, metres
            w.Write((byte)(hs.Effective ? 1 : 0));
            WritePts(w, hs.EditLine); // offset line + overshoots, for the on-map editor view
            w.Write((float)hs.EndA.E); w.Write((float)hs.EndA.N); // boundary drag handles
            w.Write((float)hs.EndB.E); w.Write((float)hs.EndB.N);
        }

        // Field Builder Tram-tab systems + generated tram lines (appended at the end).
        w.Write(s.TramSystems.Count);
        foreach (var ts in s.TramSystems)
        {
            w.Write(ts.Index);
            WriteStr(w, ts.Name);
            WriteStr(w, ts.RefLabel);
            w.Write(ts.Width);     // f64
            w.Write(ts.Mode);      // i32
            w.Write(ts.Offset);    // f64
            w.Write(ts.Direction); // i32
            w.Write(ts.PassCount); // i32
            w.Write((byte)(ts.Enabled ? 1 : 0));
            w.Write((byte)(ts.IsBoundary ? 1 : 0));
        }
        w.Write(s.TramLines.Count);
        foreach (var line in s.TramLines) WritePts(w, line);
        w.Write(s.RecordedPaths.Count);                           // #110
        foreach (var line in s.RecordedPaths) WritePts(w, line);
        w.Write(s.ContourStrips.Count);
        foreach (var line in s.ContourStrips) WritePts(w, line);
        WriteOptPts(w, s.ContourRef);
        w.Write((byte)(s.ContourLocked ? 1 : 0));
        w.Write(s.GuideLines.Count);                              // #450
        foreach (var line in s.GuideLines) WritePts(w, line);
        WriteOptPts(w, s.ToolPass);

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
        w.Write((byte)t.SkipMode); // #111 — skip mode 0/1/2
        w.Write((byte)t.TramMode);
        // Headland-distance HUD.
        w.Write((float)t.HeadlandProximityDistance);
        w.Write((byte)(t.HeadlandProximityWarning ? 1 : 0));
        // Steer-bar value (steer-angle error).
        w.Write((float)t.SteerAngleError);
        // Diagnostic-chart scalars (Tools panel charts).
        w.Write((float)t.ChartSetSteer);
        w.Write((float)t.ChartActualSteer);
        w.Write((float)t.ChartPwm);
        w.Write((float)t.ChartImuHeading);
        // Hitch pivot (implement hitch line).
        w.Write(t.HitchE);           // f64
        w.Write(t.HitchN);           // f64
        w.Write((float)t.VehicleSteerAngle); // front-wheel sprite angle (deg)
        w.Write(t.HostMs);           // f64 — host monotonic build time (client interp timeline)
        w.Write((byte)(t.IsYouTurnExecuting ? 1 : 0)); // #50 — mid-turn gate for on-screen buttons
        w.Write(t.PassNumber);       // i32 — guidance pass offset (0 = on reference)
        w.Write((float)t.NudgeOffset); // f32 — driver-relative nudge (m, +right) — #93 readout
        w.Write((float)t.RefNudgeMoved); // f32 — Ref Nudge panel total (m, +right) — #489
        w.Write((byte)(t.RefNudgePending ? 1 : 0)); // unsaved reference move
        w.Write((byte)(t.HasGoal ? 1 : 0)); // #95 — PP goal marker
        w.Write(t.GoalE);            // f64
        w.Write(t.GoalN);            // f64
        w.Write((byte)(t.IsReverse ? 1 : 0)); // #125 — reversing
        w.Write((byte)(t.ModuleNotSteering ? 1 : 0)); // #126 — engaged but module not steering
        w.Write((float)t.ChartGpsHeading); // f32 — heading chart GPS fix-to-fix (#111)
        w.Write((byte)t.TramLights);  // #451 — tram lights bits
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
        // AutoSteer live telemetry (Phase 9 AutoSteer panel).
        w.Write((float)s.ActualSteerAngle);
        w.Write((float)s.SensorPercent);
        w.Write((float)s.SetSteerAngle);
        w.Write((float)s.FreeDriveAngle);
        w.Write((byte)(s.SteerFreeDrive ? 1 : 0));
        // Smart-WAS calibration snapshot.
        w.Write((byte)(s.SmartWasCollecting ? 1 : 0));
        w.Write(s.SmartWasSamples);          // i32
        w.Write((float)s.SmartWasMean);
        w.Write((float)s.SmartWasMedian);
        w.Write((float)s.SmartWasStdDev);
        w.Write((float)s.SmartWasOffsetDeg);
        w.Write((float)s.SmartWasConfidence);
        w.Write((byte)(s.SmartWasValid ? 1 : 0));
        // Network IO panel (append-only).
        WriteStr(w, s.GpsIp);
        WriteStr(w, s.ModuleSubnet);
        WriteStr(w, s.HostIps);
        w.Write((byte)(s.NtripConnected ? 1 : 0));
        WriteStr(w, s.NtripStatus);
        w.Write(s.NtripBytes);          // f64 (raw bytes; client formats KB)
        WriteStr(w, s.NtripDestination);
        w.Write((byte)(s.NtripUnicast ? 1 : 0));
        w.Write((byte)(s.RtcmBroadcast ? 1 : 0));
        w.Write((byte)(s.NtripEnabled ? 1 : 0));
        WriteStr(w, s.NtripTestStatus);
        w.Write((byte)(s.SimPanelVisible ? 1 : 0));
        // Field Tools — Offset Fix drift (meters).
        w.Write((float)s.DriftEasting);
        w.Write((float)s.DriftNorthing);
        // Unsaved-coverage guard prompt (append-only).
        w.Write((byte)(s.UnsavedCoveragePrompt ? 1 : 0));
        // Dev diagnostics row (append-only): overlay gate + host control-loop latency.
        w.Write((byte)(s.DevOverlay ? 1 : 0));
        w.Write((float)s.GpsToPgnLatencyMs);
        // GPS source (append-only, #157): sentence type + Dual-without-dual-heading flag.
        WriteStr(w, s.GpsSentence);
        w.Write((byte)(s.DualHeadingMissing ? 1 : 0));
        // NTRIP RTCM stream (append-only, RTCM plan Phase 5): [u8 present] then totals and
        // [i32 n][ per type: i32 type, i32 count, f32 every s (NaN = once), f32 last s ].
        var rtcm = s.NtripRtcm;
        w.Write((byte)(rtcm != null ? 1 : 0));
        if (rtcm != null)
        {
            w.Write((float)rtcm.SessionSeconds);
            w.Write((int)Math.Min(rtcm.Messages, int.MaxValue));
            w.Write((int)Math.Min(rtcm.ChecksumFailures, int.MaxValue));
            w.Write((int)Math.Min(rtcm.BytesSkipped, int.MaxValue));
            w.Write((int)Math.Min(rtcm.NotSent, int.MaxValue));
            w.Write((byte)(rtcm.Unframed ? 1 : 0));
            w.Write(rtcm.Types.Count);
            foreach (var t in rtcm.Types)
            {
                w.Write(t.Type);
                w.Write((int)Math.Min(t.Count, int.MaxValue));
                w.Write((float)t.EverySeconds);
                w.Write((float)t.LastSeconds);
            }
        }
        // System Data card (append-only): [u8 present] then f32 pitch, yaw rate, IMU / dual /
        // fix-to-fix heading (NaN = none), rate Hz; i32 missed, rejected;
        // [u8 n][ per sentence: str type, str text, f32 age s ].
        var sd = s.SystemData;
        w.Write((byte)(sd != null ? 1 : 0));
        if (sd != null)
        {
            w.Write((float)sd.Pitch);
            w.Write((float)sd.YawRate);
            w.Write((float)sd.ImuHeading);
            w.Write((float)sd.DualHeading);
            w.Write((float)sd.FixToFixHeading);
            w.Write((float)sd.RateHz);
            w.Write((int)Math.Min(sd.Missed, int.MaxValue));
            w.Write((int)Math.Min(sd.Rejected, int.MaxValue));
            w.Write((byte)Math.Min(sd.Sentences.Count, 255));
            for (int i = 0; i < sd.Sentences.Count && i < 255; i++)
            {
                WriteStr(w, sd.Sentences[i].Type);
                WriteStr(w, sd.Sentences[i].Text);
                w.Write((float)sd.Sentences[i].AgeSeconds);
            }
            // Ingest counters and the source: i32 bad checksum, unknown sentence, lines joined
            // across datagrams, bytes dropped; str source ("192.168.5.11 · GPS1 :2211").
            w.Write((int)Math.Min(sd.BadChecksum, int.MaxValue));
            w.Write((int)Math.Min(sd.UnknownSentence, int.MaxValue));
            w.Write((int)Math.Min(sd.JoinedLines, int.MaxValue));
            w.Write((int)Math.Min(sd.DroppedBytes, int.MaxValue));
            WriteStr(w, sd.Source);
            // Epochs closed by the next epoch's start with a learned member missing.
            w.Write((int)Math.Min(sd.IncompleteEpochs, int.MaxValue));
        }
        // GPS data lost (append-only): the host's debounced flag, so client and host agree.
        w.Write((byte)(s.GpsLost ? 1 : 0));
        // ISOBUS task controller (append-only, #312): [u8 present] then u8 section control,
        // u8 sections, u8 clients, u32 reported low, u32 reported high.
        var iso = s.Isobus;
        w.Write((byte)(iso != null ? 1 : 0));
        if (iso != null)
        {
            w.Write((byte)(iso.SectionControl ? 1 : 0));
            w.Write((byte)Math.Clamp(iso.Sections, 0, 255));
            w.Write((byte)Math.Clamp(iso.Clients, 0, 255));
            w.Write((uint)(iso.Reported & 0xFFFFFFFF));
            w.Write((uint)(iso.Reported >> 32));
        }
        // Field data card (append-only, #321): [u8 present] then f64 boundary area,
        // actual area, trip area (m²), trip distance (m).
        var fd = s.FieldData;
        w.Write((byte)(fd != null ? 1 : 0));
        if (fd != null)
        {
            w.Write(fd.BoundaryAreaSqM);
            w.Write(fd.ActualAreaSqM);
            w.Write(fd.TripAreaSqM);
            w.Write(fd.TripDistanceM);
        }
        // Network IO: PGN 100 sent (append-only, #337).
        w.Write((byte)(s.SendCorrectedPosition ? 1 : 0));
        // Network IO: the subnet the scan came from (append-only, #334).
        WriteStr(w, s.ScanReplySubnet);
        // NTRIP session (append-only, NTRIP plan Phase 4): [u8 present] then u8 using fallback,
        // main is back, has fallback, profile per field; u8 state; i32 last error (-1 none);
        // i32 failed attempts; f32 retry s; str notice.
        var ns = s.NtripSession;
        w.Write((byte)(ns != null ? 1 : 0));
        if (ns != null)
        {
            w.Write((byte)(ns.UsingFallback ? 1 : 0));
            w.Write((byte)(ns.MainIsBack ? 1 : 0));
            w.Write((byte)(ns.HasFallback ? 1 : 0));
            w.Write((byte)(ns.ProfilePerField ? 1 : 0));
            w.Write((byte)ns.State);
            w.Write(ns.LastError);
            w.Write(ns.Attempts);
            w.Write((float)ns.RetryInSeconds);
            WriteStr(w, ns.Notice);
            // Append-only: the profile in use ("" for a caster typed in) and the service in use ("MOUNT@host:port").
            WriteStr(w, ns.Profile);
            WriteStr(w, ns.Service);
        }
        return ms.ToArray();
    }

    // Crisp worked-area edge: the vector perimeter as polylines (field-local metres). Bounded
    // by perimeter length. [i32 polylineCount][ per polyline: i32 pointCount, f32 e, f32 n … ].
    public static byte[] EncodeCoverageEdge(IReadOnlyList<IReadOnlyList<AgOpenWeb.Models.Base.Vec2>> polylines)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(CoverageEdge);
        w.Write(polylines.Count);
        foreach (var pl in polylines)
        {
            w.Write(pl.Count);
            foreach (var p in pl) { w.Write((float)p.Easting); w.Write((float)p.Northing); }
        }
        return ms.ToArray();
    }

    // Persisted web-camera view, sent once per connection in the seed so the client
    // restores its last tilt+zoom (issue #35). Pitch is RADIANS, zoom is client
    // pixels-per-metre — the client's own camera space, stored verbatim host-side.
    // Camera follow mode (#176) uses the client's numbering: 0 NorthUp, 1 HeadingUp, 3 Map.
    public static byte[] EncodeViewPrefs(double pitch, double zoom, int cameraMode)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(ViewPrefs);
        w.Write(pitch); // f64
        w.Write(zoom);  // f64
        w.Write((byte)cameraMode);
        return ms.ToArray();
    }

    // Round-trip link-latency reply. Echoes the client's token (its performance.now()
    // timestamp) verbatim so the client computes RTT = now − token on its own clock.
    public static byte[] EncodePong(string token)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Pong);
        WriteStr(w, token);
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

    // reset=true → the client must drop any coverage it has and rebuild from the snapshot that
    // follows (cold start / field reload / cell-size change). reset=false → the grid merely grew
    // (bounds expansion); the client re-anchors its existing coverage and no snapshot follows.
    public static byte[] EncodeCoverageInit(CoverageInitDto c, bool reset)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(CoverageInit);
        w.Write(c.CellSize);         // f64
        w.Write(c.OriginE);          // f64
        w.Write(c.OriginN);          // f64
        w.Write(c.Width);            // i32
        w.Write(c.Height);           // i32
        w.Write(reset);              // u8 (0/1)
        return ms.ToArray();
    }

    /// <summary>No coverage grid any more (field closed): a reset init with an empty grid.
    /// The client drops its raster and perimeter (transport.js / app.js onCoverageInit).</summary>
    public static byte[] EncodeCoverageClear() =>
        EncodeCoverageInit(new CoverageInitDto(0, 0, 0, 0, 0), reset: true);

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
