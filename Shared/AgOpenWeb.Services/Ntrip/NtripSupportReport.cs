// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.Services;

/// <summary>
/// Builds the bug report dump's <c>ntrip_session.txt</c> and the NTRIP line of <c>system_info.txt</c>:
/// what the connection did and why it failed, so a report about "no corrections" can be read
/// without asking. It says whether a login is set and masks it where a caster repeated it.
/// </summary>
public static class NtripSupportReport
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    /// <summary>The session in full; null when NTRIP has not been used this run.</summary>
    public static string? Build(INtripClientService? ntrip, IGpsService? gps)
    {
        try
        {
            if (ntrip == null) return null;
            IReadOnlyList<NtripEvent> events = ntrip.Events;
            if (events.Count == 0 && !ntrip.IsActive) return null;

            NtripSessionStatus status = ntrip.Status;
            NtripServiceInfo? service = ntrip.Service;
            NtripEvent? lastFailure = events.LastOrDefault(e => e.Category != null);
            bool idle = status == NtripSessionStatus.Idle;   // never started, or stopped by the user
            var gpsData = gps?.CurrentData;
            var (address, unicast) = ntrip.RtcmDestination;
            var sb = new StringBuilder();
            sb.AppendLine("NTRIP session (what the connection did; a login is masked where a caster repeated it)");
            sb.AppendLine(Ci, $"Service:           {service?.ToString() ?? "-"}");
            sb.AppendLine(Ci, $"Login set:         user name {YesNo(service?.HasUserName)}, password {YesNo(service?.HasPassword)}");
            sb.AppendLine(Ci, $"Requested:         {YesNo(ntrip.IsActive)}");
            sb.AppendLine(Ci, $"Using:             {(ntrip.Using == NtripService.Fallback ? "the fallback service" + (ntrip.MainIsBack ? " (the main service answers again)" : "") : "the main service")}");
            sb.AppendLine(Ci, $"State:             {(idle ? "Stopped" : status.State.ToString())}");
            sb.AppendLine(Ci, $"Message:           {(idle ? "-" : status.Message)}");
            sb.AppendLine(Ci, $"Last failure:      {Describe(lastFailure)}");
            sb.AppendLine(Ci, $"Failed attempts:   {status.Attempts} since corrections last arrived");
            if (status.State == NtripState.Waiting) sb.AppendLine(Ci, $"Next try in:       {status.RetryInSeconds:0} s");
            sb.AppendLine(Ci, $"NTRIP version:     {(idle ? "-" : Version(status.Version))}");
            sb.AppendLine(Ci, $"Caster said:       {(status.FirstLine.Length > 0 ? status.FirstLine : "-")}");
            sb.AppendLine(Ci, $"Bytes this stream: {ntrip.TotalBytesReceived}");
            sb.AppendLine(Ci, $"Corrections go to: {(address.Length > 0 ? address + (unicast ? " (the GPS module)" : " (broadcast)") : "-")}");
            sb.AppendLine(Ci, $"Receiver:          fix quality {gpsData?.FixQuality ?? 0}, differential age {gpsData?.DifferentialAge ?? 0:F1} s");
            sb.AppendLine();
            sb.AppendLine("Steps, oldest first (local time):");
            string service0 = "";
            foreach (NtripEvent e in events)
            {
                if (e.Service != service0)
                {
                    sb.AppendLine(Ci, $"-- {(e.Service.Length > 0 ? e.Service : "-")}");
                    service0 = e.Service;
                }
                string error = e.Category is { } c ? $" [{c}]" : "";
                string attempt = e.Attempts > 0 ? $" (failed attempts: {e.Attempts})" : "";
                string said = e.FirstLine.Length > 0 ? $" (caster said: {e.FirstLine})" : "";
                sb.AppendLine(Ci, $"{e.Time:yyyy-MM-dd HH:mm:ss zzz}  {e.State,-10} NTRIP {Version(e.Version)}{error}  {e.Message}{attempt}{said}");
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return "ntrip session report failed: " + ex.Message;
        }
    }

    /// <summary>One line for <c>system_info.txt</c>, so the issue says where to look; null when NTRIP has not been used.</summary>
    public static string? Summary(INtripClientService? ntrip)
    {
        try
        {
            if (ntrip == null) return null;
            IReadOnlyList<NtripEvent> events = ntrip.Events;
            if (events.Count == 0 && !ntrip.IsActive) return null;

            string service = ntrip.Service?.ToString() ?? "-";
            NtripSessionStatus status = ntrip.Status;
            if (status != NtripSessionStatus.Idle)
                return $"{status.State}{(status.LastError is { } c ? $", {c}" : "")}, {service}{(ntrip.Using == NtripService.Fallback ? " (fallback)" : "")}: {status.Message}";

            // Stopped by the user: say what the session was doing before.
            NtripEvent? before = events.LastOrDefault(e => e.State != NtripState.Stopped);
            return before == null
                ? $"Stopped, {service}"
                : $"Stopped by the user, {service}; before that {before.State}{(before.Category is { } b ? $", {b}" : "")}: {before.Message}";
        }
        catch (Exception ex)
        {
            return "failed: " + ex.Message;
        }
    }

    private static string Describe(NtripEvent? e) => e == null ? "none" : $"{e.Category}: {e.Message}";

    private static string Version(NtripVersion version) => version == NtripVersion.V1 ? "1.0" : "2.0";

    private static string YesNo(bool? value) => value == true ? "yes" : "no";
}
