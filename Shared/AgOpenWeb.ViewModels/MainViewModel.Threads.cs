// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace AgOpenWeb.ViewModels;

/// <summary>
/// The view model's control-path glue and the threads it is called on, in one place
/// (Plans/MAINVIEWMODEL_REFACTOR_PLAN.md item 7, rule 4). The view model's state is the host
/// loop's (<c>HostLoopDispatcher</c>); a handler raised on another thread crosses to it by a post,
/// or keeps a single value for the host loop to read. The 100 Hz tick and the 30 Hz render pull
/// are not here: <c>ControlTick</c> and <see cref="RenderPose"/> state their own.
///
/// <para><b>Raised on another thread, crossing by a post:</b></para>
/// <list type="bullet">
/// <item><c>OnGpsCycleCompleted</c>: the GPS pipeline's worker, at the end of each cycle; posts
/// <see cref="ApplyGpsCycleResult"/> (the snapshot, the one way out of the cycle).</item>
/// <item><c>OnGpsDataUpdated</c>: the module receive thread (UDP or the GPS serial reader, through
/// <c>GpsService.UpdateGpsData</c>), or the host loop for the simulator; posts
/// <c>HandleGpsUiUpdates</c> unless already on the host loop.</item>
/// <item><c>OnAutoSteerStateUpdated</c>: the module receive thread, at the GPS rate and on a PGN 253
/// switch change; keeps the latency (one double, read by the 5 Hz status tick) and posts
/// <c>ApplyTramState</c>.</item>
/// <item><c>OnGuidanceLost</c>: the control tick, once when guidance goes stale; posts.</item>
/// <item><c>OnSensorKickout</c>: the module receive thread (PGN 250); posts.</item>
/// <item><c>OnRemoteSwitchesReceived</c>: the module receive thread (PGN 234); posts
/// <c>ApplyRemoteSwitches</c>.</item>
/// <item><c>OnSectionStateChanged</c>: the control tick (every tick) and the host loop (a section
/// button); posts <c>UpdateSectionStates</c> from the tick, runs it on the host loop.</item>
/// </list>
///
/// <para><b>Raised on the host loop:</b> <c>OnAutoSteerToggleRequested</c> and
/// <c>OnSectionMasterToggleRequested</c>, from the module communication service's
/// <c>CheckSwitches</c>, which <c>UpdateModuleSwitches</c> calls in <c>ApplyGpsCycleResult</c>.
/// The first toggles at once and reports the result (the steer switch → sections check that
/// follows needs it), and would post from another thread; the second posts anyway, so its toggle
/// runs after the cycle's result is applied.</para>
///
/// <para><b>On the host loop only</b> once built (checked in debug and test builds,
/// <see cref="AssertHostLoop"/>; not the constructor, before anything can reach the view model):
/// <c>ApplyGpsCycleResult</c>, <c>HandleGpsUiUpdates</c>, <c>ApplyTramState</c>,
/// <c>ApplyRemoteSwitches</c>, <c>UpdateSectionStates</c>, <c>UpdateModuleSwitches</c>,
/// <c>UpdateModuleSteeringState</c>, and the pushes into the cycle and section control
/// (<c>SyncGuidanceStateToPipeline</c>, <c>SyncFieldContextToSectionControl</c>,
/// <c>SyncYouTurnConfigToPipeline</c>): setters that copy the values across, the one way in.</para>
///
/// <para><b>The module status loop</b> (<c>StartHelloTimerAsync</c>, every 100 ms: the hello,
/// the module and GPS checks, <c>State.Connections</c>, the module-lost and GPS-lost alarms, data
/// quality, the status line) runs where <see cref="Start"/> ran, as its awaits resume there: the
/// host loop, where the backend builds and starts the view model
/// (<c>WebBackend.CreateViewModelOnHostLoopAsync</c>). The heads started it on their start-up
/// thread, and it ran on the thread pool beside the host loop.</para>
/// </summary>
public partial class MainViewModel
{
    // Set as the constructor's last step; AssertHostLoop checks from then on.
    private bool _constructed;

    /// <summary>
    /// Debug and test builds: fail when <paramref name="what"/> runs off the host loop, so the
    /// unit tests and the harnesses catch a call from the wrong thread (refactor rule 4). Not in a
    /// release build.
    /// </summary>
    [Conditional("DEBUG")]
    private void AssertHostLoop([CallerMemberName] string what = "")
    {
        // The constructor may run on a thread that builds the view model before handing it to
        // the host loop (the unit tests' container); nothing else can reach the view model then,
        // so its first pushes (SyncFieldContextToSectionControl, the YouTurn config) are not a
        // crossing.
        if (!_constructed) return;
        if (!_dispatcher.CheckAccess())
            throw new InvalidOperationException($"{what} runs on the host loop: it writes the view model's state, which is the host loop's.");
    }
}
