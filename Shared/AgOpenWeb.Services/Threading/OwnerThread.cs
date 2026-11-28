// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Diagnostics;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.Services.Threading;

/// <summary>
/// A service's state written only on the thread that owns it (Plans/MAINVIEWMODEL_REFACTOR_PLAN.md,
/// migration guard 4). In debug builds, the unit tests and a debug run, a write from another
/// thread throws, so the tests fail where it happens; in a release build the check is not
/// compiled in (never on the control tick or the GPS cycle).
/// </summary>
public static class OwnerThread
{
    [Conditional("DEBUG")]
    public static void Assert(IUiDispatcher owner, string what)
    {
        if (!owner.CheckAccess())
            throw new InvalidOperationException($"{what} written off the host loop, the thread that owns it");
    }
}
