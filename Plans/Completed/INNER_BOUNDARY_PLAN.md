# Inner Boundary (Obstacle) Creation — Implementation Plan

## Issue #238
## Status: In Progress (feature/inner-boundary-ui branch)

## Context

All boundary creation infrastructure exists — model, file I/O, rendering, section control, recording service. Only the UI triggers are missing. AgOpenGPS uses a simple approach: first boundary = outer, subsequent = inner. Same tools for both.

## Current State

| Component | Inner Boundary Support | Status |
|-----------|----------------------|--------|
| `Boundary.InnerBoundaries` model | List<BoundaryPolygon> | Done |
| `BoundaryRecordingService` | `StartRecording(BoundaryType.Inner)` | Done |
| `BoundaryFileService` | Reads/writes inner boundaries to Boundary.txt | Done |
| `GeoJsonFieldService` | Import/export with FeatureRoles.InnerBoundary | Done |
| Map rendering | Draws inner boundaries | Done |
| `Boundary.IsPointInside()` | Accounts for inner holes | Done |
| Section control | Respects inner boundaries | Done |
| Route planning swath clipping | Subtracts inner boundaries | Done |
| **UI to create inner boundaries** | **Missing** | **This plan** |

## Implementation

### Step 1: Boundary List Panel

**New file:** `Views/Controls/Panels/BoundaryListPanel.axaml(.cs)`

Small panel showing all boundaries for the current field:
- "Outer" — always first, shows area
- "Inner 2", "Inner 3", etc. — each shows area
- Select to highlight on map
- Delete button (with confirmation) for each inner boundary
- "Drive Thru" toggle for inner boundaries
- "+ Add Boundary" button at bottom

This panel could be a section in the existing boundary/field tools area, or a popup from a boundary management button.

### Step 2: Update Boundary Recording Flow

**Modify:** `MainViewModel.Commands.Boundary.cs`

Add `RecordInnerBoundaryCommand`:
```csharp
RecordInnerBoundaryCommand = new RelayCommand(() =>
{
    _boundaryRecordingService.StartRecording(BoundaryType.Inner);
    // Same UI flow as outer boundary recording
});
```

**Modify:** `StopBoundaryRecordingCommand` handler:
```csharp
var polygon = _boundaryRecordingService.StopRecording();
if (polygon != null)
{
    var boundary = _boundaryFileService.LoadBoundary(fieldPath) ?? new Boundary();
    if (_boundaryRecordingService.CurrentBoundaryType == BoundaryType.Inner)
        boundary.InnerBoundaries.Add(polygon);
    else
        boundary.OuterBoundary = polygon;
    _boundaryFileService.SaveBoundary(boundary, fieldPath);
}
```

The `BoundaryRecordingService` already tracks the boundary type via its `StartRecording(BoundaryType)` parameter. Need to expose the current type so the stop handler knows where to put the result.

### Step 3: Update BoundaryRecordingPanel UI

**Modify:** `Views/Controls/Panels/BoundaryRecordingPanel.axaml`

- Show "Recording Inner Boundary" vs "Recording Outer Boundary" in the header
- Same record/pause/stop/undo buttons — no change needed
- Status shows "Inner boundary: X points, Y ha"

### Step 4: Update Create Boundary From Map Dialog

**Modify:** `Views/Controls/Dialogs/BoundaryMapDialogPanel.axaml(.cs)`

Add a toggle or mode selector:
- "Outer Boundary" (default) — creates/replaces outer boundary
- "Inner Boundary" — appends to inner boundaries list

The save flow checks the mode:
```csharp
if (isInnerBoundaryMode)
    boundary.InnerBoundaries.Add(polygon);
else
    boundary.OuterBoundary = polygon;
```

### Step 5: Wire Into Navigation

**Modify:** `LeftNavigationPanel` or `FieldToolsPanel`

Add "Add Inner Boundary" option alongside existing boundary tools. Options:
- "Drive" — starts inner boundary recording
- "Draw on Map" — opens map dialog in inner boundary mode

### Step 6: Delete Inner Boundary

**New command:** `DeleteInnerBoundaryCommand(int index)`

Removes the inner boundary at the given index, saves the updated boundary file.

### Step 7: Expose BoundaryType from Recording Service

**Modify:** `IBoundaryRecordingService` / `BoundaryRecordingService`

Add property: `BoundaryType CurrentBoundaryType { get; }` — set during `StartRecording()`, read during `StopRecording()` to determine where to save.

## Files to Create/Modify

| File | Action |
|------|--------|
| `Views/Controls/Panels/BoundaryListPanel.axaml(.cs)` | Create — boundary management list |
| `ViewModels/MainViewModel.Commands.Boundary.cs` | Modify — add inner boundary commands |
| `Services/Interfaces/IBoundaryRecordingService.cs` | Modify — expose CurrentBoundaryType |
| `Services/BoundaryRecordingService.cs` | Modify — expose CurrentBoundaryType |
| `Views/Controls/Panels/BoundaryRecordingPanel.axaml` | Modify — show boundary type |
| `Views/Controls/Dialogs/BoundaryMapDialogPanel.axaml(.cs)` | Modify — add inner mode |
| `Views/Controls/Panels/LeftNavigationPanel.axaml` | Modify — add inner boundary option |

## Implementation Order

1. Expose `CurrentBoundaryType` on recording service (no UI change)
2. Update `StopBoundaryRecordingCommand` to handle inner type
3. Add `RecordInnerBoundaryCommand` 
4. Update recording panel to show boundary type
5. Update map dialog to support inner boundary mode
6. Add boundary list panel with delete/drive-thru
7. Wire into navigation

## Verification

1. Record outer boundary → save → verify in Boundary.txt
2. Record inner boundary → save → verify appended to Boundary.txt
3. Draw inner boundary on map → save → verify
4. Delete inner boundary → verify removed
5. Open field with inner boundaries → verify rendered on map
6. Section control → verify sections turn off inside inner boundary
7. Route plan → verify swaths split around inner boundary
