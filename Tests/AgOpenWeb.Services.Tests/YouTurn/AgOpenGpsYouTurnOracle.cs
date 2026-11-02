// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Base;
using AgOpenWeb.Services.PathPlanning;

namespace AgOpenWeb.Services.Tests.YouTurn;

/// <summary>
/// AgOpenGPS's automatic U-turn on an AB line, as an oracle: CYouTurn's
/// <c>BuildABLineDubinsYouTurn</c> and what it calls (<c>CreateABOmegaTurn</c>, <c>CreateABWideTurn</c>,
/// <c>KStyleTurnAB</c>, <c>MoveTurnInsideTurnLine</c>, <c>MoveABTurnInsideTurnLine</c>,
/// <c>FindABTurnPoint</c>, <c>FindClosestTurnPoint</c>, <c>FindABOutTurnPoint</c>,
/// <c>FindInnerTurnPoints</c>, <c>AddABSequenceLines</c>, <c>SmoothYouTurn</c>), the turn line from
/// <c>CTurn.BuildTurnLines</c> (the fence moved in by the distance from boundary, spaced by
/// <c>TurnLineService.FixSpacing</c>), <c>TurnAreaService.IsPointInsideTurnArea</c> and
/// <c>FenceLineService.CalculateHeadings</c>, ported line for line from the snapshot (2026-01-24),
/// AgOpenGPS's names kept. <see cref="Build"/> runs the phases as the position cycle does, one
/// call per fix until phase 10, and smooths a style-0 turn with 6 points as the cycle does.
/// The Dubins path is <see cref="DubinsPathService"/>, AgOpenGPS.Core's own (ours adds the goal
/// point at the end, #332). One boundary, no inner ones.
/// </summary>
public sealed class AgOpenGpsYouTurnOracle
{
    private const double PIBy2 = Math.PI / 2, twoPI = Math.PI * 2;
    private const double abLength = 2000; // CABLine

    // settings
    public double youTurnRadius = 8.1;                // set_youTurnRadius
    public double uturnDistanceFromBoundary = 2;      // set_youTurnDistanceFromBoundary
    public int uTurnStyle = 0;                        // set_uTurnStyle: 0 omega/wide, 1 K
    public double toolWidth, toolOverlap, toolOffset; // mf.tool
    public int rowSkipsWidth = 1;
    public int makeUTurnCounter = 4;                  // 1.5 s have passed

    // the boundary (bndList[0])
    public List<Vec3> fenceLine = new();
    public List<Vec3> turnLine = new();

    // the line being driven (mf.ABLine) and the track (mf.trk.gArr[idx])
    private Vec3 currentLinePtA, currentLinePtB;
    private double abHeading;
    private bool isHeadingSameWay;
    private double rEastAB, rNorthAB;
    private int howManyPathsAway;
    private Vec3 trackPtA, trackPtB;
    private double trackHeading, nudgeDistance;

    // CYouTurn state
    private Vec3 pivotAxlePos;
    private bool isTurnLeft;
    private int youTurnPhase;
    private double pointSpacing;
    private bool isOutOfBounds, isOutSameCurve, isGoingStraightThrough, isTurnCreationTooClose, turnTooCloseTrigger;
    private int semiCircleIndex;
    private double distancePivotToTurnLine;
    private double rEastYT, rNorthYT;
    private double iE, iN;
    private List<Vec3> ytList = new(), ytList2 = new(), nextCurve = new();
    private CClose closestTurnPt = new(), inClosestTurnPt = new(), outClosestTurnPt = new(), startOfTurnPt = new();
    private readonly List<CClose> turnClosestList = new();

    public bool Failed { get; private set; }

    /// <summary>What the build did, step by step (for a failed build).</summary>
    public System.Text.StringBuilder Trace { get; } = new();

    private sealed class CClose
    {
        public Vec3 closePt;
        public double turnLineHeading;
        public int turnLineNum, turnLineIndex, curveIndex;
        public CClose() { }
        public CClose(CClose c) { closePt = c.closePt; turnLineHeading = c.turnLineHeading; turnLineNum = c.turnLineNum; turnLineIndex = c.turnLineIndex; curveIndex = c.curveIndex; }
    }

    /// <summary>The field: its fence as recorded (a point every metre or two), headings per FenceLineService.</summary>
    public AgOpenGpsYouTurnOracle(IReadOnlyList<Vec2> fence)
    {
        fenceLine = CalculateFenceHeadings(fence.Select(p => new Vec3(p.Easting, p.Northing, 0)).ToList());
    }

    /// <summary>
    /// Plan the turn for a tractor at <paramref name="pivot"/> on pass <paramref name="pathsAway"/> of
    /// the AB line <paramref name="trackA"/>→<paramref name="trackB"/>, driving the same way as the line.
    /// The current line is the pass, extended as AgOpenGPS extends it. Empty when AgOpenGPS could not make one.
    /// </summary>
    public List<Vec3> Build(Vec3 pivot, Vec3 trackA, Vec3 trackB, int pathsAway, bool turnLeft, double nudge = 0)
    {
        BuildTurnLines();

        trackPtA = trackA; trackPtB = trackB; nudgeDistance = nudge;
        trackHeading = Math.Atan2(trackB.Easting - trackA.Easting, trackB.Northing - trackA.Northing);
        if (trackHeading < 0) trackHeading += twoPI;
        abHeading = trackHeading;
        isHeadingSameWay = true;
        howManyPathsAway = pathsAway;
        double widthMinusOverlap = toolWidth - toolOverlap;
        double distAway = widthMinusOverlap * howManyPathsAway + (isHeadingSameWay ? -toolOffset : toolOffset) + nudgeDistance;
        distAway += 0.5 * widthMinusOverlap;
        var line = BuildNewOffsetList(distAway);
        currentLinePtA = line[0]; currentLinePtB = line[1];

        pivotAxlePos = pivot;
        // the pivot's projection on the line (ABLine.rEastAB / rNorthAB)
        double dx = currentLinePtB.Easting - currentLinePtA.Easting, dy = currentLinePtB.Northing - currentLinePtA.Northing;
        double U = ((pivot.Easting - currentLinePtA.Easting) * dx + (pivot.Northing - currentLinePtA.Northing) * dy) / (dx * dx + dy * dy);
        rEastAB = currentLinePtA.Easting + U * dx;
        rNorthAB = currentLinePtA.Northing + U * dy;

        isTurnLeft = turnLeft;
        youTurnPhase = 0;
        isOutOfBounds = isOutSameCurve = isGoingStraightThrough = isTurnCreationTooClose = turnTooCloseTrigger = false;
        Failed = false;
        ytList.Clear(); ytList2.Clear();

        Trace.Clear();
        Trace.Append($"turnLine {turnLine.Count} pts; line A ({currentLinePtA.Easting:F1},{currentLinePtA.Northing:F1}) B ({currentLinePtB.Easting:F1},{currentLinePtB.Northing:F1}); r ({rEastAB:F1},{rNorthAB:F1}); ");
        // Position.designer.cs: one build call per fix until the turn is made
        for (int fix = 0; fix < 10 && youTurnPhase != 10; fix++)
        {
            bool ok = BuildABLineDubinsYouTurn();
            Trace.Append($"fix {fix}: ok {ok} phase {youTurnPhase} ytList {ytList.Count} closest idx {closestTurnPt.turnLineIndex} at ({closestTurnPt.closePt.Easting:F1},{closestTurnPt.closePt.Northing:F1}); ");
            if (!ok || Failed) return new List<Vec3>();
        }
        if (youTurnPhase != 10) return new List<Vec3>();
        if (uTurnStyle == 0) SmoothYouTurn(6);
        return new List<Vec3>(ytList);
    }

    #region CABCurve.BuildNewOffsetList (TrackMode.AB)

    private List<Vec3> BuildNewOffsetList(double distAway)
    {
        var newCurList = new List<Vec3>();
        Vec2 nudgePtA = new(trackPtA.Easting, trackPtA.Northing);
        Vec2 nudgePtB = new(trackPtB.Easting, trackPtB.Northing);

        Vec2 point1 = new((Math.Cos(-trackHeading) * distAway) + nudgePtA.Easting, (Math.Sin(-trackHeading) * distAway) + nudgePtA.Northing);
        Vec2 point2 = new((Math.Cos(-trackHeading) * distAway) + nudgePtB.Easting, (Math.Sin(-trackHeading) * distAway) + nudgePtB.Northing);

        double easting1 = point1.Easting - (Math.Sin(trackHeading) * abLength);
        double northing1 = point1.Northing - (Math.Cos(trackHeading) * abLength);
        newCurList.Add(new Vec3(easting1, northing1, trackHeading));

        double easting2 = point2.Easting + (Math.Sin(trackHeading) * abLength);
        double northing2 = point2.Northing + (Math.Cos(trackHeading) * abLength);
        newCurList.Add(new Vec3(easting2, northing2, trackHeading));
        return newCurList;
    }

    #endregion

    #region FenceLineService, CTurn.BuildTurnLines, TurnLineService, TurnAreaService

    private static List<Vec3> CalculateFenceHeadings(List<Vec3> fenceLine)
    {
        int cnt = fenceLine.Count;
        Vec3[] arr = fenceLine.ToArray();
        var result = new List<Vec3>(cnt);
        Vec3 pt3 = arr[0];
        pt3.Heading = Math.Atan2(arr[1].Easting - arr[cnt - 1].Easting, arr[1].Northing - arr[cnt - 1].Northing);
        if (pt3.Heading < 0) pt3.Heading += twoPI;
        result.Add(pt3);
        for (int i = 1; i < cnt - 1; i++)
        {
            pt3 = arr[i];
            pt3.Heading = Math.Atan2(arr[i + 1].Easting - arr[i - 1].Easting, arr[i + 1].Northing - arr[i - 1].Northing);
            if (pt3.Heading < 0) pt3.Heading += twoPI;
            result.Add(pt3);
        }
        pt3 = arr[cnt - 1];
        pt3.Heading = Math.Atan2(arr[0].Easting - arr[cnt - 2].Easting, arr[0].Northing - arr[cnt - 2].Northing);
        if (pt3.Heading < 0) pt3.Heading += twoPI;
        result.Add(pt3);
        return result;
    }

    private void BuildTurnLines()
    {
        Vec3 point = new();
        double totalHeadWidth = uturnDistanceFromBoundary;

        turnLine.Clear();
        int ptCount = fenceLine.Count;
        for (int i = ptCount - 1; i >= 0; i--)
        {
            point.Easting = fenceLine[i].Easting + (-Math.Sin(PIBy2 + fenceLine[i].Heading) * totalHeadWidth);
            point.Northing = fenceLine[i].Northing + (-Math.Cos(PIBy2 + fenceLine[i].Heading) * totalHeadWidth);
            point.Heading = fenceLine[i].Heading;
            if (point.Heading < -twoPI) point.Heading += twoPI;

            // only add if outside actual field boundary (j == 0: inside the fence)
            if (GeometryMath.IsPointInPolygon(fenceLine, point))
                turnLine.Add(new Vec3(point.Easting, point.Northing, point.Heading));
        }
        turnLine = FixSpacing(turnLine, fenceLine, totalHeadWidth, 2);

        int cnt = turnLine.Count;
        Vec3[] arr = turnLine.ToArray();
        double delta = 0;
        turnLine.Clear();
        for (int i = 0; i < arr.Length; i++)
        {
            if (i == 0) { turnLine.Add(arr[i]); continue; }
            delta += (arr[i - 1].Heading - arr[i].Heading);
            if (Math.Abs(delta) > 0.005)
            {
                turnLine.Add(arr[i]);
                delta = 0;
            }
        }
    }

    private static List<Vec3> FixSpacing(List<Vec3> turnLine, List<Vec3> fenceLine, double totalHeadWidth, double spacing)
    {
        var result = new List<Vec3>(turnLine);
        double totalHeadWidthSq = totalHeadWidth * totalHeadWidth;
        double spacingSq = spacing * spacing;
        int lineCount = result.Count;
        for (int i = 0; i < fenceLine.Count; i++)
        {
            for (int j = 0; j < lineCount; j++)
            {
                double distance = DistanceSquared(fenceLine[i], result[j]);
                if (distance < (totalHeadWidthSq * 0.99))
                {
                    result.RemoveAt(j);
                    lineCount = result.Count;
                    j = -1;
                }
            }
        }
        int bndCount = result.Count;
        for (int i = 0; i < bndCount; i++)
        {
            int j = i + 1;
            if (j == bndCount) j = 0;
            double distance = DistanceSquared(result[i], result[j]);
            if (distance > (spacingSq * 1.8))
            {
                Vec3 pointB = new((result[i].Easting + result[j].Easting) / 2.0, (result[i].Northing + result[j].Northing) / 2.0, result[i].Heading);
                result.Insert(j, pointB);
                bndCount = result.Count;
                i--;
            }
        }
        bndCount = result.Count;
        for (int i = 0; i < bndCount - 1; i++)
        {
            double distance = DistanceSquared(result[i], result[i + 1]);
            if (distance < spacingSq)
            {
                result.RemoveAt(i + 1);
                bndCount = result.Count;
                i--;
            }
        }
        if (result.Count > 0) result = CalculateTurnLineHeadings(result);
        return result;
    }

    private static List<Vec3> CalculateTurnLineHeadings(List<Vec3> turnLine)
    {
        if (turnLine.Count < 2) return turnLine;
        int cnt = turnLine.Count;
        Vec3[] arr = turnLine.ToArray();
        var result = new List<Vec3>();
        Vec3 pt2 = arr[0];
        pt2.Heading = Math.Atan2(arr[1].Easting - arr[0].Easting, arr[1].Northing - arr[0].Northing);
        if (pt2.Heading < 0) pt2.Heading += twoPI;
        result.Add(pt2);
        Vec3 pt3 = arr[0];
        pt3.Heading = Math.Atan2(arr[1].Easting - arr[cnt - 1].Easting, arr[1].Northing - arr[cnt - 1].Northing);
        if (pt3.Heading < 0) pt3.Heading += twoPI;
        result.Add(pt3);
        for (int i = 1; i < cnt - 1; i++)
        {
            pt3 = arr[i];
            pt3.Heading = Math.Atan2(arr[i + 1].Easting - arr[i - 1].Easting, arr[i + 1].Northing - arr[i - 1].Northing);
            if (pt3.Heading < 0) pt3.Heading += twoPI;
            result.Add(pt3);
        }
        pt2 = arr[cnt - 1];
        pt2.Heading = Math.Atan2(arr[cnt - 1].Easting - arr[cnt - 2].Easting, arr[cnt - 1].Northing - arr[cnt - 2].Northing);
        if (pt2.Heading < 0) pt2.Heading += twoPI;
        result.Add(pt2);
        return result;
    }

    /// <summary>TurnAreaService: 0 inside the turn line, -1 outside (no inner boundaries here).</summary>
    public int IsPointInsideTurnArea(Vec3 pt) => GeometryMath.IsPointInPolygon(turnLine, pt) ? 0 : -1;

    #endregion

    #region CYouTurn

    private bool BuildABLineDubinsYouTurn()
    {
        double turnOffset = (toolWidth - toolOverlap) * rowSkipsWidth + (isTurnLeft ? -toolOffset * 2.0 : toolOffset * 2.0);
        pointSpacing = youTurnRadius * 0.1;

        if (uTurnStyle == 0)
        {
            if (turnOffset > (youTurnRadius * 2.0)) return CreateABWideTurn();
            else return CreateABOmegaTurn();
        }
        else if (uTurnStyle == 1)
        {
            return KStyleTurnAB();
        }
        return false;
    }

    private bool CreateABOmegaTurn()
    {
        if (makeUTurnCounter < 4) { youTurnPhase = 0; return true; }

        double head = abHeading;
        if (!isHeadingSameWay) head += Math.PI;
        if (head >= twoPI) head -= twoPI;

        switch (youTurnPhase)
        {
            case 0:
                FindABTurnPoint(new Vec3(rEastAB, rNorthAB, head));
                if (closestTurnPt.turnLineIndex != -1)
                    distancePivotToTurnLine = Distance(pivotAxlePos, closestTurnPt.closePt);
                else { FailCreate(); return false; }
                inClosestTurnPt = new CClose(closestTurnPt);

                var dubYouTurnPath = new DubinsPathService(youTurnRadius);
                double turnOffset = (toolWidth - toolOverlap) * rowSkipsWidth + (isTurnLeft ? -toolOffset * 2.0 : toolOffset * 2.0);

                Vec3 start = inClosestTurnPt.closePt;
                start.Heading = head;
                Vec3 goal = start;

                double invertedHead = head - Math.PI;
                if (invertedHead < 0) invertedHead += twoPI;
                if (invertedHead > twoPI) invertedHead -= twoPI;

                if (isTurnLeft)
                {
                    goal.Easting = goal.Easting + (Math.Cos(-invertedHead) * turnOffset);
                    goal.Northing = goal.Northing + (Math.Sin(-invertedHead) * turnOffset);
                }
                else
                {
                    goal.Easting = goal.Easting - (Math.Cos(-invertedHead) * turnOffset);
                    goal.Northing = goal.Northing - (Math.Sin(-invertedHead) * turnOffset);
                }
                goal.Heading = invertedHead;

                ytList = dubYouTurnPath.GeneratePath(start, goal);
                isOutOfBounds = true;
                if (ytList.Count == 0) { FailCreate(); return false; }
                youTurnPhase = 1;

                int cnt = ytList.Count;
                for (int i = 1; i < cnt - 2; i++)
                {
                    double distance = DistanceSquared(ytList[i], ytList[i + 1]);
                    if (distance < pointSpacing)
                    {
                        ytList.RemoveAt(i + 1);
                        i--;
                        cnt = ytList.Count;
                    }
                }
                return true;

            case 1:
                ytList = MoveTurnInsideTurnLine(ytList, head, false, false);
                if (ytList.Count == 0) { FailCreate(); return false; }
                isOutOfBounds = false;
                youTurnPhase = 10;
                turnTooCloseTrigger = false;
                isTurnCreationTooClose = false;
                if (!AddABSequenceLines()) return false;
                return true;
        }
        return true;
    }

    private bool CreateABWideTurn()
    {
        if (makeUTurnCounter < 4) { youTurnPhase = 0; return true; }

        double head = abHeading;
        if (!isHeadingSameWay) head += Math.PI;
        if (head >= twoPI) head -= twoPI;

        switch (youTurnPhase)
        {
            case 0:
                Vec3 onPurePoint = new(rEastAB, rNorthAB, 0);
                FindABTurnPoint(onPurePoint);
                inClosestTurnPt = new CClose(closestTurnPt);
                startOfTurnPt = new CClose(closestTurnPt);
                if (inClosestTurnPt.turnLineIndex == -1) { FailCreate(); return false; }

                ytList.Clear();
                Vec3 currentPos = new(inClosestTurnPt.closePt.Easting, inClosestTurnPt.closePt.Northing, head);
                ytList.Add(currentPos);
                while (Math.Abs(head - currentPos.Heading) < Math.PI)
                {
                    currentPos.Easting += pointSpacing * Math.Sin(currentPos.Heading);
                    currentPos.Northing += pointSpacing * Math.Cos(currentPos.Heading);
                    double turnParameter = isTurnLeft ? -1.0 : 1.0;
                    currentPos.Heading += (pointSpacing / youTurnRadius) * turnParameter;
                    ytList.Add(currentPos);
                }

                isOutOfBounds = true;
                ytList = MoveTurnInsideTurnLine(ytList, head, true, false);
                if (ytList.Count == 0) { FailCreate(); return false; }
                distancePivotToTurnLine = Distance(ytList[0], pivotAxlePos);
                youTurnPhase = 1;
                return true;

            case 1:
                double widthMinusOverlap = toolWidth - toolOverlap;
                double distAway = widthMinusOverlap * (howManyPathsAway + ((isTurnLeft ^ isHeadingSameWay) ? rowSkipsWidth : -rowSkipsWidth)) + (isHeadingSameWay ? toolOffset : -toolOffset) + nudgeDistance;
                distAway += (0.5 * widthMinusOverlap);
                nextCurve = BuildNewOffsetList(distAway);

                bool isTurnLineSameWay = true;
                double headingDifference = Math.Abs(startOfTurnPt.closePt.Heading - ytList[ytList.Count - 1].Heading);
                if (headingDifference > PIBy2 && headingDifference < 3 * PIBy2) isTurnLineSameWay = false;

                if (!FindABOutTurnPoint(ref nextCurve, inClosestTurnPt, isTurnLineSameWay)) { FailCreate(); return false; }
                outClosestTurnPt = new CClose(closestTurnPt);

                Vec3 pointPos = new(outClosestTurnPt.closePt.Easting, outClosestTurnPt.closePt.Northing, 0);
                double headie;
                if (!isOutSameCurve) headie = head;
                else
                {
                    headie = head + Math.PI;
                    if (headie >= twoPI) headie -= twoPI;
                }
                pointPos.Heading = headie;

                ytList2.Clear();
                ytList2.Add(pointPos);
                while (Math.Abs(headie - pointPos.Heading) < Math.PI)
                {
                    pointPos.Easting += pointSpacing * Math.Sin(pointPos.Heading);
                    pointPos.Northing += pointSpacing * Math.Cos(pointPos.Heading);
                    double turnParameter = isTurnLeft ? 1.0 : -1.0;
                    pointPos.Heading += (pointSpacing / youTurnRadius) * turnParameter;
                    ytList2.Add(pointPos);
                }

                isOutOfBounds = true;
                ytList2 = MoveTurnInsideTurnLine(ytList2, headie, true, true);
                if (ytList2.Count == 0) { FailCreate(); return false; }
                youTurnPhase = 2;
                return true;

            case 2:
                int cnt1 = ytList.Count;
                int cnt2 = ytList2.Count;

                bool isFirstTurnLineSameWay = true;
                double firstHeadingDifference = Math.Abs(inClosestTurnPt.turnLineHeading - ytList[ytList.Count - 1].Heading);
                if (firstHeadingDifference > PIBy2 && firstHeadingDifference < 3 * PIBy2) isFirstTurnLineSameWay = false;

                FindInnerTurnPoints(ytList[cnt1 - 1], ytList[0].Heading, inClosestTurnPt, isFirstTurnLineSameWay);
                CClose startClosestTurnPt = new(closestTurnPt);

                FindInnerTurnPoints(ytList2[cnt2 - 1], ytList2[0].Heading + Math.PI, outClosestTurnPt, !isFirstTurnLineSameWay);
                CClose goalClosestTurnPt = new(closestTurnPt);

                if (startClosestTurnPt.turnLineNum != goalClosestTurnPt.turnLineNum) { FailCreate(); return false; }

                if (startClosestTurnPt.turnLineIndex == goalClosestTurnPt.turnLineIndex)
                {
                    for (int a = 0; a < cnt2; cnt2--) ytList.Add(ytList2[cnt2 - 1]);
                }
                else
                {
                    Vec3 tPoint;
                    int turnCount = turnLine.Count;
                    int loops = Math.Abs(startClosestTurnPt.turnLineIndex - goalClosestTurnPt.turnLineIndex);
                    if (loops > (turnLine.Count / 2))
                    {
                        if (startClosestTurnPt.turnLineIndex < goalClosestTurnPt.turnLineIndex)
                            loops = (turnCount - goalClosestTurnPt.turnLineIndex) + startClosestTurnPt.turnLineIndex;
                        else
                            loops = (turnCount - startClosestTurnPt.turnLineIndex) + goalClosestTurnPt.turnLineIndex;
                    }

                    if (isFirstTurnLineSameWay)
                    {
                        for (int i = 0; i < loops; i++)
                        {
                            if ((startClosestTurnPt.turnLineIndex + 1) >= turnCount) startClosestTurnPt.turnLineIndex = -1;
                            tPoint = turnLine[startClosestTurnPt.turnLineIndex + 1];
                            startClosestTurnPt.turnLineIndex++;
                            if (startClosestTurnPt.turnLineIndex >= turnCount) startClosestTurnPt.turnLineIndex = 0;
                            ytList.Add(tPoint);
                        }
                    }
                    else
                    {
                        for (int i = 0; i < loops; i++)
                        {
                            tPoint = turnLine[startClosestTurnPt.turnLineIndex];
                            startClosestTurnPt.turnLineIndex--;
                            if (startClosestTurnPt.turnLineIndex == -1) startClosestTurnPt.turnLineIndex = turnCount - 1;
                            ytList.Add(tPoint);
                        }
                    }
                    for (int a = 0; a < cnt2; cnt2--) ytList.Add(ytList2[cnt2 - 1]);
                }

                int cnt = ytList.Count;
                for (int i = 1; i < cnt - 2; i++)
                {
                    int j = i + 1;
                    if (j == cnt - 1) continue;
                    double distance = DistanceSquared(ytList[i], ytList[j]);
                    if (distance > 1)
                    {
                        Vec3 pointB = new((ytList[i].Easting + ytList[j].Easting) / 2.0, (ytList[i].Northing + ytList[j].Northing) / 2.0, ytList[i].Heading);
                        ytList.Insert(j, pointB);
                        cnt = ytList.Count;
                        i--;
                    }
                }

                cnt = ytList.Count;
                Vec3[] arr = new Vec3[cnt];
                cnt -= 2;
                ytList.CopyTo(arr);
                ytList.Clear();
                for (int i = 2; i < cnt; i++)
                {
                    Vec3 pt3 = arr[i];
                    pt3.Heading = Math.Atan2(arr[i + 1].Easting - arr[i - 1].Easting, arr[i + 1].Northing - arr[i - 1].Northing);
                    if (pt3.Heading < 0) pt3.Heading += twoPI;
                    ytList.Add(pt3);
                }

                if (Distance(ytList[0], pivotAxlePos) < 3) { FailCreate(); return false; }

                isGoingStraightThrough = Math.PI - Math.Abs(Math.Abs(ytList[ytList.Count - 2].Heading - ytList[1].Heading) - Math.PI) < PIBy2;

                isOutOfBounds = false;
                youTurnPhase = 10;
                turnTooCloseTrigger = false;
                isTurnCreationTooClose = false;
                ytList2.Clear();
                if (!AddABSequenceLines()) return false;
                return true;
        }
        return false;
    }

    private bool KStyleTurnAB()
    {
        double pointSpacing = youTurnRadius * 0.1;

        int turnIndex = IsPointInsideTurnArea(pivotAxlePos);
        if (makeUTurnCounter < 4 || turnIndex != 0) { youTurnPhase = 0; return true; }
        makeUTurnCounter = 0;

        if (youTurnPhase == 0)
        {
            isOutOfBounds = true;
            Vec3 onPurePoint = new(rEastAB, rNorthAB, 0);
            FindClosestTurnPoint(onPurePoint);
            inClosestTurnPt = new CClose(closestTurnPt);
            if (inClosestTurnPt.turnLineIndex == -1) return false;
            distancePivotToTurnLine = Distance(pivotAxlePos, closestTurnPt.closePt);

            rEastYT = rEastAB;
            rNorthYT = rNorthAB;
            double head = abHeading;
            if (!isHeadingSameWay) head += Math.PI;
            if (head >= twoPI) head -= twoPI;

            double turnDiagDistance = distancePivotToTurnLine;
            rEastYT += (Math.Sin(head) * turnDiagDistance);
            rNorthYT += (Math.Cos(head) * turnDiagDistance);

            ytList.Clear();
            Vec3 currentPos = new(rEastYT, rNorthYT, head);
            ytList.Add(currentPos);
            while (Math.Abs(head - currentPos.Heading) < 2.2)
            {
                currentPos.Easting += pointSpacing * Math.Sin(currentPos.Heading);
                currentPos.Northing += pointSpacing * Math.Cos(currentPos.Heading);
                double turnParameter = 1.0;
                if (isTurnLeft) turnParameter = -1.0;
                currentPos.Heading += (pointSpacing / youTurnRadius) * turnParameter;
                ytList.Add(currentPos);
            }

            ytList = MoveABTurnInsideTurnLine(ytList, head);
            if (ytList.Count < 5 || semiCircleIndex == -1) { FailCreate(); return false; }

            double turnOffset = (toolWidth - toolOverlap) * rowSkipsWidth + (isTurnLeft ? toolOffset : -toolOffset);

            int count = ytList.Count;
            head = ytList[count - 1].Heading;
            Vec3 pt = new();
            for (int i = 1; i <= (int)(3 * turnOffset); i++)
            {
                pt.Easting = ytList[count - 1].Easting + (Math.Sin(head) * i * 0.5);
                pt.Northing = ytList[count - 1].Northing + (Math.Cos(head) * i * 0.5);
                pt.Heading = 0;
                ytList.Add(pt);
            }

            head = abHeading;
            if (isHeadingSameWay) head += Math.PI;
            if (head >= twoPI) head -= twoPI;
            for (int a = 0; a < 8; a++)
            {
                pt.Easting = ytList[0].Easting + (Math.Sin(head) * 0.511);
                pt.Northing = ytList[0].Northing + (Math.Cos(head) * 0.511);
                pt.Heading = ytList[0].Heading;
                ytList.Insert(0, pt);
            }

            Vec3[] arr = new Vec3[ytList.Count];
            ytList.CopyTo(arr);
            ytList.Clear();
            for (int i = 0; i < arr.Length - 1; i++)
            {
                arr[i].Heading = Math.Atan2(arr[i + 1].Easting - arr[i].Easting, arr[i + 1].Northing - arr[i].Northing);
                if (arr[i].Heading < 0) arr[i].Heading += twoPI;
                ytList.Add(arr[i]);
            }

            distancePivotToTurnLine = Distance(ytList[0], pivotAxlePos);
            isOutOfBounds = false;
            youTurnPhase = 10;
            turnTooCloseTrigger = false;
            isTurnCreationTooClose = false;
            return true;
        }
        return true;
    }

    private List<Vec3> MoveABTurnInsideTurnLine(List<Vec3> uTurnList, double head)
    {
        double cosHead = Math.Cos(head);
        double sinHead = Math.Sin(head);
        int cnt = uTurnList.Count;
        Vec3[] arr2 = new Vec3[cnt];
        uTurnList.CopyTo(arr2);
        uTurnList.Clear();

        semiCircleIndex = -1;
        bool pointOutOfBnd = true;
        int j = 0;
        int stopIfWayOut = 0;
        while (pointOutOfBnd)
        {
            stopIfWayOut++;
            pointOutOfBnd = false;
            distancePivotToTurnLine = DistanceSquared(arr2[0], pivotAxlePos);

            for (int i = 0; i < cnt; i++)
            {
                arr2[i].Easting -= (sinHead);
                arr2[i].Northing -= (cosHead);
            }

            for (; j < cnt; j += 1)
            {
                if (IsPointInsideTurnArea(arr2[j]) != 0)
                {
                    pointOutOfBnd = true;
                    if (j > 0) j--;
                    break;
                }
            }

            if (stopIfWayOut == 1000 || (distancePivotToTurnLine < 6))
                return uTurnList;
        }

        while (!pointOutOfBnd)
        {
            for (int i = 0; i < cnt; i++)
            {
                arr2[i].Easting += (sinHead * 0.1);
                arr2[i].Northing += (cosHead * 0.1);
            }
            for (int a = 0; a < cnt; a++)
            {
                if (IsPointInsideTurnArea(arr2[a]) != 0)
                {
                    semiCircleIndex = a;
                    uTurnList.AddRange(arr2);
                    return uTurnList;
                }
            }
        }
        return uTurnList;
    }

    private List<Vec3> MoveTurnInsideTurnLine(List<Vec3> uTurnList, double head, bool deleteSecondHalf, bool invertHeading)
    {
        double cosHead = Math.Cos(head);
        double sinHead = Math.Sin(head);
        int cnt = uTurnList.Count;
        Vec3[] arr2 = new Vec3[cnt];
        uTurnList.CopyTo(arr2);
        uTurnList.Clear();

        semiCircleIndex = -1;
        bool pointOutOfBnd = isOutOfBounds;
        int j = 0;
        int stopIfWayOut = 0;
        while (pointOutOfBnd)
        {
            stopIfWayOut++;
            pointOutOfBnd = false;
            distancePivotToTurnLine = Distance(arr2[0], pivotAxlePos);

            for (int i = 0; i < cnt; i++)
            {
                arr2[i].Easting -= (sinHead);
                arr2[i].Northing -= (cosHead);
            }

            for (; j < cnt; j += 1)
            {
                if (IsPointInsideTurnArea(arr2[j]) != 0)
                {
                    pointOutOfBnd = true;
                    if (j > 0) j--;
                    break;
                }
            }

            if (stopIfWayOut == 1000 || (distancePivotToTurnLine < 3))
                return uTurnList;
        }

        while (!pointOutOfBnd)
        {
            for (int i = 0; i < cnt; i++)
            {
                arr2[i].Easting += (sinHead * 0.1);
                arr2[i].Northing += (cosHead * 0.1);
            }
            for (int a = 0; a < cnt; a++)
            {
                if (IsPointInsideTurnArea(arr2[a]) != 0)
                {
                    semiCircleIndex = a;
                    pointOutOfBnd = true;
                    break;
                }
            }
        }

        for (int i = 0; i < cnt; i++)
        {
            if (i == semiCircleIndex && deleteSecondHalf) break;
            if (invertHeading) arr2[i].Heading += Math.PI;
            if (arr2[i].Heading >= twoPI) arr2[i].Heading -= twoPI;
            else if (arr2[i].Heading < 0) arr2[i].Heading += twoPI;
            uTurnList.Add(arr2[i]);
        }
        isOutOfBounds = false;
        return uTurnList;
    }

    private void SmoothYouTurn(int smPts)
    {
        int cnt = ytList.Count;
        Vec3[] arr = new Vec3[cnt];
        for (int s = 0; s < smPts / 2; s++)
        {
            arr[s].Easting = ytList[s].Easting; arr[s].Northing = ytList[s].Northing; arr[s].Heading = ytList[s].Heading;
        }
        for (int s = cnt - (smPts / 2); s < cnt; s++)
        {
            arr[s].Easting = ytList[s].Easting; arr[s].Northing = ytList[s].Northing; arr[s].Heading = ytList[s].Heading;
        }
        for (int i = smPts / 2; i < cnt - (smPts / 2); i++)
        {
            for (int j = -smPts / 2; j < smPts / 2; j++)
            {
                arr[i].Easting += ytList[j + i].Easting;
                arr[i].Northing += ytList[j + i].Northing;
            }
            arr[i].Easting /= smPts;
            arr[i].Northing /= smPts;
            arr[i].Heading = ytList[i].Heading;
        }
        ytList.Clear();
        for (int i = 1; i < cnt - 1; i++)
        {
            arr[i].Heading = Math.Atan2(arr[i + 1].Easting - arr[i].Easting, arr[i + 1].Northing - arr[i].Northing);
            if (arr[i].Heading < 0) arr[i].Heading += twoPI;
            ytList.Add(arr[i]);
        }
    }

    private void FindABTurnPoint(Vec3 fromPt)
    {
        double eP = fromPt.Easting, nP = fromPt.Northing;
        double eAB, nAB;
        turnClosestList.Clear();
        if (isHeadingSameWay) { eAB = currentLinePtB.Easting; nAB = currentLinePtB.Northing; }
        else { eAB = currentLinePtA.Easting; nAB = currentLinePtA.Northing; }

        for (int i = 0; i < turnLine.Count - 1; i++)
        {
            int res = GetLineIntersection(turnLine[i].Easting, turnLine[i].Northing, turnLine[i + 1].Easting, turnLine[i + 1].Northing,
                eP, nP, eAB, nAB, ref iE, ref iN);
            if (res == 1)
            {
                var cClose = new CClose();
                cClose.closePt.Easting = iE;
                cClose.closePt.Northing = iN;
                double hed = Math.Atan2(turnLine[i + 1].Easting - turnLine[i].Easting, turnLine[i + 1].Northing - turnLine[i].Northing);
                if (hed < 0) hed += twoPI;
                cClose.closePt.Heading = hed;
                cClose.turnLineHeading = turnLine[i].Heading;
                cClose.turnLineNum = 0;
                cClose.turnLineIndex = i;
                turnClosestList.Add(new CClose(cClose));
            }
        }

        double minDistance = double.MaxValue;
        closestTurnPt = new CClose { turnLineIndex = -1 };
        for (int i = 0; i < turnClosestList.Count; i++)
        {
            double dist = ((fromPt.Easting - turnClosestList[i].closePt.Easting) * (fromPt.Easting - turnClosestList[i].closePt.Easting))
                + ((fromPt.Northing - turnClosestList[i].closePt.Northing) * (fromPt.Northing - turnClosestList[i].closePt.Northing));
            if (minDistance >= dist)
            {
                minDistance = dist;
                closestTurnPt = new CClose(turnClosestList[i]);
            }
        }
    }

    private void FindClosestTurnPoint(Vec3 fromPt)
    {
        double eP = fromPt.Easting, nP = fromPt.Northing;
        double eAB, nAB;
        turnClosestList.Clear();
        if (isHeadingSameWay) { eAB = currentLinePtB.Easting; nAB = currentLinePtB.Northing; }
        else { eAB = currentLinePtA.Easting; nAB = currentLinePtA.Northing; }

        for (int i = 0; i < turnLine.Count - 1; i++)
        {
            int res = GetLineIntersection(turnLine[i].Easting, turnLine[i].Northing, turnLine[i + 1].Easting, turnLine[i + 1].Northing,
                eP, nP, eAB, nAB, ref iE, ref iN);
            if (res == 1)
            {
                var cClose = new CClose();
                cClose.closePt.Easting = iE;
                cClose.closePt.Northing = iN;
                double hed = Math.Atan2(turnLine[i + 1].Easting - turnLine[i].Easting, turnLine[i + 1].Northing - turnLine[i].Northing);
                if (hed < 0) hed += twoPI;
                cClose.closePt.Heading = hed;
                cClose.turnLineNum = 0;
                cClose.turnLineIndex = i;
                turnClosestList.Add(new CClose(cClose));
            }
        }

        double minDistance = double.MaxValue;
        closestTurnPt = new CClose { turnLineIndex = -1 };
        for (int i = 0; i < turnClosestList.Count; i++)
        {
            double dist = ((fromPt.Easting - turnClosestList[i].closePt.Easting) * (fromPt.Easting - turnClosestList[i].closePt.Easting))
                + ((fromPt.Northing - turnClosestList[i].closePt.Northing) * (fromPt.Northing - turnClosestList[i].closePt.Northing));
            if (minDistance >= dist)
            {
                minDistance = dist;
                closestTurnPt = new CClose(turnClosestList[i]);
            }
        }
    }

    private bool FindABOutTurnPoint(ref List<Vec3> nextCurve, CClose inPt, bool isTurnLineSameWay)
    {
        int a = isTurnLineSameWay ? 1 : -1;
        int turnLineIndex = inPt.turnLineIndex;
        int stopTurnLineIndex = inPt.turnLineIndex - a;
        if (stopTurnLineIndex < 0) stopTurnLineIndex = turnLine.Count - 3;
        if (stopTurnLineIndex > turnLine.Count - 1) turnLineIndex = 3;

        for (; turnLineIndex != stopTurnLineIndex; turnLineIndex += a)
        {
            if (turnLineIndex < 0) turnLineIndex = turnLine.Count - 2;
            if (turnLineIndex > turnLine.Count - 2) turnLineIndex = 0;

            if (nextCurve.Count > 1)
            {
                int res = GetLineIntersection(turnLine[turnLineIndex].Easting, turnLine[turnLineIndex].Northing,
                    turnLine[turnLineIndex + 1].Easting, turnLine[turnLineIndex + 1].Northing,
                    nextCurve[0].Easting, nextCurve[0].Northing, nextCurve[1].Easting, nextCurve[1].Northing, ref iE, ref iN);
                if (res == 1)
                {
                    closestTurnPt = new CClose();
                    closestTurnPt.closePt.Easting = iE;
                    closestTurnPt.closePt.Northing = iN;
                    closestTurnPt.closePt.Heading = abHeading;
                    closestTurnPt.turnLineIndex = turnLineIndex;
                    closestTurnPt.curveIndex = -1;
                    closestTurnPt.turnLineHeading = turnLine[turnLineIndex].Heading;
                    closestTurnPt.turnLineNum = 0;
                    return true;
                }
            }

            int res2 = GetLineIntersection(turnLine[turnLineIndex].Easting, turnLine[turnLineIndex].Northing,
                turnLine[turnLineIndex + 1].Easting, turnLine[turnLineIndex + 1].Northing,
                currentLinePtA.Easting, currentLinePtA.Northing, currentLinePtB.Easting, currentLinePtB.Northing, ref iE, ref iN);
            if (res2 == 1)
            {
                double hed;
                if (isHeadingSameWay) hed = Math.Atan2(rEastAB - iE, rNorthAB - iN);
                else hed = Math.Atan2(iE - rEastAB, iN - rNorthAB);
                if (hed < 0) hed += twoPI;
                hed = Math.Round(hed, 3);
                double hedAB = Math.Round(abHeading, 3);

                if (hed == hedAB) return false;
                else if (turnLineIndex == inPt.turnLineIndex) { }
                else
                {
                    closestTurnPt = new CClose();
                    closestTurnPt.closePt.Easting = iE;
                    closestTurnPt.closePt.Northing = iN;
                    closestTurnPt.closePt.Heading = abHeading;
                    closestTurnPt.turnLineIndex = turnLineIndex;
                    closestTurnPt.curveIndex = -1;
                    closestTurnPt.turnLineHeading = turnLine[turnLineIndex].Heading;
                    closestTurnPt.turnLineNum = 0;
                    isOutSameCurve = true;
                    return true;
                }
            }
        }
        return false;
    }

    private bool FindInnerTurnPoints(Vec3 fromPt, double inDirection, CClose refClosePt, bool isTurnLineSameWay)
    {
        double eP = fromPt.Easting + Math.Sin(inDirection);
        double nP = fromPt.Northing + Math.Cos(inDirection);
        int a = isTurnLineSameWay ? 1 : -1;
        int turnLineIndex = refClosePt.turnLineIndex;
        int stopTurnLineIndex = refClosePt.turnLineIndex - a;
        if (stopTurnLineIndex < 0) stopTurnLineIndex = turnLine.Count - 3;
        if (stopTurnLineIndex > turnLine.Count - 1) turnLineIndex = 3;

        for (; turnLineIndex != stopTurnLineIndex; turnLineIndex += a)
        {
            if (turnLineIndex < 0) turnLineIndex = turnLine.Count - 2;
            if (turnLineIndex > turnLine.Count - 2) turnLineIndex = 0;

            int res = GetLineIntersection(turnLine[turnLineIndex].Easting, turnLine[turnLineIndex].Northing,
                turnLine[turnLineIndex + 1].Easting, turnLine[turnLineIndex + 1].Northing,
                fromPt.Easting, fromPt.Northing, eP, nP, ref iE, ref iN);
            if (res == 1)
            {
                closestTurnPt = new CClose();
                closestTurnPt.closePt.Easting = iE;
                closestTurnPt.closePt.Northing = iN;
                closestTurnPt.closePt.Heading = -1;
                closestTurnPt.turnLineIndex = turnLineIndex;
                closestTurnPt.curveIndex = -1;
                closestTurnPt.turnLineHeading = turnLine[turnLineIndex].Heading;
                closestTurnPt.turnLineNum = 0;
                return true;
            }
        }
        return false;
    }

    private bool AddABSequenceLines()
    {
        double inhead = abHeading;
        if (!isHeadingSameWay) inhead += Math.PI;
        if (inhead > twoPI) inhead -= twoPI;

        double outhead = inhead;
        if (isOutSameCurve) outhead += Math.PI;
        if (outhead > twoPI) outhead -= twoPI;

        double lenny = 15;
        Vec3 pt = new();
        for (int a = 0; a < lenny; a++)
        {
            pt.Easting = ytList[0].Easting - (Math.Sin(inhead) * pointSpacing);
            pt.Northing = ytList[0].Northing - (Math.Cos(inhead) * pointSpacing);
            pt.Heading = inhead;
            ytList.Insert(0, pt);
        }

        int count = ytList.Count;
        for (int i = 0; i <= lenny; i++)
        {
            pt.Easting = ytList[count - 1].Easting - (Math.Sin(outhead) * i * pointSpacing);
            pt.Northing = ytList[count - 1].Northing - (Math.Cos(outhead) * i * pointSpacing);
            pt.Heading = outhead;
            ytList.Add(pt);
        }

        count = ytList.Count;
        for (int i = 0; i < count; i += 2)
        {
            double distancePivotToTurnLine = DistanceSquared(ytList[i], pivotAxlePos);
            if (distancePivotToTurnLine > 3) isTurnCreationTooClose = false;
            else { FailCreate(); return false; }
        }
        return true;
    }

    private static int GetLineIntersection(double p0x, double p0y, double p1x, double p1y,
        double p2x, double p2y, double p3x, double p3y, ref double iEast, ref double iNorth)
    {
        double s1x = p1x - p0x, s1y = p1y - p0y;
        double s2x = p3x - p2x, s2y = p3y - p2y;
        double s = (-s1y * (p0x - p2x) + s1x * (p0y - p2y)) / (-s2x * s1y + s1x * s2y);
        if (s >= 0 && s <= 1)
        {
            double t = (s2x * (p0y - p2y) - s2y * (p0x - p2x)) / (-s2x * s1y + s1x * s2y);
            if (t >= 0 && t <= 1)
            {
                iEast = p0x + (t * s1x);
                iNorth = p0y + (t * s1y);
                return 1;
            }
        }
        return 0;
    }

    private void FailCreate([System.Runtime.CompilerServices.CallerMemberName] string from = "", [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    {
        Trace.Append($"FailCreate from {from} line {line}; ");
        isTurnCreationTooClose = true;
        youTurnPhase = 0;
        ytList.Clear();
        Failed = true;
    }

    private static double Distance(Vec3 a, Vec3 b) => Math.Sqrt(DistanceSquared(a, b));
    private static double DistanceSquared(Vec3 a, Vec3 b) => (a.Easting - b.Easting) * (a.Easting - b.Easting) + (a.Northing - b.Northing) * (a.Northing - b.Northing);

    #endregion
}
