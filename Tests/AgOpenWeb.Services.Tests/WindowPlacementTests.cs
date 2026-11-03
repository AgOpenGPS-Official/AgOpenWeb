// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Drawing;
using System.IO;
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// #571: the desktop window had no restored size, so Restore shrank it to its title bar, and a
/// size the operator gave it was forgotten at the next start. AgOpenGPS keeps the size, place and
/// maximized state between runs; so does the launcher now.
/// </summary>
[TestFixture]
public class WindowPlacementTests
{
    private static readonly Rectangle Laptop = new(0, 0, 1920, 1040);          // work area: the taskbar off
    private static readonly Rectangle RightScreen = new(1920, 0, 2560, 1400);

    [Test]
    public void FirstStart_Maximized_WithA1280By800RestoreSizeCentred()
    {
        var p = WindowPlacement.Fit(null, new[] { Laptop });
        Assert.That(p.Maximized, Is.True, "opens maximized as before");
        Assert.That(p.Bounds, Is.EqualTo(new Rectangle(320, 120, 1280, 800)), "what Restore returns to");
    }

    [Test]
    public void FirstStart_OnASmallScreen_TheRestoreSizeFitsIt()
    {
        var p = WindowPlacement.Fit(null, new[] { new Rectangle(0, 0, 1024, 720) });
        Assert.That(p.Bounds, Is.EqualTo(new Rectangle(0, 0, 1024, 720)));
    }

    [Test]
    public void ASavedPlacement_OnAScreen_IsUsedAsItWas()
    {
        var saved = new WindowPlacement(2100, 100, 1500, 900, Maximized: false);
        Assert.That(WindowPlacement.Fit(saved, new[] { Laptop, RightScreen }), Is.EqualTo(saved));
    }

    [Test]
    public void ASavedPlacement_OnAScreenThatIsGone_TheDefaultKeepingMaximized()
    {
        var saved = new WindowPlacement(2100, 100, 1500, 900, Maximized: false);
        var p = WindowPlacement.Fit(saved, new[] { Laptop });   // the right-hand monitor unplugged
        Assert.That(p.Bounds, Is.EqualTo(new Rectangle(320, 120, 1280, 800)));
        Assert.That(p.Maximized, Is.False);
    }

    [Test]
    public void ASavedPlacement_PartlyOff_IsPulledOnAndSizedToTheScreen()
    {
        var saved = new WindowPlacement(1500, -10, 2400, 1300, Maximized: false);
        var p = WindowPlacement.Fit(saved, new[] { Laptop });
        Assert.That(p.Bounds, Is.EqualTo(new Rectangle(0, 0, 1920, 1040)));
    }

    [Test]
    public void ASavedSize_TooSmallToUse_GivesTheDefault()
    {
        // What the window collapsed to before the fix: only its title-bar buttons.
        var saved = new WindowPlacement(400, 300, 140, 30, Maximized: false);
        var p = WindowPlacement.Fit(saved, new[] { Laptop });
        Assert.That(p.Bounds, Is.EqualTo(new Rectangle(320, 120, 1280, 800)));
    }

    [Test]
    public void SavedAndLoaded_ThroughTheFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aow-window-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.That(WindowPlacement.Load(dir), Is.Null, "none yet");
            var p = new WindowPlacement(10, 20, 1300, 850, Maximized: true);
            p.Save(dir);
            Assert.That(WindowPlacement.Load(dir), Is.EqualTo(p));

            File.WriteAllText(Path.Combine(dir, WindowPlacement.FileName), "{ not json");
            Assert.That(WindowPlacement.Load(dir), Is.Null, "a damaged file: the default");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
