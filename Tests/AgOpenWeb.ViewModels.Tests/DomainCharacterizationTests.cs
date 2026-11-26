// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Guidance;
using AgOpenWeb.Models.Track;
using AgOpenWeb.Services;
using AgOpenWeb.Services.GeoJson;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// The domains the MainViewModel refactor takes out into services (<c>Plans/MAINVIEWMODEL_REFACTOR_PLAN.md</c>,
/// items 1-5: flags, headland, boundary and import, tracks), each driven through the public members
/// the web wiring calls, step by step, as an operator would. After every step the test records what
/// changed in the view model (<see cref="VmStateReader"/>) and in the field's files on disk, in
/// <c>TestData/Domains/*.txt</c>; after the refactor every step must change the same things.
/// The whole of the web commands is driven nightly by the field scenarios harness; this is the fast
/// check while the refactor is made. Made again on purpose:
/// <c>AGOPENWEB_UPDATE_DOMAINS=1 dotnet test --filter DomainCharacterizationTests</c>.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore and AGOPENWEB_DATA are process-wide
public class DomainCharacterizationTests
{
    private string _root = null!;
    private string _dir = null!;
    private string _name = null!;
    private MainViewModel _vm = null!;
    private Dictionary<string, string> _last = null!;
    private string _lastFiles = "";
    private List<string> _record = null!;
    private string? _oldData;

    [SetUp]
    public void SetUp()
    {
        ConfigurationStore.SetInstance(new ConfigurationStore());
        _root = Path.Combine(Path.GetTempPath(), "aow-domains-" + Guid.NewGuid().ToString("N"));
        // A field name and a place of this test's own: the last field opened and the last position
        // are kept between view models, so a name or place another test used would change the record.
        _name = "Domains " + TestContext.CurrentContext.Test.Name;
        _dir = Path.Combine(_root, _name);
        Directory.CreateDirectory(_dir);
        int k = Array.IndexOf(new[] { "Flags", "Headland", "BoundaryAndImport", "Tracks" }, TestContext.CurrentContext.Test.Name) + 1;
        double lat = 47.20 + 0.01 * k, lon = 8.50 + 0.01 * k;
        // The KML import reads from the data root's Import folder: a temporary one here.
        _oldData = Environment.GetEnvironmentVariable(AppDataRoot.EnvVar);
        Environment.SetEnvironmentVariable(AppDataRoot.EnvVar, Path.Combine(_root, "data"));
        Directory.CreateDirectory(Path.Combine(AppDataRoot.Documents, "Import"));
        File.WriteAllText(Path.Combine(AppDataRoot.Documents, "Import", "two.kml"), TwoPolygonKml);
        File.WriteAllText(Path.Combine(AppDataRoot.Documents, "Import", "bowtie.kml"), BowTieKml);
        var boundary = new Boundary { OuterBoundary = Square(-100, -100, 200) };
        boundary.InnerBoundaries.Add(Square(20, 20, 15));
        boundary.InnerBoundaries.Add(Square(-70, 40, 10));
        GeoJsonFieldService.Save(new Field
        {
            Name = _name, DirectoryPath = _dir, Origin = new Position { Latitude = lat, Longitude = lon }, Boundary = boundary,
        }, new[]
        {
            Track.FromABLine("AB 1", new Vec3(-60, -100, 0), new Vec3(-60, 100, 0)),
            Track.FromCurve("Curve 1", Enumerable.Range(0, 201).Select(i => new Vec3(-20 + 4 * Math.Sin(i / 30.0), -100 + i, 0)).ToList()),
            Track.FromABLine("AB 2", new Vec3(-100, 0, 0), new Vec3(100, 30, 0)),
        });
        var b = new MainViewModelBuilder { FieldService = new FieldService() };
        b.SettingsService.Settings.Returns(new AppSettings { FieldsDirectory = _root });
        _vm = b.Build();
    }

    [TearDown]
    public void TearDown()
    {
        Environment.SetEnvironmentVariable(AppDataRoot.EnvVar, _oldData);
        try { Directory.Delete(_root, true); } catch { }
        ConfigurationStore.SetInstance(new ConfigurationStore());
    }

    [Test]
    public async Task Flags()
    {
        await Begin();
        Step("place a flag", () => _vm.PlaceFlagAtWorldPosition(10, 10, FlagColor.Red));
        Step("place a second", () => _vm.PlaceFlagAtWorldPosition(-30, 50, FlagColor.Green));
        Step("place a third", () => _vm.PlaceFlagAtWorldPosition(0, -40, FlagColor.Blue));
        Step("rename the first", () => _vm.RenameFlagAt(0, "Stone"));
        Step("recolour the second", () => _vm.SetFlagColorAt(1, FlagColor.Red));
        Step("delete the second", () => _vm.DeleteFlagAt(1));
        Step("rename one past the end", () => _vm.RenameFlagAt(5, "Nowhere"));
        await Reopen();
        Step("delete all", () => _vm.DeleteAllFlagsRemote());
        await Reopen();
        Check();
    }

    [Test]
    public async Task Headland()
    {
        await Begin();
        Step("whole boundary at 12 m", () => _vm.RemoteCreateHeadlandWholeBoundary(12));
        Step("its offset to 20 m", () => _vm.RemoteSetHeadlandOffsetAt(0, 20));
        Step("rename it", () => _vm.RemoteRenameHeadlandAt(0, "Outer ring"));
        Step("a whole-boundary piece is not point-edited", () => _vm.RemoteSaveHeadlandEdit(0, -100, 60, 100, 60));
        Step("a line piece along the south edge", () => _vm.RemoteCreateHeadlandFromMapPoints(false, 15, -100, -100, 100, -100));
        Step("a curve piece along the east edge", () => _vm.RemoteCreateHeadlandFromMapPoints(true, 10, 100, -100, 100, 100));
        Step("re-point the line piece", () => _vm.RemoteSaveHeadlandEdit(1, -100, -100, 0, -100));
        Step("re-point the curve piece", () => _vm.RemoteSaveHeadlandEdit(2, 100, -100, 100, 0));
        Step("headland on and off", () => { _vm.ToggleHeadlandCommand!.Execute(null); _vm.ToggleHeadlandCommand.Execute(null); });
        await Reopen();
        Step("whole boundary at 8 m", () => _vm.RemoteCreateHeadlandWholeBoundary(8));
        Step("delete the first", () => _vm.RemoteDeleteHeadlandAt(0));
        Step("delete all", () => _vm.RemoteDeleteAllHeadland());
        await Reopen();
        Check();
    }

    [Test]
    public async Task BoundaryAndImport()
    {
        await Begin();
        Step("list the boundaries", () => _vm.RefreshBoundaryList());
        Step("select the first island", () => { _vm.SelectedBoundaryIndex = 1; });
        Step("make it drive-through", () => _vm.ToggleDriveThroughCommand!.Execute(null));
        Step("select the second island", () => { _vm.SelectedBoundaryIndex = 2; });
        Step("delete it", () => _vm.DeleteBoundaryCommand!.Execute(null));
        await Reopen();
        Step("list again", () => _vm.RefreshBoundaryList());
        Step("import a KML with two polygons", () => _vm.RemoteImportKmlBoundary("two.kml"));
        await Reopen();
        Step("import a KML bow-tie", () => _vm.RemoteImportKmlBoundary("bowtie.kml"));
        Step("import a KML that is not there", () => _vm.RemoteImportKmlBoundary("missing.kml"));
        Check();
    }

    [Test]
    public async Task Tracks()
    {
        await Begin();
        Step("select the curve", () => _vm.SelectTrackAt(1));
        Step("activate it", () => _vm.ActivateTrackAt(1));
        Step("rename the first", () => _vm.RenameTrackAt(0, "North-south"));
        Step("swap A and B of the third", () => _vm.SwapTrackABAt(2));
        Step("nudge right", () => _vm.NudgeRightCommand!.Execute(null));
        Step("activate the first", () => _vm.ActivateTrackAt(0));
        await Reopen();
        Step("delete the third", () => _vm.DeleteTrackAt(2));
        await Reopen();
        Step("delete all", () => _vm.DeleteAllTracksConfirmed());
        await Reopen();
        Check();
    }

    // ── Steps and the record ───────────────────────────────────────────

    private async Task Begin([CallerMemberName] string name = "")
    {
        _record = new List<string>
        {
            $"# {name}: what each step changed in the view model and in the field's files.",
            "# Made by DomainCharacterizationTests (AGOPENWEB_UPDATE_DOMAINS=1); do not edit by hand.",
        };
        _last = VmStateReader.Read(_vm);
        _lastFiles = Files();
        await _vm.OpenFieldAsync(_dir, _name);
        Assume.That(_vm.IsFieldOpen, "the field did not open");
        Note("open the field");
    }

    private void Step(string what, Action act)
    {
        try { act(); }
        catch (Exception ex) { _record.Add($"  threw {ex.GetType().Name}: {ex.Message}"); }
        Note(what);
    }

    private async Task Reopen()
    {
        await _vm.CloseFieldAsync();
        Note("close the field");
        await _vm.OpenFieldAsync(_dir, _name);
        Note("open it again");
    }

    private void Note(string what)
    {
        var now = VmStateReader.Read(_vm);
        string files = Files();
        _record.Add("== " + what);
        foreach (var k in VmStateReader.Differences(_last, now).OrderBy(k => k, StringComparer.Ordinal))
            _record.Add("  " + k + " = " + (now.TryGetValue(k, out var v) ? v.Replace(_root, "<root>").Replace(_name, "<field>") : "(gone)"));
        if (files != _lastFiles) _record.Add("  files: " + files);
        _last = now;
        _lastFiles = files;
    }

    /// <summary>The field's files, each by name and a hash of its text without ids and times.</summary>
    private string Files()
    {
        if (!Directory.Exists(_dir)) return "-";
        var parts = new List<string>();
        foreach (var f in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            // The ISO-XML export is written in the background when the field closes, so it may be
            // missing, half written or whole when read here; its content has tests of its own.
            if (Path.GetRelativePath(_dir, f).StartsWith("zISOXML", StringComparison.Ordinal)) continue;
            string text = File.ReadAllText(f);
            text = Regex.Replace(text, @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", "id");
            text = Regex.Replace(text, @"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})?", "time");
            unchecked
            {
                int h = 17;
                foreach (char c in text) h = h * 31 + c;
                parts.Add(Path.GetRelativePath(_dir, f) + "#" + h.ToString("x8", CultureInfo.InvariantCulture));
            }
        }
        return string.Join(" ", parts);
    }

    private void Check([CallerMemberName] string name = "")
    {
        string file = Path.Combine(TestDataDir(), "Domains", name + ".txt");
        if (Environment.GetEnvironmentVariable("AGOPENWEB_UPDATE_DOMAINS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllLines(file, _record);
            Assert.Pass($"wrote {_record.Count} lines to {file}");
        }
        var expected = File.ReadAllLines(file);
        for (int i = 0; i < Math.Max(expected.Length, _record.Count); i++)
        {
            string was = i < expected.Length ? expected[i] : "(nothing)";
            string now = i < _record.Count ? _record[i] : "(nothing)";
            if (was != now)
                Assert.Fail($"line {i + 1} of {Path.GetFileName(file)}, after \"{LastStep(i)}\":\n  was: {was}\n  now: {now}");
        }

        string LastStep(int line) => _record.Take(line + 1).LastOrDefault(l => l.StartsWith("== ")) ?? "";
    }

    private static string TestDataDir([CallerFilePath] string here = "") => Path.Combine(Path.GetDirectoryName(here)!, "TestData");

    // ── Fields ─────────────────────────────────────────────────────────

    private static BoundaryPolygon Square(double e0, double n0, double side)
    {
        var p = new BoundaryPolygon();
        foreach (var (e, n) in new[] { (e0, n0), (e0 + side, n0), (e0 + side, n0 + side), (e0, n0 + side) })
            p.Points.Add(new BoundaryPoint(e, n, 0));
        p.UpdateBounds();
        return p;
    }

    private const string TwoPolygonKml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <kml xmlns="http://www.opengis.net/kml/2.2"><Document>
        <Placemark><name>Outer</name><Polygon><outerBoundaryIs><LinearRing><coordinates>
        8.5920,47.2900,0 8.5946,47.2900,0 8.5946,47.2922,0 8.5920,47.2922,0 8.5920,47.2900,0
        </coordinates></LinearRing></outerBoundaryIs></Polygon></Placemark>
        <Placemark><name>Pond</name><Polygon><outerBoundaryIs><LinearRing><coordinates>
        8.5930,47.2908,0 8.5934,47.2908,0 8.5934,47.2911,0 8.5930,47.2911,0 8.5930,47.2908,0
        </coordinates></LinearRing></outerBoundaryIs></Polygon></Placemark>
        </Document></kml>
        """;

    private const string BowTieKml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <kml xmlns="http://www.opengis.net/kml/2.2"><Document>
        <Placemark><name>Bow-tie</name><Polygon><outerBoundaryIs><LinearRing><coordinates>
        8.5920,47.2900,0 8.5946,47.2922,0 8.5946,47.2900,0 8.5920,47.2922,0 8.5920,47.2900,0
        </coordinates></LinearRing></outerBoundaryIs></Polygon></Placemark>
        </Document></kml>
        """;
}
