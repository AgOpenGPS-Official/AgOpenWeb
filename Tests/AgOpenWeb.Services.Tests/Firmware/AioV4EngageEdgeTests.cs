// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Services.AutoSteer;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Track;
using NSubstitute;

namespace AgOpenWeb.Services.Tests.Firmware;

/// <summary>
/// Does the AgOpenGPS reference steer firmware (Boards, TeensyModules/AIO v4 Firmware/
/// AIO_v4_Firmware/Autosteer.ino) see AutoSteer engaged from the tablet, given the PGN 254
/// stream AgOpenWeb sends?
///
/// The firmware takes the engage as an edge: every PGN 254 sets
/// <c>prevGuidanceStatus = guidanceStatus; guidanceStatus = data[7];
/// guidanceStatusChanged = (guidanceStatus != prevGuidanceStatus)</c> (ReceiveUdp, run on every
/// pass of the main loop), and the control block, every LOOP_TIME = 25 ms, engages on
/// <c>guidanceStatusChanged &amp;&amp; guidanceStatus == 1</c> without clearing the flag. So the
/// flag lives until the next packet. AgOpenGPS sends PGN 254 once per GPS fix (10 Hz): the flag
/// lives 100 ms and every 25 ms pass sees it. AgOpenWeb sends it on every 100 Hz control tick
/// (#313), for fresh steer angles between fixes: the packet after the engage cleared the flag
/// 10 ms later and the firmware missed about 60 % of engages (20 of 50 loop phases engaged). After
/// a status change AgOpenWeb now holds the unchanged packets for 30 ms
/// (<see cref="AutoSteerService.SteerStatusHoldTicks"/>), longer than one 25 ms pass.
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class AioV4EngageEdgeTests
{
    /// <summary>AIO v4 Autosteer.ino, the parts that decide the tablet engage (no steer switch).</summary>
    private sealed class AioV4
    {
        private byte _guidanceStatus, _prevGuidanceStatus;
        private bool _guidanceStatusChanged;
        private int _steerSwitch = 1, _switchCounter;

        /// <summary>ReceiveUdp, PGN 254.</summary>
        public void OnPgn254(byte[] data)
        {
            _prevGuidanceStatus = _guidanceStatus;
            _guidanceStatus = data[7];
            _guidanceStatusChanged = _guidanceStatus != _prevGuidanceStatus;
        }

        /// <summary>The LOOP_TIME block of autosteerLoop.</summary>
        public void ControlPass()
        {
            // 2 Has tablet button been pressed?
            if (_guidanceStatusChanged && _guidanceStatus == 1) _steerSwitch = 0;
            // If AOG has stopped steering, wait then turn off steerswitch ready for next engage.
            if (_steerSwitch == 0 && _guidanceStatus == 0)
            {
                if (_switchCounter++ > 30) _steerSwitch = 1;
            }
            else _switchCounter = 0;
        }

        public bool Steering => _steerSwitch == 0;
    }

    private const double LoopTimeMs = 25.0; // AIO v4 LOOP_TIME
    private const int Phases = 50;          // firmware loop phase against our ticks, 0.5 ms apart

    /// <summary>
    /// The PGN 254s AgOpenWeb's AutoSteerService sends, one per 10 ms control tick, with the
    /// operator engaging AutoSteer just before tick <paramref name="engageAtTick"/>: (time in
    /// ms, packet).
    /// </summary>
    private static List<(double Ms, byte[] Packet)> AgOpenWebStream(int ticks, int engageAtTick, int disengageAtTick = -1)
    {
        var config = new ConfigurationStore();
        ConfigurationStore.SetInstance(config);
        var udp = Substitute.For<IUdpCommunicationService>();
        var gps = new GpsService();
        var svc = new AutoSteerService(new TrackGuidanceService(), udp, gps, config);
        var stream = new List<(double, byte[])>();
        double now = 0;
        udp.When(u => u.SendToModules(Arg.Any<byte[]>())).Do(ci =>
        {
            var b = ci.ArgAt<byte[]>(0);
            if (b.Length > 7 && b[3] == PgnBuilder.PGN_AUTOSTEER) stream.Add((now, (byte[])b.Clone()));
        });
        svc.Start();
        try
        {
            for (int k = 0; k < ticks; k++)
            {
                now = k * 10.0;
                if (k == engageAtTick) svc.Engage();
                if (k == disengageAtTick) svc.Disengage();
                svc.UpdateGuidanceResults(0, 0); // guidance arriving, as from the GPS cycle
                svc.SendPgnsForControlTick();
            }
        }
        finally { svc.Stop(); gps.Stop(); }
        return stream;
    }

    /// <summary>The firmware fed <paramref name="stream"/>, its control passes at phase
    /// <paramref name="phaseMs"/> + n × 25 ms: steering at the end?</summary>
    private static bool Engages(IReadOnlyList<(double Ms, byte[] Packet)> stream, double phaseMs, double endMs)
    {
        var fw = new AioV4();
        int i = 0;
        for (double pass = phaseMs; pass <= endMs; pass += LoopTimeMs)
        {
            // Packets that arrived before this pass, in order (ReceiveUdp runs far more often).
            while (i < stream.Count && stream[i].Ms < pass) fw.OnPgn254(stream[i++].Packet);
            fw.ControlPass();
        }
        return fw.Steering;
    }

    /// <summary>The model itself: AgOpenGPS's 10 Hz stream (one PGN 254 per fix) engages at
    /// every phase, as AIO v4 boards do with AgOpenGPS.</summary>
    [Test]
    public void At_AgOpenGPS_10Hz_the_reference_firmware_sees_every_engage()
    {
        var stream = new List<(double, byte[])>();
        for (int k = 0; k < 20; k++)
            stream.Add((k * 100.0, new byte[] { 0x80, 0x81, 0x7F, 0xFE, 8, 0, 0, (byte)(k >= 5 ? 1 : 0), 0, 0, 0, 0, 0 }));
        int engaged = Enumerable.Range(0, Phases).Count(p => Engages(stream, p * LoopTimeMs / Phases, 1900));
        Assert.That(engaged, Is.EqualTo(Phases));
    }

    [Test]
    public void At_AgOpenWebs_100Hz_the_reference_firmware_sees_every_engage()
    {
        var stream = AgOpenWebStream(ticks: 100, engageAtTick: 20);
        Assume.That(stream.Count, Is.EqualTo(100 - AutoSteerService.SteerStatusHoldTicks), "one PGN 254 per control tick, but the few held after the engage");
        Assume.That(stream.Last().Packet[7], Is.EqualTo(1), "engaged at the end");
        var missed = Enumerable.Range(0, Phases).Where(p => !Engages(stream, p * LoopTimeMs / Phases, 990))
            .Select(p => p * LoopTimeMs / Phases).ToList();
        TestContext.Out.WriteLine($"engaged at {Phases - missed.Count} of {Phases} firmware loop phases");
        Assert.That(missed, Is.Empty,
            $"the AIO v4 firmware missed the engage at {missed.Count} of {Phases} loop phases: the screen says steering, the motor never runs");
    }

    /// <summary>Switching off is never held: the tick after an engage, a disengage goes out at
    /// once with status 0, inside the hold that follows the engage.</summary>
    [Test]
    public void A_disengage_goes_out_at_once_even_inside_the_hold()
    {
        var stream = AgOpenWebStream(ticks: 30, engageAtTick: 20, disengageAtTick: 21);
        var on = stream.First(p => p.Packet[7] == 1);
        var off = stream.First(p => p.Ms > on.Ms && p.Packet[7] == 0);
        Assert.Multiple(() =>
        {
            Assert.That(on.Ms, Is.EqualTo(200), "the engage goes out on its tick");
            Assert.That(off.Ms, Is.EqualTo(210), "the disengage goes out on the next tick, not after the hold");
        });
    }
}
