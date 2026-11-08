// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AgOpenWeb.UpdateScenarios.Tests;

/// <summary>
/// One AgOpenGPS setting and where it must land in AgOpenWeb. <see cref="Key"/> is AgOpenGPS's
/// own setting name, <see cref="Value"/> what the 6.8.6 profile holds (the farmer's current
/// setup), <see cref="Dump"/> the AgOpenWeb dump key it must read back under and
/// <see cref="Reads"/> as what. <see cref="Stale"/> is the value the older combined
/// <c>Vehicles/&lt;name&gt;.XML</c> still holds: AgOpenGPS stopped writing that file at 6.8.2,
/// when the profile was split into VehicleProfiles, ToolProfiles and Environment, so on a 6.8.6
/// computer it is months behind. A read-back equal to it means the setting came from the wrong
/// file. <see cref="Group"/> names the settings tab, so missing settings are reported per tab.
/// </summary>
public sealed record AogSetting(string Key, string Dump, string Value, string Reads, string Group, string? Stale = null, string? StaleReads = null);

/// <summary>A tractor of the 6.8.6 installation: its <c>VehicleProfiles/&lt;name&gt;.xml</c>, and
/// whether a combined <c>Vehicles/&lt;name&gt;.XML</c> from before the split is still there
/// (paired there with the implement <see cref="ToolName"/>, as the combined file held both).</summary>
public sealed record AogVehicle(string Name, List<AogSetting> Settings, bool InLegacyFile, string ToolName);

/// <summary>An implement of the 6.8.6 installation: <c>ToolProfiles/&lt;name&gt;.xml</c>.</summary>
public sealed record AogTool(string Name, List<AogSetting> Settings, double[] SectionPositions, double[]? LegacySectionPositions);

/// <summary>AgIO's settings: the NTRIP caster among them (<c>AgIO/&lt;name&gt;.XML</c>).</summary>
public sealed record AogAgIo(string Name, string CasterHost, int CasterPort, string Mount, string User, string Password);

/// <summary>
/// A farmer's AgOpenGPS 6.8.6 folder (<c>Documents/AgOpenGPS</c>), as the program leaves it:
/// <c>VehicleProfiles/</c>, <c>ToolProfiles/</c> and <c>Environment/environment.xml</c> with the
/// current settings, <c>AgIO/</c> with the caster, <c>Vehicles/*.XML</c> left over from before
/// 6.8.2 with the settings of that day, and <c>Fields/</c> with every file AgOpenGPS writes
/// (boundary, headland, tracks, flags, coverage, contour, recorded path, elevation, background
/// image, KML, ISO-XML). The layout and the setting names are those of a real 6.8.6 computer.
/// </summary>
public sealed class AgOpenGpsInstall
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public const string Version = "6.8.6";

    public List<AogVehicle> Vehicles { get; } = new();
    public List<AogTool> Tools { get; } = new();
    /// <summary>Settings of <c>Environment/environment.xml</c> that AgOpenWeb keeps per profile
    /// (U-turn, tram): judged with every vehicle.</summary>
    public List<AogSetting> Environment { get; } = new();
    public AogAgIo AgIo { get; init; } = new("AgIO", "caster.example.net", 2101, "RTCM3_NEAR", "farmer", "s3cret-pt");
    public List<FieldSpec> Fields { get; } = new();
    /// <summary>The field with every sidecar file (coverage, contour, recorded path, elevation,
    /// background image).</summary>
    public FieldSpec FullField => Fields[0];
    /// <summary>The tractor and implement AgOpenGPS saved last, the pair in use there (their
    /// files are the newest of their folder).</summary>
    public AogVehicle VehicleInUse => Vehicles[0];
    public AogTool ToolInUse => Tools[0];
    /// <summary>The worked area of the full field's Sections.txt, m².</summary>
    public double CoverageSqM => AgOpenGpsFiles.CoverageSqM;

    /// <summary>What the farmer's 6.8.6 folder holds. Values differ from AgOpenWeb's defaults
    /// and from the stale ones, so a read-back tells which file it came from.</summary>
    public static AgOpenGpsInstall Standard()
    {
        var a = new AgOpenGpsInstall();

        // The tractor set up in 2024 on 6.7 and used since: its combined Vehicles/*.XML is still
        // there with that day's values; VehicleProfiles/ has today's.
        a.Vehicles.Add(new AogVehicle("Case Maxxum 135", new()
        {
            A("setVehicle_wheelbase", "Vehicle.Wheelbase", "2.7", "vehicle", stale: "2.55"),
            A("setVehicle_antennaHeight", "Vehicle.AntennaHeight", "2.8", "vehicle", stale: "2.9"),
            A("setVehicle_antennaPivot", "Vehicle.AntennaPivot", "1.2", "vehicle", stale: "0.9"),
            A("setVehicle_antennaOffset", "Vehicle.AntennaOffset", "-0.3", "vehicle", stale: "0.1"),
            A("setVehicle_maxSteerAngle", "Vehicle.MaxSteerAngle", "36", "vehicle", stale: "33"),
            A("setVehicle_maxAngularVelocity", "Vehicle.MaxAngularVelocity", "0.64", "vehicle", stale: "0.55"),
            A("setVehicle_trackWidth", "Vehicle.TrackWidth", "1.95", "vehicle", stale: "1.85"),
            A("setAS_Kp", "AutoSteer.ProportionalGain", "60", "steer", stale: "45"),
            A("setAS_countsPerDegree", "AutoSteer.CountsPerDegree", "112", "steer", stale: "105"),
            A("setAS_minSteerPWM", "AutoSteer.MinPwm", "32", "steer", stale: "28"),
            A("setAS_highSteerPWM", "AutoSteer.MaxPwm", "205", "steer", stale: "170"),
            A("setAS_wasOffset", "AutoSteer.WasOffset", "-42", "steer", stale: "7"),
            A("setAS_ackerman", "AutoSteer.Ackermann", "108", "steer", stale: "97"),
            A("setAS_maxSteerSpeed", "AutoSteer.MaxSteerSpeed", "16", "steer", stale: "14"),
            A("setAS_minSteerSpeed", "AutoSteer.MinSteerSpeed", "0.6", "steer", stale: "0.4"),
            A("setAS_isSteerInReverse", "AutoSteer.SteerInReverse", "True", "steer"),
            // PGN 251's bytes as AgOpenGPS keeps them: 0x58 = single A/D, Cytron, steer button;
            // 2 = pressure sensor, its trip point raw 91 (36 %).
            A("setArdSteer_setting0", "AutoSteer.ExternalEnable", "88", "steer", reads: "2", stale: "56", staleReads: "1"),
            A("setArdSteer_setting1", "AutoSteer.PressureSensorEnabled", "2", "steer", reads: "True"),
            A("setArdSteer_maxPulseCounts", "AutoSteer.PressureTripPoint", "91", "steer", reads: "36"),
            A("setIMU_rollZero", "Ahrs.RollZero", "1.3", "imu", stale: "0.7"),
            A("setIMU_rollFilter", "Ahrs.RollFilter", "0.35", "imu", stale: "0.22"),
            A("setIMU_invertRoll", "Ahrs.IsRollInvert", "True", "imu"),
            A("setGPS_headingFromWhichSource", "Connections.IsDualGps", "Dual", "gps", reads: "True"),
            A("setIMU_fusionWeight2", "Connections.HeadingFusionWeight", "0.1", "gps", reads: "0.5", stale: "0.08", staleReads: "0.4"),
            A("setGPS_dualHeadingOffset", "Connections.DualHeadingOffset", "90", "gps", stale: "45"),
            A("setGPS_minimumStepLimit", "Connections.MinGpsStep", "0.08", "gps", stale: "0.06"),
        }, InLegacyFile: true, ToolName: "Pulverizador 8 m"));

        // A tractor added on 6.8.6: no combined file ever existed for it.
        a.Vehicles.Add(new AogVehicle("Valtra N134", new()
        {
            A("setVehicle_wheelbase", "Vehicle.Wheelbase", "2.665", "vehicle"),
            A("setVehicle_antennaHeight", "Vehicle.AntennaHeight", "2.95", "vehicle"),
            A("setVehicle_antennaPivot", "Vehicle.AntennaPivot", "0.6", "vehicle"),
            A("setVehicle_maxSteerAngle", "Vehicle.MaxSteerAngle", "42", "vehicle"),
            A("setAS_Kp", "AutoSteer.ProportionalGain", "55", "steer"),
            A("setAS_countsPerDegree", "AutoSteer.CountsPerDegree", "95", "steer"),
        }, InLegacyFile: false, ToolName: "Semeador 3 m"));

        // The sprayer: 8 sections of 1 m today; 6 of 1 m in the combined file of 2024.
        a.Tools.Add(new AogTool("Pulverizador 8 m", new()
        {
            A("setVehicle_toolWidth", "Tool.Width", "8", "tool", stale: "6.5"),
            A("setVehicle_toolOverlap", "Tool.Overlap", "0.05", "tool", stale: "0.1"),
            A("setVehicle_toolOffset", "Tool.Offset", "0.1", "tool", stale: "0.2"),
            A("setVehicle_hitchLength", "Tool.HitchLength", "1.6", "tool", stale: "1.4"),
            A("setTool_isToolRearFixed", "Tool.IsToolRearFixed", "True", "tool"),
            A("setVehicle_toolLookAheadOn", "Tool.LookAheadOnSetting", "1.2", "sections", stale: "1.1"),
            A("setVehicle_toolLookAheadOff", "Tool.LookAheadOffSetting", "0.4", "sections", stale: "0.6"),
            A("setVehicle_numSections", "NumSections", "8", "sections", stale: "6"),
            A("setSection_position", "ActualToolWidth", "8", "sections", stale: "6"),
            A("setVehicle_minCoverage", "Tool.MinCoverage", "90", "sections", stale: "95"),
            A("setVehicle_goalPointLookAheadHold", "Guidance.GoalPointLookAheadHold", "2.9", "guidance", stale: "3.2"),
            A("purePursuitIntegralGainAB", "Guidance.PurePursuitIntegralGain", "0.12", "guidance", stale: "0.05"),
            A("setVehicle_isStanleyUsed", "Guidance.IsPurePursuit", "True", "guidance", reads: "False"),
            A("setHeadland_isSectionControlled", "Tool.IsHeadlandSectionControl", "False", "sections"),
        }, Positions(8, 1.0), Positions(6, 1.0)));

        // The seed drill, trailed, added on 6.8.6 (its trailing hitch length negative, as
        // AgOpenGPS writes it; AgOpenWeb keeps the distance).
        a.Tools.Add(new AogTool("Semeador 3 m", new()
        {
            A("setVehicle_toolWidth", "Tool.Width", "3", "tool"),
            A("setTool_isToolTrailing", "Tool.IsToolTrailing", "True", "tool"),
            A("setTool_isToolRearFixed", "Tool.IsToolRearFixed", "False", "tool"),
            A("setVehicle_toolTrailingHitchLength", "Tool.TrailingHitchLength", "-2.4", "tool", reads: "2.4"),
            A("setVehicle_numSections", "NumSections", "12", "sections"),
            A("setSection_position", "ActualToolWidth", "3", "sections"),
            A("setVehicle_toolOffDelay", "Tool.TurnOffDelay", "0.3", "sections"),
            A("setVehicle_slowSpeedCutoff", "Tool.SlowSpeedCutoff", "1.5", "sections"),
        }, Positions(12, 0.25), null));

        a.Environment.AddRange(new[]
        {
            A("set_youTurnRadius", "Guidance.UTurnRadius", "5.5", "uturn", stale: "7.5"),
            A("set_youTurnExtensionLength", "Guidance.UTurnExtension", "12", "uturn", stale: "15"),
            A("set_youTurnDistanceFromBoundary", "Guidance.UTurnDistanceFromBoundary", "3", "uturn", stale: "2.5"),
            A("set_uTurnStyle", "Guidance.UTurnStyle", "1", "uturn"),
            A("setAS_uTurnSmoothing", "Guidance.UTurnSmoothing", "10", "uturn", stale: "12"),
            A("setAS_uTurnCompensation", "Guidance.UTurnCompensation", "1.2", "uturn", stale: "1.1"),
            A("setTram_tramWidth", "Tram.TramWidth", "18", "tram", stale: "21"),
            A("setTram_passes", "Guidance.TramPasses", "4", "tram", stale: "5"),
            A("setAutoSwitchDualFixOn", "Connections.AutoDualFix", "True", "gps"),
            A("setIMU_isReverseOn", "Connections.ReverseDetection", "False", "gps"),
        });

        foreach (var f in Farm.Standard().Fields) { f.Job = null; a.Fields.Add(f); }
        return a;
    }

    private static AogSetting A(string key, string dump, string value, string group, string? stale = null, string? reads = null, string? staleReads = null)
        => new(key, dump, value, reads ?? value, group, stale, staleReads ?? stale);

    /// <summary>Section edges, <paramref name="n"/> sections of <paramref name="width"/> m centred.</summary>
    private static double[] Positions(int n, double width)
    {
        var p = new double[n + 1];
        for (int i = 0; i <= n; i++) p[i] = -n * width / 2 + i * width;
        return p;
    }

    /// <summary>Write the folder as AgOpenGPS 6.8.6 leaves it.</summary>
    public void Write(string aogRoot)
    {
        Directory.CreateDirectory(aogRoot);
        foreach (var v in Vehicles)
        {
            WriteSettingsXml(Path.Combine(aogRoot, "VehicleProfiles", v.Name + ".xml"), "AgOpenGPS.Properties.VehicleSettings",
                v.Settings.Select(s => (s.Key, s.Value)).Append(("setVehicle_vehicleType", "0")).Append(("setBrand_TBrand", "0")));
            File.Copy(Path.Combine(aogRoot, "VehicleProfiles", v.Name + ".xml"), Path.Combine(aogRoot, "VehicleProfiles", v.Name + ".last"), overwrite: true);
            if (v.InLegacyFile)
            {
                var tool = Tools.First(t => t.Name == v.ToolName);
                var pairs = new List<(string, string)>
                {
                    ("setWindow_Size", "1280, 800"), ("setMenu_isMetric", "True"), ("setDisplay_isDayMode", "True"),
                    ("setKey_hotkeys", "ACFGMNPTUVYZ"), ("setF_CurrentDir", "C:\\Users\\farmer\\Documents\\AgOpenGPS\\Fields\\"),
                    ("setVehicle_vehicleType", "0"),
                };
                pairs.AddRange(v.Settings.Select(s => (s.Key, s.Stale ?? s.Value)));
                // The combined file of 6.7/6.8.0 named the trailing hitch setTool_…; the sections
                // were the implement of that day.
                pairs.AddRange(tool.Settings.Where(s => !s.Key.StartsWith("setSection_position", StringComparison.Ordinal))
                    .Select(s => (s.Key == "setVehicle_toolTrailingHitchLength" ? "setTool_toolTrailingHitchLength" : s.Key, s.Stale ?? s.Value)));
                pairs.AddRange(SectionPairs(tool.LegacySectionPositions ?? tool.SectionPositions));
                pairs.AddRange(Environment.Select(s => (s.Key, s.Stale ?? s.Value)));
                WriteSettingsXml(Path.Combine(aogRoot, "Vehicles", v.Name + ".XML"), "AgOpenGPS.Properties.Settings", pairs);
            }
        }
        foreach (var t in Tools)
        {
            var pairs = t.Settings.Where(s => !s.Key.StartsWith("setSection_position", StringComparison.Ordinal)).Select(s => (s.Key, s.Value)).ToList();
            pairs.AddRange(SectionPairs(t.SectionPositions));
            pairs.Add(("setTool_zones", "2,10,20,0,0,0,0,0,0"));
            pairs.Add(("setTool_isSectionsNotZones", "True"));
            WriteSettingsXml(Path.Combine(aogRoot, "ToolProfiles", t.Name + ".xml"), "AgOpenGPS.Properties.ToolSettings", pairs);
            File.Copy(Path.Combine(aogRoot, "ToolProfiles", t.Name + ".xml"), Path.Combine(aogRoot, "ToolProfiles", t.Name + ".last"), overwrite: true);
        }
        {
            var pairs = new List<(string, string)>
            {
                ("setWindow_Size", "1280, 800"), ("setMenu_isMetric", "True"), ("setMenu_isGridOn", "False"),
                ("setDisplay_isDayMode", "True"), ("setDisplay_lightbarCmPerPixel", "4"), ("setDisplay_camZoom", "18"),
                ("setKey_hotkeys", "ACFGMNPTUVYZ"), ("setSound_isUturnOn", "True"), ("setGPS_isRTK", "True"),
                ("setF_CurrentDir", "C:\\Users\\farmer\\Documents\\AgOpenGPS\\Fields\\"),
            };
            pairs.AddRange(Environment.Select(s => (s.Key, s.Value)));
            WriteSettingsXml(Path.Combine(aogRoot, "Environment", "environment.xml"), "AgOpenGPS.Properties.Settings", pairs);
        }
        // AgOpenGPS writes the profiles in use when it saves: the first tractor and implement are
        // the newest files of their folders, the others an hour older each.
        var saved = new DateTime(2026, 9, 15, 18, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < Vehicles.Count; i++)
            foreach (var ext in new[] { ".xml", ".last" })
                File.SetLastWriteTimeUtc(Path.Combine(aogRoot, "VehicleProfiles", Vehicles[i].Name + ext), saved.AddHours(-i));
        for (int i = 0; i < Tools.Count; i++)
            foreach (var ext in new[] { ".xml", ".last" })
                File.SetLastWriteTimeUtc(Path.Combine(aogRoot, "ToolProfiles", Tools[i].Name + ext), saved.AddHours(-i));
        foreach (var legacy in Directory.EnumerateFiles(Path.Combine(aogRoot, "Vehicles")))
            File.SetLastWriteTimeUtc(legacy, new DateTime(2026, 4, 8, 9, 0, 0, DateTimeKind.Utc));

        WriteSettingsXml(Path.Combine(aogRoot, "AgIO", AgIo.Name + ".XML"), "AgIO.Properties.Settings", new[]
        {
            ("setPort_portNameGPS", "COM3"), ("setPort_baudRateGPS", "460800"), ("setUDP_isOn", "True"),
            ("setIP_thisPort", "9999"), ("setIP_autoSteerPort", "8888"),
            ("setNTRIP_casterURL", AgIo.CasterHost), ("setNTRIP_casterIP", "203.0.113.10"), ("setNTRIP_casterPort", AgIo.CasterPort.ToString(Inv)),
            ("setNTRIP_mount", AgIo.Mount), ("setNTRIP_userName", AgIo.User), ("setNTRIP_userPassword", AgIo.Password),
            ("setNTRIP_isOn", "True"), ("setNTRIP_sendGGAInterval", "10"), ("setNTRIP_isTCP", "True"), ("setNTRIP_isHTTP10", "False"),
            ("setNTRIP_sendToUDPPort", "2233"), ("setNTRIP_packetSize", "256"),
        });

        var fieldsDir = Path.Combine(aogRoot, "Fields");
        foreach (var f in Fields)
        {
            string dir = Path.Combine(fieldsDir, f.Name);
            AgOpenGpsFiles.Write(dir, f, withCoverage: f == FullField);
            if (f == FullField) WriteSidecars(dir, f);
            WriteIsoXml(dir, f);
        }
        Directory.CreateDirectory(Path.Combine(aogRoot, "Logs"));
        File.WriteAllText(Path.Combine(aogRoot, "Logs", "AgOpenGPS_Events_Log.txt"),
            "08:00:00-> AOG Version: " + Version + "\n08:00:00-> Program Directory: C:\\Program Files\\AgOpenGPS\n");
    }

    private static IEnumerable<(string, string)> SectionPairs(double[] positions)
    {
        for (int i = 0; i < 17; i++)
            yield return ("setSection_position" + (i + 1).ToString(Inv), i < positions.Length ? positions[i].ToString("0.000000", Inv) : "0");
    }

    /// <summary>The files AgOpenGPS writes beside the field besides boundary, tracks, flags and
    /// coverage: the background image, a contour strip, a recorded path, elevation and the
    /// headland lines file.</summary>
    private static void WriteSidecars(string dir, FieldSpec f)
    {
        File.WriteAllLines(Path.Combine(dir, "BackPic.Txt"), new[] { "$BackPic", "True", "150.000", "-150.000", "100.000", "-100.000" });
        File.WriteAllBytes(Path.Combine(dir, "BackPic.png"), OnePixelPng);
        var contour = new List<string> { "$Contour", "12" };
        for (int i = 0; i < 12; i++) contour.Add(Kit.F($"{-30 + i * 5.0:0.###},{-40 + i * 1.5:0.###},{1.2:0.###}"));
        File.WriteAllLines(Path.Combine(dir, "Contour.txt"), contour);
        var rec = new List<string> { "$RecPath", "10" };
        for (int i = 0; i < 10; i++) rec.Add(Kit.F($"{10 + i * 4.0:0.###},{-50 + i * 6.0:0.###},{0.6:0.###},{8.5:0.#},{(i > 2 ? 1 : 0)}"));
        File.WriteAllLines(Path.Combine(dir, "RecPath.txt"), rec);
        var elev = new List<string>
        {
            DateTime.Now.ToString("yyyy-MMM-dd hh:mm:ss tt", Inv), "$FieldDir", "Elevation", "$Offsets", "0,0", "Convergence", "0", "StartFix",
            f.Lat.ToString("0.#######", Inv) + "," + f.Lon.ToString("0.#######", Inv),
            "Latitude,Longitude,Elevation,Quality,Easting,Northing,Heading,Roll",
        };
        for (int i = 0; i < 6; i++)
            elev.Add(Kit.F($"{f.Lat + i * 0.00002:0.#######},{f.Lon + i * 0.00001:0.#######},{412.3 + i * 0.4:0.##},4,{i * 1.1:0.###},{i * 2.2:0.###},{0.3:0.###},{0.2:0.##}"));
        File.WriteAllLines(Path.Combine(dir, "Elevation.txt"), elev);
        File.WriteAllLines(Path.Combine(dir, "Headlines.txt"), new[] { "$HeadLines" });
        File.WriteAllLines(Path.Combine(dir, "RateMap.Txt"), new[] { "$RateMap", "False", "300", "-300", "300", "-300", "1" });
        File.WriteAllText(Path.Combine(dir, "Field.kml"),
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<kml xmlns=\"http://www.opengis.net/kml/2.2\"><Document><name>" + f.Name + "</name></Document></kml>\n");
    }

    /// <summary>AgOpenGPS's ISO-XML export of the field (version 4 folder), as 6.8.2+ writes it.</summary>
    private static void WriteIsoXml(string dir, FieldSpec f)
    {
        string iso = Path.Combine(dir, "zISOXML", "v4");
        Directory.CreateDirectory(iso);
        var sb = new StringBuilder();
        sb.Append("\uFEFF<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<ISO11783_TaskData VersionMajor=\"4\" VersionMinor=\"2\" ManagementSoftwareManufacturer=\"AgOpenGPS\" ManagementSoftwareVersion=\"")
          .Append(Version).Append("\" TaskControllerManufacturer=\"unknown\" TaskControllerVersion=\"unknown\" DataTransferOrigin=\"1\">\n  <PFD A=\"PFD1\" C=\"")
          .Append(f.Name).Append("\" D=\"60000\">\n    <PLN A=\"1\">\n      <LSG A=\"1\">\n");
        foreach (var (lat, lon) in f.Boundary)
            sb.Append(Kit.F($"        <PNT A=\"2\" C=\"{lat:0.000000000}\" D=\"{lon:0.000000000}\" />\n"));
        sb.Append("      </LSG>\n    </PLN>\n  </PFD>\n</ISO11783_TaskData>\n");
        File.WriteAllText(Path.Combine(iso, "TASKDATA.xml"), sb.ToString());
    }

    /// <summary>A .NET user settings file as AgOpenGPS writes them: one <c>&lt;setting&gt;</c>
    /// per key under the program's settings section.</summary>
    public static void WriteSettingsXml(string path, string section, IEnumerable<(string Key, string Value)> pairs)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var sb = new StringBuilder();
        sb.Append("\uFEFF<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<configuration>\n    <userSettings>\n        <").Append(section).Append(">\n");
        foreach (var (k, v) in pairs)
            sb.Append("            <setting name=\"").Append(k).Append("\" serializeAs=\"String\">\n                <value>")
              .Append(System.Security.SecurityElement.Escape(v)).Append("</value>\n            </setting>\n");
        sb.Append("        </").Append(section).Append(">\n    </userSettings>\n</configuration>");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    /// <summary>What a farmer does: copy the AgOpenGPS folders into AgOpenWeb's data folder
    /// (<c>Documents/AgOpenWeb</c>): every field folder into <c>Fields/</c>, and the profile
    /// folders as they are. Returns the top-level folders copied.</summary>
    public static List<string> CopyInto(string aogRoot, string aowDataRoot)
    {
        string dst = Path.Combine(aowDataRoot, "AgOpenWeb");
        var copied = new List<string>();
        string fields = Path.Combine(aogRoot, "Fields");
        if (Directory.Exists(fields))
        {
            foreach (var dir in Directory.EnumerateDirectories(fields))
                if (File.Exists(Path.Combine(dir, "Field.txt")))
                    Kit.CopyDirectory(dir, Path.Combine(dst, "Fields", Path.GetFileName(dir)));
            copied.Add("Fields");
        }
        foreach (var name in new[] { "Vehicles", "VehicleProfiles", "ToolProfiles", "Environment", "AgIO" })
        {
            string src = Path.Combine(aogRoot, name);
            if (!Directory.Exists(src)) continue;
            Kit.CopyDirectory(src, Path.Combine(dst, name));
            copied.Add(name);
        }
        return copied;
    }

    /// <summary>A hash of every file under <paramref name="dir"/> (path and bytes), to show a
    /// folder was not touched.</summary>
    public static string TreeHash(string dir)
    {
        using var sha = SHA256.Create();
        var sb = new StringBuilder();
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            sb.Append(Path.GetRelativePath(dir, f).Replace('\\', '/')).Append('=')
              .Append(Convert.ToHexString(sha.ComputeHash(File.ReadAllBytes(f)))).Append('\n');
        }
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>How many profile files of each kind the folder holds (for a farmer's own folder,
    /// counts only).</summary>
    public static (int Legacy, int VehicleProfiles, int ToolProfiles, int Fields) Count(string aogRoot)
    {
        int C(string sub, string pattern) => Directory.Exists(Path.Combine(aogRoot, sub)) ? Directory.GetFiles(Path.Combine(aogRoot, sub), pattern).Length : 0;
        int fields = Directory.Exists(Path.Combine(aogRoot, "Fields"))
            ? Directory.EnumerateDirectories(Path.Combine(aogRoot, "Fields")).Count(d => File.Exists(Path.Combine(d, "Field.txt"))) : 0;
        return (C("Vehicles", "*.XML"), C("VehicleProfiles", "*.xml"), C("ToolProfiles", "*.xml"), fields);
    }

    /// <summary>A 1 × 1 transparent PNG: AgOpenGPS's BackPic.png stands in for a real image.</summary>
    private static readonly byte[] OnePixelPng =
    {
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
        0x89, 0x00, 0x00, 0x00, 0x0A, 0x49, 0x44, 0x41, 0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
        0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE,
        0x42, 0x60, 0x82,
    };
}
