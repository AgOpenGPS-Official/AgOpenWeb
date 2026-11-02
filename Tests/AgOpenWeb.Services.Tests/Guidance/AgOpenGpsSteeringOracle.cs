// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Base;

namespace AgOpenWeb.Services.Tests.Guidance;

/// <summary>
/// AgOpenGPS's steering, ported line for line from the snapshot in <c>AgOpen_Snapshot/</c>
/// (2026-01-24): <c>AgOpenGPS.Core/Services/Guidance/PurePursuitGuidanceService.cs</c>,
/// <c>CurvePurePursuitGuidanceService.cs</c> (the curve branch; the water-pivot branch is
/// left out) and <c>StanleyGuidanceService.cs</c> (the AB line and <c>CalculateSteerAngle</c>).
/// It is the oracle the parity tests compare <see cref="Track.TrackGuidanceService"/> with,
/// so it keeps AgOpenGPS's names, order and arithmetic; only the vector types are ours.
/// Don't tidy it: a difference from the snapshot is a difference from AgOpenGPS.
/// </summary>
internal static class AgOpenGpsSteeringOracle
{
    private const double PIBy2 = Math.PI / 2.0;
    private const double TwoPI = Math.PI * 2.0;

    #region Pure Pursuit, AB line

    public sealed class PurePursuitInput
    {
        public Vec3 PivotPosition, CurrentLinePtA, CurrentLinePtB;
        public double ABHeading;
        public bool IsHeadingSameWay;
        public double Wheelbase, MaxSteerAngle, PurePursuitIntegralGain, GoalPointDistance, SideHillCompFactor;
        public double FixHeading, AvgSpeed;
        public bool IsReverse, IsAutoSteerOn;
        public double ImuRoll = 88888;
        public double PreviousIntegral, PreviousPivotDistanceError, PreviousPivotDistanceErrorLast;
        public int PreviousCounter;
    }

    public sealed class PurePursuitOutput
    {
        public double SteerAngle, DistanceFromCurrentLinePivot, PurePursuitRadius, REast, RNorth;
        public short GuidanceLineDistanceOff, GuidanceLineSteerAngle;
        public Vec2 GoalPoint, RadiusPoint;
        public double ModeActualHeadingError, ModeActualXTE;
        public double Integral, PivotDistanceError, PivotDistanceErrorLast, PivotDerivative;
        public int Counter;
    }

    public static PurePursuitOutput CalculateGuidanceABLine(PurePursuitInput input)
    {
        var output = new PurePursuitOutput();

        double dx = input.CurrentLinePtB.Easting - input.CurrentLinePtA.Easting;
        double dy = input.CurrentLinePtB.Northing - input.CurrentLinePtA.Northing;

        output.DistanceFromCurrentLinePivot = ((dy * input.PivotPosition.Easting) - (dx * input.PivotPosition.Northing)
            + (input.CurrentLinePtB.Easting * input.CurrentLinePtA.Northing)
            - (input.CurrentLinePtB.Northing * input.CurrentLinePtA.Easting))
            / Math.Sqrt((dy * dy) + (dx * dx));

        if (input.PurePursuitIntegralGain != 0 && !input.IsReverse)
        {
            output.PivotDistanceError = output.DistanceFromCurrentLinePivot * 0.2 + input.PreviousPivotDistanceError * 0.8;
            output.Counter = input.PreviousCounter + 1;

            if (output.Counter > 4)
            {
                output.PivotDerivative = output.PivotDistanceError - input.PreviousPivotDistanceErrorLast;
                output.PivotDistanceErrorLast = output.PivotDistanceError;
                output.Counter = 0;
                output.PivotDerivative *= 2;
            }
            else
            {
                output.PivotDerivative = 0;
                output.PivotDistanceErrorLast = input.PreviousPivotDistanceErrorLast;
            }

            if (input.IsAutoSteerOn
                && Math.Abs(output.PivotDerivative) < 0.1
                && input.AvgSpeed > 2.5)
            {
                if ((input.PreviousIntegral < 0 && output.DistanceFromCurrentLinePivot < 0)
                    || (input.PreviousIntegral > 0 && output.DistanceFromCurrentLinePivot > 0))
                {
                    output.Integral = input.PreviousIntegral + output.PivotDistanceError * input.PurePursuitIntegralGain * -0.04;
                }
                else
                {
                    if (Math.Abs(output.DistanceFromCurrentLinePivot) > 0.02)
                    {
                        output.Integral = input.PreviousIntegral + output.PivotDistanceError * input.PurePursuitIntegralGain * -0.02;
                        if (output.Integral > 0.2) output.Integral = 0.2;
                        else if (output.Integral < -0.2) output.Integral = -0.2;
                    }
                    else
                    {
                        output.Integral = input.PreviousIntegral;
                    }
                }
            }
            else
            {
                output.Integral = input.PreviousIntegral * 0.95;
            }
        }
        else
        {
            output.Integral = 0;
            output.PivotDistanceError = 0;
            output.PivotDistanceErrorLast = input.PreviousPivotDistanceErrorLast;
            output.Counter = input.PreviousCounter;
            output.PivotDerivative = 0;
        }

        double U = (((input.PivotPosition.Easting - input.CurrentLinePtA.Easting) * dx)
            + ((input.PivotPosition.Northing - input.CurrentLinePtA.Northing) * dy))
            / ((dx * dx) + (dy * dy));

        output.REast = input.CurrentLinePtA.Easting + (U * dx);
        output.RNorth = input.CurrentLinePtA.Northing + (U * dy);

        if (input.IsReverse ^ input.IsHeadingSameWay)
        {
            output.GoalPoint = new Vec2(
                output.REast + (Math.Sin(input.ABHeading) * input.GoalPointDistance),
                output.RNorth + (Math.Cos(input.ABHeading) * input.GoalPointDistance));
        }
        else
        {
            output.GoalPoint = new Vec2(
                output.REast - (Math.Sin(input.ABHeading) * input.GoalPointDistance),
                output.RNorth - (Math.Cos(input.ABHeading) * input.GoalPointDistance));
        }

        double goalPointDistanceDSquared = DistanceSquared(
            output.GoalPoint.Northing, output.GoalPoint.Easting,
            input.PivotPosition.Northing, input.PivotPosition.Easting);

        double localHeading;
        if (input.IsHeadingSameWay)
            localHeading = TwoPI - input.FixHeading + output.Integral;
        else
            localHeading = TwoPI - input.FixHeading - output.Integral;

        output.PurePursuitRadius = goalPointDistanceDSquared / (2 * (
            ((output.GoalPoint.Easting - input.PivotPosition.Easting) * Math.Cos(localHeading))
            + ((output.GoalPoint.Northing - input.PivotPosition.Northing) * Math.Sin(localHeading))));

        output.SteerAngle = ToDegrees(Math.Atan(2 * (
            ((output.GoalPoint.Easting - input.PivotPosition.Easting) * Math.Cos(localHeading))
            + ((output.GoalPoint.Northing - input.PivotPosition.Northing) * Math.Sin(localHeading)))
            * input.Wheelbase / goalPointDistanceDSquared));

        if (input.ImuRoll != 88888)
            output.SteerAngle += input.ImuRoll * -input.SideHillCompFactor;

        if (output.SteerAngle < -input.MaxSteerAngle)
            output.SteerAngle = -input.MaxSteerAngle;
        if (output.SteerAngle > input.MaxSteerAngle)
            output.SteerAngle = input.MaxSteerAngle;

        if (output.PurePursuitRadius < -500) output.PurePursuitRadius = -500;
        if (output.PurePursuitRadius > 500) output.PurePursuitRadius = 500;

        output.RadiusPoint = new Vec2(
            input.PivotPosition.Easting + (output.PurePursuitRadius * Math.Cos(localHeading)),
            input.PivotPosition.Northing + (output.PurePursuitRadius * Math.Sin(localHeading)));

        if (!input.IsHeadingSameWay)
            output.DistanceFromCurrentLinePivot *= -1.0;

        output.ModeActualXTE = output.DistanceFromCurrentLinePivot;

        double steerHeadingError = input.PivotPosition.Heading - input.ABHeading;

        if (steerHeadingError > Math.PI)
            steerHeadingError -= Math.PI;
        else if (steerHeadingError < -Math.PI)
            steerHeadingError += Math.PI;

        if (steerHeadingError > PIBy2)
            steerHeadingError -= Math.PI;
        else if (steerHeadingError < -PIBy2)
            steerHeadingError += Math.PI;

        output.ModeActualHeadingError = ToDegrees(steerHeadingError);

        output.GuidanceLineDistanceOff = (short)Math.Round(output.DistanceFromCurrentLinePivot * 1000.0, MidpointRounding.AwayFromZero);
        output.GuidanceLineSteerAngle = (short)(output.SteerAngle * 100);

        return output;
    }

    #endregion

    #region Pure Pursuit, curve

    public sealed class CurvePurePursuitInput
    {
        public Vec3 PivotPosition;
        public List<Vec3> CurvePoints = new();
        public int CurrentLocationIndex;
        public bool FindGlobalNearestPoint = true;
        public bool IsHeadingSameWay;
        public double Wheelbase, MaxSteerAngle, PurePursuitIntegralGain, GoalPointDistance, SideHillCompFactor;
        public double FixHeading, AvgSpeed;
        public bool IsReverse, IsAutoSteerOn, IsYouTurnTriggered;
        public double ImuRoll = 88888;
        public double PreviousIntegral, PreviousPivotDistanceError, PreviousPivotDistanceErrorLast;
        public int PreviousCounter;
    }

    public sealed class CurvePurePursuitOutput
    {
        public double SteerAngle, DistanceFromCurrentLinePivot, PurePursuitRadius, REast, RNorth, ManualUturnHeading;
        public short GuidanceLineDistanceOff, GuidanceLineSteerAngle;
        public Vec2 GoalPoint, RadiusPoint;
        public int CurrentLocationIndex;
        public bool FindGlobalNearestPoint;
        public double ModeActualHeadingError, ModeActualXTE;
        public double Integral, PivotDistanceError, PivotDistanceErrorLast, PivotDerivative;
        public int Counter;
        public bool IsAtEndOfCurve;
    }

    public static CurvePurePursuitOutput CalculateGuidanceCurve(CurvePurePursuitInput input)
    {
        var output = new CurvePurePursuitOutput
        {
            FindGlobalNearestPoint = input.FindGlobalNearestPoint,
            CurrentLocationIndex = input.CurrentLocationIndex
        };

        if (input.CurvePoints == null || input.CurvePoints.Count == 0)
        {
            output.DistanceFromCurrentLinePivot = 32000;
            output.GuidanceLineDistanceOff = 32000;
            return output;
        }

        bool reverseHeading = input.IsReverse ? !input.IsHeadingSameWay : input.IsHeadingSameWay;

        int A, B, C;

        int cc;
        if (output.FindGlobalNearestPoint)
        {
            cc = FindNearestGlobalCurvePoint(input.PivotPosition, input.CurvePoints, 10);
            output.FindGlobalNearestPoint = false;
        }
        else
        {
            cc = FindNearestLocalCurvePoint(input.PivotPosition, input.CurvePoints,
                output.CurrentLocationIndex, input.GoalPointDistance, reverseHeading);
        }

        double minDistA = double.MaxValue;
        double minDistB = double.MaxValue;
        A = 0;
        B = 0;

        int dd = cc + 8;
        if (dd > input.CurvePoints.Count - 1) dd = input.CurvePoints.Count;
        cc -= 8;
        if (cc < 0) cc = 0;

        for (int j = cc; j < dd; j++)
        {
            double dist = DistanceSquared(input.PivotPosition, input.CurvePoints[j]);
            if (dist < minDistA)
            {
                minDistB = minDistA;
                B = A;
                minDistA = dist;
                A = j;
            }
            else if (dist < minDistB)
            {
                minDistB = dist;
                B = j;
            }
        }

        if (A > B)
        {
            C = A;
            A = B;
            B = C;
        }

        output.CurrentLocationIndex = A;

        if (A > input.CurvePoints.Count - 1 || B > input.CurvePoints.Count - 1)
        {
            output.DistanceFromCurrentLinePivot = 32000;
            output.GuidanceLineDistanceOff = 32000;
            return output;
        }

        double dx = input.CurvePoints[B].Easting - input.CurvePoints[A].Easting;
        double dz = input.CurvePoints[B].Northing - input.CurvePoints[A].Northing;

        if (Math.Abs(dx) < double.Epsilon && Math.Abs(dz) < double.Epsilon)
        {
            output.DistanceFromCurrentLinePivot = 32000;
            output.GuidanceLineDistanceOff = 32000;
            return output;
        }

        output.DistanceFromCurrentLinePivot = ((dz * input.PivotPosition.Easting) - (dx * input.PivotPosition.Northing)
            + (input.CurvePoints[B].Easting * input.CurvePoints[A].Northing)
            - (input.CurvePoints[B].Northing * input.CurvePoints[A].Easting))
            / Math.Sqrt((dz * dz) + (dx * dx));

        if (input.PurePursuitIntegralGain != 0 && !input.IsReverse)
        {
            output.PivotDistanceError = output.DistanceFromCurrentLinePivot * 0.2 + input.PreviousPivotDistanceError * 0.8;
            output.Counter = input.PreviousCounter + 1;

            if (output.Counter > 4)
            {
                output.PivotDerivative = output.PivotDistanceError - input.PreviousPivotDistanceErrorLast;
                output.PivotDistanceErrorLast = output.PivotDistanceError;
                output.Counter = 0;
                output.PivotDerivative *= 2;
            }
            else
            {
                output.PivotDerivative = 0;
                output.PivotDistanceErrorLast = input.PreviousPivotDistanceErrorLast;
            }

            if (input.IsAutoSteerOn && input.AvgSpeed > 2.5 && Math.Abs(output.PivotDerivative) < 0.1 && !input.IsYouTurnTriggered)
            {
                if ((input.PreviousIntegral < 0 && output.DistanceFromCurrentLinePivot < 0)
                    || (input.PreviousIntegral > 0 && output.DistanceFromCurrentLinePivot > 0))
                {
                    output.Integral = input.PreviousIntegral + output.PivotDistanceError * input.PurePursuitIntegralGain * -0.04;
                }
                else
                {
                    if (Math.Abs(output.DistanceFromCurrentLinePivot) > 0.02)
                    {
                        output.Integral = input.PreviousIntegral + output.PivotDistanceError * input.PurePursuitIntegralGain * -0.02;
                        if (output.Integral > 0.2) output.Integral = 0.2;
                        else if (output.Integral < -0.2) output.Integral = -0.2;
                    }
                    else
                    {
                        output.Integral = input.PreviousIntegral;
                    }
                }
            }
            else
            {
                output.Integral = input.PreviousIntegral * 0.95;
            }
        }
        else
        {
            output.Integral = 0;
            output.PivotDistanceError = 0;
            output.PivotDistanceErrorLast = input.PreviousPivotDistanceErrorLast;
            output.Counter = input.PreviousCounter;
            output.PivotDerivative = 0;
        }

        double U = (((input.PivotPosition.Easting - input.CurvePoints[A].Easting) * dx)
            + ((input.PivotPosition.Northing - input.CurvePoints[A].Northing) * dz))
            / ((dx * dx) + (dz * dz));

        output.REast = input.CurvePoints[A].Easting + (U * dx);
        output.RNorth = input.CurvePoints[A].Northing + (U * dz);
        output.ManualUturnHeading = input.CurvePoints[A].Heading;

        int count = reverseHeading ? 1 : -1;
        Vec3 start = new Vec3(output.REast, output.RNorth, 0);
        double distSoFar = 0;

        for (int i = reverseHeading ? B : A; i < input.CurvePoints.Count && i >= 0;)
        {
            double tempDist = Distance(start, input.CurvePoints[i]);

            if ((tempDist + distSoFar) > input.GoalPointDistance)
            {
                double j = (input.GoalPointDistance - distSoFar) / tempDist;
                output.GoalPoint = new Vec2(
                    (((1 - j) * start.Easting) + (j * input.CurvePoints[i].Easting)),
                    (((1 - j) * start.Northing) + (j * input.CurvePoints[i].Northing)));
                break;
            }
            else
            {
                distSoFar += tempDist;
            }

            start = input.CurvePoints[i];
            i += count;
            if (i < 0) i = input.CurvePoints.Count - 1;
            if (i > input.CurvePoints.Count - 1) i = 0;
        }

        if (input.IsAutoSteerOn && !input.IsReverse)
        {
            if (input.IsHeadingSameWay)
            {
                if (Distance(output.GoalPoint, input.CurvePoints[input.CurvePoints.Count - 1]) < 0.5)
                    output.IsAtEndOfCurve = true;
            }
            else
            {
                if (Distance(output.GoalPoint, input.CurvePoints[0]) < 0.5)
                    output.IsAtEndOfCurve = true;
            }
        }

        double goalPointDistanceSquared = DistanceSquared(
            output.GoalPoint.Northing, output.GoalPoint.Easting,
            input.PivotPosition.Northing, input.PivotPosition.Easting);

        double localHeading;
        if (reverseHeading)
            localHeading = TwoPI - input.FixHeading + output.Integral;
        else
            localHeading = TwoPI - input.FixHeading - output.Integral;

        output.PurePursuitRadius = goalPointDistanceSquared / (2 * (
            ((output.GoalPoint.Easting - input.PivotPosition.Easting) * Math.Cos(localHeading))
            + ((output.GoalPoint.Northing - input.PivotPosition.Northing) * Math.Sin(localHeading))));

        output.SteerAngle = ToDegrees(Math.Atan(2 * (
            ((output.GoalPoint.Easting - input.PivotPosition.Easting) * Math.Cos(localHeading))
            + ((output.GoalPoint.Northing - input.PivotPosition.Northing) * Math.Sin(localHeading)))
            * input.Wheelbase / goalPointDistanceSquared));

        if (input.ImuRoll != 88888)
            output.SteerAngle += input.ImuRoll * -input.SideHillCompFactor;

        if (output.SteerAngle < -input.MaxSteerAngle)
            output.SteerAngle = -input.MaxSteerAngle;
        if (output.SteerAngle > input.MaxSteerAngle)
            output.SteerAngle = input.MaxSteerAngle;

        if (!input.IsHeadingSameWay)
            output.DistanceFromCurrentLinePivot *= -1.0;

        output.ModeActualXTE = output.DistanceFromCurrentLinePivot;

        double steerHeadingError = input.PivotPosition.Heading - input.CurvePoints[A].Heading;

        if (steerHeadingError > Math.PI)
            steerHeadingError -= Math.PI;
        else if (steerHeadingError < -Math.PI)
            steerHeadingError += Math.PI;

        if (steerHeadingError > PIBy2)
            steerHeadingError -= Math.PI;
        else if (steerHeadingError < -PIBy2)
            steerHeadingError += Math.PI;

        output.ModeActualHeadingError = ToDegrees(steerHeadingError);

        output.RadiusPoint = new Vec2(
            input.PivotPosition.Easting + (output.PurePursuitRadius * Math.Cos(localHeading)),
            input.PivotPosition.Northing + (output.PurePursuitRadius * Math.Sin(localHeading)));

        output.GuidanceLineDistanceOff = (short)Math.Round(output.DistanceFromCurrentLinePivot * 1000.0, MidpointRounding.AwayFromZero);
        output.GuidanceLineSteerAngle = (short)(output.SteerAngle * 100);

        return output;
    }

    private static int FindNearestGlobalCurvePoint(Vec3 refPoint, List<Vec3> curvePoints, int increment = 1)
    {
        double minDist = double.MaxValue;
        int minDistIndex = 0;

        for (int i = 0; i < curvePoints.Count; i += increment)
        {
            double dist = DistanceSquared(refPoint, curvePoints[i]);
            if (dist < minDist)
            {
                minDist = dist;
                minDistIndex = i;
            }
        }
        return minDistIndex;
    }

    private static int FindNearestLocalCurvePoint(Vec3 refPoint, List<Vec3> curvePoints,
        int startIndex, double minSearchDistance, bool reverseSearchDirection)
    {
        double minDist = DistanceSquared(refPoint, curvePoints[(startIndex + curvePoints.Count) % curvePoints.Count]);
        int minDistIndex = startIndex;

        int directionMultiplier = reverseSearchDirection ? 1 : -1;
        double distSoFar = 0;
        Vec3 start = curvePoints[startIndex];

        int offset = 1;

        while (offset < curvePoints.Count)
        {
            int pointIndex = (startIndex + (offset * directionMultiplier) + curvePoints.Count) % curvePoints.Count;
            double dist = DistanceSquared(refPoint, curvePoints[pointIndex]);

            if (dist < minDist)
            {
                minDist = dist;
                minDistIndex = pointIndex;
            }

            distSoFar += Distance(start, curvePoints[pointIndex]);
            start = curvePoints[pointIndex];

            offset++;

            if (distSoFar > minSearchDistance)
                break;
        }

        while (offset < curvePoints.Count)
        {
            int pointIndex = (startIndex + (offset * directionMultiplier) + curvePoints.Count) % curvePoints.Count;
            double dist = DistanceSquared(refPoint, curvePoints[pointIndex]);
            if (dist < minDist)
            {
                minDist = dist;
                minDistIndex = pointIndex;
            }
            else
            {
                break;
            }

            offset++;
        }

        return minDistIndex;
    }

    #endregion

    #region Stanley, AB line

    public sealed class StanleyInput
    {
        public Vec3 PivotPosition, SteerPosition;
        public double StanleyHeadingErrorGain, StanleyDistanceErrorGain, StanleyIntegralGainAB;
        public double MaxSteerAngle, SideHillCompFactor, AvgSpeed;
        public bool IsReverse, IsAutoSteerOn;
        public double ImuRoll = 88888;
        public double PreviousIntegral, PreviousXTrackSteerCorrection, PreviousDistSteerError, PreviousLastDistSteerError;
        public int PreviousCounter;
        public double PreviousPivotDistanceError;
    }

    public sealed class StanleyOutput
    {
        public double SteerAngle, DistanceFromCurrentLineSteer, DistanceFromCurrentLinePivot;
        public short GuidanceLineDistanceOff, GuidanceLineSteerAngle;
        public double REastSteer, RNorthSteer, REastPivot, RNorthPivot;
        public double SteerHeadingError, ModeActualHeadingError, ModeActualXTE;
        public double Integral, XTrackSteerCorrection, DistSteerError, LastDistSteerError;
        public int Counter;
        public double PivotDistanceError, DerivativeDistError;
    }

    public static StanleyOutput CalculateGuidanceABLine(Vec3 curPtA, Vec3 curPtB, StanleyInput input, bool isHeadingSameWay)
    {
        var output = new StanleyOutput();

        double dx = curPtB.Easting - curPtA.Easting;
        double dy = curPtB.Northing - curPtA.Northing;

        if (Math.Abs(dx) < double.Epsilon && Math.Abs(dy) < double.Epsilon)
        {
            output.DistanceFromCurrentLineSteer = 32000;
            output.GuidanceLineDistanceOff = 32000;
            return output;
        }

        output.DistanceFromCurrentLinePivot = ((dy * input.PivotPosition.Easting) - (dx * input.PivotPosition.Northing)
            + (curPtB.Easting * curPtA.Northing) - (curPtB.Northing * curPtA.Easting))
            / Math.Sqrt((dy * dy) + (dx * dx));

        if (!isHeadingSameWay)
            output.DistanceFromCurrentLinePivot *= -1.0;

        double U = (((input.PivotPosition.Easting - curPtA.Easting) * dx)
            + ((input.PivotPosition.Northing - curPtA.Northing) * dy))
            / ((dx * dx) + (dy * dy));

        output.REastPivot = curPtA.Easting + (U * dx);
        output.RNorthPivot = curPtA.Northing + (U * dy);

        Vec3 steerA = new Vec3(
            curPtA.Easting + (Math.Sin(curPtA.Heading + PIBy2) * input.PreviousIntegral),
            curPtA.Northing + (Math.Cos(curPtA.Heading + PIBy2) * input.PreviousIntegral),
            curPtA.Heading);

        Vec3 steerB = new Vec3(
            curPtB.Easting + (Math.Sin(curPtB.Heading + PIBy2) * input.PreviousIntegral),
            curPtB.Northing + (Math.Cos(curPtB.Heading + PIBy2) * input.PreviousIntegral),
            curPtB.Heading);

        dx = steerB.Easting - steerA.Easting;
        dy = steerB.Northing - steerA.Northing;

        if (Math.Abs(dx) < double.Epsilon && Math.Abs(dy) < double.Epsilon)
        {
            output.DistanceFromCurrentLineSteer = 32000;
            output.GuidanceLineDistanceOff = 32000;
            return output;
        }

        output.DistanceFromCurrentLineSteer = ((dy * input.SteerPosition.Easting) - (dx * input.SteerPosition.Northing)
            + (steerB.Easting * steerA.Northing) - (steerB.Northing * steerA.Easting))
            / Math.Sqrt((dy * dy) + (dx * dx));

        if (!isHeadingSameWay)
            output.DistanceFromCurrentLineSteer *= -1.0;

        U = (((input.SteerPosition.Easting - steerA.Easting) * dx)
            + ((input.SteerPosition.Northing - steerA.Northing) * dy))
            / ((dx * dx) + (dy * dy));

        output.REastSteer = steerA.Easting + (U * dx);
        output.RNorthSteer = steerA.Northing + (U * dy);

        double steerErr = Math.Atan2(output.REastSteer - output.REastPivot, output.RNorthSteer - output.RNorthPivot);
        output.SteerHeadingError = input.SteerPosition.Heading - steerErr;

        if (output.SteerHeadingError > Math.PI)
            output.SteerHeadingError -= Math.PI;
        else if (output.SteerHeadingError < -Math.PI)
            output.SteerHeadingError += Math.PI;

        if (output.SteerHeadingError > PIBy2)
            output.SteerHeadingError -= Math.PI;
        else if (output.SteerHeadingError < -PIBy2)
            output.SteerHeadingError += Math.PI;

        output.ModeActualHeadingError = ToDegrees(output.SteerHeadingError);

        CalculateSteerAngle(input, output);

        return output;
    }

    private static void CalculateSteerAngle(StanleyInput input, StanleyOutput output)
    {
        double steerHeadingError = output.SteerHeadingError;

        if (input.IsReverse)
            steerHeadingError *= -1;

        steerHeadingError *= input.StanleyHeadingErrorGain;

        double sped = Math.Abs(input.AvgSpeed);
        if (sped > 1)
            sped = 1 + 0.277 * (sped - 1);
        else
            sped = 1;

        double XTEc = Math.Atan((output.DistanceFromCurrentLineSteer * input.StanleyDistanceErrorGain) / sped);

        output.XTrackSteerCorrection = (input.PreviousXTrackSteerCorrection * 0.5) + XTEc * 0.5;

        output.DistSteerError = (input.PreviousDistSteerError * 0.95) + ((output.XTrackSteerCorrection * 60) * 0.05);
        output.Counter = input.PreviousCounter + 1;

        if (output.Counter > 5)
        {
            output.DerivativeDistError = output.DistSteerError - input.PreviousLastDistSteerError;
            output.LastDistSteerError = output.DistSteerError;
            output.Counter = 0;
        }
        else
        {
            output.DerivativeDistError = 0;
            output.LastDistSteerError = input.PreviousLastDistSteerError;
        }

        output.SteerAngle = ToDegrees((output.XTrackSteerCorrection + steerHeadingError) * -1.0);

        if (Math.Abs(output.DistanceFromCurrentLineSteer) > 0.5)
            output.SteerAngle *= 0.5;
        else
            output.SteerAngle *= (1 - Math.Abs(output.DistanceFromCurrentLineSteer));

        output.PivotDistanceError = (input.PreviousPivotDistanceError * 0.6) + (output.DistanceFromCurrentLinePivot * 0.4);

        if (input.AvgSpeed > 1
            && input.IsAutoSteerOn
            && Math.Abs(output.DerivativeDistError) < 1
            && Math.Abs(output.PivotDistanceError) < 0.25)
        {
            if ((input.PreviousIntegral < 0 && output.DistanceFromCurrentLinePivot < 0)
                || (input.PreviousIntegral > 0 && output.DistanceFromCurrentLinePivot > 0))
            {
                output.Integral = input.PreviousIntegral + output.PivotDistanceError * input.StanleyIntegralGainAB * -0.03;
            }
            else
            {
                output.Integral = input.PreviousIntegral + output.PivotDistanceError * input.StanleyIntegralGainAB * -0.01;
            }

            if (input.StanleyIntegralGainAB == 0)
                output.Integral = 0;
        }
        else
        {
            output.Integral = input.PreviousIntegral * 0.7;
        }

        if (input.IsReverse)
            output.Integral = 0;

        if (input.ImuRoll != 88888)
            output.SteerAngle += input.ImuRoll * -input.SideHillCompFactor;

        if (output.SteerAngle < -input.MaxSteerAngle)
            output.SteerAngle = -input.MaxSteerAngle;
        else if (output.SteerAngle > input.MaxSteerAngle)
            output.SteerAngle = input.MaxSteerAngle;

        output.ModeActualXTE = output.DistanceFromCurrentLinePivot;

        output.GuidanceLineDistanceOff = (short)Math.Round(output.DistanceFromCurrentLinePivot * 1000.0, MidpointRounding.AwayFromZero);
        output.GuidanceLineSteerAngle = (short)(output.SteerAngle * 100);
    }

    #endregion

    private static double DistanceSquared(Vec3 pt1, Vec3 pt2)
    {
        double dEast = pt1.Easting - pt2.Easting;
        double dNorth = pt1.Northing - pt2.Northing;
        return (dEast * dEast) + (dNorth * dNorth);
    }

    private static double DistanceSquared(double northing1, double easting1, double northing2, double easting2)
    {
        double dNorth = northing1 - northing2;
        double dEast = easting1 - easting2;
        return (dNorth * dNorth) + (dEast * dEast);
    }

    private static double Distance(Vec3 pt1, Vec3 pt2) => Math.Sqrt(DistanceSquared(pt1, pt2));

    private static double Distance(Vec2 pt1, Vec3 pt2)
    {
        double dEast = pt1.Easting - pt2.Easting;
        double dNorth = pt1.Northing - pt2.Northing;
        return Math.Sqrt((dEast * dEast) + (dNorth * dNorth));
    }

    private static double ToDegrees(double radians) => radians * (180.0 / Math.PI);
}
