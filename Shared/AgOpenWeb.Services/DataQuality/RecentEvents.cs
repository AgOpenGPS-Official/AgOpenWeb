// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

namespace AgOpenWeb.Services.DataQuality;

/// <summary>
/// The times of the last things a sensor counted, and the rule the counting sensors share: a
/// good source gives none, so <see cref="FaultCount"/> in <see cref="WindowSeconds"/> is a
/// fault, which ends when a whole window had none. One stray event is counted and shown, and
/// nobody is told. Nothing is allocated after it is made; the owner does the locking.
/// </summary>
internal sealed class RecentEvents
{
    /// <summary>The stretch that is judged.</summary>
    public const double WindowSeconds = 60.0;
    /// <summary>This many in the window (or more) is a fault.</summary>
    public const int FaultCount = 5;

    private const int Capacity = 1024;
    private readonly long[] _at = new long[Capacity];
    private int _count;
    private int _next;

    /// <summary>All of them since the app started.</summary>
    public long Total { get; private set; }
    public bool Faulty { get; private set; }
    public int Faults { get; private set; }
    /// <summary>How many were in the window at the last <see cref="Judge"/>.</summary>
    public int InWindow { get; private set; }

    public void Add(long timestamp)
    {
        Total++;
        _at[_next] = timestamp;
        _next = (_next + 1) % Capacity;
        if (_count < Capacity) _count++;
    }

    /// <summary>Count the window ending now and apply the rule.</summary>
    public int Judge(long now)
    {
        long from = now - (long)(WindowSeconds * System.Diagnostics.Stopwatch.Frequency);
        int n = 0;
        for (int i = 0; i < _count; i++)
        {
            if (_at[((_next - 1 - i) % Capacity + Capacity) % Capacity] < from) break;
            n++;
        }
        InWindow = n;
        if (!Faulty && n >= FaultCount)
        {
            Faulty = true;
            Faults++;
        }
        else if (Faulty && n == 0) Faulty = false;
        return n;
    }

    /// <summary>The source went silent or closed: the window is forgotten, the totals kept.</summary>
    public void Clear()
    {
        _count = 0;
        _next = 0;
        InWindow = 0;
        Faulty = false;
    }
}
