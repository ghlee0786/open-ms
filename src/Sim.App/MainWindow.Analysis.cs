using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Sim.App.Rendering;
using Sim.Core.Analysis;
using Sim.Core.Engine;
using Sim.Core.Fea;
using Sim.Core.Models;
using Sim.Core.Numerics;
using Sim.Core.Scenario;
using Sim.Core.Structures;

namespace Sim.App;

/// <summary>결과 저장·불러오기(.mnsr) · 응력/압력/재질 색과 레전드 · 구조 분석 · OpenCourant · 하중 케이스.</summary>
public partial class MainWindow
{
    private ColorMode _colorMode = ColorMode.Off;
    private string _colorMap = "Turbo";
    private readonly Dictionary<int, StressModel> _models = new();
    private readonly Dictionary<int, StressResult> _liveResults = new();
    private readonly Dictionary<int, double[]> _feaValues = new();
    private StructuralReport? _structural;
    private ScenarioDoc? _runDoc;
    private string? _resultPath;
    private int _colorVersion;
    /// <summary>분할 화면별 마지막 색 계산 키(같으면 다시 계산하지 않음).</summary>
    private readonly string[] _colorKeys = new string[ViewSplit.MaxPanes];
    private string _colorKey { set => Array.Fill(_colorKeys, value); }
    /// <summary>분할 화면 1~3 의 색 모드(0번 = _colorMode = 상단 '색상' 콤보).</summary>
    private readonly ColorMode[] _paneModes = [ColorMode.Off, ColorMode.Stress, ColorMode.Pressure, ColorMode.Safety];
    /// <summary>프레임 하나의 근사 응력 평가 캐시 — 여러 화면이 같은 시각의 압력·응력·안전계수를 보여도 한 번만 계산.</summary>
    private readonly Dictionary<int, StressResult> _evalCache = new();
    private int _evalIdx = -1;

    private static readonly (ColorMode Mode, string Label)[] ColorModes = ColorModeInfo.All.Select(x => (x.Mode, x.Label)).ToArray();

    private ColorMode PaneMode(int pane) => pane == 0 ? _colorMode : _paneModes[pane];

    private void WireAnalysis()
    {
        ColorModeBox.ItemsSource = ColorModes.Select(m => m.Label).ToList();
        ColorModeBox.SelectedIndex = 0;
        ColorMapBox.ItemsSource = ColorMaps.Names;
        ColorMapBox.SelectedIndex = 0;
        ColorModeBox.SelectionChanged += (_, _) =>
        {
            var m = ColorModes[Math.Max(0, ColorModeBox.SelectedIndex)].Mode;
            if (m != _colorMode) SetColorMode(m); // 코드에서 콤보를 맞출 때 재진입 방지
        };
        ColorMapBox.SelectionChanged += (_, _) => { _colorMap = (string?)ColorMapBox.SelectedItem ?? "Turbo"; RecolorNow(); };
        PeakHoldBox.IsCheckedChanged += (_, _) => { if (PeakHoldBox.IsChecked == true && _structural is null && _world is not null) _ = RunStructural(); RecolorNow(); };
        SelectedOnlyBox.IsCheckedChanged += (_, _) => RecolorNow();
        AutoRangeBox.IsCheckedChanged += (_, _) => { RangeMinBox.IsEnabled = RangeMaxBox.IsEnabled = AutoRangeBox.IsChecked != true; RecolorNow(); };
        RangeMinBox.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) RecolorNow(); };
        RangeMaxBox.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) RecolorNow(); };

        TbOpen.Click += async (_, _) => await OpenScenario();
        TbLoad.Click += async (_, _) => await PickAndLoadResult();
        TbSave.Click += async (_, _) => await SaveResults();
        TbRun.Click += async (_, _) => await RunSimulation();
        TbStruct.Click += async (_, _) => await RunStructural();
        MiStructural.Click += async (_, _) => await RunStructural();
        MiLoadCases.Click += async (_, _) => await ExportLoadCases();
        MiFea.Click += async (_, _) => await RunFea();
        MiFeaCheck.Click += (_, _) =>
        {
            var inst = OpenCourantInstall.TryLocate(null, out var why);
            Log(inst is null ? LogLevel.Warn : LogLevel.Info, "OpenCourant",
                inst is null ? why : $"설치 확인: {inst.Root} (starter {Path.GetFileName(inst.Starter)}, engine {Path.GetFileName(inst.Engine)}, anim_to_vtk {(inst.AnimToVtk is null ? "없음" : "있음")})");
            ShowLog();
        };
        MiMaterials.Click += async (_, _) => await ShowMaterials();
        WireCcx();
        WireShock();
        WireCredibility();
        WireWindTunnel();

        AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
#pragma warning disable CS0618 // Avalonia 11.3: Data → DataTransfer 전환 중(파일 목록 API 동일 동작)
            var p = e.Data.GetFiles()?.FirstOrDefault()?.TryGetLocalPath();
#pragma warning restore CS0618
            if (p is null) return;
            if (p.EndsWith(ResultArchive.Extension, StringComparison.OrdinalIgnoreCase)) await LoadResultFromPath(p);
            else if (p.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && !p.EndsWith(".massprops.json", StringComparison.OrdinalIgnoreCase)) LoadScenario(p);
            else if (p.EndsWith(".stl", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".massprops.json", StringComparison.OrdinalIgnoreCase)) { ImportModelFromPath(p); await RunSimulation(); }
        });
    }

    private void SetColorMode(ColorMode m)
    {
        bool changed = m != _colorMode;
        _colorMode = m;
        if (m != ColorMode.Off && !View.Layers[ViewLayer.Legend]) View.Layers[ViewLayer.Legend] = true;
        int i = Array.FindIndex(ColorModes, x => x.Mode == m);
        if (ColorModeBox.SelectedIndex != i) ColorModeBox.SelectedIndex = i;
        if (!changed) { SyncColorMenu(); RecolorNow(); return; }
        SyncColorMenu();
        RecolorNow();
        if (m == ColorMode.FeaPlastic && _feaValues.Count == 0)
            Log(LogLevel.Warn, "색상", "FEA 결과가 없습니다 — 해석 → OpenCourant 착지 충돌 해석을 먼저 실행하세요.");
        if (m is ColorMode.CcxVonMises or ColorMode.CcxSafety or ColorMode.CcxDisplacement && _ccx.Count == 0)
            Log(LogLevel.Warn, "색상", "CalculiX 결과가 없습니다 — 해석 → 정적/진동/좌굴 FEA (F7)를 먼저 실행하세요.");
        if (m is ColorMode.Safety or ColorMode.State && _world is not null && !_world.Bodies.Any(b => b.Structure?.Material is not null))
            Log(LogLevel.Warn, "색상", "재질(structure.material)이 지정된 물체가 없어 안전계수·상태는 '재질 미지정'으로 표시됩니다.");
    }

    private void RecolorNow()
    {
        _colorKey = "";
        UpdateColors();
        UpdateTelemetry();
        View.RequestNextFrameRendering();
    }

    /// <summary>새 결과가 들어오면 응력 모델·분석 캐시를 비운다.</summary>
    private void ResetAnalysis()
    {
        _models.Clear(); _liveResults.Clear(); _feaValues.Clear(); _ccx.Clear();
        ResetShock(); ResetCcxShock();
        _evalCache.Clear(); _evalIdx = -1;
        View.MeshDeform = null; View.MeshDeformVersion++;
        _structural = null; _colorKey = "";
        _deviceRowsFor = -2; _cumMaxG = null;
        foreach (var p in View.Panes) { p.FaceColors = null; p.HotspotMarks = null; }
        foreach (var l in _paneLegends) l.IsVisible = false;
    }

    private StressModel? ModelFor(int i)
    {
        if (_models.TryGetValue(i, out var m)) return m;
        if (_world is null || View.Scene?.BodyMeshes is not { } bm || i >= bm.Length || bm[i] is not { } mesh) return null;
        var b = _world.Bodies[i];
        if (b.Mode == BodyMode.Kinematic) return null;
        m = StressModel.Build(b, mesh);
        if (m is not null) _models[i] = m;
        return m;
    }

    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    /// <summary>현재 시각의 분할 화면별 면 색·레전드·핫스팟 갱신(화면마다 모드·시각·선택이 바뀔 때만 다시 계산).</summary>
    private void UpdateColors()
    {
        int n = View.PaneCount;
        for (int p = 0; p < ViewSplit.MaxPanes; p++)
        {
            if (p < n && _world is not null && View.Scene is not null && PaneMode(p) != ColorMode.Off)
            {
                UpdatePaneColors(p);
                continue;
            }
            // 물체 색(끔) 또는 그리지 않는 화면
            var pane = View.Panes[p];
            if (pane.FaceColors is not null) { pane.FaceColors = null; pane.HotspotMarks = null; }
            _paneLegends[p].IsVisible = false;
            _colorKeys[p] = "";
            if (p == 0) _liveResults.Clear();
        }
        SyncPaneHeaders();
        UpdateSection();
    }

    /// <summary>
    /// 모드별 물체 i 의 면 값 공급 — 새 색 모드는 여기 case 하나(+ ColorModeInfo.All 1행)를 더하면 분할 화면·메뉴·레전드에 붙는다.
    /// 값(면마다 double, NaN = 자료 없음) 또는 범주(states)를 채운다. 근사 응력 계열은 프레임당 한 번만 평가(_evalCache).
    /// </summary>
    private void FaceValues(ColorMode mode, int i, StressModel model, Frame f, int idx, bool peak,
        Dictionary<int, double[]> values, Dictionary<int, FailureState[]> states, List<(Vec3, float, float, float)> marks, Dictionary<int, StressResult>? live)
    {
        var s = f.Bodies[i];
        switch (ColorModeInfo.Of(mode).Source)
        {
            case ColorSource.OpenCourant:
                if (_feaValues.TryGetValue(i, out var fv) && fv.Length == model.FaceCount) values[i] = fv;
                return;
            case ColorSource.Shock:
                ShockFaceValues(mode, i, model, values, marks, live);
                return;
            case ColorSource.CcxShock:
                CcxShockFaceValues(i, model, values);
                return;
            case ColorSource.Calculix:
                if (_ccx.TryGetValue(i, out var cr))
                {
                    var v = mode switch
                    {
                        ColorMode.CcxVonMises => Scale(cr.FaceVonMises, 1e-6),
                        ColorMode.CcxSafety => cr.FaceSafety.ToArray(),
                        _ => Scale(cr.FaceDisplacement, 1e3),
                    };
                    if (v.Length == model.FaceCount) values[i] = v;
                }
                return;
            case ColorSource.Cfd:
                if (CfdFaceValues(i, model, s) is { } cv) values[i] = cv;
                return;
        }
        StressResult? r = null;
        if (!peak || s.Mode == BodyMode.Free)
        {
            if (_evalIdx != idx) { _evalCache.Clear(); _evalIdx = idx; }
            if (!_evalCache.TryGetValue(i, out r)) _evalCache[i] = r = model.Evaluate(model.InputFrom(_world!, f));
            if (live is not null) live[i] = r;
            if (s.Mode == BodyMode.Free)
                foreach (var (p, _, _) in r.Hotspots) marks.Add((s.Origin + s.Q.Rotate(p), 1f, 0.25f, 0.2f));
        }
        var h = peak ? _structural!.For(i) : null;
        switch (mode)
        {
            case ColorMode.Pressure: values[i] = Scale(h?.PeakPressure ?? r?.Pressure, 1e-3); break;
            case ColorMode.Stress: values[i] = Scale(h?.PeakVonMises ?? r?.VonMises, 1e-6); break;
            case ColorMode.Safety: values[i] = (h?.MinSafety ?? r?.Safety)?.ToArray() ?? []; break;
            case ColorMode.State: states[i] = h?.WorstState ?? r?.State ?? []; break;
        }
    }

    /// <summary>분할 화면 pane 의 면 색·레전드·핫스팟. 고정 범위(범위 자동 해제)는 0번 화면에만 적용, 나머지는 자동 범위.</summary>
    private void UpdatePaneColors(int pane)
    {
        var mode = PaneMode(pane);
        var info = ColorModeInfo.Of(mode);
        var legend = _paneLegends[pane];
        int idx = _world!.Recorder.IndexAt(_time);
        if (idx < 0) return;
        bool main = pane == 0;
        bool autoRange = !main || AutoRangeBox.IsChecked == true;
        // 충격·과도 모드는 시간 이력 자체를 보여 주므로 최대값 유지(peak-hold)를 쓰지 않는다
        bool timeHistory = info.Source is ColorSource.Shock or ColorSource.CcxShock;
        bool peak = PeakHoldBox.IsChecked == true && _structural is not null && !timeHistory;
        bool selOnly = SelectedOnlyBox.IsChecked == true;
        var key = $"{idx}|{mode}|{_colorMap}|{peak}|{selOnly}|{_selected}|{autoRange}|{(main ? RangeMinBox.Text + "|" + RangeMaxBox.Text : "")}|{_feaValues.Count}|{_ccxVersion}|{_structural?.Bodies.Count}|{View.Layers[ViewLayer.Legend]}"
            + (timeHistory ? $"|{_time.ToString("R", Ci)}|{_shock.Count}|{_shockPeaks.Count}|{_ccxShockVersion}" : "");
        if (key == _colorKeys[pane]) return;
        _colorKeys[pane] = key;
        var f = _world.Recorder.Frames[idx];
        var bodies = selOnly ? (_selected >= 0 ? [_selected] : []) : Enumerable.Range(0, _world.Bodies.Count).ToArray();
        if (info.Source == ColorSource.Shock) EnsureShock(bodies);
        var values = new Dictionary<int, double[]>();
        var states = new Dictionary<int, FailureState[]>();
        var marks = new List<(Vec3, float, float, float)>();
        if (main) _liveResults.Clear();
        foreach (int i in bodies)
        {
            var model = ModelFor(i);
            if (model is null) continue;
            // 실린 물체는 보통 건너뛰지만, 충격 가속도는 운반체에서 전달된 값을 보여 준다(내부 드론·센서)
            if (f.Bodies[i].Mode == BodyMode.Stowed && !peak && mode != ColorMode.ShockAccel) continue;
            FaceValues(mode, i, model, f, idx, peak, values, states, marks, main ? _liveResults : null);
        }
        View.Panes[pane].HotspotMarks = marks;
        var colors = new Dictionary<int, float[]>();
        bool categorical = info.Categorical;
        double vmin = 0, vmax = 1;
        bool noData = false;
        if (categorical)
        {
            foreach (var (i, st) in states)
            {
                var rgb = new float[st.Length * 3];
                for (int k = 0; k < st.Length; k++) { var (r, g, b) = ColorMaps.State(st[k]); rgb[3 * k] = r; rgb[3 * k + 1] = g; rgb[3 * k + 2] = b; }
                colors[i] = rgb;
            }
        }
        else
        {
            bool safety = info.Safety;
            double? fixedMin = null, fixedMax = null;
            if (!autoRange)
            {
                fixedMin = double.TryParse(RangeMinBox.Text, NumberStyles.Float, Ci, out var a) ? a : double.NaN;
                fixedMax = double.TryParse(RangeMaxBox.Text, NumberStyles.Float, Ci, out var bmx) ? bmx : double.NaN;
            }
            else if (info.Source == ColorSource.Shock && ShockRangeMax(mode, bodies) is { } smax)
            {
                // 충격 창 안: 범위를 창 최대로 고정 — 프레임마다 범위가 따라 움직이면 충격이 퍼지고 줄어드는 모습이 안 보인다
                fixedMin = 0; fixedMax = smax;
            }
            else if (info.Source == ColorSource.CcxShock && CcxShockRangeMax(bodies) is { } cmax)
            {
                fixedMin = 0; fixedMax = cmax;
            }
            // 안전계수: 0~(≥1.5, 5 에서 자름) · 응력/소성변형률: 0 부터 · 압력: 자료 범위(흡입 음압 포함)
            var range = ColorScale.Resolve(values.Values.SelectMany(v => v), zeroBased: info.ZeroBased,
                cap: safety ? 5 : double.PositiveInfinity, atLeastMax: safety ? 1.5 : double.NegativeInfinity, fixedMin, fixedMax);
            if (main)
            {
                if (range.Warning is { } rw && rw != _rangeWarn) Log(LogLevel.Warn, "색상 범위", rw);
                _rangeWarn = range.Warning;
            }
            vmin = range.Min; vmax = range.Max;
            if (main && AutoRangeBox.IsChecked == true) { RangeMinBox.Text = vmin.ToString("G4", Ci); RangeMaxBox.Text = vmax.ToString("G4", Ci); }
            foreach (var (i, v) in values)
            {
                var rgb = new float[v.Length * 3];
                for (int k = 0; k < v.Length; k++)
                {
                    if (double.IsNaN(v[k])) noData = true;
                    var (r, g, b) = ColorScale.ValueColor(_colorMap, v[k], range, safety);
                    rgb[3 * k] = r; rgb[3 * k + 1] = g; rgb[3 * k + 2] = b;
                }
                colors[i] = rgb;
            }
        }
        View.Panes[pane].FaceColors = colors;
        View.Panes[pane].FaceColorVersion = ++_colorVersion;
        // 레전드
        legend.Categorical = categorical;
        legend.ColorMap = _colorMap;
        legend.Reverse = info.Safety;
        legend.Min = vmin; legend.Max = vmax;
        legend.Title = info.Title;
        legend.Unit = mode == ColorMode.CcxDisplacement ? CcxModeUnit() : info.Unit;
        if (peak && info.Source == ColorSource.Approx) legend.Title += " · 최대값 유지";
        legend.Title += selOnly ? " (선택)" : " (전체)";
        legend.Note = info.Source switch
        {
            ColorSource.OpenCourant => "OpenCourant(Radioss) 셸 해석",
            ColorSource.Calculix => CcxNote(),
            ColorSource.Shock => ShockNote(bodies),
            ColorSource.CcxShock => CcxShockNote(),
            ColorSource.Cfd => CfdNote(),
            _ => "근사(FEA 아님)",
        };
        legend.NoData = noData && !categorical;
        legend.Detail = mode is ColorMode.Stress or ColorMode.Safety or ColorMode.State ? DominantDetail(peak, bodies)
            : info.Source == ColorSource.Shock ? ShockDetail(mode, bodies) : "";
        legend.IsVisible = View.Layers[ViewLayer.Legend];
        legend.Changed();
    }

    private string? _rangeWarn;

    /// <summary>레전드 보조 줄: 표시 중인 물체 가운데 최대 응력 지점의 지배 성분과 판 처짐 지표(FX-09).</summary>
    private string DominantDetail(bool peak, int[] bodies)
    {
        double best = -1; StressComponent comp = StressComponent.None; double wt = 0;
        foreach (int i in bodies)
        {
            if (peak && _structural?.For(i) is { } h)
            {
                if (h.PeakVm > best) { best = h.PeakVm; comp = h.DominantAtPeak; }
                wt = Math.Max(wt, h.MaxPanelDeflectionRatio);
            }
            else if (_evalCache.TryGetValue(i, out var r))
            {
                if (r.MaxVonMises > best) { best = r.MaxVonMises; comp = r.DominantAtMax; }
                wt = Math.Max(wt, r.MaxPanelDeflectionRatio);
            }
        }
        if (best <= 0) return "";
        return $"최대 {best / 1e6:G3} MPa · {StressModel.ComponentLabel(comp)}" + (wt > 0.5 ? $"\n판 처짐 w/t {wt:F1} (선형 = 보수적)" : "");
    }

    private static double[] Scale(double[]? v, double k)
    {
        if (v is null) return [];
        var r = new double[v.Length];
        for (int i = 0; i < v.Length; i++) r[i] = v[i] * k;
        return r;
    }

    private void SyncColorMenu()
    {
        foreach (var (mode, mi) in _colorItems) mi.IsChecked = mode == _colorMode;
    }

    private readonly List<(ColorMode, MenuItem)> _colorItems = new();

    /// <summary>보기 메뉴의 '색상 표시' 하위 메뉴(라디오처럼 동작).</summary>
    private MenuItem BuildColorMenu()
    {
        var root = new MenuItem { Header = "색상 표시 (응력 · 압력 · 재질)" };
        foreach (var (mode, label) in ColorModes)
        {
            var mi = new MenuItem { Header = label, ToggleType = MenuItemToggleType.Radio, IsChecked = mode == _colorMode };
            mi.Click += (_, _) => { SetColorMode(mode); Dispatcher.UIThread.Post(SyncColorMenu); };
            _colorItems.Add((mode, mi));
            root.Items.Add(mi);
        }
        return root;
    }

    // ================= 결과 저장 · 불러오기 =================

    private async Task SaveResults()
    {
        if (_world is null || (_runDoc ?? _doc) is not { } doc) { Log(LogLevel.Warn, "저장", "저장할 결과가 없습니다 — 먼저 실행(F5)하거나 결과를 불러오세요."); return; }
        var f = await PickSaveAsync(new FilePickerSaveOptions
        {
            Title = "결과 저장 (.mnsr — 이 프로그램에서 다시 불러오기)",
            SuggestedFileName = $"{doc.Name}_{DateTime.Now:yyyyMMdd_HHmmss}{ResultArchive.Extension}",
            DefaultExtension = "mnsr",
            FileTypeChoices = [new FilePickerFileType("MnS 결과") { Patterns = ["*.mnsr"] }],
        });
        if (f?.TryGetLocalPath() is not { } path) return;
        await SaveResultTo(path);
    }

    private async Task<bool> SaveResultTo(string path)
    {
        if (_world is null || (_runDoc ?? _doc) is not { } doc) return false;
        var job = BeginJob("결과 저장", cancellable: false);
        if (job is null) return false;
        Stage(job, $"{Path.GetFileName(path)} — 프레임 {_world.Recorder.Frames.Count:N0} · 메시 · 요약 · 구조 · MC", null);
        try
        {
            var w = _world; var summary = _summary;
            var structural = _structural?.ToJson() ?? summary?["structural"]?.DeepClone().AsObject();
            var mc = View.Mc?.ToJson();
            var fea = FeaArchive();
            await Task.Run(() => ResultArchive.Save(path, doc, w, summary, structural, mc, fea: fea));
            _resultPath = path;
            UpdateTitle();
            EndJob(job, LogLevel.Info, $"저장: {path} ({new FileInfo(path).Length / 1024.0 / 1024.0:F1} MB) — 파일 → 결과 불러오기(Ctrl+L)로 다시 볼 수 있음");
            return true;
        }
        catch (Exception ex) { _logWriter?.WriteLine(ex.ToString()); EndJob(job, LogLevel.Error, $"저장 실패: {ex.Message}"); return false; }
    }

    private async Task PickAndLoadResult()
    {
        var files = await PickOpenAsync(new FilePickerOpenOptions
        {
            Title = "결과 불러오기 (.mnsr)", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("MnS 결과") { Patterns = ["*.mnsr"] }],
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } p) return;
        await LoadResultFromPath(p);
    }

    /// <summary>.mnsr 불러오기: 계산 없이 프레임·이벤트·요약·MC·구조 결과 복원 → 같은 화면 경로로 표시.</summary>
    public async Task<bool> LoadResultFromPath(string path)
    {
        var job = BeginJob("결과 불러오기", cancellable: false);
        if (job is null) return false;
        Stage(job, $"{Path.GetFileName(path)} 압축 해제 · 시나리오 재구성 · 프레임 복원", null);
        try
        {
            var (r, scene, tree) = await Task.Run(() =>
            {
                var res = ResultArchive.Load(path);
                return (res, SceneData.Build(res.World), AssemblyTree.Build(res.World));
            });
            _doc = r.Doc;
            ShowDoc();
            ScenarioBox.SelectedIndex = -1;
            SetResult(r.Doc, r.World, tree, scene, r.Summary);
            if (r.MonteCarlo is { } mc) ShowMc(McOverlay.FromJson(mc));
            int feaN = RestoreFea(r.Fea);
            _resultPath = r.Path;
            UpdateTitle();
            LogEngine(r.World);
            string extra = (r.Structural is not null ? " · 구조 분석 포함(최대값 유지 색은 F6 로 다시 계산)" : "") + (r.MonteCarlo is not null ? " · MC 포함" : "")
                + (feaN > 0 ? $" · FEA 결과 {feaN}개(색상 'FEA …' 모드)" : "");
            EndJob(job, LogLevel.Info, $"불러옴: {Path.GetFileName(path)} — {r.Manifest["scenario"]} · 프레임 {r.World.Recorder.Frames.Count:N0} · 종료 {r.World.T:F1} s · 저장 {r.Manifest["savedAt"]}{extra}");
            return true;
        }
        catch (Exception ex) { _logWriter?.WriteLine(ex.ToString()); EndJob(job, LogLevel.Error, $"불러오기 실패: {ex.Message}"); return false; }
    }

    private void UpdateTitle()
    {
        var name = _resultPath is not null ? Path.GetFileName(_resultPath) : _doc?.Name;
        Title = $"MnS 공중투하 M&S 시뮬레이터{(name is null ? "" : $" — {name}")}";
    }

    // ================= 구조 분석 · 하중 케이스 · FEA =================

    /// <summary>전 구간 최대값 유지(peak-hold) · 최초 항복/파단/좌굴 이벤트(FR-STR-04, FR-MAT-02).</summary>
    private async Task RunStructural()
    {
        if (_world is null || View.Scene is null) { Log(LogLevel.Warn, "구조", "결과가 없습니다 — 먼저 실행하세요."); return; }
        var job = BeginJob("구조 분석 (근사)", cancellable: true);
        if (job is null) return;
        var w = _world;
        var models = Enumerable.Range(0, w.Bodies.Count).Select(ModelFor).Where(m => m is not null).Select(m => m!).ToList();
        var ct = job.Cts.Token;
        try
        {
            int done = 0;
            var rep = await Task.Run(() =>
            {
                var r = new StructuralReport();
                foreach (var m in models)
                {
                    ct.ThrowIfCancellationRequested();
                    int k = Interlocked.Increment(ref done);
                    Dispatcher.UIThread.Post(() => Stage(job, $"{k}/{models.Count} {m.Body.Name} — 면 {m.FaceCount:N0} · 프레임 추출", (k - 1.0) / models.Count));
                    r.Bodies.Add(StructuralAnalysis.Analyze(w, m, 800, ct));
                }
                return r;
            }, ct);
            _structural = rep;
            w.EventLog.RemoveAll(e => e.Name.EndsWith(".yield", StringComparison.Ordinal) || e.Name.EndsWith(".fracture", StringComparison.Ordinal) || e.Name.EndsWith(".buckling", StringComparison.Ordinal) || e.Name.EndsWith(".impactDamage", StringComparison.Ordinal));
            var judged = new StructuralReport();
            judged.Bodies.AddRange(rep.Bodies.Where(h => h.Body.Structure?.Material is not null)); // 재질 있는 물체만 항복·파단 이벤트
            StructuralAnalysis.AppendEvents(w, judged);
            if (_summary is not null) { _summary["structural"] = rep.ToJson(); SummaryText.Text = Results.SummaryText(_summary); BuildSummaryPanel(_summary); }
            EventsList.ItemsSource = w.EventLog.Select(e => new EventItem(e.Time, e.Name, e.Message)).ToList();
            foreach (var h in rep.Bodies)
                Log(h.Worst >= FailureState.Buckling ? LogLevel.Warn : LogLevel.Info, "구조",
                    string.Format(Ci, "{0}: σvm 최대 {1:F2} MPa @ {2:F2}s ({7}), 압력 최대 {3:F1} kPa, 최소 SF {4}, 상태 {5}{6}", h.Body.Name, h.PeakVm / 1e6, h.PeakVmTime,
                        h.MaxPressure / 1e3, double.IsFinite(h.MinSafetyValue) ? h.MinSafetyValue.ToString("F2", Ci) : "—", ColorMaps.StateLabel(h.Worst),
                        double.IsFinite(h.FirstFractureTime) ? $" · 최초 파단 {h.FirstFractureTime:F2}s" : double.IsFinite(h.FirstYieldTime) ? $" · 최초 항복 {h.FirstYieldTime:F2}s" : "",
                        StressModel.ComponentLabel(h.DominantAtPeak) + (h.MaxPanelDeflectionRatio > 0.5 ? string.Format(Ci, ", 판 w/t {0:F1} — 선형 판 응력은 보수적", h.MaxPanelDeflectionRatio) : "")));
            foreach (var h in rep.Bodies.Where(h => h.Impact is not null))
                Log(h.Impact!.State >= FailureState.Yield ? LogLevel.Warn : LogLevel.Info, "착지 재질",
                    string.Format(Ci, "{0} ({1}) {2:F2} m/s {3}: {4} — {5}", h.Body.Name, h.Impact.Material.Name, h.Impact.NormalSpeed, h.Impact.Contact, h.Impact.Verdict, h.Impact.Method));
            if (_colorMode == ColorMode.Off) SetColorMode(ColorMode.Stress);
            RecolorNow();
            EndJob(job, LogLevel.Info, $"완료: 물체 {rep.Bodies.Count}개 (근사, FEA 아님) — 색상 '최대값 유지' 사용 가능");
        }
        catch (OperationCanceledException) { EndJob(job, LogLevel.Warn, "취소됨"); }
        catch (Exception ex) { _logWriter?.WriteLine(ex.ToString()); EndJob(job, LogLevel.Error, $"실패: {ex.Message}"); }
    }

    private async Task ExportLoadCases()
    {
        if (_world is null) { Log(LogLevel.Warn, "하중 케이스", "결과가 없습니다."); return; }
        var folders = await PickFolderAsync(new FolderPickerOpenOptions { Title = "FEA 하중 케이스 폴더", AllowMultiple = false });
        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } dir) return;
        var job = BeginJob("하중 케이스 내보내기", cancellable: false);
        if (job is null) return;
        try
        {
            var w = _world;
            var outDir = Path.Combine(dir, $"loadcases_{DateTime.Now:yyyyMMdd_HHmmss}");
            var j = await Task.Run(() => LoadCaseExport.Export(w, outDir));
            EndJob(job, LogLevel.Info, $"내보냄: {outDir} — 케이스 {j["cases"]!.AsArray().Count}개 (loadcases.json + 면별 CSV)");
        }
        catch (Exception ex) { EndJob(job, LogLevel.Error, $"실패: {ex.Message}"); }
    }

    /// <summary>선택 물체 착지 충돌을 OpenCourant 셸 해석(FR-MAT-05) → 소성변형률을 면 색으로.</summary>
    private async Task RunFea()
    {
        if (_world is null || View.Scene is null || _selected < 0) { Log(LogLevel.Warn, "FEA", "물체를 선택하세요."); return; }
        var b = _world.Bodies[_selected];
        if (View.Scene.BodyMeshes[_selected] is not { } mesh) { Log(LogLevel.Warn, "FEA", $"{b.Name}: 형상이 없습니다."); return; }
        FeaImpactCase.Setup setup;
        try { setup = FeaImpactCase.FromLanding(_world, b, mesh); }
        catch (Exception ex) { Log(LogLevel.Error, "FEA", ex.Message); ShowLog(); return; }
        var dir = Path.Combine(_repo, "results", "fea", $"{b.Name}_{DateTime.Now:yyyyMMdd_HHmmss}");
        var deck = RadiossDeck.WriteShellImpact(dir, "impact", setup.MeshFe, setup.Material, setup.Thickness, setup.Velocity, setup.WallPoint, setup.WallNormal, setup.TEnd, totalMass: setup.Mass);
        Log(LogLevel.Info, "FEA", $"덱 작성: {deck.StarterPath} — 절점 {deck.Nodes:N0}, 셸 {deck.Elements:N0}, {setup.Note}");
        var inst = OpenCourantInstall.TryLocate(null, out var why);
        if (inst is null) { Log(LogLevel.Error, "FEA", $"{why} — 덱만 작성했습니다(설치 후 다시 실행)."); ShowLog(); return; }
        var job = BeginJob($"OpenCourant 충돌 해석 · {b.Name}", cancellable: true);
        if (job is null) return;
        Stage(job, "starter → engine → anim_to_vtk (외부 프로세스)", null);
        try
        {
            var ct = job.Cts.Token;
            var res = await Task.Run(() => OpenCourantRunner.Run(inst, deck, TimeSpan.FromMinutes(30), ct), ct);
            if (!res.Ok) { EndJob(job, LogLevel.Error, $"실패({res.Stage}): {res.Error} — 로그 {Path.Combine(dir, "mns_fea_console.log")}"); return; }
            var out_ = FeaOutcome.From(res.Final!, deck, setup.Material);
            if (out_.FacePlasticStrain is null) { EndJob(job, LogLevel.Warn, $"완료했지만 소성변형률 결과가 없습니다: {res.VtkFiles[^1]}"); return; }
            _feaValues[_selected] = out_.FacePlasticStrain;
            SetColorMode(ColorMode.FeaPlastic);
            EndJob(job, out_.State >= FailureState.Yield ? LogLevel.Warn : LogLevel.Info, $"완료 {res.Seconds:F1} s — {out_.Verdict}");
        }
        catch (OperationCanceledException) { EndJob(job, LogLevel.Warn, "취소됨"); }
        catch (Exception ex) { _logWriter?.WriteLine(ex.ToString()); EndJob(job, LogLevel.Error, $"실패: {ex.Message}"); }
    }

    private async Task ShowMaterials() => await MaterialsWindow().ShowDialog(this);

    private Window MaterialsWindow()
    {
        var sb = new StringBuilder();
        sb.AppendLine("재질 라이브러리 (Sim.Core/Structures/materials.json — 대표값, 시험값으로 structure.override 덮어쓰기)\n");
        foreach (var m in MaterialLibrary.All)
        {
            sb.AppendLine(string.Format(Ci, "■ {0}  [{1}]  {2}", m.Name, m.Key, m.Brittle ? "취성(최대 주응력 ≥ 인장강도 → 파단)" : "연성(von Mises ≥ 항복 → 영구 변형, ≥ 인장 → 파단)"));
            sb.AppendLine(string.Format(Ci, "   ρ {0:F0} kg/m³ · E {1:F1} GPa · ν {2:F2} · σy {3:F1} MPa · σu {4:F1} MPa · 연신율 {5:P0} · 속도 효과 {6}",
                m.Density, m.E / 1e9, m.Nu, m.Yield / 1e6, m.Ultimate / 1e6, m.Elongation, m.RateModel));
            sb.AppendLine($"   Fusion 이름: {string.Join(", ", m.Aliases)}");
            sb.AppendLine($"   출처: {m.Source}\n");
        }
        return DialogWindow("재질 라이브러리", Sim.App.Help.HelpKeys.Materials, new ScrollViewer
        {
            Content = new Border
            {
                Classes = { "card" },
                Child = new SelectableTextBlock { Text = sb.ToString(), Classes = { "mono" }, FontSize = 12.5, TextWrapping = TextWrapping.Wrap },
            },
        }, 920, 720);
    }
}
