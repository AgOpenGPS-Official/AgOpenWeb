// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Configuration;
using NUnit.Framework;

namespace AgOpenWeb.Models.Tests;

/// <summary>
/// A fresh install's tool settings, held to AgOpenGPS's defaults (Properties/ToolSettings.cs).
/// </summary>
[TestFixture]
public class ToolDefaultsTests
{
    // Field job harness (#690, finding 10): 'Off outside boundary' started off on a fresh
    // install (on in a new or reset profile, and in AgOpenGPS), and 446 m² was sprayed outside.
    [Test]
    public void Sections_turn_off_outside_the_boundary_by_default()
    {
        Assert.That(new ToolConfig().IsSectionOffWhenOut, Is.True);
        Assert.That(new ConfigurationStore().Tool.IsSectionOffWhenOut, Is.True);
    }

    // Field job harness C16: on 0 s, a sprayer whose valves take AgOpenGPS's assumed 1 s / 0.5 s
    // started late at every edge, ran on into the headland, and the map showed 179 m² as sprayed
    // where nothing came out. AgOpenGPS installs 1.0 s on, 0.5 s off; the turn-off delay stays 0.
    [Test]
    public void Look_ahead_is_AgOpenGPS_s_by_default()
    {
        foreach (var tool in new[] { new ToolConfig(), new ConfigurationStore().Tool })
        {
            Assert.That(tool.LookAheadOnSetting, Is.EqualTo(1.0));
            Assert.That(tool.LookAheadOffSetting, Is.EqualTo(0.5));
            Assert.That(tool.TurnOffDelay, Is.EqualTo(0.0));
        }
    }

    [Test]
    public void A_section_width_that_is_no_number_is_not_taken()
    {
        // The implement's width, the pass spacing and each section's place are sums of these.
        // The default width takes its place: a profile loaded with "NaN" for a section does not
        // keep the last tool's width there.
        var tool = new ToolConfig { DefaultSectionWidth = 250 };
        tool.SetSectionWidth(0, 300);
        tool.SetSectionWidth(1, 300);

        tool.SetSectionWidth(0, double.NaN);
        tool.SetSectionWidth(1, double.PositiveInfinity);

        Assert.That(tool.GetSectionWidth(0), Is.EqualTo(250));
        Assert.That(tool.GetSectionWidth(1), Is.EqualTo(250));
    }

    [Test]
    public void A_default_section_width_that_is_no_number_is_the_initial_one()
    {
        var tool = new ToolConfig { DefaultSectionWidth = 250 };

        tool.DefaultSectionWidth = double.NaN;
        Assert.That(tool.DefaultSectionWidth, Is.EqualTo(ToolConfig.InitialSectionWidthCm), "not the last tool's 250");

        tool.SetAllSectionWidths(double.NaN);
        Assert.That(tool.GetSectionWidth(0), Is.EqualTo(ToolConfig.InitialSectionWidthCm));
        Assert.That(tool.GetSectionWidth(ToolConfig.MaxSections - 1), Is.EqualTo(ToolConfig.InitialSectionWidthCm));
    }
}
