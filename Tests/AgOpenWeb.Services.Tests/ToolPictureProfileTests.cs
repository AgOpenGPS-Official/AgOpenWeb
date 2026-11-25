// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Profile;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgOpenWeb.Services.Tests;

/// <summary>The implement's picture on the map (Tool > Type > Picture on the map) is the
/// tool profile's: the seed drill's profile shows the drill, the sprayer's the boom and its tank.
/// A profile saved before it, or with a value this build does not know, shows the section bars. Its
/// colour (Colour on the map) is the profile's too; without one it is red, the default.</summary>
[TestFixture]
public class ToolPictureProfileTests
{
    private string _dir = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "aow-toolpic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown() { try { Directory.Delete(_dir, true); } catch { } }

    [TestCaseSource(nameof(EveryPicture))]
    public void The_picture_is_saved_with_the_tool_profile(ToolPicture picture)
    {
        var src = new ConfigurationStore();
        src.Tool.Picture = picture;
        ToolProfileJsonService.Save(_dir, "Implement", src);

        var dst = new ConfigurationStore();
        dst.Tool.Picture = picture == ToolPicture.SeedDrill ? ToolPicture.FrontSprayer : ToolPicture.SeedDrill; // the tool before
        Assert.That(ToolProfileJsonService.Load(_dir, "Implement", dst), Is.True);
        Assert.That(dst.Tool.Picture, Is.EqualTo(picture));
    }

    private static ToolPicture[] EveryPicture() => Enum.GetValues<ToolPicture>();

    [TestCase(null, Description = "saved before the setting")]
    [TestCase(99, Description = "a picture this build does not have")]
    [TestCase(-1)]
    public void A_profile_without_a_known_picture_shows_the_section_bars(int? stored)
    {
        ToolProfileJsonService.Save(_dir, "Old", new ConfigurationStore());
        var path = Path.Combine(_dir, "Old.json");
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var tool = node["tool"]!.AsObject();
        Assert.That(tool.ContainsKey("picture"), Is.True, "the setting is written");
        tool.Remove("picture");
        if (stored is int v) tool["picture"] = v;
        File.WriteAllText(path, node.ToJsonString());

        var dst = new ConfigurationStore();
        dst.Tool.Picture = ToolPicture.SeedDrill; // the tool that was active before
        Assert.That(ToolProfileJsonService.Load(_dir, "Old", dst), Is.True);
        Assert.That(dst.Tool.Picture, Is.EqualTo(ToolPicture.None));
    }

    [Test]
    public void An_unknown_value_set_in_code_is_none()
    {
        var t = new ToolConfig { Picture = ToolPicture.FrontSprayer };
        t.Picture = (ToolPicture)99;
        Assert.That(t.Picture, Is.EqualTo(ToolPicture.None));
    }

    [Test]
    public void A_new_tool_profile_has_no_picture()
    {
        var store = new ConfigurationStore();
        store.Tool.Picture = ToolPicture.SeedDrill;
        new TestToolProfileService(_dir).CreateDefaultProfile("New", store);
        Assert.That(store.Tool.Picture, Is.EqualTo(ToolPicture.None));
    }

    [TestCase(0x1B4F9C)]
    [TestCase(0x000000, Description = "black is a colour")]
    [TestCase(0xFFFFFF)]
    [TestCase(ToolConfig.DefaultPictureColor)]
    public void The_colour_is_saved_with_the_tool_profile(int colour)
    {
        var src = new ConfigurationStore();
        src.Tool.Picture = ToolPicture.RakeFour;
        src.Tool.PictureColor = colour;
        ToolProfileJsonService.Save(_dir, "Implement", src);

        var dst = new ConfigurationStore();
        dst.Tool.PictureColor = 0x123456; // the tool before
        Assert.That(ToolProfileJsonService.Load(_dir, "Implement", dst), Is.True);
        Assert.That(dst.Tool.PictureColor, Is.EqualTo(colour));
    }

    [TestCase(null, Description = "saved before the setting")]
    [TestCase(0x1000000, Description = "not a colour")]
    [TestCase(-7)]
    public void A_profile_without_a_colour_draws_the_pictures_own(int? stored)
    {
        ToolProfileJsonService.Save(_dir, "Old", new ConfigurationStore());
        var path = Path.Combine(_dir, "Old.json");
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var tool = node["tool"]!.AsObject();
        Assert.That(tool.ContainsKey("pictureColor"), Is.True, "the setting is written");
        tool.Remove("pictureColor");
        if (stored is int v) tool["pictureColor"] = v;
        File.WriteAllText(path, node.ToJsonString());

        var dst = new ConfigurationStore();
        dst.Tool.PictureColor = 0x3B8A3A; // the tool that was active before
        Assert.That(ToolProfileJsonService.Load(_dir, "Old", dst), Is.True);
        Assert.That(dst.Tool.PictureColor, Is.EqualTo(ToolConfig.DefaultPictureColor));
    }

    [Test]
    public void A_new_tool_profile_draws_the_pictures_own_colours()
    {
        var store = new ConfigurationStore();
        store.Tool.PictureColor = 0xC8322B;
        new TestToolProfileService(_dir).CreateDefaultProfile("New", store);
        Assert.That(store.Tool.PictureColor, Is.EqualTo(ToolConfig.DefaultPictureColor));
    }

    private sealed class TestToolProfileService : ToolProfileService
    {
        public TestToolProfileService(string dir)
            : base(NullLogger<ToolProfileService>.Instance, dir) { }
    }
}
