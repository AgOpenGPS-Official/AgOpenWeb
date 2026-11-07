# 03 U-turns

**Owner:** _
**Setup:** a field with a headland, an AB line, U-turns on. Do it twice: a tool narrower than
twice the turn radius (6 m, radius 8) and one wider (21 m, radius 8). Also a field whose
headland is not square to the passes.

| # | Check | Sim | HW | Notes / issue |
|---|---|---|---|---|
| 3.1 | **Automatic turn** at the headland: the trigger point, the 20 m approach alarm, the entry and exit legs | | | |
| 3.2 | The turn fits inside the boundary; **Distance from boundary** is kept | | | |
| 3.3 | **Wide rows** (tool wider than twice the radius): the turn follows the headland line between the two arcs, also on an angled headland (#544) | | | |
| 3.4 | **Turn radius** changes the turn as in AgOpenGPS | | | |
| 3.5 | **Turn styles**: U (Sagitta) and K-turn | | | |
| 3.6 | **Skip** rows; alternate skips; changing the skip re-plans a planned turn | | | |
| 3.7 | **Swap the direction** with the arrow; the next turn keeps stepping the same way | | | |
| 3.8 | **Ignore worked tracks**: a turn never lands on a worked pass | | | |
| 3.9 | **Manual U-turn** buttons, left and right, on an AB line and on a curve | | | |
| 3.10 | **Lateral move** buttons | | | |
| 3.11 | Turning on a **curve** track at the headland | | | |
| 3.12 | Cancelling a planned turn; turning U-turns off mid-turn | | | |
| 3.13 | Hydraulic lift raises and lowers at the turn as AgOpenGPS | | | |
| 3.14 | Sections switch off entering the turn and on leaving it | | | |
| 3.15 | Out of bounds during a turn: AutoSteer stays on | | | |
| 3.16 | **Drive across a headland gap up to** N m: where the pass leaves the turn area and comes back within N m across the headland only (a narrow tip of the field), no turn is planned there; the pass is driven on with the sections off and the turn comes at the far end. A gap longer than N, one outside the boundary or through an obstacle is turned at. 0 = turn at the first crossing, as before | | | |

**Signed off:** _ (name, date)
