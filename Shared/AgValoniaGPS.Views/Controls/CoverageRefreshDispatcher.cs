// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

namespace AgValoniaGPS.Views.Controls;

/// <summary>
/// Single source of truth for translating a <c>CoverageUpdated</c> notification
/// into the map control's bitmap-refresh calls.
///
/// This lives in shared code (rather than inline in each platform's
/// CoverageUpdated handler) so the Desktop / iOS / Android copies cannot drift —
/// which is exactly how the cold-start "Resume Last Job shows blank coverage"
/// bug arose: a full reload was being short-circuited to an incremental update.
/// </summary>
public static class CoverageRefreshDispatcher
{
    /// <summary>
    /// Apply a coverage-changed notification to <paramref name="map"/>.
    ///
    /// A <b>full reload</b> (field load via <c>LoadFromFile</c>, or <c>ClearAll</c>
    /// on field close) <b>always</b> forces a full rebuild. On a cold field open
    /// the coverage bitmap is created blank by
    /// <c>InitializeCoverageBitmapWithBounds</c> (which clears the full-rebuild
    /// flag) <i>before</i> the cells are loaded, and the loaded cells are not
    /// tracked as "new", so an incremental update would paint nothing — the field
    /// would open with blank coverage until a close+reopen happened to inherit a
    /// stale full-rebuild flag. Steady-state paints (<paramref name="isFullReload"/>
    /// = <c>false</c>) stay incremental for performance.
    /// </summary>
    public static void Apply(ISharedMapControl map, bool isFullReload)
    {
        if (isFullReload)
        {
            // Drop the existing SKBitmap paint + re-composite the background, then
            // repaint from every cell the service currently holds (zero after
            // ClearAll, the full loaded set after LoadFromFile).
            map.ClearCoveragePixels();
            map.MarkCoverageFullRebuildNeeded();
        }
        else
        {
            map.MarkCoverageDirty();
        }
    }
}
