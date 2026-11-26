// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text.Json.Nodes;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Job;
using AgOpenWeb.Services.Coverage;
using AgOpenWeb.Services.Fields;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// job.json and the coverage manifest are versioned like field.geojson (Docs/FIELD_FILE_FORMATS.md):
/// what a newer build adds survives this build's saves, and a job in a newer format is listed
/// but never written, its coverage neither read nor replaced.
/// </summary>
[TestFixture, NonParallelizable] // ConfigurationStore is a singleton
public class JobFormatVersionTests
{
    private string _fieldDir = null!;
    private ConfigurationStore _store = null!;
    private const string Task = "2026-11-13_Spraying";

    [SetUp]
    public void SetUp()
    {
        _fieldDir = Path.Combine(Path.GetTempPath(), $"agopenweb_jobfmt_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_fieldDir);
        _store = new ConfigurationStore();
        ConfigurationStore.SetInstance(_store);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_fieldDir, true); } catch { }
    }

    private string JobDir => JobJsonService.JobDirectory(_fieldDir, Task);
    private string JobFile => JobJsonService.JobFilePath(_fieldDir, Task);
    private string ManifestFile => Path.Combine(JobDir, "coverage", "manifest.json");

    private Job NewJob() => new() { FieldName = Path.GetFileName(_fieldDir), TaskName = Task, WorkType = "Spraying" };

    private void EditJson(string path, Action<JsonObject> edit)
    {
        var o = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        edit(o);
        File.WriteAllText(path, o.ToJsonString());
    }

    /// <summary>Every file under the job, with a hash of its content.</summary>
    private Dictionary<string, string> Snapshot() =>
        Directory.GetFiles(JobDir, "*", SearchOption.AllDirectories).ToDictionary(
            f => Path.GetRelativePath(JobDir, f),
            f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));

    private CoverageMapService Coverage()
    {
        var svc = new CoverageMapService(_store);
        svc.SetFieldBounds(-100, 100, -100, 100);
        return svc;
    }

    private static void PaintStrip(CoverageMapService svc, double e0, double e1, double n0, double n1)
    {
        svc.StartMapping(0, new Vec2(e0, n0), new Vec2(e1, n0));
        for (double n = n0 + 1; n <= n1; n += 1)
            svc.AddCoveragePoint(0, new Vec2(e0, n), new Vec2(e1, n));
        svc.StopMapping(0);
    }

    [Test]
    public void A_newer_builds_job_members_survive_this_builds_save()
    {
        JobJsonService.Save(NewJob(), _fieldDir);
        EditJson(JobFile, o => o["implementId"] = "boom-24");

        var job = JobJsonService.Load(_fieldDir, Task)!;
        job.Notes = "windy";
        JobJsonService.Save(job, _fieldDir);

        var saved = JsonNode.Parse(File.ReadAllText(JobFile))!.AsObject();
        Assert.Multiple(() =>
        {
            Assert.That(saved["implementId"]?.GetValue<string>(), Is.EqualTo("boom-24"));
            Assert.That(saved["notes"]?.GetValue<string>(), Is.EqualTo("windy"));
        });
    }

    [Test]
    public void A_newer_job_is_listed_but_never_written()
    {
        JobJsonService.Save(NewJob(), _fieldDir);
        EditJson(JobFile, o => { o["schemaVersion"] = JobJsonService.CurrentSchemaVersion + 1; o["status"] = "Paused"; });
        var bytes = File.ReadAllText(JobFile);

        var listed = JobJsonService.LoadAll(_fieldDir);
        Assert.That(listed.Select(j => j.TaskName), Is.EqualTo(new[] { Task }), "a status this build doesn't know doesn't hide the job");
        Assert.That(JobJsonService.IsNewerFormat(_fieldDir, Task), Is.True);

        var job = listed[0];
        job.Status = JobStatus.Done;
        JobJsonService.Save(job, _fieldDir);
        Assert.That(File.ReadAllText(JobFile), Is.EqualTo(bytes));
    }

    [Test]
    public void Coverage_in_a_newer_format_is_neither_read_nor_replaced()
    {
        JobJsonService.Save(NewJob(), _fieldDir);
        var first = Coverage();
        PaintStrip(first, 0, 10, 0, 20);
        first.SaveToFile(JobDir);
        Assume.That(File.Exists(ManifestFile));
        EditJson(ManifestFile, o => o["SchemaVersion"] = CoverageTileStore.CurrentSchemaVersion + 1);
        File.WriteAllText(Path.Combine(JobDir, "coverage", "d", "newer.tile"), "a tile this build doesn't know");
        var before = Snapshot();

        Assert.That(JobJsonService.IsNewerFormat(_fieldDir, Task), Is.True, "the job counts as newer by its coverage");

        var svc = Coverage();
        svc.LoadFromFile(JobDir);
        Assert.That(svc.TotalWorkedArea, Is.EqualTo(0), "its coverage isn't read");

        PaintStrip(svc, 20, 30, 0, 20);
        svc.SaveToFile(JobDir);

        Assert.That(Snapshot(), Is.EqualTo(before), "nothing was set aside, replaced or deleted");
    }

    [Test]
    public void The_current_versions_are_not_newer()
    {
        JobJsonService.Save(NewJob(), _fieldDir);
        var svc = Coverage();
        PaintStrip(svc, 0, 10, 0, 20);
        svc.SaveToFile(JobDir);

        Assert.That(JobJsonService.IsNewerFormat(_fieldDir, Task), Is.False);
        var reopened = Coverage();
        reopened.LoadFromFile(JobDir);
        Assert.That(reopened.TotalWorkedArea, Is.GreaterThan(0));
    }
}
