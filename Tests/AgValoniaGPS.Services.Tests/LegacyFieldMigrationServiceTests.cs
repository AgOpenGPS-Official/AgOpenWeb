using AgValoniaGPS.Models.Job;
using AgValoniaGPS.Services.Fields;

namespace AgValoniaGPS.Services.Tests;

[TestFixture]
public class LegacyFieldMigrationServiceTests
{
    private string _tempDir = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"agvalonia_legacymig_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Test]
    public void MigrateIfNeeded_NoCoverageFiles_ReturnsFalse()
    {
        var migrated = LegacyFieldMigrationService.MigrateIfNeeded(_tempDir);
        Assert.That(migrated, Is.False);
        Assert.That(Directory.Exists(JobJsonService.JobsRoot(_tempDir)), Is.False);
    }

    [Test]
    public void MigrateIfNeeded_AllThreeLegacyFiles_MovesEverythingIntoImportedJob()
    {
        WriteFakeFile("coverage_detect.bin", "DETECT");
        WriteFakeFile("coverage_disp.bin",   "DISP");
        WriteFakeFile("Sections.txt",        "SECTIONS");

        var migrated = LegacyFieldMigrationService.MigrateIfNeeded(_tempDir);

        Assert.That(migrated, Is.True);
        Assert.That(File.Exists(Path.Combine(_tempDir, "coverage_detect.bin")), Is.False);
        Assert.That(File.Exists(Path.Combine(_tempDir, "coverage_disp.bin")), Is.False);
        Assert.That(File.Exists(Path.Combine(_tempDir, "Sections.txt")), Is.False);

        var taskNames = JobJsonService.ListTaskNames(_tempDir);
        Assert.That(taskNames, Has.Count.EqualTo(1));
        Assert.That(taskNames[0], Does.StartWith("imported-"));

        var jobDir = JobJsonService.JobDirectory(_tempDir, taskNames[0]);
        Assert.That(File.ReadAllText(Path.Combine(jobDir, "coverage_detect.bin")), Is.EqualTo("DETECT"));
        Assert.That(File.ReadAllText(Path.Combine(jobDir, "coverage_disp.bin")),   Is.EqualTo("DISP"));
        Assert.That(File.ReadAllText(Path.Combine(jobDir, "Sections.txt")),        Is.EqualTo("SECTIONS"));
    }

    [Test]
    public void MigrateIfNeeded_OnlySectionsTxt_StillMigrates()
    {
        WriteFakeFile("Sections.txt", "legacy quad strips");

        var migrated = LegacyFieldMigrationService.MigrateIfNeeded(_tempDir);

        Assert.That(migrated, Is.True);
        var taskName = JobJsonService.ListTaskNames(_tempDir)[0];
        Assert.That(File.Exists(Path.Combine(JobJsonService.JobDirectory(_tempDir, taskName), "Sections.txt")), Is.True);
    }

    [Test]
    public void MigrateIfNeeded_WritesJobJsonWithImportedMetadata()
    {
        WriteFakeFile("coverage_detect.bin", "x");

        LegacyFieldMigrationService.MigrateIfNeeded(_tempDir);

        var taskName = JobJsonService.ListTaskNames(_tempDir)[0];
        var job = JobJsonService.Load(_tempDir, taskName);
        Assert.That(job, Is.Not.Null);
        Assert.That(job!.WorkType, Is.EqualTo("imported"));
        Assert.That(job.Notes, Is.EqualTo("Imported from legacy field"));
        Assert.That(job.Status, Is.EqualTo(JobStatus.Done));
        Assert.That(job.EndedAt, Is.Not.Null);
        Assert.That(job.FieldName, Is.EqualTo(Path.GetFileName(_tempDir)));
    }

    [Test]
    public void MigrateIfNeeded_TaskNameUsesMtime()
    {
        var path = WriteFakeFile("coverage_detect.bin", "x");
        var stamp = new DateTime(2025, 11, 30, 14, 22, 33, DateTimeKind.Local);
        File.SetLastWriteTime(path, stamp);

        LegacyFieldMigrationService.MigrateIfNeeded(_tempDir);

        var taskName = JobJsonService.ListTaskNames(_tempDir)[0];
        Assert.That(taskName, Is.EqualTo("imported-2025-11-30_142233"));
    }

    [Test]
    public void MigrateIfNeeded_TaskNameUsesLatestMtimeAcrossFiles()
    {
        var older = WriteFakeFile("coverage_disp.bin", "x");
        var newer = WriteFakeFile("coverage_detect.bin", "x");
        File.SetLastWriteTime(older, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Local));
        File.SetLastWriteTime(newer, new DateTime(2026, 3, 15, 9, 30, 0, DateTimeKind.Local));

        LegacyFieldMigrationService.MigrateIfNeeded(_tempDir);

        var taskName = JobJsonService.ListTaskNames(_tempDir)[0];
        Assert.That(taskName, Is.EqualTo("imported-2026-03-15_093000"));
    }

    [Test]
    public void MigrateIfNeeded_JobsFolderAlreadyExists_SkipsAndReturnsFalse()
    {
        // Idempotence: a field that's already been through migration (or was
        // created post-#349) must not be re-migrated.
        WriteFakeFile("coverage_detect.bin", "fresh-pass-2"); // simulate new coverage
        Directory.CreateDirectory(JobJsonService.JobsRoot(_tempDir));

        var migrated = LegacyFieldMigrationService.MigrateIfNeeded(_tempDir);

        Assert.That(migrated, Is.False);
        // The legacy file is left in place — only the first-open migration touches it.
        Assert.That(File.Exists(Path.Combine(_tempDir, "coverage_detect.bin")), Is.True);
    }

    [Test]
    public void MigrateIfNeeded_RunTwice_IsIdempotent()
    {
        WriteFakeFile("coverage_detect.bin", "x");

        var first = LegacyFieldMigrationService.MigrateIfNeeded(_tempDir);
        var second = LegacyFieldMigrationService.MigrateIfNeeded(_tempDir);

        Assert.That(first, Is.True);
        Assert.That(second, Is.False);
        Assert.That(JobJsonService.ListTaskNames(_tempDir), Has.Count.EqualTo(1));
    }

    [Test]
    public void MigrateIfNeeded_MissingFieldDirectory_ReturnsFalse()
    {
        var ghost = Path.Combine(_tempDir, "does-not-exist");
        Assert.That(LegacyFieldMigrationService.MigrateIfNeeded(ghost), Is.False);
    }

    [Test]
    public void MigrateIfNeeded_NullOrEmptyDirectory_Throws()
    {
        Assert.Throws<ArgumentException>(() => LegacyFieldMigrationService.MigrateIfNeeded(""));
        Assert.Throws<ArgumentException>(() => LegacyFieldMigrationService.MigrateIfNeeded("   "));
    }

    private string WriteFakeFile(string name, string contents)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, contents);
        return path;
    }
}
