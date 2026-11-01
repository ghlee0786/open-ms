using Sim.Core.Numerics;
using Sim.Core.Structures;

namespace Sim.Core.Fea;

/// <summary>해석해 대비 CalculiX 검증 사례 1개 결과(FR-FEA-06).</summary>
public sealed record CcxCheck(string Id, string Name, string Quantity, double Measured, double Reference, double Tolerance, string Source, string Note = "")
{
    public double Ratio => Measured / Reference;
    public bool Pass => double.IsFinite(Ratio) && Math.Abs(Ratio - 1) <= Tolerance;
}

/// <summary>
/// CalculiX 해석해 검증(FR-FEA-06): 외팔보 처짐·굽힘응력, 균일 압력 단순지지 정사각판(Roark), 원통 내압 후프,
/// 구멍 난 평판 Kt(Kirsch/Heywood), 단순보 고유진동수, 오일러 기둥 좌굴. 메시는 C# 구조 격자(외부 메셔 불필요).
/// 실패하면 예외(해석 실패) — 판정은 CcxCheck.Pass.
/// </summary>
public static class CcxBenchmarks
{
    /// <summary>검증용 강재(E 210 GPa, ν 0.3, ρ 7850).</summary>
    public static readonly Material Steel = new()
    {
        Key = "verif-steel", Name = "검증용 강(E 210 GPa)", Density = 7850, E = 210e9, Nu = 0.3, Yield = 250e6, Ultimate = 400e6, Elongation = 0.2,
        Source = "해석해 비교용 이상 재질",
    };

    private static CcxResult Solve(CalculixInstall inst, CcxModel m, string dir, string job)
    {
        var r = CcxRunner.Solve(inst, m, dir, job, TimeSpan.FromMinutes(5));
        if (!r.Ok) throw new InvalidOperationException($"{job}: {r.Error}");
        return r;
    }

    private static int NearestNode(FeMesh mesh, Vec3 p)
    {
        var used = mesh.UsedNodes();
        int best = -1; double bd = double.MaxValue;
        for (int i = 0; i < mesh.Nodes.Count; i++)
            if (used[i] && (mesh.Nodes[i] - p).NormSq < bd) { bd = (mesh.Nodes[i] - p).NormSq; best = i; }
        return best;
    }

    /// <summary>절점 집합에 총 힘을 고르게 나눔.</summary>
    private static void Spread(CcxModel m, IReadOnlyList<int> nodes, Vec3 total)
    {
        foreach (var n in nodes) m.NodalForces.Add((n, total / nodes.Count));
    }

    /// <summary>외팔보(1.0×0.1×0.1 m, C3D20R 20×2×2) 끝단 하중 1 kN: 끝 처짐(Timoshenko) · 중앙 윗면 굽힘응력 M·c/I.</summary>
    public static (CcxCheck Deflection, CcxCheck Stress) Cantilever(CalculixInstall inst, string dir)
    {
        double L = 1, b = 0.1, h = 0.1, P = 1000;
        var mesh = FeMesh.Beam(L, b, h, 20, 2, 2);
        var m = new CcxModel { Mesh = mesh, Material = Steel, Title = "cantilever" };
        foreach (var n in mesh.NodesWhere(p => p.X < 1e-9)) m.Fixed.Add((n, 1, 3));
        var tip = mesh.NodesWhere(p => p.X > L - 1e-9);
        Spread(m, tip, new Vec3(0, 0, -P));
        var r = Solve(inst, m, dir, "cantilever");
        var u = r.Displacement();
        double defl = -tip.Average(n => u[n + 1].Z);
        double I = b * h * h * h / 12, G = Steel.E / (2 * (1 + Steel.Nu));
        double refDefl = P * L * L * L / (3 * Steel.E * I) + P * L / (5.0 / 6 * G * b * h);
        var s = r.Stress();
        int mid = NearestNode(mesh, new Vec3(L / 2, 0, h / 2));
        double sig = s[mid + 1][0];
        double refSig = P * (L / 2) * (h / 2) / I;
        return (new CcxCheck("FEA-1a", "외팔보 끝 처짐", "δ [mm]", defl * 1e3, refDefl * 1e3, 0.03, "Euler–Bernoulli + Timoshenko 전단 PL/(κGA)"),
                new CcxCheck("FEA-1b", "외팔보 중앙 굽힘응력", "σx [MPa]", sig / 1e6, refSig / 1e6, 0.03, "σ = M·c/I, M = P·L/2"));
    }

    /// <summary>균일 압력 4변 단순지지 정사각판(0.5 m, t 5 mm, 10 kPa, S8R 16×16): 중앙 응력·처짐 (Roark 표 11.4 1a, ν 0.3: β 0.2874, α 0.0444).</summary>
    public static (CcxCheck Stress, CcxCheck Deflection) RoarkPlate(CalculixInstall inst, string dir)
    {
        double a = 0.5, t = 0.005, q = 1e4;
        var mesh = FeMesh.Quad8(16, 16, (u, v) => new Vec3(u * a, v * a, 0), FeType.S8R, t);
        var m = new CcxModel { Mesh = mesh, Material = Steel, Title = "roark plate" };
        foreach (var n in mesh.NodesWhere(p => p.X < 1e-9 || p.Y < 1e-9 || p.X > a - 1e-9 || p.Y > a - 1e-9)) m.Fixed.Add((n, 3, 3));
        int c0 = NearestNode(mesh, Vec3.Zero), c1 = NearestNode(mesh, new Vec3(a, 0, 0));
        m.Fixed.Add((c0, 1, 2)); m.Fixed.Add((c1, 2, 2));
        for (int e = 0; e < mesh.Elements.Count; e++) m.Pressures.Add((e, 0, q));
        var r = Solve(inst, m, dir, "roark");
        double w = r.Displacement().Values.Max(d => Math.Abs(d.Z));
        var center = new Vec3(a / 2, a / 2, 0);
        // 셸은 3D 로 확장된 절점(±t/2)에 응력이 나온다 → 중앙 근처 frd 절점의 최대 von Mises
        double sig = r.Frd!.Coords.Where(kv => (kv.Value - center).Norm < t).Select(kv => r.VonMises().GetValueOrDefault(kv.Key)).DefaultIfEmpty(0).Max();
        return (new CcxCheck("FEA-2a", "단순지지 정사각판 중앙 응력", "σ [MPa]", sig / 1e6, 0.2874 * q * a * a / (t * t) / 1e6, 0.05, "Roark 7판 표 11.4 1a (a/b=1, β 0.2874)"),
                new CcxCheck("FEA-2b", "단순지지 정사각판 중앙 처짐", "w [mm]", w * 1e3, 0.0444 * q * Math.Pow(a, 4) / (Steel.E * t * t * t) * 1e3, 0.05, "Roark 7판 표 11.4 1a (α 0.0444)"));
    }

    /// <summary>
    /// 양끝 열린 얇은 원통(R 0.1, L 0.4, t 2 mm) 내압 1 MPa(S8R 32×8): 중앙부 후프 응력 pR/t, 반경 팽창 pR²/(Et).
    /// 셸 압력 부호 확인 겸용 — ccx 셸 *DLOAD P 양수는 요소 법선(절점 순서 오른손) 방향으로 민다(실측):
    /// 바깥 법선 셸의 내압은 P = +p.
    /// </summary>
    public static (CcxCheck Hoop, CcxCheck Radial) CylinderHoop(CalculixInstall inst, string dir)
    {
        double R = 0.1, L = 0.4, t = 0.002, p = 1e6;
        var mesh = FeMesh.Quad8(32, 8, (u, v) => new Vec3(R * Math.Cos(2 * Math.PI * u), R * Math.Sin(2 * Math.PI * u), L * v), FeType.S8R, t);
        mesh.WeldCoincident(1e-9);
        var m = new CcxModel { Mesh = mesh, Material = Steel, Title = "cylinder hoop" };
        foreach (var n in mesh.NodesWhere(q => q.Z < 1e-9)) m.Fixed.Add((n, 3, 3));
        m.Fixed.Add((NearestNode(mesh, new Vec3(R, 0, 0)), 2, 2));
        m.Fixed.Add((NearestNode(mesh, new Vec3(0, R, 0)), 1, 1));
        for (int e = 0; e < mesh.Elements.Count; e++) m.Pressures.Add((e, 0, p));
        var r = Solve(inst, m, dir, "hoop");
        var vm = r.VonMises();
        var midIds = r.Frd!.Coords.Where(kv => Math.Abs(kv.Value.Z - L / 2) < L / 16).Select(kv => kv.Key).ToList();
        double sig = midIds.Average(id => vm.GetValueOrDefault(id));
        // 셸 결과는 3D 확장 절점(±t/2)에 있다 → frd 좌표로 중앙 단면 절점을 골라 반경 방향 성분 평균
        var u = r.Displacement();
        double ur = midIds.Where(u.ContainsKey).Average(id =>
        {
            var q = r.Frd.Coords[id]; var d = u[id];
            return (d.X * q.X + d.Y * q.Y) / Math.Sqrt(q.X * q.X + q.Y * q.Y);
        });
        return (new CcxCheck("FEA-3a", "원통 내압 후프 응력", "σθ [MPa]", sig / 1e6, p * R / t / 1e6, 0.03, "얇은 원통 σθ = pR/t"),
                new CcxCheck("FEA-3b", "원통 내압 반경 팽창(부호 포함)", "u_r [µm]", ur * 1e6, p * R * R / (Steel.E * t) * 1e6, 0.03, "u_r = pR²/(E t) (열린 끝)"));
    }

    /// <summary>
    /// 원형 구멍 평판 인장(구멍 반경 a 10 mm, 판 반폭 W 100 mm, CPS8 1/4 모델 2블록): 구멍 가장자리 σx 최대 / 공칭 응력.
    /// 기준 Kt(총단면) = (2 + (1 − d/D)³)/(1 − d/D) (Heywood, d/D 0.1 → 3.03; 무한판 Kirsch 3).
    /// </summary>
    public static CcxCheck KirschHole(CalculixInstall inst, string dir, int nr = 16, int nt = 8)
    {
        double a = 0.01, W = 0.1, t = 0.001, s0 = 10e6;
        Vec3 Map(double u, double th, Vec3 outer)
        {
            var inner = new Vec3(a * Math.Cos(th), a * Math.Sin(th), 0);
            double k = 3.0, s = (Math.Exp(k * u) - 1) / (Math.Exp(k) - 1);
            return inner + (outer - inner) * s;
        }
        var A = FeMesh.Quad8(nr, nt, (u, v) => { double th = v * Math.PI / 4; return Map(u, th, new Vec3(W, W * Math.Tan(th), 0)); }, FeType.CPS8, t);
        var B = FeMesh.Quad8(nr, nt, (u, v) => { double th = Math.PI / 4 + v * Math.PI / 4; return Map(u, th, new Vec3(W / Math.Tan(th), W, 0)); }, FeType.CPS8, t);
        int nA = A.Elements.Count;
        var mesh = A;
        mesh.Merge(B, 1e-9);
        var m = new CcxModel { Mesh = mesh, Material = Steel, Title = "kirsch hole" };
        foreach (var n in mesh.NodesWhere(p => Math.Abs(p.Y) < 1e-9)) m.Fixed.Add((n, 2, 2));
        foreach (var n in mesh.NodesWhere(p => Math.Abs(p.X) < 1e-9)) m.Fixed.Add((n, 1, 1));
        // 블록 A 의 u=1 변(x = W) = 요소 면 2 → 인장(음압)
        for (int j = 0; j < nt; j++) m.Pressures.Add((j * nr + (nr - 1), 2, -s0));
        _ = nA;
        var r = Solve(inst, m, dir, "kirsch");
        int top = NearestNode(mesh, new Vec3(0, a, 0));
        double sx = r.Stress()[top + 1][0];
        double dD = a / W, kt = (2 + Math.Pow(1 - dD, 3)) / (1 - dD);
        return new CcxCheck("FEA-4", "구멍 난 평판 응력집중 Kt", "Kt", sx / s0, kt, 0.05, "Kirsch(무한판 3) + Heywood 유한폭 보정",
            $"요소 {mesh.Elements.Count} (CPS8, 반경 방향 {nr} 등비 분할)");
    }

    /// <summary>단순지지보(1.0 m, □50 mm, C3D20R 20×2×2) 1차 굽힘 고유진동수 f₁ = (π/2)√(EI/ρA)/L².</summary>
    public static CcxCheck BeamFrequency(CalculixInstall inst, string dir)
    {
        double L = 1, b = 0.05;
        var mesh = FeMesh.Beam(L, b, b, 20, 2, 2);
        var m = new CcxModel { Mesh = mesh, Material = Steel, Analysis = CcxAnalysis.Modal, Modes = 4, Title = "ss beam modal" };
        int a0 = NearestNode(mesh, Vec3.Zero), a1 = NearestNode(mesh, new Vec3(L, 0, 0));
        m.Fixed.Add((a0, 1, 3)); m.Fixed.Add((a1, 2, 3));
        m.Fixed.Add((NearestNode(mesh, new Vec3(0, 0, b / 2)), 2, 2)); // 축 회전(비틀림 강체 모드) 막기
        var r = Solve(inst, m, dir, "ssbeam");
        double f = r.Dat.FrequenciesHz.Where(x => x > 1).DefaultIfEmpty(double.NaN).Min();
        double I = Math.Pow(b, 4) / 12, A = b * b;
        double fRef = Math.PI / 2 * Math.Sqrt(Steel.E * I / (Steel.Density * A)) / (L * L);
        return new CcxCheck("FEA-5", "단순지지보 1차 고유진동수", "f₁ [Hz]", f, fRef, 0.03, "Euler–Bernoulli f₁ = (π/2L²)√(EI/ρA)");
    }

    /// <summary>외팔 기둥(1.0 m, □50 mm, C3D20R 20×2×2) 오일러 좌굴 하중 P_cr = π²EI/(4L²).</summary>
    public static CcxCheck EulerColumn(CalculixInstall inst, string dir)
    {
        double L = 1, b = 0.05, P = 1000;
        var mesh = FeMesh.Beam(L, b, b, 20, 2, 2);
        var m = new CcxModel { Mesh = mesh, Material = Steel, Analysis = CcxAnalysis.Buckle, Modes = 2, Title = "euler column" };
        foreach (var n in mesh.NodesWhere(p => p.X < 1e-9)) m.Fixed.Add((n, 1, 3));
        Spread(m, mesh.NodesWhere(p => p.X > L - 1e-9), new Vec3(-P, 0, 0));
        var r = Solve(inst, m, dir, "euler");
        double f = r.Dat.BucklingFactors.Where(x => x > 0).DefaultIfEmpty(double.NaN).Min();
        double I = Math.Pow(b, 4) / 12;
        return new CcxCheck("FEA-6", "외팔 기둥 오일러 좌굴", "P_cr [kN]", f * P / 1e3, Math.PI * Math.PI * Steel.E * I / (4 * L * L) / 1e3, 0.03, "Euler P_cr = π²EI/(2L)²");
    }

    /// <summary>
    /// OpenCourant(양해법) 와 CalculiX(정적) 의 정적 한계 일치(FR-FEA-06): 외팔보 1.0×0.1×0.1 m 강, 끝 하중 10 kN.
    /// OpenCourant 는 1차 주기 T₁ 의 10배 램프 → 유지 구간 한 주기 평균 끝 처짐, CalculiX 는 같은 형상 C3D20R 정적.
    /// 두 솔버 모두 같은 절점 분배(끝면 절점 균등). 기준 = CalculiX, 허용 ±5%(HEPH 저차 육면체 4×4 단면).
    /// </summary>
    public static CcxCheck StaticLimitCrossCheck(CalculixInstall ccx, OpenCourantInstall oc, string dir)
    {
        double L = 1, b = 0.1, h = 0.1, P = 10e3;
        var mesh = FeMesh.Beam(L, b, h, 20, 2, 2);
        var m = new CcxModel { Mesh = mesh, Material = Steel, Title = "cantilever static limit" };
        foreach (var n in mesh.NodesWhere(p => p.X < 1e-9)) m.Fixed.Add((n, 1, 3));
        var tip = mesh.NodesWhere(p => p.X > L - 1e-9);
        Spread(m, tip, new Vec3(0, 0, -P));
        var r = Solve(ccx, m, Path.Combine(dir, "ccx"), "static");
        var u = r.Displacement();
        double dCcx = -tip.Average(n => u[n + 1].Z);
        // 1차 고유주기(Euler–Bernoulli 외팔보) → 램프 10 T₁, 유지 1.5 T₁, 마지막 1 T₁ 평균
        double I = b * h * h * h / 12, A = b * h;
        double f1 = 1.8751 * 1.8751 / (2 * Math.PI * L * L) * Math.Sqrt(Steel.E * I / (Steel.Density * A));
        double T1 = 1 / f1, ramp = 10 * T1, tEnd = ramp + 1.5 * T1, hold = ramp + 0.5 * T1;
        var deck = RadiossDeck.WriteCantileverRamp(Path.Combine(dir, "oc"), "beam", Steel, L, b, h, 40, 4, 4, P, ramp, tEnd, hold, 16);
        var res = OpenCourantRunner.Run(oc, deck, TimeSpan.FromMinutes(20));
        if (!res.Ok) throw new InvalidOperationException($"OpenCourant {res.Stage}: {res.Error}");
        var tipZ = new List<double>();
        foreach (var f in res.VtkFiles)
        {
            var v = VtkData.Parse(f);
            if (v.Displacement is not { } d || d.Length != v.Points.Count) continue;
            var zs = v.Points.Select((p, i) => (P0: p - d[i], U: d[i])).Where(x => x.P0.X > L - 1e-6).Select(x => x.U.Z).ToList();
            if (zs.Count > 0) tipZ.Add(zs.Average());
        }
        if (tipZ.Count < 4) throw new InvalidOperationException($"OpenCourant 유지 구간 프레임이 부족합니다({tipZ.Count}).");
        double dOc = -tipZ.Skip(tipZ.Count - 16).Average();
        double osc = (tipZ.Max() - tipZ.Min()) / 2 / dOc;
        return new CcxCheck("FEA-7", "OpenCourant ↔ CalculiX 정적 한계(외팔보 끝 처짐)", "δ [mm]", dOc * 1e3, dCcx * 1e3, 0.05,
            "CalculiX C3D20R 정적 = 기준", $"램프 {ramp * 1e3:F0} ms (10 T₁), 유지 구간 진동 ±{osc:P1}, OpenCourant HEPH 40×4×4, {res.Seconds:F0} s");
    }

    /// <summary>전체 실행(해석해 6종, 9개 비교값).</summary>
    public static List<CcxCheck> RunAll(CalculixInstall inst, string dir)
    {
        var l = new List<CcxCheck>();
        var (d, s) = Cantilever(inst, Path.Combine(dir, "cantilever")); l.Add(d); l.Add(s);
        var (ps, pw) = RoarkPlate(inst, Path.Combine(dir, "roark")); l.Add(ps); l.Add(pw);
        var (h, ur) = CylinderHoop(inst, Path.Combine(dir, "hoop")); l.Add(h); l.Add(ur);
        l.Add(KirschHole(inst, Path.Combine(dir, "kirsch")));
        l.Add(BeamFrequency(inst, Path.Combine(dir, "modal")));
        l.Add(EulerColumn(inst, Path.Combine(dir, "buckle")));
        return l;
    }
}
