// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Guidance;

namespace AgOpenWeb.Services.Pipeline;

/// <summary>
/// The offset passes of the active curve, kept until the track changes. Offsetting tests every
/// offset point against the curve, and a boundary curve has a point every metre or so (#450):
/// the displayed pass, the steered pass and the guidelines beside them were rebuilt every GPS
/// cycle. Now a pass is offset once, when the tractor moves to it. The lists returned are shared:
/// callers copy before changing them.
/// </summary>
internal sealed class OffsetPassCache
{
    // The pass, the pass the tractor is nearest, and the guidelines either side of them.
    private const int Capacity = 128;

    private readonly object _lock = new();
    private readonly Dictionary<double, (List<Vec3> Points, double PercentRemoved)> _passes = new();
    private Models.Track.Track? _track;
    private List<Vec3>? _points;
    private int _count;

    public (List<Vec3> Points, double PercentRemoved) Get(Models.Track.Track track, double distAway)
    {
        lock (_lock)
        {
            if (!ReferenceEquals(track, _track) || !ReferenceEquals(track.Points, _points) || track.Points.Count != _count)
            {
                _passes.Clear();
                _track = track;
                _points = track.Points;
                _count = track.Points.Count;
            }
            if (_passes.TryGetValue(distAway, out var hit)) return hit;
            if (_passes.Count >= Capacity) _passes.Clear();
            var made = CurveProcessing.CreateOffsetCurveWithInfo(track.Points, distAway, track.IsClosed);
            _passes[distAway] = made;
            return made;
        }
    }
}
