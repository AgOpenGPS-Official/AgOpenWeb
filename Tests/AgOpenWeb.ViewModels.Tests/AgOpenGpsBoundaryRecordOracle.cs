// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Base;
using static AgOpenWeb.ViewModels.Tests.AgOpenGpsIsoXmlOracle;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// AgOpenGPS's driven boundary recording ported line for line as a test oracle: the CFence
/// flags, FormBoundaryPlayer's buttons (record/pause, add a point, delete the last, left/right,
/// antenna/tool, the offset, Stop), Position.designer's point trigger and AddBoundaryPoint, and
/// btnStop's CalculateFenceArea + FixFenceLine (AgOpenGPS.Core FenceLineService). AgOpenGPS's
/// names kept; the vector types ours. Not tidied.
/// </summary>
public sealed class AgOpenGpsBoundaryRecordOracle
{
    // CFence
    public bool isDrawRightSide = true, isDrawAtPivot = true, isOkToAddPoints = false;
    public bool isRecBoundaryWhenSectionOn;
    public bool isBndBeingMade;
    public double createBndOffset;
    public readonly List<Vec3> bndBeingMadePts = new();
    public readonly List<CBoundaryList> bndList = new();

    // FormGPS
    private Vec2 prevBoundaryPos;
    /// <summary>manualBtnState == btnStates.On || autoBtnState == btnStates.Auto</summary>
    public bool sectionsOn = true;

    private const double PIBy2 = Math.PI / 2;

    // ───────────── FormBoundaryPlayer ─────────────

    public void FormBoundaryPlayer_Load(double toolWidth)
    {
        createBndOffset = (toolWidth * 0.5);
        isBndBeingMade = true;
    }

    public void nudOffset_Click(double metres) => createBndOffset = metres;

    public void btnPausePlay_Click()
    {
        if (isOkToAddPoints) isOkToAddPoints = false;
        else isOkToAddPoints = true;
    }

    public void btnAddPoint_Click(Vec2 pnFix, Vec3 pivotAxlePos, Vec2 section0Left, Vec2 sectionLastRight)
    {
        isOkToAddPoints = true;
        AddBoundaryPoint(pnFix, pivotAxlePos, section0Left, sectionLastRight);
        isOkToAddPoints = false;
    }

    public void btnDeleteLast_Click()
    {
        int ptCount = bndBeingMadePts.Count;
        if (ptCount > 0)
            bndBeingMadePts.RemoveAt(ptCount - 1);
    }

    public void btnLeftRight_Click() => isDrawRightSide = !isDrawRightSide;

    public void btnAntennaTool_Click() => isDrawAtPivot = !isDrawAtPivot;

    /// <summary>btnStop_Click after "Done?": true when a boundary was made.</summary>
    public bool btnStop_Click()
    {
        bool made = false;
        if (bndBeingMadePts.Count > 2)
        {
            CBoundaryList New = new CBoundaryList();
            for (int i = 0; i < bndBeingMadePts.Count; i++)
            {
                New.fenceLine.Add(bndBeingMadePts[i]);
            }
            New.CalculateFenceArea(bndList.Count);
            FixFenceLine(New, bndList.Count);
            bndList.Add(New);
            made = true;
        }
        isOkToAddPoints = false;
        isBndBeingMade = false;
        bndBeingMadePts.Clear();
        return made;
    }

    // ───────────── Position.designer.cs ─────────────

    /// <summary>UpdateFixPosition: "test if travelled far enough for new boundary point".</summary>
    public void Fix(Vec2 pnFix, Vec3 pivotAxlePos, Vec2 section0Left, Vec2 sectionLastRight)
    {
        if (isOkToAddPoints)
        {
            double boundaryDistance = Distance(pnFix, prevBoundaryPos);
            if (boundaryDistance > 1) AddBoundaryPoint(pnFix, pivotAxlePos, section0Left, sectionLastRight);
        }
    }

    public void AddBoundaryPoint(Vec2 pnFix, Vec3 pivotAxlePos, Vec2 section0Left, Vec2 sectionLastRight)
    {
        prevBoundaryPos = pnFix;
        if (isOkToAddPoints && (!isRecBoundaryWhenSectionOn ||
            (isRecBoundaryWhenSectionOn && sectionsOn)))
        {
            if (isDrawAtPivot)
            {
                if (isDrawRightSide)
                {
                    Vec3 point = new Vec3(
                        pivotAxlePos.Easting + (Math.Sin(pivotAxlePos.Heading - PIBy2) * -createBndOffset),
                        pivotAxlePos.Northing + (Math.Cos(pivotAxlePos.Heading - PIBy2) * -createBndOffset),
                        pivotAxlePos.Heading);
                    bndBeingMadePts.Add(point);
                }
                else
                {
                    Vec3 point = new Vec3(
                        pivotAxlePos.Easting + (Math.Sin(pivotAxlePos.Heading - PIBy2) * createBndOffset),
                        pivotAxlePos.Northing + (Math.Cos(pivotAxlePos.Heading - PIBy2) * createBndOffset),
                        pivotAxlePos.Heading);
                    bndBeingMadePts.Add(point);
                }
            }
            else
            {
                if (isDrawRightSide)
                {
                    Vec3 point = new Vec3(sectionLastRight.Easting, sectionLastRight.Northing, 0);
                    bndBeingMadePts.Add(point);
                }
                else
                {
                    Vec3 point = new Vec3(section0Left.Easting, section0Left.Northing, 0);
                    bndBeingMadePts.Add(point);
                }
            }
        }
    }

    private static double Distance(Vec2 a, Vec2 b)
    {
        double dE = a.Easting - b.Easting, dN = a.Northing - b.Northing;
        return Math.Sqrt(dE * dE + dN * dN);
    }

    // ───────────── CFenceLine.FixFenceLine → FenceLineService.FixSpacing ─────────────

    public static void FixFenceLine(CBoundaryList b, int bndNum)
    {
        b.fenceLine = FixSpacing(b.fenceLine, b.area, bndNum, out _);
    }

    private const double TwoPI = Math.PI * 2;

    public static List<Vec3> FixSpacing(List<Vec3> fenceLine, double area, int boundaryIndex, out List<Vec2> fenceLineEar)
    {
        fenceLineEar = new List<Vec2>();
        if (fenceLine == null || fenceLine.Count < 3)
            return fenceLine!;
        // Determine spacing based on area
        double spacing;
        if (area < 200000) spacing = 1.1;
        else if (area < 400000) spacing = 2.2;
        else spacing = 3.3;
        if (boundaryIndex > 0) spacing *= 0.5;
        List<Vec3> result = new List<Vec3>(fenceLine);
        int bndCount = result.Count;
        double distance;
        // Add points where spacing is too large (first pass: 1.5x spacing)
        for (int i = 0; i < bndCount; i++)
        {
            int j = i + 1;
            if (j == bndCount) j = 0;
            distance = Distance(result[i], result[j]);
            if (distance > spacing * 1.5)
            {
                Vec3 pointB = new Vec3(
                    (result[i].Easting + result[j].Easting) / 2.0,
                    (result[i].Northing + result[j].Northing) / 2.0,
                    result[i].Heading);
                result.Insert(j, pointB);
                bndCount = result.Count;
                i--;
            }
        }
        // Add points where spacing is too large (second pass: 1.6x spacing)
        bndCount = result.Count;
        for (int i = 0; i < bndCount; i++)
        {
            int j = i + 1;
            if (j == bndCount) j = 0;
            distance = Distance(result[i], result[j]);
            if (distance > spacing * 1.6)
            {
                Vec3 pointB = new Vec3(
                    (result[i].Easting + result[j].Easting) / 2.0,
                    (result[i].Northing + result[j].Northing) / 2.0,
                    result[i].Heading);
                result.Insert(j, pointB);
                bndCount = result.Count;
                i--;
            }
        }
        // Remove points where spacing is too small
        spacing *= 0.9;
        bndCount = result.Count;
        for (int i = 0; i < bndCount - 1; i++)
        {
            distance = Distance(result[i], result[i + 1]);
            if (distance < spacing)
            {
                result.RemoveAt(i + 1);
                bndCount = result.Count;
                i--;
            }
        }
        // Recalculate headings
        result = CalculateHeadings(result);
        // Create simplified line for ear clipping triangulation
        double delta = 0;
        for (int i = 0; i < result.Count; i++)
        {
            if (i == 0)
            {
                fenceLineEar.Add(new Vec2(result[i].Easting, result[i].Northing));
                continue;
            }
            delta += (result[i - 1].Heading - result[i].Heading);
            if (Math.Abs(delta) > 0.005)
            {
                fenceLineEar.Add(new Vec2(result[i].Easting, result[i].Northing));
                delta = 0;
            }
        }
        return result;
    }

    private static double Distance(Vec3 a, Vec3 b)
    {
        double dEast = a.Easting - b.Easting;
        double dNorth = a.Northing - b.Northing;
        return Math.Sqrt(dEast * dEast + dNorth * dNorth);
    }

    public static List<Vec3> CalculateHeadings(List<Vec3> fenceLine)
    {
        if (fenceLine == null || fenceLine.Count < 3)
            return fenceLine!;
        int cnt = fenceLine.Count;
        Vec3[] arr = new Vec3[cnt];
        for (int i = 0; i < cnt; i++)
        {
            arr[i] = fenceLine[i];
        }
        List<Vec3> result = new List<Vec3>(cnt);
        // First point uses last, first, second points
        Vec3 pt3 = arr[0];
        pt3.Heading = Math.Atan2(arr[1].Easting - arr[cnt - 1].Easting, arr[1].Northing - arr[cnt - 1].Northing);
        if (pt3.Heading < 0) pt3.Heading += TwoPI;
        result.Add(pt3);
        // Middle points
        for (int i = 1; i < cnt - 1; i++)
        {
            pt3 = arr[i];
            pt3.Heading = Math.Atan2(arr[i + 1].Easting - arr[i - 1].Easting, arr[i + 1].Northing - arr[i - 1].Northing);
            if (pt3.Heading < 0) pt3.Heading += TwoPI;
            result.Add(pt3);
        }
        // Last point uses last, first, second-to-last points
        pt3 = arr[cnt - 1];
        pt3.Heading = Math.Atan2(arr[0].Easting - arr[cnt - 2].Easting, arr[0].Northing - arr[cnt - 2].Northing);
        if (pt3.Heading < 0) pt3.Heading += TwoPI;
        result.Add(pt3);
        return result;
    }
}
