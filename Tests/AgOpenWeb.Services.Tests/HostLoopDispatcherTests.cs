// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Threading;
using System.Threading.Tasks;
using AgOpenWeb.Services.Threading;
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// The host loop stands in for the UI thread the view model was written for, and a UI
/// thread brings its awaits back to itself. The loop's thread had no
/// <see cref="SynchronizationContext"/>, so after the first <c>await</c> in a field open,
/// a field close or a job start the rest ran on a thread-pool thread and set the view
/// model's state from there while the GPS cycle and the web server read it.
/// </summary>
[TestFixture]
public class HostLoopDispatcherTests
{
    [Test]
    public async Task An_await_started_on_the_loop_resumes_on_the_loop()
    {
        using var loop = new HostLoopDispatcher();
        var done = new TaskCompletionSource<(bool before, bool afterDelay, bool afterRun)>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        loop.Post(async () =>
        {
            try
            {
                bool before = loop.CheckAccess();
                await Task.Delay(20);
                bool afterDelay = loop.CheckAccess();
                await Task.Run(() => Thread.Sleep(5));
                bool afterRun = loop.CheckAccess();
                done.SetResult((before, afterDelay, afterRun));
            }
            catch (Exception e) { done.SetException(e); }
        });

        var r = await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Multiple(() =>
        {
            Assert.That(r.before, Is.True, "the work starts on the loop");
            Assert.That(r.afterDelay, Is.True, "after Task.Delay it is back on the loop");
            Assert.That(r.afterRun, Is.True, "after Task.Run it is back on the loop");
        });
    }

    [Test]
    public async Task A_continuation_after_dispose_is_dropped_not_thrown()
    {
        var loop = new HostLoopDispatcher();
        var seen = new TaskCompletionSource<SynchronizationContext?>(TaskCreationOptions.RunContinuationsAsynchronously);
        loop.Post(() => seen.SetResult(SynchronizationContext.Current));
        var context = await seen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(context, Is.Not.Null, "the loop thread has a synchronization context");

        loop.Dispose();

        Assert.DoesNotThrow(() => context!.Post(_ => { }, null));
    }
}
