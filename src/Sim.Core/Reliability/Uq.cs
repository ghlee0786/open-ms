using System.Globalization;
using System.Text.Json.Nodes;
using Sim.Core.Analysis;
using Sim.Core.Engine;
using Sim.Core.Models;
using Sim.Core.Numerics;
using Sim.Core.Scenario;
using Sim.Core.Validation;

namespace Sim.Core.Reliability;

/// <summary>핵심 출력 정의. Model = 출력을 지배하는 모델(신뢰도 채점 연결), ModelForm = 모델 형태 불확도 키(없으면 미정량).
/// Inverse = 하중에 반비례하는 출력(안전계수) — 하중 모델 형태 배율을 나눈다. Probability = 0/1 사건(확률로 보고).</summary>
public sealed record UqOutputDef(string Key, string Label, string Unit, string Model, string? ModelForm = null, bool Inverse = false, bool Probability = false);

/// <summary>모델 형태 불확도: 공개 Validation 상대오차의 평균(편향)·표준편차(산포) — 출력 배율 (1+ε), ε ~ N(bias, sd).</summary>
public sealed record ModelFormStat(string Key, string Label, double Bias, double Sd, int N, string Evidence, bool AsParameter = false, string? ParameterPath = null);

public sealed record UqInput(string Path, string Grade, string Source, string Dist);

public sealed class UqOutputResult
{
    public required UqOutputDef Def { get; init; }
    public double Nominal { get; init; }
    public double NominalHalfDt { get; init; }
    public double NumericalU => double.IsFinite(Nominal) && double.IsFinite(NominalHalfDt) ? Math.Abs(Nominal - NominalHalfDt) : double.NaN;
    public double NumericalRel => Math.Abs(Nominal) > 1e-12 ? NumericalU / Math.Abs(Nominal) : double.NaN;
    public required double[] Samples { get; init; }
    public required double[] SamplesWithModelForm { get; init; }
    public ModelFormStat? ModelForm { get; init; }
    public double P05 { get; init; }
    public double P50 { get; init; }
    public double P95 { get; init; }
    public double Mean { get; init; }
    public double Sd { get; init; }
    /// <summary>입력 + 모델 형태 + 수치 불확도를 합친 90 % 구간(입력·모델 형태 MC 의 5–95 백분위를 수치 불확도만큼 넓힘).</summary>
    public double Lo90 { get; init; }
    public double Hi90 { get; init; }
    public double[] PrccValues { get; init; } = [];
    public double[] PValues { get; init; } = [];
    /// <summary>확률 출력: 추정 확률과 Wilson 95 % 구간.</summary>
    public double Prob { get; init; } = double.NaN;
    public double ProbLo { get; init; } = double.NaN;
    public double ProbHi { get; init; } = double.NaN;
    public int Valid => Samples.Count(double.IsFinite);
}

public sealed class UqResult
{
    public required string Scenario { get; init; }
    public required string Profile { get; init; }
    public required List<UqInput> Inputs { get; init; }
    public required List<UqOutputResult> Outputs { get; init; }
    public required List<ModelFormStat> ModelForms { get; init; }
    public required PedigreeSummary Pedigree { get; init; }
    public int Runs { get; init; }
    public int Errors { get; init; }
    public double Dt { get; init; }
    public double Seconds { get; init; }
    public ulong Seed { get; init; }
    public List<string> Notes { get; } = new();
}

public sealed class UqOptions
{
    public int Runs { get; init; } = 128;
    public ulong? Seed { get; init; }
    public string ValidationRoot { get; init; } = Path.Combine("validation", "public");
    public IProgress<int>? Progress { get; init; }
    public Action<string>? Log { get; init; }
    /// <summary>미리 계산한 공개 Validation 결과(없으면 ValidationRoot 에서 필요한 사례만 실행).</summary>
    public IReadOnlyList<CaseResult>? Validation { get; init; }
}

/// <summary>
/// 불확도 정량화(PRD FR-REL-03): ① 입력 불확도 — 출처 등급 분포(inputPedigree.dist) + 시나리오 분산으로 전체 6DOF MC(LHS)
/// ② 수치 불확도 — 공칭 dt 와 dt/2 의 차(차수 가정 없이 보수적으로 그대로) ③ 모델 형태 불확도 — 공개 Validation 상대오차(편향·산포)를
/// 출력 배율로(낙하산 개방하중) 또는 모델 계수 분포로(원통 횡류 항력) 반영 → 핵심 출력의 90 % 구간 + PRCC 민감도.
/// 프로필: opening-shock(시나리오 10) | operational(시나리오 09).
/// </summary>
public static class UqEngine
{
    public static readonly UqOutputDef[] OpeningShockOutputs =
    [
        new("chuteForce", "박스 낙하산 최대 하중", "N", "낙하산 충만·개방하중", "opening-load"),
        new("ck", "개방 하중 계수 Ck = F_max/(q_s·CdS)", "-", "낙하산 충만·개방하중", "opening-load"),
        new("cordTension", "브레이크 코드 최대 장력", "N", "정적 끈·브레이크 코드(인장 전용 스프링·파단)"),
        new("droneG", "드론 최대 G (박스 안, 마운트 경유)", "G", "박스 내부 하중 경로(cushionMount: 핀·폼 2질량)"),
        new("droneGAll", "드론 최대 G (테더 매달림 포함)", "G", "박스 내부 하중 경로(cushionMount: 핀·폼 2질량)"),
        new("boxG", "박스 최대 G", "G", "6DOF 다물체 + 사출 펄스·끈·낙하산"),
        new("minSF", "최소 항복 안전계수(전 부품)", "-", "부품 닫힌 식 응력(Roark·Winkler·러그) + 재질 라이브러리", "opening-load", Inverse: true),
    ];

    public static readonly UqOutputDef[] OperationalOutputs =
    [
        new("impactN", "센서1 착탄점 N", "m", "6DOF 전 구간(공력·낙하산·바람·드론 비행)"),
        new("impactE", "센서1 착탄점 E", "m", "6DOF 전 구간(공력·낙하산·바람·드론 비행)"),
        new("containerRate", "컨테이너 최대 각속도(노출→주낙하산)", "deg/s", "원통 횡류 항력(TR R-300) + 감쇠 미계수 + 6DOF"),
        new("drogueAoA", "드로그 전개 순간 컨테이너 총받음각", "deg", "원통 횡류 항력(TR R-300) + 감쇠 미계수 + 6DOF"),
        new("drogueForce", "드로그 최대 하중", "N", "낙하산 충만·개방하중", "opening-load"),
        new("mainForce", "주낙하산 최대 하중", "N", "낙하산 충만·개방하중", "opening-load"),
        new("boxChuteForce", "박스1 낙하산 최대 하중", "N", "낙하산 충만·개방하중", "opening-load"),
        new("anomaly", "잔해 간섭 전개 이상 발생", "-", "잔해 궤적 + 캐노피 영역 간섭 판정 + 전개 이상 계수", Probability: true),
    ];

    public static UqOutputDef[] OutputsFor(string profile) => profile switch
    {
        "opening-shock" => OpeningShockOutputs,
        "operational" => OperationalOutputs,
        _ => throw new InvalidDataException($"uq.profile: opening-shock | operational ('{profile}')"),
    };

    // ---------------- 출력 추출 ----------------

    private static double EventTime(World w, string? name) => name is not null && w.FiredEvents.TryGetValue(name, out var t) ? t : double.NaN;

    public static Dictionary<string, double> Extract(string profile, World w, JsonObject uq)
    {
        var o = new Dictionary<string, double>();
        if (profile == "opening-shock")
        {
            var r = OpeningShock.Analyze(w);
            var ms = r.MountScalars.FirstOrDefault();
            var ch = r.Chutes.FirstOrDefault();
            o["chuteForce"] = ch?.MaxForce ?? double.NaN;
            o["ck"] = ch?.OpeningFactor ?? double.NaN;
            o["cordTension"] = r.Cords.FirstOrDefault()?.MaxTension ?? double.NaN;
            o["droneG"] = ms?.PayloadGInBox ?? double.NaN;
            o["droneGAll"] = ms?.PayloadG ?? double.NaN;
            o["boxG"] = ms?.BaseG ?? double.NaN;
            o["minSF"] = r.Overall.Count > 0 ? r.MinSafety : double.NaN;
            foreach (var ((_, part), pk) in r.Overall) o[$"sf:{part}"] = pk.Safety;
            return o;
        }
        // operational
        var impact = w.GetBody(J.Str(uq, "impactBody", "sensor1"));
        bool landed = impact.Root.Mode == BodyMode.Landed;
        o["impactN"] = landed ? impact.OwnCgWorld.X : double.NaN;
        o["impactE"] = landed ? impact.OwnCgWorld.Y : double.NaN;
        var cb = w.GetBody(J.Str(uq, "tumblingBody", "container"));
        double t0 = EventTime(w, J.StrOpt(uq, "from")), t1 = EventTime(w, J.StrOpt(uq, "to"));
        if (!double.IsFinite(t1)) t1 = double.PositiveInfinity;
        double maxRate = double.NaN;
        if (double.IsFinite(t0) && w.Recorder.Frames.Count > 0)
        {
            maxRate = 0;
            foreach (var f in w.Recorder.Frames)
                if (f.T >= t0 - 1e-9 && f.T <= t1 + 1e-9 && f.Bodies[cb.Index].Mode == BodyMode.Free)
                    maxRate = Math.Max(maxRate, f.Bodies[cb.Index].Omega.Norm * 180 / Math.PI);
        }
        o["containerRate"] = maxRate;
        double td = EventTime(w, "lid_drogue");
        o["drogueAoA"] = double.IsFinite(td) && w.Recorder.Frames.Count > 0
            ? OperationalMetrics.TotalAoaDeg(w.Recorder.Frames[w.Recorder.IndexAt(td)].Bodies[cb.Index]) : double.NaN;
        var chutes = (uq["chutes"] as JsonArray)?.Select(x => x!.GetValue<string>()).ToList() ?? new List<string>();
        string[] keys = ["drogueForce", "mainForce", "boxChuteForce"];
        for (int k = 0; k < keys.Length; k++)
        {
            double f = double.NaN;
            if (k < chutes.Count)
            {
                var parts = chutes[k].Split('.');
                var p = w.GetBody(parts[0]).Devices.OfType<Parachute>().FirstOrDefault(d => d.Name == parts[1]);
                if (p is not null && !double.IsNaN(p.DeployTime)) f = p.MaxForce;
            }
            o[keys[k]] = f;
        }
        o["anomaly"] = (w.Collisions?.AnomalyCount ?? 0) > 0 ? 1 : 0;
        return o;
    }

    // ---------------- 모델 형태 ----------------

    private static IEnumerable<double> RelErrors(IEnumerable<CaseResult> cases, Func<PointResult, bool> sel) =>
        cases.SelectMany(c => c.Points).Where(sel).Select(p => p.RelError).Where(double.IsFinite);

    /// <summary>공개 Validation 결과에서 모델 형태 불확도 통계를 만든다(근거 사례 이름 포함).</summary>
    public static List<ModelFormStat> ModelForms(IReadOnlyList<CaseResult> val)
    {
        var list = new List<ModelFormStat>();
        CaseResult? Get(string id) => val.FirstOrDefault(c => c.Id == id);
        // ① 낙하산 개방하중: 독립 검증 점(보정점 제외) — Cruz DGB(대형) + NOLTR 소형 십자형(투과도별 k0 보정 후)
        var open = new[] { Get("parachute_dgb_opening_cruz2003"), Get("parachute_cross40in_opening_noltr73157") }.Where(c => c is not null).Select(c => c!).ToList();
        var eo = RelErrors(open, p => p.Judged && p.Role == "validation" && p.Unit == "N").ToList();
        if (eo.Count >= 3)
            list.Add(new ModelFormStat("opening-load", "낙하산 개방 최대 하중", eo.Average(), Sd(eo), eo.Count,
                $"{string.Join(", ", open.Select(c => c.Id))} 검증 점 {eo.Count}개 상대오차"));
        // ①' 사전 예측(계수 전이) 오차 — 보고용
        var pri = Get("parachute_cross40in_opening_noltr73157_apriori");
        if (pri is not null)
        {
            var ep = RelErrors([pri], p => p.Role == "validation" && p.Unit == "N").ToList();
            if (ep.Count > 0)
                list.Add(new ModelFormStat("opening-load-apriori", "낙하산 개방 최대 하중(대형 DGB k0 를 소형 십자형에 그대로)", ep.Average(), Sd(ep), ep.Count,
                    $"{pri.Id} — 투과도 미상 시 참고(입력 k0 범위로 이미 분산)"));
        }
        // ② 원통 횡류 2-D CD: 표 I(a) 원점 잔차(재작도·산포) ⊕ 유한 길이 η 불확도 ±0.08/0.63(README 판단) → 컨테이너 공력 배율 분포
        var cyl = Get("drag_cylinder_crossflow_mach_tr_r300");
        if (cyl is not null)
        {
            var ec = RelErrors([cyl], p => p.Role == "info").ToList();
            if (ec.Count > 0)
            {
                double s = Math.Sqrt(Sd(ec) * Sd(ec) + Math.Pow(0.08 / 0.63, 2));
                list.Add(new ModelFormStat("crossflow-drag", "원통 횡류 CD(Re, M)·η(L/D)", 0, s, ec.Count,
                    string.Format(CultureInfo.InvariantCulture, "{0} 표 I(a) 원점 {1}개 잔차 RMS {2:P1} ⊕ η(L/D) ±12.7 %(TN 2960 아임계 곡선 전이, 판단)",
                        cyl.Id, ec.Count, Math.Sqrt(ec.Average(x => x * x)))) { AsParameter = true, ParameterPath = "bodies.container.aero.scale" });
            }
        }
        return list;
    }

    private static double Sd(IReadOnlyList<double> v)
    {
        if (v.Count < 2) return 0;
        double m = v.Average();
        return Math.Sqrt(v.Sum(x => (x - m) * (x - m)) / (v.Count - 1));
    }

    public static IReadOnlyList<CaseResult> RunValidationSubset(string root, Action<string>? log = null)
    {
        var ids = new[] { "parachute_dgb_opening_cruz2003", "parachute_cross40in_opening_noltr73157", "parachute_cross40in_opening_noltr73157_apriori",
                          "drag_cylinder_crossflow_mach_tr_r300" };
        var list = new List<CaseResult>();
        foreach (var id in ids)
        {
            var f = Path.Combine(root, id, "case.json");
            if (!File.Exists(f)) { log?.Invoke($"모델 형태 근거 사례 없음: {f}"); continue; }
            list.Add(PublicValidation.RunCase(f));
        }
        return list;
    }

    // ---------------- 실행 ----------------

    public static UqResult Run(ScenarioDoc doc, UqOptions opt)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var uq = doc.Root["uq"] as JsonObject ?? throw new InvalidDataException("시나리오에 uq 절이 없습니다(profile: opening-shock | operational).");
        string profile = J.StrReq(uq, "profile", "uq");
        var defs = OutputsFor(profile);
        ulong seed = opt.Seed ?? (ulong)J.Num(uq, "seed", 1);
        var val = opt.Validation ?? RunValidationSubset(opt.ValidationRoot, opt.Log);
        var mfs = ModelForms(val);
        var pedSummary = InputPedigree.Summarize(doc.Root);

        // 입력 분포 = 시나리오 분산 + 출처 등급 분포 (+ 모델 형태 계수 분포)
        var merged = InputPedigree.MergedDispersions(doc.Root);
        var work = doc.Clone();
        var inputs = new List<UqInput>();
        var dispArr = new JsonArray();
        foreach (var (d, g, s) in merged)
        {
            dispArr.Add(DispJson(d));
            inputs.Add(new UqInput(d.Path, g, s, DistText(d)));
        }
        foreach (var mf in mfs.Where(m => m.AsParameter && m.ParameterPath is not null))
        {
            if (!PathExists(work.Root, mf.ParameterPath!)) continue;   // 그 모델을 쓰는 물체가 없는 시나리오
            EnsureNumber(work.Root, mf.ParameterPath!, 1.0);
            var d = new DispersionDef { Path = mf.ParameterPath!, Dist = "truncnormal", Mode = "set", Mean = 1, Sd = mf.Sd, Min = Math.Max(0.1, 1 - 3 * mf.Sd), Max = 1 + 3 * mf.Sd };
            dispArr.Add(DispJson(d));
            inputs.Add(new UqInput(d.Path, "모델형태", mf.Evidence, DistText(d)));
        }
        work.Root["dispersions"] = dispArr;

        bool record = profile == "operational";
        var perRun = new Dictionary<string, double>?[opt.Runs];
        string body = profile == "operational" ? J.Str(uq, "impactBody", "sensor1") : FirstBody(doc);
        var mc = MonteCarlo.Run(work, opt.Runs, body, "lhs", seed, opt.Progress, inspect: (i, w) => perRun[i] = Extract(profile, w, uq), record: record);

        // 수치 불확도: 공칭 dt vs dt/2
        double dt = J.Num(doc.Root["sim"] as JsonObject ?? new JsonObject(), "dt", 0.001);
        var nom = RunOnce(doc, null, profile, uq);
        var half = RunOnce(doc, dt / 2, profile, uq);
        opt.Log?.Invoke(string.Format(CultureInfo.InvariantCulture, "공칭 dt {0} s · dt/2 실행 완료, MC {1}회 (오류 {2})", dt, opt.Runs, mc.Runs.Count(r => r.Error is not null)));

        // 부품별 SF 출력(opening-shock): 공칭 최약 3 개
        var allDefs = defs.ToList();
        if (profile == "opening-shock")
            foreach (var (k, _) in nom.Where(kv => kv.Key.StartsWith("sf:", StringComparison.Ordinal) && double.IsFinite(kv.Value)).OrderBy(kv => kv.Value).Take(3))
                allDefs.Add(new UqOutputDef(k, PartLabel(doc, k[3..]), "-", "부품 닫힌 식 응력(Roark·Winkler·러그) + 재질 라이브러리", "opening-load", Inverse: true));

        var rng = new Rng(seed ^ 0x5DEECE66DUL);
        var okIdx = Enumerable.Range(0, opt.Runs).Where(i => mc.Runs[i].Error is null && perRun[i] is not null).ToList();
        var x = okIdx.Select(i => mc.Runs[i].Values).ToList();
        var outputs = new List<UqOutputResult>();
        var notesNa = new List<string>();
        foreach (var def in allDefs)
        {
            var ys = okIdx.Select(i => perRun[i]!.TryGetValue(def.Key, out var v) ? v : double.NaN).ToArray();
            var mf = def.ModelForm is { } mk ? mfs.FirstOrDefault(m => m.Key == mk) : null;
            var ymf = ys.Select(y =>
            {
                if (mf is null || !double.IsFinite(y)) return y;
                double e = mf.Bias + mf.Sd * Stat.NormInv(Math.Clamp(rng.NextDouble(), 1e-9, 1 - 1e-9));
                double f = Math.Max(1 + e, 0.05);
                return def.Inverse ? y / f : y * f;
            }).ToArray();
            double n0 = nom.GetValueOrDefault(def.Key, double.NaN), nh = half.GetValueOrDefault(def.Key, double.NaN);
            // 시나리오에 해당 없는 출력(예: 박스가 없는 시나리오 09 의 '박스1 낙하산 하중')은 공칭·표본 모두 NaN → 등록하지 않는다
            if (!double.IsFinite(n0) && ys.All(v => !double.IsFinite(v))) { notesNa.Add(def.Label); continue; }
            var fin = ys.Where(double.IsFinite).OrderBy(v => v).ToList();
            var finMf = ymf.Where(double.IsFinite).OrderBy(v => v).ToList();
            // PRCC: 유한 표본만
            var keep = Enumerable.Range(0, ys.Length).Where(k => double.IsFinite(ys[k])).ToList();
            var pr = def.Probability ? new double[x.FirstOrDefault()?.Length ?? 0] : Prcc.Compute(keep.Select(k => x[k]).ToList(), keep.Select(k => ys[k]).ToList());
            var pv = pr.Select(r => Prcc.PValue(r, keep.Count, pr.Length)).ToArray();
            double u = double.IsFinite(n0) && double.IsFinite(nh) ? Math.Abs(n0 - nh) : 0;
            double prob = double.NaN, plo = double.NaN, phi = double.NaN;
            if (def.Probability && fin.Count > 0)
            {
                int k = fin.Count(v => v > 0.5);
                prob = k / (double)fin.Count;
                (plo, phi) = Wilson(k, fin.Count);
            }
            outputs.Add(new UqOutputResult
            {
                Def = def, Nominal = n0, NominalHalfDt = nh, Samples = ys, SamplesWithModelForm = ymf, ModelForm = mf,
                P05 = OpeningShock.Percentile(fin, 0.05), P50 = OpeningShock.Percentile(fin, 0.5), P95 = OpeningShock.Percentile(fin, 0.95),
                Mean = fin.Count > 0 ? fin.Average() : double.NaN, Sd = Sd(fin),
                Lo90 = OpeningShock.Percentile(finMf, 0.05) - u, Hi90 = OpeningShock.Percentile(finMf, 0.95) + u,
                PrccValues = pr, PValues = pv, Prob = prob, ProbLo = plo, ProbHi = phi,
            });
        }
        sw.Stop();
        var res = new UqResult
        {
            Scenario = doc.Name, Profile = profile, Inputs = inputs, Outputs = outputs, ModelForms = mfs, Pedigree = pedSummary,
            Runs = opt.Runs, Errors = mc.Runs.Count(r => r.Error is not null), Dt = dt, Seconds = sw.Elapsed.TotalSeconds, Seed = seed,
        };
        foreach (var e in mc.Runs.Where(r => r.Error is not null).Take(3)) res.Notes.Add($"MC 오류 예: 실행 {e.Index} — {e.Error}");
        if (notesNa.Count > 0) res.Notes.Add($"이 시나리오에 해당 없는 출력(값 없음)은 등록하지 않음: {string.Join(", ", notesNa)}");
        return res;
    }

    private static string FirstBody(ScenarioDoc doc) =>
        (doc.Root["bodies"] as JsonArray)!.OfType<JsonObject>().Select(b => J.Str(b, "name", "")).First(n => n != "");

    private static string PartLabel(ScenarioDoc doc, string part)
    {
        foreach (var b in (doc.Root["bodies"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            foreach (var d in (b["devices"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                foreach (var p in (d["parts"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    if (J.Str(p, "name", "") == part) return "SF: " + J.Str(p, "label", part);
        return "SF: " + part;
    }

    private static bool PathExists(JsonObject root, string path)
    {
        var parent = path[..path.LastIndexOf('.')];
        try { return JsonPath.Resolve(root, parent, out _, out _) is JsonObject; }
        catch (KeyNotFoundException) { return false; }
    }

    private static void EnsureNumber(JsonObject root, string path, double v)
    {
        if (JsonPath.GetNumberSafe(root, path) is null) JsonPath.SetNumber(root, path, v);
    }

    private static Dictionary<string, double> RunOnce(ScenarioDoc doc, double? dt, string profile, JsonObject uq)
    {
        var d = doc.Clone();
        if (dt is { } h)
        {
            var sim = d.Root["sim"] as JsonObject ?? new JsonObject();
            sim["dt"] = h;
            d.Root["sim"] = sim;
        }
        var w = d.Build();
        w.Recorder.Enabled = profile == "operational";
        w.Run();
        return Extract(profile, w, uq);
    }

    private static JsonObject DispJson(DispersionDef d)
    {
        var o = new JsonObject { ["path"] = d.Path, ["dist"] = d.Dist, ["mode"] = d.Mode };
        if (d.Mean is { } m) o["mean"] = m;
        if (d.Dist is "normal" or "truncnormal") o["sd"] = d.Sd;
        if (d.Dist is "uniform" or "loguniform" or "truncnormal") { o["min"] = d.Min; o["max"] = d.Max; }
        if (d.Dist == "choice") o["values"] = new JsonArray(d.Values.Select(v => (JsonNode)v).ToArray());
        return o;
    }

    public static string DistText(DispersionDef d)
    {
        var inv = CultureInfo.InvariantCulture;
        string m = d.Mode switch { "scale" => "×", "add" => "+", _ => "=" };
        return d.Dist switch
        {
            "uniform" => string.Format(inv, "{0} U[{1:G4}, {2:G4}]", m, d.Min, d.Max),
            "loguniform" => string.Format(inv, "{0} logU[{1:G4}, {2:G4}]", m, d.Min, d.Max),
            "normal" => string.Format(inv, "{0} N({1:G4}, {2:G4})", m, d.Mean ?? (d.Mode == "scale" ? 1 : 0), d.Sd),
            "truncnormal" => string.Format(inv, "{0} N({1:G4}, {2:G4}) ∩ [{3:G4}, {4:G4}]", m, d.Mean ?? (d.Mode == "scale" ? 1 : 0), d.Sd, d.Min, d.Max),
            _ => $"{m} {d.Dist}",
        };
    }

    /// <summary>이항 비율 Wilson 95 % 구간.</summary>
    public static (double Lo, double Hi) Wilson(int k, int n)
    {
        if (n == 0) return (double.NaN, double.NaN);
        double z = 1.959964, p = k / (double)n, z2 = z * z;
        double c = (p + z2 / (2 * n)) / (1 + z2 / n);
        double h = z * Math.Sqrt(p * (1 - p) / n + z2 / (4.0 * n * n)) / (1 + z2 / n);
        return (Math.Max(0, c - h), Math.Min(1, c + h));
    }
}
