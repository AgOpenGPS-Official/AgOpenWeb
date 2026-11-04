// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AgOpenWeb.Services;

/// <summary>
/// Lists the <see cref="ImportFolder"/> when a picker asks (<c>import.scan</c>), on a
/// background task so a folder full of datasets never holds up the host thread, and keeps the
/// result for the FieldOps frame. <see cref="Version"/> moves on when the listing changes, so
/// the broadcaster re-sends the frame then and never walks the folder itself.
/// </summary>
public sealed class ImportFolderScanner
{
    private readonly object _gate = new();
    private Task _running = Task.CompletedTask;
    private bool _scanning, _again;
    private ImportFolder.Listing _latest = ImportFolder.Listing.Empty;
    private long _version;

    /// <summary>The last listing; empty until the first scan.</summary>
    public ImportFolder.Listing Latest => Volatile.Read(ref _latest);

    /// <summary>Moves on each time a scan finds a different listing.</summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>
    /// Make the folder if it is missing and list it in the background. A request while a scan
    /// runs makes one more scan after it, so a file copied in meanwhile is not missed. The task
    /// ends when the listing is stored (tests wait on it; the host doesn't).
    /// </summary>
    public Task Request()
    {
        lock (_gate)
        {
            if (_scanning)
            {
                _again = true;
                return _running;
            }
            _scanning = true;
            _running = Task.Run(ScanUntilAsked);
            return _running;
        }
    }

    private void ScanUntilAsked()
    {
        while (true)
        {
            ImportFolder.Listing listing;
            try
            {
                listing = ImportFolder.Scan(ImportFolder.Ensure());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                listing = ImportFolder.Listing.Empty; // no data root to make it in
            }
            catch
            {
                // Anything else: keep the last listing, and let the next picker ask again
                // (left set, _scanning would refuse every scan until the app restarts).
                lock (_gate) { _scanning = false; _again = false; }
                throw;
            }
            if (!listing.SameAs(Latest))
            {
                Volatile.Write(ref _latest, listing);
                Interlocked.Increment(ref _version);
            }
            lock (_gate)
            {
                if (!_again)
                {
                    _scanning = false;
                    return;
                }
                _again = false;
            }
        }
    }
}
