using System;
using System.IO;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// Verifies that VehicleProfileService can import the 6.8.2 split XML
/// profile format (separate VehicleSettings / ToolSettings / Environment
/// files) in addition to the legacy combined format. Both formats use the
/// same `&lt;setting name="..."&gt;` keys; the split is structural only,
/// so the service merges sibling files into one dictionary before applying.
/// </summary>
[TestFixture]
public class VehicleProfileSplitFormatImportTests
{
    private string _tempDir = null!;
    private VehicleProfileService _service = null!;

    [SetUp]
    public void SetUp()
    {
        // VehicleProfileService.VehiclesDirectory is constructed from
        // MyDocuments/AgOpenWeb/Vehicles in production; for tests we
        // redirect MyDocuments via the env var that .NET respects on macOS/Linux,
        // OR construct the service and write into its directory.
        _tempDir = Path.Combine(Path.GetTempPath(), "VehicleProfileSplitTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _service = new TestableVehicleProfileService(_tempDir, NullLogger<VehicleProfileService>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private static string SettingFile(params (string Name, string Value)[] settings)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.AppendLine("<configuration><userSettings><AgOpenGPS.Properties.Settings>");
        foreach (var (name, value) in settings)
        {
            sb.Append($"<setting name=\"{name}\"><value>{value}</value></setting>");
        }
        sb.AppendLine("</AgOpenGPS.Properties.Settings></userSettings></configuration>");
        return sb.ToString();
    }

    [Test]
    public void Load_LegacyCombinedXml_ParsesSingleFile()
    {
        // Drop a single combined file — every key in one place, mirroring
        // the pre-6.8.2 layout. No siblings.
        File.WriteAllText(Path.Combine(_tempDir, "TestProfile.XML"),
            SettingFile(
                ("setVehicle_wheelbase", "3.14"),
                ("setVehicle_toolWidth", "12.0")));

        var store = new ConfigurationStore();
        var ok = _service.Load("TestProfile", store);

        Assert.That(ok, Is.True);
        Assert.That(store.Vehicle.Wheelbase, Is.EqualTo(3.14));
        Assert.That(store.Tool.Width, Is.EqualTo(12.0));
    }

    [Test]
    public void Load_SplitFormat_PrimaryAndToolSibling_MergesBoth()
    {
        // 6.8.2-style split: primary holds vehicle keys, sibling .tool.xml
        // holds tool keys. Both should land in the merged dictionary.
        File.WriteAllText(Path.Combine(_tempDir, "Split.XML"),
            SettingFile(("setVehicle_wheelbase", "2.78")));

        File.WriteAllText(Path.Combine(_tempDir, "Split.tool.xml"),
            SettingFile(("setVehicle_toolWidth", "9.5")));

        var store = new ConfigurationStore();
        var ok = _service.Load("Split", store);

        Assert.That(ok, Is.True);
        Assert.That(store.Vehicle.Wheelbase, Is.EqualTo(2.78));
        Assert.That(store.Tool.Width, Is.EqualTo(9.5),
            "Tool keys from .tool.xml sibling must merge into the import dictionary");
    }

    [Test]
    public void Load_SplitFormat_AllThreeSiblings_AllMerged()
    {
        File.WriteAllText(Path.Combine(_tempDir, "Triple.XML"),
            SettingFile(("setVehicle_wheelbase", "1.5")));

        File.WriteAllText(Path.Combine(_tempDir, "Triple.tool.xml"),
            SettingFile(("setVehicle_toolWidth", "4.2")));

        // env file currently doesn't have any keys ApplyXmlSettingsToStore
        // reads, but should be loaded without throwing — used to verify
        // the merge path itself doesn't reject env-only siblings.
        File.WriteAllText(Path.Combine(_tempDir, "Triple.env.xml"),
            SettingFile(("setMenu_isMetric", "True")));

        var store = new ConfigurationStore();
        var ok = _service.Load("Triple", store);

        Assert.That(ok, Is.True);
        Assert.That(store.Vehicle.Wheelbase, Is.EqualTo(1.5));
        Assert.That(store.Tool.Width, Is.EqualTo(4.2));
        Assert.That(store.IsMetric, Is.True,
            "env-file keys should also reach ApplyXmlSettingsToStore");
    }

    [Test]
    public void Load_SplitFormat_VehicleOnly_PartialImport()
    {
        // User dropped only the vehicle file from a 6.8.2 export. Tool keys
        // are absent → ApplyXmlSettingsToStore uses defaults for those.
        // Documents the partial-import behavior so users can be told that
        // copying the .tool.xml sibling fills in the rest.
        File.WriteAllText(Path.Combine(_tempDir, "VehOnly.XML"),
            SettingFile(("setVehicle_wheelbase", "2.0")));

        var store = new ConfigurationStore();
        var ok = _service.Load("VehOnly", store);

        Assert.That(ok, Is.True);
        Assert.That(store.Vehicle.Wheelbase, Is.EqualTo(2.0));
        // Default tool width when no sibling provides setVehicle_toolWidth
        Assert.That(store.Tool.Width, Is.EqualTo(6.0));
    }

    [Test]
    public void Load_SiblingKeyCollision_SiblingWins()
    {
        // If both files define the same key, the sibling overwrites the
        // primary. This is conservative — split-format files shouldn't
        // share keys, but if a hand-edited file does, the more-specific
        // tool/env file should win.
        File.WriteAllText(Path.Combine(_tempDir, "Coll.XML"),
            SettingFile(("setVehicle_toolWidth", "1.0")));

        File.WriteAllText(Path.Combine(_tempDir, "Coll.tool.xml"),
            SettingFile(("setVehicle_toolWidth", "7.0")));

        var store = new ConfigurationStore();
        _service.Load("Coll", store);

        Assert.That(store.Tool.Width, Is.EqualTo(7.0));
    }

    [Test]
    public void Load_SiblingFilenameCaseInsensitive()
    {
        File.WriteAllText(Path.Combine(_tempDir, "Mixed.XML"),
            SettingFile(("setVehicle_wheelbase", "3.0")));

        // Sibling written with a different case to verify resolver is case-insensitive
        File.WriteAllText(Path.Combine(_tempDir, "Mixed.TOOL.XML"),
            SettingFile(("setVehicle_toolWidth", "5.5")));

        var store = new ConfigurationStore();
        _service.Load("Mixed", store);

        Assert.That(store.Tool.Width, Is.EqualTo(5.5),
            "Sibling resolver must match {profile}.tool.xml regardless of filename case");
    }

    [Test]
    public void Load_NoFile_ReturnsFalse()
    {
        var store = new ConfigurationStore();
        var ok = _service.Load("DoesNotExist", store);

        Assert.That(ok, Is.False);
    }

    /// <summary>
    /// Subclass that injects the test directory via the protected test-seam ctor.
    /// </summary>
    private sealed class TestableVehicleProfileService : VehicleProfileService
    {
        public TestableVehicleProfileService(string vehiclesDirectory, Microsoft.Extensions.Logging.ILogger<VehicleProfileService> logger)
            : base(logger, vehiclesDirectory)
        {
        }
    }
}
