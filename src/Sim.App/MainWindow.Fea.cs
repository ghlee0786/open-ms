using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Sim.App.Rendering;
using Sim.Core.Fea;
using Sim.Core.Models;
using Sim.Core.Numerics;
using Sim.Core.Structures;

namespace Sim.App;

/// <summary>CalculiX 정적·진동·좌굴 FEA(PRD Z, FR-FEA-05): 해석 메뉴 → 옵션 창 → 백그라운드 실행(취소) → 면 색 · 변형 표시 · 결과 저장.</summary>
public partial class MainWindow
{
    private readonly Dictionary<int, FeaRecord> _ccx = new();
    private int _ccxVersion;
    /// <summary>변형 표시 배율: 0 = 끔, &lt; 0 = 자동(최대 변위가 물체 크기의 5%).</summary>
    private double _deformScale = -1;
    private readonly List<(double Scale, MenuItem Item)> _deformItems = new();

    /// <summary>FEA 실행 옵션(대화 상자 또는 스모크 인자).</summary>
    public sealed record CcxOptions(CcxAnalysis Analysis, int CaseIndex, double? G, Vec3 Dir, double? Size, string Mesher, bool Nonlinear, double Patch = 0);

    private void WireCcx()
    {
        MiCcx.Click += async (_, _) => await RunCcx();
        MiCcxCheck.Click += (_, _) =>
        {
            var inst = CalculixInstall.TryLocate(null, out var why);
            var gm = GmshMesher.FindScript();
            Log(inst is null ? LogLevel.Warn : LogLevel.Info, "CalculiX",
                (inst is null ? why : $"설치 확인: {inst.Ccx}") + $" · Gmsh 도구 {(gm is null ? "없음(복셀 대체 사용)" : gm)} · python = {System.Environment.GetEnvironmentVariable("MNS_PYTHON") ?? "python(PATH)"}");
            ShowLog();
        };
        foreach (var (s, label) in new[] { (0.0, "끔 (원래 형상)"), (-1.0, "자동 (최대 변위 = 크기의 5%)"), (1.0, "×1 (실제)"), (10.0, "×10"), (100.0, "×100"), (1000.0, "×1000") })
        {
            var mi = new MenuItem { Header = label, ToggleType = MenuItemToggleType.Radio, IsChecked = s == _deformScale };
            mi.Click += (_, _) => { _deformScale = s; foreach (var (k, m) in _deformItems) m.IsChecked = k == s; UpdateDeform(); };
            _deformItems.Add((s, mi));
            MiCcxDeform.Items.Add(mi);
        }
    }

    private string CcxModeUnit()
    {
        bool modal = _ccx.Values.Any(r => r.Summary["analysis"]?.GetValue<string>() is "Modal" or "Buckle");
        return modal ? "mm (모드 형상 — 정규화, 크기 무의미)" : "mm (CalculiX)";
    }

    private string CcxNote()
    {
        var r = _selected >= 0 && _ccx.TryGetValue(_selected, out var s) ? s : _ccx.Values.FirstOrDefault();
        if (r is null) return "CalculiX — 결과 없음 (F7)";
        return $"CalculiX {r.Summary["analysis"]} · {r.LoadCase}";
    }

    /// <summary>결과 파일 fea.json: CalculiX 기록 + OpenCourant 소성변형률.</summary>
    private JsonObject? FeaArchive()
    {
        if (_world is null || (_ccx.Count == 0 && _feaValues.Count == 0)) return null;
        var recs = _ccx.Values.ToList();
        foreach (var (i, v) in _feaValues)
            recs.Add(new FeaRecord { Body = _world.Bodies[i].Name, Solver = "OpenCourant", LoadCase = "착지 충돌", FacePlasticStrain = v });
        return FeaRecord.Pack(recs);
    }

    private int RestoreFea(JsonObject? fea)
    {
        if (fea is null || _world is null) return 0;
        int n = 0;
        try
        {
            foreach (var r in FeaRecord.Unpack(fea))
            {
                if (!_world.BodyMap.TryGetValue(r.Body, out var b)) continue;
                if (r.Solver == "OpenCourant" && r.FacePlasticStrain.Length > 0) _feaValues[b.Index] = r.FacePlasticStrain;
                else if (r.Solver == "CalculiX") _ccx[b.Index] = r;
                n++;
            }
        }
        catch (InvalidDataException ex) { Log(LogLevel.Warn, "불러오기", $"fea.json 무시: {ex.Message}"); }
        _ccxVersion++;
        UpdateDeform();
        return n;
    }

    /// <summary>변형 형상(정점 변위 × 배율)을 GL 뷰에 넘긴다. 정적 = 실제 변위, 진동·좌굴 = 정규화 모드 형상.</summary>
    private void UpdateDeform()
    {
        var map = new Dictionary<int, float[]>();
        if (_deformScale != 0 && View.Scene?.BodyMeshes is { } bm)
            foreach (var (i, r) in _ccx)
            {
                if (i >= bm.Length || bm[i] is not { } mesh || r.VertexDisplacement.Length != mesh.Vertices.Count) continue;
                double umax = r.VertexDisplacement.Max(v => v.Norm);
                if (!(umax > 0)) continue;
                var (mn, mx) = mesh.Bounds();
                double k = _deformScale > 0 ? _deformScale : 0.05 * (mx - mn).Norm / umax;
                var d = mesh.Transformed(v => v);
                for (int j = 0; j < d.Vertices.Count; j++) d.Vertices[j] = mesh.Vertices[j] + r.VertexDisplacement[j] * k;
                map[i] = SceneData.Flatten(d);
            }
        View.MeshDeform = map.Count > 0 ? map : null;
        View.MeshDeformVersion++;
        View.RequestNextFrameRendering();
    }

    /// <summary>선택 물체 CalculiX FEA. options 가 없으면 옵션 창을 띄운다.</summary>
    private async Task RunCcx(CcxOptions? options = null)
    {
        if (_world is null || View.Scene is null || _selected < 0) { Log(LogLevel.Warn, "FEA", "물체를 선택하세요(트리 또는 3D 클릭)."); return; }
        var w = _world;
        var b = w.Bodies[_selected];
        int bi = _selected;
        if (View.Scene.BodyMeshes[bi] is not { } display || b.Mode == BodyMode.Kinematic) { Log(LogLevel.Warn, "FEA", $"{b.Name}: 형상이 없거나 운반체입니다."); return; }
        if (b.Structure?.Material is null) { Log(LogLevel.Error, "FEA", $"{b.Name}: 재질(structure.material 또는 Fusion 재질)이 없어 FEA 를 할 수 없습니다."); ShowLog(); return; }
        var model = ModelFor(bi);
        if (model is null) { Log(LogLevel.Warn, "FEA", $"{b.Name}: 응력 모델을 만들 수 없습니다."); return; }
        var inst = CalculixInstall.TryLocate(null, out var why);
        if (inst is null) { Log(LogLevel.Error, "FEA", $"{why} — CCX_PATH 를 지정하세요."); ShowLog(); return; }
        var cands = LoadCaseExport.Instants(w, b, 16).Select(x => CcxBodyFea.FromWorld(w, model, x.Frame, x.Why)).ToList();
        options ??= await CcxDialog(b, cands, model);
        if (options is null) return;
        FeaLoadInput? lc = null;
        if (options.Analysis != CcxAnalysis.Modal)
        {
            if (options.G is { } g) lc = CcxBodyFea.Synthetic(b.OwnMass, display, g, options.Dir, patch: options.Patch);
            else if (cands.Count > 0) lc = cands[Math.Clamp(options.CaseIndex < 0 ? cands.IndexOf(cands.MaxBy(c => c.Severity(model.Normal, model.Area))!) : options.CaseIndex, 0, cands.Count - 1)];
            else { Log(LogLevel.Warn, "FEA", $"{b.Name}: 자유 비행 중 하중 순간이 없습니다 — 대표 G 하중을 고르세요."); return; }
        }
        var dir = Path.Combine(_repo, "results", "ccx", $"{b.Name}_{DateTime.Now:yyyyMMdd_HHmmss}");
        var job = BeginJob($"CalculiX {options.Analysis} · {b.Name}", cancellable: true);
        if (job is null) return;
        var ct = job.Cts.Token;
        try
        {
            Stage(job, "메시 (셸 = 표시 메시 / 솔리드 = Gmsh, 없으면 복셀)", null);
            var (fm, run, res) = await Task.Run(() =>
            {
                var m = CcxBodyFea.BuildModel(b, display, dir, options.Size, options.Mesher);
                ct.ThrowIfCancellationRequested();
                Dispatcher.UIThread.Post(() => Stage(job, $"ccx 실행 — {m.Mesh.Source}, 요소 {m.Mesh.Elements.Count:N0} (외부 프로세스)", null));
                var (r, s) = CcxBodyFea.Solve(inst, m, options.Analysis, lc, dir, options.Nonlinear, TimeSpan.FromMinutes(60), ct);
                return (m, r, s);
            }, ct);
            foreach (var n in fm.Notes) Log(LogLevel.Info, "FEA", n);
            if (!run.Ok || res is null) { EndJob(job, LogLevel.Error, $"실패: {run.Error} — 로그 {ShownPath(Path.Combine(dir, "fea.ccx.log"))}"); return; }
            _ccx[bi] = FeaRecord.FromCcx(b.Name, res, lc?.Name ?? "자유-자유", fm.Notes);
            _ccxVersion++;
            UpdateDeform();
            SetColorMode(options.Analysis == CcxAnalysis.Static ? ColorMode.CcxVonMises : ColorMode.CcxDisplacement);
            RecolorNow();
            string extra = res.Relief is { } ir ? string.Format(Ci, " · 관성 완화 {0:F2} G, 지지 반력 {1:E1} N", ir.AccelG, res.Reaction?.Norm ?? double.NaN) : "";
            if (options.Analysis == CcxAnalysis.Static)
                extra += string.Format(Ci, " · σvm 최대 {0:F2} MPa @ {1} · 변위 최대 {2:F3} mm", res.MaxVonMises / 1e6, res.MaxVonMisesAt, res.MaxDisplacement * 1e3);
            if (run.SolverNote.Length > 0) Log(LogLevel.Warn, "FEA", run.SolverNote);
            EndJob(job, res.MinSafety < 1 || res.BucklingFactors.Any(f => f is > 0 and < 1) ? LogLevel.Warn : LogLevel.Info,
                $"완료 {run.Seconds:F1} s — {res.Verdict}{extra} — {ShownPath(dir)}");
        }
        catch (OperationCanceledException) { EndJob(job, LogLevel.Warn, "취소됨"); }
        catch (Exception ex) { _logWriter?.WriteLine(ex.ToString()); EndJob(job, LogLevel.Error, $"실패: {ex.Message}"); }
    }

    /// <summary>스모크 --ccx-dialog: 옵션 창을 열어 캡처만 하고 닫는다(CalculiX 설치와 무관 — 실행하지 않음).</summary>
    private async Task<string> CcxDialogShot(Body b, string path)
    {
        if (_world is null || ModelFor(b.Index) is not { } model) return $"ui.ccxdialog body={b.Name} model=none";
        SelectBody(b.Index);
        var cands = LoadCaseExport.Instants(_world, b, 16).Select(x => CcxBodyFea.FromWorld(_world, model, x.Frame, x.Why)).ToList();
        var before = OwnedWindows.ToList();
        var dialog = CcxDialog(b, cands, model);
        await Task.Delay(900);
        if (OwnedWindows.Except(before).FirstOrDefault() is not { } win) return $"ui.ccxdialog body={b.Name} open=false";
        using (var rtb = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize((int)win.Bounds.Width, (int)win.Bounds.Height)))
        {
            rtb.Render(win);
            rtb.Save(path);
        }
        string s = string.Create(Ci, $"ui.ccxdialog body={b.Name} cases={cands.Count} size={win.Bounds.Width:F0}x{win.Bounds.Height:F0}");
        win.Close();
        await dialog;
        return s;
    }

    /// <summary>옵션 창: 해석 종류 · 하중 순간(목록 / 대표 G) · 요소 크기 · 메셔 · 비선형.</summary>
    private async Task<CcxOptions?> CcxDialog(Body b, List<FeaLoadInput> cands, StressModel model)
    {
        var analysis = new ComboBox { ItemsSource = new[] { "정적 (응력·안전계수)", "고유진동 (자유-자유 모드)", "좌굴 (하중 계수)" }, SelectedIndex = 0, Width = 420 };
        var caseItems = cands.Select(c => string.Format(Ci, "t={0:F3}s  {1}  · 외력 지표 {2:F0} N · 기록 {3:F1} G", c.Time, c.Name, c.Severity(model.Normal, model.Area), c.RecordedAccel.Norm / 9.80665)).ToList();
        caseItems.Add("대표 G 하중 (아래 G · 방향)");
        int best = cands.Count > 0 ? cands.IndexOf(cands.MaxBy(c => c.Severity(model.Normal, model.Area))!) : caseItems.Count - 1;
        var caseBox = new ComboBox { ItemsSource = caseItems, SelectedIndex = best, Width = 620 };
        var gBox = new TextBox { Text = "20", Width = 80 };
        var dirBox = new ComboBox { ItemsSource = new[] { "-x", "+x", "-y", "+y", "-z", "+z" }, SelectedIndex = 1, Width = 80 };
        var patchBox = new TextBox { Text = "0", Width = 80 };
        var sizeBox = new TextBox { Text = "", Watermark = "자동", Width = 80 };
        var mesher = new ComboBox { ItemsSource = new[] { "auto", "gmsh", "voxel" }, SelectedIndex = 0, Width = 100 };
        var nonlin = new CheckBox { Content = "비선형 (대변형 NLGEOM + 이선형 소성)" };
        bool shell = b.Structure?.Section == "thinWall";
        var ok = new Button { Content = "실행", IsDefault = true, Width = 90, Classes = { "primary" }, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = "취소", IsCancel = true, Width = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        StackPanel Row(string label, params Control[] cs)
        {
            var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            p.Children.Add(new TextBlock { Text = label, Width = 150, VerticalAlignment = VerticalAlignment.Center, Classes = { "dim" } });
            foreach (var c in cs) p.Children.Add(c);
            return p;
        }
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = $"{b.Name} — {b.Structure?.Material?.Name}, {(shell ? $"얇은 벽 S6 셸 (t {b.Structure!.Thickness * 1000:G3} mm, 표시 면 1:1)" : "솔리드 (Gmsh C3D10 → 없으면 복셀 C3D20)")}, 질량 {b.OwnMass:G4} kg",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });
        panel.Children.Add(Row("해석", analysis));
        panel.Children.Add(Row("하중 순간", caseBox));
        panel.Children.Add(Row("대표 G · 방향 · 반경[m]", gBox, dirBox, patchBox, new TextBlock { Text = "(동체 축, 반경 0 = 자동)", VerticalAlignment = VerticalAlignment.Center }));
        panel.Children.Add(Row("요소 크기 [m] · 메셔", sizeBox, mesher));
        panel.Children.Add(nonlin);
        panel.Children.Add(new TextBlock
        {
            Text = "경계조건: 관성 완화(외력 → 강체 가속 → d'Alembert 하중) + 3-2-1 정정 지지 — 지지 반력 ≈ 0 이 정상. 결과: results/ccx/<물체>_<시각>/",
            Classes = { "caption" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Avalonia.Thickness(0, 6, 0, 0) };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        var win = DialogWindow("정적/진동/좌굴 FEA — CalculiX", Sim.App.Help.HelpKeys.CcxOptions, panel, 900);
        CcxOptions? result = null;
        ok.Click += (_, _) =>
        {
            try
            {
                var an = analysis.SelectedIndex switch { 1 => CcxAnalysis.Modal, 2 => CcxAnalysis.Buckle, _ => CcxAnalysis.Static };
                bool synthetic = caseBox.SelectedIndex == caseItems.Count - 1;
                double? g = synthetic ? double.Parse(gBox.Text ?? "", NumberStyles.Float, Ci) : null;
                var d = (string?)dirBox.SelectedItem switch { "-x" => -Vec3.UnitX, "-y" => new Vec3(0, -1, 0), "+y" => new Vec3(0, 1, 0), "-z" => -Vec3.UnitZ, "+z" => Vec3.UnitZ, _ => Vec3.UnitX };
                double? size = string.IsNullOrWhiteSpace(sizeBox.Text) ? null : double.Parse(sizeBox.Text, NumberStyles.Float, Ci);
                result = new CcxOptions(an, synthetic ? -1 : caseBox.SelectedIndex, g, d, size, (string?)mesher.SelectedItem ?? "auto", nonlin.IsChecked == true,
                    double.Parse(string.IsNullOrWhiteSpace(patchBox.Text) ? "0" : patchBox.Text, NumberStyles.Float, Ci));
                win.Close();
            }
            catch (FormatException) { Log(LogLevel.Warn, "FEA", "숫자 입력을 확인하세요(G · 반경 · 요소 크기)."); }
        };
        cancel.Click += (_, _) => win.Close();
        await win.ShowDialog(this);
        return result;
    }
}
