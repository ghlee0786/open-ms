using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Sim.Core.Environment;
using Sim.Core.Fea;
using Sim.Core.Models;
using Sim.Core.Numerics;
using Sim.Core.Scenario;

namespace Sim.Core.Structures;

/// <summary>컨테이너 튜브 재질 연구 옵션.</summary>
public sealed class ContainerMaterialOptions
{
    public required string OutDir { get; init; }
    /// <summary>튜브 바깥 지름·길이(링·격벽 사이 좌굴 길이) [m] — 기본값 = 시나리오 09 컨테이너.</summary>
    public double OuterDiameter { get; init; } = 0.30;
    public double Length { get; init; } = 1.50;
    /// <summary>현재 설계 튜브 두께·질량(CFRP 튜브 — CAD 부피 × 1,430 kg/m³).</summary>
    public double BaselineThickness { get; init; } = 0.003;
    public double BaselineTubeMass { get; init; } = 6.0;
    /// <summary>탑재 질량(컨테이너 + 내용물) [kg]·내용물 질량 — 시나리오에서 읽으면 덮어쓴다.</summary>
    public double PayloadMass { get; init; } = 25.0;
    public double ContentsMass { get; init; } = 15.0;
    public double MassLimit { get; init; } = 50.0;
    public double Altitude { get; init; } = 1000;
    public double Mach { get; init; } = 0.5;
    /// <summary>설계 하중(캐니스터 설계 하중) [G].</summary>
    public double DesignG { get; init; } = 50;
    /// <summary>낙하산 개방 G(포락선 없을 때 — PRD P2 드로그 개방충격 15–20 G 상한).</summary>
    public double OpeningG { get; init; } = 20;
    /// <summary>드로그 전개 순간 받음각(개방 하중의 횡성분) [deg].</summary>
    public double OpeningAoaDeg { get; init; } = 45;
    /// <summary>tumble-study 하중 포락선(mns.tumble-envelope/1). 있으면 개방 G·받음각·횡류 동압 확인에 쓴다.</summary>
    public JsonObject? Envelope { get; init; }
    public string? EnvelopePath { get; init; }
    public double[] Thicknesses { get; init; } = [0.0005, 0.00075, 0.001, 0.00125, 0.0015, 0.00175, 0.002, 0.0025, 0.003, 0.004, 0.005];
    public string[] Materials { get; init; } = ["cfrp-qi", "gfrp-qi", "al7075-t6", "al6061-t6", "ti6al4v", "steel-4130"];
    public bool Ccx { get; init; }
    public string? CcxInstall { get; init; }
    /// <summary>CalculiX 작업 폴더(덱·결과) — 기본 results/container_material_work(git 제외).</summary>
    public string? WorkDir { get; init; }
    /// <summary>셸 메시 원주 × 길이 요소 수(S8R).</summary>
    public int MeshTheta { get; init; } = 48;
    public int MeshLength { get; init; } = 24;
    /// <summary>두께별 CalculiX 동시 실행 수(다른 계산과 CPU 를 나눠 쓸 때 1).</summary>
    public int CcxJobs { get; init; } = Math.Max(1, System.Environment.ProcessorCount / 2);
    public Action<string>? Log { get; init; }
    public string Command { get; init; } = "";
}

/// <summary>
/// M 0.5 컨테이너 튜브 재질·두께 연구(사용자 질문 2026-10-08): 정상 비행 · 텀블링 횡류(옆으로 선 자세) · 분리 충격(설계 50 G) ·
/// 낙하산 개방 하중에서 얇은 원통 튜브가 항복·파손·좌굴 없이 견디는 재질·최소 두께와 질량.
/// 해석식: 막응력·보 굽힘, NASA SP-8007 축압축·굽힘·외압 좌굴(녹다운 계수) + 선형 상호작용, 횡류 Cp(θ)(퍼텐셜 + 음속 상한 + TR R-300 CD 로 보정한 기저압).
/// 선택: CalculiX S8R 셸 — 횡류 압력 분포 정적(타원화 + 보 굽힘)·좌굴(*BUCKLE), 균일 외압 좌굴 고전식 대조(검증).
/// </summary>
public static class ContainerMaterialStudy
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>안전계수(PRD 수치 기준 없음 → NASA-STD-5001B 관례값, 결정 기록): 항복 1.25 · 극한 1.5 · 좌굴 1.5(녹다운 적용 후).</summary>
    public const double SfYield = 1.25, SfUltimate = 1.5, SfBuckling = 1.5;
    /// <summary>NASA SP-8007 외압(횡압) 좌굴 녹다운.</summary>
    public const double GammaPressure = 0.56;

    // ---------------- 재질 후보 ----------------

    /// <summary>후보 재질(설계 허용값). 복합재: 항복 없음, Ftu 인장·Fcu 압축(준등방 첫 ply 파손 근사).</summary>
    public sealed record Candidate(string Key, string Name, double E, double Nu, double Density, double Fty, double Ftu, double Fcu, bool Composite, string Basis);

    public static Candidate CandidateOf(string key)
    {
        var m = MaterialLibrary.Find(key) ?? throw new InvalidDataException($"재질 '{key}' 이 라이브러리에 없습니다(mns materials).");
        bool comp = m.Class == "composite";
        // 준등방 적층 압축 강도 ≈ 인장의 0.75(일방향 Xc/Xt ≈ 0.6 — 준등방은 ±45 층이 분담, 가정) — 금속은 Fcy ≈ Fty
        double fcu = comp ? 0.75 * m.Ultimate : m.Yield;
        string basis = comp
            ? $"{m.Source} · 준등방 [{string.Join("/", (m.Layup ?? []).Take(4).Select(x => x.ToString("0", Inv)))}]s 등가 등방 셸(E {m.E / 1e9:F0} GPa) · 압축 허용 = 0.75 × 인장(가정)"
            : m.Source;
        return new Candidate(m.Key, m.Name, m.E, m.Nu, m.Density, comp ? m.Ultimate : m.Yield, m.Ultimate, fcu, comp, basis);
    }

    // ---------------- 비행 조건 ----------------

    public sealed record Flight(double Altitude, double Mach, double T, double P, double Rho, double A, double V, double Q, double Mu, double ReD,
        double Cd2D, double Eta, double CdCross, double Cp0, double CpStar, double CpBase, double C2, double CpMin);

    /// <summary>음속 Cp*(국부 마하 1) — 등엔트로피.</summary>
    public static double SonicCp(double m, double g = 1.4) =>
        2 / (g * m * m) * (Math.Pow((2 + (g - 1) * m * m) / (g + 1), g / (g - 1)) - 1);

    /// <summary>압축성 정체점 Cp0.</summary>
    public static double StagnationCp(double m, double g = 1.4) =>
        2 / (g * m * m) * (Math.Pow(1 + (g - 1) / 2 * m * m, g / (g - 1)) - 1);

    /// <summary>
    /// 횡류 원통 표면 Cp(φ), φ = 정체점에서 잰 각[rad, 0..π]: 부착 구간 Cp0 − 4 sin²φ(퍼텐셜 모양, 압축성 정체점), 음속 Cp* 하한 → φ ≥ 90° 에서
    /// 박리점 φs(110°)까지 선형 회복 → 기저압 Cp_b(뒤쪽 균일). Cp_b 는 적분 CD 가 TR R-300 CD2D(Re, M) 와 같도록 보정.
    /// </summary>
    public static double CrossflowCp(double phi, double cp0, double cpStar, double cpBase, double phiSep = 110 * Math.PI / 180)
    {
        phi = Math.Abs(phi) % (2 * Math.PI);
        if (phi > Math.PI) phi = 2 * Math.PI - phi;
        double s = Math.Sin(Math.Min(phi, Math.PI / 2));
        double att = Math.Max(cp0 - 4 * s * s, cpStar);
        if (phi <= Math.PI / 2) return att;
        if (phi >= phiSep) return cpBase;
        double f = (phi - Math.PI / 2) / (phiSep - Math.PI / 2);
        return att + (cpBase - att) * f;
    }

    /// <summary>Cp 분포의 항력 계수 CD = ½∮Cp cosφ dφ, cos2φ 푸리에 계수 c2 = (1/π)∮Cp cos2φ dφ.</summary>
    public static (double Cd, double C2) Integrate(Func<double, double> cp, int n = 3600)
    {
        double cd = 0, c2 = 0, h = 2 * Math.PI / n;
        for (int i = 0; i < n; i++)
        {
            double p = (i + 0.5) * h, c = cp(p);
            cd += 0.5 * c * Math.Cos(p) * h;   // 항력 = ∮ −p n·e_V ds, n·e_V = −cos φ
            c2 += c * Math.Cos(2 * p) * h / Math.PI;
        }
        return (cd, c2);
    }

    public static Flight FlightAt(double altitude, double mach, double diameter, double length)
    {
        var atm = new Atmosphere().At(altitude);
        double v = mach * atm.SpeedOfSound;
        double q = 0.5 * Atmosphere.Gamma * atm.Pressure * mach * mach;   // = ½ρV²
        double re = atm.Density * v * diameter / atm.Viscosity;
        double cd2 = CylinderCrossflow.Cd2D(re, mach);
        double eta = CylinderCrossflow.Eta(length / diameter, out _);
        double cp0 = StagnationCp(mach), cps = SonicCp(mach);
        // Cp_b 보정(이분법): 적분 CD = CD2D
        double lo = -3.5, hi = 0.5;
        for (int i = 0; i < 80; i++)
        {
            double mid = 0.5 * (lo + hi);
            double cd = Integrate(p => CrossflowCp(p, cp0, cps, mid)).Cd;
            if (cd > cd2) lo = mid; else hi = mid;
        }
        double cpb = 0.5 * (lo + hi);
        double c2 = Integrate(p => CrossflowCp(p, cp0, cps, cpb)).C2;
        return new Flight(altitude, mach, atm.Temperature, atm.Pressure, atm.Density, atm.SpeedOfSound, v, q, atm.Viscosity, re, cd2, eta, eta * cd2, cp0, cps, cpb, c2, Math.Min(cps, cp0 - 4));
    }

    // ---------------- 얇은 원통 해석식 ----------------

    /// <summary>NASA SP-8007 축압축 녹다운 γ = 1 − 0.901(1 − e^−φ), φ = √(R/t)/16.</summary>
    public static double GammaAxial(double r, double t) => 1 - 0.901 * (1 - Math.Exp(-Math.Sqrt(r / t) / 16));
    /// <summary>NASA SP-8007 굽힘 녹다운 γ = 1 − 0.731(1 − e^−φ).</summary>
    public static double GammaBending(double r, double t) => 1 - 0.731 * (1 - Math.Exp(-Math.Sqrt(r / t) / 16));
    /// <summary>고전 축압축 좌굴 응력 E t / (R √(3(1−ν²))).</summary>
    public static double ClassicalAxial(double e, double nu, double r, double t) => e * t / (r * Math.Sqrt(3 * (1 - nu * nu)));

    /// <summary>
    /// 고전 외압(횡압, 양끝 단순지지) 좌굴 압력: 중간 길이 Windenburg–Trilling 근사 p = 0.855 E (t/R)^2.5 / ((1−ν²)^¾ L/R) 와
    /// 긴 원통 p = E/(4(1−ν²)) (t/R)³ 중 큰 값(긴 원통 하한).
    /// </summary>
    public static double ClassicalExternalPressure(double e, double nu, double r, double t, double l)
    {
        double mid = 0.855 * e * Math.Pow(t / r, 2.5) / (Math.Pow(1 - nu * nu, 0.75) * (l / r));
        double lng = e / (4 * (1 - nu * nu)) * Math.Pow(t / r, 3);
        return Math.Max(mid, lng);
    }

    /// <summary>
    /// 고전 외압(횡압만, 양끝 단순지지 v = w = 0) 좌굴 — Timoshenko & Gere(1961) 11.5 식(축 반파 1개, 원주 파수 n 최소화):
    /// p R/(E t) = λ⁴/((n²+λ²)²(n²−1+λ²/2)) + t²/(12R²(1−ν²))·((n²+λ²)² − 2n² + 1)/(n²−1+λ²/2), λ = πR/L.
    /// </summary>
    public static (double P, int N) ExternalPressureTimoshenko(double e, double nu, double r, double t, double l)
    {
        double lam = Math.PI * r / l, best = double.PositiveInfinity; int bn = 2;
        for (int n = 2; n <= 40; n++)
        {
            double den = n * n - 1 + lam * lam / 2, a = n * n + lam * lam;
            double k = Math.Pow(lam, 4) / (a * a * den) + t * t / (12 * r * r * (1 - nu * nu)) * (a * a - 2 * n * n + 1) / den;
            double p = e * t / r * k;
            if (p < best) { best = p; bn = n; }
        }
        return (best, bn);
    }

    public static double TubeMass(double density, double outerD, double t, double l) => density * Math.PI * (outerD - t) * t * l;

    // ---------------- 하중 케이스 ----------------

    /// <summary>하중 케이스(튜브 단면 합력 + 면 압력). 응력 = 해석식 + (선택) FEA 배율.</summary>
    public sealed record LoadCase(string Id, string Name, double AxialForce, double BendingMoment, double ExternalPressure, bool Crossflow, string Basis);

    public static List<LoadCase> LoadCases(Flight f, ContainerMaterialOptions o, double payload, double contents, double openG, double openAoa)
    {
        double r = o.OuterDiameter / 2, l = o.Length, g = Atmosphere.G0;
        double aEnd = Math.PI * r * r;
        // 정상: 앞면(도어) 정체 + 기저 → 축 항력 CdA ≈ 0.82·A(평두 원통 L/D≈5, Hoerner) — 감속 관성은 내용물이 도어를 미는 압축(최대 = 항력 전체)
        double fAx = 0.82 * f.Q * aEnd;
        double w = f.Q * f.CdCross * o.OuterDiameter;          // 횡류 분포하중 [N/m]
        double fLat40 = payload * o.DesignG * g;
        double fOpen = openG * payload * g;
        double fOpenLat = fOpen * Math.Sin(Math.Clamp(openAoa, 0, 90) * Math.PI / 180);
        return
        [
            new("LC1", "정상 비행(α_t ≈ 0, M 0.5)", -fAx, 0, 0, false,
                $"축 항력 0.82·q·πR² = {fAx:F0} N 압축(감속 관성은 내용물이 도어 쪽을 미는 것으로 전부 튜브 통과 — 보수)"),
            new("LC2", "텀블링 횡류(옆으로 선 자세 α_t = 90°, M 0.5)", 0, w * l * l / 8, f.Q * (f.Cp0 - f.CpBase), true,
                $"횡류 w = q·η·CD2D·D = {w:F0} N/m, 양끝(격벽) 지지 보 M = wL²/8 · 바람받이 순 외압 q(Cp0 − Cp_b)(내부 = 후류 기저압 배기 가정) · 셸 타원화는 FEA"),
            new("LC3", $"분리 충격 횡 {o.DesignG:F0} G(설계 하중)", 0, fLat40 / l * l * l / 8, 0, false,
                $"탑재 {payload:F2} kg × {o.DesignG:F0} G 균일 분포, 양끝 반력(탄체 지지 링) M = wL²/8"),
            new("LC4", $"분리 충격 축 {o.DesignG:F0} G(설계 하중)", -contents * o.DesignG * g, 0, 0, false,
                $"내용물 {contents:F2} kg × {o.DesignG:F0} G 가 한쪽 끝 링으로 튜브 압축"),
            new("LC5", $"낙하산 개방 {openG:F1} G (α {openAoa:F0}°)", fOpen * Math.Cos(Math.Clamp(openAoa, 0, 90) * Math.PI / 180), 4.0 / 27 * fOpenLat * l, 0, false,
                $"라이저 힘 = {openG:F1} G × {payload:F2} kg 뚜껑 쪽 끝 — 축 인장 + 횡성분의 자유-자유 보(관성 완화) 최대 굽힘 4/27·F·L"),
        ];
    }

    // ---------------- 평가 ----------------

    public sealed record CaseResult(string Lc, double SigmaMax, double SigmaMin, double VonMises, double StrengthSf, double BucklingRatio, string Governing, string Source);

    public sealed record Eval(Candidate Mat, double T, double Mass, List<CaseResult> Cases, double MinStrengthSf, double MaxBucklingRatio, bool Pass, string Governing,
        double SigmaCrAxial, double SigmaCrBending, double PCrAnalytic, double? PCrFea);

    /// <summary>FEA 횡류(LC2) 결과(두께별, 기준 동압 q_ref·기준 재질): 최대/최소 주응력·von Mises(내부 구간), 좌굴 계수.</summary>
    public sealed record FeaCross(double T, double QRef, double ERef, double S1Max, double S3Min, double VmMax, double VmMaxAll, double Buckle, double MaxDispMm, int Elements, double Seconds, string Note);

    /// <param name="feaBucklingCorrection">FEA 좌굴 계수 보정 = 고전 해(Timoshenko–Gere 단순지지) / FEA 균일 외압 결과(≤ 1) — 끝 구속이 단순지지보다 강하게 들어간 만큼 낮춘다.</param>
    public static Eval Evaluate(Candidate c, double t, Flight f, List<LoadCase> lcs, ContainerMaterialOptions o, FeaCross? fea, double feaBucklingCorrection = 1)
    {
        double r = o.OuterDiameter / 2 - t / 2;   // 중립면 반경
        double l = o.Length;
        double area = 2 * Math.PI * r * t, zSec = Math.PI * r * r * t;
        double scrA = GammaAxial(r, t) * ClassicalAxial(c.E, c.Nu, r, t);
        double scrB = GammaBending(r, t) * ClassicalAxial(c.E, c.Nu, r, t);
        if (!c.Composite) { scrA = Math.Min(scrA, c.Fty); scrB = Math.Min(scrB, c.Fty); }   // 소성 영역 상한(간이 — Fcy ≈ Fty)
        double pcrA = GammaPressure * ClassicalExternalPressure(c.E, c.Nu, r, t, l);
        double? pcrF = fea is { Buckle: > 0 } ? GammaPressure * Math.Min(1, feaBucklingCorrection) * fea.Buckle * (c.E / fea.ERef) * fea.QRef : null;   // 기준 하중 q_ref·(Cp−Cp_b) 의 배수 → 동압[Pa]
        var res = new List<CaseResult>();
        foreach (var lc in lcs)
        {
            double sAx = lc.AxialForce / area, sB = lc.BendingMoment / zSec;
            double s1 = sAx + sB, s3 = sAx - sB, hoop = 0;
            string src = "해석식";
            if (lc.Crossflow)
            {
                // 링 막응력(바람받이 순 외압): σθ = −p R / t
                hoop = -lc.ExternalPressure * r / t;
                if (fea is not null)
                {
                    double k = f.Q / fea.QRef;
                    s1 = Math.Max(s1, fea.S1Max * k); s3 = Math.Min(s3, fea.S3Min * k);
                    src = "FEA(S8R 셸: 타원화 + 보 굽힘)";
                }
                else
                {
                    // 해석 대체: 자유 링 타원화 상한 σ = 2 q c2 R²/t² (격벽 지지 효과 무시 — 매우 보수)
                    double ring = 2 * f.Q * Math.Abs(f.C2) * r * r / (t * t);
                    s1 = Math.Max(s1, ring); s3 = Math.Min(s3, -ring);
                    src = "해석식(자유 링 타원화 상한)";
                }
            }
            double vm = Math.Sqrt(s1 * s1 - s1 * hoop + hoop * hoop);
            vm = Math.Max(vm, Math.Sqrt(s3 * s3 - s3 * hoop + hoop * hoop));
            if (lc.Crossflow && fea is not null) vm = Math.Max(vm, fea.VmMax * f.Q / fea.QRef);
            double sf; string gov;
            if (c.Composite)
            {
                double ft = Math.Max(s1, 0), fc = Math.Max(-Math.Min(s3, hoop), 0);
                double sft = ft > 0 ? c.Ftu / ft : double.PositiveInfinity, sfc = fc > 0 ? c.Fcu / fc : double.PositiveInfinity;
                sf = Math.Min(sft, sfc) / SfUltimate;
                gov = sft < sfc ? "인장 파손" : "압축 파손";
            }
            else
            {
                double sy = vm > 0 ? c.Fty / vm / SfYield : double.PositiveInfinity, su = vm > 0 ? c.Ftu / vm / SfUltimate : double.PositiveInfinity;
                sf = Math.Min(sy, su);
                gov = sy <= su ? "항복" : "극한";
            }
            // 좌굴 상호작용(SP-8007 선형): R = σc/σcr,a + σb/σcr,b + p/pcr ≤ 1/SF
            double comp = Math.Max(-sAx, 0);
            double pTerm = 0;
            if (lc.Crossflow)
                pTerm = pcrF is { } pf ? f.Q / pf : lc.ExternalPressure / pcrA;
            double rb = SfBuckling * (comp / scrA + Math.Abs(sB) / scrB + pTerm);
            res.Add(new CaseResult(lc.Id, s1, Math.Min(s3, hoop), vm, sf, rb, rb > 1 / Math.Max(sf, 1e-9) ? "좌굴" : gov, src));
        }
        double minSf = res.Min(x => x.StrengthSf), maxRb = res.Max(x => x.BucklingRatio);
        bool pass = minSf >= 1 && maxRb <= 1;
        var worst = res.OrderBy(x => Math.Min(x.StrengthSf, 1 / Math.Max(x.BucklingRatio, 1e-12))).First();
        string governing = worst.StrengthSf <= 1 / Math.Max(worst.BucklingRatio, 1e-12) ? $"{worst.Lc} {worst.Governing}" : $"{worst.Lc} 좌굴";
        return new Eval(c, t, TubeMass(c.Density, o.OuterDiameter, t, o.Length), res, minSf, maxRb, pass, governing, scrA, scrB, pcrA, pcrF);
    }

    // ---------------- CalculiX 셸 ----------------

    /// <summary>원통 S8R 셸(축 z, 양끝 반경·원주 구속 = 격벽이 원형 유지, z=0 끝 축 구속). nθ × nz.</summary>
    public static FeMesh CylinderMesh(double rMid, double length, double t, int nTheta, int nZ)
    {
        var mesh = FeMesh.Quad8(nTheta, nZ, (u, v) => new Vec3(rMid * Math.Cos(2 * Math.PI * u), rMid * Math.Sin(2 * Math.PI * u), length * v), FeType.S8R, t);
        mesh.WeldCoincident(1e-9);
        return mesh;
    }

    private static CcxModel ShellModel(FeMesh mesh, double length, Material mat, CcxAnalysis an)
    {
        var m = new CcxModel { Mesh = mesh, Material = mat, Analysis = an, Modes = 4, Title = "container tube" };
        foreach (var n in mesh.NodesWhere(p => p.Z < 1e-9 || p.Z > length - 1e-9)) m.Fixed.Add((n, 1, 2));
        foreach (var n in mesh.NodesWhere(p => p.Z < 1e-9)) m.Fixed.Add((n, 3, 3));
        return m;
    }

    /// <summary>횡류(바람 +x, 바람받이 θ = π) 순 외압 q(Cp(φ) − Cp_b) 를 요소별로(+ 는 바깥 법선 방향 → 외압은 음수).</summary>
    private static void AddCrossflow(CcxModel m, int nTheta, Flight f, double q)
    {
        for (int e = 0; e < m.Mesh.Elements.Count; e++)
        {
            int i = e % nTheta;
            double th = (i + 0.5) / nTheta * 2 * Math.PI;
            double phi = th - Math.PI;
            double pExt = q * (CrossflowCp(phi, f.Cp0, f.CpStar, f.CpBase) - f.CpBase);
            m.Pressures.Add((e, 0, -pExt));
        }
    }

    /// <summary>단일 스레드 실행 — ccx 2.23 *BUCKLE 은 다중 스레드 해석기(OMP/PaStiX)에서 끝나지 않는 것을 확인(단일 스레드 9 s). 두께별 병렬로 대신 빠르게.</summary>
    private static CcxResult Solve1(CalculixInstall inst, CcxModel m, string dir, string job)
    {
        string inp;
        try { inp = CcxDeck.Write(m, dir, job); }
        catch (Exception ex) { return new CcxResult { Dir = dir, Job = job, Error = $"덱 작성 실패: {ex.Message}" }; }
        return CcxRunner.Run(inst, inp, TimeSpan.FromMinutes(8), threads: 1);
    }

    private static readonly Material RefMat = new()
    {
        Key = "ref-qi", Name = "기준 등방(E 50 GPa, ν 0.3)", Density = 1550, E = 50e9, Nu = 0.3, Yield = 600e6, Ultimate = 600e6, Source = "선형 FEA 기준(응력은 E 무관, 좌굴은 E 비례 환산)",
    };

    public static FeaCross RunFeaCross(CalculixInstall inst, Flight f, ContainerMaterialOptions o, double t, string dir, int nTheta = 48, int nZ = 40)
    {
        double rMid = o.OuterDiameter / 2 - t / 2;
        var mesh = CylinderMesh(rMid, o.Length, t, nTheta, nZ);
        var st = ShellModel(mesh, o.Length, RefMat, CcxAnalysis.Static);
        AddCrossflow(st, nTheta, f, f.Q);
        var rs = Solve1(inst, st, Path.Combine(dir, "static"), "cross");
        if (!rs.Ok) throw new InvalidOperationException($"CalculiX 정적 실패: {rs.Error}");
        var stress = rs.Stress();
        double s1 = 0, s3 = 0, vm = 0, vmAll = 0;
        foreach (var (id, s) in stress)
        {
            if (rs.Frd is null || !rs.Frd.Coords.TryGetValue(id, out var p)) continue;
            double v = Tensor.VonMises(s);
            vmAll = Math.Max(vmAll, v);
            if (p.Z < 0.05 * o.Length || p.Z > 0.95 * o.Length) continue;   // 끝 격벽 접합부(구속 경계) 제외 — 내부 구간
            var pr = Tensor.Principal(s);
            s1 = Math.Max(s1, pr[0]); s3 = Math.Min(s3, pr[^1]); vm = Math.Max(vm, v);
        }
        double disp = rs.Displacement().Values.Select(d => d.Norm).DefaultIfEmpty(0).Max();
        var bk = ShellModel(mesh, o.Length, RefMat, CcxAnalysis.Buckle);
        AddCrossflow(bk, nTheta, f, f.Q);
        var rb = Solve1(inst, bk, Path.Combine(dir, "buckle"), "crossbk");
        double lam = rb.Ok ? rb.Dat.BucklingFactors.Where(x => x > 0).DefaultIfEmpty(double.NaN).Min() : double.NaN;
        return new FeaCross(t, f.Q, RefMat.E, s1, s3, vm, vmAll, lam, disp * 1e3, mesh.Elements.Count, rs.Seconds + rb.Seconds,
            rb.Ok ? "" : $"좌굴 실패: {rb.Error}");
    }

    /// <summary>검증: 균일 외압(횡압) 좌굴 FEA vs 고전 해 Timoshenko–Gere(녹다운 전). 반환 (FEA p_cr, 고전 p_cr) [Pa].</summary>
    public static (double Fea, double Classical) VerifyUniformPressure(CalculixInstall inst, ContainerMaterialOptions o, double t, string dir, int nTheta = 48, int nZ = 40)
    {
        double rMid = o.OuterDiameter / 2 - t / 2;
        var mesh = CylinderMesh(rMid, o.Length, t, nTheta, nZ);
        var bk = ShellModel(mesh, o.Length, RefMat, CcxAnalysis.Buckle);
        for (int e = 0; e < mesh.Elements.Count; e++) bk.Pressures.Add((e, 0, -1000.0));
        var rb = Solve1(inst, bk, dir, "uniformbk");
        if (!rb.Ok) throw new InvalidOperationException($"CalculiX 균일 외압 좌굴 실패: {rb.Error}");
        double lam = rb.Dat.BucklingFactors.Where(x => x > 0).DefaultIfEmpty(double.NaN).Min();
        return (lam * 1000, ExternalPressureTimoshenko(RefMat.E, RefMat.Nu, rMid, t, o.Length).P);
    }

    // ---------------- 보고서 ----------------

    public sealed class Result
    {
        public required Flight Flight { get; init; }
        public required List<LoadCase> Cases { get; init; }
        public List<Eval> Evals { get; } = new();
        public List<FeaCross> Fea { get; } = new();
        public (double Fea, double Classical)? Verify { get; set; }
        public List<string> Notes { get; } = new();
        public double Payload, Contents, OpenG, OpenAoa;
        public string Markdown { get; set; } = "";
    }

    /// <summary>시나리오에서 탑재 질량(컨테이너 + 자손)·내용물 질량.</summary>
    public static (double Payload, double Contents) PayloadOf(ScenarioDoc doc)
    {
        var w = doc.Build();
        var c = w.GetBody("container");
        double Sum(Body b) => b.OwnMass + b.Children.Sum(Sum);
        double tot = Sum(c);
        return (tot, tot - c.OwnMass);
    }

    public static Result Generate(ContainerMaterialOptions o, ScenarioDoc? doc = null)
    {
        Directory.CreateDirectory(o.OutDir);
        void L(string s) => o.Log?.Invoke(s);
        var f = FlightAt(o.Altitude, o.Mach, o.OuterDiameter, o.Length);
        double payload = o.PayloadMass, contents = o.ContentsMass;
        var notes = new List<string>();
        if (doc is not null)
        {
            try { (payload, contents) = PayloadOf(doc); notes.Add($"탑재 질량은 시나리오 `{doc.Name}` 에서 읽음(컨테이너 + 자손 물체)."); }
            catch (Exception ex) { notes.Add($"시나리오 질량 읽기 실패 — 기본값 사용: {ex.Message}"); }
        }
        double openG = o.OpeningG, openAoa = o.OpeningAoaDeg;
        if (o.Envelope is { } env)
        {
            double Get(string k, string s) => env[k]?[s] is JsonValue v && v.TryGetValue(out double d) ? d : double.NaN;
            double g = Get("drogueOpeningG", "mcMax");
            if (double.IsFinite(g) && g > 0) { openG = Math.Max(g, openG); notes.Add($"개방 G = max(포락선 드로그 MC 최대 {g:F1} G, 기본 {o.OpeningG:F0} G)."); }
            double a = Get("maxAoaFree_deg", "mcP99");
            if (double.IsFinite(a)) { openAoa = Math.Clamp(a, 0, 90); notes.Add($"개방 순간 받음각 = 포락선 자유비행 최대 α_t MC 99 백분위 {a:F1}° (≤ 90°)."); }
            double qc = Get("maxCrossflowQ_Pa", "allMax");
            if (double.IsFinite(qc)) notes.Add($"텀블링 연구의 최대 횡류 동압 q·sin²α_t = {qc:F0} Pa (전 실행 최대) — 설계 LC2 는 M 0.5 정면 동압 {f.Q:F0} Pa 로 옆으로 선 자세(포락선 이상, 보수).");
        }
        var lcs = LoadCases(f, o, payload, contents, openG, openAoa);
        var res = new Result { Flight = f, Cases = lcs, Payload = payload, Contents = contents, OpenG = openG, OpenAoa = openAoa };
        res.Notes.AddRange(notes);
        var fea = new Dictionary<double, FeaCross>();
        if (o.Ccx)
        {
            var inst = CalculixInstall.TryLocate(o.CcxInstall, out var why);
            if (inst is null) res.Notes.Add($"CalculiX 미실행: {why} — LC2 타원화는 자유 링 상한(매우 보수)으로 대체.");
            else
            {
                var work = Path.GetFullPath(o.WorkDir ?? Path.Combine("results", "container_material_work"));
                try
                {
                    L("CalculiX 검증: 균일 외압 좌굴 vs 고전식");
                    res.Verify = VerifyUniformPressure(inst, o, o.BaselineThickness, Path.Combine(work, "verify"), o.MeshTheta, o.MeshLength);
                }
                catch (Exception ex) { res.Notes.Add($"검증 실패: {ex.Message}"); }
                var feaList = new FeaCross?[o.Thicknesses.Length];
                Parallel.For(0, o.Thicknesses.Length, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, o.CcxJobs) }, i =>
                {
                    double t = o.Thicknesses[i];
                    try
                    {
                        L($"CalculiX 횡류 셸 t = {t * 1000:0.##} mm");
                        feaList[i] = RunFeaCross(inst, f, o, t, Path.Combine(work, $"t{t * 1e4:F0}"), o.MeshTheta, o.MeshLength);
                    }
                    catch (Exception ex) { lock (res.Notes) res.Notes.Add($"t = {t * 1000:0.##} mm FEA 실패: {ex.Message}"); }
                });
                foreach (var fc in feaList) if (fc is not null) { fea[fc.T] = fc; res.Fea.Add(fc); }
            }
        }
        foreach (var key in o.Materials)
        {
            var c = CandidateOf(key);
            double corr = res.Verify is { } vv && vv.Fea > 0 ? Math.Min(1, vv.Classical / vv.Fea) : 1;
            foreach (var t in o.Thicknesses) res.Evals.Add(Evaluate(c, t, f, lcs, o, fea.GetValueOrDefault(t), corr));
        }
        File.WriteAllText(Path.Combine(o.OutDir, "material_results.json"), ToJson(res, o).ToJsonString(J.Pretty), new UTF8Encoding(false));
        res.Markdown = Markdown(res, o);
        File.WriteAllText(Path.Combine(o.OutDir, "README.md"), res.Markdown, new UTF8Encoding(false));
        return res;
    }

    public static JsonObject ToJson(Result r, ContainerMaterialOptions o) => new()
    {
        ["schema"] = "mns.container-material/1",
        ["flight"] = new JsonObject
        {
            ["altitude_m"] = r.Flight.Altitude, ["mach"] = r.Flight.Mach, ["T_K"] = r.Flight.T, ["p_Pa"] = r.Flight.P, ["rho"] = r.Flight.Rho,
            ["a_mps"] = r.Flight.A, ["V_mps"] = r.Flight.V, ["q_Pa"] = r.Flight.Q, ["ReD"] = r.Flight.ReD, ["cd2d"] = r.Flight.Cd2D, ["eta"] = r.Flight.Eta,
            ["cp0"] = r.Flight.Cp0, ["cpStar"] = r.Flight.CpStar, ["cpBase"] = r.Flight.CpBase, ["c2"] = r.Flight.C2,
        },
        ["payload_kg"] = r.Payload, ["contents_kg"] = r.Contents,
        ["loadCases"] = new JsonArray(r.Cases.Select(c => (JsonNode)new JsonObject { ["id"] = c.Id, ["name"] = c.Name, ["axial_N"] = c.AxialForce, ["moment_Nm"] = c.BendingMoment, ["pressure_Pa"] = c.ExternalPressure }).ToArray()),
        ["fea"] = new JsonArray(r.Fea.Select(x => (JsonNode)new JsonObject { ["t_m"] = x.T, ["s1_Pa"] = x.S1Max, ["s3_Pa"] = x.S3Min, ["vm_Pa"] = x.VmMax, ["vmAll_Pa"] = x.VmMaxAll, ["buckle"] = x.Buckle, ["qRef_Pa"] = x.QRef, ["ERef_Pa"] = x.ERef }).ToArray()),
        ["evaluations"] = new JsonArray(r.Evals.Select(e => (JsonNode)new JsonObject
        {
            ["material"] = e.Mat.Key, ["t_m"] = e.T, ["mass_kg"] = e.Mass, ["minStrengthSf"] = e.MinStrengthSf, ["maxBucklingRatio"] = e.MaxBucklingRatio, ["pass"] = e.Pass, ["governing"] = e.Governing,
        }).ToArray()),
    };

    private static string Fm(double v, string fmt = "F2") => double.IsFinite(v) ? v.ToString(fmt, Inv) : "—";

    private static string Markdown(Result r, ContainerMaterialOptions o)
    {
        var sb = new StringBuilder();
        void P(string s = "") => sb.AppendLine(s);
        var f = r.Flight;
        P($"# 컨테이너 튜브 재질·두께 — M 0.5 공력·분리·개방 하중에서 항복·좌굴 여유 (Ø{o.OuterDiameter * 1e3:F0} × {o.Length * 1e3:N0})");
        P();
        P($"생성: `{o.Command}` · {DateTime.Now:yyyy-MM-dd HH:mm} · 엔진 `Sim.Core {typeof(ContainerMaterialStudy).Assembly.GetName().Version}`{(o.EnvelopePath is { } ep ? $" · 하중 포락선 `{ep.Replace('\\', '/')}`" : "")}");
        P();
        P($"- 대상: 컨테이너 튜브(현재 CFRP t {o.BaselineThickness * 1e3:0.#} mm, 바깥 Ø{o.OuterDiameter * 1e3:F0}, 격벽 사이 길이 {o.Length * 1e3:N0} mm). 튜브 단독 강도·좌굴 — 격벽·레일·결속 금구 국부 응력은 범위 밖(기존 `mns ccx` 실모델 해석).");
        P($"- 안전계수(결정 기록 — PRD 에 수치 기준 없음): 금속 항복 {SfYield} · 극한 {SfUltimate}, 복합재 극한 {SfUltimate}, 좌굴 {SfBuckling}(SP-8007 녹다운 적용 후). 설계 하중 {o.DesignG:F0} G(캐니스터 설계 하중).");
        P("- 원자료: [material_results.json](material_results.json).");
        P();
        P("## 1. 비행 조건 — ISA 1,000 m, M 0.5 (정확 계산)");
        P();
        P("| 양 | 값 | 식 |");
        P("|---|---|---|");
        P($"| 온도 T | {f.T:F2} K | 288.15 − 0.0065·h (지오퍼텐셜) |");
        P($"| 정압 p | {f.P:F1} Pa | 101,325·(T/288.15)^5.2559 |");
        P($"| 밀도 ρ | {f.Rho:F4} kg/m³ | p/(R T), R = 287.053 |");
        P($"| 음속 a | {f.A:F2} m/s | √(γ R T) |");
        P($"| 속도 V = 0.5 a | {f.V:F2} m/s ({f.V * 3.6:F0} km/h) | |");
        P($"| **동압 q** | **{f.Q:F0} Pa ({f.Q / 1000:F2} kPa)** | ½ ρ V² = ½ γ p M² |");
        P($"| 레이놀즈수 Re_D | {f.ReD:E3} | ρ V D / μ (Sutherland μ = {f.Mu:E3}) |");
        P($"| 원통 횡류 CD2D (TR R-300) · η(L/D {o.Length / o.OuterDiameter:F2}) | {f.Cd2D:F3} · {f.Eta:F3} → 3차원 {f.CdCross:F3} | `CylinderCrossflow` 표(A 등급) |");
        P($"| 정체점 Cp0 (압축성) | {f.Cp0:F4} | 등엔트로피 |");
        P($"| 음속 Cp* (국부 M 1) | {f.CpStar:F3} | M 0.5 횡류는 임계 마하(≈ 0.4) 초과 — 측면 흡입 피크를 Cp* 로 자름 |");
        P($"| 기저압 Cp_b (보정) | {f.CpBase:F3} | 적분 CD = CD2D 가 되도록 이분법 |");
        P($"| 타원화 성분 c₂ (cos 2φ 계수) | {f.C2:F3} | 셸 타원화(링 굽힘) 구동 성분 |");
        P();
        P("횡류 Cp(φ) 모형: 정체점에서 Cp0 − 4 sin²φ(퍼텐셜 모양) → 음속 하한 Cp* → 90°–110° 선형 회복 → 기저압 Cp_b(뒤쪽 균일). 순 외압 = q·(Cp − Cp_b) — 컨테이너 내부가 후류 기저압으로 배기된다고 본 보수 가정(밀폐·정압 배기면 바람받이 외압이 q·Cp_b 만큼 작다).");
        P();
        P("## 2. 하중 케이스");
        P();
        P($"탑재 질량 {r.Payload:F2} kg(컨테이너 + 드론·센서·팩·도어) · 내용물 {r.Contents:F2} kg · 개방 {r.OpenG:F1} G · 개방 순간 받음각 {r.OpenAoa:F0}°.");
        P();
        P("| ID | 하중 | 축력 [N] (+인장) | 굽힘 [N·m] | 순 외압 [Pa] | 근거 |");
        P("|---|---|---|---|---|---|");
        foreach (var c in r.Cases) P($"| {c.Id} | {c.Name} | {c.AxialForce:F0} | {c.BendingMoment:F0} | {(c.ExternalPressure > 0 ? c.ExternalPressure.ToString("F0", Inv) : "—")} | {c.Basis} |");
        P();
        foreach (var n in r.Notes) P($"- {n}");
        P();
        P("## 3. 후보 재질(설계 허용값)");
        P();
        P("| 재질 | ρ [kg/m³] | E [GPa] | ν | 항복/첫 ply [MPa] | 극한 인장 [MPa] | 압축 허용 [MPa] | 근거·가정 |");
        P("|---|---|---|---|---|---|---|---|");
        foreach (var key in o.Materials)
        {
            var c = CandidateOf(key);
            P($"| {c.Name} | {c.Density:F0} | {c.E / 1e9:F1} | {c.Nu:F2} | {(c.Composite ? "— (취성)" : (c.Fty / 1e6).ToString("F0", Inv))} | {c.Ftu / 1e6:F0} | {c.Fcu / 1e6:F0} | {c.Basis} |");
        }
        P();
        if (r.Fea.Count > 0)
        {
            P("## 4. CalculiX 셸 해석(LC2 횡류 — 타원화 + 보 굽힘 + 좌굴)");
            P();
            P($"S8R 2차 셸, 원주 {o.MeshTheta} × 길이 {o.MeshLength} 요소, 양끝 격벽 = 반경·원주 변위 구속(원형 유지·회전 자유), 한쪽 끝 축 구속. 하중 = 동압 {f.Q:F0} Pa 의 q(Cp(φ) − Cp_b) 분포. 기준 재질 E 50 GPa·ν 0.3 — 선형 정적 응력은 E 와 무관(ν 차이 무시), 좌굴 계수는 E 에 비례 환산. 응력 = 끝 5 % 구간 제외 내부 최대(끝 접합부는 '전체' 열).");
            P();
            if (r.Verify is { } v)
            {
                double rm = o.OuterDiameter / 2 - o.BaselineThickness / 2;
                var tg = ExternalPressureTimoshenko(RefMat.E, RefMat.Nu, rm, o.BaselineThickness, o.Length);
                P($"검증(균일 외압, t {o.BaselineThickness * 1000:F0} mm, E 50 GPa, 녹다운 전): FEA p_cr = **{v.Fea / 1000:F1} kPa** vs 고전 해 Timoshenko–Gere 단순지지(n = {tg.N}) **{v.Classical / 1000:F1} kPa** (비 {v.Fea / v.Classical:F3}) · SP-8007 설계식(Windenburg–Trilling) {ClassicalExternalPressure(RefMat.E, RefMat.Nu, rm, o.BaselineThickness, o.Length) / 1000:F1} kPa. " +
                  $"FEA 가 높은 것은 셸 끝 절점 구속이 회전을 일부 막아(단순지지와 고정 사이) 생기는 것으로 보고, 횡류 좌굴 계수에 **보정 {Math.Min(1, v.Classical / v.Fea):F3}** 를 곱해 단순지지 수준으로 낮춘 뒤 녹다운을 적용했다(보수).");
            }
            P();
            P("| t [mm] | 최대 주응력 σ₁ [MPa] | 최소 주응력 σ₃ [MPa] | von Mises 내부 [MPa] | von Mises 전체(끝 포함) [MPa] | 최대 변위 [mm] | 좌굴 계수 λ (E 50 GPa, 녹다운 전) | 요소 | 계산 |");
            P("|---|---|---|---|---|---|---|---|---|");
            foreach (var x in r.Fea)
                P($"| {x.T * 1000:0.##} | {x.S1Max / 1e6:F1} | {x.S3Min / 1e6:F1} | {x.VmMax / 1e6:F1} | {x.VmMaxAll / 1e6:F1} | {x.MaxDispMm:F2} | {(x.Buckle < 1.05 ? $"≤ 1 ({Fm(x.Buckle)} — 기준 하중 이하 좌굴, 선형 응력 무효)" : Fm(x.Buckle))} | {x.Elements} | {x.Seconds:F0} s{(x.Note.Length > 0 ? " · " + x.Note : "")} |");
            P();
            P($"좌굴 계수 λ 는 'M 0.5 옆으로 선 자세 횡류 압력'의 배수다(E 50 GPa 기준) — 재질 E 로 환산 후 녹다운 {GammaPressure} 를 곱해 SF {SfBuckling} 와 비교.");
            P();
        }
        else
        {
            P("## 4. CalculiX 셸 해석");
            P();
            P("실행하지 않음(`--ccx` 없음 또는 설치 없음) — LC2 타원화 응력은 **자유 링 상한** σ = 2 q c₂ R²/t²(격벽 지지 무시, 매우 보수)로 대체했다. `--ccx` 로 다시 산출 권장.");
            P();
        }
        P("## 5. 재질 × 두께 비교");
        P();
        P($"칸 = 최소 여유 = min(강도 SF/요구, 1/(좌굴 상호작용 × {SfBuckling})) — ≥ 1.00 이면 통과(굵게). 질량 = 튜브 단독 ρ·π(D − t)·t·L.");
        P();
        P("| 재질 \\ t [mm] | " + string.Join(" | ", o.Thicknesses.Select(t => (t * 1000).ToString("0.0", Inv))) + " |");
        P("|---|" + string.Concat(Enumerable.Repeat("---|", o.Thicknesses.Length)));
        foreach (var key in o.Materials)
        {
            var row = r.Evals.Where(e => e.Mat.Key == key).OrderBy(e => e.T).ToList();
            P($"| {row[0].Mat.Name} | " + string.Join(" | ", row.Select(e =>
            {
                double margin = Math.Min(e.MinStrengthSf, 1 / Math.Max(e.MaxBucklingRatio, 1e-12));
                string m = margin >= 100 ? ">100" : margin.ToString("F2", Inv);
                return (e.Pass ? $"**{m}**" : m) + $" ({e.Mass:F1} kg)";
            })) + " |");
        }
        P();
        P("### 재질별 최소 두께·질량(통과하는 가장 얇은 두께)");
        P();
        double baseTotal = r.Payload;
        P($"현재 설계 튜브 = CFRP 3 mm, {o.BaselineTubeMass:F2} kg(CAD 부피 × 1,430 kg/m³) · 현재 탑재 합계 {baseTotal:F2} kg · 예산 {o.MassLimit:F0} kg.");
        P();
        P("| 재질 | 최소 t [mm] | 튜브 질량 [kg] | 현재 대비 Δ [kg] | 탑재 합계 [kg] (예산 50) | 지배 기준 | t 에서 축 좌굴 σcr [MPa] · 외압 p_cr(해석/FEA) [kPa] | 3 mm 에서 최소 여유 |");
        P("|---|---|---|---|---|---|---|---|");
        var summary = new List<(string Name, double T, double Mass)>();
        foreach (var key in o.Materials)
        {
            var row = r.Evals.Where(e => e.Mat.Key == key).OrderBy(e => e.T).ToList();
            var best = row.FirstOrDefault(e => e.Pass);
            var at3 = row.FirstOrDefault(e => Math.Abs(e.T - o.BaselineThickness) < 1e-9);
            string m3 = at3 is null ? "—" : Math.Min(at3.MinStrengthSf, 1 / Math.Max(at3.MaxBucklingRatio, 1e-12)).ToString("F2", Inv) + (at3.Pass ? " 통과" : " 불합격");
            if (best is null)
            {
                P($"| {row[0].Mat.Name} | {o.Thicknesses[^1] * 1000:F1} 초과 | — | — | — | {row[^1].Governing} | — | {m3} |");
                continue;
            }
            double dm = best.Mass - o.BaselineTubeMass, tot = baseTotal + dm;
            summary.Add((best.Mat.Name, best.T, best.Mass));
            P($"| {best.Mat.Name} | {best.T * 1000:0.##} | {best.Mass:F2} | {dm:+0.00;-0.00} | {tot:F2} {(tot <= o.MassLimit ? "✓" : "✗ 초과")} | {best.Governing} | {best.SigmaCrAxial / 1e6:F0} · {best.PCrAnalytic / 1000:F0}/{(best.PCrFea is { } pf ? (pf / 1000).ToString("F0", Inv) : "—")} | {m3} |");
        }
        P();
        P("### 하중 케이스별 상세(현재 두께 3 mm)");
        P();
        P("| 재질 | LC | σ₁ [MPa] | σ_min [MPa] | von Mises [MPa] | 강도 여유(SF/요구) | 좌굴 상호작용 × SF | 지배 | 응력 출처 |");
        P("|---|---|---|---|---|---|---|---|---|");
        foreach (var e in r.Evals.Where(e => Math.Abs(e.T - o.BaselineThickness) < 1e-9))
            foreach (var c in e.Cases)
                P($"| {e.Mat.Name} | {c.Lc} | {c.SigmaMax / 1e6:F1} | {c.SigmaMin / 1e6:F1} | {c.VonMises / 1e6:F1} | {(c.StrengthSf > 100 ? ">100" : c.StrengthSf.ToString("F2", Inv))} | {c.BucklingRatio:F3} | {c.Governing} | {c.Source} |");
        P();
        P("## 6. 결론");
        P();
        if (summary.Count > 0)
        {
            var light = summary.OrderBy(s => s.Mass).ToList();
            P($"- 가장 가벼운 통과안: **{light[0].Name} t {light[0].T * 1000:0.##} mm ({light[0].Mass:F2} kg)**" + (light.Count > 1 ? $", 다음 {light[1].Name} t {light[1].T * 1000:0.##} mm ({light[1].Mass:F2} kg)." : "."));
        }
        var cf3 = r.Evals.FirstOrDefault(e => e.Mat.Key == "cfrp-qi" && Math.Abs(e.T - o.BaselineThickness) < 1e-9);
        if (cf3 is not null)
            P($"- 현재 설계(CFRP 준등방 3 mm): 최소 여유 {Math.Min(cf3.MinStrengthSf, 1 / Math.Max(cf3.MaxBucklingRatio, 1e-12)):F2} ({(cf3.Pass ? "통과" : "불합격")}), 지배 {cf3.Governing}.");
        P("- M 0.5 동압(15.7 kPa) 자체는 얇은 튜브에도 큰 막응력을 만들지 않는다 — 두께를 정하는 것은 ① 옆으로 선 자세의 **횡류 외압 좌굴·타원화**(얇을수록 t^2.5 로 급감) ② 50 G 분리 충격 굽힘·축압축이다. 정상 비행(LC1)은 어느 재질에서도 지배하지 않는다.");
        // 외압 좌굴 지배(p_cr ∝ E t^2.5) 일 때 같은 p_cr 의 질량 ∝ ρ t ∝ ρ / E^0.4 → 성능 지수 E^0.4/ρ (클수록 가벼움)
        var idx = o.Materials.Select(CandidateOf).Select(c => (c.Name, I: Math.Pow(c.E / 1e9, 0.4) / (c.Density / 1000))).OrderByDescending(x => x.I).ToList();
        P("- 좌굴 지배 재질 지수 E^0.4/ρ (외압 좌굴 p_cr ∝ E·t^2.5, 질량 ∝ ρ·t → 같은 좌굴 여유의 질량 ∝ ρ/E^0.4, 클수록 가벼움): " +
          string.Join(" > ", idx.Select(x => $"{x.Name} {x.I:F2}")) + ". 위 최소 두께 표의 질량 순서와 같은 이유다.");
        P("- 실용 하한: 준등방 CFRP 는 [0/45/−45/90]s 8 ply × 0.125 mm = 1 mm 가 최소 적층 단위 — 2 mm(16 ply)가 위 결과의 최소 통과 두께에 가깝다. 금속 1.5 mm 박판 튜브는 취급·찍힘(dent)에 약해 실제로는 더 두껍게 정하는 경우가 많다.");
        var cfMin = r.Evals.Where(e => e.Mat.Key == "cfrp-qi" && e.Pass).MinBy(e => e.T);
        if (cf3 is { Pass: true })
            P($"- 결론적으로 **M 0.5 에서 옆으로 선 자세까지 고려해도 현재 CFRP 3 mm 는 항복·파손·좌굴 여유가 있다**(최소 여유 {Math.Min(cf3.MinStrengthSf, 1 / Math.Max(cf3.MaxBucklingRatio, 1e-12)):F2})" +
              (cfMin is not null && cfMin.T < cf3.T - 1e-9 ? $" — 질량을 줄이려면 CFRP {cfMin.T * 1000:F2} mm({cfMin.Mass:F2} kg)까지 내릴 여지가 있다(적층·충격 손상 여유 확인 전제)." : "."));
        else if (cf3 is not null)
            P($"- 결론적으로 현재 CFRP 3 mm 는 이 하중 조합에서 **불합격**(지배 {cf3.Governing}) — 위 최소 두께 표의 두께로 올려야 한다.");
        P();
        P("## 7. 근거·등급·한계");
        P();
        P("| 항목 | 근거(등급) | 한계 |");
        P("|---|---|---|");
        P("| 대기·동압 | US Standard Atmosphere 1976(A) — `Atmosphere`, 검증 보고서 atmosphere_us1976_table | 표준 대기(온도 오프셋 없음) |");
        P("| 횡류 CD | NASA TR R-300 표(A) + TN 2960 η(B) | 정상(시간 평균) — 와류 방출 변동 압력(±0.3 q 수준)·피로 미반영(진동 피로(근사)는 `mns tumble-study --fatigue-simpkg` 7b · `mns loads` 6절) |");
        P("| Cp(φ) 분포 | 퍼텐셜 모양 + 음속 상한 + CD 보정 기저압(모형, C) | 실제 박리점·흡입 피크 위치는 Re·M 의존 — 설계 CFD 표면 압력이 들어오면 교체(입력 교체 지점 = `CrossflowCp`·FEA 압력 매핑) |");
        P("| 좌굴 | NASA SP-8007(1968) 고전식 + 녹다운(A — 설계 지침) · CalculiX 선형 좌굴(검증 비교 4절) | 녹다운은 등방 금속 시험 기반 — 복합재는 적층별 D 행렬 영향(준등방 등가 등방 근사, C) · 끝 격벽 강성 이상화 |");
        P("| 재질 허용값 | 핸드북 대표값(B/C) — 설계 허용값(MMPDS A/B-basis) 아님 · 복합재 압축 0.75 × 인장(D) | 충격 손상(BVID) 후 압축 강도 저하(CFRP 0.5–0.6) 미반영 → 복합재는 실제 설계에서 추가 녹다운 필요 |");
        P("| 하중 | 설계 50 G(PRD) · 개방 G(시뮬레이션 포락선 또는 15–20 G) · 횡류 = 옆으로 선 자세 M 0.5 | 하중 동시 작용(텀블링 중 개방) 미조합 · 동적 증폭(충격 응답 스펙트럼) 없음 · 격벽·레일 결속부 국부 응력 별도 |");
        P();
        P("신뢰 등급(NASA-STD-7009A, PRD AB절): **참고(Reference)** — 해석식·FEA 는 검증(Verification) 수준 2~3(CalculiX 해석해 검증 9종 + 이 보고서 균일 외압 대조), 확인(Validation) 수준 0~1(이 튜브 시험 없음), 입력 출처 B~D. 재질 선택 방향(복합재 유리·좌굴 지배)은 신뢰할 수 있으나 최소 두께 절대값은 재질 허용값·적층·시험(외압·굽힘 좌굴 시험)으로 확정해야 한다.");
        P();
        P("## 8. 재현");
        P();
        P("```");
        P(o.Command);
        P("```");
        P();
        P("- 하중 포락선은 `mns tumble-study` 의 `envelope.json` — 설계 CFD 공력 DB 가 들어오면 텀블링 연구를 `--aero-db` 로 다시 돌린 뒤 같은 명령을 반복한다.");
        return sb.ToString();
    }
}
