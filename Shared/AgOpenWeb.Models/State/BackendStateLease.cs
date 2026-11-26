// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using AgOpenWeb.Models.Configuration;

namespace AgOpenWeb.Models.State;

/// <summary>
/// The state a backend shares through statics (<see cref="ConfigurationStore.Instance"/>,
/// <see cref="ApplicationState.Instance"/>, <see cref="PersistentAppState.Instance"/>), let go
/// when the backend's service container disposes the lease. Every head registers the three
/// through it. The statics used to outlive the backend, and its handlers on them
/// (MainViewModel's on Guidance, Display, Field…) kept the whole backend alive after it stopped:
/// on Android, where the process outlives the backend, every Exit and reopen kept the backend
/// before it, its handlers still reacting (the working day harness's BACKEND-NOT-FREED-AFTER-EXIT).
/// The configuration store is the backend's own; the persistent state is the one
/// <see cref="PersistentAppState.Instance"/> holds while the backend runs.
/// </summary>
public sealed class BackendStateLease : IDisposable
{
    public ConfigurationStore Store { get; } = new();
    public ApplicationState State { get; }
    public PersistentAppState Persistent { get; }

    public BackendStateLease()
    {
        ConfigurationStore.SetInstance(Store);
        State = new ApplicationState();         // sets ApplicationState.Instance
        Persistent = PersistentAppState.Instance;
    }

    public void Dispose()
    {
        ConfigurationStore.Release(Store);
        ApplicationState.Release(State);
        PersistentAppState.Release(Persistent);
    }
}
