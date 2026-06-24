// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.Services.Threading;

/// <summary>
/// Framework-free <see cref="IUiTimer"/> that never fires on its own — exactly
/// the behaviour the VM's <c>DispatcherTimer</c>s have in tests (constructed but
/// not pumped). Use in unit tests so VM construction is deterministic and timer
/// callbacks don't fire spuriously; <see cref="Fire"/> lets a test trigger a
/// tick explicitly. See Plans/CONFIG_STATE_AUDIT.md §11.3.
/// </summary>
public sealed class ManualUiTimer : IUiTimer
{
    public TimeSpan Interval { get; set; }
    public bool IsEnabled { get; private set; }
    public event EventHandler? Tick;

    public void Start() => IsEnabled = true;
    public void Stop() => IsEnabled = false;

    /// <summary>Test hook — raise a single tick.</summary>
    public void Fire() => Tick?.Invoke(this, EventArgs.Empty);
}

public sealed class ManualUiTimerFactory : IUiTimerFactory
{
    public IUiTimer Create() => new ManualUiTimer();
}
