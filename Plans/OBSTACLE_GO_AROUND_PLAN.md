# Going around obstacles: design

**Status:** design, for review before the code; the first implementation is on the fork branch
`feature/obstacle-go-around`, on top of #613 (U-turn at obstacles). Asked for in the review of
#610, whose part 4 (go around) had two faults: the U-turn planner did not know the bent line,
and operator actions rebuilt the bend into the obstacle.

## What it is for

A field with trees in it: the operator wants the pass to bend round each tree, the tractor and
the implement kept out of it, and to come back onto the pass after it, with no U-turn and no
hand steering. Big obstacles (a ditch, a pond) keep the U-turn before them (#613). Each
obstacle has a choice: **U-turn**, **Go around** or **Drive through**.

## What went wrong in #610

1. **Two lines.** The steering, the map and the light bar followed the bent line, but the U-turn
   planner, the manual turn and the pass numbers used the straight pass. Near a headland a turn's
   entry leg sat on the straight line through the tree, and its exit leg landed on a straight
   next pass that ran through the next tree right after the turn.
2. **Rebuilt into the tree.** A nudge, a snap, a manual turn or a changed choice rebuilt the bend
   from scratch. Abreast of the tree the new line could be on the tree's other side, and the
   steering went through the tree to reach it.
3. **No room meant drive through.** An obstacle the bend could not get round (another obstacle,
   the field's edge) was driven through, with a toast.

## Principles

**P1. One pass.** The pass bent round its Go around obstacles is *the* pass: the line steered,
shown and measured against (XTE, light bar), the line the U-turn planner walks for the current
pass (`GuidancePoints`) and the next pass (the exit leg's destination), and the line a manual
turn lands on. It is a pure function of the track, the pass offset, the obstacles with their
choices, the tool geometry and the two settings, built once and cached; the pipeline and the
planner get it from the same place, so they can never disagree.

**P2. The side never flips beside the obstacle.** Which side a pass goes round an obstacle is
remembered per obstacle while the tractor is within that obstacle's bend (its length along the
pass plus the lead in and out). A rebuild for any reason keeps that side. If that side has no
room any more (the operator nudged the line into another obstacle, or added one), the obstacle
is not driven round the other way; it becomes a U-turn obstacle for this pass (P3).

**P3. No room means a U-turn, never a drive through.** A Go around obstacle the bend cannot get
round on the allowed side (another grown obstacle, a U-turn obstacle, half the implement out of
the field, or the turn area's edge) is treated as a U-turn obstacle for that pass only: it gets
the turn line of #613, the turn is planned before it, and the kickout rule of #613 protects it
when no turn fits. The operator is told once: "U-turn before Inner N on this pass: no room to go
around it".

**P4. A turn is checked against the Go around obstacles too.** A planned or manual turn path
is refused when the implement's swept path comes within the safety margin of any Go around
obstacle, as it is for a hard U-turn obstacle in #613, and the 20 m of next pass after the turn
is checked the same way. A headland turn refused because of a Go around obstacle promotes that
obstacle to a U-turn obstacle for the pass (P3), so the turn is made before it instead.

**P5. A safety net that does not trust the line.** With AutoSteer on, if the tractor's own
straight-ahead reach (10 m plus 2 s of travel, as in #613) meets a Go around obstacle grown by
half the tractor's track, and the steered line within that reach is not clear of the obstacle,
AutoSteer goes off with the alarm. Driving a sound bend never trips it: the line keeps half the
implement plus the margin away from the obstacle, and the tractor's heading on the bend points
along the line, not at the tree. It catches what no planning did: a line that still runs into
the tree after an action P2 and P3 did not cover.

**P6. Bends stay in the turn area.** A bend is only laid inside the outer turn line (the turn
area of the U-turn planner, 2 m inside the boundary by default); a bend that would leave it is
no room (P3). Because the planner walks the bent current pass, the turn's crossing of the turn
line and its entry leg are found on the bent line, and the exit leg is built on the bent next
pass. "Drive across a headland gap" (#620) composes with it: the headland strip test there
excludes obstacles, so a bend never counts as a gap.

**P7. Trailed implements** cut inside the bend; the keep-out distance includes the cut-in for
the bend's radius (`TrailedCutIn`), as #610 did.

**P8. Change means rebuild and re-plan.** The obstacle fingerprint of #613 (`ObstacleStamp`:
count, choices, Hard, outlines) is part of the bend's cache key, and a planned turn is planned
again when it changes, so a changed choice or a new obstacle takes effect on the next cycle on
every line at once.

## Geometry

Unchanged from #610 (`ObstacleDetour`): each Go around obstacle grown by the keep-out distance
(half the implement and its offset, or half the tractor's track if wider, plus the margin and
the trailed cut-in); the pass moved sideways by what the grown outline needs at each station,
on the side that needs the smaller move; the offset limited to a 45° slope and rounded with the
Lasry-Lions smoothing so that no curve is tighter than the set radius; obstacles close along
the pass gone round together on one side; a bent line checked against every grown obstacle,
every U-turn obstacle's turn line, and the turn area; a conflict tries the other side once, then
the obstacle has no room (P3). The two settings stay: **Curve radius** (6 m) and **Safety
margin** (1 m), saved with the tool profile.

## Changes by component

- **Models**: `ObstacleAction.GoRound` back (value 1, file name `goRound` as in #610 fields);
  `GuidanceConfig.GoAroundRadius` and `GoAroundMargin`.
- **`ObstacleDetour`** (Services/Track): as #610, plus a preferred side per obstacle (P2) and the
  list of obstacles with no room (P3) in its result.
- **Pipeline**: the bent pass for steering, display and XTE (as #610); the side memory (P2); the
  bend handed to the U-turn planner through the tick context, with the promoted obstacles; the
  safety net (P5) beside #613's kickout.
- **U-turn planner**: `GuidancePoints` and the next pass are the bent lines (P1);
  `TurnObstacles` takes the promoted obstacles of the pass (P3); `ClearsTurnObstacles` checks the
  Go around fences with the implement's swept path (P4), for automatic and manual turns.
- **UI**: the third choice in the obstacle list, its colour on the map, the two settings on the
  U-turn tab and the notices, as #610 had them.
- **Tests**: `ObstacleDetour` unit tests from #610; the scenario suite of #613 with the thirty
  lone trees as Go around, driven by the normal operator and the operator who does everything
  wrong (nudge, snap, manual turn, choice change and new obstacle beside the tree), with the
  rule that the tractor and the implement are never inside a Go around obstacle at any moment,
  and that the normal operator is never kicked out except where an obstacle has no room.

## Open points

- The AutoSteer-off fallback before an obstacle is the exception to #494 already discussed on
  #613; P5 adds no new exception, it reuses that one.
- Whether an obstacle with no room should rather stop the tractor than make a U-turn before it:
  we chose the U-turn, because it is what #613 already does safely at every U-turn obstacle.
