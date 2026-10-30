// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

namespace AgOpenWeb.Models.Ntrip;

/// <summary>
/// The service a profile falls back to when its own (the main service) fails. Used only in the
/// "main + fallback" mode; the "profile per field" mode ignores it.
/// </summary>
public class NtripFallback
{
    public string CasterHost { get; set; } = string.Empty;

    public int CasterPort { get; set; } = 2101;

    public string MountPoint { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Switch to this service on its own once the main service has failed
    /// <see cref="SwitchAfterFailures"/> times in a row (a wrong login or mount point counts at once).
    /// Off: the failure is reported and the operator switches.
    /// </summary>
    public bool AutoSwitch { get; set; }

    public int SwitchAfterFailures { get; set; } = 2;

    public NtripFallback Clone() => (NtripFallback)MemberwiseClone();

    /// <summary>Enough to connect: a host and a mount point.</summary>
    public bool IsComplete => !string.IsNullOrWhiteSpace(CasterHost) && !string.IsNullOrWhiteSpace(MountPoint);
}
