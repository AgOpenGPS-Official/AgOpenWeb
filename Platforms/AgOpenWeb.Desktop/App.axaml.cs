// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using AgOpenWeb.Desktop.Views;
using AgOpenWeb.Desktop.DependencyInjection;
using AgOpenWeb.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AgOpenWeb.Desktop;

public partial class App : Application
{
    private IHost? _host;
    private AgOpenWeb.RemoteServer.RemoteServerHost? _remoteServer;

    // track.* ids that are DATA/creation, not guidance actuation — exempt from the
    // operator gate so any browser can draw/create/import lines and toggle display.
    // Everything else under track.* (activate/cycle/nudge/snap/swap/delete — they change
    // the active line the machine follows) stays gated. Fail-closed: a new track.* id
    // defaults to gated until added here.
    private static readonly System.Collections.Generic.HashSet<string> UngatedTrackIds = new()
    {
        "track.drawStraight", "track.drawCurve", "track.drawPoint", "track.drawFinish",
        "track.drawUndo", "track.drawCancel", "track.aPlus", "track.driveAB",
        "track.recordCurve", "track.finishCurve", "track.setABGps",
        "track.createFromBoundary", "track.boundaryCurve", "track.allEdges",
        "track.setVisible", "track.toggleRecPaths", "track.editSave",
    };

    // Field Builder headland *building* is field-data editing (done while reviewing the
    // field, not live actuation), so it stays ungated like track creation. The live
    // headland on/off + section-in-headland toggles (headland.toggle / .sectionToggle)
    // DO change what the machine does, so they remain gated. Fail-closed: a new
    // headland.* id defaults to gated until added here.
    private static readonly System.Collections.Generic.HashSet<string> UngatedHeadlandIds = new()
    {
        "headland.fromMapPoints", "headland.wholeBoundary", "headland.setOffset",
        "headland.delete", "headland.deleteAll", "headland.rename", "headland.editSave",
    };

    public static IServiceProvider? Services { get; set; }

    /// <summary>
    /// Hook for integration tests to replace services before DI build.
    /// </summary>
    public static Action<IServiceCollection>? ConfigureTestServices { get; set; }

    /// <summary>
    /// Hook for integration tests to run scenarios after MainWindow is shown.
    /// </summary>
    public static Func<IClassicDesktopStyleApplicationLifetime, Task>? OnAppReady { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        AgOpenWeb.Views.Diagnostics.DiagFlagsBootstrap.ApplyAtStartup(this);

        // Build DI container
        _host = Host.CreateDefaultBuilder()
            .ConfigureServices((context, services) =>
            {
                services.AddAgOpenWebServices();
                ConfigureTestServices?.Invoke(services);
            })
            .Build();

        Services = _host.Services;

        // Provide DI to chart panels for auto-configuration
        AgOpenWeb.Views.Controls.Panels.SteerChartPanel.ServiceProvider = Services;
        AgOpenWeb.Views.Controls.Panels.HeadingChartPanel.ServiceProvider = Services;
        AgOpenWeb.Views.Controls.Panels.XTEChartPanel.ServiceProvider = Services;

        // Wire up cross-referencing services (AutoSteer → UDP)
        Services.WireUpServices();

        // Phase 1 web UI (Plans/REMOTE_WEB_UI_SPLIT.md): embedded SignalR map
        // server running ALONGSIDE the app, fed by the live ApplicationState.
        // Browse to http://localhost:5174 (or the LAN IP) to see the live map.
        _remoteServer = new AgOpenWeb.RemoteServer.RemoteServerHost();
        _ = _remoteServer.StartAsync(
            Services.GetRequiredService<AgOpenWeb.Models.State.ApplicationState>(),
            Services.GetRequiredService<ICoverageMapService>(),
            Services.GetRequiredService<ISectionControlService>(),
            Services.GetRequiredService<IToolPositionService>(),
            Services.GetRequiredService<AgOpenWeb.Models.Configuration.ConfigurationStore>(),
            Services.GetRequiredService<IJobService>(),
            Services.GetRequiredService<IConfigurationService>(),
            Services.GetRequiredService<IAutoSteerService>(),
            Services.GetRequiredService<ISmartWasCalibrationService>(),
            Services.GetRequiredService<IUdpCommunicationService>(),
            Services.GetRequiredService<INtripProfileService>(),
            Services.GetRequiredService<AgOpenWeb.Services.IFieldService>(),
            Services.GetRequiredService<ISettingsService>(),
            Services.GetRequiredService<IVehicleProfileService>(),
            Services.GetRequiredService<IPersistentStateService>());

        // Extract sound files from Avalonia resources for cross-platform audio
        ExtractSoundFiles(Services);

        // Load settings and sync to ConfigurationStore
        var settingsService = Services.GetRequiredService<ISettingsService>();
        settingsService.Load();
        var configService = Services.GetRequiredService<IConfigurationService>();
        configService.LoadAppSettings();

        // Load persistent application state (window/last-view/last-field/sim
        // position). Migrates from legacy appsettings.json on first run of this
        // build. Tier-2 store, separate from config.
        Services.GetRequiredService<IPersistentStateService>().Load();

        // Apply saved language (#40)
        var savedLang = settingsService.Settings.Language;
        if (!string.IsNullOrEmpty(savedLang) && savedLang != "en")
        {
            try
            {
                AgOpenWeb.Views.Localization.TranslationSource.Instance.CurrentCulture =
                    new System.Globalization.CultureInfo(savedLang);
            }
            catch { /* fall back to English */ }
        }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = new MainWindow();
            desktop.MainWindow = mainWindow;

            // Wire language change to TranslationSource (#40)
            // Must use MainWindow's ViewModel (not DI - MainViewModel is Transient)
            if (mainWindow.DataContext is AgOpenWeb.ViewModels.MainViewModel windowVm)
            {
                windowVm.LanguageChanged += code =>
                {
                    try
                    {
                        AgOpenWeb.Views.Localization.TranslationSource.Instance.CurrentCulture =
                            new System.Globalization.CultureInfo(code);
                    }
                    catch { }
                };

                // Remote/web UI client→host commands (REMOTE_WEB_UI_SPLIT §5). The
                // hub calls this off-thread; marshal to the UI thread, map known ids,
                // ignore the rest. Tier-2 (live actuation) ids only arrive when the
                // sending client holds fresh control authority (the hub gates them).
                if (_remoteServer is not null)
                    WireRemoteServer(_remoteServer, windowVm, Services!, configService);
            }

            desktop.Exit += (sender, args) =>
            {
                // Persist config (store→DTO→disk) and application state on exit.
                // Use SaveAppSettings (not raw settingsService.Save) so the
                // ConfigurationStore is the source of truth for the written file.
                configService.SaveAppSettings();
                Services.GetRequiredService<IPersistentStateService>().Save();
                _ = _remoteServer?.StopAsync();
                _host?.Dispose();
            };

            // Integration test hook: run scenarios after window is ready
            if (OnAppReady != null)
            {
                var callback = OnAppReady;
                Dispatcher.UIThread.Post(async () =>
                {
                    try
                    {
                        await Task.Delay(100); // Let window render initial frame
                        await callback(desktop);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[IntTest] SCENARIO FAILED: {ex.Message}");
                        Console.WriteLine(ex.StackTrace);
                    }
                    finally
                    {
                        desktop.Shutdown();
                    }
                });
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    // Config bridge (Phase 9a+): apply one "key:value" config write from the web client.
    // Device settings (e.g. units) persist immediately via SaveAppSettings; profile
    // settings (vehicle dims) take live effect only — the client persists them with a
    // profile.save. Grows as later sub-phases expose more of ConfigurationStore. Runs
    // on the UI thread.
    private static void ApplyConfigSet(AgOpenWeb.Models.Configuration.ConfigurationStore store,
        AgOpenWeb.Services.Interfaces.IConfigurationService cfg, string key, string val)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        bool D(out double d) => double.TryParse(val, System.Globalization.NumberStyles.Float, inv, out d);
        bool I(out int i) => int.TryParse(val, System.Globalization.NumberStyles.Integer, inv, out i);
        bool B() => val == "1";
        bool IDX(out int i, out string rest) // "i,rest" — indexed array writes
        {
            var k = val.IndexOf(','); rest = ""; i = 0;
            if (k <= 0 || !int.TryParse(val[..k], out i)) return false;
            rest = val[(k + 1)..]; return true;
        }
        var veh = store.Vehicle; var con = store.Connections; var ahrs = store.Ahrs;
        var tool = store.Tool; var gd = store.Guidance; var mch = store.Machine; var disp = store.Display;
        var ast = store.AutoSteer;
        switch (key)
        {
            case "units": store.IsMetric = val == "metric"; cfg.SaveAppSettings(); return; // device setting
            // --- Network IO module-present flags (ConfigStore.Connections). Device
            // settings → persist now; they drive the Status frame's *Configured dots. ---
            case "conn.gpsConfigured": con.IsGpsConfigured = B(); cfg.SaveAppSettings(); return;
            case "conn.imuConfigured": con.IsImuConfigured = B(); cfg.SaveAppSettings(); return;
            case "conn.autoSteerConfigured": con.IsAutoSteerConfigured = B(); cfg.SaveAppSettings(); return;
            case "conn.machineConfigured": con.IsMachineConfigured = B(); cfg.SaveAppSettings(); return;
            // --- AgShare cloud settings (ConfigStore.Connections). Persist on change. ---
            case "conn.agShareServer": con.AgShareServer = val; cfg.SaveAppSettings(); return;
            case "conn.agShareApiKey": con.AgShareApiKey = val; cfg.SaveAppSettings(); return;
            case "conn.agShareEnabled": con.AgShareEnabled = B(); cfg.SaveAppSettings(); return;
            // --- Vehicle config (Phase 9b). Live effect; persisted by a profile.save. ---
            case "vehicle.type": if (I(out var ty)) veh.Type = (AgOpenWeb.Models.VehicleType)ty; return;
            case "vehicle.hitchType": if (I(out var ht)) veh.HitchType = ht; return;
            case "vehicle.hitchLength": if (D(out var d1)) veh.HitchLength = d1; return;
            case "vehicle.wheelbase": if (D(out var d2)) veh.Wheelbase = d2; return;
            case "vehicle.trackWidth": if (D(out var d3)) veh.TrackWidth = d3; return;
            case "vehicle.antennaPivot": if (D(out var d4)) veh.AntennaPivot = d4; return;
            case "vehicle.antennaHeight": if (D(out var d5)) veh.AntennaHeight = d5; return;
            case "vehicle.antennaOffset": if (D(out var d6)) veh.AntennaOffset = d6; return;
            case "vehicle.antennaSide": // L/C/R presets (mirror ConfigurationViewModel)
                if (val == "center") veh.AntennaOffset = 0;
                else if (val == "left") veh.AntennaOffset = System.Math.Abs(veh.AntennaOffset) < 0.01 ? -0.5 : -System.Math.Abs(veh.AntennaOffset);
                else if (val == "right") veh.AntennaOffset = System.Math.Abs(veh.AntennaOffset) < 0.01 ? 0.5 : System.Math.Abs(veh.AntennaOffset);
                return;
            // --- GPS data source (ConfigStore.Connections) ---
            case "gps.isDualGps": con.IsDualGps = B(); return;
            case "gps.dualHeadingOffset": if (D(out var g1)) con.DualHeadingOffset = g1; return;
            case "gps.dualReverseDistance": if (D(out var g2)) con.DualReverseDistance = g2; return;
            case "gps.autoDualFix": con.AutoDualFix = B(); return;
            case "gps.dualSwitchSpeed": if (D(out var g3)) con.DualSwitchSpeed = g3; return;
            case "gps.minGpsStep": if (D(out var g4)) con.MinGpsStep = g4; return;
            case "gps.fixToFixDistance": if (D(out var g5)) con.FixToFixDistance = g5; return;
            case "gps.headingFusionWeight": if (D(out var g6)) con.HeadingFusionWeight = g6; return;
            case "gps.reverseDetection": con.ReverseDetection = B(); return;
            case "gps.rtkLostAlarm": con.RtkLostAlarm = B(); return;
            case "gps.rtkLostAction": if (I(out var ra)) con.RtkLostAction = ra; return;
            // --- Roll / AHRS (ConfigStore.Ahrs) ---
            case "roll.rollZero": if (D(out var r1)) ahrs.RollZero = r1; return;
            case "roll.rollFilter": if (D(out var r2)) ahrs.RollFilter = r2; return;
            case "roll.isRollInvert": ahrs.IsRollInvert = B(); return;
            case "roll.setZero": ahrs.RollZero = 0; return; // mirror SetRollZeroCommand
            // --- Tool / Implement (ConfigStore.Tool + NumSections) ---
            case "tool.type": tool.SetToolType(val); return; // front/rear/tbt/trailing
            case "tool.hitchType": if (I(out var th)) tool.HitchType = th; return;
            case "tool.hitchLength": if (D(out var t1)) tool.HitchLength = t1; return;
            case "tool.trailingHitchLength": if (D(out var t2)) tool.TrailingHitchLength = t2; return;
            case "tool.tankTrailingHitchLength": if (D(out var t3)) tool.TankTrailingHitchLength = t3; return;
            case "tool.length": if (D(out var t4)) tool.Length = t4; return;
            case "tool.lookAheadOn": if (D(out var t5)) tool.LookAheadOnSetting = t5; return;
            case "tool.lookAheadOff": if (D(out var t6)) tool.LookAheadOffSetting = t6; return;
            case "tool.turnOffDelay": if (D(out var t7)) tool.TurnOffDelay = t7; return;
            case "tool.offset": if (D(out var t8)) tool.Offset = t8; return;
            case "tool.overlap": if (D(out var t9)) tool.Overlap = t9; return;
            case "tool.trailingToolToPivotLength": if (D(out var t10)) tool.TrailingToolToPivotLength = t10; return;
            case "tool.slowSpeedCutoff": if (D(out var t11)) tool.SlowSpeedCutoff = t11; return;
            case "tool.coverageMargin": if (D(out var t12)) tool.CoverageMargin = t12; return;
            case "tool.defaultSectionWidth": if (D(out var t13)) tool.DefaultSectionWidth = t13; return;
            case "tool.minCoverage": if (I(out var t14)) tool.MinCoverage = t14; return;
            case "tool.numSections": if (I(out var t15)) store.NumSections = t15; return;
            case "tool.zones": if (I(out var t16)) tool.Zones = t16; return;
            case "tool.isSectionsNotZones": tool.IsSectionsNotZones = B(); return;
            case "tool.isMultiColoredSections": tool.IsMultiColoredSections = B(); return;
            case "tool.isSectionOffWhenOut": tool.IsSectionOffWhenOut = B(); return;
            case "tool.isHeadlandSectionControl": tool.IsHeadlandSectionControl = B(); return;
            case "tool.isWorkSwitchEnabled": tool.IsWorkSwitchEnabled = B(); return;
            case "tool.isWorkSwitchActiveLow": tool.IsWorkSwitchActiveLow = B(); return;
            case "tool.isWorkSwitchManualSections": tool.IsWorkSwitchManualSections = B(); return;
            case "tool.isSteerSwitchEnabled": tool.IsSteerSwitchEnabled = B(); return;
            case "tool.isSteerSwitchManualSections": tool.IsSteerSwitchManualSections = B(); return;
            case "tool.offsetSide": // left/right/zero (sign on Offset)
                tool.Offset = val == "zero" ? 0 : val == "left" ? -System.Math.Abs(tool.Offset == 0 ? 0.5 : tool.Offset) : System.Math.Abs(tool.Offset == 0 ? 0.5 : tool.Offset);
                return;
            case "tool.overlapSide": // overlap(+)/gap(-)/zero
                tool.Overlap = val == "zero" ? 0 : val == "gap" ? -System.Math.Abs(tool.Overlap == 0 ? 0.1 : tool.Overlap) : System.Math.Abs(tool.Overlap == 0 ? 0.1 : tool.Overlap);
                return;
            case "tool.pivotSide": // behind(+)/ahead(-)/zero
                tool.TrailingToolToPivotLength = val == "zero" ? 0 : val == "ahead" ? -System.Math.Abs(tool.TrailingToolToPivotLength == 0 ? 0.5 : tool.TrailingToolToPivotLength) : System.Math.Abs(tool.TrailingToolToPivotLength == 0 ? 0.5 : tool.TrailingToolToPivotLength);
                return;
            case "tool.sectionWidth": if (IDX(out var swi, out var swv) && double.TryParse(swv, System.Globalization.NumberStyles.Float, inv, out var swd)) tool.SetSectionWidth(swi, swd); return;
            case "tool.zoneEnd": if (IDX(out var zei, out var zev) && int.TryParse(zev, out var zen)) tool.SetZoneEndSection(zei, zen); return;
            case "tool.sectionColor": if (IDX(out var sci, out var sch) && uint.TryParse(sch, System.Globalization.NumberStyles.HexNumber, inv, out var scc)) tool.SetSectionColor(sci, scc); return;
            case "tool.singleCoverageColor": if (uint.TryParse(val, System.Globalization.NumberStyles.HexNumber, inv, out var scov)) tool.SingleCoverageColor = scov; return;
            // --- U-Turn (ConfigStore.Guidance) ---
            case "uturn.style": if (I(out var u1)) gd.UTurnStyle = u1; return;
            case "uturn.extension": if (D(out var u2)) gd.UTurnExtension = u2; return;
            case "uturn.smoothing": if (I(out var u3)) gd.UTurnSmoothing = u3; return;
            case "uturn.radius": if (D(out var u4)) gd.UTurnRadius = u4; return;
            case "uturn.distanceFromBoundary": if (D(out var u5)) gd.UTurnDistanceFromBoundary = u5; return;
            // --- Tram (ConfigStore.Guidance) ---
            case "tram.passes": if (I(out var tp)) gd.TramPasses = tp; return;
            case "tram.display": gd.TramDisplay = B(); return;
            case "tram.line": if (I(out var tl)) gd.TramLine = tl; return;
            // --- Machine Control (ConfigStore.Machine) ---
            case "machine.hydraulicLiftEnabled": mch.HydraulicLiftEnabled = B(); return;
            case "machine.raiseTime": if (I(out var m1)) mch.RaiseTime = m1; return;
            case "machine.lookAhead": if (D(out var m2)) mch.LookAhead = m2; return;
            case "machine.lowerTime": if (I(out var m3)) mch.LowerTime = m3; return;
            case "machine.invertRelay": mch.InvertRelay = B(); return;
            case "machine.user1": if (I(out var m4)) mch.User1Value = m4; return;
            case "machine.user2": if (I(out var m5)) mch.User2Value = m5; return;
            case "machine.user3": if (I(out var m6)) mch.User3Value = m6; return;
            case "machine.user4": if (I(out var m7)) mch.User4Value = m7; return;
            case "machine.pin": if (IDX(out var pi, out var pv) && int.TryParse(pv, out var pf)) mch.SetPinAssignment(pi, (AgOpenWeb.Models.Configuration.PinFunction)pf); return;
            case "machine.resetPins": mch.ResetPinAssignments(); return;
            // --- Screen & Alerts (ConfigStore.Display). Device settings → persist now. ---
            case "display.gridVisible": disp.GridVisible = B(); cfg.SaveAppSettings(); return;
            case "display.fieldTextureVisible": disp.FieldTextureVisible = B(); cfg.SaveAppSettings(); return;
            case "display.fieldTextureMoveable": disp.FieldTextureMoveable = B(); cfg.SaveAppSettings(); return;
            case "display.svennArrowVisible": disp.SvennArrowVisible = B(); cfg.SaveAppSettings(); return;
            case "display.headlandDistanceVisible": disp.HeadlandDistanceVisible = B(); cfg.SaveAppSettings(); return;
            case "display.lineSmoothEnabled": disp.LineSmoothEnabled = B(); cfg.SaveAppSettings(); return;
            case "display.autoDayNight": disp.AutoDayNight = B(); cfg.SaveAppSettings(); return;
            case "display.hardwareMessagesEnabled": disp.HardwareMessagesEnabled = B(); cfg.SaveAppSettings(); return;
            case "display.extraGuidelines": disp.ExtraGuidelines = B(); cfg.SaveAppSettings(); return;
            case "display.extraGuidelinesCount": if (I(out var dg)) { disp.ExtraGuidelinesCount = dg; cfg.SaveAppSettings(); } return;
            case "display.uTurnButtonVisible": disp.UTurnButtonVisible = B(); cfg.SaveAppSettings(); return;
            case "display.lateralButtonVisible": disp.LateralButtonVisible = B(); cfg.SaveAppSettings(); return;
            case "display.autoSteerSound": disp.AutoSteerSound = B(); cfg.SaveAppSettings(); return;
            case "display.uTurnSound": disp.UTurnSound = B(); cfg.SaveAppSettings(); return;
            case "display.hydraulicSound": disp.HydraulicSound = B(); cfg.SaveAppSettings(); return;
            case "display.sectionsSound": disp.SectionsSound = B(); cfg.SaveAppSettings(); return;
            case "display.keyboardEnabled": disp.KeyboardEnabled = B(); cfg.SaveAppSettings(); return;
            case "display.startFullscreen": disp.StartFullscreen = B(); cfg.SaveAppSettings(); return;
            case "display.elevationLogEnabled": disp.ElevationLogEnabled = B(); cfg.SaveAppSettings(); return;
            // --- AutoSteer config (Phase 9). Live effect on ConfigStore.AutoSteer (the
            // SoT the VM binds to); MarkChanged re-fingerprints so the Config frame
            // re-sends and the value persists on a profile.save / Send&Save. Hardware
            // push is the separate (gated) autosteer.* actions, not these edits. ---
            // Tab 1 — Pure Pursuit / Stanley
            case "autosteer.steerResponseHold": if (D(out var a1)) { ast.SteerResponseHold = a1; store.MarkChanged(); } return;
            case "autosteer.integralGain": if (D(out var a2)) { ast.IntegralGain = a2; store.MarkChanged(); } return;
            case "autosteer.isStanleyMode": ast.IsStanleyMode = B(); store.MarkChanged(); return;
            case "autosteer.stanleyAggressiveness": if (D(out var a3)) { ast.StanleyAggressiveness = a3; store.MarkChanged(); } return;
            case "autosteer.stanleyOvershootReduction": if (D(out var a4)) { ast.StanleyOvershootReduction = a4; store.MarkChanged(); } return;
            // Tab 2 — Steering Sensor
            case "autosteer.wasOffset": if (I(out var a5)) { ast.WasOffset = a5; store.MarkChanged(); } return;
            case "autosteer.countsPerDegree": if (D(out var a6)) { ast.CountsPerDegree = a6; store.MarkChanged(); } return;
            case "autosteer.ackermann": if (I(out var a7)) { ast.Ackermann = a7; store.MarkChanged(); } return;
            case "autosteer.maxSteerAngle": if (I(out var a8)) { ast.MaxSteerAngle = a8; store.MarkChanged(); } return;
            // Tab 3 — Deadzone / Timing
            case "autosteer.deadzoneHeading": if (D(out var a9)) { ast.DeadzoneHeading = a9; store.MarkChanged(); } return;
            case "autosteer.deadzoneDelay": if (I(out var a10)) { ast.DeadzoneDelay = a10; store.MarkChanged(); } return;
            case "autosteer.speedFactor": if (D(out var a11)) { ast.SpeedFactor = a11; store.MarkChanged(); } return;
            case "autosteer.acquireFactor": if (D(out var a12)) { ast.AcquireFactor = a12; store.MarkChanged(); } return;
            // Tab 4 — Gain / PWM
            case "autosteer.proportionalGain": if (I(out var a13)) { ast.ProportionalGain = a13; store.MarkChanged(); } return;
            case "autosteer.maxPwm": if (I(out var a14)) { ast.MaxPwm = a14; store.MarkChanged(); } return;
            case "autosteer.minPwm": if (I(out var a15)) { ast.MinPwm = a15; store.MarkChanged(); } return;
            // Tab 5 — Turn Sensors
            case "autosteer.turnSensorEnabled": ast.TurnSensorEnabled = B(); store.MarkChanged(); return;
            case "autosteer.pressureSensorEnabled": ast.PressureSensorEnabled = B(); store.MarkChanged(); return;
            case "autosteer.currentSensorEnabled": ast.CurrentSensorEnabled = B(); store.MarkChanged(); return;
            case "autosteer.turnSensorCounts": if (I(out var a16)) { ast.TurnSensorCounts = a16; store.MarkChanged(); } return;
            case "autosteer.pressureTripPoint": if (I(out var a17)) { ast.PressureTripPoint = a17; store.MarkChanged(); } return;
            case "autosteer.currentTripPoint": if (I(out var a18)) { ast.CurrentTripPoint = a18; store.MarkChanged(); } return;
            // Tab 6 — Hardware Config
            case "autosteer.danfossEnabled": ast.DanfossEnabled = B(); store.MarkChanged(); return;
            case "autosteer.invertWas": ast.InvertWas = B(); store.MarkChanged(); return;
            case "autosteer.invertMotor": ast.InvertMotor = B(); store.MarkChanged(); return;
            case "autosteer.invertRelays": ast.InvertRelays = B(); store.MarkChanged(); return;
            case "autosteer.motorDriver": if (I(out var a19)) { ast.MotorDriver = a19; store.MarkChanged(); } return;
            case "autosteer.adConverter": if (I(out var a20)) { ast.AdConverter = a20; store.MarkChanged(); } return;
            case "autosteer.imuAxisSwap": if (I(out var a21)) { ast.ImuAxisSwap = a21; store.MarkChanged(); } return;
            case "autosteer.externalEnable": if (I(out var a22)) { ast.ExternalEnable = a22; store.MarkChanged(); } return;
            // Tab 7 — Algorithm
            case "autosteer.uTurnCompensation": if (D(out var a23)) { ast.UTurnCompensation = a23; store.MarkChanged(); } return;
            case "autosteer.sideHillCompensation": if (D(out var a24)) { ast.SideHillCompensation = a24; store.MarkChanged(); } return;
            case "autosteer.steerInReverse": ast.SteerInReverse = B(); store.MarkChanged(); return;
            // Tab 8 — Speed Limits
            case "autosteer.manualTurnsEnabled": ast.ManualTurnsEnabled = B(); store.MarkChanged(); return;
            case "autosteer.manualTurnsSpeed": if (D(out var a25)) { ast.ManualTurnsSpeed = a25; store.MarkChanged(); } return;
            case "autosteer.minSteerSpeed": if (D(out var a26)) { ast.MinSteerSpeed = a26; store.MarkChanged(); } return;
            case "autosteer.maxSteerSpeed": if (D(out var a27)) { ast.MaxSteerSpeed = a27; store.MarkChanged(); } return;
            // Tab 9 — Display
            case "autosteer.lineWidth": if (I(out var a28)) { ast.LineWidth = a28; store.MarkChanged(); } return;
            case "autosteer.nudgeDistance": if (I(out var a29)) { ast.NudgeDistance = a29; store.MarkChanged(); } return;
            case "autosteer.nextGuidanceTime": if (D(out var a30)) { ast.NextGuidanceTime = a30; store.MarkChanged(); } return;
            case "autosteer.cmPerPixel": if (I(out var a31)) { ast.CmPerPixel = a31; store.MarkChanged(); } return;
            // Light/Steer are a radio MODE pair (mutually exclusive); GuidanceBarOn is
            // the master. Selecting one mode deselects the other (AgOpen parity).
            case "autosteer.lightbarEnabled": ast.LightbarEnabled = B(); if (ast.LightbarEnabled) ast.SteerBarEnabled = false; store.MarkChanged(); return;
            case "autosteer.steerBarEnabled": ast.SteerBarEnabled = B(); if (ast.SteerBarEnabled) ast.LightbarEnabled = false; store.MarkChanged(); return;
            case "autosteer.guidanceBarOn": ast.GuidanceBarOn = B(); store.MarkChanged(); return;
            // unknown key → ignored
        }
    }

    // ===== Steer Wizard host glue (Phase 9). The real SteerWizardViewModel runs here;
    // these forward the browser's nav/edit/action and project its state to a WizardDto.
    private static void ExecCmd(System.Windows.Input.ICommand? c)
    {
        if (c?.CanExecute(null) == true) c.Execute(null);
    }

    // Set a property on the wizard's current step by name (generic — covers every
    // step's editable fields without a per-step switch). Converts to the target type.
    private static void SetWizardProp(
        AgOpenWeb.ViewModels.Wizards.SteerWizard.SteerWizardViewModel? w, string name, string val)
    {
        var step = w?.CurrentStep;
        var p = step?.GetType().GetProperty(name);
        if (step == null || p == null || !p.CanWrite) return;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var t = System.Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
        object? conv = null;
        try
        {
            if (t == typeof(double)) { if (double.TryParse(val, System.Globalization.NumberStyles.Float, inv, out var d)) conv = d; }
            else if (t == typeof(int)) { if (int.TryParse(val, System.Globalization.NumberStyles.Integer, inv, out var i)) conv = i; }
            else if (t == typeof(bool)) conv = val == "1" || val == "true";
            else if (t.IsEnum) { if (int.TryParse(val, out var e)) conv = System.Enum.ToObject(t, e); }
            else if (t == typeof(string)) conv = val;
        }
        catch { return; }
        if (conv != null) p.SetValue(step, conv);
    }

    // Invoke a named command on the wizard's current step (e.g. "ZeroWas" → ZeroWasCommand).
    private static void InvokeWizardAction(
        AgOpenWeb.ViewModels.Wizards.SteerWizard.SteerWizardViewModel? w, string name)
    {
        var step = w?.CurrentStep;
        if (step?.GetType().GetProperty(name + "Command")?.GetValue(step)
            is System.Windows.Input.ICommand c && c.CanExecute(null)) c.Execute(null);
    }

    // Project the live wizard VM to a WizardDto. Editable values ride the existing
    // Config frame; this carries nav state, the status bar, and the calibration-step
    // live blob (read by reflection so missing fields default cleanly per step).
    private static AgOpenWeb.RemoteServer.WizardDto BuildWizardDto(
        AgOpenWeb.ViewModels.Wizards.SteerWizard.SteerWizardViewModel w)
    {
        var step = w.CurrentStep;
        object? s = step;
        string kind = step?.GetType().Name switch
        {
            "WelcomeStepViewModel" => "welcome",
            "VehicleTypeStepViewModel" => "vehicleType",
            "HardwareInstalledStepViewModel" => "hardware",
            "VehicleDimensionsStepViewModel" => "dimensions",
            "AntennaSetupStepViewModel" => "antenna",
            "HardwareConfigStepViewModel" => "hwconfig",
            "RollCalibrationStepViewModel" => "roll",
            "WasCalibrationStepViewModel" => "was",
            "AutoMotorCalibrationStepViewModel" => "motor",
            "MaxSteeringAngleStepViewModel" => "maxangle",
            "CpdCircleTestStepViewModel" => "cpd",
            "AckermannTestStepViewModel" => "ackermann",
            "SteeringGainsStepViewModel" => "gains",
            "SpeedAndSensorsStepViewModel" => "speed",
            "FinishStepViewModel" => "finish",
            _ => "unknown",
        };
        double GP(string n) { var p = s?.GetType().GetProperty(n); return p?.GetValue(s) is { } v ? System.Convert.ToDouble(v) : 0; }
        bool GPb(string n) { var p = s?.GetType().GetProperty(n); return p?.GetValue(s) is bool b && b; }
        string GPs(string n) { var p = s?.GetType().GetProperty(n); return p?.GetValue(s)?.ToString() ?? ""; }
        int GPi(string n) { var p = s?.GetType().GetProperty(n); return p?.GetValue(s) is { } v ? System.Convert.ToInt32(v) : 0; }
        var sb = w.StatusBar;
        string phaseResult = GPs("PhaseResult");
        bool testActive = GPb("IsRecording") || GPb("IsMeasuring") || GPb("IsPhaseA1") || GPb("IsPhaseB1");
        return new AgOpenWeb.RemoteServer.WizardDto(
            w.CurrentStepIndex, w.TotalSteps, kind, step?.Title ?? "", step?.Description ?? "",
            w.CanGoBack, w.CanGoNext, w.CanSkip, w.IsOnLastStep, step?.ValidationMessage ?? "",
            sb?.WasAngle ?? 0, sb?.RollAngle ?? 0, sb?.GpsStatus ?? "", sb?.SpeedKmh ?? 0, sb?.PwmOutput ?? 0, sb?.IsModuleConnected ?? false,
            GPi("HardwareLevel"),
            GP("LiveSteerAngle"), GP("LiveRoll"), GP("LiveSteerError"),
            GPs("PhaseDescription"), phaseResult.Length > 0 ? phaseResult : GPs("TestResult"),
            GP("Progress"), testActive,
            GPb("IsRtkFixed"), GPs("FixQualityLabel"), GP("Diameter"));
    }

    // Vehicle & Tool picker hub (Phase 9). Mirrors LoadVehicleToolDialogViewModel's
    // orchestration against IConfigurationService (the VM is dialog-scoped + Avalonia-
    // bound, so the web can't use it). Confirmations happen client-side. Args are
    // tab-separated; the leading field (where present) is the kind "vehicle"/"tool".
    private static void ApplyProfileCommand(AgOpenWeb.Models.Configuration.ConfigurationStore store,
        AgOpenWeb.Services.Interfaces.IConfigurationService cfg, string cmd, string arg)
    {
        var a = arg.Split('\t');
        switch (cmd)
        {
            case "profile.load": // <vehicle>\t<tool> — save the outgoing pair, then load
                if (a.Length == 2)
                {
                    cfg.SaveProfiles(store.ActiveVehicleProfileName, store.ActiveToolProfileName);
                    cfg.LoadProfiles(a[0], a[1]);
                }
                return;
            case "profile.new": // <kind>\t<name> — duplicate the live store under a new name
                if (a.Length == 2 && a[0] == "vehicle") cfg.SaveProfiles(a[1], store.ActiveToolProfileName);
                else if (a.Length == 2 && a[0] == "tool") cfg.SaveProfiles(store.ActiveVehicleProfileName, a[1]);
                return;
            case "profile.delete": // <kind>\t<name>
                if (a.Length == 2 && a[0] == "vehicle") cfg.DeleteVehicleProfile(a[1]);
                else if (a.Length == 2 && a[0] == "tool") cfg.DeleteToolProfile(a[1]);
                return;
            case "profile.rename": // <kind>\t<old>\t<new>
                if (a.Length == 3 && a[0] == "vehicle") cfg.RenameVehicleProfile(a[1], a[2]);
                else if (a.Length == 3 && a[0] == "tool") cfg.RenameToolProfile(a[1], a[2]);
                return;
            case "profile.reset": // <kind> — CreateProfile("Default") (matches Reset-to-Default)
                cfg.CreateProfile("Default");
                return;
            case "profile.configureVehicle": // <name> — make it active before the dialog opens
                if (!string.Equals(arg, store.ActiveVehicleProfileName, System.StringComparison.OrdinalIgnoreCase))
                {
                    cfg.SaveProfiles(store.ActiveVehicleProfileName, store.ActiveToolProfileName);
                    cfg.LoadProfiles(arg, store.ActiveToolProfileName);
                }
                return;
            case "profile.configureTool": // <name>
                if (!string.Equals(arg, store.ActiveToolProfileName, System.StringComparison.OrdinalIgnoreCase))
                {
                    cfg.SaveProfiles(store.ActiveVehicleProfileName, store.ActiveToolProfileName);
                    cfg.LoadProfiles(store.ActiveVehicleProfileName, arg);
                }
                return;
        }
    }

    // NTRIP profile CRUD for the remote Network IO editor. Mirrors the native
    // MainViewModel NTRIP commands but calls INtripProfileService directly (the VM's
    // commands also drive Avalonia chain-dialog state we don't want here). The browser
    // holds the editing buffer; save sends every field at once. Runs on the UI thread;
    // the async service calls are fire-and-forget (the read-frame re-sends on change).
    private static void ApplyNtripCommand(
        AgOpenWeb.Services.Interfaces.INtripProfileService svc,
        AgOpenWeb.Models.State.ApplicationState state, string cmd, string arg)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        switch (cmd)
        {
            case "ntrip.delete": // arg = id
                _ = svc.DeleteProfileAsync(arg);
                return;
            case "ntrip.setDefault": // arg = id
                _ = svc.SetDefaultProfileAsync(arg);
                return;
            case "ntrip.test": // arg = host \t port \t mount \t user \t pass
            {
                var t = arg.Split('\t');
                if (t.Length < 3) return;
                int.TryParse(t.ElementAtOrDefault(1), System.Globalization.NumberStyles.Integer, inv, out var tport);
                if (tport == 0) tport = 2101;
                state.Connections.NtripTestStatus = "Testing connection...";
                _ = TestNtripAndReportAsync(state, t[0], tport, t[2],
                    t.ElementAtOrDefault(3) ?? "", t.ElementAtOrDefault(4) ?? "");
                return;
            }
            case "ntrip.save": // id \t name \t host \t port \t mount \t user \t pass \t auto \t default \t assoc(csv)
            {
                var f = arg.Split('\t');
                if (f.Length < 9) return;
                var id = f[0];
                // Reuse the existing file path on edit so a rename doesn't orphan a file
                // (matches the native editor, which copies FilePath from the selection).
                var existing = string.IsNullOrEmpty(id)
                    ? null
                    : svc.Profiles.FirstOrDefault(p => p.Id == id);
                int.TryParse(f[3], System.Globalization.NumberStyles.Integer, inv, out var port);
                if (port == 0) port = 2101;
                var assoc = (f.ElementAtOrDefault(9) ?? "")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();
                var profile = new AgOpenWeb.Models.Ntrip.NtripProfile
                {
                    Name = f[1], CasterHost = f[2], CasterPort = port, MountPoint = f[4],
                    Username = f[5], Password = f[6],
                    AutoConnectOnFieldLoad = f[7] == "1", IsDefault = f[8] == "1",
                    AssociatedFields = assoc,
                };
                if (existing != null) { profile.Id = existing.Id; profile.FilePath = existing.FilePath; }
                _ = svc.SaveProfileAsync(profile);
                return;
            }
        }
    }

    // Field Operations write-side. New Field uses the VM's create command directly
    // (name + current GPS); everything else drives the host-side Fields-and-Jobs VM
    // (EnsureRemoteStartWorkSession) which reuses MainViewModel's field/job orchestration.
    private static void ApplyFieldCommand(AgOpenWeb.ViewModels.MainViewModel vm, string cmd, string arg)
    {
        var a = arg.Split('\t');
        if (cmd == "field.new") // arg = field name
        {
            if (string.IsNullOrWhiteSpace(arg)) return;
            vm.NewFieldLatitude = vm.Latitude != 0 ? vm.Latitude : 40.7128;
            vm.NewFieldLongitude = vm.Longitude != 0 ? vm.Longitude : -74.0060;
            vm.NewFieldName = arg;
            ExecCmd(vm.ConfirmNewFieldDialogCommand);
            return;
        }
        if (cmd == "field.fromExisting") // src \t newName \t flags \t mapping \t headland \t lines
        {
            if (a.Length >= 6)
                vm.RemoteCreateFromExisting(a[0], a[1], a[2] == "1", a[3] == "1", a[4] == "1", a[5] == "1");
            return;
        }
        if (cmd == "field.fromIsoXml") { if (a.Length >= 2) vm.RemoteCreateFromIsoXml(a[0], a[1]); return; } // file \t newName
        if (cmd == "field.fromKml") { if (a.Length >= 2) vm.RemoteCreateFromKml(a[0], a[1]); return; }       // file \t newName
        var sw = vm.EnsureRemoteStartWorkSession();
        sw.SelectedField = sw.Fields.FirstOrDefault(f =>
            string.Equals(f.Name, a[0], System.StringComparison.OrdinalIgnoreCase));
        switch (cmd)
        {
            case "field.openOnly":
                ExecCmd(sw.OpenFieldOnlyCommand);
                return;
            case "field.startJob": // field \t workType \t notes \t taskName
                sw.NewJobWorkType = a.ElementAtOrDefault(1) ?? "";
                sw.NewJobNotes = a.ElementAtOrDefault(2) ?? "";
                sw.NewJobTaskName = a.ElementAtOrDefault(3) ?? "";
                ExecCmd(sw.StartNewJobCommand);
                return;
            case "field.resumeJob": // field \t taskName
            {
                var job = sw.JobsForSelectedField.FirstOrDefault(j => j.TaskName == a.ElementAtOrDefault(1));
                if (job != null) sw.ResumeJobCommand.Execute(job);
                return;
            }
            case "field.deleteField":
                if (sw.DeleteFieldCommand.CanExecute(null)) sw.DeleteFieldCommand.Execute(null);
                return;
            case "field.deleteJob": // field \t taskName
            {
                var job = sw.JobsForSelectedField.FirstOrDefault(j => j.TaskName == a.ElementAtOrDefault(1));
                if (job != null && sw.DeleteJobCommand.CanExecute(job)) sw.DeleteJobCommand.Execute(job);
                return;
            }
        }
    }

    // File / Application Menu write-side. Reset (client-confirmed), hotkey edits, and bug-report
    // dump go straight to the services / ConfigStore (no native dialogs). Language rides the VM.
    private static void ApplyAppCommand(string cmd, string arg)
    {
        var configService = Services!.GetRequiredService<IConfigurationService>();
        var store = Services.GetRequiredService<AgOpenWeb.Models.Configuration.ConfigurationStore>();
        switch (cmd)
        {
            case "app.resetSettings":
                Services.GetRequiredService<ISettingsService>().ResetToDefaults();
                configService.LoadAppSettings();
                return;
            case "app.setHotkey": // arg = Action:Key
            {
                var ci = arg.IndexOf(':');
                if (ci > 0 && System.Enum.TryParse<AgOpenWeb.Models.Configuration.HotkeyAction>(arg[..ci], out var act))
                {
                    store.Hotkeys.SetKeyForAction(act, arg[(ci + 1)..]);
                    configService.SaveAppSettings();
                }
                return;
            }
            case "app.resetHotkeys":
                store.Hotkeys.ResetToDefaults();
                configService.SaveAppSettings();
                return;
            case "app.bugReport": // arg = title \t description
            {
                var t = arg.Split('\t');
                var title = t.ElementAtOrDefault(0) ?? "";
                var desc = t.ElementAtOrDefault(1) ?? "";
                var state = Services.GetRequiredService<AgOpenWeb.Models.State.ApplicationState>();
                state.BugReportStatus = "Creating bug report…";
                try
                {
                    var dir = System.IO.Path.Combine(
                        System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments),
                        "AgOpenWeb", "BugReports");
                    var slug = string.IsNullOrWhiteSpace(title) ? "untitled"
                        : new string(title.Trim().ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray());
                    if (slug.Length > 60) slug = slug.Substring(0, 60);
                    var notes = string.IsNullOrWhiteSpace(title) ? desc : "# " + title + "\n\n" + desc;
                    var zip = AgOpenWeb.Services.DebugDumpService.CreateDump(
                        Services.GetRequiredService<ISettingsService>(), state, store,
                        additionalNotes: notes, outputDirectory: dir, filePrefix: "bugreport_" + slug);
                    state.BugReportStatus = "Saved: " + zip;
                }
                catch (Exception ex) { state.BugReportStatus = "Error: " + ex.Message; }
                return;
            }
        }
    }

    private static async Task TestNtripAndReportAsync(
        AgOpenWeb.Models.State.ApplicationState state,
        string host, int port, string mount, string user, string pass)
    {
        var result = await AgOpenWeb.Services.NtripConnectionTester.TestAsync(host, port, mount, user, pass);
        // Marshal the status write through the registered dispatcher (Avalonia UI
        // thread when windowed, the host loop when headless) — not a hardcoded
        // Dispatcher.UIThread, which does not exist in the headless host.
        Services!.GetRequiredService<IUiDispatcher>().Post(() => state.Connections.NtripTestStatus = result);
    }

    private static void ExtractSoundFiles(IServiceProvider services)
    {
        try
        {
            var audioService = services.GetService<IAudioService>() as AgOpenWeb.Services.Audio.AudioServiceBase;
            if (audioService == null) return;

            var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AgOpenWeb", "Sounds");
            System.IO.Directory.CreateDirectory(tempDir);

            var soundFiles = AgOpenWeb.Services.Audio.AudioServiceBase.GetSoundFileNames();

            foreach (var fileName in soundFiles)
            {
                var destPath = System.IO.Path.Combine(tempDir, fileName);
                if (System.IO.File.Exists(destPath)) continue;

                try
                {
                    var uri = new Uri($"avares://AgOpenWeb.Views/Assets/Sounds/{fileName}");
                    using var stream = Avalonia.Platform.AssetLoader.Open(uri);
                    using var fileStream = System.IO.File.Create(destPath);
                    stream.CopyTo(fileStream);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Audio] Failed to extract {fileName}: {ex.Message}");
                }
            }

            audioService.SetSoundDirectory(tempDir);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Audio] Sound extraction failed: {ex.Message}");
        }
    }

}