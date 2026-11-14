// AgOpenWeb
// Copyright (C) 2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

namespace AgOpenWeb.FieldJobScenarios.Tests;

/// <summary>
/// The valves against the screen, fix by fix, for each section. A mismatch of up to a second at a
/// change is the switching latency, not a fault; what lasts longer counts against AgOpenWeb
/// (<see cref="Persistent"/>), unless the emulated module missed frames during that stretch while
/// it was online: then the harness's own machine was too busy to deliver them (the runner, not the
/// app) and the stretch is counted apart (<see cref="DuringLostFrames"/>). A module that is off or
/// whose Wi-Fi is down loses its frames on purpose; that is not a lost frame here.
/// </summary>
public sealed class ValveFollowTally
{
    /// <summary>Fixes (of 0.1 s) a mismatch may last before it counts.</summary>
    public const int GraceFixes = 10;

    private readonly int[] _run = new int[64];
    private readonly bool[] _lostInRun = new bool[64];
    private bool _lostThisFix;

    /// <summary>Seconds of mismatch beyond the grace, with every frame delivered: AgOpenWeb's.</summary>
    public readonly double[] Persistent = new double[64];

    /// <summary>Seconds of mismatch beyond the grace in stretches where the module missed frames.</summary>
    public readonly double[] DuringLostFrames = new double[64];

    /// <summary>Frames (PGN 239) the module should have had while online and never got (a frame
    /// that came late counts in the fix it arrived in).</summary>
    public int FramesMissed => Math.Max(0, FramesExpected - FramesReceived);

    /// <summary>Frames (PGN 239) that arrived while the module was online.</summary>
    public int FramesReceived { get; private set; }

    /// <summary>Frames (PGN 239) expected while the module was online.</summary>
    public int FramesExpected { get; private set; }

    /// <summary>Start a fix: how many PGN 239 frames the app sent in it and how many arrived, the
    /// module online through the whole fix (null: it was not, nothing is expected).</summary>
    public void BeginFix(int sent, int? received)
    {
        _lostThisFix = false;
        if (received is not { } got) return;
        FramesExpected += sent;
        FramesReceived += got;
        _lostThisFix = got < sent;   // lost, or late: either way the module acted on old frames
    }

    public void Section(int s, bool screen, bool valve, double seconds)
    {
        if (screen == valve) { _run[s] = 0; _lostInRun[s] = false; return; }
        if (_lostThisFix) _lostInRun[s] = true;
        if (++_run[s] <= GraceFixes) return;
        if (_lostInRun[s]) DuringLostFrames[s] += seconds;
        else Persistent[s] += seconds;
    }
}
