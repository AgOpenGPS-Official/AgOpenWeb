// AgValoniaGPS
// Copyright (C) 2024-2025 AgValoniaGPS Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System;

namespace AgValoniaGPS.Services.Interfaces;

/// <summary>
/// Abstraction over a periodic UI-thread timer. Replaces direct
/// <c>Avalonia.Threading.DispatcherTimer</c> use in the ViewModel layer so the
/// VM depends on an injected service rather than an ambient framework type —
/// the same de-ambient pass as <see cref="IUiDispatcher"/>. This is what lets a
/// headless host (no Avalonia dispatcher) run the VM's timers.
/// See Plans/CONFIG_STATE_AUDIT.md §11.3.
/// </summary>
public interface IUiTimer
{
    /// <summary>Tick period. May be set before or after <see cref="Start"/>.</summary>
    TimeSpan Interval { get; set; }

    /// <summary>True while the timer is running.</summary>
    bool IsEnabled { get; }

    /// <summary>Raised every <see cref="Interval"/> while running.</summary>
    event EventHandler Tick;

    void Start();
    void Stop();
}

/// <summary>Creates <see cref="IUiTimer"/> instances. Injected into the VM.</summary>
public interface IUiTimerFactory
{
    IUiTimer Create();
}
