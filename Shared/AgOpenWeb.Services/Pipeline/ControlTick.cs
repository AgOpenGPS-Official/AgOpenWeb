// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Threading;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.Services.Pipeline;

/// <summary>
/// The work of one tick of the 100 Hz control loop (#313): reads the latest GPS-anchored pose
/// from the estimator, updates the tool position and the section state machine, hands the
/// section bits to the sender, then sends PGN 254 + PGN 239 so the firmware autosteer task,
/// which also runs at 100 Hz, sees fresh data every cycle. Joined to the loop
/// (<see cref="ISteerMachineLoopService.Ticked"/>) where the services are joined
/// (<c>WebBackend.WireCrossServices</c>), before the loop starts. This gives sub-frame section
/// edge accuracy (~0.05 m at 25 km/h vs ~0.7 m on the prior 10 Hz path) and matches the
/// firmware autosteer cadence so PGNs land fresh every firmware tick.
///
/// <para><b>Thread.</b> <see cref="Run"/> runs on the control loop's thread, one tick at a time.
/// The production loop (<see cref="SteerMachineLoopService"/>) is a <c>PeriodicTimer</c> on the
/// thread pool, so a tick may run on a different pool thread from the one before; what it owns
/// is "the tick", not one thread. Debug and test builds fail a tick entered while another is
/// running (<see cref="Run"/>).</para>
///
/// <para><b>What it reads that another thread writes, and how it crosses:</b></para>
/// <list type="bullet">
/// <item>The pose snapshot: written by the GPS cycle (pipeline thread), swapped whole
/// (<c>PositionEstimator</c>, <c>Interlocked.Exchange</c>); no lock.</item>
/// <item>Section control's field context (boundary, headland line, field open, headland on):
/// published whole by the host loop (<c>SetFieldContext</c>, <c>Volatile.Write</c>); no lock.</item>
/// <item>Reversing and the painting hold: written by the GPS cycle, volatile flags.</item>
/// <item>The section buttons (Off / Auto / On) and the master: set by the host loop
/// (<c>SetSectionState</c>, <c>SetAllSections</c>, <c>MasterState</c>) on the same
/// <c>SectionControlState</c> objects the tick updates, with no lock.</item>
/// <item>The configuration (<c>ConfigurationStore</c>: tool, vehicle, section widths): written by
/// the host loop, read as it is.</item>
/// <item>AutoSteer's vehicle state (<c>_state</c>): written by the GPS cycle and the UDP receive,
/// read without a lock to build the PGNs (a torn field is acted on at the firmware's next loop,
/// <see cref="IAutoSteerService.SendPgnsForControlTick"/>).</item>
/// </list>
///
/// <para><b>What other threads read of what it writes:</b> the tool position (the host loop's
/// render pull, the GPS cycle, the web projector) behind a seqlock; the section states and bits
/// (the host loop, the GPS cycle's snapshot, the web projector) as they are; the coverage map
/// (the web broadcaster, the save, the projector) under the coverage lock.</para>
///
/// <para><b>For a real-time loop</b> (Linux PREEMPT_RT, a later change): what the tick waits on,
/// allocates or calls into, as of this move. A real-time thread that takes a lock a normal thread
/// holds waits for that thread (priority inversion); each of these is to be removed or bounded
/// before the tick runs at real-time priority:</para>
/// <list type="bullet">
/// <item><b>Locks shared with other threads:</b> the coverage map's <c>_coverageLock</c>
/// (<c>StartMapping</c>, <c>AddCoveragePoint</c>, <c>StopMapping</c>,
/// <c>FlushCoverageUpdate</c>), also held by the web projector's snapshot, the broadcaster's
/// incremental drain and the coverage save; the tool position's <c>_writeLock</c>, also taken by
/// <c>ResetTrailingState</c> on the host loop (the simulator reset, the drift and boundary
/// commands); AutoSteer's <c>_sectionDimensionsLock</c> on the start-up baseline only (shared with
/// the settle timer and a profile save).</item>
/// <item><b>Handlers of other code run on the tick:</b> <c>SectionStateChanged</c> every tick (the
/// view model posts its section bar refresh to the host loop: a delegate, the event's args and a
/// <c>BlockingCollection.Add</c>, whose semaphore takes a lock the host loop's take holds too);
/// <c>CoverageUpdated</c> when the coverage changed (the web broadcaster sets a flag);
/// <c>GuidanceLost</c> once when guidance goes stale (the view model posts).</item>
/// <item><b>Allocation per tick:</b> the PGN arrays (254, 239, 229), the section event's args, the
/// post above; the coverage strips as they grow. The hot-path budget allows 512 KB a tick
/// (<c>HotPathBudgetTests</c>); <c>ControlTickTests</c> measures it on the real services.</item>
/// <item><b>System calls:</b> the UDP sends (a blocking <c>SendTo</c> for each PGN to the module
/// and to localhost, or to every network interface's broadcast while no module has answered);
/// on a network change, the first send after it lists the network interfaces
/// (<c>UdpCommunicationService.RefreshDiscoveryIfDue</c>).</item>
/// </list>
/// </summary>
public sealed class ControlTick
{
    private readonly IToolPositionService _toolPositionService;
    private readonly ISectionControlService _sectionControlService;
    private readonly IAutoSteerService _autoSteerService;
    private readonly IPositionEstimator? _positionEstimator;

    public ControlTick(
        IToolPositionService toolPositionService,
        ISectionControlService sectionControlService,
        IAutoSteerService autoSteerService,
        IPositionEstimator? positionEstimator = null)
    {
        _toolPositionService = toolPositionService;
        _sectionControlService = sectionControlService;
        _autoSteerService = autoSteerService;
        _positionEstimator = positionEstimator;
    }

#if DEBUG
    private int _running;
#endif

    /// <summary>One tick, at <paramref name="timestampTicks"/> (<c>Clock.Current</c>).</summary>
    public void Run(long timestampTicks)
    {
#if DEBUG
        if (Interlocked.Exchange(ref _running, 1) != 0)
            throw new InvalidOperationException("A control tick was entered while another was running: the tick's state (tool position, sections) has one writer at a time.");
        try
        {
            RunTick(timestampTicks);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
#else
        RunTick(timestampTicks);
#endif
    }

    private void RunTick(long timestampTicks)
    {
        // Only run the section/tool pipeline once a GPS sample exists; before
        // then the estimator returns default(InterpolatedPose) which would
        // pin tool position at (0,0). Autosteer PGN sends are still useful
        // before the first GPS sample (firmware needs to see SOMETHING to
        // know we're alive), so they don't gate.
        if (_positionEstimator?.GetLatestSnapshot() is not null)
        {
            var p = _positionEstimator.GetPose(timestampTicks);
            _toolPositionService.Update(
                new Vec3(p.Position.Easting, p.Position.Northing, p.Heading),
                p.Heading);
            _sectionControlService.Update(
                _toolPositionService.ToolPosition,
                _toolPositionService.ToolHeading,
                p.Heading,
                p.SpeedMps);
        }
        // Hand this tick's section bits to the sender (#311). The GPS cycle writes them
        // too, but only once per fix; an edge decided here, or a section button tapped
        // between fixes, must not wait for the next one.
        _autoSteerService.SetSectionBits(_sectionControlService.GetSectionBits64());
        _autoSteerService.SendPgnsForControlTick();
    }
}
