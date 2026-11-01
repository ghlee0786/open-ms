using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Sim.App.Controls;
using Sim.App.Rendering;
using Sim.App.Theme;
using Sim.Core.Geometry;
using Sim.Core.Models;
using Sim.Core.Structures;
using Vec3 = Sim.Core.Numerics.Vec3;

namespace Sim.App;

/// <summary>풍동 보기 대상: 이름 · CFD 표면 유동장 · 표시 메시(동체 좌표 — 메인 3D 뷰와 같은 TriMesh 객체).</summary>
public sealed record WindTunnelSource(string Name, SurfaceFlowField Flow, TriMesh Mesh);

/// <summary>
/// 풍동 보기 창(해석 → 풍동 보기): CFD(OpenFOAM) 결과를 어두운 배경의 후처리 장면으로 — 표면 Cp(또는 속도비·Cf) 정점 보간 색,
/// 3-D 유선(속도 색 또는 단색 노랑), 유선을 따라 흐르는 입자, 받음각(케이스) 슬라이더 · 마하 표시 · 레전드 · 출처·한계 한 줄.
/// 표면 값은 (물체, 묶음, 장)마다 모든 받음각 케이스를 한 번에 계산해 두고(색 범위도 묶음 전체로 고정) 슬라이더는 값 버퍼만 바꾼다.
/// </summary>
public sealed class WindTunnelWindow : Window
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public const string Limitation =
        "한계: 정상 RANS(k-ω SST) — 원통 기준 실험 대비 항력을 30~60 % 과소 예측(validation/reports/cfd/reference_case_analysis.md). 흐름 모양 이해용 정성 시각화이며 공력 계산 값이 아님.";

    private readonly IReadOnlyList<WindTunnelSource> _sources;
    private readonly WindTunnelView _view = new();
    private readonly ComboBox _body = new() { MinWidth = 150 };
    private readonly ComboBox _group = new() { MinWidth = 120 };
    private readonly Slider _alpha = new() { Width = 220, Minimum = 0, Maximum = 0, TickFrequency = 1, IsSnapToTickEnabled = true, TickPlacement = TickPlacement.BottomRight, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _alphaText = new() { MinWidth = 70, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold };
    private readonly ComboBox _field = new() { MinWidth = 170 };
    private readonly ComboBox _lineMode = new() { MinWidth = 130 };
    private readonly ComboBox _map = new() { MinWidth = 90 };
    private readonly CheckBox _particles = new() { Content = "입자 흐름", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _surface = new() { Content = "표면 색", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly ColorLegend _legend = new() { Ticks = 7 };
    private readonly ColorLegend _lineLegend = new() { Ticks = 5, Title = "유선 속력", Unit = "|U|/V∞" };
    // 무대(3D) 위 글 · 바탕 = Stage* 토큰(두 테마 모두 어두운 무대 — 흐름 색 대비), 막대 · 바닥줄 = 테마 토큰
    private readonly TextBlock _hud = new TextBlock { FontSize = 14, FontWeight = FontWeight.SemiBold }.Fg("StageTextBrush");
    private readonly TextBlock _hudSub = new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, MaxWidth = 820 }.Fg("StageDimBrush");
    private readonly TextBlock _source = new() { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Classes = { "muted" } };
    private readonly StackPanel _overlayRight = new() { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 12, 12, 0) };
    private readonly StackPanel _hudPanel = new() { Spacing = 3 };
    private readonly Border _overlayLeft = new Border
    {
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12, 10, 0, 0),
        Padding = new Thickness(10, 6), CornerRadius = new CornerRadius(8),
    }.Bg("StageHudBrush");
    private readonly Grid _stage = new Grid().Res(Panel.BackgroundProperty, "StageBgBrush");

    /// <summary>(물체, 묶음, 장) → 케이스별 정점 값 · 묶음 전체 색 범위.</summary>
    private readonly Dictionary<(int, string, TunnelField), (double[][] Values, ScaleRange Range)> _cache = new();
    private readonly Dictionary<int, Vec3[]> _normals = new();
    private List<SurfaceFlowField.FlowCase> _cases = [];
    private int _meshShown = -1;
    private bool _updating;

    public WindTunnelWindow(IReadOnlyList<WindTunnelSource> sources, string subtitle = "")
    {
        _sources = sources;
        Title = "풍동 보기 — CFD 표면 압력 · 3-D 유선" + (subtitle.Length > 0 ? $" ({subtitle})" : "");
        Width = 1320; Height = 860; MinWidth = 820; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this.KeepOnScreen();

        foreach (var s in sources) _body.Items.Add(s.Name);
        foreach (var f in WindTunnel.Fields) _field.Items.Add(f.Label);
        foreach (var m in new[] { "속도 크기 색", "단색 노랑", "끔" }) _lineMode.Items.Add(m);
        foreach (var m in ColorMaps.Names) _map.Items.Add(m);
        _field.SelectedIndex = 0; _lineMode.SelectedIndex = 0; _map.SelectedIndex = 0;

        static TextBlock L(string s) => new() { Text = s, VerticalAlignment = VerticalAlignment.Center, Classes = { "dim" }, Margin = new Thickness(10, 0, 4, 0) };
        var fit = new Button { Content = "전체 보기", Margin = new Thickness(10, 0, 0, 0) };
        fit.Click += (_, _) => _view.ResetView();
        var bar = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var c in new Control[] { L("물체"), _body, L("조건"), _group, L("받음각"), _alpha, _alphaText, L("표면"), _field, _surface, L("유선"), _lineMode, _particles, L("컬러맵"), _map, fit,
                     new Help.HelpTip { HelpKey = Help.HelpKeys.WindTunnelControls, Margin = new Thickness(8, 0, 0, 0) } })
        {
            if (c is not TextBlock) c.Margin = new Thickness(c.Margin.Left, 2, 4, 2);
            bar.Children.Add(c);
        }
        var barBorder = new Border { Classes = { "toolbar" }, Child = bar };

        _hudPanel.Children.Add(_hud);
        _hudPanel.Children.Add(_hudSub);
        _overlayLeft.Child = _hudPanel;
        _overlayRight.Children.Add(_legend);
        _overlayRight.Children.Add(_lineLegend);
        _stage.Children.Add(_view);
        _stage.Children.Add(_overlayLeft);
        _stage.Children.Add(_overlayRight);
        _stage.Children.Add(new Help.HelpTip { HelpKey = Help.HelpKeys.WindTunnelStage, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(12) });
        _overlayLeft.IsHitTestVisible = false;
        _overlayRight.IsHitTestVisible = false;

        var foot = new Border { Child = _source, Classes = { "foot" } };
        var root = new DockPanel();
        DockPanel.SetDock(barBorder, Dock.Top);
        DockPanel.SetDock(foot, Dock.Bottom);
        root.Children.Add(barBorder);
        root.Children.Add(foot);
        root.Children.Add(_stage);
        Content = root;

        _body.SelectionChanged += (_, _) => { if (!_updating) OnBodyChanged(); };
        _group.SelectionChanged += (_, _) => { if (!_updating) OnGroupChanged(); };
        _alpha.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty && !_updating) ShowCase(); };
        _field.SelectionChanged += (_, _) => { if (!_updating) ShowCase(); };
        _map.SelectionChanged += (_, _) => { if (!_updating) ShowCase(); };
        _lineMode.SelectionChanged += (_, _) => { if (!_updating) ShowCase(); };
        _surface.IsCheckedChanged += (_, _) => { _view.ShowSurface = _surface.IsChecked == true; _legend.IsVisible = _view.ShowSurface; _view.RequestNextFrameRendering(); };
        _particles.IsCheckedChanged += (_, _) => ShowCase();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            else if (e.Key is Key.Left or Key.OemComma) { _alpha.Value = Math.Max(_alpha.Minimum, _alpha.Value - 1); e.Handled = true; }
            else if (e.Key is Key.Right or Key.OemPeriod) { _alpha.Value = Math.Min(_alpha.Maximum, _alpha.Value + 1); e.Handled = true; }
            else if (e.Key == Key.Space) { _view.Animate = !_view.Animate; _view.RequestNextFrameRendering(); e.Handled = true; }
            else if (e.Key == Key.F) { _view.ResetView(); e.Handled = true; }
        };

        if (sources.Count > 0)
        {
            _body.SelectedIndex = 0;
            OnBodyChanged();
        }
        else _hud.Text = "CFD 유동장이 있는 물체가 없습니다.";
    }

    // ---------------- 선택 ----------------

    private WindTunnelSource? Src => _body.SelectedIndex >= 0 && _body.SelectedIndex < _sources.Count ? _sources[_body.SelectedIndex] : null;
    private SurfaceFlowField.FlowCase? Case => _cases.Count == 0 ? null : _cases[Math.Clamp((int)Math.Round(_alpha.Value), 0, _cases.Count - 1)];
    private TunnelField Field => WindTunnel.Fields[Math.Max(_field.SelectedIndex, 0)].Field;
    private string MapName => ColorMaps.Names[Math.Max(_map.SelectedIndex, 0)];

    private void OnBodyChanged()
    {
        if (Src is not { } src) return;
        _updating = true;
        _group.Items.Clear();
        // 유효 케이스가 있는 묶음, 받음각 케이스가 많은 묶음이 먼저(M0.50 정상 RANS 7 케이스 등)
        var groups = src.Flow.Cases.Where(c => c.Valid).GroupBy(c => c.Group).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => g.Key).ToList();
        foreach (var g in groups) _group.Items.Add(g);
        _group.SelectedIndex = groups.Count > 0 ? 0 : -1;
        _updating = false;
        if (_meshShown != _body.SelectedIndex)
        {
            int bi = _body.SelectedIndex;
            if (!_normals.TryGetValue(bi, out var n)) _normals[bi] = n = WindTunnel.SmoothNormals(src.Mesh);
            _view.SetMesh(src.Mesh, n);
            _view.BodyCenter = WindTunnel.Center(src.Flow);
            var (mn, mx) = src.Mesh.Bounds();
            // 장면 = 물체 + 유선이 지나가는 주변(물체 크기의 약 1.6 배)
            _view.SceneRadius = Math.Max((mx - mn).Norm * 0.8, src.Flow.Radius * 4);
            _view.ResetView();
            _meshShown = bi;
        }
        OnGroupChanged();
    }

    private void OnGroupChanged()
    {
        if (Src is not { } src) return;
        string g = _group.SelectedItem as string ?? "";
        double keep = Case?.Alpha ?? 45;
        _cases = src.Flow.Cases.Where(c => c.Valid && c.Group == g).OrderBy(c => c.Alpha).ToList();
        _updating = true;
        _alpha.Maximum = Math.Max(0, _cases.Count - 1);
        _alpha.IsEnabled = _cases.Count > 1;
        _alpha.Value = NearestIndex(keep);
        _updating = false;
        ShowCase();
    }

    private int NearestIndex(double alpha) =>
        _cases.Count == 0 ? 0 : _cases.Select((c, i) => (D: Math.Abs(c.Alpha - alpha), i)).OrderBy(x => x.D).First().i;

    /// <summary>받음각(deg)에 가장 가까운 케이스로(스모크 · 메인 창에서 현재 자세로 열 때).</summary>
    public void SelectAlpha(double alpha) => _alpha.Value = NearestIndex(alpha);

    /// <summary>표면 장 선택(스모크 --wt-field).</summary>
    public void SelectField(TunnelField f) => _field.SelectedIndex = Math.Max(0, Array.FindIndex(WindTunnel.Fields, x => x.Field == f));

    // ---------------- 표시 ----------------

    private (double[][] Values, ScaleRange Range) Values(WindTunnelSource src, string group, TunnelField f)
    {
        var key = (_body.SelectedIndex, group, f);
        if (_cache.TryGetValue(key, out var v)) return v;
        var all = _cases.Select(c => WindTunnel.Values(src.Flow, c, src.Mesh.Vertices, f)).ToArray();
        var range = ColorScale.Resolve(all.SelectMany(a => a), zeroBased: f != TunnelField.Cp);
        _cache[key] = v = (all, range);
        return v;
    }

    private void ShowCase()
    {
        if (Src is not { } src || Case is not { } c) return;
        int ci = _cases.IndexOf(c);
        var info = Array.Find(WindTunnel.Fields, x => x.Field == Field);
        var (vals, range) = Values(src, c.Group, Field);
        _view.SetValues(vals[ci], range, MapName);
        _view.AlphaDeg = c.Alpha;
        _alphaText.Text = string.Create(Inv, $"α {c.Alpha:0.#}°");

        // 유선: CFD 좌표 → 풍동 좌표(흐름 −X), 흐름 방향으로 정렬, 색 = 속력비 컬러맵 또는 단색 노랑
        var center = WindTunnel.Center(src.Flow);
        var lines = c.Streamlines.Select(s => (P: s.Points.Select(p => WindTunnel.ToTunnel(p, c.Alpha, center)).ToArray(), S: s.SpeedRatio)).ToList();
        var particles = new StreamlineParticles(lines, WindTunnel.FlowDir);
        double sMax = Math.Max(1.2, _cases.SelectMany(k => k.Streamlines).SelectMany(s => s.SpeedRatio).DefaultIfEmpty(1).Max());
        var lineRange = new ScaleRange(0, Math.Ceiling(sMax * 10) / 10, true);
        int mode = Math.Max(_lineMode.SelectedIndex, 0);
        string map = MapName;
        _view.Lines = mode == 2 ? [] : particles.Lines.Select(l =>
        {
            var rgba = new float[4 * l.P.Length];
            for (int i = 0; i < l.P.Length; i++)
            {
                var (r, g, b) = mode == 0 ? ColorScale.ValueColor(map, l.S[i], lineRange) : (1f, 0.84f, 0.18f);   // 단색 노랑 = 데이터 색
                rgba[4 * i] = r; rgba[4 * i + 1] = g; rgba[4 * i + 2] = b; rgba[4 * i + 3] = 0.9f;
            }
            return (l.P, rgba);
        }).ToList();
        _view.Particles = _particles.IsChecked == true && particles.Lines.Count > 0 ? particles : null;
        _view.ParticleColor = null;

        _legend.Title = info.Label;
        _legend.Unit = info.Unit;
        bool synthetic = c.Method == "synthetic";   // 공개 샘플(samples/flow) — 해석적 근사로 만든 합성 예시
        _legend.Note = synthetic ? "합성 예시 (해석적 근사 — CFD 아님)" : info.Note;
        _legend.Min = range.Min; _legend.Max = range.Max;
        _legend.ColorMap = map;
        _legend.Detail = $"범위 = {c.Group} 전 받음각 공통";
        _legend.IsVisible = _view.ShowSurface;
        _legend.Changed();
        _lineLegend.Min = lineRange.Min; _lineLegend.Max = lineRange.Max;
        _lineLegend.ColorMap = map;
        _lineLegend.Note = synthetic ? "합성 유선 (해석적 근사)" : "OpenFOAM 유선 (속도장 적분)";
        _lineLegend.IsVisible = mode == 0 && lines.Count > 0;
        _lineLegend.Changed();

        string method = synthetic ? "합성 예시" : c.Method == "urans" ? "URANS 시간 평균" : "정상 RANS";
        _hud.Text = string.Create(Inv, $"받음각 α {c.Alpha:0.#}°   ·   마하 M {c.Mach:0.00}   ·   V∞ {c.Speed:0.#} m/s");
        _hudSub.Text = $"{src.Name} · {c.Label} · {method} · 유선 {lines.Count} 개{(_view.Particles is null ? "" : " · 입자 흐름")}\n흐름: 화면의 +X → −X (모형이 받음각만큼 기울어짐) · 드래그 회전 · 휠 확대 · 더블클릭 전체 보기 · ←/→ 받음각 · Space 입자 멈춤";
        _source.Text = synthetic
            ? $"출처: 합성 예시(해석적 근사 — CFD 결과 아님) — {Path.GetFileName(src.Flow.Source)} · {c.Label}.   {src.Flow.Warning}"
            : $"출처: OpenFOAM v2512 {method}(k-ω SST) — {Path.GetFileName(src.Flow.Source)} · {c.Label}.   {Limitation}";
        _view.RequestNextFrameRendering();
    }

    // ---------------- 스모크 캡처 ----------------

    /// <summary>현재 장면을 PNG 로(GL 스냅숏 + 창 + 오버레이 합성). 반환: 로그 한 줄.</summary>
    public async Task<string> Capture(string path)
    {
        await Task.Delay(1200);
        var gl = Path.ChangeExtension(path, null) + "_gl.png";
        _view.RequestSnapshot(gl);
        await Task.Delay(900);
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
        var cs = Case;
        return string.Create(Inv, $"ui.windtunnel body='{Src?.Name}' group={cs?.Group} alpha={cs?.Alpha:0.#} mach={cs?.Mach:0.00} field={Field} range={_legend.Min:G4}..{_legend.Max:G4} lines={_view.Lines.Count} particles={_view.ParticleCount} meshVerts={Src?.Mesh.Vertices.Count} frames={_view.FramesRendered} gl='{_view.GlInfo}' file={path}");
    }
}
