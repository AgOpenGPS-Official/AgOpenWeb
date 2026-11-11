// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Runtime.CompilerServices;
using AgOpenWeb.Models.Configuration;
using NUnit.Framework;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// A stopped backend can be collected. Each backend's configuration store is its own
/// (<see cref="ConfigurationStoreLease"/>, as the heads register it), and the static seam lets go
/// of it when the backend stops. The static used to be one store for the whole process, and
/// MainViewModel's handlers on it kept the whole backend alive after Exit: on Android, where the
/// process outlives the backend, every Exit and reopen kept the backend before it (the working
/// day harness's BACKEND-NOT-FREED-AFTER-EXIT, 92 MB).
/// </summary>
[TestFixture]
[NonParallelizable] // ConfigurationStore is a singleton
public class BackendFreedAfterExitTests
{
    [Test]
    public void A_stopped_backends_view_model_is_collected()
    {
        var vm = StartAndStopBackend();
        for (int i = 0; i < 3 && vm.IsAlive; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        Assert.That(vm.IsAlive, Is.False, "the stopped backend's MainViewModel is still reachable");
    }

    [Test]
    public void A_new_backend_gets_a_store_of_its_own()
    {
        using var first = new ConfigurationStoreLease();
        Assert.That(ConfigurationStore.Instance, Is.SameAs(first.Store));
        using (var second = new ConfigurationStoreLease())
            Assert.That(ConfigurationStore.Instance, Is.SameAs(second.Store).And.Not.SameAs(first.Store));
        Assert.That(ConfigurationStore.Instance, Is.Not.SameAs(first.Store), "a stopped backend's store is not handed out again");
    }

    // Out of line, so no local of the test method keeps the backend.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference StartAndStopBackend()
    {
        var lease = new ConfigurationStoreLease();   // what a head's container makes for its backend
        var vm = new MainViewModelBuilder().Build();
        var weak = new WeakReference(vm);
        lease.Dispose();                             // the container disposes it when the backend stops
        return weak;
    }
}
