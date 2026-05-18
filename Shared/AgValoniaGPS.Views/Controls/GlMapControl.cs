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

    public void SetBoundary(Boundary? boundary)
    {
        _boundary = boundary;
        _boundaryDirty = true;
    }

    public void SetActiveTrack(Track? track)
    {
        _activeTrack = track;
        _tracksDirty = true;
    }

    public void SetBaseTrack(Track? track)
    {
        _baseTrack = track;
        _tracksDirty = true;
    }

    public void SetNextTrack(Track? track)
    {
        _nextTrack = track;
        _tracksDirty = true;
    }

    public void SetHeadlandLine(IReadOnlyList<Vec3>? headland)
    {
        _headlandLine = headland;
        _headlandDirty = true;
    }

    public void SetHeadlandVisible(bool visible) => _isHeadlandVisible = visible;

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
        if (_program != 0) { gl.DeleteProgram(_program); _program = 0; }
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

        gl.ClearColor(0.13f, 0.20f, 0.13f, 1f); // dark green field background
        gl.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));

        // Camera tracks vehicle (Phase 2). Phase 3 will replace this with a
        // proper map-centric camera that the user can pan/zoom/rotate
        // independently of vehicle motion (see project_map_centric memory).
        // Eye 50m south of target, 80m up, looking at the target. World axes:
        // X east, Y north, Z up. Fallback to boundary center then origin when
        // no GPS fix yet so the scene is at least framed on loaded data.
        float vx, vy;
        if (_hasVehicle)
        {
            vx = (float)_vehicleX; vy = (float)_vehicleY;
        }
        else if (_boundary?.OuterBoundary?.Points is { Count: > 0 } b)
        {
            double sumE = 0, sumN = 0;
            foreach (var pt in b) { sumE += pt.Easting; sumN += pt.Northing; }
            vx = (float)(sumE / b.Count); vy = (float)(sumN / b.Count);
        }
        else { vx = 0; vy = 0; }
        var view = Matrix4x4.CreateLookAt(
            new Vector3(vx, vy - 50f, 80f),
            new Vector3(vx, vy, 0f),
            new Vector3(0f, 0f, 1f));
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI * 50f / 180f,
            viewportW / (float)viewportH,
            1f, 5000f);
        var mvp = view * proj;

        gl.UseProgram(_program);
        SetMvp(gl, mvp);

        // 2.5D map overlays: everything is essentially flat on top of the
        // ground. Skip depth so far-distance precision quantization on Apple
        // GL doesn't drop horizontal line edges (see spike findings).
        gl.Disable(EnableCap.DepthTest);
        gl.LineWidth(1f);

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

        RequestNextFrameRendering();
    }

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
        gl.UniformMatrix4(_uniformMvp, 1, true, (float*)&m);
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
