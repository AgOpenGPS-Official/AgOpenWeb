// AgValoniaGPS
// Copyright (C) 2024-2025 AgValoniaGPS Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System;
using System.Threading;
using AgValoniaGPS.Services.Interfaces;

namespace AgValoniaGPS.Services.Threading;

/// <summary>
/// Real firing <see cref="IUiTimer"/> backed by <see cref="System.Threading.Timer"/>,
/// with NO Avalonia dependency — for a headless host (the cab PC) where the VM's
/// timers (autosave, status rotation, etc.) must actually run but there is no
/// Avalonia dispatcher. Ticks fire on a thread-pool thread; a headless host that
/// needs them marshalled routes through <see cref="IUiDispatcher"/>.
/// See Plans/CONFIG_STATE_AUDIT.md §11.3.
/// </summary>
public sealed class ThreadingUiTimer : IUiTimer, IDisposable
{
    private readonly Timer _timer;
    private TimeSpan _interval = TimeSpan.FromSeconds(1);

    public ThreadingUiTimer()
    {
        _timer = new Timer(_ => Tick?.Invoke(this, EventArgs.Empty), null,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public TimeSpan Interval
    {
        get => _interval;
        set { _interval = value; if (IsEnabled) _timer.Change(value, value); }
    }

    public bool IsEnabled { get; private set; }
    public event EventHandler? Tick;

    public void Start() { IsEnabled = true; _timer.Change(_interval, _interval); }
    public void Stop() { IsEnabled = false; _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan); }
    public void Dispose() => _timer.Dispose();
}

public sealed class ThreadingUiTimerFactory : IUiTimerFactory
{
    public IUiTimer Create() => new ThreadingUiTimer();
}
