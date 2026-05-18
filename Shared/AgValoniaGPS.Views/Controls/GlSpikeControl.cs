// SPIKE: minimal OpenGL ES rendering of a 3D scene via Avalonia's OpenGlControlBase,
// using Silk.NET.OpenGLES for the GL API. Goal is to confirm the GL path renders
// identically on Desktop / iOS / Android before committing to it as AgValoniaGPS's
// 2.5D map renderer.

using System;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Silk.NET.Core.Contexts;
using Silk.NET.OpenGLES;

namespace AgValoniaGPS.Views.Controls;

public class GlSpikeControl : OpenGlControlBase
{
    public GlSpikeControl()
    {
        Console.WriteLine("[GlSpike] GlSpikeControl ctor");
        AttachedToVisualTree += (_, _) => Console.WriteLine("[GlSpike] AttachedToVisualTree");
    }

    protected override void OnOpenGlLost()
    {
        Console.WriteLine("[GlSpike] OnOpenGlLost — context was lost / not available");
    }

    private GL? _gl;
    private uint _program;
    private int _uniformMvp;
    private int _uniformColor;
    private uint _groundVao, _groundVbo;
    private uint _boundaryVao, _boundaryVbo;
    private uint _vehicleVao, _vehicleVbo;
    private int _boundaryVertexCount;
    private bool _logged;

    // Shaders without a #version line — added at compile time based on detected
    // GL flavor (GLSL 410 core on desktop OpenGL, GLSL ES 300 on mobile GLES).
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
        Console.WriteLine($"[GlSpike] OnOpenGlInit called. GlVersion={GlVersion}");
        // Bridge Avalonia's GlInterface (which only owns GetProcAddress for us
        // here) to Silk.NET via INativeContext. Silk.NET picks up the full GLES
        // 3.0 surface from this loader.
        _gl = GL.GetApi(new AvaloniaGlInterfaceContext(glInterface));

        // Pick a shader version header based on the GL context flavor we got.
        // Desktop OpenGL needs GLSL 330+ core; GLES needs GLSL ES 300.
        string version = GlVersion.Type == GlProfileType.OpenGLES
            ? "#version 300 es\n"
            : "#version 330 core\n";
        _program = CompileAndLink(_gl, version + VertexShaderBody, version + FragmentShaderBody);
        _uniformMvp = _gl.GetUniformLocation(_program, "u_mvp");
        _uniformColor = _gl.GetUniformLocation(_program, "u_color");

        // Ground quad — 200m × 200m flat plane centered at origin. World coords:
        // X east, Y north, Z up. Two triangles, six vertices.
        float[] groundVerts =
        {
            -100f, -100f, 0f,   +100f, -100f, 0f,   +100f, +100f, 0f,
            -100f, -100f, 0f,   +100f, +100f, 0f,   -100f, +100f, 0f,
        };
        (_groundVao, _groundVbo) = BuildVao(_gl, groundVerts);

        // Boundary outline — 60m × 40m rectangle. Lifted to z=0.5 so the depth
        // buffer at far=1000/near=1 has enough precision to keep horizontal
        // segments visibly above the ground (previously z=0.05 was z-fighting
        // and horizontal lines were losing the test on Apple GL).
        // Drawn as 4 explicit LINES (8 vertices) — LINE_LOOP is also flaky on
        // some drivers, plain LINES is the most portable primitive.
        // 20m × 15m, shifted so the whole rect sits within the camera frustum.
        // All edges traversed LEFT→RIGHT or BOTTOM→TOP (consistent winding).
        // Mixing directions revealed an Apple GL driver quirk on M4 where
        // right-to-left horizontal lines weren't rasterizing.
        float[] boundaryVerts =
        {
            -10f, +20f, 0.5f,   +10f, +20f, 0.5f, // top    (W → E)
            +10f,  +5f, 0.5f,   +10f, +20f, 0.5f, // right  (S → N)
            -10f,  +5f, 0.5f,   +10f,  +5f, 0.5f, // bottom (W → E)
            -10f,  +5f, 0.5f,   -10f, +20f, 0.5f, // left   (S → N)
        };
        (_boundaryVao, _boundaryVbo) = BuildVao(_gl, boundaryVerts);
        _boundaryVertexCount = boundaryVerts.Length / 3;

        // Vehicle marker — small cross at origin, raised above the boundary.
        float[] vehicleVerts =
        {
            -3f, 0f, 1.0f,   +3f, 0f, 1.0f,
            0f, -3f, 1.0f,   0f, +3f, 1.0f,
        };
        (_vehicleVao, _vehicleVbo) = BuildVao(_gl, vehicleVerts);
    }

    protected override void OnOpenGlDeinit(GlInterface glInterface)
    {
        if (_gl == null) return;
        var gl = _gl;
        if (_groundVbo != 0) { gl.DeleteBuffer(_groundVbo); _groundVbo = 0; }
        if (_groundVao != 0) { gl.DeleteVertexArray(_groundVao); _groundVao = 0; }
        if (_boundaryVbo != 0) { gl.DeleteBuffer(_boundaryVbo); _boundaryVbo = 0; }
        if (_boundaryVao != 0) { gl.DeleteVertexArray(_boundaryVao); _boundaryVao = 0; }
        if (_vehicleVbo != 0) { gl.DeleteBuffer(_vehicleVbo); _vehicleVbo = 0; }
        if (_vehicleVao != 0) { gl.DeleteVertexArray(_vehicleVao); _vehicleVao = 0; }
        if (_program != 0) { gl.DeleteProgram(_program); _program = 0; }
        _gl = null;
    }

    protected override unsafe void OnOpenGlRender(GlInterface glInterface, int fb)
    {
        if (_gl == null) return;
        var gl = _gl;

        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        int viewportW = Math.Max(1, (int)(Bounds.Width * scaling));
        int viewportH = Math.Max(1, (int)(Bounds.Height * scaling));
        gl.Viewport(0, 0, (uint)viewportW, (uint)viewportH);

        gl.ClearColor(0.27f, 0.40f, 0.70f, 1f);
        gl.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));
        gl.Enable(EnableCap.DepthTest);

        // Camera: tilted ~30° from straight down, 80m above and 50m south, looking
        // at the origin. World: X east, Y north, Z up. View: -Z forward (GL default).
        var view = Matrix4x4.CreateLookAt(
            new Vector3(0f, -50f, 80f),
            new Vector3(0f, 0f, 0f),
            new Vector3(0f, 0f, 1f));
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI * 50f / 180f,
            viewportW / (float)viewportH,
            1f, 1000f);
        var mvp = view * proj;

        gl.UseProgram(_program);
        SetMvp(gl, mvp);

        // Ground (dark green)
        SetColor(gl, 0.20f, 0.45f, 0.20f, 1f);
        gl.BindVertexArray(_groundVao);
        gl.DrawArrays(GLEnum.Triangles, 0, 6);

        gl.LineWidth(1f);

        // Draw line overlays (boundary, vehicle, tracks, headland, etc.) with
        // depth test disabled — they're a 2.5D-map "on top of the ground"
        // layer, not real 3D geometry that should occlude. Skipping depth test
        // also sidesteps Apple GL's aggressive depth quantization at far
        // distances, which was hiding the boundary's south edge against the
        // ground plane.
        gl.Disable(EnableCap.DepthTest);

        // Boundary outline (yellow)
        SetColor(gl, 1.0f, 0.85f, 0.20f, 1f);
        gl.BindVertexArray(_boundaryVao);
        gl.DrawArrays(GLEnum.Lines, 0, (uint)_boundaryVertexCount);

        // Vehicle cross (red)
        SetColor(gl, 1.0f, 0.30f, 0.30f, 1f);
        gl.BindVertexArray(_vehicleVao);
        gl.DrawArrays(GLEnum.Lines, 0, 4);

        gl.Enable(EnableCap.DepthTest);

        if (!_logged)
        {
            _logged = true;
            var vendor = gl.GetStringS(StringName.Vendor);
            var renderer = gl.GetStringS(StringName.Renderer);
            var version = gl.GetStringS(StringName.Version);
            Console.WriteLine($"[GlSpike] Vendor={vendor} Renderer={renderer} Version={version}");
            Console.WriteLine($"[GlSpike] Viewport={viewportW}x{viewportH} scaling={scaling:F2}");
        }

        RequestNextFrameRendering();
    }

    private unsafe void SetMvp(GL gl, Matrix4x4 m)
    {
        // Numerics stores matrices in row-major order. Tell GL to transpose
        // on upload so the column-major shader matrix matches. This is the
        // spec-compliant path and (unlike relying on byte-reinterpretation
        // with transpose=false) renders identically on macOS-GL-4.1 and
        // Android-GLES-3.0 — the Silk.NET bool marshaling for transpose=false
        // was producing different results on the two platforms.
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
        if (linked == 0) Console.WriteLine($"[GlSpike] Program link log: {gl.GetProgramInfoLog(prog)}");
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
        if (ok == 0) Console.WriteLine($"[GlSpike] {type} compile error: {gl.GetShaderInfoLog(s)}");
        return s;
    }

    private static unsafe (uint vao, uint vbo) BuildVao(GL gl, float[] verts)
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

    /// <summary>
    /// Adapter so Silk.NET can resolve GLES function pointers via Avalonia's
    /// GlInterface.GetProcAddress on whatever native context Avalonia set up
    /// (CGL/NSOpenGL on macOS, EGL/native GLES on Android, ANGLE on Windows).
    /// </summary>
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
