// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using AgOpenWeb.Services.BugReport;

namespace AgOpenWeb.Services.Tests;

/// <summary>Screenshots and files added to a bug report dump go under attachments/, within limits.</summary>
[TestFixture]
public class BugReportAttachmentsTests
{
    private string _zip = null!;

    [SetUp]
    public void SetUp()
    {
        _zip = Path.Combine(Path.GetTempPath(), "bratt_" + Guid.NewGuid().ToString("N") + ".zip");
        using var z = ZipFile.Open(_zip, ZipArchiveMode.Create);
        using var w = new StreamWriter(z.CreateEntry("system_info.txt").Open());
        w.Write("App Version: 1.0.0.0\n");
    }

    [TearDown] public void TearDown() => File.Delete(_zip);

    [Test]
    public void Add_PutsTheFileUnderAttachments_AndKeepsTheDump()
    {
        var (refusal, files) = BugReportAttachments.Add(_zip, "Screenshot 1.png", new byte[] { 1, 2, 3 });
        Assert.That(refusal, Is.EqualTo(BugReportAttachments.Refusal.None));
        Assert.That(files, Is.EqualTo(new[] { new BugReportAttachments.Attachment("Screenshot 1.png", 3) }));
        using var z = ZipFile.OpenRead(_zip);
        Assert.That(z.GetEntry("attachments/Screenshot 1.png"), Is.Not.Null);
        Assert.That(z.GetEntry("system_info.txt"), Is.Not.Null);
    }

    [Test]
    public void SameName_GetsANumber()
    {
        BugReportAttachments.Add(_zip, "photo.jpg", new byte[] { 1 });
        BugReportAttachments.Add(_zip, "photo.jpg", new byte[] { 2 });
        var (_, files) = BugReportAttachments.Add(_zip, "PHOTO.jpg", new byte[] { 3 });
        Assert.That(files.Select(f => f.Name), Is.EqualTo(new[] { "photo.jpg", "photo (2).jpg", "PHOTO (3).jpg" }));
    }

    [Test]
    public void Remove_TakesItOut()
    {
        BugReportAttachments.Add(_zip, "a.txt", new byte[] { 1 });
        BugReportAttachments.Add(_zip, "b.txt", new byte[] { 2 });
        Assert.That(BugReportAttachments.Remove(_zip, "a.txt").Select(f => f.Name), Is.EqualTo(new[] { "b.txt" }));
        Assert.That(BugReportAttachments.Remove(_zip, "nope.txt").Select(f => f.Name), Is.EqualTo(new[] { "b.txt" }));
    }

    [Test]
    public void Limits_EmptyTooLargeTooManyAndTotal()
    {
        Assert.That(BugReportAttachments.Add(_zip, "e.txt", Array.Empty<byte>()).Refusal, Is.EqualTo(BugReportAttachments.Refusal.Empty));
        Assert.That(BugReportAttachments.Add(_zip, "big.mp4", new byte[BugReportAttachments.MaxFileBytes + 1]).Refusal,
            Is.EqualTo(BugReportAttachments.Refusal.TooLarge));
        var nineteen = new byte[19 * 1024 * 1024];
        Assert.That(BugReportAttachments.Add(_zip, "v1.mp4", nineteen).Refusal, Is.EqualTo(BugReportAttachments.Refusal.None));
        Assert.That(BugReportAttachments.Add(_zip, "v2.mp4", nineteen).Refusal, Is.EqualTo(BugReportAttachments.Refusal.TotalTooLarge));
        for (int i = 0; i < BugReportAttachments.MaxFiles - 1; i++) BugReportAttachments.Add(_zip, $"n{i}.txt", new byte[] { 1 });
        Assert.That(BugReportAttachments.Add(_zip, "one-too-many.txt", new byte[] { 1 }).Refusal,
            Is.EqualTo(BugReportAttachments.Refusal.TooMany));
        Assert.That(BugReportAttachments.List(_zip), Has.Count.EqualTo(BugReportAttachments.MaxFiles));
    }

    [TestCase("C:\\Users\\me\\Desktop\\shot.png", "shot.png")]
    [TestCase("../../etc/passwd", "passwd")]
    [TestCase("a<b>c:d|e?f*.txt", "a_b_c_d_e_f_.txt")]
    [TestCase("..hidden", "hidden")]
    [TestCase("   ", "attachment")]
    public void SafeName(string given, string expected) =>
        Assert.That(BugReportAttachments.SafeName(given), Is.EqualTo(expected));

    [Test]
    public void SafeName_LongNames_KeepTheirExtension()
    {
        var s = BugReportAttachments.SafeName(new string('x', 300) + ".png");
        Assert.That(s, Has.Length.EqualTo(100));
        Assert.That(s, Does.EndWith(".png"));
    }
}
