// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Text;
using System.Text.RegularExpressions;

namespace AgOpenWeb.Services;

/// <summary>
/// Classifies a caster's reply (the client and the Test button share it).
/// "SOURCETABLE 200 OK" is NOT acceptance: an NTRIP 1 caster sends its source table for an
/// unknown mount point, and a plain "contains 200 OK" check took that as connected.
/// Casters word their errors inconsistently, so free text is matched against a few key words
/// and anything else stays <see cref="NtripCategory.NotUnderstood"/>.
/// </summary>
public static class NtripResponse
{
    private static readonly Regex HttpStatus = new(@"^HTTP/\d\.\d\s+(\d{3})", RegexOptions.CultureInvariant);

    private static readonly string[] BusyWords =
        { "too many", "already", "in use", "banned", "blocked", "busy", "limit", "try again", "start up", "starting up" };
    private static readonly string[] LoginWords = { "password", "unauthor", "login", "credential", "user" };
    private static readonly string[] MountWords = { "mount", "not found", "no such" };

    /// <summary>The reply header announces chunked transfer encoding (an NTRIP 2 caster may).</summary>
    public static bool IsChunked(string header) =>
        HeaderValue(header, "Transfer-Encoding").Contains("chunked", StringComparison.OrdinalIgnoreCase);

    /// <param name="header">The reply up to the blank line.</param>
    /// <param name="firstBody">The first bytes after the header, if any arrived.</param>
    public static NtripOutcome Classify(string header, ReadOnlySpan<byte> firstBody = default)
    {
        header ??= "";
        string first = header.Split('\n')[0].Trim();
        bool chunked = IsChunked(header);

        if (first.Length == 0)
            return new(NtripCategory.NotUnderstood, "Caster sent an empty reply", first, chunked);

        if (first.StartsWith("SOURCETABLE", StringComparison.OrdinalIgnoreCase))
            return MountNotFound("Mount point not found (the caster sent its source table)", first, chunked);

        if (first.StartsWith("ICY 200", StringComparison.Ordinal))
            return LooksLikeSourcetable(firstBody)
                ? MountNotFound("Mount point not found (the caster sent its source table)", first, chunked)
                : new(NtripCategory.Accepted, "", first, chunked);

        if (first.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase))
            return FromText(first, first, chunked, "Caster refused: " + first);   // NTRIP 1, e.g. "ERROR - Bad Password"

        Match m = HttpStatus.Match(first);
        if (!m.Success)
            return FromText(first, first, chunked, "Unexpected caster reply: " + first, ntrip2NotUnderstood: true);

        string body = BodyText(firstBody);
        switch (m.Groups[1].Value)
        {
            case "200":
                if (HeaderValue(header, "Content-Type").Contains("sourcetable", StringComparison.OrdinalIgnoreCase)
                    || LooksLikeSourcetable(firstBody))
                    return MountNotFound("Mount point not found (the caster sent its source table)", first, chunked);
                if (firstBody.Length > 0 && firstBody[0] == (byte)'<')
                    return new(NtripCategory.NotUnderstood, "This address answered with a web page, not an NTRIP caster. Check the address and port.", first, chunked);
                return new(NtripCategory.Accepted, "", first, chunked);
            case "401":
                // Some casters (AgOpenNtripCaster) also answer 401 for a mount point they do not have.
                return new(NtripCategory.LoginRejected, "Authentication failed (check username, password and mount point)", first, chunked);
            case "403":
                return FromText(first + " " + body, first, chunked, "Access denied: " + first, NtripCategory.LoginRejected);
            case "404":
                return MountNotFound("Mount point not found", first, chunked);
            case "429": case "502": case "503": case "504":
                return new(NtripCategory.CasterBusy, "Caster is busy or unavailable: " + first, first, chunked);
            case "400":
                // A caster that does not know NTRIP 2.0 may answer like this; RTK2go also asks for an e-mail address as user name.
                return FromText(first + " " + body, first, chunked, "Caster refused: " + first, ntrip2NotUnderstood: true);
            case "505":
                return new(NtripCategory.NotUnderstood, "Caster refused: " + first, first, chunked, Ntrip2NotUnderstood: true);
            default:
                return FromText(first + " " + body, first, chunked, "Caster refused: " + first);
        }
    }

    private static NtripOutcome MountNotFound(string message, string first, bool chunked) =>
        new(NtripCategory.UnknownMountpoint, message, first, chunked);

    /// <summary>Match free text against the key words; <paramref name="otherwise"/> is the message when none fit.</summary>
    private static NtripOutcome FromText(string text, string first, bool chunked, string otherwise,
        NtripCategory fallback = NtripCategory.NotUnderstood, bool ntrip2NotUnderstood = false)
    {
        string t = text.ToLowerInvariant();
        if (ContainsAny(t, BusyWords))
            return new(NtripCategory.CasterBusy, "The caster cannot take another connection now: it is busy, starting up, or this login is in use.", first, chunked);
        if (t.Contains("email") || t.Contains("e-mail"))
            return new(NtripCategory.LoginRejected, "This caster needs your e-mail address as user name.", first, chunked);
        if (ContainsAny(t, LoginWords))
            return new(NtripCategory.LoginRejected, "Authentication failed (check username and password)", first, chunked);
        if (ContainsAny(t, MountWords))
            return MountNotFound("Mount point not found", first, chunked);
        return new(fallback, otherwise, first, chunked, ntrip2NotUnderstood && fallback == NtripCategory.NotUnderstood);
    }

    private static bool ContainsAny(string text, string[] words)
    {
        foreach (string w in words)
            if (text.Contains(w, StringComparison.Ordinal)) return true;
        return false;
    }

    private static bool LooksLikeSourcetable(ReadOnlySpan<byte> body) =>
        body.StartsWith("STR;"u8) || body.StartsWith("CAS;"u8) || body.StartsWith("NET;"u8) || body.StartsWith("SOURCETABLE"u8);

    private static string BodyText(ReadOnlySpan<byte> body)
    {
        var sb = new StringBuilder();
        foreach (byte b in body[..Math.Min(body.Length, 256)])
            sb.Append(b is >= 32 and < 127 ? (char)b : ' ');
        return sb.ToString();
    }

    private static string HeaderValue(string header, string name)
    {
        foreach (string line in header.Split('\n'))
        {
            int colon = line.IndexOf(':');
            if (colon > 0 && line[..colon].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                return line[(colon + 1)..].Trim();
        }
        return "";
    }
}
