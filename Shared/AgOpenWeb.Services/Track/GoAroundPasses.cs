// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;

using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Guidance;
using AgOpenWeb.Services.Geometry;

namespace AgOpenWeb.Services.Track;

/// <summary>
/// The one place a pass is bent round the obstacles chosen as <see cref="ObstacleAction.GoRound"/>,
/// shared by the GPS pipeline (the line steered, the line shown, the light bar) and the U-turn
/// planner (the pass walked to the turn, the pass the turn lands on): every one of them sees the
/// same bent pass, so a turn is never planned from a line the tractor does not drive. The
/// geometry is <see cref="ObstacleDetour"/>; the settings come from the configuration store.
/// <para>
/// It also remembers which side the tractor is going round each obstacle (a pin): while the
/// tractor is beside an obstacle and over on that side, a nudge, a snap or a changed setting
/// bends the new pass round the same side. A tractor that gets to the other side of the
/// obstacle (a U-turn next to it onto the neighbouring pass) lets the pin go, since a bend round
/// the pinned side would then cross the obstacle in front of it. An obstacle with no room
/// (<see cref="ObstacleDetour.Bend.Blocked"/>) gets a U-turn before it on this pass, from the
/// planner; the pipeline tells the operator.
/// </para>
/// </summary>
public sealed class GoAroundPasses
{
    private readonly ConfigurationStore _config;
    private readonly IPolygonOffsetService _offset;
    private readonly ObstacleDetour _detour;

    // The side the tractor is going round an obstacle, kept while it is beside it and on that side:
    // the obstacle's centre and the pass's heading alongside it tell which side the tractor is on.
    private sealed record Pin(int Side, IReadOnlyList<Vec2> Fence, double Reach, double CentreE, double CentreN, double Heading);

    // A pin lets go when the tractor is this far over on the other side of the obstacle's centre.
    private const double SideSlack = 0.5;
    private readonly Dictionary<int, Pin> _pins = new();
    private Models.Track.Track? _pinsTrack;
    private IReadOnlyDictionary<int, int>? _pinSides;

    // The Go around obstacles grown by half the tractor's track, for the straight-ahead check.
    private (Boundary? Boundary, long Stamp, double Grow, List<(int Inner, List<Vec2> Ring)> List) _ahead = (null, 0, 0, new());

    public GoAroundPasses(ConfigurationStore config, IPolygonOffsetService offset)
    {
        _config = config;
        _offset = offset;
        _detour = new ObstacleDetour(offset);
    }

    /// <summary>The bend the tractor was steered along last (null: none on the current pass).</summary>
    public ObstacleDetour.Bend? Current { get; private set; }

    /// <summary>
    /// The Go around obstacles on the pass steered now with no room to go round them: the U-turn
    /// planner treats them as U-turn obstacles on this pass.
    /// </summary>
    public IReadOnlyList<int> CurrentBlocked => Current?.Blocked ?? Array.Empty<int>();

    /// <summary>Whether the field has an obstacle chosen as Go around.</summary>
    public static bool HasGoRound(Boundary? boundary)
    {
        if (boundary == null) return false;
        foreach (var inner in boundary.InnerBoundaries)
            if (inner.IsValid && inner.AtObstacle == ObstacleAction.GoRound) return true;
        return false;
    }

    /// <summary>How far the pass keeps from a Go around obstacle with the settings now.</summary>
    public double KeepOut
    {
        get
        {
            var c = _config;
            return ObstacleDetour.KeepOutFor(c.ActualToolWidth, c.Tool.Offset, c.Vehicle.TrackWidth, Margin);
        }
    }

    /// <summary>
    /// The least safety margin, metres: the room the follower needs on a bend. On the scenario
    /// field the tractor holds a bent line to about 0.25 m, so a margin of nothing puts the
    /// implement into a hard obstacle by that much on every bend.
    /// </summary>
    public const double MinMargin = 0.3;

    /// <summary>The bend radius <see cref="MinMargin"/> holds for: the follower cuts a tighter bend by more.</summary>
    public const double MinMarginRadius = 6.0;

    /// <summary>
    /// The least safety margin for a bend of <paramref name="radius"/>: <see cref="MinMargin"/>
    /// from <see cref="MinMarginRadius"/> up, and more on a tighter bend, faster than in inverse
    /// proportion (the scenario tractor cuts a 4 m bend by 0.4 m, a 6 m one by 0.25 m).
    /// </summary>
    public static double MinMarginFor(double radius) =>
        MinMargin * Math.Pow(Math.Max(1.0, MinMarginRadius / Math.Max(radius, 0.1)), 1.5);

    /// <summary>
    /// The radius the bends are built with: the Curve radius setting, but never under the
    /// tractor's own minimum turning radius (wheelbase over the tangent of the steer angle),
    /// since the follower cannot hold a bend the tractor cannot steer.
    /// </summary>
    public double Radius
    {
        get
        {
            double setting = _config.Guidance.GoAroundRadius, min = _config.Vehicle.MinTurningRadius;
            return double.IsFinite(min) && min > 0 ? Math.Max(setting, min) : setting;
        }
    }

    /// <summary>
    /// The safety margin in force: the setting, never under <see cref="MinMarginFor"/> the radius.
    /// What the implement keeps off a hard Go around obstacle, on the bend and in the planner.
    /// </summary>
    public double SafetyMargin => Math.Max(MinMarginFor(Radius), _config.Guidance.GoAroundMargin);

    // The safety margin plus a trailed tool's cut-in: round the obstacle the inside of the bend
    // is the obstacle's side.
    private double Margin => SafetyMargin + ObstacleDetour.TrailedCutIn(_config.Tool, _config.Vehicle.HitchLength, Radius);

    /// <summary>
    /// The pass <paramref name="pass"/> (offset <paramref name="distAway"/> from
    /// <paramref name="track"/>) bent round its Go around obstacles, or null when it runs into
    /// none. Open tracks only: a closed loop is a boundary pass. Cached while nothing changed.
    /// </summary>
    public ObstacleDetour.Bend? For(Models.Track.Track track, double distAway, IReadOnlyList<Vec3> pass, Boundary? boundary)
    {
        if (track.IsClosed || !HasGoRound(boundary)) return null;
        var pins = ReferenceEquals(track, _pinsTrack) ? _pinSides : null;
        return _detour.For(track, distAway, pass, boundary, KeepOut, Radius, Margin, pins);
    }

    /// <summary>
    /// The pass bent, or the pass itself: for the planner, which walks whatever line it gets.
    /// </summary>
    public List<Vec3> Bent(Models.Track.Track track, double distAway, List<Vec3> pass, Boundary? boundary)
    {
        var bend = For(track, distAway, pass, boundary);
        return bend != null && bend.GoneRound.Count > 0 ? bend.Points : pass;
    }

    /// <summary>
    /// Called by the pipeline with the bend it steers along this cycle (null when the pass has
    /// none) and the tractor's pivot: pins the side of every obstacle the tractor is beside and
    /// over on the bend's side of, and lets go of the pins of obstacles it has left behind or got
    /// to the other side of. A new track drops every pin.
    /// </summary>
    public void Steered(Models.Track.Track track, ObstacleDetour.Bend? bend, double easting, double northing)
    {
        Current = bend;
        if (!ReferenceEquals(track, _pinsTrack))
        {
            _pins.Clear();
            _pinsTrack = track;
        }
        bool changed = false;
        if (bend != null)
            foreach (var r in bend.Rounds)
            {
                if (!r.IsBeside(easting, northing)) continue;
                if (_pins.TryGetValue(r.InnerIndex, out var had) && had.Side == r.Side) continue;
                var (cE, cN) = Centre(r.Fence);
                double heading = HeadingAlongside(bend.Points, cE, cN);
                // Not over on the bend's side yet (still on the line, in the lead-in): a flip would
                // be harmless, so nothing is pinned.
                if (Lateral(cE, cN, heading, easting, northing) * r.Side <= 0) continue;
                _pins[r.InnerIndex] = new Pin(r.Side, r.Fence, r.Reach, cE, cN, heading);
                changed = true;
            }
        foreach (var (inner, pin) in _pins.ToList())
            if (ObstacleDetour.DistanceToRing(pin.Fence, easting, northing) > pin.Reach
                || Lateral(pin.CentreE, pin.CentreN, pin.Heading, easting, northing) * pin.Side < -SideSlack)
            {
                _pins.Remove(inner);
                changed = true;
            }
        if (changed || (_pinSides == null) != (_pins.Count == 0))
            _pinSides = _pins.Count == 0 ? null : _pins.ToDictionary(p => p.Key, p => p.Value.Side);
    }

    /// <summary>Whether the tractor's side of the obstacle is pinned, and which (+1 left of the pass, -1 right); for tests.</summary>
    public int PinnedSide(int innerIndex) => _pins.TryGetValue(innerIndex, out var pin) ? pin.Side : 0;

    // How far (easting, northing) is to the left of a line through (cE, cN) with the heading (radians).
    private static double Lateral(double cE, double cN, double heading, double easting, double northing) =>
        -Math.Cos(heading) * (easting - cE) + Math.Sin(heading) * (northing - cN);

    private static (double E, double N) Centre(IReadOnlyList<Vec2> ring)
    {
        double e = 0, n = 0;
        foreach (var p in ring) { e += p.Easting; n += p.Northing; }
        return (e / ring.Count, n / ring.Count);
    }

    // The bent pass's heading where it runs alongside the obstacle (its point nearest the centre:
    // the flat of the bend, parallel to the pass), radians.
    private static double HeadingAlongside(List<Vec3> bent, double cE, double cN)
    {
        int best = 0;
        double bestD = double.MaxValue;
        for (int i = 0; i < bent.Count; i++)
        {
            double de = bent[i].Easting - cE, dn = bent[i].Northing - cN, d = de * de + dn * dn;
            if (d < bestD) { bestD = d; best = i; }
        }
        return bent[best].Heading;
    }

    /// <summary>
    /// The first Go around obstacle (its index in <see cref="Boundary.InnerBoundaries"/>) that
    /// the tractor would run into within <paramref name="distance"/> straight ahead, the
    /// obstacle grown by half the tractor's track, or -1. The safety net under the bend: the
    /// pipeline takes AutoSteer off when this is set and the tractor is off the steered line by
    /// more than the safety margin, since a tractor on the bent line is clear by construction.
    /// </summary>
    public int ObstacleAhead(Boundary? boundary, double easting, double northing, double heading, double distance)
    {
        if (!HasGoRound(boundary)) return -1;
        var grown = GrownForAhead(boundary!);
        if (grown.Count == 0) return -1;
        double se = Math.Sin(heading), ce = Math.Cos(heading);
        int steps = Math.Max(1, (int)Math.Ceiling(distance / 0.5));
        for (int k = 0; k <= steps; k++)
        {
            double t = distance * k / steps, e = easting + se * t, n = northing + ce * t;
            foreach (var (inner, ring) in grown)
                if (GeometryMath.IsPointInPolygon(ring, e, n)) return inner;
        }
        return -1;
    }

    private List<(int Inner, List<Vec2> Ring)> GrownForAhead(Boundary boundary)
    {
        long stamp = YouTurn.YouTurnCreationService.ObstacleStamp(boundary);
        double grow = Math.Max(0.3, _config.Vehicle.TrackWidth / 2);
        var cached = _ahead;
        if (ReferenceEquals(cached.Boundary, boundary) && cached.Stamp == stamp && cached.Grow == grow) return cached.List;
        var list = new List<(int, List<Vec2>)>();
        for (int i = 0; i < boundary.InnerBoundaries.Count; i++)
        {
            var inner = boundary.InnerBoundaries[i];
            if (!inner.IsValid || inner.AtObstacle != ObstacleAction.GoRound) continue;
            var outline = inner.Points.Select(p => new Vec2(p.Easting, p.Northing)).ToList();
            var ring = _offset.CreateOutwardOffset(outline, grow);
            if (ring != null && ring.Count >= 3) list.Add((i, ring));
        }
        _ahead = (boundary, stamp, grow, list);
        return list;
    }
}
