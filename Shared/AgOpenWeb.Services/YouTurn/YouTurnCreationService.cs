// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Guidance;
using AgOpenWeb.Models.YouTurn;
using AgOpenWeb.Services.Geometry;
using AgOpenWeb.Services.PathPlanning;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AgOpenWeb.Services.YouTurn
{
    /// <summary>
    /// Service for creating U-turn paths.
    /// Generates turn geometry based on guidance lines, boundaries, and vehicle configuration.
    /// </summary>
    public partial class YouTurnCreationService
    {
        private const double TWO_PI = Math.PI * 2.0;
        private const double PI_BY_2 = Math.PI / 2.0;

        // Reusable service for Dubins path generation (radius updated per turn)
        private readonly DubinsPathService _dubinsService = new DubinsPathService(1.0);
        private readonly ILogger<YouTurnCreationService> _logger;
        private readonly IPolygonOffsetService _polygonOffsetService;
        private readonly AgOpenWeb.Models.Configuration.ConfigurationStore _configStore;

        public YouTurnCreationService(
            ILogger<YouTurnCreationService> logger,
            IPolygonOffsetService polygonOffsetService,
            AgOpenWeb.Models.Configuration.ConfigurationStore configStore)
        {
            _logger = logger;
            _polygonOffsetService = polygonOffsetService;
            _configStore = configStore;
        }

        // Current input being processed (for helper methods)
        private YouTurnCreationInput _currentInput;

        // Working variables during turn creation
        private List<Vec3> ytList = new List<Vec3>();
        private List<Vec3> ytList2 = new List<Vec3>();
        private List<Vec3> nextCurve = new List<Vec3>();
        private TurnClosePoint closestTurnPt = new TurnClosePoint();
        private TurnClosePoint inClosestTurnPt = new TurnClosePoint();
        private TurnClosePoint outClosestTurnPt = new TurnClosePoint();
        private TurnClosePoint startOfTurnPt = new TurnClosePoint();
        private List<TurnClosePoint> turnClosestList = new List<TurnClosePoint>();

        private double pointSpacing;
        private double iE, iN; // Line intersection results
        private int semiCircleIndex = -1;
        private bool isOutOfBounds = false;
        private bool isOutSameCurve = false;
        private int youTurnPhase = 0;

        /// <summary>
        /// Create a U-turn path.
        /// </summary>
        public YouTurnCreationOutput CreateTurn(YouTurnCreationInput input)
        {
            var output = new YouTurnCreationOutput();

            // Store input for helper methods
            _currentInput = input;

            // Validate input
            if (input.MakeUTurnCounter < 4)
            {
                output.Success = false;
                output.FailureReason = "Turn creation throttled - wait 1.5 seconds between turns";
                youTurnPhase = 0;
                return output;
            }

            // Initialize working state
            ytList.Clear();
            ytList2.Clear();
            nextCurve.Clear();
            turnClosestList.Clear();
            youTurnPhase = 0;
            isOutOfBounds = false;
            isOutSameCurve = false;

            // Lateral span of the arc = the pass spacing. NO ToolOffset compensation:
            // AgOpenGPS shifts the arc by ±2*ToolOffset because its steering applies the
            // implement offset to the pivot target, so the arc must pre-compensate. AgOpenWeb
            // steering never applies ToolOffset (it only positions sections in
            // SectionControlService) — the guidance line IS the pivot path — so adding the
            // shift here lands the arc end (and exit leg) 2*ToolOffset off the cyan/post-turn
            // line the pivot actually follows, causing a lateral jump + wiggle on turn exit.
            double turnOffset;
            if (input.TurnOffset > 0)
            {
                // Use the pre-calculated offset (matches cyan next-track line exactly)
                turnOffset = input.TurnOffset;
                _logger.LogDebug("Using pre-calculated TurnOffset: {TurnOffset}m", turnOffset);
            }
            else
            {
                // Fallback: calculate from RowSkipsWidth (skip=0 means 1 width, skip=1 means 2 widths, etc.)
                turnOffset = (input.ToolWidth - input.ToolOverlap) * (input.RowSkipsWidth + 1);
                _logger.LogDebug("Fallback turnOffset={TurnOffset}m from RowSkipsWidth={RowSkipsWidth}", turnOffset, input.RowSkipsWidth);
            }
            pointSpacing = input.TurnRadius * 0.1;

            // ONE turn generator for every track. AB lines arrive here as densified 2-point
            // curves (see BuildCreationInput), so there is no separate AB turn path.
            bool success = CreateCurveTurn(input, turnOffset);

            // Package output
            if (success && ytList.Count > 0)
            {
                output.Success = true;
                output.TurnPath = new List<Vec3>(ytList);
                output.IsOutSameCurve = isOutSameCurve;
                output.IsGoingStraightThrough = IsGoingStraightThrough();
                output.IsOutOfBounds = isOutOfBounds;
                output.DistancePivotToTurnLine = Distance(ytList[0], input.PivotPosition);
                output.InClosestTurnPoint = inClosestTurnPt.ClosePt;
                output.OutClosestTurnPoint = outClosestTurnPt.ClosePt;
            }
            else
            {
                output.Success = false;
                output.FailureReason = "Turn creation failed - path invalid or out of bounds";
                output.IsOutOfBounds = true;
            }

            return output;
        }

        #region Curve Turn Creation

        private bool CreateCurveTurn(YouTurnCreationInput input, double turnOffset)
        {
            if (input.TurnType == YouTurnType.SagittaStyle)
            {
                return CreateCurveSagittaTurn(input, turnOffset);
            }
            else if (input.TurnType == YouTurnType.AlbinStyle)
            {
                // Always use the omega turn: it uses Dubins paths which handle any
                // turnOffset, and it completes in a single call. The multi-phase wide
                // turn can't finish under the single-call CreateTurn contract (same
                // reason CreateABTurn always uses the omega path).
                return CreateCurveOmegaTurn(input, turnOffset);
            }
            else // KStyle
            {
                return CreateKStyleTurnCurve(input, turnOffset);
            }
        }

        private bool CreateCurveOmegaTurn(YouTurnCreationInput input, double turnOffset)
        {
            // Keep from making turns constantly - wait 1.5 seconds
            if (input.MakeUTurnCounter < 4)
            {
                youTurnPhase = 0;
                return true;
            }

            // Check for valid track mode
            if (input.TrackMode == 64 || input.TrackMode == 32) // waterPivot or bndCurve
            {
                youTurnPhase = 11; // Ignore
                return false;
            }

            switch (youTurnPhase)
            {
                case 0: // Find the crossing points
                    if (!FindCurveTurnPoint(input, false))
                    {
                        FailCreate();
                        return false;
                    }

                    // Save a copy
                    inClosestTurnPt = new TurnClosePoint(closestTurnPt);
                    ytList?.Clear();

                    int count = input.IsHeadingSameWay ? -1 : 1;
                    int curveIndex = inClosestTurnPt.CurveIndex;

                    isOutOfBounds = true;
                    int stopIfWayOut = 0;
                    double head = 0;

                    while (isOutOfBounds)
                    {
                        stopIfWayOut++;
                        isOutOfBounds = false;

                        // Creates half a circle starting at the crossing point
                        ytList.Clear();
                        curveIndex += count;

                        Vec3 currentPos = input.GuidancePoints[curveIndex];

                        if (!input.IsHeadingSameWay) currentPos.Heading += Math.PI;
                        if (currentPos.Heading >= TWO_PI) currentPos.Heading -= TWO_PI;
                        head = currentPos.Heading;

                        _dubinsService.TurningRadius = input.TurnRadius;

                        // Now we go the other way to turn round
                        double invertHead = currentPos.Heading - Math.PI;
                        if (invertHead <= -Math.PI) invertHead += TWO_PI;
                        if (invertHead >= Math.PI) invertHead -= TWO_PI;

                        Vec3 goal = new Vec3();

                        // Neat trick to not have to add pi/2
                        if (input.IsTurnLeft)
                        {
                            goal.Easting = input.GuidancePoints[curveIndex - count].Easting + (Math.Cos(-invertHead) * turnOffset);
                            goal.Northing = input.GuidancePoints[curveIndex - count].Northing + (Math.Sin(-invertHead) * turnOffset);
                        }
                        else
                        {
                            goal.Easting = input.GuidancePoints[curveIndex - count].Easting - (Math.Cos(-invertHead) * turnOffset);
                            goal.Northing = input.GuidancePoints[curveIndex - count].Northing - (Math.Sin(-invertHead) * turnOffset);
                        }

                        goal.Heading = invertHead;

                        // Generate the turn points
                        ytList = _dubinsService.GeneratePath(currentPos, goal);
                        if (ytList.Count == 0)
                        {
                            FailCreate();
                            return false;
                        }

                        if (stopIfWayOut == 300 || curveIndex < 1 || curveIndex > (input.GuidancePoints.Count - 2))
                        {
                            // For some reason it doesn't go inside boundary
                            FailCreate();
                            return false;
                        }

                        for (int i = 0; i < ytList.Count; i++)
                        {
                            if (input.IsPointInsideTurnArea(ytList[i]) != 0)
                            {
                                isOutOfBounds = true;
                                break;
                            }
                        }
                    }
                    inClosestTurnPt.CurveIndex = curveIndex;

                    // Too many points from Dubins - so cut
                    double distance;
                    int cnt = ytList.Count;
                    for (int i = 1; i < cnt - 2; i++)
                    {
                        distance = DistanceSquared(ytList[i], ytList[i + 1]);
                        if (distance < pointSpacing)
                        {
                            ytList.RemoveAt(i + 1);
                            i--;
                            cnt = ytList.Count;
                        }
                    }

                    // Move the turn to exact at the turnline
                    ytList = MoveTurnInsideTurnLine(input, ytList, head, false, false);
                    if (ytList.Count == 0)
                    {
                        FailCreate();
                        return false;
                    }

                    // CreateTurn is single-call (youTurnPhase resets to 0 each call), so the
                    // arc-plus-legs must finish in one pass — add the curve-following entry/exit
                    // legs inline here rather than relying on a second call into a (dead) case 1.
                    return CompleteCurveTurn(input);
            }
            return true;
        }

        /// <summary>
        /// Finishes a curve turn whose arc is already snapped to the turn line: builds the
        /// destination offset curve, splices on the curve-following entry/exit legs, fills
        /// gaps and recomputes smoothed headings. Shared by the omega and sagitta curve
        /// turns so both produce extension legs that follow the actual curved passes
        /// (instead of overrunning the boundary with no legs).
        /// </summary>
        private bool CompleteCurveTurn(YouTurnCreationInput input)
        {
            // Where the track crosses the turn boundary — the "too late to turn?" reference.
            // Captured BEFORE the re-anchor below overwrites inClosestTurnPt.ClosePt. The
            // vehicle being within a few metres of THIS point means it has reached the
            // headland and can't start the turn. (Measuring against the arc start or the
            // entry-leg start instead spuriously rejects valid turns: in tight geometry the
            // big arc must start deep in the field near the still-approaching vehicle, and
            // the entry leg extends a UTurnExtension lead-in back toward it.)
            Vec3 turnLineCrossing = inClosestTurnPt.ClosePt;

            Vec3 arcStartPoint = ytList.Count > 0 ? ytList[0] : input.PivotPosition;

            // Re-anchor the entry leg to where the arc was actually seated. The omega/sagitta
            // arc is generated from a curve index walked INWARD until it fits the turn area,
            // then MoveTurnInsideTurnLine shifts it back out to the turn line — so the stored
            // inClosestTurnPt.CurveIndex now lags the real arc start, which would leave a
            // corner-cut gap between the track and the turn. Snap it to the curve point
            // nearest the seated arc start so the entry leg meets the arc cleanly.
            if (ytList.Count > 0 && input.GuidancePoints.Count > 1)
            {
                double bestSq = double.MaxValue;
                int bestIdx = inClosestTurnPt.CurveIndex;
                for (int i = 0; i < input.GuidancePoints.Count; i++)
                {
                    double dSq = DistanceSquared(input.GuidancePoints[i], arcStartPoint);
                    if (dSq < bestSq) { bestSq = dSq; bestIdx = i; }
                }
                inClosestTurnPt.CurveIndex = bestIdx;
            }

            // Build the destination curve (the cyan next-track line) the exit leg follows.
            // Offset the BASE reference by the SAME formula every other consumer uses for a
            // pass — current pass, displayed line, STEERING line
            // (GpsPipelineService.CalculateTrackGuidance) and the pass driven after the turn:
            //   widthMinusOverlap * pathsAway + nudge.
            // One definition, no per-turn measurement, so the exit leg is byte-aligned with
            // the cyan line and the line the tractor actually steers after completion.
            //
            // (Previously this measured SignedPerpDistanceToCurve(base, arcEnd) to be
            // "heading-agnostic". But that projects the arc end onto the NEAREST raw base
            // point's stored heading; when the recorded curve ends before the headland the arc
            // seats on the tangent extension and a noisy end-point heading skewed the measured
            // offset by metres, plotting the exit leg off the next pass the tractor drives.)
            double widthMinusOverlap = input.ToolWidth - input.ToolOverlap;
            int passDelta = (input.IsTurnLeft ^ input.IsHeadingSameWay)
                ? (input.RowSkipsWidth + 1)
                : -(input.RowSkipsWidth + 1);
            double distAway = widthMinusOverlap * (input.HowManyPathsAway + passDelta) + input.NudgeDistance;

            // Create the next line
            nextCurve = BuildNewOffsetCurveList(input, distAway);

            // Get the index of the last yt point
            double dis = double.MaxValue;
            if (nextCurve.Count > 1)
            {
                for (int i = 1; i < nextCurve.Count; i++)
                {
                    double newdis = Distance(nextCurve[i], ytList[ytList.Count - 1]);
                    if (newdis < dis)
                    {
                        dis = newdis;
                        if (input.IsHeadingSameWay) outClosestTurnPt.CurveIndex = i - 1;
                        else outClosestTurnPt.CurveIndex = i;
                    }
                }

                if (outClosestTurnPt.CurveIndex >= 0)
                {
                    var outPt = outClosestTurnPt;
                    outPt.ClosePt = nextCurve[outClosestTurnPt.CurveIndex];
                    outClosestTurnPt = outPt;

                    var inPt = inClosestTurnPt;
                    inPt.ClosePt = input.GuidancePoints[inClosestTurnPt.CurveIndex];
                    inClosestTurnPt = inPt;

                    if (!AddCurveSequenceLines(input)) return false;
                }
            }

            // Fill in the gaps
            double distanc;
            int cnt4 = ytList.Count;
            for (int i = 1; i < cnt4 - 2; i++)
            {
                int j = i + 1;
                if (j == cnt4 - 1) continue;
                distanc = DistanceSquared(ytList[i], ytList[j]);
                if (distanc > 1)
                {
                    Vec3 pointB = new Vec3((ytList[i].Easting + ytList[j].Easting) / 2.0,
                        (ytList[i].Northing + ytList[j].Northing) / 2.0, ytList[i].Heading);

                    ytList.Insert(j, pointB);
                    cnt4 = ytList.Count;
                    i--;
                }
            }

            // Calculate the new points headings based on fore and aft of point - smoother turns
            cnt4 = ytList.Count;
            Vec3[] arr = new Vec3[cnt4];
            cnt4 -= 2;
            ytList.CopyTo(arr);
            ytList.Clear();

            for (int i = 2; i < cnt4; i++)
            {
                Vec3 pt3 = arr[i];
                pt3.Heading = Math.Atan2(arr[i + 1].Easting - arr[i - 1].Easting,
                    arr[i + 1].Northing - arr[i - 1].Northing);
                if (pt3.Heading < 0) pt3.Heading += TWO_PI;
                ytList.Add(pt3);
            }

            // Too late to turn? Vehicle within a few metres of the turn-line crossing means
            // it has reached the headland and can't start the turn (matches the analytic AB
            // path's pivot-to-turn-line check, NOT pivot-to-path-start).
            if (ytList.Count == 0 || Distance(turnLineCrossing, input.PivotPosition) < 3)
            {
                FailCreate();
                return false;
            }

            isOutOfBounds = false;
            youTurnPhase = 10;
            return true;
        }

        /// <summary>
        /// Sagitta-style curve turn (Brian Tischler's AOG dev-fork geometry).
        /// Mirrors the omega curve turn's single-call behaviour (find crossing,
        /// build an arc that lands inside the boundary, snap to the turn line),
        /// but replaces the Dubins constant-radius arc with a sagitta offset arc
        /// (<see cref="GetOffsetSemicirclePoints"/>) so the path meets the row
        /// tangentially instead of stepping curvature from 0 to 1/R.
        /// </summary>
        private bool CreateCurveSagittaTurn(YouTurnCreationInput input, double turnOffset)
        {
            // Keep from making turns constantly - wait 1.5 seconds
            if (input.MakeUTurnCounter < 4)
            {
                youTurnPhase = 0;
                return true;
            }

            // Check for valid track mode
            if (input.TrackMode == 64 || input.TrackMode == 32) // waterPivot or bndCurve
            {
                youTurnPhase = 11; // Ignore
                return false;
            }

            // A single sagitta semicircle of radius R reaches at most 2R sideways.
            // Wider rows need straight connectors, so fall back to the omega turn.
            if (Math.Abs(turnOffset) > input.TurnRadius * 2.0)
            {
                return CreateCurveOmegaTurn(input, turnOffset);
            }

            // Find the crossing point
            if (!FindCurveTurnPoint(input, false))
            {
                FailCreate();
                return false;
            }

            inClosestTurnPt = new TurnClosePoint(closestTurnPt);
            ytList?.Clear();

            int count = input.IsHeadingSameWay ? -1 : 1;
            int curveIndex = inClosestTurnPt.CurveIndex;

            // Pull the arc back from a full 2R semicircle so it lands exactly on the
            // next row: landing lateral = 2R - pullback, hence pullback = 2R - turnOffset.
            double sagittaPullback = input.TurnRadius * 2.0 - Math.Abs(turnOffset);
            bool isTurningRight = !input.IsTurnLeft;

            isOutOfBounds = true;
            int stopIfWayOut = 0;
            double head = 0;

            // Walk the start index inward until the generated arc sits inside the turn area
            while (isOutOfBounds)
            {
                stopIfWayOut++;
                isOutOfBounds = false;

                ytList.Clear();
                curveIndex += count;

                if (stopIfWayOut == 300 || curveIndex < 1 || curveIndex > (input.GuidancePoints.Count - 2))
                {
                    FailCreate();
                    return false;
                }

                Vec3 currentPos = input.GuidancePoints[curveIndex];
                if (!input.IsHeadingSameWay) currentPos.Heading += Math.PI;
                if (currentPos.Heading >= TWO_PI) currentPos.Heading -= TWO_PI;
                head = currentPos.Heading;

                ytList = SagittaTurnGeometry.BuildOffsetArc(currentPos, head, isTurningRight, input.TurnRadius, sagittaPullback, Math.PI);
                if (ytList.Count == 0)
                {
                    FailCreate();
                    return false;
                }

                for (int i = 0; i < ytList.Count; i++)
                {
                    if (input.IsPointInsideTurnArea(ytList[i]) != 0)
                    {
                        isOutOfBounds = true;
                        break;
                    }
                }
            }
            inClosestTurnPt.CurveIndex = curveIndex;

            // Remove closely spaced points
            int cnt = ytList.Count;
            for (int i = 1; i < cnt - 2; i++)
            {
                if (DistanceSquared(ytList[i], ytList[i + 1]) < pointSpacing)
                {
                    ytList.RemoveAt(i + 1);
                    i--;
                    cnt = ytList.Count;
                }
            }

            // Snap the turn to the turn line
            ytList = MoveTurnInsideTurnLine(input, ytList, head, false, false);
            if (ytList.Count == 0)
            {
                FailCreate();
                return false;
            }

            // Add the curve-following entry/exit legs (single-call: see CompleteCurveTurn).
            return CompleteCurveTurn(input);
        }

        private bool CreateCurveWideTurn(YouTurnCreationInput input, double turnOffset)
        {
            // Keep from making turns constantly
            if (input.MakeUTurnCounter < 4)
            {
                youTurnPhase = 0;
                return true;
            }

            // Check for valid track mode
            if (input.TrackMode == 64 || input.TrackMode == 32) // waterPivot or bndCurve
            {
                youTurnPhase = 11; // Ignore
                return false;
            }

            double head = 0;
            int count = input.IsHeadingSameWay ? -1 : 1;

            switch (youTurnPhase)
            {
                case 0:
                    // Create first semicircle
                    if (!FindCurveTurnPoint(input, false))
                    {
                        if (input.TrackMode == 32 || input.TrackMode == 64) // waterPivot or bndCurve
                            youTurnPhase = 11; // Ignore
                        else
                            FailCreate();
                        return false;
                    }

                    inClosestTurnPt = new TurnClosePoint(closestTurnPt);
                    startOfTurnPt = new TurnClosePoint(inClosestTurnPt);

                    int stopIfWayOut = 0;
                    isOutOfBounds = true;

                    while (isOutOfBounds)
                    {
                        isOutOfBounds = false;
                        stopIfWayOut++;

                        Vec3 currentPos = input.GuidancePoints[inClosestTurnPt.CurveIndex];

                        head = currentPos.Heading;
                        if (!input.IsHeadingSameWay) head += Math.PI;
                        if (head > TWO_PI) head -= TWO_PI;
                        currentPos.Heading = head;

                        // Creates half a circle starting at the crossing point
                        ytList.Clear();
                        ytList.Add(currentPos);

                        // Taken from Dubins
                        while (Math.Abs(head - currentPos.Heading) < Math.PI)
                        {
                            // Update the position
                            currentPos.Easting += pointSpacing * Math.Sin(currentPos.Heading);
                            currentPos.Northing += pointSpacing * Math.Cos(currentPos.Heading);

                            // Which way are we turning?
                            double turnParameter = input.IsTurnLeft ? -1.0 : 1.0;

                            // Update the heading
                            currentPos.Heading += (pointSpacing / input.TurnRadius) * turnParameter;

                            // Add the new coordinate
                            ytList.Add(currentPos);
                        }

                        int cnt4 = ytList.Count;
                        if (cnt4 == 0)
                        {
                            FailCreate();
                            return false;
                        }

                        // Are we out of bounds?
                        for (int j = 0; j < cnt4; j += 2)
                        {
                            if (input.IsPointInsideTurnArea(ytList[j]) != 0)
                            {
                                isOutOfBounds = true;
                                break;
                            }
                        }

                        // First check if not out of bounds
                        if (!isOutOfBounds)
                        {
                            ytList = MoveTurnInsideTurnLine(input, ytList, head, true, false);
                            if (ytList.Count == 0)
                            {
                                FailCreate();
                                return false;
                            }
                            youTurnPhase = 1;
                            return true;
                        }

                        if (stopIfWayOut == 300 || inClosestTurnPt.CurveIndex < 1 || inClosestTurnPt.CurveIndex > (input.GuidancePoints.Count - 2))
                        {
                            FailCreate();
                            return false;
                        }

                        // Keep moving infield till pattern is all inside
                        inClosestTurnPt.CurveIndex = inClosestTurnPt.CurveIndex + count;
                        var closePt = inClosestTurnPt.ClosePt;
                        closePt = input.GuidancePoints[inClosestTurnPt.CurveIndex];
                        inClosestTurnPt.ClosePt = closePt;

                        // Set the flag to Critical stop machine
                        if (Distance(ytList[0], input.PivotPosition) < 3)
                        {
                            FailCreate();
                            return false;
                        }
                    }

                    return false;

                case 1:
                    // Build the next line
                    double widthMinusOverlap = input.ToolWidth - input.ToolOverlap;
                    double distAway = widthMinusOverlap * (input.HowManyPathsAway + ((input.IsTurnLeft ^ input.IsHeadingSameWay) ? input.RowSkipsWidth : -input.RowSkipsWidth))
                        + (input.IsHeadingSameWay ? input.ToolOffset : -input.ToolOffset) + input.NudgeDistance;
                    distAway += (0.5 * widthMinusOverlap);

                    nextCurve = BuildNewOffsetCurveList(input, distAway);

                    // Going with or against boundary?
                    bool isTurnLineSameWay = true;
                    double headingDifference = Math.Abs(inClosestTurnPt.TurnLineHeading - ytList[ytList.Count - 1].Heading);
                    if (headingDifference > PI_BY_2 && headingDifference < 3 * PI_BY_2) isTurnLineSameWay = false;

                    if (!FindCurveOutTurnPoint(nextCurve, startOfTurnPt, isTurnLineSameWay))
                    {
                        FailCreate();
                        return false;
                    }
                    outClosestTurnPt = new TurnClosePoint(closestTurnPt);

                    // Move the turn inside of turnline
                    isOutOfBounds = true;
                    while (isOutOfBounds)
                    {
                        isOutOfBounds = false;
                        Vec3 currentPos = nextCurve[outClosestTurnPt.CurveIndex];

                        head = currentPos.Heading;
                        if ((!input.IsHeadingSameWay && !isOutSameCurve) || (input.IsHeadingSameWay && isOutSameCurve)) head += Math.PI;
                        if (head > TWO_PI) head -= TWO_PI;
                        currentPos.Heading = head;

                        ytList2.Clear();
                        ytList2.Add(currentPos);

                        while (Math.Abs(head - currentPos.Heading) < Math.PI)
                        {
                            currentPos.Easting += pointSpacing * Math.Sin(currentPos.Heading);
                            currentPos.Northing += pointSpacing * Math.Cos(currentPos.Heading);
                            double turnParameter = input.IsTurnLeft ? 1.0 : -1.0;
                            currentPos.Heading += (pointSpacing / input.TurnRadius) * turnParameter;
                            ytList2.Add(currentPos);
                        }

                        int cnt3 = ytList2.Count;
                        if (cnt3 == 0)
                        {
                            FailCreate();
                            return false;
                        }

                        for (int j = 0; j < cnt3; j += 2)
                        {
                            if (input.IsPointInsideTurnArea(ytList2[j]) != 0)
                            {
                                isOutOfBounds = true;
                                break;
                            }
                        }

                        if (!isOutOfBounds)
                        {
                            ytList2 = MoveTurnInsideTurnLine(input, ytList2, head, true, true);
                            if (ytList2.Count == 0)
                            {
                                FailCreate();
                                return false;
                            }
                            youTurnPhase = 2;
                            return true;
                        }

                        if (outClosestTurnPt.CurveIndex < 1 || outClosestTurnPt.CurveIndex > (nextCurve.Count - 2))
                        {
                            FailCreate();
                            return false;
                        }

                        if (!isOutSameCurve) outClosestTurnPt.CurveIndex = outClosestTurnPt.CurveIndex + count;
                        else outClosestTurnPt.CurveIndex = outClosestTurnPt.CurveIndex - count;

                        var outPt = outClosestTurnPt.ClosePt;
                        outPt = nextCurve[outClosestTurnPt.CurveIndex];
                        outClosestTurnPt.ClosePt = outPt;
                    }
                    return false;

                case 2:
                    // Bind the two turns together
                    int cnt1 = ytList.Count;
                    int cnt2 = ytList2.Count;

                    bool isFirstTurnLineSameWay = true;
                    double firstHeadingDifference = Math.Abs(inClosestTurnPt.TurnLineHeading - ytList[ytList.Count - 1].Heading);
                    if (firstHeadingDifference > PI_BY_2 && firstHeadingDifference < 3 * PI_BY_2) isFirstTurnLineSameWay = false;

                    FindInnerTurnPoints(ytList[cnt1 - 1], ytList[0].Heading, inClosestTurnPt, isFirstTurnLineSameWay);
                    TurnClosePoint startClosestTurnPt = new TurnClosePoint(closestTurnPt);

                    FindInnerTurnPoints(ytList2[cnt2 - 1], ytList2[0].Heading + Math.PI, outClosestTurnPt, !isFirstTurnLineSameWay);
                    TurnClosePoint goalClosestTurnPt = new TurnClosePoint(closestTurnPt);

                    if (startClosestTurnPt.TurnLineNum != goalClosestTurnPt.TurnLineNum)
                    {
                        FailCreate();
                        return false;
                    }

                    if (startClosestTurnPt.TurnLineIndex == goalClosestTurnPt.TurnLineIndex)
                    {
                        for (int a = 0; a < cnt2; cnt2--)
                        {
                            ytList.Add(ytList2[cnt2 - 1]);
                        }
                    }
                    else
                    {
                        Vec3 tPoint = new Vec3();
                        int turnCount = input.BoundaryTurnLines[startClosestTurnPt.TurnLineNum].Points.Count;
                        int loops = Math.Abs(startClosestTurnPt.TurnLineIndex - goalClosestTurnPt.TurnLineIndex);

                        if (loops > (input.BoundaryTurnLines[startClosestTurnPt.TurnLineNum].Points.Count / 2))
                        {
                            if (startClosestTurnPt.TurnLineIndex < goalClosestTurnPt.TurnLineIndex)
                                loops = (turnCount - goalClosestTurnPt.TurnLineIndex) + startClosestTurnPt.TurnLineIndex;
                            else
                                loops = (turnCount - startClosestTurnPt.TurnLineIndex) + goalClosestTurnPt.TurnLineIndex;
                        }

                        if (isFirstTurnLineSameWay)
                        {
                            for (int i = 0; i < loops; i++)
                            {
                                if ((startClosestTurnPt.TurnLineIndex + 1) >= turnCount) startClosestTurnPt.TurnLineIndex = -1;
                                tPoint = input.BoundaryTurnLines[startClosestTurnPt.TurnLineNum].Points[startClosestTurnPt.TurnLineIndex + 1];
                                startClosestTurnPt.TurnLineIndex++;
                                if (startClosestTurnPt.TurnLineIndex >= turnCount)
                                    startClosestTurnPt.TurnLineIndex = 0;
                                ytList.Add(tPoint);
                            }
                        }
                        else
                        {
                            for (int i = 0; i < loops; i++)
                            {
                                tPoint = input.BoundaryTurnLines[startClosestTurnPt.TurnLineNum].Points[startClosestTurnPt.TurnLineIndex];
                                startClosestTurnPt.TurnLineIndex--;
                                if (startClosestTurnPt.TurnLineIndex == -1)
                                    startClosestTurnPt.TurnLineIndex = turnCount - 1;
                                ytList.Add(tPoint);
                            }
                        }

                        for (int a = 0; a < cnt2; cnt2--)
                        {
                            ytList.Add(ytList2[cnt2 - 1]);
                        }
                    }

                    if (!AddCurveSequenceLines(input)) return false;

                    double distance;
                    int cnt = ytList.Count;
                    for (int i = 1; i < cnt - 2; i++)
                    {
                        int j = i + 1;
                        if (j == cnt - 1) continue;
                        distance = DistanceSquared(ytList[i], ytList[j]);
                        if (distance > 1)
                        {
                            Vec3 pointB = new Vec3((ytList[i].Easting + ytList[j].Easting) / 2.0,
                                (ytList[i].Northing + ytList[j].Northing) / 2.0, ytList[i].Heading);
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
                        pt3.Heading = Math.Atan2(arr[i + 1].Easting - arr[i - 1].Easting,
                            arr[i + 1].Northing - arr[i - 1].Northing);
                        if (pt3.Heading < 0) pt3.Heading += TWO_PI;
                        ytList.Add(pt3);
                    }

                    if (Distance(ytList[0], input.PivotPosition) < 3)
                    {
                        FailCreate();
                        return false;
                    }

                    isOutOfBounds = false;
                    youTurnPhase = 10;
                    ytList2.Clear();
                    return true;
            }

            return true;
        }

        private bool CreateKStyleTurnCurve(YouTurnCreationInput input, double turnOffset)
        {
            double pointSpacing = input.TurnRadius * 0.1;

            int turnIndex = input.IsPointInsideTurnArea(input.PivotPosition);
            if (input.MakeUTurnCounter < 4 || turnIndex != 0)
            {
                youTurnPhase = 0;
                return true;
            }

            if (!FindCurveTurnPoint(input, true))
            {
                FailCreate();
                return false;
            }

            // Save a copy
            inClosestTurnPt = new TurnClosePoint(closestTurnPt);

            ytList.Clear();

            int count = input.IsHeadingSameWay ? -1 : 1;
            int curveIndex = inClosestTurnPt.CurveIndex + count;

            bool pointOutOfBnd = true;
            int stopIfWayOut = 0;

            double head = 0;

            while (pointOutOfBnd)
            {
                stopIfWayOut++;
                pointOutOfBnd = false;

                // Creates half a circle starting at the crossing point
                ytList.Clear();
                if (curveIndex >= input.GuidancePoints.Count || curveIndex < 0)
                {
                    FailCreate();
                    return false;
                }
                Vec3 currentPos = input.GuidancePoints[curveIndex];

                curveIndex += count;

                if (!input.IsHeadingSameWay) currentPos.Heading += Math.PI;
                if (currentPos.Heading >= TWO_PI) currentPos.Heading -= TWO_PI;

                ytList.Add(currentPos);

                while (Math.Abs(ytList[0].Heading - currentPos.Heading) < 2.2)
                {
                    // Update the position of the car
                    currentPos.Easting += pointSpacing * Math.Sin(currentPos.Heading);
                    currentPos.Northing += pointSpacing * Math.Cos(currentPos.Heading);

                    // Which way are we turning?
                    double turnParameter = input.IsTurnLeft ? -1.0 : 1.0;

                    // Update the heading
                    currentPos.Heading += (pointSpacing / input.TurnRadius) * turnParameter;

                    // Add the new coordinate to the path
                    ytList.Add(currentPos);
                }

                for (int i = 0; i < ytList.Count; i++)
                {
                    if (input.IsPointInsideTurnArea(ytList[i]) != 0)
                    {
                        pointOutOfBnd = true;
                        break;
                    }
                }
            }

            // Move out
            head = ytList[0].Heading;
            double cosHead = Math.Cos(head) * 0.1;
            double sinHead = Math.Sin(head) * 0.1;
            Vec3[] arr2 = new Vec3[ytList.Count];
            ytList.CopyTo(arr2);
            ytList.Clear();

            // Step 2 move the turn inside with steps of 0.1 meter
            int j = 0;
            pointOutOfBnd = false;

            while (!pointOutOfBnd)
            {
                stopIfWayOut++;
                pointOutOfBnd = false;

                for (int i = 0; i < arr2.Length; i++)
                {
                    arr2[i].Easting += sinHead;
                    arr2[i].Northing += cosHead;
                }

                for (j = 0; j < arr2.Length; j++)
                {
                    int bob = input.IsPointInsideTurnArea(arr2[j]);
                    if (bob != 0)
                    {
                        pointOutOfBnd = true;
                        break;
                    }
                }

                if (stopIfWayOut == 300 || Distance(arr2[0], input.PivotPosition) < 6)
                {
                    // For some reason it doesn't go inside boundary, return empty list
                    return false;
                }
            }

            ytList.AddRange(arr2);

            // Add start extension from curve points
            curveIndex -= count;

            // Now we go the other way to turn round
            head = ytList[0].Heading;
            head -= Math.PI;
            if (head < -Math.PI) head += TWO_PI;
            if (head > Math.PI) head -= TWO_PI;

            if (head >= TWO_PI) head -= TWO_PI;
            else if (head < 0) head += TWO_PI;

            // Add the tail to first turn
            head = ytList[ytList.Count - 1].Heading;

            Vec3 pt = new Vec3();
            for (int i = 1; i <= (int)(3 * turnOffset); i++)
            {
                pt.Easting = ytList[ytList.Count - 1].Easting + (Math.Sin(head) * 0.5);
                pt.Northing = ytList[ytList.Count - 1].Northing + (Math.Cos(head) * 0.5);
                pt.Heading = 0;
                ytList.Add(pt);
            }

            // Leading in line of turn
            for (int i = 0; i < 4; i++)
            {
                ytList.Insert(0, input.GuidancePoints[curveIndex + i * count]);
            }

            // Fill in the gaps
            double distance;

            int cnt = ytList.Count;
            for (int i = 1; i < cnt - 2; i++)
            {
                j = i + 1;
                if (j == cnt - 1) continue;
                distance = DistanceSquared(ytList[i], ytList[j]);
                if (distance > 1)
                {
                    Vec3 pointB = new Vec3((ytList[i].Easting + ytList[j].Easting) / 2.0,
                        (ytList[i].Northing + ytList[j].Northing) / 2.0, ytList[i].Heading);

                    ytList.Insert(j, pointB);
                    cnt = ytList.Count;
                    i--;
                }
            }

            // Calculate line headings
            Vec3[] arr = new Vec3[ytList.Count];
            ytList.CopyTo(arr);
            ytList.Clear();

            for (int i = 0; i < arr.Length - 1; i++)
            {
                arr[i].Heading = Math.Atan2(arr[i + 1].Easting - arr[i].Easting, arr[i + 1].Northing - arr[i].Northing);
                if (arr[i].Heading < 0) arr[i].Heading += TWO_PI;
                ytList.Add(arr[i]);
            }

            isOutOfBounds = false;
            youTurnPhase = 10;

            return true;
        }

        #endregion


        #region Helper Methods - Turn Point Finding

        private bool FindCurveTurnPoint(YouTurnCreationInput input, bool useAlternateHeading)
        {
            // Find closest AB Curve point that will cross and go out of bounds
            int count = input.IsHeadingSameWay ? 1 : -1;
            int turnNum = 99;
            int j;

            closestTurnPt = new TurnClosePoint();

            bool loop = input.TrackMode == 32 || input.TrackMode == 64; // bndCurve or waterPivot

            for (j = input.CurrentLocationIndex; j > 0 && j < input.GuidancePoints.Count; j += count)
            {
                if (j < 0)
                {
                    if (loop)
                    {
                        loop = false;
                        j = input.GuidancePoints.Count;
                        continue;
                    }
                    break;
                }
                else if (j >= input.GuidancePoints.Count)
                {
                    if (loop)
                    {
                        loop = false;
                        j = -1;
                        continue;
                    }
                    break;
                }

                int turnIndex = input.IsPointInsideTurnArea(input.GuidancePoints[j]);
                if (turnIndex != 0)
                {
                    // IsPointInsideTurnArea returns 0 = inside, non-zero = outside as a FLAG
                    // (the AgOpenWeb lambda returns 1), NOT a boundary index. The turn line to
                    // intersect is the boundary the curve crossed; AgOpenWeb models a single
                    // turn boundary, so clamp into BoundaryTurnLines range — using the raw flag
                    // as an index threw IndexOutOfRange (BoundaryTurnLines[1] with one entry).
                    int boundaryIndex = Math.Min(turnIndex, input.BoundaryTurnLines.Count - 1);
                    if (boundaryIndex < 0) boundaryIndex = 0;
                    closestTurnPt.CurveIndex = j - count;
                    closestTurnPt.TurnLineNum = boundaryIndex;
                    turnNum = boundaryIndex;
                    break;
                }
            }

            if (turnNum < 0)
            {
                closestTurnPt.TurnLineNum = 0;
                turnNum = 0;
            }
            else if (turnNum == 99)
            {
                // Curve does not cross a boundary
                return false;
            }

            if (closestTurnPt.CurveIndex == -1)
            {
                return false;
            }

            // Find exact intersection with turn line (including closing segment)
            var turnLinePoints = input.BoundaryTurnLines[turnNum].Points;
            for (int i = 0; i < turnLinePoints.Count; i++)
            {
                int nextI = (i + 1) % turnLinePoints.Count;  // Wrap around for closing segment
                int res = GetLineIntersection(
                        turnLinePoints[i].Easting,
                        turnLinePoints[i].Northing,
                        turnLinePoints[nextI].Easting,
                        turnLinePoints[nextI].Northing,

                        input.GuidancePoints[closestTurnPt.CurveIndex].Easting,
                        input.GuidancePoints[closestTurnPt.CurveIndex].Northing,
                        input.GuidancePoints[closestTurnPt.CurveIndex + count].Easting,
                        input.GuidancePoints[closestTurnPt.CurveIndex + count].Northing,

                         ref iE, ref iN);

                if (res == 1)
                {
                    var closePt = closestTurnPt.ClosePt;
                    closePt.Easting = iE;
                    closePt.Northing = iN;

                    if (useAlternateHeading)
                    {
                        double hed = Math.Atan2(turnLinePoints[nextI].Easting - turnLinePoints[i].Easting,
                            turnLinePoints[nextI].Northing - turnLinePoints[i].Northing);
                        if (hed < 0) hed += TWO_PI;
                        closePt.Heading = hed;
                        closestTurnPt.ClosePt = closePt;
                        closestTurnPt.TurnLineIndex = i;
                    }
                    else
                    {
                        closePt.Heading = input.GuidancePoints[closestTurnPt.CurveIndex].Heading;
                        closestTurnPt.ClosePt = closePt;
                        closestTurnPt.TurnLineIndex = i;
                        closestTurnPt.TurnLineNum = turnNum;
                        closestTurnPt.TurnLineHeading = turnLinePoints[i].Heading;
                        if (!input.IsHeadingSameWay && closestTurnPt.CurveIndex > 0) closestTurnPt.CurveIndex--;
                    }
                    break;
                }
            }

            return closestTurnPt.TurnLineIndex != -1 && closestTurnPt.CurveIndex != -1;
        }

        private List<Vec3> MoveTurnInsideTurnLine(YouTurnCreationInput input, List<Vec3> uTurnList, double head, bool deleteSecondHalf, bool invertHeading)
        {
            // Step 1 make array out of the list so that we can modify the position
            double cosHead = Math.Cos(head);
            double sinHead = Math.Sin(head);
            int cnt = uTurnList.Count;
            Vec3[] arr2 = new Vec3[cnt];
            uTurnList.CopyTo(arr2);
            uTurnList.Clear();

            semiCircleIndex = -1;
            // Step 2 move the turn inside with steps of 1 meter
            bool pointOutOfBnd = isOutOfBounds;
            int j = 0;
            int stopIfWayOut = 0;
            while (pointOutOfBnd)
            {
                stopIfWayOut++;
                pointOutOfBnd = false;

                for (int i = 0; i < cnt; i++)
                {
                    arr2[i].Easting -= sinHead;
                    arr2[i].Northing -= cosHead;
                }

                for (; j < cnt; j += 1)
                {
                    int result = input.IsPointInsideTurnArea(arr2[j]);
                    if (result != 0)
                    {
                        pointOutOfBnd = true;
                        if (j > 0) j--;
                        break;
                    }
                }

                double distToVehicle = Distance(arr2[0], input.PivotPosition);
                if (stopIfWayOut == 1000 || distToVehicle < 3)
                {
                    // For some reason it doesn't go inside boundary, return empty list
                    return uTurnList;
                }
            }

            // Step 3, we are now inside turnline, move the turn forward until it hits the turnfence in steps of 0.1 meters
            while (!pointOutOfBnd)
            {
                for (int i = 0; i < cnt; i++)
                {
                    arr2[i].Easting += (sinHead * 0.1);
                    arr2[i].Northing += (cosHead * 0.1);
                }

                for (int a = 0; a < cnt; a++)
                {
                    if (input.IsPointInsideTurnArea(arr2[a]) != 0)
                    {
                        semiCircleIndex = a;
                        pointOutOfBnd = true;
                        break;
                    }
                }
            }

            // Step 4, Should we delete the points after the one that is outside? and where the points made in the wrong direction?
            for (int i = 0; i < cnt; i++)
            {
                if (i == semiCircleIndex && deleteSecondHalf)
                    break;
                if (invertHeading) arr2[i].Heading += Math.PI;
                if (arr2[i].Heading >= TWO_PI) arr2[i].Heading -= TWO_PI;
                else if (arr2[i].Heading < 0) arr2[i].Heading += TWO_PI;
                uTurnList.Add(arr2[i]);
            }

            // We have successfully moved the turn inside
            isOutOfBounds = false;

            return uTurnList;
        }

        private bool AddCurveSequenceLines(YouTurnCreationInput input)
        {
            // Leg length in METRES (user's UTurnExtension, or a headland-derived default).
            double legLength;
            if (input.LegLength > 0)
            {
                legLength = input.LegLength;
            }
            else
            {
                legLength = input.HeadlandWidth * input.YouTurnLegExtensionMultiplier;
                double minLegLength = input.TurnRadius * 2.0;
                if (legLength < minLegLength) legLength = minLegLength;
            }

            bool sameWay = input.IsHeadingSameWay;

            // Entry leg: walk the current pass inward from the arc start for legLength METRES.
            WalkLegByDistance(input.GuidancePoints, ytList[0], inClosestTurnPt.CurveIndex,
                sameWay ? -1 : 1, legLength, insertFront: true);

            // Exit leg: walk the destination pass outward from the arc end for legLength METRES.
            bool outSameWay = isOutSameCurve ? !sameWay : sameWay;
            WalkLegByDistance(nextCurve, ytList[ytList.Count - 1], outClosestTurnPt.CurveIndex,
                outSameWay ? -1 : 1, legLength, insertFront: false);

            return true;
        }

        /// <summary>
        /// Appends/prepends points along <paramref name="points"/> starting at
        /// <paramref name="start"/>/<paramref name="idx"/> until <paramref name="length"/>
        /// METRES have been covered (interpolating the final point), then stops. Mirrors
        /// AgOpen/Twol AddCurveSequenceLines: walks by accumulated DISTANCE, not a fixed point
        /// count — so the leg is the right length regardless of curve point spacing (a 2 m
        /// densified AB line and a 1 m curve both yield a legLength-metre leg). Stops cleanly
        /// if it runs off the end rather than failing the whole turn.
        /// </summary>
        private void WalkLegByDistance(List<Vec3> points, Vec3 start, int idx, int step,
            double length, bool insertFront)
        {
            if (points == null || points.Count == 0) return;
            double distSoFar = 0;
            Vec3 point = start;
            while (true)
            {
                if (idx < 0 || idx >= points.Count) return; // ran off the end — stop the leg
                double dE = point.Easting - points[idx].Easting;
                double dN = point.Northing - points[idx].Northing;
                double seg = Math.Sqrt(dE * dE + dN * dN);

                if (distSoFar + seg > length)
                {
                    double f = (length - distSoFar) / seg;
                    var p = new Vec3(point.Easting - dE * f, point.Northing - dN * f, point.Heading);
                    if (insertFront) ytList.Insert(0, p); else ytList.Add(p);
                    return;
                }

                distSoFar += seg;
                var q = new Vec3(points[idx].Easting, points[idx].Northing, points[idx].Heading);
                if (insertFront) ytList.Insert(0, q); else ytList.Add(q);
                point = points[idx];
                idx += step;
            }
        }

        private List<Vec3> BuildNewOffsetCurveList(YouTurnCreationInput input, double distAway)
        {
            // The destination pass for the exit leg. Mirrors AgOpen CABCurve.BuildNewOffsetList:
            // always offset the BASE reference curve by distAway (which already folds in
            // HowManyPathsAway/row-skips), NOT the current pass — offsetting the current pass
            // would double-count the path-away offset.
            var reference = input.ReferenceCurvePoints != null && input.ReferenceCurvePoints.Count > 1
                ? input.ReferenceCurvePoints
                : input.GuidancePoints;

            if (reference == null || reference.Count < 2)
                return new List<Vec3>();

            // Offset the NON-extended base, then extend the result — same order as the current
            // pass, so the exit leg's in-field portion matches the next guidance line exactly
            // (seamless turn→track handoff) while the tangent extension lets the curve reach
            // the arc end near the boundary.
            return CurveProcessing.ExtendCurveEnds(CurveProcessing.CreateOffsetCurve(reference, distAway));
        }

        private void FindInnerTurnPoints(Vec3 fromPt, double heading, TurnClosePoint turnPt, bool isSameWay)
        {
            double sin = Math.Sin(heading);
            double cos = Math.Cos(heading);
            closestTurnPt = new TurnClosePoint();

            int bndNum = turnPt.TurnLineNum;
            if (bndNum < 0 || bndNum >= _currentInput.BoundaryTurnLines.Count) return;

            var turnLine = _currentInput.BoundaryTurnLines[bndNum].Points;

            if (!isSameWay)
            {
                for (int i = turnLine.Count - 1; i >= 0; i--)
                {
                    int res = GetLineIntersection(
                        fromPt.Easting, fromPt.Northing,
                        fromPt.Easting + (sin * 1000), fromPt.Northing + (cos * 1000),
                        turnLine[i].Easting, turnLine[i].Northing,
                        i == 0 ? turnLine[turnLine.Count - 1].Easting : turnLine[i - 1].Easting,
                        i == 0 ? turnLine[turnLine.Count - 1].Northing : turnLine[i - 1].Northing,
                        ref iE, ref iN);

                    if (res == 1)
                    {
                        var closePt = closestTurnPt.ClosePt;
                        closePt.Easting = iE;
                        closePt.Northing = iN;
                        closestTurnPt.ClosePt = closePt;
                        closestTurnPt.TurnLineNum = bndNum;
                        closestTurnPt.TurnLineIndex = i == 0 ? turnLine.Count - 1 : i - 1;
                        return;
                    }
                }
            }
            else
            {
                // Iterate through all segments including the closing segment
                for (int i = 0; i < turnLine.Count; i++)
                {
                    int nextI = (i + 1) % turnLine.Count;  // Wrap around for closing segment
                    int res = GetLineIntersection(
                        fromPt.Easting, fromPt.Northing,
                        fromPt.Easting + (sin * 1000), fromPt.Northing + (cos * 1000),
                        turnLine[i].Easting, turnLine[i].Northing,
                        turnLine[nextI].Easting, turnLine[nextI].Northing,
                        ref iE, ref iN);

                    if (res == 1)
                    {
                        var closePt = closestTurnPt.ClosePt;
                        closePt.Easting = iE;
                        closePt.Northing = iN;
                        closestTurnPt.ClosePt = closePt;
                        closestTurnPt.TurnLineNum = bndNum;
                        closestTurnPt.TurnLineIndex = i;
                        return;
                    }
                }
            }
        }

        private bool FindCurveOutTurnPoint(List<Vec3> nextCurve, TurnClosePoint startPt, bool isTurnLineSameWay)
        {
            int count = _currentInput.IsHeadingSameWay ? 1 : -1;
            int turnNum = 99;

            closestTurnPt = new TurnClosePoint();

            for (int j = _currentInput.CurrentLocationIndex; j > 0 && j < nextCurve.Count; j += count)
            {
                if (j < 0 || j >= nextCurve.Count) break;

                int turnIndex = _currentInput.IsPointInsideTurnArea(nextCurve[j]);
                if (turnIndex != 0)
                {
                    closestTurnPt.CurveIndex = j - count;
                    closestTurnPt.TurnLineNum = turnIndex;
                    turnNum = turnIndex;
                    break;
                }
            }

            if (turnNum < 0)
            {
                closestTurnPt.TurnLineNum = 0;
                turnNum = 0;
            }
            else if (turnNum == 99)
            {
                return false;
            }

            if (closestTurnPt.CurveIndex == -1)
            {
                return false;
            }

            // Find exact intersection (including closing segment)
            if (turnNum >= _currentInput.BoundaryTurnLines.Count) return false;

            var turnLinePoints = _currentInput.BoundaryTurnLines[turnNum].Points;
            for (int i = 0; i < turnLinePoints.Count; i++)
            {
                int nextI = (i + 1) % turnLinePoints.Count;  // Wrap around for closing segment
                int res = GetLineIntersection(
                    turnLinePoints[i].Easting,
                    turnLinePoints[i].Northing,
                    turnLinePoints[nextI].Easting,
                    turnLinePoints[nextI].Northing,
                    nextCurve[closestTurnPt.CurveIndex].Easting,
                    nextCurve[closestTurnPt.CurveIndex].Northing,
                    nextCurve[closestTurnPt.CurveIndex + count].Easting,
                    nextCurve[closestTurnPt.CurveIndex + count].Northing,
                    ref iE, ref iN);

                if (res == 1)
                {
                    var closePt = closestTurnPt.ClosePt;
                    closePt.Easting = iE;
                    closePt.Northing = iN;
                    closePt.Heading = nextCurve[closestTurnPt.CurveIndex].Heading;
                    closestTurnPt.ClosePt = closePt;
                    closestTurnPt.TurnLineIndex = i;
                    closestTurnPt.TurnLineNum = turnNum;
                    closestTurnPt.TurnLineHeading = turnLinePoints[i].Heading;

                    // Check if we're going out the same curve
                    if (closestTurnPt.TurnLineNum == startPt.TurnLineNum &&
                        Math.Abs(closestTurnPt.TurnLineIndex - startPt.TurnLineIndex) < 3)
                    {
                        isOutSameCurve = true;
                    }
                    break;
                }
            }

            return closestTurnPt.TurnLineIndex != -1 && closestTurnPt.CurveIndex != -1;
        }

        #endregion

        #region Helper Methods - Utilities

        /// <summary>
        /// Calculate line intersection between two line segments.
        /// Returns 1 if lines intersect, 0 otherwise.
        /// </summary>
        private int GetLineIntersection(double p0x, double p0y, double p1x, double p1y,
                double p2x, double p2y, double p3x, double p3y, ref double iEast, ref double iNorth)
        {
            double s1x = p1x - p0x;
            double s1y = p1y - p0y;

            double s2x = p3x - p2x;
            double s2y = p3y - p2y;

            double s = (-s1y * (p0x - p2x) + s1x * (p0y - p2y)) / (-s2x * s1y + s1x * s2y);

            if (s >= 0 && s <= 1)
            {
                // Check other side
                double t = (s2x * (p0y - p2y) - s2y * (p0x - p2x)) / (-s2x * s1y + s1x * s2y);
                if (t >= 0 && t <= 1)
                {
                    // Collision detected
                    iEast = p0x + (t * s1x);
                    iNorth = p0y + (t * s1y);
                    return 1;
                }
            }

            return 0; // No collision
        }

        private static double Distance(Vec3 a, Vec3 b)
        {
            double dx = a.Easting - b.Easting;
            double dz = a.Northing - b.Northing;
            return Math.Sqrt((dx * dx) + (dz * dz));
        }

        private static double DistanceSquared(Vec3 a, Vec3 b)
        {
            double dx = a.Easting - b.Easting;
            double dz = a.Northing - b.Northing;
            return (dx * dx) + (dz * dz);
        }

        private bool IsGoingStraightThrough()
        {
            if (ytList.Count < 3) return false;
            return Math.PI - Math.Abs(Math.Abs(ytList[ytList.Count - 2].Heading - ytList[1].Heading) - Math.PI) < PI_BY_2;
        }

        private void FailCreate()
        {
            isOutOfBounds = true;
            youTurnPhase = 11;
        }

        #endregion

        #region Helper Classes

        /// <summary>
        /// Represents a close point on a turn line.
        /// </summary>
        private class TurnClosePoint
        {
            public Vec3 ClosePt { get; set; } = new Vec3();
            public int TurnLineNum { get; set; } = -1;
            public int TurnLineIndex { get; set; } = -1;
            public double TurnLineHeading { get; set; } = -1;
            public int CurveIndex { get; set; } = -1;

            public TurnClosePoint() { }

            public TurnClosePoint(TurnClosePoint other)
            {
                ClosePt = other.ClosePt;
                TurnLineNum = other.TurnLineNum;
                TurnLineIndex = other.TurnLineIndex;
                TurnLineHeading = other.TurnLineHeading;
                CurveIndex = other.CurveIndex;
            }
        }

        #endregion
    }
}
