// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

namespace AgOpenWeb.FieldJobScenarios.Tests;

/// <summary>
/// The careful operator on lines that are not straight: curves along the boundary, the boundary
/// as a closed loop worked lap by lap, a water pivot's circles, contour and a recorded path. The
/// same machine as the field jobs (the Case Puma with the 8 m sprayer in 8 sections), the same
/// truth coverage and the same checks; AgOpenWeb's clock runs on the job's own time.
/// </summary>
public static class PathJobs
{
    public static readonly SystemSpec Puma = new("um982", "tm171", "keya");

    public static IEnumerable<JobSpec> Jobs()
    {
        yield return new JobSpec("P1", "Olival, laps along the outer boundary",
            "Boundary Curve: the whole outer boundary as a closed loop, no headland. The first lap half a width inside the fence, then one pass in at the end of every lap. The narrow end of the field to the south-west closes after four laps; the laps after it go round the rest of the field.",
            "Olival", "Boundary Curve", Machines.Puma, Machines.Sprayer8, Puma)
        { Guide = GuideKind.BoundaryLoop, HeadlandM = 0, UTurns = false, SimulatedClock = true };
        yield return new JobSpec("P2", "Olival, curves along the west boundary",
            "Bnd. Curve from the south-west corner to the north-west corner along the west boundary (vertices 20 to 33), so the narrow south-western end is worked in long passes along it; headland 16 m, automatic U-turns at the north and south ends.",
            "Olival", "Bnd. Curve west", Machines.Puma, Machines.Sprayer8, Puma)
        { Guide = GuideKind.BoundarySegment, Segment = (20, 33, 27), SimulatedClock = true };
        yield return new JobSpec("P3", "Pivot field, circles from the centre typed in",
            "A field under a water pivot, a circle of 150 m round the tower (its pad an obstacle). The operator stands on the first circle, half a width inside the edge, and makes the pivot track from the tower's lat/lon; then a circle at a time, one pass in at the end of each.",
            "Pivot", "Pivot (lat/lon)", Machines.Puma, Machines.Sprayer8, Puma)
        { Guide = GuideKind.PivotCentre, Obstacles = true, HeadlandM = 0, UTurns = false, SimulatedClock = true };
        yield return new JobSpec("P4", "Pivot field, the pivot recorded by driving",
            "The same field; the pivot track recorded by driving the first third of the outer circle by hand with the sprayer on (Record Pivot, then Finish), then AutoSteer on the circles.",
            "Pivot", "Pivot (driven)", Machines.Puma, Machines.Sprayer8, Puma)
        { Guide = GuideKind.PivotDriven, Obstacles = true, HeadlandM = 0, UTurns = false, SimulatedClock = true };
        yield return new JobSpec("P5", "Pivot field, contour",
            "The same field with no line at all: the outer circle by hand with the sprayer on, then Contour and AutoSteer; at the end of each lap the operator takes over, moves a width in and engages again.",
            "Pivot", "Contour", Machines.Puma, Machines.Sprayer8, Puma)
        { Guide = GuideKind.Contour, Obstacles = true, HeadlandM = 0, UTurns = false, SimulatedClock = true };
        yield return new JobSpec("P6", "Olival, contour",
            "Olival with no line: the first lap along the boundary by hand with the sprayer on, then Contour and AutoSteer lap after lap, the operator moving a width in at the end of each.",
            "Olival", "Contour", Machines.Puma, Machines.Sprayer8, Puma)
        { Guide = GuideKind.Contour, HeadlandM = 0, UTurns = false, SimulatedClock = true };
        yield return new JobSpec("P7", "Olival, a recorded route played back",
            "Record Path: most of a lap 6 m inside the boundary driven by hand with the sprayer off, saved; back round to its start by hand; the route picked, resume 'Closest', Play, AutoSteer engaged, hands off.",
            "Olival", "Recorded path", Machines.Puma, Machines.Sprayer8, Puma)
        { Guide = GuideKind.RecordedPath, HeadlandM = 0, UTurns = false, SimulatedClock = true };
    }
}
