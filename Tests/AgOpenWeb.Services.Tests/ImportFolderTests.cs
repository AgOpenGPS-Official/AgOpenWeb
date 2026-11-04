// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// <see cref="ImportFolder"/>: the web pickers' file lists are re-sent when its stamp changes,
/// so a KML or ISO-XML dataset copied in while the page is open must change it, at the top of
/// the folder and in a subfolder; and the folder is made for the user.
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

    // An old modified time first, so the change shows on a file system with a coarse clock.
    private static void Age(string dir) => Directory.SetLastWriteTimeUtc(dir, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

    [Test]
    public void Ensure_MakesTheFolderUnderTheDataRoot()
    {
        Assert.That(ImportFolder.Stamp(ImportFolder.Path), Is.Zero, "no folder yet");

        var dir = ImportFolder.Ensure();

        Assert.That(dir, Is.EqualTo(Path.Combine(_root, "AgOpenWeb", "Import")));
        Assert.That(Directory.Exists(dir), Is.True);
    }

    [Test]
    public void AKmlCopiedIn_ChangesTheStamp_AndRemovingItToo()
    {
        var dir = ImportFolder.Ensure();
        Age(dir);
        var empty = ImportFolder.Stamp(dir);
        Assert.That(ImportFolder.Stamp(dir), Is.EqualTo(empty), "nothing changed, same stamp");

        File.WriteAllText(Path.Combine(dir, "north.kml"), "<kml/>");
        var withFile = ImportFolder.Stamp(dir);
        Assert.That(withFile, Is.Not.EqualTo(empty));

        Age(dir);
        var aged = ImportFolder.Stamp(dir);
        File.Delete(Path.Combine(dir, "north.kml"));
        Assert.That(ImportFolder.Stamp(dir), Is.Not.EqualTo(aged));
    }

    [Test]
    public void AFileInASubfolder_ChangesTheStamp()
    {
        // An ISO-XML dataset is a folder; its TASKDATA.XML often lands after the folder.
        var dir = ImportFolder.Ensure();
        var dataset = Directory.CreateDirectory(Path.Combine(dir, "Farm", "TASKDATA")).FullName;
        Age(dir);
        Age(Path.Combine(dir, "Farm"));
        Age(dataset);
        var before = ImportFolder.Stamp(dir);

        File.WriteAllText(Path.Combine(dataset, "TASKDATA.XML"), "<ISO11783_TaskData/>");

        Assert.That(ImportFolder.Stamp(dir), Is.Not.EqualTo(before));
    }
}
