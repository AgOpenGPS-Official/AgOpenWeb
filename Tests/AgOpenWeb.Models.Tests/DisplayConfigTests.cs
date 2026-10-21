// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Configuration;
using NUnit.Framework;

namespace AgOpenWeb.Models.Tests;

/// <summary>UI size (Screen &amp; Alerts): only the offered steps are kept.</summary>
[TestFixture]
public class DisplayConfigTests
{
    [Test]
    public void UiScale_DefaultsTo100()
    {
        Assert.That(new DisplayConfig().UiScale, Is.EqualTo(100));
    }

    [TestCase(100, 100)]
    [TestCase(115, 115)]
    [TestCase(130, 130)]
    [TestCase(150, 150)]
    [TestCase(0, 100)]     // a missing or broken saved value
    [TestCase(120, 115)]
    [TestCase(125, 130)]
    [TestCase(400, 150)]
    public void UiScale_SnapsToAStep(int set, int expected)
    {
        var d = new DisplayConfig { UiScale = set };
        Assert.That(d.UiScale, Is.EqualTo(expected));
    }
}
