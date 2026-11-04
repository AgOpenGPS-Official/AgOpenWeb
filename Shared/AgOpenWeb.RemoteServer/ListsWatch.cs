// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AgOpenWeb.RemoteServer;

/// <summary>
/// Versions of the lists the web client shows from disk: fields and their jobs, tracks and
/// boundaries (Fields folder), and vehicle and tool profiles (Vehicles, Tools). The broadcast
/// loop compares these numbers every tick instead of listing folders and reading files (#604):
/// loops are for work that always needs doing, and these lists change only on a user action.
/// <para>The operating system says when something in a folder changes (FileSystemWatcher), so
/// the app's own writes and a folder copied in by hand both count, with no polling. A panel
/// that shows a list asks again when it opens (<c>lists.refresh</c>). Where a folder can't be
/// watched (iOS, a folder that doesn't exist yet), every command counts as a change instead.</para>
/// </summary>
public sealed class ListsWatch : IDisposable
{
    /// <summary>Events closer together than this become one change: a copied-in field or a
    /// saved job writes several files, and the list is read once they are all there.</summary>
    internal const int QuietMs = 300;

    /// <summary>Without a watcher a command's work may finish after the command (a file
    /// written in the background), so the change is counted again this much later.</summary>
    internal const int LateMs = 1500;

    // Files in a field folder that the lists read. Anything else (coverage tiles, elevation,
    // recorded paths, background image) changes nothing in them and is ignored.
    private static readonly string[] FieldFiles =
    {
        "field.geojson", "job.json", AgOpenWeb.Services.TrackFilesService.FileName,
        // An AgOpenGPS field not yet imported (the AgShare list peeks at it).
        "Field.txt", "Boundary.txt", "ABLines.txt",
    };

    private readonly object _gate = new();
    private readonly Counter _fields = new(), _profiles = new();
    private FileSystemWatcher? _fieldsW, _vehiclesW, _toolsW;
    private string? _fieldsPath, _vehiclesPath, _toolsPath;
    private bool _retry, _disposed;

    /// <summary>Changes to the Fields folder that matter to the lists.</summary>
    public long FieldsVersion => _fields.Read();

    /// <summary>Changes to the vehicle and tool profile folders.</summary>
    public long ProfilesVersion => _profiles.Read();

    /// <summary>All three folders are watched.</summary>
    public bool Watching { get; private set; }

    /// <summary>Watch these folders. Called every tick with the current paths: it compares
    /// strings and touches the disk only when a path changed, or after <see cref="Refresh"/>
    /// when a watcher couldn't be started.</summary>
    public void Watch(string? fields, string? vehicles, string? tools)
    {
        lock (_gate)
        {
            if (_disposed) return;
            bool retry = _retry; _retry = false;
            if (retry || fields != _fieldsPath) Replace(ref _fieldsW, _fieldsPath = fields, true, _fields);
            if (retry || vehicles != _vehiclesPath) Replace(ref _vehiclesW, _vehiclesPath = vehicles, false, _profiles);
            if (retry || tools != _toolsPath) Replace(ref _toolsW, _toolsPath = tools, false, _profiles);
            Watching = _fieldsW != null && _vehiclesW != null && _toolsW != null;
        }
    }

    /// <summary>A panel showing a list opened: read the lists again (also catches anything a
    /// watcher missed), and try again to watch a folder that couldn't be watched.</summary>
    public void Refresh()
    {
        lock (_gate) _retry = !Watching;
        _fields.Bump(now: true); _profiles.Bump(now: true); // the panel is waiting for it
    }

    /// <summary>A command from a browser. Counts as a change only while a folder isn't
    /// watched, now and once more when its work has had time to finish.</summary>
    public void NoteCommand()
    {
        if (Watching) return;
        _fields.Bump(); _profiles.Bump();
        _ = Task.Delay(LateMs).ContinueWith(_ => { _fields.Bump(); _profiles.Bump(); }, TaskScheduler.Default);
    }

    private void Replace(ref FileSystemWatcher? w, string? path, bool fieldsFolder, Counter counter)
    {
        w?.Dispose();
        w = null;
        counter.Bump(); // a new folder: its list is read again
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            var root = Path.GetFullPath(path);
            var fsw = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = fieldsFolder,
                NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.FileName | NotifyFilters.LastWrite,
            };
            FileSystemEventHandler changed = (_, e) => { if (!fieldsFolder || Matters(root, e.FullPath)) counter.Bump(); };
            fsw.Created += changed;
            fsw.Deleted += changed;
            fsw.Changed += changed;
            fsw.Renamed += (_, e) => { if (!fieldsFolder || Matters(root, e.FullPath) || Matters(root, e.OldFullPath)) counter.Bump(); };
            fsw.Error += (_, _) => counter.Bump(); // events lost (buffer overflow): read it again
            fsw.EnableRaisingEvents = true;
            w = fsw;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or PlatformNotSupportedException
                                       or UnauthorizedAccessException or NotSupportedException)
        {
            // Not watched (no such folder yet, or no watcher on this platform): commands
            // count as changes until Refresh starts one.
        }
    }

    /// <summary>A field folder itself (added, removed, renamed), or a file the lists read.</summary>
    internal static bool Matters(string root, string fullPath)
    {
        var rel = Path.GetRelativePath(root, fullPath);
        if (rel.StartsWith("..", StringComparison.Ordinal)) return false;
        if (rel.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }) < 0) return true;
        var name = Path.GetFileName(fullPath);
        foreach (var f in FieldFiles)
            if (string.Equals(name, f, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _fieldsW?.Dispose(); _vehiclesW?.Dispose(); _toolsW?.Dispose();
            _fieldsW = _vehiclesW = _toolsW = null;
            Watching = false;
        }
    }

    /// <summary>A change count that shows a burst of events once it has been quiet for
    /// <see cref="QuietMs"/>.</summary>
    private sealed class Counter
    {
        private long _shown, _pending, _lastAt;

        public void Bump(bool now = false)
        {
            Interlocked.Exchange(ref _lastAt, now ? Environment.TickCount64 - QuietMs : Environment.TickCount64);
            Interlocked.Increment(ref _pending);
        }

        public long Read()
        {
            long pending = Interlocked.Read(ref _pending);
            if (pending != Interlocked.Read(ref _shown)
                && Environment.TickCount64 - Interlocked.Read(ref _lastAt) >= QuietMs)
                Interlocked.Exchange(ref _shown, pending);
            return Interlocked.Read(ref _shown);
        }
    }
}
