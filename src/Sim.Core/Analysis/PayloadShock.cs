using System.Text.Json.Nodes;
using Sim.Core.Engine;
using Sim.Core.Models;
using Sim.Core.Numerics;
using Sim.Core.Scenario;
using Sim.Core.Structures;

namespace Sim.Core.Analysis;

/// <summary>장착 강성 요소(직렬): 종류·강성 [N/m]·근거.</summary>
public sealed record MountElement(string Kind, double K, string Basis);

/// <summary>
/// massprops 부품 질량 선택: 그룹의 바디 중 정확 이름 · 접두 이름 · 기준 바디 CG 아래(Below — Fusion 축 성분이 기준 바디 CG 보다 작은 바디)의 합집합에서
/// 제외 접두(Exclude)를 뺀다. 배율 = 물체 고유 질량(ScaleBody) / massprops 그룹 질량.
/// </summary>
public sealed record MassPropsSelection(string Group, string[] Exact, string[] Prefix, string ScaleBody, (string Body, int Axis)? Below = null, string[]? Exclude = null);

/// <summary>
/// 수납 부품 정의(config/models/*_payload_parts.json, mns.payload-parts/1) — 스테이션(드론 k) 전개 후 한 개.
/// 질량·위치는 실행마다 World 에서 다시 구한다(질량 배율 UQ·실모델/합성 대체가 그대로 반영).
/// </summary>
public sealed class PayloadPart
{
    public required string Name { get; init; }
    public required string Label { get; init; }
    public int? Station { get; init; }
    public string[] MassBodies { get; init; } = [];
    /// <summary>massprops 부품 질량 선택(그룹, 정확 이름, 접두 이름, 배율 기준 물체, 기준 바디 CG 아래, 제외 접두).</summary>
    public MassPropsSelection? MassParts { get; init; }
    /// <summary>위치: null = 질량 물체 합성 CG, 아니면 (물체, 그 물체의 모델 좌표 점 — Fusion m).</summary>
    public (string Body, Vec3 Model)? At { get; init; }
    /// <summary>axial(컨테이너 축 = 동체 x) | lateral(√(y²+z²)) | vector.</summary>
    public required string Direction { get; init; }
    public string? MountFrom { get; init; }
    public string? BaseFrom { get; init; }
    public double? FnHz { get; init; }
    public List<MountElement> Mount { get; init; } = new();
    public string MountGrade { get; init; } = "D";
    public string MountBasis { get; init; } = "";
    public Material? Material { get; init; }
    public double Coef { get; init; }
    public double Share { get; init; } = 1;
    public string Governing { get; init; } = "";
    public string Formula { get; init; } = "";
    public string StressKind { get; init; } = "";
    public double? GLimit { get; init; }
    public string GLimitBasis { get; init; } = "";
    public JsonObject? Ccx { get; init; }
    public string[]? Windows { get; init; }
    public string Basis { get; init; } = "";
    public bool HasStress => Material is not null && Coef > 0;
    /// <summary>참고 항목(예: 설계 확정 재질 대신 CAD 현재 재질로 본 같은 부품) — 표에는 보이되 최소 여유·권고 집계에서 뺀다.</summary>
    public bool Reference { get; init; }
    /// <summary>판정 집계 대상(응력 있음 · 참고 항목 아님).</summary>
    public bool Judged => HasStress && !Reference;
}

public sealed record PayloadWindowDef(string Id, string Label, string From, string? To, double Duration);

/// <summary>부품 정의 파일 전체.</summary>
public sealed class PayloadPartsConfig
{
    public required string Path { get; init; }
    public required string Root { get; init; }
    public string MassPropsPath { get; init; } = "";
    public JsonObject? MassProps { get; init; }
    public double Zeta { get; init; } = ShockSpectrum.DefaultZeta;
    public double[] FnFactors { get; init; } = [0.5, 0.71, 1, 1.41, 2];
    public string FnBasis { get; init; } = "";
    public double TauNominal { get; init; } = 0.002;
    public double TauMin { get; init; } = 0.001;
    public double TauMax { get; init; } = 0.005;
    public string TauBasis { get; init; } = "";
    public double SfYield { get; init; } = 1.25;
    public double SfUltimate { get; init; } = 1.5;
    public double GMargin { get; init; } = 1.0;
    public string CriteriaBasis { get; init; } = "";
    public List<PayloadWindowDef> Windows { get; } = new();
    public List<PayloadPart> Parts { get; } = new();
    public string Hash { get; init; } = "";
    /// <summary>보고서 표기(선택): 설계 이름(제목), 질량 출처 문장, 재생성 안내 — 없으면 시나리오 이름 · 일반 문구.</summary>
    public string? Title { get; init; }
    public string? MassNote { get; init; }
    public string? RegenNote { get; init; }
    /// <summary>설계 하중 기준(선택): 제한 G × 안전계수 = 극한 G — 판정표에 '설계 극한 대비 동적 G' 열로 함께 보인다.</summary>
    public double? DesignLimitG { get; init; }
    public double DesignSf { get; init; } = 1;
    public string DesignBasis { get; init; } = "";
    public double? DesignUltimateG => DesignLimitG * DesignSf;
    /// <summary>측방 설계 하중(선택, designLoad.lateral): 제한 G × 안전계수 — direction lateral 부품의 준정적 판정은 이 G(없으면 위 극한 G).</summary>
    public double? DesignLateralLimitG { get; init; }
    public double DesignLateralSf { get; init; } = 1;
    public string DesignLateralBasis { get; init; } = "";
    public double? DesignLateralUltimateG => DesignLateralLimitG * DesignLateralSf;
    /// <summary>설계 FEA 고유진동수(선택, designModes.fn_Hz — 설계 문서 인용): 창별 드론 위치 축 SRS/강체 비를 그 진동수에서 낸다(준정적 판단 대조).</summary>
    public double[] DesignModesHz { get; init; } = [];
    public string DesignModesBasis { get; init; } = "";

    public PayloadPart Part(string name) => Parts.FirstOrDefault(p => p.Name == name) ?? throw new KeyNotFoundException($"부품 '{name}' 이 정의에 없습니다.");

    public static PayloadPartsConfig Load(string path, string? repoRoot = null)
    {
        var text = File.ReadAllText(path);
        var o = J.ParseObject(text);
        if (J.Str(o, "schema", "") != "mns.payload-parts/1") throw new InvalidDataException($"{path}: schema mns.payload-parts/1 이 아닙니다.");
        // massprops 경로: 부품 정의 파일 폴더 기준 상대(없으면 repoRoot·현재 폴더 기준)
        string mpRel = J.Str(o, "massprops", "");
        string baseDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)) ?? ".";
        string mpPath = mpRel.Length == 0 ? "" : System.IO.Path.IsPathRooted(mpRel) ? mpRel : System.IO.Path.Combine(baseDir, mpRel);
        if (mpPath.Length > 0 && !File.Exists(mpPath)) mpPath = System.IO.Path.Combine(repoRoot ?? Directory.GetCurrentDirectory(), mpRel);
        JsonObject? mp = mpPath.Length > 0 && File.Exists(mpPath) ? J.ParseObject(File.ReadAllText(mpPath)) : null;
        var tau = o["contactDuration_s"] as JsonObject;
        var cr = o["criteria"] as JsonObject;
        var cfg = new PayloadPartsConfig
        {
            Path = path, Root = J.Str(o, "root", "container"), MassPropsPath = mpRel, MassProps = mp,
            Zeta = J.Num(o, "zeta", ShockSpectrum.DefaultZeta),
            FnFactors = o["fnFactors"] is JsonArray ff ? J.Arr(ff) : [0.5, 0.71, 1, 1.41, 2], FnBasis = J.Str(o, "fnBasis", ""),
            TauNominal = tau is null ? 0.002 : J.Num(tau, "nominal", 0.002), TauMin = tau is null ? 0.001 : J.Num(tau, "min", 0.001),
            TauMax = tau is null ? 0.005 : J.Num(tau, "max", 0.005), TauBasis = tau is null ? "" : J.Str(tau, "basis", ""),
            SfYield = cr is null ? 1.25 : J.Num(cr, "yield", 1.25), SfUltimate = cr is null ? 1.5 : J.Num(cr, "ultimate", 1.5),
            GMargin = cr is null ? 1.0 : J.Num(cr, "gLimit", 1.0), CriteriaBasis = cr is null ? "" : J.Str(cr, "basis", ""),
            Hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant(),
            Title = J.StrOpt(o, "title"), MassNote = J.StrOpt(o, "massNote"), RegenNote = J.StrOpt(o, "regenNote"),
            DesignLimitG = o["designLoad"] is JsonObject dl ? J.NumReq(dl, "limitG", "designLoad") : null,
            DesignSf = o["designLoad"] is JsonObject dl2 ? J.Num(dl2, "sf", 1) : 1,
            DesignBasis = o["designLoad"] is JsonObject dl3 ? J.Str(dl3, "basis", "") : "",
            DesignLateralLimitG = o["designLoad"]?["lateral"] is JsonObject lt ? J.NumReq(lt, "limitG", "designLoad.lateral") : null,
            DesignLateralSf = o["designLoad"]?["lateral"] is JsonObject lt2 ? J.Num(lt2, "sf", 1) : 1,
            DesignLateralBasis = o["designLoad"]?["lateral"] is JsonObject lt3 ? J.Str(lt3, "basis", "") : "",
            DesignModesHz = o["designModes"]?["fn_Hz"] is JsonArray dm ? J.Arr(dm) : [],
            DesignModesBasis = o["designModes"] is JsonObject dmo ? J.Str(dmo, "basis", "") : "",
        };
        foreach (var wn in o["windows"] as JsonArray ?? new JsonArray())
        {
            var w = wn!.AsObject();
            cfg.Windows.Add(new PayloadWindowDef(J.StrReq(w, "id", "windows"), J.Str(w, "label", ""), J.StrReq(w, "from", "windows"), J.StrOpt(w, "to"), J.Num(w, "duration", 0)));
        }
        foreach (var pn in o["parts"] as JsonArray ?? throw new InvalidDataException($"{path}: parts 가 없습니다."))
        {
            var p = pn!.AsObject();
            var stations = p["stations"] is JsonArray sa ? J.Arr(sa).Select(x => (int?)(int)x).ToArray() : [null];
            foreach (var k in stations) cfg.Parts.Add(ParsePart(p, k));
        }
        return cfg;
    }

    private static string Sub(string s, int? k) => k is { } v ? s.Replace("{k}", v.ToString(System.Globalization.CultureInfo.InvariantCulture)) : s;

    private static PayloadPart ParsePart(JsonObject p, int? k)
    {
        string name = Sub(J.StrReq(p, "name", "parts"), k);
        string where = $"parts.{name}";
        var massO = p["mass"] as JsonObject ?? throw new InvalidDataException($"{where}: mass 가 필요합니다.");
        string[] bodies = massO["bodies"] is JsonArray ba ? ba.Select(x => Sub(x!.GetValue<string>(), k)).ToArray() : [];
        MassPropsSelection? mparts = null;
        if (massO["massprops"] is JsonObject mo)
        {
            (string, int)? below = null;
            if (mo["below"] is JsonObject bo)
            {
                int ax = (int)J.NumReq(bo, "axis", where);
                if (ax is < 0 or > 2) throw new InvalidDataException($"{where}.mass.massprops.below.axis: 0 | 1 | 2 (Fusion x·y·z)");
                below = (J.StrReq(bo, "body", where), ax);
            }
            string[] Names(string key) => mo[key] is JsonArray a ? a.Select(x => x!.GetValue<string>()).ToArray() : [];
            mparts = new MassPropsSelection(J.StrReq(mo, "group", where), Names("parts"), Names("prefix"), Sub(J.StrReq(mo, "scaleBody", where), k), below, Names("exclude"));
        }
        if (bodies.Length == 0 && mparts is null) throw new InvalidDataException($"{where}.mass: bodies 또는 massprops 가 필요합니다.");
        (string, Vec3)? at = null;
        if (p["at"] is JsonObject ao) at = (Sub(J.StrReq(ao, "body", where), k), J.V3Opt(ao, "fusion") ?? throw new InvalidDataException($"{where}.at.fusion 이 필요합니다."));
        else if (J.Str(p, "at", "cg") != "cg") throw new InvalidDataException($"{where}.at: \"cg\" 또는 {{body, fusion}}");
        var mount = new List<MountElement>();
        double? fn = null; string mgrade = "D", mbasis = "";
        if (p["mount"] is JsonObject m)
        {
            mgrade = J.Str(m, "grade", "D"); mbasis = J.Str(m, "basis", "");
            fn = J.NumOpt(m, "fn_Hz");
            foreach (var en in m["series"] as JsonArray ?? new JsonArray()) mount.Add(MountStiffness(en!.AsObject(), where));
            if (fn is null && mount.Count == 0) throw new InvalidDataException($"{where}.mount: fn_Hz 또는 series 가 필요합니다.");
        }
        string? mountFrom = J.StrOpt(p, "mountFrom") is { } mf ? Sub(mf, k) : null;
        if (p["mount"] is null && mountFrom is null) throw new InvalidDataException($"{where}: mount 또는 mountFrom 이 필요합니다.");
        Material? mat = null; double coef = 0, share = 1; string gov = "", formula = "", kind = "";
        if (p["stress"] is JsonObject so)
        {
            var mn = J.StrReq(so, "material", where);
            mat = MaterialLibrary.Find(mn) ?? throw new InvalidDataException($"{where}: 재질 '{mn}' 을 라이브러리에서 찾지 못했습니다(mns materials).");
            (coef, gov, formula) = PartStress.Geometry(so, where, mat);
            share = J.Num(so, "share", 1);
            kind = J.Str(so, "kind", "");
        }
        double? gl = null; string glb = "";
        if (p["gLimit"] is JsonObject go) { gl = J.NumReq(go, "G", where); glb = J.Str(go, "basis", ""); }
        var dir = J.Str(p, "direction", "vector");
        if (dir is not ("axial" or "lateral" or "vector")) throw new InvalidDataException($"{where}.direction: axial | lateral | vector ('{dir}')");
        return new PayloadPart
        {
            Name = name, Label = Sub(J.Str(p, "label", name), k), Station = k, MassBodies = bodies, MassParts = mparts, At = at, Direction = dir,
            MountFrom = mountFrom, BaseFrom = J.StrOpt(p, "baseFrom") is { } bf ? Sub(bf, k) : null, FnHz = fn, Mount = mount, MountGrade = mgrade, MountBasis = mbasis,
            Material = mat, Coef = coef, Share = share, Governing = gov, Formula = formula, StressKind = kind, GLimit = gl, GLimitBasis = glb,
            Ccx = p["ccx"]?.DeepClone() as JsonObject, Windows = p["windows"] is JsonArray wa ? wa.Select(x => x!.GetValue<string>()).ToArray() : null,
            Basis = J.Str(p, "basis", ""), Reference = J.Bool(p, "reference", false),
        };
    }

    /// <summary>장착 강성 요소 [N/m]: 외팔 관·사각·원형·끝 모멘트 보·핀(굽힘+전단 직렬)·가정 스프링.</summary>
    public static MountElement MountStiffness(JsonObject e, string where)
    {
        var kind = J.StrReq(e, "kind", where);
        string basis = J.Str(e, "basis", "");
        Material Mat() => MaterialLibrary.Find(J.StrReq(e, "material", where)) ?? throw new InvalidDataException($"{where}: 장착 재질을 찾지 못했습니다.");
        double N(string k) => J.NumReq(e, k, where);
        double k;
        switch (kind)
        {
            case "spring": k = N("k"); break;
            case "tubeCantilever":
            {
                var m = Mat(); double l = N("length");
                k = 3 * m.E * PartStress.SquareTubeI(N("width"), N("wall")) / (l * l * l);
                break;
            }
            case "cantileverRect":
            {
                var m = Mat(); double l = N("length"), b = N("width"), h = N("height");
                k = J.Num(e, "count", 1) * 3 * m.E * (b * h * h * h / 12) / (l * l * l);
                break;
            }
            case "cantileverRound":
            {
                // 원형 중실 외팔 n개 병렬: 3EI/L³, I = πd⁴/64
                var m = Mat(); double l = N("length"), d = N("diameter");
                k = J.Num(e, "count", 1) * 3 * m.E * (Math.PI * Math.Pow(d, 4) / 64) / (l * l * l);
                break;
            }
            case "momentArm":
            {
                // 끝 모멘트를 받는 사각 보(자유 길이 L)에 레버 a 로 달린 하중점: 보 끝 회전 θ = F·a·L/(EI) → 하중점 처짐 θ·a → k = n·EI/(a²·L)
                var m = Mat(); double l = N("length"), a = N("arm"), b = N("width"), h = N("height");
                k = J.Num(e, "count", 1) * m.E * (b * h * h * h / 12) / (a * a * l);
                break;
            }
            case "pin":
            {
                // 짧은 외팔 핀: Timoshenko — 굽힘 a³/(3EI) + 전단 a/(κGA), κ = 0.9(원형)
                var m = Mat(); double d = N("diameter"), a = N("lever");
                double i = Math.PI * Math.Pow(d, 4) / 64, area = Math.PI * d * d / 4, g = m.E / (2 * (1 + m.Nu));
                k = 1 / (a * a * a / (3 * m.E * i) + a / (0.9 * g * area));
                break;
            }
            default: throw new InvalidDataException($"{where}.mount.series.kind: spring | tubeCantilever | cantileverRect | cantileverRound | momentArm | pin ('{kind}')");
        }
        if (!(k > 0) || !double.IsFinite(k)) throw new InvalidDataException($"{where}: 장착 강성이 유효하지 않습니다.");
        return new MountElement(kind, k, basis);
    }
}

/// <summary>
/// 하중 측정 탐침: 루트 물체(컨테이너)의 매 스텝 비중력 가속도(동체)·각속도·각가속도(충격량 성분 제외)·합성 CG·질량과
/// 접촉 충격량(벡터)을 기록한다. 컨테이너가 자유 루트가 된 뒤부터(노출 이후).
/// </summary>
public sealed class ShockProbe
{
    public required string RootName { get; init; }
    public List<double> T { get; } = new();
    public List<Vec3> F { get; } = new();
    public List<Vec3> W { get; } = new();
    public List<Vec3> Alpha { get; } = new();
    public List<Vec3> Cg { get; } = new();
    public List<double> Mass { get; } = new();
    /// <summary>대기상대속도(동체, m/s) — 총받음각 α_t = acos(v_x/|v|).</summary>
    public List<Vec3> Air { get; } = new();

    public double AlphaTotalDeg(int i)
    {
        var v = Air[i];
        double n = v.Norm;
        return n > 1e-6 ? Math.Acos(Math.Clamp(v.X / n, -1, 1)) * 180 / Math.PI : 0;
    }
    /// <summary>접촉 충격량: 시각, CG 속도 변화(동체), 각속도 변화(동체), 상대 물체, |J|, 작용점(동체), 순응 접촉 펄스(충격량 모델이면 null).</summary>
    public List<(double T, Vec3 Dv, Vec3 Dw, string Other, double J, Vec3 PointBody, ContactPulse? Pulse)> Impulses { get; } = new();

    private Body? _body;
    private Vec3 _w0; private double _m0, _t0; private bool _was;
    private int _imp;

    public void Before(World w)
    {
        _body ??= w.GetBody(RootName);
        var r = _body;
        _was = r.Mode == BodyMode.Free && r.Root == r;
        if (_was) { _w0 = r.W; _m0 = r.Mass; }
        _t0 = w.T;
    }

    public void After(World w)
    {
        var r = _body!;
        var log = w.Collisions?.ImpulseLog;
        bool free = r.Mode == BodyMode.Free && r.Root == r;
        var dwImp = Vec3.Zero;
        if (log is not null)
        {
            for (; _imp < log.Count; _imp++)
            {
                var ci = log[_imp];
                if (!free || (ci.RootA != r && ci.RootB != r)) continue;
                var j = ci.RootB == r ? ci.J : ci.J * -1;
                var jb = r.Q.InverseRotate(j);
                var arm = r.Q.InverseRotate(ci.Point - r.R);
                var dw = r.InvInertia * Vec3.Cross(arm, jb);
                dwImp += dw;
                var other = ci.RootB == r ? ci.RootA : ci.RootB;
                Impulses.Add((ci.Time, jb / r.Mass, dw, other.Name, j.Norm, arm + r.Cg, ci.Pulse));
            }
        }
        if (!free) return;
        double h = w.T - _t0;
        var g = w.Env.GravityVector;
        var f = r.Q.InverseRotate(r.AccelEnd - g);
        var alpha = _was && Math.Abs(r.Mass - _m0) < 1e-9 && h > 1e-9 ? (r.W - _w0 - dwImp) / h : (Alpha.Count > 0 ? Alpha[^1] : Vec3.Zero);
        T.Add(w.T); F.Add(f); W.Add(r.W); Alpha.Add(alpha); Cg.Add(r.Cg); Mass.Add(r.Mass); Air.Add(r.Q.InverseRotate(r.AirVelocity));
    }

    /// <summary>동체 점 p(루트 BRF)의 강체 비중력 가속도 시간 이력(기록 스텝 시각).</summary>
    public Vec3 AccelAt(int i, Vec3 p)
    {
        var rr = p - Cg[i];
        return F[i] + Vec3.Cross(Alpha[i], rr) + Vec3.Cross(W[i], Vec3.Cross(W[i], rr));
    }
}

/// <summary>
/// 부품 한 개 · 창 한 개 결과. DynModesG·SfYieldModes·SfUltimateModes·FnModesHz = 설계 FEA 고유진동수 범위(cfg.DesignModesHz 최소–최대)로 거른
/// 1자유도 SRS 최대(탑재 부품만, run.DesignModes 일 때 — 없으면 NaN).
/// </summary>
public sealed record PartWindowResult(string Window, double RigidG, double DynNominalG, double DynG, double FnUsedHz, double Daf, double ForceN, double StressPa,
    double SfYield, double SfUltimate, double GMargin,
    double DynModesG = double.NaN, double SfYieldModes = double.NaN, double SfUltimateModes = double.NaN, double FnModesHz = double.NaN);

/// <summary>부품 한 개 결과(실행 하나): 질량·위치·고유진동수·창별·최대.</summary>
public sealed class PartResult
{
    public required PayloadPart Part { get; init; }
    public double Mass { get; init; }
    public Vec3 Position { get; init; }
    public double FnHz { get; init; }
    public double KMount { get; init; }
    public List<PartWindowResult> Windows { get; } = new();
    public PartWindowResult? Worst => Windows.Count == 0 ? null : Windows.MaxBy(w => w.DynG);
    public double MinSfYield => Windows.Select(w => w.SfYield).DefaultIfEmpty(double.PositiveInfinity).Min();
    public double MinSfUltimate => Windows.Select(w => w.SfUltimate).DefaultIfEmpty(double.PositiveInfinity).Min();
}

/// <summary>실행 하나의 결과.</summary>
public sealed class PayloadRun
{
    public required World World { get; init; }
    public required ShockProbe Probe { get; init; }
    public double Tau { get; init; }
    public Dictionary<string, (double T0, double T1)> WindowSpans { get; } = new();
    /// <summary>부품 위치(루트 BRF) — 초기 수납 상태에서 고정(분리된 물체의 실행 후 위치를 쓰지 않도록).</summary>
    public Dictionary<string, Vec3> Positions { get; } = new();
    public List<PartResult> Parts { get; } = new();
    public double[] InputValues { get; init; } = [];
    public string? Error { get; set; }
    /// <summary>설계 FEA 고유진동수 범위 SRS 도 계산(공칭·스윕 — MC 는 끔, 계산량).</summary>
    public bool DesignModes { get; init; }
    /// <summary>순응 접촉 펄스가 있으면 그 모양을 쓴다(false = 충격량 모델 비교: 모든 접촉을 가정 지속 Tau 반 사인으로).</summary>
    public bool UsePulses { get; init; } = true;
    /// <summary>순응 접촉 강성 배율(같은 실행의 펄스를 다시 적분 — 강성 범위 비교, 1 = 실행 값).</summary>
    public double PulseFactor { get; init; } = 1;
    /// <summary>탄성-소성 펄스를 탄성(힘 상한 없음)으로 다시 적분 — 같은 6DOF 실행의 탄성 순응 비교.</summary>
    public bool ElasticPulses { get; init; }
    private readonly Dictionary<ContactPulse, ContactPulse> _rescaled = new();

    /// <summary>탐침 충격량의 접촉력 모양(UsePulses·PulseFactor·ElasticPulses 반영, 충격량 모델이면 null).</summary>
    public ContactPulse? PulseOf(ContactPulse? p)
    {
        if (!UsePulses || p is null) return null;
        bool el = ElasticPulses && p.Plasticity is not null;
        if (Math.Abs(PulseFactor - 1) < 1e-12 && !el) return p;
        lock (_rescaled)
        {
            if (!_rescaled.TryGetValue(p, out var q)) _rescaled[p] = q = el ? p.Elastic(PulseFactor) : p.Rescaled(PulseFactor);
            return q;
        }
    }

    /// <summary>탄성-소성 펄스가 하나라도 있는가.</summary>
    public bool HasPlasticPulses => Probe.Impulses.Any(i => i.Pulse?.Plasticity is not null);

    /// <summary>순응 접촉 펄스가 하나라도 있는가(충격량 모델이면 false).</summary>
    public bool HasPulses => Probe.Impulses.Any(i => i.Pulse is not null);
}

/// <summary>
/// 수납물 충격 하중(사용자 질문 2026-10-09, PRD FR-AN-08·AB절): 6DOF 컨테이너 운동(드로그·주낙하산 개방 브레이크 충격, 3분할 분리 펄스,
/// 잔해 재접촉 충격량)에서 부품 위치 강체 가속도 → 장착 1자유도 SRS 동적 응답(고유진동수 = 장착 강성 닫힌 식, ×0.5–2 범위 최대)
/// → 하중 F = m·a → 닫힌 식 응력 → 항복·극한 안전계수. 등급 C(근사, FEA 아님) — 핵심 부품은 CalculiX 정적으로 대조.
/// </summary>
public static class PayloadShock
{
    public const double G0 = 9.80665;
    /// <summary>SRS 입력 재표본 간격 [s] — 최고 고유진동수 × 10 이상.</summary>
    public const double ResampleDt = 5e-5;

    /// <summary>시나리오를 탐침과 함께 실행(창 끝 = 마지막 창 끝 + 0.1 s, 최대 tMax).</summary>
    public static PayloadRun Run(ScenarioDoc doc, PayloadPartsConfig cfg, double tau, double tMax = 40, CancellationToken ct = default, double[]? inputValues = null,
        bool designModes = false)
    {
        var w = doc.Build();
        w.Recorder.Enabled = false;
        if (w.Collisions is { } cm) cm.ImpulseLog = new List<ContactImpulse>();
        var probe = new ShockProbe { RootName = cfg.Root };
        w.Initialize();
        var run = new PayloadRun { World = w, Probe = probe, Tau = tau, InputValues = inputValues ?? [], DesignModes = designModes };
        foreach (var p in cfg.Parts) run.Positions[p.Name] = PositionOf(w, cfg, p);
        double tStop = tMax;
        while (!w.Stopped && w.T < tStop)
        {
            ct.ThrowIfCancellationRequested();
            probe.Before(w);
            w.Step();
            probe.After(w);
            // 마지막 창의 끝이 정해지면(이벤트 발생) 그때까지만
            var last = cfg.Windows[^1];
            if (w.FiredEvents.TryGetValue(last.From, out var tf))
            {
                double end = last.To is { } to && w.FiredEvents.TryGetValue(to, out var tt) ? tt : last.Duration > 0 ? tf + last.Duration : double.NaN;
                if (double.IsFinite(end)) tStop = Math.Min(tStop, end + 0.1);
            }
        }
        foreach (var wd in cfg.Windows)
        {
            if (!w.FiredEvents.TryGetValue(wd.From, out var t0)) continue;
            double t1 = wd.To is { } to && w.FiredEvents.TryGetValue(to, out var tt) ? tt : wd.Duration > 0 ? t0 + wd.Duration : w.T;
            run.WindowSpans[wd.Id] = (t0, Math.Min(t1, w.T));
        }
        Evaluate(run, cfg);
        return run;
    }

    /// <summary>
    /// 부품 질량 [kg]: 물체 고유 질량 합 + massprops 부품 × (배율 물체 질량 / massprops 그룹 질량).
    /// massprops 요약에 설계 정정 보정(designCorrections[그룹] — 바디 재질 정정, 예: 캐리지 PA66 → Al6061)이 있으면
    /// 그룹 질량·정정 바디 질량은 보정 값을 쓴다(시나리오 물체도 같은 보정 질량 — 공칭 배율 1).
    /// </summary>
    public static double MassOf(World w, PayloadPartsConfig cfg, PayloadPart p)
    {
        double m = p.MassBodies.Sum(b => w.GetBody(b).OwnMass);
        if (p.MassParts is { } mp)
        {
            double gm = J.Num(cfg.MassProps?["designCorrections"]?[mp.Group] as JsonObject ?? GroupOf(cfg, p, mp), "mass", 0);
            double sel = SelectedMassParts(cfg, p).Sum(x => x.Mass);
            double scale = gm > 0 ? w.GetBody(mp.ScaleBody).OwnMass / gm : 1;
            m += sel * scale;
        }
        return m;
    }

    private static JsonObject GroupOf(PayloadPartsConfig cfg, PayloadPart p, MassPropsSelection mp) =>
        cfg.MassProps?["groups"]?[mp.Group] as JsonObject ?? throw new InvalidDataException($"{p.Name}: massprops 그룹 '{mp.Group}' 이 없습니다({cfg.MassPropsPath}).");

    /// <summary>
    /// massprops 선택이 고른 바디와 질량 [kg](그룹 배율 전 — 설계 정정 보정이 있으면 보정 질량): 정확 이름 ∪ 접두 ∪ 기준 바디 CG 아래(cg_m[축] &lt; 기준 바디 cg_m[축]) − 제외 접두.
    /// 받치는 질량을 손으로 적지 않고 CAD 바디 CG·질량에서 고르게 한다(예: 하판 아래 매달린 바디 → 스탠드오프).
    /// </summary>
    public static List<(string Body, double Mass)> SelectedMassParts(PayloadPartsConfig cfg, PayloadPart p)
    {
        var res = new List<(string, double)>();
        if (p.MassParts is not { } mp) return res;
        var grp = GroupOf(cfg, p, mp);
        var fix = cfg.MassProps?["designCorrections"]?[mp.Group] as JsonObject;
        var fixedBody = new Dictionary<string, double>();
        foreach (var bn in fix?["bodies"] as JsonArray ?? new JsonArray())
            fixedBody[J.StrReq(bn!.AsObject(), "body", "designCorrections")] = J.NumReq(bn.AsObject(), "mass_kg_to", "designCorrections");
        var parts = (grp["parts"] as JsonArray ?? new JsonArray()).Select(x => x!.AsObject()).ToList();
        double? limit = null;
        if (mp.Below is { } bl)
        {
            var refPart = parts.FirstOrDefault(x => J.Str(x, "body", "") == bl.Body) ?? throw new InvalidDataException($"{p.Name}: below 기준 바디 '{bl.Body}' 가 massprops 그룹 '{mp.Group}' 에 없습니다.");
            limit = CgOf(refPart, p.Name)[bl.Axis];
        }
        foreach (var po in parts)
        {
            var bn = J.Str(po, "body", "");
            bool pick = mp.Exact.Contains(bn) || mp.Prefix.Any(x => bn.StartsWith(x, StringComparison.Ordinal))
                        || (limit is { } lim && CgOf(po, p.Name)[mp.Below!.Value.Axis] < lim);
            if (!pick || (mp.Exclude ?? []).Any(x => bn.StartsWith(x, StringComparison.Ordinal))) continue;
            res.Add((bn, fixedBody.TryGetValue(bn, out var fm) ? fm : J.Num(po, "mass_kg", 0)));
        }
        if (!(res.Sum(x => x.Item2) > 0)) throw new InvalidDataException($"{p.Name}: massprops 그룹 '{mp.Group}' 에서 부품을 찾지 못했습니다.");
        return res;
    }

    private static double[] CgOf(JsonObject part, string owner) =>
        part["cg_m"] is JsonArray a && a.Count == 3 ? J.Arr(a) : throw new InvalidDataException($"{owner}: massprops 바디 '{J.Str(part, "body", "")}' 에 cg_m 이 없습니다(below 선택).");

    /// <summary>부품 위치(루트 BRF, 초기 수납 상태).</summary>
    public static Vec3 PositionOf(World w, PayloadPartsConfig cfg, PayloadPart p)
    {
        var root = w.GetBody(cfg.Root);
        if (p.At is { } at)
        {
            var b = w.GetBody(at.Body);
            return root.WorldToBrf(b.PointWorld(b.ModelToBody * at.Model));
        }
        double m = 0; var s = Vec3.Zero;
        foreach (var bn in p.MassBodies)
        {
            var b = w.GetBody(bn);
            m += b.OwnMass; s += root.WorldToBrf(b.OwnCgWorld) * b.OwnMass;
        }
        return m > 0 ? s / m : Vec3.Zero;
    }

    /// <summary>직렬 장착 강성 [N/m].</summary>
    public static double SeriesK(IEnumerable<MountElement> els) { double c = 0; foreach (var e in els) c += 1 / e.K; return c > 0 ? 1 / c : double.NaN; }

    /// <summary>부품 고유진동수 [Hz] = fn_Hz 또는 √(k/m)/2π (mountFrom 이면 그 부품의 값).</summary>
    public static (double Fn, double K) Frequency(World w, PayloadPartsConfig cfg, PayloadPart p)
    {
        if (p.MountFrom is { } mf) return Frequency(w, cfg, cfg.Part(mf));
        if (p.FnHz is { } f) return (f, double.NaN);
        double k = SeriesK(p.Mount), m = MassOf(w, cfg, p);
        return (Math.Sqrt(k / m) / (2 * Math.PI), k);
    }

    /// <summary>실행 설정(τ · 순응 펄스 사용 · 강성 배율)으로 <see cref="BaseAccel(ShockProbe, Vec3, double, double, double, double, Func{ContactPulse?, ContactPulse?}?)"/>.</summary>
    public static (double[] X, double[] Y, double[] Z) BaseAccel(PayloadRun run, Vec3 p, double t0, double t1, double dt = ResampleDt) =>
        BaseAccel(run.Probe, p, t0, t1, run.Tau, dt, run.PulseOf);

    /// <summary>
    /// 창 [t0, t1] 의 점 p 강체 가속도(3축, 균일 재표본) + 접촉 충격량: 점 속도 변화 Δv_p 를 접촉력 모양으로 —
    /// 순응 접촉 펄스(pulse(Pulse) ≠ null: 구조 강성으로 적분한 F(t)/J, 격자 면적 1 로 정규화) 또는 가정 지속 τ 반 사인(충격량 모델).
    /// pulse = null 이면 탐침의 펄스를 그대로 쓴다.
    /// </summary>
    public static (double[] X, double[] Y, double[] Z) BaseAccel(ShockProbe pr, Vec3 p, double t0, double t1, double tau, double dt = ResampleDt,
        Func<ContactPulse?, ContactPulse?>? pulse = null)
    {
        var ts = new List<double>(); var ax = new List<double>(); var ay = new List<double>(); var az = new List<double>();
        for (int i = 0; i < pr.T.Count; i++)
        {
            if (pr.T[i] < t0 - 0.002 || pr.T[i] > t1 + 0.002) continue;
            var a = pr.AccelAt(i, p);
            ts.Add(pr.T[i]); ax.Add(a.X); ay.Add(a.Y); az.Add(a.Z);
        }
        if (ts.Count == 0) return ([], [], []);
        var x = ShockSpectrum.Resample(ts, ax, t0, t1, dt);
        var y = ShockSpectrum.Resample(ts, ay, t0, t1, dt);
        var z = ShockSpectrum.Resample(ts, az, t0, t1, dt);
        // 접촉 충격량: 점 속도 변화 Δv_p = Δv_cg + Δω × (p − cg) 를 접촉력 모양(면적 Δv_p)으로
        foreach (var im in pr.Impulses)
        {
            if (im.T < t0 || im.T >= t1) continue;
            int ci = NearestIndex(pr.T, im.T);
            var dvp = im.Dv + Vec3.Cross(im.Dw, p - pr.Cg[ci]);
            if ((pulse is null ? im.Pulse : pulse(im.Pulse)) is { } cp && cp.Duration > 0)
            {
                // 순응 접촉: 격자 표본 F(t)/J 를 면적 1 로 다시 맞춰(격자 간격보다 짧은 펄스도 운동량 보존) 더한다
                int j0 = (int)Math.Floor((im.T - t0) / dt), m = (int)Math.Ceiling(cp.Duration / dt) + 1;
                var sv = new double[m + 1];
                double area = 0;
                for (int k = 0; k <= m; k++) { sv[k] = cp.ShapeAt(t0 + (j0 + k) * dt - im.T); area += sv[k] * dt; }
                if (!(area > 0)) { sv[0] = 1 / dt; area = 1; }
                for (int k = 0; k <= m; k++)
                {
                    int i = j0 + k;
                    if (i < 0 || i >= x.Length) continue;
                    double s = sv[k] / area;
                    x[i] += dvp.X * s; y[i] += dvp.Y * s; z[i] += dvp.Z * s;
                }
                continue;
            }
            double amp = Math.PI / (2 * tau);
            int i0 = (int)Math.Floor((im.T - t0) / dt), n = (int)Math.Ceiling(tau / dt);
            for (int k = 0; k <= n; k++)
            {
                int i = i0 + k;
                if (i < 0 || i >= x.Length) continue;
                double s = Math.Sin(Math.PI * Math.Clamp((t0 + i * dt - im.T) / tau, 0, 1)) * amp;
                x[i] += dvp.X * s; y[i] += dvp.Y * s; z[i] += dvp.Z * s;
            }
        }
        return (x, y, z);
    }

    private static int NearestIndex(List<double> t, double v)
    {
        int i = t.BinarySearch(v);
        if (i >= 0) return i;
        i = ~i;
        return Math.Clamp(i, 0, t.Count - 1);
    }

    private static double Pick(string dir, (double A, double L, double V) r) => dir switch { "axial" => r.A, "lateral" => r.L, _ => r.V };

    private static (double A, double L, double V) Peak3(double[] x, double[] y, double[] z)
    {
        double a = 0, l = 0, v = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double l2 = y[i] * y[i] + z[i] * z[i];
            a = Math.Max(a, Math.Abs(x[i])); l = Math.Max(l, Math.Sqrt(l2)); v = Math.Max(v, Math.Sqrt(l2 + x[i] * x[i]));
        }
        return (a, l, v);
    }

    /// <summary>실행 결과 → 부품별·창별 G·하중·응력·안전계수.</summary>
    public static void Evaluate(PayloadRun run, PayloadPartsConfig cfg)
    {
        var w = run.World; var pr = run.Probe;
        run.Parts.Clear();
        foreach (var p in cfg.Parts)
        {
            double m = MassOf(w, cfg, p);
            var pos = run.Positions.TryGetValue(p.Name, out var pp) ? pp : PositionOf(w, cfg, p);
            var (fn, k) = Frequency(w, cfg, p);
            var res = new PartResult { Part = p, Mass = m, Position = pos, FnHz = fn, KMount = k };
            foreach (var wd in cfg.Windows)
            {
                if (p.Windows is { } only && !only.Contains(wd.Id)) continue;
                if (!run.WindowSpans.TryGetValue(wd.Id, out var span) || !(span.T1 > span.T0)) continue;
                double rigid, dynNom = double.NaN, dyn = 0, fUsed = fn;
                double[] px, py, pz;   // 부품 위치 강체 가속도(설계 모드 SRS 입력)
                if (p.BaseFrom is { } bf)
                {
                    // 2자유도 사슬: 상류 장착(드론 = m_up − m, k_up) 위에 이 부품(m, k = m(2πfₙ)²) — 기저 = 상류 위치 컨테이너 가속도
                    var bp = cfg.Part(bf);
                    var bpos = run.Positions.TryGetValue(bp.Name, out var bq) ? bq : PositionOf(w, cfg, bp);
                    var (bx, by, bz) = BaseAccel(run, bpos, span.T0, span.T1);
                    if (bx.Length == 0) continue;
                    var (cx, cy, cz) = BaseAccel(run, pos, span.T0, span.T1);
                    (px, py, pz) = (cx, cy, cz);
                    rigid = Pick(p.Direction, Peak3(cx, cy, cz));
                    double mUp = MassOf(w, cfg, bp), m1 = Math.Max(mUp - m, 0.05 * mUp);
                    var (fUp, kUp0) = Frequency(w, cfg, bp);
                    double kUp = double.IsFinite(kUp0) ? kUp0 : mUp * Math.Pow(2 * Math.PI * fUp, 2);
                    double k2 = m * Math.Pow(2 * Math.PI * fn, 2);
                    foreach (var fu in cfg.FnFactors)
                        foreach (var fac in cfg.FnFactors)
                        {
                            var r = ShockSpectrum.Chain2Maximax3(bx, by, bz, ResampleDt, m1, kUp * fu * fu, m, k2 * fac * fac, cfg.Zeta);
                            double v = Pick(p.Direction, r);
                            if (Math.Abs(fu - 1) < 1e-9 && Math.Abs(fac - 1) < 1e-9) dynNom = v;
                            if (v > dyn) { dyn = v; fUsed = fn * fac; }
                        }
                }
                else
                {
                    var (bx, by, bz) = BaseAccel(run, pos, span.T0, span.T1);
                    if (bx.Length == 0) continue;
                    (px, py, pz) = (bx, by, bz);
                    rigid = Pick(p.Direction, Peak3(bx, by, bz));
                    foreach (var fac in cfg.FnFactors)
                    {
                        var r = ShockSpectrum.Maximax3(bx, by, bz, ResampleDt, fn * fac, cfg.Zeta);
                        double v = Pick(p.Direction, r);
                        if (Math.Abs(fac - 1) < 1e-9) dynNom = v;
                        if (v > dyn) { dyn = v; fUsed = fn * fac; }
                    }
                }
                double force = m * dyn;
                double sigma = p.HasStress ? p.Coef * force * p.Share : double.NaN;
                double sfy = double.NaN, sfu = double.NaN;
                if (p.HasStress && sigma > 0)
                {
                    var mat = p.Material!;
                    sfu = mat.Ultimate / sigma;
                    sfy = mat.Brittle ? double.NaN : mat.Yield / sigma;
                }
                double gm = p.GLimit is { } gl && dyn > 0 ? gl / (dyn / G0) : double.NaN;
                // 설계 FEA 고유진동수 범위(탑재 전체 모드 — 장착 사슬 유연성 포함)로 거른 1자유도 SRS: 부품 위치 강체 가속도 입력
                double dm = double.NaN, fm = double.NaN, sym = double.NaN, sum = double.NaN;
                if (run.DesignModes && p.Station is not null && cfg.DesignModesHz.Length > 0)
                {
                    foreach (var f in ModeSweepHz(cfg))
                    {
                        double v = Pick(p.Direction, ShockSpectrum.Maximax3(px, py, pz, ResampleDt, f, cfg.Zeta));
                        if (!(v <= dm)) { dm = v; fm = f; }
                    }
                    if (p.HasStress && dm > 0)
                    {
                        double sm = p.Coef * m * dm * p.Share;
                        sum = p.Material!.Ultimate / sm;
                        sym = p.Material.Brittle ? double.NaN : p.Material.Yield / sm;
                    }
                }
                res.Windows.Add(new PartWindowResult(wd.Id, rigid / G0, dynNom / G0, dyn / G0, fUsed, rigid > 0 ? dyn / rigid : double.NaN,
                    force, sigma, sfy, sfu, gm, dm / G0, sym, sum, fm));
            }
            run.Parts.Add(res);
        }
    }

    /// <summary>
    /// 설계 극한 하중 준정적 판정(설계 쪽 국부 FEA 와 같은 하중 조건으로 비교): F = 부품이 받치는 질량 × 설계 극한 G × g₀(부품 하중 방향 —
    /// 동적 증폭 없음), σ = 같은 닫힌 식(계수 × F × 분담) → 항복·극한 SF. 설계 하중 기준이 없거나 응력 모델이 없으면 null.
    /// 측방 기준(designLoad.lateral)이 있으면 direction lateral 부품은 측방 극한 G(설계: 측방 제한 10 G × 2.0 = 20 G).
    /// </summary>
    public static (double G, double ForceN, double StressPa, double SfYield, double SfUltimate)? DesignUltimate(PayloadPartsConfig cfg, PartResult p)
    {
        double? gd = p.Part.Direction == "lateral" && cfg.DesignLateralUltimateG is { } gl ? gl : cfg.DesignUltimateG;
        if (gd is not { } g || !p.Part.HasStress || !(p.Mass > 0)) return null;
        double f = p.Mass * g * G0, s = p.Part.Coef * f * p.Part.Share;
        var mat = p.Part.Material!;
        return (g, f, s, mat.Brittle ? double.NaN : mat.Yield / s, mat.Ultimate / s);
    }

    /// <summary>설계 모드 SRS 진동수: 설계 FEA 고유진동수 + 그 최소–최대 범위 등간격 9점(모드 사이 응답 최대를 놓치지 않게).</summary>
    public static double[] ModeSweepHz(PayloadPartsConfig cfg)
    {
        if (cfg.DesignModesHz.Length == 0) return [];
        double lo = cfg.DesignModesHz.Min(), hi = cfg.DesignModesHz.Max();
        return cfg.DesignModesHz.Concat(Enumerable.Range(0, 9).Select(i => lo + (hi - lo) * i / 8)).Distinct().OrderBy(x => x).ToArray();
    }

    /// <summary>
    /// 설계 FEA 고유진동수(cfg.DesignModesHz)에서 창별 축(동체 x) SRS — 점 p(루트 BRF)의 강체 가속도(접촉 펄스 τ 포함) 입력, 감쇠 cfg.Zeta.
    /// 반환: 창, 강체 축 최대 [G], 진동수별 SRS 축 최대 [G](비 = SRS/강체 — 준정적이면 ≈ 1).
    /// </summary>
    public static List<(string Window, double RigidG, double[] SrsG)> DesignModeSrs(PayloadRun run, PayloadPartsConfig cfg, Vec3 p)
    {
        var res = new List<(string, double, double[])>();
        if (cfg.DesignModesHz.Length == 0) return res;
        foreach (var wd in cfg.Windows)
        {
            if (!run.WindowSpans.TryGetValue(wd.Id, out var span) || !(span.T1 > span.T0)) continue;
            var (x, y, z) = BaseAccel(run, p, span.T0, span.T1);
            if (x.Length == 0) continue;
            double rigid = x.Select(Math.Abs).Max();
            res.Add((wd.Id, rigid / G0, cfg.DesignModesHz.Select(f => ShockSpectrum.Maximax3(x, y, z, ResampleDt, f, cfg.Zeta).Axial / G0).ToArray()));
        }
        return res;
    }

    /// <summary>판정: 항복 SF ≥ 기준·극한 SF ≥ 기준(취성은 극한만), G 기준 여유 ≥ 1. 기준 없음 = "보고만".</summary>
    public static string Verdict(PayloadPartsConfig cfg, PayloadPart p, double sfy, double sfu, double gm)
    {
        if (p.HasStress)
        {
            bool okU = !(sfu < cfg.SfUltimate);
            bool okY = p.Material!.Brittle || !(sfy < cfg.SfYield);
            if (sfu < 1 || (!p.Material.Brittle && sfy < 1)) return "파손/항복";
            return okU && okY ? "통과" : "기준 미달";
        }
        if (double.IsFinite(gm)) return gm >= cfg.GMargin ? "통과(참고 G)" : "G 초과(참고)";
        return "보고만(허용치 없음)";
    }

    /// <summary>창의 컨테이너 CG 최대 G — 연속 강체 가속도 + 접촉 충격량 펄스(τ), 시각.</summary>
    public static (double G, double T) ContainerPeak(PayloadRun run, double t0, double t1)
    {
        var pr = run.Probe;
        int i0 = pr.T.FindIndex(t => t >= t0);
        if (i0 < 0) return (0, double.NaN);
        var (x, y, z) = BaseAccel(run, pr.Cg[i0], t0, t1);
        double best = 0; int bi = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double n = Math.Sqrt(x[i] * x[i] + y[i] * y[i] + z[i] * z[i]);
            if (n > best) { best = n; bi = i; }
        }
        return (best / G0, t0 + bi * ResampleDt);
    }

    /// <summary>창의 컨테이너 강체 최대 G(CG, 벡터) — 연속 가속도만(접촉 충격량 제외).</summary>
    public static (double G, double T) ContainerPeak(ShockProbe pr, double t0, double t1)
    {
        double best = 0, tb = double.NaN;
        for (int i = 0; i < pr.T.Count; i++)
        {
            if (pr.T[i] < t0 || pr.T[i] > t1) continue;
            double g = pr.F[i].Norm / G0;
            if (g > best) { best = g; tb = pr.T[i]; }
        }
        return (best, tb);
    }
}
