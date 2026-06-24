// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.IO;

namespace AgOpenWeb.Models.Diagnostics;

/// <summary>
/// FPS diagnostic flags. File-presence based so each test scenario is a single
/// file touch + app restart — no rebuilds for the whole isolation matrix.
///
/// All flags are read ONCE at process startup from
/// <c>Documents/AgOpenWeb/&lt;flag-name&gt;</c>. The flag is set if the file
/// exists, regardless of contents. Restart the app to pick up changes.
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

    // Vehicle-debug overlay (track-width / wheelbase / wheel-target squares)
    public static readonly bool ShowVehicleDebug;

    // Panel/UI flags
    public static readonly bool PanelsOpaque;
    public static readonly bool HideAllPanels;

    // Compositor/binding behaviour flags
    public static readonly bool DisableAnimationFrameUpdate;
    public static readonly bool LogSendStateFrequency;
    public static readonly bool LogRenderTiming;

    // PERF-05 subsystem markers — each enables [<Subsystem>-PERF] emission
    // (time + GC.GetAllocatedBytesForCurrentThread deltas at 1 Hz). See
    // Plans/PERF_05_SUBSYSTEM_CHURN_AUDIT.md. Subsystem 1 (2D render path)
    // reuses LogRenderTiming above.
    public static readonly bool PerfStateMirror;
    public static readonly bool PerfGpsPipeline;
    public static readonly bool PerfGuidance;
    public static readonly bool PerfCoverage;
    public static readonly bool PerfUdp;
    public static readonly bool PerfAutoSteer;
    // Phase 2a: UI-thread bridge from background GPS cycle to State updates.
    // Suspected dominant source of the iPad "+13 ms outside OnRender" cost
    // observed at S5 in Phase 1.
    public static readonly bool PerfApplyGpsCycle;

    // Test-harness flags
    public static readonly bool AutoResumeField;

    public static readonly bool AnySet;

    static DiagFlags()
    {
        SkipCoverageDraw           = MarkerPresent(".skip_coverage_draw");
        SkipBoundaryDraw           = MarkerPresent(".skip_boundary_draw");
        SkipTracks                 = MarkerPresent(".skip_tracks");
        SkipGroundTexture          = MarkerPresent(".skip_ground_texture");
        SkipGrid                   = MarkerPresent(".skip_grid");
        SkipVehicle                = MarkerPresent(".skip_vehicle");
        ShowVehicleDebug           = MarkerPresent(".show_vehicle_debug");
        PanelsOpaque               = MarkerPresent(".panels_opaque");
        HideAllPanels              = MarkerPresent(".hide_all_panels");
        DisableAnimationFrameUpdate = MarkerPresent(".disable_animation_frame_update");
        LogSendStateFrequency      = MarkerPresent(".log_send_state_frequency");
        LogRenderTiming            = MarkerPresent(".log_render_timing");
        PerfStateMirror            = MarkerPresent(".perf_state_mirror");
        PerfGpsPipeline            = MarkerPresent(".perf_gps_pipeline");
        PerfGuidance               = MarkerPresent(".perf_guidance");
        PerfCoverage               = MarkerPresent(".perf_coverage");
        PerfUdp                    = MarkerPresent(".perf_udp");
        PerfAutoSteer              = MarkerPresent(".perf_autosteer");
        PerfApplyGpsCycle          = MarkerPresent(".perf_apply_gps_cycle");
        AutoResumeField            = MarkerPresent(".auto_resume_field");

        AnySet = SkipCoverageDraw || SkipBoundaryDraw || SkipTracks
               || SkipGroundTexture || SkipGrid || SkipVehicle
               || ShowVehicleDebug
               || PanelsOpaque || HideAllPanels
               || DisableAnimationFrameUpdate || LogSendStateFrequency
               || LogRenderTiming
               || PerfStateMirror || PerfGpsPipeline || PerfGuidance
               || PerfCoverage || PerfUdp || PerfAutoSteer
               || PerfApplyGpsCycle
               || AutoResumeField;
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
            + $" vehicleDebug={ShowVehicleDebug}"
            + $" opaquePanels={PanelsOpaque}"
            + $" hidePanels={HideAllPanels}"
            + $" disableAnimFrame={DisableAnimationFrameUpdate}"
            + $" logSendState={LogSendStateFrequency}"
            + $" logRenderTiming={LogRenderTiming}"
            + $" perfStateMirror={PerfStateMirror}"
            + $" perfGpsPipeline={PerfGpsPipeline}"
            + $" perfGuidance={PerfGuidance}"
            + $" perfCoverage={PerfCoverage}"
            + $" perfUdp={PerfUdp}"
            + $" perfAutoSteer={PerfAutoSteer}"
            + $" perfApplyGpsCycle={PerfApplyGpsCycle}"
            + $" autoResumeField={AutoResumeField}");
    }

    private static string? ResolveDiagDir()
    {
        try
        {
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrEmpty(documents)) return null;
            return Path.Combine(documents, "AgOpenWeb");
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
