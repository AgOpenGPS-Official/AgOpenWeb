// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System.IO.Compression;
using System.Text.Json;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.State;
using AgOpenWeb.Services.Interfaces;
using NSubstitute;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// Bug Report Dumps get attached to public GitHub issues. They must carry the GPS /
/// dual-antenna / IMU / AutoSteer settings needed to diagnose heading and steering reports
/// (#157 had none), and must never carry credentials.
/// </summary>
[TestFixture, NonParallelizable]
public class DebugDumpContentTests
{
    private string _dir = null!;

    [SetUp] public void SetUp() => _dir = Path.Combine(Path.GetTempPath(), "dumptest_" + Guid.NewGuid().ToString("N"));
    [TearDown] public void TearDown() { try { Directory.Delete(_dir, true); } catch { } }

    private Dictionary<string, string> MakeDump(AppSettings settings, ConfigurationStore store)
    {
        var svc = Substitute.For<ISettingsService>();
        svc.Settings.Returns(settings);
        var zip = DebugDumpService.CreateDump(svc, new ApplicationState(), store, outputDirectory: _dir);
        using var a = ZipFile.OpenRead(zip);
        return a.Entries.ToDictionary(e => e.FullName, e => new StreamReader(e.Open()).ReadToEnd());
    }

    [Test]
    public void Dump_includes_gps_dual_imu_and_autosteer_settings()
    {
        var store = new ConfigurationStore();
        store.Connections.IsDualGps = true;
        store.Connections.DualHeadingOffset = 90;
        store.Connections.AutoDualFix = true;
        store.Connections.DualSwitchSpeed = 1.2;
        store.Ahrs.IsRollInvert = true;
        store.AutoSteer.CountsPerDegree = 142;

        var files = MakeDump(new AppSettings(), store);
        using var cfg = JsonDocument.Parse(files["configuration.json"]);
        var gps = cfg.RootElement.GetProperty("Gps");

        Assert.Multiple(() =>
        {
            Assert.That(gps.GetProperty("IsDualGps").GetBoolean(), Is.True);
            Assert.That(gps.GetProperty("DualHeadingOffset").GetDouble(), Is.EqualTo(90));
            Assert.That(gps.GetProperty("AutoDualFix").GetBoolean(), Is.True);
            Assert.That(gps.GetProperty("DualSwitchSpeed").GetDouble(), Is.EqualTo(1.2));
            Assert.That(cfg.RootElement.GetProperty("Ahrs").GetProperty("IsRollInvert").GetBoolean(), Is.True);
            Assert.That(cfg.RootElement.GetProperty("AutoSteer").GetProperty("CountsPerDegree").GetDouble(), Is.EqualTo(142));
        });
    }

    [Test]
    public void Dump_never_contains_credentials()
    {
        var store = new ConfigurationStore();
        store.Connections.NtripUsername = "farmer-bob";
        store.Connections.NtripPassword = "hunter2-secret";
        store.Connections.AgShareApiKey = "agshare-key-123";
        var settings = new AppSettings
        {
            NtripUsername = "farmer-bob",
            NtripPassword = "hunter2-secret",
            AgShareApiKey = "agshare-key-123",
        };

        var files = MakeDump(settings, store);

        foreach (var (name, text) in files)
            foreach (var secret in new[] { "farmer-bob", "hunter2-secret", "agshare-key-123" })
                Assert.That(text, Does.Not.Contain(secret), $"{secret} leaked into {name}");
        Assert.That(files["appsettings.json"], Does.Contain("REDACTED"));
    }

    [Test]
    public void Empty_credentials_stay_empty()
    {
        var json = DebugDumpService.RedactSecrets("{\"NtripPassword\":\"\",\"AgShareApiKey\":\"\",\"NtripCasterPort\":2101}");
        Assert.That(json, Does.Not.Contain("REDACTED"), "an unset credential should still read as unset");
        Assert.That(json, Does.Contain("2101"));
    }
}

/// <summary>
/// #175: coverage display bugs could not be reproduced from a dump because it carried only
/// the field folder's files, not the job's coverage tiles under <c>jobs/</c>, and not the
/// section widths that set the pass spacing and painted width.
/// </summary>
[TestFixture, NonParallelizable]
public class DebugDumpJobContentTests
{
    private string _dir = null!;

    [SetUp] public void SetUp() => _dir = Path.Combine(Path.GetTempPath(), "dumptest_" + Guid.NewGuid().ToString("N"));
    [TearDown] public void TearDown() { try { Directory.Delete(_dir, true); } catch { } }

    private static ISettingsService Settings()
    {
        var svc = Substitute.For<ISettingsService>();
        svc.Settings.Returns(new AppSettings());
        return svc;
    }

    [Test]
    public void Dump_includes_the_active_jobs_coverage_tiles_and_nothing_from_other_jobs()
    {
        var fieldDir = Path.Combine(_dir, "Fields", "monte");
        Directory.CreateDirectory(Path.Combine(fieldDir, "jobs", "2026-09-23", "coverage"));
        Directory.CreateDirectory(Path.Combine(fieldDir, "jobs", "other-job", "coverage"));
        File.WriteAllText(Path.Combine(fieldDir, "field.geojson"), "{}");
        File.WriteAllText(Path.Combine(fieldDir, "jobs", "2026-09-23", "job.json"), "{}");
        File.WriteAllBytes(Path.Combine(fieldDir, "jobs", "2026-09-23", "coverage", "tile_0_0.bin"), new byte[] { 1, 2, 3 });
        File.WriteAllBytes(Path.Combine(fieldDir, "jobs", "other-job", "coverage", "tile_0_0.bin"), new byte[] { 9 });

        var state = new ApplicationState();
        state.Field.ActiveField = new Field { Name = "monte", DirectoryPath = fieldDir };

        var zip = DebugDumpService.CreateDump(Settings(), state, new ConfigurationStore(),
            outputDirectory: _dir, activeJobTaskName: "2026-09-23");
        using var a = ZipFile.OpenRead(zip);
        var names = a.Entries.Select(e => e.FullName).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(names, Does.Contain("field/field.geojson"));
            Assert.That(names, Does.Contain("field/jobs/2026-09-23/job.json"));
            Assert.That(names, Does.Contain("field/jobs/2026-09-23/coverage/tile_0_0.bin"));
            Assert.That(names.Where(n => n.Contains("other-job")), Is.Empty, "only the active job");
        });
    }

    [Test]
    public void Dump_without_a_job_has_no_job_entries()
    {
        var fieldDir = Path.Combine(_dir, "Fields", "monte");
        Directory.CreateDirectory(Path.Combine(fieldDir, "jobs", "x", "coverage"));
        File.WriteAllBytes(Path.Combine(fieldDir, "jobs", "x", "coverage", "t.bin"), new byte[] { 1 });
        var state = new ApplicationState();
        state.Field.ActiveField = new Field { Name = "monte", DirectoryPath = fieldDir };

        var zip = DebugDumpService.CreateDump(Settings(), state, new ConfigurationStore(), outputDirectory: _dir);
        using var a = ZipFile.OpenRead(zip);
        Assert.That(a.Entries.Select(e => e.FullName).Where(n => n.StartsWith("field/jobs/")), Is.Empty);
    }

    [Test]
    public void Dump_configuration_carries_section_widths_and_the_actual_tool_width()
    {
        var store = new ConfigurationStore();
        store.Tool.Width = 6;
        store.NumSections = 2;
        store.Tool.SetSectionWidth(0, 600);
        store.Tool.SetSectionWidth(1, 600);

        var zip = DebugDumpService.CreateDump(Settings(), new ApplicationState(), store, outputDirectory: _dir);
        using var a = ZipFile.OpenRead(zip);
        var cfgText = new StreamReader(a.GetEntry("configuration.json")!.Open()).ReadToEnd();
        using var cfg = JsonDocument.Parse(cfgText);
        var widths = cfg.RootElement.GetProperty("SectionWidthsCm").EnumerateArray().Select(x => x.GetDouble()).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(widths, Is.EqualTo(new[] { 600.0, 600.0 }));
            Assert.That(cfg.RootElement.GetProperty("ActualToolWidth").GetDouble(), Is.EqualTo(12));
        });
    }
}
