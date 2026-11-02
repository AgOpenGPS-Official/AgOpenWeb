// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Xml;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// AgOpenGPS's "From ISO-XML" ported line for line as a test oracle: FormFieldISOXML.cs
/// (the field list and btnBuildFields_Click), GPS/Protocols/ISOBUS/IsoXmlParserHelpers.cs,
/// CurveCABTools.cs, and the fence area and winding of AgOpenGPS.Core's FenceLineService. Taken
/// from AgOpenGPS develop at 64984a3d (2026-10-23), not from the 2026-01-24 snapshot: the
/// snapshot's AgOpenGPS.Core copy of the parser builds every point as Vec3(northing, easting),
/// which transposes the field (the Core Vec3 is (easting, northing, heading)); the WinForms
/// parser on develop is the one AgOpenGPS runs. AgOpenGPS's names are kept; the vector types
/// are ours. Not tidied.
/// </summary>
public static class AgOpenGpsIsoXmlOracle
{
    public enum TrackMode { None = 0, AB = 2, Curve = 4 }

    public class CBoundaryList
    {
        public List<Vec3> fenceLine = new();
        public List<Vec3> hdLine = new();
        public double area;

        //obvious
        public bool CalculateFenceArea(int idx)
        {
            if (fenceLine.Count < 1) return false;
            fenceLine = CalculateAreaAndFixWinding(fenceLine, idx, out area);
            return true;
        }
    }

    public class CTrk
    {
        public string name = "";
        public double heading;
        public TrackMode mode;
        public Vec2 ptA, ptB;
        public List<Vec3> curvePts = new();
    }

    public class ApplicationModel
    {
        public LocalPlane LocalPlane = null!;
    }

    /// <summary>What btnBuildFields_Click leaves in the app for the chosen PFD.</summary>
    public sealed class Field
    {
        public Wgs84 origin;
        public List<CBoundaryList> bndList = new();
        public List<CTrk> gArr = new();
    }

    // ───────────── FormFieldISOXML.cs ─────────────

    /// <summary>The tree's field labels: FormFieldIsoXml_Load, one per PFD.</summary>
    public static List<string> FieldLabels(XmlDocument iso)
    {
        var labels = new List<string>();
        var pfd = iso.GetElementsByTagName("PFD");
        const double SqmToHectares = 0.0001;
        foreach (XmlNode nodePFD in pfd)
        {
            // 1) Get area in m² (either from attribute D or fallback estimate)
            double areaSqm;
            if (double.TryParse(nodePFD.Attributes!["D"]?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double areaRaw) && areaRaw >= 1)
            {
                areaSqm = areaRaw; // still in m²
            }
            else
            {
                areaSqm = EstimateAreaFromPln(nodePFD); // still in m²
            }
            // 2) Convert to hectares and round to 2 decimals
            double areaHa = Math.Round(areaSqm * SqmToHectares, 2, MidpointRounding.AwayFromZero);
            // 3) Build label (always show 2 decimals)
            string fieldName = nodePFD.Attributes["C"]?.Value ?? "Unnamed";
            string fieldLabel = $"{fieldName} Area: {areaHa:0.00} Ha";
            labels.Add(fieldLabel);
        }
        return labels;
    }

    private static double EstimateAreaFromPln(XmlNode nodePfd)
    {
        // Find PLN with type "1" (outer boundary)
        foreach (XmlNode nodePln in nodePfd.SelectNodes("PLN")!)
        {
            if (nodePln.Attributes!["A"]?.Value != "1") continue;
            XmlNode? lsg = nodePln.SelectSingleNode("LSG[@A='1']");
            if (lsg == null) continue;
            var pts = lsg.SelectNodes("PNT")!;
            if (pts.Count < 3) continue;
            // The form's appModel.LocalPlane is the plane of whatever field is open; the area
            // doesn't depend on where the plane sits, so this plane is on the ring's first point.
            double.TryParse(pts[0]!.Attributes!["C"]?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double lat0);
            double.TryParse(pts[0]!.Attributes["D"]?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double lon0);
            var plane = new LocalPlane(new Wgs84(lat0, lon0), new SharedFieldProperties());
            var vecs = new Vec2[pts.Count];
            for (int i = 0; i < pts.Count; i++)
            {
                double lat, lon;
                if (!double.TryParse(pts[i]!.Attributes!["C"]?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out lat)) continue;
                if (!double.TryParse(pts[i]!.Attributes["D"]?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out lon)) continue;
                GeoCoord geo = plane.ConvertWgs84ToGeoCoord(new Wgs84(lat, lon));
                vecs[i] = new Vec2(geo.Easting, geo.Northing);
            }
            // Shoelace formula to compute area (in m²)
            double area = 0;
            for (int i = 0, j = vecs.Length - 1; i < vecs.Length; j = i++)
            {
                area += (vecs[j].Easting + vecs[i].Easting) * (vecs[j].Northing - vecs[i].Northing);
            }
            return Math.Abs(area / 2.0); // m²
        }
        return 0.0;
    }

    /// <summary>btnBuildFields_Click for the PFD at <paramref name="idxFieldSelected"/>; null where
    /// AgOpenGPS stops with "Can't calculate center of field. Missing Outer Boundary or AB line."</summary>
    public static Field? Build(XmlDocument iso, int idxFieldSelected)
    {
        var pfd = iso.GetElementsByTagName("PFD");
        var fieldParts = pfd[idxFieldSelected]!.ChildNodes;
        if (!TryExtractOrigin(fieldParts, out Wgs84 _origin))
        {
            return null;
        }
        var mf = new Field { origin = _origin };
        // mf.pn.DefineLocalPlane(_origin, true);
        var appModel = new ApplicationModel { LocalPlane = new LocalPlane(_origin, new SharedFieldProperties()) };
        List<CBoundaryList> boundaries = ParseBoundaries(fieldParts, appModel);
        // Calculate area for all boundaries first to enable sorting
        foreach (var bnd in boundaries)
        {
            bnd.CalculateFenceArea(0); // Calculate area with temporary index
        }
        // Sort by area descending (largest first)
        boundaries.Sort((a, b) => b.area.CompareTo(a.area));
        foreach (var bnd in boundaries)
        {
            mf.bndList.Add(bnd);
            int idx = mf.bndList.Count - 1;
            bnd.CalculateFenceArea(idx);
            // bnd.FixFenceLine(idx): the fence resampled to AgOpenGPS's spacing; AgOpenWeb
            // normalises its boundaries its own way on open, so the raw ring is what is compared.
        }
        List<Vec3> headland = ParseHeadland(fieldParts, appModel);
        if (headland.Count > 0 && mf.bndList.Count > 0 && mf.bndList[0].hdLine.Count == 0)
        {
            mf.bndList[0].hdLine.AddRange(headland);
        }
        List<CTrk> guidanceLines = ParseAllGuidanceLines(fieldParts, appModel);
        mf.gArr.AddRange(guidanceLines);
        return mf;
    }

    // ───────────── IsoXmlParserHelpers.cs ─────────────

    // Extract WGS84 origin from PLN or GGP lines
    public static bool TryExtractOrigin(XmlNodeList fieldParts, out Wgs84 origin)
    {
        double latSum = 0, lonSum = 0;
        int count = 0;
        foreach (XmlNode node in fieldParts)
        {
            if (node.Name == "PLN" && node.Attributes!["A"]?.Value == "1")
            {
                AccumulateCoordinates(node, ref latSum, ref lonSum, ref count);
            }
        }
        if (count == 0)
        {
            foreach (XmlNode node in fieldParts)
            {
                if (node.Name == "GGP")
                {
                    var lsg = node.SelectSingleNode("GPN/LSG");
                    if (lsg != null) AccumulateCoordinates(lsg.ParentNode!, ref latSum, ref lonSum, ref count);
                }
            }
        }
        if (count == 0)
        {
            origin = new Wgs84();
            return false;
        }
        origin = new Wgs84(latSum / count, lonSum / count);
        return true;
    }

    // Parse PLN boundaries into CBoundaryList objects
    public static List<CBoundaryList> ParseBoundaries(XmlNodeList fieldParts, ApplicationModel appModel)
    {
        List<CBoundaryList> boundaries = new List<CBoundaryList>();
        bool outerBuilt = false;
        foreach (XmlNode node in fieldParts)
        {
            if (node.Name != "PLN") continue;
            string? type = node.Attributes!["A"]?.Value;
            if ((type == "1" || type == "9") && !outerBuilt)
            {
                if (node.SelectSingleNode("LSG[@A='1']") is XmlNode lsg)
                {
                    boundaries.Add(ParseBoundaryFromLSG(lsg, appModel));
                    outerBuilt = true;
                }
            }
            else if (type == "3" || type == "4" || type == "6")
            {
                // Inner boundaries can use LSG type 1 (exterior) or 2 (interior)
                XmlNode? lsg = node.SelectSingleNode("LSG[@A='1']")
                            ?? node.SelectSingleNode("LSG[@A='2']");
                if (lsg is XmlNode validLsg)
                {
                    boundaries.Add(ParseBoundaryFromLSG(validLsg, appModel));
                }
            }
        }
        return boundaries;
    }

    // Parse Headland if available
    public static List<Vec3> ParseHeadland(XmlNodeList fieldParts, ApplicationModel appModel)
    {
        foreach (XmlNode node in fieldParts)
        {
            if (node.Name == "PLN" && node.Attributes!["A"]?.Value == "10")
            {
                if (node.SelectSingleNode("LSG[@A='1']") is XmlNode lsg)
                {
                    var list = new List<Vec3>();
                    foreach (XmlNode pnt in lsg.SelectNodes("PNT")!)
                    {
                        if (double.TryParse(pnt.Attributes!["C"]?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double lat) &&
                            double.TryParse(pnt.Attributes["D"]?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double lon))
                        {
                            GeoCoord geo = appModel.LocalPlane.ConvertWgs84ToGeoCoord(new Wgs84(lat, lon));
                            list.Add(new Vec3(geo));
                        }
                    }
                    CalculateHeadings(list);
                    return list;
                }
            }
        }
        return new List<Vec3>();
    }

    // Parse all valid guidance lines
    public static List<CTrk> ParseAllGuidanceLines(XmlNodeList fieldParts, ApplicationModel appModel)
    {
        List<CTrk> tracks = new List<CTrk>();
        foreach (XmlNode node in fieldParts)
        {
            if (node.Name == "GGP")
            {
                var trk = ParseGGPNode(node, appModel);
                if (trk != null) tracks.Add(trk);
            }
            else if (node.Name == "LSG" && node.Attributes!["A"]?.Value == "5")
            {
                var trk = ParseLSGNode(node, appModel);
                if (trk != null) tracks.Add(trk);
            }
        }
        return tracks;
    }

    public static CBoundaryList ParseBoundaryFromLSG(XmlNode lsg, ApplicationModel appModel)
    {
        var list = new CBoundaryList();
        foreach (XmlNode pnt in lsg.SelectNodes("PNT")!)
        {
            if (double.TryParse(pnt.Attributes!["C"]?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double lat) &&
                double.TryParse(pnt.Attributes["D"]?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double lon))
            {
                GeoCoord geo = appModel.LocalPlane.ConvertWgs84ToGeoCoord(new Wgs84(lat, lon));
                list.fenceLine.Add(new Vec3(geo));
            }
        }
        return list;
    }

    // Parse GGP → GPN → LSG line
    private static CTrk? ParseGGPNode(XmlNode node, ApplicationModel appModel)
    {
        var gpn = node.SelectSingleNode("GPN");
        var lsg = gpn?.SelectSingleNode("LSG[@A='5']");
        if (gpn == null || lsg == null) return null;
        string? lineType = gpn.Attributes!["C"]?.Value;
        string name = gpn.Attributes["B"]?.Value ?? node.Attributes!["B"]?.Value ?? "Unnamed";
        if (lineType == "1") return ParseABLine(lsg, name, appModel);
        else if (lineType == "3") return ParseCurveLine(lsg, name, appModel);
        return null;
    }

    // Parse LSG line directly
    private static CTrk? ParseLSGNode(XmlNode lsg, ApplicationModel appModel)
    {
        string name = lsg.Attributes!["B"]?.Value ?? "Unnamed";
        int count = lsg.SelectNodes("PNT")!.Count;
        if (count == 2) return ParseABLine(lsg, name, appModel);
        else if (count > 2) return ParseCurveLine(lsg, name, appModel);
        return null;
    }

    // Parse AB line into CTrk
    private static CTrk? ParseABLine(XmlNode lsg, string name, ApplicationModel appModel)
    {
        var points = lsg.SelectNodes("PNT")!;
        if (points.Count < 2) return null;
        var ptA = ParseVec2(points[0]!, appModel);
        var ptB = ParseVec2(points[1]!, appModel);
        double heading = Math.Atan2(ptB.Easting - ptA.Easting, ptB.Northing - ptA.Northing);
        if (heading < 0) heading += twoPI;
        return new CTrk
        {
            heading = heading,
            mode = TrackMode.AB,
            ptA = ptA,
            ptB = ptB,
            name = name.Trim()
        };
    }

    // Parse Curve line into CTrk
    private static CTrk? ParseCurveLine(XmlNode lsg, string name, ApplicationModel appModel)
    {
        var points = lsg.SelectNodes("PNT");
        if (points == null || points.Count <= 2) return null;
        // Build raw list from ISOXML
        var desList = new List<Vec3>();
        foreach (XmlNode pnt in points)
        {
            var geo = ParseGeoCoord(pnt, appModel);
            desList.Add(new Vec3(geo));
        }
        // Keep originals if you want ptA/ptB to reflect the original span
        var originalFirst = desList[0];
        var originalLast = desList[desList.Count - 1];
        // Extend ends before preprocessing (pure, no ref)
        double extendMeters = 100.0;     // tweak as needed
        bool keepOriginalAB = false;   // true => ptA/ptB = original endpoints
        desList = ExtendEnds(desList, extendMeters);
        // CABCurve-like pipeline (no ref, returns new lists)
        desList = Preprocess(desList, 1.6, 0.5);
        if (desList == null || desList.Count < 2) return null;
        double avgHeading = ComputeAverageHeading(desList);
        // Decide ptA/ptB (extended vs original)
        Vec2 ptA, ptB;
        if (keepOriginalAB)
        {
            ptA = new Vec2(originalFirst.Easting, originalFirst.Northing);
            ptB = new Vec2(originalLast.Easting, originalLast.Northing);
        }
        else
        {
            ptA = new Vec2(desList[0].Easting, desList[0].Northing);
            ptB = new Vec2(desList[desList.Count - 1].Easting, desList[desList.Count - 1].Northing);
        }
        // Build track
        var track = new CTrk
        {
            heading = avgHeading,
            mode = TrackMode.Curve,
            ptA = ptA,
            ptB = ptB,
            name = string.IsNullOrWhiteSpace(name) ? "Curve_" + DateTime.Now.ToString("HHmmss") : name
        };
        // Copy processed curve points
        for (int i = 0; i < desList.Count; i++)
            track.curvePts.Add(desList[i]);
        return track;
    }

    // Now we can use some helpers
    private static void AccumulateCoordinates(XmlNode parent, ref double latSum, ref double lonSum, ref int count)
    {
        foreach (XmlNode pnt in parent.SelectNodes(".//PNT")!)
        {
            if (double.TryParse(pnt.Attributes!["C"]?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double lat) &&
                double.TryParse(pnt.Attributes["D"]?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double lon))
            {
                latSum += lat;
                lonSum += lon;
                count++;
            }
        }
    }

    private static Vec2 ParseVec2(XmlNode pnt, ApplicationModel appModel)
    {
        double.TryParse(pnt.Attributes!["C"]?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double lat);
        double.TryParse(pnt.Attributes["D"]?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double lon);
        GeoCoord geo = appModel.LocalPlane.ConvertWgs84ToGeoCoord(new Wgs84(lat, lon));
        return new Vec2(geo);
    }

    private static GeoCoord ParseGeoCoord(XmlNode pnt, ApplicationModel appModel)
    {
        double.TryParse(pnt.Attributes!["C"]?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double lat);
        double.TryParse(pnt.Attributes["D"]?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double lon);
        return appModel.LocalPlane.ConvertWgs84ToGeoCoord(new Wgs84(lat, lon));
    }

    private static List<Vec3> ExtendEnds(List<Vec3> pts, double extendMeters)
    {
        if (pts == null || pts.Count < 2 || extendMeters <= 0) return pts!;
        // Work on a copy to keep this pure
        var list = new List<Vec3>(pts);
        // Extend before the first point (backwards along first segment)
        var first = list[0];
        var second = list[1];
        double dxF = first.Easting - second.Easting;
        double dyF = first.Northing - second.Northing;
        double lenF = Math.Sqrt(dxF * dxF + dyF * dyF);
        if (lenF > 1e-6)
        {
            dxF /= lenF; dyF /= lenF;
            list.Insert(0, new Vec3(
                first.Easting + dxF * extendMeters,
                first.Northing + dyF * extendMeters,
                0
            ));
        }
        // Extend after the last point (forwards along last segment)
        var last = list[list.Count - 1];
        var beforeLast = list[list.Count - 2];
        double dxL = last.Easting - beforeLast.Easting;
        double dyL = last.Northing - beforeLast.Northing;
        double lenL = Math.Sqrt(dxL * dxL + dyL * dyL);
        if (lenL > 1e-6)
        {
            dxL /= lenL; dyL /= lenL;
            list.Add(new Vec3(
                last.Easting + dxL * extendMeters,
                last.Northing + dyL * extendMeters,
                0
            ));
        }
        return list;
    }

    // ───────────── CurveCABTools.cs ─────────────

    private const double twoPI = Math.PI * 2;

    // Full pipeline: minimum spacing -> interpolate -> headings
    public static List<Vec3> Preprocess(List<Vec3> points, double minSpacing, double interpolationSpacing)
    {
        points = MakePointMinimumSpacing(points, minSpacing);
        points = InterpolatePoints(points, interpolationSpacing);
        points = CalculateHeadings(points);
        return points;
    }

    // Step 1: Ensure minimum spacing between points
    private static List<Vec3> MakePointMinimumSpacing(List<Vec3> points, double minSpacing)
    {
        if (points == null || points.Count < 2) return points!;
        var spaced = new List<Vec3>(points.Count);
        Vec3 last = points[0];
        spaced.Add(last);
        double minSq = minSpacing * minSpacing;
        for (int i = 1; i < points.Count; i++)
        {
            double dx = points[i].Easting - last.Easting;
            double dy = points[i].Northing - last.Northing;
            if ((dx * dx + dy * dy) >= minSq)
            {
                spaced.Add(points[i]);
                last = points[i];
            }
        }
        // Always add the original last point
        Vec3 final = points[points.Count - 1];
        Vec3 compare = spaced[spaced.Count - 1];
        if (DistanceSquared(final, compare) > 1e-10)
            spaced.Add(final);
        return spaced;
    }

    private static double DistanceSquared(Vec3 a, Vec3 b)
        => (a.Easting - b.Easting) * (a.Easting - b.Easting) + (a.Northing - b.Northing) * (a.Northing - b.Northing);

    // Step 2: Interpolate points at fixed spacing
    private static List<Vec3> InterpolatePoints(List<Vec3> points, double spacingMeters)
    {
        if (points == null || points.Count < 2) return points!;
        var result = new List<Vec3>(points.Count * 2);
        for (int i = 0; i < points.Count - 1; i++)
        {
            Vec3 a = points[i];
            Vec3 b = points[i + 1];
            result.Add(a);
            double dx = b.Easting - a.Easting;
            double dy = b.Northing - a.Northing;
            double distance = Math.Sqrt(dx * dx + dy * dy);
            int steps = (int)(distance / spacingMeters);
            for (int j = 1; j < steps; j++)
            {
                double t = (double)j / steps;
                double x = a.Easting + dx * t;
                double y = a.Northing + dy * t;
                result.Add(new Vec3(x, y, 0));
            }
        }
        result.Add(points[points.Count - 1]);
        return result;
    }

    // Step 3: Calculate headings
    public static List<Vec3> CalculateHeadings(List<Vec3> points)
    {
        if (points == null || points.Count < 2) return points!;
        for (int i = 0; i < points.Count - 1; i++)
        {
            double dx = points[i + 1].Easting - points[i].Easting;
            double dy = points[i + 1].Northing - points[i].Northing;
            var pt = points[i];
            pt.Heading = Math.Atan2(dx, dy);
            if (pt.Heading < 0) pt.Heading += twoPI;
            points[i] = pt;
        }
        // Copy last heading from the second last
        var last = points[points.Count - 1];
        last.Heading = points[points.Count - 2].Heading;
        points[points.Count - 1] = last;
        return points;
    }

    // Step 4: Compute circular mean heading
    public static double ComputeAverageHeading(List<Vec3> points)
    {
        if (points == null || points.Count == 0) return 0;
        double cx = 0, sy = 0;
        for (int i = 0; i < points.Count; i++)
        {
            cx += Math.Cos(points[i].Heading);
            sy += Math.Sin(points[i].Heading);
        }
        cx /= points.Count;
        sy /= points.Count;
        double avg = Math.Atan2(sy, cx);
        if (avg < 0) avg += twoPI;
        return avg;
    }

    // ───────────── AgOpenGPS.Core FenceLineService ─────────────

    public static List<Vec3> CalculateAreaAndFixWinding(List<Vec3> fenceLine, int boundaryIndex, out double area)
    {
        area = 0;
        if (fenceLine == null || fenceLine.Count < 3)
            return fenceLine!;
        int ptCount = fenceLine.Count;
        int j = ptCount - 1;
        // Calculate signed area
        for (int i = 0; i < ptCount; j = i++)
        {
            area += (fenceLine[j].Easting + fenceLine[i].Easting) * (fenceLine[j].Northing - fenceLine[i].Northing);
        }
        bool isClockwise = area >= 0;
        area = Math.Abs(area / 2);
        // Fix winding if needed
        // Outer boundary (idx=0) should be counter-clockwise (negative signed area before abs)
        // Inner boundary (idx>0) should be clockwise (positive signed area before abs)
        if ((boundaryIndex == 0 && isClockwise) || (boundaryIndex > 0 && !isClockwise))
        {
            return ReverseWinding(fenceLine);
        }
        return fenceLine;
    }

    public static List<Vec3> ReverseWinding(List<Vec3> fenceLine)
    {
        if (fenceLine == null || fenceLine.Count < 2)
            return fenceLine!;
        int cnt = fenceLine.Count;
        Vec3[] arr = new Vec3[cnt];
        for (int i = 0; i < cnt; i++)
        {
            arr[i] = fenceLine[i];
        }
        List<Vec3> result = new List<Vec3>(cnt);
        for (int i = cnt - 1; i >= 0; i--)
        {
            Vec3 pt = arr[i];
            pt.Heading -= Math.PI;
            if (pt.Heading < 0) pt.Heading += twoPI;
            result.Add(pt);
        }
        return result;
    }
}
