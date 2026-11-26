// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Threading.Tasks;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.Services;

/// <summary>
/// One-shot NTRIP caster probe: opens a connection exactly as the client does (NTRIP 2.0, then
/// 1.0 for a caster that does not understand it) and returns a human-readable result. A passing
/// test therefore means the real connection passes too. Pure network IO with no UI or VM
/// dependency, so the profile editor and the remote (web) Network IO editor share the same logic.
/// </summary>
public static class NtripConnectionTester
{
    public static async Task<string> TestAsync(string host, int port, string mountPoint,
        string username, string password)
    {
        if (string.IsNullOrWhiteSpace(host)) return "Error: Caster host is required";
        if (string.IsNullOrWhiteSpace(mountPoint)) return "Error: Mount point is required";

        try
        {
            var config = new NtripConfiguration
            {
                CasterAddress = host.Trim(), CasterPort = port, MountPoint = mountPoint.Trim(),
                Username = username, Password = password,
            };
            using var connection = await NtripConnection.OpenAsync(config, NtripVersion.V2, () => null, new NtripTimeouts(), default);
            return connection.Outcome.IsAccepted
                ? "Success: Connected to caster and mount point"
                : "Error: " + connection.Outcome.Message;
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }
}
