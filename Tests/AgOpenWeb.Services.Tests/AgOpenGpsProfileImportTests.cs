// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Gps;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Profile;
using AgOpenWeb.Services.Tool;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// AgOpenGPS 6.8.2+ keeps a tractor in <c>VehicleProfiles/</c>, an implement in <c>ToolProfiles/</c>,
/// the U-turn and tram settings in <c>Environment/environment.xml</c> (<c>DefaultEnvironment.xml</c>
/// on a fresh install) and the NTRIP caster in
/// <c>AgIO/</c>. Copied into AgOpenWeb's data folder, each becomes an AgOpenWeb profile once, and
/// the copied file is gone after.
/// </summary>
[TestFixture]
[NonParallelizable] // AGOPENWEB_DATA is process-wide (the NTRIP test)
public class AgOpenGpsProfileImportTests
{
    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "aow-aogimport-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed class Tools : ToolProfileService
    {
        public Tools(string dir) : base(NullLogger<ToolProfileService>.Instance, dir) { }
    }

    /// <summary>An AgOpenGPS settings file as 6.8.6 writes it.</summary>
    private string Write(string relative, DateTime savedUtc, params (string Name, string Value)[] settings)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.AppendLine("<configuration><userSettings><AgOpenGPS.Properties.Settings>");
        foreach (var (name, value) in settings)
            sb.Append("<setting name=\"").Append(name).Append("\" serializeAs=\"String\"><value>").Append(value).AppendLine("</value></setting>");
        sb.AppendLine("</AgOpenGPS.Properties.Settings></userSettings></configuration>");
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, sb.ToString());
        File.SetLastWriteTimeUtc(path, savedUtc);
        return path;
    }

    private static Dictionary<string, string> Settings(params (string Name, string Value)[] settings)
        => settings.ToDictionary(s => s.Name, s => s.Value);

    [Test]
    public void Sections_TakeTheirWidthsFromAgOpenGpsEdges()
    {
        // 12 sections of 25 cm: AgOpenWeb's section widths are what it uses, not the edges.
        var s = new List<(string, string)> { ("setVehicle_toolWidth", "3"), ("setVehicle_numSections", "12") };
        for (int i = 0; i <= 12; i++)
            s.Add(($"setSection_position{i + 1}", (-1.5 + 0.25 * i).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var store = new ConfigurationStore();
        AgOpenGpsSettings.ApplyToStore(Settings(s.ToArray()), "Semeador", "Semeador.xml", store);

        Assert.That(store.NumSections, Is.EqualTo(12));
        Assert.That(store.Tool.GetSectionWidth(0), Is.EqualTo(25.0).Within(1e-9));
        Assert.That(store.Tool.GetSectionWidth(11), Is.EqualTo(25.0).Within(1e-9));
        Assert.That(store.ActualToolWidth, Is.EqualTo(3.0).Within(1e-9));
    }

    [Test]
    public void Zones_AreSectionsOfTheMultiWidth()
    {
        var store = new ConfigurationStore();
        AgOpenGpsSettings.ApplyToStore(Settings(
            ("setTool_isSectionsNotZones", "False"), ("setVehicle_numSections", "3"),
            ("setTool_numSectionsMulti", "20"), ("setTool_sectionWidthMulti", "0.3"),
            ("setTool_zones", "4,5,10,15,20,0,0,0,0")), "Planter", "Planter.xml", store);

        Assert.That(store.NumSections, Is.EqualTo(20));
        Assert.That(store.ActualToolWidth, Is.EqualTo(6.0).Within(1e-9));
        Assert.That(store.Tool.Zones, Is.EqualTo(4));
    }

    [Test]
    public void Zones_KeepTheSectionEachZoneEndsAt()
    {
        // AgOpenGPS: setTool_zones is "count,end1,end2,…" (CTool: zoneRanges[0] is the count),
        // and zone z switches sections zoneRanges[z-1]..zoneRanges[z]-1, zone 1 from 0
        // (Sections.Designer btnZoneX_Click). AgOpenWeb keeps the count apart and [0] at 0.
        var store = new ConfigurationStore();
        AgOpenGpsSettings.ApplyToStore(Settings(
            ("setTool_isSectionsNotZones", "False"), ("setTool_numSectionsMulti", "20"),
            ("setTool_sectionWidthMulti", "0.3"), ("setTool_zones", "4,3,10,14,20,0,0,0,0")), "Planter", "Planter.xml", store);

        Assert.That(store.Tool.Zones, Is.EqualTo(4));
        Assert.That(store.Tool.ZoneRanges, Is.EqualTo(new[] { 0, 3, 10, 14, 20, 0, 0, 0, 0 }).AsCollection);
        Assert.That(store.Tool.GetZoneEndSection(1), Is.EqualTo(3), "zone 1 is sections 1 to 3");
        Assert.That(store.Tool.GetZoneEndSection(4), Is.EqualTo(20), "zone 4 is sections 15 to 20");
    }

    [Test]
    public void AntennaOffset_LeftInAgOpenGps_PutsThePivotOnTheCentreline()
    {
        // AgOpenGPS ConfigVehicle: "Left" is checked when the setting is above 0, so an antenna
        // 30 cm left of centre is saved as 0.3 (Position.designer adds it to the fix, towards the
        // right, to reach the centreline). AgOpenWeb's transform takes a left antenna negative.
        var store = new ConfigurationStore();
        AgOpenGpsSettings.ApplyToStore(Settings(("setVehicle_antennaOffset", "0.3"), ("setVehicle_antennaPivot", "0")), "T", "T.xml", store);

        Assert.That(store.Vehicle.AntennaOffset, Is.EqualTo(-0.3));
        // Heading north: an antenna 30 cm to the left is 30 cm west of the centreline.
        double easting = -0.3, northing = 0;
        AntennaToPivotTransform.Apply(ref easting, ref northing, 0.0, store.Vehicle, 0.0);
        Assert.That(easting, Is.EqualTo(0.0).Within(1e-9), "the pivot is on the centreline, not 60 cm off it");
    }

    [Test]
    public void AntennaOffset_RightInAgOpenGps_PutsThePivotOnTheCentreline()
    {
        // AgOpenGPS ConfigVehicle: "Right" saves the number typed × -1.
        var store = new ConfigurationStore();
        AgOpenGpsSettings.ApplyToStore(Settings(("setVehicle_antennaOffset", "-0.3"), ("setVehicle_antennaPivot", "0")), "T", "T.xml", store);

        Assert.That(store.Vehicle.AntennaOffset, Is.EqualTo(0.3));
        double easting = 0.3, northing = 0;
        AntennaToPivotTransform.Apply(ref easting, ref northing, 0.0, store.Vehicle, 0.0);
        Assert.That(easting, Is.EqualTo(0.0).Within(1e-9));
    }

    [Test]
    public void AntennaOffset_Centre_StaysZero()
    {
        var store = new ConfigurationStore();
        AgOpenGpsSettings.ApplyToStore(Settings(("setVehicle_antennaOffset", "0")), "T", "T.xml", store);
        Assert.That(store.Vehicle.AntennaOffset, Is.EqualTo(0.0));
        Assert.That(double.IsNegative(store.Vehicle.AntennaOffset), Is.False, "0, not -0");
    }

    [Test]
    public void TankHitch_SavedNegativeByAgOpenGps_PutsTheTankBehindTheHitch()
    {
        // AgOpenGPS ConfigTool: the tank hitch and the trailing hitch are saved as the number
        // typed × -1 (nudTankHitch, nudTrailingHitchLength) and shown with Math.Abs. A tow-between
        // tank 1.8 m behind the tractor's hitch, the tool 2.4 m behind the tank.
        var store = new ConfigurationStore();
        AgOpenGpsSettings.ApplyToStore(Settings(
            ("setTool_isToolTrailing", "True"), ("setTool_isToolTBT", "True"), ("setTool_isToolRearFixed", "False"),
            ("setVehicle_tankTrailingHitchLength", "-1.8"), ("setVehicle_toolTrailingHitchLength", "-2.4"),
            ("setTool_trailingToolToPivotLength", "0")), "Cart", "Cart.xml", store);

        Assert.That(store.Tool.TankTrailingHitchLength, Is.EqualTo(1.8));
        // Heading north, straight behind the tractor: the tool is 4.2 m south of the hitch.
        var (easting, northing) = AgOpenWeb.Models.Tool.RigGeometry.TrailedToolCentre(0, 0, 0.0, 0.0, store.Tool);
        Assert.That(easting, Is.EqualTo(0.0).Within(1e-9));
        Assert.That(northing, Is.EqualTo(-4.2).Within(1e-9), "tank and tool behind the hitch (-0.6 with the tank ahead of it)");
    }

    [Test]
    public void TankHitch_AgOpenGpsDefault_IsTheSameDistance()
    {
        // The value AgOpenGPS ships with is +3 (ToolSettings); nobody typed it, so it is not negative.
        var store = new ConfigurationStore();
        AgOpenGpsSettings.ApplyToStore(Settings(("setVehicle_tankTrailingHitchLength", "3")), "T", "T.xml", store);
        Assert.That(store.Tool.TankTrailingHitchLength, Is.EqualTo(3.0));
    }

    [Test]
    public void MachineModule_RelayMapHydraulicLiftAndUserValues()
    {
        // AgOpenGPS ConfigModule: setArdMac_setting0 bit 0 inverts the relays, bit 1 is the
        // hydraulic lift switch; setRelay_pinConfig is the function of each of the 24 pins
        // (17 Hyd Up, 18 Hyd Down, 19 Tram Right, 20 Tram Left, 21 Geo Stop).
        var store = new ConfigurationStore();
        AgOpenGpsSettings.ApplyToStore(Settings(
            ("setArdMac_setting0", "3"), ("setArdMac_isHydEnabled", "0"),
            ("setArdMac_hydRaiseTime", "5"), ("setArdMac_hydLowerTime", "6"), ("setVehicle_hydraulicLiftLookAhead", "2.5"),
            ("setArdMac_user1", "11"), ("setArdMac_user2", "12"), ("setArdMac_user3", "13"), ("setArdMac_user4", "14"),
            ("setRelay_pinConfig", "1,2,3,4,5,6,7,8,17,18,19,20,21,0,0,0,0,0,0,0,0,0,0,0")), "Sprayer", "Sprayer.xml", store);

        var m = store.Machine;
        Assert.That((m.InvertRelay, m.HydraulicLiftEnabled), Is.EqualTo((true, true)));
        Assert.That((m.RaiseTime, m.LowerTime, m.LookAhead), Is.EqualTo((5, 6, 2.5)));
        Assert.That((m.User1Value, m.User2Value, m.User3Value, m.User4Value), Is.EqualTo((11, 12, 13, 14)));
        Assert.That(m.PinAssignments.Take(13), Is.EqualTo(new[]
        {
            PinFunction.Section1, PinFunction.Section2, PinFunction.Section3, PinFunction.Section4,
            PinFunction.Section5, PinFunction.Section6, PinFunction.Section7, PinFunction.Section8,
            PinFunction.HydUp, PinFunction.HydDown, PinFunction.TramRight, PinFunction.TramLeft, PinFunction.GeoStop,
        }).AsCollection);
        Assert.That(m.PinAssignments.Skip(13), Is.All.EqualTo(PinFunction.None));

        // Hydraulic lift off, relays not inverted: the lift follows bit 1 only.
        AgOpenGpsSettings.ApplyToStore(Settings(("setArdMac_setting0", "0"), ("setArdMac_isHydEnabled", "1")), "Sprayer", "Sprayer.xml", store);
        Assert.That((store.Machine.InvertRelay, store.Machine.HydraulicLiftEnabled), Is.EqualTo((false, false)));
    }

    [Test]
    public void TripPoint_IsThePercentAgOpenGpsShows()
    {
        // FormSteer: lblhsbarSensor = (int)(raw × 0.3921568627) %, cut not rounded: raw 91 is 35 %.
        var a = new AutoSteerConfig();
        AgOpenGpsSettings.ApplyAutoSteer(Settings(("setArdSteer_setting1", "2"), ("setArdSteer_maxPulseCounts", "91")), a);
        Assert.That(a.PressureSensorEnabled, Is.True);
        Assert.That(a.PressureTripPoint, Is.EqualTo(35));
    }

    [Test]
    public void DeadZoneAndNudgeSteps_InAgOpenWebsUnits()
    {
        // FormSteer: setAS_deadZoneHeading is the degrees typed × 100; the nudge steps are cm.
        var a = new AutoSteerConfig();
        AgOpenGpsSettings.ApplyAutoSteer(Settings(
            ("setAS_deadZoneHeading", "25"), ("setAS_deadZoneDelay", "3"),
            ("setAS_snapDistance", "12.7"), ("setAS_snapDistanceRef", "2")), a);
        Assert.That(a.DeadzoneHeading, Is.EqualTo(0.25).Within(1e-9));
        Assert.That(a.DeadzoneDelay, Is.EqualTo(3));
        Assert.That((a.NudgeDistance, a.RefNudgeDistance), Is.EqualTo((13, 2)));
    }

    [Test]
    public void WholeNumbersAgOpenGpsKeepsAsDoubles_AreRounded()
    {
        var store = new ConfigurationStore();
        AgOpenGpsSettings.ApplyToStore(Settings(("setVehicle_minCoverage", "87.5"), ("setAS_uTurnSmoothing", "12.4")), "T", "T.xml", store);
        Assert.That(store.Tool.MinCoverage, Is.EqualTo(88), "not the default 100");
        Assert.That(store.Guidance.UTurnSmoothing, Is.EqualTo(12), "not the default 14");
    }

    [Test]
    public void MultiColouredSections_ReadUnderTheNameAgOpenGpsWrites()
    {
        var store = new ConfigurationStore();
        AgOpenGpsSettings.ApplyToStore(Settings(("setColor_isMultiColorSections", "True")), "T", "T.xml", store);
        Assert.That(store.Tool.IsMultiColoredSections, Is.True);
    }

    [Test]
    public void Run_ConvertsEveryTractorAndImplement_AndDeletesTheCopies()
    {
        var older = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
        var newer = older.AddDays(10);
        string tractorA = Write("VehicleProfiles/Case.xml", newer, ("setVehicle_wheelbase", "2.76"), ("setAS_Kp", "120"), ("setAS_minSteerSpeed", "1.2"));
        File.WriteAllText(Path.ChangeExtension(tractorA, ".last"), "copy");
        Write("VehicleProfiles/Valtra.xml", older, ("setVehicle_wheelbase", "2.65"));
        // The implement saved last is the one in use: its guidance gains go to every tractor.
        // AgOpenGPS writes a hitch behind the tractor negative; a trailed implement's is the
        // tractor's hitch pin, which AgOpenWeb keeps with the tractor.
        Write("ToolProfiles/Sprayer.xml", newer, ("setVehicle_toolWidth", "8"), ("setVehicle_isStanleyUsed", "True"), ("stanleyHeadingErrorGain", "0.7"),
            ("setTool_isToolRearFixed", "True"), ("setVehicle_hitchLength", "-1.6"));
        Write("ToolProfiles/Seeder.xml", older, ("setVehicle_toolWidth", "3"), ("setVehicle_isStanleyUsed", "False"),
            ("setTool_isToolTrailing", "True"), ("setTool_isToolRearFixed", "False"), ("setVehicle_hitchLength", "-1.5"));
        Write("Environment/environment.xml", older, ("set_youTurnRadius", "7.5"), ("setTram_passes", "4"));

        string vehicles = Path.Combine(_root, "Vehicles");
        Directory.CreateDirectory(vehicles);
        var tools = new Tools(Path.Combine(_root, "Tools"));
        var autoSteer = new Dictionary<string, AutoSteerConfig>();

        var result = AgOpenGpsProfileImport.Run(_root, vehicles, tools, (n, a) => autoSteer[n] = a);

        Assert.That(result.Vehicles, Is.EqualTo(new[] { "Case", "Valtra" }).AsCollection, "the tractor saved last first");
        Assert.That(result.Tools, Is.EqualTo(new[] { "Sprayer", "Seeder" }).AsCollection);
        Assert.That(result.Skipped, Is.Empty);

        var store = new ConfigurationStore();
        Assert.That(VehicleProfileJsonService.Load(vehicles, "Case", store), Is.True);
        Assert.That(store.Vehicle.Wheelbase, Is.EqualTo(2.76));
        Assert.That(store.Guidance.IsPurePursuit, Is.False, "Stanley, from the implement in use");
        Assert.That(store.Vehicle.HitchLength, Is.EqualTo(1.5), "the hitch pin, from the trailed implement");
        Assert.That(autoSteer["Case"].ProportionalGain, Is.EqualTo(120));
        Assert.That(autoSteer["Case"].MinSteerSpeed, Is.EqualTo(1.2));

        var sprayer = new ConfigurationStore();
        Assert.That(tools.Load("Sprayer", sprayer), Is.True);
        Assert.That(sprayer.Tool.HitchLength, Is.EqualTo(1.6), "the distance, not 0");

        var tool = new ConfigurationStore();
        Assert.That(tools.Load("Seeder", tool), Is.True);
        Assert.That(tool.Tool.Width, Is.EqualTo(3.0));
        Assert.That(tool.Guidance.UTurnRadius, Is.EqualTo(7.5), "the environment's U-turn goes with each implement");
        Assert.That(tool.Guidance.TramPasses, Is.EqualTo(4));

        foreach (var gone in new[] { "VehicleProfiles/Case.xml", "VehicleProfiles/Case.last", "VehicleProfiles/Valtra.xml", "ToolProfiles/Sprayer.xml", "ToolProfiles/Seeder.xml", "Environment/environment.xml" })
            Assert.That(File.Exists(Path.Combine(_root, gone)), Is.False, gone + " is deleted once converted");

        Assert.That(AgOpenGpsProfileImport.Run(_root, vehicles, tools, (_, _) => { }).Any, Is.False, "nothing the second time");
    }

    [Test]
    public void Run_SwitchesMachineModuleAndTramSide_GoWithTheImplement()
    {
        // 6.8.2+ keeps the work switch in the environment and the steer switch's on/off, the
        // machine module and the tram side with the implement; AgOpenWeb keeps all with the tool.
        var when = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
        Write("ToolProfiles/Sprayer.xml", when, ("setVehicle_toolWidth", "8"),
            ("setF_isSteerWorkSwitchEnabled", "True"), ("setTool_isTramOuterInverted", "True"),
            ("setArdMac_setting0", "2"), ("setArdMac_hydRaiseTime", "5"),
            ("setRelay_pinConfig", "1,2,3,4,5,6,7,8,0,0,0,0,0,0,0,0,0,0,0,0,0,0,17,18"));
        Write("Environment/environment.xml", when,
            ("setF_isWorkSwitchEnabled", "True"), ("setF_isWorkSwitchActiveLow", "True"),
            ("setF_isWorkSwitchManualSections", "True"), ("setF_isSteerWorkSwitchManualSections", "True"));
        var tools = new Tools(Path.Combine(_root, "Tools"));

        AgOpenGpsProfileImport.Run(_root, Path.Combine(_root, "Vehicles"), tools, (_, _) => { });

        var tool = new ConfigurationStore();
        Assert.That(tools.Load("Sprayer", tool), Is.True);
        Assert.That((tool.Tool.IsWorkSwitchEnabled, tool.Tool.IsWorkSwitchActiveLow, tool.Tool.IsWorkSwitchManualSections),
            Is.EqualTo((true, true, true)));
        Assert.That((tool.Tool.IsSteerSwitchEnabled, tool.Tool.IsSteerSwitchManualSections), Is.EqualTo((true, true)));
        Assert.That(tool.Tram.IsOuterInverted, Is.True);
        Assert.That((tool.Machine.HydraulicLiftEnabled, tool.Machine.InvertRelay, tool.Machine.RaiseTime), Is.EqualTo((true, false, 5)));
        Assert.That(tool.Machine.GetPinAssignment(7), Is.EqualTo(PinFunction.Section8), "pin 8, past AgOpenWeb's default of six");
        Assert.That(tool.Machine.GetPinAssignment(22), Is.EqualTo(PinFunction.HydUp));
        Assert.That(tool.Machine.GetPinAssignment(23), Is.EqualTo(PinFunction.HydDown));
    }

    [Test]
    public void Run_DeadZoneNudgeStepsAndRtkAlarm_GoWithTheTractor()
    {
        // The dead zone and the nudge steps are with the implement in 6.8.2+, the RTK alarm in the
        // environment; AgOpenWeb keeps them with the tractor (the implement saved last is in use).
        var older = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
        var newer = older.AddDays(10);
        Write("VehicleProfiles/Case.xml", newer, ("setVehicle_wheelbase", "2.76"));
        Write("ToolProfiles/Sprayer.xml", newer, ("setVehicle_toolWidth", "8"),
            ("setAS_deadZoneHeading", "30"), ("setAS_deadZoneDelay", "2"), ("setAS_snapDistance", "15"), ("setAS_snapDistanceRef", "3"));
        Write("ToolProfiles/Seeder.xml", older, ("setVehicle_toolWidth", "3"), ("setAS_deadZoneHeading", "80"), ("setAS_snapDistance", "40"));
        Write("Environment/environment.xml", older, ("setGPS_isRTK", "True"), ("setGPS_isRTK_KillAutoSteer", "True"), ("setAS_uTurnCompensation", "1.2"));
        string vehicles = Path.Combine(_root, "Vehicles");
        Directory.CreateDirectory(vehicles);
        var autoSteer = new Dictionary<string, AutoSteerConfig>();

        AgOpenGpsProfileImport.Run(_root, vehicles, new Tools(Path.Combine(_root, "Tools")), (n, a) => autoSteer[n] = a);

        Assert.That(autoSteer["Case"].DeadzoneHeading, Is.EqualTo(0.3).Within(1e-9));
        Assert.That(autoSteer["Case"].DeadzoneDelay, Is.EqualTo(2));
        Assert.That((autoSteer["Case"].NudgeDistance, autoSteer["Case"].RefNudgeDistance), Is.EqualTo((15, 3)));
        var store = new ConfigurationStore();
        Assert.That(VehicleProfileJsonService.Load(vehicles, "Case", store), Is.True);
        Assert.That((store.Connections.RtkLostAlarm, store.Connections.RtkLostAction), Is.EqualTo((true, 1)), "the alarm stops AutoSteer");
        Assert.That(store.Guidance.UTurnCompensation, Is.EqualTo(1.2), "from the environment when the implement has none");
    }

    [Test]
    public void Run_UTurnCompensationWithTheImplement_WinsOverTheEnvironment()
    {
        // AgOpenGPS after 6.8.6 keeps the U-turn settings with the implement again.
        var when = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
        Write("VehicleProfiles/Case.xml", when, ("setVehicle_wheelbase", "2.76"));
        Write("ToolProfiles/Sprayer.xml", when, ("setVehicle_toolWidth", "8"), ("setAS_uTurnCompensation", "0.8"));
        Write("Environment/environment.xml", when, ("setAS_uTurnCompensation", "1.2"));
        string vehicles = Path.Combine(_root, "Vehicles");
        Directory.CreateDirectory(vehicles);

        AgOpenGpsProfileImport.Run(_root, vehicles, new Tools(Path.Combine(_root, "Tools")), (_, _) => { });

        var store = new ConfigurationStore();
        Assert.That(VehicleProfileJsonService.Load(vehicles, "Case", store), Is.True);
        Assert.That(store.Guidance.UTurnCompensation, Is.EqualTo(0.8));
    }

    [Test]
    public void Run_FreshInstall_TakesTheEnvironmentFromDefaultEnvironment()
    {
        // A fresh AgOpenGPS 6.8.2+ install has no environment.xml (only converting a combined
        // profile from before 6.8.2 makes one): it loads and saves DefaultEnvironment.xml.
        var when = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
        Write("ToolProfiles/Sprayer.xml", when, ("setVehicle_toolWidth", "8"));
        Write("Environment/DefaultEnvironment.xml", when, ("set_youTurnRadius", "6.5"), ("setTram_passes", "3"));
        var tools = new Tools(Path.Combine(_root, "Tools"));

        var result = AgOpenGpsProfileImport.Run(_root, Path.Combine(_root, "Vehicles"), tools, (_, _) => { });

        Assert.That(result.Tools, Is.EqualTo(new[] { "Sprayer" }).AsCollection);
        var tool = new ConfigurationStore();
        Assert.That(tools.Load("Sprayer", tool), Is.True);
        Assert.That(tool.Guidance.UTurnRadius, Is.EqualTo(6.5), "the U-turn from DefaultEnvironment.xml");
        Assert.That(tool.Guidance.TramPasses, Is.EqualTo(3));
        Assert.That(File.Exists(Path.Combine(_root, "Environment/DefaultEnvironment.xml")), Is.False, "deleted once converted");
    }

    [Test]
    public void Run_EnvironmentXml_WinsOverDefaultEnvironment()
    {
        // With both, AgOpenGPS reads and saves environment.xml; DefaultEnvironment.xml is the
        // install's leftover (as on a computer that converted a pre-6.8.2 profile).
        var when = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
        Write("ToolProfiles/Sprayer.xml", when, ("setVehicle_toolWidth", "8"));
        Write("Environment/environment.xml", when, ("set_youTurnRadius", "7.5"), ("setTram_passes", "4"));
        Write("Environment/DefaultEnvironment.xml", when.AddDays(-30), ("set_youTurnRadius", "25"), ("setTram_passes", "1"));
        var tools = new Tools(Path.Combine(_root, "Tools"));

        AgOpenGpsProfileImport.Run(_root, Path.Combine(_root, "Vehicles"), tools, (_, _) => { });

        var tool = new ConfigurationStore();
        Assert.That(tools.Load("Sprayer", tool), Is.True);
        Assert.That(tool.Guidance.UTurnRadius, Is.EqualTo(7.5), "environment.xml, as AgOpenGPS");
        Assert.That(tool.Guidance.TramPasses, Is.EqualTo(4));
        foreach (var gone in new[] { "Environment/environment.xml", "Environment/DefaultEnvironment.xml" })
            Assert.That(File.Exists(Path.Combine(_root, gone)), Is.False, gone + " is deleted once converted");
    }

    [Test]
    public void Run_LeavesAProfileOfTheSameNameAlone()
    {
        var when = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
        string tools = Path.Combine(_root, "Tools");
        var service = new Tools(tools);
        var mine = new ConfigurationStore();
        mine.Tool.Width = 12;
        service.Save("Sprayer", mine);
        string copied = Write("ToolProfiles/Sprayer.xml", when, ("setVehicle_toolWidth", "8"));

        var result = AgOpenGpsProfileImport.Run(_root, Path.Combine(_root, "Vehicles"), service, (_, _) => { });

        Assert.That(result.Tools, Is.Empty);
        Assert.That(result.Skipped, Is.EqualTo(new[] { "ToolProfiles/Sprayer.xml" }).AsCollection);
        var back = new ConfigurationStore();
        service.Load("Sprayer", back);
        Assert.That(back.Tool.Width, Is.EqualTo(12.0), "AgOpenWeb's own profile is kept");
        Assert.That(File.Exists(copied), Is.True, "a file not converted stays");
    }

    /// <summary>A start with these AgIO settings copied into the data folder.</summary>
    private async Task<NtripProfileService> StartWithAgIo(params (string Name, string Value)[] agIo)
    {
        string? old = Environment.GetEnvironmentVariable(AppDataRoot.EnvVar);
        Environment.SetEnvironmentVariable(AppDataRoot.EnvVar, _root);
        try
        {
            Write("AgOpenWeb/AgIO/Farm.XML", DateTime.UtcNow, agIo);
            var settings = Substitute.For<ISettingsService>();
            settings.Settings.Returns(new AppSettings());
            var svc = new NtripProfileService(settings, NullLogger<NtripProfileService>.Instance);
            await svc.LoadProfilesAsync();
            return svc;
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppDataRoot.EnvVar, old);
        }
    }

    private string AgIoFile => Path.Combine(_root, "AgOpenWeb/AgIO/Farm.XML");

    [Test]
    public async Task AgIoCaster_BecomesTheDefaultNtripProfile_Once()
    {
        var svc = await StartWithAgIo(
            ("setNTRIP_casterURL", "caster.example.net"), ("setNTRIP_casterIP", "192.0.2.10"),
            ("setNTRIP_casterPort", "2102"), ("setNTRIP_mount", "NEAR"),
            ("setNTRIP_userName", "farm"), ("setNTRIP_userPassword", "secret"), ("setNTRIP_isOn", "True"));

        var p = svc.Profiles.Single();
        Assert.That(p.Name, Is.EqualTo("NEAR (AgIO)"));
        Assert.That((p.CasterHost, p.CasterPort, p.MountPoint, p.Username, p.Password),
            Is.EqualTo(("caster.example.net", 2102, "NEAR", "farm", "secret")));
        Assert.That(svc.DefaultProfile?.Id, Is.EqualTo(p.Id));
        Assert.That(p.AutoConnectOnFieldLoad, Is.True);
        Assert.That(File.Exists(AgIoFile), Is.False, "AgIO's copied settings are deleted once read");

        // The same caster copied in again: no second profile.
        var again = await StartWithAgIo(("setNTRIP_casterURL", "caster.example.net"),
            ("setNTRIP_casterPort", "2102"), ("setNTRIP_mount", "NEAR"));
        Assert.That(again.Profiles, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task AgIoCaster_AsAgIoShipsIt_IsNotImported()
    {
        // AgIO writes every field: a farmer who never set NTRIP up has its starting values
        // (AgIO/Source/Properties/Settings.cs).
        var svc = await StartWithAgIo(
            ("setNTRIP_casterURL", "NTRIP.itsware.net"), ("setNTRIP_casterIP", "69.75.31.235"),
            ("setNTRIP_casterPort", "2101"), ("setNTRIP_mount", "SCSC"),
            ("setNTRIP_userName", ""), ("setNTRIP_userPassword", ""), ("setNTRIP_isOn", "False"));

        Assert.That(svc.Profiles, Is.Empty);
        Assert.That(svc.DefaultProfile, Is.Null);
        Assert.That(File.Exists(AgIoFile), Is.False, "read, so deleted");
    }

    [Test]
    public async Task AgIoCaster_SetUpButSwitchedOff_IsImportedAndNotTheDefault()
    {
        var svc = await StartWithAgIo(
            ("setNTRIP_casterURL", "caster.example.net"), ("setNTRIP_casterPort", "2101"), ("setNTRIP_mount", "NEAR"),
            ("setNTRIP_userName", "farm"), ("setNTRIP_userPassword", "secret"), ("setNTRIP_isOn", "False"));

        var p = svc.Profiles.Single();
        Assert.That(p.Name, Is.EqualTo("NEAR (AgIO)"));
        Assert.That(svc.DefaultProfile, Is.Null, "NTRIP was off in AgIO: the farmer chooses");
        Assert.That(p.AutoConnectOnFieldLoad, Is.False);
    }
}
