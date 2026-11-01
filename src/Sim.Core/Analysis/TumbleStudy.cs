using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Sim.Core.Engine;
using Sim.Core.Models;
using Sim.Core.Numerics;
using Sim.Core.Reliability;
using Sim.Core.Scenario;

namespace Sim.Core.Analysis;

/// <summary>텀블링 연구 옵션(시나리오 09 — 3분할 충격 변화 → 컨테이너 텀블링).</summary>
public sealed class TumbleStudyOptions
{
    public required string OutDir { get; init; }
    public int McRuns { get; init; } = 256;
    public ulong Seed { get; init; } = 4009;
    /// <summary>
    /// 공력 입력 교체 지점: CFD 공력 DB(mns.aero-db/1). 지정하면 컨테이너 aero 를 {db, damping} 으로 바꾼다
    /// (압력중심 이동 인자는 DB 의 Cm 이 대신하므로 건너뜀).
    /// </summary>
    public string? AeroDb { get; init; }
    /// <summary>감쇠 미계수(Clp, Cmq, Cnr) 교체 — CFD 강제진동·시험 추정값이 생기면 여기에.</summary>
    public Vec3? Damping { get; init; }
    /// <summary>1-D 스윕·2-D 지도 실행 여부(테스트에서 끄기).</summary>
    public bool Sweeps { get; init; } = true;
    public bool Maps { get; init; } = true;
    public Action<string>? Log { get; init; }
    public string Command { get; init; } = "";
    /// <summary>진동 피로(근사) 7b절 입력: <c>mns simpkg --run --modal</c> 의 summary.json(고유진동 · 바디별 99 % 응력). 없으면 f_s 와 가속도 진폭만.</summary>
    public string? FatigueSimpkg { get; init; }
}

/// <summary>
/// 3분할 분리 충격 변화 → 컨테이너 텀블링 연구(사용자 질문 2026-10-08, PRD AA절 FR-AN-02·AB절 FR-REL-03).
/// 시나리오 09(운반 탄체 120° 3분할 → 컨테이너 노출)의 분리 입력을 체계적으로 바꿔(1-D 스윕 · 2-D 지도 · LHS 몬테카를로)
/// 컨테이너 자세·각속도·텀블링 등급, 드로그 전개 순간 자세, 조각–컨테이너 재접촉, 잔해–드로그 간섭을 정량화한다.
/// 공력은 시나리오의 컨테이너 모델 그대로(L1 TR R-300 횡류 + 압력중심 + 감쇠 미계수) — <see cref="TumbleStudyOptions.AeroDb"/> 로 CFD DB 교체.
/// </summary>
public static class TumbleStudy
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ---------------- 판정 기준 ----------------

    /// <summary>텀블링 등급 문턱 [deg].</summary>
    public const double StableAoaDeg = 15, FlipAoaDeg = 90, TumbleRotationDeg = 360;
    /// <summary>드로그 전개 순간 총받음각 문턱 [deg]: 양호 &lt; 30 ≤ 주의 &lt; 90 ≤ 위험(뒤끝·드로그 쪽이 상류).</summary>
    public const double DrogueCautionDeg = 30, DrogueDangerDeg = 90;
    /// <summary>재접촉: 노출 후 이 시간이 지난 뒤의 조각·잔해–컨테이너 접촉(분리 순간의 밀어내기 접촉 제외) [s].</summary>
    public const double RecontactAfter = 0.05;

    public static readonly string[] ClassNames = ["T0 안정", "T1 진동", "T2 뒤집힘", "T3 텀블링"];
    public static readonly string[] DrogueClassNames = ["양호", "주의", "위험"];

    /// <summary>
    /// 텀블링 등급(자유비행 창 = 노출 → 드로그 전개):
    /// T3 텀블링 = 횡축(q,r) 누적 회전 ≥ 360°(끝-끝 완전 회전) · T2 뒤집힘 = 최대 총받음각 ≥ 90°(옆으로 서거나 뒤집힘, 완전 회전 전) ·
    /// T1 진동 = 15° ≤ 최대 총받음각 &lt; 90° · T0 안정 = 최대 총받음각 &lt; 15°.
    /// </summary>
    public static int Classify(double maxAoaDeg, double cumTransverseDeg) =>
        cumTransverseDeg >= TumbleRotationDeg ? 3 : maxAoaDeg >= FlipAoaDeg ? 2 : maxAoaDeg >= StableAoaDeg ? 1 : 0;

    public static int DrogueClass(double aoaDeg) => !double.IsFinite(aoaDeg) ? 2 : aoaDeg >= DrogueDangerDeg ? 2 : aoaDeg >= DrogueCautionDeg ? 1 : 0;

    // ---------------- 인자 ----------------

    /// <summary>분리 입력 인자. Sweep = 1-D 스윕 값(빈 배열 = 스윕 없음), Min/Max = MC 균등 분포 범위.</summary>
    public sealed record Factor(string Key, string Label, string Unit, double Nominal, double[] Sweep, double Min, double Max, string Basis);

    public static readonly Factor[] Factors =
    [
        new("dvScale", "조각 Δv 배율(3장 공통)", "×", 1, [0.25, 0.5, 1, 1.5, 2, 3], 0.25, 2.5,
            "공칭 Δv 12 m/s(조각당 반경 6 m/s + 힌지 회전) — 화약 꽃잎 분리 속도 공개 값 없음(D)"),
        new("petal1Factor", "조각1 Δv 비대칭 배율", "×", 1, [0, 0.25, 0.5, 1, 1.5, 2], 0, 2,
            "한 폭약 줄의 약폭·불발(0)~과장약(2) — 공개 값 없음(D)"),
        new("petal3DelayMs", "조각3 기폭 지연", "ms", 0.6, [0.6, 2, 5, 10, 20, 50], 0, 30,
            "공칭 120° 3줄 0.3 ms 간격 — 기폭 계열 지연 편차(D)"),
        new("shockScale", "컨테이너 충격 배율(15 G·10 ms)", "×", 1, [0, 0.5, 1, 2, 3], 0, 2,
            "기폭 강체 충격 15 G·10 ms — 공개 값 없음, 시나리오 출처 등급 범위 0–2(D)"),
        new("shockSideDeg", "충격 방향 측면 기울기", "°", 0, [0, 30, 60, 90], -90, 90,
            "공칭 = 탄체 아래(동체 +Z) — 비대칭 기폭이면 옆으로 기움(D)"),
        new("aoaDeg", "분리 순간 탄체 받음각", "°", 0, [-10, -5, 0, 5, 10, 15], -10, 15,
            "활공 디스펜서 분리 자세 미정(PRD 열린 질문 3, D)"),
        new("tipRateDps", "컨테이너 tip-off 각속도", "°/s", 0, [0, 30, 60, 120, 240, 360], 0, 180,
            "비대칭 충격량·탄체 각운동·조각 접촉이 주는 초기 각속도(D) — 2-D 지도에서 경계 탐색"),
        new("tipDirDeg", "tip-off 방향(피치 0°·요 90°)", "°", 0, [], 0, 360, "MC 전용 — 피치/요 평면 방향 균등"),
        new("dampScale", "감쇠 미계수 배율(Clp·Cmq·Cnr)", "×", 1, [0, 0.1, 0.25, 0.5, 1, 2], 0.05, 2,
            "Cmq −6 등 가정(D) — 공개 자료·CFD 강제진동 없음(FR-AER-03). 횡류 이론 추정은 배율 ≈ 0.1(7절) → MC 범위를 0.05–2 로 넓힘"),
        new("cpShiftPct", "압력중심 이동(길이 대비, + = 도어 쪽)", "%L", 0, [-10, -5, 0, 5, 10], -8, 8,
            "L1 측면적 중심 CP — 세장체 Munk 모멘트(불안정화)·비정상 박리 미반영 → 모델 형태 불확도 대리 인자"),
        new("drogueDelay", "노출→뚜껑·드로그 지연", "s", 1, [0.3, 0.5, 1, 1.5, 2, 3, 4], 0.3, 1.7,
            "가정 1 s(시나리오 출처 등급 0.3–1.7, D) — 자유비행 시간 = 텀블링 성장 시간"),
    ];

    public static Factor F(string key) => Factors.First(f => f.Key == key);

    /// <summary>2-D 지도 정의(x 인자, y 인자, 값).</summary>
    public sealed record MapDef(string Id, string Title, string XKey, double[] Xs, string YKey, double[] Ys);

    public static readonly MapDef[] Maps =
    [
        new("A", "조각 Δv 배율 × 조각1 비대칭", "dvScale", [0.25, 0.5, 1, 1.5, 2, 3], "petal1Factor", [0, 0.25, 0.5, 1, 1.5, 2]),
        new("B", "tip-off 각속도 × 드로그 지연(자유비행 시간)", "tipRateDps", [0, 30, 60, 120, 180, 240, 360], "drogueDelay", [0.3, 0.5, 1, 1.5, 2, 3, 4]),
        new("C", "분리 받음각 × 압력중심 이동(공력 모델 불확도)", "aoaDeg", [-10, -5, 0, 5, 10, 15], "cpShiftPct", [-10, -5, 0, 5, 10]),
        new("D", "감쇠 미계수 배율 × 드로그 지연(공력 감쇠 불확도 × 자유비행 시간)", "dampScale", [0, 0.05, 0.1, 0.25, 0.5, 1, 2], "drogueDelay", [0.3, 0.5, 1, 1.5, 2, 3, 4]),
    ];

    // ---------------- 진동 피로(근사) ----------------

    /// <summary>와류 흘림 Strouhal 범위 · 변동 양력 C'_L(rms) 범위 — LoadsStudyOptions 와 같은 공개 범위(St 0.20 공개 · 0.256 이 연구 3-D URANS, C'_L Norberg 2003 초임계).</summary>
    public static readonly (double Lo, double Hi) FatigueStrouhal = (0.20, 0.256), FatigueClRms = (0.05, 0.3);
    /// <summary>구조 감쇠비(가정 — ContainerAeroFea 와류 동적 계수와 같은 2 %).</summary>
    public const double FatigueZeta = 0.02;

    /// <summary>
    /// 7b 진동 피로(근사): 공칭 실행 자유비행 창(노출 → 드로그)의 대기속도 범위로 f_s = St·V/D, 변동 양력 가속도 진폭(강체) √2·C'_L·q_max·D·L/(m·g),
    /// <c>mns simpkg --run --modal</c> summary.json 의 고유진동수 · 바디별 99 % 응력(그 해석 가속도 기준)으로 공진 여유와 Miner 손상(공개 S-N 이 있는 6061 · 7075 만).
    /// </summary>
    public static List<string> FatigueLines(Run nom, Baseline bl, string? simpkgSummary)
    {
        var L = new List<string> { "### 7b. 진동 피로(근사)", "" };
        double d = bl.Diameter, len = bl.Length, m = bl.Mass, T = nom.TDrogue - nom.TExposed, q = nom.MaxQ;
        double vLo = nom.MinSpeedFree, vHi = nom.MaxSpeedFree;
        if (!(d > 0) || !double.IsFinite(vLo) || !double.IsFinite(vHi) || !(T > 0))
        {
            L.Add("- 공칭 실행에 자유비행 창(노출 → 드로그)이 없어 계산하지 않음.");
            return L;
        }
        double fsLo = Structures.VibrationFatigue.SheddingHz(FatigueStrouhal.Lo, vLo, d), fsHi = Structures.VibrationFatigue.SheddingHz(FatigueStrouhal.Hi, vHi, d);
        double aLo = Structures.VibrationFatigue.LiftAccelAmplitudeG(FatigueClRms.Lo, q, d, len, m), aHi = Structures.VibrationFatigue.LiftAccelAmplitudeG(FatigueClRms.Hi, q, d, len, m);
        L.Add($"- 와류 흘림: 원통 Ø{d * 1000:F0} mm, 공칭 자유비행 {T:F2} s(노출 → 드로그) 대기속도 {vLo:F0}–{vHi:F0} m/s, St {FatigueStrouhal.Lo:0.00}–{FatigueStrouhal.Hi:0.000} → **f_s = St·V/D = {fsLo:F0}–{fsHi:F0} Hz**.");
        L.Add($"- 변동 양력 가속도 진폭(강체): √2·C'_L·q·D·L/(m·g), q = 자유비행 최대 동압 {q:F0} Pa, L {len:F3} m, m {m:F1} kg, C'_L rms {FatigueClRms.Lo}–{FatigueClRms.Hi} → **{aLo:F2}–{aHi:F2} G**.");
        if (simpkgSummary is null || !File.Exists(simpkgSummary))
        {
            L.Add("- 구조 고유진동수 입력 없음(`--fatigue-simpkg <mns simpkg --run --modal 의 summary.json>`) — 공진 여유 · 손상은 계산하지 않음.");
            L.Add("");
            return L;
        }
        var s = J.ParseObject(File.ReadAllText(simpkgSummary));
        var modes = (s["modal"]?["modes"] as JsonArray ?? []).OfType<JsonObject>().Select(x => (Mode: (int)J.Num(x, "mode", 0), Hz: J.Num(x, "hz", double.NaN), Body: J.Str(x, "dominantBody", ""))).Where(x => x.Hz > 0).ToList();
        L.Add($"- 구조: `{simpkgSummary}` ({J.Str(s, "project", "")} / {J.Str(s, "study", "")}) — {s["modal"]?["boundary"]?.GetValue<string>() ?? "고유진동 없음"}.");
        L.Add("");
        if (modes.Count == 0) { L.Add("- summary.json 에 고유진동(modal)이 없음 — `--modal` 로 다시 실행."); L.Add(""); return L; }
        L.Add($"| 모드 | f_n [Hz] | 주 변위 바디 | f_s/f_n | 판정(f_s < {Structures.VibrationFatigue.FrequencyLimit:0.0} f_n) |");
        L.Add("|---|---|---|---|---|");
        var res = modes.Select(x => (x, R: Structures.VibrationFatigue.CheckResonance(x.Hz, fsLo, fsHi))).ToList();
        foreach (var (x, r) in res) L.Add($"| {x.Mode} | {x.Hz:F1} | {x.Body} | {r.Ratio:F2} | {r.Verdict} |");
        var gov = res.MaxBy(t => Structures.VibrationFatigue.Amplification(t.R.Ratio, FatigueZeta));
        double h = Structures.VibrationFatigue.Amplification(gov.R.Ratio, FatigueZeta);
        L.Add("");
        L.Add($"지배 모드 {gov.x.Mode}({gov.x.Hz:F1} Hz, f_s/f_n {gov.R.Ratio:F2}) → 1자유도 증폭 H = {h:F1}(ζ {FatigueZeta}, 가정). 반복 수 n = f_s,max × {T:F2} s = {fsHi * T:N0}(보수: 최대 진폭을 자유비행 전 구간에).");
        L.Add("");
        double aRef = s["acceleration_mps2"] is JsonArray acc ? new Vec3(J.ToDouble(acc[0]!), J.ToDouble(acc[1]!), J.ToDouble(acc[2]!)).Norm / 9.80665 : double.NaN;
        var rows = new List<(string Body, string Mat, double Sig, Structures.VibrationFatigue.PartEstimate Lo, Structures.VibrationFatigue.PartEstimate Hi)>();
        foreach (var b in (s["bodies"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (J.Bool(b, "fixed", false) || J.Num(b, "p99VonMises_Pa", double.NaN) is not (> 0 and var sig)) continue;
            string body = J.Str(b, "body", ""), mat = J.Str(b, "material", "");
            var lo = Structures.VibrationFatigue.EstimatePart(body, mat, sig, aRef, aLo, gov.R.Ratio, FatigueZeta, fsHi, T);
            var hi = Structures.VibrationFatigue.EstimatePart(body, mat, sig, aRef, aHi, gov.R.Ratio, FatigueZeta, fsHi, T);
            if (lo is not null && hi is not null) rows.Add((body, mat, sig, lo, hi));
        }
        if (rows.Count == 0) { L.Add("- 공개 S-N 자료가 있는 재료(6061 · 7075) 바디가 없어 손상을 계산하지 않음."); L.Add(""); return L; }
        L.Add($"Miner 손상 상위 10 바디(정적 99 % 응력 σ_ref 는 simpkg 해석 가속도 {aRef:F1} G 기준 — σ_a = σ_ref/{aRef:F1} G × a_dyn × H):");
        L.Add("");
        L.Add("| 바디 | 재료 | σ_ref 99 % [MPa] | σ_a [MPa] (C'_L 0.05 / 0.3) | 파손 반복 N (0.3) | **D** (C'_L 0.05 / 0.3) |");
        L.Add("|---|---|---|---|---|---|");
        foreach (var x in rows.OrderByDescending(x => x.Hi.Damage).Take(10))
            L.Add($"| {x.Body} | {x.Mat} | {x.Sig / 1e6:G4} | {x.Lo.SigmaA / 1e6:G3} / {x.Hi.SigmaA / 1e6:G3} | {x.Hi.CyclesToFailure:G3} | {x.Lo.Damage:G3} / **{x.Hi.Damage:G3}** |");
        L.Add("");
        L.Add($"- S-N: 6061-T6 — {Structures.VibrationFatigue.Al6061T6.Source}: {Structures.VibrationFatigue.Al6061T6.Form}. 7075-T6 — {Structures.VibrationFatigue.Al7075T6.Source}: {Structures.VibrationFatigue.Al7075T6.Form}.");
        L.Add("- 한계(근사): 응력 분포는 정적 해석(그 가속도 방향)의 비례 확대 — 와류 측력은 컨테이너 축에 수직이라 방향이 다를 수 있다 · 강체 가속도 × 1자유도 증폭(모드 질량 · 형상 없음) · 평균 응력 · 노치 · 표면 보정 없음 · 한 진폭 블록. 판정이 아니라 크기 가늠(D ≪ 1 이면 무시 가능, D ~ 0.1 이상이면 진동 시험 · 비정상 CFD 필요).");
        L.Add("");
        return L;
    }

    // ---------------- 실행 결과 ----------------

    public sealed class Run
    {
        public required string Tag { get; init; }
        public required Dictionary<string, double> Values { get; init; }
        public string? Error { get; set; }
        public double TExposed, TDrogue, TEnd;
        /// <summary>자유비행 창(노출 → 드로그).</summary>
        public double MaxAoaFree, CumFree, MaxRateFree;
        /// <summary>드로그 전개 순간.</summary>
        public double AoaAtDrogue, RateAtDrogue, SpeedAtDrogue;
        /// <summary>드로그 구간(드로그 → 종료) — 의도한 수평→수직 회전 포함(참고).</summary>
        public double MaxAoaDrogue, CumDrogue;
        public int Class, DrogueCls;
        /// <summary>조각·잔해–컨테이너 접촉(전체 / 노출 50 ms 이후 재접촉).</summary>
        public int Contacts, Recontacts;
        public double MaxContactSpeed, MaxContactImpulse, LastContactTime = double.NaN;
        public string RecontactWith = "";
        /// <summary>드로그 캐노피·라인 영역 간섭 기록·전개 이상 수, 드로그 개방 G.</summary>
        public int DrogueInterferences, DrogueAnomalies;
        public double DrogueG;
        /// <summary>하중 포락선(재질 연구 입력): 최대 동압 q, 최대 횡류 동압 q·sin²α_t, 최대 각속도·각가속도, 최대 G.</summary>
        public double MaxQ, MaxCrossQ, MaxOmega, MaxAlphaDot, MaxG;
        /// <summary>자유비행 창 대기속도 범위(진동 피로 — 와류 흘림 진동수).</summary>
        public double MaxSpeedFree = double.NaN, MinSpeedFree = double.NaN;
    }

    public sealed class Result
    {
        public required List<Run> Nominal { get; init; }
        public Dictionary<string, List<Run>> Sweeps { get; } = new();
        public Dictionary<string, List<Run>> MapRuns { get; } = new();
        public List<Run> Mc { get; } = new();
        public string[] McKeys { get; set; } = [];
        public double[] Prcc { get; set; } = [];
        public double[] PrccDrogue { get; set; } = [];
        public JsonObject Envelope { get; set; } = new();
        public string AeroLabel { get; set; } = "";
        public double ElapsedSeconds { get; set; }
        public string Markdown { get; set; } = "";
    }

    // ---------------- 문서 조작 ----------------

    /// <summary>공칭 문서에서 읽어 둔 기준값(인자 적용의 원점).</summary>
    public sealed class Baseline
    {
        public required string Exposed { get; init; }
        public required Vec3[] PetalVel { get; init; }
        public required Vec3[] PetalOmega { get; init; }
        public required double[] PetalT { get; init; }
        public double ShockG { get; init; }
        public Vec3 Damping { get; init; }
        public Vec3? Cp { get; init; }
        public double Length { get; init; }
        public double Diameter { get; init; }
        public double TExposedNominal { get; init; }
        public bool HasDb { get; init; }
        /// <summary>컨테이너 + 수납된 자손 질량 [kg](노출 시점 기준 시나리오 값) — 변동 양력 가속도 진폭.</summary>
        public double Mass { get; init; }
    }

    private static JsonObject Ev(JsonObject root, string name) =>
        root["events"]!.AsArray().OfType<JsonObject>().FirstOrDefault(e => J.StrOpt(e, "name") == name)
        ?? throw new InvalidDataException($"이벤트 '{name}' 이 없습니다(3분할 시나리오 09 형식 필요).");

    private static JsonObject SeparateAction(JsonObject ev, string body) =>
        ev["do"]!.AsArray().OfType<JsonObject>().FirstOrDefault(a => J.StrOpt(a, "type") == "separate" && J.StrOpt(a, "body") == body)
        ?? throw new InvalidDataException($"이벤트 '{J.StrOpt(ev, "name")}' 에 {body} 분리 동작이 없습니다.");

    private static JsonObject BodyDef(JsonObject root, string name) =>
        root["bodies"]!.AsArray().OfType<JsonObject>().FirstOrDefault(b => J.StrOpt(b, "name") == name)
        ?? throw new InvalidDataException($"물체 '{name}' 이 없습니다.");

    private static JsonArray A(Vec3 v) => new(v.X, v.Y, v.Z);

    /// <summary>공력 교체(CFD DB·감쇠) 적용 문서.</summary>
    public static ScenarioDoc Prepare(ScenarioDoc doc, TumbleStudyOptions o)
    {
        var d = doc.Clone();
        var cont = BodyDef(d.Root, "container");
        var aero = cont["aero"] as JsonObject ?? new JsonObject();
        if (o.AeroDb is { } db)
        {
            var damping = aero["damping"]?.DeepClone() ?? new JsonArray(-2, -6, -6);
            aero = new JsonObject { ["db"] = Path.GetFullPath(db), ["damping"] = damping };
            cont["aero"] = aero;
        }
        if (o.Damping is { } dmp) aero["damping"] = A(dmp);
        return d;
    }

    public static Baseline ReadBaseline(ScenarioDoc doc)
    {
        var r = doc.Root;
        string exposed = r["events"]!.AsArray().OfType<JsonObject>().Any(e => J.StrOpt(e, "name") == "container_exposed") ? "container_exposed"
            : throw new InvalidDataException("container_exposed 이벤트가 없습니다 — 3분할 시나리오(09)만 지원합니다.");
        var pv = new Vec3[3]; var pw = new Vec3[3]; var pt = new double[3];
        for (int i = 0; i < 3; i++)
        {
            var ev = Ev(r, $"petal{i + 1}_cut");
            var act = SeparateAction(ev, $"petal{i + 1}");
            pv[i] = J.V3(act, "relVel", Vec3.Zero); pw[i] = J.V3(act, "relOmega", Vec3.Zero);
            pt[i] = J.NumReq((JsonObject)ev["when"]!, "t", "petal_cut.when");
        }
        var cont = BodyDef(r, "container");
        var aero = cont["aero"] as JsonObject ?? new JsonObject();
        var w = doc.Build();
        var b = w.GetBody("container");
        double len = b.Aero?.Shape is { Kind: Geometry.ShapeKind.Cylinder } cyl ? cyl.Length : b.Shape?.CharacteristicLength ?? 1.5;
        var shock = cont["devices"]?.AsArray().OfType<JsonObject>().FirstOrDefault(x => J.StrOpt(x, "name") == "shock");
        return new Baseline
        {
            Exposed = exposed, PetalVel = pv, PetalOmega = pw, PetalT = pt,
            ShockG = shock is null ? 0 : J.Num(shock, "peakG", 15),
            Damping = J.V3(aero, "damping", Vec3.Zero), Cp = b.Aero?.Cp, Length = len,
            Diameter = b.Aero?.Shape is { Kind: Geometry.ShapeKind.Cylinder } c2 ? 2 * c2.Radius : 0.30,
            TExposedNominal = pt[0] + 0.002, HasDb = aero["db"] is not null, Mass = b.OwnMass + b.SubtreeStowed().Sum(x => x.OwnMass),
        };
    }

    /// <summary>인자 값(없으면 공칭)을 문서에 적용.</summary>
    public static ScenarioDoc Apply(ScenarioDoc nominal, Baseline bl, IReadOnlyDictionary<string, double> v)
    {
        double G(string k) => v.TryGetValue(k, out var x) ? x : F(k).Nominal;
        var d = nominal.Clone();
        var r = d.Root;
        double dv = G("dvScale"), p1 = G("petal1Factor");
        for (int i = 0; i < 3; i++)
        {
            var ev = Ev(r, $"petal{i + 1}_cut");
            var act = SeparateAction(ev, $"petal{i + 1}");
            double k = dv * (i == 0 ? p1 : 1);
            act["relVel"] = A(bl.PetalVel[i] * k);
            act["relOmega"] = A(bl.PetalOmega[i] * k);
        }
        // 조각3 기폭 지연(공칭 = 원래 간격 그대로)
        ((JsonObject)Ev(r, "petal3_cut")["when"]!)["t"] = bl.PetalT[0] + G("petal3DelayMs") / 1000.0;
        var cont = BodyDef(r, "container");
        if (cont["devices"]?.AsArray().OfType<JsonObject>().FirstOrDefault(x => J.StrOpt(x, "name") == "shock") is { } shock)
        {
            shock["peakG"] = bl.ShockG * G("shockScale");
            double ph = G("shockSideDeg") * Math.PI / 180;
            shock["direction"] = new JsonArray(0, Math.Sin(ph), Math.Cos(ph));
        }
        if (r["carrier"] is JsonObject car) car["pitch"] = G("aoaDeg");
        double rate = G("tipRateDps") * Math.PI / 180, psi = G("tipDirDeg") * Math.PI / 180;
        SeparateAction(Ev(r, bl.Exposed), "container")["relOmega"] = new JsonArray(0, rate * Math.Cos(psi), rate * Math.Sin(psi));
        var aero = cont["aero"] as JsonObject ?? new JsonObject();
        aero["damping"] = A(bl.Damping * G("dampScale"));
        if (!bl.HasDb && bl.Cp is { } cp && Math.Abs(G("cpShiftPct")) > 0)
            aero["cp"] = A(cp + new Vec3(G("cpShiftPct") / 100 * bl.Length, 0, 0));
        cont["aero"] = aero;
        double delay = G("drogueDelay");
        ((JsonObject)Ev(r, "lid_drogue")["when"]!)["delay"] = delay;
        var sim = r["sim"] as JsonObject ?? new JsonObject();
        sim["tEnd"] = bl.TExposedNominal + delay + 2.5;
        r["sim"] = sim;
        return d;
    }

    // ---------------- 한 실행 ----------------

    private static double Deg(double rad) => rad * 180 / Math.PI;

    public static Run Execute(ScenarioDoc nominal, Baseline bl, string tag, Dictionary<string, double> values)
    {
        var run = new Run { Tag = tag, Values = values };
        try
        {
            var doc = Apply(nominal, bl, values);
            var w = doc.Build();
            w.Run();
            Measure(w, run);
        }
        catch (Exception ex) { run.Error = ex.Message; }
        return run;
    }

    private static readonly string[] DebrisPrefixes = ["petal", "nose", "tail", "wingkit"];

    public static void Measure(World w, Run run)
    {
        var b = w.GetBody("container");
        run.TExposed = w.FiredEvents.TryGetValue("container_exposed", out var te) ? te : double.NaN;
        run.TDrogue = w.FiredEvents.TryGetValue("lid_drogue", out var td) ? td : double.NaN;
        run.TEnd = w.T;
        if (!double.IsFinite(run.TExposed)) throw new InvalidOperationException("컨테이너가 노출되지 않았습니다.");
        double t1 = double.IsFinite(run.TDrogue) ? run.TDrogue : w.T;
        double prevT = double.NaN; Vec3 prevW = Vec3.Zero;
        double cumF = 0, cumD = 0;
        foreach (var f in w.Recorder.Frames)
        {
            if (f.T < run.TExposed - 1e-9) continue;
            var s = f.Bodies[b.Index];
            if (s.Mode != BodyMode.Free) { prevT = double.NaN; continue; }
            double trans = Math.Sqrt(s.Omega.Y * s.Omega.Y + s.Omega.Z * s.Omega.Z);
            double aoa = OperationalMetrics.TotalAoaDeg(s);
            bool free = f.T <= t1 + 1e-9;
            if (double.IsFinite(prevT))
            {
                double dt = f.T - prevT;
                if (free) cumF += trans * dt; else cumD += trans * dt;
                if (dt > 0) run.MaxAlphaDot = Math.Max(run.MaxAlphaDot, (s.Omega - prevW).Norm / dt);
            }
            prevT = f.T; prevW = s.Omega;
            if (free)
            {
                run.MaxRateFree = Math.Max(run.MaxRateFree, Deg(s.Omega.Norm));
                if (s.Airspeed > 0 && !(run.MaxSpeedFree >= s.Airspeed)) run.MaxSpeedFree = s.Airspeed;
                if (s.Airspeed > 0 && !(run.MinSpeedFree <= s.Airspeed)) run.MinSpeedFree = s.Airspeed;
                if (double.IsFinite(aoa)) run.MaxAoaFree = Math.Max(run.MaxAoaFree, aoa);
            }
            else if (double.IsFinite(aoa)) run.MaxAoaDrogue = Math.Max(run.MaxAoaDrogue, aoa);
            double rho = w.Env.Atmosphere.At(-s.Cg.Z).Density;
            double q = 0.5 * rho * s.AirVelocity.NormSq;
            double sa = double.IsFinite(aoa) ? Math.Sin(aoa * Math.PI / 180) : 0;
            run.MaxQ = Math.Max(run.MaxQ, q);
            run.MaxCrossQ = Math.Max(run.MaxCrossQ, q * sa * sa);
            run.MaxOmega = Math.Max(run.MaxOmega, s.Omega.Norm);
        }
        run.CumFree = Deg(cumF); run.CumDrogue = Deg(cumD);
        run.MaxG = b.MaxLoadG;
        if (double.IsFinite(run.TDrogue))
        {
            var s = w.Recorder.Frames[w.Recorder.IndexAt(run.TDrogue)].Bodies[b.Index];
            run.AoaAtDrogue = OperationalMetrics.TotalAoaDeg(s);
            run.RateAtDrogue = Deg(s.Omega.Norm);
            run.SpeedAtDrogue = s.Airspeed;
        }
        else { run.AoaAtDrogue = double.NaN; run.RateAtDrogue = double.NaN; run.SpeedAtDrogue = double.NaN; }
        run.Class = Classify(run.MaxAoaFree, run.CumFree);
        run.DrogueCls = DrogueClass(run.AoaAtDrogue);
        if (w.Collisions is { } col)
        {
            foreach (var rec in col.Records)
            {
                if (rec.Kind == "contact")
                {
                    string? other = rec.A == "container" ? rec.B : rec.B == "container" ? rec.A : null;
                    if (other is null || !DebrisPrefixes.Any(p => other.StartsWith(p, StringComparison.Ordinal))) continue;
                    run.Contacts++;
                    run.MaxContactSpeed = Math.Max(run.MaxContactSpeed, rec.RelSpeed);
                    run.MaxContactImpulse = Math.Max(run.MaxContactImpulse, rec.Impulse);
                    if (rec.Time > run.TExposed + RecontactAfter)
                    {
                        run.Recontacts++;
                        run.LastContactTime = double.IsFinite(run.LastContactTime) ? Math.Max(run.LastContactTime, rec.Time) : rec.Time;
                        if (!run.RecontactWith.Split(',').Contains(other)) run.RecontactWith = run.RecontactWith.Length == 0 ? other : run.RecontactWith + "," + other;
                    }
                }
                else if (rec.B == "container.drogue" || rec.A == "container.drogue") run.DrogueInterferences++;
            }
        }
        if (b.Devices.OfType<Parachute>().FirstOrDefault(p => p.Name == "drogue") is { } dr)
        {
            run.DrogueAnomalies = dr.Anomalies.Count;
            run.DrogueG = dr.MaxG;
        }
    }

    // ---------------- 연구 전체 ----------------

    public static Result Generate(ScenarioDoc doc, TumbleStudyOptions o)
    {
        Directory.CreateDirectory(o.OutDir);
        void L(string s) => o.Log?.Invoke(s);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var nominal = Prepare(doc, o);
        var bl = ReadBaseline(nominal);
        var res = new Result
        {
            Nominal = [Execute(nominal, bl, "공칭", new Dictionary<string, double>())],
            AeroLabel = bl.HasDb ? $"CFD 공력 DB ({Path.GetFileName(J.Str((JsonObject)BodyDef(nominal.Root, "container")["aero"]!, "db", ""))})"
                : "L1 — TR R-300 원통 횡류(Re·M) + 압력중심 'auto'(측면적 중심) + 감쇠 미계수",
        };
        L($"공칭: {ClassNames[res.Nominal[0].Class]}, 최대 α_t {res.Nominal[0].MaxAoaFree:F1}°");
        var jobs = new List<(string Group, string Tag, Dictionary<string, double> V)>();
        if (o.Sweeps)
            foreach (var f in Factors.Where(f => f.Sweep.Length > 0 && !(bl.HasDb && f.Key == "cpShiftPct")))
                foreach (var x in f.Sweep) jobs.Add(("S:" + f.Key, $"{f.Key}={x.ToString("G", Inv)}", new() { [f.Key] = x }));
        if (o.Maps)
            foreach (var m in Maps.Where(m => !(bl.HasDb && (m.XKey == "cpShiftPct" || m.YKey == "cpShiftPct"))))
                foreach (var y in m.Ys)
                    foreach (var x in m.Xs) jobs.Add(("M:" + m.Id, $"{m.XKey}={x.ToString("G", Inv)},{m.YKey}={y.ToString("G", Inv)}", new() { [m.XKey] = x, [m.YKey] = y }));
        var mcFactors = Factors.Where(f => !(bl.HasDb && f.Key == "cpShiftPct")).ToArray();
        res.McKeys = mcFactors.Select(f => f.Key).ToArray();
        if (o.McRuns > 0)
        {
            var u = Sampler.Uniforms(o.McRuns, mcFactors.Length, "lhs", o.Seed);
            for (int i = 0; i < o.McRuns; i++)
            {
                var v = new Dictionary<string, double>();
                for (int k = 0; k < mcFactors.Length; k++) v[mcFactors[k].Key] = mcFactors[k].Min + (mcFactors[k].Max - mcFactors[k].Min) * u[i][k];
                jobs.Add(("MC", $"mc{i}", v));
            }
        }
        L($"실행 {jobs.Count}회(스윕·지도·MC {o.McRuns})");
        var runs = new Run[jobs.Count];
        int done = 0;
        Parallel.For(0, jobs.Count, i =>
        {
            runs[i] = Execute(nominal, bl, jobs[i].Tag, jobs[i].V);
            int n = Interlocked.Increment(ref done);
            if (n % 50 == 0) L($"  {n}/{jobs.Count}");
        });
        for (int i = 0; i < jobs.Count; i++)
        {
            var g = jobs[i].Group;
            if (g == "MC") res.Mc.Add(runs[i]);
            else if (g.StartsWith("S:", StringComparison.Ordinal)) { if (!res.Sweeps.TryGetValue(g[2..], out var l)) res.Sweeps[g[2..]] = l = new(); l.Add(runs[i]); }
            else { if (!res.MapRuns.TryGetValue(g[2..], out var l)) res.MapRuns[g[2..]] = l = new(); l.Add(runs[i]); }
        }
        var ok = res.Mc.Where(r => r.Error is null).ToList();
        if (ok.Count > mcFactors.Length + 3)
        {
            var x = ok.Select(r => res.McKeys.Select(k => r.Values[k]).ToArray()).ToList();
            res.Prcc = Prcc.Compute(x, ok.Select(r => r.MaxAoaFree).ToList());
            res.PrccDrogue = Prcc.Compute(x, ok.Select(r => double.IsFinite(r.AoaAtDrogue) ? r.AoaAtDrogue : 180).ToList());
        }
        res.Envelope = Envelope(res);
        sw.Stop();
        res.ElapsedSeconds = sw.Elapsed.TotalSeconds;
        WriteData(res, o.OutDir);
        WriteFigures(res, o.OutDir);
        res.Markdown = Markdown(doc, res, o, bl);
        File.WriteAllText(Path.Combine(o.OutDir, "README.md"), res.Markdown, new UTF8Encoding(false));
        return res;
    }

    private static IEnumerable<Run> All(Result r) => r.Nominal.Concat(r.Sweeps.Values.SelectMany(x => x)).Concat(r.MapRuns.Values.SelectMany(x => x)).Concat(r.Mc);

    /// <summary>
    /// 하중 포락선(container-material 입력): MC(설계 불확도 범위) 의 최댓값·99 백분위와 전 실행(스윕·지도 극단 포함) 최댓값.
    /// </summary>
    public static JsonObject Envelope(Result r)
    {
        var mc = r.Mc.Where(x => x.Error is null).ToList();
        var all = All(r).Where(x => x.Error is null).ToList();
        static double P(IEnumerable<double> v, double p)
        {
            var s = v.Where(double.IsFinite).OrderBy(x => x).ToList();
            if (s.Count == 0) return double.NaN;
            double idx = p * (s.Count - 1); int lo = (int)Math.Floor(idx); int hi = Math.Min(lo + 1, s.Count - 1);
            return s[lo] + (s[hi] - s[lo]) * (idx - lo);
        }
        JsonObject Stat(Func<Run, double> f) => new()
        {
            ["nominal"] = Parachute.Num(r.Nominal.Count > 0 && r.Nominal[0].Error is null ? f(r.Nominal[0]) : double.NaN),
            ["mcP99"] = Parachute.Num(P(mc.Select(f), 0.99)), ["mcMax"] = Parachute.Num(mc.Count > 0 ? mc.Max(f) : double.NaN),
            ["allMax"] = Parachute.Num(all.Count > 0 ? all.Max(f) : double.NaN),
        };
        return new JsonObject
        {
            ["schema"] = "mns.tumble-envelope/1",
            ["aero"] = r.AeroLabel,
            ["maxQ_Pa"] = Stat(x => x.MaxQ),
            ["maxCrossflowQ_Pa"] = Stat(x => x.MaxCrossQ),
            ["maxOmega_radps"] = Stat(x => x.MaxOmega),
            ["maxAngularAccel_radps2"] = Stat(x => x.MaxAlphaDot),
            ["maxLoadG"] = Stat(x => x.MaxG),
            ["drogueOpeningG"] = Stat(x => x.DrogueG),
            ["maxAoaFree_deg"] = Stat(x => x.MaxAoaFree),
            ["note"] = "q·sin²α_t = 원통 횡류 동압(옆으로 선 자세의 횡류 하중 척도). 각가속도는 50 Hz 기록 프레임 차분(충격 펄스는 평균화됨).",
        };
    }

    // ---------------- 출력 ----------------

    private static string Fx(double v, string fmt = "F1") => double.IsFinite(v) ? v.ToString(fmt, Inv) : "—";

    private static void WriteData(Result r, string dir)
    {
        File.WriteAllText(Path.Combine(dir, "envelope.json"), r.Envelope.ToJsonString(J.Pretty), new UTF8Encoding(false));
        var sb = new StringBuilder();
        sb.AppendLine("group,tag," + string.Join(",", Factors.Select(f => f.Key)) +
                      ",class,maxAoaFree_deg,cumFree_deg,maxRateFree_dps,aoaAtDrogue_deg,rateAtDrogue_dps,speedAtDrogue_mps,drogueClass,maxAoaDrogue_deg,contacts,recontacts,maxContactSpeed_mps,maxContactImpulse_Ns,drogueInterferences,drogueAnomalies,drogueG,maxQ_Pa,maxCrossQ_Pa,maxOmega_radps,maxAlphaDot_radps2,maxG,error");
        void Row(string g, Run x)
        {
            double V(string k) => x.Values.TryGetValue(k, out var v) ? v : F(k).Nominal;
            sb.AppendLine(string.Join(",", new[] { g, x.Tag.Replace(',', ';') }.Concat(Factors.Select(f => V(f.Key).ToString("G6", Inv)))
                .Concat(new[] { x.Class.ToString(Inv), Fx(x.MaxAoaFree, "F2"), Fx(x.CumFree, "F2"), Fx(x.MaxRateFree, "F2"), Fx(x.AoaAtDrogue, "F2"), Fx(x.RateAtDrogue, "F2"),
                    Fx(x.SpeedAtDrogue, "F2"), x.DrogueCls.ToString(Inv), Fx(x.MaxAoaDrogue, "F2"), x.Contacts.ToString(Inv), x.Recontacts.ToString(Inv), Fx(x.MaxContactSpeed, "F3"),
                    Fx(x.MaxContactImpulse, "F3"), x.DrogueInterferences.ToString(Inv), x.DrogueAnomalies.ToString(Inv), Fx(x.DrogueG, "F2"), Fx(x.MaxQ, "F0"), Fx(x.MaxCrossQ, "F0"),
                    Fx(x.MaxOmega, "F3"), Fx(x.MaxAlphaDot, "F2"), Fx(x.MaxG, "F2"), (x.Error ?? "").Replace(',', ';') })));
        }
        foreach (var x in r.Nominal) Row("nominal", x);
        foreach (var (k, l) in r.Sweeps) foreach (var x in l) Row("sweep:" + k, x);
        foreach (var (k, l) in r.MapRuns) foreach (var x in l) Row("map:" + k, x);
        foreach (var x in r.Mc) Row("mc", x);
        File.WriteAllText(Path.Combine(dir, "runs.csv"), sb.ToString(), new UTF8Encoding(false));
    }

    private static readonly string[] ClassColor = ["#1baf7a", "#eda100", "#eb6834", "#c0282a"];

    /// <summary>2-D 지도 SVG: 칸 색 = 텀블링 등급, 칸 글자 = 자유비행 최대 α_t[°] / 드로그 순간 α_t[°], 테두리 굵게 = 재접촉.</summary>
    public static string MapSvg(MapDef m, IReadOnlyList<Run> runs)
    {
        var fx = F(m.XKey); var fy = F(m.YKey);
        int nx = m.Xs.Length, ny = m.Ys.Length;
        double cw = 86, ch = 40, ml = 150, mt = 64, mb = 86;
        double width = ml + nx * cw + 24, height = mt + ny * ch + mb;
        var sb = new StringBuilder();
        sb.Append(Inv, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width:F0}\" height=\"{height:F0}\" viewBox=\"0 0 {width:F0} {height:F0}\" font-family=\"Malgun Gothic, Segoe UI, sans-serif\" font-size=\"12\">");
        sb.Append(Inv, $"<rect width=\"{width:F0}\" height=\"{height:F0}\" fill=\"#fcfcfb\"/>");
        sb.Append(Inv, $"<text x=\"16\" y=\"24\" font-size=\"15\" font-weight=\"600\" fill=\"#0b0b0b\">지도 {m.Id}: {Esc(m.Title)}</text>");
        sb.Append(Inv, $"<text x=\"16\" y=\"44\" fill=\"#52514e\">칸 = 자유비행 최대 α_t / 드로그 순간 α_t [°] · 색 = 텀블링 등급 · 굵은 테두리 = 조각 재접촉</text>");
        Run? Find(double x, double y) => runs.FirstOrDefault(r => r.Values.TryGetValue(m.XKey, out var a) && Math.Abs(a - x) < 1e-9 && r.Values.TryGetValue(m.YKey, out var b) && Math.Abs(b - y) < 1e-9);
        for (int j = 0; j < ny; j++)
        {
            double y0 = mt + (ny - 1 - j) * ch;
            sb.Append(Inv, $"<text x=\"{ml - 8}\" y=\"{y0 + ch / 2 + 4:F1}\" text-anchor=\"end\" fill=\"#52514e\">{m.Ys[j].ToString("G", Inv)}</text>");
            for (int i = 0; i < nx; i++)
            {
                double x0 = ml + i * cw;
                var r = Find(m.Xs[i], m.Ys[j]);
                string fill = r is null || r.Error is not null ? "#c3c2b7" : ClassColor[r.Class];
                bool re = r is not null && r.Recontacts > 0;
                sb.Append(Inv, $"<rect x=\"{x0 + 1:F1}\" y=\"{y0 + 1:F1}\" width=\"{cw - 2:F1}\" height=\"{ch - 2:F1}\" fill=\"{fill}\" stroke=\"{(re ? "#0b0b0b" : "#fcfcfb")}\" stroke-width=\"{(re ? 2.5 : 1)}\">");
                if (r is not null)
                    sb.Append(Inv, $"<title>{Esc(m.XKey)}={m.Xs[i].ToString("G", Inv)}, {Esc(m.YKey)}={m.Ys[j].ToString("G", Inv)}: {(r.Error is null ? $"{ClassNames[r.Class]}, 누적 횡회전 {r.CumFree:F0}°, 드로그 {DrogueClassNames[r.DrogueCls]}, 재접촉 {r.Recontacts}" : Esc(r.Error))}</title>");
                sb.Append("</rect>");
                string txt = r is null ? "" : r.Error is not null ? "오류" : $"{r.MaxAoaFree:F0} / {Fx(r.AoaAtDrogue, "F0")}";
                sb.Append(Inv, $"<text x=\"{x0 + cw / 2:F1}\" y=\"{y0 + ch / 2 + 4:F1}\" text-anchor=\"middle\" fill=\"#0b0b0b\">{txt}</text>");
            }
        }
        for (int i = 0; i < nx; i++)
            sb.Append(Inv, $"<text x=\"{ml + i * cw + cw / 2:F1}\" y=\"{mt + ny * ch + 16:F1}\" text-anchor=\"middle\" fill=\"#52514e\">{m.Xs[i].ToString("G", Inv)}</text>");
        sb.Append(Inv, $"<text x=\"{ml + nx * cw / 2:F1}\" y=\"{mt + ny * ch + 36:F1}\" text-anchor=\"middle\" fill=\"#0b0b0b\">{Esc(fx.Label)} [{Esc(fx.Unit)}]</text>");
        sb.Append(Inv, $"<text x=\"16\" y=\"{mt + ny * ch / 2:F1}\" fill=\"#0b0b0b\">{Esc(fy.Label)}</text>");
        sb.Append(Inv, $"<text x=\"16\" y=\"{mt + ny * ch / 2 + 16:F1}\" fill=\"#52514e\">[{Esc(fy.Unit)}]</text>");
        double lx = ml, ly = mt + ny * ch + 56;
        for (int c = 0; c < 4; c++)
        {
            sb.Append(Inv, $"<rect x=\"{lx:F1}\" y=\"{ly:F1}\" width=\"14\" height=\"14\" fill=\"{ClassColor[c]}\"/>");
            sb.Append(Inv, $"<text x=\"{lx + 20:F1}\" y=\"{ly + 11:F1}\" fill=\"#0b0b0b\">{ClassNames[c]}</text>");
            lx += 110;
        }
        sb.Append("</svg>");
        return sb.ToString();
    }

    private static string Esc(string s) => System.Security.SecurityElement.Escape(s) ?? "";

    private static void WriteFigures(Result r, string dir)
    {
        foreach (var m in Maps)
            if (r.MapRuns.TryGetValue(m.Id, out var runs))
                File.WriteAllText(Path.Combine(dir, $"fig_map_{m.Id}.svg"), MapSvg(m, runs), new UTF8Encoding(false));
        // 1-D 스윕: 인자별 자유비행 최대 α_t (정규화 x = 스윕 순번)
        foreach (var (k, runs) in r.Sweeps)
        {
            var f = F(k);
            var s1 = new SvgPlot.Series("자유비행 최대 α_t", runs.Where(x => x.Error is null).Select(x => (x.Values[k], x.MaxAoaFree)).ToList(), true);
            var s2 = new SvgPlot.Series("드로그 순간 α_t", runs.Where(x => x.Error is null && double.IsFinite(x.AoaAtDrogue)).Select(x => (x.Values[k], x.AoaAtDrogue)).ToList(), true);
            var s3 = new SvgPlot.Series("누적 횡회전/4", runs.Where(x => x.Error is null).Select(x => (x.Values[k], x.CumFree / 4)).ToList(), true);
            File.WriteAllText(Path.Combine(dir, $"fig_sweep_{k}.svg"),
                SvgPlot.Line($"1-D 스윕: {f.Label}", $"{f.Label} [{f.Unit}]", "각 [°]", [s1, s2, s3], yMin: 0,
                    note: $"기준선: 15°(안정) · 90°(뒤집힘) · 누적 횡회전 360° = 그래프 90 (텀블링)", height: 300), new UTF8Encoding(false));
        }
    }

    /// <summary>보고서 대상 설명: 실모델 폴더 이름의 설계 태그(fusion_live_&lt;태그&gt;_groups)와 컨테이너 구조 재질·두께(시나리오에서).</summary>
    private static (string Rev, string Label) DesignOf(ScenarioDoc doc)
    {
        var dir = doc.Root["realModels"]?["dir"]?.GetValue<string>() ?? "";
        string rev = System.Text.RegularExpressions.Regex.Match(dir, @"fusion_live_([A-Za-z0-9]+)_groups") is { Success: true } m ? m.Groups[1].Value : "기준 설계";
        var c = BodyDef(doc.Root, "container");
        var st = c["structure"] as JsonObject;
        string mat = st is not null && J.StrOpt(st, "material") is { } key ? (Structures.MaterialLibrary.Find(key)?.Name ?? key) : "CFRP";
        double t = st is null ? 0.003 : J.Num(st, "thickness", 0.003);
        return (rev, $"{rev} 컨테이너({mat} 튜브 t {t * 1e3:0.#} mm, 드론 4대 수납 — 실모델 또는 같은 값의 합성 대체 질량특성)");
    }

    private static string Markdown(ScenarioDoc doc, Result r, TumbleStudyOptions o, Baseline bl)
    {
        var sb = new StringBuilder();
        void P(string s = "") => sb.AppendLine(s);
        var nom = r.Nominal[0];
        var mc = r.Mc.Where(x => x.Error is null).ToList();
        int n = mc.Count;
        var design = DesignOf(doc);
        P($"# 시나리오 {doc.Name.Split('_')[0]} — 3분할 분리 충격 변화에 따른 컨테이너 텀블링 지도");
        P();
        P($"생성: `{o.Command}` · {DateTime.Now:yyyy-MM-dd HH:mm} · 엔진 `Sim.Core {typeof(TumbleStudy).Assembly.GetName().Version}` · 시나리오 `{doc.Name}` 해시 `{doc.Hash()}` · MC 시드 {o.Seed} · 계산 {r.ElapsedSeconds:F0} s");
        P();
        P($"- 대상: {design.Label}가 운반 탄체 120° 3분할(t = 4.0 s, 고도 1,000 m, M 0.5)로 노출된 뒤 드로그 전개까지의 **자유비행**.");
        P($"- 컨테이너 공력: **{r.AeroLabel}**. 감쇠 미계수(Clp, Cmq, Cnr) = ({bl.Damping.X:G3}, {bl.Damping.Y:G3}, {bl.Damping.Z:G3}). 압력중심 {(bl.Cp is { } cp ? $"x = {cp.X:F3} m (동체, 기준 길이 {bl.Length:F3} m)" : "—")}.");
        P("- 입력 교체 지점: CFD 공력 DB 가 나오면 `--aero-db config/aero_db/<설계>.json`(mns.aero-db/1 — 축력·법선력·피칭 모멘트 표), 동적 미계수가 나오면 `--damping Clp,Cmq,Cnr` — 같은 명령으로 지도·MC·포락선이 다시 나온다.");
        P("- 원자료: [runs.csv](runs.csv)(모든 실행 · 인자 · 지표), [envelope.json](envelope.json)(하중 포락선 → `mns container-material --envelope`).");
        P();
        P("## 0. 핵심 결과");
        P();
        P("| 항목 | 값 | 비고 |");
        P("|---|---|---|");
        P($"| 공칭(분리 입력 가정값) 텀블링 등급 | **{ClassNames[nom.Class]}** | 자유비행 최대 α_t {Fx(nom.MaxAoaFree)}° · 누적 횡회전 {Fx(nom.CumFree, "F0")}° · 최대 각속도 {Fx(nom.MaxRateFree, "F0")}°/s |");
        P($"| 공칭 드로그 전개 순간 자세 | α_t {Fx(nom.AoaAtDrogue)}° ({DrogueClassNames[nom.DrogueCls]}) · {Fx(nom.RateAtDrogue, "F0")}°/s · {Fx(nom.SpeedAtDrogue, "F0")} m/s | 드로그 개방 {Fx(nom.DrogueG)} G |");
        P($"| 공칭 조각–컨테이너 접촉 / 재접촉 | {nom.Contacts} / {nom.Recontacts} | 최대 상대속도 {Fx(nom.MaxContactSpeed, "F2")} m/s · 충격량 {Fx(nom.MaxContactImpulse, "F1")} N·s |");
        if (n > 0)
        {
            for (int c = 0; c < 4; c++)
            {
                int k = mc.Count(x => x.Class == c); var (lo, hi) = OperationalReport.Wilson(k, n);
                P($"| MC {n}회: {ClassNames[c]} | {k}/{n} = {(double)k / n:P1} | Wilson 95% {lo:P1}–{hi:P1} |");
            }
            int kd = mc.Count(x => x.DrogueCls == 2), kc = mc.Count(x => x.DrogueCls >= 1);
            var (dlo, dhi) = OperationalReport.Wilson(kd, n); var (clo, chi) = OperationalReport.Wilson(kc, n);
            P($"| MC: 드로그 전개 자세 위험(α_t ≥ 90°) | {kd}/{n} = {(double)kd / n:P1} | 95% {dlo:P1}–{dhi:P1} · 주의 이상(≥ 30°) {(double)kc / n:P1} ({clo:P1}–{chi:P1}) |");
            int kr = mc.Count(x => x.Recontacts > 0); var (rlo, rhi) = OperationalReport.Wilson(kr, n);
            P($"| MC: 조각·잔해–컨테이너 재접촉(노출 50 ms 이후) | {kr}/{n} = {(double)kr / n:P1} | 95% {rlo:P1}–{rhi:P1} |");
            int ka = mc.Count(x => x.DrogueAnomalies > 0); var (alo, ahi) = OperationalReport.Wilson(ka, n);
            P($"| MC: 잔해 간섭 드로그 전개 이상 | {ka}/{n} = {(double)ka / n:P1} | 95% {alo:P1}–{ahi:P1} |");
            P($"| MC 자유비행 최대 α_t 중앙값 / 95 백분위 | {Pct(mc.Select(x => x.MaxAoaFree), 0.5):F1}° / {Pct(mc.Select(x => x.MaxAoaFree), 0.95):F1}° | 최댓값 {mc.Max(x => x.MaxAoaFree):F1}° |");
        }
        if (r.Sweeps.TryGetValue("dampScale", out var ds))
            foreach (var x in ds.Where(x => x.Error is null && x.Values["dampScale"] < 0.3))
                P($"| 감쇠 배율 {x.Values["dampScale"]:G} (횡류 이론 ≈ 0.1, 7절) 나머지 공칭 | **{ClassNames[x.Class]}** | 최대 α_t {x.MaxAoaFree:F1}° · 누적 {x.CumFree:F0}° · 드로그 순간 α_t {Fx(x.AoaAtDrogue)}° ({DrogueClassNames[x.DrogueCls]}) |");
        if (r.MapRuns.TryGetValue("D", out var md))
        {
            var t3 = md.Where(x => x.Error is null && x.Class == 3).OrderBy(x => x.Values["drogueDelay"]).ThenByDescending(x => x.Values["dampScale"]).FirstOrDefault();
            P($"| 지도 D 텀블링(T3) 최단 자유비행 | {(t3 is null ? "없음" : $"드로그 지연 {t3.Values["drogueDelay"]:G} s · 감쇠 배율 {t3.Values["dampScale"]:G}")} | 감쇠가 작을수록·자유비행이 길수록 T3 |");
        }
        int err = All(r).Count(x => x.Error is not null);
        P($"| 실행 오류 | {err} | 전체 {All(r).Count()}회 |");
        P();
        P("## 1. 텀블링 판정 기준(이 연구의 정의)");
        P();
        P("창 = **노출(container_exposed) → 뚜껑·드로그 전개(lid_drogue)** 자유비행. 드로그 전개 뒤의 수평→수직 회전은 의도된 거동이라 텀블링 판정에서 뺀다(참고 지표로만 기록).");
        P();
        P("| 등급 | 조건 | 의미 |");
        P("|---|---|---|");
        P($"| T0 안정 | 최대 총받음각 α_t < {StableAoaDeg:F0}° | 노출 자세 유지(작은 흔들림) |");
        P($"| T1 진동 | {StableAoaDeg:F0}° ≤ α_t < {FlipAoaDeg:F0}°, 누적 횡회전 < {TumbleRotationDeg:F0}° | 큰 흔들림 — 공력 복원·감쇠 범위 |");
        P($"| T2 뒤집힘 | α_t ≥ {FlipAoaDeg:F0}° (누적 < {TumbleRotationDeg:F0}°) | 옆으로 서거나 뒤끝(드로그 쪽)이 상류 — 끝-끝 회전 직전 |");
        P($"| T3 텀블링 | 횡축(q, r) 각속도 적분 누적 회전 ≥ {TumbleRotationDeg:F0}° | 끝-끝 완전 회전(기존 OperationalMetrics 기준과 같음) |");
        P();
        P($"드로그 전개 순간 자세: α_t < {DrogueCautionDeg:F0}° 양호 · {DrogueCautionDeg:F0}–{DrogueDangerDeg:F0}° 주의(라이저가 동체를 비스듬히 당김 — 수직 전환 시 큰 각속도) · ≥ {DrogueDangerDeg:F0}° 위험(드로그·뚜껑 쪽이 상류 — 드로그가 동체를 넘어 펴지며 라인 감김 위험).");
        P($"재접촉: 노출 {RecontactAfter * 1000:F0} ms 이후 조각(petal1–3)·노즈·꼬리·날개 파트와 컨테이너의 접촉 기록(분리 순간 밀어내기 접촉 제외).");
        P();
        P("## 2. 분리 입력 인자와 근거");
        P();
        P("| 인자 | 공칭 | 1-D 스윕 | MC 분포(균등) | 근거·등급 |");
        P("|---|---|---|---|---|");
        foreach (var f in Factors)
            P($"| {f.Label} [{f.Unit}] | {f.Nominal.ToString("G", Inv)} | {(f.Sweep.Length > 0 ? string.Join(", ", f.Sweep.Select(x => x.ToString("G", Inv))) : "—")} | {f.Min.ToString("G", Inv)} – {f.Max.ToString("G", Inv)} | {f.Basis} |");
        P();
        P("적용 방식: 조각 Δv 는 각 `petalN_cut` 분리 동작의 relVel·relOmega(반경 Δv + 힌지 회전)에 배율, 조각3 지연은 그 기폭 시각, 컨테이너 충격은 `shock` 펄스(작용점 CG — 모멘트 없음)의 크기·방향, 받음각은 운반체 pitch(규정 궤적 수평 비행 → 동체 기울기 = 받음각), tip-off 는 `container_exposed` 분리의 relOmega(피치·요 평면), 감쇠·압력중심은 컨테이너 `aero` 를 바꾼다. 시나리오 파일은 바꾸지 않는다(메모리 사본).");
        P();
        P("## 3. 1-D 스윕(한 인자씩, 나머지 공칭)");
        P();
        P("| 인자 | 값 | 등급 | 자유비행 최대 α_t [°] | 누적 횡회전 [°] | 최대 각속도 [°/s] | 드로그 순간 α_t [°] (판정) | 드로그 개방 [G] | 재접촉 | 최대 접촉 [m/s] |");
        P("|---|---|---|---|---|---|---|---|---|---|");
        foreach (var (k, runs) in r.Sweeps)
        {
            var f = F(k);
            foreach (var x in runs)
                P(x.Error is not null
                    ? $"| {f.Label} | {x.Values[k].ToString("G", Inv)} {f.Unit} | 오류 | {x.Error} | | | | | | |"
                    : $"| {f.Label} | {x.Values[k].ToString("G", Inv)} {f.Unit} | {ClassNames[x.Class]} | {x.MaxAoaFree:F1} | {x.CumFree:F0} | {x.MaxRateFree:F0} | {Fx(x.AoaAtDrogue)} ({DrogueClassNames[x.DrogueCls]}) | {Fx(x.DrogueG)} | {x.Recontacts}{(x.Recontacts > 0 ? $" ({x.RecontactWith})" : "")} | {Fx(x.MaxContactSpeed, "F2")} |");
        }
        P();
        foreach (var k in r.Sweeps.Keys) P($"![{k}](fig_sweep_{k}.svg)");
        P();
        P("## 4. 2-D 텀블링 지도");
        P();
        foreach (var m in Maps)
        {
            if (!r.MapRuns.TryGetValue(m.Id, out var runs)) { P($"지도 {m.Id}: CFD DB 사용 시 압력중심 인자는 해당 없음(DB 의 Cm 사용)."); P(); continue; }
            P($"### 지도 {m.Id} — {m.Title}");
            P();
            P($"![지도 {m.Id}](fig_map_{m.Id}.svg)");
            P();
            var fy = F(m.YKey); var fx = F(m.XKey);
            P($"| {fy.Label} [{fy.Unit}] \\ {fx.Label} [{fx.Unit}] | " + string.Join(" | ", m.Xs.Select(x => x.ToString("G", Inv))) + " |");
            P("|---|" + string.Concat(Enumerable.Repeat("---|", m.Xs.Length)));
            foreach (var y in Enumerable.Reverse(m.Ys))
            {
                var cells = m.Xs.Select(x =>
                {
                    var rr = runs.FirstOrDefault(q => Math.Abs(q.Values[m.XKey] - x) < 1e-9 && Math.Abs(q.Values[m.YKey] - y) < 1e-9);
                    if (rr is null) return "";
                    if (rr.Error is not null) return "오류";
                    return $"T{rr.Class} {rr.MaxAoaFree:F0}°/{Fx(rr.AoaAtDrogue, "F0")}°{(rr.Recontacts > 0 ? " ⚠" : "")}";
                });
                P($"| **{y.ToString("G", Inv)}** | " + string.Join(" | ", cells) + " |");
            }
            P();
            P(Boundary(m, runs));
            P();
        }
        P("칸 = `T등급 자유비행 최대 α_t / 드로그 순간 α_t`, ⚠ = 노출 50 ms 이후 조각·잔해 재접촉.");
        P();
        P($"## 5. Monte Carlo ({n}회, LHS, 시드 {o.Seed}) — 민감도");
        P();
        if (n > 0)
        {
            P("모든 인자를 2절 MC 범위에서 동시에 균등 표본(LHS). 출력 = 자유비행 최대 α_t, 드로그 순간 α_t. PRCC(부분 순위 상관, Marino 2008) — |PRCC| 가 클수록 그 인자가 순위로 출력을 좌우.");
            P();
            P("| 인자 | PRCC(최대 α_t) | p | PRCC(드로그 순간 α_t) | p |");
            P("|---|---|---|---|---|");
            var order = Enumerable.Range(0, r.McKeys.Length).OrderByDescending(i => r.Prcc.Length > i ? Math.Abs(r.Prcc[i]) : 0);
            foreach (int i in order)
            {
                double a = r.Prcc.Length > i ? r.Prcc[i] : double.NaN, d = r.PrccDrogue.Length > i ? r.PrccDrogue[i] : double.NaN;
                P($"| {F(r.McKeys[i]).Label} | {a:+0.00;-0.00} | {Prcc.PValue(a, n, r.McKeys.Length):0.000} | {d:+0.00;-0.00} | {Prcc.PValue(d, n, r.McKeys.Length):0.000} |");
            }
            P();
            P("| 자유비행 최대 α_t 구간 [°] | 실행 수 | 재접촉 | 드로그 위험 |");
            P("|---|---|---|---|");
            foreach (var (lo, hi) in new[] { (0.0, 15.0), (15.0, 30.0), (30.0, 60.0), (60.0, 90.0), (90.0, 1e9) })
            {
                var g = mc.Where(x => x.MaxAoaFree >= lo && x.MaxAoaFree < hi).ToList();
                P($"| {lo:F0}–{(hi > 1e8 ? "" : hi.ToString("F0", Inv))} | {g.Count} | {g.Count(x => x.Recontacts > 0)} | {g.Count(x => x.DrogueCls == 2)} |");
            }
            P();
            var who = mc.SelectMany(x => x.RecontactWith.Split(',', StringSplitOptions.RemoveEmptyEntries)).GroupBy(x => x).OrderByDescending(g => g.Count());
            P("재접촉 상대(MC 실행 수 — 한 실행에 여럿 가능): " + string.Join(" · ", who.Select(g => $"{g.Key} {g.Count()}")) +
              $". 재접촉 최대 상대속도 중앙값 {Pct(mc.Where(x => x.Recontacts > 0).Select(x => x.MaxContactSpeed), 0.5):F1} m/s · 최대 {mc.Select(x => x.MaxContactSpeed).DefaultIfEmpty(0).Max():F1} m/s, 최대 충격량 {mc.Select(x => x.MaxContactImpulse).DefaultIfEmpty(0).Max():F0} N·s.");
            P();
            var worst = mc.OrderByDescending(x => x.MaxAoaFree).Take(5).ToList();
            P("최악 5회(자유비행 최대 α_t 순):");
            P();
            P("| 실행 | 등급 | 최대 α_t [°] | 누적 [°] | 드로그 α_t [°] | 주요 인자 |");
            P("|---|---|---|---|---|---|");
            foreach (var x in worst)
                P($"| {x.Tag} | {ClassNames[x.Class]} | {x.MaxAoaFree:F1} | {x.CumFree:F0} | {Fx(x.AoaAtDrogue)} | Δv×{x.Values["dvScale"]:F2}, 조각1×{x.Values["petal1Factor"]:F2}, tip-off {x.Values["tipRateDps"]:F0}°/s, α {x.Values["aoaDeg"]:F1}°, 감쇠×{x.Values["dampScale"]:F2}, CP {x.Values.GetValueOrDefault("cpShiftPct"):+0.0;-0.0}%L, 드로그 {x.Values["drogueDelay"]:F2} s |");
        }
        P();
        P("## 6. 하중 포락선(재질 연구 입력)");
        P();
        P("| 양 | 공칭 | MC 99 백분위 | MC 최대 | 전 실행 최대(스윕·지도 극단 포함) |");
        P("|---|---|---|---|---|");
        foreach (var (key, label) in new[] { ("maxQ_Pa", "최대 동압 q [Pa]"), ("maxCrossflowQ_Pa", "최대 횡류 동압 q·sin²α_t [Pa]"), ("maxOmega_radps", "최대 각속도 [rad/s]"),
                     ("maxAngularAccel_radps2", "최대 각가속도 [rad/s²] (50 Hz 차분)"), ("maxLoadG", "최대 하중 [G]"), ("drogueOpeningG", "드로그 개방 [G]") })
        {
            var s = (JsonObject)r.Envelope[key]!;
            P($"| {label} | {N(s["nominal"])} | {N(s["mcP99"])} | {N(s["mcMax"])} | {N(s["allMax"])} |");
        }
        P();
        P("→ `mns container-material --envelope validation/reports/tumbling09/envelope.json` 가 횡류 동압·각속도·G 를 하중 케이스로 쓴다.");
        P();
        P("## 7. 공력 모델의 근거와 한계(큰 받음각·회전 중)");
        P();
        P("| 항목 | 현재 모델 | 근거(등급) | 한계·영향 |");
        P("|---|---|---|---|");
        P("| 정적 힘(전 받음각) | 원통 투영면적 × [축방향 Cd + 횡류 CD(Re, M)·η(L/D)] — 횡류 독립 원리 | NASA TR R-300 원통 횡류 표(A, 133행, Re 4.4×10⁶·M 0.5 CD ≈ 1.0–1.2), TN 2960 η(B) | 축방향–횡류 결합·끝단 효과·박리 비대칭(측력) 없음. 소받음각 양력(세장체 sin2α)·Munk 불안정 모멘트 없음 → 압력중심 이동 인자(±8 %L)로 대리 |");
        P("| 압력중심 | 'auto' = 측면 투영면적 중심(정적 여유 ≈ −5 %, 약한 불안정) | 형상 근사(D) | 실제 받음각별 Cp 이동·Cm(α) 는 CFD DB 필요(FR-REL-01) — 지도 C 가 이 불확도의 영향 |");
        P("| 마하 보정 | TR R-300 표가 M 0.5 까지 직접 포함(횡류 성분 마하) | A | 국부 초음속(임계 마하 ≈ 0.4 초과)·충격파 박리 진동 없음 |");
        P("| 동적 미계수 | Clp −2, Cmq −6, Cnr −6 (가정, 무차원화 l²) | D — 공개 자료·CFD 강제진동(FR-CFD-07) 없음 | 감쇠 배율 0–2 스윕·MC 로 영향 표시. 큰 받음각 회전 중 감쇠는 받음각 의존(대개 감소·부호 반전 가능) — 상수 모델 |");
        P("| 회전 중 항력 | 회전각별 투영 항력의 순간값(준정상) | — | 공개 검증 [tumbling_cylinder_britcher](../tumbling_cylinder_britcher.md)(L/D 1, 저속): 텀블링 평균 항력 **−31~−35 % 과소**(B 등급, 경고) — 회전 유발 항력·마그누스·자동회전 없음 |");
        P("| 압축성 비정상 | 없음(준정상) | — | 와류 방출(St ≈ 0.2, 원통 Ø300 @ 168 m/s → ≈ 110 Hz)·측력 진동은 자세에 평균화 — 진동 피로는 7b 진동 피로(근사) |");
        P("| 다물체 간섭 | 조각·잔해 공력 독립(FR-AER-09) | — | 분리 직후 조각–컨테이너 사이 후류·차폐 없음 → 초기 tip-off 를 과소/과대 추정할 수 있음 |");
        P();
        double cdc = Models.CylinderCrossflow.Cd2D(3.4e6, 0.5), eta = Models.CylinderCrossflow.Eta(bl.Length / bl.Diameter, out _);
        double sRef = 0.5 * (Math.PI * bl.Diameter * bl.Diameter / 4 + bl.Diameter * bl.Length);
        double cmqEst = bl.Diameter > 0 ? bl.Diameter * cdc * bl.Length / (3 * sRef) : double.NaN;
        P();
        P($"감쇠 미계수 크기 점검(횡류 이론, 준정상): 축 위치 x 의 국부 횡속도 ω·x 가 만드는 횡류 항력 ρ V ω x² D CD 를 길이로 적분하면 M = ρ V ω D CD L³/12. " +
          $"엔진의 감쇠 정의 M = ¼ ρ V S l² Cmq ω (S = 평균 정면적 {sRef:F3} m², l = L {bl.Length:F3} m)로 맞추면 Cmq ≈ −D·CD·L/(3S) = **{-cmqEst:F2}** (CD2D {cdc:F2}; 유한 길이 η {eta:F2} 를 곱하면 {-cmqEst * eta:F2}). " +
          $"시나리오 가정 Cmq {bl.Damping.Y:G3} 는 이 값의 약 {(bl.Damping.Y / -cmqEst):F0} 배 — **감쇠 배율 ≈ {(-cmqEst / bl.Damping.Y):F2}(η 포함 {(-cmqEst * eta / bl.Damping.Y):F2})가 횡류 이론값**이다(스윕 0.1 · 지도 D 의 0.05/0.1 열). " +
          "세장체 퍼텐셜(부가질량·Cmα̇) 항과 박리 지연은 빠져 있으므로 실제 값은 CFD 강제진동/시험으로 정해야 하며, 그 전까지 공칭(배율 1) 결과는 **비보수적(텀블링 과소 예측)일 수 있다**.");
        P();
        P("결론: 현재 모델은 텀블링의 **발생 경향(어느 입력이 지배하는지·경계가 어디쯤인지)** 을 보는 데 쓰고, 절대 경계값은 CFD DB(정적 Cm(α)·CN(α)) + 강제진동 Cmq 로 갱신해야 한다. 입력 교체 지점은 머리말의 `--aero-db` · `--damping`.");
        P();
        foreach (var line in FatigueLines(nom, bl, o.FatigueSimpkg)) P(line);
        P("## 8. 신뢰성(PRD AB절, NASA-STD-7009A) 연결");
        P();
        P("결과 등급: **추정(Estimate)** — 의사결정 범위: 분리 입력(Δv·비대칭·tip-off·지연)의 상대 영향 비교·시험 계획 우선순위. 텀블링 확률의 절대값으로 설계 승인 금지.");
        P();
        var ev = Credibility.EvidenceFor("원통 횡류 항력(TR R-300) + 감쇠 미계수 + 6DOF");
        P("| 7009A 요인 | 수준(0–4) | 근거 |");
        P("|---|---|---|");
        P("| 검증(Verification) | 2 | 6DOF 엔진 NASA check-case 회귀(VerificationTests, VV-05)·원통 횡류 표 단위 테스트(ComponentAeroTests), 이 연구의 인자 적용·판정 단위 테스트(TumbleMaterialStudyTests) |");
        P($"| 확인(Validation) | 1 | {ev.ValidationNote} |");
        P($"| 입력 출처(Input Pedigree) | 1 | {ev.DataEvidence} · 분리 입력(Δv·충격·tip-off·지연) 전부 D(가정) — 그래서 범위 스윕 |");
        P("| 결과 불확도(Results Uncertainty) | 2 | 입력 불확도 = 2절 범위 LHS MC + Wilson 구간, 모델 형태 = 압력중심·감쇠 인자, 수치 = dt 1 ms(기존 dt/2 수렴 검사 UQ 보고서) |");
        P("| 결과 강건성(Results Robustness) | 2 | 1-D 스윕·2-D 지도·PRCC 로 민감 인자 식별 |");
        P($"| 사용 이력(Use History) | 0 | 이 형상({design.Rev}) 텀블링 예측의 비교 사례 없음 |");
        P("| M&S 관리 | 2 | 명령·시나리오 해시·시드·엔진 버전 기록, runs.csv 원자료 저장, 저장소 테스트 |");
        P("| 수행자 자격 | — | 채점 안 함(자동 산출 불가) |");
        P();
        P("시험 우선순위(이 출력의 불확도를 가장 많이 줄이는 순서): ① 컨테이너 동적 미계수·Cm(α) — CFD 강제진동/소형 풍동 ② 3분할 기폭 시 조각 Δv·컨테이너 tip-off 각속도 — 지상 분리 시험(고속 카메라) ③ 분리 자세(받음각)·드로그 지연 — 운반체 자료.");
        P();
        P("## 9. 재현");
        P();
        P("```");
        P(o.Command);
        P("```");
        P();
        P("- 같은 명령에 `--aero-db <컨테이너 CFD DB>` 를 붙이면 CFD 공력으로, `--damping -2,-6,-6` 으로 감쇠를 바꿔 다시 산출. `--runs N` MC 횟수, `--no-maps`/`--no-sweeps` 로 축소.");
        P($"- 실모델({design.Rev} 그룹 내보내기, git 밖)이 없으면 시나리오의 합성 대체 질량특성으로 동작한다(`--real-models <폴더>` 로 지정).");
        return sb.ToString();
    }

    private static string N(JsonNode? n) => n is JsonValue v && v.TryGetValue(out double d) ? (Math.Abs(d) >= 100 ? d.ToString("F0", Inv) : d.ToString("F2", Inv)) : "—";

    private static double Pct(IEnumerable<double> v, double p)
    {
        var s = v.Where(double.IsFinite).OrderBy(x => x).ToList();
        if (s.Count == 0) return double.NaN;
        return s[Math.Clamp((int)Math.Round(p * (s.Count - 1)), 0, s.Count - 1)];
    }

    /// <summary>지도 경계 요약: y 행마다 등급이 처음 T1·T2·T3 이상이 되는 x.</summary>
    private static string Boundary(MapDef m, List<Run> runs)
    {
        var fx = F(m.XKey); var fy = F(m.YKey);
        var lines = new List<string>();
        foreach (var y in m.Ys)
        {
            var row = m.Xs.Select(x => runs.FirstOrDefault(q => Math.Abs(q.Values[m.XKey] - x) < 1e-9 && Math.Abs(q.Values[m.YKey] - y) < 1e-9)).ToList();
            string First(Func<Run, bool> pred)
            {
                for (int i = 0; i < row.Count; i++) if (row[i] is { Error: null } rr && pred(rr)) return $"{m.Xs[i].ToString("G", Inv)}";
                return "없음";
            }
            lines.Add($"{fy.Label} {y.ToString("G", Inv)} {fy.Unit}: 진동(T1+) ≥ {First(q => q.Class >= 1)} · 뒤집힘(T2+) ≥ {First(q => q.Class >= 2)} · 텀블링(T3) ≥ {First(q => q.Class >= 3)} · 드로그 위험 ≥ {First(q => q.DrogueCls == 2)} [{fx.Unit}]");
        }
        return "경계(행별, " + fx.Label + " 가 처음 그 등급에 드는 값):\n\n" + string.Join("\n", lines.Select(l => "- " + l));
    }
}
