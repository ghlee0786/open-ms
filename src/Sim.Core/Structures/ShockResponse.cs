using System.Text.Json.Nodes;
using Sim.Core.Engine;
using Sim.Core.Models;
using Sim.Core.Numerics;

namespace Sim.Core.Structures;

/// <summary>충격 창: 물체 점하중 합이 빠르게 오른 순간(낙하산 개방 충격 · 테더 스냅 등) 앞뒤 구간.</summary>
/// <param name="Label">표시 이름(예: "container.drogue 개방 충격").</param>
/// <param name="Source">하중 상승을 주도한 연결점 하중 출처(PointLoad.Source).</param>
/// <param name="TTrigger">하중 상승 시작 시각 [s].</param>
/// <param name="PeakLoadN">창 안 점하중 합 최대 [N].</param>
public sealed record ShockWindow(string Label, string Source, double TTrigger, double T0, double T1, double PeakLoadN);

/// <summary>
/// 물체 하나의 충격 응답 시간 이력(보 동해석 결과): 출력 시각별 슬라이스(보 절점) 비중력 가속도 [m/s², 동체 좌표].
/// 면 응력은 표시 시각에 StressModel.Evaluate(…, sliceAccel) 로 다시 계산한다(동적 관성 하중 −m_k·a_k).
/// </summary>
public sealed class ShockHistory
{
    public required StressModel Model { get; init; }
    public List<ShockWindow> Windows { get; } = new();
    /// <summary>창별 출력 시각·슬라이스 가속도(창 순서, 시각 오름차순).</summary>
    public List<double[]> Times { get; } = new();
    public List<Vec3[][]> SliceAccel { get; } = new();
    /// <summary>창별 슬라이스 최대 가속도 [G] (출력 시각마다 최대 |a_k|).</summary>
    public List<double[]> PeakAccelG { get; } = new();
    /// <summary>창별 실린 탑재물(보 질량에 포함한 Stowed 자손).</summary>
    public List<List<CarriedMass>> Carried { get; } = new();
    /// <summary>탄성 1차 고유진동수 [Hz]: 축, 굽힘(u 평면), 굽힘(v 평면). 자유-자유, 집중 질량 보.</summary>
    public double AxialHz, BendUHz, BendVHz;
    public double Zeta { get; init; }
    public double Dt { get; init; }
    public double OutputDt { get; init; }
    public double E { get; init; }

    public int WindowIndexAt(double t)
    {
        for (int i = 0; i < Windows.Count; i++)
            if (t >= Windows[i].T0 && t <= Windows[i].T1) return i;
        return -1;
    }

    public ShockWindow? WindowAt(double t) => WindowIndexAt(t) is var i and >= 0 ? Windows[i] : null;

    /// <summary>시각 t 의 슬라이스 가속도(창 안: 출력 시각 사이 선형 보간, 창 밖: null).</summary>
    public Vec3[]? AccelAt(double t)
    {
        int w = WindowIndexAt(t);
        if (w < 0) return null;
        var ts = Times[w]; var a = SliceAccel[w];
        if (ts.Length == 0) return null;
        int i = Array.BinarySearch(ts, t);
        if (i >= 0) return a[i];
        i = ~i;
        if (i <= 0) return a[0];
        if (i >= ts.Length) return a[^1];
        double f = (t - ts[i - 1]) / Math.Max(ts[i] - ts[i - 1], 1e-15);
        var r = new Vec3[a[i].Length];
        for (int k = 0; k < r.Length; k++) r[k] = a[i - 1][k] * (1 - f) + a[i][k] * f;
        return r;
    }

    /// <summary>창 w 의 최대 슬라이스 가속도 [G] 와 그 시각.</summary>
    public (double G, double T) WindowPeak(int w)
    {
        var p = PeakAccelG[w];
        int i = p.Length == 0 ? -1 : Array.IndexOf(p, p.Max());
        return i < 0 ? (0, double.NaN) : (p[i], Times[w][i]);
    }

    public string Method => $"1D 보 동해석(축+2평면 굽힘 Euler–Bernoulli, 집중 질량 {Model.Slices}절점, Newmark 평균가속도 Δt {Dt * 1e3:G3} ms, " +
        $"강성 비례 감쇠 ζ {Zeta:P0} @ 1차) — 탄성 1차 축 {AxialHz:F0} Hz · 굽힘 {Math.Min(BendUHz, BendVHz):F0} Hz" +
        (BeamLike ? "" : " · 짧고 굵은 물체(길이 < 2×폭) — 보 모델 신뢰 낮음(전단·판 거동 지배)");

    /// <summary>보 모델 적합: 보 축 길이 ≥ 단면 최대 폭의 2배(가늘고 긴 물체). 아니면 전단·판 거동이 지배해 1D 보 근사의 신뢰가 낮다.</summary>
    public bool BeamLike
    {
        get
        {
            double width = 2 * Math.Max(Model.SecExtU.DefaultIfEmpty(0).Max(), Model.SecExtV.DefaultIfEmpty(0).Max());
            return Model.X1 - Model.X0 >= 2 * width;
        }
    }

    public JsonObject ToJson() => new()
    {
        ["body"] = Model.Body.Name, ["method"] = Method, ["grade"] = ShockResponse.Grade, ["beamLike"] = BeamLike,
        ["axialHz"] = AxialHz, ["bendUHz"] = BendUHz, ["bendVHz"] = BendVHz, ["zeta"] = Zeta, ["E_Pa"] = E,
        ["windows"] = new JsonArray(Windows.Select((x, i) =>
        {
            var (g, t) = WindowPeak(i);
            return (JsonNode)new JsonObject
            {
                ["label"] = x.Label, ["source"] = x.Source, ["trigger_s"] = x.TTrigger, ["t0_s"] = x.T0, ["t1_s"] = x.T1,
                ["peakLoad_N"] = x.PeakLoadN, ["peakAccel_g"] = g, ["peakAccelTime_s"] = double.IsFinite(t) ? t : null,
            };
        }).ToArray()),
    };
}

/// <summary>
/// 개방 충격·스냅 하중 충격 응답(근사 등급 C, FEA 아님): StressModel 슬라이스를 절점으로 한 자유-자유 1D 보
/// (축 막대 EA + 두 평면 Euler–Bernoulli 굽힘 EI, 단면 성질 = StressModel, 집중 질량 = 슬라이스 질량, 회전 관성 m·Δx²/12)를
/// Newmark 평균가속도(무조건 안정)로 적분한다. 하중 = 기록 프레임 점하중(라이저·테더·힌지·펄스) + 수정 뉴턴 공력 압력(슬라이스 합력),
/// 프레임 사이 선형 보간. 중력은 균일 체적력이라 내력을 만들지 않아 뺀다 → 계산 가속도 = 가속도계 값(비중력).
/// 초기 상태 = 창 시작 순간의 준정적 평형(강체 가속도 + 자기평형 하중의 탄성 처짐, 상대 속도 0).
/// 감쇠 = 강성 비례 βK(ζ @ 탄성 1차, 강체 운동은 감쇠 안 함 — 질량 비례 항을 쓰지 않는 이유). 응력파(축 방향 c = √(EA/m'))와
/// 구조 울림(1차 굽힘·축 모드)이 시간에 따라 퍼지고 줄어드는 모습을 낸다. 한계: 전단 변형·비틀림·국부 셸 모드 없음, 선형.
/// </summary>
public static class ShockResponse
{
    public const string Grade = "C";
    public const double DefaultZeta = 0.03;

    // ---------------- 충격 창 탐지 ----------------

    /// <summary>
    /// 물체가 자유 비행 중인 기록 프레임에서 점하중 합 S(t) 가 lookBack 안에 riseG·m·g₀ 이상 오른 순간 → 창 [시작 − pre, 시작 + post]
    /// (하중 최대가 늦으면 최대 + 0.15 s 까지 늘림). 출처 = 그 사이 가장 많이 오른 점하중.
    /// </summary>
    public static List<ShockWindow> FindWindows(World w, StressModel m, double riseG = 3, double pre = 0.02, double post = 0.5, int maxWindows = 12, double lookBack = 0.25)
    {
        var res = new List<ShockWindow>();
        var frames = w.Recorder.Frames;
        var b = m.Body;
        double dS = riseG * m.TotalMass * StressModel.G0;
        var ts = new List<double>(); var ss = new List<double>(); var src = new List<Dictionary<string, double>>();
        foreach (var f in frames)
        {
            if (f.Bodies[b.Index].Mode != BodyMode.Free) { ts.Add(f.T); ss.Add(double.NaN); src.Add(new()); continue; }
            var inp = m.InputFrom(w, f);
            var d = new Dictionary<string, double>();
            double s = 0;
            foreach (var pl in inp.PointLoads) { double fm = pl.Force.Norm; s += fm; d[pl.Source] = d.GetValueOrDefault(pl.Source) + fm; }
            ts.Add(f.T); ss.Add(s); src.Add(d);
        }
        double busyUntil = double.NegativeInfinity;
        for (int i = 1; i < ts.Count && res.Count < maxWindows; i++)
        {
            if (double.IsNaN(ss[i]) || ts[i] <= busyUntil) continue;
            // lookBack 안 최소값(같은 자유 비행 구간) — 주낙하산처럼 수백 ms 에 걸쳐 차오르는 개방 하중도 잡도록 0.25 s
            int jMin = i;
            for (int j = i - 1; j >= 0 && ts[i] - ts[j] <= lookBack && !double.IsNaN(ss[j]); j--) if (ss[j] < ss[jMin]) jMin = j;
            if (ss[i] - ss[jMin] < dS) continue;
            double tStart = ts[jMin];
            // 최대 하중 시각(post 안 — 다음 충격(예: 주낙하산)은 별도 창으로)
            int iPk = i;
            for (int j = i; j < ts.Count && ts[j] - tStart <= post && !double.IsNaN(ss[j]); j++) if (ss[j] > ss[iPk]) iPk = j;
            double t0 = Math.Max(tStart - pre, FreeStart(ts, ss, jMin));
            double t1 = Math.Min(Math.Max(tStart + post, ts[iPk] + 0.15), FreeEnd(ts, ss, i));
            // 출처: tStart → 최대 사이 가장 많이 오른 하중
            string best = "?"; double up = double.NegativeInfinity;
            foreach (var (k, v) in src[iPk]) { double dv = v - src[jMin].GetValueOrDefault(k); if (dv > up) { up = dv; best = k; } }
            res.Add(new ShockWindow(Label(w, best), best, tStart, t0, t1, ss[iPk]));
            busyUntil = t1;
        }
        return res;
    }

    private static double FreeStart(List<double> ts, List<double> ss, int i)
    {
        while (i > 0 && !double.IsNaN(ss[i - 1])) i--;
        return ts[i];
    }

    private static double FreeEnd(List<double> ts, List<double> ss, int i)
    {
        while (i + 1 < ts.Count && !double.IsNaN(ss[i + 1])) i++;
        return ts[i];
    }

    private static string Label(World w, string source)
    {
        foreach (var b in w.Bodies)
            foreach (var d in b.Devices)
                if (d.FullName == source)
                    return d switch { Parachute => $"{source} 개방 충격", Pulse => $"{source} 충격 펄스", _ => $"{source} 하중" };
        foreach (var c in w.Connectors)
            if (c.Name == source)
                return c switch { Tether => $"{source} 스냅", Hinge => $"{source} 힌지 하중", _ => $"{source} 하중" };
        return $"{source} 하중";
    }

    // ---------------- 보 모델 ----------------

    /// <summary>한 부분계(축 또는 굽힘 한 평면): 강성 K(밀집), 질량 M(대각), 강체 모드 R.</summary>
    internal sealed class Part
    {
        public required double[,] K;
        public required double[] M;
        public required double[][] R;
        public int N => M.Length;
    }

    /// <summary>슬라이스별 실린 탑재물 질량(가장 가까운 슬라이스에 집중).</summary>
    public static double[] CarriedSliceMass(StressModel m, IEnumerable<CarriedMass>? carried)
    {
        var extra = new double[m.Slices];
        foreach (var c in carried ?? []) extra[m.SliceAt(m.ToAxisFrame(c.Point).X)] += c.Mass;
        return extra;
    }

    internal static (Part Axial, Part U, Part V) Build(StressModel m, double e, double[]? extraMass = null)
    {
        int n = m.Slices;
        double dx = (m.X1 - m.X0) / n;
        var x = Enumerable.Range(0, n).Select(k => (k + 0.5) * dx).ToArray();
        var mass = m.SliceMass.Select((v, k) => v + (extraMass?[k] ?? 0)).ToArray();
        double xc = x.Zip(mass).Sum(p => p.First * p.Second) / Math.Max(mass.Sum(), 1e-30);
        // 축
        var ka = new double[n, n];
        for (int k = 0; k + 1 < n; k++)
        {
            double ea = e * 0.5 * (m.SecArea[k] + m.SecArea[k + 1]) / dx;
            ka[k, k] += ea; ka[k + 1, k + 1] += ea; ka[k, k + 1] -= ea; ka[k + 1, k] -= ea;
        }
        var axial = new Part { K = ka, M = mass.ToArray(), R = [Enumerable.Repeat(1.0, n).ToArray()] };
        // 굽힘: u 방향 처짐은 ∫u² dA = SecIv, v 방향 처짐은 SecIu
        Part Bend(double[] inertia)
        {
            var kb = new double[2 * n, 2 * n];
            for (int k = 0; k + 1 < n; k++)
            {
                double ei = e * 0.5 * (inertia[k] + inertia[k + 1]), l = dx, c = ei / (l * l * l);
                double[,] ke =
                {
                    { 12 * c, 6 * l * c, -12 * c, 6 * l * c },
                    { 6 * l * c, 4 * l * l * c, -6 * l * c, 2 * l * l * c },
                    { -12 * c, -6 * l * c, 12 * c, -6 * l * c },
                    { 6 * l * c, 2 * l * l * c, -6 * l * c, 4 * l * l * c },
                };
                int[] dof = [2 * k, 2 * k + 1, 2 * k + 2, 2 * k + 3];
                for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++) kb[dof[i], dof[j]] += ke[i, j];
            }
            var mb = new double[2 * n];
            for (int k = 0; k < n; k++) { mb[2 * k] = mass[k]; mb[2 * k + 1] = m.SliceMass[k] * dx * dx / 12; }
            var tr = new double[2 * n]; var rot = new double[2 * n];
            for (int k = 0; k < n; k++) { tr[2 * k] = 1; rot[2 * k] = x[k] - xc; rot[2 * k + 1] = 1; }
            return new Part { K = kb, M = mb, R = [tr, rot] };
        }
        return (axial, Bend(m.SecIv), Bend(m.SecIu));
    }

    /// <summary>부분계 탄성 고유진동수 [Hz] 오름차순(강체 모드 제외). 대칭화 M^-½ K M^-½ 의 Jacobi 고유값.</summary>
    internal static double[] ElasticHz(Part p)
    {
        int n = p.N;
        var a = new double[n, n];
        for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) a[i, j] = p.K[i, j] / Math.Sqrt(p.M[i] * p.M[j]);
        var ev = Dense.SymmetricEigenvalues(a);
        Array.Sort(ev);
        return ev.Skip(p.R.Length).Select(l => Math.Sqrt(Math.Max(l, 0)) / (2 * Math.PI)).ToArray();
    }

    /// <summary>재질 E [Pa] (없으면 알루미늄 70 GPa — StressModel 판 항과 같은 기본값).</summary>
    public static double YoungModulus(StressModel m) => m.Def?.Material?.E ?? 70e9;

    /// <summary>탄성 1차 고유진동수 [Hz]: 축, 굽힘 u, 굽힘 v.</summary>
    public static (double Axial, double BendU, double BendV) Frequencies(StressModel m, double[]? extraMass = null)
    {
        var (ax, u, v) = Build(m, YoungModulus(m), extraMass);
        return (ElasticHz(ax).FirstOrDefault(), ElasticHz(u).FirstOrDefault(), ElasticHz(v).FirstOrDefault());
    }

    // ---------------- 적분 ----------------

    /// <summary>
    /// 하중 함수 loads(t, F[], M[]) (슬라이스별 외력·원점 기준 모멘트, 동체 좌표)를 [t0, t1] 적분 → 출력 시각·슬라이스 가속도.
    /// </summary>
    public static (double[] Times, Vec3[][] Accel) Integrate(StressModel m, Action<double, Vec3[], Vec3[]> loads, double t0, double t1,
        double zeta = DefaultZeta, double dt = 1e-4, double outDt = 2.5e-4, CancellationToken ct = default, double[]? extraMass = null)
    {
        if (!(t1 > t0)) return ([], []);
        int n = m.Slices;
        double e = YoungModulus(m);
        var (pa, pu, pv) = Build(m, e, extraMass);
        var parts = new[] { pa, pu, pv };
        double dx = (m.X1 - m.X0) / n;
        var nodes = Enumerable.Range(0, n).Select(k => m.FromAxisFrame(m.X0 + (k + 0.5) * dx, m.SecCu[k], m.SecCv[k])).ToArray();
        var fBuf = new Vec3[n]; var mBuf = new Vec3[n];
        // 일반화 하중: 축 = F_x, u 평면 = (F_u, M_v), v 평면 = (F_v, −M_u)  — 모멘트는 절점 기준
        double[][] Generalized(double t)
        {
            loads(t, fBuf, mBuf);
            var g = new[] { new double[n], new double[2 * n], new double[2 * n] };
            for (int k = 0; k < n; k++)
            {
                var (fx, fu, fv) = m.ToAxisFrame(fBuf[k]);
                var (_, mu, mv) = m.ToAxisFrame(mBuf[k] - Vec3.Cross(nodes[k], fBuf[k]));
                g[0][k] = fx;
                g[1][2 * k] = fu; g[1][2 * k + 1] = mv;
                g[2][2 * k] = fv; g[2][2 * k + 1] = -mu;
            }
            return g;
        }
        int steps = Math.Max(1, (int)Math.Ceiling((t1 - t0) / dt));
        dt = (t1 - t0) / steps;
        int every = Math.Max(1, (int)Math.Round(outDt / dt));
        var solvers = parts.Select(p => new Newmark(p, zeta, dt)).ToArray();
        var g0 = Generalized(t0);
        for (int s = 0; s < 3; s++) solvers[s].Init(g0[s]);
        var times = new List<double>(); var acc = new List<Vec3[]>();
        void Output(double t)
        {
            var a = new Vec3[n];
            for (int k = 0; k < n; k++) a[k] = m.FromAxisFrame(solvers[0].A[k], solvers[1].A[2 * k], solvers[2].A[2 * k]);
            times.Add(t); acc.Add(a);
        }
        Output(t0);
        for (int i = 1; i <= steps; i++)
        {
            if ((i & 255) == 0) ct.ThrowIfCancellationRequested();
            double t = t0 + i * dt;
            var g = Generalized(t);
            for (int s = 0; s < 3; s++) solvers[s].Step(g[s]);
            if (i % every == 0 || i == steps) Output(t);
        }
        return (times.ToArray(), acc.ToArray());
    }

    /// <summary>Newmark 평균가속도(γ = ½, β = ¼) 부분계 적분기 — 유효 강성은 한 번만 Cholesky 분해.</summary>
    private sealed class Newmark
    {
        private readonly Part _p;
        private readonly double _dt, _beta;
        private readonly double[,] _c;
        private readonly double[,] _l;
        public double[] U, V, A;
        public double OmegaElastic { get; }

        public Newmark(Part p, double zeta, double dt)
        {
            _p = p; _dt = dt;
            int n = p.N;
            var hz = ElasticHz(p);
            double w1 = hz.Length > 0 ? 2 * Math.PI * hz[0] : 0;
            _beta = w1 > 0 ? 2 * zeta / w1 : 0;
            _c = new double[n, n];
            for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) _c[i, j] = _beta * p.K[i, j];
            var keff = new double[n, n];
            double a0 = 4 / (dt * dt), a1 = 2 / dt;
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++) keff[i, j] = p.K[i, j] + a1 * _c[i, j];
                keff[i, i] += a0 * p.M[i];
            }
            _l = Dense.Cholesky(keff);
            U = new double[n]; V = new double[n]; A = new double[n];
            OmegaElastic = w1;
        }

        /// <summary>준정적 초기 상태: 강체 가속도 a_r = R(RᵀMR)⁻¹RᵀF, 자기평형 하중 F − M a_r 의 탄성 처짐(Rᵀ M u = 0), 상대 속도 0.</summary>
        public void Init(double[] f)
        {
            int n = _p.N, r = _p.R.Length;
            var g = new double[r, r]; var rf = new double[r];
            for (int i = 0; i < r; i++)
            {
                for (int k = 0; k < n; k++) rf[i] += _p.R[i][k] * f[k];
                for (int j = 0; j < r; j++) for (int k = 0; k < n; k++) g[i, j] += _p.R[i][k] * _p.M[k] * _p.R[j][k];
            }
            var gi = Dense.Inverse(g);
            var q = new double[r];
            for (int i = 0; i < r; i++) for (int j = 0; j < r; j++) q[i] += gi[i, j] * rf[j];
            Array.Clear(A);
            for (int i = 0; i < r; i++) for (int k = 0; k < n; k++) A[k] += _p.R[i][k] * q[i];
            var ft = new double[n];
            for (int k = 0; k < n; k++) ft[k] = f[k] - _p.M[k] * A[k];
            // K_reg = K + κ·(M R) G⁻¹ (M R)ᵀ — 강체 모드 방향만 채워 정칙화(자기평형 하중에서 해는 K u = F̃ 그대로)
            double kappa = 0; for (int k = 0; k < n; k++) kappa = Math.Max(kappa, _p.K[k, k] / _p.M[k]);
            kappa = Math.Max(kappa, 1);
            var kr = (double[,])_p.K.Clone();
            for (int a = 0; a < n; a++)
                for (int b = 0; b < n; b++)
                {
                    double s = 0;
                    for (int i = 0; i < r; i++) for (int j = 0; j < r; j++) s += _p.M[a] * _p.R[i][a] * gi[i, j] * _p.R[j][b] * _p.M[b];
                    kr[a, b] += kappa * s;
                }
            U = Dense.SolveCholesky(Dense.Cholesky(kr), ft);
            Array.Clear(V);
        }

        public void Step(double[] f)
        {
            int n = _p.N;
            double dt = _dt, a0 = 4 / (dt * dt), a1 = 2 / dt, a2 = 4 / dt;
            var rhs = new double[n];
            var cv = new double[n];
            for (int i = 0; i < n; i++)
            {
                rhs[i] = f[i] + _p.M[i] * (a0 * U[i] + a2 * V[i] + A[i]);
                cv[i] = a1 * U[i] + V[i];
            }
            if (_beta > 0)
                for (int i = 0; i < n; i++)
                {
                    double s = 0;
                    for (int j = 0; j < n; j++) s += _c[i, j] * cv[j];
                    rhs[i] += s;
                }
            var u1 = Dense.SolveCholesky(_l, rhs);
            for (int i = 0; i < n; i++)
            {
                double an = a0 * (u1[i] - U[i]) - a2 * V[i] - A[i];
                V[i] += dt / 2 * (A[i] + an);
                A[i] = an; U[i] = u1[i];
            }
        }
    }

    // ---------------- World → 이력 ----------------

    /// <summary>
    /// 기록된 World 에서 물체 충격 창을 찾고(없으면 windows 지정) 창마다 보 동해석. 하중 = 창 안 기록 프레임(shockCapture 가 켜졌으면 매 스텝)의
    /// 점하중·공력 슬라이스 하중을 선형 보간.
    /// </summary>
    public static ShockHistory Solve(World w, StressModel m, IReadOnlyList<ShockWindow>? windows = null, double zeta = DefaultZeta,
        double dt = 1e-4, double outDt = 2.5e-4, CancellationToken ct = default)
    {
        var frames = w.Recorder.Frames;
        var wins = windows ?? FindWindows(w, m);
        // 고유진동수 보고: 첫 창 시작 순간의 탑재물 포함(창 없으면 자기 질량만)
        var extra0 = wins.Count > 0 ? CarriedSliceMass(m, m.CarriedAt(w, frames[w.Recorder.IndexAt(wins[0].TTrigger)])) : null;
        var (fa, fu, fv) = Frequencies(m, extra0);
        var h = new ShockHistory { Model = m, Zeta = zeta, Dt = dt, OutputDt = outDt, E = YoungModulus(m), AxialHz = fa, BendUHz = fu, BendVHz = fv };
        int n = m.Slices;
        foreach (var win in wins)
        {
            ct.ThrowIfCancellationRequested();
            var carried = m.CarriedAt(w, frames[w.Recorder.IndexAt(win.TTrigger)]);
            var extra = CarriedSliceMass(m, carried);
            int i0 = Math.Max(0, w.Recorder.IndexAt(win.T0)), i1 = Math.Min(frames.Count - 1, w.Recorder.IndexAt(win.T1) + 1);
            var ft = new List<double>(); var fl = new List<(Vec3[] F, Vec3[] M)>();
            for (int i = i0; i <= i1; i++)
            {
                var f = frames[i];
                var F = new Vec3[n]; var M = new Vec3[n];
                if (f.Bodies[m.Body.Index].Mode == BodyMode.Free) m.ExternalSliceLoads(m.InputFrom(w, f), F, M);
                ft.Add(f.T); fl.Add((F, M));
            }
            if (ft.Count == 0) continue;
            var tArr = ft.ToArray();
            void Loads(double t, Vec3[] F, Vec3[] M)
            {
                int j = Array.BinarySearch(tArr, t);
                if (j < 0) j = ~j - 1;
                j = Math.Clamp(j, 0, tArr.Length - 1);
                int j2 = Math.Min(j + 1, tArr.Length - 1);
                double u = j2 > j ? Math.Clamp((t - tArr[j]) / (tArr[j2] - tArr[j]), 0, 1) : 0;
                for (int k = 0; k < n; k++)
                {
                    F[k] = fl[j].F[k] * (1 - u) + fl[j2].F[k] * u;
                    M[k] = fl[j].M[k] * (1 - u) + fl[j2].M[k] * u;
                }
            }
            var (times, acc) = Integrate(m, Loads, win.T0, win.T1, zeta, dt, outDt, ct, extra);
            h.Windows.Add(win);
            h.Carried.Add(carried);
            h.Times.Add(times);
            h.SliceAccel.Add(acc);
            h.PeakAccelG.Add(acc.Select(a => a.Max(v => v.Norm) / StressModel.G0).ToArray());
        }
        return h;
    }
}

/// <summary>작은 밀집 행렬 도구(보 모델 ≤ 수백 자유도).</summary>
internal static class Dense
{
    public static double[,] Cholesky(double[,] a)
    {
        int n = a.GetLength(0);
        var l = new double[n, n];
        for (int j = 0; j < n; j++)
        {
            double s = a[j, j];
            for (int k = 0; k < j; k++) s -= l[j, k] * l[j, k];
            if (!(s > 0)) throw new InvalidOperationException("행렬이 양의 정부호가 아닙니다(보 모델 단면·질량 확인).");
            l[j, j] = Math.Sqrt(s);
            for (int i = j + 1; i < n; i++)
            {
                double t = a[i, j];
                for (int k = 0; k < j; k++) t -= l[i, k] * l[j, k];
                l[i, j] = t / l[j, j];
            }
        }
        return l;
    }

    public static double[] SolveCholesky(double[,] l, double[] b)
    {
        int n = b.Length;
        var y = new double[n];
        for (int i = 0; i < n; i++)
        {
            double s = b[i];
            for (int k = 0; k < i; k++) s -= l[i, k] * y[k];
            y[i] = s / l[i, i];
        }
        var x = new double[n];
        for (int i = n - 1; i >= 0; i--)
        {
            double s = y[i];
            for (int k = i + 1; k < n; k++) s -= l[k, i] * x[k];
            x[i] = s / l[i, i];
        }
        return x;
    }

    public static double[,] Inverse(double[,] a)
    {
        int n = a.GetLength(0);
        var m = (double[,])a.Clone();
        var inv = new double[n, n];
        for (int i = 0; i < n; i++) inv[i, i] = 1;
        for (int c = 0; c < n; c++)
        {
            int p = c;
            for (int r = c + 1; r < n; r++) if (Math.Abs(m[r, c]) > Math.Abs(m[p, c])) p = r;
            if (Math.Abs(m[p, c]) < 1e-300) throw new InvalidOperationException("특이 행렬");
            for (int k = 0; k < n; k++) { (m[c, k], m[p, k]) = (m[p, k], m[c, k]); (inv[c, k], inv[p, k]) = (inv[p, k], inv[c, k]); }
            double d = m[c, c];
            for (int k = 0; k < n; k++) { m[c, k] /= d; inv[c, k] /= d; }
            for (int r = 0; r < n; r++)
            {
                if (r == c) continue;
                double f = m[r, c];
                if (f == 0) continue;
                for (int k = 0; k < n; k++) { m[r, k] -= f * m[c, k]; inv[r, k] -= f * inv[c, k]; }
            }
        }
        return inv;
    }

    /// <summary>대칭 행렬 고유값(순환 Jacobi).</summary>
    public static double[] SymmetricEigenvalues(double[,] a0)
    {
        int n = a0.GetLength(0);
        var a = (double[,])a0.Clone();
        for (int sweep = 0; sweep < 100; sweep++)
        {
            double off = 0, diag = 0;
            for (int i = 0; i < n; i++) { diag += a[i, i] * a[i, i]; for (int j = i + 1; j < n; j++) off += a[i, j] * a[i, j]; }
            if (off <= 1e-24 * Math.Max(diag, 1e-300)) break;
            for (int p = 0; p < n; p++)
                for (int q = p + 1; q < n; q++)
                {
                    double apq = a[p, q];
                    if (Math.Abs(apq) < 1e-300) continue;
                    double th = (a[q, q] - a[p, p]) / (2 * apq);
                    double t = Math.Sign(th) / (Math.Abs(th) + Math.Sqrt(th * th + 1));
                    if (th == 0) t = 1;
                    double c = 1 / Math.Sqrt(t * t + 1), s = t * c;
                    for (int k = 0; k < n; k++)
                    {
                        double akp = a[k, p], akq = a[k, q];
                        a[k, p] = c * akp - s * akq; a[k, q] = s * akp + c * akq;
                    }
                    for (int k = 0; k < n; k++)
                    {
                        double apk = a[p, k], aqk = a[q, k];
                        a[p, k] = c * apk - s * aqk; a[q, k] = s * apk + c * aqk;
                    }
                }
        }
        return Enumerable.Range(0, n).Select(i => a[i, i]).ToArray();
    }
}
