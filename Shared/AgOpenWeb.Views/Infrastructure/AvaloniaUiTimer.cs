// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System;
using Avalonia.Threading;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.Views.Infrastructure;

/// <summary>
/// Avalonia-backed <see cref="IUiTimer"/> wrapping <see cref="DispatcherTimer"/>
/// — fires on the UI thread, identical to the VM's previous direct use.
/// Registered by each platform's DI alongside <see cref="AvaloniaUiDispatcher"/>.
/// See Plans/CONFIG_STATE_AUDIT.md §11.3.
/// </summary>
public sealed class AvaloniaUiTimer : IUiTimer
{
    private readonly DispatcherTimer _timer = new();

    public AvaloniaUiTimer()
    {
        _timer.Tick += (_, _) => Tick?.Invoke(this, EventArgs.Empty);
    }

    public TimeSpan Interval
    {
        get => _timer.Interval;
        set => _timer.Interval = value;
    }

    public bool IsEnabled => _timer.IsEnabled;
    public event EventHandler? Tick;

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();
}

public sealed class AvaloniaUiTimerFactory : IUiTimerFactory
{
    public IUiTimer Create() => new AvaloniaUiTimer();
}
