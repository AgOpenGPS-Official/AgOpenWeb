// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using NUnit.Framework;

namespace AgOpenWeb.RemoteServer.Tests;

/// <summary>
/// Menu panels stay on the screen at a bigger UI size: .ln-panel's max-height holds them and they
/// scroll. The AutoSteer panel set overflow: hidden over that scroll, so at 115 % and up on a
/// 1368x840 screen (and Full at 100 % on a 1280x800 tablet) Test Mode, Steer Wizard and the
/// Set/Act/Err bar were cut off. NTRIP Settings and Edit NTRIP Profile set their own max-height
/// (100vh - 72px), which left out the bar above them: Save was below the screen (24 Nov 2026).
/// </summary>
[TestFixture]
public class PanelFitTests
{
    [Test]
    public void The_AutoSteer_panes_scroll_between_the_title_bar_and_the_status_bar()
    {
        Assert.That(Html(), Does.Contain("<div id=\"autosteercfg\" class=\"ln-panel as-shell\">"),
            "the AutoSteer panel takes .ln-panel's max-height");
        var css = Css("panels.css");
        Assert.That(Decls(css, ".as-shell.open"), Is.SupersetOf(new[] { "display: flex", "flex-direction: column" }),
            "the open panel is a column: title bar, panes, status bar");
        Assert.That(Decls(css, ".as-titlebar"), Does.Contain("flex: none"), "the title bar keeps its height");
        Assert.That(Decls(css, ".as-statusbar"), Does.Contain("flex: none"), "the Set/Act/Err bar keeps its height");
        Assert.That(Decls(css, ".as-body"), Is.SupersetOf(new[] { "flex: 1 1 auto", "min-height: 0", "overflow: auto" }),
            "the panes take what is left and scroll");
    }

    [Test]
    public void The_NTRIP_panels_take_the_menu_panels_max_height()
    {
        var html = Html();
        Assert.That(html, Does.Contain("<div id=\"ntripsettings\" class=\"ln-panel nio-ntset\">"));
        Assert.That(html, Does.Contain("<div id=\"ntripeditor\" class=\"ln-panel nio-ntedit\">"));
        var css = Css("panels.css");
        foreach (var sel in new[] { ".nio-ntset", ".nio-ntedit" })
        {
            var decls = Decls(css, sel);
            Assert.That(decls, Is.Not.Empty, sel);
            Assert.That(decls, Has.None.StartsWith("max-height"), sel + " leaves the max-height to .ln-panel");
        }
    }

    /// <summary>Declarations of every rule whose selector list names <paramref name="selector"/> exactly.</summary>
    private static List<string> Decls(string css, string selector)
    {
        css = Regex.Replace(css, @"/\*.*?\*/", "", RegexOptions.Singleline);
        var decls = new List<string>();
        foreach (Match m in Regex.Matches(css, @"([^{}]+)\{([^{}]*)\}"))
        {
            if (!m.Groups[1].Value.Split(',').Any(s => s.Trim() == selector)) continue;
            decls.AddRange(m.Groups[2].Value.Split(';').Select(d => Regex.Replace(d.Trim(), @"\s+", " ")).Where(d => d.Length > 0));
        }
        return decls;
    }

    private static string Html() => File.ReadAllText(Path.Combine(WwwRoot(), "index.html"));

    private static string Css(string name) => File.ReadAllText(Path.Combine(WwwRoot(), "css", name));

    private static string WwwRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AgOpenWeb.sln"))) dir = dir.Parent;
        Assert.That(dir, Is.Not.Null, "Could not locate repo root (AgOpenWeb.sln) from test base dir.");
        return Path.Combine(dir!.FullName, "Shared", "AgOpenWeb.RemoteServer", "wwwroot");
    }
}
