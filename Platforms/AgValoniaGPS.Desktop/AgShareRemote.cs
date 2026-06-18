// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

// Host-side AgShare orchestration for the remote (web) Field Operations panel. The native
// AgShare logic lives in the dialog code-behind (View layer, deleted at headless cutover),
// so the web cannot reuse it; this replicates the same orchestration against the AgShare
// services (which aren't DI-registered — instantiated directly, as the dialogs do). Runtime
// status/results are written to ApplicationState.AgShare (projected on the AgShare frame);
// settings (server/key/enabled) live in ConfigStore.Connections and ride config.set.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using AgValoniaGPS.Models;
using AgValoniaGPS.Models.AgShare;
using AgValoniaGPS.Models.Base;
using AgValoniaGPS.Models.Configuration;
using AgValoniaGPS.Models.State;
using AgValoniaGPS.Services;
using AgValoniaGPS.Services.AgShare;
using AgValoniaGPS.Services.Interfaces;

namespace AgValoniaGPS.Desktop;

internal static class AgShareRemote
{
    public static void Handle(string cmd, string arg, ApplicationState state,
        ConfigurationStore config, ISettingsService settings)
    {
        var c = config.Connections;
        var url = string.IsNullOrEmpty(c.AgShareServer) ? "https://agshare.agopengps.com" : c.AgShareServer;
        var key = c.AgShareApiKey ?? "";
        var root = settings.Settings.FieldsDirectory;
        if (string.IsNullOrWhiteSpace(root))
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AgValoniaGPS", "Fields");
        switch (cmd)
        {
            case "agshare.test": _ = TestAsync(state, url, key); return;
            case "agshare.fetch": _ = FetchAsync(state, url, key); return;
            case "agshare.download": _ = DownloadOneAsync(state, url, key, root, arg); return;     // arg = id
            case "agshare.downloadAll": _ = DownloadAllAsync(state, url, key, root, arg == "1"); return; // arg = force
            case "agshare.upload": _ = UploadAsync(state, url, key, root, arg); return;            // arg = public \t name…
        }
    }

    private static void Set(ApplicationState s, string status, bool busy) =>
        Dispatcher.UIThread.Post(() => { s.AgShare.Status = status; s.AgShare.Busy = busy; });

    private static async Task TestAsync(ApplicationState s, string url, string key)
    {
        if (string.IsNullOrEmpty(key)) { Set(s, "Please set an API key first", false); return; }
        Set(s, "Testing connection…", true);
        try { var (ok, msg) = await new AgShareClient(url, key).CheckApiAsync(); Set(s, (ok ? "Connected: " : "Failed: ") + msg, false); }
        catch (Exception ex) { Set(s, "Error: " + ex.Message, false); }
    }

    private static async Task FetchAsync(ApplicationState s, string url, string key)
    {
        if (string.IsNullOrEmpty(key)) { Set(s, "Please set an API key first", false); return; }
        Set(s, "Loading fields from AgShare…", true);
        try
        {
            var fields = await new AgShareDownloaderService(new AgShareClient(url, key)).GetOwnFieldsAsync();
            var list = (fields ?? new List<AgShareGetOwnFieldDto>())
                .OrderBy(f => f.Name)
                .Select(f => new AgShareCloudFieldInfo(f.Id.ToString(), f.Name, f.AreaHa))
                .ToList();
            Dispatcher.UIThread.Post(() =>
            {
                s.AgShare.CloudFields = list;
                s.AgShare.Status = "Found " + list.Count + " field(s)";
                s.AgShare.Busy = false;
            });
        }
        catch (Exception ex) { Set(s, "Error: " + ex.Message, false); }
    }

    private static async Task DownloadOneAsync(ApplicationState s, string url, string key, string root, string id)
    {
        if (!Guid.TryParse(id, out var gid)) return;
        Set(s, "Downloading…", true);
        try { var (ok, msg) = await new AgShareDownloaderService(new AgShareClient(url, key)).DownloadAndSaveAsync(gid, root); Set(s, ok ? "Downloaded" : "Failed: " + msg, false); }
        catch (Exception ex) { Set(s, "Error: " + ex.Message, false); }
    }

    private static async Task DownloadAllAsync(ApplicationState s, string url, string key, string root, bool force)
    {
        Set(s, "Downloading all…", true);
        try { var (d, sk) = await new AgShareDownloaderService(new AgShareClient(url, key)).DownloadAllAsync(root, force, null); Set(s, $"Downloaded {d}, skipped {sk}", false); }
        catch (Exception ex) { Set(s, "Error: " + ex.Message, false); }
    }

    private static async Task UploadAsync(ApplicationState s, string url, string key, string root, string arg)
    {
        if (string.IsNullOrEmpty(key)) { Set(s, "Please set an API key first", false); return; }
        var parts = arg.Split('\t');
        bool isPublic = parts.Length > 0 && parts[0] == "1";
        var names = parts.Skip(1).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        if (names.Count == 0) { Set(s, "No fields selected", false); return; }
        Set(s, "Uploading…", true);
        var client = new AgShareClient(url, key);
        var uploader = new AgShareUploaderService();
        var boundarySvc = new BoundaryFileService();
        int ok = 0, fail = 0;
        foreach (var name in names)
        {
            try { var (success, _) = await UploadOne(client, uploader, boundarySvc, Path.Combine(root, name), name, isPublic); if (success) ok++; else fail++; }
            catch { fail++; }
            Set(s, $"Uploaded {ok}, failed {fail} of {names.Count}…", true);
        }
        Set(s, $"Uploaded {ok} field(s)" + (fail > 0 ? $", {fail} failed" : ""), false);
    }

    // Mirrors AgShareUploadDialogPanel.UploadSingleFieldAsync: origin from Field.txt StartFix,
    // boundary via BoundaryFileService, existing cloud id from agshare.txt.
    private static async Task<(bool, string)> UploadOne(AgShareClient client, AgShareUploaderService uploader,
        BoundaryFileService boundarySvc, string dir, string name, bool isPublic)
    {
        var origin = new Wgs84(0, 0);
        var fieldTxt = Path.Combine(dir, "Field.txt");
        if (File.Exists(fieldTxt))
        {
            var lines = await File.ReadAllLinesAsync(fieldTxt);
            for (int i = 0; i < lines.Length - 1; i++)
                if (lines[i].Contains("StartFix"))
                {
                    var coords = lines[i + 1].Split(',');
                    if (coords.Length >= 2 && double.TryParse(coords[0], out var lat) && double.TryParse(coords[1], out var lon))
                        origin = new Wgs84(lat, lon);
                    break;
                }
        }
        var boundaries = new List<List<Vec3>>();
        var b = boundarySvc.LoadBoundary(dir);
        if (b?.OuterBoundary != null && b.OuterBoundary.Points.Count > 0)
        {
            boundaries.Add(b.OuterBoundary.Points.Select(p => new Vec3(p.Easting, p.Northing, p.Heading)).ToList());
            if (b.InnerBoundaries != null)
                foreach (var inner in b.InnerBoundaries)
                    boundaries.Add(inner.Points.Select(p => new Vec3(p.Easting, p.Northing, p.Heading)).ToList());
        }
        if (boundaries.Count == 0) return (false, "No boundary");
        Guid? existing = null;
        var idFile = Path.Combine(dir, "agshare.txt");
        if (File.Exists(idFile) && Guid.TryParse((await File.ReadAllTextAsync(idFile)).Trim(), out var pid)) existing = pid;
        var input = new FieldSnapshotInput
        {
            FieldId = existing, FieldName = name, Origin = origin, Boundaries = boundaries,
            Tracks = new List<TrackLineInput>(), IsPublic = isPublic, Convergence = 0,
        };
        var (resOk, msg, _) = await uploader.UploadFieldAsync(input, client, dir);
        return (resOk, msg);
    }
}
