# Display Features Implementation Plan

This document tracks display features with configuration infrastructure in place, documented from AgOpenGPS WinForms reference implementation.

**Status**: Config wiring complete, features pending implementation
**Reference**: `/Users/chris/Code/AgOpenWeb2/SourceCode/GPS/`

---

## 1. Map Rendering Features

### 1.1 Section Lines (isSectionlinesOn)
**Config**: `Display.SectionLinesVisible`
**WinForms Setting**: `setDisplay_isSectionLinesOn`
**Reference**: `OpenGL.Designer.cs:199-229`

**AgOpenGPS Implementation**:
```csharp
// In patch rendering loop, after drawing the filled triangle strip
if (isSectionlinesOn)
{
    //highlight lines
    GL.Color4(0.2, 0.2, 0.2, 1.0);
    GL.Begin(PrimitiveType.LineStrip);
    // Draw outline of each patch using same vertices as triangle strip
    // Uses mipmap skipping for zoomed out views
    GL.End();
}
```

**Purpose**: Draws dark outline around section coverage patches for better visibility.

**Implementation for Avalonia**:
- In section patch drawing code, after filling the coverage polygon
- Draw a dark stroke (0.2, 0.2, 0.2 = dark gray) around each patch
- Read `ConfigurationStore.Instance.Display.SectionLinesVisible`

---

### 1.2 Direction Markers (isDirectionMarkers)
**Config**: `Display.DirectionMarkersVisible`
**WinForms Setting**: `setTool_isDirectionMarkers`
**Reference**: `OpenGL.Designer.cs:234-269`, `GUI.Designer.cs:53,526`

**AgOpenGPS Implementation**:
```csharp
// In patch rendering, after drawing section patches
if (isDirectionMarkers)
{
    if (triList.Count > 42)  // Only for patches with enough points
    {
        // Calculate heading from points 37-39
        double headz = Math.Atan2(triList[39].easting - triList[37].easting,
                                   triList[39].northing - triList[37].northing);

        // Calculate left/right points at factor (0.37) along edge
        left = lerp(triList[37], triList[38], 0.37);
        right = lerp(triList[37], triList[38], 0.63);

        // Calculate tip point ahead of center
        double dist = Distance(left, right) * 1.5;
        ptTip = center + heading * dist;

        // Draw triangle arrow with inverted section color + white tip
        GL.Color4(255-R, 255-G, 255-B, 150);  // Inverted color for base
        GL.Begin(PrimitiveType.Triangles);
        GL.Vertex3(left);
        GL.Vertex3(right);
        GL.Color4(0.85, 0.85, 1, 1.0);  // Light blue-white tip
        GL.Vertex3(ptTip);
        GL.End();
    }
}
```

**Purpose**: Shows direction arrows on section coverage patches to indicate travel direction.

**Implementation for Avalonia**:
- Draw triangle arrows on patches with >42 points
- Arrow points in direction of travel (calculated from patch points 37-39)
- Uses inverted section color for visibility, white tip

---

### 1.3 Line Smoothing (isLineSmooth)
**Config**: `Display.LineSmoothEnabled`
**WinForms Setting**: `setDisplay_isLineSmooth`
**Reference**: `OpenGL.Designer.cs:70-71`, `GUI.Designer.cs:55,513`

**AgOpenGPS Implementation**:
```csharp
// In oglMain_Resize
if (isLineSmooth) GL.Enable(EnableCap.LineSmooth);
else GL.Disable(EnableCap.LineSmooth);
```

**Purpose**: Enables OpenGL anti-aliasing for smoother line rendering.

**Implementation for Avalonia**:
- Avalonia DrawingContext uses anti-aliasing by default
- May use `RenderOptions` to control interpolation quality
- Consider: This may have minimal visible effect in Avalonia

---

### 1.4 Polygons Visible (isDrawPolygons)
**Config**: `Display.PolygonsVisible`
**WinForms**: `isDrawPolygons` (GUI.Designer.cs:33)
**Reference**: `OpenGL.Designer.cs:119`

**AgOpenGPS Implementation**:
```csharp
if (isDrawPolygons) GL.PolygonMode(MaterialFace.Front, PolygonMode.Line);
```

**Purpose**: Renders filled polygons as wireframe outlines instead.

**Implementation for Avalonia**:
- When enabled, draw section patches as outlines only (no fill)
- Use `context.DrawGeometry(null, strokePen, geometry)` instead of fill

---

### 1.5 Svenn Arrow (isSvennArrowOn)
**Config**: `Display.SvennArrowVisible`
**WinForms Setting**: `setDisplay_isSvennArrowOn`
**Reference**: `CVehicle.cs:503-516`

**AgOpenGPS Implementation**:
```csharp
if (mf.isSvennArrowOn && mf.camera.camSetDistance > -1000)
{
    // Scale based on camera distance
    double svennDist = mf.camera.camSetDistance * -0.07;
    double svennWidth = svennDist * 0.22;

    // Draw chevron/arrow pointing forward from vehicle
    LineStyle svenArrowLineStyle = new LineStyle(mf.ABLine.lineWidth, Colors.SvenArrowColor);
    XyCoord[] vertices = {
        new XyCoord(svennWidth, VehicleConfig.Wheelbase + svennDist),      // Right
        new XyCoord(0, VehicleConfig.Wheelbase + svennWidth + 0.5 + svennDist), // Tip
        new XyCoord(-svennWidth, VehicleConfig.Wheelbase + svennDist)      // Left
    };
    GLW.DrawLineStripPrimitive(vertices);
}
```

**Purpose**: Draws a forward-pointing arrow/chevron ahead of the vehicle position. Named after a user who requested the feature.

**Implementation for Avalonia**:
- Draw in vehicle rendering code
- Only show when zoom > -1000 (zoomed in enough)
- Scale arrow size based on zoom level
- Position ahead of vehicle by Wheelbase + scaled distance

---

### 1.6 Field Texture (isTextureOn)
**Config**: `Display.FieldTextureVisible`
**WinForms Setting**: `setDisplay_isTextureOn`
**Reference**: `OpenGL.Designer.cs:113`

**AgOpenGPS Implementation**:
```csharp
worldGrid.DrawFieldSurface(fieldColor, camera.ZoomValue, isTextureOn);
```

**Purpose**: Draws textured background in field area (grass/soil appearance).

**Implementation for Avalonia**:
- Pass flag to field surface drawing method
- When true, tile a texture within boundary
- When false, use solid color fill

---

## 2. Audio Service

### 2.1 Sound Files
**Location**: `/Users/chris/Code/AgOpenWeb2/SourceCode/GPS/Resources/`

| Sound File | Purpose | Config Setting |
|------------|---------|----------------|
| SteerOn.wav | AutoSteer engaged | `setSound_isAutoSteerOn` |
| SteerOff.wav | AutoSteer disengaged | `setSound_isAutoSteerOn` |
| HydUp.wav | Hydraulic lift up | `setSound_isHydLiftOn` |
| HydDown.wav | Hydraulic lift down | `setSound_isHydLiftOn` |
| SectionOn.wav | Section turned on | `setSound_isSectionsOn` |
| SectionOff.wav | Section turned off | `setSound_isSectionsOn` |
| Alarm10.wav | Boundary alarm | (always plays) |
| TF012.wav | U-turn too close warning | `setSound_isUturnOn` |
| Headland.wav | Headland approach | (always plays) |
| rtk_lost.wav | RTK signal lost | (always plays) |
| rtk_back.wav | RTK signal recovered | (always plays) |

**Reference**: `CSound.cs:1-37`

**AgOpenGPS Implementation**:
```csharp
public class CSound
{
    // Sound players loaded from embedded resources
    public readonly SoundPlayer sndAutoSteerOn = new SoundPlayer(Properties.Resources.SteerOn);
    public readonly SoundPlayer sndAutoSteerOff = new SoundPlayer(Properties.Resources.SteerOff);
    public readonly SoundPlayer sndHydLiftUp = new SoundPlayer(Properties.Resources.HydUp);
    public readonly SoundPlayer sndHydLiftDn = new SoundPlayer(Properties.Resources.HydDown);
    // ... etc

    // Config flags
    public bool isSteerSoundOn, isTurnSoundOn, isHydLiftSoundOn, isSectionsSoundOn;

    public CSound()
    {
        // Load settings
        isSteerSoundOn = Properties.Settings.Default.setSound_isAutoSteerOn;
        isHydLiftSoundOn = Properties.Settings.Default.setSound_isHydLiftOn;
        isTurnSoundOn = Properties.Settings.Default.setSound_isUturnOn;
        isSectionsSoundOn = Properties.Settings.Default.setSound_isSectionsOn;
    }
}
```

### 2.2 Sound Trigger Locations

| Event | Location | Code |
|-------|----------|------|
| AutoSteer On | Controls.Designer.cs:190 | `if (sounds.isSteerSoundOn) sounds.sndAutoSteerOn.Play();` |
| AutoSteer Off | Controls.Designer.cs:163,182 | `if (sounds.isSteerSoundOn) sounds.sndAutoSteerOff.Play();` |
| Hydraulic Up | CHead.cs:31 | `if (mf.sounds.isHydLiftSoundOn) mf.sounds.sndHydLiftUp.Play();` |
| Hydraulic Down | CHead.cs:40 | `if (mf.sounds.isHydLiftSoundOn) mf.sounds.sndHydLiftDn.Play();` |
| Section On | Sections.Designer.cs:87,99 | `sounds.sndSectionOn.Play();` |
| Section Off | Sections.Designer.cs:44 | `sounds.sndSectionOff.Play();` |
| RTK Lost | OpenGL.Designer.cs:511 | `sounds.sndRTKAlarm.Play();` |
| RTK Recovered | OpenGL.Designer.cs:539 | `sounds.sndRTKRecoverd.Play();` |
| U-Turn Too Close | Position.designer.cs:1118 | `sounds.sndUTurnTooClose.Play();` |
| Headland | CHead.cs:125 | `mf.sounds.sndHeadland.Play();` |

**Implementation for Avalonia**:
- Copy WAV files to `Shared/AgOpenWeb.Views/Assets/Sounds/`
- Create `IAudioService` interface
- Platform implementations:
  - Desktop: NAudio or System.Media.SoundPlayer
  - iOS: AVFoundation
  - Android: MediaPlayer
- Inject into services that need to trigger sounds

---

## 3. Keyboard Shortcuts

**Config**: `Display.KeyboardEnabled`
**WinForms Setting**: `setDisplay_isKeyboardOn`, `setKey_hotkeys`
**Reference**: `Form_Keys.cs`

**Default Hotkeys**: `"ACFGMNPTYVW12345678"`

| Index | Default | Action |
|-------|---------|--------|
| 0 | A | AutoSteer toggle |
| 1 | C | Cycle Lines |
| 2 | F | Field Menu |
| 3 | G | New Flag |
| 4 | M | Manual Section |
| 5 | N | Auto Section |
| 6 | P | Snap to Pivot |
| 7 | T | Move Line Left |
| 8 | Y | Move Line Right |
| 9 | V | Vehicle Settings |
| 10 | W | Steer Wizard |
| 11-18 | 1-8 | Section 1-8 |

**AgOpenGPS Implementation**:
- Hotkeys stored as char array
- User can customize each key via Form_Keys dialog
- KeyDown handler in main form checks `isKeyboardOn` flag

**Implementation for Avalonia**:
- Desktop only (keyboard not applicable to iOS/Android)
- Handle `KeyDown` in MainWindow
- Check `ConfigurationStore.Instance.Display.KeyboardEnabled`
- Map key to command execution

---

## 4. Window/Startup Features

### 4.1 Start Fullscreen
**Config**: `Display.StartFullscreen`
**WinForms Setting**: `setDisplay_isStartFullScreen`

**Implementation**:
- Check on MainWindow.OnOpened
- If true, set `WindowState = WindowState.FullScreen`

### 4.2 Auto Day/Night
**Config**: `Display.AutoDayNight`

**Implementation** (not found in AgOpenGPS - may be new feature):
- Simple approach: Day mode 6am-6pm, Night mode otherwise
- Advanced: Calculate sunrise/sunset from GPS position

---

## 5. UI Visibility Features

### 5.1 Button Visibility
**Config**: `Display.UTurnButtonVisible`, `Display.LateralButtonVisible`

**Implementation**:
- Bind button `IsVisible` to config property
- Or expose via MainViewModel for easier binding

### 5.2 Speedometer/Headland Distance
**Config**: `Display.SpeedometerVisible`, `Display.HeadlandDistanceVisible`

**Implementation**:
- Create overlay controls in main views
- Bind visibility to config properties
- Speedometer: Display `GpsData.Speed` converted to km/h or mph
- Headland Distance: Calculate distance to nearest headland point

---

## 6. Logging Features

### 6.1 Elevation Logging
**Config**: `Display.ElevationLogEnabled`
**WinForms Setting**: `setDisplay_isLogElevation`

**Implementation**:
- When enabled, log elevation data during operation
- Store in field folder
- Format: timestamp, position, elevation

---

## Implementation Priority

### Phase 1 - High Impact (Enhance Core Experience)
1. **Section Lines** - Easy, improves patch visibility
2. **Direction Markers** - Useful for complex fields
3. **Audio Service** - Important feedback mechanism

### Phase 2 - Medium Impact
4. **Keyboard Shortcuts** - Power user productivity
5. **Svenn Arrow** - Visual guidance aid
6. **Button Visibility** - UI customization

### Phase 3 - Polish
7. **Start Fullscreen** - User preference
8. **Line Smoothing** - May have minimal effect
9. **Polygons Visible** - Debug/visualization mode
10. **Field Texture** - Aesthetic improvement

---

## Files to Create/Modify

### New Files
- `Shared/AgOpenWeb.Services/Interfaces/IAudioService.cs`
- `Platforms/AgOpenWeb.Desktop/Services/AudioService.cs`
- `Platforms/AgOpenWeb.iOS/Services/AudioService.cs`
- `Platforms/AgOpenWeb.Android/Services/AudioService.cs`
- `Shared/AgOpenWeb.Views/Assets/Sounds/` (copy WAV files)

### Modify
- `Shared/AgOpenWeb.Views/Controls/DrawingContextMapControl.cs` - Add display option checks
- `Platforms/AgOpenWeb.Desktop/Views/MainWindow.axaml.cs` - Keyboard handling, start fullscreen
- DI registration files for AudioService
