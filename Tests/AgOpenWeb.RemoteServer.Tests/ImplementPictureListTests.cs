// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using AgOpenWeb.Models.Configuration;
using NUnit.Framework;

namespace AgOpenWeb.RemoteServer.Tests;

/// <summary>
/// Tool > Type > Picture on the map lists the rows of IMPLEMENT_PICTURES (js/map-draw.js), and the host keeps
/// only a ToolPicture it knows. A value with no row could be saved and never drawn or chosen; a row
/// with no value would be refused by the host when picked.
/// </summary>
[TestFixture]
public class ImplementPictureListTests
{
    [Test]
    public void Every_picture_the_host_keeps_has_one_row_in_the_page_s_list_and_no_more()
    {
        var js = File.ReadAllText(Path.Combine(WwwRoot(), "js", "map-draw.js"));
        var list = js[js.IndexOf("const IMPLEMENT_PICTURES = [", StringComparison.Ordinal)..];
        list = list[..list.IndexOf("];", StringComparison.Ordinal)];
        var rows = Regex.Matches(list, @"\{ id: (\d+), type: ([0-3]), vehicles: \[([0-2](?:, [0-2])*)\], draw: (\w+),")
            .Select(m => (Id: int.Parse(m.Groups[1].Value), Draw: m.Groups[4].Value)).ToList();
        var values = Enum.GetValues<ToolPicture>().Where(p => p != ToolPicture.None).Select(p => (int)p).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(rows.Select(r => r.Id), Is.EquivalentTo(values), "one row per ToolPicture value but None");
            foreach (var (id, draw) in rows)
                Assert.That(js, Does.Contain("function " + draw + "(canvas, tl, veh)"), "row " + id + " draws with " + draw);
        });
    }

    private static string WwwRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AgOpenWeb.sln"))) dir = dir.Parent;
        Assert.That(dir, Is.Not.Null, "Could not locate repo root (AgOpenWeb.sln) from test base dir.");
        return Path.Combine(dir!.FullName, "Shared", "AgOpenWeb.RemoteServer", "wwwroot");
    }
}
