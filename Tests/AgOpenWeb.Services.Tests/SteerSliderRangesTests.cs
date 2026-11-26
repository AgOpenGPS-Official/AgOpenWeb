// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// The steer settings sliders reach as far as AgOpenGPS's (FormSteer.Designer.cs). A value an
/// operator ran with in AgOpenGPS must be one they can set here (#600: Min PWM 80 was cut to 50).
/// </summary>
[TestFixture]
public class SteerSliderRangesTests
{
    // AgOpenGPS FormSteer: hsbarMinPWM 0..200, hsbarProportionalGain 1..200.
    [TestCase("autosteer.minPwm", 200)]
    [TestCase("autosteer.proportionalGain", 200)]
    public void Slider_ReachesAgOpenGpsMaximum(string key, int agOpenGpsMax)
    {
        var (_, max) = SliderRange(key);
        Assert.That(max, Is.EqualTo(agOpenGpsMax), $"{key}: AgOpenGPS goes to {agOpenGpsMax}");
    }

    internal static (double Min, double Max) SliderRange(string key)
    {
        var html = File.ReadAllText(Path.Combine(RepoRoot(), "Shared", "AgOpenWeb.RemoteServer", "wwwroot", "index.html"));
        var tag = Regex.Match(html, "<input class=\"cfg-slider\" data-key=\"" + Regex.Escape(key) + "\"[^>]*>");
        Assert.That(tag.Success, Is.True, $"no slider for {key} in index.html");
        double Attr(string name) => double.Parse(Regex.Match(tag.Value, name + "=\"(-?[0-9.]+)\"").Groups[1].Value,
            System.Globalization.CultureInfo.InvariantCulture);
        return (Attr("min"), Attr("max"));
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
