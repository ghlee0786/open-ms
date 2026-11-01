using System.Globalization;
using Avalonia.Threading;
using Sim.App.Rendering;
using Sim.Core.Engine;
using Sim.Core.Fea;
using Sim.Core.Models;
using Sim.Core.Numerics;
using Sim.Core.Structures;

namespace Sim.App;

/// <summary>
/// 개방 충격 · 스냅 하중 충격 응답 색(충격 가속도 G · 충격 응력 MPa): 물체별 보 동해석 이력(ShockResponse, 근사 C)을 배경에서 한 번 계산하고,
/// 재생 시각(프레임 사이 보간)마다 StressModel.Evaluate(…, sliceAccel) 로 면 값을 낸다. 실린(Stowed) 드론·센서는 운반체 보 가속도를
/// 그 위치에서 받아 충격 가속도만 표시(마운트 동특성 없음). 충격 창 안에서는 레전드 범위를 그 창 최대로 고정해 퍼지고 줄어드는 모습이 보이게.
/// </summary>
public partial class MainWindow
{
    private readonly Dictionary<int, ShockHistory> _shock = new();
    /// <summary>(물체, 창) → 창 안 면 최대 (가속도 G, 응력 MPa).</summary>
    private readonly Dictionary<(int Body, int Window), (double G, double MPa)> _shockPeaks = new();
    private readonly HashSet<int> _shockFailed = new();
    private Task? _shockTask;
    private int _shockGen;
    private Frame? _tFrame;
    private double _tFrameT = double.NaN;
    private readonly Dictionary<int, StressResult> _shockEval = new();
    private double _shockEvalT = double.NaN;

    private static bool IsShockMode(ColorMode m) => ColorModeInfo.Of(m).Source == ColorSource.Shock;

    private void ResetShock()
    {
        _shockGen++;
        _shock.Clear(); _shockPeaks.Clear(); _shockFailed.Clear(); _shockEval.Clear();
        _shockEvalT = double.NaN; _tFrame = null; _tFrameT = double.NaN;
    }

    /// <summary>재생 시각의 보간 프레임(충격 창은 ms 단위라 기록 프레임 사이도 이어서).</summary>
    private Frame? TimeFrame()
    {
        if (_world is null) return null;
        if (_tFrame is null || _tFrameT != _time) { _tFrame = _world.Recorder.Interpolate(_time); _tFrameT = _time; }
        return _tFrame;
    }

    /// <summary>프레임 f 에서 물체 i 를 싣고 있는 루트(자기 자신이면 i).</summary>
    private int CarrierOf(int i, Frame f)
    {
        var b = _world!.Bodies[i];
        while (f.Bodies[b.Index].Mode == BodyMode.Stowed && (b.Parent ?? b.FormerParent) is { } p) b = p;
        return b.Index;
    }

    /// <summary>물체 i 의 충격 이력이 아직 없고 계산할 수 있는가(형상·질량 있고 운반체 아님).</summary>
    private bool ShockPending(int i) =>
        !_shock.ContainsKey(i) && !_shockFailed.Contains(i) && _world is not null && _world.Bodies[i].Mode != BodyMode.Kinematic && ModelFor(i) is not null;

    /// <summary>표시할 물체들에 필요한 충격 이력(실린 물체는 운반체 것)을 배경에서 계산 — 없는 것만.</summary>
    private void EnsureShock(IEnumerable<int> bodies)
    {
        if (_world is null || View.Scene is null) return;
        var f = TimeFrame();
        if (f is null) return;
        var need = bodies.Select(i => CarrierOf(i, f)).Distinct().Where(i => ShockPending(i)).ToList();
        if (need.Count == 0 || _shockTask is { IsCompleted: false } || _job is not null) return;
        _shockTask = SolveShock(need);
    }

    private async Task SolveShock(List<int> need)
    {
        var job = BeginJob("충격 응답 (보 동해석, 근사 C)", cancellable: true);
        if (job is null) return;
        var w = _world!; int gen = _shockGen;
        var items = need.Select(i => (I: i, Model: ModelFor(i)!, Mesh: View.Scene!.BodyMeshes[i]!)).ToList();
        var ct = job.Cts.Token;
        try
        {
            int done = 0;
            var res = await Task.Run(() =>
            {
                var list = new List<(int I, ShockHistory? H, Dictionary<int, (double, double)> Peaks, string? Err)>();
                foreach (var (i, m, mesh) in items)
                {
                    ct.ThrowIfCancellationRequested();
                    int k = Interlocked.Increment(ref done);
                    Dispatcher.UIThread.Post(() => Stage(job, $"{k}/{items.Count} {m.Body.Name} — 충격 창 탐지 · Newmark 적분 · 창별 최대", (k - 1.0) / items.Count));
                    try
                    {
                        var h = ShockResponse.Solve(w, m, ct: ct);
                        // 창별 면 최대(별도 모델 — Evaluate 의 공유 버퍼가 UI 스레드와 겹치지 않게), 2 ms 간격 표본
                        var pm = StressModel.Build(m.Body, mesh)!;
                        var peaks = new Dictionary<int, (double, double)>();
                        var r = new StressResult();
                        for (int wi = 0; wi < h.Windows.Count; wi++)
                        {
                            double g = 0, s = 0;
                            var ts = h.Times[wi];
                            int stride = Math.Max(1, (int)Math.Round(0.002 / Math.Max(h.OutputDt, 1e-6)));
                            for (int j = 0; j < ts.Length; j += stride)
                            {
                                ct.ThrowIfCancellationRequested();
                                if (w.Recorder.Interpolate(ts[j]) is not { } fr) continue;
                                pm.Evaluate(pm.InputFrom(w, fr), r, h.SliceAccel[wi][j]);
                                g = Math.Max(g, r.MaxAccelG); s = Math.Max(s, r.MaxVonMises / 1e6);
                            }
                            peaks[wi] = (g, s);
                        }
                        list.Add((i, h, peaks, null));
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { list.Add((i, null, new(), ex.Message)); }
                }
                return list;
            }, ct);
            if (gen != _shockGen) { EndJob(job, LogLevel.Info, "결과가 바뀌어 버림"); return; }
            int nWin = 0;
            foreach (var (i, h, peaks, err) in res)
            {
                if (h is null) { _shockFailed.Add(i); Log(LogLevel.Warn, "충격 응답", $"{w.Bodies[i].Name}: {err}"); continue; }
                _shock[i] = h;
                foreach (var (wi, p) in peaks) _shockPeaks[(i, wi)] = p;
                nWin += h.Windows.Count;
                foreach (var (win, wi) in h.Windows.Select((x, k) => (x, k)))
                    Log(LogLevel.Info, "충격 응답", string.Format(CultureInfo.InvariantCulture,
                        "{0}: {1} {2:F3}–{3:F3} s — 점하중 최대 {4:F0} N, 면 가속도 최대 {5:F1} G, 충격 응력 최대 {6:F2} MPa",
                        w.Bodies[i].Name, win.Label, win.T0, win.T1, win.PeakLoadN, peaks[wi].Item1, peaks[wi].Item2));
                Log(LogLevel.Info, "충격 응답", $"{w.Bodies[i].Name}: {h.Method}");
            }
            EndJob(job, LogLevel.Info, $"완료: 물체 {res.Count(x => x.H is not null)}개 · 충격 창 {nWin}개 (근사 C — 1D 보 동해석, FEA 아님). 충격 창으로 이동하려면 이벤트 목록 또는 슬로모션(0.001×~0.02×)");
            RecolorNow();
        }
        catch (OperationCanceledException) { EndJob(job, LogLevel.Warn, "취소됨"); foreach (var i in need) _shockFailed.Add(i); }
        catch (Exception ex) { _logWriter?.WriteLine(ex.ToString()); EndJob(job, LogLevel.Error, $"실패: {ex.Message}"); foreach (var i in need) _shockFailed.Add(i); }
    }

    /// <summary>충격 모드 면 값: 자유 물체 = 동해석(창 안) 또는 준정적(창 밖) 평가, 실린 물체 = 운반체 가속도장(가속도만).</summary>
    private void ShockFaceValues(ColorMode mode, int i, StressModel model, Dictionary<int, double[]> values, List<(Vec3, float, float, float)> marks, Dictionary<int, StressResult>? live)
    {
        var f = TimeFrame();
        if (f is null) return;
        var s = f.Bodies[i];
        if (s.Mode == BodyMode.Free)
        {
            var r = ShockEval(i, model, f);
            if (live is not null) live[i] = r;
            values[i] = mode == ColorMode.ShockAccel ? r.AccelG.ToArray() : Scale(r.VonMises, 1e-6);
            foreach (var (p, _, _) in r.Hotspots) marks.Add((s.Origin + s.Q.Rotate(p), 1f, 0.25f, 0.2f));
            return;
        }
        if (s.Mode != BodyMode.Stowed || mode != ColorMode.ShockAccel) return;
        // 실린 물체: 운반체 c 의 보 가속도(창 안) 또는 관성 완화 강체장(창 밖)을 면 위치에서
        int ci = CarrierOf(i, f);
        if (ci == i || ModelFor(ci) is not { } cm) return;
        var sc = f.Bodies[ci];
        var rc = ShockEval(ci, cm, f);
        var acc = _shock.TryGetValue(ci, out var h) ? h.AccelAt(_time) : null;
        var v = new double[model.FaceCount];
        for (int k = 0; k < v.Length; k++)
        {
            var pw = s.Origin + s.Q.Rotate(model.Centroid[k]);
            var pc = sc.Q.InverseRotate(pw - sc.Origin);
            var rho = pc - rc.MassCenter;
            var a = acc is not null ? cm.InterpSlice(acc, cm.ToAxisFrame(pc).X) : rc.RigidAccel + Vec3.Cross(rc.RigidAlpha, rho);
            a += Vec3.Cross(sc.Omega, Vec3.Cross(sc.Omega, rho));
            v[k] = a.Norm / StressModel.G0;
        }
        values[i] = v;
    }

    /// <summary>물체 i 의 재생 시각 평가(같은 시각이면 캐시).</summary>
    private StressResult ShockEval(int i, StressModel model, Frame f)
    {
        if (_shockEvalT != _time) { _shockEval.Clear(); _shockEvalT = _time; }
        if (_shockEval.TryGetValue(i, out var r)) return r;
        var acc = _shock.TryGetValue(i, out var h) ? h.AccelAt(_time) : null;
        r = model.Evaluate(model.InputFrom(_world!, f), null, acc);
        _shockEval[i] = r;
        return r;
    }

    /// <summary>충격 창 안이면 표시 물체들의 창 최대(레전드 고정 범위), 아니면 null.</summary>
    private double? ShockRangeMax(ColorMode mode, IEnumerable<int> bodies)
    {
        var f = TimeFrame();
        if (f is null) return null;
        double? best = null;
        foreach (int i in bodies)
        {
            int ci = CarrierOf(i, f);
            if (!_shock.TryGetValue(ci, out var h)) continue;
            int wi = h.WindowIndexAt(_time);
            if (wi < 0 || !_shockPeaks.TryGetValue((ci, wi), out var p)) continue;
            double v = mode == ColorMode.ShockAccel ? p.G : p.MPa;
            if (v > 0) best = Math.Max(best ?? 0, v);
        }
        return best;
    }

    /// <summary>레전드 각주: 등급·방법·현재 창(+경과 ms) 또는 창 밖·계산 중.</summary>
    private string ShockNote(IEnumerable<int> bodies)
    {
        var f = TimeFrame();
        if (f is null) return "";
        var list = bodies.Select(i => CarrierOf(i, f)).Distinct().ToList();
        if (list.Any(ShockPending)) return "근사 C · 보 동해석 계산 중… (지금은 준정적)";
        foreach (int i in list)
            if (_shock.TryGetValue(i, out var h) && h.WindowAt(_time) is { } win)
                return string.Format(CultureInfo.InvariantCulture, "근사 C · 1D 보 동해석 — {0} {1:+0.0;-0.0} ms\n범위 = 이 창 최대(고정){2}", win.Label, (_time - win.TTrigger) * 1e3,
                    h.BeamLike ? "" : " · 짧고 굵은 물체: 보 모델 신뢰 낮음");
        return "근사 C · 충격 창 밖 = 준정적(관성 완화)";
    }

    /// <summary>레전드 보조 줄: 충격 응력 최대·지배 성분 또는 가속도 최대.</summary>
    private string ShockDetail(ColorMode mode, IEnumerable<int> bodies)
    {
        double best = -1; string who = ""; StressComponent comp = StressComponent.None;
        foreach (int i in bodies)
        {
            if (!_shockEval.TryGetValue(i, out var r)) continue;
            double v = mode == ColorMode.ShockAccel ? r.MaxAccelG : r.MaxVonMises / 1e6;
            if (v > best) { best = v; who = _world!.Bodies[i].Name; comp = r.DominantAtMax; }
        }
        if (best < 0) return "";
        return mode == ColorMode.ShockAccel ? $"최대 {best:G3} G · {who}" : $"최대 {best:G3} MPa · {who} · {StressModel.ComponentLabel(comp)}";
    }

    /// <summary>다음/이전 충격 창 시작(촉발 − 10 ms)으로 이동 — 표시 중인 물체(없으면 전체) 기준.</summary>
    private void SeekShock(int dir)
    {
        var starts = _shock.Values.SelectMany(h => h.Windows.Select(x => x.TTrigger - 0.01)).OrderBy(x => x).ToList();
        if (starts.Count == 0) { Log(LogLevel.Warn, "충격 응답", "충격 창이 없습니다 — 색상을 '충격 가속도/응력'으로 바꾸면 계산합니다."); return; }
        double? t = dir > 0 ? starts.Where(x => x > _time + 1e-4).Cast<double?>().FirstOrDefault() : starts.Where(x => x < _time - 1e-4).Cast<double?>().LastOrDefault();
        if (t is not { } tt) return;
        Seek(tt);
        if (_rate > 0.02) RateBox.SelectedIndex = 1; // 0.005× — 1 ms 를 0.2 s 에
    }

    private void WireShock()
    {
        MiShock.Click += (_, _) => { if (!IsShockMode(_colorMode)) SetColorMode(ColorMode.ShockAccel); else RecolorNow(); };
        MiCcxShock.Click += async (_, _) => await RunCcxShock();
        MiShockNext.Click += (_, _) => SeekShock(1);
        MiShockPrev.Click += (_, _) => SeekShock(-1);
        SectionBox.IsCheckedChanged += (_, _) => { UpdateSection(); View.RequestNextFrameRendering(); };
    }

    /// <summary>단면 보기 대상: 선택 물체를 싣고 있는 운반체(실려 있지 않으면 자신).</summary>
    private void UpdateSection()
    {
        int target = -1;
        if (SectionBox.IsChecked == true && _world is not null && _selected >= 0 && TimeFrame() is { } f) target = CarrierOf(_selected, f);
        View.SectionBody = target;
    }

    // ================= CalculiX 모달 과도 (등급 B) =================

    private readonly Dictionary<int, CcxTransientResult> _ccxShock = new();
    private int _ccxShockVersion;

    private void ResetCcxShock() { _ccxShock.Clear(); _ccxShockVersion++; }

    private void CcxShockFaceValues(int i, StressModel model, Dictionary<int, double[]> values)
    {
        if (!_ccxShock.TryGetValue(i, out var r) || r.AtTime(_time) is not { } fv || fv.Length != model.FaceCount) return;
        var v = new double[fv.Length];
        for (int k = 0; k < v.Length; k++) v[k] = fv[k] * 1e-6;
        values[i] = v;
    }

    private double? CcxShockRangeMax(IEnumerable<int> bodies)
    {
        double? best = null;
        foreach (int i in bodies)
            if (_ccxShock.TryGetValue(i, out var r) && r.MaxVonMises > 0) best = Math.Max(best ?? 0, r.MaxVonMises / 1e6);
        return best;
    }

    private string CcxShockNote()
    {
        var r = _selected >= 0 && _ccxShock.TryGetValue(_selected, out var s) ? s : _ccxShock.Values.FirstOrDefault();
        if (r is null) return "CalculiX 과도 — 결과 없음 (해석 → 개방 충격 과도 응답)";
        if (r.AtTime(_time) is null) return string.Format(CultureInfo.InvariantCulture, "등급 B · CalculiX 모달 과도 — 결과 구간 {0:F3}–{1:F3} s 밖", r.Times[0], r.Times[^1]);
        return string.Format(CultureInfo.InvariantCulture, "등급 B · CalculiX 모달 과도({0}모드, ζ {1:P0})\n{2} {3:+0.0;-0.0} ms · 범위 = 결과 최대(고정)",
            r.Modes, r.Zeta, r.Window.Label, (_time - r.Window.TTrigger) * 1e3);
    }

    /// <summary>선택 물체(실려 있으면 운반체)의 현재(없으면 다음) 충격 창을 CalculiX 모달 과도로.</summary>
    private async Task RunCcxShock(int elasticModes = 12)
    {
        if (_world is null || View.Scene is null || _selected < 0) { Log(LogLevel.Warn, "과도 FEA", "물체를 선택하세요(트리 또는 3D 클릭)."); return; }
        var f = TimeFrame();
        if (f is null) return;
        int bi = CarrierOf(_selected, f);
        var w = _world;
        var b = w.Bodies[bi];
        if (View.Scene.BodyMeshes[bi] is not { } display || ModelFor(bi) is not { } model) { Log(LogLevel.Warn, "과도 FEA", $"{b.Name}: 형상·응력 모델이 없습니다."); return; }
        if (b.Structure?.Material is null) { Log(LogLevel.Error, "과도 FEA", $"{b.Name}: 재질이 없어 FEA 를 할 수 없습니다."); ShowLog(); return; }
        if (b.Structure.Section == "thinWall") { Log(LogLevel.Warn, "과도 FEA", $"{b.Name}: {Sim.Core.Fea.CcxTransient.ShellUnsupported}"); ShowLog(); return; }
        var inst = Sim.Core.Fea.CalculixInstall.TryLocate(null, out var why);
        if (inst is null) { Log(LogLevel.Error, "과도 FEA", $"{why} — CCX_PATH 를 지정하세요."); ShowLog(); return; }
        // 충격 창: 보 동해석 이력이 있으면 그 창, 없으면 탐지만
        var wins = _shock.TryGetValue(bi, out var h) ? h.Windows : ShockResponse.FindWindows(w, model);
        var win = wins.FirstOrDefault(x => _time >= x.T0 && _time <= x.T1) ?? wins.FirstOrDefault(x => x.T0 > _time) ?? wins.LastOrDefault();
        if (win is null) { Log(LogLevel.Warn, "과도 FEA", $"{b.Name}: 충격 창이 없습니다(자유 비행 중 급격한 연결점 하중 상승 없음)."); return; }
        var dir = Path.Combine(_repo, "results", "ccx", $"{b.Name}_shock_{DateTime.Now:yyyyMMdd_HHmmss}");
        var job = BeginJob($"CalculiX 모달 과도 · {b.Name} · {win.Label}", cancellable: true);
        if (job is null) return;
        var ct = job.Cts.Token;
        try
        {
            Stage(job, "메시 · 하중 이력 · 탑재물 질량", null);
            var (run, res) = await Task.Run(() =>
            {
                var fe = Sim.Core.Fea.CcxTransient.FeSurface(b, display);
                var fm = Sim.Core.Fea.CcxBodyFea.BuildModel(b, fe, dir);
                if (fm.Mesh.Elements.Count > 40000) throw new InvalidOperationException($"FE 요소 {fm.Mesh.Elements.Count:N0}개 — 과도 해석에는 너무 많습니다(≤ 40,000).");
                var hist = Sim.Core.Fea.CcxTransient.History(w, model, win);
                var carried = model.CarriedAt(w, w.Recorder.Frames[w.Recorder.IndexAt(win.TTrigger)]);
                Dispatcher.UIThread.Post(() => Stage(job, $"ccx 고유진동 {elasticModes}모드 → 모달 과도 — {fm.Mesh.Source}, 요소 {fm.Mesh.Elements.Count:N0} (외부 프로세스)", null));
                return Sim.Core.Fea.CcxTransient.Solve(inst, fm, hist, carried, win, display, dir, elasticModes, ct: ct);
            }, ct);
            if (!run.Ok || res is null) { EndJob(job, LogLevel.Error, $"실패: {run.Error} — {ShownPath(dir)}"); return; }
            foreach (var n in res.Notes) Log(LogLevel.Info, "과도 FEA", n);
            _ccxShock[bi] = res;
            _ccxShockVersion++;
            if (_selected != bi) SelectBody(bi);
            SetColorMode(ColorMode.CcxShock);
            if (_time < res.Times[0] || _time > res.Times[^1]) Seek(Math.Clamp(res.MaxTime, res.Times[0], res.Times[^1]));
            RecolorNow();
            // 보 동해석(근사 C)과 같은 창 최대 비교 — 근사 모델 신뢰도 확인
            string cmp = "";
            int wi = h?.Windows.IndexOf(win) ?? -1;
            if (wi >= 0 && _shockPeaks.TryGetValue((bi, wi), out var pk) && pk.MPa > 0)
                cmp = string.Format(CultureInfo.InvariantCulture, " · 보 동해석(근사 C) 창 최대 {0:F2} MPa (비 {1:F2})", pk.MPa, res.MaxVonMises / 1e6 / pk.MPa);
            EndJob(job, LogLevel.Info, $"완료 {run.Seconds:F1} s — {res.Verdict}{cmp} — {ShownPath(dir)}");
        }
        catch (OperationCanceledException) { EndJob(job, LogLevel.Warn, "취소됨"); }
        catch (Exception ex) { _logWriter?.WriteLine(ex.ToString()); EndJob(job, LogLevel.Error, $"실패: {ex.Message}"); }
    }
}
