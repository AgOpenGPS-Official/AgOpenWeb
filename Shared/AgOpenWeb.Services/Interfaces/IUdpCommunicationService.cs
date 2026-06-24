// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
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
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;

namespace AgOpenWeb.Services.Interfaces;

/// <summary>
/// Service for UDP communication with Teensy modules (Steer, Machine, IMU, GPS)
/// Based on AgIO UDP networking pattern - eliminates USB serial connections
/// </summary>
public interface IUdpCommunicationService
{
    /// <summary>
    /// Event fired when data is received from any module
    /// </summary>
    event EventHandler<UdpDataReceivedEventArgs>? DataReceived;

    /// <summary>
    /// Event fired when module connection status changes
    /// </summary>
    event EventHandler<ModuleConnectionEventArgs>? ModuleConnectionChanged;

    /// <summary>
    /// Whether UDP network is connected
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Local IP address being used
    /// </summary>
    string? LocalIPAddress { get; }

    /// <summary>
    /// All non-loopback IPv4 addresses of the host's up network interfaces.
    /// </summary>
    IReadOnlyList<string> GetLocalIpAddresses();

    /// <summary>
    /// Start UDP communication service
    /// Port 9999 for receiving from modules
    /// </summary>
    Task StartAsync();

    /// <summary>
    /// Stop UDP communication service
    /// </summary>
    Task StopAsync();

    /// <summary>
    /// Send data to modules via UDP broadcast
    /// Default endpoint: [subnet].255:8888
    /// </summary>
    void SendToModules(byte[] data);

    /// <summary>
    /// Send hello/ping packet to check module connections
    /// PGN 200 (0xC8) - communication check
    /// </summary>
    void SendHelloPacket();

    /// <summary>
    /// Check if module hello is OK (2 second timeout)
    /// </summary>
    bool IsModuleHelloOk(ModuleType moduleType);

    /// <summary>
    /// Check if module data is flowing (50Hz for Steer/Machine, 10Hz for IMU)
    /// </summary>
    bool IsModuleDataOk(ModuleType moduleType);

    /// <summary>
    /// Most-recently-observed remote IP for the given module, or null if no packet
    /// has been received from it yet. Dotted-quad string (no port).
    /// </summary>
    string? GetModuleIpAddress(ModuleType moduleType);

    /// <summary>
    /// The /24 subnet (first three octets, e.g. "192.168.5") most recently
    /// reported by a module in a PGN 203 scan reply, or null if none seen.
    /// </summary>
    string? GetModuleSubnet();

    /// <summary>
    /// Broadcast a scan request (PGN 202) asking every module to reply with its
    /// IP + subnet (PGN 203). Matches AgIO's FormUDP "Scan" button.
    /// </summary>
    void ScanModules();

    /// <summary>
    /// Broadcast a set-subnet command (PGN 201) changing the first three IP octets
    /// (/24) on ALL modules at once, then re-arm discovery so the app follows the
    /// modules onto their new subnet. Matches AgIO's "Send Subnet" button.
    /// </summary>
    void SetModuleSubnet(byte octet1, byte octet2, byte octet3);
}

/// <summary>
/// Types of modules that can connect via UDP
/// </summary>
public enum ModuleType
{
    AutoSteer,
    Machine,
    IMU,
    GPS
}

/// <summary>
/// Event args for UDP data received
/// </summary>
public class UdpDataReceivedEventArgs : EventArgs
{
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public IPEndPoint RemoteEndPoint { get; set; } = new IPEndPoint(IPAddress.Any, 0);
    public byte PGN { get; set; } // Parameter Group Number (data[3])
    public DateTime Timestamp { get; set; } = DateTime.Now;
}

/// <summary>
/// Event args for module connection status change
/// </summary>
public class ModuleConnectionEventArgs : EventArgs
{
    public ModuleType ModuleType { get; set; }
    public bool IsConnected { get; set; }
    public string? IPAddress { get; set; }
}