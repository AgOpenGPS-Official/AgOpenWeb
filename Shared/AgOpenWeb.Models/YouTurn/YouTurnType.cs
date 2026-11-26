// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
// SPDX-License-Identifier: Apache-2.0

namespace AgOpenWeb.Models.YouTurn
{
    /// <summary>
    /// Type of U-turn pattern to create.
    /// </summary>
    public enum YouTurnType
    {
        /// <summary>
        /// Sagitta turn (Brian Tischler's AOG dev-fork "sagitta" U-turn) — the
        /// default. A single offset arc with a short counter-arc lead-in so the
        /// path meets the crop row tangentially, eliminating the straight-leg→arc
        /// curvature step that makes Omega turns feel sharp. The sagitta (extra
        /// arc depth) lets it connect rows closer than twice the turn radius
        /// without looping. For rows wider than 2× the radius it adds straight
        /// connectors via the internal Dubins omega fallback.
        ///
        /// Value 0 so that profiles persisted under the old numbering (where
        /// 0 = Albin/Dubins, the looping shortest-path turn we removed) land on
        /// Sagitta automatically — no migration needed. Twol dropped Dubins as a
        /// selectable style for the same reason; it is no longer offered here.
        /// </summary>
        SagittaStyle = 0,

        /// <summary>
        /// K-style turn. A more squared-off / tighter turn pattern (Twol's
        /// alternate-sweep style).
        /// </summary>
        KStyle = 1,

        /// <summary>
        /// Keyhole turn (#418). Like Sagitta for rows closer than twice the turn radius, but
        /// with the counter-arc mirrored at the end, so the loop is centred between the rows:
        /// about 3 m shallower on a close row and half the swing past the row being left.
        /// Wider rows use the same omega fallback as Sagitta.
        /// </summary>
        KeyholeStyle = 2
    }

    /// <summary>
    /// Skip mode for determining next guidance line.
    /// </summary>
    public enum SkipMode
    {
        /// <summary>
        /// Normal skip - use configured skip width.
        /// </summary>
        Normal = 0,

        /// <summary>
        /// Alternate between different skip widths.
        /// </summary>
        Alternative = 1,

        /// <summary>
        /// Skip worked tracks - find next unworked track.
        /// </summary>
        IgnoreWorkedTracks = 2
    }

    /// <summary>
    /// Guidance line type for U-turn.
    /// </summary>
    public enum GuidanceLineType
    {
        /// <summary>
        /// AB straight line.
        /// </summary>
        ABLine = 0,

        /// <summary>
        /// Curved guidance line.
        /// </summary>
        Curve = 1
    }
}
