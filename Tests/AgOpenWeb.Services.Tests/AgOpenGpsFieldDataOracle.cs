// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// AgOpenGPS's field data figures ported as a test oracle: the worked area as CPatches adds it
/// per fix (AgOpenGPS.Core WorkedAreaService.CalculateTriangleStripArea), the actual area and
/// overlap as oglZoom_Paint reads them off the pixels of the whole field drawn once (OpenGL.Designer.cs),
/// the boundary areas (FieldStatisticsService.UpdateBoundaryAreas, FenceLineService's area), the
/// remaining / rate / time strings of CFieldData, and the field list's distance and area columns
/// (FormFieldExisting.cs). AgOpenGPS's names kept; the vector types ours. Not tidied.
/// </summary>
public static class AgOpenGpsFieldDataOracle
{
    // ───────────── CPatches.AddMappingPoint + WorkedAreaService ─────────────

    /// <summary>A section's patch as AgOpenGPS keeps it: triangleList[0] is the colour, then the
    /// left and right points of every fix. AddMappingPoint adds the area of the two new triangles.</summary>
    public sealed class CPatches
    {
        public readonly List<Vec3> triangleList = new();
        public double workedAreaTotal;

        public void TurnMappingOn(Vec2 leftPoint, Vec2 rightPoint)
        {
            triangleList.Clear();
            triangleList.Add(new Vec3(0, 0, 0)); // the colour
            triangleList.Add(new Vec3(leftPoint.Easting, leftPoint.Northing, 0));
            triangleList.Add(new Vec3(rightPoint.Easting, rightPoint.Northing, 0));
        }

        //every time a new fix, a new patch point from last point to this point
        public void AddMappingPoint(Vec2 leftPoint, Vec2 rightPoint)
        {
            triangleList.Add(new Vec3(leftPoint.Easting, leftPoint.Northing, 0));
            triangleList.Add(new Vec3(rightPoint.Easting, rightPoint.Northing, 0));
            int c = triangleList.Count - 1;
            if (c >= 3)
            {
                Vec3[] corePoints = new Vec3[c + 1];
                for (int i = 0; i <= c; i++) corePoints[i] = triangleList[i];
                double area = CalculateTriangleStripArea(corePoints, c - 3);
                workedAreaTotal += area;
            }
        }
    }

    public static double CalculateTriangleStripArea(Vec3[] points, int startIndex)
    {
        if (points == null || points.Length < startIndex + 4)
            return 0;
        int c = startIndex + 3; // Last point index (c-3, c-2, c-1, c form 4 points = 2 triangles)
        // Calculate area of first triangle (points: c, c-1, c-2)
        double area1 = Math.Abs(
            (points[c].Easting * (points[c - 1].Northing - points[c - 2].Northing))
            + (points[c - 1].Easting * (points[c - 2].Northing - points[c].Northing))
            + (points[c - 2].Easting * (points[c].Northing - points[c - 1].Northing)));
        // Calculate area of second triangle (points: c-1, c-2, c-3)
        double area2 = Math.Abs(
            (points[c - 1].Easting * (points[c - 2].Northing - points[c - 3].Northing))
            + (points[c - 2].Easting * (points[c - 3].Northing - points[c - 1].Northing))
            + (points[c - 3].Easting * (points[c - 1].Northing - points[c - 2].Northing)));
        // Return total area (formula includes * 0.5, but we apply it once to the sum)
        return (area1 + area2) * 0.5;
    }

    // ───────────── oglZoom_Paint: actual area and overlap off the pixels ─────────────

    /// <summary>
    /// The whole field's patches drawn once into oglZoom (400 x 400 pixels over maxFieldDistance,
    /// the field's larger extent, about its centre), every fix's pair of points at half rate
    /// (mipmap = 2), each patch a translucent grey, and the green of every pixel read back: one
    /// patch over it reads about 64, two about 96, three or more over 105. Here a pixel's level is
    /// how many patches cover its centre, which is what those thresholds sort.
    /// </summary>
    public static (double actualAreaCovered, double overlapPercent) OglZoomPaint(
        IEnumerable<CPatches> triStrip, double minFieldX, double maxFieldX, double minFieldY, double maxFieldY,
        double workedAreaTotal, int oglZoomWidth = 400)
    {
        // CalculateMinMax
        double dist = Math.Abs(minFieldX - maxFieldX);
        double dist2 = Math.Abs(minFieldY - maxFieldY);
        double maxFieldDistance = dist > dist2 ? dist : dist2;
        double fieldCenterX = (maxFieldX + minFieldX) / 2;
        double fieldCenterY = (maxFieldY + minFieldY) / 2;

        int grnWidth = oglZoomWidth, grnHeight = oglZoomWidth;
        double metresPerPixel = maxFieldDistance / grnWidth;
        var overPix = new int[grnHeight * grnWidth];

        foreach (var patch in triStrip)
        {
            var triList = patch.triangleList;
            int count2 = triList.Count;
            int mipmap = 2;
            var verts = new List<Vec3>();
            if (count2 >= (mipmap))
            {
                int step = mipmap;
                for (int i = 1; i < count2; i += step)
                {
                    verts.Add(triList[i]); i++;
                    verts.Add(triList[i]); i++;
                    //too small to mipmap it
                    if (count2 - i <= (mipmap))
                        break;
                }
            }
            else
            {
                for (int i = 1; i < count2; i++) verts.Add(triList[i]);
            }
            // GL_TRIANGLE_STRIP: every vertex after the second makes a triangle with the two before.
            for (int i = 2; i < verts.Count; i++)
                Rasterize(verts[i - 2], verts[i - 1], verts[i], overPix, grnWidth, grnHeight, metresPerPixel, fieldCenterX, fieldCenterY);
        }

        int once = 0;
        int twice = 0;
        int more = 0;
        double total = 0;
        double total2 = 0;
        for (int i = 0; i < grnHeight * grnWidth; i++)
        {
            int level = overPix[i] switch { 0 => 0, 1 => 64, 2 => 96, _ => 112 };
            if (level > 105)
            {
                more++;
            }
            else if (level > 85)
            {
                twice++;
            }
            else if (level > 50)
            {
                once++;
            }
        }
        total = once + twice + more;
        total2 = total + twice + more + more;
        if (total2 > 0)
        {
            return (total / total2 * workedAreaTotal, Math.Round(((1 - total / total2) * 100), 2));
        }
        return (0, 0);
    }

    /// <summary>One fragment per pixel whose centre the triangle covers, as the GPU rasterises it.</summary>
    private static void Rasterize(Vec3 a, Vec3 b, Vec3 c, int[] pix, int w, int h, double mpp, double cx, double cy)
    {
        double ToPx(double e) => (e - cx) / mpp + w / 2.0;
        double ToPy(double n) => (n - cy) / mpp + h / 2.0;
        double ax = ToPx(a.Easting), ay = ToPy(a.Northing);
        double bx = ToPx(b.Easting), by = ToPy(b.Northing);
        double ccx = ToPx(c.Easting), ccy = ToPy(c.Northing);
        int x0 = Math.Max(0, (int)Math.Floor(Math.Min(ax, Math.Min(bx, ccx))));
        int x1 = Math.Min(w - 1, (int)Math.Ceiling(Math.Max(ax, Math.Max(bx, ccx))));
        int y0 = Math.Max(0, (int)Math.Floor(Math.Min(ay, Math.Min(by, ccy))));
        int y1 = Math.Min(h - 1, (int)Math.Ceiling(Math.Max(ay, Math.Max(by, ccy))));
        double area = (bx - ax) * (ccy - ay) - (ccx - ax) * (by - ay);
        if (Math.Abs(area) < 1e-12) return;
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                double px = x + 0.5, py = y + 0.5;
                double w0 = ((bx - ax) * (py - ay) - (px - ax) * (by - ay)) / area;
                double w1 = ((ccx - bx) * (py - by) - (px - bx) * (ccy - by)) / area;
                double w2 = ((ax - ccx) * (py - ccy) - (px - ccx) * (ay - ccy)) / area;
                // The top-left rule in spirit: a centre on a shared edge belongs to one triangle.
                if (w0 >= 0 && w1 >= 0 && w2 >= 0 && !(w0 == 0 && w1 == 0 && w2 == 0))
                    pix[y * w + x]++;
            }
    }

    // ───────────── FieldStatisticsService.UpdateBoundaryAreas, FenceLineService ─────────────

    public static (double areaOuterBoundary, double areaBoundaryOuterLessInner) UpdateBoundaryAreas(IList<double> boundaryAreas)
    {
        if (boundaryAreas == null || boundaryAreas.Count == 0) return (0, 0);
        double areaOuterBoundary = boundaryAreas[0];
        double areaBoundaryOuterLessInner = boundaryAreas[0];
        for (int i = 1; i < boundaryAreas.Count; i++)
            areaBoundaryOuterLessInner -= boundaryAreas[i];
        return (areaOuterBoundary, areaBoundaryOuterLessInner);
    }

    /// <summary>FenceLineService.CalculateAreaAndFixWinding, the area part.</summary>
    public static double FenceArea(IReadOnlyList<Vec3> fenceLine)
    {
        double area = 0;
        if (fenceLine == null || fenceLine.Count < 3) return 0;
        int ptCount = fenceLine.Count;
        int j = ptCount - 1;
        for (int i = 0; i < ptCount; j = i++)
        {
            area += (fenceLine[j].Easting + fenceLine[i].Easting) * (fenceLine[j].Northing - fenceLine[i].Northing);
        }
        return Math.Abs(area / 2);
    }

    // ───────────── CFieldData strings ─────────────

    private const double m2ha = 0.0001;

    public static string WorkedAreaRemainHectares(double areaBoundaryOuterLessInner, double workedAreaTotal)
        => ((areaBoundaryOuterLessInner - workedAreaTotal) * m2ha).ToString("N2", CultureInfo.InvariantCulture);

    public static string WorkedAreaRemainPercentage(double areaBoundaryOuterLessInner, double workedAreaTotal)
    {
        if (areaBoundaryOuterLessInner > 10)
        {
            double barPercent = ((areaBoundaryOuterLessInner - workedAreaTotal) * 100 / areaBoundaryOuterLessInner);
            return barPercent.ToString("N1", CultureInfo.InvariantCulture) + "%";
        }
        else
        {
            return "0%";
        }
    }

    public static string ActualOverlapPercent(double overlapPercent) => overlapPercent.ToString("N1", CultureInfo.InvariantCulture) + "% ";

    public static string TimeTillFinished(double areaBoundaryOuterLessInner, double workedAreaTotal, double toolWidth, double avgSpeed)
    {
        if (avgSpeed > 2)
        {
            TimeSpan timeSpan = TimeSpan.FromHours(((areaBoundaryOuterLessInner - workedAreaTotal) * m2ha
                / (toolWidth * avgSpeed * 0.1)));
            return timeSpan.Hours.ToString("00:") + timeSpan.Minutes.ToString("00") + '"';
        }
        else return "∞ Hrs";
    }

    public static string WorkRateHectares(double toolWidth, double avgSpeed) => (toolWidth * avgSpeed * 0.1).ToString("N1", CultureInfo.InvariantCulture) + " ha/hr";

    // ───────────── FormFieldExisting: the field list's columns ─────────────

    /// <summary>One field's row: the directory, the distance from the current position to the field's
    /// StartFix in km, and the area of the first ring of Boundary.txt in ha (more than 5 points, else "No Bndry").</summary>
    public static (string name, string distance, string area) FieldListRow(string fieldDirectory, Wgs84 startLatLon, Wgs84 currentLatLon,
        IReadOnlyList<Vec3>? pointList, bool isMetric)
    {
        double distance = startLatLon.DistanceInKiloMeters(currentLatLon);
        string distanceColumn = Math.Round(distance, 2).ToString("N2", CultureInfo.InvariantCulture).PadLeft(10);
        double area = 0;
        if (pointList != null)
        {
            int ptCount = pointList.Count;
            if (ptCount > 5)
            {
                area = 0;         // Accumulates area in the loop
                int j = ptCount - 1;  // The last vertex is the 'previous' one to the first
                for (int i = 0; i < ptCount; j = i++)
                {
                    area += (pointList[j].Easting + pointList[i].Easting) * (pointList[j].Northing - pointList[i].Northing);
                }
                if (isMetric)
                {
                    area = (Math.Abs(area / 2)) * 0.0001;
                }
                else
                {
                    area = (Math.Abs(area / 2)) * 0.00024711;
                }
            }
        }
        string areaColumn = area == 0 ? "No Bndry" : Math.Round(area, 1).ToString("N1", CultureInfo.InvariantCulture).PadLeft(10);
        return (fieldDirectory, distanceColumn, areaColumn);
    }

    /// <summary>btnSort_Click: order 0 by name, 1 by distance, 2 by area (the list sorts its first
    /// column ascending as text; the numbers are padded so that is numeric order).</summary>
    public static List<(string name, string distance, string area)> Sorted(IEnumerable<(string name, string distance, string area)> rows, int order)
    {
        var list = new List<(string name, string distance, string area)>(rows);
        list.Sort((a, b) => string.CompareOrdinal(
            order == 0 ? a.name : order == 1 ? a.distance : a.area,
            order == 0 ? b.name : order == 1 ? b.distance : b.area));
        return list;
    }
}
