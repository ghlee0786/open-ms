using Sim.Core.Fea;
using Sim.Core.Models;
using Sim.Core.Numerics;

namespace Sim.Core.Structures;

/// <summary>
/// 3분할 분리 후 컨테이너 튜브 외부 공력 하중 FEA(사용자 질문 2026-10-09): OpenFOAM 정상 RANS 표면 Cp(받음각별, mns.surface-flow/1)를
/// CalculiX S8R 원통 셸 요소 중심에 표본화해 압력 하중으로 건다(경험식 Cp(φ) 대신 CFD 분포 — ContainerMaterialStudy 의 셸·경계조건 그대로:
/// 양끝 격벽 = 반경·원주 구속, 뒤끝(뚜껑) 축 구속, 기준 등방 E 50 GPa·ν 0.3).
/// CFD 과소 예측 보정(validation/reports/cfd/reference_case_analysis.md 5.1): 박리 후류(바람그늘, φ &gt; 90°) Cp 를 균일하게 δ 만큼 더 낮춰
/// 적분 법선력을 K 배로 맞춘다(바람받이 정체압은 그대로 — 부착 구간은 RANS 오차가 작은 유형). 내부 압력 = 후류 평균(φ ≥ 110°)으로 배기(보수,
/// 기존 연구와 같은 가정) → 보정은 바람받이 반쪽의 순 외압 +δq 와 같다. 정적은 선형 중첩(기본 + 단위 보정 하중), 좌굴은 경우마다 실행.
/// </summary>
public static class ContainerAeroFea
{
    public sealed class Options
    {
        public required SurfaceFlowField Flow { get; init; }
        public string Group { get; init; } = "M0.50";
        public double[] Alphas { get; init; } = [15, 30, 45, 60, 75, 90];
        public double Q { get; init; } = 15728;
        public double OuterDiameter { get; init; } = 0.30;
        public double Length { get; init; } = 1.50;
        public double Thickness { get; init; } = 0.003;
        public int NTheta { get; init; } = 48;
        public int NZ { get; init; } = 40;
        /// <summary>보수 보정 K(2-D 벽 함수 편향 중앙값 ×1.7, 범위 1.4–2.0).</summary>
        public double KHigh { get; init; } = 1.7;
        /// <summary>공칭 보정의 기준 = L1 횡류(TR R-300 CD2D × η) 법선력 계수 [S = πd²/4 기준] — α 의 함수.</summary>
        public Func<double, double>? CnReference { get; init; }
        public required CalculixInstall Install { get; init; }
        public required string WorkDir { get; init; }
        public int Threads { get; init; } = 1;
        public Action<string>? Log { get; init; }
    }

    /// <summary>요소 기하(중심·면적·바깥 법선 각 θ)와 CFD Cp.</summary>
    public sealed record Mapped(Vec3[] Centroid, double[] Area, double[] Theta, double[] Cp, double CpInternal, double CnMesh, double CnCfd, double LeewardArm);

    public sealed record StaticField(double S1Max, double S3Min, double VmMax, double VmAll, double DispMaxMm);

    public sealed class AlphaResult
    {
        public double Alpha { get; init; }
        public required Mapped Map { get; init; }
        public double CnRef { get; init; }
        public double KNominal { get; init; }
        /// <summary>K 수준(이름 → K, δ, 정적 결과, 좌굴 계수).</summary>
        public List<(string Level, double K, double Delta, StaticField Field, double? Buckle)> Levels { get; } = new();
        public double Seconds { get; set; }
    }

    public static FeMesh Mesh(Options o) => ContainerMaterialStudy.CylinderMesh(o.OuterDiameter / 2 - o.Thickness / 2, o.Length, o.Thickness, o.NTheta, o.NZ);

    /// <summary>
    /// CFD Cp → 요소. 셸 z ∈ [0, L] ↔ CFD 동체 x ∈ [x0(뒤·뚜껑), x1(앞·도어)] 선형 대응, 셸 θ(바깥 법선 = (cosθ, sinθ, 0)) 의 바람받이 = θ = π
    /// (바람 +x 방향, ContainerMaterialStudy 와 같음) ↔ CFD 횡류 방향 +z 에서 잰 각 φ = θ − π.
    /// </summary>
    public static Mapped Map(FeMesh mesh, SurfaceFlowField ff, string group, double alphaDeg, double length, double q)
    {
        int n = mesh.Elements.Count;
        var c = new Vec3[n]; var area = new double[n]; var th = new double[n];
        var pts = new Vec3[n];
        double r = ff.Radius;
        for (int e = 0; e < n; e++)
        {
            var en = mesh.Elements[e].N;
            var p0 = mesh.Nodes[en[0]]; var p1 = mesh.Nodes[en[1]]; var p2 = mesh.Nodes[en[2]]; var p3 = mesh.Nodes[en[3]];
            c[e] = (p0 + p1 + p2 + p3) / 4;
            area[e] = 0.5 * Vec3.Cross(p2 - p0, p3 - p1).Norm;
            th[e] = Math.Atan2(c[e].Y, c[e].X);
            double phi = th[e] - Math.PI;
            double x = ff.X0 + Math.Clamp(c[e].Z / length, 0, 1) * (ff.X1 - ff.X0);
            pts[e] = new Vec3(x, r * Math.Sin(phi), r * Math.Cos(phi));
        }
        var cp = ff.Sample(pts, alphaDeg, Vec3.UnitZ, "cp", group);
        // 내부(배기) = 후류 평균 φ ≥ 110°
        double sw = 0, sa = 0;
        for (int e = 0; e < n; e++)
        {
            double phi = Math.Abs(Wrap(th[e] - Math.PI));
            if (phi >= 110 * Math.PI / 180 && double.IsFinite(cp[e])) { sw += cp[e] * area[e]; sa += area[e]; }
        }
        double cpi = sa > 0 ? sw / sa : 0;
        // 법선력(바람 방향 +x): F = Σ p_in·A·(−cosθ), p_in = q(Cp − Cp_int)
        double fx = 0, arm = 0;
        for (int e = 0; e < n; e++)
        {
            if (!double.IsFinite(cp[e])) continue;
            fx += q * (cp[e] - cpi) * area[e] * -Math.Cos(th[e]);
            if (Math.Cos(th[e]) < 0) arm += q * area[e] * -Math.Cos(th[e]);   // 바람받이 반쪽 단위 δ 의 법선력
        }
        double s = Math.PI * Math.Pow(ff.Radius, 2);
        double cnCfd = Math.Abs(ff.Coefficient("CZ", alphaDeg, group));
        return new Mapped(c, area, th, cp, cpi, fx / (q * s), cnCfd, arm);
    }

    private static double Wrap(double a)
    {
        while (a > Math.PI) a -= 2 * Math.PI;
        while (a < -Math.PI) a += 2 * Math.PI;
        return a;
    }


    private static CcxModel Model(FeMesh mesh, double length, Material mat, CcxAnalysis an)
    {
        var m = new CcxModel { Mesh = mesh, Material = mat, Analysis = an, Modes = 4, Title = "container tube CFD Cp" };
        foreach (var nd in mesh.NodesWhere(p => p.Z < 1e-9 || p.Z > length - 1e-9)) m.Fixed.Add((nd, 1, 2));
        foreach (var nd in mesh.NodesWhere(p => p.Z < 1e-9)) m.Fixed.Add((nd, 3, 3));
        return m;
    }

    public static readonly Material RefMat = new()
    {
        Key = "ref-qi", Name = "기준 등방(E 50 GPa, ν 0.3)", Density = 1550, E = 50e9, Nu = 0.3, Yield = 600e6, Ultimate = 600e6,
        Source = "선형 FEA 기준(응력은 E 무관, 좌굴은 E 비례 환산) — ContainerMaterialStudy 와 같음",
    };

    /// <summary>요소 압력 하중: 기본(CFD) 또는 단위 보정(바람받이 반쪽 +q·1).</summary>
    private static void AddPressure(CcxModel m, Mapped map, double q, double delta)
    {
        for (int e = 0; e < map.Cp.Length; e++)
        {
            double pin = double.IsFinite(map.Cp[e]) ? q * (map.Cp[e] - map.CpInternal) : 0;
            if (Math.Cos(map.Theta[e]) < 0) pin += q * delta;
            if (pin != 0) m.Pressures.Add((e, 0, -pin));   // + = 바깥 법선 방향 → 누름(내향)은 음수
        }
    }

    private static Dictionary<int, double[]> StaticStress(CalculixInstall inst, CcxModel m, string dir, string job, int threads, out CcxResult rs)
    {
        rs = CcxRunner.Solve(inst, m, dir, job, TimeSpan.FromMinutes(10), threads: threads);
        if (!rs.Ok) throw new InvalidOperationException($"CalculiX 정적 실패({job}): {rs.Error}");
        return rs.Stress();
    }

    /// <summary>정적 결과 장(선형 결합 σ = σa + c·σb): 내부 구간(끝 5 % 제외) 최대 주응력·최소 주응력·von Mises, 전체 von Mises, 최대 변위.</summary>
    public static StaticField Combine(CcxResult ra, Dictionary<int, double[]> a, CcxResult? rb, Dictionary<int, double[]>? b, double c, double length)
    {
        double s1 = 0, s3 = 0, vm = 0, vmAll = 0;
        foreach (var (id, sa) in a)
        {
            var s = (double[])sa.Clone();
            if (b is not null && b.TryGetValue(id, out var sb)) for (int i = 0; i < 6; i++) s[i] += c * sb[i];
            double v = Tensor.VonMises(s);
            vmAll = Math.Max(vmAll, v);
            if (ra.Frd is null || !ra.Frd.Coords.TryGetValue(id, out var p)) continue;
            if (p.Z < 0.05 * length || p.Z > 0.95 * length) continue;
            var pr = Tensor.Principal(s);
            s1 = Math.Max(s1, pr[0]); s3 = Math.Min(s3, pr[^1]); vm = Math.Max(vm, v);
        }
        var da = ra.Displacement(); var db = rb?.Displacement();
        double dmax = 0;
        foreach (var (id, d) in da)
        {
            var v = d;
            if (db is not null && db.TryGetValue(id, out var d2)) v += d2 * c;
            dmax = Math.Max(dmax, v.Norm);
        }
        return new StaticField(s1, s3, vm, vmAll, dmax * 1e3);
    }

    /// <summary>받음각마다: 메시 Cp 매핑·적분 대조 → 정적 2회(CFD 기본, 단위 보정) → K 수준 중첩 → 좌굴(buckleAlphas 의 받음각만, K 수준마다).</summary>
    public static List<AlphaResult> Run(Options o, IReadOnlyCollection<double> buckleAlphas)
    {
        var mesh = Mesh(o);
        var list = new List<AlphaResult>();
        foreach (var a in o.Alphas)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var map = Map(mesh, o.Flow, o.Group, a, o.Length, o.Q);
            double cnRef = o.CnReference?.Invoke(a) ?? double.NaN;
            double kNom = double.IsFinite(cnRef) && map.CnMesh > 1e-6 ? Math.Max(1, cnRef / map.CnMesh) : 1;
            var res = new AlphaResult { Alpha = a, Map = map, CnRef = cnRef, KNominal = kNom };
            string dir = Path.Combine(o.WorkDir, $"a{a:000}");
            o.Log?.Invoke($"CFD Cp 셸 α {a:0}°: CN 메시 {map.CnMesh:F3} / CFD {map.CnCfd:F3} / L1 {cnRef:F3} → K 공칭 {kNom:F2}");
            var mA = Model(mesh, o.Length, RefMat, CcxAnalysis.Static); AddPressure(mA, map, o.Q, 0);
            var sa = StaticStress(o.Install, mA, Path.Combine(dir, "base"), "base", o.Threads, out var ra);
            var mB = Model(mesh, o.Length, RefMat, CcxAnalysis.Static);
            for (int e = 0; e < map.Cp.Length; e++) if (Math.Cos(map.Theta[e]) < 0) mB.Pressures.Add((e, 0, -o.Q));
            var sb = StaticStress(o.Install, mB, Path.Combine(dir, "unit"), "unit", o.Threads, out var rb);
            foreach (var (lvl, k) in new[] { ("CFD 그대로", 1.0), ("공칭(L1 적분력)", kNom), ($"보수(×{o.KHigh:0.0#})", o.KHigh) })
            {
                double delta = map.LeewardArm > 0 ? (k - 1) * map.CnMesh * o.Q * Math.PI * Math.Pow(o.Flow.Radius, 2) / map.LeewardArm : 0;
                var field = Combine(ra, sa, rb, sb, delta, o.Length);
                double? lam = null;
                if (buckleAlphas.Contains(a))
                {
                    var mk = Model(mesh, o.Length, RefMat, CcxAnalysis.Buckle); AddPressure(mk, map, o.Q, delta);
                    var rk = CcxRunner.Solve(o.Install, mk, Path.Combine(dir, $"bk{k:0.00}"), "bk", TimeSpan.FromMinutes(10), threads: 1);
                    lam = rk.Ok ? rk.Dat.BucklingFactors.Where(x => x > 0).DefaultIfEmpty(double.NaN).Min() : null;
                    if (!rk.Ok) o.Log?.Invoke($"좌굴 실패 α {a}° K {k:F2}: {rk.Error}");
                }
                res.Levels.Add((lvl, k, delta, field, lam));
            }
            res.Seconds = sw.Elapsed.TotalSeconds;
            list.Add(res);
        }
        return list;
    }

    /// <summary>조합 하중 케이스(공력 + 관성): α·K 공력 압력 + 균일 관성 가속도(튜브 + 탑재물 질량을 셸에 고르게 — 밀도 환산) + 원심(횡축 회전).</summary>
    public sealed record ComboCase(string Id, string Label, double Alpha, double K, Vec3 InertialAccel, double OmegaTransverse, double TotalMass, string Basis);

    public sealed record ComboResult(ComboCase Case, StaticField Field, double? Buckle, double Seconds, string Note);

    public static ComboResult RunCombo(Options o, ComboCase cc, double xCgFromAft)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var mesh = Mesh(o);
        var map = Map(mesh, o.Flow, o.Group, cc.Alpha, o.Length, o.Q);
        double delta = map.LeewardArm > 0 ? (cc.K - 1) * map.CnMesh * o.Q * Math.PI * Math.Pow(o.Flow.Radius, 2) / map.LeewardArm : 0;
        // 밀도 환산: FE 셸 질량 = 컨테이너 합성 질량(탑재물 포함, 고르게 분포 — 근사)
        double area = 2 * Math.PI * (o.OuterDiameter / 2 - o.Thickness / 2) * o.Length;
        var mat = new Material
        {
            Key = RefMat.Key, Name = RefMat.Name + " · 밀도 환산", Density = cc.TotalMass / (area * o.Thickness), E = RefMat.E, Nu = RefMat.Nu,
            Yield = RefMat.Yield, Ultimate = RefMat.Ultimate, Source = RefMat.Source,
        };
        string dir = Path.Combine(o.WorkDir, "combo_" + cc.Id);
        var ms = Model(mesh, o.Length, mat, CcxAnalysis.Static);
        AddPressure(ms, map, o.Q, delta);
        ms.BodyAccel = cc.InertialAccel;
        if (cc.OmegaTransverse > 0) ms.Centrif = (new Vec3(0, cc.OmegaTransverse, 0), new Vec3(0, 0, xCgFromAft));
        var rs = CcxRunner.Solve(o.Install, ms, Path.Combine(dir, "static"), "combo", TimeSpan.FromMinutes(10), threads: o.Threads);
        if (!rs.Ok) return new ComboResult(cc, new StaticField(double.NaN, double.NaN, double.NaN, double.NaN, double.NaN), null, sw.Elapsed.TotalSeconds, rs.Error);
        var field = Combine(rs, rs.Stress(), null, null, 0, o.Length);
        var mb = Model(mesh, o.Length, mat, CcxAnalysis.Buckle);
        AddPressure(mb, map, o.Q, delta);
        mb.BodyAccel = cc.InertialAccel;
        if (cc.OmegaTransverse > 0) mb.Centrif = (new Vec3(0, cc.OmegaTransverse, 0), new Vec3(0, 0, xCgFromAft));
        var rb = CcxRunner.Solve(o.Install, mb, Path.Combine(dir, "buckle"), "combobk", TimeSpan.FromMinutes(10), threads: 1);
        double? lam = rb.Ok ? rb.Dat.BucklingFactors.Where(x => x > 0).DefaultIfEmpty(double.NaN).Min() : null;
        return new ComboResult(cc, field, lam, sw.Elapsed.TotalSeconds, rb.Ok ? "" : $"좌굴 실패: {rb.Error}");
    }

    /// <summary>셸 고유진동(튜브 단독 질량, 양끝 격벽 구속) — 와류 흘림 진동수 대비 동적 증폭용.</summary>
    public static List<double> Modal(Options o)
    {
        var mesh = Mesh(o);
        var m = Model(mesh, o.Length, RefMat, CcxAnalysis.Modal);
        m.Modes = 6;
        var r = CcxRunner.Solve(o.Install, m, Path.Combine(o.WorkDir, "modal"), "modal", TimeSpan.FromMinutes(10), threads: 1);
        if (!r.Ok) throw new InvalidOperationException($"CalculiX 고유진동 실패: {r.Error}");
        return r.Dat.FrequenciesHz.Where(f => f > 1).ToList();
    }

    /// <summary>
    /// 와류 흘림 동적 하중 계수(비정상 해석 없이 공개 범위로): 변동 양력 진폭 √2·C'_L·q·D 가 셸 1차 모드 증폭 H(r) = 1/√((1−r²)² + (2ζr)²)
    /// (r = f_s/f_n, f_s = St·V/D) 로 커질 때 평균 횡류 하중 대비 최대 합력 비 = √(1 + (√2 C'_L H / C_D)²) + √2·(C'_L/5)·H / C_D
    /// (변동 항력 ≈ 변동 양력의 1/5 — 가정).
    /// </summary>
    public static double SheddingFactor(double clRms, double cdMean, double fs, double fn, double zeta = 0.02)
    {
        double r = fs / fn;
        double h = 1 / Math.Sqrt(Math.Pow(1 - r * r, 2) + Math.Pow(2 * zeta * r, 2));
        double l = Math.Sqrt(2) * clRms * h / cdMean, d = Math.Sqrt(2) * clRms / 5 * h / cdMean;
        return Math.Sqrt(1 + l * l) + d;
    }
}
