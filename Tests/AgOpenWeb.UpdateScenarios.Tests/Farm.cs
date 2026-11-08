// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using AgOpenWeb.Models;

namespace AgOpenWeb.UpdateScenarios.Tests;

/// <summary>
/// A farm as the operator leaves it after a season: what was set, so it can be read back and
/// compared after an update or a restart. Every value here is applied through the web client's
/// own commands (<see cref="FarmBuilder"/>), so the same farm can be made on an older release
/// (over its WebSocket) and on this build (in-process). The place is made up (about 41.6° N,
/// 5.7° W, open country), as in the other scenario harnesses: no real farm in the repo.
/// </summary>
public sealed class Farm
{
    public string Name { get; init; } = "Quinta da Serra";

    /// <summary>Vehicle profiles with the settings each carries (config.set key → value,
    /// vehicle.* and autosteer.* keys; the autosteer ones end up in Name.AutoSteer.json).</summary>
    public List<ProfileSpec> Vehicles { get; } = new();
    /// <summary>Implement profiles (tool.*, uturn.*, tram.* keys: saved with the tool).</summary>
    public List<ProfileSpec> Tools { get; } = new();
    /// <summary>The pair left in use at the end of the season.</summary>
    public (string Vehicle, string Tool) ActivePair { get; init; }

    /// <summary>Device settings (config.set keys that persist at once: units, display.*, conn.*,
    /// gps.*, roll.*) and what they must read back as (dump key → value).</summary>
    public List<Setting> Device { get; } = new();
    public string Language { get; init; } = "pt";
    public Dictionary<string, string> Hotkeys { get; } = new();
    public bool NightMode { get; init; } = true;
    public (double Pitch, double Zoom) View { get; init; } = (-1.1, 14.0);
    public List<NtripSpec> Ntrips { get; } = new();
    public List<FieldSpec> Fields { get; } = new();

    /// <summary>The field opened last: the one "Resume last" must bring back.</summary>
    public string LastField => Fields.Count > 0 ? Fields[^1].Name : "";

    /// <summary>A season on a mixed farm: a tractor with a sprayer and a second tractor with a seed
    /// drill, two NTRIP casters, three fields with tracks, flags and a sprayed job.</summary>
    public static Farm Standard()
    {
        var f = new Farm { ActivePair = ("Fendt 312", "Sprayer 8 m"), Language = "pt", NightMode = true, View = (-1.1, 14.0) };

        f.Vehicles.Add(new ProfileSpec("Fendt 312", new()
        {
            S("vehicle.type", "Vehicle.Type", "0", "Tractor"),
            S("vehicle.wheelbase", "Vehicle.Wheelbase", "2.7"),
            S("vehicle.trackWidth", "Vehicle.TrackWidth", "1.9"),
            S("vehicle.antennaHeight", "Vehicle.AntennaHeight", "3.1"),
            S("vehicle.antennaPivot", "Vehicle.AntennaPivot", "0.45"),
            S("vehicle.antennaOffset", "Vehicle.AntennaOffset", "-0.12"),
            S("autosteer.maxSteerAngle", "Vehicle.MaxSteerAngle", "38"),
            S("autosteer.countsPerDegree", "AutoSteer.CountsPerDegree", "118"),
            S("autosteer.ackermann", "AutoSteer.Ackermann", "104"),
            S("autosteer.proportionalGain", "AutoSteer.ProportionalGain", "72"),
            S("autosteer.minPwm", "AutoSteer.MinPwm", "31"),
            S("autosteer.maxPwm", "AutoSteer.MaxPwm", "190"),
            S("autosteer.invertWas", "AutoSteer.InvertWas", "1", "True"),
            // After Invert WAS: the toggle negates the offset so the wheels stay zeroed, by design.
            S("autosteer.wasOffset", "AutoSteer.WasOffset", "-31"),
            S("autosteer.motorDriver", "AutoSteer.MotorDriver", "1"),
            S("autosteer.steerInReverse", "AutoSteer.SteerInReverse", "1", "True"),
            S("autosteer.maxSteerSpeed", "AutoSteer.MaxSteerSpeed", "17"),
            S("autosteer.steerResponseHold", "Guidance.GoalPointLookAheadHold", "2.9"),
            S("autosteer.integralGain", "Guidance.PurePursuitIntegralGain", "0.12"),
        }));
        f.Vehicles.Add(new ProfileSpec("Valtra N134", new()
        {
            S("vehicle.wheelbase", "Vehicle.Wheelbase", "2.665"),
            S("vehicle.antennaHeight", "Vehicle.AntennaHeight", "2.95"),
            S("vehicle.antennaPivot", "Vehicle.AntennaPivot", "0.6"),
            S("autosteer.maxSteerAngle", "Vehicle.MaxSteerAngle", "42"),
            S("autosteer.countsPerDegree", "AutoSteer.CountsPerDegree", "95"),
            S("autosteer.proportionalGain", "AutoSteer.ProportionalGain", "55"),
        }));
        f.Tools.Add(new ProfileSpec("Sprayer 8 m", new()
        {
            S("tool.type", "Tool.IsToolRearFixed", "rear", "True"),
            S("tool.hitchLength", "Tool.HitchLength", "1.6"),   // a distance (0–30 m); the type gives the side
            S("tool.numSections", "NumSections", "8"),
            S("tool.defaultSectionWidth", "ActualToolWidth", "100", "8"),
            S("tool.lookAheadOn", "Tool.LookAheadOnSetting", "1.2"),
            S("tool.lookAheadOff", "Tool.LookAheadOffSetting", "0.4"),   // at most 0.8 × on; excludes the turn-off delay
            S("tool.overlap", "Tool.Overlap", "0.05"),
            S("tool.offset", "Tool.Offset", "0.1"),
            S("uturn.radius", "Guidance.UTurnRadius", "5.5"),
            S("uturn.distanceFromBoundary", "Guidance.UTurnDistanceFromBoundary", "3"),
            S("uturn.extension", "Guidance.UTurnExtension", "12"),
            S("tram.passes", "Guidance.TramPasses", "4"),
        }));
        f.Tools.Add(new ProfileSpec("Seed drill 3 m", new()
        {
            S("tool.type", "Tool.IsToolTrailing", "trailing", "True"),
            S("tool.trailingHitchLength", "Tool.TrailingHitchLength", "2.4"),   // 0.1–30 m, behind the hitch
            S("tool.turnOffDelay", "Tool.TurnOffDelay", "0.3"),
            S("tool.numSections", "NumSections", "12"),
            S("tool.defaultSectionWidth", "ActualToolWidth", "25", "3"),
            S("uturn.radius", "Guidance.UTurnRadius", "7"),
            S("uturn.style", "Guidance.UTurnStyle", "1"),
        }));

        f.Device.AddRange(new[]
        {
            S("units", "AppSettings.IsMetric", "metric", "True"),
            S("display.gridVisible", "AppSettings.GridVisible", "0", "False"),
            S("display.uiScale", "AppSettings.UiScale", "130"),   // one of the steps 100, 115, 130, 150
            S("display.extraGuidelines", "AppSettings.ExtraGuidelines", "1", "True"),
            S("display.extraGuidelinesCount", "AppSettings.ExtraGuidelinesCount", "6"),
            S("display.autoDayNight", "AppSettings.AutoDayNight", "0", "False"),
            S("display.sectionsSound", "AppSettings.SectionsSound", "0", "False"),
            S("conn.rtcmBroadcast", "AppSettings.RtcmBroadcast", "1", "True"),
            S("conn.sendCorrectedPosition", "AppSettings.SendCorrectedPosition", "1", "True"),
            S("conn.machineConfigured", "AppSettings.IsMachineConfigured", "0", "False"),
            S("gps.isDualGps", "Connections.IsDualGps", "1", "True"),
            S("gps.dualHeadingOffset", "Connections.DualHeadingOffset", "90"),
            S("gps.minGpsStep", "Connections.MinGpsStep", "0.08"),
            S("roll.rollZero", "Ahrs.RollZero", "1.5"),
            S("roll.isRollInvert", "Ahrs.IsRollInvert", "1", "True"),
        });
        f.Hotkeys["AutoSteer"] = "S";
        f.Hotkeys["Flag"] = "L";

        f.Ntrips.Add(new NtripSpec("Caster PT", "caster.example.net", 2101, "RTCM3_NEAR", "farmer", "s3cret-pt", AutoConnect: true, IsDefault: true));
        f.Ntrips.Add(new NtripSpec("Backup caster", "backup.example.net", 2102, "VRS_32", "farmer2", "pass-2", AutoConnect: false, IsDefault: false));

        // Fields: lat/lon rectangles and polygons around their origin (the tractor's position
        // when the field is made). 1e-5° of latitude ≈ 1.11 m; of longitude at 41.6° ≈ 0.83 m.
        var norte = new FieldSpec("Tapada Norte", 41.6010, -5.7010);
        norte.Boundary.AddRange(Rect(norte.Lat, norte.Lon, 300, 200));
        norte.HeadlandOffset = 8;
        norte.Tracks.Add(new TrackSpec("AB north", false, new() { (norte.Lat - 0.0008, norte.Lon - 0.0006), (norte.Lat + 0.0008, norte.Lon - 0.0006) }));
        norte.Tracks.Add(new TrackSpec("Curve east", true, new()
        {
            (norte.Lat - 0.0007, norte.Lon + 0.0004), (norte.Lat - 0.0003, norte.Lon + 0.0005),
            (norte.Lat + 0.0001, norte.Lon + 0.00045), (norte.Lat + 0.0005, norte.Lon + 0.0006), (norte.Lat + 0.0008, norte.Lon + 0.00055),
        }));
        norte.Flags.Add(new FlagSpec("Well", "Blue", norte.Lat + 0.0004, norte.Lon - 0.0012));
        norte.Flags.Add(new FlagSpec("Rock", "Red", norte.Lat - 0.0005, norte.Lon + 0.0010));
        norte.Job = new JobSpec("Spring spraying", "Spraying", DriveSeconds: 20, SpeedSteps: 10);
        f.Fields.Add(norte);

        var sul = new FieldSpec("Couto Sul", 41.5920, -5.7120);
        sul.Boundary.AddRange(new (double, double)[]
        {
            (sul.Lat - 0.0012, sul.Lon - 0.0020), (sul.Lat + 0.0006, sul.Lon - 0.0024), (sul.Lat + 0.0014, sul.Lon - 0.0002),
            (sul.Lat + 0.0009, sul.Lon + 0.0016), (sul.Lat - 0.0010, sul.Lon + 0.0012),
        });
        sul.Tracks.Add(new TrackSpec("Along the road", false, new() { (sul.Lat - 0.0008, sul.Lon - 0.0015), (sul.Lat + 0.0008, sul.Lon + 0.0008) }));
        sul.Flags.Add(new FlagSpec("Gate", "Green", sul.Lat - 0.0011, sul.Lon - 0.0004));
        f.Fields.Add(sul);

        var varzea = new FieldSpec("Várzea do Rio", 41.5850, -5.6900);   // a name with accents: a folder name on every OS
        varzea.Boundary.AddRange(Rect(varzea.Lat, varzea.Lon, 180, 260));
        varzea.Tracks.Add(new TrackSpec("Rio AB", false, new() { (varzea.Lat - 0.0010, varzea.Lon + 0.0003), (varzea.Lat + 0.0010, varzea.Lon + 0.0003) }));
        f.Fields.Add(varzea);
        return f;
    }

    private static Setting S(string key, string dump, string value, string? reads = null) => new(key, dump, value, reads ?? value);

    /// <summary>A rectangle of <paramref name="eastWest"/> × <paramref name="northSouth"/> metres
    /// centred on the point, as lat/lon corners (counter-clockwise).</summary>
    public static IEnumerable<(double Lat, double Lon)> Rect(double lat, double lon, double eastWest, double northSouth)
    {
        double dLat = northSouth / 2 / 111_320.0;
        double dLon = eastWest / 2 / (111_320.0 * Math.Cos(lat * Math.PI / 180));
        yield return (lat - dLat, lon - dLon);
        yield return (lat - dLat, lon + dLon);
        yield return (lat + dLat, lon + dLon);
        yield return (lat + dLat, lon - dLon);
    }
}

/// <summary>One config.set key, where it reads back in the dump and what it must read as.</summary>
public sealed record Setting(string Key, string Dump, string Value, string Reads);

public sealed record ProfileSpec(string Name, List<Setting> Settings);

public sealed record NtripSpec(string Name, string Host, int Port, string Mount, string User, string Password, bool AutoConnect, bool IsDefault);

public sealed class FieldSpec
{
    public FieldSpec(string name, double lat, double lon) { Name = name; Lat = lat; Lon = lon; }
    public string Name { get; }
    /// <summary>The tractor's position when the field is made: the field origin.</summary>
    public double Lat { get; }
    public double Lon { get; }
    public List<(double Lat, double Lon)> Boundary { get; } = new();
    public double? HeadlandOffset { get; set; }
    public List<TrackSpec> Tracks { get; } = new();
    public List<FlagSpec> Flags { get; } = new();
    public JobSpec? Job { get; set; }

    /// <summary>The boundary's area in m² (shoelace on the plane of the origin).</summary>
    public double AreaSqM()
    {
        var plane = new LocalPlane(new Wgs84(Lat, Lon), new SharedFieldProperties());
        var pts = Boundary.Select(p => plane.ConvertWgs84ToGeoCoord(new Wgs84(p.Lat, p.Lon))).ToList();
        double a = 0;
        for (int i = 0; i < pts.Count; i++)
        {
            var p = pts[i]; var q = pts[(i + 1) % pts.Count];
            a += p.Easting * q.Northing - q.Easting * p.Northing;
        }
        return Math.Abs(a) / 2;
    }
}

public sealed record TrackSpec(string Name, bool Curve, List<(double Lat, double Lon)> Points);

public sealed record FlagSpec(string Name, string Color, double Lat, double Lon);

/// <summary>A job on the field: the tractor drives straight from the origin with the sections in
/// Auto for <paramref name="DriveSeconds"/> at <paramref name="SpeedSteps"/> presses of the
/// simulator's + button (1 km/h each).</summary>
public sealed record JobSpec(string Task, string WorkType, double DriveSeconds, int SpeedSteps);

/// <summary>Where the builder's commands go: this build's command handler, or an older build's
/// WebSocket. <see cref="PauseAsync"/> is how long a step is given to land: nothing on this build
/// (the rig waits for the host loop), real seconds on a child process.</summary>
public interface ICommandSender
{
    string DataRoot { get; }
    Task SendAsync(string cmd, string arg = "");
    Task PauseAsync(double seconds);
}

/// <summary>Makes a <see cref="Farm"/> through the web client's commands, in the order an operator
/// would: profiles and their settings, device settings, casters, then each field (made at the
/// tractor's position, boundary drawn on the map, headland, tracks drawn on the map, flags, a job
/// driven with the simulator), closing each. The field opened last is left as the last one.</summary>
public static class FarmBuilder
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static async Task BuildAsync(ICommandSender s, Farm farm, Scenario sc, bool slow)
    {
        // Settle a fresh install: the simulator on, standing still.
        await s.SendAsync("sim.stop");
        await s.PauseAsync(slow ? 1.0 : 0);

        // Profiles. profile.new copies the live settings under the new name and makes it active;
        // the settings then go in with config.set and are written by profile.save.
        foreach (var v in farm.Vehicles)
        {
            await s.SendAsync("profile.new", "vehicle\t" + v.Name);
            await s.PauseAsync(slow ? 1.0 : 0);
            foreach (var st in v.Settings) await s.SendAsync("config.set", st.Key + ":" + st.Value);
            await s.SendAsync("profile.save");
            await s.PauseAsync(slow ? 1.0 : 0);
            sc.Step(Kit.F($"vehicle profile '{v.Name}' made with {v.Settings.Count} settings and saved"));
        }
        foreach (var t in farm.Tools)
        {
            await s.SendAsync("profile.new", "tool\t" + t.Name);
            await s.PauseAsync(slow ? 1.0 : 0);
            foreach (var st in t.Settings) await s.SendAsync("config.set", st.Key + ":" + st.Value);
            await s.SendAsync("profile.save");
            await s.PauseAsync(slow ? 1.0 : 0);
            sc.Step(Kit.F($"implement profile '{t.Name}' made with {t.Settings.Count} settings and saved"));
        }
        await s.SendAsync("profile.load", farm.ActivePair.Vehicle + "\t" + farm.ActivePair.Tool);
        await s.PauseAsync(slow ? 1.5 : 0);
        sc.Step(Kit.F($"pair in use: '{farm.ActivePair.Vehicle}' / '{farm.ActivePair.Tool}'"));

        // Device settings, language, hotkeys, theme, camera.
        foreach (var st in farm.Device) await s.SendAsync("config.set", st.Key + ":" + st.Value);
        await s.SendAsync("app.setLanguage", farm.Language);
        foreach (var (action, key) in farm.Hotkeys) await s.SendAsync("app.setHotkey", action + ":" + key);
        if (farm.NightMode) await s.SendAsync("display.toggleTheme");
        await s.SendAsync("view.save", Kit.F($"{farm.View.Pitch}|{farm.View.Zoom}|0"));
        await s.PauseAsync(slow ? 1.0 : 0);
        sc.Step(Kit.F($"{farm.Device.Count} device settings, language '{farm.Language}', {farm.Hotkeys.Count} hotkeys, night mode {farm.NightMode}, camera saved"));

        // NTRIP casters.
        foreach (var n in farm.Ntrips)
        {
            await s.SendAsync("ntrip.save", string.Join("\t", "", n.Name, n.Host, n.Port.ToString(Inv), n.Mount, n.User, n.Password,
                n.AutoConnect ? "1" : "0", n.IsDefault ? "1" : "0", ""));
            await s.PauseAsync(slow ? 1.0 : 0.2);
        }
        // Keep the corrections link off: the casters are made up.
        await s.SendAsync("config.set", "conn.ntripEnabled:0");
        await s.PauseAsync(slow ? 1.0 : 0);
        sc.Step(Kit.F($"{farm.Ntrips.Count} NTRIP profiles saved, NTRIP switched off"));

        // Fields.
        foreach (var fs in farm.Fields)
        {
            await s.SendAsync("sim.setCoords", Kit.F($"{fs.Lat:0.00000000},{fs.Lon:0.00000000}"));
            await s.PauseAsync(slow ? 2.0 : 0.6);   // the simulator's first fixes at the new place
            await s.SendAsync("field.new", fs.Name);
            await s.PauseAsync(slow ? 3.0 : 0.3);
            string dir = Path.Combine(s.DataRoot, "AgOpenWeb", "Fields", fs.Name);
            var origin = await WaitForOriginAsync(dir, slow ? 15 : 5);
            if (origin == null)
            {
                sc.Harness(Kit.F($"the field '{fs.Name}' was not made (no folder with an origin under {dir} within the wait)"));
                continue;
            }
            var plane = new LocalPlane(new Wgs84(origin.Value.Lat, origin.Value.Lon), new SharedFieldProperties());
            string En(double lat, double lon)
            {
                var g = plane.ConvertWgs84ToGeoCoord(new Wgs84(lat, lon));
                return Kit.F($"{g.Easting:0.000},{g.Northing:0.000}");
            }
            sc.Step(Kit.F($"field '{fs.Name}' made at {origin.Value.Lat:0.00000},{origin.Value.Lon:0.00000}"));

            await s.SendAsync("boundary.fromMapPoints", string.Join(";", fs.Boundary.Select(p => En(p.Lat, p.Lon))));
            await s.PauseAsync(slow ? 1.5 : 0);
            if (fs.HeadlandOffset is { } ho)
            {
                await s.SendAsync("headland.wholeBoundary", ho.ToString(Inv));
                await s.PauseAsync(slow ? 1.5 : 0);
            }
            int trackIndex = 0;
            foreach (var t in fs.Tracks)
            {
                if (!t.Curve)
                {
                    await s.SendAsync("track.drawStraight");
                    await s.SendAsync("track.drawPoint", En(t.Points[0].Lat, t.Points[0].Lon));
                    await s.SendAsync("track.drawPoint", En(t.Points[^1].Lat, t.Points[^1].Lon));
                }
                else
                {
                    await s.SendAsync("track.drawCurve");
                    foreach (var p in t.Points) await s.SendAsync("track.drawPoint", En(p.Lat, p.Lon));
                    await s.SendAsync("track.drawFinish");
                }
                await s.PauseAsync(slow ? 1.0 : 0);
                await s.SendAsync("track.rename", Kit.F($"{trackIndex},{t.Name}"));
                trackIndex++;
            }
            int flagIndex = 0;
            foreach (var fl in fs.Flags)
            {
                await s.SendAsync("flag.placeAt", En(fl.Lat, fl.Lon));
                await s.PauseAsync(slow ? 0.5 : 0);
                await s.SendAsync("flag.rename", Kit.F($"{flagIndex},{fl.Name}"));
                await s.SendAsync("flag.setColor", Kit.F($"{flagIndex},{fl.Color}"));
                flagIndex++;
            }
            await s.PauseAsync(slow ? 1.0 : 0);
            sc.Step(Kit.F($"'{fs.Name}': boundary of {fs.Boundary.Count} points{(fs.HeadlandOffset is { } h ? Kit.F($", headland {h} m") : "")}, {fs.Tracks.Count} tracks, {fs.Flags.Count} flags"));

            if (fs.Job is { } job)
            {
                // A job needs the field opened with one: close the field-only open, start the job.
                await s.SendAsync("field.close");
                await s.PauseAsync(slow ? 2.5 : 0.3);
                await s.SendAsync("field.startJob", string.Join("\t", fs.Name, job.WorkType, "made by the update scenario harness", job.Task));
                await s.PauseAsync(slow ? 3.0 : 0.5);
                await s.SendAsync("section.master");
                for (int i = 0; i < job.SpeedSteps; i++) await s.SendAsync("sim.speedUp");
                await s.PauseAsync(job.DriveSeconds);
                await s.SendAsync("sim.stop");
                await s.PauseAsync(slow ? 1.0 : 0.3);
                await s.SendAsync("section.master");
                sc.Step(Kit.F($"'{fs.Name}': job '{job.Task}' ({job.WorkType}) driven {job.DriveSeconds} s at {job.SpeedSteps} km/h with the sections in Auto"));
            }
            await s.SendAsync("field.close");
            await s.PauseAsync(slow ? 3.0 : 0.5);
            sc.Step(Kit.F($"'{fs.Name}' closed"));
        }
    }

    /// <summary>The field's origin from what the build wrote: field.geojson (this and recent
    /// builds) or Field.txt's StartFix (older ones).</summary>
    public static async Task<(double Lat, double Lon)?> WaitForOriginAsync(string fieldDir, double seconds)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            var o = ReadOrigin(fieldDir);
            if (o != null) return o;
            await Task.Delay(200);
        }
        return ReadOrigin(fieldDir);
    }

    public static (double Lat, double Lon)? ReadOrigin(string fieldDir)
    {
        try
        {
            string geo = Path.Combine(fieldDir, "field.geojson");
            if (File.Exists(geo))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(geo));
                foreach (var feat in doc.RootElement.GetProperty("features").EnumerateArray())
                {
                    if (!feat.TryGetProperty("properties", out var props)) continue;
                    if (props.TryGetProperty("originLatitude", out var la) && props.TryGetProperty("originLongitude", out var lo))
                        return (la.GetDouble(), lo.GetDouble());
                }
            }
            string origin = Path.Combine(fieldDir, "field.origin");
            if (File.Exists(origin))
            {
                var parts = File.ReadAllText(origin).Trim().Split(',');
                if (parts.Length == 2) return (double.Parse(parts[0], Inv), double.Parse(parts[1], Inv));
            }
            string txt = Path.Combine(fieldDir, "Field.txt");
            if (File.Exists(txt))
            {
                var lines = File.ReadAllLines(txt);
                for (int i = 0; i < lines.Length - 1; i++)
                    if (lines[i].StartsWith("StartFix", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = lines[i + 1].Split(',');
                        if (parts.Length >= 2) return (double.Parse(parts[0], Inv), double.Parse(parts[1], Inv));
                    }
            }
        }
        catch { /* half-written: try again */ }
        return null;
    }
}

/// <summary>This build's command handler as the sender: every command runs on the host loop and
/// the rig waits for it (and the work it posts) to finish.</summary>
public sealed class RigSender : ICommandSender
{
    private readonly UpdateRig _rig;
    public RigSender(UpdateRig rig) { _rig = rig; }
    public string DataRoot => _rig.DataRoot;
    public Task SendAsync(string cmd, string arg = "") => _rig.SendAsync(cmd, arg);
    public async Task PauseAsync(double seconds)
    {
        if (seconds > 0) await Task.Delay(TimeSpan.FromSeconds(seconds));
        await _rig.DrainAsync();
    }
}
