// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models;
using AgOpenWeb.Models.Job;
using AgOpenWeb.Models.State;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// Checklist 7.5 / 7.9 / 13.10: work done in a field is never dropped without a word.
/// Coverage is saved with a job; a field opened without one (Open Field Only) keeps its coverage
/// only if a job is made for it. Close Field asked first; opening another field didn't (it closes
/// the current one on the way), and the app stopping or going to the background didn't save the
/// field at all (iOS wrote the coverage to the field's root folder, which the job loader never reads).
/// </summary>
[TestFixture]
public class LeavingAFieldKeepsCoverageTests
{
    private string _dir = null!;
    private MainViewModelBuilder _b = null!;
    private Job? _activeJob;

    [SetUp]
    public void SetUp()
    {
        _dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aow-leave-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(_dir);
        _b = new MainViewModelBuilder();
        _activeJob = null;
        _b.JobService.ActiveJob.Returns(_ => _activeJob);
        _b.JobService.CreateJob(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns(ci => _activeJob = new Job { FieldName = ci.ArgAt<string>(0), TaskName = "2026-11-02" });
    }

    [TearDown]
    public void TearDown() { try { System.IO.Directory.Delete(_dir, true); } catch { } }

    /// <summary>A field open with no job, coverage painted in it.</summary>
    private MainViewModel FieldOnlyWithCoverage(int patches = 12)
    {
        var vm = _b.Build();
        vm.ActiveField = new Field { Name = "North", DirectoryPath = _dir };
        vm.IsFieldOpen = true;
        _b.CoverageMapService.PatchCount.Returns(patches);
        return vm;
    }

    [Test]
    public async Task OpeningAnotherField_AsksFirst_AndCancelStaysPut()
    {
        var vm = FieldOnlyWithCoverage();

        await vm.OpenFieldAsync("/elsewhere/South", "South");

        Assert.That(vm.State.UI.ActiveDialog, Is.EqualTo(DialogType.UnsavedCoverage), "the question, before anything closes");
        Assert.That(vm.ActiveField?.Name, Is.EqualTo("North"));
        _b.CoverageMapService.DidNotReceive().ClearAll();

        vm.CancelUnsavedCoverageCommand!.Execute(null);
        Assert.That(vm.ActiveField?.Name, Is.EqualTo("North"), "Cancel stays in the field");
        _b.CoverageMapService.DidNotReceive().ClearAll();
    }

    [Test]
    public async Task OpeningAnotherField_SaveToJob_SavesTheCoverageUnderTheNewJob()
    {
        var vm = FieldOnlyWithCoverage();
        await vm.OpenFieldAsync("/elsewhere/South", "South");

        vm.SaveCoverageToJobCommand!.Execute(null);

        _b.JobService.Received(1).CreateJob("North", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());
        // The open goes on from the prompt and closes North first, saving on a background task.
        Assert.That(() => _b.CoverageMapService.ReceivedCalls().Any(c =>
                c.GetMethodInfo().Name == nameof(_b.CoverageMapService.SaveToFile) &&
                c.GetArguments() is [string dir, string task] && dir == _dir && task == "2026-11-02"),
            Is.True.After(3000, 20), "North's coverage saved under the new job");
    }

    [Test]
    public void SaveToJob_ThatCannotMakeTheJob_StaysInTheField()
    {
        var vm = FieldOnlyWithCoverage();
        _b.JobService.CreateJob(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .Throws(new System.IO.IOException("disk full"));
        string? failure = null;
        vm.FailureReported += m => failure = m;
        bool closed = false;
        Assert.That(vm.TryShowUnsavedCoverageGuard(() => closed = true), Is.True);

        vm.SaveCoverageToJobCommand!.Execute(null);

        Assert.That(closed, Is.False, "going on would close with no job and drop the coverage");
        Assert.That(failure, Does.Contain("disk full"));
        Assert.That(vm.ActiveField?.Name, Is.EqualTo("North"));
    }

    [Test]
    public async Task OpeningAField_WithNothingAtRisk_DoesNotAsk()
    {
        var vm = FieldOnlyWithCoverage(patches: 0);
        await vm.OpenFieldAsync("/elsewhere/South", "South");
        Assert.That(vm.State.UI.ActiveDialog, Is.Not.EqualTo(DialogType.UnsavedCoverage));
    }

    [Test]
    public void Exit_WithAJob_SavesItsCoverageAndTheField()
    {
        var vm = FieldOnlyWithCoverage();
        _activeJob = new Job { FieldName = "North", TaskName = "Spraying" };

        Assert.That(vm.SaveOpenFieldForExit(TimeSpan.FromSeconds(5)), Is.True);

        _b.CoverageMapService.Received(1).SaveToFile(_dir, "Spraying");
        _b.JobService.DidNotReceive().CreateJob(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());
        _b.FieldService.Received().SaveField(Arg.Is<Field>(f => f.Name == "North"));
        _b.CoverageMapService.DidNotReceive().SaveToFile(_dir);   // the field's root: never read back
    }

    [Test]
    public void Exit_WorkedWithNoJob_SavesTheCoverageToANewJob()
    {
        var vm = FieldOnlyWithCoverage();

        Assert.That(vm.SaveOpenFieldForExit(TimeSpan.FromSeconds(5)), Is.True);

        _b.JobService.Received(1).CreateJob("North", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());
        _b.CoverageMapService.Received(1).SaveToFile(_dir, "2026-11-02");
    }

    [Test]
    public void Exit_NothingWorked_MakesNoJob()
    {
        var vm = FieldOnlyWithCoverage(patches: 0);

        Assert.That(vm.SaveOpenFieldForExit(TimeSpan.FromSeconds(5)), Is.True);

        _b.JobService.DidNotReceive().CreateJob(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());
        _b.CoverageMapService.DidNotReceive().SaveToFile(Arg.Any<string>(), Arg.Any<string>());
        _b.FieldService.Received().SaveField(Arg.Is<Field>(f => f.Name == "North"));
    }

    [Test]
    public void Exit_NoFieldOpen_DoesNothing()
    {
        var vm = _b.Build();
        Assert.That(vm.SaveOpenFieldForExit(TimeSpan.FromSeconds(5)), Is.True);
        _b.FieldService.DidNotReceive().SaveField(Arg.Any<Field>());
    }
}
