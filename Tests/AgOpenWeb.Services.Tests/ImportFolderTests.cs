// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// <see cref="ImportFolder"/> and <see cref="ImportFolderScanner"/>: the folder is made for the
/// user, a scan lists the KML/KMZ files and ISO-XML datasets anywhere under it, and the
/// scanner's version moves only when the listing changes, so the FieldOps frame is re-sent
/// then and not on every picker scan.
/// </summary>
[TestFixture]
[NonParallelizable] // AGOPENWEB_DATA is process-wide
public class ImportFolderTests
{
    private string _root = null!;
    private string? _oldData;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "aow-import-" + Guid.NewGuid().ToString("N"));
        _oldData = Environment.GetEnvironmentVariable(AppDataRoot.EnvVar);
        Environment.SetEnvironmentVariable(AppDataRoot.EnvVar, _root);
    }

    [TearDown]
    public void TearDown()
    {
        Environment.SetEnvironmentVariable(AppDataRoot.EnvVar, _oldData);
        try { Directory.Delete(_root, true); } catch { }
    }

    [Test]
    public void Ensure_MakesTheFolderUnderTheDataRoot()
    {
        Assert.That(Directory.Exists(ImportFolder.Path), Is.False, "no folder yet");

        var dir = ImportFolder.Ensure();

        Assert.That(dir, Is.EqualTo(Path.Combine(_root, "AgOpenWeb", "Import")));
        Assert.That(Directory.Exists(dir), Is.True);
    }

    [Test]
    public void Scan_ListsKmlAndKmzFiles_InSubfoldersToo_AndIsoXmlDatasets()
    {
        var dir = ImportFolder.Ensure();
        File.WriteAllText(Path.Combine(dir, "north.kml"), "<kml/>");
        Directory.CreateDirectory(Path.Combine(dir, "2026"));
        File.WriteAllText(Path.Combine(dir, "2026", "south.kmz"), "zip");
        var dataset = Directory.CreateDirectory(Path.Combine(dir, "Farm")).FullName;
        File.WriteAllText(Path.Combine(dataset, "TASKDATA.XML"), "<ISO11783_TaskData><PFD A=\"PFD1\" C=\"Ribeira\" D=\"20000\"/></ISO11783_TaskData>");

        var listing = ImportFolder.Scan(dir);

        Assert.That(listing.Kml, Is.EquivalentTo(new[] { "north.kml", "south.kmz" }));
        Assert.That(listing.IsoXml, Is.EqualTo(new[] { "Farm" }).AsCollection);
    }

    [Test]
    public async Task ARequest_MakesTheFolder_AndStoresTheListing()
    {
        var scanner = new ImportFolderScanner();
        Assert.That(scanner.Latest.Kml, Is.Empty, "nothing listed before a picker asks");

        await scanner.Request();

        Assert.That(Directory.Exists(ImportFolder.Path), Is.True, "made for the user");
        Assert.That(scanner.Latest.Kml, Is.Empty);

        File.WriteAllText(Path.Combine(ImportFolder.Path, "north.kml"), "<kml/>");
        Assert.That(scanner.Latest.Kml, Is.Empty, "the folder is not watched: listed only on request");
        await scanner.Request();
        Assert.That(scanner.Latest.Kml, Is.EqualTo(new[] { "north.kml" }).AsCollection);
    }

    [Test]
    public async Task TheVersion_MovesOnlyWhenTheListingChanges()
    {
        var scanner = new ImportFolderScanner();
        ImportFolder.Ensure();
        await scanner.Request();
        long empty = scanner.Version;

        await scanner.Request();
        Assert.That(scanner.Version, Is.EqualTo(empty), "the same listing: the frame is not sent again");

        File.WriteAllText(Path.Combine(ImportFolder.Path, "north.kml"), "<kml/>");
        await scanner.Request();
        long withFile = scanner.Version;
        Assert.That(withFile, Is.Not.EqualTo(empty));

        File.Delete(Path.Combine(ImportFolder.Path, "north.kml"));
        await scanner.Request();
        Assert.That(scanner.Version, Is.Not.EqualTo(withFile), "a file taken out shows too");
    }

    [Test]
    public async Task ARequestWhileAScanRuns_ScansOnceMore()
    {
        // The picker's 3 s rescan can arrive while a slow scan runs: the file copied in between
        // must not wait for the next one.
        var scanner = new ImportFolderScanner();
        ImportFolder.Ensure();
        var first = scanner.Request();
        File.WriteAllText(Path.Combine(ImportFolder.Path, "north.kml"), "<kml/>");
        var second = scanner.Request(); // the running scan's task, which now scans once more
        await Task.WhenAll(first, second);

        Assert.That(scanner.Latest.Kml, Is.EqualTo(new[] { "north.kml" }).AsCollection);
    }
}
