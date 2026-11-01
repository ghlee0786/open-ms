using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Sim.Core.Scenario;

namespace Sim.Core.Validation;

/// <summary>사례 판정. Pass: 허용치 이내. Warn: B/C 등급 불합격(경고만). Fail: A 등급 불합격(테스트 실패).
/// Calibration: 모델 계수를 이 자료로 정함(통과 주장 금지, VV-50). Consistency: 모델 입력과 측정값이 같은 출처(독립 검증 아님).
/// Unsupported: 재현기(kind) 미구현. OutOfEnvelope: 판정 대상 점이 모두 모델 적용 범위 밖(외삽, VV-32) — 오차는 보고하되 합격/불합격 없음.</summary>
public enum CaseStatus { Pass, Warn, Fail, Calibration, Consistency, Unsupported, OutOfEnvelope }

/// <summary>
/// 비교 한 줄. Role: validation(검증) | calibration(계수 보정에 쓴 점) | info(참고, 판정 안 함).
/// Judged=false 이면 판정 제외(모델 적용 범위 밖 = 외삽, 또는 참고) — 사유는 Note.
/// </summary>
public sealed record PointResult(string Label, double Measured, double Predicted, string Unit, double RelError, bool WithinTolerance, string Provenance)
{
    public string Role { get; init; } = "validation";
    /// <summary>점별 자료 등급(사례 등급과 다를 때: 예 입력값 일부가 추정). 불합격 시 A 만 실패, B/C 는 경고.</summary>
    public string Grade { get; init; } = "A";
    public bool Judged { get; init; } = true;
    public string Note { get; init; } = "";
    public double? ToleranceAbs { get; init; }
    public double ToleranceRel { get; init; }
    /// <summary>|오차| / 허용치 (1 이하면 이내).</summary>
    public double ToleranceRatio => ToleranceAbs is { } a ? Math.Abs(Predicted - Measured) / a : Math.Abs(RelError) / ToleranceRel;
}

public sealed class CaseResult
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Vv { get; init; }
    public required string Grade { get; init; }
    public required string Use { get; init; }
    public required string Kind { get; init; }
    public required string Model { get; init; }
    public required double Tolerance { get; init; }
    public List<PointResult> Points { get; } = new();
    public List<string> Notes { get; } = new();
    public CaseStatus Status { get; set; }
    public string Envelope { get; init; } = "";
    public JsonArray Sources { get; init; } = new();
    public string Directory { get; init; } = "";
    /// <summary>판정 대상 점의 최대 |상대오차| (판정 점이 없으면 NaN).</summary>
    public double MaxAbsRelError
    {
        get
        {
            var v = Points.Where(p => p.Judged && double.IsFinite(p.RelError)).Select(p => Math.Abs(p.RelError)).ToList();
            return v.Count == 0 ? double.NaN : v.Max();
        }
    }
    /// <summary>판정 대상 점의 최대 |오차|/허용치.</summary>
    public double MaxToleranceRatio
    {
        get
        {
            var v = Points.Where(p => p.Judged && double.IsFinite(p.ToleranceRatio)).Select(p => p.ToleranceRatio).ToList();
            return v.Count == 0 ? double.NaN : v.Max();
        }
    }
    /// <summary>판정 대상 점이 하나 이상 있고 모두 허용치 이내.</summary>
    public bool AllWithin => Points.Any(p => p.Judged) && Points.Where(p => p.Judged).All(p => p.WithinTolerance);
}

/// <summary>재현기 출력 한 줄. Measured=null 이면 사례 점의 "measured" 를 쓴다.</summary>
public sealed record KindOutput(string Label, double? Measured, double Predicted, string Unit)
{
    public double? ToleranceAbs { get; init; }
    public double? ToleranceRel { get; init; }
    public bool Judged { get; init; } = true;
    public string? Role { get; init; }
    public string Note { get; init; } = "";
}

/// <summary>
/// 공개 데이터 Validation(PRD O-4, VV-49~51). `&lt;root&gt;/&lt;case&gt;/case.json`(schema mns.validation-case/1)을 읽어
/// kind 별 재현기로 시뮬레이터(엔진·모델 함수)를 돌리고 측정값과 비교한다. 형식은 docs/validation-cases.md.
/// 사례 수준 "calibration": {parameter, lo, hi} 가 있으면 role=calibration 점(그룹별, 점의 "group")으로 계수를
/// 이분법으로 맞춘 뒤(평균 상대오차 0) 같은 그룹의 나머지 점을 그 계수로 예측한다(보정/검증 분리, VV-50).
/// </summary>
public static partial class PublicValidation
{
    public const string Schema = "mns.validation-case/1";

    private delegate IEnumerable<KindOutput> KindFn(JsonObject inputs, JsonObject point);

    private static KindFn Single(Func<JsonObject, JsonObject, (double Value, string Unit)> f) => (i, p) =>
    {
        var (v, u) = f(i, p);
        return new[] { new KindOutput(J.Str(p, "label", ""), null, v, u) };
    };

    private static readonly Dictionary<string, KindFn> Kinds = new()
    {
        ["parachute-steady-descent"] = ParachuteDescent,
        ["drag-coefficient"] = DragCoefficient,
        ["young-penetration"] = YoungPenetration,
        ["atmosphere-table"] = Single(AtmosphereTable),
        ["nasa-checkcase"] = NasaCheckcase,
        ["parachute-opening-infinite-mass"] = ParachuteOpeningInfiniteMass,
        ["parachute-deployment-flight"] = ParachuteDeploymentFlight,
        ["rotor-vrs-ct-ratio"] = RotorVrsCtRatio,
        ["propeller-static-ct"] = PropellerStaticCt,
        ["sphere-free-fall"] = SphereFreeFall,
        ["tumbling-mean-drag"] = TumblingMeanDrag,
        ["cylinder-crossflow-2d"] = CylinderCrossflow2D,
    };

    public static IReadOnlyCollection<string> SupportedKinds => Kinds.Keys;

    public static List<CaseResult> RunAll(string root)
    {
        var list = new List<CaseResult>();
        if (!System.IO.Directory.Exists(root)) return list;
        foreach (var dir in System.IO.Directory.GetDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
        {
            var f = Path.Combine(dir, "case.json");
            if (File.Exists(f)) list.Add(RunCase(f));
        }
        return list;
    }

    private static JsonObject MergeInputs(JsonObject common, JsonObject p)
    {
        var inputs = (JsonObject)common.DeepClone();
        if (p["inputs"] is JsonObject pi) foreach (var (k, v) in pi) inputs[k] = v?.DeepClone();
        return inputs;
    }

    private static string RoleOf(JsonObject p, string use) => J.Str(p, "role", use == "calibration" ? "calibration" : "validation");

    public static CaseResult RunCase(string caseFile)
    {
        var c = J.ParseObject(File.ReadAllText(caseFile));
        var schema = J.Str(c, "schema", Schema);
        var kind = J.Str(c, "kind", "");
        var grade = J.Str(c, "grade", "C").ToUpperInvariant();
        var use = J.Str(c, "use", "validation");
        var tol = c["tolerance"] is JsonObject t ? J.Num(t, "relative", 0.1) : 0.1;
        var res = new CaseResult
        {
            Id = J.Str(c, "id", Path.GetFileName(Path.GetDirectoryName(caseFile)!)),
            Title = J.Str(c, "title", ""), Vv = J.Str(c, "vv", ""), Grade = grade, Use = use, Kind = kind,
            Model = J.Str(c, "model", ""), Tolerance = tol, Envelope = J.Str(c, "envelope", ""),
            Sources = c["sources"] as JsonArray is { } s ? (JsonArray)s.DeepClone() : new JsonArray(),
            Directory = Path.GetDirectoryName(caseFile)!,
        };
        if (c["notes"] is JsonArray notes) res.Notes.AddRange(notes.Select(n => n!.GetValue<string>()));
        if (schema != Schema || !Kinds.TryGetValue(kind, out var run))
        {
            res.Status = CaseStatus.Unsupported;
            res.Notes.Add(schema != Schema ? $"schema '{schema}' 미지원(필요: {Schema})" : $"kind '{kind}' 재현기 미구현 — 지원: {string.Join(", ", Kinds.Keys)}");
            return res;
        }
        var common = c["inputs"] as JsonObject ?? new JsonObject();
        var points = (c["points"]?.AsArray() ?? new JsonArray()).Select(n => n!.AsObject()).ToList();

        // ---- 보정(calibration) ----
        var fitted = new Dictionary<string, double>();
        string? param = null;
        if (c["calibration"] is JsonObject cal)
        {
            param = J.StrReq(cal, "parameter", "calibration");
            double lo = J.Num(cal, "lo", 0), hi = J.Num(cal, "hi", 1);
            foreach (var g in points.Where(p => RoleOf(p, use) == "calibration").GroupBy(p => J.Str(p, "group", "")))
            {
                var pts = g.ToList();
                double F(double v) => pts.Average(p =>
                {
                    var inputs = MergeInputs(common, p);
                    inputs[param] = v;
                    var o = run(inputs, p).First();
                    double m = o.Measured ?? J.NumReq(p, "measured", "points[]");
                    return (o.Predicted - m) / m;
                });
                double flo = F(lo), fhi = F(hi);
                double a = lo, b = hi, fa = flo;
                if (flo > 0 || fhi < 0)
                    res.Notes.Add(string.Format(CultureInfo.InvariantCulture, "보정 경고: {0}[{1}] 가 탐색 범위 [{2}, {3}] 경계에 걸림", param, g.Key, lo, hi));
                for (int k = 0; k < 60; k++)
                {
                    double mid = 0.5 * (a + b), fm = F(mid);
                    if ((fm > 0) == (fa > 0)) { a = mid; fa = fm; } else b = mid;
                }
                fitted[g.Key] = 0.5 * (a + b);
                res.Notes.Add(string.Format(CultureInfo.InvariantCulture, "보정: {0}{1} = {2:G5} (보정점: {3})", param,
                    g.Key.Length > 0 ? $"[{g.Key}]" : "", fitted[g.Key], string.Join(", ", pts.Select(p => J.Str(p, "label", "")))));
            }
        }

        foreach (var p in points)
        {
            var inputs = MergeInputs(common, p);
            if (param is not null)
            {
                var key = J.Str(p, "group", "");
                if (fitted.TryGetValue(key, out var v) || fitted.TryGetValue("", out v)) inputs[param] = v;
                else if (!J.Has(inputs, param)) res.Notes.Add($"{J.Str(p, "label", "")}: 그룹 '{key}' 의 보정값이 없어 {param} 미지정");
            }
            string role = RoleOf(p, use);
            foreach (var o in run(inputs, p))
            {
                double measured = o.Measured ?? J.NumReq(p, "measured", "points[]");
                double? tolAbs = o.ToleranceAbs ?? J.NumOpt(p, "toleranceAbs");
                double tolRel = o.ToleranceRel ?? J.NumOpt(p, "tolerance") ?? tol;
                double rel = measured != 0 ? (o.Predicted - measured) / measured : double.NaN;
                bool within = tolAbs is { } ta ? Math.Abs(o.Predicted - measured) <= ta : Math.Abs(rel) <= tolRel;
                string r = o.Role ?? role;
                string unit = o.Measured is null ? J.Str(p, "unit", o.Unit) : o.Unit;
                string pg = J.Str(p, "grade", grade).ToUpperInvariant();
                string note = string.Join(" · ", new[] { o.Note, J.Str(p, "note", "") }.Where(x => x.Length > 0));
                res.Points.Add(new PointResult(o.Label, measured, o.Predicted, unit, rel, within,
                    J.Str(p, "provenance", J.Str(c, "provenance", "")))
                {
                    Role = r, Grade = pg, Judged = o.Judged && r != "info", Note = note, ToleranceAbs = tolAbs, ToleranceRel = tolRel,
                });
            }
        }

        var judgedVal = res.Points.Where(p => p.Judged && p.Role == "validation").ToList();
        res.Status = use switch
        {
            "calibration" => CaseStatus.Calibration,
            "consistency" => CaseStatus.Consistency,
            _ when judgedVal.Count > 0 => judgedVal.All(p => p.WithinTolerance) ? CaseStatus.Pass
                : judgedVal.Any(p => !p.WithinTolerance && p.Grade == "A") ? CaseStatus.Fail : CaseStatus.Warn,
            _ when res.Points.Any(p => p.Judged && p.Role == "calibration") => CaseStatus.Calibration,
            _ => CaseStatus.OutOfEnvelope,
        };
        return res;
    }

    // ---------------- 보고서 ----------------

    public static string StatusLabel(CaseStatus s) => s switch
    {
        CaseStatus.Pass => "통과",
        CaseStatus.Warn => "경고(B/C 불합격)",
        CaseStatus.Fail => "실패(A 등급)",
        CaseStatus.Calibration => "보정용(검증 아님)",
        CaseStatus.Consistency => "일관성 확인(독립 검증 아님)",
        CaseStatus.OutOfEnvelope => "외삽(모델 적용 범위 밖)",
        _ => "재현기 미구현",
    };

    private static string RoleLabel(PointResult p) => p.Role switch
    {
        "calibration" => "보정",
        "info" => "참고",
        _ => p.Judged ? "검증" : "외삽",
    };

    /// <summary>공개 데이터로 대체할 수 없는 항목(VV-51) — 자체 시험·CFD 가 필요.</summary>
    public static readonly string[] NotReplaceable =
    [
        "우리 형상(컨테이너 · 박스 · 드론 · 센서 날개)의 공력 계수·동적 미계수 — CFD(M5)·풍동/투하 시험 필요",
        "컨테이너 드로그 고속(M≈0.4) 전개·개방하중 — 운반 탄체 투하 시험",
        "레일 분리·테더 스냅·사출 기구 동역학 — 박스 실기 투하(V3)",
        "센서 날개 축소안의 낙하 자세 안정성·관입 — 드론 센서 투하(V1)",
        "VRS 이탈(S2) 전체 거동 — 드론 분리 시험(V3), UIUC 자료는 추력 손실 곡선만 대체",
        "소형(< 10 kg) 낙하산 개방하중 시계열 — 공개 자료 없음(대형 DGB 풍동 자료로 모델 형태만 검증)",
        "공기 중 원통·상자 자유낙하 6-DOF 자세 이력 — 공개 실측 없음(텀블링 풍동 평균 항력만)",
    ];

    public static string CaseMarkdown(CaseResult r)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine($"# {r.Id} — {r.Title}");
        sb.AppendLine();
        sb.AppendLine($"- 요구: {r.Vv} · 등급 {r.Grade} · 용도 `{r.Use}` · 재현기 `{r.Kind}`");
        sb.AppendLine($"- 검증 대상 모델: {r.Model}");
        sb.AppendLine(string.Format(inv, "- 기본 허용치: ±{0:0.#}% (점별 절대 허용치가 있으면 그것을 씀) · **판정: {1}**", r.Tolerance * 100, StatusLabel(r.Status)));
        if (r.Envelope.Length > 0) sb.AppendLine($"- 적용 범위(envelope): {r.Envelope}");
        int nVal = r.Points.Count(p => p.Judged && p.Role == "validation");
        int nCal = r.Points.Count(p => p.Role == "calibration");
        int nOut = r.Points.Count(p => !p.Judged && p.Role != "info");
        sb.AppendLine($"- 비교 {r.Points.Count}줄: 검증 {nVal} (이내 {r.Points.Count(p => p.Judged && p.Role == "validation" && p.WithinTolerance)}) · 보정 {nCal} · 외삽/판정 제외 {nOut} · 참고 {r.Points.Count(p => p.Role == "info")}");
        sb.AppendLine();
        sb.AppendLine("## 출처");
        foreach (var s in r.Sources.OfType<JsonObject>())
            sb.AppendLine($"- {J.Str(s, "title", "")}{(J.StrOpt(s, "url") is { } u ? $" <{u}>" : "")}{(J.StrOpt(s, "note") is { } n ? $" — {n}" : "")}");
        sb.AppendLine();
        if (r.Points.Count > 0)
        {
            sb.AppendLine("## 비교");
            sb.AppendLine("| 조건 | 구분 | 측정 | 시뮬레이터 | 단위 | 오차 | 허용 | 판정 | 값 출처 · 비고 |");
            sb.AppendLine("|---|---|---:|---:|---|---:|---:|---|---|");
            foreach (var p in r.Points)
            {
                string err = p.ToleranceAbs is not null || !double.IsFinite(p.RelError)
                    ? Signed(p.Predicted - p.Measured, 4) : Signed(p.RelError * 100, 2) + "%";
                string tolS = p.ToleranceAbs is { } ta ? "±" + ta.ToString("G4", inv) : "±" + (p.ToleranceRel * 100).ToString("0.#", inv) + "%";
                string verdict = !p.Judged ? "—" : p.WithinTolerance ? "이내" : "**초과**";
                string note = string.Join(" · ", new[] { p.Provenance, p.Note }.Where(x => x.Length > 0));
                sb.AppendLine(string.Format(inv, "| {0} | {1} | {2:G5} | {3:G5} | {4} | {5} | {6} | {7} | {8} |",
                    p.Label, RoleLabel(p) + (p.Grade != r.Grade ? $"({p.Grade})" : ""), p.Measured, p.Predicted, p.Unit, err, tolS, verdict, note));
            }
            sb.AppendLine();
            sb.AppendLine(Bars(r));
            sb.AppendLine();
        }
        if (r.Notes.Count > 0)
        {
            sb.AppendLine("## 비고");
            foreach (var n in r.Notes) sb.AppendLine($"- {n}");
        }
        return sb.ToString();
    }

    /// <summary>오차 막대(텍스트 그래프): 각 줄의 |오차|/허용치. | = 허용 경계.</summary>
    private static string Bars(CaseResult r)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder("```text\n");
        sb.AppendLine("|오차|/허용치 (| = 허용 경계, # 한 칸 = 허용치의 10%, 최대 2배까지 표시; 외삽·참고 줄은 ~)");
        foreach (var p in r.Points)
        {
            double ratio = p.ToleranceRatio;
            int n = double.IsFinite(ratio) ? (int)Math.Round(Math.Min(ratio, 2) * 10) : 0;
            var bar = new string(p.Judged ? '#' : '~', n).PadRight(20);
            bar = bar[..10] + "|" + bar[10..];
            double signed = p.Predicted - p.Measured;
            sb.AppendLine(string.Format(inv, "{0,-34} {1}{2} {3:0.00}", Trunc(p.Label, 34), signed < 0 ? "-" : "+", bar, ratio));
        }
        sb.Append("```");
        return sb.ToString();
    }

    private static string Signed(double x, int digits)
    {
        double r = Math.Round(x, digits);
        if (r == 0) r = 0; // −0 → +0
        var s = r.ToString("F" + digits, CultureInfo.InvariantCulture);
        return s.StartsWith('-') ? s : "+" + s;
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    public static string SummaryMarkdown(IReadOnlyList<CaseResult> results, string root, IEnumerable<string>? extraNotes = null)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("# 공개 데이터 Validation 종합 (VV-49~51)");
        sb.AppendLine();
        sb.AppendLine($"- 사례 폴더: `{root}` · 생성 엔진: Sim.Core {Analysis.Results.EngineVersion}");
        sb.AppendLine($"- 판정 규칙(VV-50): A 등급 불합격 = 테스트 실패, B/C 불합격 = 경고. 계수를 이 자료로 정했으면 '보정용', 모델 입력과 측정값의 출처가 같으면 '일관성 확인' — 어느 쪽도 검증 통과로 세지 않는다.");
        sb.AppendLine("- 모델 적용 범위(코드에 미리 선언) 밖의 점은 '외삽'으로 오차만 보고하고 합격/불합격에 넣지 않는다(VV-32).");
        sb.AppendLine();
        sb.AppendLine("| 사례 | 요구 | 등급 | 용도 | 줄 수(검증/보정/외삽) | 최대 오차/허용 | 판정 |");
        sb.AppendLine("|---|---|---|---|---:|---:|---|");
        foreach (var r in results)
        {
            int nv = r.Points.Count(p => p.Judged && p.Role == "validation"), nc = r.Points.Count(p => p.Role == "calibration"), no = r.Points.Count(p => !p.Judged && p.Role != "info");
            double tr = r.MaxToleranceRatio;
            sb.AppendLine(string.Format(inv, "| [{0}]({0}.md) | {1} | {2} | {3} | {4} ({5}/{6}/{7}) | {8} | {9} |",
                r.Id, r.Vv, r.Grade, r.Use, r.Points.Count, nv, nc, no, double.IsNaN(tr) ? "—" : tr.ToString("0.00", inv), StatusLabel(r.Status)));
        }
        sb.AppendLine();
        int indep = results.Count(r => r.Status == CaseStatus.Pass);
        sb.AppendLine("## 모델별 적용 범위(VV-32 형식)");
        foreach (var g in results.GroupBy(r => r.Model))
        {
            string level = g.Any(r => r.Status == CaseStatus.Pass) ? "검증됨(공개 데이터)" :
                g.Any(r => r.Status is CaseStatus.Calibration or CaseStatus.Consistency) ? "보정·일관성만(독립 검증 없음)" :
                g.Any(r => r.Status == CaseStatus.OutOfEnvelope) ? "외삽만(적용 범위 밖 자료)" : "미검증";
            sb.AppendLine($"- **{g.Key}** — {level}: " + string.Join("; ", g.Where(r => r.Envelope.Length > 0).Select(r => r.Envelope).Distinct()));
        }
        sb.AppendLine();
        sb.AppendLine($"독립 검증 통과 사례: {indep} / {results.Count}");
        sb.AppendLine();
        sb.AppendLine("## 공개 데이터로 대체할 수 없는 항목 (VV-51)");
        foreach (var s in NotReplaceable) sb.AppendLine($"- {s}");
        if (extraNotes is not null)
        {
            sb.AppendLine();
            sb.AppendLine("## 비고");
            foreach (var n in extraNotes) sb.AppendLine($"- {n}");
        }
        return sb.ToString();
    }

    /// <summary>사례별 보고서 + SUMMARY.md 를 outDir 에 쓴다.</summary>
    public static void WriteReports(IReadOnlyList<CaseResult> results, string root, string outDir, IEnumerable<string>? extraNotes = null)
    {
        System.IO.Directory.CreateDirectory(outDir);
        var enc = new UTF8Encoding(false);
        foreach (var r in results) File.WriteAllText(Path.Combine(outDir, r.Id + ".md"), CaseMarkdown(r), enc);
        File.WriteAllText(Path.Combine(outDir, "SUMMARY.md"), SummaryMarkdown(results, root, extraNotes), enc);
    }
}
