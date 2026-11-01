using System.Globalization;
using System.Text.Json.Nodes;
using Sim.Core.Models;
using Sim.Core.Scenario;
using Sim.Core.Validation;

namespace Sim.Core.Reliability;

/// <summary>요인 점수(수준 0–4, 서수 — 합산·평균 금지, NASA-STD-7009A E.4). Evidence = 근거 문장, Links = 근거 파일.</summary>
public sealed record FactorScore(string Factor, string Category, int Level, int Threshold, string Evidence, string Definition, IReadOnlyList<string> Links)
{
    public bool Gap => Level < Threshold;
}

public sealed record OutputCredibility(string Key, string Label, string Unit, string Model, IReadOnlyList<FactorScore> Factors, string Class, string UseScope,
    double Nominal, double Lo90, double Hi90, double Prob, double ProbLo, double ProbHi)
{
    public int Level(string factor) => Factors.First(f => f.Factor == factor).Level;
}

/// <summary>
/// 모델별 근거(코드에 미리 선언 — 채점이 결과를 보고 바뀌지 않게): 개발 자료(Data Pedigree)·검증 사례(Validation)·코드 검증 테스트(Verification).
/// ValidationCases 는 '충분히 비슷한 참조계(representative/similar referent)'로 볼 수 있는 독립 검증 사례만 — 근거 문장 포함.
/// </summary>
public sealed record ModelEvidence(string Model, int DataPedigree, string DataEvidence, string[] ValidationCases, string ValidationNote,
    string[] VerificationTests, string[] Links);

public static class Credibility
{
    /// <summary>프로젝트 충분성 문턱(7009A E.5 — 표준은 문턱을 정하지 않는다): 개발·사용 요인 2, 지원 근거 요인 1 (예비 설계 판단용).</summary>
    public static int Threshold(string category) => category == "Supporting Evidence" ? 1 : 2;

    public static readonly ModelEvidence[] Models =
    [
        new("낙하산 충만·개방하중", 2,
            "개발 자료: Cruz 2003 DGB 풍동·투하(A, 표) 로 k0 보정, Knacke NWC TP 6575 표 5-6 충만 상수(A), Iris Ultra 정격(C) — 공식 문서 추적·불확도 추정됨. " +
            "대표성: 대형 DGB·소형 십자형(무한 질량)으로 우리 소형 환형 캐노피·유한 질량과 다름 → 수준 3 불가",
            ["parachute_dgb_opening_cruz2003", "parachute_cross40in_opening_noltr73157"],
            "소형(S0 0.47 m²) 저투과 캐노피 무한 질량 개방하중(NOLTR 73-157)과 대형 DGB 풍동(Cruz) 독립 검증 통과 — 충분히 비슷한 참조계. " +
            "유한 질량·저하중(박스 낙하산 30 Pa) 실측은 공개 자료 없음(Knacke X1 곡선 범위 밖)",
            ["ParachuteAndConvergenceTests", "OpeningShockTests", "PublicValidationTests(VV-41)"],
            ["validation/reports/parachute_dgb_opening_cruz2003.md", "validation/reports/parachute_cross40in_opening_noltr73157.md",
             "validation/reports/parachute_cross40in_opening_noltr73157_apriori.md"]),
        new("정적 끈·브레이크 코드(인장 전용 스프링·파단)", 2,
            "파단 하중 V-T-295 규격 표(A), 신율 AFFDL 그림 4.6(B) — 규격 최소값이라 실제 분포 미상(−0/+25 % 추정)",
            [], "끈 스냅·파단 동역학을 실측과 비교한 공개 자료 없음 — 해석해(에너지 해) 검증만",
            ["OpeningShockTests(스냅 장력 에너지 해 ±0.5 %)"], ["validation/reports/opening_shock/README.md"]),
        new("박스 내부 하중 경로(cushionMount: 핀·폼 2질량)", 1,
            "EPP 폼 제조사 준정적 표(C) — 동적 곡선 공개 자료 없음, 핀 치수·감쇠 가정(D)",
            [], "폼 완충 드론 G 실측 공개 자료 없음 — 해석해(Duhamel half-sine ±2 %) 검증만",
            ["OpeningShockTests(Duhamel 반정현 응답 ±2 %, 정적 1 G 평형)"], ["validation/reports/opening_shock/README.md"]),
        new("6DOF 다물체 + 사출 펄스·끈·낙하산", 1,
            "엔진은 NASA 체크케이스(A) 로 검증, 사출 펄스(15 G·25 ms)는 가정(D)",
            [], "다물체 사출 동역학 실측 없음(V3 박스 실기 투하 필요)",
            ["VerificationTests", "PublicValidationTests(NASA check-case VV-05)"], ["validation/reports/nasa_6dof_checkcases.md"]),
        new("부품 닫힌 식 응력(Roark·Winkler·러그) + 재질 라이브러리", 1,
            "응력 식 = 교과서 해석해(A), 재질 = 핸드북 값(B/C), 부품 치수 = 가정(D)",
            [], "부품 응력 실측 없음 — CalculiX 실모델 대조(같은 하중)만, 시험 없음",
            ["StructuresTests", "FeaTests", "CalculixTests(외팔보 0.9998)"], ["validation/reports/fea_calculix_verification.md", "validation/reports/opening_shock/README.md"]),
        new("6DOF 전 구간(공력·낙하산·바람·드론 비행)", 1,
            "구성 모델별 자료는 A–C 이나 투하 조건·바람·드론 제어 상수는 가정(D)",
            [], "전 구간(투하→관입) 비교 자료 없음 — 구성 요소 검증(NASA 체크케이스·낙하산 하강 일관성·VRS)만",
            ["VerificationTests", "PublicValidationTests", "OperationalScenarioTests"], ["validation/reports/SUMMARY.md"]),
        new("원통 횡류 항력(TR R-300) + 감쇠 미계수 + 6DOF", 1,
            "횡류 CD = NASA TR R-300 표(A) + TN 2960 η·아임계 마하(B), 감쇠 미계수 Cmq·Cnr = 가정(D, 유의 입력)",
            [], "텀블링 자세 이력 실측 공개 자료 없음. TR R-300 비교는 모델 원천과 같은 자료(일관성). CFD 기준 사례(2-D 원통 횡류)는 정상 RANS k-ω SST 가 "
                + "CD 평균 −51 %(y⁺<1, 10 건)·−40 %(벽 함수, 2 건)로 검증 실패 — 코드 검증(TMR 평판 Cf −0.2 %)은 통과, 원인은 정상 해가 와류 흘림을 없애는 모델 형태. "
                + "2-D URANS(R2 비압축)도 −58 %(흘림 없음·통계 미수렴) — 그래서 CFD 공력 DB 는 이 모델의 근거가 아니라 비교·UQ 편향 입력(validation/reports/cfd/reference_case_analysis.md). "
                + "3-D DDES(컨테이너 실형상, M 0.5 α 90°, 60 d/V)는 흘림을 재현(St 0.203–0.207·9 주기, 고 Re 기준 0.2–0.3 범위 · α 45° 횡류 기준 St 0.194 독립 원리 일치), "
                + "측면 기준 CD 0.60 = L1 대비 −14 %(같은 보고서 7절)",
            ["ComponentAeroTests", "PublicValidationTests(cylinder-crossflow-2d)", "VerificationTests"],
            ["validation/reports/drag_cylinder_crossflow_mach_tr_r300.md", "validation/reports/cfd/README.md", "validation/reports/cfd/reference_case_analysis.md"]),
        new("수납물 충격 응답(6DOF 강체 가속도 + 장착 1·2자유도 SRS)", 1,
            "부품 위치·질량 = 설계 STEP·질량표(CAD 재질 또는 사용자 질량표, A/C), 장착 강성 = 닫힌 식 + 가정 스프링(D, fₙ ×0.5–2 포락), 감쇠 Q 10(시험 규격 관례)",
            [], "장착 진동(타격·스윕) 시험 없음 — SRS 필터 해석해(계단 2.0 · 반 사인 1.77 · 램프 DAF) 검증만",
            ["LoadsStudyTests(SRS 해석해·2자유도 극한·F = m·a)", "VerificationTests"], ["validation/reports/loads09/README.md"]),
        new("분리 재접촉(충격량 접촉 + 반 사인 지속 τ)", 0,
            "접촉 지속 τ 1–5 ms·노즈 L1 형상(평평한 원통)·분리 Δv 모두 가정(D) — 출처 없음",
            [], "탄체 분리·재접촉 실측 없음", ["MunitionSplitTests", "HingeConvergenceTests", "LoadsStudyTests"], ["validation/reports/loads09/README.md"]),
        new(CompliantSeparationModel, 0,
            "접촉 강성 = 교과서 닫힌 식(막 퍼짐·자유-자유 봉/보 관성 완화·곡률 영향 반경 판 굽힘·Reissner 구 셸·평평한 압자, A) × 시나리오 기본 형상·벽 두께(CAD·설계 답변, C) × 재질(핸드북, B) — " +
            "이상화(관·껍질 조각, 준정적 스프링, 느린 모드 제외 규칙)는 등급 D(배율 ×0.5–2 MC), CalculiX S8R 관성 완화 강성으로 교차 확인. " +
            "탄성-소성(선택): 힘 상한 = 경도 2.8·σ_y(Tabor·Johnson, A) × 패치(이상화 D) · 벽 압괴(Alexander·DeRuntz–Hodge 평균 압괴력, B). 반발 e 0.3 · 분리 Δv · 3분할 힌지는 여전히 가정(D) — 출처 없음 → 수준 0 유지",
            [], "탄체 분리·재접촉 실측 없음(접촉 펄스·재접촉 상대속도·접촉 자국 깊이 시험 필요)",
            ["CollisionComplianceTests(질량–스프링 반 사인·반발 e·봉/보 관성 완화 1/3·1/105·13/35·보 끝 충격·BaseAccel 면적 = Δv)",
             "ElasticPlasticContactTests(Thornton 1997 구 반발 닫힌 식·선형 완전소성 e = v_y/v·경화 에너지 해·탄성 한계 아래 같은 펄스)", "MunitionSplitTests", "LoadsStudyTests"],
            ["validation/reports/loads09/README.md"]),
        new("컨테이너 셸 CFD Cp(정상 RANS) + CalculiX S8R 정적·좌굴", 1,
            "CFD = OpenFOAM v2512 정상 RANS(이미지 해시 고정), 적분력 보정 기준 = NASA TR R-300(A), 셸 = 등가 등방 E 50 GPa(준등방 근사, C), 와류 계수 = 공개 범위(C'_L 0.05–0.3, C)",
            [], "2-D 원통 기준 사례에서 정상 RANS 검증 실패(−51 %), 3-D 는 L1 과 4 % — 튜브 외압·굽힘 시험 없음",
            ["LoadsStudyTests(Cp 매핑 적분 해석해)", "CalculixTests", "TumbleMaterialStudyTests"],
            ["validation/reports/loads09/README.md", "validation/reports/cfd/reference_case_analysis.md", "validation/reports/container_material/README.md"]),
        new("잔해 궤적 + 캐노피 영역 간섭 판정 + 전개 이상 계수", 0,
            "간섭 영역(캐노피 원뿔+라인)·전개 이상 계수(지연 0.3 s·부분 충만 0.6·라인 손상)는 모두 가정(D) — 출처 없음",
            [], "잔해–낙하산 간섭 실측 없음",
            ["MunitionSplitTests", "AdversarialTests(간섭 판정 경계)"], ["validation/reports/scenario09/README.md"]),
    ];

    /// <summary>순응 접촉(loads.json contactModel.model = compliant) 분리 재접촉 모델 이름.</summary>
    public const string CompliantSeparationModel = "분리 재접촉(순응 접촉 — 구조 강성 닫힌 식 + 접촉 동안 적분)";

    public static ModelEvidence EvidenceFor(string model) =>
        Models.FirstOrDefault(m => m.Model == model) ?? new ModelEvidence(model, 0, "근거 미선언", [], "근거 미선언", [], []);

    private static string Def(string factor, int level)
    {
        var c = ReferenceData.Section("credibility7009A");
        var f = c["factors"]!.AsArray().OfType<JsonObject>().First(x => J.Str(x, "factor", "") == factor);
        return J.Str(f["levels"]!.AsObject(), level.ToString(CultureInfo.InvariantCulture), "");
    }

    private static string CategoryOf(string factor)
    {
        var c = ReferenceData.Section("credibility7009A");
        var f = c["factors"]!.AsArray().OfType<JsonObject>().First(x => J.Str(x, "factor", "") == factor);
        return J.Str(f, "category", "");
    }

    public static readonly string[] Factors =
        ["Data Pedigree", "Verification", "Validation", "Input Pedigree", "Uncertainty Characterization", "Results Robustness", "M&S History", "M&S Process/Product Management"];

    public static string FactorKo(string f) => f switch
    {
        "Data Pedigree" => "개발 자료 출처",
        "Verification" => "검증(Verification)",
        "Validation" => "확인(Validation)",
        "Input Pedigree" => "입력 출처",
        "Uncertainty Characterization" => "불확도 특성화",
        "Results Robustness" => "결과 강건성(민감도)",
        "M&S History" => "사용 이력",
        "M&S Process/Product Management" => "M&S 관리",
        _ => f,
    };

    private static FactorScore Score(string factor, int level, string evidence, params string[] links) =>
        new(factor, CategoryOf(factor), level, Threshold(CategoryOf(factor)), evidence, Def(factor, level), links);

    /// <summary>
    /// 출력 하나를 8요인으로 채점(규칙은 코드 주석과 보고서에 그대로 공개):
    ///  · Data Pedigree — 모델 근거 표(선언값).
    ///  · Verification — 2: 자동 회귀 테스트가 있고 수치 불확도(dt/2 차) ≤ 5 %; 1: 테스트는 있으나 수치 오차 &gt; 5 % 또는 미측정.
    ///  · Validation — 2: 선언된 독립 검증 사례가 모두 '통과'(A 등급 실패 없음); 1: 개념 모델·문제 정의 문서화(PRD·시나리오)만.
    ///  · Input Pedigree — 유의 입력(|PRCC| ≥ 0.2, p &lt; 0.05, 환경 변동 제외) 이 모두 A–C 이고 분포가 있으면 2, D 가 하나라도 있으면 1.
    ///  · Uncertainty Characterization — 3: MC(≥ 100 회, 오류 &lt; 5 %) + 수치 + 검증 기반 모델 형태가 모두 반영되고 유의 입력이 모두 분포를 가짐;
    ///    2: MC + 수치(모델 형태 미정량); 1: 그 밖.
    ///  · Results Robustness — 2: 모든 분포 입력의 PRCC 정량(조합 민감도 없음 → 3 불가); 1: PRCC 불가(확률 출력 등).
    ///  · M&S History — 1: 새 모델(2026), 변경·사용 문서화(git·PRD). · M&S 관리 — 1: CM(git·엔진 해시·Docker 이미지 고정)은 있으나 역할·책임 공식 문서 없음.
    /// </summary>
    public static OutputCredibility Assess(UqOutputResult o, UqResult uq, IReadOnlyList<CaseResult> val)
    {
        var ev = EvidenceFor(o.Def.Model);
        var inv = CultureInfo.InvariantCulture;
        var fs = new List<FactorScore>();
        fs.Add(Score("Data Pedigree", ev.DataPedigree, ev.DataEvidence, ev.Links));

        double num = o.NumericalRel;
        bool numOk = o.Def.Probability || (double.IsFinite(num) && num <= 0.05);
        string numText = o.Def.Probability ? "확률 출력 — dt 영향은 사건 판정에만" : double.IsFinite(num) ? $"공칭 dt 대 dt/2 차 {num:P2}" : "수치 불확도 미측정";
        fs.Add(Score("Verification", ev.VerificationTests.Length > 0 && numOk ? 2 : 1,
            $"자동 회귀 테스트: {string.Join(", ", ev.VerificationTests)} (dotnet test 통과가 완료 조건). 해 검증: {numText} (요구 ≤ 5 %)."));

        var cases = ev.ValidationCases.Select(id => val.FirstOrDefault(c => c.Id == id)).ToList();
        bool valOk = cases.Count > 0 && cases.All(c => c is not null && c.Status == CaseStatus.Pass);
        string caseText = cases.Count == 0 ? "독립 검증 사례 없음" :
            string.Join("; ", ev.ValidationCases.Select((id, k) => $"{id}: {(cases[k] is { } c ? PublicValidation.StatusLabel(c.Status) + string.Format(inv, " (최대 |오차|/허용 {0:0.00})", c.MaxToleranceRatio) : "미실행")}"));
        fs.Add(Score("Validation", valOk ? 2 : 1, $"{caseText}. {ev.ValidationNote}", ev.Links));

        var sig = new List<(UqInput In, double R)>();
        for (int j = 0; j < uq.Inputs.Count && j < o.PrccValues.Length; j++)
            if (Math.Abs(o.PrccValues[j]) >= 0.2 && o.PValues[j] < 0.05 && uq.Inputs[j].Grade is not "환경") sig.Add((uq.Inputs[j], o.PrccValues[j]));
        var gradesSig = sig.Select(s => s.In.Grade).ToList();
        bool anyD = gradesSig.Any(g => g == "D");
        string sigText = sig.Count == 0 ? "유의 입력 없음(|PRCC| ≥ 0.2) — 등록 입력 전체 기준" :
            string.Join(", ", sig.OrderByDescending(s => Math.Abs(s.R)).Select(s => string.Format(inv, "{0}[{1}] {2:+0.00;-0.00}", Short(s.In.Path), s.In.Grade, s.R)));
        int inLevel = sig.Count == 0 ? (uq.Inputs.Any(i => i.Grade == "D") ? 1 : 2) : (anyD ? 1 : 2);
        fs.Add(Score("Input Pedigree", inLevel,
            $"유의 입력: {sigText}. 등록 {uq.Pedigree.Entries.Count}개(A {uq.Pedigree.ByGrade["A"]}·B {uq.Pedigree.ByGrade["B"]}·C {uq.Pedigree.ByGrade["C"]}·D {uq.Pedigree.ByGrade["D"]}), " +
            $"미등록 수치 {uq.Pedigree.Unregistered}/{uq.Pedigree.NumericLeaves}(설계 기하·표시 등 — 'D(미분류)')."));

        bool mcOk = uq.Runs >= 100 && uq.Errors < 0.05 * uq.Runs;
        bool mfOk = o.ModelForm is not null || uq.Inputs.Any(i => i.Grade == "모델형태") && o.Def.Model.Contains("횡류");
        int uqLevel = !mcOk ? 1 : (mfOk && !anyD ? 3 : 2);
        fs.Add(Score("Uncertainty Characterization", uqLevel,
            string.Format(inv, "입력 MC {0}회(LHS, 오류 {1}) · 수치(dt/2) · 모델 형태: {2}.{3}", uq.Runs, uq.Errors,
                o.ModelForm is { } mf ? string.Format(inv, "{0} 편향 {1:+0.0%;-0.0%} 산포 {2:0.0%} (N {3})", mf.Label, mf.Bias, mf.Sd, mf.N)
                    : mfOk ? "원통 횡류 계수 분포(공력 배율)로 반영" : "미정량(검증 자료 없음)",
                anyD ? " 유의 입력 중 D(가정) 분포는 판단값." : "")));
        fs.Add(Score("Results Robustness", o.Def.Probability ? 1 : 2,
            o.Def.Probability ? "확률 출력 — 입력별 민감도는 정량하지 않음(조건부 확률 표로 대신)"
                : $"분포 입력 {uq.Inputs.Count}개 전부 PRCC 정량, 최대 기여 입력 식별. 입력 조합(상호작용) 민감도 미정량 → 수준 3 불가."));
        fs.Add(Score("M&S History", 1, "새 모델(2026-10 개발), 변경·사용은 git 이력·PRD·보고서에 문서화."));
        fs.Add(Score("M&S Process/Product Management", 1,
            "형상 관리: git(하네스 브랜치·커밋), 결과 provenance(엔진 버전·시나리오 해시), OpenFOAM Docker 이미지 ID 고정. 역할·책임·승인 절차의 공식 문서 없음 → 수준 2 불가."));

        int ver = fs[1].Level, vali = fs[2].Level, inp = fs[3].Level, unc = fs[4].Level;
        bool anyZero = fs.Any(f => f.Level == 0);   // 7009A 수준 0 = '근거 부족' → 의사결정용 등급 불가
        string cls = anyZero ? "추정" : ver >= 2 && vali >= 2 && inp >= 2 && unc >= 2 ? "신뢰" : ver >= 2 && vali >= 1 && unc >= 2 ? "참고" : "추정";
        string scope = cls switch
        {
            "신뢰" => "설계 판단·요구 충족 판정에 사용(90 % 구간의 불리한 쪽 기준). 적용 범위 밖(검증 사례 envelope) 외삽 금지.",
            "참고" => "설계안 비교·경향·민감도·시험 계획 근거로 사용. 절대값 합격/불합격 판정에는 쓰지 않는다(시험 확인 필요).",
            _ => "규모 파악·위험 식별용 추정치. 설계 결정 근거로 쓰지 않는다.",
        };
        return new OutputCredibility(o.Def.Key, o.Def.Label, o.Def.Unit, o.Def.Model, fs, cls, scope, o.Nominal, o.Lo90, o.Hi90, o.Prob, o.ProbLo, o.ProbHi);
    }

    /// <summary>
    /// `mns loads` 결과(loads.json, mns.loads/1)의 핵심 출력을 같은 8요인 규칙으로 채점(Assess 와 같은 규칙 — UQ 객체 대신 loads.json 의 MC 백분위·PRCC 유의 입력 사용):
    /// 90 % 구간 = MC 5–95 백분위(공력 셸은 보정 수준 범위). 반환 (시나리오 이름, 출력).
    /// </summary>
    public static List<(string Scenario, OutputCredibility C)> AssessLoads(JsonObject j, IReadOnlyList<CaseResult> val, string? reportDir = null)
    {
        if (reportDir is { Length: > 0 } && J.StrOpt(j, "reportDir") is null) { j = (JsonObject)j.DeepClone(); j["reportDir"] = reportDir.Replace('\\', '/'); }
        var res = new List<(string, OutputCredibility)>();
        if (J.Str(j, "schema", "") != "mns.loads/1") return res;
        string scen = J.Str(j, "scenario", "");
        int runs = (int)J.Num(j, "mcRuns", 0);
        double errRate = J.Num(j, "mcErrorRate", 0);
        var nomS = j["nominalScalars"] as JsonObject ?? new JsonObject();
        var mcS = j["mcScalars"] as JsonObject ?? new JsonObject();
        var sens = (j["sensitivity"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
        var inv = CultureInfo.InvariantCulture;
        string link = J.StrOpt(j, "reportDir") is { Length: > 0 } rd ? $"{rd.TrimEnd('/')}/README.md" : "validation/reports/loads09/README.md";
        bool compliant = j["contactModel"] is JsonObject cmo && J.Str(cmo, "model", "impulse") == "compliant";
        string sepModel = compliant ? CompliantSeparationModel : "분리 재접촉(충격량 접촉 + 반 사인 지속 τ)";

        OutputCredibility Make(string key, string label, string unit, string model, double nominal, double lo, double hi, string sensKey, bool mcBased, string extra)
        {
            var ev = EvidenceFor(model);
            var fs = new List<FactorScore> { Score("Data Pedigree", ev.DataPedigree, ev.DataEvidence, ev.Links) };
            fs.Add(Score("Verification", ev.VerificationTests.Length > 0 ? 2 : 1, $"자동 회귀 테스트: {string.Join(", ", ev.VerificationTests)} (dotnet test 통과가 완료 조건).{extra}"));
            var cases = ev.ValidationCases.Select(id => val.FirstOrDefault(c => c.Id == id)).ToList();
            bool valOk = cases.Count > 0 && cases.All(c => c is not null && c.Status == CaseStatus.Pass);
            fs.Add(Score("Validation", valOk ? 2 : ev.DataPedigree == 0 ? 0 : 1, (cases.Count == 0 ? "독립 검증 사례 없음. " : "") + ev.ValidationNote, ev.Links));
            var sig = sens.Where(x => J.Str(x, "output", "") == sensKey).ToList();
            bool anyD = sig.Any(x => J.Str(x, "grade", "") == "D");
            string sigText = sig.Count == 0 ? "유의 입력 없음 또는 미산출" : string.Join(", ", sig.OrderByDescending(x => Math.Abs(J.Num(x, "prcc", 0))).Take(5)
                .Select(x => string.Format(inv, "{0}[{1}] {2:+0.00;-0.00}", Short(J.Str(x, "path", "")), J.Str(x, "grade", ""), J.Num(x, "prcc", 0))));
            int inLevel = sig.Count == 0 ? 1 : anyD ? 1 : 2;
            fs.Add(Score("Input Pedigree", inLevel, $"유의 입력(|PRCC| ≥ 0.2): {sigText}. 장착 강성·{(compliant ? "접촉 강성 이상화(배율)" : "접촉 지속")}·일부 치수는 가정(D)."));
            bool mcOk = mcBased && runs >= 100 && errRate < 0.05;
            fs.Add(Score("Uncertainty Characterization", mcOk ? 2 : 1, mcBased
                ? string.Format(inv, "입력 MC {0}회(LHS, 오류율 {1:P1}) + 장착 fₙ ×0.5–2 포락 + {2} — 모델 형태 미정량", runs, errRate,
                    compliant ? "순응 접촉 강성 배율 범위(MC 입력)" : "접촉 τ 범위")
                : "보정 수준(CFD 그대로 · 공칭 L1 · 보수 ×1.7) 범위 + 와류 계수 상한 — 입력 MC 없음"));
            fs.Add(Score("Results Robustness", sig.Count > 0 ? 2 : 1, sig.Count > 0 ? "분포 입력 전부 PRCC 정량(조합 민감도 미정량 → 3 불가)" : "PRCC 미산출 — 1-D 스윕·보정 수준 비교만"));
            fs.Add(Score("M&S History", 1, "새 모델(2026-10 개발), 변경·사용은 git 이력·PRD·보고서에 문서화."));
            fs.Add(Score("M&S Process/Product Management", 1, "형상 관리: git, 결과 provenance(엔진·시나리오·부품 정의 해시). 역할·책임 공식 문서 없음 → 수준 2 불가."));
            int ver = fs[1].Level, vali = fs[2].Level, inp = fs[3].Level, unc = fs[4].Level;
            bool anyZero = fs.Any(f => f.Level == 0);
            string cls = anyZero ? "추정" : ver >= 2 && vali >= 2 && inp >= 2 && unc >= 2 ? "신뢰" : ver >= 2 && vali >= 1 && unc >= 2 ? "참고" : "추정";
            string scope = cls switch
            {
                "신뢰" => "설계 판단·요구 충족 판정에 사용(90 % 구간의 불리한 쪽 기준).",
                "참고" => "설계안 비교·경향·민감도·시험 계획 근거로 사용. 절대값 합격/불합격 판정에는 쓰지 않는다(시험 확인 필요).",
                _ => "규모 파악·위험 식별용 추정치. 설계 결정 근거로 쓰지 않는다.",
            };
            return new OutputCredibility(key, label, unit, model, fs, cls, scope, nominal, lo, hi, double.NaN, double.NaN, double.NaN);
        }

        double Nom(string k) => J.Num(nomS, k, double.NaN);
        double Mc(string k, string stat) => mcS[k] is JsonObject o ? J.Num(o, stat, double.NaN) : double.NaN;
        res.Add((scen, Make("loads:containerG:drogue", "컨테이너 드로그 개방 G(loads)", "G", "낙하산 충만·개방하중", Nom("containerG:drogue"), Mc("containerG:drogue", "p05"), Mc("containerG:drogue", "p95"), "containerG:drogue", true, "")));
        res.Add((scen, Make("loads:latchForce:drogue", "드론 1 래치 하중(드로그 개방)", "N", "수납물 충격 응답(6DOF 강체 가속도 + 장착 1·2자유도 SRS)", Nom("latchForce:drogue"),
            double.NaN, double.NaN, "latchForce:drogue", true, " F = m·a 검산 테스트 포함.")));
        if ((j["nominal"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(p => J.Str(p, "name", "") == "latch_pin1") is { } lp && lp["mcWindows"] is JsonObject mw && mw["drogue"] is JsonObject d)
            res[^1] = (scen, res[^1].Item2 with { Lo90 = J.Num(d, "force_p05_N", double.NaN), Hi90 = J.Num(d, "force_p95_N", double.NaN) });
        res.Add((scen, Make("loads:minMarginOpening", $"수납 부품 최소 여유(낙하산 개방) — {J.Str(nomS, "minMarginOpeningPart", "")}", "SF/기준", "부품 닫힌 식 응력(Roark·Winkler·러그) + 재질 라이브러리",
            Nom("minMarginOpening"), Mc("minMarginOpening", "p05"), Mc("minMarginOpening", "p95"), "minMarginOpening", true, " 닫힌 식 손계산·CalculiX 대조.")));
        res.Add((scen, Make("loads:containerG:separation", "분리 직후 컨테이너 G(재접촉 포함)", "G", sepModel, Nom("containerG:separation"),
            Mc("containerG:separation", "p05"), Mc("containerG:separation", "p95"), "containerG:separation", true, "")));
        res.Add((scen, Make("loads:minMarginSeparation", $"수납 부품 최소 여유(분리·재접촉) — {J.Str(nomS, "minMarginSeparationPart", "")}", "SF/기준", sepModel,
            Nom("minMarginSeparation"), Mc("minMarginSeparation", "p05"), Mc("minMarginSeparation", "p95"), "minMarginSeparation", true, "")));
        if (j["aeroSummary"] is JsonObject a)
        {
            double sn = J.Num(a, "strengthSf_nominal", double.NaN), sh = J.Num(a, "strengthSf_high", double.NaN), sc = J.Num(a, "strengthSf_cfd", double.NaN);
            res.Add((scen, Make("loads:containerAero:strength", "컨테이너 공력 강도 SF(α 90°, ×와류 상한)", "SF", "컨테이너 셸 CFD Cp(정상 RANS) + CalculiX S8R 정적·좌굴",
                sn, Math.Min(sh, sc), Math.Max(sh, sc), "", false, " Cp 매핑 적분 대조·균일 외압 좌굴 대조.")));
            double bn = J.Num(a, "buckleSf_nominal", double.NaN), bh = J.Num(a, "buckleSf_high", double.NaN), bc = J.Num(a, "buckleSf_cfd", double.NaN);
            res.Add((scen, Make("loads:containerAero:buckle", "컨테이너 공력 좌굴 SF(α 90°, 녹다운·끝 구속 보정·와류 상한)", "SF", "컨테이너 셸 CFD Cp(정상 RANS) + CalculiX S8R 정적·좌굴",
                bn, Math.Min(bh, bc), Math.Max(bh, bc), "", false, "")));
            if (a["vibration"] is JsonObject vb)
                res.Add((scen, Make("loads:containerAero:vibration", $"진동 피로(근사) — 와류 흘림 f_s/f_n(셸 1차): {J.Str(vb, "verdict", "")}", "f_s/f_n",
                    "St·V/D(St 범위) + CalculiX S8R 셸 고유진동", J.Num(vb, "ratio", double.NaN), double.NaN, double.NaN, "", false,
                    $" 기준 f_s < {J.Num(vb, "limit", 0.8):0.0} f_n(ASME PTC 19.3 TW-2016 공개 해설). 피로 손상은 공개 S-N 자료가 있는 알루미늄 부품만(tumble-study 7b).")));
        }
        return res;
    }

    public static string Short(string path)
    {
        var p = path.Replace("bodies.", "").Replace("devices.", "").Replace("connectors.", "").Replace("events.", "");
        return p.Length > 48 ? "…" + p[^47..] : p;
    }

    // ---------------- 시험 우선순위 ----------------

    public sealed record TestPriority(string Test, double Score, IReadOnlyList<string> Inputs, IReadOnlyList<string> Outputs);

    private static readonly (string Pattern, string Test)[] TestMap =
    [
        ("lid_drogue", "뚜껑 분리·드로그 전개 지연 측정(분리 기구 시험)"),
        ("drogue", "드로그 고속 전개 시험(운반 탄체 투하 시험 또는 고속 풍동) — 충만 상수·k0·CD0"),
        ("chute.fill", "박스 낙하산 개방 시험: 하중계 + 고속 영상(충만 시계열 → n·j·k0 동시 추정), 크레인/헬기 투하(V2)"),
        ("chute.apparentMassK0", "박스 낙하산 개방 시험: 하중계 + 고속 영상(충만 시계열 → n·j·k0 동시 추정), 크레인/헬기 투하(V2)"),
        ("main.fill", "주낙하산 개방 시험(하중계·고속 영상, V4)"),
        ("main.apparentMassK0", "주낙하산 개방 시험(하중계·고속 영상, V4)"),
        ("cd0", "낙하산 정상 하강률 측정(드롭 시험, 기압·GPS 로거)"),
        ("lineStretchTime", "전개 순서 고속 영상(라인 신장 시간)"),
        ("cord1", "브레이크 코드(실) 인장·충격 시험 — 파단 하중·신율 실측(만능시험기)"),
        ("eject", "박스 사출 기구 시험(가속도계 — 펄스 크기·지속시간, V3)"),
        ("mount.pads", "EPP 패드 동적 압축(낙하 충격) 시험 — 동적 응력–변형률"),
        ("mount", "박스 내부 고정 핀·패드 하중 경로 시험(드론 가속도계)"),
        ("tether1", "2 mm 다이니마 테더 인장 시험(EA·파단 하중)"),
        ("aero.damping", "컨테이너 풍동 동적 미계수(강제 진동) 또는 URANS 검증 CFD"),
        ("aero.scale", "컨테이너 횡류 항력 풍동 시험(M 0.3–0.5) — 원통 횡류 모델 형태 확인"),
        ("shock", "탄체 분리 기폭 시험(V5) — 강체 분리 속도·각속도"),
        ("petal", "탄체 분리 기폭 시험(V5) — 강체 분리 속도·각속도"),
        ("chuteAnomaly", "잔해–낙하산 간섭 시험(지연·부분 충만 계수)"),
        ("carrier", "운용 조건 확정(운용 기관 자료) — 시험 아님"),
        ("wind", "환경(기상) — 시험으로 줄지 않는 자연 변동"),
    ];

    public static string TestFor(string path) => TestMap.FirstOrDefault(t => path.Contains(t.Pattern, StringComparison.Ordinal)).Test ?? "입력 실측·도면 확인";

    private static double GradeWeight(string g) => g switch { "D" => 1.0, "모델형태" => 0.8, "C" => 0.7, "B" => 0.4, "A" => 0.2, _ => 0.0 };

    /// <summary>
    /// 시험 우선순위(FR-REL-05): 입력 j 의 점수 = Σ_출력 PRCC² × 등급 가중(D 1.0, 모델형태 0.8, C 0.7, B 0.4, A 0.2, 환경 0)
    /// — '그 입력을 시험으로 확정하면 줄어드는 출력 분산 몫'의 순위 지표(분산 분해의 근사, 판단 가중).
    /// 같은 시험으로 묶이는 입력은 합산.
    /// </summary>
    public static List<TestPriority> Priorities(IEnumerable<UqResult> uqs)
    {
        var acc = new Dictionary<string, (double S, HashSet<string> In, HashSet<string> Out)>();
        foreach (var uq in uqs)
            foreach (var o in uq.Outputs)
                for (int j = 0; j < uq.Inputs.Count && j < o.PrccValues.Length; j++)
                {
                    double s = o.PrccValues[j] * o.PrccValues[j] * GradeWeight(uq.Inputs[j].Grade);
                    if (s < 1e-4) continue;
                    var t = TestFor(uq.Inputs[j].Path);
                    if (!acc.TryGetValue(t, out var a)) a = (0, new HashSet<string>(), new HashSet<string>());
                    a.S += s;
                    a.In.Add($"{Short(uq.Inputs[j].Path)}[{uq.Inputs[j].Grade}]");
                    if (Math.Abs(o.PrccValues[j]) >= 0.2) a.Out.Add($"{uq.Scenario[..2]}:{o.Def.Label}");
                    acc[t] = a;
                }
        return acc.Select(kv => new TestPriority(kv.Key, kv.Value.S, kv.Value.In.ToList(), kv.Value.Out.ToList()))
                  .OrderByDescending(t => t.Score).ToList();
    }
}
