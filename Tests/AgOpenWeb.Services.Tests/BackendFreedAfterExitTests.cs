// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Runtime.CompilerServices;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.State;
using AgOpenWeb.Services.Threading;
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// A stopped backend lets go of everything it set up, as the process may outlive it (Android:
/// Exit stops the backend, reopening starts another in the same process). Each of these held the
/// whole backend after it stopped in the working day harness (BACKEND-NOT-FREED-AFTER-EXIT, 92 MB).
/// </summary>
[TestFixture]
[NonParallelizable] // the statics are the process's
public class BackendFreedAfterExitTests
{
    [Test]
    public void The_host_loops_timers_stop_with_it()
    {
        var handler = StartTimerOnLoopAndStopIt();
        for (int i = 0; i < 3 && handler.IsAlive; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        Assert.That(handler.IsAlive, Is.False, "a timer of the stopped loop still holds its Tick handler (MainViewModel's)");
    }

    [Test]
    public void The_backends_state_statics_let_go_when_it_stops()
    {
        var lease = new BackendStateLease();
        Assert.That(ConfigurationStore.Instance, Is.SameAs(lease.Store));
        Assert.That(ApplicationState.Instance, Is.SameAs(lease.State));
        Assert.That(PersistentAppState.Instance, Is.SameAs(lease.Persistent));

        lease.Dispose();

        Assert.That(ConfigurationStore.Instance, Is.Not.SameAs(lease.Store));
        Assert.That(ApplicationState.Instance, Is.Not.SameAs(lease.State));
        Assert.That(PersistentAppState.Instance, Is.Not.SameAs(lease.Persistent));
    }

    [Test]
    public void A_later_backends_state_is_not_let_go_by_an_earlier_ones_stop()
    {
        var first = new BackendStateLease();
        using var second = new BackendStateLease();
        first.Dispose();
        Assert.That(ConfigurationStore.Instance, Is.SameAs(second.Store));
        Assert.That(ApplicationState.Instance, Is.SameAs(second.State));
    }

    // Out of line, so no local of the test keeps the handler's target.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference StartTimerOnLoopAndStopIt()
    {
        var loop = new HostLoopDispatcher();
        var target = new object();
        var timer = loop.Create();
        timer.Interval = TimeSpan.FromMilliseconds(50);
        timer.Tick += (_, _) => GC.KeepAlive(target);   // a handler on the backend, as MainViewModel's
        timer.Start();
        loop.Dispose();                                  // the backend stops
        return new WeakReference(target);
    }
}
