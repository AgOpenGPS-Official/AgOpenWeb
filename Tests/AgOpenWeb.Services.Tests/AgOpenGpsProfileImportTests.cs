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
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Profile;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// AgOpenGPS 6.8.2+ keeps a tractor in <c>VehicleProfiles/</c>, an implement in <c>ToolProfiles/</c>,
/// the U-turn and tram settings in <c>Environment/environment.xml</c> and the NTRIP caster in
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

    [Test]
    public async Task AgIoCaster_BecomesTheDefaultNtripProfile_Once()
    {
        string? old = Environment.GetEnvironmentVariable(AppDataRoot.EnvVar);
        Environment.SetEnvironmentVariable(AppDataRoot.EnvVar, _root);
        try
        {
            string agIo = Write("AgOpenWeb/AgIO/Farm.XML", DateTime.UtcNow,
                ("setNTRIP_casterURL", "caster.example.net"), ("setNTRIP_casterIP", "192.0.2.10"),
                ("setNTRIP_casterPort", "2102"), ("setNTRIP_mount", "NEAR"),
                ("setNTRIP_userName", "farm"), ("setNTRIP_userPassword", "secret"), ("setNTRIP_isOn", "True"));

            var settings = Substitute.For<ISettingsService>();
            settings.Settings.Returns(new AppSettings());
            var svc = new NtripProfileService(settings, NullLogger<NtripProfileService>.Instance);
            await svc.LoadProfilesAsync();

            var p = svc.Profiles.Single();
            Assert.That(p.Name, Is.EqualTo("NEAR (AgIO)"));
            Assert.That((p.CasterHost, p.CasterPort, p.MountPoint, p.Username, p.Password),
                Is.EqualTo(("caster.example.net", 2102, "NEAR", "farm", "secret")));
            Assert.That(svc.DefaultProfile?.Id, Is.EqualTo(p.Id));
            Assert.That(File.Exists(agIo), Is.False, "AgIO's copied settings are deleted once read");

            // The same caster copied in again: no second profile.
            Write("AgOpenWeb/AgIO/Farm.XML", DateTime.UtcNow, ("setNTRIP_casterURL", "caster.example.net"),
                ("setNTRIP_casterPort", "2102"), ("setNTRIP_mount", "NEAR"));
            var again = new NtripProfileService(settings, NullLogger<NtripProfileService>.Instance);
            await again.LoadProfilesAsync();
            Assert.That(again.Profiles, Has.Count.EqualTo(1));
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppDataRoot.EnvVar, old);
        }
    }
}
