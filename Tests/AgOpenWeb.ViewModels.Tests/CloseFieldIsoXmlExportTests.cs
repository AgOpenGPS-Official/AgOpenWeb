// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Track;
using AgOpenWeb.Services;
using AgOpenWeb.Services.GeoJson;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// Closing a field exports it as ISO-XML (AgOpenGPS ExportFieldAs_ISOXMLv4, #110) with its
/// boundary and its tracks. The export is a close step that reads the field's tracks, and the
/// field session drops the tracks with the field (Plans/MAINVIEWMODEL_REFACTOR_PLAN.md, item 6):
/// it must run before they go. The characterization of the field's files leaves the export out
/// (it is written in the background), so it is held here.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class CloseFieldIsoXmlExportTests
{
    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        _root = Path.Combine(Path.GetTempPath(), "aow-isoxml-close-" + Guid.NewGuid().ToString("N"));
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, true); } catch { }
        ConfigurationStore.SetInstance(new ConfigurationStore());
    }

    [Test]
    public async Task The_export_on_close_has_the_fields_tracks()
    {
        var dir = Path.Combine(_root, "Export");
        Directory.CreateDirectory(dir);
        var outer = new BoundaryPolygon();
        foreach (var (e, n) in new[] { (-50.0, -50.0), (50.0, -50.0), (50.0, 50.0), (-50.0, 50.0) })
            outer.Points.Add(new BoundaryPoint(e, n, 0));
        outer.UpdateBounds();
        GeoJsonFieldService.Save(new Field
        {
            Name = "Export", DirectoryPath = dir, Origin = new Position { Latitude = 47.61, Longitude = 8.41 },
            Boundary = new Boundary { OuterBoundary = outer },
        }, new[]
        {
            Track.FromABLine("Line West", new Vec3(-20, -40, 0), new Vec3(-20, 40, 0)),
            Track.FromCurve("Curve East", Enumerable.Range(0, 81).Select(i => new Vec3(20 + 2 * Math.Sin(i / 10.0), -40 + i, 0)).ToList()),
        });

        var b = new MainViewModelBuilder { FieldService = new FieldService() };
        b.SettingsService.Settings.Returns(new AppSettings { FieldsDirectory = _root });
        var vm = b.Build();
        await vm.OpenFieldAsync(dir, "Export");
        Assume.That(vm.SavedTracks, Has.Count.EqualTo(2), "the field's tracks were not loaded");

        await vm.CloseFieldAsync();

        // Written in the background: wait for it.
        var file = Path.Combine(dir, "zISOXML", "v4", "TASKDATA.XML");
        for (int i = 0; i < 100 && !File.Exists(file); i++) await Task.Delay(50);
        Assert.That(file, Does.Exist, "no ISO-XML export on close");
        string xml = "";
        for (int i = 0; i < 20; i++)
        {
            try { xml = File.ReadAllText(file); if (xml.Contains("</ISO11783_TaskData>")) break; } catch (IOException) { }
            await Task.Delay(50);
        }
        Assert.Multiple(() =>
        {
            Assert.That(xml, Does.Contain("Line West"), "the AB line is missing from the export");
            Assert.That(xml, Does.Contain("Curve East"), "the curve is missing from the export");
        });
    }
}
