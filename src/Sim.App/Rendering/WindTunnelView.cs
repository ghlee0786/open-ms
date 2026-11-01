using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Input;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Rendering;
using Silk.NET.OpenGL;
using Sim.Core.Geometry;
using Sim.Core.Models;
using Sim.Core.Structures;
using Mat3 = Sim.Core.Numerics.Mat3;
using Vec3 = Sim.Core.Numerics.Vec3;

namespace Sim.App.Rendering;

/// <summary>
/// 풍동 보기 3D 뷰(어두운 배경 CFD 후처리 장면): 물체 표면을 정점 값 + 컬러맵 텍스처로 부드럽게 칠하고(값을 보간 → 프래그먼트에서 색 조회),
/// 주위 3-D 유선을 화면 폭 일정한 리본으로, 유선을 따라 흐르는 입자를 둥근 점으로 그린다.
/// 메시 VBO(동체 좌표 위치·평활 법선)는 메시가 바뀔 때만 1벌 올리고, 케이스·장을 바꾸면 값 VBO(정점당 float 1)만 다시 올린다.
/// 받음각 자세는 모델 행렬(풍동 좌표 = R_α(p − 중심))로 — 메시는 다시 올리지 않는다.
/// 좌표는 풍동 좌표(<see cref="WindTunnel"/>, 흐름 −X)를 NED 처럼 다뤄 GlView 와 같은 카메라(ViewCamera)·조작을 쓴다.
/// 조작: 왼쪽 드래그 궤도 회전 · Ctrl+왼쪽 시점 회전 · 오른쪽/가운데(Shift+왼쪽) 드래그 이동 · 휠 커서 위치 확대 · 더블클릭 전체 보기.
/// </summary>
public sealed class WindTunnelView : OpenGlControlBase, ICustomHitTest
{
    private const float Fov = (float)ViewCamera.Fov;
    private const float NoValue = -1e30f;

    private GL? _gl;
    private bool _isEs;
    private uint _surfProg, _lineProg, _vao, _meshVbo, _valVbo, _tex, _dynVao, _dynVbo, _quadProg, _quadVao, _quadVbo, _sliceTex;
    private int _uMvp, _uModel, _uEye, _uRange, _uMode, _uFlat, _uMap, _uLMvp, _uPointSize, _uRound, _uQMvp, _uQTex;
    private readonly MsaaTarget _msaa = new();
    private int _meshCount, _meshUploaded = -1, _valUploaded = -1, _mapUploaded = -1;
    private readonly List<float> _dyn = new(1 << 16);
    private readonly List<(Vec3 P, double S)> _parts = new(4096);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private string? _snapshotPath;

    private readonly ViewCamera _cam = new();
    private Point _last, _press;
    private bool _rotating, _looking, _panning, _dragged;

    // ---------------- 장면(창이 채움) ----------------
    private float[] _mesh = [];
    private int _meshVersion;
    private float[] _values = [];
    private int _valueVersion;
    private string _map = "Turbo";
    private int _mapVersion;
    private IReadOnlyList<(Vec3[] P, float[] Rgba)> _lines = [];

    /// <summary>물체 메시(동체 좌표)와 꼭짓점 법선(정점 수 = 메시 정점 수) — 바뀔 때만 GPU 로 올린다.</summary>
    public void SetMesh(TriMesh mesh, Vec3[] normals)
    {
        var d = new float[mesh.Vertices.Count * 6];
        for (int i = 0; i < mesh.Vertices.Count; i++)
        {
            var p = mesh.Vertices[i]; var n = normals[i];
            d[6 * i] = (float)p.X; d[6 * i + 1] = (float)p.Y; d[6 * i + 2] = (float)p.Z;
            d[6 * i + 3] = (float)n.X; d[6 * i + 4] = (float)n.Y; d[6 * i + 5] = (float)n.Z;
        }
        _mesh = d;
        _meshVersion++;
        RequestNextFrameRendering();
    }

    /// <summary>꼭짓점마다 장 값(NaN = 자료 없음 회색)과 색 범위·컬러맵.</summary>
    public void SetValues(double[] perVertex, ScaleRange range, string colorMap)
    {
        var v = new float[perVertex.Length];
        for (int i = 0; i < v.Length; i++) v[i] = double.IsFinite(perVertex[i]) ? (float)perVertex[i] : NoValue;
        _values = v;
        _valueVersion++;
        Range = range;
        if (colorMap != _map) { _map = colorMap; _mapVersion++; }
        RequestNextFrameRendering();
    }

    public ScaleRange Range { get; private set; } = new(0, 1, false);
    /// <summary>표면 색 끔 = 회색 물체.</summary>
    public bool ShowSurface { get; set; } = true;
    /// <summary>모델 자세: 받음각 · 옆미끄럼 [deg] · 물체 중심(동체 좌표) — 풍동 좌표 = R(α, β)(p − 중심), R = Rz(−β)·Ry(α).</summary>
    public double AlphaDeg { get; set; }
    public double BetaDeg { get; set; }
    public Vec3 BodyCenter { get; set; }

    // ---------------- GPU 가상 풍동(실시간 장) ----------------
    private byte[] _sliceRgba = [];
    private int _sliceW, _sliceH, _sliceVersion, _sliceUploaded = -1;
    private Vec3 _sliceO, _sliceA, _sliceB;
    private float[] _points = [];

    /// <summary>단면 사각형(풍동 좌표: 원점 + a·u + b·v, u,v ∈ [0,1])에 RGBA 텍스처(w = a 방향 셀 수, h = b 방향, 행 = b) — null 이면 끔.</summary>
    public void SetSlice(Vec3 origin, Vec3 axisA, Vec3 axisB, int w, int h, byte[]? rgba)
    {
        if (rgba is null || w <= 0 || h <= 0 || rgba.Length != 4 * w * h) { _sliceRgba = []; _sliceW = _sliceH = 0; }
        else { _sliceRgba = rgba; _sliceW = w; _sliceH = h; }
        _sliceO = origin; _sliceA = axisA; _sliceB = axisB;
        _sliceVersion++;
        RequestNextFrameRendering();
    }

    public bool HasSlice => _sliceW > 0;

    /// <summary>실시간 입자(풍동 좌표 x, y, z, 속력비 — 4 개씩). 색 = <see cref="ParticleColor"/>(없으면 흰 연기).</summary>
    public float[] LivePoints { get => _points; set { _points = value ?? []; RequestNextFrameRendering(); } }
    public double LivePointSize { get; set; } = 3.2;

    /// <summary>유선(풍동 좌표 점, 점마다 RGBA).</summary>
    public IReadOnlyList<(Vec3[] P, float[] Rgba)> Lines { get => _lines; set { _lines = value; RequestNextFrameRendering(); } }
    /// <summary>유선 화면 폭 [px].</summary>
    public double LineWidthPx { get; set; } = 1.8;
    /// <summary>입자(풍동 좌표). null = 끔.</summary>
    public StreamlineParticles? Particles { get; set; }
    public bool Animate { get; set; } = true;
    /// <summary>초당 τ 진행(자유류 속력 기준 m/s 에 해당하는 화면 속도) · 입자 τ 간격.</summary>
    public double ParticleRate { get; set; } = 0.9;
    public double ParticleSpacing { get; set; } = 0.7;
    /// <summary>입자 색 = 속도 컬러맵(아니면 밝은 흰색).</summary>
    public Func<double, (float R, float G, float B)>? ParticleColor { get; set; }

    public string GlInfo { get; private set; } = "";
    public long FramesRendered { get; private set; }
    public int ParticleCount { get; private set; }

    public WindTunnelView()
    {
        Focusable = true;
        ClipToBounds = true;
        DoubleTapped += (_, e) => { Fit(); e.Handled = true; };
    }

    // ---------------- 카메라 ----------------

    private double _fitRadius = 1;

    /// <summary>장면 반경 [m] — 전체 보기 거리 기준.</summary>
    public double SceneRadius { get => _fitRadius; set => _fitRadius = Math.Max(value, 0.05); }

    public void Fit()
    {
        _cam.LookAt(Vec3.Zero, Math.Clamp(_fitRadius / Math.Sin(Fov / 2) * 1.05, 0.3, 1000));
        RequestNextFrameRendering();
    }

    public void ResetView()
    {
        _cam.SetAngles(-2.2, 0.32);
        Fit();
    }

    public ViewCamera Camera => _cam;

    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var p = e.GetCurrentPoint(this);
        _last = _press = p.Position;
        _dragged = false;
        bool shift = (e.KeyModifiers & KeyModifiers.Shift) != 0, ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0;
        _panning = p.Properties.IsRightButtonPressed || p.Properties.IsMiddleButtonPressed || (p.Properties.IsLeftButtonPressed && shift);
        _looking = !_panning && p.Properties.IsLeftButtonPressed && ctrl;
        _rotating = !_panning && !_looking && p.Properties.IsLeftButtonPressed;
        e.Pointer.Capture(this);
        Focus();
        base.OnPointerPressed(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        _rotating = _looking = _panning = false;
        e.Pointer.Capture(null);
        base.OnPointerReleased(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var pos = e.GetPosition(this);
        var d = pos - _last;
        _last = pos;
        bool any = _rotating || _looking || _panning;
        if (any && !_dragged)
        {
            var t = pos - _press;
            if (Math.Abs(t.X) + Math.Abs(t.Y) < 4) { base.OnPointerMoved(e); return; }
            _dragged = true;
            d = t;
        }
        _cam.SetViewport(Bounds.Width, Bounds.Height);
        if (_rotating) _cam.OrbitDrag(d.X, d.Y);
        else if (_looking) _cam.LookDrag(d.X, d.Y);
        else if (_panning) _cam.PanPixels(d.X, d.Y);
        if (any) RequestNextFrameRendering();
        base.OnPointerMoved(e);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        var pos = e.GetPosition(this);
        _cam.SetViewport(Bounds.Width, Bounds.Height);
        _cam.Wheel(e.Delta.Y, pos.X, pos.Y);
        RequestNextFrameRendering();
        e.Handled = true;
        base.OnPointerWheelChanged(e);
    }

    public void RequestSnapshot(string path)
    {
        _snapshotPath = path;
        RequestNextFrameRendering();
    }

    // ---------------- GL ----------------

    protected override unsafe void OnOpenGlInit(GlInterface gli)
    {
        base.OnOpenGlInit(gli);
        var gl = _gl = GL.GetApi(gli.GetProcAddress);
        _isEs = GlVersion.Type == GlProfileType.OpenGLES;
        string head = GlKit.Header(_isEs);
        _surfProg = GlKit.Program(gl, head + """
            layout(location=0) in vec3 aPos;
            layout(location=1) in vec3 aNormal;
            layout(location=2) in float aVal;
            uniform mat4 uMvp;
            uniform mat4 uModel;
            out vec3 vN;
            out vec3 vW;
            out float vV;
            void main(){ vN = mat3(uModel) * aNormal; vW = (uModel * vec4(aPos, 1.0)).xyz; vV = aVal; gl_Position = uMvp * vec4(aPos, 1.0); }
            """, head + """
            uniform sampler2D uMap;
            uniform vec2 uRange;
            uniform vec3 uEye;
            uniform int uMode;
            uniform vec3 uFlat;
            in vec3 vN;
            in vec3 vW;
            in float vV;
            out vec4 FragColor;
            void main(){
              vec3 base = uFlat;
              if (uMode == 1) {
                if (vV < -1e19) base = vec3(0.42, 0.44, 0.47);
                else {
                  float t = clamp((vV - uRange.x) / max(uRange.y - uRange.x, 1e-12), 0.0, 1.0);
                  base = texture(uMap, vec2(t * (255.0 / 256.0) + 0.5 / 256.0, 0.5)).rgb;
                }
              }
              vec3 n = normalize(vN);
              vec3 v = normalize(uEye - vW);
              if (dot(n, v) < 0.0) n = -n;
              vec3 l = normalize(vec3(0.35, 1.0, 0.25));
              float diff = 0.38 + 0.42 * max(dot(n, v), 0.0) + 0.25 * max(dot(n, l), 0.0);
              float spec = pow(max(dot(n, normalize(l + v)), 0.0), 48.0) * 0.22;
              FragColor = vec4(base * diff + vec3(spec), 1.0);
            }
            """);
        _lineProg = GlKit.Program(gl, head + """
            layout(location=0) in vec3 aPos;
            layout(location=1) in vec4 aColor;
            uniform mat4 uMvp;
            uniform float uPointSize;
            out vec4 vC;
            void main(){ vC = aColor; gl_Position = uMvp * vec4(aPos, 1.0); gl_PointSize = uPointSize; }
            """, head + """
            uniform int uRound;
            in vec4 vC;
            out vec4 FragColor;
            void main(){
              float a = vC.a;
              if (uRound == 1) {
                vec2 d = gl_PointCoord * 2.0 - 1.0;
                float r = dot(d, d);
                if (r > 1.0) discard;
                a *= 1.0 - smoothstep(0.55, 1.0, r);
              }
              FragColor = vec4(vC.rgb, a);
            }
            """);
        _uMvp = gl.GetUniformLocation(_surfProg, "uMvp");
        _uModel = gl.GetUniformLocation(_surfProg, "uModel");
        _uEye = gl.GetUniformLocation(_surfProg, "uEye");
        _uRange = gl.GetUniformLocation(_surfProg, "uRange");
        _uMode = gl.GetUniformLocation(_surfProg, "uMode");
        _uFlat = gl.GetUniformLocation(_surfProg, "uFlat");
        _uMap = gl.GetUniformLocation(_surfProg, "uMap");
        _uLMvp = gl.GetUniformLocation(_lineProg, "uMvp");
        _uPointSize = gl.GetUniformLocation(_lineProg, "uPointSize");
        _uRound = gl.GetUniformLocation(_lineProg, "uRound");
        // 단면(텍스처 사각형) — 실시간 단면 장
        _quadProg = GlKit.Program(gl, head + """
            layout(location=0) in vec3 aPos;
            layout(location=1) in vec2 aUv;
            uniform mat4 uMvp;
            out vec2 vUv;
            void main(){ vUv = aUv; gl_Position = uMvp * vec4(aPos, 1.0); }
            """, head + """
            uniform sampler2D uTex;
            in vec2 vUv;
            out vec4 FragColor;
            void main(){ vec4 c = texture(uTex, vUv); if (c.a < 0.02) discard; FragColor = c; }
            """);
        _uQMvp = gl.GetUniformLocation(_quadProg, "uMvp");
        _uQTex = gl.GetUniformLocation(_quadProg, "uTex");
        _quadVao = gl.GenVertexArray();
        _quadVbo = gl.GenBuffer();
        gl.BindVertexArray(_quadVao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _quadVbo);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 5 * sizeof(float), (void*)0);
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 5 * sizeof(float), (void*)(3 * sizeof(float)));
        gl.BindVertexArray(0);
        _sliceTex = gl.GenTexture();
        _sliceUploaded = -1;

        // 물체: 메시 VBO(위치·법선) + 값 VBO(float) 를 한 VAO 에
        _vao = gl.GenVertexArray();
        _meshVbo = gl.GenBuffer();
        _valVbo = gl.GenBuffer();
        gl.BindVertexArray(_vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _meshVbo);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), (void*)0);
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), (void*)(3 * sizeof(float)));
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _valVbo);
        gl.EnableVertexAttribArray(2);
        gl.VertexAttribPointer(2, 1, VertexAttribPointerType.Float, false, sizeof(float), (void*)0);
        // 유선 리본 · 입자(프레임마다 채우는 동적 버퍼)
        _dynVao = gl.GenVertexArray();
        _dynVbo = gl.GenBuffer();
        gl.BindVertexArray(_dynVao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _dynVbo);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 7 * sizeof(float), (void*)0);
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, 7 * sizeof(float), (void*)(3 * sizeof(float)));
        gl.BindVertexArray(0);
        _tex = gl.GenTexture();
        _meshUploaded = _valUploaded = _mapUploaded = -1;
        GlInfo = $"{gl.GetStringS(StringName.Renderer)} | {gl.GetStringS(StringName.Version)}";
    }

    protected override void OnOpenGlDeinit(GlInterface gli)
    {
        if (_gl is { } gl)
        {
            gl.DeleteProgram(_surfProg); gl.DeleteProgram(_lineProg);
            gl.DeleteVertexArray(_vao); gl.DeleteBuffer(_meshVbo); gl.DeleteBuffer(_valVbo);
            gl.DeleteVertexArray(_dynVao); gl.DeleteBuffer(_dynVbo);
            gl.DeleteTexture(_tex);
            gl.DeleteProgram(_quadProg); gl.DeleteVertexArray(_quadVao); gl.DeleteBuffer(_quadVbo); gl.DeleteTexture(_sliceTex);
            _msaa.Delete(gl);
        }
        _gl = null;
        base.OnOpenGlDeinit(gli);
    }

    private unsafe void Upload(GL gl)
    {
        if (_meshUploaded != _meshVersion)
        {
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, _meshVbo);
            fixed (float* p = _mesh) gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(_mesh.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);
            _meshCount = _mesh.Length / 6;
            _meshUploaded = _meshVersion;
            _valUploaded = -1;
        }
        if (_valUploaded != _valueVersion)
        {
            // 값 개수가 메시와 다르면(메시만 먼저 바뀐 순간) 자료 없음으로 채운다
            var v = _values.Length == _meshCount ? _values : Enumerable.Repeat(NoValue, _meshCount).ToArray();
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, _valVbo);
            fixed (float* p = v) gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(v.Length * sizeof(float)), p, BufferUsageARB.DynamicDraw);
            _valUploaded = _values.Length == _meshCount ? _valueVersion : -2;
        }
        if (_mapUploaded != _mapVersion)
        {
            var rgba = new byte[256 * 4];
            for (int i = 0; i < 256; i++)
            {
                var (r, g, b) = ColorScale.Map(_map, i / 255.0);
                rgba[4 * i] = (byte)Math.Round(r * 255); rgba[4 * i + 1] = (byte)Math.Round(g * 255); rgba[4 * i + 2] = (byte)Math.Round(b * 255); rgba[4 * i + 3] = 255;
            }
            gl.BindTexture(TextureTarget.Texture2D, _tex);
            fixed (byte* p = rgba)
                gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, 256, 1, 0, Silk.NET.OpenGL.PixelFormat.Rgba, PixelType.UnsignedByte, p);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            gl.BindTexture(TextureTarget.Texture2D, 0);
            _mapUploaded = _mapVersion;
        }
        if (_sliceUploaded != _sliceVersion && _sliceW > 0)
        {
            gl.BindTexture(TextureTarget.Texture2D, _sliceTex);
            gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
            fixed (byte* p = _sliceRgba)
                gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)_sliceW, (uint)_sliceH, 0, Silk.NET.OpenGL.PixelFormat.Rgba, PixelType.UnsignedByte, p);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            gl.BindTexture(TextureTarget.Texture2D, 0);
            _sliceUploaded = _sliceVersion;
        }
    }

    protected override unsafe void OnOpenGlRender(GlInterface gli, int fb)
    {
        var gl = _gl;
        if (gl is null) return;
        FramesRendered++;
        double scaling = VisualRoot?.RenderScaling ?? 1.0;
        int w = Math.Max(1, (int)(Bounds.Width * scaling)), h = Math.Max(1, (int)(Bounds.Height * scaling));
        bool msaa = _msaa.Ensure(gl, w, h);
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, msaa ? _msaa.Fbo : (uint)fb);
        gl.Viewport(0, 0, (uint)w, (uint)h);
        gl.ClearColor(0.015f, 0.017f, 0.022f, 1f);
        gl.DepthMask(true);
        gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        gl.Enable(EnableCap.DepthTest);
        gl.DepthFunc(DepthFunction.Lequal);
        gl.Enable(EnableCap.Blend);
        gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        if (!_isEs) gl.Enable(EnableCap.ProgramPointSize);
        Upload(gl);

        _cam.SetViewport(Bounds.Width, Bounds.Height);
        double dist = _cam.Distance;
        var center = _cam.Focus;
        var eyeGl = Frames.ToGl(_cam.Forward * -dist);
        var view = Matrix4x4.CreateLookAt(new Vector3((float)eyeGl.X, (float)eyeGl.Y, (float)eyeGl.Z), Vector3.Zero, Vector3.UnitY);
        float near = (float)Math.Max(0.005, dist * 0.002), far = (float)(dist * 200 + 100);
        var vp = view * GlKit.Perspective(Fov, (float)w / h, near, far);

        // ---- 물체(받음각 자세 = 모델 행렬) ----
        if (_meshCount > 0)
        {
            var rot = Sim.Core.Airflow.AirflowFrames.BodyToTunnel(AlphaDeg, BetaDeg);
            var model = GlKit.Model(rot, Frames.ToGl(rot * -BodyCenter - center));
            var mvp = model * vp;
            gl.UseProgram(_surfProg);
            gl.UniformMatrix4(_uMvp, 1, false, (float*)&mvp);
            gl.UniformMatrix4(_uModel, 1, false, (float*)&model);
            gl.Uniform3(_uEye, (float)eyeGl.X, (float)eyeGl.Y, (float)eyeGl.Z);
            gl.Uniform2(_uRange, (float)Range.Min, (float)Range.Max);
            gl.Uniform1(_uMode, ShowSurface && _valUploaded == _valueVersion ? 1 : 0);
            gl.Uniform3(_uFlat, 0.62f, 0.65f, 0.7f);
            gl.ActiveTexture(TextureUnit.Texture0);
            gl.BindTexture(TextureTarget.Texture2D, _tex);
            gl.Uniform1(_uMap, 0);
            gl.BindVertexArray(_vao);
            gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)_meshCount);
            gl.BindTexture(TextureTarget.Texture2D, 0);
        }

        // ---- 단면(반투명 텍스처 사각형, 양면) ----
        if (_sliceW > 0 && _sliceUploaded == _sliceVersion)
        {
            var o = Frames.ToGl(_sliceO - center);
            var pa = Frames.ToGl(_sliceO + _sliceA - center);
            var pb = Frames.ToGl(_sliceO + _sliceB - center);
            var pab = Frames.ToGl(_sliceO + _sliceA + _sliceB - center);
            // 텍스처 s = a 방향(셀 i), t = b 방향(셀 j)
            float[] q =
            [
                (float)o.X, (float)o.Y, (float)o.Z, 0, 0, (float)pa.X, (float)pa.Y, (float)pa.Z, 1, 0, (float)pab.X, (float)pab.Y, (float)pab.Z, 1, 1,
                (float)o.X, (float)o.Y, (float)o.Z, 0, 0, (float)pab.X, (float)pab.Y, (float)pab.Z, 1, 1, (float)pb.X, (float)pb.Y, (float)pb.Z, 0, 1,
            ];
            gl.UseProgram(_quadProg);
            var vq = vp;
            gl.UniformMatrix4(_uQMvp, 1, false, (float*)&vq);
            gl.ActiveTexture(TextureUnit.Texture0);
            gl.BindTexture(TextureTarget.Texture2D, _sliceTex);
            gl.Uniform1(_uQTex, 0);
            gl.BindVertexArray(_quadVao);
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, _quadVbo);
            fixed (float* p = q) gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(q.Length * sizeof(float)), p, BufferUsageARB.StreamDraw);
            gl.DepthMask(false);
            gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
            gl.DepthMask(true);
            gl.BindTexture(TextureTarget.Texture2D, 0);
        }

        // ---- 유선 리본(화면 폭 일정) ----
        gl.UseProgram(_lineProg);
        var vpc = vp;
        gl.UniformMatrix4(_uLMvp, 1, false, (float*)&vpc);
        gl.Uniform1(_uRound, 0);
        var dyn = _dyn;
        dyn.Clear();
        double pxToM = 2 * Math.Tan(Fov / 2) / Math.Max(Bounds.Height, 1) * LineWidthPx / 2;
        foreach (var (pts, rgba) in _lines)
        {
            for (int i = 1; i < pts.Length; i++)
            {
                var ga = Frames.ToGl(pts[i - 1] - center); var gb = Frames.ToGl(pts[i] - center);
                var dir = gb - ga;
                if (dir.Norm < 1e-9) continue;
                var side = Vec3.Cross(dir, eyeGl - ga);
                if (side.Norm < 1e-12) continue;
                double hw = pxToM * (eyeGl - ga).Norm;
                side = side.Normalized() * hw;
                var sb = side * ((eyeGl - gb).Norm / Math.Max((eyeGl - ga).Norm, 1e-9));
                Quad(dyn, ga - side, ga + side, gb + sb, gb - sb, rgba, i - 1, i);
            }
        }
        if (dyn.Count > 0)
        {
            gl.DepthMask(false);
            Draw(gl, dyn, PrimitiveType.Triangles);
            gl.DepthMask(true);
        }

        // ---- 입자 ----
        ParticleCount = 0;
        if (Particles is { } sp)
        {
            // 애니메이션 시각: 켜져 있는 동안만 흐른다(끄면 그 자리에 멈춤, 프레임이 오래 멈췄다 와도 한 번에 튀지 않게 0.1 s 로 자름)
            double now = _clock.Elapsed.TotalSeconds;
            if (Animate) _animT += Math.Min(now - _lastFrameT, 0.1);
            _lastFrameT = now;
            _parts.Clear();
            sp.Positions(_animT, ParticleRate, ParticleSpacing, _parts);
            dyn.Clear();
            foreach (var (p, sr) in _parts)
            {
                var g = Frames.ToGl(p - center);
                var (r, gg, b) = ParticleColor?.Invoke(sr) ?? (1f, 0.98f, 0.9f);
                dyn.AddRange([(float)g.X, (float)g.Y, (float)g.Z, r, gg, b, 0.85f]);
            }
            ParticleCount = _parts.Count;
            gl.Uniform1(_uRound, 1);
            gl.Uniform1(_uPointSize, (float)(4.5 * scaling));
            gl.DepthMask(false);
            Draw(gl, dyn, PrimitiveType.Points);
            gl.DepthMask(true);
            gl.Uniform1(_uRound, 0);
        }

        // ---- 실시간 입자(연기) ----
        if (_points.Length >= 4)
        {
            dyn.Clear();
            var pts = _points;
            for (int i = 0; i + 3 < pts.Length; i += 4)
            {
                var g = Frames.ToGl(new Vec3(pts[i], pts[i + 1], pts[i + 2]) - center);
                var (r, gg, b) = ParticleColor?.Invoke(pts[i + 3]) ?? (0.93f, 0.95f, 0.98f);
                dyn.AddRange([(float)g.X, (float)g.Y, (float)g.Z, r, gg, b, 0.8f]);
            }
            ParticleCount += pts.Length / 4;
            gl.Uniform1(_uRound, 1);
            gl.Uniform1(_uPointSize, (float)(LivePointSize * scaling));
            gl.DepthMask(false);
            Draw(gl, dyn, PrimitiveType.Points);
            gl.DepthMask(true);
            gl.Uniform1(_uRound, 0);
        }
        gl.BindVertexArray(0);
        gl.UseProgram(0);
        if (msaa) _msaa.Resolve(gl, (uint)fb);
        if (_snapshotPath is { } path)
        {
            _snapshotPath = null;
            try { GlKit.SaveSnapshot(gl, w, h, path); } catch { /* 스냅숏 실패는 화면에 영향 없음 */ }
        }
        if (Animate && Particles is not null) RequestNextFrameRendering();
    }

    private double _animT, _lastFrameT;

    private static void Quad(List<float> d, Vec3 a, Vec3 b, Vec3 c, Vec3 e, float[] rgba, int ia, int ib)
    {
        void V(Vec3 p, int i) => d.AddRange([(float)p.X, (float)p.Y, (float)p.Z, rgba[4 * i], rgba[4 * i + 1], rgba[4 * i + 2], rgba[4 * i + 3]]);
        V(a, ia); V(b, ia); V(c, ib);
        V(a, ia); V(c, ib); V(e, ib);
    }

    private unsafe void Draw(GL gl, List<float> data, PrimitiveType prim)
    {
        if (data.Count == 0) return;
        gl.BindVertexArray(_dynVao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _dynVbo);
        var span = CollectionsMarshal.AsSpan(data);
        fixed (float* p = span) gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(span.Length * sizeof(float)), p, BufferUsageARB.StreamDraw);
        gl.DrawArrays(prim, 0, (uint)(span.Length / 7));
    }
}
