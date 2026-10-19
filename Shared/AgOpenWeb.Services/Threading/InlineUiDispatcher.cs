// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Threading.Tasks;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.Services.Threading;

/// <summary>
/// Framework-free <see cref="IUiDispatcher"/> that runs callbacks synchronously
/// on the calling thread. Intended for unit tests and headless hosts that have
/// no separate UI thread — there is nothing to marshal to, so everything is
/// "already on the UI thread". See Plans/CONFIG_STATE_AUDIT.md §11.
/// </summary>
public sealed class InlineUiDispatcher : IUiDispatcher
{
    public bool CheckAccess() => true;

    public void Post(Action action, UiDispatcherPriority priority = UiDispatcherPriority.Default)
        => action();

    public Task InvokeAsync(Action action, UiDispatcherPriority priority = UiDispatcherPriority.Default)
    {
        action();
        return Task.CompletedTask;
    }
}
