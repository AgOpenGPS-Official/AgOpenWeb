// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
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

    // Sentinel-encoded last-wins snap slot: 0 = no request, 1 = left, 2 = right.
    private int _snap;

    // Accumulating nudge delta stored as double bit pattern so we can use
    // Interlocked on the 64-bit representation. A CAS loop handles the
    // read-modify-write for addition; drain uses Exchange(0) which matches
    // 0.0's bit pattern.
    private long _nudgeAccumulatorBits;

    // 0 = no request, 1 = reset pending. Reset always wins over accumulated
    // nudge in the same tick (see Drain).
    private int _resetNudge;

    public void RequestManualYouTurn(bool turnLeft)
    {
        Interlocked.Exchange(ref _manualYouTurn, turnLeft ? 1 : 2);
    }

    public void RequestClearYouTurn()
    {
        Interlocked.Exchange(ref _clearYouTurn, 1);
    }

    public void RequestGuidanceSnap(bool left)
    {
        Interlocked.Exchange(ref _snap, left ? 1 : 2);
    }

    public void RequestGuidanceNudge(double deltaMeters)
    {
        long currentBits, nextBits;
        do
        {
            currentBits = Interlocked.Read(ref _nudgeAccumulatorBits);
            double current = BitConverter.Int64BitsToDouble(currentBits);
            double next = current + deltaMeters;
            nextBits = BitConverter.DoubleToInt64Bits(next);
        }
        while (Interlocked.CompareExchange(ref _nudgeAccumulatorBits, nextBits, currentBits) != currentBits);
    }

    public void RequestGuidanceResetNudge()
    {
        Interlocked.Exchange(ref _resetNudge, 1);
    }

    public PipelineIntentBatch Drain()
    {
        int manual = Interlocked.Exchange(ref _manualYouTurn, 0);
        int clear = Interlocked.Exchange(ref _clearYouTurn, 0);
        int snap = Interlocked.Exchange(ref _snap, 0);
        long nudgeBits = Interlocked.Exchange(ref _nudgeAccumulatorBits, 0);
        int resetNudge = Interlocked.Exchange(ref _resetNudge, 0);

        bool? manualYouTurn = manual switch
        {
            1 => true,
            2 => false,
            _ => null,
        };

        bool? guidanceSnap = snap switch
        {
            1 => true,
            2 => false,
            _ => null,
        };

        return new PipelineIntentBatch
        {
            ManualYouTurn = manualYouTurn,
            ClearYouTurn = clear == 1,
            GuidanceSnap = guidanceSnap,
            GuidanceNudgeMeters = BitConverter.Int64BitsToDouble(nudgeBits),
            GuidanceResetNudge = resetNudge == 1,
        };
    }
}
