// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.AutoSteer;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// Checklist 9.3, 9.4, 9.6 and 6.8: PGN 254, 252, 251 and 239 go out byte for byte as AgOpenGPS
/// sends them. The same values go to <see cref="PgnBuilder"/> and to <see cref="AgOpenGpsPgnOracle"/>
/// (the snapshot's fill code) and the whole frames are compared, CRC included.
/// </summary>
[TestFixture]
public class PgnFramesMatchAgOpenGpsTests
{
    private static string Hex(byte[] b) => string.Join(" ", b.Select(x => x.ToString("X2")));

    private static void AssertSame(byte[] ours, byte[] theirs, string what)
        => Assert.That(Hex(ours), Is.EqualTo(Hex(theirs)), what);

    // AgOpenGPS: guidanceLineDistanceOff = (short)Math.Round(DistanceFromCurrentLinePivot * 1000.0, AwayFromZero)
    private static short Mm(double xte) => unchecked((short)Math.Round(xte * 1000.0, MidpointRounding.AwayFromZero));

    private static readonly double[] SpeedsKmh = { 0, 0.3, 1.1, 3.7, 7.5, 12.34, 25.5, 40 };
    private static readonly double[] Xtes = { -40, -3.2, -2.54, -0.5, -0.013, -0.01, 0, 0.009, 0.013, 0.5, 2.54, 3.2, 40 };
    private static readonly double[] Angles = { -35.67, -12.345, -0.01, 0, 0.004, 12.345, 35 };
    private static readonly ulong[] Sections = { 0, 0x0001, 0x0080, 0x0100, 0x8181, 0xFFFF };

    [Test]
    public void SteerData_254_IsAgOpenGps()
    {
        var wrong = new List<string>();
        int n = 0;
        foreach (double speed in SpeedsKmh)
            foreach (double xte in Xtes)
                foreach (double angle in Angles)
                    foreach (bool engaged in new[] { false, true })
                        foreach (bool reverse in new[] { false, true })
                            foreach (bool steerInReverse in new[] { false, true })
                                foreach (ulong sections in Sections)
                                {
                                    var state = new VehicleState
                                    {
                                        Speed = speed / 3.6, IsAutoSteerEngaged = engaged,
                                        IsSteerPaused = reverse && !steerInReverse,   // AutoSteerService pauses steering in reverse unless Steer in reverse is on
                                        CrossTrackError = xte, SteerAngle = angle, SectionStates = sections,
                                        GpsValid = true, GuidanceValid = true,
                                    };
                                    var ours = (byte[])PgnBuilder.BuildAutoSteerPgn(ref state).Clone();
                                    var theirs = AgOpenGpsPgnOracle.SteerData(speed, engaged, Mm(xte), (short)(angle * 100),
                                        reverse, steerInReverse, false, 0, (byte)(sections & 0xFF), (byte)((sections >> 8) & 0xFF));
                                    n++;
                                    if (!ours.SequenceEqual(theirs))
                                        wrong.Add($"v {speed} xte {xte} angle {angle} engaged {engaged} rev {reverse} sir {steerInReverse} sc {sections:X4}:\n    ours   {Hex(ours)}\n    AgOpenGPS {Hex(theirs)}");
                                }
        Assert.That(wrong, Is.Empty, $"{wrong.Count} of {n} frames differ:\n" + string.Join("\n", wrong.Take(8)));
    }

    [Test]
    public void SteerData_254_NoGuidanceLine_SendsAgOpenGps32000()
    {
        // The guidance service's 32000 (no usable line) reaches the builder as the cross-track error.
        var state = new VehicleState { Speed = 2, IsAutoSteerEngaged = true, CrossTrackError = 32000, SteerAngle = 0, GpsValid = true, GuidanceValid = true };
        var ours = (byte[])PgnBuilder.BuildAutoSteerPgn(ref state).Clone();
        var theirs = AgOpenGpsPgnOracle.SteerData(7.2, true, 32000, 0, false, false, false, 0, 0, 0);
        AssertSame(ours, theirs, "no line");
        Assert.That(ours[10], Is.EqualTo(255));

        state.CrossTrackError = 0.5; state.GuidanceValid = false;      // nothing computed this cycle
        ours = (byte[])PgnBuilder.BuildAutoSteerPgn(ref state).Clone();
        Assert.That(ours[10], Is.EqualTo(255), "no guidance this cycle");
    }

    [Test]
    public void SteerData_254_FreeDrive_IsAgOpenGps()
    {
        foreach (double angle in Angles)
            foreach (ulong sections in Sections)
            {
                var state = new VehicleState
                {
                    Speed = 1.0, IsInFreeDriveMode = true, FreeDriveSteerAngle = angle, SectionStates = sections,
                    GpsValid = true, GuidanceValid = false,
                };
                var ours = (byte[])PgnBuilder.BuildAutoSteerPgn(ref state).Clone();
                var theirs = AgOpenGpsPgnOracle.SteerData(3.6, false, 0, 0, false, false, true, angle,
                    (byte)(sections & 0xFF), (byte)((sections >> 8) & 0xFF));
                // AgOpenGPS leaves byte 10 as the last steering cycle left it; ours sends 127 (on the line).
                theirs[10] = ours[10] = 127;
                theirs[^1] = ours[^1] = 0;
                AssertSame(ours, theirs, $"free drive at {angle}° with sections {sections:X4}");
            }
    }

    [Test]
    public void MachineData_239_IsAgOpenGps()
    {
        var wrong = new List<string>();
        int n = 0;
        foreach (double speed in SpeedsKmh.Where(s => s < 25.6))
            foreach (bool inTurn in new[] { false, true })
                foreach (byte hydLift in new byte[] { 0, 1, 2 })
                    foreach (byte tram in new byte[] { 0, 1, 2, 3 })
                        foreach (bool outOfBounds in new[] { false, true })
                            foreach (ulong sections in Sections)
                            {
                                var state = new VehicleState { Speed = speed / 3.6, SectionStates = sections, GpsValid = true };
                                var ours = (byte[])PgnBuilder.BuildMachinePgn(ref state,
                                    uturn: inTurn ? (byte)1 : (byte)0, hydLift: hydLift, tram: tram, geoStop: outOfBounds ? (byte)1 : (byte)0).Clone();
                                var theirs = AgOpenGpsPgnOracle.MachineData(inTurn, speed, hydLift, tram, outOfBounds,
                                    (byte)(sections & 0xFF), (byte)((sections >> 8) & 0xFF));
                                n++;
                                if (!ours.SequenceEqual(theirs))
                                    wrong.Add($"v {speed} turn {inTurn} lift {hydLift} tram {tram} oob {outOfBounds} sc {sections:X4}:\n    ours   {Hex(ours)}\n    AgOpenGPS {Hex(theirs)}");
                            }
        Assert.That(wrong, Is.Empty, $"{wrong.Count} of {n} frames differ:\n" + string.Join("\n", wrong.Take(8)));
    }

    [Test]
    public void MachineData_239_SpeedPast25Kmh_IsHeldAt255()
    {
        // The speed byte is km/h × 10. AgOpenGPS casts past 255 unchecked (what comes out is up to
        // the runtime); ours clamps, so a fast transport run reads 25.5 rather than nonsense.
        var state = new VehicleState { Speed = 40 / 3.6, GpsValid = true };
        Assert.That(PgnBuilder.BuildMachinePgn(ref state)[6], Is.EqualTo(255));
    }

    [Test]
    public void SteerSettings_252_IsAgOpenGps()
    {
        var wrong = new List<string>();
        int n = 0;
        foreach (int kp in new[] { 1, 10, 55, 100 })
            foreach (int highPwm in new[] { 50, 120, 235, 255 })
                foreach (int minPwm in new[] { 1, 5, 25, 50 })
                    foreach (double cpd in new[] { 1, 50, 100, 110.9, 255 })
                        foreach (int wasOffset in new[] { -32768, -1000, -1, 0, 1, 255, 256, 1000, 32767 })
                            foreach (int ackermann in new[] { 0, 80, 100, 200 })
                            {
                                var c = new AutoSteerConfig
                                {
                                    ProportionalGain = kp, MaxPwm = highPwm, MinPwm = minPwm, CountsPerDegree = cpd,
                                    WasOffset = wasOffset, Ackermann = ackermann,
                                };
                                var ours = (byte[])PgnBuilder.BuildSteerSettingsPgn(c).Clone();
                                // AgOpenGPS's setting is an int; the CPD test rounds before storing it (#112)
                                var theirs = AgOpenGpsPgnOracle.SteerSettings((int)Math.Round(cpd, MidpointRounding.AwayFromZero), ackermann, wasOffset,
                                    highPwm, AgOpenGpsPgnOracle.LowSteerPWM(highPwm), kp, minPwm);
                                n++;
                                if (!ours.SequenceEqual(theirs))
                                    wrong.Add($"kp {kp} high {highPwm} min {minPwm} cpd {cpd} was {wasOffset} ack {ackermann}:\n    ours   {Hex(ours)}\n    AgOpenGPS {Hex(theirs)}");
                            }
        Assert.That(wrong, Is.Empty, $"{wrong.Count} of {n} frames differ:\n" + string.Join("\n", wrong.Take(8)));
    }

    [Test]
    public void SteerConfig_251_IsAgOpenGps()
    {
        var wrong = new List<string>();
        int n = 0;
        // every set0 / set1 bit on its own, all off, all on, and a few mixes
        var set0s = Enumerable.Range(0, 8).Select(b => 1 << b).Concat(new[] { 0, 0xFF, 0x1D, 0xA6 }).ToArray();
        var set1s = Enumerable.Range(0, 4).Select(b => 1 << b).Concat(new[] { 0, 0x0F, 0x05 }).ToArray();
        foreach (int s0 in set0s)
            foreach (int s1 in set1s)
                foreach (int counts in new[] { 0, 3, 12, 255 })
                    foreach (int sensorPct in new[] { 1, 40, 75, 99 })       // the panel's range
                        foreach (double minSpeed in new[] { 0, 0.5, 2.0, 25.5 })
                        {
                            bool invertWas = (s0 & 1) != 0, invertRelays = (s0 & 2) != 0, invertMotor = (s0 & 4) != 0;
                            bool single = (s0 & 8) != 0, cytron = (s0 & 16) != 0, encoder = (s0 & 128) != 0;
                            int enable = (s0 >> 5) & 3;                         // 0 none, 1 switch, 2 button (3 is not a form state)
                            if (enable == 3) continue;
                            bool danfoss = (s1 & 1) != 0, pressure = (s1 & 2) != 0, current = (s1 & 4) != 0, axisY = (s1 & 8) != 0;

                            var c = new AutoSteerConfig
                            {
                                InvertWas = invertWas, InvertRelays = invertRelays, InvertMotor = invertMotor,
                                AdConverter = single ? 1 : 0, MotorDriver = cytron ? 1 : 0, ExternalEnable = enable, TurnSensorEnabled = encoder,
                                DanfossEnabled = danfoss, PressureSensorEnabled = pressure, CurrentSensorEnabled = current, ImuAxisSwap = axisY ? 1 : 0,
                                TurnSensorCounts = counts, PressureTripPoint = sensorPct, CurrentTripPoint = sensorPct, MinSteerSpeed = minSpeed,
                            };
                            var ours = (byte[])PgnBuilder.BuildSteerConfigPgn(c).Clone();

                            // AgOpenGPS's sensor slider holds the raw 0-255 value; ours holds the % the panel shows (#105)
                            int sensorRaw = (int)Math.Round(sensorPct * 255.0 / 100.0);
                            byte set0 = AgOpenGpsPgnOracle.Setting0(invertWas, invertRelays, invertMotor, single ? "Single" : "Differential",
                                cytron ? "Cytron" : "IBT2", enable == 1 ? "Switch" : enable == 2 ? "Button" : "None", encoder);
                            byte set1 = AgOpenGpsPgnOracle.Setting1(danfoss, pressure, current, axisY ? "Y" : "X");
                            byte pulses = AgOpenGpsPgnOracle.MaxPulseCounts(current, pressure, sensorRaw, counts);
                            var theirs = AgOpenGpsPgnOracle.SteerConfig(set0, set1, pulses, minSpeed, false);
                            n++;
                            if (!ours.SequenceEqual(theirs))
                                wrong.Add($"set0 {s0:X2} set1 {s1:X2} counts {counts} sensor {sensorPct}% min {minSpeed}:\n    ours   {Hex(ours)}\n    AgOpenGPS {Hex(theirs)}");
                        }
        Assert.That(wrong, Is.Empty, $"{wrong.Count} of {n} frames differ:\n" + string.Join("\n", wrong.Take(8)));
    }
}
