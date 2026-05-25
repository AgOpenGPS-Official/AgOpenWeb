// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.Collections.Generic;
using System.Linq;
using AgValoniaGPS.Services.Storage;

namespace AgValoniaGPS.ViewModels;

/// <summary>
/// Startup crash-recovery notification. When <see cref="ISettingsService.Load"/>
/// or the active vehicle/tool profile load had to fall back to a <c>.bak</c>
/// last-known-good copy (the "settings reset on shutdown" failure mode), the
/// app already recovered the good data — this surfaces one consolidated prompt
/// so the user knows it happened and can choose to start fresh instead.
/// </summary>
public partial class MainViewModel
{
    private bool _startupRecoveryChecked;

    private void CheckStartupRecovery()
    {
        // Guard re-entrancy: a "start fresh" reset re-runs RestoreSettings,
        // which re-posts this; we only prompt once per launch.
        if (_startupRecoveryChecked)
            return;
        _startupRecoveryChecked = true;

        var restored = new List<string>();
        var lost = new List<string>();

        // Settings file
        switch (_settingsService.LastLoadStatus)
        {
            case LoadOutcome.RecoveredFromBackup:
                restored.Add($"• Settings ({DescribeBackup(_settingsService.RecoveredBackupTime)})");
                break;
            case LoadOutcome.CorruptNoBackup:
                lost.Add("• Settings");
                break;
        }

        // Active vehicle / tool profile pair
        var profileRecovery = _configurationService.LastRecovery;
        if (profileRecovery is not null)
        {
            foreach (var f in profileRecovery.Damaged)
            {
                if (f.Outcome == LoadOutcome.RecoveredFromBackup)
                    restored.Add($"• The {f.Label} profile ({DescribeBackup(f.BackupTimestamp)})");
                else if (f.Outcome == LoadOutcome.CorruptNoBackup)
                    lost.Add($"• The {f.Label} profile");
            }
        }

        if (restored.Count == 0 && lost.Count == 0)
            return; // clean startup — nothing to report

        bool settingsAffected = _settingsService.LastLoadStatus is
            LoadOutcome.RecoveredFromBackup or LoadOutcome.CorruptNoBackup;
        bool profilesAffected = profileRecovery?.NeedsPrompt == true;

        if (restored.Count > 0)
        {
            // At least one item was restored from its backup — offer the choice
            // between keeping the recovered data (the safe default, "No") and
            // starting fresh ("Yes", the explicit/destructive button).
            var msg = "The following were damaged on disk and have been restored from their "
                      + "last good backup:\n\n" + string.Join("\n", restored);
            if (lost.Count > 0)
            {
                msg += "\n\nThese had no usable backup and were reset to defaults:\n\n"
                       + string.Join("\n", lost);
            }
            msg += "\n\nKeep the restored settings, or start fresh with factory defaults?";

            ShowConfirmationDialog(
                "Settings Recovered",
                msg,
                confirmLabel: "New From Defaults",
                cancelLabel: "Use Restored",
                onConfirm: () => ResetStartupItemsToDefaults(settingsAffected, profilesAffected));
        }
        else
        {
            // Nothing was recoverable — purely informational.
            var msg = "The following were damaged on disk and no backup was available, "
                      + "so factory defaults were loaded:\n\n" + string.Join("\n", lost);
            ShowErrorDialog("Settings Reset", msg);
        }
    }

    private static string DescribeBackup(DateTime? when)
        => when.HasValue
            ? $"backup saved {when.Value:g}"
            : "last good backup";

    /// <summary>
    /// "Start fresh": reset the damaged items to factory defaults and persist,
    /// then re-apply via <see cref="RestoreSettings"/> (guarded against the
    /// re-posted recovery check). Only resets what was actually damaged so a
    /// recovered settings file doesn't wipe healthy profiles, and vice versa.
    /// </summary>
    private void ResetStartupItemsToDefaults(bool settingsAffected, bool profilesAffected)
    {
        if (profilesAffected)
        {
            var store = _configurationService.Store;
            var vehicle = string.IsNullOrEmpty(store.ActiveVehicleProfileName)
                ? "Default" : store.ActiveVehicleProfileName;
            var tool = string.IsNullOrEmpty(store.ActiveToolProfileName)
                ? vehicle : store.ActiveToolProfileName;

            _configurationService.CreateProfile(vehicle);      // store → defaults
            _configurationService.SaveProfiles(vehicle, tool); // overwrite healed files with defaults
        }

        if (settingsAffected)
        {
            _settingsService.ResetToDefaults();
            _settingsService.Save();
        }

        // Re-apply the now-default settings/profiles to the live UI. The guard
        // above stops this from re-prompting.
        RestoreSettings();
    }
}
