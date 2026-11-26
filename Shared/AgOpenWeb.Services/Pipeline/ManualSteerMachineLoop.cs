// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.Services.Pipeline;

/// <summary>
/// Test implementation of <see cref="ISteerMachineLoopService"/>. No timer;
/// tests call <see cref="Tick"/> directly to advance the loop.
/// </summary>
public sealed class ManualSteerMachineLoop : ISteerMachineLoopService
{
    public ManualSteerMachineLoop(double frequencyHz = 100.0)
    {
        FrequencyHz = frequencyHz;
    }

    public bool IsRunning { get; private set; }

    public double FrequencyHz { get; }

    public event Action<long>? Ticked;

    public void Start() => IsRunning = true;

    public void Stop() => IsRunning = false;

    public void Tick(long timestampTicks)
    {
        if (!IsRunning)
            return;
        Ticked?.Invoke(timestampTicks);
    }
}
