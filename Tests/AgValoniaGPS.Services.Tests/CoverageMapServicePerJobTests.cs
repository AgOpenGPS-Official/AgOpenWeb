using AgValoniaGPS.Services.Coverage;

namespace AgValoniaGPS.Services.Tests;

[TestFixture]
public class CoverageMapServicePerJobTests
{
    private string _fieldDir = null!;

    [SetUp]
    public void SetUp()
    {
        _fieldDir = Path.Combine(Path.GetTempPath(), $"agvalonia_covperjob_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_fieldDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_fieldDir)) Directory.Delete(_fieldDir, recursive: true);
    }

    private static CoverageMapService NewService()
    {
        var svc = new CoverageMapService();
        svc.SetFieldBounds(-100, 100, -100, 100);
        return svc;
    }

    [Test]
    public void SaveToFile_PerJob_WritesUnderJobsTaskFolder()
    {
        var svc = NewService();

        svc.SaveToFile(_fieldDir, "2026-05-05_spraying");

        var jobDir = Path.Combine(_fieldDir, "jobs", "2026-05-05_spraying");
        Assert.That(Directory.Exists(jobDir), Is.True);
        Assert.That(File.Exists(Path.Combine(jobDir, "coverage_detect.bin")), Is.True);
        // Field root must remain free of legacy filenames.
        Assert.That(File.Exists(Path.Combine(_fieldDir, "coverage_detect.bin")), Is.False);
    }

    [Test]
    public void SaveToFile_TwoJobsSameField_DoNotShareCoverage()
    {
        var svc = NewService();

        svc.SaveToFile(_fieldDir, "jobA");
        svc.SaveToFile(_fieldDir, "jobB");

        var aFile = Path.Combine(_fieldDir, "jobs", "jobA", "coverage_detect.bin");
        var bFile = Path.Combine(_fieldDir, "jobs", "jobB", "coverage_detect.bin");

        Assert.That(File.Exists(aFile), Is.True);
        Assert.That(File.Exists(bFile), Is.True);
        Assert.That(Path.GetDirectoryName(aFile), Is.Not.EqualTo(Path.GetDirectoryName(bFile)));
    }

    [Test]
    public void LoadFromFile_PerJob_ReadsFromTaskFolder()
    {
        // Round-trip: save under one task, load it back.
        var saver = NewService();
        saver.SaveToFile(_fieldDir, "2026-05-05_spraying");

        var loader = NewService();
        bool fired = false;
        loader.CoverageUpdated += (_, _) => fired = true;

        loader.LoadFromFile(_fieldDir, "2026-05-05_spraying");

        Assert.That(fired, Is.True, "CoverageUpdated should fire when loading saved bytes");
    }

    [Test]
    public void LoadFromFile_PerJob_DoesNotPickUpFieldRootCoverage()
    {
        // Write legacy-shaped coverage at the field root (the pre-#349 path),
        // then load with a per-job taskName: the field-root file must be
        // ignored — coverage is now keyed by job.
        var legacy = NewService();
        legacy.SaveToFile(_fieldDir);
        Assert.That(File.Exists(Path.Combine(_fieldDir, "coverage_detect.bin")), Is.True);

        var fresh = NewService();
        bool fired = false;
        fresh.CoverageUpdated += (_, _) => fired = true;

        fresh.LoadFromFile(_fieldDir, "freshjob");

        Assert.That(fired, Is.False, "Field-root coverage must not be loaded under a different taskName");
    }

    [Test]
    public void LoadFromFile_PerJob_FallsBackToSectionsTxtInsideJobFolder()
    {
        // After LegacyFieldMigrationService runs, Sections.txt sits inside
        // jobs/imported-*/. The per-job loader's legacy fallback must look
        // there, not the field root.
        var content = "5\n0.0, 1.0, 0.0\n10.0, 10.0, 0.0\n12.0, 10.0, 0.0\n10.0, 12.0, 0.0\n12.0, 12.0, 0.0";

        var jobDir = Path.Combine(_fieldDir, "jobs", "imported-2025-01-01_000000");
        Directory.CreateDirectory(jobDir);
        File.WriteAllText(Path.Combine(jobDir, "Sections.txt"), content);

        var svc = NewService();
        bool fired = false;
        svc.CoverageUpdated += (_, _) => fired = true;

        svc.LoadFromFile(_fieldDir, "imported-2025-01-01_000000");

        Assert.That(fired, Is.True, "Legacy Sections.txt inside the job folder should still load");
    }

    [Test]
    public void SaveToFile_NullOrEmptyArgs_Throw()
    {
        var svc = NewService();
        Assert.Throws<ArgumentException>(() => svc.SaveToFile("", "task"));
        Assert.Throws<ArgumentException>(() => svc.SaveToFile(_fieldDir, ""));
    }
}
