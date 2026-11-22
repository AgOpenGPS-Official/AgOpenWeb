// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using NUnit.Framework;

namespace AgOpenWeb.RemoteServer.Tests;

/// <summary>
/// The boundary menu and the recorder can be read on a tablet. Their buttons were icons whose words
/// were only a title (a tooltip: shown under a mouse, never under a finger), and the recorder said
/// "Stop Record Pause Boundary" whether it recorded the field's outer boundary or an obstacle: on a
/// tester's field (Couto da Ribeira, 20 Nov 2026) three obstacles driven round with the field's
/// button (the steering wheel, beside the obstacle's) were each saved as the outer boundary.
/// </summary>
[TestFixture]
public class BoundaryRecorderPageTests
{
    private static BoundaryDto Recording(bool obstacle) => new(
        Array.Empty<BoundaryItemDto>(), -1, true, true, false, 12, 0.5, 100, true, true, false,
        new double[] { 1, 2, 3, 4 }, obstacle);

    [Test]
    public void The_obstacle_flag_is_the_Boundary_frame_s_last_byte()
    {
        Assert.Multiple(() =>
        {
            Assert.That(WireCodec.EncodeBoundary(Recording(true))[^1], Is.EqualTo(1), "an obstacle");
            Assert.That(WireCodec.EncodeBoundary(Recording(false))[^1], Is.EqualTo(0), "the field's boundary");
        });
    }

    [Test]
    public void Every_button_with_an_icon_has_its_words_under_it()
    {
        var html = Html();
        var menu = Between(html, "<div id=\"boundarymenu\"", "<div class=\"bm-lhdr\">");
        var recorder = Between(html, "<div id=\"boundaryplayer\"", "<!-- Import KML boundary picker");
        var buttons = Regex.Matches(menu + recorder, "<button ([^>]*)>(.*?)</button>", RegexOptions.Singleline)
            .Where(m => m.Groups[2].Value.Contains("<img", StringComparison.Ordinal)).ToList();
        Assert.That(buttons, Has.Count.EqualTo(16), "8 in the menu's toolbar, 8 in the recorder");
        foreach (Match b in buttons)
        {
            var id = Regex.Match(b.Groups[1].Value, "id=\"([^\"]+)\"").Groups[1].Value;
            var words = Regex.Match(b.Groups[2].Value, "<span [^>]*class=\"(?:bm-tlbl|bp-lbl)\"[^>]*>([^<]*)</span>");
            Assert.That(words.Success && words.Groups[1].Value.Trim().Length > 0, Is.True, id + ": words under the icon, not only a title");
        }
    }

    [Test]
    public void The_field_s_and_the_obstacle_s_buttons_say_which_is_which()
    {
        var html = Html();
        Assert.That(Words(html, "bm-drivearound"), Does.Contain("field"));
        Assert.That(Words(html, "bm-driveinner"), Does.Contain("obstacle"));
    }

    [Test]
    public void The_recorder_s_title_comes_from_what_the_host_records()
    {
        var html = Html();
        Assert.That(html, Does.Not.Contain("Stop Record Pause Boundary"), "one title for both");
        Assert.That(html, Does.Contain("<span id=\"bp-title\" class=\"ln-hdr-title\">"));
        var js = File.ReadAllText(Path.Combine(WwwRoot(), "js", "hud-tools.js"));
        Assert.That(js, Does.Contain("b.recordingObstacle"), "the host's flag");
        Assert.That(js, Does.Contain("tr('Record Obstacle')").And.Contain("tr('Record Field Boundary')"));
        var transport = File.ReadAllText(Path.Combine(WwwRoot(), "transport.js"));
        Assert.That(transport, Does.Contain("recordingObstacle"), "decoded from the Boundary frame");
    }

    private static string Words(string html, string id)
    {
        var b = Regex.Match(html, "<button id=\"" + id + "\"[^>]*>(.*?)</button>", RegexOptions.Singleline);
        Assert.That(b.Success, Is.True, id);
        return Regex.Match(b.Groups[1].Value, "class=\"bm-tlbl\">([^<]*)<").Groups[1].Value;
    }

    private static string Between(string s, string from, string to)
    {
        int a = s.IndexOf(from, StringComparison.Ordinal);
        Assert.That(a, Is.GreaterThanOrEqualTo(0), from);
        int b = s.IndexOf(to, a, StringComparison.Ordinal);
        Assert.That(b, Is.GreaterThan(a), to);
        return s[a..b];
    }

    private static string Html() => File.ReadAllText(Path.Combine(WwwRoot(), "index.html"));

    private static string WwwRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AgOpenWeb.sln"))) dir = dir.Parent;
        Assert.That(dir, Is.Not.Null, "Could not locate repo root (AgOpenWeb.sln) from test base dir.");
        return Path.Combine(dir!.FullName, "Shared", "AgOpenWeb.RemoteServer", "wwwroot");
    }
}
