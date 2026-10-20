// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

// Send Report in the Bug Report panel: queues the dump in the BugReportOutbox and keeps the
// outbox moving, at start (reports left from last time) and every minute after, on a
// host-loop timer (the sends themselves run off the loop). The panel
// shows the latest report's state from ApplicationState.BugReportSendStatus, a code the
// client words: "sending", "queued", "sent|<issue>|<url>", "accepted", "rejected|<message>".

using System;
using System.IO;
using System.Threading.Tasks;
using AgOpenWeb.Models.State;
using AgOpenWeb.Services;
using AgOpenWeb.Services.BugReport;
using AgOpenWeb.Services.Interfaces;

namespace AgOpenWeb.RemoteWiring;

internal static class BugReportRemote
{
    private static BugReportOutbox? _outbox;
    private static IUiTimer? _timer;
    private static ApplicationState? _state;
    private static IUiDispatcher? _ui;
    private static string? _current; // the zip the panel is showing

    internal static string ReportsDirectory => Path.Combine(AppDataRoot.Documents, "BugReports");

    /// <summary>Once, from RemoteServerWiring.Wire.</summary>
    public static void Start(ApplicationState state, IUiDispatcher ui, IUiTimerFactory timers)
    {
        if (_outbox != null) return;
        _state = state;
        _ui = ui;
        // AGOPENWEB_BUGREPORT_URL points a test run at a local `wrangler dev`.
        var url = Environment.GetEnvironmentVariable("AGOPENWEB_BUGREPORT_URL");
        _outbox = new BugReportOutbox(ReportsDirectory,
            endpoint: Uri.TryCreate(url, UriKind.Absolute, out var u) ? u : null);
        _timer = timers.Create();
        _timer.Interval = TimeSpan.FromMinutes(1);
        _timer.Tick += (_, _) => Kick();
        _timer.Start();
        Kick();
    }

    /// <summary>arg = zip name \t GitHub user \t title \t description.</summary>
    public static void Send(string arg, string language)
    {
        if (_outbox == null || _state == null) return;
        var a = arg.Split('\t');
        var zip = a.Length > 0 ? a[0] : "";
        try
        {
            _outbox.Enqueue(zip, new BugReportMeta(
                Title: a.Length > 2 ? a[2] : "",
                Description: a.Length > 3 ? string.Join("\t", a, 3, a.Length - 3) : "",
                GitHub: a.Length > 1 ? a[1].Trim() : "",
                Language: language));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _state.BugReportSendStatus = "rejected|" + ex.Message;
            return;
        }
        _current = zip;
        _state.BugReportSendStatus = "sending";
        Kick();
    }

    /// <summary>A new dump replaces the one the panel shows; its send state starts empty.</summary>
    public static void NewReport(string? zipPath)
    {
        _current = zipPath == null ? null : Path.GetFileName(zipPath);
        if (_state != null) _state.BugReportSendStatus = "";
    }

    private static void Kick()
    {
        var outbox = _outbox;
        if (outbox == null) return;
        _ = Task.Run(async () =>
        {
            try
            {
                foreach (var (zip, r) in await outbox.SendDueAsync().ConfigureAwait(false))
                {
                    if (zip != _current) continue;
                    var status = r.Outcome switch
                    {
                        BugReportOutcome.Sent when r.Issue is int n => $"sent|{n}|{r.Url}",
                        BugReportOutcome.Sent => "accepted",
                        BugReportOutcome.Retry => "queued",
                        _ => "rejected|" + r.Message,
                    };
                    _ui?.Post(() => { if (_state != null && zip == _current) _state.BugReportSendStatus = status; });
                }
            }
            catch (Exception) { /* next tick tries again */ }
        });
    }
}
