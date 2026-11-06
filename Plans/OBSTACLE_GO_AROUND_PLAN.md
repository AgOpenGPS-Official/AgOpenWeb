# Going around obstacles: design

**Status:** implemented on the fork branch `feature/obstacle-go-around`, on top of #613 (U-turn
at obstacles), with the scenario suite; for review upstream. Asked for in the review of #610,
whose part 4 (go around) had two faults: the U-turn planner did not know the bent line, and
operator actions rebuilt the bend into the obstacle.

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
pass plus the lead in and out) and over on that side of the obstacle's centre. A rebuild for any
reason keeps that side. If that side has no room any more (the operator nudged the line into
another obstacle, or added one), the obstacle is not driven round the other way; it becomes a
U-turn obstacle for this pass (P3). The pin is let go when the tractor gets to the other side of
the obstacle: a U-turn next to a tree lands the tractor on the neighbouring pass, on the other
side of it, and that pass must bend away from the tractor, not back across the tree (found by the
scenario suite: Tree circle 18 between passes 12 and 13, the pin of pass 12 bent pass 13 across
the tree in front of the tractor). In the lead-in, a tractor still on the line and not yet over
on the bend's side pins nothing: a flip there is harmless.

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

**P5. A safety net that does not trust the line.** With AutoSteer on and no turn being driven,
if the tractor's own straight-ahead reach (10 m plus 2 s of travel, as in #613) meets a Go around
obstacle grown by half the tractor's track, and the tractor is more than 1.5 m off the steered
line (the safety margin and the room the bend gave the implement are spent), AutoSteer goes off
with the alarm. Driving a sound bend never trips it: the line keeps half the implement plus the
margin away from the obstacle, and a tractor on the line, or lagging it a little on the way into
the bend, is not off it by 1.5 m. It catches what no planning did: a tractor that is still
heading into the tree after an action P2 and P3 did not cover. The distance from the line is the
steering cycle's own, from the bent line steered (the shown line's only while driving by hand):
the scenario suite found the first build reading a value only refreshed with AutoSteer off, so
the net never fired while it mattered. With it, an obstacle made Go around again 6 m before it
takes AutoSteer off at once ("AutoSteer off: Inner N ahead and the tractor off the line round it").

**P6. Bends stay in the field.** A bend keeps half the implement inside the field's edge and
never moves the line into a U-turn obstacle; a bend that would do either is no room (P3). Because
the planner walks the bent current pass, the turn's crossing of the turn line and its entry leg
are found on the bent line, and the exit leg is built on the bent next pass. "Drive across a
headland gap" (#620) composes with it: the headland strip test there excludes obstacles, so a
bend never counts as a gap.

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
margin** (1 m, never under 0.3 m: on the scenario field the tractor holds a bent line to about
0.25 m, so a margin of nothing would put the implement into a hard obstacle on every bend),
saved with the tool profile.

## Changes by component

- **Models**: `ObstacleAction.GoRound` back (value 1, file name `goRound` as in #610 fields);
  `GuidanceConfig.GoAroundRadius` and `GoAroundMargin`.
- **`ObstacleDetour`** (Services/Track): as #610, plus pinned sides (P2: a pinned obstacle keeps
  its side and is blocked, not flipped, when that side has no room) and, for every obstacle gone
  round, its side and where the tractor counts as beside it (`Bend.Rounds`).
- **`GoAroundPasses`** (Services/Track, one singleton for the pipeline and the planner): takes the
  settings and the tool geometry from the configuration store (the margin never under
  `MinMargin`, 0.3 m), keeps the pins (set and released by the pipeline from the bend it steers,
  `Steered`: taken beside an obstacle once the tractor is over on the bend's side, let go when it
  has left the bend or got to the other side), exposes the bend steered now (`Current`,
  `CurrentBlocked`) and the straight-ahead test for the net (P5, `ObstacleAhead`).
- **Pipeline**: the bent pass for steering, display and XTE (as #610); `Steered` after each
  steering cycle (P2); the promoted obstacles handed to #613's kickout; the net (P5) beside it;
  the notice "No room to go around Inner N on this pass: U-turn before it".
- **U-turn planner**: `GuidancePoints` and the next pass (`YouTurnCreationInput.BendPass`) are the
  bent lines (P1); `TurnObstacles` takes the promoted obstacles of the pass (P3), for the turn
  lines, the kickout and the manual turn; `ClearsTurnObstacles` keeps the tractor out of every Go
  around obstacle grown by half its track plus the margin and the implement off a hard one (P4),
  for automatic and manual turns.
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
