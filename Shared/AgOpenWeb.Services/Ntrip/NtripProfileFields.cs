// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using AgOpenWeb.Models.Ntrip;

namespace AgOpenWeb.Services;

/// <summary>Reads the fallback block of the profile editor's <c>ntrip.save</c> command. Pure, so it is tested without a server.</summary>
public static class NtripProfileFields
{
    /// <summary>
    /// The fallback from the command's fields, which start at <paramref name="first"/>:
    /// present ("1" or "0"), host, port, mount point, user, password, auto-switch ("1" or "0"), failed attempts.
    /// With no such block the profile keeps <paramref name="existing"/>; "0" removes the fallback. The editor
    /// never has a stored password, only <paramref name="mask"/>, which stands for the existing one.
    /// </summary>
    public static NtripFallback? FallbackFrom(string[] fields, int first, NtripFallback? existing, string mask)
    {
        if (fields.Length <= first) return existing;
        if (fields[first] != "1") return null;

        string Get(int offset) => first + offset < fields.Length ? fields[first + offset] : "";
        int.TryParse(Get(2), NumberStyles.Integer, CultureInfo.InvariantCulture, out int port);
        int.TryParse(Get(7), NumberStyles.Integer, CultureInfo.InvariantCulture, out int after);
        return new NtripFallback
        {
            CasterHost = Get(1).Trim(),
            CasterPort = port == 0 ? 2101 : port,
            MountPoint = Get(3).Trim(),
            Username = Get(4),
            Password = Get(5) == mask ? existing?.Password ?? "" : Get(5),
            AutoSwitch = Get(6) == "1",
            SwitchAfterFailures = after < 1 ? 2 : after,
        };
    }
}
