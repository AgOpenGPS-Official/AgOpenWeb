// AgValoniaGPS
// Copyright (C) 2024-2025 AgValoniaGPS Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AgValoniaGPS.VehicleSimulator.Modules;

/// <summary>
/// GPS message flavour. PANDA = single antenna + IMU (heading/roll as int×10,
/// 65535 = no-IMU sentinel). PAOGI = dual antenna (heading/roll as floats).
/// </summary>
public enum GpsMessageType
{
    Panda,
    Paogi
}

/// <summary>
/// Virtual GPS receiver that sends $PANDA (single) or $PAOGI (dual) NMEA
/// sentences via UDP, matching the AiO firmware wire format.
/// </summary>
public class VirtualGpsReceiver : IDisposable
{
    private readonly UdpClient _udp;
    private readonly IPEndPoint _target;
    private CancellationTokenSource? _cts;
    private Task? _sendTask;

    // Position state
    public double Latitude { get; set; } = 43.712800;
    public double Longitude { get; set; } = -74.006000;
    public double Altitude { get; set; } = 100.0;
    public double HeadingDegrees { get; set; }
    public double SpeedKnots { get; set; }
    public int FixQuality { get; set; } = 4; // RTK Fixed
    public int Satellites { get; set; } = 12;
    public double Hdop { get; set; } = 0.7;
    public double DifferentialAge { get; set; } = 1.0;

    // IMU data (embedded in $PANDA / $PAOGI)
    public double RollDegrees { get; set; }
    public double PitchDegrees { get; set; }
    public double YawRateDegPerSec { get; set; }

    /// <summary>PANDA only: when false, the heading field is sent as the 65535
    /// "no-IMU" sentinel so the host's ImuValid=false branch can be exercised.</summary>
    public bool ImuValid { get; set; } = true;

    /// <summary>Wire format: PANDA (single+IMU) or PAOGI (dual antenna).</summary>
    public GpsMessageType MessageType { get; set; } = GpsMessageType.Panda;

    // Dual antenna
    public bool IsDualAntenna { get; set; }
    public double DualHeadingDegrees { get; set; }

    // Timing
    public int UpdateRateHz { get; set; } = 10;

    // Counters for test verification
    public long SentCount { get; private set; }

    /// <summary>Fired with each outgoing NMEA sentence (for the sim's Sent pane).</summary>
    public Action<string>? OnSent;

    public VirtualGpsReceiver(int targetPort = 9999, string targetIp = "127.0.0.1")
    {
        _udp = new UdpClient();
        _target = new IPEndPoint(IPAddress.Parse(targetIp), targetPort);
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _sendTask = Task.Run(() => SendLoop(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _sendTask?.Wait(TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// Send a single GPS sentence immediately (for test control). PANDA or
    /// PAOGI depending on <see cref="MessageType"/>.
    /// </summary>
    public void SendOnce()
    {
        var sentence = BuildSentence();
        var bytes = Encoding.ASCII.GetBytes(sentence);
        _udp.Send(bytes, bytes.Length, _target);
        SentCount++;
        OnSent?.Invoke(sentence.TrimEnd('\r', '\n'));
    }

    /// <summary>
    /// Move the virtual GPS by applying speed and heading for one time step.
    /// </summary>
    public void Step(double deltaSeconds)
    {
        double speedMs = SpeedKnots * 0.514444; // knots to m/s
        double headingRad = HeadingDegrees * Math.PI / 180.0;

        // Approximate: 1 degree latitude ~ 111,320m, 1 degree longitude ~ 111,320 * cos(lat)
        double dNorth = speedMs * Math.Cos(headingRad) * deltaSeconds;
        double dEast = speedMs * Math.Sin(headingRad) * deltaSeconds;

        Latitude += dNorth / 111320.0;
        Longitude += dEast / (111320.0 * Math.Cos(Latitude * Math.PI / 180.0));
    }

    private async Task SendLoop(CancellationToken ct)
    {
        int intervalMs = 1000 / UpdateRateHz;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                SendOnce();
                await Task.Delay(intervalMs, ct);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    private string BuildSentence()
    {
        // Shared layout (both flavours):
        //   $HDR,time,lat,N/S,lon,E/W,fix,sats,hdop,alt,age,speed,heading,roll,pitch,yaw*cs
        // Fields 12-14 (heading, roll, pitch) differ by flavour:
        //   PANDA: heading = int(deg×10) or 65535 sentinel, roll = int(deg×10), pitch = float
        //   PAOGI: heading = float (dual), roll = float (dual), pitch = int
        var ci = CultureInfo.InvariantCulture;
        bool paogi = MessageType == GpsMessageType.Paogi;

        string time = DateTime.UtcNow.ToString("HHmmss.ff", ci);
        string lat = FormatLatitude(Latitude);
        string ns = Latitude >= 0 ? "N" : "S";
        string lon = FormatLongitude(Longitude);
        string ew = Longitude >= 0 ? "E" : "W";

        var sb = new StringBuilder();
        sb.Append(paogi ? "$PAOGI," : "$PANDA,");
        sb.Append(time); sb.Append(',');
        sb.Append(lat); sb.Append(',');
        sb.Append(ns); sb.Append(',');
        sb.Append(lon); sb.Append(',');
        sb.Append(ew); sb.Append(',');
        sb.Append(FixQuality.ToString(ci)); sb.Append(',');
        sb.Append(Satellites.ToString(ci)); sb.Append(',');
        sb.Append(Hdop.ToString("F1", ci)); sb.Append(',');
        sb.Append(Altitude.ToString("F1", ci)); sb.Append(',');
        sb.Append(DifferentialAge.ToString("F1", ci)); sb.Append(',');
        sb.Append(SpeedKnots.ToString("F2", ci)); sb.Append(',');

        // Field 12: heading
        if (paogi)
            sb.Append(HeadingDegrees.ToString("F1", ci));
        else
            sb.Append(ImuValid ? ((int)Math.Round(HeadingDegrees * 10.0)).ToString(ci) : "65535");
        sb.Append(',');

        // Field 13: roll
        if (paogi)
            sb.Append(RollDegrees.ToString("F2", ci));
        else
            sb.Append(((int)Math.Round(RollDegrees * 10.0)).ToString(ci));
        sb.Append(',');

        // Field 14: pitch
        sb.Append(paogi
            ? ((int)Math.Round(PitchDegrees)).ToString(ci)
            : PitchDegrees.ToString("F2", ci));
        sb.Append(',');

        // Field 15: yaw rate
        sb.Append(YawRateDegPerSec.ToString("F2", ci));

        // NMEA checksum (XOR of all chars between $ and *)
        string body = sb.ToString().Substring(1); // Skip the $
        byte checksum = 0;
        foreach (char c in body)
            checksum ^= (byte)c;

        sb.Append('*');
        sb.Append(checksum.ToString("X2", ci));
        sb.Append("\r\n");

        return sb.ToString();
    }

    /// <summary>Format latitude as DDMM.MMMMM (5 decimal places = 0.019m resolution)</summary>
    private static string FormatLatitude(double lat)
    {
        lat = Math.Abs(lat);
        int degrees = (int)lat;
        double minutes = (lat - degrees) * 60.0;
        return string.Format(CultureInfo.InvariantCulture, "{0:D2}{1:00.00000}", degrees, minutes);
    }

    /// <summary>Format longitude as DDDMM.MMMMM (5 decimal places = 0.019m resolution)</summary>
    private static string FormatLongitude(double lon)
    {
        lon = Math.Abs(lon);
        int degrees = (int)lon;
        double minutes = (lon - degrees) * 60.0;
        return string.Format(CultureInfo.InvariantCulture, "{0:D3}{1:00.00000}", degrees, minutes);
    }

    public void Dispose()
    {
        Stop();
        _udp.Dispose();
        _cts?.Dispose();
    }
}
