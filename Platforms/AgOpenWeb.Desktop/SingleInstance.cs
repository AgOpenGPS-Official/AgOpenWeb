// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Threading;

namespace AgOpenWeb.Desktop;

/// <summary>
/// A machine-wide named mutex held for the life of the process. "Global\" makes it one per
/// computer on Windows (the service and a desktop session see the same one); .NET implements
/// named mutexes on macOS and Linux too. A copy that crashed leaves it abandoned, which the
/// next start takes over.
/// </summary>
static class SingleInstance
{
    private const string Name = @"Global\AgOpenWeb.Desktop";

    /// <summary>The held mutex, or null when another copy holds it.</summary>
    public static Mutex? TryAcquire()
    {
        var m = new Mutex(false, Name);
        try
        {
            if (m.WaitOne(0)) return m;
        }
        catch (AbandonedMutexException)
        {
            return m; // the last holder died without releasing; it's ours now
        }
        m.Dispose();
        return null;
    }
}
