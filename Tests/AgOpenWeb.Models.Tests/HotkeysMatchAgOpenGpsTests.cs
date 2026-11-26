// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Configuration;
using NUnit.Framework;

namespace AgOpenWeb.Models.Tests;

/// <summary>
/// Checklist 10.8 / 12.9: the hotkeys. Their defaults are AgOpenGPS's "ACFGMNPTYVW12345678"
/// (Form_Keys btnReset) in its order; a key that is already another action's is refused
/// (AgOpenGPS lets two actions share a key and the first in its list wins, silently; the
/// checklist asks for a refusal). The browser sends the matching command for a bound key
/// (map-input.js HOTKEY_CMD: AutoSteer → autosteer.toggle, NudgeLeft/Right → track.nudge*,
/// SnapPivot → track.snapPivot).
/// </summary>
[TestFixture]
public class HotkeysMatchAgOpenGpsTests
{
    [Test]
    public void Defaults_AreAgOpenGpss_InItsOrder()
    {
        const string agOpenGps = "ACFGMNPTYVW12345678"; // autosteer, cycle lines, field menu, flag, manual, auto, snap, left, right, vehicle, wizard, sections 1-8
        var cfg = new HotkeyConfig();
        var ours = string.Concat(Enum.GetValues<HotkeyAction>().Select(a => cfg.GetKeyForAction(a)));
        Assert.That(ours, Is.EqualTo(agOpenGps));
        cfg.SetKeyForAction(HotkeyAction.AutoSteer, "Z");
        cfg.ResetToDefaults();
        Assert.That(string.Concat(Enum.GetValues<HotkeyAction>().Select(a => cfg.GetKeyForAction(a))), Is.EqualTo(agOpenGps), "after a reset");
    }

    [Test]
    public void AKeyThatIsAnotherActions_IsRefused_AndNothingChanges()
    {
        var cfg = new HotkeyConfig();
        Assert.That(cfg.SetKeyForAction(HotkeyAction.NudgeLeft, "a"), Is.False, "A is AutoSteer's");
        Assert.That(cfg.GetKeyForAction(HotkeyAction.NudgeLeft), Is.EqualTo("T"), "the nudge keeps its key");
        Assert.That(cfg.GetKeyForAction(HotkeyAction.AutoSteer), Is.EqualTo("A"), "and AutoSteer keeps A");
        Assert.That(cfg.BoundTo("a"), Is.EqualTo(HotkeyAction.AutoSteer));
        Assert.That(cfg.GetActionForKey("t"), Is.EqualTo(HotkeyAction.NudgeLeft));
    }

    [Test]
    public void AFreeKey_IsTaken_AndTheSameKeyAgain_IsFine()
    {
        var cfg = new HotkeyConfig();
        Assert.That(cfg.SetKeyForAction(HotkeyAction.NudgeLeft, "ArrowLeft"), Is.True);
        Assert.That(cfg.GetActionForKey("ArrowLeft"), Is.EqualTo(HotkeyAction.NudgeLeft));
        Assert.That(cfg.GetActionForKey("T"), Is.Null, "the old key is free");
        Assert.That(cfg.SetKeyForAction(HotkeyAction.NudgeLeft, "arrowleft"), Is.True, "re-binding the same key to the same action");
        Assert.That(cfg.SetKeyForAction(HotkeyAction.NudgeRight, "T"), Is.True, "the freed key can be taken");
    }
}
