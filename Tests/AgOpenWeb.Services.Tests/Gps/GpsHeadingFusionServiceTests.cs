// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.Gps;

namespace AgOpenWeb.Services.Tests.Gps;

/// <summary>#112: heading follows AgOpenGPS's "Fix" and "Dual" heading sources
/// (Position.designer.cs).</summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class GpsHeadingFusionServiceTests
{
    private GpsHeadingFusionService _service = null!;
    private const double Fast = 3.0; // m/s = 10.8 km/h, above the 1.5 km/h start speed

    [SetUp]
    public void SetUp()
    {
        _service = new GpsHeadingFusionService(ConfigurationStore.Instance);

        var c = ConfigurationStore.Instance.Connections;
        c.IsDualGps = false;
        c.AutoDualFix = false;
        c.DualHeadingOffset = 0;
        c.DualSwitchSpeed = 2.0;   // km/h
        c.MinGpsStep = 0.05;       // m
        c.FixToFixDistance = 0.5;  // m
        c.HeadingFusionWeight = 0.3;
        c.ReverseDetection = true;
        c.DualReverseDistance = 0.25;
    }

    // Drive north from (0,0) one step at a time; returns the last heading.
    private double DriveNorth(int steps, double stepM = 0.3, double speedMs = Fast,
        double imu = 0, bool imuValid = false, double startN = 0, double gpsHeading = 0,
        bool hasDualHeading = true)
    {
        double h = double.NaN;
        for (int i = 0; i < steps; i++)
            h = _service.FuseHeading(gpsHeading, imu, imuValid, speedMs, 0, startN + i * stepM,
                hasDualHeading);
        return h;
    }

    [Test]
    public void BeforeAFirstHeading_TheSentenceHeadingPassesThrough()
    {
        double h = _service.FuseHeading(45, 0, false, 0.1, 0, 0, false);
        Assert.That(h, Is.EqualTo(45).Within(1e-9));
    }

    [Test]
    public void NoFirstHeadingBelow1Point5Kmh()
    {
        // 0.3 m/s = 1.08 km/h: fixes are moving north but too slowly to set a heading.
        double h = DriveNorth(6, speedMs: 0.3, gpsHeading: 45);
        Assert.That(h, Is.EqualTo(45).Within(1e-9));
    }

    [Test]
    public void SingleAntenna_HeadingFromFixToFix()
    {
        double h = DriveNorth(6);
        Assert.That(h, Is.EqualTo(0).Within(1e-6));

        // Turn east.
        for (int i = 1; i <= 5; i++) h = _service.FuseHeading(0, 0, false, Fast, i * 0.3, 1.5, false);
        Assert.That(h, Is.EqualTo(90).Within(1e-6));
    }

    [Test]
    public void MovesShorterThanMinGpsStep_KeepTheHeading()
    {
        ConfigurationStore.Instance.Connections.MinGpsStep = 1.0;
        double h = DriveNorth(4, stepM: 1.2);         // heading north set
        // Tiny sideways jitter under the min step must not swing it.
        h = _service.FuseHeading(0, 0, false, Fast, 0.5, 3.6 + 0.1, false);
        Assert.That(h, Is.EqualTo(0).Within(1e-6));
    }

    [Test]
    public void FusionWeight_IsTheGpsShareTimesPoint2_LikeAgOpenGPS()
    {
        Assert.That(GpsHeadingFusionService.FusionShareToWeight, Is.EqualTo(0.2));
        Assert.That(new ConnectionConfig().HeadingFusionWeight, Is.EqualTo(0.3),
            "default 30% GPS = AgOpenGPS fusionWeight 0.06");
    }

    [Test]
    public void Imu_IsSnappedToGpsAtStart_ThenSlowlyPulledOntoGps()
    {
        // IMU says 10°, travel is due north (0°). At start the offset snaps to -10°.
        double h = DriveNorth(3, imu: 10, imuValid: true);
        Assert.That(h, Is.EqualTo(0).Within(1e-6));

        // IMU now drifts to 20° while travel stays north: output = IMU + offset, and
        // each GPS heading pulls the offset back by 6% (0.3 share × 0.2) of the error.
        h = DriveNorth(1, imu: 20, imuValid: true, startN: 0.9);
        // offset: -10° + (0 - (20-10)) × 0.06 = -10.6° → 20 - 10.6 = 9.4°
        Assert.That(h, Is.EqualTo(9.4).Within(1e-6));
    }

    [Test]
    public void WithImu_HeadingFollowsImuWhileStopped()
    {
        DriveNorth(3, imu: 10, imuValid: true);            // offset -10°
        double h = _service.FuseHeading(0, 40, true, 0, 0, 0.6, false); // stopped, IMU turned to 40°
        Assert.That(h, Is.EqualTo(30).Within(1e-6));
    }

    [Test]
    public void Dual_AppliesOffsetAndNormalizes()
    {
        var c = ConfigurationStore.Instance.Connections;
        c.IsDualGps = true;
        c.DualHeadingOffset = 10.0;

        double h = _service.FuseHeading(355, 0, false, Fast, 0, 0, true);
        Assert.That(h, Is.EqualTo(5).Within(1e-9));
    }

    [Test]
    public void Dual_StaysOnDualWhenAutoDualFixIsOff_EvenFast()
    {
        ConfigurationStore.Instance.Connections.IsDualGps = true;
        double h = DriveNorth(6, gpsHeading: 30);   // travel north, dual says 30°
        Assert.That(h, Is.EqualTo(30).Within(1e-9));
    }

    [Test]
    public void Dual_SwitchesToFixAboveTheSwitchSpeed_InKmh()
    {
        var c = ConfigurationStore.Instance.Connections;
        c.IsDualGps = true;
        c.AutoDualFix = true;
        c.DualSwitchSpeed = 5.0; // km/h
        c.HeadingFusionWeight = 1.0; // 100% GPS → offset moves 20% per fix

        // 1.2 m/s = 4.32 km/h: below the switch speed → dual heading (30°).
        double slow = DriveNorth(6, speedMs: 1.2, gpsHeading: 30);
        Assert.That(slow, Is.EqualTo(30).Within(1e-9),
            "below the switch speed the dual heading is used (the old code did the opposite, and in m/s)");

        // 1.7 m/s = 6.12 km/h: above → Fix, with dual as the IMU pulled toward travel (0°).
        double fast = DriveNorth(10, speedMs: 1.7, gpsHeading: 30, startN: 1.8);
        Assert.That(fast, Is.LessThan(30).And.GreaterThan(0),
            "above the switch speed the fix heading pulls the dual-as-IMU heading toward travel");
    }

    [Test]
    public void Reset_KeepsTheHeading_ButForgetsStoredFixes()
    {
        DriveNorth(6);                          // heading north
        _service.Reset();

        // A fix in a new frame far away must not yield a heading toward it.
        double h = _service.FuseHeading(0, 0, false, Fast, 500, -300, false);
        Assert.That(h, Is.EqualTo(0).Within(1e-6));
    }

    // ── Reverse (#125) ───────────────────────────────────────────────────

    [Test]
    public void Imu_BackingUp_IsReverse_AndHeadingKeepsFacingForward()
    {
        DriveNorth(6, imu: 0, imuValid: true);          // facing and driving north
        Assert.That(_service.IsReverse, Is.False);

        // Back up: fixes move south while the IMU still says north.
        double h = 0;
        for (int i = 1; i <= 6; i++) h = _service.FuseHeading(0, 0, true, Fast, 0, 1.5 - i * 0.3, false);
        Assert.That(_service.IsReverse, Is.True);
        Assert.That(h, Is.EqualTo(0).Within(0.5), "heading still points the way the vehicle faces");
    }

    [Test]
    public void Imu_TurnedAround_IsNotReverse_AndHeadingFlips()
    {
        // The simulator's Flip: heading and travel both turn 180° at once. With an IMU
        // reporting the new heading this is a turn, not backing up.
        DriveNorth(6, imu: 0, imuValid: true);
        double h = 0;
        for (int i = 1; i <= 6; i++) h = _service.FuseHeading(180, 180, true, Fast, 0, 1.5 - i * 0.3, false);
        Assert.That(_service.IsReverse, Is.False);
        Assert.That(h, Is.EqualTo(180).Within(0.5));
    }

    [Test]
    public void NoImu_BackingUp_HoldsWhileUnsure_ThenIsReverse()
    {
        DriveNorth(6);
        bool sawChanging = false;
        double h = 0;
        for (int i = 1; i <= 20; i++)
        {
            h = _service.FuseHeading(0, 0, false, Fast, 0, 1.5 - i * 0.3, false);
            sawChanging |= _service.IsChangingDirection;
        }
        Assert.That(sawChanging, Is.True, "a direction change is held until the filter settles");
        Assert.That(_service.IsReverse, Is.True);
        Assert.That(_service.IsChangingDirection, Is.False);
        Assert.That(h, Is.EqualTo(0).Within(1e-6), "heading still points the way the vehicle faces");
    }

    [Test]
    public void ReverseDetectionOff_NeverReverse()
    {
        ConfigurationStore.Instance.Connections.ReverseDetection = false;
        DriveNorth(6, imu: 0, imuValid: true);
        for (int i = 1; i <= 6; i++) _service.FuseHeading(0, 0, true, Fast, 0, 1.5 - i * 0.3, false);
        Assert.That(_service.IsReverse, Is.False);
    }

    [Test]
    public void Dual_BackingUp_IsReverse()
    {
        ConfigurationStore.Instance.Connections.IsDualGps = true;
        for (int i = 0; i < 5; i++) _service.FuseHeading(0, 0, false, Fast, 0, i * 0.3, true);
        Assert.That(_service.IsReverse, Is.False);

        for (int i = 1; i <= 5; i++) _service.FuseHeading(0, 0, false, Fast, 0, 1.2 - i * 0.3, true);
        Assert.That(_service.IsReverse, Is.True);
    }

    // ── Dual GPS on, but the receiver sends $PANDA (#157) ────────────────
    // PANDA's heading field is the IMU heading (the reporter's sat 65–142° off travel),
    // never a dual-antenna heading: AgIO puts it in imuHeading, not headingTrueDual.

    private static void DualOnWithAutoSwitch()
    {
        var c = ConfigurationStore.Instance.Connections;
        c.IsDualGps = true;
        c.AutoDualFix = true;
        c.DualSwitchSpeed = 5.0; // km/h
    }

    [Test]
    public void Dual_Paogi_UsesTheAntennaHeading_NoWarning()
    {
        DualOnWithAutoSwitch();
        double h = DriveNorth(6, speedMs: 1.2, gpsHeading: 30, hasDualHeading: true);
        Assert.That(h, Is.EqualTo(30).Within(1e-9), "below the switch speed the PAOGI heading is used, as before");
        Assert.That(_service.IsDualHeadingMissing, Is.False);
    }

    [Test]
    public void Dual_Panda_UsesFixHeading_NoJumpWhenStopping()
    {
        DualOnWithAutoSwitch();
        // Travel north at 10.8 km/h; the PANDA heading (= IMU) says 95°.
        double h = DriveNorth(6, imu: 95, imuValid: true, gpsHeading: 95, hasDualHeading: false);
        Assert.That(h, Is.EqualTo(0).Within(1e-6), "heading from fix-to-fix, IMU offset snapped onto it");

        // Stop (below the switch speed): the old code showed the raw 95° here.
        h = _service.FuseHeading(95, 95, true, 0, 0, 1.5, false);
        Assert.That(h, Is.EqualTo(0).Within(1e-6), "no ~90° rotation when stopping");
        Assert.That(_service.IsReverse, Is.False);
        Assert.That(_service.IsDualHeadingMissing, Is.True);
    }

    [Test]
    public void Dual_Panda_DriftingImu_IsNotReverse()
    {
        var c = ConfigurationStore.Instance.Connections;
        c.IsDualGps = true;          // AutoDualFix off: the old code always used the "dual" heading
        c.DualReverseDistance = 0.25;

        // Forward north at 4.3 km/h while the PANDA/IMU heading drifts 120° → 140°.
        bool sawReverse = false;
        for (int i = 0; i < 20; i++)
        {
            double imu = 120 + i;
            _service.FuseHeading(imu, imu, true, 1.2, 0, i * 0.3, false);
            sawReverse |= _service.IsReverse;
        }
        Assert.That(sawReverse, Is.False, "the dual reverse check must not run on an IMU heading");
    }

    [Test]
    public void Dual_Panda_IsExactlyWhatDualOffDoes()
    {
        var c = ConfigurationStore.Instance.Connections;
        c.AutoDualFix = true;
        c.DualSwitchSpeed = 5.0;
        var dualOff = new GpsHeadingFusionService(ConfigurationStore.Instance);

        for (int i = 0; i < 16; i++)
        {
            double imu = 95 + i * 2;
            double speed = i < 10 ? Fast : 0;      // drive, then stop
            double e = i * 0.1, n = Math.Min(i, 10) * 0.3;

            c.IsDualGps = true;
            double a = _service.FuseHeading(imu, imu, true, speed, e, n, false);
            c.IsDualGps = false;
            double b = dualOff.FuseHeading(imu, imu, true, speed, e, n, false);

            Assert.That(a, Is.EqualTo(b).Within(1e-9), $"fix {i}");
            Assert.That(_service.IsReverse, Is.EqualTo(dualOff.IsReverse), $"fix {i}");
        }
    }

    [Test]
    public void Dual_PandaAfterDualFixes_RestartsFix_NotStuckInReverse()
    {
        // The simulator (a dual source, on by default) ran first, so the heading was
        // already "started" with no IMU offset learned. Then the real $PANDA receiver
        // takes over: travel north while the IMU says 95°. Seen headless: reverse
        // latched and the heading crept toward 180°.
        DualOnWithAutoSwitch();
        for (int i = 0; i < 5; i++) _service.FuseHeading(0, 0, false, 0, 0, 0, true);

        bool sawReverse = false;
        double h = double.NaN;
        for (int i = 0; i < 12; i++)
        {
            h = _service.FuseHeading(95, 95, true, Fast, 0, 10 + i * 0.3, false);
            sawReverse |= _service.IsReverse;
        }
        Assert.That(sawReverse, Is.False);
        Assert.That(h, Is.EqualTo(0).Within(1e-6), "fix-to-fix heading north, IMU offset snapped onto it");
    }

    [Test]
    public void Dual_PandaInterludeWithoutImu_HoldsTheHeading_InsteadOfNorth()
    {
        // A dual receiver whose firmware falls back to $PANDA with no IMU heading at
        // every stop (#157). The heading field is 0 then; the tractor must not swing to
        // north while stopped, it holds the last dual heading.
        DualOnWithAutoSwitch();
        double h = double.NaN;
        for (int i = 0; i < 5; i++)
            h = _service.FuseHeading(90, 0, false, 0.3, i * 0.03, 0, true);   // slow: dual direct
        Assume.That(h, Is.EqualTo(90).Within(1e-6));

        for (int i = 0; i < 10; i++)
            h = _service.FuseHeading(0, 0, false, 0.1, 0.15, i * 0.005, false); // $PANDA, no IMU
        Assert.That(h, Is.EqualTo(90).Within(1e-6), "stopped on $PANDA without an IMU: hold the heading");
        Assert.That(_service.IsDualHeadingMissing, Is.True);

        // Moving off on $PANDA: the fix-to-fix heading takes over as before.
        for (int i = 0; i < 12; i++)
            h = _service.FuseHeading(0, 0, false, Fast, 0.15, 1 + i * 0.3, false);
        Assert.That(h, Is.EqualTo(0).Within(1e-6));
    }

    [Test]
    public void DualHeadingMissing_RaisedByPanda_ClearedByPaogi_NeverWithDualOff()
    {
        var c = ConfigurationStore.Instance.Connections;
        _service.FuseHeading(95, 95, true, Fast, 0, 0, false);
        Assert.That(_service.IsDualHeadingMissing, Is.False, "Dual off: PANDA is expected");

        c.IsDualGps = true;
        _service.FuseHeading(95, 95, true, Fast, 0, 0.3, false);
        Assert.That(_service.IsDualHeadingMissing, Is.True);

        double h = _service.FuseHeading(30, 0, false, Fast, 0, 0.6, true);
        Assert.That(_service.IsDualHeadingMissing, Is.False, "a PAOGI fix clears it");
        Assert.That(h, Is.EqualTo(30).Within(1e-9));
    }

    // ── First heading needs real travel; Reset Direction (from PR #249) ─────────────

    private static double OffNorth(double headingDeg) => Math.Abs((headingDeg + 180) % 360 - 180);

    [Test]
    public void SmallSteps_DoNotSetTheFirstHeading_UntilEnoughDistanceIsCovered()
    {
        // Five fixes 5 cm apart: 0.25 m in all, short of the 0.35 m a heading needs
        // (FixToFixDistance 0.5 m). Three such fixes used to be enough, and GPS noise over
        // 10 cm can point anywhere.
        for (int i = 0; i < 5; i++)
        {
            double h = _service.FuseHeading(45, 0, false, Fast, 0, i * 0.05, false);
            Assert.That(h, Is.EqualTo(45).Within(1e-9), "the sentence heading passes through meanwhile");
        }

        double first = _service.FuseHeading(45, 0, false, Fast, 0, 0.40, false);
        Assert.That(first, Is.EqualTo(0).Within(1e-6), "set from the travel, once it is long enough");
    }

    [Test]
    public void ANoisyFixBehindTheStart_DoesNotSetABackwardsHeading()
    {
        // Driving north with an IMU that says north. The second fix lands 8 cm behind the
        // first (noise). Taken as a heading that would be south, and the IMU offset would
        // lock 180° out.
        _service.FuseHeading(0, 0, true, Fast, 0, 0.00, false);
        _service.FuseHeading(0, 0, true, Fast, 0, -0.08, false);
        _service.FuseHeading(0, 0, true, Fast, 0, 0.10, false);
        double h = DriveNorth(10, imu: 0, imuValid: true, startN: 0.3);

        Assert.That(h, Is.EqualTo(0).Within(1e-6));
        Assert.That(_service.IsReverse, Is.False);
    }

    [Test]
    public void TheRunTowardsAFirstHeading_StartsAgain_AfterDroppingBelowTheStartSpeed()
    {
        // One fix at speed far to the west, then a stop; the vehicle is moved and sets off
        // north. The heading must come from the new run, not from the old fix.
        _service.FuseHeading(45, 0, false, Fast, -50, 0, false);
        _service.FuseHeading(45, 0, false, 0.1, 0, 0, false);     // below 1.5 km/h

        double h = DriveNorth(4, gpsHeading: 45);

        Assert.That(h, Is.EqualTo(0).Within(1e-6));
    }

    [Test]
    public void ALongRealReverse_StaysAReverse()
    {
        // Backing 20 m while facing north. No distance turns a reverse into "forward the
        // other way": the tractor and the IMU still face north.
        DriveNorth(20, imu: 0, imuValid: true);
        double n = 19 * 0.3, h = 0;
        for (int i = 0; i < 67; i++)
        {
            n -= 0.3;
            h = _service.FuseHeading(0, 0, true, Fast, 0, n, false);
        }

        Assert.That(_service.IsReverse, Is.True);
        Assert.That(OffNorth(h), Is.LessThan(1.0), "still facing north");
    }

    [Test]
    public void StartedWhileBacking_ForwardTravelReadsAsReverse_UntilTheDirectionIsReset()
    {
        // First heading taken while backing south; the IMU says the tractor faces north.
        for (int i = 0; i < 3; i++) _service.FuseHeading(180, 0, true, Fast, 0, -i * 0.3, false);

        // Driving forward north now reads as reverse, for as long as it lasts.
        double n = -0.6;
        for (int i = 0; i < 100; i++) { n += 0.3; _service.FuseHeading(0, 0, true, Fast, 0, n, false); }
        Assert.That(_service.IsReverse, Is.True, "the trap");

        _service.ResetDirection();
        double h = 0;
        for (int i = 0; i < 6; i++) { n += 0.3; h = _service.FuseHeading(0, 0, true, Fast, 0, n, false); }

        Assert.That(_service.IsReverse, Is.False);
        Assert.That(OffNorth(h), Is.LessThan(1e-6), "learned again from forward travel");
    }

    [Test]
    public void ResetDirection_HoldsTheHeading_UntilTheNewOneIsLearned()
    {
        DriveNorth(6);   // no IMU: heading north from the fixes

        _service.ResetDirection();
        double held = _service.FuseHeading(90, 0, false, Fast, 0, 1.5, false);   // same place: nothing to learn from yet
        Assert.That(held, Is.EqualTo(0).Within(1e-6), "not the sentence heading, which would swing the tractor");

        double h = 0;
        for (int i = 1; i <= 4; i++) h = _service.FuseHeading(90, 0, false, Fast, i * 0.3, 1.5, false);
        Assert.That(h, Is.EqualTo(90).Within(1e-6), "east, from the new travel");
    }

    [Test]
    public void ResetDirection_IsIgnored_WhileTheHeadingComesFromTheDualAntenna()
    {
        ConfigurationStore.Instance.Connections.IsDualGps = true;
        _service.FuseHeading(30, 0, false, Fast, 0, 0, true);

        _service.ResetDirection();
        double h = _service.FuseHeading(30, 0, false, Fast, 0, 0.3, true);

        Assert.That(h, Is.EqualTo(30).Within(1e-9));
        Assert.That(_service.IsReverse, Is.False);
    }
}
