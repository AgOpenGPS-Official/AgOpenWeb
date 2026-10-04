// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.IO;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Profile;

namespace AgOpenWeb.Services.Tests;

/// <summary>The Machine tab of the tool configuration (hydraulic lift, user values, relay
/// pin functions) is saved with the tool profile. Before, nothing saved it: every restart
/// put the defaults back and sent them to the machine module.</summary>
[TestFixture]
public class ToolProfileMachineTests
{
    private string _dir = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "aow-toolmach-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown() { try { Directory.Delete(_dir, true); } catch { } }

    [Test]
    public void MachineSettings_RoundTripThroughTheToolProfile()
    {
        var src = new ConfigurationStore();
        var m = src.Machine;
        m.HydraulicLiftEnabled = true;
        m.RaiseTime = 7;
        m.LowerTime = 5;
        m.LookAhead = 3.5;
        m.InvertRelay = true;
        m.User1Value = 11;
        m.User2Value = 22;
        m.User3Value = 33;
        m.User4Value = 44;
        m.SetPinAssignment(0, PinFunction.HydUp);
        m.SetPinAssignment(1, PinFunction.HydDown);
        m.SetPinAssignment(6, PinFunction.TramLeft);
        m.SetPinAssignment(23, PinFunction.GeoStop);

        ToolProfileJsonService.Save(_dir, "Sprayer", src);
        var dst = new ConfigurationStore();
        Assert.That(ToolProfileJsonService.Load(_dir, "Sprayer", dst), Is.True);

        var d = dst.Machine;
        Assert.Multiple(() =>
        {
            Assert.That(d.HydraulicLiftEnabled, Is.True);
            Assert.That(d.RaiseTime, Is.EqualTo(7));
            Assert.That(d.LowerTime, Is.EqualTo(5));
            Assert.That(d.LookAhead, Is.EqualTo(3.5));
            Assert.That(d.InvertRelay, Is.True);
            Assert.That(d.User1Value, Is.EqualTo(11));
            Assert.That(d.User2Value, Is.EqualTo(22));
            Assert.That(d.User3Value, Is.EqualTo(33));
            Assert.That(d.User4Value, Is.EqualTo(44));
            Assert.That(d.PinAssignments, Is.EqualTo(m.PinAssignments));
        });
    }

    [Test]
    public void AToolFileWithoutTheMachineSection_LoadsTheDefaults_NotThePreviousTool()
    {
        ToolProfileJsonService.Save(_dir, "Plain", new ConfigurationStore());
        var path = Path.Combine(_dir, "Plain.json");
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.That(node.Remove("machine"), Is.True, "the section is written");
        File.WriteAllText(path, node.ToJsonString());

        var dst = new ConfigurationStore();   // as left by the tool that was active before
        dst.Machine.HydraulicLiftEnabled = true;
        dst.Machine.RaiseTime = 9;
        dst.Machine.SetPinAssignment(0, PinFunction.HydUp);

        Assert.That(ToolProfileJsonService.Load(_dir, "Plain", dst), Is.True);

        var def = new MachineConfig();
        Assert.Multiple(() =>
        {
            Assert.That(dst.Machine.HydraulicLiftEnabled, Is.EqualTo(def.HydraulicLiftEnabled));
            Assert.That(dst.Machine.RaiseTime, Is.EqualTo(def.RaiseTime));
            Assert.That(dst.Machine.PinAssignments, Is.EqualTo(def.PinAssignments));
        });
    }

    [Test]
    public void AnUnknownPinFunctionInTheFile_BecomesNone()
    {
        ToolProfileJsonService.Save(_dir, "Odd", new ConfigurationStore());
        var path = Path.Combine(_dir, "Odd.json");
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        node["machine"]!["pinAssignments"]![0] = 99;
        File.WriteAllText(path, node.ToJsonString());

        var dst = new ConfigurationStore();
        Assert.That(ToolProfileJsonService.Load(_dir, "Odd", dst), Is.True);
        Assert.That(dst.Machine.GetPinAssignment(0), Is.EqualTo(PinFunction.None));
        Assert.That(dst.Machine.GetPinAssignment(1), Is.EqualTo(PinFunction.Section2));
    }
}
