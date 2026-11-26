// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Threading;
using System.Threading.Tasks;
using AgOpenWeb.ViewModels.Wizards;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// A step's live readings (the roll, the WAS, the wheel angle) are set from the module's data
/// thread, while the host loop enters the step again (which clears what it changed) and asks
/// what it changed. The sets behind that were plain HashSets: changed from both threads at once
/// they threw "Operations that change non-concurrent collections must have exclusive access",
/// and the steer wizard scenarios stopped in the Roll step (a run on 25 Nov 2026).
/// </summary>
[TestFixture]
public class WizardStepTouchedThreadTests
{
    private sealed class LiveStep : WizardStepViewModel
    {
        public override string Title => "Live";
        public override string Description => "";

        private double _live;
        public double Live
        {
            get => _live;
            set => SetProperty(ref _live, value);
        }

        /// <summary>A property changed under another name, as a step with many live fields raises them.</summary>
        public void Raise(string name) => OnPropertyChanged(name);

        public bool LiveTouched => Touched(nameof(Live));
        public bool LiveTouchedInWizard => TouchedInWizard(nameof(Live));
    }

    [Test]
    public void Readings_from_the_module_thread_while_the_host_loop_enters_and_asks_do_not_throw()
    {
        var step = new LiveStep();
        using var ready = new Barrier(2); // both threads running before the clock starts
        var run = TimeSpan.FromSeconds(2);
        Exception? failure = null;
        long moduleSets = 0, hostVisits = 0;
        bool stop = false;

        // Many names, so the set grows and is rehashed while the other thread reads or clears it.
        var module = Task.Run(() =>
        {
            ready.SignalAndWait();
            int i = 0;
            try
            {
                while (!Volatile.Read(ref stop))
                {
                    step.Live = ++i;
                    step.Raise("Reading" + (i % 512));
                    moduleSets++;
                }
            }
            catch (Exception ex) { failure ??= ex; Volatile.Write(ref stop, true); }
        });

        ready.SignalAndWait();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            while (!Volatile.Read(ref stop) && clock.Elapsed < run)
            {
                step.IsActive = true;
                _ = step.LiveTouched;
                step.IsActive = false;
                hostVisits++;
            }
        }
        catch (Exception ex) { failure ??= ex; }
        Volatile.Write(ref stop, true);
        module.Wait();

        Assert.That(failure, Is.Null, failure?.ToString());
        // Both sides really ran side by side, so the test cannot pass without trying the race.
        Assert.That(moduleSets, Is.GreaterThan(1000), "module thread readings");
        Assert.That(hostVisits, Is.GreaterThan(100), "host loop visits");
        // The tracking itself still works: what the module thread set is counted for the wizard.
        Assert.That(step.LiveTouchedInWizard, Is.True);
    }

    [Test]
    public void A_reading_set_from_another_thread_counts_as_changed_by_the_step()
    {
        var step = new LiveStep();
        step.IsActive = true;
        Assert.That(step.LiveTouched, Is.False, "entering clears what the step changed");

        Task.Run(() => step.Live = 1.5).Wait();

        Assert.That(step.LiveTouched, Is.True);
        Assert.That(step.LiveTouchedInWizard, Is.True);
    }
}
