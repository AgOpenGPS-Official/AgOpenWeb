// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models;

namespace AgOpenWeb.Models.Tests;

public class WebCardBookmarksTests
{
    private static readonly List<WebCardBookmark> Two = new()
    {
        new() { Name = "HAT", Url = "http://{host}/" },
        new() { Name = "Rate", Url = "http://192.168.5.30:8080/" },
    };

    [TestCase("http://{host}/", true)]
    [TestCase("http://{host}:8080/setup", true)]
    [TestCase("https://192.168.1.10/", true)]
    [TestCase("ftp://192.168.1.10/", false)]
    [TestCase("javascript:alert(1)", false)]
    [TestCase("192.168.1.10", false)]
    [TestCase("", false)]
    public void Urls(string url, bool ok) => Assert.That(WebCardBookmarks.IsValidUrl(url), Is.EqualTo(ok));

    [Test]
    public void Save_AddsAtTheEnd_AndLeavesTheOldListAlone()
    {
        var r = WebCardBookmarks.Save(Two, -1, " Camera ", " http://{host}:81/ ");
        Assert.That(r!.Select(b => b.Name), Is.EqualTo(new[] { "HAT", "Rate", "Camera" }));
        Assert.That(r[2].Url, Is.EqualTo("http://{host}:81/"));
        Assert.That(Two, Has.Count.EqualTo(2));
    }

    [Test]
    public void Save_ReplacesOne() =>
        Assert.That(WebCardBookmarks.Save(Two, 1, "Rate 2", "http://{host}:8081/")![1].Name, Is.EqualTo("Rate 2"));

    [Test]
    public void Save_Refuses_BadInput()
    {
        Assert.That(WebCardBookmarks.Save(Two, -1, "", "http://{host}/"), Is.Null);
        Assert.That(WebCardBookmarks.Save(Two, -1, "x", "file:///etc/passwd"), Is.Null);
        Assert.That(WebCardBookmarks.Save(Two, 2, "x", "http://{host}/"), Is.Null);
        Assert.That(WebCardBookmarks.Save(Two, -1, new string('n', 41), "http://{host}/"), Is.Null);
        var full = Enumerable.Range(0, WebCardBookmarks.MaxCount).Select(i => new WebCardBookmark { Name = "b" + i, Url = "http://{host}/" }).ToList();
        Assert.That(WebCardBookmarks.Save(full, -1, "one more", "http://{host}/"), Is.Null);
    }

    [Test]
    public void Delete_RemovesOne_OrRefuses()
    {
        Assert.That(WebCardBookmarks.Delete(Two, 0)!.Single().Name, Is.EqualTo("Rate"));
        Assert.That(WebCardBookmarks.Delete(Two, 5), Is.Null);
    }
}
