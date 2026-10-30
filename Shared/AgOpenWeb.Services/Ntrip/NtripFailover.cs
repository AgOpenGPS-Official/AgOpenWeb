// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.Services;

/// <summary>When the client switches from the main to the fallback service, and what it must not do. Pure logic.</summary>
public static class NtripFailover
{
    /// <summary>Seconds between the background tries of the main service while the fallback is in use.</summary>
    public static readonly int[] ProbeBackoff = [30, 60, 120, 300];

    private const string RtkWarning = "GPS may lose RTK and the position may shift a little.";

    public static string NoticeText(NtripNoticeKind kind, bool automatic) => kind switch
    {
        NtripNoticeKind.SwitchedToFallback when automatic => "The main service failed, so the fallback service is in use. " + RtkWarning,
        NtripNoticeKind.SwitchedToFallback => "Switched to the fallback service. " + RtkWarning,
        NtripNoticeKind.SwitchedToMain => "Back on the main service. " + RtkWarning,
        _ => "The main service answers again. The fallback stays in use until you switch back.",
    };

    public static bool IsConfigured(NtripFallbackConfiguration? fallback) =>
        fallback != null && !string.IsNullOrWhiteSpace(fallback.CasterAddress) && !string.IsNullOrWhiteSpace(fallback.MountPoint);

    /// <summary>
    /// Main and fallback are the same login on the same caster (for example two mount points of one
    /// subscription). A second connection could push the first one off, so the main service is not
    /// tried in the background while the fallback is in use.
    /// </summary>
    public static bool SameAccount(NtripConfiguration main, NtripFallbackConfiguration fallback) =>
        string.Equals(main.CasterAddress.Trim(), fallback.CasterAddress.Trim(), StringComparison.OrdinalIgnoreCase)
        && main.CasterPort == fallback.CasterPort
        && string.Equals(main.Username, fallback.Username, StringComparison.Ordinal);

    /// <summary>
    /// The main service has failed enough for the switch to happen by itself: Auto-switch is on and
    /// it failed <see cref="NtripFallbackConfiguration.SwitchAfterFailures"/> attempts in a row, or
    /// it failed in a way retrying cannot fix (wrong login or mount point).
    /// </summary>
    public static bool ShouldSwitch(NtripSessionStatus mainStatus, NtripFallbackConfiguration fallback) =>
        fallback.AutoSwitch
        && (mainStatus.State == NtripState.GaveUp
            || (mainStatus.State == NtripState.Waiting && mainStatus.Attempts >= Math.Max(1, fallback.SwitchAfterFailures)));

    /// <summary>The configuration to connect to the fallback service: the main one's forwarding settings, the fallback's caster.</summary>
    public static NtripConfiguration ForFallback(NtripConfiguration main, NtripFallbackConfiguration fallback)
    {
        NtripConfiguration config = main.Copy();
        config.CasterAddress = fallback.CasterAddress;
        config.CasterPort = fallback.CasterPort;
        config.MountPoint = fallback.MountPoint;
        config.Username = fallback.Username;
        config.Password = fallback.Password;
        config.Fallback = null;
        return config;
    }
}
