// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Threading;
using AgOpenWeb.Services.Network;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// #385: one watcher for network changes. Subscribers run in a fixed order (modules,
/// corrections, uploads) whatever order they subscribed in, a burst of changes gives one
/// check once it settles, and the heartbeat checks with no change at all.
/// </summary>
[TestFixture]
public class NetworkWatcherTests
{
    [Test]
    public void Subscribers_RunInStageOrder()
    {
        using var w = new NetworkWatcher(null, systemEvents: false, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        var order = new List<string>();
        w.Subscribe(NetworkStage.Uploads, _ => order.Add("uploads"));
        w.Subscribe(NetworkStage.Modules, _ => order.Add("modules"));
        w.Subscribe(NetworkStage.Corrections, _ => order.Add("corrections"));

        w.Check(NetworkCheckReason.AddressChanged);

        Assert.That(order, Is.EqualTo(new[] { "modules", "corrections", "uploads" }));
    }

    [Test]
    public void AFailingSubscriber_DoesNotStopTheOthers()
    {
        using var w = new NetworkWatcher(null, systemEvents: false, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        bool uploads = false;
        w.Subscribe(NetworkStage.Modules, _ => throw new InvalidOperationException("boom"));
        w.Subscribe(NetworkStage.Uploads, _ => uploads = true);

        w.Check(NetworkCheckReason.Heartbeat);

        Assert.That(uploads, Is.True);
    }

    [Test]
    public void ABurstOfChanges_GivesOneCheck_OnceSettled()
    {
        using var w = new NetworkWatcher(null, systemEvents: false, TimeSpan.FromMilliseconds(300), Timeout.InfiniteTimeSpan);
        int checks = 0;
        w.Subscribe(NetworkStage.Modules, r => { if (r == NetworkCheckReason.AddressChanged) Interlocked.Increment(ref checks); });

        for (int i = 0; i < 5; i++) { w.NotifyChanged(); Thread.Sleep(50); }
        Assert.That(Volatile.Read(ref checks), Is.EqualTo(0), "still settling");

        Thread.Sleep(600);
        Assert.That(Volatile.Read(ref checks), Is.EqualTo(1));
    }

    [Test]
    public void TheHeartbeat_ChecksWithoutAnyChange()
    {
        using var w = new NetworkWatcher(null, systemEvents: false, TimeSpan.Zero, TimeSpan.FromMilliseconds(100));
        int beats = 0;
        w.Subscribe(NetworkStage.Uploads, r => { if (r == NetworkCheckReason.Heartbeat) Interlocked.Increment(ref beats); });

        Thread.Sleep(450);

        Assert.That(Volatile.Read(ref beats), Is.GreaterThanOrEqualTo(2));
    }
}
