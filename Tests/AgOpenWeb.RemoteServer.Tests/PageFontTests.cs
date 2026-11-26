// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using NUnit.Framework;

namespace AgOpenWeb.RemoteServer.Tests;

/// <summary>
/// The web client has one typeface. The body set none, so every rule named its own, and text a rule
/// missed was in the browser's default serif (Times New Roman: Roll Correction, GPS Offset Fix,
/// Recorded Path, the chart and VT card titles), buttons, boxes and lists in the browser's Arial
/// 13.3 px (20 Nov 2026, measured on the 1280x800 tablet layout).
/// </summary>
[TestFixture]
public class PageFontTests
{
    [Test]
    public void The_body_sets_the_typeface_and_form_controls_take_it()
    {
        var css = Css("base.css");
        Assert.That(css, Does.Match(@"\bbody\s*\{[^}]*font-family:\s*system-ui,\s*sans-serif"), "the body's typeface");
        Assert.That(css, Does.Match(@"button,\s*input,\s*select,\s*textarea\s*\{[^}]*font-family:\s*inherit"),
            "buttons and boxes inherit it instead of the browser's Arial");
    }

    [Test]
    public void No_stylesheet_names_another_typeface()
    {
        foreach (var file in Directory.GetFiles(Path.Combine(WwwRoot(), "css"), "*.css"))
        {
            var css = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(css, @"font(?:-family)?\s*:\s*([^;}]+)"))
            {
                var value = m.Groups[1].Value.Trim();
                bool ok = value == "inherit" || value.Contains("system-ui", StringComparison.Ordinal)
                    || value.Contains("monospace", StringComparison.Ordinal);
                Assert.That(ok, Is.True, Path.GetFileName(file) + ": " + m.Value);
            }
        }
    }

    private static string Css(string name) => File.ReadAllText(Path.Combine(WwwRoot(), "css", name));

    private static string WwwRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AgOpenWeb.sln"))) dir = dir.Parent;
        Assert.That(dir, Is.Not.Null, "Could not locate repo root (AgOpenWeb.sln) from test base dir.");
        return Path.Combine(dir!.FullName, "Shared", "AgOpenWeb.RemoteServer", "wwwroot");
    }
}
