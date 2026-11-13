// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Guidance;
using AgOpenWeb.Services.GeoJson;
using TrackModel = AgOpenWeb.Models.Track.Track;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// field.geojson is versioned and extensible, so a field saved by one build is safe in another
/// (Docs/FIELD_FILE_FORMATS.md): a newer build's added properties and roles survive an older
/// build's saves, and a field in a newer format version is read but never written.
/// </summary>
[TestFixture]
public class FieldFormatVersionTests
{
    private string _dir = null!;
    private string FilePath => Path.Combine(_dir, "field.geojson");

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"agopenweb_fmt_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private Field NewField() => new()
    {
        Name = "F",
        DirectoryPath = _dir,
        Origin = new Position { Latitude = 52.0, Longitude = 5.0 },
        Boundary = new Boundary { OuterBoundary = Square(0, 100), InnerBoundaries = { Square(40, 50) } },
    };

    private static BoundaryPolygon Square(double min, double max)
    {
        var p = new BoundaryPolygon();
        foreach (var (e, n) in new[] { (min, min), (max, min), (max, max), (min, max) })
            p.Points.Add(new BoundaryPoint(e, n, 0));
        p.UpdateBounds();
        return p;
    }

    /// <summary>A field with every part this build writes.</summary>
    private void SaveWholeField()
    {
        GeoJsonFieldService.Save(NewField(), tracks: null);
        GeoJsonFieldService.SaveTracks(_dir, new[] { TrackModel.FromABLine("A", new Vec3(10, 10, 0), new Vec3(10, 90, 0)) });
        GeoJsonFieldService.SaveFlags(_dir, new[] { new Flag(20, 30, FlagColor.Blue, 7, "Rock") { Notes = "big one" } });
        GeoJsonFieldService.SaveHeadlandLine(_dir, new HeadlandLine
        {
            Tracks = { new HeadlandPath { Name = "H", MoveDistance = 12, TrackPoints = { new Vec3(5, 5, 0), new Vec3(95, 5, 0) } } },
        });
        GeoJsonFieldService.SaveBackground(_dir, new FieldBackground("background.png", 52.001, 4.999, 51.999, 5.001, null));
    }

    private JsonObject ReadJson() => JsonNode.Parse(File.ReadAllText(FilePath))!.AsObject();

    private void WriteJson(JsonObject o) => File.WriteAllText(FilePath, o.ToJsonString());

    private static IEnumerable<JsonObject> Features(JsonObject o) => o["features"]!.AsArray().Select(f => f!.AsObject());

    private static JsonObject Props(JsonObject feature) => feature["properties"]!.AsObject();

    private static string Role(JsonObject feature) => Props(feature)["role"]!.GetValue<string>();

    /// <summary>Every part's own properties as this build's load and save leave them.</summary>
    private static Dictionary<string, string?> Marks(JsonObject o) =>
        Features(o).Where(f => Props(f)["newThing"] != null)
                   .ToDictionary(f => Role(f) + (Props(f)["name"]?.ToString() ?? ""), f => Props(f)["newThing"]!.ToJsonString());

    /// <summary>The app's own saves of each part, after loading it.</summary>
    private void LoadAndSaveEveryPart()
    {
        var (field, _) = GeoJsonFieldService.Load(_dir);
        GeoJsonFieldService.Save(field, tracks: null);
        GeoJsonFieldService.SaveTracks(_dir, GeoJsonFieldService.LoadTracks(_dir));
        GeoJsonFieldService.SaveFlags(_dir, GeoJsonFieldService.LoadFlags(_dir));
        GeoJsonFieldService.SaveHeadlandLine(_dir, GeoJsonFieldService.LoadHeadlandLine(_dir));
        GeoJsonFieldService.SaveBackground(_dir, GeoJsonFieldService.LoadBackground(_dir));
    }

    [Test]
    public void A_field_is_written_with_its_format_version_and_one_without_reads_as_version_1()
    {
        GeoJsonFieldService.Save(NewField(), tracks: null);
        var meta = Features(ReadJson()).Single(f => Role(f) == "metadata");
        Assert.That(Props(meta)["formatVersion"]!.GetValue<int>(), Is.EqualTo(GeoJsonFieldService.CurrentFormatVersion));

        var o = ReadJson();
        Props(Features(o).Single(f => Role(f) == "metadata")).Remove("formatVersion");
        WriteJson(o);
        Assert.That(GeoJsonFieldService.FormatVersion(_dir), Is.EqualTo(1));
        Assert.That(GeoJsonFieldService.IsNewerFormat(_dir), Is.False);
    }

    [Test]
    public void A_newer_builds_properties_and_roles_survive_this_builds_saves()
    {
        SaveWholeField();
        var o = ReadJson();
        int k = 0;
        foreach (var f in Features(o))
            Props(f)["newThing"] = new JsonObject { ["v"] = ++k, ["s"] = "x" }; // a property on every part
        o["features"]!.AsArray().Add(new JsonObject                           // a role this build doesn't know
        {
            ["type"] = "Feature",
            ["id"] = "zone-1",
            ["geometry"] = new JsonObject { ["type"] = "Point", ["coordinates"] = new JsonArray(5.0, 52.0) },
            ["properties"] = new JsonObject { ["role"] = "rate-zone", ["rate"] = 120 },
        });
        o["bbox"] = new JsonArray(4.9, 51.9, 5.1, 52.1);                       // a member of the collection
        WriteJson(o);
        var before = Marks(ReadJson());
        Assume.That(before, Has.Count.EqualTo(Features(ReadJson()).Count() - 1), "every part but the new role is marked");

        LoadAndSaveEveryPart();

        var after = ReadJson();
        Assert.Multiple(() =>
        {
            Assert.That(Marks(after), Is.EquivalentTo(before), "each part keeps its own unknown property");
            var zone = Features(after).SingleOrDefault(f => Role(f) == "rate-zone");
            Assert.That(zone, Is.Not.Null, "the unknown role is kept");
            Assert.That(zone?["id"]?.GetValue<string>(), Is.EqualTo("zone-1"), "with its feature members");
            Assert.That(Props(zone!)["rate"]!.GetValue<int>(), Is.EqualTo(120));
            Assert.That(after["bbox"]?.AsArray().Count, Is.EqualTo(4), "the collection's members are kept");
        });
    }

    [Test]
    public void An_unknown_property_never_brings_back_one_this_build_removed()
    {
        SaveWholeField();
        var o = ReadJson();
        Props(Features(o).Single(f => Role(f) == "flag"))["newThing"] = 1;
        WriteJson(o);

        var flag = GeoJsonFieldService.LoadFlags(_dir).Single();
        flag.Notes = "";                      // the operator clears the notes: the builder writes none
        GeoJsonFieldService.SaveFlags(_dir, new[] { flag });

        var props = Props(Features(ReadJson()).Single(f => Role(f) == "flag"));
        Assert.Multiple(() =>
        {
            Assert.That(props.ContainsKey("notes"), Is.False, "a property this build reads is never kept as unknown");
            Assert.That(props["newThing"]!.GetValue<int>(), Is.EqualTo(1));
        });
    }

    [Test]
    public void A_field_in_a_newer_format_is_read_but_never_written()
    {
        SaveWholeField();
        var o = ReadJson();
        Props(Features(o).Single(f => Role(f) == "metadata"))["formatVersion"] = GeoJsonFieldService.CurrentFormatVersion + 1;
        WriteJson(o);
        var bytes = File.ReadAllText(FilePath);

        Assert.That(GeoJsonFieldService.IsNewerFormat(_dir), Is.True);
        var (field, tracks) = GeoJsonFieldService.Load(_dir);
        Assert.That(field.Boundary?.OuterBoundary?.Points, Has.Count.EqualTo(4), "it is read");
        Assert.That(tracks, Has.Count.EqualTo(1));

        LoadAndSaveEveryPart();
        GeoJsonFieldService.SaveTracks(_dir, Array.Empty<TrackModel>());
        field.Boundary = new Boundary();
        GeoJsonFieldService.Save(field, tracks: null);

        Assert.That(File.ReadAllText(FilePath), Is.EqualTo(bytes), "no save changed the file");
    }
}
