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
