// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models;
using AgOpenWeb.Models.Job;
using AgOpenWeb.Models.State;
using AgOpenWeb.Services;
using AgOpenWeb.Services.Interfaces;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// Checklist 7.7 (Delete Field) and 5.7 (delete a boundary; the area figures update): the two
/// deletes that throw work away. Delete Field removes the folder only after the operator has
/// agreed, also to the jobs inside, and never the open field. Delete Boundary removes the one
/// picked in the list: the list skips a ring too short to be a boundary, and the delete must
/// count the same way, or it takes the wrong one.
/// </summary>
[TestFixture]
public class DeleteFieldAndBoundaryTests
{
    #region Delete Field

    private IFieldService _fields = null!;
    private IJobService _jobs = null!;
    private List<(string message, Action onConfirm)> _confirms = null!;
    private List<Action<bool>> _confirmsWithJobs = null!;

    private StartWorkSessionDialogViewModel Dialog(int jobsInField = 0, string? openField = null)
    {
        _fields = Substitute.For<IFieldService>();
        _jobs = Substitute.For<IJobService>();
        var settings = Substitute.For<ISettingsService>();
        settings.Settings.Returns(new AppSettings { FieldsDirectory = "/tmp/agtest" });
        _jobs.SuggestWorkTypes().Returns(JobWorkTypeSuggestions.Seed);
        var jobs = Enumerable.Range(0, jobsInField).Select(i => new JobSummary(Guid.NewGuid(), "north40", $"job{i}",
            "", "", DateTime.Now, null, DateTime.Now, JobStatus.InProgress)).ToArray();
        _jobs.ListJobs(Arg.Any<string>()).Returns(jobs);
        if (openField != null) _fields.ActiveField.Returns(new Field { Name = openField, DirectoryPath = "/tmp/agtest/" + openField });
        _confirms = new(); _confirmsWithJobs = new();

        var vm = new StartWorkSessionDialogViewModel(_fields, _jobs, settings, new ApplicationState(),
            close: () => { }, openField: (_, _) => { }, openFieldStartingNewJob: (_, _, _, _, _) => { },
            openFieldResumingJob: (_, _, _) => { },
            confirm: (m, a) => _confirms.Add((m, a)),
            confirmWithOption: (_, _, _, _, a) => _confirmsWithJobs.Add(a));
        vm.Fields.Add(new NearbyField("north40", "/tmp/agtest/north40", 0, 0));
        vm.SelectedField = vm.Fields[0];
        foreach (var j in jobs) vm.JobsForSelectedField.Add(j);
        return vm;
    }

    [Test]
    public void DeleteField_OnlyAfterTheOperatorAgrees()
    {
        var vm = Dialog();
        vm.DeleteFieldCommand.Execute(null);
        _fields.DidNotReceive().DeleteField(Arg.Any<string>());
        Assert.That(_confirms, Has.Count.EqualTo(1));

        _confirms[0].onConfirm();
        _fields.Received(1).DeleteField("/tmp/agtest/north40");
    }

    [Test]
    public void DeleteField_WithJobs_NeedsTheJobsTickedToo()
    {
        var vm = Dialog(jobsInField: 2);
        vm.DeleteFieldCommand.Execute(null);
        Assert.That(_confirmsWithJobs, Has.Count.EqualTo(1), "the question names the jobs");

        _confirmsWithJobs[0](false);
        _fields.DidNotReceive().DeleteField(Arg.Any<string>());

        _confirmsWithJobs[0](true);
        _fields.Received(1).DeleteField("/tmp/agtest/north40");
    }

    [Test]
    public void DeleteField_TheOpenField_IsRefused()
    {
        var vm = Dialog(openField: "NORTH40");   // the name, whatever its case
        Assert.That(vm.DeleteFieldCommand.CanExecute(null), Is.False);
    }

    [Test]
    public void FieldService_DeleteField_RemovesTheFolder_AndOnlyIt()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aow-delete-" + Guid.NewGuid().ToString("N"));
        var a = System.IO.Path.Combine(root, "A"); var b = System.IO.Path.Combine(root, "B");
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(a, "jobs", "j1"));
        System.IO.Directory.CreateDirectory(b);
        System.IO.File.WriteAllText(System.IO.Path.Combine(b, "field.geojson"), "{}");
        try
        {
            var service = new FieldService();
            service.DeleteField(a);
            service.DeleteField(System.IO.Path.Combine(root, "gone"));   // not there: nothing happens
            Assert.That(System.IO.Directory.Exists(a), Is.False);
            Assert.That(System.IO.File.Exists(System.IO.Path.Combine(b, "field.geojson")), Is.True);
        }
        finally { try { System.IO.Directory.Delete(root, true); } catch { } }
    }

    #endregion

    #region Delete Boundary

    private static BoundaryPolygon Square(double e, double n, double side)
    {
        var p = new BoundaryPolygon();
        p.Points.Add(new BoundaryPoint(e, n, 0));
        p.Points.Add(new BoundaryPoint(e + side, n, 0));
        p.Points.Add(new BoundaryPoint(e + side, n + side, 0));
        p.Points.Add(new BoundaryPoint(e, n + side, 0));
        p.UpdateBounds();
        return p;
    }

    private (MainViewModel vm, MainViewModelBuilder b, Func<Boundary?> saved) FieldWithIslands()
    {
        var b = new MainViewModelBuilder();
        b.SettingsService.Settings.Returns(new AppSettings { FieldsDirectory = "/tmp/agtest" });
        var boundary = new Boundary { OuterBoundary = Square(0, 0, 100) };
        boundary.InnerBoundaries.Add(Square(10, 10, 10));                  // list row 1
        var stub = new BoundaryPolygon();                                   // a ring too short to be one: not listed
        stub.Points.Add(new BoundaryPoint(50, 50, 0));
        boundary.InnerBoundaries.Add(stub);
        boundary.InnerBoundaries.Add(Square(60, 60, 20));                  // list row 2
        boundary.InnerBoundaries.Add(Square(30, 70, 5));                   // list row 3
        Boundary? saved = null;
        b.FieldService.LoadField(Arg.Any<string>()).Returns(_ => new Field { Name = "F", DirectoryPath = "/tmp/agtest/F", Boundary = boundary });
        b.FieldService.When(f => f.SaveField(Arg.Any<Field>())).Do(ci => saved = ci.Arg<Field>().Boundary);
        var vm = b.Build();
        vm.ActiveField = new Field { Name = "F", DirectoryPath = "/tmp/agtest/F" };
        vm.IsFieldOpen = true;
        vm.State.Field.CurrentBoundary = boundary;
        return (vm, b, () => saved);
    }

    [Test]
    public void DeleteBoundary_TakesTheOnePickedInTheList_PastARingTheListSkips()
    {
        var (vm, _, saved) = FieldWithIslands();
        vm.SelectedBoundaryIndex = 2;                       // the 20 m island, after the unlisted stub

        vm.DeleteBoundaryCommand!.Execute(null);

        var left = saved();
        Assert.That(left, Is.Not.Null, "the field is saved with the boundary gone");
        Assert.That(left!.OuterBoundary, Is.Not.Null);
        var sides = left.InnerBoundaries.Where(i => i.IsValid).Select(i => i.Points.Max(p => p.Easting) - i.Points.Min(p => p.Easting)).ToList();
        Assert.That(sides, Is.EquivalentTo(new[] { 10.0, 5.0 }), "the 20 m island went, the others stayed");
    }

    [Test]
    public void DeleteBoundary_TheAreaFiguresFollow()
    {
        var (vm, _, _) = FieldWithIslands();
        double before = vm.FieldBoundaryAreaSqM;
        Assert.That(before, Is.EqualTo(100 * 100 - 10 * 10 - 20 * 20 - 5 * 5).Within(1e-6));

        vm.SelectedBoundaryIndex = 2;
        vm.DeleteBoundaryCommand!.Execute(null);

        Assert.That(vm.FieldBoundaryAreaSqM, Is.EqualTo(100 * 100 - 10 * 10 - 5 * 5).Within(1e-6), "the island's area is worked area again");
    }

    [Test]
    public void DeleteBoundary_TheOuterOne_WhileIslandsRemain_IsRefused()
    {
        // AgOpenGPS FormBoundary: Delete is enabled for the outer boundary only when it is the only one.
        var (vm, b, _) = FieldWithIslands();
        string? failure = null;
        vm.FailureReported += m => failure = m;
        vm.SelectedBoundaryIndex = 0;

        vm.DeleteBoundaryCommand!.Execute(null);

        b.FieldService.DidNotReceive().SaveField(Arg.Any<Field>());
        Assert.That(failure, Does.Contain("inner boundaries first"));
        Assert.That(vm.State.Field.CurrentBoundary!.OuterBoundary, Is.Not.Null);
    }

    [Test]
    public void DeleteBoundary_TheOuterOne_WhenItIsTheLast_IsDeleted()
    {
        var (vm, _, saved) = FieldWithIslands();
        var boundary = vm.State.Field.CurrentBoundary!;
        boundary.InnerBoundaries.RemoveAll(i => i.IsValid);   // the unlisted stub stays: not a boundary
        vm.SelectedBoundaryIndex = 0;

        vm.DeleteBoundaryCommand!.Execute(null);

        Assert.That(saved(), Is.Not.Null);
        Assert.That(saved()!.OuterBoundary, Is.Null);
    }

    [Test]
    public void DeleteBoundary_NothingPicked_DeletesNothing()
    {
        var (vm, b, _) = FieldWithIslands();
        vm.SelectedBoundaryIndex = -1;
        vm.DeleteBoundaryCommand!.Execute(null);
        b.FieldService.DidNotReceive().SaveField(Arg.Any<Field>());
    }

    #endregion
}
