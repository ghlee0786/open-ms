using System.Numerics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Input;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Rendering;
using Avalonia.Threading;
using Silk.NET.OpenGL;
using Sim.Core.Engine;
using Sim.Core.Geometry;
using Sim.Core.Models;
using Mat3 = Sim.Core.Numerics.Mat3;
using Vec3 = Sim.Core.Numerics.Vec3;

namespace Sim.App.Rendering;

public enum ViewPreset { Top, North, East, Iso }

/// <summary>
/// 3D 뷰 분할 화면 하나의 상태 — 같은 장면·같은 메시 VBO 를 공유하고, 화면마다 다른 것은 면 색(해석 조건)과 카메라뿐.
/// </summary>
public sealed class ViewPane
{
    public ViewPane(int index) => Index = index;
    public int Index { get; }
    /// <summary>이 화면 고유 카메라(동기 해제 때 사용 — 동기 중에는 모든 화면이 0번 카메라를 쓴다).</summary>
    public ViewCamera OwnCamera { get; } = new();
    /// <summary>물체별 면 색(면당 RGB 3 float, 면 순서 = SceneData.BodyMeshes). null = 물체 색.</summary>
    public IReadOnlyDictionary<int, float[]>? FaceColors { get; set; }
    /// <summary>FaceColors 를 교체하면 올린다 — 그 때만 이 화면의 색 버퍼를 GPU 로 다시 올림.</summary>
    public int FaceColorVersion { get; set; }
    /// <summary>연결점 국부 하중 핫스팟(NED 위치, 색) — FR-STR-02(b).</summary>
    public IReadOnlyList<(Vec3 Point, float R, float G, float B)>? HotspotMarks { get; set; }
}

/// <summary>GPU 버퍼 핸들(VAO · VBO · 정점 수).</summary>
public readonly record struct GpuBuf(uint Vao, uint Vbo, int Count);

/// <summary>
/// Avalonia OpenGlControlBase 기반 3D 뷰(GPU 렌더링, PRD S절). GLES 3.0(ANGLE)·GL 3.3 core 모두 지원.
/// 좌표: NED → GL(x=동, y=위, z=−북). 정밀도를 위해 카메라 기준 상대좌표로 그린다.
/// 조작(카메라 수학은 ViewCamera): 왼쪽 드래그 궤도 회전 · Ctrl+왼쪽 드래그 제자리 시점 회전 ·
/// 오른쪽/가운데(또는 Shift+왼쪽) 드래그 화면 평면 이동(잡은 점이 커서를 따라옴) · 휠 커서 위치로 확대 ·
/// Ctrl+휠 커서 방향 전진·후진 · W/S/A/D/E/Q(MainWindow 가 전달) 누르는 동안 공간 이동 ·
/// 클릭 물체 선택 · 더블클릭 물체 추적 확대(빈 곳은 전체 보기).
/// 분할(Layout 1·2·4): 한 GL 표면 안에서 뷰포트만 나눠 같은 장면을 화면마다 다른 면 색으로 그린다.
/// 메시 VBO 는 물체마다 한 벌(SurfaceBufferCache)로 모든 화면이 공유하고, 화면마다 색 VBO 와 카메라만 따로 둔다.
/// 카메라는 기본 동기(SyncCameras), 해제하면 화면마다 따로 — 조작은 누른 화면(ActivePane)에 적용.
/// </summary>
public sealed class GlView : OpenGlControlBase, ICustomHitTest
{
    private const float Fov = (float)ViewCamera.Fov;
    private const double DragThreshold = 4;

    private GL? _gl;
    private uint _meshProg, _lineProg;
    private int _uMvp, _uModel, _uColor, _uLMvp, _uPointSize;
    private uint _lineVao, _lineVbo;
    /// <summary>메시(물체별 1벌, 모든 분할 화면 공유) · 면 색((화면, 물체)별) 버퍼.</summary>
    private readonly SurfaceBufferCache<GpuBuf> _bufs;
    private float[][] _meshSource = [];
    private GpuBuf _canopy;
    // 멀티로터 암·모터·프롭 부품(단위 상자 · 축 z 단위 원통)
    private GpuBuf _unitBox, _unitCyl;
    private readonly List<(Matrix4x4 Model, float Alpha)> _propDiscs = new();
    private readonly List<GpuBuf> _traj = new();
    private GpuBuf _mcPts;
    private McOverlay? _mcUploaded;
    private Vec3 _mcOrigin;
    private int _uploadedVersion = -1;
    private bool _isEs;
    // 프레임마다 재사용하는 동적 정점 버퍼(할당 없음)
    private readonly List<float> _lines = new(4096);
    private readonly List<(Vec3 Pos, Vec3 Dir, double Size)> _canopies = new();

    public SceneData? Scene { get => _scene; set { _scene = value; _sectionBounds.Clear(); } }
    private SceneData? _scene;
    public McOverlay? Mc { get; set; }

    // ---------------- 분할 화면 ----------------
    private readonly ViewPane[] _panes = Enumerable.Range(0, ViewSplit.MaxPanes).Select(i => new ViewPane(i)).ToArray();
    private SplitLayout _layout = SplitLayout.Single;
    private bool _syncCameras = true;
    private int _activePane;
    private int _bufPanes = 1;
    private readonly PickState?[] _picks = new PickState?[ViewSplit.MaxPanes];

    /// <summary>분할 화면 상태(항상 4개, 앞의 PaneCount 개를 그린다).</summary>
    public IReadOnlyList<ViewPane> Panes => _panes;
    public int PaneCount => ViewSplit.PaneCount(_layout);

    /// <summary>바탕 팔레트(배경 · 지면 · 격자 · 구분선 · 표식) — 테마 토큰에서(ThemeService). 데이터 색과 무관.</summary>
    public ViewPalette Palette { get; set; } = ViewPalette.Dark;

    /// <summary>장면 환경(보기 → 장면 환경: 하늘 · 지면 · 태양 · 그림자 · 안개). 기본 = 기본(분석) — 이전 모습(단색 배경 + 격자) 그대로.</summary>
    public SceneSettings SceneEnv { get; set; } = new();
    /// <summary>지금 그리는 프리셋(자동 = 시나리오 토양에 맞춤).</summary>
    public EnvironmentDef ActiveEnvironment => Environments.Resolve(SceneEnv.EnvironmentId, Scene?.Soil);
    /// <summary>환경 목록(config/environments 내장 + 사용자 환경) — MainWindow 가 읽어 넣는다. 처음엔 기본(분석)만.</summary>
    public EnvironmentRegistry Environments { get; set; } = new();
    private uint _skyProg, _groundProg, _skyTex, _groundTex, _emptyVao, _quadVao, _quadVbo;
    private int _uSkyRight, _uSkyUp, _uSkyFwd, _uSkyRot, _uSkyExp, _uSkySun, _uSkySunCol, _uSkyTree, _uSkyHaze;
    private int _uGVp, _uGGround, _uGScale, _uGOff, _uGMacro, _uGRefl, _uFlat, _uSkyV, _uSkyTrees;
    /// <summary>지금 GPU 에 올린 환경 · 환경별 계산 자료(텍스처 · 조명 · 확산 사전 필터) 캐시.</summary>
    private EnvironmentDef? _texEnv;
    private readonly Dictionary<EnvironmentDef, EnvironmentAssets> _assets = new();
    /// <summary>조명 · 안개 uniform 위치(메시 · 면 색 · 지면 프로그램 공통 이름).</summary>
    private readonly record struct LightU(int Env, int SunDir, int SunCol, int SkyAmb, int GndAmb, int FogCol, int FogDen, int Eye, int Exposure);
    private LightU _lMesh, _lCol, _lGround;
    /// <summary>화면 하나의 조명 값(GL 카메라 기준 좌표). On = false 면 셰이더가 이전(분석) 식을 쓴다.</summary>
    private readonly record struct EnvLight(bool On, Vector3 SunDir, Vector3 Sun, Vector3 SkyAmb, Vector3 GndAmb, Vector3 Fog, float FogDen, Vector3 Eye, float Exposure);

    public SplitLayout Layout
    {
        get => _layout;
        set
        {
            if (value == _layout) return;
            _layout = value;
            if (_activePane >= PaneCount) _activePane = 0;
            RequestNextFrameRendering();
        }
    }

    /// <summary>분할 화면 카메라 동기(기본 켬). 끄면 현재 시점을 화면마다 복사해 따로 움직이고, 다시 켜면 누른 화면의 시점으로 맞춘다.</summary>
    public bool SyncCameras
    {
        get => _syncCameras;
        set
        {
            if (value == _syncCameras) return;
            var from = ActiveCamera;
            _syncCameras = value;
            if (value) _panes[0].OwnCamera.CopyFrom(from);
            else foreach (var p in _panes) p.OwnCamera.CopyFrom(from);
            RequestNextFrameRendering();
        }
    }

    /// <summary>카메라 명령(확대·맞춤·프리셋·비행)을 받는 화면 — 마지막으로 누르거나 휠을 굴린 화면.</summary>
    public int ActivePane
    {
        get => _activePane;
        set
        {
            int v = Math.Clamp(value, 0, PaneCount - 1);
            if (v == _activePane) return;
            _cam.StopFlying();
            _flyTimer?.Stop();
            _activePane = v;
            RequestNextFrameRendering();
        }
    }

    private ViewCamera CamOf(int pane) => _syncCameras ? _panes[0].OwnCamera : _panes[pane].OwnCamera;
    private ViewCamera ActiveCamera => CamOf(_activePane);

    /// <summary>GPU 버퍼 현황 — 메시 버퍼 수·바이트는 분할 수와 무관해야 한다(메모리 요구).</summary>
    public (int MeshBuffers, long MeshBytes, int ColorBuffers, long ColorBytes, long MeshCreations) BufferStats =>
        (_bufs.MeshBufferCount, _bufs.MeshBuffers.Sum(m => (long)m.Count * 6 * sizeof(float)),
         _bufs.ColorBufferCount, _bufs.ColorBuffers.Sum(c => (long)c.Count * 3 * sizeof(float)), _bufs.MeshCreations);

    /// <summary>
    /// 0번 화면 면 색(면당 RGB 3 float, 면 순서 = SceneData.BodyMeshes) — 응력·압력·상태 색 표시(FR-STR-01~04).
    /// 교체 방식으로 갱신하고 FaceColorVersion 을 올리면 그 때만 GPU 로 다시 올린다. 다른 화면은 Panes[i].
    /// </summary>
    public IReadOnlyDictionary<int, float[]>? FaceColors { get => _panes[0].FaceColors; set => _panes[0].FaceColors = value; }
    public int FaceColorVersion { get => _panes[0].FaceColorVersion; set => _panes[0].FaceColorVersion = value; }
    /// <summary>
    /// 물체별 변형 형상(SceneData.Flatten 형식, 정점 수 = 원 메시) — FEA 변형 표시(FR-FEA-05). 없으면 원래 형상.
    /// 교체 후 MeshDeformVersion 을 올리면 그 때만 메시 VBO 를 다시 채운다(면 색 VAO 는 같은 VBO 를 쓰므로 그대로 동작).
    /// </summary>
    public IReadOnlyDictionary<int, float[]>? MeshDeform { get; set; }
    public int MeshDeformVersion { get; set; }
    private int _deformUploaded;
    private readonly HashSet<int> _deformed = new();
    /// <summary>0번 화면 연결점 국부 하중 핫스팟(NED 위치, 색) — FR-STR-02(b).</summary>
    public IReadOnlyList<(Vec3 Point, float R, float G, float B)>? HotspotMarks { get => _panes[0].HotspotMarks; set => _panes[0].HotspotMarks = value; }
    /// <summary>CFD 유선 겹쳐 보기(레이어 CfdStreamlines): (물체, 그 시각 상태) → 유선(동체 좌표 점, 점마다 RGBA). null = 없음.</summary>
    public Func<int, BodySample, IReadOnlyList<(Vec3[] P, float[] Rgba)>?>? FlowLinesProvider { get; set; }
    private uint _meshColProg;
    private int _uCMvp, _uCModel, _uCAlpha;
    public double PlaybackTime { get; set; }
    public int SelectedBody { get; set; } = -1;
    /// <summary>단면 보기 물체(−1 = 끔): 메시 경계 중심을 지나고 긴 축을 품는 평면으로 잘라 카메라 쪽 절반을 그리지 않는다(셰이더 discard).</summary>
    public int SectionBody { get; set; } = -1;
    private readonly Dictionary<int, (Vec3 Center, int LongAxis)> _sectionBounds = new();
    private int _uClip, _uCClip;
    public bool Follow { get; set; } = true;
    public ViewLayers Layers { get; } = new();
    /// <summary>어셈블리 트리에서 숨긴 물체 인덱스(교체 방식으로 갱신).</summary>
    public IReadOnlySet<int> HiddenBodies { get; set; } = new HashSet<int>();
    public string GlInfo { get; private set; } = "";
    public long FramesRendered { get; private set; }
    public event Action<string>? Info;
    /// <summary>클릭으로 물체 선택.</summary>
    public event Action<int>? BodyPicked;
    /// <summary>카메라 모드(추적 등)가 뷰 내부 조작으로 바뀜.</summary>
    public event Action? CameraChanged;

    // 카메라(궤도 + 공간 이동) — 상태·수학은 ViewCamera, 조작 대상은 활성 화면 카메라
    private ViewCamera _cam => ActiveCamera;
    private Point _lastPointer, _pressPointer;
    private bool _rotating, _looking, _panning, _dragged;
    /// <summary>팬 드래그를 시작한 곳의 깊이 [m](NaN = 초점 깊이).</summary>
    private double _panDepth = double.NaN;
    private DispatcherTimer? _flyTimer;

    // 피킹용 마지막 프레임 정보(화면별, 좌표는 그 화면 기준 DIP)
    private sealed record PickState(Matrix4x4 Vp, Vec3 Center, Frame Frame, double W, double H, ViewCamera Cam);

    /// <summary>활성 화면 카메라의 궤도 거리 [m].</summary>
    public double Distance => _cam.Distance;

    public GlView()
    {
        _bufs = new SurfaceBufferCache<GpuBuf>(
            body => UploadMesh(body < _meshSource.Length ? _meshSource[body] : []),
            (_, _, mesh) => CreateColorVao(mesh),
            Delete);
        Focusable = true;
        ClipToBounds = true;
        DoubleTapped += (_, e) =>
        {
            int i = Pick(e.GetPosition(this));
            if (i >= 0) { BodyPicked?.Invoke(i); FocusSelected(); }
            else FitScene();
            e.Handled = true;
        };
    }

    // ---------------- 카메라 API ----------------

    /// <summary>활성 화면 카메라(카메라 수학이 쓰는 뷰 크기를 그 화면 크기로 맞춤).</summary>
    private ViewCamera Cam
    {
        get
        {
            var r = PaneRect(_activePane);
            _cam.SetViewport(r.Width, r.Height);
            return _cam;
        }
    }

    private Thickness _contentInset;

    /// <summary>
    /// 장면 영역 인셋(DIP) — 3D 컨트롤은 본문 전체를 덮고(바탕은 전체), 장면 · 분할 화면은 고정된 패널(레일 · 서랍 · 대시보드 · 아래 서랍)을
    /// 비킨 안쪽 사각형에 그린다. 카메라 · 선택 · 분할 칸 오버레이가 모두 이 영역 기준.
    /// </summary>
    public Thickness ContentInset
    {
        get => _contentInset;
        set { if (_contentInset == value) return; _contentInset = value; RequestNextFrameRendering(); }
    }

    /// <summary>장면 영역(컨트롤 기준 DIP).</summary>
    public Rect ContentRect
    {
        get
        {
            var i = _contentInset;
            return new Rect(i.Left, i.Top, Math.Max(1, Bounds.Width - i.Left - i.Right), Math.Max(1, Bounds.Height - i.Top - i.Bottom));
        }
    }

    /// <summary>분할 화면 i 의 영역(컨트롤 기준 DIP).</summary>
    public Rect PaneRect(int pane)
    {
        var a = ContentRect;
        var (x, y, w, h) = ViewSplit.Rect(_layout, pane, a.Width, a.Height);
        return new Rect(a.X + x, a.Y + y, w, h);
    }

    /// <summary>컨트롤 좌표 → (화면 번호, 그 화면 기준 좌표).</summary>
    private (int Pane, Point Local) Locate(Point pos)
    {
        var a = ContentRect;
        int p = ViewSplit.PaneAt(_layout, a.Width, a.Height, pos.X - a.X, pos.Y - a.Y);
        return (p, pos - PaneRect(p).Position);
    }

    private static double Now => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

    /// <summary>카메라 초기화 — 모든 분할 화면.</summary>
    public void ResetCamera(double distance)
    {
        foreach (var p in _panes) p.OwnCamera.Reset(distance);
        RequestNextFrameRendering();
    }

    public void SetPreset(ViewPreset p)
    {
        var (yaw, pitch) = p switch
        {
            ViewPreset.Top => (0.0, 1.55),
            ViewPreset.North => (0.0, 0.05),
            ViewPreset.East => (Math.PI / 2, 0.05),
            _ => (ViewCamera.DefaultYaw, ViewCamera.DefaultPitch),
        };
        _cam.SetAngles(yaw, pitch);
        RequestNextFrameRendering();
    }

    /// <summary>화면 중심 기준 확대(f &lt; 1) · 축소(f &gt; 1) — 최소 거리에서는 앞으로 전진.</summary>
    public void Zoom(double f)
    {
        Cam.Zoom(f);
        RequestNextFrameRendering();
    }

    /// <summary>화면 평면 이동(DIP 단위, 초점 깊이의 내용이 dx·dy 만큼 움직임).</summary>
    public void PanPixels(double dx, double dy)
    {
        Cam.PanPixels(dx, dy);
        RequestNextFrameRendering();
    }

    /// <summary>초점 중심 궤도 회전 [rad].</summary>
    public void Orbit(double dYaw, double dPitch)
    {
        _cam.Orbit(dYaw, dPitch);
        RequestNextFrameRendering();
    }

    /// <summary>제자리 시점 회전 [rad](눈 고정).</summary>
    public void Look(double dYaw, double dPitch)
    {
        _cam.Look(dYaw, dPitch);
        RequestNextFrameRendering();
    }

    /// <summary>공간 이동 키를 누름·뗌 — 누르고 있는 동안 프레임마다 이동(가속).</summary>
    public void FlyKey(FlyDir dir, bool down)
    {
        _cam.Fly(dir, down, Now);
        RequestNextFrameRendering();
        if (!_cam.IsFlying) { _flyTimer?.Stop(); return; }
        _flyTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Render, (_, _) => FlyTick());
        _flyTimer.Start();
    }

    public bool IsFlying => _cam.IsFlying;

    /// <summary>모든 공간 이동 키를 놓은 것으로(창 비활성 등).</summary>
    public void StopFlying()
    {
        foreach (var p in _panes) p.OwnCamera.StopFlying();
        _flyTimer?.Stop();
    }

    private void FlyTick()
    {
        _cam.Advance(Now);
        RequestNextFrameRendering();
        if (!_cam.IsFlying) _flyTimer?.Stop();
    }

    public void FitMc()
    {
        if (Mc is null) return;
        SetFollow(false);
        _cam.LookAt(new Vec3(Mc.Stats.MeanN, Mc.Stats.MeanE, -Mc.GroundAltitude), Math.Max(50, Mc.Stats.R95 * 4));
        _cam.SetAngles(_cam.Yaw, 1.2);
        RequestNextFrameRendering();
    }

    /// <summary>전체 보기: 자유 물체 궤적 경계(+MC 산포)가 화면에 들어오게.</summary>
    public void FitScene()
    {
        var s = Scene;
        if (s is null || s.Frames.Count == 0) { FitMc(); return; }
        var min = s.BoundsMin; var max = s.BoundsMax;
        if (Mc is { } mc && Layers[ViewLayer.McPoints])
        {
            double r = Math.Max(mc.Stats.R95 * 1.5, 1);
            min = new Vec3(Math.Min(min.X, mc.Stats.MeanN - r), Math.Min(min.Y, mc.Stats.MeanE - r), min.Z);
            max = new Vec3(Math.Max(max.X, mc.Stats.MeanN + r), Math.Max(max.Y, mc.Stats.MeanE + r), max.Z);
        }
        SetFollow(false);
        double radius = Math.Max((max - min).Norm / 2, 2);
        _cam.LookAt((min + max) * 0.5, Math.Clamp(radius / Math.Sin(Fov / 2) * 1.05, 2, 200000));
        RequestNextFrameRendering();
    }

    /// <summary>선택 물체 추적 + 크기에 맞춘 거리.</summary>
    public void FocusSelected()
    {
        var s = Scene;
        if (s is null || SelectedBody < 0 || SelectedBody >= s.World.Bodies.Count) return;
        _cam.LookAt(_cam.Target, Math.Max(2.5, s.World.Bodies[SelectedBody].BoundingRadius * 10));
        SetFollow(true);
        RequestNextFrameRendering();
    }

    private void SetFollow(bool on)
    {
        if (Follow == on) return;
        Follow = on;
        CameraChanged?.Invoke();
    }

    // ---------------- 입력 ----------------

    /// <summary>
    /// GL 표면은 Avalonia 그리기 목록에 아무것도 남기지 않아 기본 히트 테스트에서 투명으로 취급된다
    /// (포인터가 통과해 휠·드래그·클릭이 오지 않음). 컨트롤 영역 전체를 히트로 잡는다.
    /// </summary>
    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var p = e.GetCurrentPoint(this);
        _lastPointer = _pressPointer = p.Position;
        _dragged = false;
        // 누른 화면이 카메라 조작 대상(드래그가 화면 경계를 넘어가도 그대로)
        ActivePane = Locate(p.Position).Pane;
        bool shift = (e.KeyModifiers & KeyModifiers.Shift) != 0, ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0;
        _panning = p.Properties.IsRightButtonPressed || p.Properties.IsMiddleButtonPressed || (p.Properties.IsLeftButtonPressed && shift);
        _looking = !_panning && p.Properties.IsLeftButtonPressed && ctrl;
        _rotating = !_panning && !_looking && p.Properties.IsLeftButtonPressed;
        // 잡은 곳이 물체면 그 깊이에서, 아니면 초점 깊이에서 커서를 정확히 따라오게
        _panDepth = _panning ? AnchorDepth(p.Position) : double.NaN;
        e.Pointer.Capture(this);
        Focus();
        base.OnPointerPressed(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (!_dragged && e.InitialPressMouseButton == MouseButton.Left)
        {
            int i = Pick(e.GetPosition(this));
            if (i >= 0) BodyPicked?.Invoke(i);
        }
        _rotating = _looking = _panning = false;
        e.Pointer.Capture(null);
        base.OnPointerReleased(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var pos = e.GetPosition(this);
        var d = pos - _lastPointer;
        _lastPointer = pos;
        bool any = _rotating || _looking || _panning;
        if (any && !_dragged)
        {
            var t = pos - _pressPointer;
            if (Math.Abs(t.X) + Math.Abs(t.Y) < DragThreshold) { base.OnPointerMoved(e); return; }
            _dragged = true;
            d = t;
        }
        if (_rotating) Cam.OrbitDrag(d.X, d.Y);
        else if (_looking) Cam.LookDrag(d.X, d.Y);
        else if (_panning) Cam.PanPixels(d.X, d.Y, _panDepth);
        if (any) RequestNextFrameRendering();
        base.OnPointerMoved(e);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        var pos = e.GetPosition(this);
        var (pane, local) = Locate(pos);
        ActivePane = pane;
        // Ctrl+휠: 공간 안에서 커서 방향 전진·후진(거리 그대로) · 휠: 커서 아래 점을 고정한 확대(최소 거리에서는 전진)
        if ((e.KeyModifiers & KeyModifiers.Control) != 0) Cam.Dolly(e.Delta.Y, local.X, local.Y, Now);
        else Cam.Wheel(e.Delta.Y, local.X, local.Y, AnchorDepth(pos), Now);
        RequestNextFrameRendering();
        e.Handled = true;
        base.OnPointerWheelChanged(e);
    }

    /// <summary>컨트롤 좌표 아래 물체의 그 화면 카메라 시선 방향 깊이 [m], 물체가 없으면 NaN(초점 깊이를 쓰게).</summary>
    private double AnchorDepth(Point pos)
    {
        int i = Pick(pos);
        var (pane, _) = Locate(pos);
        if (i < 0 || _picks[pane] is not { } ps || i >= ps.Frame.Bodies.Length) return double.NaN;
        double z = Vec3.Dot(ps.Frame.Bodies[i].Cg - ps.Cam.Eye, ps.Cam.Forward);
        return z > 0 ? z : double.NaN;
    }

    /// <summary>컨트롤 좌표에 가장 가까운 보이는 물체(그 분할 화면 기준, 투영 반경 또는 14 DIP 이내), 없으면 −1.</summary>
    public int Pick(Point point)
    {
        var (pane, pos) = Locate(point);
        var ps = _picks[pane];
        var s = Scene;
        if (ps is null || s is null) return -1;
        int best = -1;
        double bestD = double.MaxValue;
        for (int i = 0; i < ps.Frame.Bodies.Length; i++)
        {
            var b = ps.Frame.Bodies[i];
            if (!BodyVisible(s, i, b)) continue;
            var g = Frames.ToGl(b.Cg - ps.Center);
            var clip = Vector4.Transform(new Vector4((float)g.X, (float)g.Y, (float)g.Z, 1), ps.Vp);
            if (clip.W <= 1e-6f) continue;
            double sx = (clip.X / clip.W + 1) / 2 * ps.W, sy = (1 - clip.Y / clip.W) / 2 * ps.H;
            double d = Math.Sqrt((sx - pos.X) * (sx - pos.X) + (sy - pos.Y) * (sy - pos.Y));
            double rPx = s.World.Bodies[i].BoundingRadius / Math.Max(clip.W * Math.Tan(Fov / 2), 1e-9) * ps.H / 2;
            if (d <= Math.Max(14, rPx) && d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    private bool BodyVisible(SceneData s, int i, BodySample b)
    {
        if (HiddenBodies.Contains(i)) return false;
        if (s.World.Bodies[i].Mode == BodyMode.Kinematic) return Layers[ViewLayer.Carrier];
        if (!Layers[ViewLayer.Bodies]) return false;
        return b.Mode != BodyMode.Stowed || Layers[ViewLayer.Stowed];
    }

    // ---------------- GL ----------------

    protected override unsafe void OnOpenGlInit(GlInterface gl)
    {
        base.OnOpenGlInit(gl);
        _gl = GL.GetApi(gl.GetProcAddress);
        _isEs = GlVersion.Type == GlProfileType.OpenGLES;
        string head = _isEs ? "#version 300 es\nprecision highp float;\n" : "#version 330 core\n";
        // 조명(조각 셰이더 공통): uEnv = 0 → 이전 식(고정 광원 abs(n·l), 분석 환경 그대로),
        // 1 → 태양 방향광 + 반구 환경광(하늘 · 땅) + 블린-퐁 스페큘러 + 노출, 그다음 거리 안개(지수).
        const string light = """
            uniform int uEnv;
            uniform vec3 uSunDir;
            uniform vec3 uSunCol;
            uniform vec3 uSkyAmb;
            uniform vec3 uGndAmb;
            uniform vec3 uFogCol;
            uniform vec3 uEye;
            uniform float uFogDen;
            uniform float uExposure;
            float FogF(vec3 wp){ return 1.0 - exp(-uFogDen * length(wp - uEye)); }
            vec3 Shade(vec3 base, vec3 nIn, vec3 wp, float a0, float a1, float specK){
              vec3 n = normalize(nIn);
              if (uEnv == 0) return base * (a0 + a1 * abs(dot(n, normalize(vec3(0.35, 1.0, 0.25)))));
              vec3 v = normalize(uEye - wp);
              if (dot(n, v) < 0.0) n = -n;
              float ndl = max(dot(n, uSunDir), 0.0);
              vec3 h = normalize(uSunDir + v);
              float spec = ndl > 0.0 ? specK * pow(max(dot(n, h), 0.0), 48.0) : 0.0;
              vec3 c = (base * (mix(uGndAmb, uSkyAmb, 0.5 + 0.5 * n.y) + uSunCol * ndl) + uSunCol * spec) * uExposure;
              return mix(c, uFogCol, FogF(wp));
            }
            """;
        _meshProg = Program(head + """
            layout(location=0) in vec3 aPos;
            layout(location=1) in vec3 aNormal;
            uniform mat4 uMvp;
            uniform mat4 uModel;
            out vec3 vN;
            out vec3 vP;
            out vec3 vW;
            void main(){ vN = mat3(uModel) * aNormal; vP = aPos; vec4 w = uModel * vec4(aPos, 1.0); vW = w.xyz / w.w; gl_Position = uMvp * vec4(aPos, 1.0); }
            """, head + light + """
            uniform vec4 uColor;
            uniform vec4 uClip;
            uniform float uFlat;
            in vec3 vN;
            in vec3 vP;
            in vec3 vW;
            out vec4 FragColor;
            void main(){
              if (dot(vP, uClip.xyz) > uClip.w) discard;
              // 투영 그림자: 단색 · 안개만큼 옅게
              if (uFlat > 0.5) { FragColor = vec4(uColor.rgb, uColor.a * (1.0 - FogF(vW))); return; }
              FragColor = vec4(Shade(uColor.rgb, vN, vW, 0.35, 0.65, 0.25), uColor.a);
            }
            """);
        _lineProg = Program(head + """
            layout(location=0) in vec3 aPos;
            layout(location=1) in vec4 aColor;
            uniform mat4 uMvp;
            uniform float uPointSize;
            out vec4 vC;
            void main(){ vC = aColor; gl_Position = uMvp * vec4(aPos, 1.0); gl_PointSize = uPointSize; }
            """, head + """
            in vec4 vC;
            out vec4 FragColor;
            void main(){ FragColor = vC; }
            """);
        _meshColProg = Program(head + """
            layout(location=0) in vec3 aPos;
            layout(location=1) in vec3 aNormal;
            layout(location=2) in vec3 aColor;
            uniform mat4 uMvp;
            uniform mat4 uModel;
            out vec3 vN;
            out vec3 vC;
            out vec3 vP;
            out vec3 vW;
            void main(){ vN = mat3(uModel) * aNormal; vC = aColor; vP = aPos; vW = (uModel * vec4(aPos, 1.0)).xyz; gl_Position = uMvp * vec4(aPos, 1.0); }
            """, head + light + """
            uniform float uAlpha;
            uniform vec4 uClip;
            in vec3 vN;
            in vec3 vC;
            in vec3 vP;
            in vec3 vW;
            out vec4 FragColor;
            void main(){
              if (dot(vP, uClip.xyz) > uClip.w) discard;
              FragColor = vec4(Shade(vC, vN, vW, 0.55, 0.45, 0.15), uAlpha);
            }
            """);
        // 하늘 돔: 전체 화면 삼각형(정점 버퍼 없음, gl_VertexID) → 시선 방향의 고도 · 방위로 하늘 텍스처 + 태양(원반 · 번짐)
        _skyProg = Program(head + """
            uniform vec3 uRight;
            uniform vec3 uUp;
            uniform vec3 uFwd;
            out vec3 vDir;
            void main(){
              vec2 p = vec2(gl_VertexID == 1 ? 3.0 : -1.0, gl_VertexID == 2 ? 3.0 : -1.0);
              vDir = uFwd + p.x * uRight + p.y * uUp;
              gl_Position = vec4(p, 1.0, 1.0);
            }
            """, head + """
            uniform sampler2D uSky;
            uniform float uRot;
            uniform float uExposure;
            uniform vec3 uSunDir;
            uniform vec3 uSunCol;
            uniform vec3 uTree;
            uniform vec3 uHaze;
            uniform vec2 uSkyV;
            uniform float uTrees;
            in vec3 vDir;
            out vec4 FragColor;
            void main(){
              vec3 d = normalize(vDir);
              float el = degrees(asin(clamp(d.y, -1.0, 1.0)));
              float az = atan(d.x, -d.z);
              // 세로 = 고도 uSkyV.x(절차적 −10°, 영상 −90°) → 90°
              vec4 sk = texture(uSky, vec2((az - uRot) / 6.2831853, (el - uSkyV.x) / uSkyV.y));
              vec3 c = sk.rgb;
              // 숲 실루엣: 알파 = 나무 꼭대기 고도 / 10°(TreeMaxElevation), 가장자리 0.03° 부드럽게 · 밑동은 연무
              float top = sk.a * 10.0;
              float tr = uTrees > 0.5 && sk.a > 0.001 ? 1.0 - smoothstep(top - 0.03, top + 0.03, el) : 0.0;
              c = mix(c, mix(uTree, uHaze, clamp(1.0 - el / 0.8, 0.0, 0.6)), tr);
              float s = max(dot(d, uSunDir), 0.0);
              c += uSunCol * (3.0 * smoothstep(0.99985, 0.99996, s) + 0.35 * pow(s, 64.0));
              FragColor = vec4(c * uExposure, 1.0);
            }
            """);
        // 지면: 큰 사각형 하나(카메라 기준 좌표) · 같은 타일 텍스처를 두 축척(10 배)으로 섞어 화면 범위에 맞춘 자동 축척 + 조명 + 안개
        _groundProg = Program(head + """
            layout(location=0) in vec2 aXZ;
            uniform mat4 uVp;
            uniform vec4 uGround;
            out vec3 vW;
            void main(){ vW = vec3(uGround.x + aXZ.x * uGround.w, uGround.y, uGround.z + aXZ.y * uGround.w); gl_Position = uVp * vec4(vW, 1.0); }
            """, head + light + """
            uniform sampler2D uTex;
            uniform vec3 uScale;
            uniform vec4 uOff;
            uniform vec3 uMacro;
            uniform vec4 uRefl;
            in vec3 vW;
            out vec4 FragColor;
            float Luma(vec3 c){ return dot(c, vec3(0.3, 0.59, 0.11)); }
            void main(){
              vec2 g = vec2(vW.x, -vW.z);
              vec3 t = mix(texture(uTex, g * uScale.x + uOff.xy).rgb, texture(uTex, g * uScale.y + uOff.zw).rgb, uScale.z);
              // 타일 반복 깨기: 7.7 배 큰 축척 · 비스듬한 표본의 밝기 비(텍스처 평균 대비)로 넓게 밝고 어둡게
              vec2 m = mat2(0.8, 0.6, -0.6, 0.8) * (g * uMacro.z) + uMacro.xy;
              float avg = Luma(textureLod(uTex, vec2(0.5), 8.0).rgb);
              t *= mix(1.0, Luma(textureLod(uTex, m, 4.0).rgb) / max(avg, 0.02), 0.6);   // 16 px 평균(흐린 밉) — 균열 · 얼룩이 크게 보이지 않게
              vec3 c = Shade(t, vec3(0.0, 1.0, 0.0), vW, 1.0, 0.0, 0.0);
              // 지면 반사(장면 설정): 스치는 각에서 하늘(지평선) 색 — 프레넬 근사 × (1 − 거칠기), 안개 뒤로는 약하게
              float fres = pow(1.0 - max(normalize(uEye - vW).y, 0.0), 5.0);
              c = mix(c, uRefl.rgb, fres * uRefl.a * (1.0 - FogF(vW)));
              FragColor = vec4(c, 1.0);
            }
            """);
        _uSkyRight = _gl.GetUniformLocation(_skyProg, "uRight");
        _uSkyUp = _gl.GetUniformLocation(_skyProg, "uUp");
        _uSkyFwd = _gl.GetUniformLocation(_skyProg, "uFwd");
        _uSkyRot = _gl.GetUniformLocation(_skyProg, "uRot");
        _uSkyExp = _gl.GetUniformLocation(_skyProg, "uExposure");
        _uSkySun = _gl.GetUniformLocation(_skyProg, "uSunDir");
        _uSkySunCol = _gl.GetUniformLocation(_skyProg, "uSunCol");
        _uSkyTree = _gl.GetUniformLocation(_skyProg, "uTree");
        _uSkyHaze = _gl.GetUniformLocation(_skyProg, "uHaze");
        _uGVp = _gl.GetUniformLocation(_groundProg, "uVp");
        _uGGround = _gl.GetUniformLocation(_groundProg, "uGround");
        _uGScale = _gl.GetUniformLocation(_groundProg, "uScale");
        _uGOff = _gl.GetUniformLocation(_groundProg, "uOff");
        _uGMacro = _gl.GetUniformLocation(_groundProg, "uMacro");
        _uGRefl = _gl.GetUniformLocation(_groundProg, "uRefl");
        _uSkyV = _gl.GetUniformLocation(_skyProg, "uSkyV");
        _uSkyTrees = _gl.GetUniformLocation(_skyProg, "uTrees");
        _uFlat = _gl.GetUniformLocation(_meshProg, "uFlat");
        _lMesh = LightLocs(_meshProg); _lCol = LightLocs(_meshColProg); _lGround = LightLocs(_groundProg);
        _emptyVao = _gl.GenVertexArray();
        _quadVao = _gl.GenVertexArray();
        _quadVbo = _gl.GenBuffer();
        _gl.BindVertexArray(_quadVao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _quadVbo);
        // 지면 격자(−1…1, 가운데가 촘촘하게 t³) — 큰 삼각형 하나는 근평면에서 잘릴 때 깊이 정밀도가 무너져 격자선 · 그림자가 가려진다
        var quad = new float[GroundCells * GroundCells * 12];
        float G(int i) { float t = 2f * i / GroundCells - 1; return t * t * t; }
        for (int i = 0, k = 0; i < GroundCells; i++)
            for (int j = 0; j < GroundCells; j++)
                foreach (var (di, dj) in new[] { (0, 0), (1, 0), (1, 1), (0, 0), (1, 1), (0, 1) })
                {
                    quad[k++] = G(i + di); quad[k++] = G(j + dj);
                }
        fixed (float* qp = quad) _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(quad.Length * sizeof(float)), qp, BufferUsageARB.StaticDraw);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), (void*)0);
        _gl.BindVertexArray(0);
        _skyTex = _gl.GenTexture();
        _groundTex = _gl.GenTexture();
        _texEnv = null;
        _uCMvp = _gl.GetUniformLocation(_meshColProg, "uMvp");
        _uCModel = _gl.GetUniformLocation(_meshColProg, "uModel");
        _uCAlpha = _gl.GetUniformLocation(_meshColProg, "uAlpha");
        _uMvp = _gl.GetUniformLocation(_meshProg, "uMvp");
        _uModel = _gl.GetUniformLocation(_meshProg, "uModel");
        _uColor = _gl.GetUniformLocation(_meshProg, "uColor");
        _uClip = _gl.GetUniformLocation(_meshProg, "uClip");
        _uCClip = _gl.GetUniformLocation(_meshColProg, "uClip");
        _uLMvp = _gl.GetUniformLocation(_lineProg, "uMvp");
        _uPointSize = _gl.GetUniformLocation(_lineProg, "uPointSize");
        _lineVao = _gl.GenVertexArray();
        _lineVbo = _gl.GenBuffer();
        _gl.BindVertexArray(_lineVao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _lineVbo);
        SetupLineAttribs();
        _canopy = UploadMesh(SceneData.Flatten(Canopy()));
        _unitBox = UploadMesh(SceneData.Flatten(TriMesh.Box(new Vec3(1, 1, 1), Vec3.Zero)));
        _unitCyl = UploadMesh(SceneData.Flatten(TriMesh.Cylinder(1, 1, 2, Vec3.Zero, 20)));
        _gl.BindVertexArray(0);
        _uploadedVersion = -1;
        _mcUploaded = null;
        GlInfo = $"{_gl.GetStringS(StringName.Renderer)} | {_gl.GetStringS(StringName.Version)}";
        Info?.Invoke(GlInfo);
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        if (_gl is null) return;
        _bufs.Clear();
        foreach (var m in _traj) Delete(m);
        _traj.Clear();
        Delete(_mcPts); _mcPts = default; _mcUploaded = null;
        _gl.DeleteProgram(_meshColProg);
        _gl.DeleteProgram(_meshProg); _gl.DeleteProgram(_lineProg);
        _gl.DeleteVertexArray(_lineVao); _gl.DeleteBuffer(_lineVbo);
        Delete(_canopy); Delete(_unitBox); Delete(_unitCyl);
        _gl.DeleteProgram(_skyProg); _gl.DeleteProgram(_groundProg);
        _gl.DeleteTexture(_skyTex); _gl.DeleteTexture(_groundTex);
        _gl.DeleteVertexArray(_emptyVao); _gl.DeleteVertexArray(_quadVao); _gl.DeleteBuffer(_quadVbo);
        DeleteMsaa(_gl);
        base.OnOpenGlDeinit(gl);
    }

    // ---- 다중 샘플(MSAA) 프레임버퍼 ----
    private const int MsaaSamples = 4;
    private uint _msFbo, _msColor, _msDepth;
    private int _msW, _msH;
    private bool _msUnsupported;

    /// <summary>창 크기의 4× MSAA 프레임버퍼를 준비(크기가 바뀌면 다시 만듦). 지원되지 않으면 false — 기존 경로로 그린다.</summary>
    private bool EnsureMsaa(GL gl, int w, int h)
    {
        if (_msUnsupported) return false;
        if (_msFbo != 0 && _msW == w && _msH == h) return true;
        DeleteMsaa(gl);
        gl.GetInteger(GLEnum.MaxSamples, out int max);
        uint samples = (uint)Math.Min(MsaaSamples, max);
        if (samples < 2) { _msUnsupported = true; return false; }
        _msColor = gl.GenRenderbuffer();
        gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _msColor);
        gl.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, samples, InternalFormat.Rgba8, (uint)w, (uint)h);
        _msDepth = gl.GenRenderbuffer();
        gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _msDepth);
        // 깊이 + 스텐실(투영 그림자가 화소마다 한 번만 어둡게 하도록)
        gl.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, samples, InternalFormat.Depth24Stencil8, (uint)w, (uint)h);
        _msFbo = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, _msFbo);
        gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, _msColor);
        gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthStencilAttachment, RenderbufferTarget.Renderbuffer, _msDepth);
        var status = gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, 0);
        if (status != GLEnum.FramebufferComplete)
        {
            Info?.Invoke($"MSAA 프레임버퍼 사용 불가({status}) — 다중 샘플 없이 그립니다");
            DeleteMsaa(gl);
            _msUnsupported = true;
            return false;
        }
        _msW = w; _msH = h;
        return true;
    }

    private void DeleteMsaa(GL gl)
    {
        if (_msFbo != 0) gl.DeleteFramebuffer(_msFbo);
        if (_msColor != 0) gl.DeleteRenderbuffer(_msColor);
        if (_msDepth != 0) gl.DeleteRenderbuffer(_msDepth);
        _msFbo = _msColor = _msDepth = 0;
        _msW = _msH = 0;
    }

    private void Delete(GpuBuf m)
    {
        if (_gl is null || m.Vao == 0) return;
        _gl.DeleteVertexArray(m.Vao); _gl.DeleteBuffer(m.Vbo);
    }

    private uint Program(string vs, string fs)
    {
        var gl = _gl!;
        uint Compile(ShaderType t, string src)
        {
            uint s = gl.CreateShader(t);
            gl.ShaderSource(s, src);
            gl.CompileShader(s);
            gl.GetShader(s, ShaderParameterName.CompileStatus, out int ok);
            if (ok == 0) throw new InvalidOperationException($"셰이더 컴파일 실패: {gl.GetShaderInfoLog(s)}");
            return s;
        }
        uint v = Compile(ShaderType.VertexShader, vs), f = Compile(ShaderType.FragmentShader, fs);
        uint p = gl.CreateProgram();
        gl.AttachShader(p, v); gl.AttachShader(p, f);
        gl.LinkProgram(p);
        gl.GetProgram(p, ProgramPropertyARB.LinkStatus, out int lok);
        if (lok == 0) throw new InvalidOperationException($"셰이더 링크 실패: {gl.GetProgramInfoLog(p)}");
        gl.DeleteShader(v); gl.DeleteShader(f);
        return p;
    }

    private LightU LightLocs(uint p)
    {
        var gl = _gl!;
        int L(string n) => gl.GetUniformLocation(p, n);
        return new(L("uEnv"), L("uSunDir"), L("uSunCol"), L("uSkyAmb"), L("uGndAmb"), L("uFogCol"), L("uFogDen"), L("uEye"), L("uExposure"));
    }

    private static void SetLight(GL gl, uint prog, LightU u, in EnvLight e)
    {
        gl.UseProgram(prog);
        gl.Uniform1(u.Env, e.On ? 1 : 0);
        gl.Uniform3(u.SunDir, e.SunDir.X, e.SunDir.Y, e.SunDir.Z);
        gl.Uniform3(u.SunCol, e.Sun.X, e.Sun.Y, e.Sun.Z);
        gl.Uniform3(u.SkyAmb, e.SkyAmb.X, e.SkyAmb.Y, e.SkyAmb.Z);
        gl.Uniform3(u.GndAmb, e.GndAmb.X, e.GndAmb.Y, e.GndAmb.Z);
        gl.Uniform3(u.FogCol, e.Fog.X, e.Fog.Y, e.Fog.Z);
        gl.Uniform3(u.Eye, e.Eye.X, e.Eye.Y, e.Eye.Z);
        gl.Uniform1(u.FogDen, e.FogDen);
        gl.Uniform1(u.Exposure, e.Exposure);
    }

    /// <summary>환경이 바뀌면 하늘 · 지면 텍스처를 다시 올린다(지면은 밉맵 — 먼 곳은 부드럽게 흐려짐). 계산 자료는 환경마다 한 번만 만든다.</summary>
    private unsafe EnvironmentAssets EnsureEnvTextures(GL gl, EnvironmentDef env)
    {
        if (!_assets.TryGetValue(env, out var a))
        {
            try { a = SceneEnvironment.Prepare(env); }
            catch (Exception ex)
            {
                // 사용자 영상이 사라졌거나 깨짐 → 그 환경의 지면 · 하늘만 기본 절차적으로
                Info?.Invoke($"환경 '{env.Id}' 자료 실패: {ex.Message}");
                a = SceneEnvironment.Prepare(new EnvironmentDef { Id = env.Id, Lights = env.Lights });
            }
            _assets[env] = a;
        }
        if (ReferenceEquals(env, _texEnv)) return a;
        void Upload(uint tex, byte[] px, int w, int h, bool mip)
        {
            gl.BindTexture(TextureTarget.Texture2D, tex);
            fixed (byte* d = px)
                gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)w, (uint)h, 0, Silk.NET.OpenGL.PixelFormat.Rgba, PixelType.UnsignedByte, d);
            if (mip) gl.GenerateMipmap(TextureTarget.Texture2D);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)(mip ? GLEnum.LinearMipmapLinear : GLEnum.Linear));
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.Repeat);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)(mip ? GLEnum.Repeat : GLEnum.ClampToEdge));
        }
        Upload(_groundTex, a.GroundPx, a.GroundSize, a.GroundSize, true);
        Upload(_skyTex, a.SkyPx, a.SkyW, a.SkyH, false);
        gl.BindTexture(TextureTarget.Texture2D, 0);
        _texEnv = env;
        return a;
    }

    private unsafe void SetupLineAttribs()
    {
        var gl = _gl!;
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 7 * sizeof(float), (void*)0);
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, 7 * sizeof(float), (void*)(3 * sizeof(float)));
    }

    private unsafe GpuBuf UploadMesh(float[] data)
    {
        var gl = _gl!;
        uint vao = gl.GenVertexArray(), vbo = gl.GenBuffer();
        gl.BindVertexArray(vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        fixed (float* p = data) gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(data.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), (void*)0);
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), (void*)(3 * sizeof(float)));
        gl.BindVertexArray(0);
        return new(vao, vbo, data.Length / 6);
    }

    /// <summary>xyz 정점 + 단색 → 정적 선/점 버퍼.</summary>
    private unsafe GpuBuf UploadColored(float[] xyz, (float, float, float, float) color)
    {
        var gl = _gl!;
        int n = xyz.Length / 3;
        var data = new float[n * 7];
        for (int i = 0; i < n; i++)
        {
            data[7 * i] = xyz[3 * i]; data[7 * i + 1] = xyz[3 * i + 1]; data[7 * i + 2] = xyz[3 * i + 2];
            data[7 * i + 3] = color.Item1; data[7 * i + 4] = color.Item2; data[7 * i + 5] = color.Item3; data[7 * i + 6] = color.Item4;
        }
        uint vao = gl.GenVertexArray(), vbo = gl.GenBuffer();
        gl.BindVertexArray(vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        fixed (float* p = data) gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(data.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);
        SetupLineAttribs();
        gl.BindVertexArray(0);
        return new(vao, vbo, n);
    }

    /// <summary>
    /// 면 색 VAO = 공유 메시 VBO(위치·법선, 복제 없음) + 이 화면 전용 색 VBO(정점마다 RGB). 색은 ColorVao 가 버전이 바뀔 때만 올린다.
    /// </summary>
    private unsafe GpuBuf CreateColorVao(GpuBuf mesh)
    {
        var gl = _gl!;
        uint vao = gl.GenVertexArray(), vbo = gl.GenBuffer();
        gl.BindVertexArray(vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, mesh.Vbo);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), (void*)0);
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), (void*)(3 * sizeof(float)));
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        gl.EnableVertexAttribArray(2);
        gl.VertexAttribPointer(2, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), (void*)0);
        gl.BindVertexArray(0);
        return new(vao, vbo, mesh.Count);
    }

    /// <summary>화면 pane · 물체 body 의 면 색 VAO — 버전이 바뀔 때만 색 VBO 를 다시 올림.</summary>
    private unsafe uint ColorVao(int pane, int body, float[] faceRgb, int version)
    {
        var gl = _gl!;
        var (cb, stale) = _bufs.Color(pane, body, version);
        if (stale)
        {
            int vertexCount = cb.Count, faces = vertexCount / 3;
            var data = new float[vertexCount * 3];
            for (int f = 0; f < faces && 3 * f + 2 < faceRgb.Length; f++)
                for (int k = 0; k < 3; k++)
                {
                    data[9 * f + 3 * k] = faceRgb[3 * f]; data[9 * f + 3 * k + 1] = faceRgb[3 * f + 1]; data[9 * f + 3 * k + 2] = faceRgb[3 * f + 2];
                }
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, cb.Vbo);
            fixed (float* p = data) gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(data.Length * sizeof(float)), p, BufferUsageARB.DynamicDraw);
            _bufs.MarkUploaded(pane, body, version);
        }
        return cb.Vao;
    }

    /// <summary>변형 형상 반영: 바뀐 물체만 메시 VBO 를 BufferSubData 로 다시 채우고, 빠진 물체는 원 형상으로 되돌린다.</summary>
    private unsafe void ApplyDeform(SceneData s)
    {
        if (MeshDeformVersion == _deformUploaded) return;
        var gl = _gl!;
        var want = MeshDeform;
        foreach (var i in _deformed.ToList())
            if (want is null || !want.ContainsKey(i))
            {
                if (_bufs.TryGetMesh(i, out var m) && i < s.Meshes.Length && s.Meshes[i].Length == m.Count * 6)
                {
                    gl.BindBuffer(BufferTargetARB.ArrayBuffer, m.Vbo);
                    fixed (float* p = s.Meshes[i]) gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, (nuint)(s.Meshes[i].Length * sizeof(float)), p);
                }
                _deformed.Remove(i);
            }
        if (want is not null)
            foreach (var (i, data) in want)
            {
                if (!_bufs.TryGetMesh(i, out var m) || data.Length != m.Count * 6) continue;
                gl.BindBuffer(BufferTargetARB.ArrayBuffer, m.Vbo);
                fixed (float* p = data) gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, (nuint)(data.Length * sizeof(float)), p);
                _deformed.Add(i);
            }
        _deformUploaded = MeshDeformVersion;
    }

    private void UploadScene(SceneData s)
    {
        _bufs.Clear();
        foreach (var m in _traj) Delete(m);
        _traj.Clear();
        // 메시는 물체마다 한 벌 — 분할 화면이 몇 개든 이 VBO 를 같이 쓴다
        _meshSource = s.Meshes;
        for (int i = 0; i < s.Meshes.Length; i++) _bufs.Mesh(i);
        _deformed.Clear(); _deformUploaded = -1;
        for (int i = 0; i < s.Trajectories.Length; i++)
        {
            var c = s.Colors[i];
            _traj.Add(UploadColored(s.Trajectories[i], (c.R, c.G, c.B, 0.85f)));
        }
        _uploadedVersion = s.Version;
    }

    /// <summary>MC 착탄점은 결과가 바뀔 때만 GPU 로 올린다(평균 착탄점 기준 상대 좌표).</summary>
    private void UploadMc(McOverlay? mc, double groundD)
    {
        Delete(_mcPts);
        _mcPts = default;
        _mcUploaded = mc;
        if (mc is null || mc.Points.Length == 0) return;
        _mcOrigin = new Vec3(mc.Stats.MeanN, mc.Stats.MeanE, groundD - 0.02);
        var xyz = new float[mc.Points.Length * 3];
        for (int i = 0; i < mc.Points.Length; i++)
        {
            var g = Frames.ToGl(new Vec3(mc.Points[i].N, mc.Points[i].E, groundD - 0.02) - _mcOrigin);
            xyz[3 * i] = (float)g.X; xyz[3 * i + 1] = (float)g.Y; xyz[3 * i + 2] = (float)g.Z;
        }
        _mcPts = UploadColored(xyz, (1f, 0.75f, 0.2f, 0.8f));
    }

    private static TriMesh Canopy()
    {
        // 단위 반구(돔이 +Z 방향), 반경 1
        var m = new TriMesh();
        int st = 8, sl = 24;
        Vec3 P(int i, int j)
        {
            double th = Math.PI / 2 * i / st, ph = 2 * Math.PI * j / sl;
            return new Vec3(Math.Sin(th) * Math.Cos(ph), Math.Sin(th) * Math.Sin(ph), Math.Cos(th) * 0.6);
        }
        for (int i = 0; i < st; i++)
            for (int j = 0; j < sl; j++)
            {
                var a = P(i, j); var b = P(i + 1, j); var c = P(i + 1, j + 1); var d = P(i, j + 1);
                m.Vertices.AddRange([a, b, c, a, c, d]);
            }
        return m;
    }

    private static Matrix4x4 PerspectiveGl(float fovy, float aspect, float n, float f)
    {
        float t = 1f / MathF.Tan(fovy / 2);
        return new Matrix4x4(t / aspect, 0, 0, 0, 0, t, 0, 0, 0, 0, (f + n) / (n - f), -1, 0, 0, 2 * f * n / (n - f), 0);
    }

    /// <summary>회전(동체→NED DCM) + 위치(카메라 기준 GL) → 모델 행렬(행벡터 관례).</summary>
    /// <summary>
    /// 단면 평면(메시 = 동체 좌표): 경계 중심 c 를 지나고 긴 축을 품으며 법선이 카메라 쪽(긴 축 성분 제거)인 평면.
    /// 셰이더는 dot(p, n) &gt; dot(c, n) 인 조각(카메라 쪽 절반)을 버린다.
    /// </summary>
    private Vector4? SectionPlane(SceneData s, int i, BodySample b, Vec3 eyeNed)
    {
        if (!_sectionBounds.TryGetValue(i, out var sb))
        {
            if (i >= s.BodyMeshes.Length || s.BodyMeshes[i] is not { } m || m.TriangleCount == 0) return null;
            var (mn, mx) = m.Bounds();
            var size = mx - mn;
            int ax = size.X >= size.Y && size.X >= size.Z ? 0 : size.Y >= size.Z ? 1 : 2;
            _sectionBounds[i] = sb = ((mn + mx) / 2, ax);
        }
        var n = b.Q.InverseRotate(eyeNed - b.Origin) - sb.Center;
        var nOff = sb.LongAxis switch { 0 => new Vec3(0, n.Y, n.Z), 1 => new Vec3(n.X, 0, n.Z), _ => new Vec3(n.X, n.Y, 0) };
        if (nOff.Norm > 1e-9 * Math.Max(n.Norm, 1e-12)) n = nOff;
        if (n.Norm < 1e-12) return null;
        n = n.Normalized();
        return new Vector4((float)n.X, (float)n.Y, (float)n.Z, (float)Vec3.Dot(n, sb.Center));
    }

    private static Matrix4x4 ModelMatrix(Sim.Core.Numerics.Mat3 bodyToNed, Vec3 posGl, float scale = 1)
    {
        var r = Frames.NedToGl * bodyToNed;
        // 행벡터 관례: v_row * M → M 의 행 = 열벡터 관례 행렬의 열
        return new Matrix4x4(
            (float)r.M11 * scale, (float)r.M21 * scale, (float)r.M31 * scale, 0,
            (float)r.M12 * scale, (float)r.M22 * scale, (float)r.M32 * scale, 0,
            (float)r.M13 * scale, (float)r.M23 * scale, (float)r.M33 * scale, 0,
            (float)posGl.X, (float)posGl.Y, (float)posGl.Z, 1);
    }

    protected override unsafe void OnOpenGlRender(GlInterface gli, int fb)
    {
        var gl = _gl;
        if (gl is null) return;
        FramesRendered++;
        double scaling = VisualRoot?.RenderScaling ?? 1.0;
        int w = Math.Max(1, (int)(Bounds.Width * scaling)), h = Math.Max(1, (int)(Bounds.Height * scaling));
        // 다중 샘플 프레임버퍼에 그린 뒤 Avalonia 프레임버퍼로 내려 복사 — 가는 선(격자·궤적·줄)의 계단·반짝임 제거
        bool msaa = EnsureMsaa(gl, w, h);
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, msaa ? _msFbo : (uint)fb);
        gl.Viewport(0, 0, (uint)w, (uint)h);
        int paneCount = PaneCount;
        var pal = Palette;
        // 장면 영역(인셋 안) — 바깥(고정 패널 밑)은 바탕색만
        var ins = _contentInset;
        int ax = Math.Clamp((int)(ins.Left * scaling), 0, w - 1), ayTop = Math.Clamp((int)(ins.Top * scaling), 0, h - 1);
        int aw = Math.Max(1, w - ax - (int)(ins.Right * scaling)), ah = Math.Max(1, h - ayTop - (int)(ins.Bottom * scaling));
        gl.ClearColor(pal.Background.R, pal.Background.G, pal.Background.B, 1f);
        gl.DepthMask(true);
        gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        // 분할 화면 사이 구분선 = 장면 영역을 구분선 색으로 지운 뒤 각 화면을 1 px 안쪽으로
        if (paneCount > 1)
        {
            gl.Enable(EnableCap.ScissorTest);
            gl.Scissor(ax, h - (ayTop + ah), (uint)aw, (uint)ah);
            gl.ClearColor(pal.Divider.R, pal.Divider.G, pal.Divider.B, 1f);
            gl.Clear(ClearBufferMask.ColorBufferBit);
            gl.Disable(EnableCap.ScissorTest);
        }
        gl.Enable(EnableCap.DepthTest);
        gl.DepthFunc(DepthFunction.Lequal);
        gl.Enable(EnableCap.Blend);
        gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        if (!_isEs) gl.Enable(EnableCap.ProgramPointSize);

        var s = Scene;
        if (s is not null && s.Version != _uploadedVersion) UploadScene(s);
        double groundD = -(s?.GroundAltitude ?? Mc?.GroundAltitude ?? 0);
        if (!ReferenceEquals(Mc, _mcUploaded)) UploadMc(Mc, groundD);
        // 분할 수를 줄이면 빠진 화면의 색 버퍼만 해제(메시 VBO 는 그대로)
        if (paneCount < _bufPanes) _bufs.TrimPanes(paneCount);
        _bufPanes = paneCount;
        for (int p = paneCount; p < _picks.Length; p++) _picks[p] = null;
        // FEA 변형은 형상 — 공유 메시 VBO 에 한 번 반영하면 모든 화면에 보인다
        if (s is not null) ApplyDeform(s);

        Frame? frame = null;
        int frameIndex = 0;
        if (s is not null && s.Frames.Count > 0)
        {
            frameIndex = Math.Max(s.World.Recorder.IndexAt(PlaybackTime), 0);
            // 기록 프레임 사이 보간(50 Hz 기록 · ~60 Hz 화면 → 보간이 없으면 몇 프레임마다 같은 장면이 반복되어 툭툭 끊김)
            frame = ReferenceEquals(s.Frames, s.World.Recorder.Frames) ? s.World.Recorder.Interpolate(PlaybackTime) ?? s.Frames[frameIndex] : s.Frames[frameIndex];
        }
        int sel = SelectedBody;
        bool selValid = frame is not null && sel >= 0 && sel < frame.Bodies.Length;
        // 선택·추적·재생 시각은 모든 화면 공통
        if (Follow && selValid)
            for (int p = 0; p < paneCount; p++) CamOf(p).Target = frame!.Bodies[sel].Cg;

        gl.Enable(EnableCap.ScissorTest);
        for (int p = 0; p < paneCount; p++)
        {
            var (rx, ry, rw, rh) = ViewSplit.Rect(_layout, p, aw, ah);
            int inset = paneCount > 1 ? 1 : 0;
            int px = ax + (int)rx + inset, pw = Math.Max(1, (int)rw - 2 * inset), ph = Math.Max(1, (int)rh - 2 * inset);
            int py = h - (ayTop + (int)(ry + rh)) + inset; // GL 원점 = 왼쪽 아래
            if (paneCount > 1 && p == _activePane && !_syncCameras)
            {
                // 카메라를 따로 움직일 때 조작 대상 화면 테두리 강조
                gl.Scissor(px - 1, py - 1, (uint)(pw + 2), (uint)(ph + 2));
                gl.ClearColor(pal.Active.R, pal.Active.G, pal.Active.B, 1f);
                gl.Clear(ClearBufferMask.ColorBufferBit);
            }
            gl.Viewport(px, py, (uint)pw, (uint)ph);
            gl.Scissor(px, py, (uint)pw, (uint)ph);
            gl.ClearColor(pal.Background.R, pal.Background.G, pal.Background.B, 1f);
            gl.DepthMask(true);
            gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit | ClearBufferMask.StencilBufferBit);
            var dip = PaneRect(p);
            RenderPane(gl, p, pw, ph, dip.Width, dip.Height, s, frame, frameIndex, groundD);
        }
        gl.Disable(EnableCap.ScissorTest);
        gl.Viewport(0, 0, (uint)w, (uint)h);
        gl.BindVertexArray(0);
        gl.UseProgram(0);
        // 모든 분할 화면을 그린 다중 샘플 버퍼를 한 번에 Avalonia 프레임버퍼로 내림(시저 해제 뒤 — 전체 영역)
        if (msaa)
        {
            gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _msFbo);
            gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, (uint)fb);
            gl.BlitFramebuffer(0, 0, w, h, 0, 0, w, h, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)fb);
        }

        if (_snapshotPath is { } path)
        {
            _snapshotPath = null;
            try { SaveSnapshot(gl, w, h, path); }
            catch (Exception ex) { Info?.Invoke($"스냅샷 실패: {ex.Message}"); }
        }
    }

    /// <summary>분할 화면 하나 그리기(뷰포트·시저는 호출자가 설정) — 그 화면 카메라와 면 색, 공유 메시 VBO.</summary>
    private unsafe void RenderPane(GL gl, int paneIndex, int w, int h, double dipW, double dipH, SceneData? s, Frame? frame, int frameIndex, double groundD)
    {
        var pane = _panes[paneIndex];
        var cam = CamOf(paneIndex);
        cam.SetViewport(dipW, dipH);
        var L = Layers;
        var hidden = HiddenBodies;
        int sel = SelectedBody;
        bool selValid = frame is not null && sel >= 0 && sel < frame.Bodies.Length;
        double dist = cam.Distance;
        var center = cam.Focus;

        // 뷰·투영(카메라 기준 원점 = center)
        var eyeGl = Frames.ToGl(cam.Forward * -dist);
        var view = Matrix4x4.CreateLookAt(new Vector3((float)eyeGl.X, (float)eyeGl.Y, (float)eyeGl.Z), Vector3.Zero, Vector3.UnitY);
        float near = (float)Math.Max(0.02, dist * 0.002), far = (float)(dist * 200 + 50000);
        var proj = PerspectiveGl(Fov, (float)w / h, near, far);
        var vp = view * proj;
        if (frame is not null) _picks[paneIndex] = new PickState(vp, center, frame, dipW, dipH, cam);

        Vec3 Rel(Vec3 ned) => Frames.ToGl(ned - center);

        var lines = _lines;
        lines.Clear();
        void Line(Vec3 a, Vec3 b, (float r, float g, float bl, float al) c)
        {
            var ga = Rel(a); var gb = Rel(b);
            lines.AddRange([(float)ga.X, (float)ga.Y, (float)ga.Z, c.r, c.g, c.bl, c.al,
                            (float)gb.X, (float)gb.Y, (float)gb.Z, c.r, c.g, c.bl, c.al]);
        }
        void Circle(Vec3 c0, double r, (float, float, float, float) col, int n = 48)
        {
            Vec3 prev = default;
            for (int i = 0; i <= n; i++)
            {
                double a = 2 * Math.PI * i / n;
                var p = c0 + new Vec3(Math.Cos(a), Math.Sin(a), 0) * r;
                if (i > 0) Line(prev, p, col);
                prev = p;
            }
        }

        var envDef = ActiveEnvironment;
        bool envOn = !envDef.IsAnalysis;
        var assets = envOn ? EnsureEnvTextures(gl, envDef) : null;

        // ---- 지면 격자 · 방위 · 바람 ----
        double heightAbove = Math.Max(0, groundD - center.Z);
        double spacing = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(Math.Max(dist, heightAbove), 1) / 2)));
        double gx0 = Math.Round(center.X / spacing) * spacing, gy0 = Math.Round(center.Y / spacing) * spacing;
        if (L[ViewLayer.Grid])
        {
            // 격자선은 잘게 나눠 정점마다 투명도를 준다: 화면에서 이웃 선과의 간격이 2 px 아래면 사라지고 6 px 이상이면 그대로
            // (지평선 쪽 빽빽한 1 px 선이 카메라가 조금만 움직여도 켜졌다 꺼지는 반짝임 제거) + 격자 가장자리 서서히 흐리게.
            var eye = center + cam.Forward * -dist;
            // 환경 지면 위에서는 지면 밝기에 맞서는 색(밝은 지면 = 어두운 선, 그 밖 = 흰 선)으로 계속 보이게
            var gridC = Palette.Grid;
            if (assets is not null && SceneEnv.GroundDisplayed)
                gridC = assets.GroundLuma > 0.6f ? (0.15f, 0.17f, 0.2f) : (0.95f, 0.97f, 0.95f);
            double eyeH = Math.Max(Math.Abs(groundD - eye.Z), 1e-3);
            double pxPerRad = h / (2 * Math.Tan(Fov / 2));
            void GridLine(Vec3 a, Vec3 b, Vec3 sepDir, double sp, float baseA, double gcx, double gcy, double half)
            {
                const int seg = 32;
                float Alpha(Vec3 p)
                {
                    var dv = p - eye;
                    double d = dv.Norm;
                    if (d < 1e-6) return baseA;
                    double hl = Math.Sqrt(dv.X * dv.X + dv.Y * dv.Y);
                    double c = hl > 1e-9 ? (sepDir.X * dv.X + sepDir.Y * dv.Y) / hl : 0;
                    double graze = Math.Min(1, eyeH / d);
                    double px = sp * Math.Sqrt(c * c * graze * graze + (1 - c * c)) * pxPerRad / d;
                    double moire = Math.Clamp((px - 2) / 4, 0, 1);
                    double r = Math.Max(Math.Abs(p.X - gcx), Math.Abs(p.Y - gcy));
                    double edge = Math.Clamp((half - r) / (0.3 * half), 0, 1);
                    return (float)(baseA * moire * edge);
                }
                var prev = a; float pa = Alpha(a);
                for (int k = 1; k <= seg; k++)
                {
                    var p = a + (b - a) * ((double)k / seg);
                    float qa = Alpha(p);
                    if (pa > 0.003f || qa > 0.003f)
                    {
                        var ga = Rel(prev); var gb = Rel(p);
                        lines.AddRange([(float)ga.X, (float)ga.Y, (float)ga.Z, gridC.R, gridC.G, gridC.B, pa,
                                        (float)gb.X, (float)gb.Y, (float)gb.Z, gridC.R, gridC.G, gridC.B, qa]);
                    }
                    prev = p; pa = qa;
                }
            }
            const int nGrid = 20;
            var ex = new Vec3(1, 0, 0); var ey = new Vec3(0, 1, 0);
            double half = nGrid * spacing;
            for (int i = -nGrid; i <= nGrid; i++)
            {
                float a = i % 5 == 0 ? 0.35f : 0.15f;
                GridLine(new Vec3(gx0 + i * spacing, gy0 - half, groundD), new Vec3(gx0 + i * spacing, gy0 + half, groundD), ex, spacing, a, gx0, gy0, half);
                GridLine(new Vec3(gx0 - half, gy0 + i * spacing, groundD), new Vec3(gx0 + half, gy0 + i * spacing, groundD), ey, spacing, a, gx0, gy0, half);
            }
            // 바깥 넓은 격자(10배 간격) — 공간이 좁게 끝나 보이지 않도록 지평선 쪽까지(같은 규칙으로 흐려짐)
            double big = spacing * 10, bhalf = nGrid * big;
            double bx0 = Math.Round(center.X / big) * big, by0 = Math.Round(center.Y / big) * big;
            for (int i = -nGrid; i <= nGrid; i++)
            {
                GridLine(new Vec3(bx0 + i * big, by0 - bhalf, groundD), new Vec3(bx0 + i * big, by0 + bhalf, groundD), ex, big, 0.12f, bx0, by0, bhalf);
                GridLine(new Vec3(bx0 - bhalf, by0 + i * big, groundD), new Vec3(bx0 + bhalf, by0 + i * big, groundD), ey, big, 0.12f, bx0, by0, bhalf);
            }
        }
        var n0 = new Vec3(gx0, gy0, groundD - 0.01);
        if (L[ViewLayer.Compass])
        {
            var tip = n0 + new Vec3(spacing * 3, 0, 0);
            Line(n0, tip, (0.9f, 0.3f, 0.3f, 1f));
            Line(tip, tip + new Vec3(-spacing * 0.5, spacing * 0.3, 0), (0.9f, 0.3f, 0.3f, 1f));
            Line(tip, tip + new Vec3(-spacing * 0.5, -spacing * 0.3, 0), (0.9f, 0.3f, 0.3f, 1f));
        }
        if (L[ViewLayer.Wind] && s is not null && s.WindAtGround.Norm > 0.01)
            Line(n0, n0 + s.WindAtGround.Normalized() * (spacing * 2.5), (0.4f, 0.8f, 1f, 1f));

        // ---- 장치·연결·선택 표식 ----
        var canopies = _canopies;
        canopies.Clear();
        if (frame is not null && s is not null)
        {
            if (L[ViewLayer.Parachutes])
                foreach (var d in frame.Devices)
                {
                    if (!d.Visible || d.Type != "parachute") continue;
                    var dir = d.Tip - d.Anchor;
                    var dn = dir.Norm > 1e-6 ? dir / dir.Norm : new Vec3(0, 0, -1);
                    // 라이저 4가닥(근사)
                    double r = d.Size / 2 * (0.3 + 0.7 * Math.Clamp(d.Fill, 0, 1));
                    var side1 = Vec3.Cross(dn, Math.Abs(dn.Z) < 0.9 ? new Vec3(0, 0, 1) : new Vec3(1, 0, 0)).Normalized();
                    var side2 = Vec3.Cross(dn, side1);
                    foreach (var sd in new[] { side1, -side1, side2, -side2 })
                        Line(d.Anchor, d.Tip + sd * r, (Palette.Riser.R, Palette.Riser.G, Palette.Riser.B, 0.9f));
                    canopies.Add((d.Tip, dn, r));
                }
            if (L[ViewLayer.Connectors])
                foreach (var c in frame.Connectors)
                {
                    if (!c.Visible) continue;
                    float k = (float)Math.Clamp(c.Tension / 500.0, 0, 1);
                    Line(c.A, c.B, c.Type == "tether" ? (1f, 1f - 0.7f * k, 0.2f, 1f) : (0.6f, 0.6f, 1f, 1f));
                }
            if (L[ViewLayer.Impacts])
                for (int i = 0; i < s.World.Bodies.Count; i++)
                {
                    if (hidden.Contains(i) || s.World.Bodies[i].Impact is not { } imp || imp.Time > PlaybackTime + 1e-9) continue;
                    var c = s.Colors[i];
                    var p0 = new Vec3(imp.Position.X, imp.Position.Y, groundD - 0.03);
                    double r = Math.Max(0.15, dist * 0.012);
                    Circle(p0, r, (c.R, c.G, c.B, 1f), 24);
                    Line(p0 + new Vec3(-r, -r, 0), p0 + new Vec3(r, r, 0), (c.R, c.G, c.B, 1f));
                    Line(p0 + new Vec3(-r, r, 0), p0 + new Vec3(r, -r, 0), (c.R, c.G, c.B, 1f));
                }
            if (selValid && !hidden.Contains(sel))
            {
                var b = frame.Bodies[sel];
                if (L[ViewLayer.Selection] && groundD - b.Cg.Z > 0.05)
                {
                    // 지면 수직선(고도 인지)
                    var mk = Palette.Marker;
                    Line(b.Cg, new Vec3(b.Cg.X, b.Cg.Y, groundD), (mk.R, mk.G, mk.B, 0.35f));
                    Circle(new Vec3(b.Cg.X, b.Cg.Y, groundD - 0.02), Math.Max(0.1, s.World.Bodies[sel].BoundingRadius), (mk.R, mk.G, mk.B, 0.5f), 32);
                }
                if (L[ViewLayer.BodyAxes])
                {
                    double len = Math.Max(0.3, s.World.Bodies[sel].BoundingRadius * 1.6);
                    Line(b.Cg, b.Cg + b.Q.Rotate(new Vec3(len, 0, 0)), (1f, 0.3f, 0.3f, 1f));
                    Line(b.Cg, b.Cg + b.Q.Rotate(new Vec3(0, len, 0)), (0.3f, 1f, 0.3f, 1f));
                    Line(b.Cg, b.Cg + b.Q.Rotate(new Vec3(0, 0, len)), (0.35f, 0.55f, 1f, 1f));
                }
                if (L[ViewLayer.Velocity] && b.Velocity.Norm > 1e-3)
                    Line(b.Cg, b.Cg + b.Velocity * 0.5, (1f, 0.95f, 0.5f, 1f));
            }
        }

        // ---- 연결점 하중 핫스팟 ----
        if (L[ViewLayer.Hotspots] && pane.HotspotMarks is { Count: > 0 } marks)
        {
            double hr = Math.Max(0.03, dist * 0.012);
            foreach (var (p, r, g, bl) in marks)
            {
                var c = (r, g, bl, 1f);
                Line(p - new Vec3(hr, 0, 0), p + new Vec3(hr, 0, 0), c);
                Line(p - new Vec3(0, hr, 0), p + new Vec3(0, hr, 0), c);
                Line(p - new Vec3(0, 0, hr), p + new Vec3(0, 0, hr), c);
            }
        }

        // ---- CFD 유선 겹쳐 보기(선택 물체, 동체 좌표 → 현재 자세) ----
        if (L[ViewLayer.CfdStreamlines] && selValid && s is not null && !hidden.Contains(sel) && FlowLinesProvider?.Invoke(sel, frame!.Bodies[sel]) is { Count: > 0 } flow)
        {
            var b = frame!.Bodies[sel];
            foreach (var (pts, rgba) in flow)
                for (int k = 1; k < pts.Length; k++)
                    Line(b.Origin + b.Q.Rotate(pts[k - 1]), b.Origin + b.Q.Rotate(pts[k]), (rgba[4 * k], rgba[4 * k + 1], rgba[4 * k + 2], rgba[4 * k + 3]));
        }

        // ---- MC CEP 원 · 95% 타원 ----
        if (Mc is { } mc && L[ViewLayer.McRings])
        {
            var st = mc.Stats;
            var cc = new Vec3(st.MeanN, st.MeanE, groundD - 0.03);
            Circle(cc, st.Cep50, (0.3f, 1f, 0.4f, 1f), 72);
            double az = st.EllipseAzimuthDeg * Math.PI / 180;
            var ax1 = new Vec3(Math.Cos(az), Math.Sin(az), 0);
            var ax2 = new Vec3(-Math.Sin(az), Math.Cos(az), 0);
            Vec3 prev = default;
            for (int i = 0; i <= 72; i++)
            {
                double a = 2 * Math.PI * i / 72;
                var p = cc + ax1 * (Math.Cos(a) * st.EllipseMajor) + ax2 * (Math.Sin(a) * st.EllipseMinor);
                if (i > 0) Line(prev, p, (1f, 0.4f, 0.3f, 1f));
                prev = p;
            }
        }

        // 장면 환경(하늘 · 태양 · 환경광 · 안개) — 기본(분석)이면 조명 끔(셰이더가 이전 식) · 하늘 · 텍스처 지면 · 그림자 없음
        var envSet = SceneEnv;
        var eyeV = new Vector3((float)eyeGl.X, (float)eyeGl.Y, (float)eyeGl.Z);
        double range = Math.Max(Math.Max(dist, heightAbove), 5);
        var light = new EnvLight(false, Vector3.UnitY, Vector3.One, Vector3.One, Vector3.One, Vector3.Zero, 0f, eyeV, 1f);
        if (assets is not null)
        {
            // 노출 = 밝기 × IBL 노출 × 2^카메라 EV · 회전 = 장면 회전 + 환경 정면 각(centerAngleDeg)
            float exposure = (float)(envSet.Brightness * assets.Exposure * Math.Pow(2, envSet.ExposureEV));
            double rot = (envSet.RotationDeg + envDef.CenterAngleDeg) * Math.PI / 180;
            var sd = assets.SunDirGl;
            var sunDir = new Vector3((float)(sd.X * Math.Cos(rot) - sd.Z * Math.Sin(rot)), sd.Y, (float)(sd.Z * Math.Cos(rot) + sd.X * Math.Sin(rot)));
            static Vector3 V((float R, float G, float B) c) => new(c.R, c.G, c.B);
            // 안개 색 = 배경과 같은 색(하늘이면 지평선 색, 숲은 밑동 연무 색) — 먼 지면이 배경에 녹아든다. 밀도 = 화면 범위에 비례.
            var solid = envSet.BackgroundColor ?? envDef.Background ?? Palette.Background;
            var fog = envSet.SkyBackground ? V(assets.Trees ? assets.Haze : assets.Horizon) * exposure : V(solid);
            light = new EnvLight(true, sunDir, V(assets.Sun), V(assets.SkyAmbient), V(assets.GroundAmbient), fog,
                envSet.Fog ? (float)(0.03 / range) : 0f, eyeV, exposure);
            if (envSet.SkyBackground) DrawSky(gl, eyeV, (float)w / h, light, rot, assets);
            else
            {
                gl.ClearColor(solid.R, solid.G, solid.B, 1f);
                gl.Clear(ClearBufferMask.ColorBufferBit);
            }
        }
        SetLight(gl, _meshColProg, _lCol, light);
        SetLight(gl, _meshProg, _lMesh, light);
        // 지면 높이 오프셋(장면 설정)은 텍스처 지면 · 그림자 평면만 옮긴다(격자 · 표식은 시나리오 지면 고도 그대로)
        float groundY = (float)(Rel(new Vec3(center.X, center.Y, groundD)).Y + envSet.GroundOffset);
        bool groundOn = assets is not null && envSet.GroundDisplayed;
        if (groundOn)
        {
            // 지면 크기: 평탄화 = 지평선까지 · 아니면 자동 축척(화면 범위 × 배율) 또는 메시 형상 크기
            var gf = envDef.Ground;
            double half = envSet.GroundFlattened ? Math.Min(far * 0.5, 1e6)
                : envDef.Scaling.AutoScale || gf?.Mesh is null ? range * envDef.Scaling.AutoScaleFactor : gf.Mesh.HalfExtentMeters;
            float refl = envSet.GroundReflections ? (float)(0.6 * (1 - envSet.GroundRoughness)) : 0f;
            DrawGround(gl, vp, center, eyeV, groundY, half, range * assets!.TileFactor, light, new Vector4(light.Fog, refl));
        }
        Matrix4x4 CanopyModel(Vec3 pos, Vec3 dir, double size)
        {
            // 돔(+Z 로컬)을 dir 방향으로
            var z = dir;
            var x = Vec3.Cross(Math.Abs(z.Z) < 0.9 ? new Vec3(0, 0, 1) : new Vec3(1, 0, 0), z).Normalized();
            var y = Vec3.Cross(z, x);
            return ModelMatrix(Sim.Core.Numerics.Mat3.FromColumns(x, y, z), Rel(pos - dir * (size * 0.3)), (float)size);
        }

        // 메시 그리기
        // 단면 평면 기본 = 끔(dot ≤ 1e30)
        gl.UseProgram(_meshColProg); gl.Uniform4(_uCClip, 0f, 0f, 0f, 1e30f);
        gl.UseProgram(_meshProg); gl.Uniform4(_uClip, 0f, 0f, 0f, 1e30f);
        // 지면 그림자: 물체 · 캐노피를 태양 방향으로 지면 평면에 투영해 반투명 검정으로(스텐실 = 화소마다 한 번만 어둡게)
        if (groundOn && envSet.Shadows && s is not null && frame is not null)
        {
            // 평면 y = g 로 태양 방향 L 을 따라 투영(행벡터): p' = (Ly·p − L·(p.y − g)) / Ly
            var l = light.SunDir;
            var shadow = new Matrix4x4(l.Y, 0, 0, 0, -l.X, 0, -l.Z, 0, 0, 0, l.Y, 0, l.X * groundY, l.Y * groundY, l.Z * groundY, l.Y);
            var sc = (0f, 0f, 0f, 0.42f);
            gl.Enable(EnableCap.StencilTest);
            gl.StencilFunc(StencilFunction.Equal, 0, 0xFF);
            gl.StencilOp(StencilOp.Keep, StencilOp.Keep, StencilOp.Incr);
            gl.DepthMask(false);
            // 색만 어둡게 — 대상 알파는 그대로(낮아지면 창 합성 · 캡처에서 그림자가 비쳐 밝게 보인다)
            gl.BlendFuncSeparate(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha, BlendingFactor.Zero, BlendingFactor.One);
            gl.Uniform1(_uFlat, 1f);
            for (int i = 0; i < frame.Bodies.Length; i++)
            {
                if (!_bufs.TryGetMesh(i, out var mesh) || mesh.Count == 0) continue;
                var b = frame.Bodies[i];
                if (!BodyVisible(s, i, b) || b.Mode == BodyMode.Stowed) continue;
                DrawPart(gl, mesh, ModelMatrix(b.Q.ToMatrix(), Rel(b.Origin)) * shadow, vp, sc);
            }
            foreach (var (pos, dir, size) in canopies) DrawPart(gl, _canopy, CanopyModel(pos, dir, size) * shadow, vp, sc);
            gl.Uniform1(_uFlat, 0f);
            gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            gl.DepthMask(true);
            gl.Disable(EnableCap.StencilTest);
        }
        // 불투명 지면(격자 레이어와 함께, 기본(분석) 환경) — 격자선만 있으면 지면 아래로 뻗은 선·쓰러진 캐노피·물체가 그대로 보여
        // 땅을 뚫고 들어가는 것처럼 보인다. 윗면 = 지면 고도, 격자선과 같은 깊이라 폴리곤 오프셋으로 살짝 뒤로.
        if (!envOn && L[ViewLayer.Grid])
        {
            double half = Math.Min(far * 0.5, 1e6), thick = Math.Max(0.01, half * 1e-5);
            var groundModel = ModelMatrix(Mat3.Diagonal(2 * half, 2 * half, thick), Rel(new Vec3(center.X, center.Y, groundD + thick / 2)));
            gl.Enable(EnableCap.PolygonOffsetFill);
            gl.PolygonOffset(1f, 4f);
            DrawPart(gl, _unitBox, groundModel, vp, (Palette.Ground.R, Palette.Ground.G, Palette.Ground.B, 1f));
            gl.Disable(EnableCap.PolygonOffsetFill);
        }
        if (s is not null && frame is not null)
        {
            var faceColors = pane.FaceColors;
            var eyeNed = center + cam.Forward * -dist;
            for (int i = 0; i < frame.Bodies.Length; i++)
            {
                if (!_bufs.TryGetMesh(i, out var mesh) || mesh.Count == 0) continue;
                var b = frame.Bodies[i];
                if (!BodyVisible(s, i, b)) continue;
                var model = ModelMatrix(b.Q.ToMatrix(), Rel(b.Origin));
                var mvp = model * vp;
                var clip = i == SectionBody ? SectionPlane(s, i, b, eyeNed) : null;
                // 면당 RGB 3개 = 정점 수(면 × 3)와 같은 길이일 때만(메시와 면 순서가 맞는 색)
                if (faceColors is not null && faceColors.TryGetValue(i, out var rgb) && rgb.Length == mesh.Count)
                {
                    // 응력·압력·상태 면 색
                    gl.UseProgram(_meshColProg);
                    gl.UniformMatrix4(_uCMvp, 1, false, (float*)&mvp);
                    gl.UniformMatrix4(_uCModel, 1, false, (float*)&model);
                    // 단면 보기 중에는 안에 실린 물체 색을 또렷하게(불투명)
                    gl.Uniform1(_uCAlpha, b.Mode == BodyMode.Stowed && SectionBody < 0 ? 0.55f : 1f);
                    if (clip is { } cc) gl.Uniform4(_uCClip, cc.X, cc.Y, cc.Z, cc.W);
                    gl.BindVertexArray(ColorVao(paneIndex, i, rgb, pane.FaceColorVersion));
                    gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)mesh.Count);
                    if (clip is not null) gl.Uniform4(_uCClip, 0f, 0f, 0f, 1e30f);
                    gl.UseProgram(_meshProg);
                    continue;
                }
                if (clip is { } c0)
                {
                    gl.Uniform4(_uClip, c0.X, c0.Y, c0.Z, c0.W);
                    gl.UniformMatrix4(_uMvp, 1, false, (float*)&mvp);
                    gl.UniformMatrix4(_uModel, 1, false, (float*)&model);
                    var cs = s.Colors[i];
                    gl.Uniform4(_uColor, cs.R, cs.G, cs.B, b.Mode == BodyMode.Stowed ? 0.55f : 1f);
                    gl.BindVertexArray(mesh.Vao);
                    gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)mesh.Count);
                    gl.Uniform4(_uClip, 0f, 0f, 0f, 1e30f);
                    continue;
                }
                gl.UniformMatrix4(_uMvp, 1, false, (float*)&mvp);
                gl.UniformMatrix4(_uModel, 1, false, (float*)&model);
                var c = s.Colors[i];
                float alpha = s.World.Bodies[i].Mode == BodyMode.Kinematic ? 0.35f : (b.Mode == BodyMode.Stowed ? 0.55f : 1f);
                if (i == sel && L[ViewLayer.Selection]) gl.Uniform4(_uColor, Math.Min(1, c.R * 1.3f), Math.Min(1, c.G * 1.3f), Math.Min(1, c.B * 1.3f), alpha);
                else gl.Uniform4(_uColor, c.R, c.G, c.B, alpha);
                gl.BindVertexArray(mesh.Vao);
                gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)mesh.Count);
            }
            foreach (var (pos, dir, size) in canopies)
            {
                var model = CanopyModel(pos, dir, size);
                var mvp = model * vp;
                gl.UniformMatrix4(_uMvp, 1, false, (float*)&mvp);
                gl.UniformMatrix4(_uModel, 1, false, (float*)&model);
                gl.Uniform4(_uColor, 0.95f, 0.55f, 0.15f, 0.85f);
                gl.BindVertexArray(_canopy.Vao);
                gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)_canopy.Count);
            }
            if (L[ViewLayer.Rotors]) DrawRotors(gl, s, frame, frameIndex, vp, center);
        }

        // 선·점
        gl.UseProgram(_lineProg);
        gl.Uniform1(_uPointSize, 4f);
        if (s is not null && L[ViewLayer.Trajectories] && frame is not null)
        {
            var off = Frames.ToGl(s.Origin - center);
            var mvp = Matrix4x4.CreateTranslation((float)off.X, (float)off.Y, (float)off.Z) * vp;
            gl.UniformMatrix4(_uLMvp, 1, false, (float*)&mvp);
            int count = Math.Max(0, s.World.Recorder.IndexAt(PlaybackTime) + 1);
            for (int i = 0; i < _traj.Count; i++)
            {
                if (hidden.Contains(i)) continue;
                if (s.World.Bodies[i].Mode == BodyMode.Kinematic && (i != sel || !L[ViewLayer.Carrier])) continue;
                gl.BindVertexArray(_traj[i].Vao);
                gl.DrawArrays(PrimitiveType.LineStrip, 0, (uint)Math.Min(count, _traj[i].Count));
            }
        }
        if (_mcPts.Count > 0 && L[ViewLayer.McPoints])
        {
            var off = Frames.ToGl(_mcOrigin - center);
            var mvp = Matrix4x4.CreateTranslation((float)off.X, (float)off.Y, (float)off.Z) * vp;
            gl.UniformMatrix4(_uLMvp, 1, false, (float*)&mvp);
            gl.Uniform1(_uPointSize, 5f);
            gl.BindVertexArray(_mcPts.Vao);
            gl.DrawArrays(PrimitiveType.Points, 0, (uint)_mcPts.Count);
        }
        var vpCopy = vp;
        gl.UniformMatrix4(_uLMvp, 1, false, (float*)&vpCopy);
        DrawDynamic(gl, lines, PrimitiveType.Lines);
    }

    /// <summary>
    /// 멀티로터 암(접힘 → 전개 보간)·모터·2엽 프롭(작동 중 회전, 이웃 로터 반대 방향)·회전 원판(반투명) — FR-SEP-04 표시.
    /// 고정 팔 기체(props)는 암·모터 없이 실모델 허브에서, 모터가 돌 때만 펴진 2엽 블레이드·원판을 그린다(정지 중엔 형상의 접힌 날 그대로).
    /// 메시 프로그램이 바인딩된 상태에서 호출한다.
    /// </summary>
    private unsafe void DrawRotors(GL gl, SceneData s, Frame frame, int frameIndex, Matrix4x4 vp, Vec3 center)
    {
        var discs = _propDiscs;
        discs.Clear();
        foreach (var r in s.Rotors)
        {
            if (r.Device >= frame.Devices.Length || r.Body >= frame.Bodies.Length) continue;
            var b = frame.Bodies[r.Body];
            if (!BodyVisible(s, r.Body, b)) continue;
            var d = frame.Devices[r.Device];
            double deploy = r.ArmDeploy(d);
            double spin = frameIndex < r.PropAngle.Length ? r.PropAngle[frameIndex] : 0;
            var rot = b.Q.ToMatrix();
            float alpha = b.Mode == BodyMode.Stowed ? 0.55f : 1f;
            var c = s.Colors[r.Body];
            double w = Math.Max(0.008, r.ArmLength * 0.07);
            Matrix4x4 Model(Mat3 axes, Vec3 posBody, Vec3 scale) =>
                ModelMatrix(rot * axes * Mat3.Diagonal(scale.X, scale.Y, scale.Z), Frames.ToGl(b.Origin + rot * posBody - center));
            if (r.Props.Length > 0)
            {
                if (!d.Visible) continue;
                for (int k = 0; k < r.Props.Length; k++)
                {
                    var p = r.Props[k];
                    var (pd, ps) = RotorVisual.PropPlane(p.Axis);
                    double th = p.Spin * spin + k * 0.7;
                    var blade = pd * Math.Cos(th) + ps * Math.Sin(th);
                    // 허브 윗면 바로 위(축 방향 4 mm)에 블레이드 — 실모델 허브와 겹쳐 가려지지 않게
                    var hub = p.Position + p.Axis * 0.004;
                    DrawPart(gl, _unitBox, Model(Mat3.FromColumns(blade, Vec3.Cross(p.Axis, blade), p.Axis), hub, new Vec3(2 * r.PropRadius, r.PropRadius * 0.14, 0.004)),
                        vp, (0.92f, 0.92f, 0.95f, alpha));
                    discs.Add((Model(Mat3.FromColumns(pd, ps, p.Axis), hub, new Vec3(r.PropRadius, r.PropRadius, 0.002)), 0.22f * alpha));
                }
                continue;
            }
            for (int k = 0; k < r.ArmCount; k++)
            {
                var (hinge, dir, side, up) = r.Arm(k, deploy);
                var axes = Mat3.FromColumns(dir, side, up);
                var tip = hinge + dir * r.ArmLength;
                if (r.ArmsOverlay)
                {
                    DrawPart(gl, _unitBox, Model(axes, hinge + dir * (r.ArmLength / 2), new Vec3(r.ArmLength, w, w)), vp, (c.R * 0.7f, c.G * 0.7f, c.B * 0.7f, alpha));
                    DrawPart(gl, _unitCyl, Model(axes, tip + up * w, new Vec3(w * 0.9, w * 0.9, w * 2.2)), vp, (0.75f, 0.75f, 0.8f, alpha));
                }
                var hub = tip + up * (w * 2.3);
                double th = (k % 2 == 0 ? spin : -spin) + k * 0.7;
                var blade = dir * Math.Cos(th) + side * Math.Sin(th);
                DrawPart(gl, _unitBox, Model(Mat3.FromColumns(blade, Vec3.Cross(up, blade), up), hub, new Vec3(2 * r.PropRadius, r.PropRadius * 0.14, 0.004)),
                    vp, (0.92f, 0.92f, 0.95f, alpha));
                if (d.Visible) discs.Add((Model(axes, hub, new Vec3(r.PropRadius, r.PropRadius, 0.002)), 0.22f * alpha));
            }
        }
        if (discs.Count == 0) return;
        gl.DepthMask(false);
        foreach (var (model, a) in discs) DrawPart(gl, _unitCyl, model, vp, (0.85f, 0.88f, 0.95f, a));
        gl.DepthMask(true);
    }

    private unsafe void DrawPart(GL gl, GpuBuf mesh, Matrix4x4 model, Matrix4x4 vp, (float R, float G, float B, float A) c)
    {
        var mvp = model * vp;
        gl.UniformMatrix4(_uMvp, 1, false, (float*)&mvp);
        gl.UniformMatrix4(_uModel, 1, false, (float*)&model);
        gl.Uniform4(_uColor, c.R, c.G, c.B, c.A);
        gl.BindVertexArray(mesh.Vao);
        gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)mesh.Count);
    }

    /// <summary>하늘 돔(전체 화면, 깊이 쓰기 없음 — 화면의 맨 처음에 그린다). 카메라 기준 방향 = 앞 + x·오른쪽 + y·위(시야각 · 화면비 반영).</summary>
    private void DrawSky(GL gl, Vector3 eye, float aspect, in EnvLight light, double rotationRad, EnvironmentAssets a)
    {
        var fwd = Vector3.Normalize(-eye);
        var right = Vector3.Normalize(Vector3.Cross(fwd, Vector3.UnitY));
        var up = Vector3.Cross(right, fwd);
        float t = MathF.Tan(Fov / 2);
        right *= t * aspect; up *= t;
        gl.UseProgram(_skyProg);
        gl.Uniform3(_uSkyRight, right.X, right.Y, right.Z);
        gl.Uniform3(_uSkyUp, up.X, up.Y, up.Z);
        gl.Uniform3(_uSkyFwd, fwd.X, fwd.Y, fwd.Z);
        gl.Uniform1(_uSkyRot, (float)rotationRad);
        gl.Uniform2(_uSkyV, (float)a.SkyMinEl, (float)(90 - a.SkyMinEl));
        gl.Uniform1(_uSkyTrees, a.Trees ? 1f : 0f);
        gl.Uniform1(_uSkyExp, light.Exposure);
        gl.Uniform3(_uSkySun, light.SunDir.X, light.SunDir.Y, light.SunDir.Z);
        gl.Uniform3(_uSkySunCol, light.Sun.X, light.Sun.Y, light.Sun.Z);
        gl.Uniform3(_uSkyTree, a.TreeColor.R, a.TreeColor.G, a.TreeColor.B);
        gl.Uniform3(_uSkyHaze, a.Haze.R, a.Haze.G, a.Haze.B);
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.Texture2D, _skyTex);
        gl.Disable(EnableCap.DepthTest);
        gl.DepthMask(false);
        gl.BindVertexArray(_emptyVao);
        gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        gl.DepthMask(true);
        gl.Enable(EnableCap.DepthTest);
        gl.BindTexture(TextureTarget.Texture2D, 0);
    }

    /// <summary>
    /// 텍스처 지면(시나리오 지면 고도, 눈 바로 아래 중심 · 한 변 2·half). 자동 축척(Fusion 지면 자동 축척처럼): 타일 한 변 = tile(화면 범위 × 2.5 / 환경 autoScaleFactor) 근처의
    /// 10 의 거듭제곱 두 단계를 로그 비율로 섞어 확대 · 축소해도 끊김이 없다. 격자선과 같은 깊이라 폴리곤 오프셋으로 살짝 뒤로.
    /// </summary>
    private const int GroundCells = 64;

    private unsafe void DrawGround(GL gl, Matrix4x4 vp, Vec3 center, Vector3 eye, float y, double half, double tile, in EnvLight light, Vector4 refl)
    {
        SetLight(gl, _groundProg, _lGround, light);
        gl.Uniform4(_uGRefl, refl.X, refl.Y, refl.Z, refl.W);
        double lt = Math.Log10(Math.Max(tile, 1e-3)), f = Math.Floor(lt);
        double s0 = Math.Pow(10, f), s1 = s0 * 10;
        static double Frac(double v) => v - Math.Floor(v);
        gl.Uniform3(_uGScale, (float)(1 / s0), (float)(1 / s1), (float)(lt - f));
        // 텍스처 좌표 = (동, 북) 절대 위치 / 타일 — 카메라 기준 좌표에 더할 소수부만 넘겨 정밀도 유지
        gl.Uniform4(_uGOff, (float)Frac(center.Y / s0), (float)Frac(center.X / s0), (float)Frac(center.Y / s1), (float)Frac(center.X / s1));
        // 큰 축척 밝기 변조(셰이더에서 회전 → 원점 오프셋도 같은 회전)
        double s2 = s1 * 7.7, me = center.Y / s2, mn = center.X / s2;
        gl.Uniform3(_uGMacro, (float)Frac(0.8 * me - 0.6 * mn), (float)Frac(0.6 * me + 0.8 * mn), (float)(1 / s2));
        gl.Uniform4(_uGGround, eye.X, y, eye.Z, (float)half);
        gl.UniformMatrix4(_uGVp, 1, false, (float*)&vp);
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.Texture2D, _groundTex);
        gl.Enable(EnableCap.PolygonOffsetFill);
        gl.PolygonOffset(1f, 4f);
        gl.BindVertexArray(_quadVao);
        gl.DrawArrays(PrimitiveType.Triangles, 0, GroundCells * GroundCells * 6);
        gl.Disable(EnableCap.PolygonOffsetFill);
        gl.BindTexture(TextureTarget.Texture2D, 0);
    }

    private string? _snapshotPath;

    /// <summary>다음 프레임의 GL 프레임버퍼를 PNG 로 저장(검증·보고서용).</summary>
    public void RequestSnapshot(string path)
    {
        _snapshotPath = path;
        RequestNextFrameRendering();
    }

    private static unsafe void SaveSnapshot(GL gl, int w, int h, string path)
    {
        var buf = new byte[w * h * 4];
        fixed (byte* p = buf) gl.ReadPixels(0, 0, (uint)w, (uint)h, Silk.NET.OpenGL.PixelFormat.Rgba, PixelType.UnsignedByte, p);
        var bmp = new Avalonia.Media.Imaging.WriteableBitmap(new PixelSize(w, h), new Avalonia.Vector(96, 96),
            Avalonia.Platform.PixelFormat.Rgba8888, Avalonia.Platform.AlphaFormat.Unpremul);
        using (var fb = bmp.Lock())
        {
            for (int y = 0; y < h; y++)
            {
                var src = (h - 1 - y) * w * 4;
                Marshal.Copy(buf, src, fb.Address + y * fb.RowBytes, w * 4);
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        bmp.Save(path);
    }

    private unsafe void DrawDynamic(GL gl, List<float> data, PrimitiveType prim)
    {
        if (data.Count == 0) return;
        gl.BindVertexArray(_lineVao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _lineVbo);
        var span = CollectionsMarshal.AsSpan(data);
        fixed (float* p = span) gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(span.Length * sizeof(float)), p, BufferUsageARB.StreamDraw);
        gl.DrawArrays(prim, 0, (uint)(span.Length / 7));
    }
}
