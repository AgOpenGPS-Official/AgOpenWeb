// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Threading.Tasks;

namespace AgOpenWeb.Services.Interfaces;

/// <summary>
/// Service for connecting to NTRIP caster to receive RTK correction data
/// Forwards RTCM3 corrections to GPS module via UDP port 2233
/// </summary>
public interface INtripClientService
{
    /// <summary>
    /// Event fired when connection status changes
    /// </summary>
    event EventHandler<NtripConnectionEventArgs>? ConnectionStatusChanged;

    /// <summary>
    /// Event fired when RTCM data is received and forwarded
    /// </summary>
    event EventHandler<RtcmDataReceivedEventArgs>? RtcmDataReceived;

    /// <summary>
    /// Whether the caster accepted the request (answered 200) and the session is open
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Whether a connection was requested and not disconnected: connected, connecting,
    /// retrying, or stopped after the caster rejected it. Use this to decide whether to
    /// disconnect or reconnect; <see cref="IsConnected"/> is only the live state.
    /// </summary>
    bool IsActive { get; }

    /// <summary>
    /// What the session is doing: its state, the last failure and when it tries again.
    /// </summary>
    NtripSessionStatus Status { get; }

    /// <summary>
    /// Total bytes received from caster
    /// </summary>
    ulong TotalBytesReceived { get; }

    /// <summary>
    /// Where RTCM is being sent ("192.168.5.126" or "192.168.5.255"), empty with no session,
    /// and whether that is the GPS module's own address (unicast) or the subnet broadcast.
    /// </summary>
    (string Address, bool Unicast) RtcmDestination { get; }

    /// <summary>
    /// The RTCM messages the caster has sent this session, by type, with checksum failures
    /// and skipped bytes. Diagnostics only: counted beside the forwarder.
    /// </summary>
    RtcmStreamSnapshot GetRtcmStreamSnapshot();

    /// <summary>
    /// Connect to NTRIP caster with specified configuration. Returns once the session is
    /// started; it retries by itself, and its progress comes through <see cref="ConnectionStatusChanged"/>.
    /// </summary>
    Task ConnectAsync(NtripConfiguration config);

    /// <summary>
    /// Disconnect from NTRIP caster
    /// </summary>
    Task DisconnectAsync();

    /// <summary>
    /// Send GGA sentence to caster (for VRS/network RTK)
    /// </summary>
    Task SendGgaSentenceAsync(string ggaSentence);
}

/// <summary>
/// NTRIP connection configuration
/// </summary>
public class NtripConfiguration
{
    /// <summary>
    /// Caster IP address or hostname
    /// </summary>
    public string CasterAddress { get; set; } = "";

    /// <summary>
    /// Caster port (typically 2101 or 80)
    /// </summary>
    public int CasterPort { get; set; } = 2101;

    /// <summary>
    /// Mount point name
    /// </summary>
    public string MountPoint { get; set; } = "";

    /// <summary>
    /// Username for authentication
    /// </summary>
    public string Username { get; set; } = "";

    /// <summary>
    /// Password for authentication
    /// </summary>
    public string Password { get; set; } = "";

    /// <summary>
    /// HTTP version (1.0 or 1.1)
    /// </summary>
    public string HttpVersion { get; set; } = "1.0";

    /// <summary>
    /// UDP port to forward RTCM data to (default 2233)
    /// </summary>
    public int UdpForwardPort { get; set; } = 2233;

    /// <summary>
    /// Subnet for UDP broadcast (e.g., "192.168.5")
    /// </summary>
    public string SubnetAddress { get; set; } = "192.168.5";

    /// <summary>
    /// The modules' live /24 (e.g. from auto-discovery), read for every RTCM send so a
    /// subnet change is followed without reconnecting. Null, or a null/empty result,
    /// falls back to <see cref="SubnetAddress"/>.
    /// </summary>
    public Func<string?>? SubnetProvider { get; set; }

    /// <summary>
    /// The GPS module's address, when known (where its position sentences come from).
    /// RTCM is sent there; with none it is broadcast to the subnet.
    /// </summary>
    public Func<System.Net.IPAddress?>? GpsModuleAddressProvider { get; set; }

    /// <summary>
    /// True to broadcast RTCM to the subnet even when the GPS module's address is known.
    /// Read for every datagram, so the setting takes effect without reconnecting.
    /// </summary>
    public Func<bool>? BroadcastOnly { get; set; }

    /// <summary>
    /// GGA send interval in seconds (0 = disabled)
    /// </summary>
    public int GgaIntervalSeconds { get; set; } = 10;

    /// <summary>
    /// Where to tell the caster the receiver is before it has had a fix (the open field's
    /// origin); null for nowhere. With neither, no GGA is sent until the first fix (#443).
    /// </summary>
    public Func<(double Latitude, double Longitude)?>? FallbackPositionProvider { get; set; }

    /// <summary>
    /// Use manual position for GGA (vs GPS position)
    /// </summary>
    public bool UseManualPosition { get; set; } = false;

    /// <summary>
    /// Manual latitude for GGA (if UseManualPosition is true)
    /// </summary>
    public double ManualLatitude { get; set; } = 0;

    /// <summary>
    /// Manual longitude for GGA (if UseManualPosition is true)
    /// </summary>
    public double ManualLongitude { get; set; } = 0;
}

/// <summary>
/// Event args for NTRIP connection status change
/// </summary>
public class NtripConnectionEventArgs : EventArgs
{
    public bool IsConnected { get; set; }
    public string? Message { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.Now;
}

/// <summary>
/// Event args for RTCM data received
/// </summary>
public class RtcmDataReceivedEventArgs : EventArgs
{
    public int BytesReceived { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.Now;
}