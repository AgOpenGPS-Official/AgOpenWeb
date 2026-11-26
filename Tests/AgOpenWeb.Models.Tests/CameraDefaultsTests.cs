// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.State;
using NUnit.Framework;

namespace AgOpenWeb.Models.Tests;

/// <summary>A fresh install's camera, held to AgOpenGPS's default.</summary>
[TestFixture]
public class CameraDefaultsTests
{
    // #730: a fresh install started in Map mode (the world fixed, the tractor moving across it);
    // AgOpenGPS starts with the tractor centred and the map turning with its heading. A saved
    // mode (appstate.json, view.save) still wins.
    [Test]
    public void The_camera_follows_the_tractor_heading_up_by_default()
    {
        Assert.That(new PersistentAppState().CameraMode, Is.EqualTo(CameraMode.HeadingUp));
    }
}
