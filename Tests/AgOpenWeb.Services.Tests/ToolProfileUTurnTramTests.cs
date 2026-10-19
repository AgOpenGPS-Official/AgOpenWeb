// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.YouTurn;
using AgOpenWeb.Services.Profile;
using AgOpenWeb.Services.Tram;

namespace AgOpenWeb.Services.Tests;

/// <summary>The U-Turn and Tram tabs of the tool configuration are saved with the tool
/// profile (#330). They were saved with the vehicle (tram width with the field), so
/// swapping a drill for a sprayer on one tractor kept the drill's turn and tram
/// settings.</summary>
[TestFixture]
public class ToolProfileUTurnTramTests
{
    private string _tools = null!;
    private string _vehicles = null!;
    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "aow-tooluturn-" + Guid.NewGuid().ToString("N"));
        _tools = Path.Combine(_root, "Tools");
        _vehicles = Path.Combine(_root, "Vehicles");
        Directory.CreateDirectory(_tools);
        Directory.CreateDirectory(_vehicles);
    }

    [TearDown]
    public void TearDown() { try { Directory.Delete(_root, true); } catch { } }

    private static ConfigurationStore Sprayer()
    {
        var s = new ConfigurationStore();
        s.Guidance.UTurnRadius = 11.5;
        s.Guidance.UTurnExtension = 32.0;
        s.Guidance.UTurnDistanceFromBoundary = 6.5;
        s.Guidance.UTurnStyle = (int)YouTurnType.KStyle;
        s.Guidance.UTurnSmoothing = 22;
        s.Tram.TramWidth = 21.0;
        s.Guidance.TramPasses = 7;
        s.Guidance.TramDisplay = false;
        s.Guidance.TramLine = 4;
        return s;
    }

    private static void AssertSprayer(ConfigurationStore d) => Assert.Multiple(() =>
    {
        Assert.That(d.Guidance.UTurnRadius, Is.EqualTo(11.5));
        Assert.That(d.Guidance.UTurnExtension, Is.EqualTo(32.0));
        Assert.That(d.Guidance.UTurnDistanceFromBoundary, Is.EqualTo(6.5));
        Assert.That(d.Guidance.UTurnStyle, Is.EqualTo((int)YouTurnType.KStyle));
        Assert.That(d.Guidance.UTurnSmoothing, Is.EqualTo(22));
        Assert.That(d.Tram.TramWidth, Is.EqualTo(21.0));
        Assert.That(d.Guidance.TramPasses, Is.EqualTo(7));
        Assert.That(d.Guidance.TramDisplay, Is.False);
        Assert.That(d.Guidance.TramLine, Is.EqualTo(4));
    });

    [Test]
    public void UTurnAndTramSettings_RoundTripThroughTheToolProfile()
    {
        ToolProfileJsonService.Save(_tools, "Sprayer", Sprayer());
        var dst = new ConfigurationStore();
        Assert.That(ToolProfileJsonService.Load(_tools, "Sprayer", dst), Is.True);
        AssertSprayer(dst);
    }

    [Test]
    public void LoadingAVehicle_LeavesTheToolsUTurnAndTramSettingsAlone()
    {
        // Another tractor's profile, saved while a drill with other settings was on it.
        VehicleProfileJsonService.Save(_vehicles, "Other tractor", new ConfigurationStore());

        var live = Sprayer();
        Assert.That(VehicleProfileJsonService.Load(_vehicles, "Other tractor", live), Is.True);
        AssertSprayer(live);
    }

    [Test]
    public void SwitchingTools_OnOneTractor_BringsEachToolsSettings()
    {
        var store = Sprayer();
        ToolProfileJsonService.Save(_tools, "Sprayer", store);
        ToolProfileJsonService.Save(_tools, "Drill", new ConfigurationStore());

        Assert.That(ToolProfileJsonService.Load(_tools, "Drill", store), Is.True);
        var def = new GuidanceConfig();
        Assert.Multiple(() =>
        {
            Assert.That(store.Guidance.UTurnRadius, Is.EqualTo(def.UTurnRadius));
            Assert.That(store.Guidance.UTurnDistanceFromBoundary, Is.EqualTo(def.UTurnDistanceFromBoundary));
            Assert.That(store.Tram.TramWidth, Is.EqualTo(new TramConfig().TramWidth));
            Assert.That(store.Guidance.TramPasses, Is.EqualTo(def.TramPasses));
        });

        Assert.That(ToolProfileJsonService.Load(_tools, "Sprayer", store), Is.True);
        AssertSprayer(store);
    }

    [Test]
    public void AToolFileWithoutTheSections_LoadsTheDefaults_NotThePreviousTool()
    {
        ToolProfileJsonService.Save(_tools, "Plain", new ConfigurationStore());
        var path = Path.Combine(_tools, "Plain.json");
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.That(node.Remove("youTurn"), Is.True, "the U-turn section is written");
        Assert.That(node.Remove("tram"), Is.True, "the tram section is written");
        File.WriteAllText(path, node.ToJsonString());

        var dst = Sprayer();   // as left by the tool that was active before
        Assert.That(ToolProfileJsonService.Load(_tools, "Plain", dst), Is.True);

        var def = new GuidanceConfig();
        Assert.Multiple(() =>
        {
            Assert.That(dst.Guidance.UTurnRadius, Is.EqualTo(def.UTurnRadius));
            Assert.That(dst.Guidance.UTurnExtension, Is.EqualTo(def.UTurnExtension));
            Assert.That(dst.Guidance.UTurnDistanceFromBoundary, Is.EqualTo(def.UTurnDistanceFromBoundary));
            Assert.That(dst.Guidance.UTurnStyle, Is.EqualTo(def.UTurnStyle));
            Assert.That(dst.Guidance.UTurnSmoothing, Is.EqualTo(def.UTurnSmoothing));
            Assert.That(dst.Tram.TramWidth, Is.EqualTo(new TramConfig().TramWidth));
            Assert.That(dst.Guidance.TramPasses, Is.EqualTo(def.TramPasses));
            Assert.That(dst.Guidance.TramDisplay, Is.EqualTo(def.TramDisplay));
            Assert.That(dst.Guidance.TramLine, Is.EqualTo(def.TramLine));
        });
    }

    [Test]
    public void OpeningAField_LeavesTheToolsTramWidthAndFirstPassAlone()
    {
        string field = Path.Combine(_root, "Field");
        Directory.CreateDirectory(field);

        // A field saved while another tool's tram settings were live...
        var other = new TramConfig { TramWidth = 36.0, StartPass = 5, IsOuterInverted = true };
        TramConfigFileService.Save(field, other);

        // ...opened with the sprayer on: its width and first pass stay, the field's own
        // state loads.
        var tram = new TramConfig { TramWidth = 21.0, StartPass = 3 };
        TramConfigFileService.Load(field, tram);

        Assert.Multiple(() =>
        {
            Assert.That(tram.TramWidth, Is.EqualTo(21.0));
            Assert.That(tram.StartPass, Is.EqualTo(3));
            Assert.That(tram.IsOuterInverted, Is.True);
        });
    }
}
