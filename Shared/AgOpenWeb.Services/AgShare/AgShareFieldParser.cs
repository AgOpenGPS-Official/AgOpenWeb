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

using AgOpenWeb.Models;
using AgOpenWeb.Models.AgShare;
using AgOpenWeb.Models.Base;

namespace AgOpenWeb.Services.AgShare
{
    /// <summary>
    /// Core service for parsing AgShare field data from DTO format to local field models
    /// </summary>
    public static class AgShareFieldParser
    {
        /// <summary>
        /// Parse AgShare field DTO to local field model
        /// Converts boundaries and AB lines from WGS84 to local NE coordinates
        /// </summary>
        public static LocalFieldModel Parse(AgShareFieldDto dto)
        {
            var result = new LocalFieldModel
            {
                FieldId = dto.Id,
                Name = dto.Name,
                Origin = new Wgs84(dto.Latitude, dto.Longitude),
                Boundaries = new List<List<LocalPoint>>(),
                AbLines = new List<AbLineLocal>()
            };

            // LocalPlane, like live GPS and the uploader: longitude is scaled at each point's
            // own latitude. GeoConversion scaled it at the origin's, which sheared the field
            // east-west on every upload→download round trip (AgOpenGPS #1214/#1215).
            var plane = new LocalPlane(result.Origin, new SharedFieldProperties());
            LocalPoint ToLocal(CoordinateDto c)
            {
                var g = plane.ConvertWgs84ToGeoCoord(new Wgs84(c.Latitude, c.Longitude));
                return new LocalPoint(g.Easting, g.Northing);
            }

            // Boundary rings: the first is the outer boundary, the rest are holes. A ring of
            // fewer than 3 valid points would make Boundary.txt unreadable, so it's dropped;
            // without a usable outer ring there's no boundary at all (a hole mustn't become it).
            var rings = dto.Boundaries ?? new List<List<CoordinateDto>>();
            for (int r = 0; r < rings.Count; r++)
            {
                var ringList = new List<LocalPoint>();
                foreach (var point in rings[r] ?? new List<CoordinateDto>())
                    if (IsValid(point)) ringList.Add(ToLocal(point));
                // The uploader closes each ring by repeating its first point; Boundary.txt rings
                // are implicitly closed, so drop the duplicate (a zero-length last edge).
                if (ringList.Count > 1
                    && Math.Abs(ringList[0].Easting - ringList[^1].Easting) < 1e-3
                    && Math.Abs(ringList[0].Northing - ringList[^1].Northing) < 1e-3)
                    ringList.RemoveAt(ringList.Count - 1);

                if (ringList.Count >= 3) result.Boundaries.Add(ringList);
                else if (r == 0) break;
            }

            // AB-lines and curves
            foreach (var ab in dto.AbLines ?? new List<AbLineUploadDto>())
            {
                if (ab?.Coords == null || ab.Coords.Count < 2) continue;
                if (!IsValid(ab.Coords[0]) || !IsValid(ab.Coords[1])) continue;

                var ptA = ToLocal(ab.Coords[0]);
                var ptB = ToLocal(ab.Coords[1]);

                var abLine = new AbLineLocal
                {
                    Name = ab.Name ?? "Unnamed",
                    Heading = GeoConversion.HeadingFromPoints(
                        new Vec2(ptA.Easting, ptA.Northing), new Vec2(ptB.Easting, ptB.Northing)),
                    PtA = ptA,
                    PtB = ptB,
                    CurvePoints = new List<LocalPoint>()
                };

                if (ab.Coords.Count > 2)
                {
                    var pts = new List<LocalPoint>();
                    foreach (var c in ab.Coords)
                        if (IsValid(c)) pts.Add(ToLocal(c));

                    // Each point heads to the next; the last keeps the final segment's heading
                    // (it used to be 0, i.e. north).
                    for (int i = 0; i < pts.Count; i++)
                    {
                        var (from, to) = i < pts.Count - 1 ? (pts[i], pts[i + 1])
                            : pts.Count > 1 ? (pts[i - 1], pts[i]) : (pts[i], pts[i]);
                        double heading = from.Easting == to.Easting && from.Northing == to.Northing ? 0
                            : new GeoDir(new GeoDelta(
                                new GeoCoord(from.Northing, from.Easting),
                                new GeoCoord(to.Northing, to.Easting))).AngleInRadians;
                        abLine.CurvePoints.Add(new LocalPoint(pts[i].Easting, pts[i].Northing, heading));
                    }
                }

                result.AbLines.Add(abLine);
            }

            return result;
        }

        private static bool IsValid(CoordinateDto? c) =>
            c != null && double.IsFinite(c.Latitude) && double.IsFinite(c.Longitude)
            && Math.Abs(c.Latitude) <= 90 && Math.Abs(c.Longitude) <= 180;
    }
}
