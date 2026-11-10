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
}
