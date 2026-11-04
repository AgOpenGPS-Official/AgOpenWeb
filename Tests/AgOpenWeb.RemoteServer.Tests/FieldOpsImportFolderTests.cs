// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.State;
using AgOpenWeb.RemoteServer;
using AgOpenWeb.Services;
using AgOpenWeb.Services.Interfaces;
using NSubstitute;
using NUnit.Framework;

namespace AgOpenWeb.RemoteServer.Tests;

/// <summary>
/// The FieldOps frame's Import folder lists: the broadcaster asks for the fingerprint ten times
/// a second and the frame on every field or job change, so neither may walk the folder. Only a
/// picker's scan (<c>import.scan</c>, <see cref="SceneProjector.ImportScan"/>) lists it.
/// </summary>
[TestFixture]
[NonParallelizable] // AGOPENWEB_DATA is process-wide
public class FieldOpsImportFolderTests
{
    private string _root = null!;
    private string? _oldData;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "aow-fieldops-" + Guid.NewGuid().ToString("N"));
        _oldData = Environment.GetEnvironmentVariable(AppDataRoot.EnvVar);
        Environment.SetEnvironmentVariable(AppDataRoot.EnvVar, _root);
    }

    [TearDown]
    public void TearDown()
    {
        Environment.SetEnvironmentVariable(AppDataRoot.EnvVar, _oldData);
        try { Directory.Delete(_root, true); } catch { }
    }

    private static SceneProjector Projector()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.Settings.Returns(new AppSettings { FieldsDirectory = "" });
        var jobs = Substitute.For<IJobService>();
        jobs.ListAllJobs().Returns(new List<AgOpenWeb.Models.Job.JobSummary>());
        jobs.SuggestWorkTypes().Returns(new List<string>());
        return new SceneProjector(new ApplicationState(), Substitute.For<ISectionControlService>(),
            Substitute.For<IToolPositionService>(), new ConfigurationStore(), Substitute.For<ICoverageMapService>(),
            jobs, Substitute.For<IConfigurationService>(), Substitute.For<IAutoSteerService>(),
            Substitute.For<ISmartWasCalibrationService>(), Substitute.For<IUdpCommunicationService>(),
            Substitute.For<INtripProfileService>(), Substitute.For<IFieldService>(), settings,
            Substitute.For<IVehicleProfileService>(), Substitute.For<IPersistentStateService>());
    }

    [Test]
    public void TheFingerprintAndTheFrame_NeverTouchTheImportFolder()
    {
        var projector = Projector();

        long before = projector.FieldOpsFingerprint();
        var frame = projector.BuildFieldOps();

        Assert.That(Directory.Exists(ImportFolder.Path), Is.False, "not even made: nothing looked at it");
        Assert.That(frame.KmlFiles, Is.Empty);

        Directory.CreateDirectory(ImportFolder.Path);
        File.WriteAllText(Path.Combine(ImportFolder.Path, "north.kml"), "<kml/>");
        Assert.That(projector.FieldOpsFingerprint(), Is.EqualTo(before), "a file copied in changes nothing until a picker scans");
        Assert.That(projector.BuildFieldOps().KmlFiles, Is.Empty);
    }

    [Test]
    public async Task APickerScan_MovesTheFingerprint_AndTheFrameCarriesTheFiles()
    {
        var projector = Projector();
        long before = projector.FieldOpsFingerprint();
        Directory.CreateDirectory(ImportFolder.Path);
        File.WriteAllText(Path.Combine(ImportFolder.Path, "north.kml"), "<kml/>");

        await projector.ImportScan.Request();

        Assert.That(projector.FieldOpsFingerprint(), Is.Not.EqualTo(before), "the next tick sends the frame");
        Assert.That(projector.BuildFieldOps().KmlFiles, Is.EqualTo(new[] { "north.kml" }).AsCollection);
    }
}
