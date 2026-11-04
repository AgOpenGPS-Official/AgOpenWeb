// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.RegularExpressions;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// The WAS offset can be set by hand, not only by Zero WAS (#599): AgOpenGPS FormSteer has a
/// slider for it (hsbarWasOffset, -4000 to 4000 counts) beside the zero button.
/// </summary>
[TestFixture]
public class WasOffsetSliderTests
{
    [Test]
    public void WasOffset_HasASlider_OverAgOpenGpsRange()
    {
        var html = File.ReadAllText(Path.Combine(RepoRoot(), "Shared", "AgOpenWeb.RemoteServer", "wwwroot", "index.html"));
        var tag = Regex.Match(html, "<input class=\"cfg-slider\" data-key=\"autosteer.wasOffset\"[^>]*>");
        Assert.That(tag.Success, Is.True, "no slider for autosteer.wasOffset in index.html");
        double Attr(string name) => double.Parse(Regex.Match(tag.Value, name + "=\"(-?[0-9.]+)\"").Groups[1].Value, CultureInfo.InvariantCulture);
        Assert.That((Attr("min"), Attr("max")), Is.EqualTo((-4000.0, 4000.0)));
        Assert.That(html, Does.Contain("data-for=\"autosteer.wasOffset\""), "the slider's value box (tap to type)");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AgOpenWeb.sln")))
            dir = dir.Parent;
        Assert.That(dir, Is.Not.Null, "Could not locate repo root (AgOpenWeb.sln) from test base dir.");
        return dir!.FullName;
    }
}
