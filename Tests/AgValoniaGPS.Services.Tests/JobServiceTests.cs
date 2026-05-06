using AgValoniaGPS.Models.Job;
using AgValoniaGPS.Services;
using AgValoniaGPS.Services.Fields;

namespace AgValoniaGPS.Services.Tests;

[TestFixture]
public class JobServiceTests
{
    private string _root = null!;
    private JobService _svc = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), $"agvalonia_jobsvc_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _svc = new JobService(() => _root);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string SeedField(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Test]
    public void CreateJob_PersistsAndActivates()
    {
        SeedField("north40");

        var job = _svc.CreateJob("north40", "spraying", "first pass");

        Assert.That(_svc.ActiveJob, Is.Not.Null);
        Assert.That(_svc.ActiveJob!.TaskName, Is.EqualTo(job.TaskName));
        Assert.That(JobJsonService.Exists(Path.Combine(_root, "north40"), job.TaskName), Is.True);
        Assert.That(job.Status, Is.EqualTo(JobStatus.InProgress));
        Assert.That(job.EndedAt, Is.Null);
    }

    [Test]
    public void CreateJob_DefaultNameUsesDateAndWorkType()
    {
        SeedField("north40");

        var job = _svc.CreateJob("north40", "spraying", notes: "");

        Assert.That(job.TaskName, Does.StartWith($"{DateTime.Now:yyyy-MM-dd}_spraying"));
    }

    [Test]
    public void CreateJob_DefaultName_WhitespaceWorkType_FallsBackToDateOnly()
    {
        SeedField("north40");

        var job = _svc.CreateJob("north40", workType: "  ", notes: "");

        Assert.That(job.TaskName, Is.EqualTo($"{DateTime.Now:yyyy-MM-dd}"));
    }

    [Test]
    public void CreateJob_NormalizesWorkTypeWithSpacesAndPunctuation()
    {
        SeedField("north40");

        var job = _svc.CreateJob("north40", "Side-Dress N at V6", notes: "");

        Assert.That(job.TaskName, Does.EndWith("_side-dress_n_at_v6"));
    }

    [Test]
    public void CreateJob_DuplicateDefaultName_BumpsSuffix()
    {
        SeedField("north40");

        var first = _svc.CreateJob("north40", "spraying", "");
        var second = _svc.CreateJob("north40", "spraying", "");

        Assert.That(second.TaskName, Is.Not.EqualTo(first.TaskName));
        Assert.That(second.TaskName, Does.EndWith("_2"));
    }

    [Test]
    public void CreateJob_FreeTextWorkTypeRoundTrips()
    {
        SeedField("north40");

        var job = _svc.CreateJob("north40", "harvesting (corn)", "");

        var loaded = _svc.GetJob("north40", job.TaskName);
        Assert.That(loaded!.WorkType, Is.EqualTo("harvesting (corn)"));
    }

    [Test]
    public void CreateJob_MissingFieldDirectory_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            _svc.CreateJob("nonexistent", "spraying", ""));
    }

    [Test]
    public void ListJobs_OrdersByLastOpenedAtDesc()
    {
        SeedField("f");

        var older = _svc.CreateJob("f", "spraying", "");
        Thread.Sleep(15);
        var newer = _svc.CreateJob("f", "seeding", "");

        var summaries = _svc.ListJobs("f");
        Assert.That(summaries[0].TaskName, Is.EqualTo(newer.TaskName));
        Assert.That(summaries[1].TaskName, Is.EqualTo(older.TaskName));
    }

    [Test]
    public void ListJobs_UnknownField_ReturnsEmpty()
    {
        Assert.That(_svc.ListJobs("ghost"), Is.Empty);
    }

    [Test]
    public void ListAllJobs_AcrossFields_OrderedByLastOpenedAtDesc()
    {
        SeedField("a");
        SeedField("b");

        _svc.CreateJob("a", "spraying", "");
        Thread.Sleep(15);
        var bJob = _svc.CreateJob("b", "spraying", "");
        Thread.Sleep(15);
        var aJob2 = _svc.CreateJob("a", "seeding", "");

        var all = _svc.ListAllJobs();
        Assert.That(all, Has.Count.EqualTo(3));
        Assert.That(all[0].TaskName, Is.EqualTo(aJob2.TaskName));
        Assert.That(all[1].TaskName, Is.EqualTo(bJob.TaskName));
    }

    [Test]
    public void ResumeJob_ClearsEndedAtAndFlipsStatusBack()
    {
        SeedField("f");
        var job = _svc.CreateJob("f", "spraying", "");
        _svc.CloseCurrentJob();

        // sanity: closed job persisted with EndedAt
        var afterClose = _svc.GetJob("f", job.TaskName)!;
        Assert.That(afterClose.Status, Is.EqualTo(JobStatus.Done));
        Assert.That(afterClose.EndedAt, Is.Not.Null);

        _svc.ResumeJob("f", job.TaskName);

        Assert.That(_svc.ActiveJob, Is.Not.Null);
        Assert.That(_svc.ActiveJob!.TaskName, Is.EqualTo(job.TaskName));
        Assert.That(_svc.ActiveJob.Status, Is.EqualTo(JobStatus.InProgress));
        Assert.That(_svc.ActiveJob.EndedAt, Is.Null);

        var persisted = _svc.GetJob("f", job.TaskName)!;
        Assert.That(persisted.Status, Is.EqualTo(JobStatus.InProgress));
        Assert.That(persisted.EndedAt, Is.Null);
    }

    [Test]
    public void ResumeJob_UnknownTask_Throws()
    {
        SeedField("f");
        Assert.Throws<InvalidOperationException>(() => _svc.ResumeJob("f", "ghost"));
    }

    [Test]
    public void CloseCurrentJob_StampsEndedAtAndPersists()
    {
        SeedField("f");
        var job = _svc.CreateJob("f", "spraying", "");

        _svc.CloseCurrentJob();

        Assert.That(_svc.ActiveJob, Is.Null);
        var persisted = _svc.GetJob("f", job.TaskName)!;
        Assert.That(persisted.Status, Is.EqualTo(JobStatus.Done));
        Assert.That(persisted.EndedAt, Is.Not.Null);
    }

    [Test]
    public void CloseCurrentJob_NoActiveJob_NoOp()
    {
        Assert.DoesNotThrow(() => _svc.CloseCurrentJob());
        Assert.That(_svc.ActiveJob, Is.Null);
    }

    [Test]
    public void SuspendCurrentJob_KeepsStatusInProgress_NextOpenResumesSameJob()
    {
        SeedField("f");
        var first = _svc.CreateJob("f", "spraying", "");

        _svc.SuspendCurrentJob();

        Assert.That(_svc.ActiveJob, Is.Null);
        var persisted = _svc.GetJob("f", first.TaskName)!;
        Assert.That(persisted.Status, Is.EqualTo(JobStatus.InProgress));
        Assert.That(persisted.EndedAt, Is.Null);

        // The silent-path "open same field" should resume rather than create.
        var resumed = _svc.GetOrCreateDefaultJob("f");
        Assert.That(resumed.TaskName, Is.EqualTo(first.TaskName));
        Assert.That(_svc.ActiveJob!.TaskName, Is.EqualTo(first.TaskName));
    }

    [Test]
    public void SuspendCurrentJob_NoActiveJob_NoOp()
    {
        Assert.DoesNotThrow(() => _svc.SuspendCurrentJob());
        Assert.That(_svc.ActiveJob, Is.Null);
    }

    [Test]
    public void DeleteJob_RemovesJobDirectoryAndFiles()
    {
        SeedField("f");
        var job = _svc.CreateJob("f", "spraying", "");
        _svc.SuspendCurrentJob();   // so DeleteJob doesn't refuse

        var jobDir = JobJsonService.JobDirectory(Path.Combine(_root, "f"), job.TaskName);
        Assert.That(Directory.Exists(jobDir), Is.True);

        var deleted = _svc.DeleteJob("f", job.TaskName);

        Assert.That(deleted, Is.True);
        Assert.That(Directory.Exists(jobDir), Is.False);
        Assert.That(_svc.ListJobs("f"), Is.Empty);
    }

    [Test]
    public void DeleteJob_ActiveJob_Throws()
    {
        SeedField("f");
        var job = _svc.CreateJob("f", "spraying", "");
        // Job is still active here.

        Assert.Throws<InvalidOperationException>(() => _svc.DeleteJob("f", job.TaskName));
        // Files must remain intact after refusal.
        Assert.That(JobJsonService.Exists(Path.Combine(_root, "f"), job.TaskName), Is.True);
    }

    [Test]
    public void DeleteJob_UnknownField_ReturnsFalse()
    {
        Assert.That(_svc.DeleteJob("ghost", "task"), Is.False);
    }

    [Test]
    public void DeleteJob_UnknownTask_ReturnsFalse()
    {
        SeedField("f");
        Assert.That(_svc.DeleteJob("f", "no-such-task"), Is.False);
    }

    [Test]
    public void GetOrCreateDefaultJob_ResumesExistingInProgress()
    {
        SeedField("f");
        var first = _svc.CreateJob("f", "spraying", "");

        // Simulate the user closing the dialog without closing the job, then
        // reopening the field. The silent path should pick up the same job.
        _svc.CloseCurrentJob(JobStatus.InProgress);   // mimic: still in progress

        var resumed = _svc.GetOrCreateDefaultJob("f");
        Assert.That(resumed.TaskName, Is.EqualTo(first.TaskName));
    }

    [Test]
    public void GetOrCreateDefaultJob_NoInProgressJob_CreatesNew()
    {
        SeedField("f");
        var first = _svc.CreateJob("f", "spraying", "");
        _svc.CloseCurrentJob();   // marks Done

        var created = _svc.GetOrCreateDefaultJob("f");

        Assert.That(created.TaskName, Is.Not.EqualTo(first.TaskName));
        Assert.That(created.Status, Is.EqualTo(JobStatus.InProgress));
    }

    [Test]
    public void SuggestWorkTypes_SeedFirst_PriorLabelsByRecency_NoDupes()
    {
        SeedField("f");

        // Seed a known label early, then a custom label, then the same custom
        // label again on a different field/timestamp.
        _svc.CreateJob("f", "spraying", "");
        Thread.Sleep(15);
        _svc.CreateJob("f", "side-dress N", "");
        Thread.Sleep(15);
        SeedField("g");
        _svc.CreateJob("g", "side-dress N", "");

        var suggestions = _svc.SuggestWorkTypes();

        // Seed list comes first.
        for (int i = 0; i < JobWorkTypeSuggestions.Seed.Count; i++)
            Assert.That(suggestions[i], Is.EqualTo(JobWorkTypeSuggestions.Seed[i]));

        // Custom label appears once (case-insensitive de-dup).
        var customs = suggestions
            .Where(s => !JobWorkTypeSuggestions.Seed.Contains(s, StringComparer.OrdinalIgnoreCase))
            .ToList();
        Assert.That(customs, Has.Count.EqualTo(1));
        Assert.That(customs[0], Is.EqualTo("side-dress N"));
    }

    [Test]
    public void ActiveJobChanged_FiresOnCreateAndClose()
    {
        SeedField("f");
        var events = new List<Job?>();
        _svc.ActiveJobChanged += (_, j) => events.Add(j);

        _svc.CreateJob("f", "spraying", "");
        _svc.CloseCurrentJob();

        Assert.That(events, Has.Count.EqualTo(2));
        Assert.That(events[0], Is.Not.Null);
        Assert.That(events[1], Is.Null);
    }
}
