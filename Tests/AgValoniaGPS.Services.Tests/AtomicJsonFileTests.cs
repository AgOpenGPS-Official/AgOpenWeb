using System.Text.Json;
using AgValoniaGPS.Services.Storage;

namespace AgValoniaGPS.Services.Tests;

/// <summary>
/// Crash-safety tests for <see cref="AtomicJsonFile"/>: atomic write,
/// last-known-good backup recovery, and quarantine of damaged files.
/// Simulates the power-loss-during-shutdown scenario behind the
/// "settings reset on restart" reports.
/// </summary>
[TestFixture]
public class AtomicJsonFileTests
{
    private string _dir = null!;
    private string _path = null!;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public sealed class Box
    {
        public string Name { get; set; } = "";
        public int Count { get; set; }
    }

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"AgValoniaGPS_Atomic_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "config.json");
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    // ── Write ────────────────────────────────────────────────────────────

    [Test]
    public void WriteJson_RoundTrips_AndLeavesNoTempFile()
    {
        AtomicJsonFile.WriteJson(_path, new Box { Name = "vehicle", Count = 7 }, Options);

        var result = AtomicJsonFile.Read<Box>(_path, Options);
        Assert.That(result.Outcome, Is.EqualTo(LoadOutcome.Ok));
        Assert.That(result.Value!.Name, Is.EqualTo("vehicle"));
        Assert.That(result.Value!.Count, Is.EqualTo(7));
        Assert.That(File.Exists(_path + AtomicJsonFile.TempSuffix), Is.False,
            "scratch .tmp file should not survive a successful write");
    }

    [Test]
    public void WriteJson_SecondWrite_PromotesPriorContentsToBackup()
    {
        AtomicJsonFile.WriteJson(_path, new Box { Name = "first", Count = 1 }, Options);
        AtomicJsonFile.WriteJson(_path, new Box { Name = "second", Count = 2 }, Options);

        var bak = _path + AtomicJsonFile.BackupSuffix;
        Assert.That(File.Exists(bak), Is.True, "prior good copy should be kept as .bak");

        var backup = JsonSerializer.Deserialize<Box>(File.ReadAllText(bak), Options)!;
        Assert.That(backup.Name, Is.EqualTo("first"),
            ".bak should hold the previous (known-good) contents");
    }

    // ── Read / recovery ────────────────────────────────────────────────────

    [Test]
    public void Read_Missing_WhenNeitherFileExists()
    {
        var result = AtomicJsonFile.Read<Box>(_path, Options);
        Assert.That(result.Outcome, Is.EqualTo(LoadOutcome.Missing));
        Assert.That(result.Loaded, Is.False);
    }

    [Test]
    public void Read_RecoversFromBackup_WhenPrimaryTruncated()
    {
        // Establish a good primary + backup, then simulate a power cut that
        // left the primary half-written.
        AtomicJsonFile.WriteJson(_path, new Box { Name = "good", Count = 1 }, Options);
        AtomicJsonFile.WriteJson(_path, new Box { Name = "newer", Count = 2 }, Options);
        File.WriteAllText(_path, "{ \"name\": \"newe");  // truncated JSON

        var result = AtomicJsonFile.Read<Box>(_path, Options);

        Assert.That(result.Outcome, Is.EqualTo(LoadOutcome.RecoveredFromBackup));
        Assert.That(result.Value!.Name, Is.EqualTo("good"));
        Assert.That(result.BackupTimestamp, Is.Not.Null);
    }

    [Test]
    public void Read_RecoversFromBackup_WhenPrimaryZeroLength()
    {
        AtomicJsonFile.WriteJson(_path, new Box { Name = "good", Count = 1 }, Options);
        AtomicJsonFile.WriteJson(_path, new Box { Name = "newer", Count = 2 }, Options);
        File.WriteAllText(_path, "");  // zero-length, the classic power-loss outcome

        var result = AtomicJsonFile.Read<Box>(_path, Options);

        Assert.That(result.Outcome, Is.EqualTo(LoadOutcome.RecoveredFromBackup));
        Assert.That(result.Value!.Name, Is.EqualTo("good"));
    }

    [Test]
    public void Read_QuarantinesDamagedPrimary_OnRecovery()
    {
        AtomicJsonFile.WriteJson(_path, new Box { Name = "good", Count = 1 }, Options);
        AtomicJsonFile.WriteJson(_path, new Box { Name = "newer", Count = 2 }, Options);
        File.WriteAllText(_path, "garbage{{{");

        AtomicJsonFile.Read<Box>(_path, Options);

        Assert.That(File.Exists(_path), Is.False, "damaged primary should be moved aside");
        var quarantined = Directory.GetFiles(_dir, "config.json.corrupt.*");
        Assert.That(quarantined, Is.Not.Empty, "damaged primary should be quarantined for forensics");
    }

    [Test]
    public void Read_DoesNotQuarantine_WhenQuarantineDisabled()
    {
        // Probe / preview path: a damaged primary must be left in place so a
        // read-only inspection has no side effects.
        AtomicJsonFile.WriteJson(_path, new Box { Name = "good", Count = 1 }, Options);
        AtomicJsonFile.WriteJson(_path, new Box { Name = "newer", Count = 2 }, Options);
        File.WriteAllText(_path, "garbage{{{");

        var result = AtomicJsonFile.Read<Box>(_path, Options, quarantineOnFailure: false);

        Assert.That(result.Outcome, Is.EqualTo(LoadOutcome.RecoveredFromBackup));
        Assert.That(File.Exists(_path), Is.True, "primary must stay put when quarantine is disabled");
        Assert.That(Directory.GetFiles(_dir, "config.json.corrupt.*"), Is.Empty);
    }

    [Test]
    public void Read_CorruptNoBackup_WhenOnlyPrimaryExistsAndIsDamaged()
    {
        File.WriteAllText(_path, "not json at all");

        var result = AtomicJsonFile.Read<Box>(_path, Options);

        Assert.That(result.Outcome, Is.EqualTo(LoadOutcome.CorruptNoBackup));
        Assert.That(result.Value, Is.Null);
        Assert.That(result.Loaded, Is.False);
    }

    [Test]
    public void Read_RejectsPayloadFailingValidatePredicate()
    {
        AtomicJsonFile.WriteJson(_path, new Box { Name = "", Count = -5 }, Options);

        // Validator demands a non-empty name and non-negative count.
        var result = AtomicJsonFile.Read<Box>(_path, Options,
            validate: b => !string.IsNullOrEmpty(b.Name) && b.Count >= 0);

        Assert.That(result.Outcome, Is.EqualTo(LoadOutcome.CorruptNoBackup),
            "a structurally-valid but semantically-rejected payload counts as corrupt");
    }

    [Test]
    public void Read_PrefersValidBackup_WhenPrimaryFailsValidation()
    {
        // Good backup, primary that parses but fails the predicate.
        AtomicJsonFile.WriteJson(_path, new Box { Name = "good", Count = 3 }, Options);
        AtomicJsonFile.WriteJson(_path, new Box { Name = "newer", Count = 4 }, Options);
        File.WriteAllText(_path, JsonSerializer.Serialize(new Box { Name = "", Count = 0 }, Options));

        var result = AtomicJsonFile.Read<Box>(_path, Options,
            validate: b => !string.IsNullOrEmpty(b.Name));

        Assert.That(result.Outcome, Is.EqualTo(LoadOutcome.RecoveredFromBackup));
        Assert.That(result.Value!.Name, Is.EqualTo("good"));
    }
}
