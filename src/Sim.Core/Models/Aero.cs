using Sim.Core.Environment;
using Sim.Core.Geometry;
using Sim.Core.Numerics;

namespace Sim.Core.Models;

/// <summary>
/// 1차원 표 보간(다선형). periodic=true 이면 각도 축(±180°) 주기 경계로 연속 보간(FR-AER-05, VV-10).
/// </summary>
public sealed class Table1D
{
    public double[] X { get; }
    public double[] Y { get; }
    public bool Periodic { get; }
    public double Period { get; }
    /// <summary>범위 밖 조회 횟수(FR-AER-06).</summary>
    public long OutOfRangeCount { get; private set; }

    public Table1D(double[] x, double[] y, bool periodic = false, double period = 360)
    {
        if (x.Length != y.Length || x.Length < 1) throw new ArgumentException("표 크기 불일치");
        for (int i = 1; i < x.Length; i++)
            if (!(x[i] > x[i - 1])) throw new ArgumentException("표 X 는 단조 증가여야 합니다");
        X = x; Y = y; Periodic = periodic; Period = period;
    }

    public double Eval(double x)
    {
        int n = X.Length;
        if (n == 1) return Y[0];
        if (Periodic)
        {
            double x0 = X[0];
            x = x0 + ((x - x0) % Period + Period) % Period;
            if (x > X[n - 1])
            {
                // 마지막 점 → 첫 점(+Period) 구간
                double span = X[0] + Period - X[n - 1];
                double f = span > 0 ? (x - X[n - 1]) / span : 0;
                return Y[n - 1] + (Y[0] - Y[n - 1]) * f;
            }
        }
        else
        {
            if (x <= X[0]) { if (x < X[0]) OutOfRangeCount++; return Y[0]; }
            if (x >= X[n - 1]) { if (x > X[n - 1]) OutOfRangeCount++; return Y[n - 1]; }
        }
        int lo = 0, hi = n - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) >> 1;
            if (X[mid] <= x) lo = mid; else hi = mid;
        }
        double t = (x - X[lo]) / (X[hi] - X[lo]);
        return Y[lo] + (Y[hi] - Y[lo]) * t;
    }
}

/// <summary>
/// 물체 공력 모델(Device 가 아닌 Body 고유 속성).
/// L0: 상수 Cd·S (CG 작용, 모멘트 없음).
/// L1: 형상 근사 투영면적·Cd 또는 총받음각 Cd 표 + 압력중심(CP) 오프셋 + 회전 감쇠(Clp, Cmq, Cnr).
/// </summary>
public sealed class AeroModel
{
    public double? Cd { get; init; }
    public double? Area { get; init; }
    /// <summary>총받음각 α_t [deg] (0~180) → Cd. 축 = 동체 +X.</summary>
    public Table1D? CdTable { get; init; }
    /// <summary>압력중심(동체 기준좌표). null 이면 CG 작용.</summary>
    public Vec3? Cp { get; init; }
    public double RefLength { get; init; } = 1;
    /// <summary>회전 감쇠 미계수 (Clp, Cmq, Cnr), 음수=감쇠.</summary>
    public Vec3 Damping { get; init; }
    /// <summary>감쇠 무차원화 기준길이 (p: b, q: c̄, r: b). null 이면 세 축 모두 RefLength (FR-AER-03).</summary>
    public Vec3? DampingLengths { get; init; }
    /// <summary>
    /// 법선력 기울기 CNα [1/rad] (성분 합성 Barrowman, FX-07). 0 이면 미적용. 법선력 = q·A_N·CNα·sinα·|cosα|,
    /// 방향은 동체축 수직 상대풍 성분의 반대, 작용점 Cp. 축방향·횡류 항력은 기존 L1 형상 항력이 담당(소받음각 양력만 추가).
    /// </summary>
    public double NormalForceSlope { get; init; }
    /// <summary>CNα 기준면적 [m²] (π·d_ref²/4).</summary>
    public double NormalRefArea { get; init; }
    /// <summary>성분 합성 내역(cp:"component" 일 때, 보고용).</summary>
    public BuildUp? Components { get; init; }
    /// <summary>공력 계수 배율(MC 불확도용, FR-AER-07).</summary>
    public double Scale { get; init; } = 1;
    public Shape? Shape { get; init; }
    /// <summary>
    /// 둔체 압축성 마하 보정(FR-SHL-05, 시나리오 aero.machCorrection). Cd × <see cref="BluffMachFactor"/>(M).
    /// 기본 false — 기존 시나리오(저속) 결과 불변.
    /// </summary>
    public bool MachCorrection { get; init; }
    /// <summary>
    /// 원통 횡류 모델(FR-REL-06 ①, 시나리오 aero.crossflow): "legacy"(유한 길이 아임계 표 × 둔체 마하 배율, 기본) |
    /// "tr-r300"(η(L/D)·CD2D(Re, M) — NASA TR R-300 표 + TN 2960, 횡류 Re·M 은 수직 성분). 원통 형상에만 적용.
    /// </summary>
    public string Crossflow { get; init; } = "legacy";
    /// <summary>
    /// CFD 공력 DB(FR-REL-01, 시나리오 aero.db). 있으면 형상 근사 대신 물체축 CX·CN·Cm(α_t, M) 표를 쓴다(감쇠 미계수는 그대로).
    /// </summary>
    public AeroDb? Db { get; init; }
    /// <summary>
    /// CFD 표면 유동장(시나리오 aero.flowField, mns.surface-flow/1) — 시각화 전용(면마다 Cp·Cf, 유선). 힘·모멘트 계산에는 쓰지 않는다.
    /// </summary>
    public SurfaceFlowField? FlowField { get; init; }
    /// <summary>계산 중 만난 최대 마하수(적용 범위 기록용, VV-32).</summary>
    public double MaxMach { get; private set; }
    /// <summary>계산 중 만난 최대 레이놀즈수(기준 길이 = 원통 지름 또는 대표 길이, 적용 범위 기록용).</summary>
    public double MaxReynolds { get; private set; }

    public string Fidelity => (Cp is null && Damping.NormSq == 0 && CdTable is null && Cd is not null) ? "L0" : "L1";

    // 둔체(원통 횡류·평판·상자) Cd 압축성 배율 Cd(M)/Cd(M≤0.3).
    // 출처: Lindsey, NACA TR-619 (1938) 원통 횡류 아임계 자료와 Hoerner, Fluid-Dynamic Drag 15장 그림의 개략 디지타이즈 — (가정, 검증 전).
    private static readonly Table1D BluffMach = new([0.0, 0.3, 0.4, 0.5, 0.6, 0.7], [1.0, 1.0, 1.05, 1.12, 1.25, 1.42]);
    /// <summary>마하 보정 표 적용 범위 상한(그 위는 상한값 고정 + 범위 이탈).</summary>
    public const double MaxValidMach = 0.7;

    /// <summary>둔체 Cd 압축성 배율. M ≤ 0.3 → 1, 0.7 초과는 0.7 값 고정.</summary>
    public static double BluffMachFactor(double mach) => BluffMach.Eval(Math.Clamp(mach, 0, MaxValidMach));

    /// <summary>
    /// 힘(NED)·모멘트(동체, CG 기준) 계산. vAirBody: CG 대기상대속도(동체계), cgBrf: 동체 기준좌표 CG.
    /// mach: 대기속도 마하수(마하 보정 시에만 사용).
    /// </summary>
    public (Vec3 ForceBody, Vec3 MomentBody) Compute(Vec3 vAirBody, Vec3 omegaBody, double rho, Vec3 cgBrf, double mach = 0, double mu = 0)
    {
        if (mach > MaxMach) MaxMach = mach;
        double v = vAirBody.Norm;
        if (v < 1e-9 || rho <= 0) return (Vec3.Zero, Vec3.Zero);
        var dir = vAirBody / v;
        double q = 0.5 * rho * v * v;
        double cd, area;
        if (Db is not null)
        {
            var (fdb, mdb) = Db.Compute(dir, q * Scale, mach);
            var mtot = mdb + Vec3.Cross(Db.MomentRef - cgBrf, fdb);
            return (fdb, mtot + DampingMoment(omegaBody, rho, v, Area ?? Db.RefArea));
        }
        if (Crossflow == "tr-r300" && CdTable is null && Cd is null && Shape is { Kind: ShapeKind.Cylinder } cyl)
        {
            double dRef = 2 * cyl.Radius;
            double re = mu > 0 ? rho * v * dRef / mu : 0;
            if (re > MaxReynolds) MaxReynolds = re;
            double axial = MachCorrection ? BluffMachFactor(Math.Abs(dir[cyl.Axis]) * mach) : 1;
            (area, cd) = cyl.ProjectedReMach(dir, re, mach, axial);
            if (Area is not null) area = Area.Value;
            var fc = dir * (-q * cd * area * Scale);
            var mc = Vec3.Zero;
            if (Cp is { } cpc) mc += Vec3.Cross(cpc - cgBrf, fc);
            mc += DampingMoment(omegaBody, rho, v, Area ?? cyl.MeanFrontalArea);
            return (fc, mc);
        }
        if (CdTable is null && Cd is null && Shape is { Kind: ShapeKind.ShellSegment } shell)
        {
            var fs = ShellForce(shell, dir, q * Scale * (MachCorrection ? BluffMachFactor(mach) : 1));
            var ms = Vec3.Zero;
            if (Cp is { } cps) ms += Vec3.Cross(cps - cgBrf, fs);
            return (fs, ms + DampingMoment(omegaBody, rho, v, Area ?? shell.MeanFrontalArea));
        }
        if (CdTable is not null)
        {
            double alphaT = Math.Acos(Math.Clamp(dir.X, -1, 1)) * 180 / Math.PI;
            cd = CdTable.Eval(alphaT);
            area = Area ?? Shape?.MeanFrontalArea ?? 0.01;
        }
        else if (Cd is not null)
        {
            cd = Cd.Value; area = Area ?? Shape?.MeanFrontalArea ?? 0.01;
        }
        else if (Shape is not null)
        {
            (area, cd) = Shape.Projected(dir);
            if (Area is not null) area = Area.Value;
        }
        else return (Vec3.Zero, Vec3.Zero);
        if (MachCorrection) cd *= BluffMachFactor(mach);

        var f = dir * (-q * cd * area * Scale);
        if (NormalForceSlope > 0 && NormalRefArea > 0)
        {
            var vPerp = new Vec3(0, vAirBody.Y, vAirBody.Z);
            double vp = vPerp.Norm;
            if (vp > 1e-12)
            {
                double sa = vp / v, ca = Math.Abs(vAirBody.X) / v;
                f += vPerp * (-q * NormalRefArea * NormalForceSlope * sa * ca * Scale / vp);
            }
        }
        var m = Vec3.Zero;
        if (Cp is { } cp) m += Vec3.Cross(cp - cgBrf, f);
        m += DampingMoment(omegaBody, rho, v, Area ?? Shape?.MeanFrontalArea ?? area);
        return (f, m);
    }

    private Vec3 DampingMoment(Vec3 omegaBody, double rho, double v, double sref)
    {
        if (Damping.NormSq == 0) return Vec3.Zero;
        var l = DampingLengths ?? new Vec3(RefLength, RefLength, RefLength);
        // M_i = q S l_i C_i (ω_i l_i / 2V) = ¼ ρ V S l_i² C_i ω_i
        double k = 0.25 * rho * v * sref * Scale;
        return new Vec3(k * l.X * l.X * Damping.X * omegaBody.X, k * l.Y * l.Y * Damping.Y * omegaBody.Y, k * l.Z * l.Z * Damping.Z * omegaBody.Z);
    }

    /// <summary>
    /// 곡면 판(외피 조각) L1 공력(FR-SHL-05): 횡류 독립 원리 — 조각 국부축(법선 e_n · 접선 e_t · 축 e_x) 성분별
    /// F_i = −q·S_i·Cd_i·d_i|d_i|·e_i. 법선 성분은 오목면이 바람을 받을 때(d_n &lt; 0) Cd 1.6, 볼록면 0.85(Hoerner 축소 근사),
    /// 접선·축 성분은 모서리 면적 × 1.0. 받음각이 생기면 법선력이 생겨(항력만인 모델과 달리) 힌지 둘레로 조각을 벌린다.
    /// qs = 동압 × 배율(마하 보정 포함).
    /// </summary>
    public static Vec3 ShellForce(Shape s, Vec3 dir, double qs)
    {
        var en = s.SegmentNormal;
        var ex = s.Axis switch { 0 => Vec3.UnitX, 1 => Vec3.UnitY, _ => Vec3.UnitZ };
        var et = Vec3.Cross(ex, en);
        double half = s.SpanDeg * Math.PI / 360;
        double chord = 2 * s.Radius * Math.Sin(Math.Min(half, Math.PI / 2));
        double sag = s.Radius * (1 - Math.Cos(Math.Min(half, Math.PI))) + s.Thickness;
        double dn = Vec3.Dot(dir, en), dt = Vec3.Dot(dir, et), dx = Vec3.Dot(dir, ex);
        double cn = dn >= 0 ? Shape.ShellCdConvex : Shape.ShellCdConcave;
        return en * (-qs * s.Length * chord * cn * dn * Math.Abs(dn))
             + et * (-qs * s.Length * sag * Shape.ShellCdEdge * dt * Math.Abs(dt))
             + ex * (-qs * chord * sag * 0.67 * Shape.ShellCdEdge * dx * Math.Abs(dx));
    }
}

/// <summary>
/// Young 관입식(SAND97-2426) 토양 관입 깊이·평균 감속 추정(FR-GND-02, FX-02).
/// 원식(영국 단위: D[ft], W[lb], A[in²], V[ft/s]) 을 그대로 두고 SI 입력을 변환해 계산한다(계수 단일 원천):
///   V &lt; 200 fps: D = 0.3·S·N·(W/A)^0.7·ln(1 + 2·10⁻⁵·V²)      (SAND97-2426 식 3.1)
///   V ≥ 200 fps: D = 0.00178·S·N·(W/A)^0.7·(V − 100)          (식 3.2)
///   경량 관입체 보정(토양, W &lt; 60 lb): Ks = 0.2·W^0.4          (식 3.3, SI 부록 0.27·m^0.4, m &lt; 27 kg)
/// 노즈 계수 N: 평두 0.56, 원뿔 0.25·(L/d)+0.56(식 3.9), 오자이브 0.18·(L/d)+0.56(식 3.7).
/// 원문 대조(2026-10-07, 메인 validation/public/penetration_young): 이전 계수 0.53/0.0031 은 N 정의가 다른 구판 값이라
/// 1997 판 N(평두 0.56)과 함께 쓰면 1.77배 과대 — 원문 값으로 교체.
/// A 는 <b>관입체(스파이크·노즈) 단면적</b>이지 날개 포함 투영면적이 아니다.
/// 원문 정확도 진술: 유사 표적 평균 대비 약 10~15 %, 경량 보정 적용 시 더 큼. 원문 적용 하한: 토양 W ≈ 5 lb.
/// </summary>
public static class Penetration
{
    public const double Ft = 0.3048, Lb = 0.45359237, In2 = 0.0254 * 0.0254;
    /// <summary>SAND97-2426 식 3.1/3.2 계수(영국 단위).</summary>
    public const double CoefLow = 0.3, CoefHigh = 0.00178;
    /// <summary>원문 적용 하한(토양): 관입체 무게 약 5 lb. 이보다 가벼우면 외삽(VV-32).</summary>
    public const double MinValidMassSoil = 5 * Lb;

    /// <summary>영국 단위 원식. 반환 D[ft].</summary>
    public static double YoungImperial(double weightLb, double areaIn2, double speedFps, double soilS, double noseN)
    {
        if (speedFps <= 0 || areaIn2 <= 0 || weightLb <= 0) return 0;
        double wa = Math.Pow(weightLb / areaIn2, 0.7);
        double ks = weightLb < 60 ? 0.2 * Math.Pow(weightLb, 0.4) : 1;
        double d = speedFps < 200
            ? CoefLow * soilS * noseN * wa * Math.Log(1 + 2e-5 * speedFps * speedFps)
            : CoefHigh * soilS * noseN * wa * (speedFps - 100);
        return d * ks;
    }

    /// <summary>SI: 질량[kg], 관입체 단면적[m²], 충돌속도[m/s] → (관입 깊이[m], 평균 감속[g]).</summary>
    public static (double Depth, double MeanG) Young(double mass, double area, double speed, double soilS, double noseN)
    {
        if (speed <= 0 || area <= 0) return (0, 0);
        double d = YoungImperial(mass / Lb, area / In2, speed / Ft, soilS, noseN) * Ft;
        d = Math.Max(d, 1e-4);
        double g = speed * speed / (2 * d * Atmosphere.G0);
        return (d, g);
    }

    /// <summary>Young 노즈 성능 계수 N. shape: flat | cone | ogive, lOverD: 노즈 길이/지름.</summary>
    public static double NoseN(string shape, double lOverD) => shape switch
    {
        "flat" or "blunt" => 0.56,
        "cone" or "conical" => 0.25 * lOverD + 0.56,
        "ogive" or "tangent-ogive" => 0.18 * lOverD + 0.56,
        _ => throw new ArgumentException($"알 수 없는 노즈 형상: {shape} (flat|cone|ogive)")
    };
}

/// <summary>관입체(스파이크·노즈) 정의(FX-02). Source: scenario | shape-nose(형상 기수 끝 단면 추정).</summary>
public sealed record Penetrator(double Area, double NoseN, string Source)
{
    public double Diameter => Math.Sqrt(4 * Area / Math.PI);
}
