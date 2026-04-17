// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.IO;
using Avalonia;
using Avalonia.Media;

namespace AgValoniaGPS.Views.Diagnostics;

/// <summary>
/// FPS diagnostic flags. File-presence based so each test scenario is a single
/// file touch + app restart — no rebuilds for the whole isolation matrix.
///
/// All flags are read ONCE at process startup from
/// <c>Documents/AgValoniaGPS/&lt;flag-name&gt;</c>. The flag is set if the file
/// exists, regardless of contents. Restart the app to pick up changes.
///
/// Flag state is logged once at init via <c>LogInitState</c> so every test run
/// is self-documenting in logcat.
///
/// See <c>Plans/FPS_DIAGNOSTIC_PLAN.md</c> for methodology.
/// </summary>
public static class DiagFlags
{
    // Draw-op skip flags — isolate individual contributors to the render budget
    public static readonly bool SkipCoverageDraw;
    public static readonly bool SkipBoundaryDraw;
    public static readonly bool SkipTracks;
    public static readonly bool SkipGroundTexture;
    public static readonly bool SkipGrid;
    public static readonly bool SkipVehicle;

    // Panel/UI flags
    public static readonly bool PanelsOpaque;
    public static readonly bool HideAllPanels;

    // Compositor/binding behaviour flags
    public static readonly bool DisableAnimationFrameUpdate;
    public static readonly bool LogSendStateFrequency;

    // Any flag set?
    public static readonly bool AnySet;

    static DiagFlags()
    {
        SkipCoverageDraw           = MarkerPresent(".skip_coverage_draw");
        SkipBoundaryDraw           = MarkerPresent(".skip_boundary_draw");
        SkipTracks                 = MarkerPresent(".skip_tracks");
        SkipGroundTexture          = MarkerPresent(".skip_ground_texture");
        SkipGrid                   = MarkerPresent(".skip_grid");
        SkipVehicle                = MarkerPresent(".skip_vehicle");
        PanelsOpaque               = MarkerPresent(".panels_opaque");
        HideAllPanels              = MarkerPresent(".hide_all_panels");
        DisableAnimationFrameUpdate = MarkerPresent(".disable_animation_frame_update");
        LogSendStateFrequency      = MarkerPresent(".log_send_state_frequency");

        AnySet = SkipCoverageDraw
               || SkipBoundaryDraw
               || SkipTracks
               || SkipGroundTexture
               || SkipGrid
               || SkipVehicle
               || PanelsOpaque
               || HideAllPanels
               || DisableAnimationFrameUpdate
               || LogSendStateFrequency;
    }

    /// <summary>
    /// Call from each platform's App.OnFrameworkInitializationCompleted after
    /// Application.Current is available. Logs flag state and applies any
    /// resource-level overrides (e.g. panels_opaque replaces the system chrome
    /// brush with an opaque version).
    /// </summary>
    public static void ApplyAtStartup(Application app)
    {
        LogInitState();

        if (PanelsOpaque)
        {
            // Replace the DynamicResource used by all FloatingPanel styles so
            // every panel background becomes opaque without per-panel edits.
            var opaque = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
            app.Resources["SystemControlBackgroundChromeMediumBrush"] = opaque;
            Console.WriteLine("[DiagFlags] panels_opaque: overrode SystemControlBackgroundChromeMediumBrush with solid #2A2A2A");
        }
    }

    /// <summary>
    /// Emit the full flag state to logcat/console. Call once at app init so every
    /// diagnostic run is self-documenting.
    /// </summary>
    public static void LogInitState()
    {
        Console.WriteLine(
            $"[DiagFlags] dir={ResolveDiagDir() ?? "<none>"}"
            + $" coverage={SkipCoverageDraw}"
            + $" boundary={SkipBoundaryDraw}"
            + $" tracks={SkipTracks}"
            + $" ground={SkipGroundTexture}"
            + $" grid={SkipGrid}"
            + $" vehicle={SkipVehicle}"
            + $" opaquePanels={PanelsOpaque}"
            + $" hidePanels={HideAllPanels}"
            + $" disableAnimFrame={DisableAnimationFrameUpdate}"
            + $" logSendState={LogSendStateFrequency}");
    }

    private static string? ResolveDiagDir()
    {
        try
        {
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrEmpty(documents)) return null;
            return Path.Combine(documents, "AgValoniaGPS");
        }
        catch
        {
            return null;
        }
    }

    private static bool MarkerPresent(string fileName)
    {
        try
        {
            var dir = ResolveDiagDir();
            if (dir == null) return false;
            return File.Exists(Path.Combine(dir, fileName));
        }
        catch
        {
            return false;
        }
    }
}
