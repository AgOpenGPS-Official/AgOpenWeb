// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models;
using AgOpenWeb.Models.Job;
using AgOpenWeb.Services.Fields;

namespace AgOpenWeb.Services.Tests;

[TestFixture]
public class JobJsonServiceTests
{
    private string _tempDir = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"agopenweb_jobjson_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Test]
    public void JobJson_SaveAndLoad_RoundTrip()
    {
        var job = new Job
        {
            FieldName = "north40",
            TaskName = "2026-05-05_spraying",
            WorkType = "spraying",
            Notes = "First pass, north section.\nWind 5mph SW.",
            StartedAt = new DateTime(2026, 5, 5, 8, 0, 0, DateTimeKind.Local),
            EndedAt = new DateTime(2026, 5, 5, 11, 30, 0, DateTimeKind.Local),
            LastOpenedAt = new DateTime(2026, 5, 5, 11, 30, 0, DateTimeKind.Local),
            Status = JobStatus.Done,
            DistanceTraveledMeters = 12345.6,
            AreaWorkedHectares = 18.4,
            UTurnCount = 7
        };
        var originalId = job.Id;

        JobJsonService.Save(job, _tempDir);

        Assert.That(JobJsonService.Exists(_tempDir, job.TaskName), Is.True);

        var loaded = JobJsonService.Load(_tempDir, job.TaskName);
        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded!.Id, Is.EqualTo(originalId));
        Assert.That(loaded.TaskName, Is.EqualTo(job.TaskName));
        Assert.That(loaded.WorkType, Is.EqualTo("spraying"));
        Assert.That(loaded.Notes, Is.EqualTo(job.Notes));
        Assert.That(loaded.Status, Is.EqualTo(JobStatus.Done));
        Assert.That(loaded.EndedAt, Is.EqualTo(job.EndedAt));
        Assert.That(loaded.DistanceTraveledMeters, Is.EqualTo(12345.6).Within(1e-9));
        Assert.That(loaded.AreaWorkedHectares, Is.EqualTo(18.4).Within(1e-9));
        Assert.That(loaded.UTurnCount, Is.EqualTo(7));
    }

    [Test]
    public void JobJson_StatusSerializesAsString()
    {
        var job = new Job
        {
            FieldName = "north40",
            TaskName = "2026-05-05_seeding",
            WorkType = "seeding",
            Status = JobStatus.InProgress
        };

        JobJsonService.Save(job, _tempDir);

        var jsonText = File.ReadAllText(JobJsonService.JobFilePath(_tempDir, job.TaskName));
        Assert.That(jsonText, Does.Contain("\"InProgress\""));
        Assert.That(jsonText, Does.Not.Contain("\"status\": 0"));
    }

    [Test]
    public void JobJson_FreeTextWorkType_RoundTrips()
    {
        // Decision #1: WorkType is free-text. Custom labels must round-trip.
        var job = new Job
        {
            FieldName = "north40",
            TaskName = "2026-05-05_custom",
            WorkType = "side-dress N at V6",
            Status = JobStatus.InProgress
        };

        JobJsonService.Save(job, _tempDir);
        var loaded = JobJsonService.Load(_tempDir, job.TaskName);

        Assert.That(loaded!.WorkType, Is.EqualTo("side-dress N at V6"));
    }

    [Test]
    public void JobJson_ListTaskNames_FindsAllJobs()
    {
        var taskA = new Job { FieldName = "f", TaskName = "2026-05-01_a", WorkType = "spraying" };
        var taskB = new Job { FieldName = "f", TaskName = "2026-05-02_b", WorkType = "spraying" };
        JobJsonService.Save(taskA, _tempDir);
        JobJsonService.Save(taskB, _tempDir);

        var names = JobJsonService.ListTaskNames(_tempDir);
        Assert.That(names, Is.EquivalentTo(new[] { "2026-05-01_a", "2026-05-02_b" }));
    }

    [Test]
    public void JobJson_ListTaskNames_EmptyWhenNoJobsFolder()
    {
        Assert.That(JobJsonService.ListTaskNames(_tempDir), Is.Empty);
    }

    [Test]
    public void JobJson_LoadAll_SkipsCorruptJobs()
    {
        var good = new Job { FieldName = "f", TaskName = "good", WorkType = "spraying" };
        JobJsonService.Save(good, _tempDir);

        var corruptDir = JobJsonService.JobDirectory(_tempDir, "broken");
        Directory.CreateDirectory(corruptDir);
        File.WriteAllText(Path.Combine(corruptDir, "job.json"), "{ this is not valid json");

        var loaded = JobJsonService.LoadAll(_tempDir);
        Assert.That(loaded, Has.Count.EqualTo(1));
        Assert.That(loaded[0].TaskName, Is.EqualTo("good"));
    }

    [Test]
    public void JobJson_FilePath_LayoutMatchesPlan()
    {
        // Acceptance for file layout from FIELDS_AND_JOBS_PLAN.md:
        //   <FieldsRoot>/<FieldName>/jobs/<TaskName>/job.json
        var path = JobJsonService.JobFilePath(_tempDir, "2026-05-05_spraying");
        Assert.That(path, Is.EqualTo(
            Path.Combine(_tempDir, "jobs", "2026-05-05_spraying", "job.json")));
    }
}
