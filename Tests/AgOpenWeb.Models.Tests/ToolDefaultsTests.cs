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
}
