using System.Text.Json.Nodes;
using Sim.Core.Geometry;
using Atmosphere = Sim.Core.Environment.Atmosphere;
using Sim.Core.Models;
using Sim.Core.Numerics;

namespace Sim.Core.Structures;

/// <summary>착지 접촉 형태: 상자 면/모서리/꼭짓점, 원통 끝면/테두리/옆면, 구·기타 점.</summary>
public enum ContactKind { Face, Edge, Corner, End, Rim, Side, Point }

/// <summary>착지 충돌 재질 판정 결과(1·2단계, FR-MAT-02~04 를 지면 충돌에 적용). 근사 — FEA 아님.</summary>
public sealed class ImpactAssessment
{
    public required string BodyName { get; init; }
    public required Material Material { get; init; }
    /// <summary>thinWall | solid | foam</summary>
    public required string Model { get; init; }
    public ContactKind Contact { get; init; }
    /// <summary>접촉점(동체 좌표, 모델 원점 기준) — 손상 위치 표시용.</summary>
    public Vec3 ContactPointBrf { get; init; }
    /// <summary>착지 순간 지면 방향(동체 좌표, 단위벡터).</summary>
    public Vec3 DownBrf { get; init; } = Vec3.UnitZ;
    public double Time { get; init; } = double.NaN;
    public double Mass { get; init; }
    public double NormalSpeed { get; init; }
    public double TangentialSpeed { get; init; }
    /// <summary>법선 운동에너지 ½·m·v_n² [J] — 구조가 흡수해야 하는 몫(접선 성분은 미끄러짐·구름으로 가정).</summary>
    public double Energy { get; init; }
    public double Thickness { get; init; }
    public double StrainRate { get; init; }
    public double RateFactor { get; init; } = 1;
    /// <summary>탄성 변형에너지 용량 [J](thinWall) — 이 값 이하면 영구 손상 없음.</summary>
    public double ElasticCapacity { get; init; } = double.NaN;
    /// <summary>평균 압궤력 [N](연성 thinWall · foam).</summary>
    public double CrushForce { get; init; } = double.NaN;
    /// <summary>찌그러짐(압궤) 깊이 [m]. 손상 없으면 0, 해당 없음 NaN.</summary>
    public double DentDepth { get; init; } = double.NaN;
    /// <summary>압궤 가능 길이 [m](이를 넘으면 구조 붕괴·찢어짐 또는 폼 바닥 침).</summary>
    public double CrushLimit { get; init; } = double.NaN;
    /// <summary>접힘 반파장 H [m](Wierzbicki) — 손상 영역 크기.</summary>
    public double FoldLength { get; init; } = double.NaN;
    /// <summary>solid: 충돌 응력 [Pa](1D 파동 ρcv 와 지면 한계 중 작은 값).</summary>
    public double PeakStress { get; init; } = double.NaN;
    /// <summary>solid 연성: 영구 단축량 [m](Taylor).</summary>
    public double Shortening { get; init; } = double.NaN;
    /// <summary>안전계수(응력 기준, thinWall 은 √(용량/에너지)).</summary>
    public double Safety { get; init; } = double.NaN;
    /// <summary>손상 시작 법선 속도 [m/s] — 이 속도 이상이면 영구 변형(연성) 또는 깨짐(취성).</summary>
    public double DamageSpeed { get; init; } = double.NaN;
    /// <summary>붕괴(전면 압궤·찢어짐·바닥 침) 법선 속도 [m/s] — 연성·폼만.</summary>
    public double CollapseSpeed { get; init; } = double.NaN;
    public double PeakG { get; init; } = double.NaN;
    public FailureState State { get; init; }
    public required string Verdict { get; init; }
    public required string Method { get; init; }

    /// <summary>손상 영역 반경 [m](면 색 표시): 찌그러짐 깊이 + 접힘 반파장, 최소 물체 크기의 8%.</summary>
    public double DamageRadius { get; init; }

    internal ImpactAssessment With(Vec3 down) => new()
    {
        BodyName = BodyName, Material = Material, Model = Model, Contact = Contact, ContactPointBrf = ContactPointBrf, DownBrf = down, Time = Time,
        Mass = Mass, NormalSpeed = NormalSpeed, TangentialSpeed = TangentialSpeed, Energy = Energy, Thickness = Thickness, StrainRate = StrainRate,
        RateFactor = RateFactor, ElasticCapacity = ElasticCapacity, CrushForce = CrushForce, DentDepth = DentDepth, CrushLimit = CrushLimit,
        FoldLength = FoldLength, PeakStress = PeakStress, Shortening = Shortening, Safety = Safety, DamageSpeed = DamageSpeed,
        CollapseSpeed = CollapseSpeed, PeakG = PeakG, State = State, Verdict = Verdict, Method = Method, DamageRadius = DamageRadius,
    };

    public JsonObject ToJson()
    {
        static JsonNode? N(double v) => double.IsFinite(v) ? JsonValue.Create(v) : null;
        return new JsonObject
        {
            ["note"] = "착지 충돌 근사(공학 추정식, FEA 아님) — 정밀 판정은 3단계 OpenCourant",
            ["material"] = Material.Key, ["materialName"] = Material.Name, ["brittle"] = Material.Brittle,
            ["model"] = Model, ["contact"] = Contact.ToString(), ["contactPoint_brf_m"] = Scenario.J.ToJson(ContactPointBrf), ["down_brf"] = Scenario.J.ToJson(DownBrf),
            ["time_s"] = N(Time), ["mass_kg"] = Mass, ["normalSpeed_mps"] = NormalSpeed, ["tangentialSpeed_mps"] = TangentialSpeed,
            ["energy_J"] = Energy, ["thickness_m"] = N(Thickness), ["strainRate_1ps"] = StrainRate, ["rateFactor"] = RateFactor,
            ["elasticCapacity_J"] = N(ElasticCapacity), ["crushForce_N"] = N(CrushForce), ["dentDepth_m"] = N(DentDepth),
            ["crushLimit_m"] = N(CrushLimit), ["foldLength_m"] = N(FoldLength), ["peakStress_MPa"] = N(PeakStress / 1e6),
            ["shortening_m"] = N(Shortening), ["safetyFactor"] = N(Safety), ["damageSpeed_mps"] = N(DamageSpeed),
            ["collapseSpeed_mps"] = N(CollapseSpeed), ["peakG"] = N(PeakG), ["state"] = State.ToString(), ["verdict"] = Verdict,
            ["method"] = Method, ["damageRadius_m"] = DamageRadius,
        };
    }
}

/// <summary>
/// 착지 충돌 재질 판정(1·2단계 — 사용자 질문 "플라스틱은 깨지고 양철은 찌그러진다"). 엔진은 착지를 순간 정지로 처리하므로
/// 비행 하중 응력 모델(StressModel)에는 착지 충격이 들어가지 않는다 → 착지 기록(속도·자세)으로 따로 판정한다.
/// <para>얇은 벽(thinWall): 접촉부 벽의 탄성 변형에너지 용량 U = σ_lim²/(2E)·V/3 (V = 접촉부 벽 폭 × t × min(벽 높이, 2H),
/// H = 0.983·t^(1/3)·b^(2/3) Wierzbicki 접힘 반파장, 1/3 = 영역 안 선형 응력 감소). 법선 운동에너지 E_n ≤ U 면 손상 없음.
/// 연성: 넘는 에너지를 평균 압궤력으로 흡수 → 찌그러짐 깊이 δ = (E_n − U)/P. P = 사각관 9.56·σ0·t^(5/3)·b^(1/3)
/// (Wierzbicki &amp; Abramowicz 1983)의 코너 요소 몫(면 4/4·모서리 2/4·꼭짓점 1/4), 원통 끝면 Alexander(1960) 6·σ0·t^1.5·√D
/// (테두리 1/4), 원통 옆면 링 압궤 σ0·t²·L/R (DeRuntz &amp; Hodge 1963). σ0 = (σy+σu)/2. δ ≥ 0.73·(높이) 면 전면 압궤(붕괴).
/// 취성: E_n &gt; U 면 깨짐.</para>
/// <para>속이 찬 단면(solid): 강체 벽 1D 파동 응력 σ = ρ·c·v_n(c = √(E/ρ)), 지면이 낼 수 있는 응력 m·g·G_pen/A_pen 으로 상한
/// (Young 관입 — 무른 흙이 먼저 밀림). 연성 σ ≥ σy → Taylor 영구 단축 L_f/L_0 = exp(−ρv²/2σy), 평균 변형률 &gt; 연신율이면 파단.
/// 취성 σ ≥ σu → 파단. 폼(foam): 압축 평탄 응력 σy × 접촉 면적으로 흡수, 0.7·높이 넘으면 바닥 침.</para>
/// <para>변형률 속도(FR-MAT-03): ε̇ ≈ v_n/(2H)(thinWall) 또는 v_n/높이 → JC·DIF 배율(보정 전 정적 판정도 함께 보고).
/// 지면은 thinWall 에서 강체로 본다(Young 식은 가볍고 뭉툭한 물체에 적용 범위 밖) — 보수적.</para>
/// </summary>
public static class ImpactDamage
{
    public const double WierzbickiCoef = 9.56;
    public const double AlexanderCoef = 6.0;
    public const double CrushEfficiency = 0.73;
    public const double FoamDensification = 0.7;
    /// <summary>
    /// 접촉부 응력 분포 계수: 응력이 접촉 가장자리 최대값에서 손상 영역(2H) 끝 0 까지 선형으로 준다고 보면 변형에너지 = 균일 응력의 1/3.
    /// (처음에는 균일 응력(1)으로 두었으나 OpenCourant 셸 해석에서 ABS 면 착지 7.4 m/s 가 국부 파단(0.5% 요소 삭제)인데
    /// 1단계는 안전계수 1.17 로 '손상 없음' — 비보수적이라 고침. FEA 결과에 맞춘 값이 아니라 선형 분포에서 유도한 값.)
    /// </summary>
    public const double StressDistribution = 1.0 / 3;

    /// <summary>World 착지 기록으로 판정. 재질·착지 기록이 없으면 null.</summary>
    public static ImpactAssessment? Assess(Body b, double gravity = Atmosphere.G0)
    {
        if (b.Impact is not { } imp || b.Structure?.Material is not { } mat || b.Shape is not { } shape) return null;
        var q = Quat.FromEuler(imp.EulerDeg.X * Math.PI / 180, imp.EulerDeg.Y * Math.PI / 180, imp.EulerDeg.Z * Math.PI / 180);
        double pen = imp.PenetrationG > 0 && imp.PenetrationArea > 0 ? imp.Mass * gravity * imp.PenetrationG / imp.PenetrationArea : double.PositiveInfinity;
        return Assess(b.Name, mat, b.Structure, shape, imp.Mass > 0 ? imp.Mass : b.OwnMass, imp.Velocity, q, pen, imp.Time, gravity);
    }

    /// <summary>
    /// 판정 핵심(헤드리스·테스트용). velocityNed: 충돌 직전 속도(NED, +z 아래), q: 동체→NED, groundStressLimit: 지면이 낼 수 있는 평균 응력[Pa](∞ = 강체).
    /// </summary>
    public static ImpactAssessment Assess(string name, Material mat, StructureDef def, Shape shape, double mass, Vec3 velocityNed, Quat q,
        double groundStressLimit = double.PositiveInfinity, double time = double.NaN, double gravity = Atmosphere.G0)
    {
        if (!(mass > 0)) throw new InvalidDataException($"{name}: 착지 판정에 양의 질량이 필요합니다.");
        if (!velocityNed.IsFinite) throw new InvalidDataException($"{name}: 충돌 속도가 유한하지 않습니다.");
        double vn = Math.Max(velocityNed.Z, 0);                          // 지면 법선(아래) 성분
        double vt = Math.Sqrt(velocityNed.X * velocityNed.X + velocityNed.Y * velocityNed.Y);
        double en = 0.5 * mass * vn * vn;
        var down = q.InverseRotate(Vec3.UnitZ);                          // 동체 좌표의 지면 방향
        var geo = Contact(shape, down);
        var a = mat.Class == "foam" ? Foam(name, mat, geo, mass, vn, vt, en, time, gravity)
            : def.Section == "thinWall" && def.Thickness is { } t && t > 0 ? ThinWall(name, mat, geo, t, mass, vn, vt, en, time, gravity)
            : Solid(name, mat, geo, mass, vn, vt, en, groundStressLimit, time);
        return a.With(down.Normalized());
    }

    /// <summary>접촉 기하: 형태, 접촉점, 벽 대표 폭 b, 접촉부 벽 둘레 몫 w, 법선 방향 높이 h, 접촉 면적.</summary>
    public sealed record ContactGeometry(ContactKind Kind, Vec3 Point, double B, double WallWidth, double Height, double Share, double Diameter, double Length, double Area, bool Cylindrical);

    public static ContactGeometry Contact(Shape s, Vec3 downBody)
    {
        var d = downBody.Normalized();
        if (s.Kind == ShapeKind.Cylinder)
        {
            int ax = s.Axis;
            double cosA = Math.Abs(d[ax]);
            double D = 2 * s.Radius, L = s.Length;
            var axis = ax == 0 ? Vec3.UnitX : ax == 1 ? Vec3.UnitY : Vec3.UnitZ;
            var radial = d - axis * d[ax];
            var rh = radial.NormSq > 1e-18 ? radial.Normalized() : Vec3.Zero;
            var endPt = s.Center + axis * (Math.Sign(d[ax] == 0 ? 1 : d[ax]) * L / 2);
            double cos10 = Math.Cos(10 * Math.PI / 180), sin10 = Math.Sin(10 * Math.PI / 180);
            if (cosA >= cos10)
                return new(ContactKind.End, endPt, D, Math.PI * D, L, 1, D, L, Math.PI * D * D / 4, true);
            if (cosA <= sin10)
                return new(ContactKind.Side, s.Center + rh * s.Radius, D, L, D, 1, D, L, L * D * 0.1, true);
            return new(ContactKind.Rim, endPt + rh * s.Radius, D, Math.PI * D / 4, Math.Abs(d[ax]) * L + (1 - cosA) * D, 0.25, D, L, D * D * 0.05, true);
        }
        // 상자 근사(Box · Mesh · ShellSegment · Sphere 의 바운딩 박스)
        var size = s.Kind == ShapeKind.Sphere ? new Vec3(2 * s.Radius, 2 * s.Radius, 2 * s.Radius) : s.Size;
        var hs = size / 2;
        var corners = new List<Vec3>(8);
        for (int i = 0; i < 8; i++)
            corners.Add(s.Center + new Vec3((i & 1) == 0 ? -hs.X : hs.X, (i & 2) == 0 ? -hs.Y : hs.Y, (i & 4) == 0 ? -hs.Z : hs.Z));
        double maxD = corners.Max(c => Vec3.Dot(c, d));
        double tol = 0.03 * Math.Max(size.X, Math.Max(size.Y, size.Z));
        var low = corners.Where(c => maxD - Vec3.Dot(c, d) <= tol).ToList();
        var pt = low.Aggregate(Vec3.Zero, (a, c) => a + c) / low.Count;
        double height = Math.Abs(d.X) * size.X + Math.Abs(d.Y) * size.Y + Math.Abs(d.Z) * size.Z;
        if (s.Kind == ShapeKind.Sphere)
            return new(ContactKind.Point, s.Center + d * s.Radius, size.X, Math.PI * size.X / 4, size.X, 0.25, size.X, size.X, size.X * size.X * 0.02, false);
        if (low.Count >= 3)
        {
            int n = Math.Abs(d.X) >= Math.Abs(d.Y) && Math.Abs(d.X) >= Math.Abs(d.Z) ? 0 : Math.Abs(d.Y) >= Math.Abs(d.Z) ? 1 : 2;
            double b1 = size[(n + 1) % 3], b2 = size[(n + 2) % 3];
            return new(ContactKind.Face, pt, 0.5 * (b1 + b2), 2 * (b1 + b2), size[n], 1, 0, 0, b1 * b2, false);
        }
        double bm = (size.X + size.Y + size.Z) / 3;
        if (low.Count == 2)
        {
            double edge = (low[0] - low[1]).Norm;
            return new(ContactKind.Edge, pt, bm, 2 * bm, height, 0.5, 0, 0, edge * 0.1 * bm, false);
        }
        return new(ContactKind.Corner, pt, bm, bm, height, 0.25, 0, 0, 0.01 * bm * bm, false);
    }

    /// <summary>Wierzbicki 접힘 반파장 H = 0.983·t^(1/3)·b^(2/3).</summary>
    public static double FoldHalfLength(double t, double b) => 0.983 * Math.Cbrt(t) * Math.Pow(b, 2.0 / 3);

    /// <summary>사각관 평균 압궤력(Wierzbicki &amp; Abramowicz 1983) 9.56·σ0·t^(5/3)·b^(1/3).</summary>
    public static double SquareTubeMeanCrush(double sigma0, double t, double b) => WierzbickiCoef * sigma0 * Math.Pow(t, 5.0 / 3) * Math.Cbrt(b);

    /// <summary>원통 축 압궤 평균력(Alexander 1960) 6·σ0·t^1.5·√D.</summary>
    public static double TubeAxialMeanCrush(double sigma0, double t, double d) => AlexanderCoef * sigma0 * Math.Pow(t, 1.5) * Math.Sqrt(d);

    /// <summary>원통 옆면 링 압궤(강소성, DeRuntz &amp; Hodge 1963) σ0·t²·L/R.</summary>
    public static double RingCrush(double sigma0, double t, double length, double radius) => sigma0 * t * t * length / radius;

    private static ImpactAssessment ThinWall(string name, Material mat, ContactGeometry g, double t, double mass, double vn, double vt, double en, double time, double gravity)
    {
        // 접힘 반파장: 상자 Wierzbicki, 원통은 고전 축대칭 좌굴 반파장 1.72·√(R·t)
        double hFold = g.Cylindrical ? 1.72 * Math.Sqrt(g.Diameter / 2 * t) : FoldHalfLength(t, g.B);
        double zone = Math.Min(g.Height, 2 * hFold);
        double rate = vn > 0 ? vn / (2 * hFold) : 0;
        double rf = mat.RateFactor(rate);
        double sLim = (mat.Brittle ? mat.Ultimate : mat.Yield) * rf;
        double vol = g.WallWidth * t * zone * StressDistribution;
        double u = sLim * sLim / (2 * mat.E) * vol;
        double uStatic = Math.Pow(mat.Brittle ? mat.Ultimate : mat.Yield, 2) / (2 * mat.E) * vol;
        double sigma0 = 0.5 * (mat.Yield + mat.Ultimate) * rf;
        double p = g.Kind switch
        {
            ContactKind.End or ContactKind.Rim => TubeAxialMeanCrush(sigma0, t, g.Diameter) * g.Share,
            ContactKind.Side => RingCrush(sigma0, t, g.Length, g.Diameter / 2),
            _ => SquareTubeMeanCrush(sigma0, t, g.B) * g.Share,
        };
        double limit = CrushEfficiency * g.Height;
        double sf = en > 0 ? Math.Sqrt(u / en) : double.PositiveInfinity;
        double vDmg = Math.Sqrt(2 * u / mass);
        double radiusMin = 0.08 * g.Height;
        string method = mat.Brittle
            ? $"thinWall 취성: U = σu²/(2E)·V/3 (V = 벽 폭 {g.WallWidth * 1000:F0} mm × t × {zone * 1000:F0} mm) vs E_n"
            : $"thinWall 연성: U = σy²/(2E)·V/3, 찌그러짐 δ = (E_n − U)/P, P = {(g.Kind is ContactKind.End or ContactKind.Rim ? "Alexander" : g.Kind == ContactKind.Side ? "링 압궤" : "Wierzbicki 코너 요소")} × {g.Share:0.##}";
        if (mat.Brittle)
        {
            bool broken = en > u;
            var st = broken ? FailureState.Fracture : sf < StressModel.CautionSafety ? FailureState.Caution : FailureState.Safe;
            string verdict = broken
                ? $"깨짐(취성 파단) — 충돌 에너지 {en:F1} J 가 접촉부 탄성 용량 {u:F1} J 의 {en / u:F1}배 ({g.Kind}, 손상 시작 {vDmg:F2} m/s)"
                : $"손상 없음(탄성) — 에너지 {en:F1} J ≤ 용량 {u:F1} J, 안전계수 {sf:F2}";
            return new ImpactAssessment
            {
                BodyName = name, Material = mat, Model = "thinWall", Contact = g.Kind, ContactPointBrf = g.Point, Time = time, Mass = mass,
                NormalSpeed = vn, TangentialSpeed = vt, Energy = en, Thickness = t, StrainRate = rate, RateFactor = rf, ElasticCapacity = u,
                FoldLength = hFold, Safety = sf, DamageSpeed = vDmg, State = st, Verdict = verdict, Method = method + $" (정적 강도면 용량 {uStatic:F1} J)",
                DamageRadius = Math.Max(broken ? 2 * hFold : hFold, radiusMin),
            };
        }
        double dent = en > u ? (en - u) / p : 0;
        var state = dent >= limit ? FailureState.Fracture : dent > 0 ? FailureState.Yield : sf < StressModel.CautionSafety ? FailureState.Caution : FailureState.Safe;
        double vCol = Math.Sqrt(2 * (u + p * limit) / mass);
        string v = state switch
        {
            FailureState.Fracture => $"전면 압궤(구조 붕괴·찢어짐 가능) — 필요한 압궤 {(en - u) / p * 1000:F0} mm > 압궤 가능 {limit * 1000:F0} mm ({g.Kind}, 붕괴 속도 {vCol:F2} m/s)",
            FailureState.Yield => $"찌그러짐 깊이 ≈ {dent * 1000:F1} mm ({g.Kind}, 평균 압궤력 {p / 1000:F2} kN, 손상 시작 {vDmg:F2} m/s)",
            _ => $"손상 없음(탄성) — 에너지 {en:F1} J ≤ 용량 {u:F1} J, 안전계수 {sf:F2}",
        };
        return new ImpactAssessment
        {
            BodyName = name, Material = mat, Model = "thinWall", Contact = g.Kind, ContactPointBrf = g.Point, Time = time, Mass = mass,
            NormalSpeed = vn, TangentialSpeed = vt, Energy = en, Thickness = t, StrainRate = rate, RateFactor = rf, ElasticCapacity = u,
            CrushForce = p, DentDepth = Math.Min(dent, limit), CrushLimit = limit, FoldLength = hFold, Safety = sf, DamageSpeed = vDmg,
            CollapseSpeed = vCol, PeakG = dent > 0 ? p / (mass * gravity) : double.NaN, State = state, Verdict = v,
            Method = method + $" (정적 강도면 용량 {uStatic:F1} J)",
            DamageRadius = Math.Max(Math.Min(dent, limit) + hFold, radiusMin),
        };
    }

    private static ImpactAssessment Foam(string name, Material mat, ContactGeometry g, double mass, double vn, double vt, double en, double time, double gravity)
    {
        double rate = vn > 0 ? vn / Math.Max(g.Height, 1e-3) : 0;
        double rf = mat.RateFactor(rate);
        // 접촉 면적: 면은 면 전체, 모서리·꼭짓점은 압궤가 진행되며 넓어지므로 면적의 1/4 를 평균으로(가정)
        double area = g.Kind is ContactKind.Face or ContactKind.End ? g.Area : Math.Max(g.Area, 0.25 * g.B * g.B);
        double p = mat.Yield * rf * area;
        double limit = FoamDensification * g.Height;
        double dent = p > 0 ? en / p : double.PositiveInfinity;
        var st = dent >= limit ? FailureState.Fracture : dent > 0 ? FailureState.Yield : FailureState.Safe;
        double vCol = Math.Sqrt(2 * p * limit / mass);
        return new ImpactAssessment
        {
            BodyName = name, Material = mat, Model = "foam", Contact = g.Kind, ContactPointBrf = g.Point, Time = time, Mass = mass,
            NormalSpeed = vn, TangentialSpeed = vt, Energy = en, Thickness = double.NaN, StrainRate = rate, RateFactor = rf,
            CrushForce = p, DentDepth = Math.Min(dent, limit), CrushLimit = limit, Safety = dent > 0 ? limit / dent : double.PositiveInfinity,
            DamageSpeed = 0, CollapseSpeed = vCol, PeakG = p / (mass * gravity), State = st,
            Verdict = st == FailureState.Fracture ? $"폼 바닥 침(완충 한계 초과) — 필요 압축 {dent * 1000:F0} mm > {limit * 1000:F0} mm"
                : $"완충재 압축 ≈ {dent * 1000:F1} mm (평탄 응력 {mat.Yield * rf / 1e6:F2} MPa × 접촉 {area * 1e4:F0} cm², 감속 {p / (mass * gravity):F0} G)",
            Method = "foam: 평탄 응력 × 접촉 면적 일정력 흡수, 치밀화 0.7·높이",
            DamageRadius = Math.Max(Math.Min(dent, limit) + 0.02, 0.08 * g.Height),
        };
    }

    private static ImpactAssessment Solid(string name, Material mat, ContactGeometry g, double mass, double vn, double vt, double en, double groundLimit, double time)
    {
        double c = Math.Sqrt(mat.E / mat.Density);
        double rate = vn > 0 ? vn / Math.Max(g.Height, 1e-3) : 0;
        double rf = mat.RateFactor(rate);
        double sWave = mat.Density * c * vn;
        double sigma = Math.Min(sWave, groundLimit);
        double sy = mat.Yield * rf, su = mat.Ultimate * rf;
        double sLim = mat.Brittle ? su : sy;
        double sf = sigma > 0 ? sLim / sigma : double.PositiveInfinity;
        double vDmg = sLim / (mat.Density * c);
        FailureState st; string verdict; double shortening = 0;
        if (mat.Brittle)
        {
            st = sigma >= su ? FailureState.Fracture : sf < StressModel.CautionSafety ? FailureState.Caution : FailureState.Safe;
            verdict = st == FailureState.Fracture ? $"깨짐(취성 파단) — 충돌 응력 {sigma / 1e6:F0} MPa ≥ 동적 인장강도 {su / 1e6:F0} MPa"
                : $"손상 없음 — 충돌 응력 {sigma / 1e6:F1} MPa, 안전계수 {sf:F2}";
        }
        else if (sigma >= sy)
        {
            double ratio = Fea.RadiossDeck.TaylorFinalLengthRatio(mat.Density, vn, sy);
            shortening = g.Height * (1 - ratio);
            double strain = -Math.Log(Math.Max(ratio, 1e-12));
            st = strain > mat.Elongation ? FailureState.Fracture : FailureState.Yield;
            verdict = st == FailureState.Fracture ? $"파단(충돌면 버섯 변형 균열) — 평균 소성변형률 {strain:P1} > 연신율 {mat.Elongation:P0}"
                : $"영구 변형(충돌면 눌림) — 단축 ≈ {shortening * 1000:F2} mm (Taylor), 충돌 응력 {sigma / 1e6:F0} MPa ≥ σy {sy / 1e6:F0} MPa";
        }
        else
        {
            st = sf < StressModel.CautionSafety ? FailureState.Caution : FailureState.Safe;
            verdict = $"손상 없음 — 충돌 응력 {sigma / 1e6:F1} MPa < σy {sy / 1e6:F0} MPa (안전계수 {sf:F2})";
        }
        return new ImpactAssessment
        {
            BodyName = name, Material = mat, Model = "solid", Contact = g.Kind, ContactPointBrf = g.Point, Time = time, Mass = mass,
            NormalSpeed = vn, TangentialSpeed = vt, Energy = en, Thickness = double.NaN, StrainRate = rate, RateFactor = rf,
            PeakStress = sigma, Shortening = shortening, Safety = sf, DamageSpeed = vDmg, State = st, Verdict = verdict,
            Method = double.IsFinite(groundLimit) && groundLimit < sWave
                ? $"solid: 지면 한계 응력 m·g·G/A = {groundLimit / 1e6:F1} MPa(< 1D 파동 ρcv {sWave / 1e6:F0} MPa — 흙이 먼저 밀림)"
                : $"solid: 1D 파동 응력 ρ·c·v (c = {c:F0} m/s, 강체 지면)",
            DamageRadius = Math.Max(0.1 * g.Height, shortening * 3),
        };
    }

    /// <summary>
    /// 판정 결과를 면 상태에 반영: 꼭짓점·모서리·점 착지는 접촉점에서 DamageRadius 안(구), 면·끝면 착지는 접촉면에서
    /// 지면 반대쪽으로 DamageRadius 두께의 판(slab) 안 면을 판정 상태로 올린다(면 착지 손상은 바닥 둘레 전체).
    /// </summary>
    public static int Paint(ImpactAssessment a, IReadOnlyList<Vec3> centroids, FailureState[] states)
    {
        if (a.State <= FailureState.Safe) return 0;
        int n = 0;
        double r2 = a.DamageRadius * a.DamageRadius;
        bool slab = a.Contact is ContactKind.Face or ContactKind.End;
        for (int i = 0; i < centroids.Count && i < states.Length; i++)
            if (slab ? Vec3.Dot(centroids[i] - a.ContactPointBrf, a.DownBrf) >= -a.DamageRadius : (centroids[i] - a.ContactPointBrf).NormSq <= r2)
            {
                if (a.State > states[i]) states[i] = a.State;
                n++;
            }
        return n;
    }
}
