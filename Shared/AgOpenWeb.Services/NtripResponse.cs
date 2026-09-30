// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.Text.RegularExpressions;

namespace AgOpenWeb.Services;

/// <summary>How a caster answered the mount-point request.</summary>
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
/// Classifies a caster's reply header (the client and the connection tester share it).
/// "SOURCETABLE 200 OK" is NOT acceptance: an NTRIP 1 caster sends its source table for an
/// unknown mount point, and a plain "contains 200 OK" check took that as connected.
/// </summary>
public static class NtripResponse
{
    private static readonly Regex HttpStatus = new(@"^HTTP/\d\.\d\s+(\d{3})", RegexOptions.CultureInvariant);

    public static (NtripReply Reply, string Reason) Classify(string header)
    {
        string first = (header ?? "").Split('\n')[0].Trim();
        if (first.Length == 0)
            return (NtripReply.RejectedRetry, "Caster sent an empty reply");

        if (first.StartsWith("SOURCETABLE", StringComparison.OrdinalIgnoreCase))
            return (NtripReply.Rejected, "Mount point not found (the caster sent its source table)");
        if (first.StartsWith("ICY 200", StringComparison.Ordinal))
            return (NtripReply.Accepted, "");
        if (first.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase))
            return (NtripReply.Rejected, first); // NTRIP 1, e.g. "ERROR - Bad Password"

        var m = HttpStatus.Match(first);
        if (!m.Success)
            return (NtripReply.RejectedRetry, "Unexpected caster reply: " + first);

        return m.Groups[1].Value switch
        {
            "200" => (NtripReply.Accepted, ""),
            "401" => (NtripReply.Rejected, "Authentication failed (check username and password)"),
            "403" => (NtripReply.Rejected, "Access denied: " + first),
            "404" => (NtripReply.Rejected, "Mount point not found"),
            _ => (NtripReply.RejectedRetry, "Caster refused: " + first),
        };
    }
}
