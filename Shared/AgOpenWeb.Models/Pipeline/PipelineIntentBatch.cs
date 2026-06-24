// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

namespace AgOpenWeb.Models.Pipeline;

/// <summary>
/// Immutable batch of intents drained from <see cref="IPipelineIntents"/> at the
/// start of a cycle. Consumed by the cycle worker on the background thread.
/// See <c>Plans/threading_model.svg</c> (cycle stage 1 — Drain intents).
/// </summary>
public readonly record struct PipelineIntentBatch
{
    /// <summary>
    /// Manual U-turn request. <c>null</c> = none, <c>true</c> = left, <c>false</c> = right.
    /// Last-wins: only the most recent request since the previous drain is observed.
    /// </summary>
    public bool? ManualYouTurn { get; init; }

    /// <summary>
    /// Whether the UI requested a full YouTurn state reset since the previous drain.
    /// </summary>
    public bool ClearYouTurn { get; init; }

    /// <summary>
    /// Guidance snap request. <c>null</c> = none, <c>true</c> = left,
    /// <c>false</c> = right. Last-wins semantics.
    /// </summary>
    public bool? GuidanceSnap { get; init; }

    /// <summary>
    /// Accumulated guidance nudge delta in meters (positive = right,
    /// negative = left; pre-heading-adjustment). <c>0</c> = no request.
    /// Multiple nudge requests between drains sum into this value.
    /// </summary>
    public double GuidanceNudgeMeters { get; init; }

    /// <summary>
    /// Whether the UI requested a nudge-offset reset since the previous
    /// drain. Applied after any accumulated <see cref="GuidanceNudgeMeters"/>
    /// — reset wins if both arrive in the same tick.
    /// </summary>
    public bool GuidanceResetNudge { get; init; }
}
