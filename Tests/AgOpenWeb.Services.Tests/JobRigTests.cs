// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Job;
using AgOpenWeb.Models.Tool;
using AgOpenWeb.Services.Fields;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// A job keeps the machine it was worked with (#547): the vehicle's hitch points and the chain of
/// tools on them, each measured from the connector it hangs on, in job.json's <c>rig</c>.
/// </summary>
[TestFixture]
public class JobRigTests
{
    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), $"aow_jobrig_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_root, "north40"));
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static ConfigurationStore Store(string type)
    {
        var s = new ConfigurationStore { ActiveVehicleProfileName = "Fendt 312", ActiveToolProfileName = "Drill 3 m" };
        s.Vehicle.HitchLength = 1.9;
        s.Vehicle.ThreePointHitchLength = 1.2;
        s.Vehicle.FrontHitchLength = 2.6;
        s.Vehicle.HitchType = 3;
        s.Tool.SetToolType(type);
        s.Tool.WorkingPointFromHitch = 0.8;
        s.Tool.TrailingHitchLength = 4.0;
        s.Tool.TankTrailingHitchLength = 3.0;
        s.Tool.TrailingToolToPivotLength = 0.5;
        s.Tool.Offset = 0.25;
        s.Tool.Length = 2.0;
        s.Tool.HitchType = 5;
        s.NumSections = 3;
        s.Tool.SetSectionWidth(0, 100);
        s.Tool.SetSectionWidth(1, 150);
        s.Tool.SetSectionWidth(2, 50);
        return s;
    }

    [TestCase("rear", RigConnector.RearThreePoint, ToolMount.RearFixed)]
    [TestCase("front", RigConnector.FrontThreePoint, ToolMount.FrontFixed)]
    [TestCase("trailing", RigConnector.Drawbar, ToolMount.Trailing)]
    [TestCase("tbt", RigConnector.Drawbar, ToolMount.TBT)]
    public void The_copy_is_the_loaded_vehicle_and_tool(string type, RigConnector connector, ToolMount mount)
    {
        var rig = JobRig.From(Store(type));
        var t = rig.Tools.Single();
        bool rigid = mount is ToolMount.FrontFixed or ToolMount.RearFixed;
        Assert.Multiple(() =>
        {
            Assert.That(rig.Vehicle.ProfileName, Is.EqualTo("Fendt 312"));
            Assert.That(rig.Vehicle.Type, Is.EqualTo("Tractor"));
            Assert.That(rig.Vehicle.HitchType, Is.EqualTo(3));
            Assert.That(rig.Vehicle.DrawbarHitch, Is.EqualTo(1.9));
            Assert.That(rig.Vehicle.RearThreePointHitch, Is.EqualTo(1.2));
            Assert.That(rig.Vehicle.FrontThreePointHitch, Is.EqualTo(2.6));
            Assert.That(t.ProfileName, Is.EqualTo("Drill 3 m"));
            Assert.That(t.Connector, Is.EqualTo(connector));
            Assert.That(t.Mount, Is.EqualTo(mount));
            Assert.That(t.HitchType, Is.EqualTo(5));
            // Only the distances the tool type uses: the others are the profile's leftovers.
            Assert.That(t.WorkingPointFromHitch, Is.EqualTo(rigid ? 0.8 : 0));
            Assert.That(t.TrailingHitchLength, Is.EqualTo(rigid ? 0 : 4.0));
            Assert.That(t.TankTrailingHitchLength, Is.EqualTo(mount == ToolMount.TBT ? 3.0 : 0));
            Assert.That(t.TrailingToolToPivotLength, Is.EqualTo(rigid ? 0 : 0.5));
            Assert.That(t.Offset, Is.EqualTo(0.25));
            Assert.That(t.Length, Is.EqualTo(2.0));
            Assert.That(t.SectionWidths, Is.EqualTo(new[] { 1.0, 1.5, 0.5 }));
            Assert.That(t.Width, Is.EqualTo(3.0).Within(1e-9));
            Assert.That(t.RearConnectorFromHitch, Is.Null);
        });
    }

    [Test]
    public void A_chain_of_tools_is_read_back_as_written()
    {
        var dir = Path.Combine(_root, "north40");
        var rig = JobRig.From(Store("rear"));
        rig.Tools[0].RearConnectorFromHitch = 2.4;
        rig.Tools.Add(new RigTool
        {
            ProfileName = "Roller", Connector = RigConnector.PreviousTool, Mount = ToolMount.Trailing,
            TrailingHitchLength = 1.5, Width = 3, SectionWidths = { 3 },
        });
        JobJsonService.Save(new Job { FieldName = "north40", TaskName = "t1", Rig = rig }, dir);

        var back = JobJsonService.Load(dir, "t1")!.Rig;
        Assert.That(back, Is.Not.Null);
        Assert.That(back!.SameAs(rig), Is.True);

        // The names another program reads (Docs/FIELD_FILE_FORMATS.md).
        var o = JsonNode.Parse(File.ReadAllText(JobJsonService.JobFilePath(dir, "t1")))!;
        Assert.Multiple(() =>
        {
            Assert.That((int)o["schemaVersion"]!, Is.EqualTo(1), "a new member does not change the version");
            Assert.That((double)o["rig"]!["vehicle"]!["rearThreePointHitch"]!, Is.EqualTo(1.2));
            Assert.That((string)o["rig"]!["tools"]![0]!["connector"]!, Is.EqualTo("RearThreePoint"));
            Assert.That((string)o["rig"]!["tools"]![0]!["mount"]!, Is.EqualTo("RearFixed"));
            Assert.That((double)o["rig"]!["tools"]![0]!["workingPointFromHitch"]!, Is.EqualTo(0.8));
            Assert.That((string)o["rig"]!["tools"]![1]!["connector"]!, Is.EqualTo("PreviousTool"));
            Assert.That(o["rig"]!["tools"]![1]!["rearConnectorFromHitch"], Is.Null, "left out when the tool has none");
        });
    }

    [Test]
    public void A_job_saved_before_has_no_machine_and_gets_none_written()
    {
        var dir = Path.Combine(_root, "north40");
        JobJsonService.Save(new Job { FieldName = "north40", TaskName = "old" }, dir);
        Assert.That(JobJsonService.Load(dir, "old")!.Rig, Is.Null);
        Assert.That(File.ReadAllText(JobJsonService.JobFilePath(dir, "old")), Does.Not.Contain("\"rig\""));
    }

    [Test]
    public void The_active_job_keeps_the_machine_and_writes_only_a_change()
    {
        var svc = new JobService(() => _root);
        var job = svc.CreateJob("north40", "drilling", "");
        var path = JobJsonService.JobFilePath(Path.Combine(_root, "north40"), job.TaskName);
        var store = Store("rear");

        svc.RecordRig(JobRig.From(store));
        Assert.That(JobJsonService.Load(Path.Combine(_root, "north40"), job.TaskName)!.Rig!.Tools[0].WorkingPointFromHitch, Is.EqualTo(0.8));

        // The same machine again: no write (the file taken away stays away).
        File.Delete(path);
        svc.RecordRig(JobRig.From(store));
        Assert.That(File.Exists(path), Is.False, "the same machine is not written again");

        // The operator moves the tractor's 3-point hitch: the job's copy follows.
        store.Vehicle.ThreePointHitchLength = 1.4;
        svc.RecordRig(JobRig.From(store));
        Assert.That(JobJsonService.Load(Path.Combine(_root, "north40"), job.TaskName)!.Rig!.Vehicle.RearThreePointHitch, Is.EqualTo(1.4));
    }

    [Test]
    public void No_active_job_nothing_is_written()
    {
        var svc = new JobService(() => _root);
        Assert.DoesNotThrow(() => svc.RecordRig(JobRig.From(Store("rear"))));
        Assert.That(Directory.Exists(JobJsonService.JobsRoot(Path.Combine(_root, "north40"))), Is.False);
    }

    [Test]
    public void A_newer_builds_job_is_not_written()
    {
        var dir = Path.Combine(_root, "north40");
        var svc = new JobService(() => _root);
        var job = svc.CreateJob("north40", "drilling", "");
        var path = JobJsonService.JobFilePath(dir, job.TaskName);
        var o = JsonNode.Parse(File.ReadAllText(path))!;
        o["schemaVersion"] = JobJsonService.CurrentSchemaVersion + 1;
        File.WriteAllText(path, o.ToJsonString());
        var before = File.ReadAllText(path);

        svc.RecordRig(JobRig.From(Store("rear")));
        Assert.That(File.ReadAllText(path), Is.EqualTo(before));
    }
}
