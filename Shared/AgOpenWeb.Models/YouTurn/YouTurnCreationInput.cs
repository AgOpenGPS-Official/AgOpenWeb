// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

using AgOpenWeb.Models.Base;
using System;
using System.Collections.Generic;

namespace AgOpenWeb.Models.YouTurn
{
    /// <summary>
    /// Input data for U-turn path creation.
    /// </summary>
    public class YouTurnCreationInput
    {
        // Turn style and direction
        public YouTurnType TurnType { get; set; }
        public bool IsTurnLeft { get; set; }
        public GuidanceLineType GuidanceType { get; set; }

        // Boundary data
        public List<BoundaryTurnLine> BoundaryTurnLines { get; set; } = new List<BoundaryTurnLine>();

        /// <summary>
        /// Function to check if a point is inside the turn area, as AgOpenGPS's
        /// IsPointInsideTurnArea: 0 in the workable area, -1 outside the outer turn line
        /// (<see cref="BoundaryTurnLines"/>[0]), k inside obstacle turn line k.
        /// </summary>
        public Func<Vec3, int> IsPointInsideTurnArea { get; set; }

        /// <summary>
        /// Whether a point of a turn being laid out is blocked: inside a turn line
        /// (<see cref="IsPointInsideTurnArea"/> not 0) or in the room kept round a Go around
        /// obstacle, which a turn keeps out of as the bent pass does. The generator seats its
        /// arcs and slides them to the turn line by this test; the crossing a turn is made at
        /// is still found by the turn lines. Null: the turn lines alone.
        /// </summary>
        public Func<Vec3, bool>? IsPointBlockedForTurn { get; set; }

        // Guidance line data
        /// <summary>
        /// For curves: List of curve points.
        /// For AB lines: Can be empty (AB line defined by heading and reference point).
        /// </summary>
        public List<Vec3> GuidancePoints { get; set; } = new List<Vec3>();

        /// <summary>
        /// Current location index in the guidance points (for curves).
        /// </summary>
        public int CurrentLocationIndex { get; set; }

        /// <summary>
        /// For curves: the BASE (path 0) reference curve points with headings.
        /// <see cref="GuidancePoints"/> is the current pass — this base track offset
        /// by <see cref="HowManyPathsAway"/> — whereas the destination pass for the
        /// exit leg is built by offsetting THIS reference curve (matches AgOpen's
        /// CABCurve.BuildNewOffsetList, which always offsets the reference curve).
        /// </summary>
        public List<Vec3> ReferenceCurvePoints { get; set; } = new List<Vec3>();

        /// <summary>
        /// Bends a pass (offset that far from the reference) round the field's Go around
        /// obstacles, as the pipeline steers it: the pass a turn lands on is built with it, so
        /// the exit leg joins the line that is driven. Null: passes are not bent.
        /// </summary>
        public Func<double, List<Vec3>, List<Vec3>>? BendPass { get; set; }

        /// <summary>
        /// For curves: Is vehicle heading same way as curve direction?
        /// For AB: Is vehicle heading same way as AB line direction?
        /// </summary>
        public bool IsHeadingSameWay { get; set; }

        /// <summary>
        /// For AB lines: AB line heading in radians.
        /// </summary>
        public double ABHeading { get; set; }

        /// <summary>
        /// For AB lines: Reference point on AB line (rEast, rNorth).
        /// </summary>
        public Vec2 ABReferencePoint { get; set; }

        // Vehicle position and configuration
        public Vec3 PivotPosition { get; set; }
        public double ToolWidth { get; set; }
        public double ToolOverlap { get; set; }
        public double ToolOffset { get; set; }
        public double TurnRadius { get; set; }

        // Turn parameters
        public int RowSkipsWidth { get; set; }
        public int TurnStartOffset { get; set; }  // How far before/after boundary to start turn

        /// <summary>
        /// Pre-calculated turn offset in meters (perpendicular distance to next track).
        /// When set (> 0), this value is used directly instead of calculating from RowSkipsWidth.
        /// This ensures the U-turn arc matches the cyan next-track line exactly.
        /// </summary>
        public double TurnOffset { get; set; }

        /// <summary>
        /// How many paths away from current line (negative or positive).
        /// </summary>
        public int HowManyPathsAway { get; set; }

        /// <summary>
        /// Nudge distance for track mode.
        /// </summary>
        public double NudgeDistance { get; set; }

        /// <summary>Reference line to pass 0's centre: half a width, or 0 for a
        /// boundary-follow track (PassGeometry.EdgeOffset).</summary>
        public double PassEdgeOffset { get; set; }

        /// <summary>
        /// Track mode (for determining if this is a special mode like waterPivot).
        /// </summary>
        public int TrackMode { get; set; }

        // State machine
        /// <summary>
        /// Counter to prevent making turns too frequently (must be >= 4).
        /// </summary>
        public int MakeUTurnCounter { get; set; }

        /// <summary>
        /// List of worked tracks (for IgnoreWorkedTracks mode).
        /// </summary>
        public HashSet<int> WorkedTracks { get; set; } = new HashSet<int>();

        /// <summary>
        /// U-turn leg extension multiplier. The straight legs before/after the turn arc
        /// are extended by this factor times the turn diameter.
        /// Typical values are 2-3 (default 2.5).
        /// Only used if LegLength is not set.
        /// </summary>
        public double YouTurnLegExtensionMultiplier { get; set; } = 2.5;

        /// <summary>
        /// The headland width in meters. Used to ensure U-turn legs extend
        /// far enough to guide the tractor through the entire headland area.
        /// </summary>
        public double HeadlandWidth { get; set; } = 20.0;

        /// <summary>
        /// Direct leg length in meters. If > 0, this is used directly for the
        /// entry/exit legs instead of calculating from HeadlandWidth * Multiplier.
        /// This is the user's UTurnExtension setting.
        /// </summary>
        public double LegLength { get; set; } = 0;
    }
}
