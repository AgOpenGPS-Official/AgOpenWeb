// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Threading.Tasks;

namespace AgOpenWeb.Services;

/// <summary>
/// One-shot NTRIP caster reachability probe: opens a TCP connection, sends an
/// NTRIP/2.0 GET for the mount point, and classifies the response. Returns a
/// human-readable status string (success / auth-failed / not-found / timeout /
/// error). Pure network IO with no UI or VM dependency, so the native profile
/// editor and the remote (web) Network IO editor share the exact same logic.
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
            using var tcpClient = new System.Net.Sockets.TcpClient();
            var connectTask = tcpClient.ConnectAsync(host, port);

            if (await Task.WhenAny(connectTask, Task.Delay(5000)) != connectTask)
                return "Error: Connection timed out";
            if (!tcpClient.Connected)
                return "Error: Could not connect to caster";

            using var stream = tcpClient.GetStream();
            var request = $"GET /{mountPoint} HTTP/1.1\r\n" +
                          $"Host: {host}\r\n" +
                          $"Ntrip-Version: Ntrip/2.0\r\n" +
                          $"User-Agent: NTRIP AgOpenWeb/Test\r\n";

            if (!string.IsNullOrEmpty(username))
            {
                var credentials = Convert.ToBase64String(
                    System.Text.Encoding.ASCII.GetBytes($"{username}:{password}"));
                request += $"Authorization: Basic {credentials}\r\n";
            }
            request += "\r\n";

            var requestBytes = System.Text.Encoding.ASCII.GetBytes(request);
            await stream.WriteAsync(requestBytes, 0, requestBytes.Length);

            var buffer = new byte[1024];
            stream.ReadTimeout = 3000;
            var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
            var response = System.Text.Encoding.ASCII.GetString(buffer, 0, bytesRead);

            if (response.Contains("200 OK") || response.Contains("ICY 200"))
                return "Success: Connected to caster and mount point";
            if (response.Contains("401"))
                return "Error: Authentication failed (check username/password)";
            if (response.Contains("404"))
                return "Error: Mount point not found";
            return "Connected to caster (mount point status unknown)";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }
}
