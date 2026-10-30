// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.Services;

/// <summary>The NTRIP version a request speaks.</summary>
public enum NtripVersion
{
    V1,
    V2,
}

/// <summary>Builds the mount point request. Pure, so it is tested without a socket.</summary>
public static class NtripRequest
{
    public const string UserAgent = "NTRIP AgOpenWeb";

    /// <param name="gga">A complete GGA sentence for the Ntrip-GGA header (NTRIP 2.0 only), or null.</param>
    public static string Build(NtripConfiguration config, NtripVersion version, string? gga)
    {
        bool v2 = version == NtripVersion.V2;
        var sb = new StringBuilder();
        sb.Append("GET /").Append(Uri.EscapeDataString(config.MountPoint)).Append(v2 ? " HTTP/1.1\r\n" : " HTTP/1.0\r\n");
        if (v2)
        {
            sb.Append("Host: ").Append(config.CasterAddress);
            if (config.CasterPort != 80) sb.Append(':').Append(config.CasterPort.ToString(CultureInfo.InvariantCulture));
            sb.Append("\r\nNtrip-Version: Ntrip/2.0\r\n");
        }
        sb.Append("User-Agent: ").Append(UserAgent).Append("\r\n");
        if (NtripCredentials.BasicAuthorization(config.Username, config.Password) is { } authorization)
            sb.Append("Authorization: ").Append(authorization).Append("\r\n");
        if (v2 && gga != null) sb.Append("Ntrip-GGA: ").Append(gga.TrimEnd('\r', '\n')).Append("\r\n");
        if (v2) sb.Append("Connection: close\r\n");
        sb.Append("\r\n");
        return sb.ToString();
    }
}
