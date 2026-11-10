// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Guidance;
using Clipper2Lib;

namespace AgOpenWeb.Services.YouTurn;

/// <summary>
/// The opening between the ends of an open curve whose ends face each other, as a virtual
/// boundary for the U-turn (#694). A boundary curve made the long way round (the field builder's
/// two-tap Bnd. Curve) follows the fence from A to B and leaves out the stretch between them; every
/// pass has that opening too, and A and B are the ends of the line, not points along it. The
/// tractor turns there as at a headland: at B onto the next pass back towards A, at A onto the one
/// after.
/// <para>
/// The opening is the corridor between the line square to the curve's end at A and the one at B,
/// from outside the fence in to halfway across the field (where the passes round the far side
/// begin), or, with A and B on two sides of a corner, in to where those two lines meet. The
/// U-turn is given, in place of the field's:
/// <list type="bullet">
/// <item>a headland line with the whole corridor cut out of the cultivated area, so the passes'
/// ends are where it meets them (the turn trigger, the zones, the landing room);</item>
/// <item>an outer boundary with the corridor cut out but for a strip as wide as the headland at
/// each end, so the turn line (the boundary set in by the distance from boundary) crosses the
/// passes in the opening and the turn is built in that strip, as in a headland. The rest of the
/// field, its inner boundaries (obstacles) and their flags are as they are; the turn stays inside
/// this boundary, so inside the real one.</item>
/// </list>
/// For the turn's own geometry the passes run on into the opening (<see cref="CurveProcessing.ExtendPassForTurn"/>).
/// Nothing else is changed: the line steered and drawn ends at A and B.
/// </para>
/// </summary>
public static class CurveEndOpening
{
    private const double Scale = 1000.0;

    /// <summary>The U-turn's field: the boundary and headland line it turns at.</summary>
    public sealed record TurnField(Boundary Boundary, List<Vec3> HeadlandLine);

    /// <summary>
    /// The field the U-turn sees for <paramref name="reference"/>, or null when the curve's ends
    /// don't face each other (or it is closed, or no usable corridor comes out of it).
    /// </summary>
    /// <param name="reference">The track's points (pass 0).</param>
    /// <param name="boundary">The field's boundary.</param>
    /// <param name="headlandLine">The field's headland line (the cultivated area's edge).</param>
    /// <param name="headlandWidth">The headland's width: the strip at each end of the opening the turn is built in.</param>
    public static TurnField? Build(IReadOnlyList<Vec3> reference, Boundary? boundary, IReadOnlyList<Vec3>? headlandLine,
        double headlandWidth)
    {
        if (reference == null || reference.Count < 3) return null;
        if (boundary?.OuterBoundary == null || !boundary.OuterBoundary.IsValid) return null;
        if (headlandLine == null || headlandLine.Count < 3) return null;
        var (startFaces, endFaces) = CurveProcessing.EndsFacingEachOther(reference);
        if (!startFaces || !endFaces) return null;

        var a = reference[0];
        var b = reference[^1];
        double gE = b.Easting - a.Easting, gN = b.Northing - a.Northing;
        double gap = Math.Sqrt(gE * gE + gN * gN);
        double uE = gE / gap, uN = gN / gap;      // A towards B
        double nE = -uN, nN = uE;                 // square to the opening
        var outer = boundary.OuterBoundary.Points;

        // Which side of the opening the field is on, and how far across it: the corridor runs
        // in to halfway (past that the passes are the ones round the far side).
        double mE = (a.Easting + b.Easting) / 2, mN = (a.Northing + b.Northing) / 2;
        double side;
        double across;
        double inPlus = RayToRing(outer, mE, mN, nE, nN), inMinus = RayToRing(outer, mE, mN, -nE, -nN);
        bool plusInside = boundary.OuterBoundary.IsPointInside(mE + nE * 2, mN + nN * 2);
        bool minusInside = boundary.OuterBoundary.IsPointInside(mE - nE * 2, mN - nN * 2);
        if (plusInside && (!minusInside || inPlus >= inMinus)) { side = 1; across = inPlus; }
        else if (minusInside) { side = -1; across = inMinus; }
        else return null;
        if (!double.IsFinite(across) || across <= 0) return null;

        double depth = across / 2;
        double outside = Math.Max(10.0, gap); // out past the fence
        double strip = Math.Clamp(headlandWidth, 0, gap / 2 - 1.0);
        if (strip < 1.0) return null;

        // Each side of the corridor is square to the curve's end there (where the passes end),
        // set in from the end by `inset` along the way the end points (into the opening).
        int span = Math.Min(5, reference.Count - 1);
        double dAE = a.Easting - reference[span].Easting, dAN = a.Northing - reference[span].Northing;
        double dBE = b.Easting - reference[^(1 + span)].Easting, dBN = b.Northing - reference[^(1 + span)].Northing;
        double lA = Math.Sqrt(dAE * dAE + dAN * dAN), lB = Math.Sqrt(dBE * dBE + dBN * dBN);
        if (lA < 1e-9 || lB < 1e-9) return null;
        dAE /= lA; dAN /= lA; dBE /= lB; dBN /= lB;
        // Square to each end, towards the field.
        double nAE = -dAN, nAN = dAE, nBE = -dBN, nBN = dBE;
        if (nAE * nE * side + nAN * nN * side < 0) { nAE = -nAE; nAN = -nAN; }
        if (nBE * nE * side + nBN * nN * side < 0) { nBE = -nBE; nBN = -nBN; }

        Path64 Corridor(double inset)
        {
            double aE = a.Easting + dAE * inset, aN = a.Northing + dAN * inset;
            double bE = b.Easting + dBE * inset, bN = b.Northing + dBN * inset;
            var outA = P(aE - nAE * outside, aN - nAN * outside);
            var outB = P(bE - nBE * outside, bN - nBN * outside);
            // A and B on two sides of a corner: the lines square to the ends meet in the field, and
            // the passes further in than that have no opening (the curve's far legs cover it). The
            // corridor ends where they meet; run on to `depth` they crossed and made a bowtie.
            double den = nAE * nBN - nAN * nBE;
            if (Math.Abs(den) > 1e-9)
            {
                // a + nA·s = b + nB·r
                double s = ((bE - aE) * nBN - (bN - aN) * nBE) / den;
                double r = ((bE - aE) * nAN - (bN - aN) * nAE) / den;
                if (s > 0 && r > 0 && (s < depth || r < depth))
                    return new Path64 { outA, outB, P(aE + nAE * s, aN + nAN * s) };
            }
            return new Path64
            {
                outA, outB,
                P(bE + nBE * depth, bN + nBN * depth), P(aE + nAE * depth, aN + nAN * depth),
            };
        }

        var fence = ToPath(outer.Select(p => (p.Easting, p.Northing)));
        var headland = ToPath(headlandLine.Select(p => (p.Easting, p.Northing)));
        var turnOuter = Single(Clipper.Difference(new Paths64 { fence }, new Paths64 { Corridor(strip) }, FillRule.NonZero));
        var cultivated = Single(Clipper.Difference(new Paths64 { headland }, new Paths64 { Corridor(0) }, FillRule.NonZero));
        if (turnOuter == null || cultivated == null) return null;

        var turnBoundary = new Boundary
        {
            OuterBoundary = new BoundaryPolygon
            {
                Points = turnOuter.Select(p => new BoundaryPoint(p.X / Scale, p.Y / Scale, 0)).ToList(),
                IsHard = boundary.OuterBoundary.IsHard,
                IsDriveThrough = boundary.OuterBoundary.IsDriveThrough,
            },
            InnerBoundaries = boundary.InnerBoundaries,
            HeadlandPolygon = boundary.HeadlandPolygon,
            IsActive = boundary.IsActive,
        };
        var line = cultivated.Select(p => new Vec3(p.X / Scale, p.Y / Scale, 0)).ToList();
        return new TurnField(turnBoundary, line);
    }

    private static Point64 P(double e, double n) => new((long)Math.Round(e * Scale), (long)Math.Round(n * Scale));

    private static Path64 ToPath(IEnumerable<(double E, double N)> pts)
    {
        var path = new Path64();
        foreach (var (e, n) in pts) path.Add(P(e, n));
        return path;
    }

    // One piece and no hole: the corridor notches the area, it doesn't cut it in two or sit
    // inside it (a curve whose ends face each other away from the fence). The ring keeps the
    // orientation of the input's.
    private static Path64? Single(Paths64 result)
    {
        var rings = result.Where(r => r.Count >= 3 && Math.Abs(Clipper.Area(r)) > 1.0 * Scale * Scale).ToList();
        if (rings.Count != 1) return null;
        return rings[0];
    }

    // Distance from (e, n) along (de, dn) to the ring's nearest crossing ahead; +inf when none.
    private static double RayToRing(IReadOnlyList<BoundaryPoint> ring, double e, double n, double de, double dn)
    {
        double best = double.PositiveInfinity;
        int count = ring.Count;
        for (int i = 0; i < count; i++)
        {
            var p = ring[i];
            var q = ring[(i + 1) % count];
            double sE = q.Easting - p.Easting, sN = q.Northing - p.Northing;
            double den = de * sN - dn * sE;
            if (Math.Abs(den) < 1e-12) continue;
            double t = ((p.Easting - e) * sN - (p.Northing - n) * sE) / den;
            double u = ((p.Easting - e) * dn - (p.Northing - n) * de) / den;
            if (t > 1e-6 && u >= 0 && u <= 1) best = Math.Min(best, t);
        }
        return best;
    }
}
