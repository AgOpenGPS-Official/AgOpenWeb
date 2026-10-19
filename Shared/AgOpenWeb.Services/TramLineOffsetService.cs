// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.Geometry;
using AgOpenWeb.Models.Base;

namespace AgOpenWeb.Services;

/// <summary>
/// Core tramline offset generation service.
/// Uses Clipper2 (via PolygonOffsetService) for proper polygon inset
/// that handles concave boundaries without self-intersections.
/// </summary>
public class TramLineOffsetService : ITramLineOffsetService
{
    private readonly PolygonOffsetService _clipperOffset = new();

    /// <summary>
    /// Generate inner tramline offset from boundary fence line.
    /// Inner tramline is offset inward by (tramWidth * 0.5) + halfWheelTrack.
    /// </summary>
    public List<Vec2> GenerateInnerTramline(List<Vec3> fenceLine, double tramWidth, double halfWheelTrack)
    {
        double offset = (tramWidth * 0.5) + halfWheelTrack;
        return GenerateClipperOffset(fenceLine, offset);
    }

    /// <summary>
    /// Generate outer tramline offset from boundary fence line.
    /// Outer tramline is offset inward by (tramWidth * 0.5) - halfWheelTrack.
    /// </summary>
    public List<Vec2> GenerateOuterTramline(List<Vec3> fenceLine, double tramWidth, double halfWheelTrack)
    {
        double offset = (tramWidth * 0.5) - halfWheelTrack;
        return GenerateClipperOffset(fenceLine, offset);
    }

    /// <summary>
    /// Generate an inward offset at a specific distance from the fence line.
    /// </summary>
    public List<Vec2> GenerateClipperOffsetPublic(List<Vec3> fenceLine, double offset)
        => GenerateClipperOffset(fenceLine, offset);

    /// <summary>
    /// Use Clipper2 for proper polygon inset that handles concave shapes.
    /// </summary>
    private List<Vec2> GenerateClipperOffset(List<Vec3> fenceLine, double offset)
    {
        if (fenceLine == null || fenceLine.Count < 3)
            return new List<Vec2>();

        var boundaryVec2 = fenceLine.Select(p => new Vec2(p.Easting, p.Northing)).ToList();
        var result = _clipperOffset.CreateInwardOffset(boundaryVec2, offset);
        return result ?? new List<Vec2>();
    }
}
