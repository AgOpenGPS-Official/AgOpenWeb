// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;

namespace AgOpenWeb.Services.Interfaces;

/// <summary>
/// Fixed-rate host control loop. Runs at a target frequency
/// (default 100 Hz, matching the firmware autosteer cadence) on a
/// dedicated thread. Each tick raises <see cref="Ticked"/> with the
/// timestamp of the tick; subscribers (section control, autosteer,
/// guidance) do their work in response.
///
/// Production impl uses <c>PeriodicTimer</c>; test impl is driven
/// manually so unit tests can advance virtual time deterministically.
/// </summary>
public interface ISteerMachineLoopService
{
    /// <summary>
    /// Begin ticking. No-op if already running.
    /// </summary>
    void Start();

    /// <summary>
    /// Stop ticking. No-op if already stopped. Subscribers may still
    /// see one tick already in flight.
    /// </summary>
    void Stop();

    bool IsRunning { get; }

    /// <summary>
    /// Target tick frequency in Hz. 100 Hz by default.
    /// </summary>
    double FrequencyHz { get; }

    /// <summary>
    /// Manually fire a tick. Test hook only — production timer drives
    /// this internally.
    /// </summary>
    /// <param name="timestampTicks">
    /// Timestamp to publish with the tick. Production passes
    /// <c>Clock.Current.GetTimestamp()</c>.
    /// </param>
    void Tick(long timestampTicks);

    /// <summary>
    /// Raised on every tick. The argument is the tick timestamp from
    /// <c>Clock.Current</c>. Subscribers run synchronously on the
    /// loop thread.
    /// </summary>
    event Action<long>? Ticked;
}
