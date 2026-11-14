// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Job;
using NSubstitute;
using NUnit.Framework;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// The active job keeps the machine it is worked with (#547): the view model hands the job service
/// the loaded vehicle and tool when a job becomes active and whenever a profile is loaded or saved.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class JobRigRecordingTests
{
    private MainViewModelBuilder _b = null!;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        var s = ConfigurationStore.Instance;
        s.ActiveVehicleProfileName = "Fendt 312";
        s.Vehicle.ThreePointHitchLength = 1.2;
        s.Tool.SetToolType("rear");
        s.Tool.WorkingPointFromHitch = 0.8;
        _b = new MainViewModelBuilder();
        _b.Build();
    }

    private static bool IsLoaded(JobRig r) =>
        r.Vehicle.ProfileName == "Fendt 312" && r.Vehicle.RearThreePointHitch == 1.2
        && r.Tools.Count == 1 && r.Tools[0].WorkingPointFromHitch == 0.8;

    [Test]
    public void A_job_that_becomes_active_gets_the_loaded_machine()
    {
        var job = new Job { FieldName = "north40", TaskName = "t1" };
        _b.JobService.ActiveJob.Returns(job);
        _b.JobService.ActiveJobChanged += Raise.Event<EventHandler<Job?>>(_b.JobService, job);
        _b.JobService.Received(1).RecordRig(Arg.Is<JobRig>(r => IsLoaded(r)));
    }

    [Test]
    public void A_profile_loaded_or_saved_during_the_job_updates_it()
    {
        _b.JobService.ActiveJob.Returns(new Job { FieldName = "north40", TaskName = "t1" });
        _b.ConfigurationService.ProfileSaved += Raise.Event<EventHandler<string>>(_b.ConfigurationService, "Fendt 312");
        _b.ConfigurationService.ProfileLoaded += Raise.Event<EventHandler<string>>(_b.ConfigurationService, "Fendt 312");
        _b.JobService.Received(2).RecordRig(Arg.Is<JobRig>(r => IsLoaded(r)));
    }

    [Test]
    public void No_active_job_nothing_is_recorded()
    {
        _b.JobService.ActiveJob.Returns((Job?)null);
        _b.ConfigurationService.ProfileSaved += Raise.Event<EventHandler<string>>(_b.ConfigurationService, "Fendt 312");
        _b.JobService.ActiveJobChanged += Raise.Event<EventHandler<Job?>>(_b.JobService, (Job?)null);
        _b.JobService.DidNotReceive().RecordRig(Arg.Any<JobRig>());
    }
}
