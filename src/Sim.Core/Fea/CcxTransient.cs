using System.Globalization;
using System.Text.Json.Nodes;
using Sim.Core.Engine;
using Sim.Core.Geometry;
using Sim.Core.Models;
using Sim.Core.Numerics;
using Sim.Core.Structures;

namespace Sim.Core.Fea;

/// <summary>CalculiX 모달 과도 응답 결과: 출력 시각(시뮬레이션 시각)별 표시 메시 면 von Mises [Pa].</summary>
public sealed class CcxTransientResult
{
    public required string Body { get; init; }
    public required ShockWindow Window { get; init; }
    public double[] Times { get; init; } = [];
    public List<float[]> FaceVonMises { get; init; } = new();
    public double MaxVonMises { get; init; }
    public double MaxTime { get; init; } = double.NaN;
    public double[] FrequenciesHz { get; init; } = [];
    public int Modes { get; init; }
    public double Zeta { get; init; }
    public double Dt { get; init; }
    public double Seconds { get; init; }
    public string Dir { get; init; } = "";
    public List<string> Notes { get; init; } = new();
    public const string Grade = "B";

    /// <summary>탄성 모드(강체 6개 제외) 진동수 [Hz].</summary>
    public IEnumerable<double> ElasticHz => FrequenciesHz.Where(f => f > CcxBodyFea.RigidHz(FrequenciesHz));

    /// <summary>시각 t 의 면 값(출력 사이 선형 보간). 범위 밖이면 null.</summary>
    public float[]? AtTime(double t)
    {
        if (Times.Length == 0 || t < Times[0] - 1e-9 || t > Times[^1] + 1e-9) return null;
        int i = Array.BinarySearch(Times, t);
        if (i >= 0) return FaceVonMises[i];
        i = ~i;
        if (i <= 0) return FaceVonMises[0];
        if (i >= Times.Length) return FaceVonMises[^1];
        double f = (t - Times[i - 1]) / Math.Max(Times[i] - Times[i - 1], 1e-15);
        var a = FaceVonMises[i - 1]; var b = FaceVonMises[i];
        var r = new float[a.Length];
        for (int k = 0; k < r.Length; k++) r[k] = (float)(a[k] * (1 - f) + b[k] * f);
        return r;
    }

    public string Verdict => string.Format(CultureInfo.InvariantCulture,
        "CalculiX 모달 과도({0}모드, ζ {1:P0}) — {2}: σvm 최대 {3:F2} MPa @ {4:F4} s, 탄성 1차 {5:F0} Hz",
        Modes, Zeta, Window.Label, MaxVonMises / 1e6, MaxTime, ElasticHz.DefaultIfEmpty(double.NaN).First());

    public JsonObject ToJson() => new()
    {
        ["body"] = Body, ["grade"] = Grade, ["window"] = Window.Label, ["trigger_s"] = Window.TTrigger,
        ["t0_s"] = Times.Length > 0 ? Times[0] : null, ["t1_s"] = Times.Length > 0 ? Times[^1] : null, ["outputs"] = Times.Length,
        ["maxVonMises_MPa"] = MaxVonMises / 1e6, ["maxTime_s"] = double.IsFinite(MaxTime) ? MaxTime : null,
        ["modes"] = Modes, ["zeta"] = Zeta, ["dt_s"] = Dt, ["elasticHz"] = new JsonArray(ElasticHz.Take(10).Select(x => (JsonNode)x).ToArray()),
        ["seconds"] = Seconds, ["dir"] = Dir, ["notes"] = new JsonArray(Notes.Select(x => (JsonNode)x).ToArray()),
    };
}

/// <summary>
/// 개방 충격·스냅 하중의 CalculiX 모달 과도 응답(등급 B — 3D 셸/솔리드 FE, 외부 솔버): 자유-자유 모델(약한 접지 스프링,
/// 강체 모드 ≈ 0 Hz 포함)의 *FREQUENCY(STORAGE) → *MODAL DYNAMIC. 하중 = 기록 프레임의 연결점 하중(라이저·테더·힌지·펄스)을
/// 출처별·성분별 *AMPLITUDE 로(부착 반경 안 표면 절점에 고르게). 창 시작 하중은 반-코사인 램프(20 ms, 구조 주기보다 충분히 김)로 먼저 걸어
/// 정지 상태에서 갑자기 거는 인공 충격을 피한다. 실린 탑재물(Stowed 자손)은 그 축 위치 둘레 절점 링에 MASS 요소로 나눠 단다.
/// 한계: 공력 압력·중력(균일, 내력 없음)·접촉·비선형 없음, 모드 절단(고차 모드 응력 일부 누락), 선형.
/// </summary>
public static class CcxTransient
{
    public const double RampSeconds = 0.02;
    /// <summary>
    /// 얇은 벽(S6 셸) 모델의 모달 과도는 지원하지 않는다: 이 설치(CalculiX 2.23 win)에서 09 컨테이너(셸 864개, 관성 완화·매달기 스프링, 탑재물 유무 무관)
    /// 응력이 시간³ 으로 커져 10⁶ MPa 에 이름(강체 운동이 가짜 변형률을 냄, 실측) + 한 번에 14 분. 솔리드(복셀·Gmsh) 모델은 막대 계단 하중 DAF 1.92
    /// (보 동해석 2.00)로 확인됐다.
    /// </summary>
    public const string ShellUnsupported = "얇은 벽(셸) 물체의 CalculiX 모달 과도는 지원하지 않습니다 — 이 설치에서 셸 모달 과도가 강체 운동으로 가짜 응력(10⁶ MPa)을 냄(실측). "
        + "솔리드 단면 물체만 가능하며, 셸 물체는 '충격 응력(보 동해석, 근사 C)'을 쓰세요.";

    /// <summary>매달기 강체 모드 진동수 [Hz] (탄성 1차의 1/10 이하 권장).</summary>
    public const double SuspensionHz = 2;

    /// <summary>하중 이력: 시각 [s] → 연결점 하중(동체 좌표).</summary>
    public sealed record LoadSample(double T, IReadOnlyList<PointLoad> Loads);

    /// <summary>World 기록에서 창 하중 이력: [촉발 − 5 ms, min(창 끝, 촉발 + span)].</summary>
    public static List<LoadSample> History(World w, StressModel m, ShockWindow win, double span = 0.08)
    {
        double t0 = Math.Max(win.T0, win.TTrigger - 0.005), t1 = Math.Min(win.T1, win.TTrigger + span);
        var list = new List<LoadSample>();
        var frames = w.Recorder.Frames;
        int i0 = w.Recorder.IndexAt(t0), i1 = Math.Min(frames.Count - 1, w.Recorder.IndexAt(t1) + 1);
        for (int i = Math.Max(0, i0); i <= i1; i++)
        {
            var f = frames[i];
            if (f.Bodies[m.Body.Index].Mode != BodyMode.Free) continue;
            list.Add(new LoadSample(f.T, m.InputFrom(w, f).PointLoads));
        }
        return list;
    }

    /// <summary>
    /// 모달 과도 모델. 시각 원점 = history[0].T − RampSeconds (스텝 시각 τ ↔ 시뮬레이션 시각 τ + 원점).
    /// </summary>
    public static (CcxModel Model, double TimeOrigin, List<string> Notes) Build(BodyFeModel fm, IReadOnlyList<LoadSample> history, IReadOnlyList<CarriedMass>? carried,
        int elasticModes = 30, double zeta = 0.03, double dt = 1e-4, int maxOutputs = 40) => Build(fm, history, carried, out _, elasticModes, zeta, dt, maxOutputs);

    /// <param name="ramp">실제 램프 길이 [s] — 창 시작 하중이 최대의 1% 이하이면 0(정지 상태에서 바로 시작).</param>
    public static (CcxModel Model, double TimeOrigin, List<string> Notes) Build(BodyFeModel fm, IReadOnlyList<LoadSample> history, IReadOnlyList<CarriedMass>? carried,
        out double ramp, int elasticModes = 30, double zeta = 0.03, double dt = 1e-4, int maxOutputs = 40)
    {
        if (history.Count < 2) throw new InvalidDataException("과도 해석: 하중 이력 점이 2개 이상 필요합니다(기록 프레임 부족 — sim.shockCapture 로 충격 구간을 매 스텝 기록).");
        var mesh = fm.Mesh;
        var notes = new List<string>();
        var m = new CcxModel { Mesh = mesh, Material = fm.Material, Analysis = CcxAnalysis.ModalDynamic, Modes = elasticModes + 6, ModalDampingRatio = zeta, Title = $"{fm.BodyName} modal transient" };
        var (_, cg, _) = mesh.MassProperties(fm.Material.Density);
        var (bmin, bmax) = mesh.Bounds();
        // 하중 출처별 부착 절점
        var used = mesh.UsedNodes();
        var surf = fm.Shell ? Enumerable.Range(0, mesh.Nodes.Count).Where(i => used[i]).ToList() : fm.Surface.SelectMany(f => f.Corners).Distinct().ToList();
        var sources = history.SelectMany(h => h.Loads.Select(l => l.Source)).Distinct().ToList();
        var taken = new HashSet<int>();
        double peakAll = history.Max(h => h.Loads.Sum(l => l.Force.Norm));
        ramp = history[0].Loads.Sum(l => l.Force.Norm) > 0.01 * peakAll ? RampSeconds : 0;
        double t0 = history[0].T, origin = t0 - ramp;
        double rampLen = ramp;
        int nh = history.Count, nn = mesh.Nodes.Count;
        // 절점별 가해진 하중 이력 [표본][절점]
        var applied = new Dictionary<int, Vec3[]>();
        foreach (var src in sources)
        {
            // 위치 = 하중이 가장 큰 순간의 작용점
            var pk = history.SelectMany(h => h.Loads.Where(l => l.Source == src)).MaxBy(l => l.Force.Norm);
            double r = Math.Max(fm.AttachRadius, 1.5 * fm.Size);
            var near = surf.Where(n => !taken.Contains(n) && (mesh.Nodes[n] - pk.Point).Norm <= r).ToList();
            if (near.Count == 0) near = surf.Where(n => !taken.Contains(n)).OrderBy(n => (mesh.Nodes[n] - pk.Point).NormSq).Take(4).ToList();
            if (near.Count == 0) continue;
            foreach (var n in near) taken.Add(n);
            Vec3 F(LoadSample h) => h.Loads.Where(l => l.Source == src).Aggregate(Vec3.Zero, (a, l) => a + l.Force);
            double fmax = history.Max(h => F(h).Norm);
            if (!(fmax > 0)) continue;
            foreach (var n in near)
            {
                if (!applied.TryGetValue(n, out var arr)) applied[n] = arr = new Vec3[nh];
                for (int k = 0; k < nh; k++) arr[k] += F(history[k]) / near.Count;
            }
            notes.Add(string.Format(CultureInfo.InvariantCulture, "하중 {0}: 절점 {1}개(반경 {2:F3} m), 최대 {3:F0} N", src, near.Count, r, fmax));
        }
        if (applied.Count == 0) throw new InvalidDataException("과도 해석: 이 창에 연결점 하중이 없습니다.");
        // 절점 집중 질량(요소 질량을 절점에 고르게) + 실린 탑재물(축 위치 둘레 절점 링, MASS 요소)
        // 꼭짓점 절점에만 집중(중간 절점 제외) — 절점 하중 이력(*AMPLITUDE) 수가 ~1/4 로 준다. ccx 는 같은 절점·자유도에 이력이 다른 하중을
        // 두 번 걸 수 없어(forcadd 오류, 실측) 절점마다 이력 하나가 필요하고, 이력 수천 개는 입력 읽기만 수 분(이름 선형 탐색) 걸린다.
        var nodeMass = new double[nn];
        for (int e = 0; e < mesh.Elements.Count; e++)
        {
            var el = mesh.Elements[e];
            int corners = el.Type switch
            {
                FeType.S6 => 3, FeType.S8R or FeType.CPS8 => 4, FeType.C3D10 or FeType.C3D4 => 4,
                FeType.C3D20 or FeType.C3D20R or FeType.C3D8 or FeType.C3D8I => 8, _ => el.N.Length,
            };
            corners = Math.Min(corners, el.N.Length);
            double me = fm.Material.Density * mesh.ElementVolume(e) / corners;
            for (int k = 0; k < corners; k++) nodeMass[el.N[k]] += me;
        }
        if (carried is { Count: > 0 })
        {
            var size = bmax - bmin;
            int ax = size.X >= size.Y && size.X >= size.Z ? 0 : size.Y >= size.Z ? 1 : 2;
            var usedIds = Enumerable.Range(0, nn).Where(i => used[i]).ToList();
            foreach (var c in carried)
            {
                double band = Math.Max(0.02, 2 * fm.Size);
                var ring = usedIds.Where(n => Math.Abs(mesh.Nodes[n][ax] - c.Point[ax]) <= band).ToArray();
                if (ring.Length < 4) ring = usedIds.OrderBy(n => (mesh.Nodes[n] - c.Point).NormSq).Take(8).ToArray();
                m.PointMasses.Add((ring, c.Mass / ring.Length));
                foreach (var n in ring) nodeMass[n] += c.Mass / ring.Length;
                notes.Add(string.Format(CultureInfo.InvariantCulture, "탑재물 {0} {1:F2} kg → 절점 링 {2}개(축 위치 ±{3:F3} m)", c.Name, c.Mass, ring.Length, band));
            }
        }
        // 시간 영역 관성 완화: 매 순간 강체 a(t), α(t) 를 구해 절점마다 −m_n(a + α×r) 를 더함 → 하중이 늘 자기평형이라 강체 모드(≈0 Hz)가
        // 들뜨지 않는다(자유 물체가 순 하중으로 미터 단위로 움직이면 수치적으로 오염된 강체 모드가 가짜 응력을 낸다 — 실측 3.5e6 MPa).
        double mTot = nodeMass.Sum();
        var cm = Vec3.Zero;
        for (int n = 0; n < nn; n++) cm += mesh.Nodes[n] * nodeMass[n];
        cm /= mTot;
        var inertia = Mat3.Zero;
        for (int n = 0; n < nn; n++) if (nodeMass[n] > 0) inertia = inertia + Mat3.ParallelAxis(nodeMass[n], mesh.Nodes[n] - cm);
        var invI = inertia.Inverse();
        // 유연 매달기(suspension): 3-2-1 절점의 접지 스프링으로 강체 모드를 ≈ SuspensionHz 로 — 자유-자유 모달 시험처럼 탄성 1차보다 훨씬 낮게.
        // 아주 약한 스프링(≈0 Hz)은 ARPACK 이 수천 번 반복해 표시 메시 8천 셸에서 40 분 넘게 걸렸다(실측). 하중이 자기평형이라 강체 모드는 거의 안 들뜬다.
        double kSpring = mTot * Math.Pow(2 * Math.PI * SuspensionHz, 2) / 3;
        foreach (var nd in CcxBodyFea.Supports321(mesh, cg).Nodes.Distinct())
            for (int d = 1; d <= 3; d++) m.GroundSprings.Add((nd, d, kSpring));
        var acc = new Vec3[nh]; var alp = new Vec3[nh];
        for (int k = 0; k < nh; k++)
        {
            var f = Vec3.Zero; var mo = Vec3.Zero;
            foreach (var (n, arr) in applied) { f += arr[k]; mo += Vec3.Cross(mesh.Nodes[n] - cm, arr[k]); }
            acc[k] = f / mTot; alp[k] = invI * mo;
        }
        double peakNode = 0;
        var series = new List<(int Node, int Dof, double[] V)>();
        for (int n = 0; n < nn; n++)
        {
            if (nodeMass[n] <= 0 && !applied.ContainsKey(n)) continue;
            var v = new Vec3[nh];
            var rr = mesh.Nodes[n] - cm;
            for (int k = 0; k < nh; k++)
                v[k] = (applied.TryGetValue(n, out var arr) ? arr[k] : Vec3.Zero) - (acc[k] + Vec3.Cross(alp[k], rr)) * nodeMass[n];
            for (int d = 0; d < 3; d++)
            {
                var vd = v.Select(x => x[d]).ToArray();
                double mx = vd.Max(Math.Abs);
                peakNode = Math.Max(peakNode, mx);
                series.Add((n, d + 1, vd));
            }
        }
        int ai = 0;
        foreach (var (n, d, vd) in series)
        {
            if (vd.Max(Math.Abs) < 1e-9 * peakNode) continue;
            // 램프: 0 → 창 시작 값 반-코사인, 이후 기록 그대로(스텝 시각 = 시뮬레이션 시각 − origin)
            var pts = new List<(double, double)>();
            if (rampLen > 0) for (int k = 0; k <= 8; k++) pts.Add((rampLen * k / 8, vd[0] * (1 - Math.Cos(Math.PI * k / 8)) / 2));
            else pts.Add((0, vd[0]));
            for (int k = 1; k < nh; k++) pts.Add((history[k].T - origin, vd[k]));
            string name = $"A{++ai}";
            m.Amplitudes.Add((name, pts.ToArray()));
            m.AmplitudeLoads.Add((n, d, 1.0, name));
        }
        notes.Add(string.Format(CultureInfo.InvariantCulture, "시간 영역 관성 완화: 질량 {0:F2} kg(구조+탑재물), 강체 가속도 최대 {1:F1} G — 절점 하중 이력 {2}개",
            mTot, acc.Max(a => a.Norm) / 9.80665, ai));
        double total = history[^1].T - origin;
        m.DynDt = dt;
        m.DynTotal = total;
        // 출력 간격은 램프 뒤(실제 충격 구간) 기준으로 maxOutputs 개
        int incs = (int)Math.Ceiling((total - rampLen) / dt);
        m.OutputEvery = Math.Max(1, (int)Math.Ceiling(incs / (double)maxOutputs));
        notes.Add(string.Format(CultureInfo.InvariantCulture, "모달 과도: 탄성 {0}모드(+강체 6), ζ {1:P0}, Δt {2:G3} ms, {3:F1} ms(램프 {4:F0} ms 포함), 출력 {5} 증분마다 — 공력 압력·중력(균일) 제외",
            elasticModes, zeta, dt * 1e3, total * 1e3, rampLen * 1e3, m.OutputEvery));
        return (m, origin, notes);
    }

    /// <summary>표시 메시 면 → 확인할 frd 절점 번호들(정적 결과 매핑과 같은 규칙: 셸 = 꼭짓점·중심 ±t/2, 솔리드 = 중심·꼭짓점의 최근접 표면 절점).</summary>
    public static int[][] FaceProbes(BodyFeModel fm, FrdData frd, TriMesh display)
    {
        var keys = frd.Coords.Keys.ToArray();
        var pts = keys.Select(k => frd.Coords[k]).ToArray();
        IEnumerable<int>? subset = null;
        if (!fm.Shell)
        {
            var surf = new HashSet<int>(fm.Surface.SelectMany(f => f.Corners).Select(n => n + 1));
            subset = Enumerable.Range(0, keys.Length).Where(i => surf.Contains(keys[i]));
        }
        double cell = Math.Max(fm.Size, (fm.Mesh.Thickness ?? 0) * 2);
        var hash = new SpatialHash(pts, Math.Max(cell, 1e-6), subset);
        double t2 = (fm.Mesh.Thickness ?? 0) / 2;
        var res = new int[display.TriangleCount][];
        for (int f = 0; f < res.Length; f++)
        {
            Vec3 a = display.Vertices[3 * f], b = display.Vertices[3 * f + 1], c = display.Vertices[3 * f + 2];
            var n = Vec3.Cross(b - a, c - a);
            n = n.Norm > 0 ? n / n.Norm : Vec3.Zero;
            var probes = fm.Shell
                ? new[] { a + n * t2, a - n * t2, b + n * t2, b - n * t2, c + n * t2, c - n * t2, (a + b + c) / 3 + n * t2, (a + b + c) / 3 - n * t2 }
                : new[] { (a + b + c) / 3, a, b, c };
            res[f] = probes.Select(p => hash.Nearest(p, out _, 24)).Where(k => k >= 0).Select(k => keys[k]).Distinct().ToArray();
        }
        return res;
    }

    /// <summary>모델 → 덱 → ccx(.frd 는 스트리밍) → 출력 시각별 면 von Mises.</summary>
    public static (CcxResult Run, CcxTransientResult? Result) Solve(CalculixInstall inst, BodyFeModel fm, IReadOnlyList<LoadSample> history, IReadOnlyList<CarriedMass>? carried,
        ShockWindow win, TriMesh display, string dir, int elasticModes = 30, double zeta = 0.03, double dt = 1e-4, int maxOutputs = 40,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (fm.Shell) throw new NotSupportedException(ShellUnsupported);
        var (model, origin, notes) = Build(fm, history, carried, out double ramp, elasticModes, zeta, dt, maxOutputs);
        string inp;
        try { inp = CcxDeck.Write(model, dir, "shock"); }
        catch (Exception ex) { return (new CcxResult { Dir = dir, Job = "shock", Error = $"덱 작성 실패: {ex.Message}" }, null); }
        var run = CcxRunner.Run(inst, inp, timeout ?? TimeSpan.FromMinutes(60), ct, parseFrd: false);
        if (!run.Ok) return (run, null);
        int[][]? probes = null;
        HashSet<int>? need = null;
        var times = new List<double>(); var faces = new List<float[]>();
        double best = 0, bestT = double.NaN;
        FrdData.Parse(run.FrdPath, (frd, blk) =>
        {
            ct.ThrowIfCancellationRequested();
            if (!blk.Name.Equals("STRESS", StringComparison.OrdinalIgnoreCase)) return;
            if (probes is null) { probes = FaceProbes(fm, frd, display); need = probes.SelectMany(p => p).ToHashSet(); }
            var vm = new Dictionary<int, double>(need!.Count);
            foreach (var id in need!)
                if (blk.Values.TryGetValue(id, out var s) && s.Length >= 6) vm[id] = Tensor.VonMises(s);
            var fv = new float[probes.Length];
            for (int f = 0; f < fv.Length; f++)
            {
                double mx = 0;
                foreach (var id in probes[f]) if (vm.TryGetValue(id, out var v) && v > mx) mx = v;
                fv[f] = (float)mx;
            }
            double t = blk.Value + origin;
            // 램프 구간 출력은 버린다(창 시작 하중을 천천히 거는 준비 구간)
            if (blk.Value < ramp - 1e-9) return;
            times.Add(t); faces.Add(fv);
            // 최대값은 FE 절점 전체 기준
            foreach (var (_, s) in blk.Values) if (s.Length >= 6) { double v = Tensor.VonMises(s); if (v > best) { best = v; bestT = t; } }
        });
        run.Dat = DatData.Parse(Path.Combine(dir, "shock.dat"));
        var res = new CcxTransientResult
        {
            Body = fm.BodyName, Window = win, Times = times.ToArray(), FaceVonMises = faces, MaxVonMises = best, MaxTime = bestT,
            FrequenciesHz = run.Dat.FrequenciesHz.ToArray(), Modes = elasticModes, Zeta = zeta, Dt = dt, Seconds = run.Seconds, Dir = dir,
            Notes = fm.Notes.Concat(notes).ToList(),
        };
        if (res.Times.Length == 0) { run.Ok = false; run.Error = "과도 응답 출력(STRESS)이 없습니다."; return (run, null); }
        return (run, res);
    }

    /// <summary>과도 해석용 FE 표면 최대 면 수 — CalculiX 자유-자유 고유값 단계가 느려(복셀 막대 640 요소 ~2 분, 표시 메시 8천 셸은 1 시간 넘게 실측)
    /// 거친 표면으로 풀고 결과를 표시 메시에 최근접 매핑한다.</summary>
    public const int MaxFeFaces = 1200;

    /// <summary>
    /// 과도 해석용 FE 표면(셸 1:1 이라 요소 수 = 면 수): 표시 메시가 MaxFeFaces 이하면 그대로, 아니면 기본 형상은 거친 구조 격자,
    /// 가져온 메시는 원본 STL 이 충분히 거칠 때만. 그 밖은 오류(요소 수를 줄일 방법이 없음).
    /// </summary>
    public static TriMesh FeSurface(Body b, TriMesh display, int maxFaces = MaxFeFaces)
    {
        if (display.TriangleCount <= maxFaces) return display;
        var s = b.Shape;
        switch (s?.Kind)
        {
            case ShapeKind.Cylinder: return TriMesh.CylinderGrid(s.Radius, s.Length, s.Axis, s.Center, 16, 24, 2);
            case ShapeKind.ShellSegment: return TriMesh.ShellSegment(s.Radius, s.Thickness, s.Length, s.SpanDeg, s.CenterAngleDeg, s.Axis, s.Center, 0, 24);
            case ShapeKind.Sphere: return TriMesh.Sphere(s.Radius, s.Center, 12, 24);
            case ShapeKind.Box:
            {
                var z = s.Size; var c = s.Center; var hs = z / 2;
                int ax = z.X >= z.Y && z.X >= z.Z ? 0 : z.Y >= z.Z ? 1 : 2;
                int N(int a) => a == ax ? 16 : 4;
                int nx = N(0), ny = N(1), nz = N(2);
                Vec3 P(int x, int y, int zz) => c + new Vec3(x * hs.X, y * hs.Y, zz * hs.Z);
                var m = new TriMesh();
                m.AddGrid(P(1, -1, -1), P(1, 1, -1), P(1, 1, 1), P(1, -1, 1), ny, nz);
                m.AddGrid(P(-1, -1, -1), P(-1, -1, 1), P(-1, 1, 1), P(-1, 1, -1), nz, ny);
                m.AddGrid(P(-1, 1, -1), P(-1, 1, 1), P(1, 1, 1), P(1, 1, -1), nz, nx);
                m.AddGrid(P(-1, -1, -1), P(1, -1, -1), P(1, -1, 1), P(-1, -1, 1), nx, nz);
                m.AddGrid(P(-1, -1, 1), P(1, -1, 1), P(1, 1, 1), P(-1, 1, 1), nx, ny);
                m.AddGrid(P(-1, -1, -1), P(-1, 1, -1), P(1, 1, -1), P(1, -1, -1), ny, nx);
                return m;
            }
            case ShapeKind.Mesh:
                try
                {
                    var raw = s.BuildMesh();
                    if (raw.TriangleCount > 0 && raw.TriangleCount <= maxFaces) return raw;
                    throw new InvalidOperationException($"가져온 형상 {raw.TriangleCount:N0}면 — 과도 해석은 {maxFaces:N0}면 이하 표면만(CalculiX 고유값 단계 시간). 합성 대체 형상(realModels.mode=synthetic) 또는 거친 STL 을 쓰세요.");
                }
                catch (InvalidDataException ex) { throw new InvalidOperationException($"형상 메시를 읽지 못했습니다: {ex.Message}"); }
        }
        throw new InvalidOperationException($"{b.Name}: 과도 해석용 거친 메시를 만들 수 없습니다(표시 면 {display.TriangleCount:N0} > {maxFaces:N0}).");
    }
}
