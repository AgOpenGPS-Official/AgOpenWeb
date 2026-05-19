// Phase-2 GL map view: renders real boundary, tracks, headland, vehicle,
// and tool data pulled from MapService. Camera tracks the vehicle with a
// fixed tilt (Phase-3 wires CameraPitch / Zoom / Pan / Rotation).

using System;
using System.Collections.Generic;
using System.Numerics;
using AgValoniaGPS.Models;
using AgValoniaGPS.Models.Base;
using AgValoniaGPS.Models.Track;
using Avalonia;
using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Silk.NET.Core.Contexts;
using Silk.NET.OpenGLES;

namespace AgValoniaGPS.Views.Controls;

public class GlMapControl : OpenGlControlBase
{
    public GlMapControl()
    {
        Console.WriteLine("[GlMap] GlMapControl ctor");
        AttachedToVisualTree += (_, _) => Console.WriteLine("[GlMap] AttachedToVisualTree");
    }

    // ----- Phase-2 public data API ------------------------------------------------

    // Reference-gate every Set* that owns a GPU buffer: callers (MainViewModel /
    // MapService) may push the same instance repeatedly across GPS cycles, and a
    // dirty-flag set here triggers a glDelete/glGen/glBufferData cycle each frame
    // — ANGLE-D3D11 and mobile GLES drivers handle that churn badly. The gate
    // makes "set what you already had" a no-op without depending on upstream
    // discipline. Setting null when null is also a no-op.
    public void SetBoundary(Boundary? boundary)
    {
        if (ReferenceEquals(_boundary, boundary)) return;
        _boundary = boundary;
        _boundaryDirty = true;
    }

    public void SetActiveTrack(Track? track)
    {
        if (ReferenceEquals(_activeTrack, track)) return;
        _activeTrack = track;
        _tracksDirty = true;
    }

    public void SetBaseTrack(Track? track)
    {
        if (ReferenceEquals(_baseTrack, track)) return;
        _baseTrack = track;
        _tracksDirty = true;
    }

    public void SetNextTrack(Track? track)
    {
        if (ReferenceEquals(_nextTrack, track)) return;
        _nextTrack = track;
        _tracksDirty = true;
    }

    public void SetHeadlandLine(IReadOnlyList<Vec3>? headland)
    {
        if (ReferenceEquals(_headlandLine, headland)) return;
        _headlandLine = headland;
        _headlandDirty = true;
    }

    public void SetHeadlandVisible(bool visible) => _isHeadlandVisible = visible;

    /// <summary>
    /// Camera pitch in degrees. -90 = looking straight down (matches 2D look),
    /// -10 = nearly horizontal. Values outside [-90, -10] are clamped.
    /// </summary>
    public void SetCameraPitchDegrees(double degrees) => _pitchDegrees = Math.Clamp(degrees, -90.0, -10.0);

    /// <summary>
    /// Camera zoom level. 1.0 is the spike's reference distance (~80m above
    /// target); higher values move closer, lower values pull back.
    /// </summary>
    public void SetCameraZoom(double zoomLevel) => _zoomLevel = Math.Max(0.05, zoomLevel);

    public void SetAllPositions(double vehicleX, double vehicleY, double vehicleHeading,
        double toolX, double toolY, double toolHeading, double toolWidth,
        double hitchX, double hitchY, bool toolReady)
    {
        _vehicleX = vehicleX;
        _vehicleY = vehicleY;
        _vehicleHeading = vehicleHeading;
        _toolX = toolX;
        _toolY = toolY;
        _toolHeading = toolHeading;
        _toolWidth = toolWidth;
        _hitchX = hitchX;
        _hitchY = hitchY;
        _toolReady = toolReady;
        _hasVehicle = true;
        _toolDirty = true;
    }

    // ----- Internal state ---------------------------------------------------------

    private GL? _gl;
    private uint _program;
    private int _uniformMvp;
    private int _uniformColor;
    private bool _logged;

    // Data state, written from UI thread, consumed on render thread.
    private Boundary? _boundary;
    private bool _boundaryDirty;
    private Track? _activeTrack, _baseTrack, _nextTrack;
    private bool _tracksDirty;
    private IReadOnlyList<Vec3>? _headlandLine;
    private bool _headlandDirty;
    private bool _isHeadlandVisible;
    private double _vehicleX, _vehicleY, _vehicleHeading;
    private double _toolX, _toolY, _toolHeading, _toolWidth, _hitchX, _hitchY;
    private bool _toolReady, _hasVehicle, _toolDirty;

    // Camera state — pitch in degrees (-90 overhead .. -10 near-horizon),
    // zoom multiplier (1.0 = reference distance, higher = closer in).
    // Default -62 matches AgOpenGPS's DisplaySettingsService 3D default
    // (CameraPitch = -62.0), so a freshly-attached GlMapControl with no
    // MainViewModel push yet starts at the same tilt as upstream AOG.
    private double _pitchDegrees = -62.0;
    private double _zoomLevel = 1.0;
    // Heading-up vs north-up. AOG default for the 3D view is heading-up
    // (world rotates with the vehicle heading; vehicle stays pointing up).
    // North-up is the legacy 2D fallback. Toggleable in Phase 3b.
    private bool _followHeading = true;
    // Manual pan offset applied between world-translate and view rotation —
    // wired to gesture/mouse pan in a later phase. Defaults to 0.
    private float _panX, _panY;

    // Static 10m world-aligned grid covering 2 km × 2 km centred on origin.
    // Renders under everything else so geographic translation reads as
    // "grid lines scrolling south" instead of abstract perspective. Built once
    // in OnOpenGlInit since the geometry is fixed in world space.
    private uint _gridVao, _gridVbo; private int _gridCount;

    // Brown ground quad (Z=0 plane) + sky-blue clear color. Diagnostic to
    // verify world orientation: if the horizon (brown/blue boundary) appears
    // visually correct (brown below the vehicle, blue above), the camera
    // orientation is right. If blue ends up below brown, there's a flip.
    private uint _groundVao, _groundVbo;

    // GPU buffers — one VAO/VBO per geometry stream. Each stream's vertex count
    // is the number of GL_LINES vertices uploaded (2 per line segment).
    private uint _outerVao, _outerVbo; private int _outerCount;
    private readonly List<(uint Vao, uint Vbo, int Count)> _innerBoundaries = new();
    private uint _headlandVao, _headlandVbo; private int _headlandCount;
    private uint _activeTrackVao, _activeTrackVbo; private int _activeTrackCount;
    private uint _baseTrackVao, _baseTrackVbo; private int _baseTrackCount;
    private uint _nextTrackVao, _nextTrackVbo; private int _nextTrackCount;
    private uint _vehicleVao, _vehicleVbo; private int _vehicleCount;
    private uint _toolVao, _toolVbo; private int _toolCount;

    protected override void OnOpenGlLost()
    {
        Console.WriteLine("[GlMap] OnOpenGlLost — context was lost / not available");
    }

    // Shader bodies. The version header is prepended at compile time so the
    // same shader source works on both desktop GL (GLSL 330 core) and GLES
    // (GLSL ES 300). Keep these vec3+uniform-only — coloring and transform
    // are driven entirely from the uniform block.
    private const string VertexShaderBody = @"
in vec3 in_pos;
uniform mat4 u_mvp;
void main() { gl_Position = u_mvp * vec4(in_pos, 1.0); }
";

    private const string FragmentShaderBody = @"
precision mediump float;
uniform vec4 u_color;
out vec4 out_color;
void main() { out_color = u_color; }
";

    // Ground shader: renders a fullscreen quad and for each pixel ray-casts
    // from the camera through that pixel, intersects with the Z=0 ground
    // plane. If the ray hits ground in front of the camera, fragment is brown
    // (true ground). Otherwise, blue sky. This produces a real infinite-plane
    // horizon without depending on a finite quad's edges.
    private const string GroundVertexShaderBody = @"
in vec3 in_pos;
uniform mat4 u_inv_mvp;
out vec3 v_near;
out vec3 v_far;
void main() {
    // in_pos is in NDC already; pass through to clip space unchanged.
    gl_Position = vec4(in_pos.xy, 0.0, 1.0);
    // Reconstruct world-space ray endpoints by unprojecting near/far plane.
    // System.Numerics CreatePerspectiveFieldOfView uses NDC Z in [0, 1]
    // (DirectX style), so NDC Z = 0 is the near plane and NDC Z = 1 is far.
    vec4 near4 = u_inv_mvp * vec4(in_pos.xy, 0.0, 1.0);
    vec4 far4  = u_inv_mvp * vec4(in_pos.xy, 1.0, 1.0);
    v_near = near4.xyz / near4.w;
    v_far  = far4.xyz  / far4.w;
}
";

    private const string GroundFragmentShaderBody = @"
precision highp float;
in vec3 v_near;
in vec3 v_far;
out vec4 out_color;
void main() {
    // Ray-cast from camera through this pixel, intersect with Z=0 plane.
    // Ray hit in front of camera (t > 0) → ground. Otherwise → sky.
    vec3 dir = v_far - v_near;
    if (abs(dir.z) < 1e-6) {
        out_color = vec4(0.45, 0.65, 0.85, 1.0);  // sky blue
        return;
    }
    float t = -v_near.z / dir.z;
    if (t > 0.0) {
        out_color = vec4(0.55, 0.35, 0.18, 1.0);  // saddle brown ground
    } else {
        out_color = vec4(0.45, 0.65, 0.85, 1.0);  // sky blue
    }
}
";

    private uint _groundProgram;
    private int _uniformGroundInvMvp;

    protected override void OnOpenGlInit(GlInterface glInterface)
    {
        Console.WriteLine($"[GlMap] OnOpenGlInit called. GlVersion={GlVersion}");
        _gl = GL.GetApi(new AvaloniaGlInterfaceContext(glInterface));

        string version = GlVersion.Type == GlProfileType.OpenGLES
            ? "#version 300 es\n"
            : "#version 330 core\n";
        _program = CompileAndLink(_gl, version + VertexShaderBody, version + FragmentShaderBody);
        _uniformMvp = _gl.GetUniformLocation(_program, "u_mvp");
        _uniformColor = _gl.GetUniformLocation(_program, "u_color");

        // Vehicle marker geometry is fixed in model space — an arrow pointing
        // +Y (vehicle local "forward"), 6m long, 4m wide. World transform from
        // (vehicle.X, vehicle.Y, heading) is applied via the MVP each frame.
        float[] vehicleVerts =
        {
            0f, +3f, 0.6f,   -2f, -3f, 0.6f,   // left side of arrow
            0f, +3f, 0.6f,   +2f, -3f, 0.6f,   // right side
            -2f, -3f, 0.6f,  +2f, -3f, 0.6f,   // base
        };
        (_vehicleVao, _vehicleVbo) = BuildStaticVao(_gl, vehicleVerts);
        _vehicleCount = vehicleVerts.Length / 3;

        // Build the static world grid: lines every 10m covering [-1000..1000]
        // on both axes. 201 lines per axis × 2 verts × 3 floats = 1206 floats
        // × 2 axes = 2412 floats. Plenty of motion reference, cheap to draw.
        const int GridHalfExtent = 1000;
        const int GridStep = 10;
        const int Lines = (GridHalfExtent * 2 / GridStep) + 1; // 201
        var grid = new float[Lines * 2 * 2 * 3]; // 2 verts per line, X and Y axes
        int gi = 0;
        // Vertical lines (constant X, varying Y)
        for (int i = -GridHalfExtent; i <= GridHalfExtent; i += GridStep)
        {
            grid[gi++] = i; grid[gi++] = -GridHalfExtent; grid[gi++] = 0f;
            grid[gi++] = i; grid[gi++] = +GridHalfExtent; grid[gi++] = 0f;
        }
        // Horizontal lines (constant Y, varying X)
        for (int j = -GridHalfExtent; j <= GridHalfExtent; j += GridStep)
        {
            grid[gi++] = -GridHalfExtent; grid[gi++] = j; grid[gi++] = 0f;
            grid[gi++] = +GridHalfExtent; grid[gi++] = j; grid[gi++] = 0f;
        }
        (_gridVao, _gridVbo) = BuildStaticVao(_gl, grid);
        _gridCount = grid.Length / 3;

        // Fullscreen quad in NDC for the ground shader. Just covers [-1,+1]²
        // in clip space; the ground fragment shader does the per-pixel ray
        // cast against the Z=0 world plane.
        float[] groundVerts =
        {
            -1f, -1f, 0f,
            +1f, -1f, 0f,
            -1f, +1f, 0f,
            +1f, +1f, 0f,
        };
        (_groundVao, _groundVbo) = BuildStaticVao(_gl, groundVerts);

        // Compile the ground shader program (separate from the line shader).
        _groundProgram = CompileAndLink(_gl, version + GroundVertexShaderBody, version + GroundFragmentShaderBody);
        _uniformGroundInvMvp = _gl.GetUniformLocation(_groundProgram, "u_inv_mvp");
    }

    protected override void OnOpenGlDeinit(GlInterface glInterface)
    {
        if (_gl == null) return;
        var gl = _gl;
        DeleteVao(gl, ref _outerVao, ref _outerVbo);
        foreach (var (vao, vbo, _) in _innerBoundaries) { uint v = vao, b = vbo; DeleteVao(gl, ref v, ref b); }
        _innerBoundaries.Clear();
        DeleteVao(gl, ref _headlandVao, ref _headlandVbo);
        DeleteVao(gl, ref _activeTrackVao, ref _activeTrackVbo);
        DeleteVao(gl, ref _baseTrackVao, ref _baseTrackVbo);
        DeleteVao(gl, ref _nextTrackVao, ref _nextTrackVbo);
        DeleteVao(gl, ref _vehicleVao, ref _vehicleVbo);
        DeleteVao(gl, ref _toolVao, ref _toolVbo);
        DeleteVao(gl, ref _gridVao, ref _gridVbo);
        DeleteVao(gl, ref _groundVao, ref _groundVbo);
        if (_program != 0) { gl.DeleteProgram(_program); _program = 0; }
        if (_groundProgram != 0) { gl.DeleteProgram(_groundProgram); _groundProgram = 0; }
        _gl = null;
    }

    private static void DeleteVao(GL gl, ref uint vao, ref uint vbo)
    {
        if (vbo != 0) { gl.DeleteBuffer(vbo); vbo = 0; }
        if (vao != 0) { gl.DeleteVertexArray(vao); vao = 0; }
    }

    protected override unsafe void OnOpenGlRender(GlInterface glInterface, int fb)
    {
        if (_gl == null) return;
        var gl = _gl;

        // Rebuild dirty VBOs.
        if (_boundaryDirty) { RebuildBoundary(gl); _boundaryDirty = false; }
        if (_headlandDirty) { RebuildHeadland(gl); _headlandDirty = false; }
        if (_tracksDirty)   { RebuildTracks(gl);   _tracksDirty = false; }
        if (_toolDirty)     { RebuildTool(gl);     _toolDirty = false; }

        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        int viewportW = Math.Max(1, (int)(Bounds.Width * scaling));
        int viewportH = Math.Max(1, (int)(Bounds.Height * scaling));
        gl.Viewport(0, 0, (uint)viewportW, (uint)viewportH);

        // Diagnostic clear color: sky blue. With the brown ground quad drawn
        // below, anywhere the camera's view doesn't intersect Z=0 (i.e. "above
        // the horizon") will show sky blue. Brown ground + sky blue makes the
        // world orientation unambiguous in screenshots.
        gl.ClearColor(0.45f, 0.65f, 0.85f, 1f); // sky blue
        gl.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));

        // AOG camera model (ported from AgOpenGPS Camera.cs:41-54).
        // Effective vertex pipeline for world geometry (read top-to-bottom):
        //   v * T(-lookAt) * R_z(+heading)? * T(panX, panY) * R_x(aogPitch) * T(0,0,-dist)
        // - T(-lookAt) puts the vehicle pivot at the GL origin.
        // - R_z(+heading) (heading-up only) rotates the world so the vehicle's
        //   heading direction is up on screen. Vehicle marker counter-rotates
        //   by R_z(-heading) to stay upright.
        // - T(pan) lets the user offset the camera independently.
        // - R_x(aogPitch) tilts the world; 0 = overhead, -65 = AOG 3D default.
        // - T(0,0,-dist) moves the camera back along Z.
        //
        // MainViewModel.CameraPitch uses -90 = overhead, -10 = horizon; AOG's
        // PitchInDegrees uses 0 = overhead, -65 = tilted. Convert with
        //   aogPitch = -(_pitchDegrees + 90)
        // so MainVM -90 -> 0, -60 -> -30, -25 -> -65.
        float vx, vy;
        if (_hasVehicle) { vx = (float)_vehicleX; vy = (float)_vehicleY; }
        else if (_boundary?.OuterBoundary?.Points is { Count: > 0 } b)
        {
            double sumE = 0, sumN = 0;
            foreach (var pt in b) { sumE += pt.Easting; sumN += pt.Northing; }
            vx = (float)(sumE / b.Count); vy = (float)(sumN / b.Count);
        }
        else { vx = 0; vy = 0; }

        float aogPitchRad = MathF.PI * (-(float)_pitchDegrees - 90f) / 180f;
        // AOG-matched distance formula: distance = 0.5 * zoomScalar^2 where
        // zoomScalar ranges (4, 60). At zoomScalar=9 (AOG default 3D) we get
        // distance=40.5m. We map MainViewModel's _zoomLevel (1.0 default) to
        // zoomScalar via: zoomScalar = 9 / zoom. Higher zoom-out (smaller
        // _zoomLevel) → bigger zoomScalar → much bigger distance, same curve
        // AOG uses. At _zoomLevel=1.0 → zoomScalar=9 → distance=40.5m. At
        // _zoomLevel=0.2 → zoomScalar=45 → distance=1012m (matching AOG when
        // user dials its zoom out comparably).
        float zoomScalar = MathF.Max(4f, MathF.Min(60f, 9f / MathF.Max(0.05f, (float)_zoomLevel)));
        float distance = 0.5f * zoomScalar * zoomScalar;
        float headingRad = (float)_vehicleHeading;

        var t1 = Matrix4x4.CreateTranslation(-vx, -vy, 0);
        var rZ = _followHeading ? Matrix4x4.CreateRotationZ(headingRad) : Matrix4x4.Identity;
        var pan = Matrix4x4.CreateTranslation(_panX, _panY, 0);
        var rX = Matrix4x4.CreateRotationX(aogPitchRad);
        var tBack = Matrix4x4.CreateTranslation(0, 0, -distance);
        var view = t1 * rZ * pan * rX * tBack;
        // FOV: AOG uses 0.7 rad (40°). Narrower than the previous 50°, which
        // matches AOG's perspective behavior across zoom changes.
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(
            0.7f,
            viewportW / (float)viewportH,
            1f, MathF.Max(5000f, distance * 4f));
        var mvp = view * proj;

        // 2.5D map overlays: everything is essentially flat on top of the
        // ground. Skip depth so far-distance precision quantization on Apple
        // GL doesn't drop horizontal line edges (see spike findings).
        gl.Disable(EnableCap.DepthTest);
        gl.LineWidth(1f);

        // Infinite ground plane via fragment-shader ray-cast: render a
        // fullscreen quad, per-pixel intersect with Z=0 plane. Brown where
        // ground is visible (ray hits Z=0 in front of camera), blue
        // elsewhere. Produces a true horizon with no quad-edge ambiguity.
        // Uses its own shader program with the inverse MVP as a uniform.
        Matrix4x4.Invert(mvp, out var invMvp);
        gl.UseProgram(_groundProgram);
        unsafe { gl.UniformMatrix4(_uniformGroundInvMvp, 1, false, (float*)&invMvp); }
        gl.BindVertexArray(_groundVao);
        gl.DrawArrays(GLEnum.TriangleStrip, 0, 4);

        // Switch to the line shader for all the boundary/track/vehicle overlays.
        gl.UseProgram(_program);
        SetMvp(gl, mvp);

        // World-aligned 10m grid — drawn on top of the ground quad.
        if (_gridCount > 0)
        {
            SetColor(gl, 0.30f, 0.20f, 0.10f, 1f); // darker brown for visibility on light brown
            gl.BindVertexArray(_gridVao);
            gl.DrawArrays(GLEnum.Lines, 0, (uint)_gridCount);
        }

        // Outer boundary — yellow
        if (_outerCount > 0)
        {
            SetColor(gl, 1.0f, 0.85f, 0.20f, 1f);
            gl.BindVertexArray(_outerVao);
            gl.DrawArrays(GLEnum.Lines, 0, (uint)_outerCount);
        }

        // Inner boundaries — orange-ish
        if (_innerBoundaries.Count > 0)
        {
            SetColor(gl, 0.95f, 0.55f, 0.20f, 1f);
            foreach (var (vao, _, count) in _innerBoundaries)
            {
                gl.BindVertexArray(vao);
                gl.DrawArrays(GLEnum.Lines, 0, (uint)count);
            }
        }

        // Headland — green
        if (_isHeadlandVisible && _headlandCount > 0)
        {
            SetColor(gl, 0.30f, 0.95f, 0.30f, 1f);
            gl.BindVertexArray(_headlandVao);
            gl.DrawArrays(GLEnum.Lines, 0, (uint)_headlandCount);
        }

        // Base track (the user's reference line) — gray
        if (_baseTrackCount > 0)
        {
            SetColor(gl, 0.60f, 0.60f, 0.60f, 1f);
            gl.BindVertexArray(_baseTrackVao);
            gl.DrawArrays(GLEnum.Lines, 0, (uint)_baseTrackCount);
        }

        // Active track — bright cyan
        if (_activeTrackCount > 0)
        {
            SetColor(gl, 0.20f, 0.95f, 0.95f, 1f);
            gl.BindVertexArray(_activeTrackVao);
            gl.DrawArrays(GLEnum.Lines, 0, (uint)_activeTrackCount);
        }

        // Next track (the one the U-turn leads to) — muted blue
        if (_nextTrackCount > 0)
        {
            SetColor(gl, 0.30f, 0.55f, 0.95f, 1f);
            gl.BindVertexArray(_nextTrackVao);
            gl.DrawArrays(GLEnum.Lines, 0, (uint)_nextTrackCount);
        }

        // Tool footprint — light gray segment
        if (_toolCount > 0)
        {
            SetColor(gl, 0.85f, 0.85f, 0.85f, 1f);
            gl.BindVertexArray(_toolVao);
            gl.DrawArrays(GLEnum.Lines, 0, (uint)_toolCount);
        }

        // Vehicle marker — red arrow rotated to heading.
        if (_hasVehicle)
        {
            var vehicleModel =
                Matrix4x4.CreateRotationZ(-(float)_vehicleHeading) *
                Matrix4x4.CreateTranslation(vx, vy, 0f);
            SetMvp(gl, vehicleModel * mvp);
            SetColor(gl, 1.0f, 0.30f, 0.30f, 1f);
            gl.BindVertexArray(_vehicleVao);
            gl.DrawArrays(GLEnum.Lines, 0, (uint)_vehicleCount);
            // Restore base MVP for any future draws.
            SetMvp(gl, mvp);
        }

        if (!_logged)
        {
            _logged = true;
            var vendor = gl.GetStringS(StringName.Vendor);
            var renderer = gl.GetStringS(StringName.Renderer);
            var version = gl.GetStringS(StringName.Version);
            Console.WriteLine($"[GlMap] Vendor={vendor} Renderer={renderer} Version={version}");
            Console.WriteLine($"[GlMap] Viewport={viewportW}x{viewportH} scaling={scaling:F2}");
        }

        // 1 Hz diagnostic: where is the vehicle projecting on screen + how
        // many VBO rebuilds have we accumulated since startup. Cheap and
        // useful for spotting per-frame churn regressions (rebBnd/rebHead/
        // rebTracks should be flat once a field is open; rebTool ticks at
        // GPS rate while the sim runs).
        _diagFrames++;
        if (_diagFrames >= 30)
        {
            _diagFrames = 0;
            var vehWorld = new Vector4(vx, vy, 0.6f, 1f);
            var clip = Vector4.Transform(vehWorld, mvp);
            float ndcX = clip.W != 0 ? clip.X / clip.W : 0;
            float ndcY = clip.W != 0 ? clip.Y / clip.W : 0;
            float scrX = (ndcX + 1f) * 0.5f * viewportW;
            float scrY = (1f - ndcY) * 0.5f * viewportH;
            Console.WriteLine($"[GlMap] vp={viewportW}x{viewportH} pitch={_pitchDegrees:F1} zoom={_zoomLevel:F2} dist={distance:F1} veh=({vx:F2},{vy:F2}) vehScrPx=({scrX:F0},{scrY:F0}) hasVeh={_hasVehicle}");
            Console.WriteLine($"[GlMap-CNTS] rebBnd={_rebuildBndCount} rebHead={_rebuildHeadCount} rebTracks={_rebuildTracksCount} rebTool={_rebuildToolCount}");
        }

        RequestNextFrameRendering();
    }

    private int _diagFrames;
    private int _rebuildBndCount, _rebuildHeadCount, _rebuildTracksCount, _rebuildToolCount;

    // ----- Geometry rebuild ------------------------------------------------------

    private void RebuildBoundary(GL gl)
    {
        DeleteVao(gl, ref _outerVao, ref _outerVbo);
        foreach (var (vao, vbo, _) in _innerBoundaries) { uint v = vao, b = vbo; DeleteVao(gl, ref v, ref b); }
        _innerBoundaries.Clear();
        _outerCount = 0;

        if (_boundary?.OuterBoundary?.Points is { Count: >= 2 } outer)
        {
            var (vao, vbo, count) = BuildPolygonOutline(gl, outer);
            _outerVao = vao; _outerVbo = vbo; _outerCount = count;
            // Diagnostic: confirm boundary VBO contents on each rebuild — if this
            // fires per-frame the boundary is being uploaded every render (bug);
            // if it fires once and the first-3-vert values stay stable that
            // rules out world-coord churn as the motion-artifact source.
            var p0 = outer[0]; var p1 = outer[1]; var pN = outer[outer.Count - 1];
            Console.WriteLine($"[GlMap-REB-BND] #{++_rebuildBndCount} count={count} p0=({p0.Easting:F3},{p0.Northing:F3}) p1=({p1.Easting:F3},{p1.Northing:F3}) pN=({pN.Easting:F3},{pN.Northing:F3})");
        }
        else
        {
            _rebuildBndCount++;
            Console.WriteLine($"[GlMap-REB-BND] #{_rebuildBndCount} (cleared)");
        }

        if (_boundary?.InnerBoundaries is { Count: > 0 } inners)
        {
            foreach (var poly in inners)
            {
                if (poly.Points.Count < 2) continue;
                _innerBoundaries.Add(BuildPolygonOutline(gl, poly.Points));
            }
        }
    }

    private static (uint Vao, uint Vbo, int Count) BuildPolygonOutline(GL gl, List<BoundaryPoint> pts)
    {
        // Closed polygon: N points → N line segments (each a pair of verts).
        int n = pts.Count;
        var verts = new float[n * 2 * 3];
        for (int i = 0; i < n; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % n];
            int o = i * 6;
            verts[o + 0] = (float)a.Easting; verts[o + 1] = (float)a.Northing; verts[o + 2] = 0.1f;
            verts[o + 3] = (float)b.Easting; verts[o + 4] = (float)b.Northing; verts[o + 5] = 0.1f;
        }
        var (vao, vbo) = BuildStaticVao(gl, verts);
        return (vao, vbo, n * 2);
    }

    private void RebuildHeadland(GL gl)
    {
        DeleteVao(gl, ref _headlandVao, ref _headlandVbo);
        _headlandCount = 0;
        _rebuildHeadCount++;
        Console.WriteLine($"[GlMap-REB-HEAD] #{_rebuildHeadCount} pts={_headlandLine?.Count ?? 0}");
        if (_headlandLine is not { Count: >= 2 } pts) return;

        // Closed polyline (the headland loops around the field interior).
        int n = pts.Count;
        var verts = new float[n * 2 * 3];
        for (int i = 0; i < n; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % n];
            int o = i * 6;
            verts[o + 0] = (float)a.Easting; verts[o + 1] = (float)a.Northing; verts[o + 2] = 0.2f;
            verts[o + 3] = (float)b.Easting; verts[o + 4] = (float)b.Northing; verts[o + 5] = 0.2f;
        }
        (_headlandVao, _headlandVbo) = BuildStaticVao(gl, verts);
        _headlandCount = n * 2;
    }

    private void RebuildTracks(GL gl)
    {
        DeleteVao(gl, ref _activeTrackVao, ref _activeTrackVbo); _activeTrackCount = 0;
        DeleteVao(gl, ref _baseTrackVao,   ref _baseTrackVbo);   _baseTrackCount = 0;
        DeleteVao(gl, ref _nextTrackVao,   ref _nextTrackVbo);   _nextTrackCount = 0;
        _rebuildTracksCount++;
        Console.WriteLine($"[GlMap-REB-TRACKS] #{_rebuildTracksCount} active={_activeTrack?.Points.Count ?? 0} base={_baseTrack?.Points.Count ?? 0} next={_nextTrack?.Points.Count ?? 0}");

        if (_activeTrack is { Points.Count: >= 2 })
            (_activeTrackVao, _activeTrackVbo, _activeTrackCount) = BuildPolyline(gl, _activeTrack.Points, 0.3f);
        if (_baseTrack is { Points.Count: >= 2 })
            (_baseTrackVao, _baseTrackVbo, _baseTrackCount) = BuildPolyline(gl, _baseTrack.Points, 0.25f);
        if (_nextTrack is { Points.Count: >= 2 })
            (_nextTrackVao, _nextTrackVbo, _nextTrackCount) = BuildPolyline(gl, _nextTrack.Points, 0.3f);
    }

    private static (uint Vao, uint Vbo, int Count) BuildPolyline(GL gl, List<Vec3> pts, float z)
    {
        // Open polyline: (N-1) line segments, each a pair of verts.
        int n = pts.Count;
        var verts = new float[(n - 1) * 2 * 3];
        for (int i = 0; i < n - 1; i++)
        {
            var a = pts[i];
            var b = pts[i + 1];
            int o = i * 6;
            verts[o + 0] = (float)a.Easting; verts[o + 1] = (float)a.Northing; verts[o + 2] = z;
            verts[o + 3] = (float)b.Easting; verts[o + 4] = (float)b.Northing; verts[o + 5] = z;
        }
        var (vao, vbo) = BuildStaticVao(gl, verts);
        return (vao, vbo, (n - 1) * 2);
    }

    private void RebuildTool(GL gl)
    {
        DeleteVao(gl, ref _toolVao, ref _toolVbo);
        _toolCount = 0;
        _rebuildToolCount++;
        // Tool is expected to rebuild every GPS tick (vehicle/tool position
        // pushed via SetAllPositions); counter shows up in the 1-Hz CNTS line.
        if (!_toolReady || _toolWidth <= 0) return;

        // Tool centerline → a transverse segment at the tool position
        // (perpendicular to heading), one tool-width wide. Cheap stand-in for
        // a real tool sprite; Phase 4 swaps in the section-strip rendering.
        float half = (float)(_toolWidth * 0.5);
        float cos = MathF.Cos((float)_toolHeading);
        float sin = MathF.Sin((float)_toolHeading);
        float lx = (float)_toolX - half * cos;
        float ly = (float)_toolY + half * sin;
        float rx = (float)_toolX + half * cos;
        float ry = (float)_toolY - half * sin;

        // Hitch line from vehicle anchor to tool pivot, plus the transverse
        // segment showing tool width.
        float[] verts =
        {
            (float)_hitchX, (float)_hitchY, 0.4f,   (float)_toolX, (float)_toolY, 0.4f,
            lx, ly, 0.4f,   rx, ry, 0.4f,
        };
        (_toolVao, _toolVbo) = BuildStaticVao(gl, verts);
        _toolCount = verts.Length / 3;
    }

    // ----- GL helpers ------------------------------------------------------------

    private unsafe void SetMvp(GL gl, Matrix4x4 m)
    {
        // transpose=FALSE: we upload our row-major Matrix4x4 bytes; GL interprets
        // them as column-major; GLSL sees our matrix transposed. Shader's
        // column-vector M*v_col then equals our row-vector v*M, which gives
        // correct perspective foreshortening. transpose=true (the previous
        // setting) produces orthographic-like output where clip.w doesn't
        // depend on world Y — the bug behind the "no horizon convergence",
        // "tractor doesn't move away from boundary", and "world rotates"
        // perceptions reported across the spike.
        gl.UniformMatrix4(_uniformMvp, 1, false, (float*)&m);
    }

    private void SetColor(GL gl, float r, float g, float b, float a)
    {
        gl.Uniform4(_uniformColor, r, g, b, a);
    }

    private static uint CompileAndLink(GL gl, string vs, string fs)
    {
        uint vsId = CompileShader(gl, ShaderType.VertexShader, vs);
        uint fsId = CompileShader(gl, ShaderType.FragmentShader, fs);
        uint prog = gl.CreateProgram();
        gl.AttachShader(prog, vsId);
        gl.AttachShader(prog, fsId);
        gl.BindAttribLocation(prog, 0, "in_pos");
        gl.LinkProgram(prog);
        gl.GetProgram(prog, ProgramPropertyARB.LinkStatus, out int linked);
        if (linked == 0) Console.WriteLine($"[GlMap] Program link log: {gl.GetProgramInfoLog(prog)}");
        gl.DeleteShader(vsId);
        gl.DeleteShader(fsId);
        return prog;
    }

    private static uint CompileShader(GL gl, ShaderType type, string source)
    {
        uint s = gl.CreateShader(type);
        gl.ShaderSource(s, source);
        gl.CompileShader(s);
        gl.GetShader(s, ShaderParameterName.CompileStatus, out int ok);
        if (ok == 0) Console.WriteLine($"[GlMap] {type} compile error: {gl.GetShaderInfoLog(s)}");
        return s;
    }

    private static unsafe (uint vao, uint vbo) BuildStaticVao(GL gl, float[] verts)
    {
        uint vao = gl.GenVertexArray();
        gl.BindVertexArray(vao);
        uint vbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        fixed (float* p = verts)
            gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(verts.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), (void*)0);
        gl.EnableVertexAttribArray(0);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
        gl.BindVertexArray(0);
        return (vao, vbo);
    }

    private sealed class AvaloniaGlInterfaceContext : INativeContext
    {
        private readonly GlInterface _gl;
        public AvaloniaGlInterfaceContext(GlInterface gl) => _gl = gl;
        public IntPtr GetProcAddress(string proc, int? slot = null) => _gl.GetProcAddress(proc);
        public bool TryGetProcAddress(string proc, out IntPtr addr, int? slot = null)
        {
            addr = _gl.GetProcAddress(proc);
            return addr != IntPtr.Zero;
        }
        public void Dispose() { }
    }
}
