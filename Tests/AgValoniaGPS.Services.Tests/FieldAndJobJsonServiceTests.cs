using AgValoniaGPS.Models;
using AgValoniaGPS.Models.Job;
using AgValoniaGPS.Services.Fields;

namespace AgValoniaGPS.Services.Tests;

[TestFixture]
public class FieldAndJobJsonServiceTests
{
    private string _tempDir = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"agvalonia_fieldjson_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Test]
    public void FieldJson_SaveAndLoad_RoundTrip()
    {
        var field = new Models.Field
        {
            Name = "north40",
            DirectoryPath = _tempDir,
            Origin = new Position { Latitude = 32.5904, Longitude = -87.1804 },
            Convergence = 1.234,
            OffsetX = 10.5,
            OffsetY = -7.25,
            CreatedDate = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Local),
            LastModifiedDate = new DateTime(2026, 4, 15, 12, 0, 0, DateTimeKind.Local),
            LastOpenedDate = new DateTime(2026, 5, 1, 8, 30, 0, DateTimeKind.Local)
        };
        var originalId = field.Id;

        FieldJsonService.Save(field, _tempDir);

        Assert.That(FieldJsonService.Exists(_tempDir), Is.True);

        var loaded = FieldJsonService.Load(_tempDir);
        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded!.Id, Is.EqualTo(originalId));
        Assert.That(loaded.Name, Is.EqualTo("north40"));
        Assert.That(loaded.Origin.Latitude, Is.EqualTo(32.5904).Within(1e-9));
        Assert.That(loaded.Origin.Longitude, Is.EqualTo(-87.1804).Within(1e-9));
        Assert.That(loaded.Convergence, Is.EqualTo(1.234).Within(1e-9));
        Assert.That(loaded.OffsetX, Is.EqualTo(10.5).Within(1e-9));
        Assert.That(loaded.OffsetY, Is.EqualTo(-7.25).Within(1e-9));
        Assert.That(loaded.LastOpenedDate, Is.EqualTo(field.LastOpenedDate));
    }

    [Test]
    public void FieldJson_LoadMissing_ReturnsNull()
    {
        Assert.That(FieldJsonService.Exists(_tempDir), Is.False);
        Assert.That(FieldJsonService.Load(_tempDir), Is.Null);
    }

    [Test]
    public void FieldJson_LoadIgnoresUnknownProperties()
    {
        // forward-compat: a future writer adds new fields; older readers must tolerate them
        var json = """
        {
          "schemaVersion": 999,
          "id": "11111111-1111-1111-1111-111111111111",
          "name": "south20",
          "origin": { "latitude": 1.0, "longitude": 2.0 },
          "convergence": 0,
          "offsetX": 0,
          "offsetY": 0,
          "futureField": "not yet defined"
        }
        """;
        File.WriteAllText(Path.Combine(_tempDir, "field.json"), json);

        var loaded = FieldJsonService.Load(_tempDir);
        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded!.Name, Is.EqualTo("south20"));
        Assert.That(loaded.Id, Is.EqualTo(Guid.Parse("11111111-1111-1111-1111-111111111111")));
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
