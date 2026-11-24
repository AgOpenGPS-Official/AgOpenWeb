// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using AgOpenWeb.Services.AutoSteer;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.Services.Isobus;

/// <summary>
/// Sends the tractor's speed, distance and guidance line deviation to the ISOBUS task
/// controller in PGN 242, as AgOpenGPS does (#921). AOG-TaskController's tractor ECU puts
/// the speed and distance on the bus for implements that take their speed from the tractor;
/// PGN 242 is its only source, so without it such an implement applies nothing while its
/// sections work.
/// <para>
/// What AgOpenGPS sends, each fix (Position.designer.cs, CISOBUS.cs): speed in mm/s, negative
/// in reverse (DDI 397); the trip distance in mm (DDI 597); the distance off the line in mm
/// times 100 (DDI 513), which AOG-TaskController divides by 1000 into NMEA 2000's 0.01 m.
/// </para>
/// <para>
/// One difference from AgOpenGPS: it sends a value only when it changes, so a task
/// controller started while the speed is steady reads 0 until the speed moves. Here speed
/// and distance go out with every fix while the task controller is heard, at most every
/// <see cref="MinIntervalMs"/>. The deviation goes with them while a line is guided on;
/// AgOpenGPS sends its "no line" number (32000) there, which reads as 32 m off a line.
/// </para>
/// To the task controller's own address (<see cref="IUdpCommunicationService.SendToTaskController"/>):
/// it does not hear the localhost copy of the module PGNs. One buffer, the send being synchronous.
/// </summary>
public sealed class IsobusProcessDataSender
{
    /// <summary>A 10 Hz receiver's every fix, a 20 Hz receiver's every other one: AgOpenGPS's 100 ms.</summary>
    public const int MinIntervalMs = 80;

    private readonly IUdpCommunicationService _udp;
    private readonly byte[] _buffer = new byte[PgnBuilder.ISOBUS_PROCESS_DATA_PGN_SIZE];
    private long _sentAt = long.MinValue;

    public IsobusProcessDataSender(IUdpCommunicationService udp) => _udp = udp;

    /// <summary>Send this fix's values. Nothing while no task controller is heard.</summary>
    /// <param name="speedMetersPerSecond">Ground speed; its sign is not used.</param>
    /// <param name="distanceMeters">The trip distance (the field data card's).</param>
    /// <param name="distanceOffLineMm">The distance off the guided line in mm, as PGN 254
    /// carries it; null while no line is guided on.</param>
    public void Send(double speedMetersPerSecond, bool reverse, double distanceMeters, int? distanceOffLineMm)
        => Send(speedMetersPerSecond, reverse, distanceMeters, distanceOffLineMm, Environment.TickCount64);

    /// <summary>As <see cref="Send(double, bool, double, int?)"/>, with the current time given (tests).</summary>
    public void Send(double speedMetersPerSecond, bool reverse, double distanceMeters, int? distanceOffLineMm, long nowMs)
    {
        if (!_udp.IsTaskControllerHeard) return;
        if (_sentAt != long.MinValue && nowMs - _sentAt < MinIntervalMs) return;
        _sentAt = nowMs;

        int speed = ToInt(Math.Abs(speedMetersPerSecond) * 1000.0);
        _udp.SendToTaskController(PgnBuilder.WriteIsobusProcessDataPgn(_buffer, PgnBuilder.DDI_ACTUAL_SPEED, reverse ? -speed : speed));
        _udp.SendToTaskController(PgnBuilder.WriteIsobusProcessDataPgn(_buffer, PgnBuilder.DDI_TOTAL_DISTANCE, ToInt(Math.Max(0, distanceMeters) * 1000.0)));
        if (distanceOffLineMm is { } off)
            _udp.SendToTaskController(PgnBuilder.WriteIsobusProcessDataPgn(_buffer, PgnBuilder.DDI_GUIDANCE_LINE_DEVIATION, off * 100));
    }

    // AgOpenGPS casts to int (towards zero); a value past int's range is held at its end.
    private static int ToInt(double value) =>
        double.IsNaN(value) ? 0 : value >= int.MaxValue ? int.MaxValue : value <= int.MinValue ? int.MinValue : (int)value;
}
