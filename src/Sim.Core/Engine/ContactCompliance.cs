using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using Sim.Core.Geometry;
using Sim.Core.Models;
using Sim.Core.Numerics;
using Sim.Core.Structures;

namespace Sim.Core.Engine;

/// <summary>
/// 순응 접촉 강성 요소 하나(물체 하나의 구조 경로 또는 쌍의 국부 접촉): 컴플라이언스 C [m/N](접촉 법선 성분 가중 포함),
/// 연관 구조 모드 진동수 [Hz](국부 = NaN, 항상 포함), 접촉 동안 응답할 수 있어 직렬 사슬에 넣었는지, 식(값 포함).
/// </summary>
public sealed record ContactElement(string Body, string Kind, double Compliance, double ModeHz, bool Included, string Formula)
{
    public double K => Compliance > 0 ? 1 / Compliance : double.PositiveInfinity;

    public JsonObject ToJson() => new()
    {
        ["body"] = Body, ["kind"] = Kind, ["k_Npm"] = double.IsFinite(K) ? K : null, ["compliance_mpN"] = Compliance,
        ["modeHz"] = double.IsFinite(ModeHz) ? ModeHz : null, ["included"] = Included, ["formula"] = Formula,
    };
}

/// <summary>탄성-소성 접촉 힘 상한 후보 하나: 물체(경도 = 무른 쪽 재질), 종류, 힘 [N], 식(값 포함).</summary>
public sealed record ContactLimit(string Body, string Kind, double Force, string Formula)
{
    public JsonObject ToJson() => new() { ["body"] = Body, ["kind"] = Kind, ["force_N"] = Force, ["formula"] = Formula };
}

/// <summary>접촉 한 번의 소성 상한: 지배 힘 F_lim(후보 최소) · 선형 경화 비 α(소성 구간 기울기/탄성 기울기, 0 = 완전 소성) · 후보 전부.</summary>
public sealed record PlasticLimit(double Force, double Hardening, IReadOnlyList<ContactLimit> Candidates)
{
    public ContactLimit? Governing => Candidates.Where(c => c.Force > 0 && double.IsFinite(c.Force)).MinBy(c => c.Force);
}

/// <summary>
/// 탄성-소성 접촉(collisions.contact.plasticity = elastoplastic) 설정과 힘 상한 계산. 접촉 힘 = 직렬 스프링 탄성력이되 패치 평균 압력이 경도에 닿거나
/// 벽이 국부 압괴 하중에 닿으면 그 값으로 제한(완전 소성, 또는 선형 경화 α) — 소성 변형(눌림)은 누적되고 제하는 같은 직렬 강성으로 탄성 회복
/// (Thornton 1997 구 탄성-완전소성 모델의 직렬 선형 스프링 꼴: 압력 상한 → 힘 상한, 제하 탄성, 영구 눌림 δ_p = δ_max − F_max/k).
/// <list type="bullet">
/// <item>경도 H = c_H·σ_y·(속도 배율), c_H 기본 2.8 — 완전 소성 압입 평균 압력 p_m ≈ 2.8–3.0·Y(Tabor 1951 <i>The Hardness of Metals</i>; Johnson 1985
/// <i>Contact Mechanics</i> §6.3). σ_y = 두 재질 중 무른 쪽. 패치 = 강성 국부 접촉과 같은 면적(두 벽 모서리 교차 w_A·w_B).</item>
/// <item>벽 압괴 = <see cref="ContactStructure.WallCrush"/>(Alexander 테두리 · DeRuntz–Hodge 링 — <see cref="ImpactDamage"/> 식 재사용).</item>
/// </list>
/// </summary>
public sealed class ContactPlasticityModel
{
    public double HardnessFactor { get; init; } = 2.8;
    public double Hardening { get; init; }
    public bool Crush { get; init; } = true;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static ContactPlasticityModel? FromJson(JsonObject? co)
    {
        if (co is null) return null;
        var mode = Scenario.J.Str(co, "plasticity", "elastic");
        if (mode is not ("elastic" or "elastoplastic")) throw new InvalidDataException($"collisions.contact.plasticity: elastic | elastoplastic ('{mode}')");
        if (mode == "elastic") return null;
        double h = Scenario.J.Num(co, "hardnessFactor", 2.8), a = Scenario.J.Num(co, "hardening", 0);
        if (!(h > 0)) throw new InvalidDataException("collisions.contact.hardnessFactor 은 양수");
        if (!(a >= 0 && a < 1)) throw new InvalidDataException("collisions.contact.hardening 은 0 ≤ α < 1");
        return new ContactPlasticityModel { HardnessFactor = h, Hardening = a, Crush = co["crush"]?.GetValue<bool>() ?? true };
    }

    public JsonObject ToJson() => new()
    {
        ["model"] = "elastoplastic", ["hardnessFactor"] = HardnessFactor, ["hardening"] = Hardening, ["crush"] = Crush,
        ["basis"] = "힘 상한 = min(경도 c_H·σ_y·패치, 벽 국부 압괴) — Tabor 1951 · Johnson 1985 §6.3 · Alexander 1960 · DeRuntz–Hodge 1963, 제하 탄성(Thornton 1997 꼴)",
    };

    /// <summary>접촉 한 번의 힘 상한 후보: 경도(무른 쪽) + 양쪽 벽 압괴. 재질이 하나도 없으면 null(탄성).</summary>
    public PlasticLimit? Limits((ContactStructure? S, Vec3 P, Vec3 N) a, (ContactStructure? S, Vec3 P, Vec3 N) b, double area, double vn)
    {
        var list = new List<ContactLimit>();
        var soft = new[] { a.S, b.S }.Where(s => s?.Material is { Yield: > 0 }).MinBy(s => s!.Material!.Yield);
        if (soft?.Material is { } m && area > 0)
        {
            double r = Math.Sqrt(area / Math.PI), rf = m.RateFactor(vn > 0 ? vn / (2 * r) : 0);
            list.Add(new ContactLimit(soft.Body, "접촉 압력 = 경도", HardnessFactor * m.Yield * rf * area, string.Format(Inv,
                "c_H·σ_y·(속도 배율)·A — c_H {0:G3}(완전 소성 압입 p_m ≈ 2.8–3·Y), σ_y {1:F0} MPa({2} — 무른 쪽), 배율 {3:F3}(ε̇ ≈ v/(2a) {4:G3} 1/s), A {5:G3} mm² → H {6:F0} MPa",
                HardnessFactor, m.Yield / 1e6, m.Key, rf, vn / (2 * r), area * 1e6, HardnessFactor * m.Yield * rf / 1e6)));
        }
        if (Crush)
            foreach (var (s, p, n) in new[] { a, b })
                if (s?.WallCrush(p, n, vn) is { } c) list.Add(c);
        if (list.Count == 0) return null;
        return new PlasticLimit(list.Min(x => x.Force), Hardening, list);
    }
}

/// <summary>
/// Thornton(1997, J. Appl. Mech. 64:383) 탄성-완전소성 구 충돌 — 검증 기준(문헌 해석해). 하중: Hertz F = (4/3)E*√R δ^1.5 → 최대 압력 p_y 에서
/// 항복(δ_y = (πp_y/2E*)²R, F_y = (4/3)E*√R δ_y^1.5) → 선형 F = F_y + πp_yR(δ − δ_y). 제하: Hertz(곡률 R_p = (4E*/3F*)·((2F*+F_y)/(2πp_y))^1.5,
/// δ_p = δ* − (3F*/(4E*√R_p))^(2/3)). 반발 e(V_y/V) 닫힌 식. 수치 적분은 순응 접촉 펄스와 같은 방식(RK4 단계 안 소성 상태 고정, 단계 뒤 갱신).
/// </summary>
public static class ThorntonSphere
{
    /// <summary>닫힌 식 e = √(6√3/5)·√(1 − (V_y/V)²/6)·[(V_y/V)/((V_y/V) + 2√(6/5 − (V_y/V)²/5))]^¼ (V ≥ V_y, 아래는 1).</summary>
    public static double Restitution(double vyOverV)
    {
        double r = vyOverV;
        if (r >= 1) return 1;
        return Math.Sqrt(6 * Math.Sqrt(3) / 5) * Math.Sqrt(1 - r * r / 6) * Math.Pow(r / (r + 2 * Math.Sqrt(6.0 / 5 - r * r / 5)), 0.25);
    }

    /// <summary>항복 시작 접근 속도 V_y = √(2W_y/m), W_y = (8/15)E*√R δ_y^2.5.</summary>
    public static double YieldVelocity(double eStar, double r, double py, double m)
    {
        double dy = Math.Pow(Math.PI * py / (2 * eStar), 2) * r;
        return Math.Sqrt(2 * (8.0 / 15) * eStar * Math.Sqrt(r) * Math.Pow(dy, 2.5) / m);
    }

    /// <summary>수치 적분 반발(유효 질량 m, 접근 v) — 반환 (e, 영구 눌림 δ_p, 최대 힘).</summary>
    public static (double E, double DeltaP, double FMax) Integrate(double eStar, double r, double py, double m, double v)
    {
        double dy = Math.Pow(Math.PI * py / (2 * eStar), 2) * r, fy = 4.0 / 3 * eStar * Math.Sqrt(r) * Math.Pow(dy, 1.5);
        double dMax = 0, fMax = 0, rp = r, dp = 0;
        double Load(double d) => d <= dy ? 4.0 / 3 * eStar * Math.Sqrt(r) * Math.Pow(Math.Max(d, 0), 1.5) : fy + Math.PI * py * r * (d - dy);
        double Force(double d) => d >= dMax ? Load(d) : 4.0 / 3 * eStar * Math.Sqrt(rp) * Math.Pow(Math.Max(d - dp, 0), 1.5);
        // 시간 척도: 탄성 Hertz 접촉 시간 ~ 2.87(m²/(R E*² v))^(1/5)
        double tc = 2.87 * Math.Pow(m * m / (r * eStar * eStar * v), 0.2), h = tc / 20000;
        double x = 0, u = v;
        for (int step = 0; step < 10_000_000; step++)
        {
            double Acc(double d) => -Force(d) / m;
            double k1x = u, k1v = Acc(x);
            double k2x = u + 0.5 * h * k1v, k2v = Acc(x + 0.5 * h * k1x);
            double k3x = u + 0.5 * h * k2v, k3v = Acc(x + 0.5 * h * k2x);
            double k4x = u + h * k3v, k4v = Acc(x + h * k3x);
            double xn = x + h / 6 * (k1x + 2 * k2x + 2 * k3x + k4x), un = u + h / 6 * (k1v + 2 * k2v + 2 * k3v + k4v);
            if (step > 0 && xn <= dp && un < 0)
            {
                double f = (x - dp) / (x - xn);
                return (-(u + (un - u) * f) / v, dp, fMax);
            }
            x = xn; u = un;
            if (x > dMax)
            {
                dMax = x; fMax = Load(x);
                if (x > dy)
                {
                    rp = 4 * eStar / (3 * fMax) * Math.Pow((2 * fMax + fy) / (2 * Math.PI * py), 1.5);
                    dp = dMax - Math.Pow(3 * fMax / (4 * eStar * Math.Sqrt(rp)), 2.0 / 3);
                }
            }
        }
        throw new InvalidOperationException("Thornton 적분이 끝나지 않았습니다.");
    }
}

/// <summary>
/// 자유-자유 균일 봉·보의 관성 완화 컴플라이언스(무차원, 길이 L = 1, 질량 균일): 하중점 ξ = x/L 에서 강체 운동(질량 가중 평균 병진·회전)을 뺀 탄성 변위.
/// 순응 접촉에서 '접촉점 ↔ 물체 강체 질량' 사이 구조 경로의 준정적 컴플라이언스(강체 충격량 식의 유효 질량과 같은 강체 기준).
/// 해석해: 봉 끝 하중 1/3 (L/(3EA)) · 보 끝 횡하중 1/105 (L³/(105EI)) · 보 끝 모멘트 회전 13/35 (13L/(35EI)) — 단위 시험.
/// </summary>
public static class BeamRelief
{
    private const int N = 2000;

    /// <summary>축하중(봉) 또는 비틀림 토크(같은 식): u_e(ξ)·EA/(F·L).</summary>
    public static double Bar(double xi)
    {
        xi = Math.Clamp(xi, 0, 1);
        // 내력 N(x) = x − H(x−ξ) (관성 하중 −1/길이 + 점하중 1), u = ∫N, 강체 = 평균
        var u = Cumulate(x => x - (x > xi ? 1 : 0));
        return Elastic(u, xi, false);
    }

    /// <summary>횡하중(보): w_e(ξ)·EI/(P·L³).</summary>
    public static double Bending(double xi)
    {
        xi = Math.Clamp(xi, 0, 1);
        double al = 12 * (xi - 0.5);
        // M(x) = −(x²/2 + α(x³/6 − x²/4)) + (x − ξ)H(x−ξ), w'' = M
        var th = Cumulate(x => -(x * x / 2 + al * (x * x * x / 6 - x * x / 4)) + (x > xi ? x - xi : 0));
        var w = Cumulate(th);
        return Elastic(w, xi, true);
    }

    /// <summary>집중 모멘트(보) → 그 점 회전: φ_e(ξ)·EI/(M·L).</summary>
    public static double Moment(double xi)
    {
        xi = Math.Clamp(xi, 0, 1);
        // α = 12, M(x) = −12(x³/6 − x²/4) − H(x−ξ)
        var th = Cumulate(x => -12 * (x * x * x / 6 - x * x / 4) - (x > xi ? 1 : 0));
        var w = Cumulate(th);
        // 강체 회전 B = 12 ∫ w (x − ½) dx, 탄성 회전 = w'(ξ) − B
        double h = 1.0 / N, b = 0;
        for (int i = 0; i <= N; i++) b += (i == 0 || i == N ? 0.5 : 1) * w[i] * (i * h - 0.5) * h;
        return Math.Abs(Interp(th, xi) - 12 * b);
    }

    private static double[] Cumulate(Func<double, double> f)
    {
        // 구간 중점 규칙 — 하중점 계단(H(x−ξ))을 절점에서 표본하지 않는다(ξ = 0·1 끝 하중 정확)
        var y = new double[N + 1];
        double h = 1.0 / N;
        for (int i = 1; i <= N; i++) y[i] = y[i - 1] + f((i - 0.5) * h) * h;
        return y;
    }

    private static double[] Cumulate(double[] g)
    {
        var y = new double[N + 1];
        double h = 1.0 / N;
        for (int i = 1; i <= N; i++) y[i] = y[i - 1] + 0.5 * (g[i - 1] + g[i]) * h;
        return y;
    }

    private static double Interp(double[] y, double xi)
    {
        double s = xi * N; int i = Math.Min((int)s, N - 1);
        return y[i] + (y[i + 1] - y[i]) * (s - i);
    }

    /// <summary>질량 가중(균일) 강체 성분 제거 후 ξ 값: 평균(병진) + 회전(rot 일 때 12∫w(x−½)).</summary>
    private static double Elastic(double[] y, double xi, bool rot)
    {
        double h = 1.0 / N, a = 0, b = 0;
        for (int i = 0; i <= N; i++)
        {
            double wt = (i == 0 || i == N ? 0.5 : 1) * h;
            a += wt * y[i];
            if (rot) b += wt * y[i] * (i * h - 0.5);
        }
        return Math.Abs(Interp(y, xi) - a - (rot ? 12 * b * (xi - 0.5) : 0));
    }
}

/// <summary>
/// 물체 하나의 접촉 구조 이상화(순응 접촉): 기본 형상(Body.ContactShape) + 벽 두께 + 재질 → 접촉점·법선(동체 좌표)의 구조 컴플라이언스 요소(닫힌 식, 등급 D).
/// Kind: tube(얇은 원통 관) | panel(원통 껍질 조각 — 꽃잎) | sphereShell | boxWall(얇은 판 상자) | solid(두께 없음 — 국부 접촉만) | rigid(재질 없음).
/// 요소: 막 퍼짐(면내 성분) · 봉 축 관성 완화 · 편심 축하중 보 굽힘 · 횡 보 굽힘 · 비틀림 · 국부 판 굽힘(벽 법선 성분) — 식은 각 요소 Formula 에.
/// 힌지·끈 같은 연결은 접촉점과 이 물체 질량 사이의 직렬 경로가 아니라 다른 물체로 가는 병렬 경로라 넣지 않는다(강체 충격량 식도 연결을 무시 — 같은 기준).
/// </summary>
public sealed class ContactStructure
{
    /// <summary>두께 없는(솔리드·상자) 물체의 국부 접촉 패치 폭 [m](가정).</summary>
    public const double SolidPatch = 0.01;

    public required string Body { get; init; }
    public required string Kind { get; init; }
    public required Shape Shape { get; init; }
    public Material? Material { get; init; }
    /// <summary>벽 두께 [m](솔리드 0).</summary>
    public double Thickness { get; init; }
    /// <summary>물체 고유 질량 [kg] — 구조 모드 진동수 추정(질량/길이).</summary>
    public double Mass { get; init; }
    public string Source { get; init; } = "";
    public double PatchWidth => Thickness > 0 ? Thickness : SolidPatch;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static ContactStructure? For(Body b)
    {
        var s = b.ContactShape ?? b.Shape;
        if (s is null) return null;
        if (s.Kind == ShapeKind.Mesh) s = new Shape { Kind = ShapeKind.Box, Size = s.Size, Center = s.Center };
        var mat = b.Structure?.Material;
        double t = s.Kind == ShapeKind.ShellSegment ? s.Thickness : b.Structure?.Thickness ?? 0;
        string kind = mat is null ? "rigid" : s.Kind switch
        {
            ShapeKind.ShellSegment => "panel",
            ShapeKind.Cylinder when t > 0 && t < s.Radius / 2 => "tube",
            ShapeKind.Sphere when t > 0 && t < s.Radius / 2 => "sphereShell",
            ShapeKind.Box when t > 0 && t < Math.Min(s.Size.X, Math.Min(s.Size.Y, s.Size.Z)) / 2 => "boxWall",
            _ => "solid",
        };
        return new ContactStructure
        {
            Body = b.Name, Kind = kind, Shape = s, Material = mat, Thickness = kind is "solid" or "rigid" ? 0 : t, Mass = b.OwnMass,
            Source = b.ContactShapeSource.Length > 0 ? b.ContactShapeSource : "shape",
        };
    }

    public string Describe()
    {
        var s = Shape;
        string geo = s.Kind switch
        {
            ShapeKind.Cylinder => string.Format(Inv, "원통 R {0:F0} mm L {1:F0} mm", s.Radius * 1e3, s.Length * 1e3),
            ShapeKind.ShellSegment => string.Format(Inv, "껍질 조각 R {0:F0} mm L {1:F0} mm {2:F0}°", s.Radius * 1e3, s.Length * 1e3, s.SpanDeg),
            ShapeKind.Sphere => string.Format(Inv, "구 R {0:F0} mm", s.Radius * 1e3),
            _ => string.Format(Inv, "상자 {0:F0}×{1:F0}×{2:F0} mm", s.Size.X * 1e3, s.Size.Y * 1e3, s.Size.Z * 1e3),
        };
        return $"{Body}: {Kind} — {geo}{(Thickness > 0 ? string.Format(Inv, " t {0:G3} mm", Thickness * 1e3) : "")}, {Material?.Key ?? "재질 없음"}, 형상 출처 {Source}";
    }

    /// <summary>접촉점 p·법선 n(이 물체 동체 좌표, |n| = 1)의 구조 컴플라이언스 요소. w0 = 상대 물체 패치 폭(막 퍼짐 시작 폭).</summary>
    public List<ContactElement> Elements(Vec3 p, Vec3 n, double w0)
    {
        var list = new List<ContactElement>();
        if (Material is null || Kind is "solid" or "rigid") return list;
        n = n.NormSq > 0 ? n.Normalized() : Vec3.UnitX;
        switch (Kind)
        {
            case "tube": case "panel": WallOfRevolution(list, p, n, w0); break;
            case "sphereShell": SphereShell(list, p, n, w0); break;
            case "boxWall": BoxWall(list, p, n, w0); break;
        }
        return list;
    }

    private void Add(List<ContactElement> l, string kind, double c, double hz, string formula)
    {
        if (c > 0 && double.IsFinite(c)) l.Add(new ContactElement(Body, kind, c, hz, true, formula));
    }

    /// <summary>얇은 벽 막 퍼짐(면내 점하중 → 45° 퍼짐, 띠 폭 w0 → 전개 폭 W): [ln(W/w0) − 1 + w0/W]/(s·E·t), s = 퍼지는 방향 수(옆 변 = 1).</summary>
    private static double Spread(double W, double w0, int sides, double E, double t) =>
        (Math.Log(W / w0) - 1 + w0 / W) / (sides * E * t);

    private void WallOfRevolution(List<ContactElement> l, Vec3 p, Vec3 n, double w0)
    {
        var m = Material!; var s = Shape;
        double E = m.E, nu = m.Nu, G = E / (2 * (1 + nu)), t = Thickness, L = s.Length;
        int ax = s.Axis;
        var (a, b, c) = Shape.AxisUnmap(ax, p - s.Center);
        double th = Math.Atan2(b, a);
        double Rm = s.Radius - t / 2;
        bool panel = Kind == "panel";
        double beta = panel ? s.SpanDeg * Math.PI / 360 : Math.PI, th0 = panel ? s.CenterAngleDeg * Math.PI / 180 : 0;
        double dth = Math.Atan2(Math.Sin(th - th0), Math.Cos(th - th0));
        var eAx = Shape.AxisMap(ax, 0, 0, 1);
        var eR = Shape.AxisMap(ax, Math.Cos(th), Math.Sin(th), 0);
        var eC = Shape.AxisMap(ax, -Math.Sin(th), Math.Cos(th), 0);
        double na = Vec3.Dot(n, eAx), nr = Vec3.Dot(n, eR), nc = Vec3.Dot(n, eC);
        double xi = Math.Clamp((c + L / 2) / L, 0, 1);
        double W = 2 * beta * Rm, A = W * t;
        // 단면(축 a·b 좌표): 도심 d(중앙 방사 방향), 2차 모멘트 텐서 S(도심 기준), 비틀림 상수 J, 전단 중심 e_sc(열린 원호 — 볼록면 쪽)
        double sb = Math.Sin(beta), cb = Math.Cos(beta);
        double d = panel ? Rm * sb / beta : 0;
        double Suu = Rm * Rm * Rm * t * (beta + sb * cb) - A * d * d, Svv = Rm * Rm * Rm * t * (beta - sb * cb);
        double c0 = Math.Cos(th0), s0 = Math.Sin(th0);
        double Saa = Suu * c0 * c0 + Svv * s0 * s0, Sbb = Suu * s0 * s0 + Svv * c0 * c0, Sab = (Suu - Svv) * c0 * s0;
        double det = Saa * Sbb - Sab * Sab;
        double Q(double x, double y) => (Sbb * x * x - 2 * Sab * x * y + Saa * y * y) / det;
        double J = panel ? W * t * t * t / 3 : 2 * Math.PI * Rm * Rm * Rm * t;
        double esc = panel ? 2 * Rm * (sb - beta * cb) / (beta - sb * cb) : 0;
        double csx = d * c0, csy = d * s0, scx = esc * c0, scy = esc * s0;
        double px = Rm * Math.Cos(th), py = Rm * Math.Sin(th);
        double mpl = Mass / L;
        double BendHz(double I) => 22.373 / (2 * Math.PI * L * L) * Math.Sqrt(E * I / mpl);
        string geo = string.Format(Inv, "R_m {0:F1} mm, t {1:G3} mm, L {2:F0} mm, ξ {3:F2}", Rm * 1e3, t * 1e3, L * 1e3, xi);

        // 1. 막 퍼짐(면내 성분: 축 + 둘레)
        double wm = na * na + nc * nc;
        if (wm > 1e-9)
        {
            double w = Math.Clamp(w0, 1e-4, W);
            bool side = panel && Rm * (beta - Math.Abs(dth)) < Math.Max(w, t);
            int sides = side ? 1 : 2;
            Add(l, "막 퍼짐(면내)", wm * Spread(W, w, sides, E, t), double.NaN,
                string.Format(Inv, "(n_a²+n_c²)·[ln(W/w₀) − 1 + w₀/W]/(s·E·t) — W {0:F0} mm, w₀ {1:F1} mm, s {2}{3}, {4}", W * 1e3, w * 1e3, sides, side ? "(옆 변)" : "", geo));
        }
        if (na * na > 1e-9)
        {
            // 2. 봉 축 관성 완화
            double g = BeamRelief.Bar(xi);
            Add(l, "봉 축(관성 완화)", na * na * g * L / (E * A), Math.Sqrt(E * A / mpl) / (2 * L),
                string.Format(Inv, "n_a²·g(ξ)·L/(E·A), g {0:F3}(끝 1/3), A {1:F0} mm²", g, A * 1e6));
            // 3. 편심 축하중 → 보 굽힘(끝 모멘트 F·e, 그 점 회전 × e)
            double ex = px - csx, ey = py - csy, qe = Q(ex, ey);
            if (qe > 0 && ex * ex + ey * ey > 1e-12)
            {
                double gm = BeamRelief.Moment(xi);
                Add(l, "편심 축하중 → 보 굽힘", na * na * gm * L * qe / E, BendHz((ex * ex + ey * ey) / qe),
                    string.Format(Inv, "n_a²·g_M(ξ)·L·eᵀS⁻¹e/E, g_M {0:F3}(끝 13/35), |e| {1:F0} mm(단면 도심 → 접촉점)", gm, Math.Sqrt(ex * ex + ey * ey) * 1e3));
            }
        }
        // 4. 횡 보 굽힘 · 5. 비틀림(단면 내 성분 n_t)
        double tx = nr * Math.Cos(th) - nc * Math.Sin(th), ty = nr * Math.Sin(th) + nc * Math.Cos(th);
        double wt = tx * tx + ty * ty;
        if (wt > 1e-9)
        {
            double qd = Q(tx, ty), gb = BeamRelief.Bending(xi);
            Add(l, "보 굽힘(횡, 관성 완화)", gb * L * L * L * qd / E, BendHz(wt / qd),
                string.Format(Inv, "g_b(ξ)·L³·n_tᵀS⁻¹n_t/E, g_b {0:F4}(끝 1/105), I {1:G3} mm⁴", gb, wt / qd * 1e12));
            double lever = (px - scx) * ty - (py - scy) * tx;
            double rg2 = (Saa + Sbb) / A + (scx - csx) * (scx - csx) + (scy - csy) * (scy - csy);
            if (Math.Abs(lever) > 1e-6)
            {
                double gt = BeamRelief.Bar(xi);
                Add(l, "비틀림(관성 완화)", gt * L * lever * lever / (G * J), Math.Sqrt(G * J / (mpl * rg2)) / (2 * L),
                    string.Format(Inv, "g(ξ)·L·r²/(G·J), 전단 중심 팔 r {0:F0} mm, J {1:G3} mm⁴({2})", Math.Abs(lever) * 1e3, J * 1e12, panel ? "열린 단면 Wt³/3 — 뒤틀림 구속 무시" : "닫힌 관 2πR³t"));
            }
        }
        // 6. 국부 판 굽힘(벽 법선 성분): 곡률이 가두는 영향 반경 ρ = π/β_s(β_s = [3(1−ν²)]^¼/√(R t)), 원판 중앙 집중하중 ρ²/(16πD), 자유 변 ×2·모서리 ×4
        if (nr * nr > 1e-9)
        {
            double bs = Math.Pow(3 * (1 - nu * nu), 0.25) / Math.Sqrt(Rm * t);
            double rho = Math.Min(Math.PI / bs, Math.Min(L, W) / 2);
            double D = E * t * t * t / (12 * (1 - nu * nu));
            int edges = (L / 2 - Math.Abs(c) < rho / 2 ? 1 : 0) + (panel && Rm * (beta - Math.Abs(dth)) < rho / 2 ? 1 : 0);
            double f = Math.Pow(2, edges);
            Add(l, "국부 판 굽힘(벽 법선)", nr * nr * f * rho * rho / (16 * Math.PI * D), 10.21 / (2 * Math.PI * rho * rho) * Math.Sqrt(D / (m.Density * t)),
                string.Format(Inv, "n_r²·f·ρ²/(16πD), ρ = π/β_s {0:F0} mm, D {1:G4} N·m, 자유 변 {2}개 → f {3:G2}", rho * 1e3, D, edges, f));
        }
    }

    private void SphereShell(List<ContactElement> l, Vec3 p, Vec3 n, double w0)
    {
        var m = Material!; var s = Shape;
        double E = m.E, nu = m.Nu, t = Thickness, Rm = s.Radius - t / 2;
        var er = (p - s.Center).NormSq > 1e-12 ? (p - s.Center).Normalized() : Vec3.UnitX;
        double nr = Vec3.Dot(n, er), wm = Math.Max(0, 1 - nr * nr);
        if (nr * nr > 1e-9)
            Add(l, "구 셸 점하중(Reissner)", nr * nr * Math.Sqrt(3 * (1 - nu * nu)) * Rm / (4 * E * t * t), double.NaN,
                string.Format(Inv, "n_r²·√(3(1−ν²))·R/(4Et²) — R_m {0:F1} mm, t {1:G3} mm", Rm * 1e3, t * 1e3));
        if (wm > 1e-9)
        {
            double W = 2 * Math.PI * Rm, w = Math.Clamp(w0, 1e-4, W);
            Add(l, "막 퍼짐(면내)", wm * Spread(W, w, 2, E, t), double.NaN, string.Format(Inv, "(1−n_r²)·[ln(W/w₀) − 1 + w₀/W]/(2Et), W {0:F0} mm", W * 1e3));
        }
    }

    private void BoxWall(List<ContactElement> l, Vec3 p, Vec3 n, double w0)
    {
        var m = Material!; var s = Shape;
        double E = m.E, nu = m.Nu, t = Thickness;
        var dd = p - s.Center; var hs = s.Size / 2;
        int k = 0; double best = -1;
        for (int i = 0; i < 3; i++) { double r = Math.Abs(dd[i]) / Math.Max(hs[i], 1e-9); if (r > best) { best = r; k = i; } }
        int i1 = (k + 1) % 3, i2 = (k + 2) % 3;
        double nk = n[k], wm = Math.Max(0, 1 - nk * nk);
        double D = E * t * t * t / (12 * (1 - nu * nu));
        double rho = Math.Min(s.Size[i1], s.Size[i2]) / 2;
        if (nk * nk > 1e-9)
            Add(l, "판 굽힘(상자 벽)", nk * nk * rho * rho / (16 * Math.PI * D), 10.21 / (2 * Math.PI * rho * rho) * Math.Sqrt(D / (m.Density * t)),
                string.Format(Inv, "n²·ρ²/(16πD) — 벽 중앙 고정 원판 근사, ρ {0:F0} mm, t {1:G3} mm", rho * 1e3, t * 1e3));
        if (wm > 1e-9)
        {
            double W = s.Size[i1] + s.Size[i2], w = Math.Clamp(w0, 1e-4, W);
            Add(l, "막 퍼짐(면내)", wm * Spread(W, w, 2, E, t), double.NaN, string.Format(Inv, "[ln(W/w₀) − 1 + w₀/W]/(2Et), W {0:F0} mm", W * 1e3));
        }
    }

    /// <summary>
    /// 얇은 벽 국부 압괴 하중(탄성-소성 접촉 힘 상한 후보, tube·panel 만 — 나머지 null). 접촉 법선의 축 성분이 반경 성분 이상이고 접촉점이 끝에서
    /// 축대칭 좌굴 반파장 1.72√(R t) 안이면 끝 테두리 압괴 = Alexander(1960) 6·σ₀·t^1.5·√D × 몫(관 테두리 ¼ — <see cref="ImpactDamage"/> Rim 과 같은 몫,
    /// 꽃잎 min(¼, 폭각/360°)), 반경 성분이 크면 링 압괴 σ₀·t²·L_eff/R(DeRuntz &amp; Hodge 1963, L_eff = 국부 판 굽힘 영향 지름 2ρ — 강성 요소와 같은 ρ).
    /// σ₀ = (σy+σu)/2 × 변형률 속도 배율(ε̇ ≈ v_n/(2·1.72√(R t))). 평균 압괴력이다 — 첫 접힘 최대는 더 클 수 있다(식에 적는다). 벽 중간의 면내 하중은 압괴 상한 없음(null).
    /// </summary>
    public ContactLimit? WallCrush(Vec3 p, Vec3 n, double vn)
    {
        if (Material is not { } m || Kind is not ("tube" or "panel")) return null;
        var s = Shape;
        double t = Thickness, L = s.Length, Rm = s.Radius - t / 2;
        int ax = s.Axis;
        var (a, b, c) = Shape.AxisUnmap(ax, p - s.Center);
        double th = Math.Atan2(b, a);
        n = n.NormSq > 0 ? n.Normalized() : Vec3.UnitX;
        double na = Math.Abs(Vec3.Dot(n, Shape.AxisMap(ax, 0, 0, 1))), nr = Math.Abs(Vec3.Dot(n, Shape.AxisMap(ax, Math.Cos(th), Math.Sin(th), 0)));
        double hf = 1.72 * Math.Sqrt(Rm * t);
        double rf = m.RateFactor(vn > 0 ? vn / (2 * hf) : 0);
        double s0 = 0.5 * (m.Yield + m.Ultimate) * rf;
        bool panel = Kind == "panel";
        if (na >= nr)
        {
            if (L / 2 - Math.Abs(c) > hf) return null;
            double share = panel ? Math.Min(0.25, s.SpanDeg / 360) : 0.25;
            double f = ImpactDamage.TubeAxialMeanCrush(s0, t, 2 * s.Radius) * share;
            return new ContactLimit(Body, "벽 끝 테두리 압괴(Alexander)", f, string.Format(Inv,
                "6·σ₀·t^1.5·√D × {0:G3} — σ₀ (σy+σu)/2 × 속도 배율 {1:F3} = {2:F0} MPa, t {3:G3} mm, D {4:F0} mm({5}; 평균 압괴력 — 첫 접힘 최대는 더 큼)",
                share, rf, s0 / 1e6, t * 1e3, 2 * s.Radius * 1e3, panel ? "꽃잎 몫 min(¼, 폭각/360°)" : "관 테두리 몫 ¼ = ImpactDamage Rim"));
        }
        double bs = Math.Pow(3 * (1 - m.Nu * m.Nu), 0.25) / Math.Sqrt(Rm * t);
        double W = panel ? s.SpanDeg * Math.PI / 180 * Rm : 2 * Math.PI * Rm;
        double rho = Math.Min(Math.PI / bs, Math.Min(L, W) / 2);
        double fr = ImpactDamage.RingCrush(s0, t, 2 * rho, Rm);
        return new ContactLimit(Body, "벽 링 압괴(DeRuntz–Hodge)", fr, string.Format(Inv,
            "σ₀·t²·L_eff/R — σ₀ {0:F0} MPa(속도 배율 {1:F3}), t {2:G3} mm, L_eff = 2ρ {3:F0} mm(국부 판 굽힘 영향 지름), R_m {4:F1} mm(강소성 붕괴 하중)",
            s0 / 1e6, rf, t * 1e3, 2 * rho * 1e3, Rm * 1e3));
    }

    /// <summary>국부 접촉(두 벽 모서리 교차 패치): 평평한 원형 압자 k = 2E*·a, a = √(w_A·w_B/π), 1/E* = Σ(1−ν²)/E.</summary>
    public static (ContactElement Element, double Area) Local(ContactStructure? A, ContactStructure? B)
    {
        double inv = 0; bool any = false;
        foreach (var s in new[] { A, B })
            if (s?.Material is { } m) { inv += (1 - m.Nu * m.Nu) / m.E; any = true; }
        string note = "";
        if (!any) { inv = 2 * (1 - 0.33 * 0.33) / 70e9; note = " (두 물체 모두 재질 없음 — Al 가정)"; }
        double es = 1 / inv;
        double wa = A?.PatchWidth ?? SolidPatch, wb = B?.PatchWidth ?? SolidPatch, area = wa * wb, a = Math.Sqrt(area / Math.PI);
        return (new ContactElement($"{A?.Body ?? "?"}↔{B?.Body ?? "?"}", "국부 접촉(압자)", 1 / (2 * es * a), double.NaN, true,
            string.Format(Inv, "1/(2E*·a), a = √(w_A·w_B/π) = {0:F2} mm(패치 {1:G3}×{2:G3} mm), E* {3:F1} GPa{4}", a * 1e3, wa * 1e3, wb * 1e3, es / 1e9, note)), area);
    }
}

/// <summary>
/// 무차원 접촉 펄스(선형 스프링 + 침투 비례 감쇠 Hunt–Crossley 꼴): x'' = −x(1 + q·x'), 힘 ≥ 0, x(0) = 0, x'(0) = 1 (시간 단위 1/ω, ω = √(k/m)).
/// q = 반발 e 가 나오도록 같은 적분기로 이분법(e = 1 → q = 0: 반 사인, 지속 π). 힘은 0 에서 시작해 0 으로 끝난다(점성 감쇠의 시작 계단·끝 인장 없음).
/// </summary>
public sealed record MasterPulse(double Restitution, double Q, double TEnd, double PeakF, double[] T, double[] F, double ReboundError)
{
    /// <summary>무차원 힘 상한 F̃_lim = F_lim/(v√(km)) (∞ = 탄성) · 선형 경화 β = α/(1−α)(소성 변형 x̃_p 당 상한 증가).</summary>
    public double Cap { get; init; } = double.PositiveInfinity;
    public double Beta { get; init; }
    /// <summary>이 펄스의 실제 되튐 속도비(탄성 = 목표 e ± 이분법 오차).</summary>
    public double Rebound { get; init; } = double.NaN;
    /// <summary>영구 소성 변형 x̃_p(눌림, 단위 v/ω) · 최대 접근 x̃_max.</summary>
    public double PlasticSet { get; init; }
    public double XMax { get; init; }
    /// <summary>소성 일 ∫F̃ dx̃_p (단위 m·v² — 운동에너지 ½ 대비).</summary>
    public double PlasticWork { get; init; }
    /// <summary>감쇠 없이(q = 0) 소성 소산만으로 나는 반발 e_p(탄성 = NaN). e_p &lt; 목표 e 면 감쇠 0 이고 펄스 되튐 = e_p(6DOF e 가 소성 소산보다 큼).</summary>
    public double PlasticRestitution { get; init; } = double.NaN;
    public bool Plastic => PlasticSet > 0;
    /// <summary>소성 소산만으로 6DOF 반발 e 보다 낮은 되튐 — 펄스 운동량 (1+e_p)mv &lt; 6DOF (1+e)mv.</summary>
    public bool Mismatch => PlasticRestitution < Restitution - 1e-9;
}

/// <summary>
/// 순응 접촉 한 번의 결과: 직렬 강성 요소(접촉 동안 응답 가능한 경로만 포함 — 느린 구조 모드는 질량처럼 거동해 제외, 준정적 전부 값도 기록),
/// 유효 질량(강체 충격량 식과 같은 1/k 인자), 접근 속도, 반발 → 접촉력 이력 F(t) = v√(km)·F̃(ωt). ∫F dt = (1+e)·m·v (충격량 모델과 같은 운동량 교환).
/// Shape(t) = F(t)/∫F (단위 1/s, 면적 1) — 하중 연구가 Δv 를 이 모양으로 되살린다.
/// </summary>
public sealed class ContactPulse
{
    public required List<ContactElement> Elements { get; init; }
    public double MEff { get; init; }
    public double Vn { get; init; }
    public double Restitution { get; init; }
    /// <summary>배율(collisions.contact.stiffnessScale — 불확도 MC·스윕) 적용 후 직렬 강성 [N/m].</summary>
    public double K { get; init; }
    /// <summary>준정적 전부(느린 모드 포함) 직렬 강성 × 배율 [N/m] — 무른 쪽 참고값.</summary>
    public double KQuasiStatic { get; init; }
    public double Scale { get; init; } = 1;
    public double Duration { get; init; }
    /// <summary>힘이 실린 구간 [s] — 최대의 1 % 를 넘는 마지막 시각(되튐 이동 제외).</summary>
    public double ActiveDuration { get; init; }
    public double PeakForce { get; init; }
    /// <summary>국부 접촉 패치 면적 [m²] — 최대 접촉 압력 = PeakForce/Area (재질 경도 ≈ 3σ_y 와 비교).</summary>
    public double PatchArea { get; init; }
    public double[] T { get; init; } = [];
    public double[] Shape { get; init; } = [];
    /// <summary>탄성-소성 힘 상한(null = 탄성 접촉).</summary>
    public PlasticLimit? Plasticity { get; init; }
    /// <summary>무차원 펄스(소성 상태·되튐 포함).</summary>
    public MasterPulse? Dimensionless { get; init; }
    /// <summary>영구 소성 변형(접촉 자국 눌림 깊이) [m] · 최대 접근 [m].</summary>
    public double Indentation { get; init; }
    public double MaxApproach { get; init; }
    public bool Plastic => Indentation > 0;
    /// <summary>같은 강성·질량·속도의 탄성 펄스 최대 힘 [N](상한이 없었다면).</summary>
    public double ElasticPeakForce { get; init; }
    /// <summary>펄스 실제 되튐(소성 + 감쇠) — 6DOF 와 같으면 Restitution.</summary>
    public double Rebound { get; init; } = double.NaN;
    /// <summary>소성 소산만의 반발 e_p(감쇠 0) — 탄성 접촉 NaN.</summary>
    public double PlasticRestitution { get; init; } = double.NaN;
    /// <summary>소성 일 [J].</summary>
    public double PlasticWork { get; init; }
    /// <summary>양쪽 물체 이상화·접촉점·법선(동체 좌표) — CalculiX 교차 확인용.</summary>
    public (ContactStructure? S, Vec3 P, Vec3 N, double W0) SideA { get; init; }
    public (ContactStructure? S, Vec3 P, Vec3 N, double W0) SideB { get; init; }

    private static readonly ConcurrentDictionary<long, MasterPulse> Cache = new();
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public double ContactFrequencyHz => Duration > 0 ? 1 / (2 * Duration) : double.NaN;

    /// <summary>모양 값(1/s) — 접촉 시작 기준 시각 t, 범위 밖 0.</summary>
    public double ShapeAt(double t)
    {
        if (T.Length < 2 || t < 0 || t > T[^1]) return 0;
        int i = Array.BinarySearch(T, t);
        if (i >= 0) return Shape[i];
        i = ~i;
        if (i <= 0) return Shape[0];
        if (i >= T.Length) return Shape[^1];
        double f = (t - T[i - 1]) / (T[i] - T[i - 1]);
        return Shape[i - 1] + (Shape[i] - Shape[i - 1]) * f;
    }

    /// <summary>무차원 펄스(캐시) — 반발 e ∈ [0.01, 1].</summary>
    public static MasterPulse Master(double e)
    {
        e = Math.Clamp(e, 0.01, 1);
        long key = (long)Math.Round(e * 1e6);
        return Cache.GetOrAdd(key, _ =>
        {
            if (e >= 1 - 1e-9) return Integrate(0);
            double lo = 0, hi = 1;
            while (Integrate(hi, false).Restitution > e && hi < 1e4) hi *= 2;
            for (int it = 0; it < 50; it++)
            {
                double mid = 0.5 * (lo + hi);
                if (Integrate(mid, false).Restitution > e) lo = mid; else hi = mid;
            }
            var r = Integrate(0.5 * (lo + hi));
            return r with { ReboundError = r.Restitution - e, Restitution = e };
        });
    }

    private static readonly ConcurrentDictionary<(long, long, long), MasterPulse> PlasticCache = new();

    /// <summary>
    /// 탄성-소성 무차원 펄스: 힘 상한 cap(F̃_lim) · 선형 경화 β. 탄성 펄스 최대가 상한 이하면 탄성 펄스 그대로(같은 수 — 탄성 한계 아래).
    /// 넘으면: 감쇠 없는(q = 0) 소성 펄스의 되튐 e_p ≥ e 면 전체 되튐 = e 가 되게 q 이분법, e_p &lt; e 면 q = 0(되튐 e_p, Mismatch).
    /// </summary>
    public static MasterPulse Master(double e, double cap, double beta = 0)
    {
        var el = Master(e);
        if (!(cap < el.PeakF)) return el;
        e = Math.Clamp(e, 0.01, 1);
        var key = ((long)Math.Round(e * 1e6), (long)Math.Round(Math.Log(cap) * 1e6), (long)Math.Round(beta * 1e6));
        if (PlasticCache.Count > 20000) PlasticCache.Clear();
        return PlasticCache.GetOrAdd(key, _ =>
        {
            var p0 = Integrate(0, false, cap, beta);
            double ep = p0.Rebound;
            if (ep <= e)
            {
                var r0 = Integrate(0, true, cap, beta);
                return r0 with { Restitution = e, ReboundError = 0, PlasticRestitution = ep };
            }
            double lo = 0, hi = 1;
            while (Integrate(hi, false, cap, beta).Rebound > e && hi < 1e4) hi *= 2;
            for (int it = 0; it < 50; it++)
            {
                double mid = 0.5 * (lo + hi);
                if (Integrate(mid, false, cap, beta).Rebound > e) lo = mid; else hi = mid;
            }
            var r = Integrate(0.5 * (lo + hi), true, cap, beta);
            return r with { ReboundError = r.Rebound - e, Restitution = e, PlasticRestitution = ep };
        });
    }

    /// <summary>
    /// x'' = −F, F = max(0, (x − x_p)(1 + q x')) RK4(Δ = π/2000), 탄성 변형 x − x_p 가 0 아래로 내려가는 순간까지(선형 보간). 반환 Restitution = Rebound = 되튐 속도.
    /// 탄성-소성(cap 유한): F ≤ cap + β·x_p — RK4 단계 안에서는 x_p 고정(상한을 넘는 시험 상태는 그 상태의 반환 사상 힘), 단계 뒤 반환 사상 x_p = ((1+qv)x − cap)/((1+qv) + β)(시험 해가 상한을 넘을 때만,
    /// x_p 는 줄지 않는다). 제하는 같은 강성의 탄성(영구 눌림 x_p 남김). cap = ∞ 면 x_p = 0 — 탄성 펄스와 같은 수.
    /// </summary>
    public static MasterPulse Integrate(double q, bool keep = true, double cap = double.PositiveInfinity, double beta = 0)
    {
        double xp = 0, work = 0, xmax = 0;
        double Acc(double x, double v)
        {
            double f = (x - xp) * (1 + q * v);
            // 상한을 넘는 시험 상태 → 그 상태의 반환 사상 힘(경화: cap + β·x_p'(x, v), 완전 소성: cap) — 단계 안 단조 하중이면 정확
            if (f > cap + beta * xp) { double s = 1 + q * v; f = cap + beta * Math.Max(xp, (s * x - cap) / (s + beta)); }
            return -Math.Max(0, f);
        }
        double h = Math.PI / 2000, x = 0, v = 1, t = 0, peak = 0;
        // 긴 소성 평탄(상한 ≪ 1)은 표본만 솎는다(적분 간격은 그대로) — 저장 2만 점 안팎
        int stride = keep && cap < 1 ? Math.Max(1, (int)Math.Ceiling((Math.PI + 1 / cap) / h / 20000)) : 1;
        var ts = new List<double> { 0 }; var fs = new List<double> { 0 };
        for (int step = 0; step < 50_000_000; step++)
        {
            double k1x = v, k1v = Acc(x, v);
            double k2x = v + 0.5 * h * k1v, k2v = Acc(x + 0.5 * h * k1x, v + 0.5 * h * k1v);
            double k3x = v + 0.5 * h * k2v, k3v = Acc(x + 0.5 * h * k2x, v + 0.5 * h * k2v);
            double k4x = v + h * k3v, k4v = Acc(x + h * k3x, v + h * k3v);
            double xn = x + h / 6 * (k1x + 2 * k2x + 2 * k3x + k4x), vn = v + h / 6 * (k1v + 2 * k2v + 2 * k3v + k4v);
            if (xn - xp <= 0 && step > 0)
            {
                double f = (x - xp) / (x - xn);
                double te = t + f * h, ve = v + (vn - v) * f;
                if (keep) { ts.Add(te); fs.Add(0); }
                return new MasterPulse(-ve, q, te, peak, ts.ToArray(), fs.ToArray(), 0)
                    { Cap = cap, Beta = beta, Rebound = -ve, PlasticSet = xp, XMax = xmax, PlasticWork = work };
            }
            x = xn; v = vn; t += h;
            xmax = Math.Max(xmax, x);
            if ((x - xp) * (1 + q * v) > cap + beta * xp)
            {
                double xp2 = ((1 + q * v) * x - cap) / ((1 + q * v) + beta);
                if (xp2 > xp)
                {
                    work += (cap + beta * 0.5 * (xp + xp2)) * (xp2 - xp);
                    xp = xp2;
                }
            }
            double F = Math.Max(0, (x - xp) * (1 + q * v));
            peak = Math.Max(peak, F);
            if (keep && (stride == 1 || step % stride == 0)) { ts.Add(t); fs.Add(F); }
        }
        throw new InvalidOperationException("접촉 펄스 적분이 끝나지 않았습니다.");
    }

    /// <summary>
    /// 직렬 강성 → 펄스. elements = 국부 접촉 + 양쪽 구조 요소(모두). 배율 scale 은 직렬 강성에 곱한다.
    /// 포함 규칙(반복): 모드 진동수가 접촉 진동수 1/(2τ) 보다 낮은 경로는 접촉 동안 응답하지 못해(질량처럼 거동) 직렬에서 뺀다 — G 높은 쪽(보수).
    /// </summary>
    public static ContactPulse Build(IReadOnlyList<ContactElement> elements, double mEff, double vn, double e, double scale, double patchArea,
        (ContactStructure?, Vec3, Vec3, double) sideA = default, (ContactStructure?, Vec3, Vec3, double) sideB = default, PlasticLimit? plastic = null)
    {
        // 탄성-소성: 무차원 상한 F̃ = F_lim/(v√(km)) 은 강성에 따라 바뀐다 — 포함 경로 반복 안에서 펄스도 다시
        double beta = plastic is null ? 0 : plastic.Hardening / (1 - plastic.Hardening);
        MasterPulse For(double kk) => plastic is null || !(vn > 0) ? Master(e) : Master(e, plastic.Force / (vn * Math.Sqrt(kk * mEff)), beta);
        scale = scale > 0 ? scale : 1;
        double cAll = elements.Sum(x => x.Compliance);
        double kQs = scale / cAll;
        var inc = elements.Select(_ => true).ToArray();
        double k = kQs;
        var mp = For(k);
        for (int it = 0; it < 12; it++)
        {
            mp = For(k);
            double tau = mp.TEnd / Math.Sqrt(k / mEff), fc = 1 / (2 * tau);
            var next = elements.Select(x => !double.IsFinite(x.ModeHz) || x.ModeHz >= fc).ToArray();
            double c = elements.Where((x, i) => next[i]).Sum(x => x.Compliance);
            double k2 = scale / c;
            bool same = next.SequenceEqual(inc);
            inc = next; k = k2;
            if (same) break;
        }
        mp = For(k);
        double w = Math.Sqrt(k / mEff);
        double fScale = vn * Math.Sqrt(k * mEff);
        // 모양 = F/J(면적 1): 소성 소산만으로 되튐이 6DOF e 보다 낮으면(Mismatch) 펄스 자신의 운동량 (1+e_p)mv 로 — 하중 연구가 6DOF Δv 면적으로 다시 맞춘다
        double j = (1 + (mp.Mismatch ? mp.Rebound : mp.Restitution)) * mEff * vn;
        int last = Array.FindLastIndex(mp.F, f => f > 0.01 * mp.PeakF);
        double lv = vn / w;
        return new ContactPulse
        {
            Elements = elements.Select((x, i) => x with { Included = inc[i] }).ToList(),
            MEff = mEff, Vn = vn, Restitution = mp.Restitution, K = k, KQuasiStatic = kQs, Scale = scale,
            Duration = mp.TEnd / w, ActiveDuration = (last >= 0 ? mp.T[last] : mp.TEnd) / w, PeakForce = mp.PeakF * fScale, PatchArea = patchArea,
            T = mp.T.Select(t => t / w).ToArray(), Shape = mp.F.Select(f => f * fScale / j).ToArray(),
            SideA = sideA, SideB = sideB,
            Plasticity = plastic, Dimensionless = mp, Indentation = mp.PlasticSet * lv, MaxApproach = mp.XMax * lv,
            ElasticPeakForce = Master(e).PeakF * fScale, Rebound = mp.Rebound, PlasticRestitution = mp.PlasticRestitution,
            PlasticWork = mp.PlasticWork * mEff * vn * vn,
        };
    }

    /// <summary>같은 접촉(요소·유효 질량·접근 속도·반발·소성 상한)을 강성 배율 factor 로 다시(6DOF 재실행 없이 — 운동량 교환은 같다). 힘 상한은 재질·형상 값이라 그대로.</summary>
    public ContactPulse Rescaled(double factor) =>
        Build(Elements.Select(x => x with { Included = true }).ToList(), MEff, Vn, Restitution, Scale * factor, PatchArea, SideA, SideB, Plasticity);

    /// <summary>같은 접촉을 탄성(힘 상한 없음)으로 — 탄성-소성 비교(같은 6DOF 실행). 탄성 접촉이면 자신.</summary>
    public ContactPulse Elastic(double factor = 1) => Plasticity is null && Math.Abs(factor - 1) < 1e-12 ? this
        : Build(Elements.Select(x => x with { Included = true }).ToList(), MEff, Vn, Restitution, Scale * factor, PatchArea, SideA, SideB);

    /// <summary>
    /// 접촉 쌍 → 펄스: 국부 접촉 + A·B 구조 요소(접촉점 p·법선 n 은 월드 NED → 각 물체 동체 좌표). plasticity ≠ null 이면 탄성-소성(힘 상한 후보 계산).
    /// </summary>
    public static ContactPulse Compute(Body a, ContactStructure? sa, Body b, ContactStructure? sb, Vec3 pWorld, Vec3 nWorld, double mEff, double vn, double e, double scale,
        ContactPlasticityModel? plasticity = null)
    {
        var (loc, area) = ContactStructure.Local(sa, sb);
        var els = new List<ContactElement> { loc };
        var pa = a.WorldToBrf(pWorld); var na = a.WorldQ.InverseRotate(nWorld);
        var pb = b.WorldToBrf(pWorld); var nb = b.WorldQ.InverseRotate(nWorld);
        double wa = sa?.PatchWidth ?? ContactStructure.SolidPatch, wb = sb?.PatchWidth ?? ContactStructure.SolidPatch;
        if (sa is not null) els.AddRange(sa.Elements(pa, na, wb));
        if (sb is not null) els.AddRange(sb.Elements(pb, nb, wa));
        var lim = plasticity?.Limits((sa, pa, na), (sb, pb, nb), area, vn);
        return Build(els, mEff, vn, e, scale, area, (sa, pa, na, wb), (sb, pb, nb, wa), lim);
    }

    public JsonObject ToJson() => new()
    {
        ["k_Npm"] = K, ["kQuasiStaticAll_Npm"] = KQuasiStatic, ["stiffnessScale"] = Scale, ["mEff_kg"] = MEff, ["approach_mps"] = Vn, ["restitution"] = Restitution,
        ["duration_ms"] = Duration * 1e3, ["activeDuration_ms"] = ActiveDuration * 1e3, ["peakForce_N"] = PeakForce, ["contactFrequency_Hz"] = ContactFrequencyHz,
        ["patchArea_mm2"] = PatchArea * 1e6, ["peakPressure_MPa"] = PatchArea > 0 ? PeakForce / PatchArea / 1e6 : null,
        ["structureA"] = SideA.S?.Describe(), ["structureB"] = SideB.S?.Describe(),
        ["elements"] = new JsonArray(Elements.Select(x => (JsonNode)x.ToJson()).ToArray()),
        ["plasticity"] = PlasticityJson(),
        ["model"] = Plasticity is null
            ? "선형 직렬 스프링 + 침투 비례 감쇠(Hunt–Crossley 꼴, 반발 e 일치) — 접촉 동안 RK4 적분, 운동량 교환 = 충격량 모델"
            : "탄성-소성 직렬 스프링(힘 상한 = min(경도·패치, 벽 압괴), 소성 눌림 누적, 제하 탄성 — Thornton 1997 꼴) + 침투 비례 감쇠(전체 되튐 = 6DOF e) — 접촉 동안 RK4 적분",
    };

    private JsonObject? PlasticityJson()
    {
        if (Plasticity is not { } pl) return null;
        var g = pl.Governing;
        return new JsonObject
        {
            ["forceLimit_N"] = pl.Force, ["governing"] = g?.Kind, ["governingBody"] = g?.Body, ["hardening"] = pl.Hardening,
            ["candidates"] = new JsonArray(pl.Candidates.Select(c => (JsonNode)c.ToJson()).ToArray()),
            ["plastic"] = Plastic, ["indentation_mm"] = Indentation * 1e3, ["maxApproach_mm"] = MaxApproach * 1e3,
            ["elasticPeakForce_N"] = ElasticPeakForce, ["forceRatio"] = ElasticPeakForce > 0 ? PeakForce / ElasticPeakForce : null,
            ["plasticWork_J"] = PlasticWork, ["plasticWorkFraction"] = MEff * Vn * Vn > 0 ? PlasticWork / (0.5 * MEff * Vn * Vn) : null,
            ["restitution6dof"] = Restitution, ["rebound"] = double.IsFinite(Rebound) ? Rebound : null,
            ["plasticRestitution"] = double.IsFinite(PlasticRestitution) ? PlasticRestitution : null,
            ["restitutionMismatch"] = Dimensionless?.Mismatch ?? false,
            ["momentumRatio"] = double.IsFinite(Rebound) ? (1 + Rebound) / (1 + Restitution) : null,
            ["note"] = Dimensionless?.Mismatch == true
                ? "소성 소산만으로 되튐 e_p < 6DOF e — 감쇠 0, 펄스 운동량 (1+e_p)mv; 하중 연구는 6DOF Δv((1+e)mv) 면적으로 모양을 맞춘다(보수)"
                : Plastic ? "소성 소산 e_p ≥ 6DOF e — 나머지 손실은 침투 비례 감쇠(전체 되튐 = 6DOF e, 운동량 교환 = 충격량 모델)" : "탄성 한계 아래 — 탄성 펄스 그대로",
        };
    }

    public string Summary() => string.Format(Inv, "k {0:G3} N/m(준정적 전부 {1:G3}), m_eff {2:F2} kg, τ {3:F2} ms, F_max {4:F0} N", K, KQuasiStatic, MEff, Duration * 1e3, PeakForce)
        + (Plasticity is { } pl ? Plastic
            ? string.Format(Inv, " | 소성: 상한 {0:F0} N({1}) — 눌림 {2:F3} mm, 탄성이면 {3:F0} N, 소성 반발 e_p {4:F2}", pl.Force, pl.Governing?.Kind, Indentation * 1e3, ElasticPeakForce, PlasticRestitution)
            : string.Format(Inv, " | 탄성 한계 아래(상한 {0:F0} N {1})", pl.Force, pl.Governing?.Kind) : "");
}
