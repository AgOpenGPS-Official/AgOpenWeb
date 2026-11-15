// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using NUnit.Framework;
using AgOpenWeb.Models;
using AgOpenWeb.RemoteServer;

namespace AgOpenWeb.RemoteServer.Tests;

/// <summary>
/// The page hides what the host doesn't run (FeatureSwitches): it starts with every feature hidden
/// and the Hello frame names the ones to show, so a page never shows a hidden feature's controls,
/// not even before the host has answered.
/// </summary>
public class FeatureSwitchesPageTests
{
    [Test]
    public void Hello_carries_the_features_after_the_client_id()
    {
        var f = WireCodec.EncodeHello("abc", SwitchedFeature.WebCards | SwitchedFeature.AgShare);
        Assert.That(f[^1], Is.EqualTo(6), "the last byte: web cards 2 + AgShare 4");
        Assert.That(WireCodec.EncodeHello("abc", SwitchedFeature.None)[^1], Is.Zero);
    }

    [Test]
    public void The_page_starts_with_every_hideable_feature_hidden()
    {
        var html = File.ReadAllText(Path.Combine(WwwRoot(), "index.html"));
        var body = Regex.Match(html, "<body class=\"([^\"]*)\"");
        Assert.That(body.Success, Is.True, "the body has the fs-no-* classes");
        Assert.That(body.Groups[1].Value.Split(' '), Is.SupersetOf(new[] { "fs-no-vt", "fs-no-webcards", "fs-no-agshare" }));
    }

    [Test]
    public void Every_agshare_button_is_inside_the_hidden_block()
    {
        var html = File.ReadAllText(Path.Combine(WwwRoot(), "index.html"));
        var block = Regex.Match(html, "<div class=\"fo-grid fs-agshare\">(.*?)</div>", RegexOptions.Singleline);
        Assert.That(block.Success, Is.True);
        foreach (var id in new[] { "fo-agupload", "fo-agdownload", "fo-agapi" })
        {
            Assert.That(block.Groups[1].Value, Does.Contain("id=\"" + id + "\""), id);
            Assert.That(Regex.Matches(html, "id=\"" + id + "\"").Count, Is.EqualTo(1), id + " only there");
        }
    }

    [Test]
    public void The_css_hides_each_feature_and_core_clears_it_from_the_hello()
    {
        var css = File.ReadAllText(Path.Combine(WwwRoot(), "css", "base.css"));
        foreach (var rule in new[] { ".fs-no-vt #ln-vt", ".fs-no-webcards #ln-web", ".fs-no-webcards .web-card", ".fs-no-agshare .fs-agshare", ".fs-no-vt.fs-no-webcards .fs-cardpane" })
            Assert.That(css, Does.Contain(rule), rule);
        var core = File.ReadAllText(Path.Combine(WwwRoot(), "js", "core.js"));
        foreach (var cls in new[] { "'fs-no-vt', !(features & 1)", "'fs-no-webcards', !(features & 2)", "'fs-no-agshare', !(features & 4)" })
            Assert.That(core, Does.Contain(cls), cls);
    }

    private static string WwwRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AgOpenWeb.sln"))) dir = dir.Parent;
        Assert.That(dir, Is.Not.Null, "Could not locate repo root (AgOpenWeb.sln) from test base dir.");
        return Path.Combine(dir!.FullName, "Shared", "AgOpenWeb.RemoteServer", "wwwroot");
    }
}
