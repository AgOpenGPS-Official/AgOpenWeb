// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System.Threading;
using AgValoniaGPS.Models.Pipeline;

namespace AgValoniaGPS.Services.Pipeline;

/// <summary>
/// Lock-free implementation of <see cref="IPipelineIntents"/>.
/// Intent slots are int-encoded so every operation is a single
/// <see cref="Interlocked.Exchange(ref int, int)"/> — no boxing, no locks,
/// no waits. Registered as a singleton so UI commands and the cycle worker
/// share the same instance.
/// </summary>
public sealed class PipelineIntents : IPipelineIntents
{
    // Sentinel-encoded last-wins slot: 0 = no request, 1 = left, 2 = right.
    private int _manualYouTurn;

    // 0 = no request, 1 = clear pending.
    private int _clearYouTurn;

    public void RequestManualYouTurn(bool turnLeft)
    {
        Interlocked.Exchange(ref _manualYouTurn, turnLeft ? 1 : 2);
    }

    public void RequestClearYouTurn()
    {
        Interlocked.Exchange(ref _clearYouTurn, 1);
    }

    public PipelineIntentBatch Drain()
    {
        int manual = Interlocked.Exchange(ref _manualYouTurn, 0);
        int clear = Interlocked.Exchange(ref _clearYouTurn, 0);

        bool? manualYouTurn = manual switch
        {
            1 => true,
            2 => false,
            _ => null,
        };

        return new PipelineIntentBatch
        {
            ManualYouTurn = manualYouTurn,
            ClearYouTurn = clear == 1,
        };
    }
}
