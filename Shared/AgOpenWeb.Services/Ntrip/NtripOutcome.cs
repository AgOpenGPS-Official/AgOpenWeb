// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

namespace AgOpenWeb.Services;

/// <summary>What an NTRIP attempt or session came to.</summary>
public enum NtripCategory
{
    /// <summary>The caster accepted the request and will stream corrections.</summary>
    Accepted,
    /// <summary>The device has no network.</summary>
    NoNetwork,
    /// <summary>No answer, refused, or closed without a reply.</summary>
    CasterUnreachable,
    LoginRejected,
    UnknownMountpoint,
    /// <summary>Too many connections, this login in use, or the caster is starting up.</summary>
    CasterBusy,
    /// <summary>Accepted, but no corrections arrived.</summary>
    NoCorrections,
    /// <summary>Corrections were arriving and stopped.</summary>
    StreamLost,
    /// <summary>The reply is not one this client knows.</summary>
    NotUnderstood,
}

/// <summary>How a caster answered, in the three groups the first version of the client used.</summary>
public enum NtripReply
{
    /// <summary>The caster will stream corrections.</summary>
    Accepted,
    /// <summary>Refused for a reason retrying won't fix (mount point, login).</summary>
    Rejected,
    /// <summary>Refused or unclear, possibly temporary (server error, busy).</summary>
    RejectedRetry,
}

/// <summary>
/// The result of one attempt: a category and a plain message. Produced by
/// <see cref="NtripResponse.Classify"/> from a caster's reply and by <see cref="ConnectionErrors"/>
/// from a socket error, so there is one classification for the client and the Test button.
/// </summary>
/// <param name="Message">One sentence for the user; empty when accepted.</param>
/// <param name="FirstLine">The caster's first reply line, for the log.</param>
/// <param name="Chunked">The body is HTTP chunked and goes through <see cref="ChunkedDecoder"/>.</param>
/// <param name="Ntrip2NotUnderstood">The caster did not understand an NTRIP 2.0 request, so NTRIP 1.0 is worth one try.</param>
public sealed record NtripOutcome(NtripCategory Category, string Message, string FirstLine = "",
    bool Chunked = false, bool Ntrip2NotUnderstood = false)
{
    public bool IsAccepted => Category == NtripCategory.Accepted;

    /// <summary>Retrying will not help until the user changes the login or the mount point.</summary>
    public bool IsFinal => Category is NtripCategory.LoginRejected or NtripCategory.UnknownMountpoint;

    public NtripReply Reply => IsAccepted ? NtripReply.Accepted : IsFinal ? NtripReply.Rejected : NtripReply.RejectedRetry;
}
