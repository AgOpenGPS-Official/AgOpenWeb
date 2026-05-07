using AgValoniaGPS.Models;
using AgValoniaGPS.Models.Job;
using AgValoniaGPS.Services.Interfaces;
using AgValoniaGPS.ViewModels;
using NSubstitute;

namespace AgValoniaGPS.ViewModels.Tests;

[TestFixture]
public class ResumeJobDialogViewModelTests
{
    private IJobService _jobs = null!;
    private ISettingsService _settings = null!;

    private List<(string path, string name, string taskName)> _resumeCalls = null!;
    private int _closeCount;

    [SetUp]
    public void SetUp()
    {
        _jobs = Substitute.For<IJobService>();
        _settings = Substitute.For<ISettingsService>();
        _settings.Settings.Returns(new AppSettings { FieldsDirectory = "/tmp/agtest" });

        _resumeCalls = new();
        _closeCount = 0;
    }

    private ResumeJobDialogViewModel BuildVm() =>
        new ResumeJobDialogViewModel(
            _jobs, _settings,
            close: () => _closeCount++,
            openFieldResumingJob: (p, n, t) => _resumeCalls.Add((p, n, t)));

    private static JobSummary Make(string field, string task, DateTime lastOpened) =>
        new(Guid.NewGuid(), field, task, "spraying", "",
            lastOpened, null, lastOpened, JobStatus.InProgress);

    [Test]
    public void Refresh_PopulatesJobsInServiceOrder()
    {
        // ListAllJobs already orders by LastOpenedAt DESC; the dialog just
        // mirrors that order so the most-recent row sits at the top.
        var newest = Make("a", "task-newest", DateTime.Now);
        var middle = Make("b", "task-middle", DateTime.Now.AddMinutes(-5));
        var oldest = Make("a", "task-oldest", DateTime.Now.AddHours(-1));
        _jobs.ListAllJobs().Returns(new[] { newest, middle, oldest });

        var vm = BuildVm();
        vm.Refresh();

        Assert.That(vm.Jobs, Has.Count.EqualTo(3));
        Assert.That(vm.Jobs[0].TaskName, Is.EqualTo("task-newest"));
        Assert.That(vm.Jobs[1].TaskName, Is.EqualTo("task-middle"));
        Assert.That(vm.Jobs[2].TaskName, Is.EqualTo("task-oldest"));
    }

    [Test]
    public void Refresh_AutoSelectsTopRow()
    {
        var top = Make("a", "top", DateTime.Now);
        var second = Make("b", "second", DateTime.Now.AddMinutes(-1));
        _jobs.ListAllJobs().Returns(new[] { top, second });

        var vm = BuildVm();
        vm.Refresh();

        Assert.That(vm.SelectedJob?.TaskName, Is.EqualTo("top"));
    }

    [Test]
    public void Refresh_NoJobs_SelectionIsNull()
    {
        _jobs.ListAllJobs().Returns(Array.Empty<JobSummary>());

        var vm = BuildVm();
        vm.Refresh();

        Assert.That(vm.Jobs, Is.Empty);
        Assert.That(vm.SelectedJob, Is.Null);
    }

    [Test]
    public void ResumeJob_OpensFieldAtComposedPath_AndCloses()
    {
        // Field path is composed from FieldsDirectory + FieldName so the
        // dialog doesn't need each NearbyField pre-resolved.
        var summary = Make("north40", "2026-05-05_spraying", DateTime.Now);
        _jobs.ListAllJobs().Returns(new[] { summary });

        var vm = BuildVm();
        vm.Refresh();
        vm.ResumeJobCommand.Execute(summary);

        Assert.That(_resumeCalls, Has.Count.EqualTo(1));
        Assert.That(_resumeCalls[0].path, Is.EqualTo(Path.Combine("/tmp/agtest", "north40")));
        Assert.That(_resumeCalls[0].name, Is.EqualTo("north40"));
        Assert.That(_resumeCalls[0].taskName, Is.EqualTo("2026-05-05_spraying"));
        Assert.That(_closeCount, Is.EqualTo(1));
    }

    [Test]
    public void ResumeJob_DoesNotCallJobServiceResume_DefersViaOpenField()
    {
        // Same defer-via-pending-intent contract as StartWorkSessionDialog:
        // mutating ActiveJob before MainViewModel.CloseFieldAsync runs would
        // misroute the previous job's coverage.
        var summary = Make("f", "task", DateTime.Now);

        var vm = BuildVm();
        vm.ResumeJobCommand.Execute(summary);

        _jobs.DidNotReceiveWithAnyArgs().ResumeJob(default!, default!);
        Assert.That(_resumeCalls, Has.Count.EqualTo(1));
    }

    [Test]
    public void ResumeJob_NullSummary_NoOp()
    {
        var vm = BuildVm();
        vm.ResumeJobCommand.Execute(null);

        Assert.That(_resumeCalls, Is.Empty);
        Assert.That(_closeCount, Is.EqualTo(0));
    }
}
