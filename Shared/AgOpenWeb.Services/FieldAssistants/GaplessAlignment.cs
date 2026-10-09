using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
#nullable disable
using System;using System.Collections.Generic;using System.Globalization;using System.Linq;
namespace AgOpenWeb.Services.FieldAssistants;
public sealed record GaplessInput(string FieldKey,string TrackKey,Vec2 LineStart,double Heading,bool HeadingSameWay,double ToolWidth,double ToolOffset,IReadOnlyList<BoundaryPolygon> Boundaries,bool HasCoverage,Func<Vec2,bool> IsCovered)
{
    // Stable selection for offer/dismissal; TrackKey still validates the exact line.
    public string SelectionKey { get; init; }
}
/// <summary>AOG gapless thresholds and analysis, with native coverage membership supplied by the host.</summary>
public sealed class GaplessAlignment(GaplessInput input)
{
private const double GaplessSafetyOverlapM=.10;
private sealed record CoverageSpatialIndex(bool HasCoverage,Func<Vec2,bool> IsCovered) {public int Count=>HasCoverage?1:0;public bool Contains(Vec2 point)=>IsCovered(point);}
public GaplessAbResult Analyze()
{
if(input.Boundaries.Count==0||input.Boundaries[0].Points.Count<3||!double.IsFinite(input.Heading)||!double.IsFinite(input.ToolWidth)||input.ToolWidth is <.5 or >100) return GaplessAbResult.Fail("Field boundary and a valid machine width required");
CoverageSpatialIndex coverage = new(input.HasCoverage, input.IsCovered);
            if (coverage.Count == 0)
                return GaplessAbResult.Fail("No mapped coverage. Record working passes before analyzing the AB line.");

            Vec2 origin = input.LineStart;
            double heading = input.Heading;
            Vec2 along = new Vec2(Math.Sin(heading), Math.Cos(heading));
            Vec2 right = new Vec2(Math.Cos(heading), -Math.Sin(heading));
            double implementOffset = input.HeadingSameWay ? input.ToolOffset : -input.ToolOffset;

            List<Vec2> outer = BoundaryPoints(input.Boundaries[0]);
            double minAlong = double.MaxValue, maxAlong = double.MinValue;
            foreach (Vec2 point in outer)
            {
                double projection = Dot(Subtract(point, origin), along);
                minAlong = Math.Min(minAlong, projection);
                maxAlong = Math.Max(maxAlong, projection);
            }
            if (maxAlong - minAlong < 10)
                return GaplessAbResult.Fail("The AB segment inside the field is too short to analyze.");
            if(!double.IsFinite(minAlong)||!double.IsFinite(maxAlong)||maxAlong-minAlong>30000)
                return GaplessAbResult.Fail("The boundary exceeds the 30 km analysis limit.");

            const double longitudinalStep = 1.0;
            double lateralStep = Math.Max(0.08, Math.Min(0.18, input.ToolWidth / 60.0));
            double searchDistance = Math.Max(input.ToolWidth * 1.75, input.ToolWidth * 0.5 + 3.0);
            var samples = new List<GaplessSample>();
            for (double distance = minAlong; distance <= maxAlong; distance += longitudinalStep)
            {
                Vec2 guidePoint = Add(origin, Scale(along, distance));
                Vec2 implementCenter = Add(guidePoint, Scale(right, implementOffset));
                if (!IsInsideWorkableField(implementCenter)) continue;
                samples.Add(new GaplessSample
                {
                    AlongM = distance,
                    LeftCoverageM = FindCoverageStart(coverage, implementCenter, right, -1, searchDistance, lateralStep),
                    RightCoverageM = FindCoverageStart(coverage, implementCenter, right, 1, searchDistance, lateralStep)
                });
            }

            if (samples.Count < 15)
                return GaplessAbResult.Fail("Too little of the AB line lies inside the field to calculate a shift.");

            int leftCount = samples.Count(sample => sample.LeftCoverageM.HasValue);
            int rightCount = samples.Count(sample => sample.RightCoverageM.HasValue);
            int requiredCount = Math.Max(12, (int)Math.Ceiling(samples.Count * 0.70));
            if (leftCount < requiredCount && rightCount < requiredCount)
                return GaplessAbResult.Fail(
                    "No continuous mapped strip along at least 70% of the line. Record more coverage.");

            if (leftCount >= requiredCount && rightCount >= requiredCount &&
                Math.Abs(leftCount - rightCount) < samples.Count * 0.15)
                return GaplessAbResult.Fail(
                    "Coverage is present on both sides of the line. Use manual alignment.");

            int side = rightCount > leftCount ? 1 : -1;
            List<GaplessSample> chosen = samples.Where(sample =>
                side > 0 ? sample.RightCoverageM.HasValue : sample.LeftCoverageM.HasValue).ToList();
            int longestMissingRun = LongestInternalMissingRun(samples, side);
            double toleratedMappingGapM = Math.Max(12.0, input.ToolWidth * 2.0);
            if (longestMissingRun * longitudinalStep > toleratedMappingGapM)
                return GaplessAbResult.Fail(
                    "A long gap interrupts the mapped edge. Check coverage or align the line manually.");

            double halfWidth = input.ToolWidth * 0.5;
            double requiredTowardCoverage = chosen.Max(sample =>
                (side > 0 ? sample.RightCoverageM.Value : sample.LeftCoverageM.Value) +
                GaplessSafetyOverlapM - halfWidth);
            double signedShift = side * requiredTowardCoverage;
            double nudgeCommand = input.HeadingSameWay ? signedShift : -signedShift;
            if (Math.Abs(nudgeCommand) > input.ToolWidth * 0.80)
                return GaplessAbResult.Fail(
                    "The proposed shift exceeds 80% of the tool width. Check the AB line and mapped strip.");

            double edgeDistance = halfWidth + requiredTowardCoverage;
            double minimumOverlap = chosen.Min(sample => edgeDistance -
                (side > 0 ? sample.RightCoverageM.Value : sample.LeftCoverageM.Value));
            double maximumOverlap = chosen.Max(sample => edgeDistance -
                (side > 0 ? sample.RightCoverageM.Value : sample.LeftCoverageM.Value));
            double analyzedLength = chosen.Count * longitudinalStep;
            string direction = nudgeCommand >= 0 ? "right" : "left";

            if (Math.Abs(nudgeCommand) < 0.02)
            {
                return new GaplessAbResult { Success=true,AlreadyAligned=true,
                    Message="No missed strip detected on this AB line",MinimumOverlapM=minimumOverlap,
                    MaximumOverlapM=maximumOverlap,AnalyzedLengthM=analyzedLength,SampleCount=chosen.Count };
            }

            return new GaplessAbResult
            {
                Success = true,
                NudgeCommandM = nudgeCommand,
                Direction = direction,
                MinimumOverlapM = minimumOverlap,
                MaximumOverlapM = maximumOverlap,
                AnalyzedLengthM = analyzedLength,
                SampleCount = chosen.Count,
                Message = ""
            };
        }
private double? FindCoverageStart(CoverageSpatialIndex coverage, Vec2 center, Vec2 right, int side,
            double maximumDistance, double step)
        {
            for (double distance = 0; distance <= maximumDistance; distance += step)
            {
                Vec2 point = Add(center, Scale(right, side * distance));
                if (!coverage.Contains(point)) continue;
                // Odrzuca pojedynczy piksel lub bardzo wąski klin powstały z błędu GPS.
                Vec2 deeper = Add(center, Scale(right, side * (distance + 0.24)));
                if (coverage.Contains(deeper)) return distance;
            }
            return null;
        }
private bool IsInsideWorkableField(Vec2 point)
        {
            if (!PointInPolygon(BoundaryPoints(input.Boundaries[0]), point)) return false;
            for (int i = 1; i < input.Boundaries.Count; i++)
                if (PointInPolygon(BoundaryPoints(input.Boundaries[i]), point)) return false;
            return true;
        }
private static List<Vec2> BoundaryPoints(BoundaryPolygon boundary)
        {
            return boundary?.Points?.Select(point => new Vec2(point.Easting, point.Northing)).ToList()
                   ?? new List<Vec2>();
        }
private static bool PointInPolygon(IList<Vec2> polygon, Vec2 point)
        {
            if (polygon == null || polygon.Count < 3) return false;
            bool inside = false;
            for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            {
                Vec2 a = polygon[i], b = polygon[j];
                bool crosses = (a.Northing > point.Northing) != (b.Northing > point.Northing) &&
                    point.Easting < (b.Easting - a.Easting) * (point.Northing - a.Northing) /
                    (b.Northing - a.Northing) + a.Easting;
                if (crosses) inside = !inside;
            }
            return inside;
        }
private static int LongestInternalMissingRun(IList<GaplessSample> samples, int side)
        {
            int firstCovered = -1, lastCovered = -1;
            for (int i = 0; i < samples.Count; i++)
            {
                bool present = side > 0 ? samples[i].RightCoverageM.HasValue : samples[i].LeftCoverageM.HasValue;
                if (!present) continue;
                if (firstCovered < 0) firstCovered = i;
                lastCovered = i;
            }

            if (firstCovered < 0 || lastCovered <= firstCovered) return 0;
            int current = 0, longest = 0;
            for (int i = firstCovered; i <= lastCovered; i++)
            {
                GaplessSample sample = samples[i];
                bool present = side > 0 ? sample.RightCoverageM.HasValue : sample.LeftCoverageM.HasValue;
                if (present) current = 0;
                else longest = Math.Max(longest, ++current);
            }
            return longest;
        }
private static string FormatDistanceCm(double metres) =>
            Math.Abs(metres * 100.0).ToString("0", CultureInfo.CurrentCulture) + " cm";

        private static string FormatCentimetres(double metres) =>
            Math.Max(0, metres * 100.0).ToString("0", CultureInfo.CurrentCulture) + " cm";

        private static Vec2 Add(Vec2 a, Vec2 b) => new Vec2(a.Easting + b.Easting, a.Northing + b.Northing);
        private static Vec2 Subtract(Vec2 a, Vec2 b) => new Vec2(a.Easting - b.Easting, a.Northing - b.Northing);
        private static Vec2 Scale(Vec2 value, double scale) => new Vec2(value.Easting * scale, value.Northing * scale);
        private static double Dot(Vec2 a, Vec2 b) => a.Easting * b.Easting + a.Northing * b.Northing;


private sealed class GaplessSample
        {
            public double AlongM { get; set; }
            public double? LeftCoverageM { get; set; }
            public double? RightCoverageM { get; set; }
        }
public sealed class GaplessAbResult
        {
            public bool Success { get; set; }
            public bool AlreadyAligned { get; set; }
            public string Message { get; set; }
            public double NudgeCommandM { get; set; }
            public string Direction { get; set; }
            public double MinimumOverlapM { get; set; }
            public double MaximumOverlapM { get; set; }
            public double AnalyzedLengthM { get; set; }
            public int SampleCount { get; set; }

            public static GaplessAbResult Fail(string message) => new GaplessAbResult { Message = message };
            public static GaplessAbResult Aligned(string message) =>
                new GaplessAbResult { Success = true, AlreadyAligned = true, Message = message };
        }
}
