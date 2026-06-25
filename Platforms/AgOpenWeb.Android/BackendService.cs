// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.OS;

namespace AgOpenWeb.Android;

/// <summary>
/// Foreground service that owns the in-process guidance backend (<see cref="AndroidBackendHost"/>)
/// for the all-in-one launcher. A foreground service with a persistent notification is what keeps
/// Android from killing the process — so the 100 Hz control loop, UDP to the AiO hardware, and the
/// LAN web feed keep running while the WebView Activity is backgrounded (or the screen is off).
///
/// Type = specialUse: this is a long-running guidance host, not a fit for dataSync (Android 15 caps
/// it at a few hours/day) or a companion-device association. specialUse has no daily limit and needs
/// no prerequisite, which suits a sideloaded in-cab app.
/// </summary>
[Service(
    Name = "com.agopenweb.android.BackendService",
    Exported = false,
    ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeSpecialUse)]
internal sealed class BackendService : Service
{
    private const string ChannelId = "agopenweb_host";
    private const int NotificationId = 1;

    private static AndroidBackendHost? _host;
    private static readonly object Gate = new();

    /// <summary>Completes with the bound server port once the backend is serving, so the WebView
    /// Activity can wait for the host before it navigates to localhost (avoids a blank "connection
    /// refused" first paint). Recreated per service start.</summary>
    public static TaskCompletionSource<int> HostReady { get; private set; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static void Start(Context context)
    {
        var intent = new Intent(context, typeof(BackendService));
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
            context.StartForegroundService(intent);
        else
            context.StartService(intent);
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnCreate()
    {
        base.OnCreate();
        CreateNotificationChannel();
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        // Enter the foreground immediately (must happen within ~5 s of StartForegroundService).
        var notification = BuildNotification();
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
            StartForeground(NotificationId, notification, global::Android.Content.PM.ForegroundService.TypeSpecialUse);
        else
            StartForeground(NotificationId, notification);

        // Build + start the backend once, off the main thread (DI graph + VM + server bind).
        lock (Gate)
        {
            if (_host == null)
            {
                if (HostReady.Task.IsCompleted)
                    HostReady = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                var host = new AndroidBackendHost();
                _host = host;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await host.StartAsync().ConfigureAwait(false);
                        HostReady.TrySetResult(host.Port);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[BackendService] host start FAILED: {ex}");
                        HostReady.TrySetException(ex);
                    }
                });
            }
        }

        // Sticky: if Android kills us under memory pressure, restart the service (no intent).
        return StartCommandResult.Sticky;
    }

    // Swiping the app away from Recents is the user's "quit" — stop the host (→ OnDestroy save),
    // the Android parallel to closing the desktop launcher window. NOTE: this fires ONLY on a
    // task swipe-away, NOT when the app is merely backgrounded (home / app-switch), so the host
    // keeps running in the background as intended and only a deliberate close tears it down.
    public override void OnTaskRemoved(Intent? rootIntent)
    {
        Console.WriteLine("[BackendService] task removed — stopping host.");
        StopSelf();
        base.OnTaskRemoved(rootIntent);
    }

    public override void OnDestroy()
    {
        AndroidBackendHost? host;
        lock (Gate) { host = _host; _host = null; }
        if (host != null)
        {
            // Block briefly so the config + field-state save completes before the process exits.
            try { host.StopAsync().GetAwaiter().GetResult(); }
            catch (Exception ex) { Console.WriteLine($"[BackendService] host stop failed: {ex.Message}"); }
        }
        base.OnDestroy();
    }

    private Notification BuildNotification()
    {
        var builder = OperatingSystem.IsAndroidVersionAtLeast(26)
            ? new Notification.Builder(this, ChannelId)
            : new Notification.Builder(this);
        builder.SetContentTitle("AgOpenWeb")
            .SetContentText("Guidance host running — open the app or browse to this device on :5174")
            .SetSmallIcon(ApplicationInfo?.Icon ?? global::Android.Resource.Drawable.IcDialogInfo)
            .SetOngoing(true);
        return builder.Build();
    }

    private void CreateNotificationChannel()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;
        var channel = new NotificationChannel(ChannelId, "AgOpenWeb host", NotificationImportance.Low)
        {
            Description = "Keeps the guidance host running in the background.",
        };
        var manager = (NotificationManager?)GetSystemService(NotificationService);
        manager?.CreateNotificationChannel(channel);
    }
}
