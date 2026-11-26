// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

namespace AgOpenWeb.Services;

public enum NtripState
{
    /// <summary>Not started, or stopped by the user.</summary>
    Stopped,
    /// <summary>Connecting and waiting for the caster's reply.</summary>
    Connecting,
    /// <summary>Accepted by the caster, no RTCM message yet.</summary>
    Connected,
    /// <summary>Corrections are arriving.</summary>
    Streaming,
    /// <summary>The last attempt failed; waiting to try again.</summary>
    Waiting,
    /// <summary>Retrying will not help (wrong login or mount point); waits for the user.</summary>
    GaveUp,
}

/// <summary>A snapshot of one <see cref="NtripSession"/>: the state, the last failure and what the client does next.</summary>
/// <param name="Message">One plain sentence; for a failure it says what was wrong and what happens next.</param>
/// <param name="LastError">The category of the last failure; null once corrections flow again.</param>
/// <param name="FirstLine">The caster's first reply line, for the log.</param>
/// <param name="Attempts">Failed attempts since corrections last arrived.</param>
/// <param name="RetryInSeconds">Seconds until the next attempt; 0 when none is waiting.</param>
/// <param name="Version">The NTRIP version of the latest attempt.</param>
public sealed record NtripSessionStatus(
    NtripState State,
    string Message,
    NtripCategory? LastError,
    string FirstLine,
    int Attempts,
    double RetryInSeconds,
    NtripVersion Version)
{
    public static readonly NtripSessionStatus Idle = new(NtripState.Stopped, "Not Connected", null, "", 0, 0, NtripVersion.V2);

    /// <summary>The caster accepted the request and the connection is open.</summary>
    public bool IsOpen => State is NtripState.Connected or NtripState.Streaming;
}
