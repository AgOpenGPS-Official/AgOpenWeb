// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace AgOpenWeb.RemoteServer.Tests;

/// <summary>
/// Every picture the web client names is in <c>wwwroot/icons</c>: a literal <c>/icons/X.png</c>, and the
/// lists of picture names it picks from (the measurement diagrams, chosen by the vehicle or tool type).
/// A missing one is an empty box in a settings panel, which no other check sees (the hitch diagrams,
/// redrawn for #547, replaced two of AgOpenGPS's).
/// </summary>
[TestFixture]
public class IconReferenceTests
{
    [Test]
    public void Every_picture_the_client_names_exists()
    {
        var www = Path.Combine(RepoRoot(), "Shared", "AgOpenWeb.RemoteServer", "wwwroot");
        // By the exact name: a Linux host serves files case-sensitively (a Mac's disk would find "About.png" for "about").
        var icons = Directory.GetFiles(Path.Combine(www, "icons"), "*.png").Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.Ordinal);
        bool Exists(string n) => icons.Contains(n);
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var f in Directory.GetFiles(Path.Combine(www, "js"), "*.js").Append(Path.Combine(www, "index.html")))
        {
            var src = File.ReadAllText(f);
            foreach (Match m in Regex.Matches(src, @"/icons/([A-Za-z0-9_-]+)\.png"))
                names.Add(m.Groups[1].Value);
            // The lists of names a picture is chosen from (by the vehicle or tool type): every list in a
            // setImg(...) call or on a line that builds an /icons/ path, and any list in which one name is
            // a picture (a list of pictures, so every name in it must be one).
            const string List = @"\[((?:\s*'[A-Za-z0-9_-]*'\s*,?)+)\]";
            static IEnumerable<string> Items(Match list) =>
                Regex.Matches(list.Groups[1].Value, @"'([A-Za-z0-9_-]+)'").Select(n => n.Groups[1].Value);
            var chosen = Regex.Matches(src, @"setImg\('[^']*',.*?\);", RegexOptions.Singleline).Select(m => m.Value)
                .Concat(src.Split('\n').Where(l => l.Contains("/icons/", StringComparison.Ordinal)));
            foreach (var text in chosen)
                foreach (Match list in Regex.Matches(text, List))
                    names.UnionWith(Items(list));
            foreach (Match list in Regex.Matches(src, List))
                if (Items(list).Any(Exists)) names.UnionWith(Items(list));
        }
        Assert.That(names, Does.Contain("ToolHitch3ptRear").And.Contain("VehicleHitch3ptFront"), "the scan finds the hitch diagrams");
        var missing = names.Where(n => !Exists(n)).ToList();
        Assert.That(missing, Is.Empty, "named by the web client but not in wwwroot/icons");
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
