// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Text;

namespace AgOpenWeb.Services;

/// <summary>
/// The Basic Authorization value for an NTRIP request, shared by the client and the Test
/// button so both log in the same way (#443).
/// </summary>
public static class NtripCredentials
{
    /// <summary>
    /// "Basic base64(user:password)", or null when both are empty (an open caster). UTF-8, as
    /// RFC 7617 recommends: ASCII turned every letter outside it (č, ľ, ž, ä…) into '?', so a
    /// password with one was refused.
    /// </summary>
    public static string? BasicAuthorization(string? username, string? password)
    {
        if (string.IsNullOrEmpty(username) && string.IsNullOrEmpty(password)) return null;
        return "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
    }
}
