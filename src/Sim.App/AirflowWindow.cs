using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Sim.App.Controls;
using Sim.App.Rendering;
using Sim.App.Theme;
using Sim.Core.Airflow;
using Sim.Core.Geometry;
using Sim.Core.Models;
using Sim.Core.Structures;
using Vec3 = Sim.Core.Numerics.Vec3;

namespace Sim.App;

/// <summary>GPU 가상 풍동 대상: 이름 · 동체 좌표 메시 · 기준 길이/면적(0 = 서버 기본) · 나란히 볼 OpenFOAM 정상 RANS 계수(없으면 null).</summary>
public sealed record AirflowSource(string Name, TriMesh Mesh, double RefLength, double RefArea, OpenFoamReference? Reference);

/// <summary>
/// GPU 가상 풍동 창(해석 → GPU 가상 풍동 · Shift+F8): 물체를 가상 풍동에 놓고 받음각 · 옆미끄럼 · 유속 · 계산 Re 를 바꾸면
/// 외부 오픈소스 솔버 XLB(격자 볼츠만, NVIDIA Warp GPU)가 별도 파이썬 프로세스(tools/airflow/server.py)에서 그 자리에서 흐름을 계산하고,
/// 단면 속도/와도 색 · 연기 입자 · 순간 유선이 시간에 따라 움직인다. 같은 화면의 'OpenFOAM 재생' 모드는 미리 계산한 비정상 해석
/// 시간 시퀀스(<see cref="FlowSequence"/>)를 같은 그리기로 재생 — 자료가 없으면 비활성.
/// 신뢰 등급 = 시각화(근사). 물리 한계는 창 아래에 늘 표시(<see cref="AirflowFrames.Limitation"/>).
/// </summary>
public sealed class AirflowWindow : Window
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private const double ReMin = 50, SpeedRange = 1.6;
    /// <summary>
    /// 기본 계산 Re. 격자 한계(저 해상도 ≈ 615) 가까이에서는 닫힌 시험부의 입구–출구 음향 정상파(주기 ≈ 2·nx/c_s 스텝)가 거의 감쇠하지 않아
    /// 힘 평균이 크게 치우친다(α 90°: Re 300 CD 5.3 ± 0.3 · Re 600 CD 12 ± 5). Re 300 은 와류 흘림이 보이면서 힘이 안정한 값.
    /// </summary>
    public const double DefaultRe = 300;

    private readonly IReadOnlyList<AirflowSource> _sources;
    private readonly string _repo;
    private readonly Action<string, string> _log;
    private readonly WindTunnelView _view = new();
    private readonly ComboBox _mode = new() { MinWidth = 130 };
    private readonly ComboBox _body = new() { MinWidth = 150 };
    private readonly Slider _alpha = new() { Width = 170, Minimum = -180, Maximum = 180, TickFrequency = 1, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly Slider _beta = new() { Width = 110, Minimum = -45, Maximum = 45, TickFrequency = 1, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly Slider _speed = new() { Width = 120, Minimum = 5, Maximum = 340, TickFrequency = 1, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly Slider _re = new() { Width = 120, Minimum = 0, Maximum = 1, Value = 1, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _alphaText = new() { MinWidth = 46, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _betaText = new() { MinWidth = 40, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _speedText = new() { MinWidth = 62, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _reText = new() { MinWidth = 90, VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _res = new() { MinWidth = 150 };
    private readonly ComboBox _plane = new() { MinWidth = 130 };
    private readonly Slider _offset = new() { Width = 100, Minimum = -1, Maximum = 1, VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _field = new() { MinWidth = 120 };
    private readonly ComboBox _map = new() { MinWidth = 80 };
    private readonly CheckBox _particles = new() { Content = "연기 입자", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _lines = new() { Content = "유선", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly ToggleButton _pause = new() { Content = "일시정지" };
    private readonly Button _restart = new() { Content = "다시 시작" };
    private readonly ComboBox _seq = new() { MinWidth = 220 };
    private readonly ColorLegend _legend = new() { Ticks = 7, Note = "GPU 격자 볼츠만 (시각화 근사)" };
    // 무대(3D) 위 HUD · 계수 상자 = Stage* 토큰(두 테마 모두 어두운 무대), 막대 · 바닥줄 = 테마 토큰
    private readonly TextBlock _hud = new TextBlock { FontSize = 14, FontWeight = FontWeight.SemiBold }.Fg("StageTextBrush");
    private readonly TextBlock _hudSub = new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, MaxWidth = 760 }.Fg("StageDimBrush");
    private readonly TextBlock _status = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 760 }.Fg("StageWarnBrush");
    private readonly TextBlock _coeffs = new TextBlock { FontSize = 12, Classes = { "mono" } }.Fg("StageTextBrush");
    private readonly TextBlock _grade = new TextBlock { FontSize = 12, FontWeight = FontWeight.SemiBold }.Fg("GradeBadgeTextBrush");
    private readonly TextBlock _foot = new() { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Classes = { "muted" } };
    private readonly StackPanel _overlayRight = new() { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 12, 12, 0) };
    private readonly Border _overlayLeft = new Border
    {
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12, 10, 0, 0),
        Padding = new Thickness(10, 6), CornerRadius = new CornerRadius(8),
    }.Bg("StageHudBrush");
    private readonly Border _coeffBox = new Border
    {
        Padding = new Thickness(10, 6), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
    }.Bg("StagePanelBrush").Res(Border.BorderBrushProperty, "StageLineBrush");

    private AirflowClient? _client;
    private AirflowFrame? _latest, _shown;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(160) };
    private readonly List<FlowSequence> _sequences;
    private readonly string _meshDir;
    private string _meshPath = "";
    private bool _updating, _closing;
    private double _reMax = 600, _vortRange = 6;
    private double _replayT;
    private DateTime _lastTick = DateTime.UtcNow;
    private DateTime _fpsT0 = DateTime.UtcNow;
    private int _fpsN;
    private double _fps;
    private string _serverState = "서버 시작 전";

    public bool Live => _mode.SelectedIndex == 0;
    public long FramesShown { get; private set; }
    public AirflowFrame? Shown => _shown;
    public string ServerState => _serverState;

    public AirflowWindow(IReadOnlyList<AirflowSource> sources, string repo, double alpha0, Action<string, string> log)
    {
        _sources = sources;
        _repo = repo;
        _log = log;
        _meshDir = Path.Combine(repo, "results", "airflow");
        Title = "GPU 가상 풍동 — XLB 격자 볼츠만 실시간 흐름 (시각화 근사)";
        Width = 1400; Height = 900; MinWidth = 900; MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this.KeepOnScreen();

        var reasons = new List<string>();
        _sequences = FlowSequence.Discover(repo, reasons);
        foreach (var r in reasons) _log("warn", $"OpenFOAM 재생 시퀀스 건너뜀: {r}");
        _mode.Items.Add("GPU 실시간");
        _mode.Items.Add(_sequences.Count > 0 ? "OpenFOAM 재생" : "OpenFOAM 재생 (자료 없음)");
        _mode.SelectedIndex = 0;
        foreach (var s in _sequences) _seq.Items.Add(s.Label);
        _seq.SelectedIndex = _sequences.Count > 0 ? 0 : -1;
        _seq.IsVisible = false;
        foreach (var s in sources) _body.Items.Add(s.Name);
        foreach (var (k, lbl) in new[] { ("low", "저 256×96×96 (빠름)"), ("medium", "중 352×128×128"), ("high", "고 480×176×176") }) _res.Items.Add(lbl);
        foreach (var p in new[] { "XZ 피치면 (α)", "XY 요잉면 (β)" }) _plane.Items.Add(p);
        foreach (var f in new[] { "속도 |U|/V∞", "와도 ω·L/V∞", "단면 끔" }) _field.Items.Add(f);
        foreach (var m in ColorMaps.Names) _map.Items.Add(m);
        _res.SelectedIndex = 0; _plane.SelectedIndex = 0; _field.SelectedIndex = 0; _map.SelectedIndex = 0;
        _alpha.Value = Math.Round(Math.Clamp(alpha0, -180, 180));
        _speed.Value = 168;
        _beta.Value = 0;

        TextBlock L(string s) => new() { Text = s, VerticalAlignment = VerticalAlignment.Center, Classes = { "dim" }, Margin = new Thickness(10, 0, 4, 0) };
        var fit = new Button { Content = "전체 보기" };
        fit.Click += (_, _) => _view.ResetView();
        WrapPanel Row(IEnumerable<Control> cs)
        {
            var w = new WrapPanel { Margin = new Thickness(10, 4), Orientation = Orientation.Horizontal };
            foreach (var c in cs)
            {
                if (c is not TextBlock) c.Margin = new Thickness(c.Margin.Left, 2, 4, 2);
                w.Children.Add(c);
            }
            return w;
        }
        var row1 = Row([L("모드"), _mode, _seq, L("물체"), _body, L("받음각 α"), _alpha, _alphaText, L("옆미끄럼 β"), _beta, _betaText,
            L("유속"), _speed, _speedText, L("계산 Re"), _re, _reText, L("격자"), _res]);
        var row2 = Row([L("단면"), _plane, L("위치"), _offset, L("색"), _field, L("컬러맵"), _map, _particles, _lines, _pause, _restart, fit,
            new Help.HelpTip { HelpKey = Help.HelpKeys.AirflowControls, Margin = new Thickness(8, 0, 0, 0) }]);
        var bars = new Border { Classes = { "toolbar" }, Padding = new Thickness(0, 2), Child = new StackPanel { Children = { row1, row2 } } };

        var hudPanel = new StackPanel { Spacing = 3, Children = { _hud, _hudSub, _status } };
        _overlayLeft.Child = hudPanel;
        var gradeBadge = new Border
        {
            Child = _grade, Padding = new Thickness(8, 2), CornerRadius = new CornerRadius(4),
            HorizontalAlignment = HorizontalAlignment.Left,
        }.Bg("GradeBadgeBrush");
        _grade.Text = $"신뢰 등급: {AirflowFrames.Grade}";
        ToolTip.SetTip(gradeBadge, AirflowFrames.GradeNote);
        var coeffTip = new Help.HelpTip { HelpKey = Help.HelpKeys.AirflowCoeffs, Margin = new Thickness(8, 0, 0, 0) };
        _coeffBox.Child = new StackPanel { Spacing = 6, Children = { new StackPanel { Orientation = Orientation.Horizontal, Children = { gradeBadge, coeffTip } }, _coeffs } };
        _overlayRight.Children.Add(_legend);
        _overlayRight.Children.Add(_coeffBox);
        var stage = new Grid().Res(Panel.BackgroundProperty, "StageBgBrush");
        stage.Children.Add(_view);
        stage.Children.Add(_overlayLeft);
        stage.Children.Add(_overlayRight);
        _overlayLeft.IsHitTestVisible = false;
        _foot.Text = AirflowFrames.Limitation;
        var foot = new Border { Child = _foot, Classes = { "foot" } };
        var root = new DockPanel();
        DockPanel.SetDock(bars, Dock.Top);
        DockPanel.SetDock(foot, Dock.Bottom);
        root.Children.Add(bars);
        root.Children.Add(foot);
        root.Children.Add(stage);
        Content = root;

        _view.ShowSurface = false;
        _view.LineWidthPx = 1.4;
        _view.Animate = false;

        void Changed() { if (!_updating) { UpdateLabels(); _debounce.Stop(); _debounce.Start(); } }
        foreach (var s in new[] { _alpha, _beta, _speed, _re, _offset })
            s.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) Changed(); };
        _res.SelectionChanged += (_, _) => Changed();
        _plane.SelectionChanged += (_, _) => Changed();
        _particles.IsCheckedChanged += (_, _) => Changed();
        _lines.IsCheckedChanged += (_, _) => Changed();
        _pause.IsCheckedChanged += (_, _) => Changed();
        _field.SelectionChanged += (_, _) => { if (_shown is { } f) Apply(f, force: true); };
        _map.SelectionChanged += (_, _) => { if (_shown is { } f) Apply(f, force: true); };
        _body.SelectionChanged += (_, _) => { if (!_updating) OnBodyChanged(); };
        _mode.SelectionChanged += (_, _) => OnModeChanged();
        _seq.SelectionChanged += (_, _) => { _replayT = 0; };
        _restart.Click += async (_, _) => { if (_client is { } c) { try { await c.ResetAsync(); await SendConfig(); } catch (Exception ex) { SetStatus(ex.Message); } } else await StartServer(); };
        _debounce.Tick += async (_, _) => { _debounce.Stop(); await SendConfig(); };
        _timer.Tick += (_, _) => Tick();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            else if (e.Key is Key.Left or Key.OemComma) { _alpha.Value = Math.Max(_alpha.Minimum, _alpha.Value - 5); e.Handled = true; }
            else if (e.Key is Key.Right or Key.OemPeriod) { _alpha.Value = Math.Min(_alpha.Maximum, _alpha.Value + 5); e.Handled = true; }
            else if (e.Key == Key.Space) { _pause.IsChecked = _pause.IsChecked != true; e.Handled = true; }
            else if (e.Key == Key.F) { _view.ResetView(); e.Handled = true; }
        };
        Opened += async (_, _) => { _timer.Start(); await StartServer(); };
        Closing += (_, _) =>
        {
            _closing = true;
            _timer.Stop();
            var c = _client;
            _client = null;
            if (c is not null) Task.Run(c.Dispose);
        };

        SetRe(DefaultRe);
        if (sources.Count > 0) { _body.SelectedIndex = 0; OnBodyChanged(send: false); }
        else SetStatus("가상 풍동에 놓을 물체 메시가 없습니다.");
    }

    // ---------------- 설정 ----------------

    private AirflowSource? Src => _body.SelectedIndex >= 0 && _body.SelectedIndex < _sources.Count ? _sources[_body.SelectedIndex] : null;

    /// <summary>Re 슬라이더(0–1, 로그) → 계산 Re. 1 = 격자 최대(0 을 보냄).</summary>
    private double ReValue => _re.Value >= 0.999 ? 0 : ReMin * Math.Pow(Math.Max(_reMax, ReMin * 1.01) / ReMin, _re.Value);

    private void SetRe(double re)
    {
        _updating = true;
        _re.Value = re <= 0 || re >= _reMax ? 1 : Math.Clamp(Math.Log(Math.Max(re, ReMin) / ReMin) / Math.Log(Math.Max(_reMax, ReMin * 1.01) / ReMin), 0, 0.998);
        _updating = false;
        UpdateLabels();
    }

    private void UpdateLabels()
    {
        _alphaText.Text = string.Create(Inv, $"{_alpha.Value:0}°");
        _betaText.Text = string.Create(Inv, $"{_beta.Value:0}°");
        _speedText.Text = string.Create(Inv, $"{_speed.Value:0} m/s");
        _reText.Text = ReValue <= 0 ? string.Create(Inv, $"최대 {_reMax:0}") : string.Create(Inv, $"{ReValue:0}");
    }

    private AirflowSettings Settings()
    {
        var s = Src;
        string res = AirflowSettings.Resolutions[Math.Clamp(_res.SelectedIndex, 0, 2)];
        double half = s is null ? 1 : (s.Mesh.Bounds().Max - s.Mesh.Bounds().Min).Norm / 2;
        return new AirflowSettings
        {
            Mesh = _meshPath, Alpha = _alpha.Value, Beta = _beta.Value, Speed = _speed.Value, Re = ReValue, Resolution = res,
            RefLength = s?.RefLength ?? 0, RefArea = s?.RefArea ?? 0, Plane = _plane.SelectedIndex == 1 ? "xy" : "xz",
            SliceOffset = _offset.Value * half, Particles = _particles.IsChecked == true, Lines = _lines.IsChecked == true,
            Paused = _pause.IsChecked == true || !Live,
        };
    }

    private void OnBodyChanged(bool send = true)
    {
        if (Src is not { } s) return;
        try
        {
            Directory.CreateDirectory(_meshDir);
            _meshPath = Path.Combine(_meshDir, $"body_{_body.SelectedIndex}_{SafeName(s.Name)}.stl");
            AirflowEnvironment.WriteStl(s.Mesh, _meshPath);
        }
        catch (Exception ex) { SetStatus($"메시 저장 실패: {ex.Message}"); }
        var (mn, mx) = s.Mesh.Bounds();
        _view.SetMesh(s.Mesh, WindTunnel.SmoothNormals(s.Mesh));
        _view.BodyCenter = (mn + mx) * 0.5;
        _view.SceneRadius = (mx - mn).Norm * 1.1;
        _view.ResetView();
        _view.AlphaDeg = _alpha.Value; _view.BetaDeg = _beta.Value;
        if (send) _ = SendConfig();
    }

    private static string SafeName(string n) => new(n.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray());

    private async Task SendConfig()
    {
        if (_client is null || !_client.Connected || _closing) return;
        try { await _client.SendConfigAsync(Settings()); }
        catch (Exception ex) { SetStatus($"설정 전송 실패: {ex.Message}"); }
    }

    private void OnModeChanged()
    {
        bool replay = _mode.SelectedIndex == 1;
        if (replay && _sequences.Count == 0)
        {
            SetStatus("OpenFOAM 재생 자료가 없습니다 — 비정상 해석 시퀀스(schema mns.flow-sequence/1)가 results/cfd 또는 config/aero_db 아래 sequence.json 으로 생기면 이 모드가 동작합니다.");
            _mode.SelectedIndex = 0;
            return;
        }
        _seq.IsVisible = replay;
        foreach (var c in new Control[] { _body, _alpha, _beta, _speed, _re, _res, _restart }) c.IsEnabled = !replay;
        _replayT = 0;
        _ = SendConfig();   // 재생 중에는 GPU 일시정지
        SetStatus(replay ? "OpenFOAM 재생 — GPU 계산 일시정지" : "");
    }

    // ---------------- 서버 ----------------

    private async Task StartServer()
    {
        if (_client is not null || _closing) return;
        var env = AirflowEnvironment.Find(_repo, out var why);
        if (env is null)
        {
            _serverState = "파이썬 환경 없음";
            SetStatus(why);
            _log("warn", why.Replace('\n', ' '));
            return;
        }
        _serverState = "서버 시작 중";
        SetStatus("GPU 가상 풍동 서버 시작 중 (XLB · NVIDIA Warp) …");
        try
        {
            var c = await AirflowClient.StartAsync(env.Value.Python, env.Value.Script, _repo, TimeSpan.FromSeconds(90));
            if (_closing) { _ = Task.Run(c.Dispose); return; }
            c.FrameReceived += f => _latest = f;
            c.Status += (phase, msg) => Dispatcher.UIThread.Post(() => { _serverState = phase; SetStatus(phase == "ready" ? "" : msg); _log("info", $"GPU 풍동: {msg}"); });
            c.Error += (msg, diverged, newRe) => Dispatcher.UIThread.Post(() =>
            {
                SetStatus((diverged ? "발산 — " : "오류 — ") + msg);
                _log(diverged ? "warn" : "error", $"GPU 풍동: {msg}");
                if (newRe is { } r) SetRe(r);
            });
            c.Log += line => { if (line.Contains("Error", StringComparison.OrdinalIgnoreCase) || line.Contains("Traceback")) Dispatcher.UIThread.Post(() => _log("warn", $"airflow: {line}")); };
            c.Disconnected += why2 => Dispatcher.UIThread.Post(() => { if (!_closing) { _serverState = "끊김"; SetStatus($"서버 연결이 끊겼습니다: {why2} — '다시 시작'"); _client = null; } });
            _client = c;
            _serverState = "연결됨";
            await SendConfig();
        }
        catch (Exception ex)
        {
            _serverState = "시작 실패";
            SetStatus($"서버 시작 실패: {ex.Message}");
            _log("error", $"GPU 풍동 서버 시작 실패: {ex.Message}");
        }
    }

    private void SetStatus(string s) => _status.Text = s;

    // ---------------- 표시 ----------------

    private void Tick()
    {
        var now = DateTime.UtcNow;
        double dt = Math.Min((now - _lastTick).TotalSeconds, 0.2);
        _lastTick = now;
        if (!Live)
        {
            if (_seq.SelectedIndex >= 0 && _seq.SelectedIndex < _sequences.Count)
            {
                var sq = _sequences[_seq.SelectedIndex];
                double span = sq.Frames[^1].T - sq.Frames[0].T;
                _replayT += dt * (span > 0 ? span / 10.0 : 0);   // 시퀀스 전체를 약 10 s 에 한 번
                try { Apply(sq.ReadFrame(sq.IndexAt(sq.Frames[0].T + _replayT))); }
                catch (Exception ex) { SetStatus($"재생 프레임 읽기 실패: {ex.Message}"); }
            }
            return;
        }
        if (_latest is { } f && !ReferenceEquals(f, _shown)) Apply(f);
    }

    private void Apply(AirflowFrame f, bool force = false)
    {
        bool fresh = !ReferenceEquals(f, _shown);
        _shown = f;
        if (fresh && !force)
        {
            FramesShown++;
            _fpsN++;
            var now = DateTime.UtcNow;
            if ((now - _fpsT0).TotalSeconds >= 1) { _fps = _fpsN / (now - _fpsT0).TotalSeconds; _fpsN = 0; _fpsT0 = now; }
        }
        if (f.ReMax > 0 && Math.Abs(f.ReMax - _reMax) > 0.5) { double keep = ReValue; _reMax = f.ReMax; SetRe(keep); }
        _view.AlphaDeg = f.Alpha;
        _view.BetaDeg = f.Beta;
        string map = ColorMaps.Names[Math.Max(_map.SelectedIndex, 0)];
        var speedRange = new ScaleRange(0, SpeedRange, true);
        int field = Math.Max(_field.SelectedIndex, 0);
        // 단면
        if (f.Slice is { } sl && field < 2)
        {
            bool vort = field == 1;
            var vals = vort ? sl.Vorticity : sl.Speed;
            ScaleRange range;
            if (vort)
            {
                var finite = vals.Where(float.IsFinite).Select(v => (double)Math.Abs(v)).OrderBy(v => v).ToArray();
                double p98 = finite.Length > 0 ? finite[(int)(0.98 * (finite.Length - 1))] : 1;
                _vortRange = 0.85 * _vortRange + 0.15 * Math.Max(p98, 0.5);
                double r = Math.Round(_vortRange, 1);
                range = new ScaleRange(-r, r, true);
            }
            else range = speedRange;
            var rgba = new byte[4 * sl.NA * sl.NB];
            // 텍스처 행 = b(높이), 열 = a(폭)
            for (int a = 0; a < sl.NA; a++)
                for (int b = 0; b < sl.NB; b++)
                {
                    float v = vals[a * sl.NB + b];
                    int o = 4 * (b * sl.NA + a);
                    if (!float.IsFinite(v)) { rgba[o + 3] = 0; continue; }
                    var (r, g, bb) = ColorScale.ValueColor(map, v, range);
                    rgba[o] = (byte)(r * 255); rgba[o + 1] = (byte)(g * 255); rgba[o + 2] = (byte)(bb * 255);
                    rgba[o + 3] = (byte)(vort ? 70 + 150 * Math.Min(1, Math.Abs(v) / Math.Max(range.Max, 1e-9)) : 205);
                }
            _view.SetSlice(sl.Origin, sl.AxisA, sl.AxisB, sl.NA, sl.NB, rgba);
            _legend.Title = vort ? "단면 와도 ω·L/V∞" : "단면 속도 |U|/V∞";
            _legend.Unit = vort ? (sl.Plane == "xz" ? "ω_y (피치면)" : "ω_z (요잉면)") : "자유류 대비";
            _legend.Min = range.Min; _legend.Max = range.Max;
            _legend.ColorMap = map;
            _legend.NoData = false;
            _legend.Detail = sl.Plane == "xz" ? "XZ 피치면 (받음각 평면)" : "XY 요잉면 (옆미끄럼 평면)";
            _legend.IsVisible = true;
            _legend.Changed();
        }
        else
        {
            _view.SetSlice(Vec3.Zero, Vec3.Zero, Vec3.Zero, 0, 0, null);
            _legend.IsVisible = false;
        }
        // 입자 · 유선(속력 색)
        _view.ParticleColor = s => ColorScale.ValueColor(map, s, speedRange);
        _view.LivePoints = f.Particles;
        _view.Lines = f.Streamlines().Select(l =>
        {
            var rgba = new float[4 * l.P.Length];
            for (int i = 0; i < l.P.Length; i++)
            {
                var (r, g, b) = ColorScale.ValueColor(map, l.S[i], speedRange);
                rgba[4 * i] = r; rgba[4 * i + 1] = g; rgba[4 * i + 2] = b; rgba[4 * i + 3] = 0.85f;
            }
            return (l.P, rgba);
        }).ToList();

        // HUD
        string src = Live ? $"GPU 실시간 · {f.Source} {f.Device}" : $"OpenFOAM 재생 · {(_seq.SelectedIndex >= 0 ? _sequences[_seq.SelectedIndex].Label : "")}";
        _hud.Text = string.Create(Inv, $"받음각 α {f.Alpha:0.#}°  ·  옆미끄럼 β {f.Beta:0.#}°  ·  V∞ {f.Speed:0} m/s (M {f.Mach:0.00})");
        string reLine = f.Re > 0
            ? string.Create(Inv, $"계산 Re {f.Re:0}{(f.ReClamped ? " (격자 최대)" : "")} · 실제 Re {f.RePhys:0.0e0} (기준 길이 {f.RefLength:0.###} m)")
            : "";
        string grid = f.Shape.Length == 3 ? string.Create(Inv, $"격자 {f.Shape[0]}×{f.Shape[1]}×{f.Shape[2]} · dx {f.Dx * 1000:0.#} mm") : "";
        string clock = string.Create(Inv, $"흐름 시간 {f.TimePhys * 1000:0.0} ms ({f.FlowThroughs:0.#} L 통과) · {f.StepsPerSecond:0} 스텝/s · 화면 {_fps:0.#} fps");
        _hudSub.Text = $"{src}\n{reLine}\n{grid} · {clock}\n입자 {f.ParticleCount} · 유선 {f.LineCounts.Length} · 흐름 +X → −X · 드래그 회전 · 휠 확대 · ←/→ 받음각 5° · Space 일시정지"
            + (f.Mach > 0.3 ? "\n⚠ 마하 0.3 초과: 압축성 효과(충격파·밀도 변화)는 이 계산에 없습니다." : "");
        // 계수: LBM 평균 vs OpenFOAM 정상 RANS
        var lines = new List<string>();
        if (f.CoeffsMean is { } m) lines.Add(string.Create(Inv, $"LBM 평균   CD {m.CD,6:0.00}  CL {m.CL,6:0.00}  CY {m.CY,6:0.00}"));
        else if (f.Settling || f.Coeffs is not null) lines.Add("LBM 평균   (과도 구간 — 평균 준비 중)");
        if (f.Coeffs is { } c) lines.Add(string.Create(Inv, $"LBM 순간   CD {c.CD,6:0.00}  CL {c.CL,6:0.00}"));
        if (Src?.Reference?.At(f.Alpha, f.Beta) is { } of)
            lines.Add(string.Create(Inv, $"{of.Label}\n           CD {of.CD,6:0.00}  CL {of.CL,6:0.00}  (M 0.5, Re ~10⁶)"));
        else lines.Add("OpenFOAM 비교값 없음 (이 물체의 표면 유동장 없음)");
        lines.Add(string.Create(Inv, $"기준 S = {f.RefArea:0.####} m², 축 = OpenFOAM DB 와 같음"));
        lines.Add("LBM 은 저 Re·비압축·닫힌 시험부 → 크기 차이 정상");
        if (f.Re > 450) lines.Add("Re 가 격자 한계에 가까움 → 압력파 잡음으로 힘 평균 과대");
        _coeffs.Text = string.Join("\n", lines);
        _view.RequestNextFrameRendering();
    }

    // ---------------- 스모크 ----------------

    /// <summary>프레임이 n 개 이상 보이고(그리고 평균이 준비되거나) 제한 시간이 지날 때까지 기다림. 반환: 조건 충족 여부.</summary>
    public async Task<bool> WaitFrames(int n, TimeSpan timeout, bool needMean = false)
    {
        var t0 = DateTime.UtcNow;
        while (DateTime.UtcNow - t0 < timeout)
        {
            if (FramesShown >= n && (!needMean || _shown?.CoeffsMean is not null)) return true;
            await Task.Delay(200);
        }
        return false;
    }

    public void SelectField(int i) => _field.SelectedIndex = Math.Clamp(i, 0, 2);

    public void SetAlpha(double a) => _alpha.Value = Math.Clamp(a, -180, 180);

    public void SetBeta(double b) => _beta.Value = Math.Clamp(b, -45, 45);

    public void SetResolution(string r) => _res.SelectedIndex = Math.Max(0, Array.IndexOf(AirflowSettings.Resolutions, r));

    /// <summary>현재 장면을 PNG 로(GL 스냅숏 + 창 + 오버레이 합성). 반환: 로그 한 줄.</summary>
    public async Task<string> Capture(string path)
    {
        await Task.Delay(600);
        var gl = Path.ChangeExtension(path, null) + "_gl.png";
        _view.RequestSnapshot(gl);
        await Task.Delay(700);
        var size = new PixelSize(Math.Max(1, (int)Bounds.Width), Math.Max(1, (int)Bounds.Height));
        using var rtb = new Avalonia.Media.Imaging.RenderTargetBitmap(size);
        rtb.Render(this);
        if (File.Exists(gl) && _view.TranslatePoint(new Point(0, 0), this) is { } o)
        {
            using var bmp = new Avalonia.Media.Imaging.Bitmap(gl);
            using (var ctx = rtb.CreateDrawingContext(false)) ctx.DrawImage(bmp, new Rect(bmp.Size), new Rect(o, _view.Bounds.Size));
            foreach (var c in new Control[] { _overlayLeft, _overlayRight })
                if (c.IsVisible && c.Bounds.Width >= 1 && c.Bounds.Height >= 1 && c.TranslatePoint(new Point(0, 0), this) is { } p)
                {
                    using var over = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)c.Bounds.Width, (int)c.Bounds.Height));
                    over.Render(c);
                    using var ctx = rtb.CreateDrawingContext(false);
                    ctx.DrawImage(over, new Rect(over.Size), new Rect(p, c.Bounds.Size));
                }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        rtb.Save(path);
        var f = _shown;
        return string.Create(Inv, $"ui.airflow state='{_serverState}' mode={(Live ? "gpu" : "replay")} body='{Src?.Name}' frames={FramesShown} alpha={f?.Alpha:0.#} beta={f?.Beta:0.#} re={f?.Re:0} reMax={f?.ReMax:0} step={f?.Step} stepsPerS={f?.StepsPerSecond:0} fps={_fps:0.0} slice={(f?.Slice is { } s ? $"{s.Plane}:{s.NA}x{s.NB}" : "none")} particles={f?.ParticleCount} lines={f?.LineCounts.Length} cd={f?.CoeffsMean?.CD:0.00} cl={f?.CoeffsMean?.CL:0.00} replaySeqs={_sequences.Count} status='{_status.Text?.Replace('\n', ' ')}' gl='{_view.GlInfo}' file={path}");
    }
}
