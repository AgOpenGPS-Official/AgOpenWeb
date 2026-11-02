// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Base;

namespace AgOpenWeb.Services.Tests.Guidance;

/// <summary>
/// AgOpenGPS's contour and U-turn steering, ported line for line from the snapshot in
/// <c>AgOpen_Snapshot/</c> (2026-01-24): the Stanley branch of <c>CContour.DistanceFromContourLine</c>,
/// <c>AgOpenGPS.Core/Services/Guidance/ContourPurePursuitGuidanceService.cs</c> and
/// <c>AgOpenGPS.Core/Services/YouTurn/YouTurnGuidanceService.cs</c>. It is the oracle the parity
/// tests compare <see cref="Contour.ContourGuidance"/> and <see cref="YouTurn.YouTurnGuidanceService"/>
/// with, so it keeps AgOpenGPS's names, order and arithmetic; only the vector types are ours.
/// Don't tidy it: a difference from the snapshot is a difference from AgOpenGPS.
/// </summary>
internal static class AgOpenGpsContourAndTurnOracle
{
    private const double PIBy2 = Math.PI / 2.0;
    private const double TwoPI = Math.PI * 2.0;

    #region Contour, Stanley (CContour.DistanceFromContourLine)

    /// <summary>Returns (steerAngleCT, distanceFromCurrentLinePivot), or null where AgOpenGPS returns early.</summary>
    public static (double steerAngle, double distance)? ContourStanley(List<Vec3> ctList, Vec3 pivot, Vec3 steer,
        double stanleyHeadingErrorGain, double stanleyDistanceErrorGain, double maxSteerAngle, double avgSpeed, bool isReverse)
    {
        double minDistA = 1000000, minDistB = 1000000;
        int ptCount = ctList.Count;
        int A = 0, B = 0, C;
        if (ptCount > 8)
        {
            for (int t = 0; t < ptCount; t++)
            {
                double dist = ((steer.Easting - ctList[t].Easting) * (steer.Easting - ctList[t].Easting))
                                + ((steer.Northing - ctList[t].Northing) * (steer.Northing - ctList[t].Northing));
                if (dist < minDistA)
                {
                    minDistB = minDistA;
                    B = A;
                    minDistA = dist;
                    A = t;
                }
                else if (dist < minDistB)
                {
                    minDistB = dist;
                    B = t;
                }
            }

            if (A > B) { C = A; A = B; B = C; }

            double dx = ctList[B].Easting - ctList[A].Easting;
            double dy = ctList[B].Northing - ctList[A].Northing;

            if (Math.Abs(dx) < double.Epsilon && Math.Abs(dy) < double.Epsilon) return null;

            double distanceFromCurrentLinePivot = ((dy * steer.Easting) - (dx * steer.Northing) + (ctList[B].Easting
                        * ctList[A].Northing) - (ctList[B].Northing * ctList[A].Easting))
                            / Math.Sqrt((dy * dy) + (dx * dx));

            double abHeading = Math.Atan2(dx, dy);
            if (abHeading < 0) abHeading += TwoPI;

            bool isHeadingSameWay = Math.PI - Math.Abs(Math.Abs(pivot.Heading - abHeading) - Math.PI) < PIBy2;

            double abFixHeadingDelta;
            if (isHeadingSameWay)
            {
                abFixHeadingDelta = (steer.Heading - abHeading);
            }
            else
            {
                distanceFromCurrentLinePivot *= -1.0;
                abFixHeadingDelta = (steer.Heading - abHeading + Math.PI);
            }

            if (abFixHeadingDelta > Math.PI) abFixHeadingDelta -= Math.PI;
            else if (abFixHeadingDelta < Math.PI) abFixHeadingDelta += Math.PI;

            if (abFixHeadingDelta > PIBy2) abFixHeadingDelta -= Math.PI;
            else if (abFixHeadingDelta < -PIBy2) abFixHeadingDelta += Math.PI;

            if (isReverse) abFixHeadingDelta *= -1;

            abFixHeadingDelta *= stanleyHeadingErrorGain;
            if (abFixHeadingDelta > 0.74) abFixHeadingDelta = 0.74;
            if (abFixHeadingDelta < -0.74) abFixHeadingDelta = -0.74;

            double steerAngleCT = Math.Atan((distanceFromCurrentLinePivot * stanleyDistanceErrorGain)
                / ((Math.Abs(avgSpeed) * 0.277777) + 1));

            if (steerAngleCT > 0.74) steerAngleCT = 0.74;
            if (steerAngleCT < -0.74) steerAngleCT = -0.74;

            steerAngleCT = ToDegrees((steerAngleCT + abFixHeadingDelta) * -1.0);

            if (steerAngleCT < -maxSteerAngle) steerAngleCT = -maxSteerAngle;
            if (steerAngleCT > maxSteerAngle) steerAngleCT = maxSteerAngle;

            return (steerAngleCT, distanceFromCurrentLinePivot);
        }
        return null;
    }

    #endregion

    #region Contour, Pure Pursuit (Core ContourPurePursuitGuidanceService)

    public sealed class ContourPurePursuitInput
    {
        public Vec3 PivotPosition, FixPosition;
        public List<Vec3> ContourPoints = new();
        public bool IsLocked;
        public double Wheelbase, MaxSteerAngle, PurePursuitIntegralGain, GoalPointDistance, SideHillCompFactor;
        public double FixHeading, AvgSpeed;
        public bool IsReverse, IsAutoSteerOn, IsYouTurnTriggered;
        public double ImuRoll = 88888;
        public double PreviousIntegral, PreviousPivotDistanceError, PreviousPivotDistanceErrorLast;
        public int PreviousCounter;
    }

    public sealed class ContourPurePursuitOutput
    {
        public double SteerAngle, DistanceFromCurrentLinePivot, REast, RNorth;
        public short GuidanceLineDistanceOff, GuidanceLineSteerAngle;
        public Vec2 GoalPoint;
        public bool IsHeadingSameWay, IsLocked;
        public double ModeActualXTE;
        public double Integral, PivotDistanceError, PivotDistanceErrorLast, PivotDerivative;
        public int Counter;
    }

    public static ContourPurePursuitOutput CalculateGuidanceContour(ContourPurePursuitInput input)
    {
        var output = new ContourPurePursuitOutput { IsLocked = input.IsLocked };

        if (input.ContourPoints == null || input.ContourPoints.Count <= 8)
        {
            output.DistanceFromCurrentLinePivot = 0;
            output.GuidanceLineDistanceOff = 0;
            return output;
        }

        int ptCount = input.ContourPoints.Count;

        double minDistA = 1000000, minDistB = 1000000;
        int A = 0, B = 0, C;

        for (int t = 0; t < ptCount; t++)
        {
            double dist = DistanceSquared(input.PivotPosition, input.ContourPoints[t]);
            if (dist < minDistA)
            {
                minDistB = minDistA;
                B = A;
                minDistA = dist;
                A = t;
            }
            else if (dist < minDistB)
            {
                minDistB = dist;
                B = t;
            }
        }

        if (A > B)
        {
            C = A;
            A = B;
            B = C;
        }

        if (output.IsLocked && (A < 2 || B > ptCount - 3))
        {
            output.IsLocked = false;
            return output;
        }

        double dx = input.ContourPoints[B].Easting - input.ContourPoints[A].Easting;
        double dy = input.ContourPoints[B].Northing - input.ContourPoints[A].Northing;

        if (Math.Abs(dx) < double.Epsilon && Math.Abs(dy) < double.Epsilon)
        {
            output.DistanceFromCurrentLinePivot = 0;
            output.GuidanceLineDistanceOff = 0;
            return output;
        }

        output.DistanceFromCurrentLinePivot = ((dy * input.FixPosition.Easting) - (dx * input.FixPosition.Northing)
            + (input.ContourPoints[B].Easting * input.ContourPoints[A].Northing)
            - (input.ContourPoints[B].Northing * input.ContourPoints[A].Easting))
            / Math.Sqrt((dy * dy) + (dx * dx));

        if (input.PurePursuitIntegralGain != 0)
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
                && input.AvgSpeed > 2.5
                && !input.IsYouTurnTriggered)
            {
                if ((input.PreviousIntegral < 0 && output.DistanceFromCurrentLinePivot < 0)
                    || (input.PreviousIntegral > 0 && output.DistanceFromCurrentLinePivot > 0))
                {
                    output.Integral = input.PreviousIntegral + output.PivotDistanceError * input.PurePursuitIntegralGain * -0.06;
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

        if (input.IsReverse)
            output.Integral = 0;

        output.IsHeadingSameWay = Math.PI - Math.Abs(Math.Abs(input.PivotPosition.Heading - input.ContourPoints[A].Heading) - Math.PI) < PIBy2;

        if (!output.IsHeadingSameWay)
            output.DistanceFromCurrentLinePivot *= -1.0;

        double U = (((input.PivotPosition.Easting - input.ContourPoints[A].Easting) * dx)
            + ((input.PivotPosition.Northing - input.ContourPoints[A].Northing) * dy))
            / ((dx * dx) + (dy * dy));

        output.REast = input.ContourPoints[A].Easting + (U * dx);
        output.RNorth = input.ContourPoints[A].Northing + (U * dy);

        bool reverseHeading = input.IsReverse ? !output.IsHeadingSameWay : output.IsHeadingSameWay;
        int count = reverseHeading ? 1 : -1;
        Vec3 start = new Vec3(output.REast, output.RNorth, 0);
        double distSoFar = 0;

        for (int i = reverseHeading ? B : A; i < ptCount && i >= 0; i += count)
        {
            double tempDist = Distance(start, input.ContourPoints[i]);

            if ((tempDist + distSoFar) > input.GoalPointDistance)
            {
                double j = (input.GoalPointDistance - distSoFar) / tempDist;
                output.GoalPoint = new Vec2(
                    (((1 - j) * start.Easting) + (j * input.ContourPoints[i].Easting)),
                    (((1 - j) * start.Northing) + (j * input.ContourPoints[i].Northing)));
                break;
            }
            else
            {
                distSoFar += tempDist;
            }

            start = input.ContourPoints[i];
        }

        double goalPointDistanceSquared = DistanceSquared(
            output.GoalPoint.Northing, output.GoalPoint.Easting,
            input.PivotPosition.Northing, input.PivotPosition.Easting);

        double localHeading;
        if (output.IsHeadingSameWay)
            localHeading = TwoPI - input.FixHeading + output.Integral;
        else
            localHeading = TwoPI - input.FixHeading - output.Integral;

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

        output.ModeActualXTE = output.DistanceFromCurrentLinePivot;

        output.GuidanceLineDistanceOff = (short)Math.Round(output.DistanceFromCurrentLinePivot * 1000.0, MidpointRounding.AwayFromZero);
        output.GuidanceLineSteerAngle = (short)(output.SteerAngle * 100);

        return output;
    }

    #endregion

    #region U-turn (Core YouTurnGuidanceService)

    public sealed class YouTurnInput
    {
        public List<Vec3> TurnPath = new();
        public Vec3 PivotPosition, SteerPosition;
        public double Wheelbase, MaxSteerAngle;
        public bool UseStanley;
        public double StanleyHeadingErrorGain, StanleyDistanceErrorGain, GoalPointDistance, UTurnCompensation;
        public double FixHeading, AvgSpeed;
        public bool IsReverse;
        public int UTurnStyle;
    }

    public sealed class YouTurnOutput
    {
        public bool IsTurnComplete;
        public double SteerAngle, DistanceFromCurrentLine, REast, RNorth, PPRadius;
        public Vec2 GoalPoint, RadiusPoint;
        public int PointA, PointB;
        public short GuidanceLineDistanceOff, GuidanceLineSteerAngle;
        public double ModeActualXTE;
        public int PathCount;
    }

    public static YouTurnOutput CalculateGuidance(YouTurnInput input)
    {
        var output = new YouTurnOutput();

        int ptCount = input.TurnPath.Count;
        if (ptCount == 0)
        {
            output.IsTurnComplete = true;
            return output;
        }

        if (input.UseStanley)
            CalculateStanleyGuidance(input, output, ptCount);
        else
            CalculatePurePursuitGuidance(input, output, ptCount);

        return output;
    }

    private static void CalculateStanleyGuidance(YouTurnInput input, YouTurnOutput output, int ptCount)
    {
        Vec3 pivot = input.SteerPosition;

        double minDistA = double.MaxValue;
        double minDistB = double.MaxValue;
        int A = 0, B = 0;

        for (int t = 0; t < ptCount; t++)
        {
            double dist = ((pivot.Easting - input.TurnPath[t].Easting) * (pivot.Easting - input.TurnPath[t].Easting))
                            + ((pivot.Northing - input.TurnPath[t].Northing) * (pivot.Northing - input.TurnPath[t].Northing));
            if (dist < minDistA)
            {
                minDistB = minDistA;
                B = A;
                minDistA = dist;
                A = t;
            }
            else if (dist < minDistB)
            {
                minDistB = dist;
                B = t;
            }
        }

        if (minDistA > 16)
        {
            output.IsTurnComplete = true;
            return;
        }

        if (A > B)
        {
            (B, A) = (A, B);
        }

        if (A < 0) A = 0;
        B = A + 1;

        if (B >= ptCount - 1)
        {
            output.IsTurnComplete = true;
            return;
        }

        if (input.UTurnStyle == 1 && input.IsReverse)
        {
            output.IsTurnComplete = true;
            return;
        }

        double dx = input.TurnPath[B].Easting - input.TurnPath[A].Easting;
        double dz = input.TurnPath[B].Northing - input.TurnPath[A].Northing;
        if (Math.Abs(dx) < double.Epsilon && Math.Abs(dz) < double.Epsilon)
        {
            output.IsTurnComplete = false;
            return;
        }

        double abHeading = input.TurnPath[A].Heading;

        double distanceFromCurrentLine = ((dz * pivot.Easting) - (dx * pivot.Northing) + (input.TurnPath[B].Easting
                    * input.TurnPath[A].Northing) - (input.TurnPath[B].Northing * input.TurnPath[A].Easting))
                        / Math.Sqrt((dz * dz) + (dx * dx));

        double U = (((pivot.Easting - input.TurnPath[A].Easting) * dx)
                    + ((pivot.Northing - input.TurnPath[A].Northing) * dz))
                    / ((dx * dx) + (dz * dz));

        double rEast = input.TurnPath[A].Easting + (U * dx);
        double rNorth = input.TurnPath[A].Northing + (U * dz);

        double abFixHeadingDelta = (pivot.Heading - abHeading);

        if (abFixHeadingDelta > Math.PI) abFixHeadingDelta -= Math.PI;
        else if (abFixHeadingDelta < Math.PI) abFixHeadingDelta += Math.PI;
        if (abFixHeadingDelta > PIBy2) abFixHeadingDelta -= Math.PI;
        else if (abFixHeadingDelta < -PIBy2) abFixHeadingDelta += Math.PI;

        if (input.IsReverse) abFixHeadingDelta *= -1;

        abFixHeadingDelta *= input.StanleyHeadingErrorGain;
        if (abFixHeadingDelta > 0.74) abFixHeadingDelta = 0.74;
        if (abFixHeadingDelta < -0.74) abFixHeadingDelta = -0.74;

        double steerAngle = Math.Atan((distanceFromCurrentLine * input.StanleyDistanceErrorGain) / ((input.AvgSpeed * 0.277777) + 1));

        if (steerAngle > 0.74) steerAngle = 0.74;
        if (steerAngle < -0.74) steerAngle = -0.74;

        steerAngle = ToDegrees((steerAngle + abFixHeadingDelta * input.UTurnCompensation) * -1.0);
        if (steerAngle < -input.MaxSteerAngle) steerAngle = -input.MaxSteerAngle;
        if (steerAngle > input.MaxSteerAngle) steerAngle = input.MaxSteerAngle;

        output.IsTurnComplete = false;
        output.DistanceFromCurrentLine = distanceFromCurrentLine;
        output.REast = rEast;
        output.RNorth = rNorth;
        output.SteerAngle = steerAngle;
        output.PointA = A;
        output.PointB = B;
        output.ModeActualXTE = distanceFromCurrentLine;
        output.GuidanceLineDistanceOff = (short)Math.Round(distanceFromCurrentLine * 1000.0, MidpointRounding.AwayFromZero);
        output.GuidanceLineSteerAngle = (short)(steerAngle * 100);
        output.PathCount = ptCount - B;
    }

    private static void CalculatePurePursuitGuidance(YouTurnInput input, YouTurnOutput output, int ptCount)
    {
        Vec3 pivot = input.PivotPosition;

        double minDistA = double.MaxValue;
        double minDistB = double.MaxValue;
        int A = 0, B = 0;

        for (int t = 0; t < ptCount; t++)
        {
            double dist = ((pivot.Easting - input.TurnPath[t].Easting) * (pivot.Easting - input.TurnPath[t].Easting))
                            + ((pivot.Northing - input.TurnPath[t].Northing) * (pivot.Northing - input.TurnPath[t].Northing));
            if (dist < minDistA)
            {
                minDistB = minDistA;
                B = A;
                minDistA = dist;
                A = t;
            }
            else if (dist < minDistB)
            {
                minDistB = dist;
                B = t;
            }
        }

        if (A > B)
        {
            (B, A) = (A, B);
        }

        double distancePiv = Distance(input.TurnPath[A], pivot);

        if ((A > 0 && distancePiv > 2) || (B >= ptCount - 1))
        {
            output.IsTurnComplete = true;
            return;
        }

        double dx = input.TurnPath[B].Easting - input.TurnPath[A].Easting;
        double dz = input.TurnPath[B].Northing - input.TurnPath[A].Northing;

        if (Math.Abs(dx) < double.Epsilon && Math.Abs(dz) < double.Epsilon)
        {
            output.IsTurnComplete = false;
            return;
        }

        double distanceFromCurrentLine = ((dz * pivot.Easting) - (dx * pivot.Northing) + (input.TurnPath[B].Easting
                    * input.TurnPath[A].Northing) - (input.TurnPath[B].Northing * input.TurnPath[A].Easting))
                        / Math.Sqrt((dz * dz) + (dx * dx));

        double U = (((pivot.Easting - input.TurnPath[A].Easting) * dx)
                    + ((pivot.Northing - input.TurnPath[A].Northing) * dz))
                    / ((dx * dx) + (dz * dz));

        double rEast = input.TurnPath[A].Easting + (U * dx);
        double rNorth = input.TurnPath[A].Northing + (U * dz);

        double goalPointDistance = input.GoalPointDistance;

        bool isHeadingSameWay = true;
        bool reverseHeading = !input.IsReverse;

        int count = reverseHeading ? 1 : -1;
        Vec3 start = new Vec3(rEast, rNorth, 0);
        double distSoFar = 0;
        Vec2 goalPoint = new Vec2();

        for (int i = reverseHeading ? B : A; i < ptCount && i >= 0; i += count)
        {
            double tempDist = Distance(start, input.TurnPath[i]);

            if ((tempDist + distSoFar) > goalPointDistance)
            {
                double j = (goalPointDistance - distSoFar) / tempDist;

                goalPoint.Easting = (((1 - j) * start.Easting) + (j * input.TurnPath[i].Easting));
                goalPoint.Northing = (((1 - j) * start.Northing) + (j * input.TurnPath[i].Northing));
                break;
            }
            else distSoFar += tempDist;

            start = input.TurnPath[i];

            if (i == ptCount - 1)
            {
                output.IsTurnComplete = true;
                return;
            }

            if (input.UTurnStyle == 1 && input.IsReverse)
            {
                output.IsTurnComplete = true;
                return;
            }
        }

        double goalPointDistanceSquared = DistanceSquared(goalPoint.Northing, goalPoint.Easting, pivot.Northing, pivot.Easting);

        double localHeading = TwoPI - input.FixHeading;
        double ppRadius = goalPointDistanceSquared / (2 * (((goalPoint.Easting - pivot.Easting) * Math.Cos(localHeading)) + ((goalPoint.Northing - pivot.Northing) * Math.Sin(localHeading))));

        double steerAngle = ToDegrees(Math.Atan(2 * (((goalPoint.Easting - pivot.Easting) * Math.Cos(localHeading))
            + ((goalPoint.Northing - pivot.Northing) * Math.Sin(localHeading))) * input.Wheelbase / goalPointDistanceSquared));

        steerAngle *= input.UTurnCompensation;

        if (steerAngle < -input.MaxSteerAngle) steerAngle = -input.MaxSteerAngle;
        if (steerAngle > input.MaxSteerAngle) steerAngle = input.MaxSteerAngle;

        if (ppRadius < -500) ppRadius = -500;
        if (ppRadius > 500) ppRadius = 500;

        Vec2 radiusPoint = new Vec2(
            pivot.Easting + (ppRadius * Math.Cos(localHeading)),
            pivot.Northing + (ppRadius * Math.Sin(localHeading)));

        if (!isHeadingSameWay)
            distanceFromCurrentLine *= -1.0;

        output.IsTurnComplete = false;
        output.DistanceFromCurrentLine = distanceFromCurrentLine;
        output.REast = rEast;
        output.RNorth = rNorth;
        output.SteerAngle = steerAngle;
        output.GoalPoint = goalPoint;
        output.RadiusPoint = radiusPoint;
        output.PPRadius = ppRadius;
        output.PointA = A;
        output.PointB = B;
        output.ModeActualXTE = distanceFromCurrentLine;
        output.GuidanceLineDistanceOff = (short)Math.Round(distanceFromCurrentLine * 1000.0, MidpointRounding.AwayFromZero);
        output.GuidanceLineSteerAngle = (short)(steerAngle * 100);
        output.PathCount = ptCount - B;
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

    private static double ToDegrees(double radians) => radians * (180.0 / Math.PI);
}
