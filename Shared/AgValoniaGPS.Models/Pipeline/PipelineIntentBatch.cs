// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

namespace AgValoniaGPS.Models.Pipeline;

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
}
