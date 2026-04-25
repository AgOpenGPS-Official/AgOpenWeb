// AgValoniaGPS
// Copyright (C) 2024-2026 AgValoniaGPS Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.
//
// =============================================================================
// This file is a C# port of the Reeds-Shepp implementation in
//   github.com/Fields2Cover/steering_functions (Apache 2.0)
// which is itself derived from the Open Motion Planning Library (OMPL) v1.3.1
//   github.com/ompl/ompl (BSD 3-Clause)
// Copyright (c) 2010, Rice University.
// Original copyright/license notices retained below.
// =============================================================================
//
// Software License Agreement (BSD 3-Clause License)
//   Copyright (c) 2010, Rice University
//   All rights reserved.
//   Redistribution and use in source and binary forms, with or without
//   modification, are permitted provided that the following conditions are met:
//     * Redistributions of source code must retain the above copyright notice,
//       this list of conditions and the following disclaimer.
//     * Redistributions in binary form must reproduce the above copyright
//       notice, this list of conditions and the following disclaimer in the
//       documentation and/or other materials provided with the distribution.
//     * Neither the name of the Rice University nor the names of its
//       contributors may be used to endorse or promote products derived from
//       this software without specific prior written permission.
//   THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
//   AND ANY EXPRESS OR IMPLIED WARRANTIES ARE DISCLAIMED. IN NO EVENT SHALL THE
//   COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DAMAGES.
// =============================================================================

using System;
using System.Collections.Generic;
using AgValoniaGPS.Models.Base;

namespace AgValoniaGPS.Services.PathPlanning;

/// <summary>
/// Reeds-Shepp path planner. Computes the shortest path between two SE(2)
/// poses for a vehicle that can move both forwards and backwards with a
/// minimum turning radius. Implements all 48 Reeds-Shepp curves as catalogued
/// in Reeds &amp; Shepp 1990, returning the shortest valid solution.
///
/// Compared to <see cref="DubinsPathService"/> (forward-only), Reeds-Shepp
/// adds reverse motion and handles tighter geometric configurations,
/// including 3-point turns common at narrow agricultural headlands.
/// </summary>
public class ReedsSheppPathService
{
    public enum SegmentType
    {
        NoOp = 0,
        Left = 1,
        Straight = 2,
        Right = 3,
    }

    /// <summary>
    /// Catalogue of Reeds-Shepp path families. Each entry is an ordered sequence
    /// of up to 5 segment types; trailing entries are NoOp.
    /// </summary>
    public static readonly SegmentType[][] PathTypes =
    {
        new[] { SegmentType.Left, SegmentType.Right, SegmentType.Left, SegmentType.NoOp, SegmentType.NoOp },         // 0
        new[] { SegmentType.Right, SegmentType.Left, SegmentType.Right, SegmentType.NoOp, SegmentType.NoOp },        // 1
        new[] { SegmentType.Left, SegmentType.Right, SegmentType.Left, SegmentType.Right, SegmentType.NoOp },        // 2
        new[] { SegmentType.Right, SegmentType.Left, SegmentType.Right, SegmentType.Left, SegmentType.NoOp },        // 3
        new[] { SegmentType.Left, SegmentType.Right, SegmentType.Straight, SegmentType.Left, SegmentType.NoOp },     // 4
        new[] { SegmentType.Right, SegmentType.Left, SegmentType.Straight, SegmentType.Right, SegmentType.NoOp },    // 5
        new[] { SegmentType.Left, SegmentType.Straight, SegmentType.Right, SegmentType.Left, SegmentType.NoOp },     // 6
        new[] { SegmentType.Right, SegmentType.Straight, SegmentType.Left, SegmentType.Right, SegmentType.NoOp },    // 7
        new[] { SegmentType.Left, SegmentType.Right, SegmentType.Straight, SegmentType.Right, SegmentType.NoOp },    // 8
        new[] { SegmentType.Right, SegmentType.Left, SegmentType.Straight, SegmentType.Left, SegmentType.NoOp },     // 9
        new[] { SegmentType.Right, SegmentType.Straight, SegmentType.Right, SegmentType.Left, SegmentType.NoOp },    // 10
        new[] { SegmentType.Left, SegmentType.Straight, SegmentType.Left, SegmentType.Right, SegmentType.NoOp },     // 11
        new[] { SegmentType.Left, SegmentType.Straight, SegmentType.Right, SegmentType.NoOp, SegmentType.NoOp },     // 12
        new[] { SegmentType.Right, SegmentType.Straight, SegmentType.Left, SegmentType.NoOp, SegmentType.NoOp },     // 13
        new[] { SegmentType.Left, SegmentType.Straight, SegmentType.Left, SegmentType.NoOp, SegmentType.NoOp },      // 14
        new[] { SegmentType.Right, SegmentType.Straight, SegmentType.Right, SegmentType.NoOp, SegmentType.NoOp },    // 15
        new[] { SegmentType.Left, SegmentType.Right, SegmentType.Straight, SegmentType.Left, SegmentType.Right },    // 16
        new[] { SegmentType.Right, SegmentType.Left, SegmentType.Straight, SegmentType.Right, SegmentType.Left },    // 17
    };

    /// <summary>
    /// One Reeds-Shepp path. <see cref="Lengths"/> entries are signed in
    /// unit-curvature space (negative = reverse motion). <see cref="TotalLength"/>
    /// is the absolute sum.
    /// </summary>
    public sealed class RsPath
    {
        public SegmentType[] Types { get; }
        public double[] Lengths { get; }
        public double TotalLength { get; }

        public RsPath(SegmentType[] types, double t, double u, double v, double w = 0, double x = 0)
        {
            Types = types;
            Lengths = new[] { t, u, v, w, x };
            TotalLength = Math.Abs(t) + Math.Abs(u) + Math.Abs(v) + Math.Abs(w) + Math.Abs(x);
        }

        internal static RsPath WithMaxLength() =>
            new(PathTypes[0], double.MaxValue, 0, 0, 0, 0);
    }

    private readonly double _kappa;
    private readonly double _kappaInv;

    /// <param name="turningRadius">Minimum turning radius in meters (must be &gt; 0).</param>
    public ReedsSheppPathService(double turningRadius)
    {
        if (turningRadius <= 0)
            throw new ArgumentException("Turning radius must be > 0", nameof(turningRadius));
        _kappa = 1.0 / turningRadius;
        _kappaInv = turningRadius;
    }

    /// <summary>Shortest path length from start to goal at the configured turning radius.</summary>
    public double GetDistance(Vec3 start, Vec3 goal)
    {
        return _kappaInv * GetUnitCurvaturePath(start, goal).TotalLength;
    }

    /// <summary>
    /// Discretized waypoint sequence along the shortest path from start to goal.
    /// Each waypoint includes heading; reverse-motion segments are encoded by the
    /// per-segment direction returned via <see cref="GetShortestPath"/>.
    /// </summary>
    public List<Vec3> GetWaypoints(Vec3 start, Vec3 goal, double discretization = 0.1)
    {
        var path = GetUnitCurvaturePath(start, goal);
        return Integrate(start, path, discretization);
    }

    /// <summary>
    /// Full path description including per-segment direction (forward / reverse).
    /// </summary>
    public ShortestPath GetShortestPath(Vec3 start, Vec3 goal, double discretization = 0.1)
    {
        var path = GetUnitCurvaturePath(start, goal);
        var (waypoints, isReverse) = IntegrateWithDirection(start, path, discretization);
        return new ShortestPath
        {
            Waypoints = waypoints,
            IsReverse = isReverse,
            Length = _kappaInv * path.TotalLength,
            SegmentTypes = path.Types,
            SegmentSignedLengths = path.Lengths,
        };
    }

    public sealed class ShortestPath
    {
        public required List<Vec3> Waypoints { get; init; }
        public required List<bool> IsReverse { get; init; }
        public required double Length { get; init; }
        public required SegmentType[] SegmentTypes { get; init; }
        public required double[] SegmentSignedLengths { get; init; }
    }

    // =========================================================================
    // Core: convert (state1, state2) → unit-curvature problem and solve.
    // =========================================================================

    private RsPath GetUnitCurvaturePath(Vec3 start, Vec3 goal)
    {
        // Convert to OMPL convention (theta from +x axis CCW) for the RS formulas.
        // Our heading h is from +y (north) CW; OMPL theta = π/2 − h.
        double startThetaOmpl = Math.PI / 2.0 - start.Heading;
        double goalThetaOmpl = Math.PI / 2.0 - goal.Heading;

        double dx = goal.Easting - start.Easting;
        double dy = goal.Northing - start.Northing;
        double dth = goalThetaOmpl - startThetaOmpl;
        double c = Math.Cos(startThetaOmpl);
        double s = Math.Sin(startThetaOmpl);
        double x = c * dx + s * dy;
        double y = -s * dx + c * dy;
        return ReedsSheppUnit(x * _kappa, y * _kappa, dth);
    }

    /// <summary>
    /// Solve Reeds-Shepp at unit curvature for the relative pose (x, y, phi).
    /// Tries all 5 path families (CSC, CCC, CCCC, CCSC, CCSCC), returns the shortest.
    /// </summary>
    private static RsPath ReedsSheppUnit(double x, double y, double phi)
    {
        var path = RsPath.WithMaxLength();
        Csc(x, y, phi, ref path);
        Ccc(x, y, phi, ref path);
        Cccc(x, y, phi, ref path);
        Ccsc(x, y, phi, ref path);
        Ccscc(x, y, phi, ref path);
        return path;
    }

    // =========================================================================
    // Integration (path → waypoints).
    // =========================================================================

    private List<Vec3> Integrate(Vec3 start, RsPath path, double discretization)
    {
        var (waypoints, _) = IntegrateWithDirection(start, path, discretization);
        return waypoints;
    }

    private (List<Vec3>, List<bool>) IntegrateWithDirection(Vec3 start, RsPath path, double discretization)
    {
        var pts = new List<Vec3>();
        var rev = new List<bool>();

        // Integrate in OMPL frame (theta from +x CCW), convert back to our heading
        // (from +y CW) for each emitted Vec3.
        double currentX = start.Easting;
        double currentY = start.Northing;
        double currentTheta = Math.PI / 2.0 - start.Heading;

        for (int i = 0; i < 5; i++)
        {
            if (path.Types[i] == SegmentType.NoOp) break;

            // Reeds-Shepp formulas operate at unit curvature; scale lengths back up.
            double signedLen = _kappaInv * path.Lengths[i];
            double absLen = Math.Abs(signedLen);
            int direction = Math.Sign(signedLen); // +1 forward, -1 reverse, 0 zero-length

            // Curvature for this segment in OMPL convention: Left = CCW = +kappa.
            double kappa = path.Types[i] switch
            {
                SegmentType.Left => _kappa,
                SegmentType.Right => -_kappa,
                _ => 0.0,
            };

            // Push the current pose at the start of the segment.
            pts.Add(new Vec3(currentX, currentY, NormalizeOursHeading(Math.PI / 2.0 - currentTheta)));
            rev.Add(direction < 0);

            int steps = (int)Math.Ceiling(absLen / discretization);
            double sSeg = 0;
            for (int k = 0; k < steps; k++)
            {
                sSeg += discretization;
                double step = (sSeg > absLen) ? discretization - (sSeg - absLen) : discretization;
                if (sSeg > absLen) sSeg = absLen;
                double signedStep = direction * step;

                if (Math.Abs(kappa) < 1e-9)
                {
                    // Straight in OMPL: dx = signedStep*cos(theta), dy = signedStep*sin(theta)
                    currentX += signedStep * Math.Cos(currentTheta);
                    currentY += signedStep * Math.Sin(currentTheta);
                }
                else
                {
                    // Arc: theta_new = theta + signedStep*kappa
                    double thetaNew = currentTheta + signedStep * kappa;
                    currentX += (Math.Sin(thetaNew) - Math.Sin(currentTheta)) / kappa;
                    currentY -= (Math.Cos(thetaNew) - Math.Cos(currentTheta)) / kappa;
                    currentTheta = thetaNew;
                }

                pts.Add(new Vec3(currentX, currentY, NormalizeOursHeading(Math.PI / 2.0 - currentTheta)));
                rev.Add(direction < 0);
            }
        }

        return (pts, rev);
    }

    /// <summary>Normalize heading to [0, 2π) per our convention.</summary>
    private static double NormalizeOursHeading(double h)
    {
        const double TwoPi = 2.0 * Math.PI;
        h = h % TwoPi;
        if (h < 0) h += TwoPi;
        return h;
    }

    // =========================================================================
    // Helpers.
    // =========================================================================

    private const double RsEps = 1e-6;
    private const double RsZero = 10 * 1e-15; // ~10 * double.Epsilon for arithmetic-meaningful zero

    /// <summary>Wrap angle to (-π, π].</summary>
    private static double Pify(double angle)
    {
        const double TwoPi = 2.0 * Math.PI;
        double v = angle % TwoPi;
        if (v <= -Math.PI) v += TwoPi;
        else if (v > Math.PI) v -= TwoPi;
        return v;
    }

    /// <summary>Cartesian → polar.</summary>
    private static void Polar(double x, double y, out double r, out double theta)
    {
        r = Math.Sqrt(x * x + y * y);
        theta = Math.Atan2(y, x);
    }

    private static void TauOmega(double u, double v, double xi, double eta, double phi,
        out double tau, out double omega)
    {
        double delta = Pify(u - v);
        double a = Math.Sin(u) - Math.Sin(delta);
        double b = Math.Cos(u) - Math.Cos(delta) - 1.0;
        double t1 = Math.Atan2(eta * a - xi * b, xi * a + eta * b);
        double t2 = 2.0 * (Math.Cos(delta) - Math.Cos(v) - Math.Cos(u)) + 3.0;
        tau = (t2 < 0) ? Pify(t1 + Math.PI) : Pify(t1);
        omega = Pify(tau - u + v - phi);
    }

    // =========================================================================
    // Reeds-Shepp formulas (numbered per Reeds & Shepp 1990 §8).
    // Each Lp* function attempts one configuration and returns true if valid,
    // setting (t, u, v) lengths. Reflections / time-flips of these are tried by
    // the family solvers (Csc / Ccc / Cccc / Ccsc / Ccscc).
    // =========================================================================

    // Formula 8.1: L+S+L+
    private static bool LpSpLp(double x, double y, double phi, out double t, out double u, out double v)
    {
        Polar(x - Math.Sin(phi), y - 1.0 + Math.Cos(phi), out u, out t);
        if (t >= -RsZero)
        {
            v = Pify(phi - t);
            if (v >= -RsZero) return true;
        }
        v = 0;
        return false;
    }

    // Formula 8.2: L+S+R+
    private static bool LpSpRp(double x, double y, double phi, out double t, out double u, out double v)
    {
        Polar(x + Math.Sin(phi), y - 1.0 - Math.Cos(phi), out double u1, out double t1);
        u1 *= u1;
        if (u1 >= 4.0)
        {
            u = Math.Sqrt(u1 - 4.0);
            double theta = Math.Atan2(2.0, u);
            t = Pify(t1 + theta);
            v = Pify(t - phi);
            return t >= -RsZero && v >= -RsZero;
        }
        t = u = v = 0;
        return false;
    }

    // CSC family: 8 variants (L|R for first/last C, plus reflections / time-flips)
    private static void Csc(double x, double y, double phi, ref RsPath path)
    {
        double t, u, v;
        double lMin = path.TotalLength;

        if (LpSpLp(x, y, phi, out t, out u, out v))
        {
            double l = Math.Abs(t) + Math.Abs(u) + Math.Abs(v);
            if (lMin > l) { path = new RsPath(PathTypes[14], t, u, v); lMin = l; }
        }
        if (LpSpLp(-x, y, -phi, out t, out u, out v))
        {
            double l = Math.Abs(t) + Math.Abs(u) + Math.Abs(v);
            if (lMin > l) { path = new RsPath(PathTypes[14], -t, -u, -v); lMin = l; }
        }
        if (LpSpLp(x, -y, -phi, out t, out u, out v))
        {
            double l = Math.Abs(t) + Math.Abs(u) + Math.Abs(v);
            if (lMin > l) { path = new RsPath(PathTypes[15], t, u, v); lMin = l; }
        }
        if (LpSpLp(-x, -y, phi, out t, out u, out v))
        {
            double l = Math.Abs(t) + Math.Abs(u) + Math.Abs(v);
            if (lMin > l) { path = new RsPath(PathTypes[15], -t, -u, -v); lMin = l; }
        }
        if (LpSpRp(x, y, phi, out t, out u, out v))
        {
            double l = Math.Abs(t) + Math.Abs(u) + Math.Abs(v);
            if (lMin > l) { path = new RsPath(PathTypes[12], t, u, v); lMin = l; }
        }
        if (LpSpRp(-x, y, -phi, out t, out u, out v))
        {
            double l = Math.Abs(t) + Math.Abs(u) + Math.Abs(v);
            if (lMin > l) { path = new RsPath(PathTypes[12], -t, -u, -v); lMin = l; }
        }
        if (LpSpRp(x, -y, -phi, out t, out u, out v))
        {
            double l = Math.Abs(t) + Math.Abs(u) + Math.Abs(v);
            if (lMin > l) { path = new RsPath(PathTypes[13], t, u, v); lMin = l; }
        }
        if (LpSpRp(-x, -y, phi, out t, out u, out v))
        {
            double l = Math.Abs(t) + Math.Abs(u) + Math.Abs(v);
            if (lMin > l) path = new RsPath(PathTypes[13], -t, -u, -v);
        }
    }

    // Formula 8.3 / 8.4 (paper has typo): L+R-L
    private static bool LpRmL(double x, double y, double phi, out double t, out double u, out double v)
    {
        double xi = x - Math.Sin(phi);
        double eta = y - 1.0 + Math.Cos(phi);
        Polar(xi, eta, out double u1, out double theta);
        if (u1 <= 4.0)
        {
            u = -2.0 * Math.Asin(0.25 * u1);
            t = Pify(theta + 0.5 * u + Math.PI);
            v = Pify(phi - t + u);
            return t >= -RsZero && u <= RsZero;
        }
        t = u = v = 0;
        return false;
    }

    // CCC family: 8 variants
    private static void Ccc(double x, double y, double phi, ref RsPath path)
    {
        double t, u, v;
        double lMin = path.TotalLength;

        if (LpRmL(x, y, phi, out t, out u, out v))
        {
            double l = Math.Abs(t) + Math.Abs(u) + Math.Abs(v);
            if (lMin > l) { path = new RsPath(PathTypes[0], t, u, v); lMin = l; }
        }
        if (LpRmL(-x, y, -phi, out t, out u, out v))
        {
            double l = Math.Abs(t) + Math.Abs(u) + Math.Abs(v);
            if (lMin > l) { path = new RsPath(PathTypes[0], -t, -u, -v); lMin = l; }
        }
        if (LpRmL(x, -y, -phi, out t, out u, out v))
        {
            double l = Math.Abs(t) + Math.Abs(u) + Math.Abs(v);
            if (lMin > l) { path = new RsPath(PathTypes[1], t, u, v); lMin = l; }
        }
        if (LpRmL(-x, -y, phi, out t, out u, out v))
        {
            double l = Math.Abs(t) + Math.Abs(u) + Math.Abs(v);
            if (lMin > l) { path = new RsPath(PathTypes[1], -t, -u, -v); lMin = l; }
        }

        // Backwards variants (swap start/end roles)
        double xb = x * Math.Cos(phi) + y * Math.Sin(phi);
        double yb = x * Math.Sin(phi) - y * Math.Cos(phi);

        if (LpRmL(xb, yb, phi, out t, out u, out v))
        {
            double l = Math.Abs(t) + Math.Abs(u) + Math.Abs(v);
            if (lMin > l) { path = new RsPath(PathTypes[0], v, u, t); lMin = l; }
        }
        if (LpRmL(-xb, yb, -phi, out t, out u, out v))
        {
            double l = Math.Abs(t) + Math.Abs(u) + Math.Abs(v);
            if (lMin > l) { path = new RsPath(PathTypes[0], -v, -u, -t); lMin = l; }
        }
        if (LpRmL(xb, -yb, -phi, out t, out u, out v))
        {
            double l = Math.Abs(t) + Math.Abs(u) + Math.Abs(v);
            if (lMin > l) { path = new RsPath(PathTypes[1], v, u, t); lMin = l; }
        }
        if (LpRmL(-xb, -yb, phi, out t, out u, out v))
        {
            double l = Math.Abs(t) + Math.Abs(u) + Math.Abs(v);
            if (lMin > l) path = new RsPath(PathTypes[1], -v, -u, -t);
        }
    }

    // Formula 8.7: L+R+u L-u R-
    private static bool LpRupLumRm(double x, double y, double phi, out double t, out double u, out double v)
    {
        double xi = x + Math.Sin(phi);
        double eta = y - 1.0 - Math.Cos(phi);
        double rho = 0.25 * (2.0 + Math.Sqrt(xi * xi + eta * eta));
        if (rho <= 1.0)
        {
            u = Math.Acos(rho);
            TauOmega(u, -u, xi, eta, phi, out t, out v);
            return t >= -RsZero && v <= RsZero;
        }
        t = u = v = 0;
        return false;
    }

    // Formula 8.8: L+R-u L-u R+
    private static bool LpRumLumRp(double x, double y, double phi, out double t, out double u, out double v)
    {
        double xi = x + Math.Sin(phi);
        double eta = y - 1.0 - Math.Cos(phi);
        double rho = (20.0 - xi * xi - eta * eta) / 16.0;
        if (rho >= 0 && rho <= 1)
        {
            u = -Math.Acos(rho);
            if (u >= -0.5 * Math.PI)
            {
                TauOmega(u, u, xi, eta, phi, out t, out v);
                return t >= -RsZero && v >= -RsZero;
            }
        }
        t = u = v = 0;
        return false;
    }

    // CCCC family: 8 variants
    private static void Cccc(double x, double y, double phi, ref RsPath path)
    {
        double t, u, v;
        double lMin = path.TotalLength;

        if (LpRupLumRm(x, y, phi, out t, out u, out v))
        {
            double l = Math.Abs(t) + 2.0 * Math.Abs(u) + Math.Abs(v);
            if (lMin > l) { path = new RsPath(PathTypes[2], t, u, -u, v); lMin = l; }
        }
        if (LpRupLumRm(-x, y, -phi, out t, out u, out v))
        {
            double l = Math.Abs(t) + 2.0 * Math.Abs(u) + Math.Abs(v);
            if (lMin > l) { path = new RsPath(PathTypes[2], -t, -u, u, -v); lMin = l; }
        }
        if (LpRupLumRm(x, -y, -phi, out t, out u, out v))
        {
            double l = Math.Abs(t) + 2.0 * Math.Abs(u) + Math.Abs(v);
            if (lMin > l) { path = new RsPath(PathTypes[3], t, u, -u, v); lMin = l; }
        }
        if (LpRupLumRm(-x, -y, phi, out t, out u, out v))
        {
            double l = Math.Abs(t) + 2.0 * Math.Abs(u) + Math.Abs(v);
            if (lMin > l) { path = new RsPath(PathTypes[3], -t, -u, u, -v); lMin = l; }
        }

        if (LpRumLumRp(x, y, phi, out t, out u, out v))
        {
            double l = Math.Abs(t) + 2.0 * Math.Abs(u) + Math.Abs(v);
            if (lMin > l) { path = new RsPath(PathTypes[2], t, u, u, v); lMin = l; }
        }
        if (LpRumLumRp(-x, y, -phi, out t, out u, out v))
        {
            double l = Math.Abs(t) + 2.0 * Math.Abs(u) + Math.Abs(v);
            if (lMin > l) { path = new RsPath(PathTypes[2], -t, -u, -u, -v); lMin = l; }
        }
        if (LpRumLumRp(x, -y, -phi, out t, out u, out v))
        {
            double l = Math.Abs(t) + 2.0 * Math.Abs(u) + Math.Abs(v);
            if (lMin > l) { path = new RsPath(PathTypes[3], t, u, u, v); lMin = l; }
        }
        if (LpRumLumRp(-x, -y, phi, out t, out u, out v))
        {
            double l = Math.Abs(t) + 2.0 * Math.Abs(u) + Math.Abs(v);
            if (lMin > l) path = new RsPath(PathTypes[3], -t, -u, -u, -v);
        }
    }

    // Formula 8.9: L+R-S-L-
    private static bool LpRmSmLm(double x, double y, double phi, out double t, out double u, out double v)
    {
        double xi = x - Math.Sin(phi);
        double eta = y - 1.0 + Math.Cos(phi);
        Polar(xi, eta, out double rho, out double theta);
        if (rho >= 2.0)
        {
            double r = Math.Sqrt(rho * rho - 4.0);
            u = 2.0 - r;
            t = Pify(theta + Math.Atan2(r, -2.0));
            v = Pify(phi - 0.5 * Math.PI - t);
            return t >= -RsZero && u <= RsZero && v <= RsZero;
        }
        t = u = v = 0;
        return false;
    }

    // Formula 8.10: L+R-S-R-
    private static bool LpRmSmRm(double x, double y, double phi, out double t, out double u, out double v)
    {
        double xi = x + Math.Sin(phi);
        double eta = y - 1.0 - Math.Cos(phi);
        Polar(-eta, xi, out double rho, out double theta);
        if (rho >= 2.0)
        {
            t = theta;
            u = 2.0 - rho;
            v = Pify(t + 0.5 * Math.PI - phi);
            return t >= -RsZero && u <= RsZero && v <= RsZero;
        }
        t = u = v = 0;
        return false;
    }

    // CCSC family: 16 variants
    private static void Ccsc(double x, double y, double phi, ref RsPath path)
    {
        double t, u, v;
        double lMin = path.TotalLength - 0.5 * Math.PI;

        if (LpRmSmLm(x, y, phi, out t, out u, out v))
        {
            double l = Math.Abs(t) + Math.Abs(u) + Math.Abs(v);
            if (lMin > l) { path = new RsPath(PathTypes[4], t, -0.5 * Math.PI, u, v); lMin = l; }
        }
        if (LpRmSmLm(-x, y, -phi, out t, out u, out v))
        {
            double l = Math.Abs(t) + Math.Abs(u) + Math.Abs(v);
            if (lMin > l) { path = new RsPath(PathTypes[4], -t, 0.5 * Math.PI, -u, -v); lMin = l; }
        }
        if (LpRmSmLm(x, -y, -phi, out t, out u, out v))
        {
            double l = Math.Abs(t) + Math.Abs(u) + Math.Abs(v);
            if (lMin > l) { path = new RsPath(PathTypes[5], t, -0.5 * Math.PI, u, v); lMin = l; }
        }
        if (LpRmSmLm(-x, -y, phi, out t, out u, out v))
        {
            double l = Math.Abs(t) + Math.Abs(u) + Math.Abs(v);
            if (lMin > l) { path = new RsPath(PathTypes[5], -t, 0.5 * Math.PI, -u, -v); lMin = l; }
        }

        if (LpRmSmRm(x, y, phi, out t, out u, out v))
        {
            double l = Math.Abs(t) + Math.Abs(u) + Math.Abs(v);
            if (lMin > l) { path = new RsPath(PathTypes[8], t, -0.5 * Math.PI, u, v); lMin = l; }
        }
        if (LpRmSmRm(-x, y, -phi, out t, out u, out v))
        {
            double l = Math.Abs(t) + Math.Abs(u) + Math.Abs(v);
            if (lMin > l) { path = new RsPath(PathTypes[8], -t, 0.5 * Math.PI, -u, -v); lMin = l; }
        }
        if (LpRmSmRm(x, -y, -phi, out t, out u, out v))
        {
            double l = Math.Abs(t) + Math.Abs(u) + Math.Abs(v);
            if (lMin > l) { path = new RsPath(PathTypes[9], t, -0.5 * Math.PI, u, v); lMin = l; }
        }
        if (LpRmSmRm(-x, -y, phi, out t, out u, out v))
        {
            double l = Math.Abs(t) + Math.Abs(u) + Math.Abs(v);
            if (lMin > l) { path = new RsPath(PathTypes[9], -t, 0.5 * Math.PI, -u, -v); lMin = l; }
        }

        // Backwards variants (swap start/end roles)
        double xb = x * Math.Cos(phi) + y * Math.Sin(phi);
        double yb = x * Math.Sin(phi) - y * Math.Cos(phi);

        if (LpRmSmLm(xb, yb, phi, out t, out u, out v))
        {
            double l = Math.Abs(t) + Math.Abs(u) + Math.Abs(v);
            if (lMin > l) { path = new RsPath(PathTypes[6], v, u, -0.5 * Math.PI, t); lMin = l; }
        }
        if (LpRmSmLm(-xb, yb, -phi, out t, out u, out v))
        {
            double l = Math.Abs(t) + Math.Abs(u) + Math.Abs(v);
            if (lMin > l) { path = new RsPath(PathTypes[6], -v, -u, 0.5 * Math.PI, -t); lMin = l; }
        }
        if (LpRmSmLm(xb, -yb, -phi, out t, out u, out v))
        {
            double l = Math.Abs(t) + Math.Abs(u) + Math.Abs(v);
            if (lMin > l) { path = new RsPath(PathTypes[7], v, u, -0.5 * Math.PI, t); lMin = l; }
        }
        if (LpRmSmLm(-xb, -yb, phi, out t, out u, out v))
        {
            double l = Math.Abs(t) + Math.Abs(u) + Math.Abs(v);
            if (lMin > l) { path = new RsPath(PathTypes[7], -v, -u, 0.5 * Math.PI, -t); lMin = l; }
        }

        if (LpRmSmRm(xb, yb, phi, out t, out u, out v))
        {
            double l = Math.Abs(t) + Math.Abs(u) + Math.Abs(v);
            if (lMin > l) { path = new RsPath(PathTypes[10], v, u, -0.5 * Math.PI, t); lMin = l; }
        }
        if (LpRmSmRm(-xb, yb, -phi, out t, out u, out v))
        {
            double l = Math.Abs(t) + Math.Abs(u) + Math.Abs(v);
            if (lMin > l) { path = new RsPath(PathTypes[10], -v, -u, 0.5 * Math.PI, -t); lMin = l; }
        }
        if (LpRmSmRm(xb, -yb, -phi, out t, out u, out v))
        {
            double l = Math.Abs(t) + Math.Abs(u) + Math.Abs(v);
            if (lMin > l) { path = new RsPath(PathTypes[11], v, u, -0.5 * Math.PI, t); lMin = l; }
        }
        if (LpRmSmRm(-xb, -yb, phi, out t, out u, out v))
        {
            double l = Math.Abs(t) + Math.Abs(u) + Math.Abs(v);
            if (lMin > l) path = new RsPath(PathTypes[11], -v, -u, 0.5 * Math.PI, -t);
        }
    }

    // Formula 8.11: L+R-S-L+R+ (typo in paper)
    private static bool LpRmSLmRp(double x, double y, double phi, out double t, out double u, out double v)
    {
        double xi = x + Math.Sin(phi);
        double eta = y - 1.0 - Math.Cos(phi);
        Polar(xi, eta, out double rho, out double theta);
        if (rho >= 2.0)
        {
            u = 4.0 - Math.Sqrt(rho * rho - 4.0);
            if (u <= RsZero)
            {
                t = Pify(Math.Atan2((4 - u) * xi - 2 * eta, -2 * xi + (u - 4) * eta));
                v = Pify(t - phi);
                return t >= -RsZero && v >= -RsZero;
            }
        }
        t = u = v = 0;
        return false;
    }

    // CCSCC family: 4 variants
    private static void Ccscc(double x, double y, double phi, ref RsPath path)
    {
        double t, u, v;
        double lMin = path.TotalLength - Math.PI;

        if (LpRmSLmRp(x, y, phi, out t, out u, out v))
        {
            double l = Math.Abs(t) + Math.Abs(u) + Math.Abs(v);
            if (lMin > l) { path = new RsPath(PathTypes[16], t, -0.5 * Math.PI, u, -0.5 * Math.PI, v); lMin = l; }
        }
        if (LpRmSLmRp(-x, y, -phi, out t, out u, out v))
        {
            double l = Math.Abs(t) + Math.Abs(u) + Math.Abs(v);
            if (lMin > l) { path = new RsPath(PathTypes[16], -t, 0.5 * Math.PI, -u, 0.5 * Math.PI, -v); lMin = l; }
        }
        if (LpRmSLmRp(x, -y, -phi, out t, out u, out v))
        {
            double l = Math.Abs(t) + Math.Abs(u) + Math.Abs(v);
            if (lMin > l) { path = new RsPath(PathTypes[17], t, -0.5 * Math.PI, u, -0.5 * Math.PI, v); lMin = l; }
        }
        if (LpRmSLmRp(-x, -y, phi, out t, out u, out v))
        {
            double l = Math.Abs(t) + Math.Abs(u) + Math.Abs(v);
            if (lMin > l) path = new RsPath(PathTypes[17], -t, 0.5 * Math.PI, -u, 0.5 * Math.PI, -v);
        }
    }
}
