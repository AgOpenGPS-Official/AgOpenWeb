// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

namespace AgValoniaGPS.Models.Pipeline;

/// <summary>
/// One-way intent channel from the UI thread into the cycle worker.
/// UI commands push intents via the <c>Request*</c> methods; the cycle worker
/// calls <see cref="Drain"/> at the start of each tick and reacts on that cycle.
///
/// Thread contract:
/// <list type="bullet">
///   <item><c>Request*</c> methods: safe to call from any thread (typically UI).</item>
///   <item><see cref="Drain"/>: called once per cycle from the cycle worker.</item>
///   <item>Implementations must be lock-free / wait-free so the UI thread is
///         never blocked by a concurrent drain.</item>
/// </list>
///
/// See <c>Plans/threading_model.svg</c> (purple intent arrow, cycle stage 1).
/// </summary>
public interface IPipelineIntents
{
    /// <summary>
    /// Request a manual U-turn. Last-wins semantics — if multiple requests arrive
    /// between drains, only the most recent is observed.
    /// </summary>
    /// <param name="turnLeft"><c>true</c> for a left turn, <c>false</c> for right.</param>
    void RequestManualYouTurn(bool turnLeft);

    /// <summary>
    /// Request a full YouTurn state reset. Idempotent between drains.
    /// </summary>
    void RequestClearYouTurn();

    /// <summary>
    /// Request a snap to the adjacent offset pass. Last-wins semantics —
    /// only the most recent left/right request since the previous drain is
    /// observed. The cycle applies the direction (±1) to
    /// <c>HowManyPathsAway</c> and zeroes <c>NudgeOffset</c>.
    /// </summary>
    /// <param name="left"><c>true</c> to snap left, <c>false</c> to snap right.</param>
    void RequestGuidanceSnap(bool left);

    /// <summary>
    /// Atomically read and clear all pending intents. Called once per cycle tick.
    /// </summary>
    PipelineIntentBatch Drain();
}
