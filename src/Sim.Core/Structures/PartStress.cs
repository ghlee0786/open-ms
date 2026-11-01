using System.Text.Json.Nodes;
using Sim.Core.Numerics;
using Sim.Core.Scenario;

namespace Sim.Core.Structures;

/// <summary>
/// 부품별 닫힌 식 응력(FR-AN-08 ③ "근사"): 하중 F [N] 에 선형인 등가응력 σ_eq = s·F (s = 응력 계수 [Pa/N]).
/// 형식(kind)과 출처:
///   plate      — 원판 중앙 집중 하중(반경 r0 분포), 가장자리 단순지지/고정 (Roark &amp; Young, Formulas for Stress and Strain 7판 표 24 경우 16·17,
///                r0 &lt; 0.5t 이면 등가 반경 r0' = √(1.6r0² + t²) − 0.675t)
///   lug        — 핀 하중 러그: 순단면 인장 F/((w−d)t)·Kt, 지압 F/(d·t)/1.5(지압 허용 ≈ 1.5σy 로 정규화), 전단 이탈 √3·F/(2(e−d/2)t) 중 최대
///   bolt       — 볼트·핀 n개: 전단 √3·F/(n·면수·πd²/4) 또는 인장 F/(n·A_s), A_s = π/4·(d − 0.9382p)² (ISO 898 응력 단면적)
///   hook       — 원형 단면 곡선보(Winkler–Bach): 하중선이 곡률 중심을 지날 때 내측 응력 F/A + M(r_n − r_i)/(A·e·r_i), M = F·R
///   cantilever — 외팔 굽힘 M = F·L: 사각 6M/(b h²) 또는 원형 32M/(πd³), ×Kt
///   bar        — 단순 인장/압축 F/A ×Kt
///   pin        — 짧은 외팔 핀(지름 d, 지지면 → 하중선 a): max(굽힘 32F·a/(πd³), √3·(4/3)·4F/(πd²) 횡전단 최대) — 같은 점에서 최대가 아니므로 큰 쪽
///   tubeCantilever — 정사각 관(바깥 폭 b, 벽 t, 길이 L) 외팔 굽힘 M·c/I, I = (b⁴ − (b−2t)⁴)/12
/// 형상 치수는 시나리오 입력(대개 (가정)), 재질은 재질 라이브러리. 안전계수 = 항복강도(취성: 인장강도) × 변형률 속도 배율 / σ_eq.
/// </summary>
public sealed class PartStressDef
{
    public required string Name { get; init; }
    public string Label { get; init; } = "";
    public required string Kind { get; init; }
    public required Material Material { get; init; }
    /// <summary>σ_eq / F [Pa/N].</summary>
    public required double Coefficient { get; init; }
    /// <summary>지배 성분 설명(예: "순단면 인장").</summary>
    public string Governing { get; init; } = "";
    public string Formula { get; init; } = "";
    /// <summary>하중 출처: channel 이름(장력·장치 힘) 또는 inertia.</summary>
    public string LoadChannel { get; init; } = "";
    /// <summary>관성 하중 질량 [kg] (LoadChannel = "inertia:payload|base").</summary>
    public double InertiaMass { get; init; }
    /// <summary>관성 하중 방향 성분(동체 단위 벡터, null = 크기 전체).</summary>
    public Vec3? InertiaAxis { get; init; }
    /// <summary>스케일(하중 분담, 예: 4개 중 1개 = 0.25).</summary>
    public double Share { get; init; } = 1;
    public bool Assumed { get; init; } = true;
    public string Note { get; init; } = "";

    public double Allowable(double rate = 1) => (Material.Brittle ? Material.Ultimate : Material.Yield) * rate;
    public double Stress(double force) => Coefficient * Math.Abs(force) * Share;
    public double Safety(double force, double rate = 1)
    {
        double s = Stress(force);
        return s > 0 ? Allowable(rate) / s : double.PositiveInfinity;
    }

    public JsonObject ToJson() => new()
    {
        ["name"] = Name, ["label"] = Label, ["kind"] = Kind, ["material"] = Material.Key, ["materialName"] = Material.Name,
        ["allowable_MPa"] = Allowable() / 1e6, ["allowableBasis"] = Material.Brittle ? "인장강도(취성)" : "항복강도",
        ["coefficient_MPa_per_kN"] = Coefficient * Share * 1e3 / 1e6, ["governing"] = Governing, ["formula"] = Formula,
        ["load"] = LoadChannel, ["inertiaMass_kg"] = InertiaMass > 0 ? InertiaMass : null, ["share"] = Share, ["assumed"] = Assumed, ["note"] = Note,
    };
}

public static class PartStress
{
    /// <summary>Roark 등가 반경: r0 &lt; 0.5t 이면 √(1.6r0² + t²) − 0.675t.</summary>
    public static double EquivalentRadius(double r0, double t) => r0 < 0.5 * t ? Math.Sqrt(1.6 * r0 * r0 + t * t) - 0.675 * t : r0;

    /// <summary>원판(반경 a, 두께 t) 중앙 집중 하중 W 의 최대 굽힘응력 / W [Pa/N]. clamped=고정 가장자리(중앙·가장자리 중 큰 값).</summary>
    public static (double Coef, string Where) PlateCentral(double a, double t, double r0, double nu, bool clamped)
    {
        if (!(a > 0) || !(t > 0) || !(r0 > 0) || r0 >= a) throw new InvalidDataException("plate: 0 < r0 < a, t > 0 이어야 합니다.");
        double r = EquivalentRadius(r0, t);
        double k = 3 / (2 * Math.PI * t * t);
        if (!clamped) return (k * ((1 + nu) * Math.Log(a / r) + 1), "중앙(단순지지)");
        double center = k * (1 + nu) * Math.Log(a / r), edge = k;
        return center >= edge ? (center, "중앙(고정)") : (edge, "가장자리(고정)");
    }

    /// <summary>핀 하중 러그(폭 w, 구멍 d, 두께 t, 구멍 중심~끝 e) — (계수, 지배 성분).</summary>
    public static (double Coef, string Mode) Lug(double w, double d, double t, double e, double kt = 1)
    {
        if (!(w > d) || !(d > 0) || !(t > 0) || !(e > d / 2)) throw new InvalidDataException("lug: w > d > 0, t > 0, e > d/2 이어야 합니다.");
        var modes = new (double C, string M)[]
        {
            (kt / ((w - d) * t), "순단면 인장"),
            (1 / (d * t) / 1.5, "지압(허용 1.5σy 정규화)"),
            (Math.Sqrt(3) / (2 * (e - d / 2) * t), "전단 이탈(√3τ)"),
        };
        var g = modes.MaxBy(x => x.C);
        return (g.C, g.M);
    }

    /// <summary>볼트·핀 n개 전단(면수 planes) 또는 인장(나사 피치 p, 0 이면 원 단면).</summary>
    public static (double Coef, string Mode) Bolt(double d, int n, string mode, int planes = 1, double pitch = 0)
    {
        if (!(d > 0) || n < 1) throw new InvalidDataException("bolt: d > 0, n ≥ 1 이어야 합니다.");
        if (mode == "tension")
        {
            double ds = pitch > 0 ? d - 0.9382 * pitch : d;
            return (1 / (n * Math.PI / 4 * ds * ds), pitch > 0 ? "나사 인장(응력 단면적)" : "인장");
        }
        if (mode != "shear") throw new InvalidDataException($"bolt.mode: shear | tension ('{mode}')");
        return (Math.Sqrt(3) / (n * Math.Max(planes, 1) * Math.PI / 4 * d * d), "전단(√3τ)");
    }

    /// <summary>
    /// 원형 단면(지름 d) 곡선보, 단면 중심 곡률 반경 R(> d/2): 하중선이 곡률 중심을 지날 때(M = F·R) 내측 섬유 응력/F.
    /// r_n = c²/(2(R − √(R² − c²))), e = R − r_n, σ_i = F/A + M(r_n − r_i)/(A e r_i).
    /// </summary>
    public static double HookInner(double d, double R)
    {
        double c = d / 2;
        if (!(c > 0) || !(R > c)) throw new InvalidDataException("hook: R > d/2 > 0 이어야 합니다.");
        double A = Math.PI * c * c;
        double rn = c * c / (2 * (R - Math.Sqrt(R * R - c * c)));
        double e = R - rn, ri = R - c;
        return 1 / A + R * (rn - ri) / (A * e * ri);
    }

    /// <summary>Winkler 곡률 계수 K_i = (내측 굽힘응력) / (직선보 Mc/I) — 검증용(Shigley 표 3-4 원형 단면).</summary>
    public static double HookCurvatureFactor(double d, double R)
    {
        double c = d / 2, A = Math.PI * c * c;
        double rn = c * c / (2 * (R - Math.Sqrt(R * R - c * c)));
        double e = R - rn, ri = R - c;
        double curved = (rn - ri) / (A * e * ri);
        double straight = c / (Math.PI * Math.Pow(c, 4) / 4);
        return curved / straight;
    }

    public static double CantileverRect(double L, double b, double h, double kt = 1) => kt * 6 * L / (b * h * h);
    public static double CantileverRound(double L, double d, double kt = 1) => kt * 32 * L / (Math.PI * d * d * d);

    /// <summary>짧은 외팔 핀(지름 d, 지지면에서 하중선까지 a) — (계수, 지배): 굽힘 32a/(πd³) vs 횡전단 최대 √3·(4/3)·4/(πd²).</summary>
    public static (double Coef, string Mode) Pin(double d, double a, double kt = 1)
    {
        if (!(d > 0) || !(a >= 0)) throw new InvalidDataException("pin: d > 0, lever ≥ 0 이어야 합니다.");
        double bend = kt * 32 * a / (Math.PI * d * d * d);
        double shear = Math.Sqrt(3) * 4.0 / 3 * 4 / (Math.PI * d * d);
        return bend >= shear ? (bend, "외팔 굽힘") : (shear, "횡전단(√3τ_max)");
    }

    /// <summary>정사각 관 단면 2차 모멘트 (b⁴ − (b−2t)⁴)/12.</summary>
    public static double SquareTubeI(double b, double t) => (Math.Pow(b, 4) - Math.Pow(b - 2 * t, 4)) / 12;

    /// <summary>정사각 관 외팔 굽힘 σ/F = L·(b/2)/I.</summary>
    public static double TubeCantilever(double L, double b, double t, double kt = 1)
    {
        if (!(b > 2 * t) || !(t > 0) || !(L > 0)) throw new InvalidDataException("tubeCantilever: width > 2·wall > 0, length > 0 이어야 합니다.");
        return kt * L * (b / 2) / SquareTubeI(b, t);
    }

    /// <summary>
    /// 형상만으로 응력 계수(하중 없이) — parts 항목 공통(kind·치수·kt). 반환 (계수 [Pa/N], 지배 성분, 식).
    /// </summary>
    public static (double Coef, string Governing, string Formula) Geometry(JsonObject o, string where, Material mat)
    {
        var kind = J.StrReq(o, "kind", where);
        double kt = J.Num(o, "kt", 1);
        double coef; string gov, formula;
        double N(string k) => J.NumReq(o, k, where);
        switch (kind)
        {
            case "plate":
            {
                bool clamped = J.Str(o, "edge", "clamped") switch { "clamped" => true, "simple" => false, var x => throw new InvalidDataException($"{where}.edge: clamped | simple ('{x}')") };
                (coef, gov) = PlateCentral(N("radius"), N("thickness"), N("loadRadius"), mat.Nu, clamped);
                coef *= kt;
                formula = "Roark 표 24 경우 16/17: σ = 3W/(2πt²)·[(1+ν)ln(a/r0') (+1)]";
                break;
            }
            case "lug":
                (coef, gov) = Lug(N("width"), N("hole"), N("thickness"), N("edge"), kt);
                formula = "러그: max(Kt·F/((w−d)t), F/(d t)/1.5, √3F/(2(e−d/2)t))";
                break;
            case "bolt":
            {
                var mode = J.Str(o, "mode", "shear");
                (coef, gov) = Bolt(N("diameter"), (int)J.Num(o, "count", 1), mode, (int)J.Num(o, "planes", 1), J.Num(o, "pitch", 0));
                coef *= kt;
                formula = mode == "tension" ? "F/(n·A_s)" : "√3·F/(n·m·πd²/4)";
                break;
            }
            case "hook":
                coef = kt * HookInner(N("wireDiameter"), N("bendRadius"));
                gov = "곡선보 내측 인장"; formula = "Winkler–Bach: F/A + F·R(r_n − r_i)/(A e r_i)";
                break;
            case "cantilever":
            {
                double L = N("lever");
                if (J.NumOpt(o, "diameter") is { } d) { coef = CantileverRound(L, d, kt); formula = "32·F·L/(πd³)"; }
                else { coef = CantileverRect(L, N("width"), N("height"), kt); formula = "6·F·L/(b h²)"; }
                gov = "루트 굽힘";
                break;
            }
            case "bar":
            {
                double area = J.NumOpt(o, "area") ?? (J.NumOpt(o, "diameter") is { } d ? Math.PI / 4 * d * d : N("width") * N("height"));
                coef = kt / area; gov = "인장/압축(지압)"; formula = "F/A";
                break;
            }
            case "pin":
                (coef, gov) = Pin(N("diameter"), N("lever"), kt);
                formula = "max(32·F·a/(πd³), √3·(4/3)·4F/(πd²))";
                break;
            case "tubeCantilever":
                coef = TubeCantilever(N("length"), N("width"), N("wall"), kt);
                gov = "루트 굽힘"; formula = "F·L·(b/2)/I, I = (b⁴ − (b−2t)⁴)/12";
                break;
            default: throw new InvalidDataException($"{where}.kind: plate | lug | bolt | hook | cantilever | bar | pin | tubeCantilever ('{kind}')");
        }
        if (!(coef > 0) || !double.IsFinite(coef)) throw new InvalidDataException($"{where}: 응력 계수가 유효하지 않습니다.");
        return (coef, gov, formula);
    }

    /// <summary>시나리오 parts[] 항목 → 정의. 재질 키 필수(라이브러리 별칭 허용).</summary>
    public static PartStressDef Parse(JsonObject o, string owner)
    {
        var name = J.StrReq(o, "name", $"{owner}.parts");
        string where = $"{owner}.parts.{name}";
        var matName = J.StrReq(o, "material", where);
        var mat = MaterialLibrary.Find(matName) ?? throw new InvalidDataException($"{where}: 재질 '{matName}' 을 라이브러리에서 찾지 못했습니다(mns materials).");
        var kind = J.StrReq(o, "kind", where);
        var (coef, gov, formula) = Geometry(o, where, mat);
        string load; double im = 0; Vec3? axis = null;
        if (o["load"] is JsonObject lo)
        {
            if (J.StrOpt(lo, "channel") is { } ch) load = ch;
            else if (J.NumOpt(lo, "inertiaMass") is { } m)
            {
                if (!(m > 0)) throw new InvalidDataException($"{where}.load.inertiaMass 는 양수여야 합니다.");
                im = m; load = "inertia:" + J.Str(lo, "frame", "payload");
                if (load is not ("inertia:payload" or "inertia:base")) throw new InvalidDataException($"{where}.load.frame: payload | base");
                if (J.V3Opt(lo, "axis") is { } ax) axis = ax.Normalized();
            }
            else throw new InvalidDataException($"{where}.load: channel 또는 inertiaMass 가 필요합니다.");
        }
        else throw new InvalidDataException($"{where}: load 가 필요합니다.");
        return new PartStressDef
        {
            Name = name, Label = J.Str(o, "label", name), Kind = kind, Material = mat, Coefficient = coef, Governing = gov, Formula = formula,
            LoadChannel = load, InertiaMass = im, InertiaAxis = axis, Share = J.Num(o, "share", 1), Assumed = J.Bool(o, "assumed", true),
            Note = J.Str(o, "note", ""),
        };
    }
}
