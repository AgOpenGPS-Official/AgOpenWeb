// AgValoniaGPS
// Copyright (C) 2024-2025 AgValoniaGPS Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System;
using System.Linq;
using System.Threading.Tasks;
using AgValoniaGPS.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace AgValoniaGPS.Desktop;

public partial class App
{
    // Wire the RemoteServer's command handler, Tier-2 gating, authority/failsafe
    // hooks, and every state projector to a live MainViewModel. Shared by the
    // windowed host (App.OnFrameworkInitializationCompleted) and the headless host
    // (HeadlessHost). The ONLY behavioural difference is the injected IUiDispatcher
    // resolved below: the Avalonia UI thread when windowed, the single-thread
    // HostLoopDispatcher when headless. Commands + projectors are otherwise
    // identical. See Plans/WEBUI_MIGRATION_PLAN.md Phase 10.
    internal static void WireRemoteServer(
        AgValoniaGPS.RemoteServer.RemoteServerHost server,
        AgValoniaGPS.ViewModels.MainViewModel vm,
        IServiceProvider services,
        IConfigurationService configService)
    {
        // UI-thread marshaller. Windowed => AvaloniaUiDispatcher (Dispatcher.UIThread);
        // headless => HostLoopDispatcher (the dedicated host-loop thread). Replaces the
        // old hardcoded Avalonia.Threading.Dispatcher.UIThread.Post in this block.
        var dispatcher = services.GetRequiredService<IUiDispatcher>();

        // Was the App instance field _remoteWizardActive; now a captured local
        // shared by the command-handler and wizard-projector closures below.
        bool wizardActive = false;

                    server.CommandHandler = (cmd, arg) =>
                        dispatcher.Post(() =>
                        {
                            var inv = System.Globalization.CultureInfo.InvariantCulture;
                            var num = System.Globalization.NumberStyles.Float;
                            // Sim ids that set a property or carry an arg (all Tier-1,
                            // hardware-safe) are handled directly; the rest map to a VM
                            // command below.
                            switch (cmd)
                            {
                                case "sim.toggleEnable":
                                    vm.IsSimulatorEnabled = !vm.IsSimulatorEnabled; return;
                                case "sim.toggle10x":
                                    vm.IsSimulatorSpeed10x = !vm.IsSimulatorSpeed10x; return;
                                case "sim.togglePanel": // show/hide the sim panel; persisted across runs
                                {
                                    var ps = services.GetRequiredService<IPersistentStateService>();
                                    ps.State.SimulatorPanelVisible = !ps.State.SimulatorPanelVisible;
                                    ps.Save();
                                    vm.IsSimulatorPanelVisible = ps.State.SimulatorPanelVisible;
                                    return;
                                }
                                case "sim.setSteer":
                                    if (double.TryParse(arg, num, inv, out var deg))
                                        vm.SimulatorSteerAngle = deg;
                                    return;
                                case "sim.setCoords":
                                    var parts = arg.Split(',');
                                    if (parts.Length == 2
                                        && double.TryParse(parts[0], num, inv, out var lat)
                                        && double.TryParse(parts[1], num, inv, out var lon))
                                        vm.SetSimulatorCoordinates(lat, lon);
                                    return;
                                case "section.toggle": // Tier-2 (gated); cycle one section
                                    if (int.TryParse(arg, System.Globalization.NumberStyles.Integer, inv, out var si)
                                        && vm.ToggleSectionCommand?.CanExecute(si) == true)
                                        vm.ToggleSectionCommand.Execute(si);
                                    return;
                                case "config.set": // config bridge (Phase 9a). arg = "key:value"
                                    var ci = arg.IndexOf(':');
                                    if (ci > 0)
                                        ApplyConfigSet(
                                            services.GetRequiredService<AgValoniaGPS.Models.Configuration.ConfigurationStore>(),
                                            configService, arg[..ci], arg[(ci + 1)..]);
                                    return;
                                case "roll.zeroCalibrate": // Tools→Roll Correction "Zero Roll":
                                    // capture the current live roll as the new zero offset.
                                    // Mirrors RollCalibrationStepViewModel.ZeroRollCommand
                                    // (RollZero += LiveRoll; LiveRoll is already post-calibration
                                    // so the running offset accumulates correctly on repeat presses).
                                {
                                    var rollStore = services.GetRequiredService<AgValoniaGPS.Models.Configuration.ConfigurationStore>();
                                    var rollState = services.GetRequiredService<AgValoniaGPS.Models.State.ApplicationState>();
                                    rollStore.Ahrs.RollZero += rollState.Vehicle.Roll;
                                    return;
                                }
                                case "field.deleteApplied": // Tier-1; browser already confirmed
                                    vm.DeleteAppliedAreaConfirmed();
                                    return;
                                case "field.importTracks": // arg = source field name (Tier-1)
                                    if (vm.ImportTracksFromFieldCommand?.CanExecute(arg) == true)
                                        vm.ImportTracksFromFieldCommand.Execute(arg);
                                    return;
                                case "recpath.save": // arg = name typed in the browser (Tier-1)
                                    vm.RecordedPathName = arg;
                                    vm.SaveNamedRecordedPathCommand?.Execute(null);
                                    return;
                                case "recpath.selectFile": // arg = .rec file name → load for playback (Tier-1)
                                    vm.SelectedRecFile = arg;
                                    return;
                                case "recpath.delete": // arg = .rec file name (Tier-1; browser confirmed)
                                    if (vm.DeleteRecordedPathCommand?.CanExecute(arg) == true)
                                        vm.DeleteRecordedPathCommand.Execute(arg);
                                    return;
                                case "boundary.refresh": // rebuild the menu list from the field (Tier-1)
                                    vm.RefreshBoundaryList();
                                    return;
                                case "boundary.select": // arg = boundary list index (Tier-1)
                                    if (int.TryParse(arg, out var bsi)) vm.SelectedBoundaryIndex = bsi;
                                    return;
                                case "boundary.setOffset": // arg = recording offset cm (Tier-1)
                                    if (double.TryParse(arg, num, inv, out var boff)) vm.BoundaryOffset = boff;
                                    return;
                                case "boundary.toggleSectionControl": // ToggleButton has no command (Tier-1)
                                    vm.IsBoundarySectionControlOn = !vm.IsBoundarySectionControlOn;
                                    return;
                                case "track.rename": // Field Builder. arg = "index,new name". Tier-2.
                                {
                                    var ri = arg.IndexOf(',');
                                    if (ri > 0 && int.TryParse(arg[..ri], out var rti))
                                        vm.RenameTrackAt(rti, arg[(ri + 1)..]);
                                    return;
                                }
                                case "track.select": // Tracks manager — tap a row. arg = index.
                                {                    // Mirrors native: tapping the active track
                                    if (int.TryParse(arg, out var tsi) // deactivates; else activates.
                                        && tsi >= 0 && tsi < vm.SavedTracks.Count)
                                    {
                                        var t = vm.SavedTracks[tsi];
                                        vm.SelectedTrack = vm.SelectedTrack == t ? null : t;
                                    }
                                    return;
                                }
                                case "track.setVisible": // arg = "index,0|1" — show/hide on map.
                                {
                                    var vp = arg.Split(',');
                                    if (vp.Length == 2 && int.TryParse(vp[0], out var tvi)
                                        && tvi >= 0 && tvi < vm.SavedTracks.Count)
                                    {
                                        vm.SavedTracks[tvi].IsVisible = vp[1] == "1";
                                        vm.OnTrackVisibilityChanged();
                                    }
                                    return;
                                }
                                case "headland.fromMapPoints": // Field Builder stage 2. arg =
                                {                              // "<line|curve>,offset,e1,n1,e2,n2"
                                    var hp = arg.Split(',');   // (m). Host snaps to boundary +
                                    if (hp.Length >= 6         // builds the segment + headland.
                                        && double.TryParse(hp[1], num, inv, out var hoff)
                                        && double.TryParse(hp[2], num, inv, out var he1)
                                        && double.TryParse(hp[3], num, inv, out var hn1)
                                        && double.TryParse(hp[4], num, inv, out var he2)
                                        && double.TryParse(hp[5], num, inv, out var hn2))
                                        vm.RemoteCreateHeadlandFromMapPoints(
                                            hp[0] == "curve", hoff, he1, hn1, he2, hn2);
                                    return;
                                }
                                case "headland.wholeBoundary": // arg = offset (m). Whole boundary
                                    if (double.TryParse(arg, num, inv, out var hwb)) // offset inward.
                                        vm.RemoteCreateHeadlandWholeBoundary(hwb);
                                    return;
                                case "headland.setOffset": // arg = "index,offset" (m).
                                {
                                    var so = arg.Split(',');
                                    if (so.Length == 2 && int.TryParse(so[0], out var hsi)
                                        && double.TryParse(so[1], num, inv, out var hso))
                                        vm.RemoteSetHeadlandOffsetAt(hsi, hso);
                                    return;
                                }
                                case "headland.delete": // arg = segment index.
                                    if (int.TryParse(arg, out var hdi)) vm.RemoteDeleteHeadlandAt(hdi);
                                    return;
                                case "headland.deleteAll":
                                    vm.RemoteDeleteAllHeadland();
                                    return;
                                case "headland.rename": // arg = "index,new name" (name may contain commas)
                                {
                                    var hr = arg.IndexOf(',');
                                    if (hr > 0 && int.TryParse(arg[..hr], out var hri))
                                        vm.RemoteRenameHeadlandAt(hri, arg[(hr + 1)..]);
                                    return;
                                }
                                case "track.editSave": // stage 4 on-map edit. arg = "index;e,n;e,n;…"
                                {
                                    var teParts = arg.Split(';');
                                    if (teParts.Length >= 3 && int.TryParse(teParts[0], out var tei))
                                    {
                                        var ep = new System.Collections.Generic.List<(double, double)>();
                                        for (int k = 1; k < teParts.Length; k++)
                                        {
                                            var en = teParts[k].Split(',');
                                            if (en.Length == 2
                                                && double.TryParse(en[0], num, inv, out var pe)
                                                && double.TryParse(en[1], num, inv, out var pn))
                                                ep.Add((pe, pn));
                                        }
                                        if (ep.Count >= 2) vm.RemoteSaveTrackEdit(tei, ep);
                                    }
                                    return;
                                }
                                case "headland.editSave": // stage 4. arg = "index,e1,n1,e2,n2"
                                {
                                    var hp = arg.Split(',');
                                    if (hp.Length >= 5 && int.TryParse(hp[0], out var hei)
                                        && double.TryParse(hp[1], num, inv, out var he1)
                                        && double.TryParse(hp[2], num, inv, out var hn1)
                                        && double.TryParse(hp[3], num, inv, out var he2)
                                        && double.TryParse(hp[4], num, inv, out var hn2))
                                        vm.RemoteSaveHeadlandEdit(hei, he1, hn1, he2, hn2);
                                    return;
                                }
                                case "tram.add": // Field Builder Tram tab — add a system.
                                    vm.RemoteAddTramSystem();
                                    return;
                                case "tram.delete": // arg = system index.
                                    if (int.TryParse(arg, out var tdi)) vm.RemoteDeleteTramSystemAt(tdi);
                                    return;
                                case "tram.set": // arg = "index,field,value" (value may contain commas: track name)
                                {
                                    var t1 = arg.IndexOf(',');
                                    var t2 = t1 >= 0 ? arg.IndexOf(',', t1 + 1) : -1;
                                    if (t2 > t1 && int.TryParse(arg[..t1], out var tsi))
                                        vm.RemoteSetTramField(tsi, arg[(t1 + 1)..t2], arg[(t2 + 1)..]);
                                    return;
                                }
                                case "boundary.importKmlFile": // Import-KML picker. arg = KML file name.
                                    vm.RemoteImportKmlBoundary(arg);
                                    return;
                                case "boundary.fromMapPoints": // Phase MT — Draw boundary on map.
                                {                              // arg = "e,n;e,n;…" (field E/N from s2w).
                                    var bmp = new System.Collections.Generic.List<(double, double)>();
                                    foreach (var pair in arg.Split(';'))
                                    {
                                        var en = pair.Split(',');
                                        if (en.Length == 2
                                            && double.TryParse(en[0], num, inv, out var pe)
                                            && double.TryParse(en[1], num, inv, out var pn))
                                            bmp.Add((pe, pn));
                                    }
                                    vm.RemoteCreateBoundaryFromMapPoints(bmp);
                                    // Capture + save the aerial imagery covering the boundary in a
                                    // CRASH-ISOLATED child process — SkiaSharp can hard-crash on a
                                    // headless board and must never take the host down. On success,
                                    // apply the background back on the UI thread.
                                    if (vm.GetBoundaryImageryBounds(bmp) is { } bb)
                                        _ = System.Threading.Tasks.Task.Run(async () =>
                                        {
                                            var outPath = System.IO.Path.Combine(
                                                System.IO.Path.GetTempPath(), "AgValoniaGPS_SatCap",
                                                "BackPic_" + System.Guid.NewGuid().ToString("N") + ".png");
                                            var ok = await ImageryCaptureProcess.TryCaptureAsync(
                                                bb.mercMinX, bb.mercMaxX, bb.mercMinY, bb.mercMaxY, outPath);
                                            if (ok)
                                                dispatcher.Post(() =>
                                                    vm.ApplyCapturedBackground(outPath,
                                                        bb.nwLat, bb.nwLon, bb.seLat, bb.seLon,
                                                        bb.mercMinX, bb.mercMaxX, bb.mercMinY, bb.mercMaxY));
                                        });
                                    return;
                                }
                                case "track.drawPoint": // Phase MT — map-tap AB/curve point.
                                {                       // arg = "e,n" (m, from s2w). Routes to the
                                    var tp = arg.Split(','); // native SetABPointCommand (DrawAB =
                                    if (tp.Length >= 2  // tap A then B; DrawCurve = tap N points).
                                        && double.TryParse(tp[0], num, inv, out var te)
                                        && double.TryParse(tp[1], num, inv, out var tn))
                                    {
                                        var pos = new AgValoniaGPS.Models.Position { Easting = te, Northing = tn };
                                        if (vm.SetABPointCommand?.CanExecute(pos) == true)
                                            vm.SetABPointCommand.Execute(pos);
                                    }
                                    return;
                                }
                                case "flag.placeAt": // Phase MT map-tap. arg = "easting,northing"
                                {                     // (m, field-local, from s2w). Tier-1 marker.
                                    var fp = arg.Split(',');
                                    if (fp.Length >= 2
                                        && double.TryParse(fp[0], num, inv, out var fe)
                                        && double.TryParse(fp[1], num, inv, out var fn))
                                        vm.PlaceFlagAtWorldPosition(fe, fn);
                                    return;
                                }
                                // Flag list (Phase MT). Index into the projected flag list. Tier-1.
                                case "flag.delete":
                                    if (int.TryParse(arg, out var fdi)) vm.DeleteFlagAt(fdi);
                                    return;
                                case "flag.deleteAll":
                                    vm.DeleteAllFlagsRemote();
                                    return;
                                case "flag.rename": // arg = "index,new name" (name may contain commas)
                                {
                                    var fc = arg.IndexOf(',');
                                    if (fc > 0 && int.TryParse(arg[..fc], out var fri))
                                        vm.RenameFlagAt(fri, arg[(fc + 1)..]);
                                    return;
                                }
                                case "flag.setColor": // arg = "index,ColorName" (FlagColor enum)
                                {
                                    var sc = arg.Split(',');
                                    if (sc.Length == 2 && int.TryParse(sc[0], out var fsi)
                                        && Enum.TryParse<AgValoniaGPS.Models.FlagColor>(sc[1], out var col))
                                        vm.SetFlagColorAt(fsi, col);
                                    return;
                                }
                                case "offset.set": // Offset Fix manual entry. arg = "easting,northing" (m)
                                {
                                    var op = arg.Split(',');
                                    if (op.Length == 2
                                        && double.TryParse(op[0], num, inv, out var oe)
                                        && double.TryParse(op[1], num, inv, out var on))
                                        vm.OffsetFixSet(oe, on);
                                    return;
                                }
                                case "profile.save": // persist active vehicle+tool profiles (Phase 9b)
                                    var cs = services.GetRequiredService<AgValoniaGPS.Models.Configuration.ConfigurationStore>();
                                    configService.SaveProfiles(cs.ActiveVehicleProfileName, cs.ActiveToolProfileName);
                                    return;
                                // --- Vehicle & Tool picker hub (Phase 9). Args are tab-separated. ---
                                case "profile.load": case "profile.new": case "profile.delete":
                                case "profile.rename": case "profile.reset":
                                case "profile.configureVehicle": case "profile.configureTool":
                                    ApplyProfileCommand(
                                        services.GetRequiredService<AgValoniaGPS.Models.Configuration.ConfigurationStore>(),
                                        configService, cmd, arg);
                                    return;
                                // --- AutoSteer config hardware-push actions (Phase 9). All
                                // route through the AutoSteerConfigViewModel so the PGN /
                                // MarkChanged / free-drive logic is shared with native, not
                                // duplicated. All "autosteer." → Tier-2 gated (push to module
                                // / motor actuation). Plain field edits ride config.set above.
                                case "autosteer.sendSave": case "autosteer.zeroWas":
                                case "autosteer.reset":
                                case "autosteer.freedrive.toggle": case "autosteer.freedrive.left":
                                case "autosteer.freedrive.right": case "autosteer.freedrive.center":
                                {
                                    vm.EnsureAutoSteerConfigViewModel();
                                    var asvm = vm.AutoSteerConfigViewModel!;
                                    System.Windows.Input.ICommand? ac = cmd switch
                                    {
                                        "autosteer.sendSave" => asvm.SendAndSaveCommand,
                                        "autosteer.zeroWas" => asvm.ZeroWasCommand,
                                        "autosteer.reset" => asvm.ConfirmResetCommand, // web shows its own confirm
                                        "autosteer.freedrive.toggle" => asvm.ToggleFreeDriveCommand,
                                        "autosteer.freedrive.left" => asvm.SteerLeftCommand,
                                        "autosteer.freedrive.right" => asvm.SteerRightCommand,
                                        "autosteer.freedrive.center" => asvm.SteerCenterCommand,
                                        _ => null,
                                    };
                                    if (ac?.CanExecute(null) == true) ac.Execute(null);
                                    return;
                                }
                                // --- Smart-WAS calibration dialog actions. Routed through
                                // SmartWasViewModel (shared WAS-offset/PGN/buffer logic).
                                // All "smartwas." → Tier-2 gated (calibrate while engaged). ---
                                case "smartwas.start": case "smartwas.stop":
                                case "smartwas.reset": case "smartwas.apply":
                                {
                                    vm.EnsureSmartWasViewModel();
                                    var sw = vm.SmartWasViewModel!;
                                    System.Windows.Input.ICommand? sc = cmd switch
                                    {
                                        "smartwas.start" => sw.StartCommand,
                                        "smartwas.stop" => sw.StopCommand,
                                        "smartwas.reset" => sw.ResetCommand,
                                        "smartwas.apply" => sw.ApplyCommand,
                                        _ => null,
                                    };
                                    if (sc?.CanExecute(null) == true) sc.Execute(null);
                                    return;
                                }
                                // --- Steer Wizard (host-driven). The real SteerWizardViewModel
                                // runs here; the browser forwards nav + edits + actions. Only
                                // wizard.action (calibration/actuation) is Tier-2 gated. ---
                                case "wizard.open":
                                    vm.StartRemoteSteerWizard();
                                    wizardActive = true;
                                    return;
                                case "wizard.cancel": case "wizard.close":
                                    vm.SteerWizardViewModel?.CancelCommand.Execute(null);
                                    vm.EndRemoteSteerWizard();
                                    wizardActive = false;
                                    return;
                                case "wizard.next": ExecCmd(vm.SteerWizardViewModel?.NextCommand); return;
                                case "wizard.back": ExecCmd(vm.SteerWizardViewModel?.BackCommand); return;
                                case "wizard.skip": ExecCmd(vm.SteerWizardViewModel?.SkipCommand); return;
                                case "wizard.finish":
                                    ExecCmd(vm.SteerWizardViewModel?.FinishCommand);
                                    vm.EndRemoteSteerWizard();
                                    wizardActive = false;
                                    return;
                                case "wizard.hw": // arg = hardware level 0/1/2
                                    SetWizardProp(vm.SteerWizardViewModel, "HardwareLevel", arg);
                                    return;
                                case "wizard.set": // arg = "Property:value" on the current step
                                {
                                    var wi = arg.IndexOf(':');
                                    if (wi > 0) SetWizardProp(vm.SteerWizardViewModel, arg[..wi], arg[(wi + 1)..]);
                                    return;
                                }
                                case "wizard.action": // arg = command base name on the current step
                                    InvokeWizardAction(vm.SteerWizardViewModel, arg);
                                    return;
                                // --- Network IO (Phase 9). Module scan (PGN 202) is a harmless
                                // probe (Tier-1); the subnet change (PGN 201) restarts every
                                // module, so it is gated (Tier-2). Both go straight to the UDP
                                // service — no PGN logic in JS. ---
                                case "net.scan":
                                    services.GetRequiredService<IUdpCommunicationService>().ScanModules();
                                    return;
                                case "net.subnet": // arg = "o1.o2.o3" (gated)
                                {
                                    var oct = arg.Split('.');
                                    if (oct.Length == 3
                                        && byte.TryParse(oct[0], out var o1)
                                        && byte.TryParse(oct[1], out var o2)
                                        && byte.TryParse(oct[2], out var o3))
                                        services.GetRequiredService<IUdpCommunicationService>().SetModuleSubnet(o1, o2, o3);
                                    return;
                                }
                                // --- NTRIP profile CRUD (Phase 9). Calls INtripProfileService
                                // directly (mirrors the vehicle/tool ApplyProfileCommand path);
                                // confirmations happen client-side. Tier-1 (config files). ---
                                case "ntrip.save": case "ntrip.delete": case "ntrip.setDefault":
                                case "ntrip.test":
                                    ApplyNtripCommand(
                                        services.GetRequiredService<INtripProfileService>(),
                                        services.GetRequiredService<AgValoniaGPS.Models.State.ApplicationState>(),
                                        cmd, arg);
                                    return;
                                // --- Field Operations (Phase 9). Lifecycle routes through the real
                                // StartWorkSessionDialogViewModel (host-driven) so field/job open/
                                // start/resume/delete reuse MainViewModel's orchestration; deletes
                                // are confirmed client-side. Tier-1 (data management, not actuation). ---
                                case "field.openOnly": case "field.startJob": case "field.resumeJob":
                                case "field.deleteField": case "field.deleteJob": case "field.new":
                                case "field.fromExisting": case "field.fromIsoXml": case "field.fromKml":
                                    ApplyFieldCommand(vm, cmd, arg);
                                    return;
                                case "field.resumeLast": ExecCmd(vm.ResumeLastJobCommand); return;
                                case "field.driveIn": ExecCmd(vm.DriveInCommand); return;
                                case "field.close": ExecCmd(vm.CloseFieldCommand); return;
                                // --- AgShare cloud sync (test / fetch / download / upload).
                                // Replicates the dialog code-behind orchestration host-side;
                                // results land in ApplicationState.AgShare. Tier-1. ---
                                case "agshare.test": case "agshare.fetch": case "agshare.download":
                                case "agshare.downloadAll": case "agshare.upload":
                                    AgShareRemote.Handle(cmd, arg,
                                        services.GetRequiredService<AgValoniaGPS.Models.State.ApplicationState>(),
                                        services.GetRequiredService<AgValoniaGPS.Models.Configuration.ConfigurationStore>(),
                                        services.GetRequiredService<ISettingsService>());
                                    return;
                                // --- File / Application Menu (Phase 9). Language via the VM
                                // command; reset/hotkeys/bug-report via services/ConfigStore
                                // (reset confirmed client-side). Tier-1 (settings/data). ---
                                case "app.setLanguage":
                                    if (vm.SetLanguageCommand?.CanExecute(arg) == true) vm.SetLanguageCommand.Execute(arg);
                                    return;
                                case "app.resetSettings": case "app.setHotkey":
                                case "app.resetHotkeys": case "app.bugReport":
                                    ApplyAppCommand(cmd, arg);
                                    return;
                            }

                            System.Windows.Input.ICommand? c = cmd switch
                            {
                                "sim.steerLeft" => vm.SimulatorSteerLeftCommand,
                                "sim.steerRight" => vm.SimulatorSteerRightCommand,
                                "sim.speedUp" => vm.SimulatorSpeedUpCommand,
                                "sim.speedDown" => vm.SimulatorSpeedDownCommand,
                                "sim.stop" => vm.SimulatorStopCommand,
                                "sim.reset" => vm.ResetSimulatorCommand,
                                "sim.steerReset" => vm.ResetSteerAngleCommand,
                                "sim.reverseDir" => vm.SimulatorReverseDirectionCommand,
                                // Right-nav operational toolbar (Tier-2).
                                "contour.toggle" => vm.ToggleContourModeCommand,
                                "section.master" => vm.ToggleSectionMasterCommand,
                                "section.manual" => vm.ToggleManualModeCommand,
                                "youturn.toggle" => vm.ToggleYouTurnCommand,
                                "youturn.direction" => vm.ToggleUTurnDirectionCommand,
                                "youturn.manualLeft" => vm.ManualYouTurnLeftCommand,
                                "youturn.manualRight" => vm.ManualYouTurnRightCommand,
                                "autosteer.toggle" => vm.ToggleAutoSteerCommand,
                                // Screen & Alerts (Phase 9): theme + display-quality cycle.
                                "display.toggleTheme" => vm.ToggleDayNightCommand,
                                "display.cycleResolution" => vm.CycleDisplayResolutionCommand,
                                // Bottom-nav field tools (Phase 8). track.* + headland.*
                                // are Tier-2 (guidance/section actuation, gated); the
                                // tool./map./flag./tram. ids are Tier-1 (display/markers).
                                "youturn.skipCycle" => vm.CycleUTurnSkipRowsCommand,
                                "youturn.skipToggle" => vm.ToggleUTurnSkipRowsCommand,
                                "headland.toggle" => vm.ToggleHeadlandCommand,
                                "headland.sectionToggle" => vm.ToggleSectionInHeadlandCommand,
                                "tram.cycle" => vm.ToggleTramDisplayCommand,
                                "tool.resetHeading" => vm.ResetToolHeadingCommand,
                                "map.cycleColor" => vm.ChangeMappingColorCommand,
                                "flag.placeHere" => vm.PlaceFlagHereCommand,
                                "track.snapLeft" => vm.SnapLeftCommand,
                                "track.snapRight" => vm.SnapRightCommand,
                                "track.snapPivot" => vm.SnapToPivotCommand,
                                "track.nudgeLeft" => vm.NudgeLeftCommand,
                                "track.nudgeRight" => vm.NudgeRightCommand,
                                "track.fineNudgeLeft" => vm.FineNudgeLeftCommand,
                                "track.fineNudgeRight" => vm.FineNudgeRightCommand,
                                "track.halfNudgeLeft" => vm.HalfToolNudgeLeftCommand,
                                "track.halfNudgeRight" => vm.HalfToolNudgeRightCommand,
                                "track.resetNudge" => vm.ResetNudgeCommand,
                                "track.cycle" => vm.CycleABLinesCommand,
                                "track.smooth" => vm.SmoothABLineCommand,
                                "track.deleteContours" => vm.DeleteContoursCommand,
                                "track.autoTrack" => vm.ToggleAutoTrackCommand,
                                "track.createFromBoundary" => vm.CreateTrackFromBoundaryCommand,
                                // Phase MT — draw-on-map track creation (mirrors native
                                // DrawABDialog). The point taps ride track.drawPoint (arg
                                // switch above); these are the mode/lifecycle + boundary-
                                // based creators. All Tier-2 (track.* changes guidance).
                                "track.drawStraight" => vm.StartDrawABModeCommand,
                                "track.drawCurve" => vm.StartDrawCurveModeCommand,
                                "track.drawFinish" => vm.FinishDrawCurveCommand,
                                "track.drawUndo" => vm.UndoLastDrawnPointCommand,
                                "track.drawCancel" => vm.CancelABCreationCommand,
                                "track.aPlus" => vm.StartAPlusLineCommand,
                                "track.boundaryCurve" => vm.CreateCurveFromBoundaryCommand,
                                "track.allEdges" => vm.CreateTracksFromAllEdgesCommand,
                                "track.deleteAll" => vm.DeleteAllTracksCommand, // Field Builder
                                // Quick-AB selector (GPS-driven): drive A→B, record a curve
                                // by driving, and set-point-at-vehicle (param ignored in
                                // DriveAB/Curve modes → uses live GPS).
                                "track.driveAB" => vm.StartDriveABCommand,
                                "track.recordCurve" => vm.StartCurveRecordingCommand,
                                "track.finishCurve" => vm.FinishCurveRecordingCommand,
                                "track.setABGps" => vm.SetABPointCommand,
                                // Tracks manager toolbar (act on the active/selected track).
                                "track.delete" => vm.DeleteContourTrackCommand,
                                "track.swapAB" => vm.SwapABPointsCommand,
                                "track.activate" => vm.SelectTrackAsActiveCommand,
                                "track.toggleRecPaths" => vm.ToggleRecordedPathsCommand,
                                // Field Tools — Recorded Path. Record/save/select are Tier-1
                                // (data); recpath.play drives the vehicle → Tier-2 (gated below).
                                "recpath.start" => vm.StartRecordedPathCommand,
                                "recpath.stop" => vm.StopRecordedPathCommand,
                                "recpath.play" => vm.PlayRecordedPathCommand,
                                "recpath.cycleResume" => vm.CycleResumeModeCommand,
                                "recpath.reverse" => vm.ReverseRecordedPathCommand,
                                "recpath.turnOff" => vm.TurnOffRecordedPathCommand,
                                // Field Tools — Boundary (Tier-1; geometry capture, not steering).
                                // Menu (operate on SelectedBoundaryIndex):
                                "boundary.delete" => vm.DeleteBoundaryCommand,
                                "boundary.driveThru" => vm.ToggleDriveThroughCommand,
                                "boundary.hard" => vm.ToggleHardCommand,
                                "boundary.importKml" => vm.ImportKmlBoundaryCommand,
                                "boundary.buildFromTracks" => vm.BuildFromTracksCommand,
                                "boundary.driveAround" => vm.DriveAroundFieldCommand,
                                "boundary.driveAroundInner" => vm.DriveAroundInnerBoundaryCommand,
                                "boundary.accept" => vm.ToggleBoundaryPanelCommand,
                                // Player (drive-around recording):
                                "boundary.clear" => vm.ClearBoundaryCommand,
                                "boundary.undo" => vm.UndoBoundaryPointCommand,
                                "boundary.addPoint" => vm.AddBoundaryPointCommand,
                                "boundary.toggleLeftRight" => vm.ToggleBoundaryLeftRightCommand,
                                "boundary.toggleAntennaTool" => vm.ToggleBoundaryAntennaToolCommand,
                                "boundary.toggleRecording" => vm.ToggleRecordingCommand,
                                "boundary.stop" => vm.StopBoundaryRecordingCommand,
                                // Field Tools — Offset Fix D-pad (Tier-1; GPS drift nudge, 1 cm/click).
                                "offset.north" => vm.OffsetFixNorthCommand,
                                "offset.south" => vm.OffsetFixSouthCommand,
                                "offset.east" => vm.OffsetFixEastCommand,
                                "offset.west" => vm.OffsetFixWestCommand,
                                "offset.zero" => vm.OffsetFixZeroCommand,
                                _ => null, // unknown id → ignored (safety boundary)
                            };
                            if (c?.CanExecute(null) == true) c.Execute(null);
                        });

                    // Tier-2 (live actuation) ids — honored only while a client holds
                    // fresh control authority (Phase 2 safety gate).
                    server.IsRestrictedCommand = id =>
                        id.StartsWith("section.") || id.StartsWith("autosteer.")
                        || id.StartsWith("youturn.") || id.StartsWith("contour.")
                        || (id.StartsWith("track.") && !UngatedTrackIds.Contains(id)) // see below
                        || (id.StartsWith("headland.") && !UngatedHeadlandIds.Contains(id))
                        || id.StartsWith("smartwas.") || id.StartsWith("wizard.action")
                        || id == "net.subnet" // restarts every module → gate it
                        || id == "recpath.play"; // drives the vehicle along the path → actuation

                    // One operator, via the browser. When the control session ends —
                    // release, disconnect, or deadman — the machine must not keep
                    // actuating with no interface: disengage autosteer and turn sections
                    // off. (Headless target → control state lives in the browser.)
                    server.AuthorityChangedHandler = (held, name) =>
                    {
                        if (held)
                        {
                            System.Diagnostics.Debug.WriteLine($"[remote] control taken by {name}");
                            return;
                        }
                        dispatcher.Post(() =>
                        {
                            if (vm.IsAutoSteerEngaged
                                && vm.ToggleAutoSteerCommand?.CanExecute(null) == true)
                                vm.ToggleAutoSteerCommand.Execute(null);
                            if (vm.IsManualSectionMode
                                && vm.ToggleManualModeCommand?.CanExecute(null) == true)
                                vm.ToggleManualModeCommand.Execute(null);
                            if (vm.IsSectionMasterOn
                                && vm.ToggleSectionMasterCommand?.CanExecute(null) == true)
                                vm.ToggleSectionMasterCommand.Execute(null);
                        });
                    };

                    // Involuntary loss (disconnect / deadman) — log the reason; the
                    // disengage runs in AuthorityChangedHandler (covers release too).
                    server.FailsafeHandler = reason =>
                        System.Diagnostics.Debug.WriteLine($"[remote] actuation failsafe: {reason}");

                    // Steer Wizard projector: while the remote wizard is open, project the
                    // live SteerWizardViewModel to a WizardDto each broadcast tick. Read off
                    // the broadcaster thread (read-only; tolerates the same transient races
                    // as the other projectors).
                    server.WizardProvider = () =>
                        wizardActive && vm.SteerWizardViewModel is { } w
                            ? BuildWizardDto(w) : null;

                    // Recorded Path projector: the panel's UI state (IsRecordingPath,
                    // HasUnsaved, info/label) is VM-owned, so project it from the live VM
                    // each tick; the .rec file list comes off disk. Read-only on the
                    // broadcaster thread, same race tolerance as the other projectors.
                    server.RecordedPathProvider = () =>
                    {
                        var dir = services.GetRequiredService<AgValoniaGPS.Services.IFieldService>()
                            .ActiveField?.DirectoryPath;
                        var recFiles = !string.IsNullOrEmpty(dir)
                            ? AgValoniaGPS.Services.RecPathFileService.ListRecFiles(dir)
                            : new System.Collections.Generic.List<string>();
                        var st = services.GetRequiredService<AgValoniaGPS.Models.State.ApplicationState>();
                        var live = vm.LiveRecordingPoints;
                        var pts = new System.Collections.Generic.List<double>(live.Count * 2);
                        foreach (var p in live) { pts.Add(p.Easting); pts.Add(p.Northing); }
                        return new AgValoniaGPS.RemoteServer.RecordedPathDto(
                            recFiles,
                            vm.IsRecordingPath,
                            st.RecordedPath.IsDrivingRecordedPath,
                            vm.HasUnsavedRecordedPath,
                            vm.RecordedPathInfo ?? "",
                            vm.ResumeModeLabel ?? "Start",
                            vm.RecordedPathName ?? "",
                            pts);
                    };

                    // Boundary projector: the menu list (VM BoundaryItems) + live
                    // drive-around recording metrics/points (IBoundaryRecordingService is
                    // the SoT) + the VM-owned record toggles. Read-only on the broadcaster
                    // thread, same race tolerance as the other projectors.
                    server.BoundaryProvider = () =>
                    {
                        var bst = services.GetRequiredService<AgValoniaGPS.Models.State.ApplicationState>();
                        var brs = services.GetRequiredService<AgValoniaGPS.Services.Interfaces.IBoundaryRecordingService>();
                        var items = new System.Collections.Generic.List<AgValoniaGPS.RemoteServer.BoundaryItemDto>();
                        foreach (var it in vm.BoundaryItems)
                            items.Add(new AgValoniaGPS.RemoteServer.BoundaryItemDto(
                                it.Index, it.BoundaryType, it.AreaDisplay, it.IsDriveThrough, it.IsHard));
                        var bpts = new System.Collections.Generic.List<double>(brs.RecordedPoints.Count * 2);
                        foreach (var p in brs.RecordedPoints) { bpts.Add(p.Easting); bpts.Add(p.Northing); }
                        return new AgValoniaGPS.RemoteServer.BoundaryDto(
                            items,
                            vm.SelectedBoundaryIndex,
                            vm.IsBoundaryPlayerPanelVisible,
                            brs.IsRecording,
                            bst.BoundaryRec.IsPaused,
                            brs.PointCount,
                            brs.AreaHectares,
                            vm.BoundaryOffset,
                            vm.IsDrawRightSide,
                            vm.IsDrawAtPivot,
                            vm.IsBoundarySectionControlOn,
                            bpts);
                    };

                    // Field Builder Headland-tab list: the segments live on the VM
                    // (MainViewModel.HeadlandSegments — no ApplicationState SoT), so project
                    // them from the live VM each tick. Read-only on the broadcaster thread,
                    // same transient-race tolerance as the other VM-coupled projectors.
                    server.HeadlandSegsProvider = () =>
                    {
                        var segs = vm.HeadlandSegments;
                        var list = new System.Collections.Generic.List<AgValoniaGPS.RemoteServer.HeadlandSegInfoDto>(segs.Count);
                        for (int i = 0; i < segs.Count; i++)
                        {
                            var s = segs[i];
                            var edit = vm.GetHeadlandSegmentEditLine(s);
                            var editPts = new System.Collections.Generic.List<AgValoniaGPS.RemoteServer.Vec2Dto>(edit.Count);
                            foreach (var p in edit) editPts.Add(new AgValoniaGPS.RemoteServer.Vec2Dto(p.Easting, p.Northing));
                            var bp = s.BoundaryPoints;
                            var endA = bp.Count > 0 ? new AgValoniaGPS.RemoteServer.Vec2Dto(bp[0].Easting, bp[0].Northing) : new AgValoniaGPS.RemoteServer.Vec2Dto(0, 0);
                            var endB = bp.Count > 0 ? new AgValoniaGPS.RemoteServer.Vec2Dto(bp[^1].Easting, bp[^1].Northing) : new AgValoniaGPS.RemoteServer.Vec2Dto(0, 0);
                            list.Add(new AgValoniaGPS.RemoteServer.HeadlandSegInfoDto(
                                i, s.Name, s.Type.ToString(), s.Offset, s.IsEffective, editPts, endA, endB));
                        }
                        return list;
                    };

                    // Tram lines: the generated geometry lives in ITramLineService (pipeline
                    // state, not injected into the projector), so project it here. Native draws
                    // FOUR collections (SetTramLines): the outer + inner boundary tracks, the
                    // parallel lines, and the boundary-extra passes — gather them all.
                    var tramSvc = services.GetRequiredService<AgValoniaGPS.Services.Interfaces.ITramLineService>();
                    server.TramLinesProvider = () =>
                    {
                        var outLines = new System.Collections.Generic.List<System.Collections.Generic.IReadOnlyList<AgValoniaGPS.RemoteServer.Vec2Dto>>();
                        void Add(System.Collections.Generic.IReadOnlyList<AgValoniaGPS.Models.Base.Vec2> line)
                        {
                            if (line == null || line.Count < 2) return;
                            var pl = new System.Collections.Generic.List<AgValoniaGPS.RemoteServer.Vec2Dto>(line.Count);
                            foreach (var p in line) pl.Add(new AgValoniaGPS.RemoteServer.Vec2Dto(p.Easting, p.Northing));
                            outLines.Add(pl);
                        }
                        Add(tramSvc.OuterBoundaryTrack);
                        Add(tramSvc.InnerBoundaryTrack);
                        foreach (var line in tramSvc.ParallelTramLines) Add(line);
                        foreach (var line in tramSvc.BoundaryExtraLines) Add(line);
                        return outLines;
                    };
    }
}
