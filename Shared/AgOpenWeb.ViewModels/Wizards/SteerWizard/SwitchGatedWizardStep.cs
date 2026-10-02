// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.Threading;
using System.Threading.Tasks;

using AgOpenWeb.Models;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.ViewModels.Wizards.SteerWizard;

/// <summary>
/// Base for steer-wizard steps that drive the steering themselves through Free Drive
/// (motor calibration, maximum steering angle). It owns the Free Drive lifecycle so every
/// test starts and ends the same way (#154):
/// <list type="bullet">
/// <item><see cref="BeginFreeDriveAsync"/> turns Free Drive on at 0°, waits
/// <see cref="ArmSettleMs"/>, then checks the module is armed — the steer switch is on. PGN 253
/// byte 11 bit 1 (SteerSwitchActive, AgOpenGPS steerSwitchHigh) is set when the module is NOT
/// steering. AIO v4 also reports "not steering" whenever the app sends status 0, so the bit
/// only means "switch off" once Free Drive (status 1) has been going for a moment; it is never
/// judged before Start. AgOpenGPS only jogs when its steer circle is yellow (module armed,
/// AutoSteer off) — FormSteerWiz CheckSteerSwitch.</item>
/// <item><see cref="EndFreeDriveAsync"/> holds 0° until the WAS is back near centre (or
/// <see cref="CenterHoldTimeoutMs"/> passes), then <see cref="ReleaseFreeDrive"/> hands PGN 254
/// back with AgOpenWeb's AutoSteer off — never to an engaged AutoSteer, whose guidance angle
/// could be full lock.</item>
/// </list>
/// AgOpenWeb's own AutoSteer is never needed (or allowed) here: MainViewModel keeps it off
/// while the wizard is open.
/// </summary>
public abstract class SwitchGatedWizardStep : WizardStepViewModel
{
    /// <summary>How long Free Drive runs before the module's arming is judged.</summary>
    internal const int ArmSettleMs = 500;
    /// <summary>End of a test: give up waiting for the wheels to centre after this long.</summary>
    internal const int CenterHoldTimeoutMs = 3000;
    /// <summary>End of a test: the wheels count as centred within this many degrees.</summary>
    internal const double CenterToleranceDeg = 1.0;
    private const int CenterPollMs = 100;

    internal const string NotArmedText =
        "The steer module isn't steering — turn on the steer switch, then try again.";

    protected IConfigurationService ConfigService { get; }
    protected IAutoSteerService? AutoSteerService { get; }

    private readonly IUiDispatcher _dispatcher;
    private bool _gateSubscribed;
    private CancellationTokenSource? _testCts;
    private bool _freeDriveOn;
    private bool _armSettled;
    private string _lastHint = "";

    protected SwitchGatedWizardStep(IConfigurationService configService,
        IAutoSteerService? autoSteerService, IUiDispatcher dispatcher)
    {
        ConfigService = configService;
        AutoSteerService = autoSteerService;
        _dispatcher = dispatcher;
    }

    /// <summary>Injectable delay function for testing. Production uses Task.Delay.</summary>
    internal Func<int, CancellationToken, Task> DelayFunc { get; set; } = Task.Delay;

    /// <summary>True when hardware is connected and sending data.</summary>
    public bool HasHardware => AutoSteerService != null;

    /// <summary>
    /// Gate for the Start button. It no longer waits on the steer switch: the module's arming
    /// can only be read once Free Drive is on, so Start checks it and says why it stopped.
    /// </summary>
    public bool CanStartTest => HasHardware;

    /// <summary>Live module feedback; steps with an injectable reader override this.</summary>
    protected virtual SteerModuleData CurrentModuleData =>
        AutoSteerService?.LastSteerData ?? SteerModuleData.Empty;

    /// <summary>Live WAS angle; steps with an injectable reader override this.</summary>
    protected virtual double CurrentWasAngle => CurrentModuleData.ActualSteerAngle;

    /// <summary>PGN 253 says the module isn't steering (steer switch off / not armed).</summary>
    private bool ModuleNotSteering => CurrentModuleData.SteerSwitchActive;

    /// <summary>
    /// Live warning under Start: during a test the module reports it isn't steering, so the
    /// wheels won't follow (switch turned off, or a kickout).
    /// </summary>
    public string RecordHint =>
        _freeDriveOn && _armSettled && ModuleNotSteering ? NotArmedText : "";

    /// <summary>Fresh cancellation for a test run; <see cref="StopFreeDriveTest"/> cancels it.</summary>
    protected CancellationToken NewTestToken()
    {
        _testCts = new CancellationTokenSource();
        return _testCts.Token;
    }

    /// <summary>
    /// Free Drive on at 0°, wait <see cref="ArmSettleMs"/>, then check the module is armed.
    /// Returns false (Free Drive already released) when it isn't — the caller reports
    /// <see cref="NotArmedText"/>. Callers must run this inside the try whose finally calls
    /// <see cref="EndFreeDriveAsync"/>.
    /// </summary>
    protected async Task<bool> BeginFreeDriveAsync(CancellationToken token)
    {
        _armSettled = false;
        _freeDriveOn = true;
        AutoSteerService?.EnableFreeDrive(); // status 1, 0°
        await DelayFunc(ArmSettleMs, token);
        _armSettled = true;
        if (!ModuleNotSteering)
            return true;
        ReleaseFreeDrive();
        return false;
    }

    /// <summary>
    /// End of a test: command 0° and hold it until the WAS is within
    /// <see cref="CenterToleranceDeg"/> of centre or <see cref="CenterHoldTimeoutMs"/> passes,
    /// then release. Not cancellable — it is the cleanup path — but returns at once if
    /// Free Drive was already released (step left, wizard closed).
    /// </summary>
    protected async Task EndFreeDriveAsync()
    {
        if (!_freeDriveOn)
            return;
        AutoSteerService?.SetFreeDriveAngle(0);
        for (int elapsed = 0; _freeDriveOn && elapsed < CenterHoldTimeoutMs; elapsed += CenterPollMs)
        {
            if (Math.Abs(CurrentWasAngle) <= CenterToleranceDeg)
                break;
            await DelayFunc(CenterPollMs, CancellationToken.None);
        }
        ReleaseFreeDrive();
    }

    /// <summary>
    /// Hand PGN 254 back to normal mode now. AgOpenWeb's AutoSteer goes off first: normal mode
    /// sends status + guidance angle, and an engaged AutoSteer off its line asks for full lock —
    /// the jerk to the right in #154. MainViewModel blocks engaging it while the wizard is
    /// open; this is the backstop.
    /// </summary>
    protected void ReleaseFreeDrive()
    {
        _freeDriveOn = false;
        _armSettled = false;
        if (AutoSteerService == null)
            return;
        if (AutoSteerService.IsEngaged)
            AutoSteerService.Disengage();
        AutoSteerService.SetFreeDriveAngle(0);
        AutoSteerService.DisableFreeDrive();
    }

    /// <summary>
    /// Stop a running test and release Free Drive at once (the step is being left or the
    /// wizard closed). The test's own cleanup then finds Free Drive already off.
    /// </summary>
    public void StopFreeDriveTest()
    {
        _testCts?.Cancel();
        if (_freeDriveOn || AutoSteerService?.IsInFreeDriveMode == true)
            ReleaseFreeDrive();
    }

    /// <summary>
    /// Follow PGN 253 so <see cref="RecordHint"/> refreshes for bindings. Idempotent — safe
    /// to call from OnEntering without a "first time" guard.
    /// </summary>
    protected void SubscribeToSwitchGate()
    {
        if (_gateSubscribed)
            return;
        _gateSubscribed = true;

        if (AutoSteerService != null)
            AutoSteerService.StateUpdated += OnSwitchGateStateUpdated;
    }

    /// <summary>
    /// Mirror of <see cref="SubscribeToSwitchGate"/>. Subclasses must call this from
    /// <c>OnLeaving</c> so the step doesn't leak handlers onto the singletons it observes.
    /// </summary>
    protected void UnsubscribeFromSwitchGate()
    {
        if (!_gateSubscribed)
            return;
        _gateSubscribed = false;

        if (AutoSteerService != null)
            AutoSteerService.StateUpdated -= OnSwitchGateStateUpdated;
    }

    private void OnSwitchGateStateUpdated(object? sender, VehicleStateSnapshot snapshot)
    {
        // StateUpdated fires on the control-loop / UDP thread at 100 Hz; raise on the UI
        // thread, and only when the hint actually changes.
        if (_dispatcher.CheckAccess())
            RefreshRecordHint();
        else
            _dispatcher.Post(RefreshRecordHint);
    }

    private void RefreshRecordHint()
    {
        string hint = RecordHint;
        if (hint == _lastHint)
            return;
        _lastHint = hint;
        OnPropertyChanged(nameof(RecordHint));
    }
}
