using System;
using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models.Base;

namespace AgOpenWeb.Services.Track;

public enum GuidanceShiftTarget { Base, Guiding }

/// <summary>Build a replacement before persistence. The old track stays untouched,
/// including the reference still in use by the GPS worker.</summary>
public static class SavedGuidanceShift
{
    public static Models.Track.Track Build(Models.Track.Track source, GuidanceShiftTarget target,
        double driverDelta, bool headingSameWay, int pass, double nudge, double passWidth)
    {
        if (!source.IsABLine || source.Points.Count != 2 || !double.IsFinite(driverDelta)
            || !double.IsFinite(nudge) || !double.IsFinite(passWidth) || passWidth <= 0
            || !Enum.IsDefined(target) || source.Points.Any(p => !double.IsFinite(p.Easting) || !double.IsFinite(p.Northing)))
            throw new InvalidOperationException("A valid straight AB line is required");
        var a = source.Points[0]; var b = source.Points[1];
        if (double.Hypot(b.Easting - a.Easting, b.Northing - a.Northing) < .01)
            throw new InvalidOperationException("A valid straight AB line is required");
        double delta = headingSameWay ? driverDelta : -driverDelta;
        double total = pass * passWidth + nudge;
        var points = source.Points.ToList();
        if (target == GuidanceShiftTarget.Base)
        {
            double heading = source.Heading;
            points = points.Select(p => new Vec3(p.Easting + Math.Cos(heading) * delta,
                p.Northing - Math.Sin(heading) * delta, p.Heading)).ToList();
        }
        else total += delta;
        return new Models.Track.Track {
            Name = source.Name, Type = source.Type, Points = points, IsClosed = source.IsClosed,
            NoPassOffset = source.NoPassOffset, IsVisible = source.IsVisible, NudgeDistance = total,
            WorkedPaths = new HashSet<int>(source.WorkedPaths),
        };
    }
}
