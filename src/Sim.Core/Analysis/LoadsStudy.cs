using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Sim.Core.Engine;
using Sim.Core.Fea;
using Sim.Core.Models;
using Sim.Core.Numerics;
using Sim.Core.Reliability;
using Sim.Core.Scenario;
using Sim.Core.Structures;

namespace Sim.Core.Analysis;

public sealed class LoadsStudyOptions
{
    public required string OutDir { get; init; }
    public string? RawDir { get; init; }
    public string PartsPath { get; init; } = Path.Combine("config", "models", "payload_parts.json");
    public int Runs { get; init; } = 128;
    public ulong Seed { get; init; } = 2742;
    public bool Ccx { get; init; }
    public string? CcxInstall { get; init; }
    /// <summary>CalculiX 스레드 상한(동시 실행은 항상 1 — 다른 계산과 CPU 공유, PLAN 결정).</summary>
    public int CcxThreads { get; init; } = 4;
    public string? WorkDir { get; init; }
    public string? EnvelopePath { get; init; } = Path.Combine("validation", "reports", "tumbling09", "envelope.json");
    public string? MaterialResultsPath { get; init; } = Path.Combine("validation", "reports", "container_material", "material_results.json");
    public bool NoAero { get; init; }
    public int MaxParallel { get; init; } = Math.Clamp(System.Environment.ProcessorCount / 2, 1, 8);
    /// <summary>와류 흘림 변동 양력 C'_L(rms) 범위(Norberg 2003 리뷰 — 초임계 Re 0.05–0.3)·Strouhal 범위(0.20 공개 · 0.256 이 연구 3-D URANS 참고).</summary>
    public (double Lo, double Hi) ClRms { get; init; } = (0.05, 0.3);
    public (double Lo, double Hi) Strouhal { get; init; } = (0.20, 0.256);
    public Action<string>? Log { get; init; }
    public string Command { get; init; } = "";
    public string RealModelsNote { get; init; } = "";
    /// <summary>1-D 민감도 스윕(경로, 설명, 값들) — 기본: 노즈 공력 배율(재접촉 상대속도를 정하는 L1 형상 가정).</summary>
    /// <summary>힌지 Δt 수렴 점검 종료 시각 [s] — 기폭 4.0 s → 꼬리–컨테이너 첫 접촉(합성 4.157 · 실모델 4.18 s) 전, 회귀 테스트와 같은 구간. 0 = 생략.</summary>
    public double HingeCheckTEnd { get; init; } = 4.15;
    public List<(string Path, string Label, double[] Values)> Sweeps { get; init; } =
        [("bodies.nose.aero.scale", "노즈 공력 힘 배율(L1 평평한 원통 = 1, 오자이브 추정 0.35–0.5)", [0.35, 0.5, 0.75, 1.0])];
}

/// <summary>힌지 Δt 수렴 점검 한 줄: 강성 상한 방식(Fixed = 고정 기준 Δt, 수정 후)·Δt·힌지별 5 ms 평균/순간 최대 반력·사용 강성·이탈 시각·그 꽃잎(B)의 접촉 수.</summary>
public sealed record HingeConvRow(bool Fixed, double Dt, string Hinge, double AvgN, double PeakN, double K, double ReleaseT, int Contacts);

/// <summary>
/// 스윕 한 점의 결과. PartMargins = 판정 부품별 창 묶음(separation · opening = 드로그·주낙하산) 최소 여유(SF/기준).
/// …Tau = 같은 실행을 충격량 모델(접촉 지속 τ 공칭 반 사인)로 다시 평가한 값(순응 접촉 실행일 때 — 비교용, 아니면 NaN).
/// …Elastic = 탄성-소성 실행을 탄성 순응(힘 상한 없음)으로 다시 평가한 값, MaxIndentation = 소성 눌림 최대 [m](탄성-소성 실행일 때, 아니면 NaN).
/// </summary>
public sealed record LoadsSweepPoint(string Path, double Value, double ContainerSepG, double MaxJ, double MaxApproach, int Contacts,
    double LatchForce, double LatchDynG, string WorstPart, double WorstSf, string? Error,
    IReadOnlyDictionary<string, (double Separation, double Opening, double SeparationModes, double OpeningModes)>? PartMargins = null,
    double ContainerSepGTau = double.NaN, double WorstSfTau = double.NaN, string WorstPartTau = "",
    IReadOnlyDictionary<string, (double Separation, double Opening, double SeparationModes, double OpeningModes)>? PartMarginsTau = null,
    double ContainerSepGElastic = double.NaN, double WorstSfElastic = double.NaN, double MaxIndentation = double.NaN);

/// <summary>
/// `mns loads` — 수납물 브레이크 충격(부품별 G·하중·응력·항복 SF, SRS 동적 증폭, UQ MC) + 3분할 분리 후 컨테이너 외부 공력(CFD Cp → CalculiX 셸 정적·좌굴,
/// 보정·와류 동적 계수) + 조합 하중 + 요약 판정표 + 신뢰성(NASA-STD-7009A) → README.md · loads.json. 모든 수치는 이 명령의 실행 결과.
/// </summary>
public static class LoadsStudy
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static string F(double v, string f = "F1") => double.IsFinite(v) ? v.ToString(f, Inv) : "—";
    private static string Sf(double v) => double.IsPositiveInfinity(v) ? "∞" : double.IsFinite(v) ? (v >= 100 ? ">100" : v.ToString("F2", Inv)) : "—";

    public sealed class McStat
    {
        public List<double> DynG { get; } = new();
        public List<double> Force { get; } = new();
        public List<double> SfY { get; } = new();
        public List<double> SfU { get; } = new();
        public List<double> GMargin { get; } = new();
    }

    public sealed class Result
    {
        public required PayloadPartsConfig Config { get; init; }
        public required PayloadRun Nominal { get; init; }
        /// <summary>충격량 모델(가정 지속 τ 반 사인) 재평가 — τ 최소·최대(충격량 모델 실행이면 공칭 실행 자체가 τ 공칭).</summary>
        public PayloadRun? TauMin { get; set; }
        public PayloadRun? TauMax { get; set; }
        /// <summary>순응 접촉 실행: 같은 6DOF 실행을 충격량 모델 τ 공칭으로 다시 평가(이전 τ 가정 값 — 비교용). 충격량 모델이면 null.</summary>
        public PayloadRun? TauNom { get; set; }
        /// <summary>순응 접촉 실행: 직렬 강성 배율 범위 끝(펄스 재적분 — 운동량 교환은 같아 6DOF 재실행 없음).</summary>
        public PayloadRun? StiffLo { get; set; }
        public PayloadRun? StiffHi { get; set; }
        public (double Lo, double Hi) StiffRange { get; set; } = (0.5, 2);
        /// <summary>탄성-소성 실행: 같은 6DOF 실행을 탄성 순응(힘 상한 없음)으로 다시 평가(이전 탄성 순응 값 — 비교용). 아니면 null.</summary>
        public PayloadRun? Elastic { get; set; }
        public bool Compliant => Nominal.HasPulses;
        public bool Plastic => Nominal.HasPlasticPulses;
        public List<Fea.ContactFea.Row> ContactFeaRows { get; } = new();
        public Dictionary<string, McStat> Mc { get; } = new();
        public List<(string Path, string Grade, string Dist)> McInputs { get; } = new();
        public List<double[]> McX { get; } = new();
        public Dictionary<string, List<double>> McScalars { get; } = new();
        public int McErrors { get; set; }
        public List<string> McErrorSamples { get; } = new();
        public List<(PayloadPart Part, PartFea.Result Fea, double ForceNom, double ForceP95)> PartFea { get; } = new();
        public List<ContainerAeroFea.AlphaResult> Aero { get; } = new();
        public List<ContainerAeroFea.ComboResult> Combos { get; } = new();
        public List<double> ShellModes { get; } = new();
        public (double Fea, double Classical)? BuckleVerify { get; set; }
        public double ShedLo = double.NaN, ShedHi = double.NaN, ShedFs1 = double.NaN, ShedFs2 = double.NaN;
        /// <summary>진동 피로(근사): 와류 흘림 f_s 범위(St 범위) 대 셸 1차 f_n 근접 판정.</summary>
        public VibrationFatigue.Resonance? ShellResonance;
        public List<string> Notes { get; } = new();
        public List<LoadsSweepPoint> Sweep { get; } = new();
        public List<HingeConvRow> HingeConv { get; } = new();
        public double Seconds { get; set; }
        public string Markdown { get; set; } = "";
        /// <summary>컨테이너 튜브 재질(시나리오 container.structure.material — 없으면 CFRP 준등방)·두께: 공력 셸 판정 허용치·좌굴 E 환산.</summary>
        public ContainerMaterialStudy.Candidate Shell { get; set; } = ContainerMaterialStudy.CandidateOf("cfrp-qi");
        public double ShellThickness { get; set; } = 0.003;
    }

    /// <summary>
    /// 공력 셸 강도 SF(극한 1.5 기준으로 읽음): 복합재 min(Ftu/σ₁, Fcu/|σ₃|), 금속 min(Ftu/σ_vm, (Fty/σ_vm)·1.5/1.25)
    /// (항복 1.25 · 극한 1.5 를 한 수로 — 둘 다 만족하면 ≥ 1.5).
    /// </summary>
    public static double ShellStrengthSf(ContainerMaterialStudy.Candidate c, ContainerAeroFea.StaticField f) => c.Composite
        ? Math.Min(c.Ftu / Math.Max(f.S1Max, 1), c.Fcu / Math.Max(-f.S3Min, 1))
        : Math.Min(c.Ftu / Math.Max(f.VmMax, 1), c.Fty / Math.Max(f.VmMax, 1) * ContainerMaterialStudy.SfUltimate / 1.25);

    /// <summary>좌굴 λ 환산: FEA 는 기준 등방 E(ContainerAeroFea.RefMat)로 풀었으므로 실제 재질 E 비례.</summary>
    public static double ShellBuckleScale(ContainerMaterialStudy.Candidate c) => c.E / ContainerAeroFea.RefMat.E;

    private static (ContainerMaterialStudy.Candidate, double) ShellOf(ScenarioDoc doc)
    {
        foreach (var b in doc.Root["bodies"] as JsonArray ?? new JsonArray())
            if (b is JsonObject bo && J.Str(bo, "name", "") == "container" && bo["structure"] is JsonObject s && J.StrOpt(s, "material") is { } key
                && MaterialLibrary.Find(key) is not null)
                return (ContainerMaterialStudy.CandidateOf(key), J.Num(s, "thickness", 0.003));
        return (ContainerMaterialStudy.CandidateOf("cfrp-qi"), 0.003);
    }

    // ---------------- 실행 ----------------

    public static Result Generate(ScenarioDoc doc, LoadsStudyOptions o)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Directory.CreateDirectory(o.OutDir);
        void L(string s) => o.Log?.Invoke(s);
        var cfg = PayloadPartsConfig.Load(o.PartsPath);
        L($"부품 정의 {o.PartsPath}: {cfg.Parts.Count}개(스테이션 전개), 창 {cfg.Windows.Count}개");
        var nominal = PayloadShock.Run(doc, cfg, cfg.TauNominal, designModes: true);
        var res = new Result { Config = cfg, Nominal = nominal };
        (res.Shell, res.ShellThickness) = ShellOf(doc);
        bool pulses = nominal.HasPulses;
        res.TauMin = Retau(nominal, cfg, cfg.TauMin, false);
        res.TauMax = Retau(nominal, cfg, cfg.TauMax, false);
        if (pulses)
        {
            res.TauNom = Retau(nominal, cfg, cfg.TauNominal, false);
            res.StiffRange = StiffnessRange(doc);
            res.StiffLo = Retau(nominal, cfg, cfg.TauNominal, true, res.StiffRange.Lo);
            res.StiffHi = Retau(nominal, cfg, cfg.TauNominal, true, res.StiffRange.Hi);
            if (nominal.HasPlasticPulses)
            {
                res.Elastic = Retau(nominal, cfg, cfg.TauNominal, true, 1, elastic: true);
                L($"탄성-소성 접촉: 소성 접촉 {nominal.Probe.Impulses.Count(i => i.Pulse?.Plastic == true)}건 — 탄성 순응(힘 상한 없음) 비교 재평가");
            }
            L($"순응 접촉: 접촉 {nominal.Probe.Impulses.Count(i => i.Pulse is not null)}건 — 강성 배율 범위 ×{res.StiffRange.Lo:G3}–{res.StiffRange.Hi:G3} 재평가, 충격량 모델 τ {cfg.TauNominal * 1e3:G3} ms 비교 재평가");
        }
        L($"공칭 6DOF 실행 t = {nominal.World.T:F2} s, 창 {string.Join(", ", nominal.WindowSpans.Select(kv => $"{kv.Key} {kv.Value.T0:F2}–{kv.Value.T1:F2}"))}");
        RunMc(doc, cfg, o, res);
        RunSweeps(doc, cfg, o, res);
        RunHingeConvergence(doc, o, res);
        if (o.Ccx)
        {
            var inst = CalculixInstall.TryLocate(o.CcxInstall, out var why);
            if (inst is null) res.Notes.Add($"CalculiX 미실행: {why}");
            else
            {
                var work = Path.GetFullPath(o.WorkDir ?? Path.Combine("results", "loads_work"));
                if (pulses) RunContactFea(o, res, inst, Path.Combine(work, "contact"));
                RunPartFea(doc, cfg, o, res, inst, Path.Combine(work, "parts"));
                if (!o.NoAero) RunAero(doc, o, res, inst, Path.Combine(work, "aero"));
            }
        }
        else res.Notes.Add("CalculiX 미실행(--ccx 없음) — 부품 FEA 대조·컨테이너 CFD Cp 셸 해석 생략.");
        res.Seconds = sw.Elapsed.TotalSeconds;
        var json = ToJson(doc, o, res);
        File.WriteAllText(Path.Combine(o.OutDir, "loads.json"), json.ToJsonString(J.Pretty), new UTF8Encoding(false));
        WriteFigures(o.OutDir, res);
        res.Markdown = Markdown(doc, o, res);
        File.WriteAllText(Path.Combine(o.OutDir, "README.md"), res.Markdown, new UTF8Encoding(false));
        if (o.RawDir is { } raw)
        {
            Directory.CreateDirectory(raw);
            WriteProbeCsv(Path.Combine(raw, "probe_nominal.csv"), nominal.Probe);
        }
        return res;
    }

    /// <summary>같은 6DOF 실행을 다른 접촉력 이력으로 다시 평가: τ(충격량 모델 반 사인) · 순응 펄스 사용 여부 · 강성 배율.</summary>
    public static PayloadRun Retau(PayloadRun r, PayloadPartsConfig cfg, double tau, bool usePulses = true, double factor = 1, bool designModes = false, bool elastic = false)
    {
        var x = new PayloadRun { World = r.World, Probe = r.Probe, Tau = tau, UsePulses = usePulses, PulseFactor = factor, DesignModes = designModes || r.DesignModes, ElasticPulses = elastic };
        foreach (var kv in r.WindowSpans) x.WindowSpans[kv.Key] = kv.Value;
        foreach (var kv in r.Positions) x.Positions[kv.Key] = kv.Value;
        PayloadShock.Evaluate(x, cfg);
        return x;
    }

    /// <summary>순응 접촉 강성 배율 경로(시나리오 collisions.contact.stiffnessScale).</summary>
    public const string StiffnessPath = "collisions.contact.stiffnessScale";

    /// <summary>강성 배율 불확도 범위: 시나리오 분산(inputPedigree·dispersions)의 그 경로 min–max, 없으면 ×0.5–2(등급 D 닫힌 식 관례).</summary>
    public static (double Lo, double Hi) StiffnessRange(ScenarioDoc doc)
    {
        var d = InputPedigree.MergedDispersions(doc.Root).Select(m => m.Def).FirstOrDefault(x => x.Path == StiffnessPath);
        return d is { Dist: "uniform" or "loguniform" } && d.Min > 0 && d.Max >= d.Min ? (d.Min, d.Max) : (0.5, 2.0);
    }

    private static bool IsCompliant(ScenarioDoc doc) => doc.Root["collisions"]?["contact"] is JsonObject c && J.Str(c, "model", "impulse") == "compliant";

    /// <summary>
    /// 순응 접촉 강성 CalculiX 교차 확인: 공칭 분리 창 접촉(충격량 큰 순 최대 4건)의 양쪽 물체 중 얇은 벽 이상화(tube·panel)를 S8R 셸로 메시해
    /// 접촉점 패치 하중 + 수동 관성 완화 + 3-2-1 지지 → 강체 운동을 뺀 법선 변위 = 구조 컴플라이언스 → 닫힌 식(준정적 전부 · 포함 경로)과 비.
    /// </summary>
    private static void RunContactFea(LoadsStudyOptions o, Result res, CalculixInstall inst, string work)
    {
        var nom = res.Nominal;
        var span = nom.WindowSpans.GetValueOrDefault("separation");
        var imps = nom.Probe.Impulses.Where(i => i.Pulse is not null && i.T >= span.T0 && i.T < span.T1).OrderByDescending(i => i.J).Take(4).ToList();
        int k = 0;
        foreach (var im in imps)
        {
            foreach (var side in new[] { im.Pulse!.SideA, im.Pulse.SideB })
            {
                if (side.S is not { Kind: "tube" or "panel" } s) continue;
                o.Log?.Invoke($"CalculiX 접촉 강성: {s.Body} @ {im.T:F3} s ({im.Other})");
                var row = Fea.ContactFea.Run(s, side.P, side.N, side.W0, inst, Path.Combine(work, $"c{k++}_{s.Body}"), o.CcxThreads);
                double cAll = im.Pulse.Elements.Where(e => e.Body == s.Body).Sum(e => e.Compliance);
                double cInc = im.Pulse.Elements.Where(e => e.Body == s.Body && e.Included).Sum(e => e.Compliance);
                res.ContactFeaRows.Add(row with { Time = im.T, Other = im.Other, ClosedAll = cAll, ClosedIncluded = cInc });
                if (!row.Ok) res.Notes.Add($"접촉 강성 FEA 실패 {s.Body}: {row.Error}");
            }
        }
        if (imps.Count == 0) res.Notes.Add("접촉 강성 FEA 생략: 분리 창 순응 접촉 없음.");
    }

    private static void RunMc(ScenarioDoc doc, PayloadPartsConfig cfg, LoadsStudyOptions o, Result res)
    {
        if (o.Runs <= 0) return;
        var merged = InputPedigree.MergedDispersions(doc.Root);
        var defs = merged.Select(m => m.Def).ToList();
        foreach (var (d, g, _) in merged) res.McInputs.Add((d.Path, g, UqEngine.DistText(d)));
        // 접촉력 이력 불확도: 충격량 모델 = 가정 지속 τ U[min, max] (마지막 입력) · 순응 접촉 = 직렬 강성 배율(시나리오 분산에 없으면 logU[0.5, 2], 등급 D)
        bool compliant = IsCompliant(doc);
        if (compliant && defs.All(d => d.Path != StiffnessPath))
        {
            var sd = new DispersionDef { Path = StiffnessPath, Dist = "loguniform", Mode = "set", Min = 0.5, Max = 2 };
            defs.Add(sd);
            res.McInputs.Add((sd.Path, "D", UqEngine.DistText(sd)));
        }
        if (!compliant) res.McInputs.Add(("contactDuration_s", "D", string.Format(Inv, "= U[{0:G3}, {1:G3}] s", cfg.TauMin, cfg.TauMax)));
        var u = Sampler.Uniforms(o.Runs, defs.Count + (compliant ? 0 : 1), "lhs", o.Seed);
        var nominalValues = defs.Select(d => JsonPath.GetNumber(doc.Root, d.Path) ?? 0).ToArray();
        var perRun = new (Dictionary<string, (double G, double F, double SfY, double SfU, double Gm)> Parts, Dictionary<string, double> Scalars, double[] X)?[o.Runs];
        var errors = new string?[o.Runs];
        int done = 0;
        Parallel.For(0, o.Runs, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, o.MaxParallel) }, i =>
        {
            var x = new double[defs.Count + (compliant ? 0 : 1)];
            try
            {
                var d = doc.Clone();
                for (int k = 0; k < defs.Count; k++)
                {
                    x[k] = defs[k].Apply(nominalValues[k], defs[k].Sample(u[i][k]));
                    JsonPath.SetNumber(d.Root, defs[k].Path, x[k]);
                }
                double tau = cfg.TauNominal;
                if (!compliant) x[defs.Count] = tau = cfg.TauMin + (cfg.TauMax - cfg.TauMin) * u[i][defs.Count];
                var sim = d.Root["sim"] as JsonObject ?? new JsonObject();
                sim["seed"] = (double)(o.Seed * 1_000_003UL + (ulong)i);
                d.Root["sim"] = sim;
                var run = PayloadShock.Run(d, cfg, tau);
                var parts = new Dictionary<string, (double, double, double, double, double)>();
                foreach (var pr in run.Parts)
                {
                    if (pr.Windows.Count == 0) continue;
                    parts[pr.Part.Name] = (pr.Windows.Max(w => w.DynG), pr.Windows.Max(w => w.ForceN), pr.Windows.Select(w => w.SfYield).Where(double.IsFinite).DefaultIfEmpty(double.NaN).Min(),
                        pr.Windows.Select(w => w.SfUltimate).Where(double.IsFinite).DefaultIfEmpty(double.NaN).Min(), pr.Windows.Select(w => w.GMargin).Where(double.IsFinite).DefaultIfEmpty(double.NaN).Min());
                    foreach (var ww in pr.Windows) parts[$"{pr.Part.Name}|{ww.Window}"] = (ww.DynG, ww.ForceN, ww.SfYield, ww.SfUltimate, ww.GMargin);
                }
                var sc = Scalars(run);
                foreach (var (key, wins) in new[] { ("minMarginOpening", new[] { "drogue", "main" }), ("minMarginSeparation", new[] { "separation" }) })
                {
                    double mm = double.PositiveInfinity;
                    foreach (var pr in run.Parts.Where(p => p.Part.Judged))
                        foreach (var ww in pr.Windows.Where(v => wins.Contains(v.Window)))
                            mm = Math.Min(mm, Margin(cfg, pr.Part, ww.SfYield, ww.SfUltimate));
                    sc[key] = mm;
                }
                perRun[i] = (parts, sc, x);
            }
            catch (Exception ex) { errors[i] = ex.Message; }
            int n = Interlocked.Increment(ref done);
            if (n % 16 == 0 || n == o.Runs) o.Log?.Invoke($"MC {n}/{o.Runs}");
        });
        for (int i = 0; i < o.Runs; i++)
        {
            if (perRun[i] is not { } r) { res.McErrors++; if (res.McErrorSamples.Count < 3 && errors[i] is { } e) res.McErrorSamples.Add($"실행 {i}: {e}"); continue; }
            res.McX.Add(r.X);
            foreach (var (k, v) in r.Parts)
            {
                if (!res.Mc.TryGetValue(k, out var st)) res.Mc[k] = st = new McStat();
                st.DynG.Add(v.G); st.Force.Add(v.F); st.SfY.Add(v.SfY); st.SfU.Add(v.SfU); st.GMargin.Add(v.Gm);
            }
            foreach (var (k, v) in r.Scalars)
            {
                if (!res.McScalars.TryGetValue(k, out var l)) res.McScalars[k] = l = new List<double>();
                l.Add(v);
            }
        }
    }

    /// <summary>
    /// 힌지 반력 Δt 수렴 점검(1절): 공칭 시나리오를 HingeCheckTEnd 까지 Δt, Δt/2, Δt/4 로 다시 돌려 5 ms 평균 반력·이탈 시각 비교.
    /// 강성 상한 방식 두 가지 — 고정 기준 Δt(수정 후, 힌지 stabilityDt 기본) · 현재 Δt(수정 전, stabilityDt = 0).
    /// </summary>
    private static void RunHingeConvergence(ScenarioDoc doc, LoadsStudyOptions o, Result res)
    {
        if (!(o.HingeCheckTEnd > 0)) return;
        double dt0 = JsonPath.GetNumber(doc.Root, "sim.dt") ?? 0.001;
        var jobs = new (bool Fixed, int Div)[] { (true, 1), (true, 2), (true, 4), (false, 1), (false, 2), (false, 4) };
        var rows = new List<HingeConvRow>?[jobs.Length];
        Parallel.For(0, jobs.Length, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, o.MaxParallel) }, i =>
        {
            var (fix, div) = jobs[i];
            var d = doc.Clone();
            JsonPath.SetNumber(d.Root, "sim.dt", dt0 / div);
            JsonPath.SetNumber(d.Root, "sim.tEnd", o.HingeCheckTEnd);
            if (!fix && d.Root["connectors"] is JsonArray cs)
                foreach (var c in cs.OfType<JsonObject>().Where(c => J.Str(c, "type", "") == "hinge")) c["stabilityDt"] = 0.0;
            var w = d.Build();
            w.Run();
            var recs = w.Collisions?.Records.Where(r => r.Kind == "contact").ToList() ?? [];
            rows[i] = w.Connectors.OfType<Hinge>().Select(h => new HingeConvRow(fix, dt0 / div, h.Name, h.MaxForceAvg, h.MaxForce, h.EffectiveStiffness, h.ReleaseTime,
                recs.Count(r => r.A == h.B.Name || r.B == h.B.Name))).ToList();
        });
        foreach (var r in rows) if (r is not null) res.HingeConv.AddRange(r);
    }

    private static void RunSweeps(ScenarioDoc doc, PayloadPartsConfig cfg, LoadsStudyOptions o, Result res)
    {
        var jobs = o.Sweeps.SelectMany(sw => sw.Values.Select(v => (sw.Path, v))).ToList();
        var pts = new LoadsSweepPoint?[jobs.Count];
        Parallel.For(0, jobs.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, o.MaxParallel) }, i =>
        {
            var (path, v) = jobs[i];
            try
            {
                var d = doc.Clone();
                JsonPath.SetNumber(d.Root, path, v);
                var run = PayloadShock.Run(d, cfg, cfg.TauNominal, designModes: true);
                var sp = run.WindowSpans.GetValueOrDefault("separation");
                double sepG = PayloadShock.ContainerPeak(run, sp.T0, sp.T1).G;
                var recs = run.World.Collisions?.Records.Where(r => r.Kind == "contact" && (r.A == cfg.Root || r.B == cfg.Root)).ToList() ?? [];
                var lp = run.Parts.FirstOrDefault(p => p.Part.Name == "latch_pin1");
                static (PartResult? P, double Sf) Worst(PayloadRun run, PayloadPartsConfig cfg) => run.Parts.Where(p => p.Part.Judged)
                    .Select(p => ((PartResult?)p, p.Part.Material!.Brittle ? p.MinSfUltimate / cfg.SfUltimate : Math.Min(p.MinSfYield / cfg.SfYield, p.MinSfUltimate / cfg.SfUltimate)))
                    .OrderBy(x => x.Item2).FirstOrDefault();
                var worst = Worst(run, cfg);
                // 순응 접촉 실행이면 같은 실행을 충격량 모델(τ 공칭 반 사인)로 다시 평가 — 이전 τ 가정 값 비교
                PayloadRun? tauRun = run.HasPulses ? Retau(run, cfg, cfg.TauNominal, false) : null;
                var wt = tauRun is null ? default : Worst(tauRun, cfg);
                // 탄성-소성 실행이면 탄성 순응(힘 상한 없음)으로도 — 이전 탄성 순응 값 비교
                PayloadRun? elRun = run.HasPlasticPulses ? Retau(run, cfg, cfg.TauNominal, true, 1, elastic: true) : null;
                var we = elRun is null ? default : Worst(elRun, cfg);
                double dent = run.HasPlasticPulses ? run.Probe.Impulses.Where(x => x.Pulse is not null && x.T >= sp.T0 && x.T < sp.T1).Select(x => x.Pulse!.Indentation).DefaultIfEmpty(0).Max() : double.NaN;
                pts[i] = new LoadsSweepPoint(path, v, sepG, run.Probe.Impulses.Select(x => x.J).DefaultIfEmpty(0).Max(), recs.Select(r => r.RelSpeed).DefaultIfEmpty(0).Max(), recs.Count,
                    lp?.Windows.Select(w => w.ForceN).DefaultIfEmpty(0).Max() ?? double.NaN, lp?.Windows.Select(w => w.DynG).DefaultIfEmpty(0).Max() ?? double.NaN,
                    worst.P?.Part.Label ?? "", worst.P is null ? double.NaN : worst.Sf, null, PartMargins(run, cfg),
                    tauRun is null ? double.NaN : PayloadShock.ContainerPeak(tauRun, sp.T0, sp.T1).G, wt.P is null ? double.NaN : wt.Sf, wt.P?.Part.Label ?? "",
                    tauRun is null ? null : PartMargins(tauRun, cfg),
                    elRun is null ? double.NaN : PayloadShock.ContainerPeak(elRun, sp.T0, sp.T1).G, we.P is null ? double.NaN : we.Sf, dent);
            }
            catch (Exception ex) { pts[i] = new LoadsSweepPoint(path, v, double.NaN, double.NaN, double.NaN, 0, double.NaN, double.NaN, "", double.NaN, ex.Message); }
        });
        res.Sweep.AddRange(pts.Where(x => x is not null)!);
    }

    /// <summary>
    /// 판정 부품별(참고 항목 포함 — 표기는 reference 로 구분) 분리 창 · 개방 창(드로그·주낙하산) 최소 여유(SF/기준) — 장착 fₙ ×0.5–2 포락과
    /// 설계 FEA 고유진동수 범위(…Modes, run.DesignModes 일 때 탑재 부품). 창이 없으면 NaN.
    /// </summary>
    public static Dictionary<string, (double Separation, double Opening, double SeparationModes, double OpeningModes)> PartMargins(PayloadRun run, PayloadPartsConfig cfg)
    {
        var d = new Dictionary<string, (double, double, double, double)>();
        foreach (var pr in run.Parts.Where(p => p.Part.HasStress))
        {
            double M(bool modes, params string[] wins) => pr.Windows.Where(w => wins.Contains(w.Window))
                .Select(w => modes ? (double.IsNaN(w.DynModesG) ? double.NaN : Margin(cfg, pr.Part, w.SfYieldModes, w.SfUltimateModes)) : Margin(cfg, pr.Part, w.SfYield, w.SfUltimate))
                .Where(x => !double.IsNaN(x)).DefaultIfEmpty(double.NaN).Min();
            d[pr.Part.Name] = (M(false, "separation"), M(false, "drogue", "main"), M(true, "separation"), M(true, "drogue", "main"));
        }
        return d;
    }

    /// <summary>기준 대비 여유 = min(SF_y/기준_y, SF_u/기준_u) (취성은 극한만).</summary>
    public static double Margin(PayloadPartsConfig cfg, PayloadPart p, double sfy, double sfu)
    {
        if (!p.HasStress) return double.NaN;
        double u = double.IsFinite(sfu) ? sfu / cfg.SfUltimate : double.PositiveInfinity;
        if (p.Material!.Brittle) return u;
        double y = double.IsFinite(sfy) ? sfy / cfg.SfYield : double.PositiveInfinity;
        return Math.Min(u, y);
    }

    /// <summary>MC 출력별 PRCC 유의 입력(|PRCC| ≥ 0.2, p &lt; 0.05) — 신뢰도 채점(입력 출처 요인)용.</summary>
    public static List<(string Output, string Path, string Grade, double Prcc)> Sensitivity(Result r, IEnumerable<(string Key, IReadOnlyList<double> Y)> outputs)
    {
        var list = new List<(string, string, string, double)>();
        if (r.McX.Count < 20) return list;
        foreach (var (key, y) in outputs)
        {
            if (y.Count != r.McX.Count) continue;
            var idx = Enumerable.Range(0, y.Count).Where(i => double.IsFinite(y[i])).ToList();
            if (idx.Count < 20) continue;
            var pr = Prcc.Compute(idx.Select(i => r.McX[i]).ToList(), idx.Select(i => y[i]).ToList());
            for (int j = 0; j < pr.Length && j < r.McInputs.Count; j++)
            {
                double pv = Prcc.PValue(pr[j], idx.Count, pr.Length);
                if (Math.Abs(pr[j]) >= 0.2 && pv < 0.05) list.Add((key, r.McInputs[j].Path, r.McInputs[j].Grade, pr[j]));
            }
        }
        return list;
    }

    /// <summary>실행 하나의 스칼라 지표(MC 집계·PRCC): 창별 컨테이너 G, 낙하산 최대 하중, 힌지 5 ms 평균 반력, 재접촉 최대 충격량.</summary>
    public static Dictionary<string, double> Scalars(PayloadRun run)
    {
        var d = new Dictionary<string, double>();
        foreach (var (id, (t0, t1)) in run.WindowSpans) d[$"containerG:{id}"] = PayloadShock.ContainerPeak(run, t0, t1).G;
        foreach (var b in run.World.Bodies)
            foreach (var dev in b.Devices)
                if (dev is Parachute p && b.Name == run.Probe.RootName) d[$"chuteForce:{p.Name}"] = p.MaxForce;
        foreach (var h in run.World.Connectors.OfType<Hinge>()) d[$"hingeAvg:{h.Name}"] = h.MaxForceAvg;
        d["recontactMaxJ"] = run.Probe.Impulses.Select(i => i.J).DefaultIfEmpty(0).Max();
        return d;
    }

    private static double P(IEnumerable<double> v, double q)
    {
        var s = v.Where(double.IsFinite).OrderBy(x => x).ToList();
        return OpeningShock.Percentile(s, q);
    }

    // ---------------- CalculiX 부품 ----------------

    private static string? RealDir(ScenarioDoc doc)
    {
        var env = Scenario.ScenarioLoader.RealModelsEnv();
        if (string.Equals(env, "synthetic", StringComparison.OrdinalIgnoreCase)) return null;
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env)) return env;
        if (doc.Root["realModels"] is JsonObject rm && J.StrOpt(rm, "dir") is { } d)
        {
            var full = Path.GetFullPath(Path.Combine(doc.BaseDir, d));
            if (Directory.Exists(full)) return full;
        }
        return null;
    }

    private static void RunPartFea(ScenarioDoc doc, PayloadPartsConfig cfg, LoadsStudyOptions o, Result res, CalculixInstall inst, string work)
    {
        var real = RealDir(doc);
        if (real is null) { res.Notes.Add("부품 FEA 생략: 실모델 STEP 폴더 없음(--real-models)."); return; }
        // 같은 형상(스테이션별 같은 바디)은 한 번만: 최대 하중 스테이션 기준
        var groups = cfg.Parts.Where(p => p.Ccx is not null).GroupBy(p => p.Ccx!.ToJsonString());
        foreach (var g in groups)
        {
            var worst = g.MaxBy(p => res.Nominal.Parts.First(x => x.Part == p).Windows.Select(w => w.ForceN).DefaultIfEmpty(0).Max())!;
            var spec = PartFea.Parse(worst.Ccx!, worst.Name);
            o.Log?.Invoke($"CalculiX 부품: {worst.Label} (STEP {spec.Step} 바디 {spec.Body})");
            var r = PartFea.Run(spec, real, worst.Material!, inst, Path.Combine(work, worst.Name), o.CcxThreads);
            double fNom = res.Nominal.Parts.First(x => x.Part == worst).Windows.Select(w => w.ForceN).DefaultIfEmpty(0).Max();
            double fP95 = res.Mc.TryGetValue(worst.Name, out var st) ? P(st.Force, 0.95) : double.NaN;
            res.PartFea.Add((worst, r, fNom * worst.Share, fP95 * worst.Share));
            if (!r.Ok) res.Notes.Add($"부품 FEA 실패 {worst.Label}: {r.Error}");
        }
    }

    // ---------------- 컨테이너 공력 ----------------

    private static void RunAero(ScenarioDoc doc, LoadsStudyOptions o, Result res, CalculixInstall inst, string work)
    {
        var flowPath = FlowPath(doc);
        if (flowPath is null || !File.Exists(flowPath)) { res.Notes.Add($"CFD 표면 장 없음 — 공력 셸 해석 생략({flowPath ?? "container.aero.flowField 미지정"})."); return; }
        var ff = SurfaceFlowField.Load(flowPath);
        var flight = ContainerMaterialStudy.FlightAt(1000, 0.5, 0.30, 1.50);
        double rCfd = ff.Radius, lCfd = ff.X1 - ff.X0, sRef = Math.PI * rCfd * rCfd;
        double CnL1(double a) { double s = Math.Sin(a * Math.PI / 180); return s * s * flight.CdCross * 2 * rCfd * lCfd / sRef; }
        var ao = new ContainerAeroFea.Options
        {
            Flow = ff, Group = ff.GroupNearest(0.5), Q = flight.Q, CnReference = CnL1, Install = inst, WorkDir = work, Threads = o.CcxThreads, Log = o.Log,
        };
        o.Log?.Invoke($"CFD Cp 셸: {flowPath} 묶음 {ao.Group}, q = {flight.Q:F0} Pa");
        try
        {
            var cmo = new ContainerMaterialOptions { OutDir = work };
            res.BuckleVerify = ContainerMaterialStudy.VerifyUniformPressure(inst, cmo, 0.003, Path.Combine(work, "verify"), 48, 40);
        }
        catch (Exception ex) { res.Notes.Add($"균일 외압 좌굴 검증 실패: {ex.Message}"); }
        try { res.ShellModes.AddRange(ContainerAeroFea.Modal(ao)); }
        catch (Exception ex) { res.Notes.Add($"셸 고유진동 실패: {ex.Message}"); }
        try { res.Aero.AddRange(ContainerAeroFea.Run(ao, [60.0, 90.0])); }
        catch (Exception ex) { res.Notes.Add($"CFD Cp 셸 해석 실패: {ex.Message}"); return; }
        // 와류 흘림 동적 계수(α 90°): f_s = St·V/D, 셸 1차 모드
        double cd3 = ff.Coefficient("CZ", 90, ao.Group) is var cz && double.IsFinite(cz) ? Math.Abs(cz) * sRef / (2 * rCfd * lCfd) : 0.67;
        if (res.ShellModes.Count > 0)
        {
            double fn = res.ShellModes[0];
            res.ShedFs1 = o.Strouhal.Lo * flight.V / 0.30; res.ShedFs2 = o.Strouhal.Hi * flight.V / 0.30;
            var cands = new[] { (o.ClRms.Lo, res.ShedFs1), (o.ClRms.Lo, res.ShedFs2), (o.ClRms.Hi, res.ShedFs1), (o.ClRms.Hi, res.ShedFs2) }
                .Select(c => ContainerAeroFea.SheddingFactor(c.Item1, cd3, c.Item2, fn)).ToList();
            res.ShedLo = cands.Min(); res.ShedHi = cands.Max();
            res.ShellResonance = VibrationFatigue.CheckResonance(fn, res.ShedFs1, res.ShedFs2);
        }
        // 조합 하중: 공칭 실행의 분리 창 최대 비중력 가속도(CG) · MC 95 % · 텀블링 포락선 각속도
        var nom = res.Nominal;
        double xCg = 0.78;
        double sepG = Peak(nom, "separation", out var sepVec, out double sepAlpha);
        double drogueG95 = res.McScalars.TryGetValue("containerG:drogue", out var dg) ? P(dg, 0.95) : Peak(nom, "drogue", out _, out _);
        double sepG95 = res.McScalars.TryGetValue("containerG:separation", out var sg) ? P(sg, 0.95) : sepG;
        double omega99 = EnvelopeValue(o.EnvelopePath, "maxOmega_radps", "mcP99");
        double mTot = nom.Probe.Mass.Count > 0 ? nom.Probe.Mass[0] : 31;
        double kNom90 = res.Aero.FirstOrDefault(a => Math.Abs(a.Alpha - 90) < 1e-6)?.KNominal ?? 1;
        double aSep = Math.Clamp(Math.Round(sepAlpha / 15) * 15, 15, 90);
        double kNomSep = res.Aero.FirstOrDefault(a => Math.Abs(a.Alpha - aSep) < 1e-6)?.KNominal ?? 1;
        var lat = Math.Sqrt(sepVec.Y * sepVec.Y + sepVec.Z * sepVec.Z) * PayloadShock.G0;
        var ax = Math.Abs(sepVec.X) * PayloadShock.G0;
        var combos = new List<ContainerAeroFea.ComboCase>
        {
            new("C1", $"분리 직후: 공력 α {aSep:0}°(분리 창 최대 G 순간 α_t {sepAlpha:F0}°) + 분리·재접촉 관성(공칭 {sepG:F1} G)", aSep, kNomSep,
                new Vec3(lat, 0, -ax), 0, mTot, "공칭 실행 분리 창 CG 최대 비중력 가속도(접촉 τ 공칭) — 횡성분은 공력 하중 방향, 축성분은 튜브 압축 방향으로(보수)"),
            new("C2", $"텀블링 옆으로 선 자세 + 드로그 개방: α 90° K 공칭 + 축 {drogueG95:F1} G(MC 95 %) + ω {omega99:F1} rad/s(포락선 MC 99 %)", 90, kNom90,
                new Vec3(0, 0, -drogueG95 * PayloadShock.G0), double.IsFinite(omega99) ? omega99 : 0, mTot, "T2 뒤집힘(α ≥ 90°) 중 드로그 개방 — 개방 축하중은 압축 방향(보수), 원심 = 횡축 회전"),
            new("C3", $"보수 포락: α 90° K ×{ao.KHigh:0.0} + 횡 {sepG95:F1} G(분리 MC 95 %) + 축 {drogueG95:F1} G + ω {omega99:F1} rad/s", 90, ao.KHigh,
                new Vec3(sepG95 * PayloadShock.G0, 0, -drogueG95 * PayloadShock.G0), double.IsFinite(omega99) ? omega99 : 0, mTot, "동시 작용하지 않는 최대값들을 겹친 상한(설계 포락)"),
        };
        foreach (var c in combos)
        {
            o.Log?.Invoke($"조합 하중 {c.Id}: {c.Label}");
            try { res.Combos.Add(ContainerAeroFea.RunCombo(ao, c, xCg)); }
            catch (Exception ex) { res.Notes.Add($"조합 {c.Id} 실패: {ex.Message}"); }
        }
    }

    /// <summary>창의 CG 최대 비중력 가속도 [G]·그 벡터(동체, G)·그 순간 총받음각 [deg] — 접촉 펄스 포함(τ = 실행 값).</summary>
    public static double Peak(PayloadRun run, string window, out Vec3 vecG, out double alphaDeg)
    {
        vecG = Vec3.Zero; alphaDeg = 0;
        if (!run.WindowSpans.TryGetValue(window, out var span)) return double.NaN;
        var pr = run.Probe;
        int i0 = pr.T.FindIndex(t => t >= span.T0);
        if (i0 < 0) return double.NaN;
        var cg = pr.Cg[i0];
        var (x, y, z) = PayloadShock.BaseAccel(run, cg, span.T0, span.T1);
        double best = 0; int bi = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double n = Math.Sqrt(x[i] * x[i] + y[i] * y[i] + z[i] * z[i]);
            if (n > best) { best = n; bi = i; }
        }
        if (x.Length > 0) vecG = new Vec3(x[bi], y[bi], z[bi]) / PayloadShock.G0;
        double tb = span.T0 + bi * PayloadShock.ResampleDt;
        int k = pr.T.FindIndex(t => t >= tb);
        if (k >= 0) alphaDeg = pr.AlphaTotalDeg(k);
        return best / PayloadShock.G0;
    }

    private static double EnvelopeValue(string? path, string key, string stat)
    {
        if (path is null || !File.Exists(path)) return double.NaN;
        try { return J.ParseObject(File.ReadAllText(path))[key]?[stat] is JsonValue v && v.TryGetValue(out double d) ? d : double.NaN; }
        catch { return double.NaN; }
    }

    private static string? FlowPath(ScenarioDoc doc)
    {
        foreach (var b in doc.Root["bodies"] as JsonArray ?? new JsonArray())
            if (b is JsonObject bo && J.Str(bo, "name", "") == "container" && bo["aero"] is JsonObject a && J.StrOpt(a, "flowField") is { } f)
                return Path.GetFullPath(Path.Combine(doc.BaseDir, f));
        return null;
    }

    // ---------------- 출력 ----------------

    private static void WriteProbeCsv(string path, ShockProbe p)
    {
        using var w = new StreamWriter(path, false, new UTF8Encoding(false));
        w.WriteLine("t_s,fx_mps2,fy_mps2,fz_mps2,p_radps,q_radps,r_radps,alpha_x,alpha_y,alpha_z,mass_kg,alpha_t_deg");
        for (int i = 0; i < p.T.Count; i++)
            w.WriteLine(string.Join(",", new[] { p.T[i], p.F[i].X, p.F[i].Y, p.F[i].Z, p.W[i].X, p.W[i].Y, p.W[i].Z, p.Alpha[i].X, p.Alpha[i].Y, p.Alpha[i].Z, p.Mass[i], p.AlphaTotalDeg(i) }
                .Select(v => v.ToString("G7", Inv))));
        w.WriteLine();
        w.WriteLine("impulse_t_s,dv_x,dv_y,dv_z,dw_x,dw_y,dw_z,other,J_Ns");
        foreach (var im in p.Impulses)
            w.WriteLine(string.Join(",", new[] { im.T, im.Dv.X, im.Dv.Y, im.Dv.Z, im.Dw.X, im.Dw.Y, im.Dw.Z }.Select(v => v.ToString("G7", Inv))) + $",{im.Other},{im.J.ToString("G6", Inv)}");
    }

    private static void WriteFigures(string dir, Result r)
    {
        var nom = r.Nominal; var pr = nom.Probe;
        // 1. 컨테이너 CG 비중력 가속도(강체, 접촉 펄스 포함)
        var series = new List<SvgPlot.Series>();
        foreach (var (id, (t0, t1)) in nom.WindowSpans)
        {
            int i0 = pr.T.FindIndex(t => t >= t0);
            if (i0 < 0) continue;
            var (x, y, z) = PayloadShock.BaseAccel(nom, pr.Cg[i0], t0, t1, 2e-4);
            var pts = new List<(double, double)>();
            for (int i = 0; i < x.Length; i++) pts.Add((t0 + i * 2e-4, Math.Sqrt(x[i] * x[i] + y[i] * y[i] + z[i] * z[i]) / PayloadShock.G0));
            series.Add(new SvgPlot.Series(r.Config.Windows.First(w => w.Id == id).Label, Decimate(pts, 3000)));
        }
        if (series.Count > 0)
            File.WriteAllText(Path.Combine(dir, "fig1_container_g.svg"), SvgPlot.Line("컨테이너 CG 비중력 가속도(6DOF 강체 + 접촉 충격량 τ 공칭)", "시간 [s]", "|a| [G]", series,
                note: "mns loads 공칭 실행 — 접촉 충격량은 반 사인(τ 공칭)으로 되살림"), new UTF8Encoding(false));
        // 2. SRS(드론 1 래치 하중 경로, 축 방향) — 창별
        var p1 = nom.Parts.FirstOrDefault(p => p.Part.Name == "latch_pin1");
        if (p1 is not null)
        {
            var srs = new List<SvgPlot.Series>();
            foreach (var (id, (t0, t1)) in nom.WindowSpans)
            {
                var (x, _, _) = PayloadShock.BaseAccel(nom, p1.Position, t0, t1);
                if (x.Length == 0) continue;
                var (f, s) = ShockSpectrum.Spectrum(x, PayloadShock.ResampleDt, 5, 1000, 6, r.Config.Zeta);
                srs.Add(new SvgPlot.Series(r.Config.Windows.First(w => w.Id == id).Label, f.Zip(s, (a, b) => (Math.Log10(a), b / PayloadShock.G0)).ToList()));
            }
            File.WriteAllText(Path.Combine(dir, "fig2_srs_drone1_axial.svg"), SvgPlot.Line($"충격 응답 스펙트럼(Q = {1 / (2 * r.Config.Zeta):F0}) — 드론 1 질량 중심 축 방향",
                "log₁₀ 고유진동수 [Hz]  (1 = 10 Hz, 2 = 100 Hz, 3 = 1 kHz)", "SRS [G]", srs,
                note: $"드론 장착 공칭 고유진동수 {p1.FnHz:F0} Hz(log {Math.Log10(Math.Max(p1.FnHz, 1)):F2}) — Smallwood 램프 불변 필터"), new UTF8Encoding(false));
        }
        // 3. CFD Cp 둘레 분포(중앙 단면) vs 경험식
        if (r.Aero.Count > 0)
        {
            var s3 = new List<SvgPlot.Series>();
            foreach (var a in r.Aero.Where(a => a.Alpha is 30 or 60 or 90))
            {
                var m = a.Map;
                var pts = new List<(double, double)>();
                double zMid = m.Centroid.Select(c => c.Z).OrderBy(z => Math.Abs(z - 0.78)).First();
                for (int e = 0; e < m.Cp.Length; e++)
                    if (Math.Abs(m.Centroid[e].Z - zMid) < 1e-6) pts.Add((Math.Abs(WrapDeg((m.Theta[e] - Math.PI) * 180 / Math.PI)), m.Cp[e]));
                s3.Add(new SvgPlot.Series($"CFD α {a.Alpha:0}°", pts.OrderBy(p => p.Item1).ToList(), true));
            }
            var fl = ContainerMaterialStudy.FlightAt(1000, 0.5, 0.30, 1.50);
            var emp = Enumerable.Range(0, 91).Select(i => (i * 2.0, ContainerMaterialStudy.CrossflowCp(i * 2.0 * Math.PI / 180, fl.Cp0, fl.CpStar, fl.CpBase))).ToList();
            s3.Add(new SvgPlot.Series("경험식(기존 재질 연구, 횡류 90°)", emp));
            File.WriteAllText(Path.Combine(dir, "fig3_cp_cfd_vs_empirical.svg"), SvgPlot.Line("컨테이너 중앙 단면 표면 Cp — OpenFOAM 정상 RANS vs 경험식",
                "바람받이에서 잰 각 φ [°]", "Cp", s3, 0, 180, note: "컨테이너 aero.flowField(M 0.5) · ContainerMaterialStudy.CrossflowCp"), new UTF8Encoding(false));
        }
    }

    private static double WrapDeg(double a) { while (a > 180) a -= 360; while (a < -180) a += 360; return a; }

    private static List<(double, double)> Decimate(List<(double X, double Y)> p, int max)
    {
        if (p.Count <= max) return p;
        int step = (int)Math.Ceiling(p.Count / (double)max);
        var r = new List<(double, double)>();
        for (int i = 0; i < p.Count; i += step)
        {
            var seg = p.Skip(i).Take(step).ToList();
            var mx = seg.MaxBy(s => s.Y);
            r.Add((mx.X, mx.Y));
        }
        return r;
    }

    public static JsonObject ToJson(ScenarioDoc doc, LoadsStudyOptions o, Result r)
    {
        var cfg = r.Config;
        JsonObject PartJ(PartResult p)
        {
            var mc = r.Mc.GetValueOrDefault(p.Part.Name);
            return new JsonObject
            {
                ["name"] = p.Part.Name, ["label"] = p.Part.Label, ["station"] = p.Part.Station, ["mass_kg"] = p.Mass, ["fn_Hz"] = p.FnHz,
                ["kMount_Npm"] = double.IsFinite(p.KMount) ? p.KMount : null, ["direction"] = p.Part.Direction, ["material"] = p.Part.Material?.Key,
                ["coef_MPa_per_kN"] = p.Part.HasStress ? p.Part.Coef * p.Part.Share * 1e3 / 1e6 : null, ["governing"] = p.Part.Governing,
                ["reference"] = p.Part.Reference,
                ["massParts"] = p.Part.MassParts is null ? null : new JsonArray(PayloadShock.SelectedMassParts(cfg, p.Part).Select(x => (JsonNode)new JsonObject { ["body"] = x.Body, ["mass_kg"] = x.Mass }).ToArray()),
                ["massBodies"] = p.Part.MassBodies.Length == 0 ? null : new JsonArray(p.Part.MassBodies.Select(b => (JsonNode)b).ToArray()),
                ["designUltimate"] = PayloadShock.DesignUltimate(cfg, p) is { } du ? new JsonObject
                {
                    ["g"] = du.G, ["force_N"] = du.ForceN, ["stress_MPa"] = du.StressPa / 1e6, ["sfYield"] = Num(du.SfYield), ["sfUltimate"] = Num(du.SfUltimate),
                } : null,
                ["windows"] = new JsonArray(p.Windows.Select(w => (JsonNode)new JsonObject
                {
                    ["window"] = w.Window, ["rigid_g"] = w.RigidG, ["dynNominal_g"] = w.DynNominalG, ["dyn_g"] = w.DynG, ["fnUsed_Hz"] = w.FnUsedHz, ["daf"] = Num(w.Daf),
                    ["force_N"] = w.ForceN, ["stress_MPa"] = Num(w.StressPa / 1e6), ["sfYield"] = Num(w.SfYield), ["sfUltimate"] = Num(w.SfUltimate), ["gMargin"] = Num(w.GMargin),
                    ["dynDesignModes_g"] = Num(w.DynModesG), ["fnDesignModes_Hz"] = Num(w.FnModesHz), ["sfYieldDesignModes"] = Num(w.SfYieldModes), ["sfUltimateDesignModes"] = Num(w.SfUltimateModes),
                }).ToArray()),
                ["mcWindows"] = new JsonObject(p.Windows.Select(w => new KeyValuePair<string, JsonNode?>(w.Window, r.Mc.TryGetValue($"{p.Part.Name}|{w.Window}", out var mw) ? new JsonObject
                {
                    ["dynG_p50"] = Num(P(mw.DynG, 0.5)), ["dynG_p95"] = Num(P(mw.DynG, 0.95)), ["force_p05_N"] = Num(P(mw.Force, 0.05)), ["force_p95_N"] = Num(P(mw.Force, 0.95)),
                    ["sfYield_p05"] = Num(P(mw.SfY, 0.05)), ["sfUltimate_p05"] = Num(P(mw.SfU, 0.05)),
                } : null))),
                ["mc"] = mc is null ? null : new JsonObject
                {
                    ["runs"] = mc.DynG.Count, ["dynG_p50"] = Num(P(mc.DynG, 0.5)), ["dynG_p95"] = Num(P(mc.DynG, 0.95)), ["force_p95_N"] = Num(P(mc.Force, 0.95)),
                    ["sfYield_p05"] = Num(P(mc.SfY, 0.05)), ["sfUltimate_p05"] = Num(P(mc.SfU, 0.05)), ["sfYield_min"] = Num(mc.SfY.Where(double.IsFinite).DefaultIfEmpty(double.NaN).Min()),
                },
            };
        }
        var nom = r.Nominal;
        return new JsonObject
        {
            ["schema"] = "mns.loads/1", ["scenario"] = doc.Name, ["scenarioHash"] = doc.Hash(), ["parts"] = cfg.Path, ["partsHash"] = cfg.Hash,
            ["command"] = o.Command, ["reportDir"] = o.OutDir.Replace('\\', '/'), ["generated"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm", Inv), ["engine"] = typeof(World).Assembly.GetName().Version?.ToString(),
            ["tau_s"] = new JsonObject { ["nominal"] = cfg.TauNominal, ["min"] = cfg.TauMin, ["max"] = cfg.TauMax },
            ["windows"] = new JsonObject(nom.WindowSpans.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, new JsonObject { ["t0_s"] = kv.Value.T0, ["t1_s"] = kv.Value.T1, ["containerG"] = PayloadShock.ContainerPeak(nom, kv.Value.T0, kv.Value.T1).G }))),
            ["nominal"] = new JsonArray(nom.Parts.Select(p => (JsonNode)PartJ(p)).ToArray()),
            ["mcRuns"] = o.Runs, ["mcErrors"] = r.McErrors,
            ["mcScalars"] = new JsonObject(r.McScalars.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, new JsonObject { ["p05"] = Num(P(kv.Value, 0.05)), ["p50"] = Num(P(kv.Value, 0.5)), ["p95"] = Num(P(kv.Value, 0.95)), ["max"] = Num(kv.Value.Where(double.IsFinite).DefaultIfEmpty(double.NaN).Max()), ["min"] = Num(kv.Value.Where(double.IsFinite).DefaultIfEmpty(double.NaN).Min()) }))),
            ["mcErrorRate"] = o.Runs > 0 ? r.McErrors / (double)o.Runs : null,
            ["nominalScalars"] = NominalScalars(r),
            ["partMargins"] = PartMarginsJson(r),
            ["sensitivity"] = new JsonArray(Sensitivity(r, SensOutputs(r)).Select(x => (JsonNode)new JsonObject { ["output"] = x.Output, ["path"] = x.Path, ["grade"] = x.Grade, ["prcc"] = x.Prcc }).ToArray()),
            ["aeroSummary"] = AeroSummary(r),
            ["partFea"] = new JsonArray(r.PartFea.Select(x => (JsonNode)new JsonObject
            {
                ["part"] = x.Part.Name, ["ok"] = x.Fea.Ok, ["error"] = x.Fea.Error, ["coefFea_MPa_per_kN"] = Num(x.Fea.CoefPaPerN * 1e3 / 1e6), ["coefFeaAway_MPa_per_kN"] = Num(x.Fea.CoefAwayPaPerN * 1e3 / 1e6), ["coefClosed_MPa_per_kN"] = x.Part.Coef * 1e3 / 1e6,
                ["k_Npm"] = Num(x.Fea.StiffnessNpm), ["elements"] = x.Fea.Elements, ["forceNom_N"] = x.ForceNom, ["forceP95_N"] = Num(x.ForceP95),
            }).ToArray()),
            ["aero"] = new JsonArray(r.Aero.Select(a => (JsonNode)new JsonObject
            {
                ["alpha_deg"] = a.Alpha, ["cnMesh"] = a.Map.CnMesh, ["cnCfd"] = Num(a.Map.CnCfd), ["cnL1"] = Num(a.CnRef), ["kNominal"] = a.KNominal, ["cpInternal"] = a.Map.CpInternal,
                ["levels"] = new JsonArray(a.Levels.Select(l => (JsonNode)new JsonObject
                {
                    ["level"] = l.Level, ["K"] = l.K, ["delta"] = l.Delta, ["s1Max_MPa"] = l.Field.S1Max / 1e6, ["s3Min_MPa"] = l.Field.S3Min / 1e6, ["vmMax_MPa"] = l.Field.VmMax / 1e6,
                    ["vmAll_MPa"] = l.Field.VmAll / 1e6, ["dispMax_mm"] = l.Field.DispMaxMm, ["buckle"] = l.Buckle is { } b ? Num(b) : null,
                }).ToArray()),
            }).ToArray()),
            ["combos"] = new JsonArray(r.Combos.Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.Case.Id, ["label"] = c.Case.Label, ["s1Max_MPa"] = Num(c.Field.S1Max / 1e6), ["s3Min_MPa"] = Num(c.Field.S3Min / 1e6), ["vmMax_MPa"] = Num(c.Field.VmMax / 1e6),
                ["dispMax_mm"] = Num(c.Field.DispMaxMm), ["buckle"] = c.Buckle is { } b ? Num(b) : null, ["note"] = c.Note,
            }).ToArray()),
            ["shellModes_Hz"] = new JsonArray(r.ShellModes.Select(f => (JsonNode)f).ToArray()),
            ["shedding"] = new JsonObject { ["dlfLo"] = Num(r.ShedLo), ["dlfHi"] = Num(r.ShedHi), ["fsLo_Hz"] = Num(r.ShedFs1), ["fsHi_Hz"] = Num(r.ShedFs2) },
            ["sweep"] = new JsonArray(r.Sweep.Select(x => (JsonNode)new JsonObject
            {
                ["path"] = x.Path, ["value"] = x.Value, ["containerSepG"] = Num(x.ContainerSepG), ["maxImpulse_Ns"] = Num(x.MaxJ), ["maxApproach_mps"] = Num(x.MaxApproach),
                ["contacts"] = x.Contacts, ["latchForce_N"] = Num(x.LatchForce), ["latchDyn_g"] = Num(x.LatchDynG), ["worstPart"] = x.WorstPart, ["worstMargin"] = Num(x.WorstSf), ["error"] = x.Error,
                ["partMargins"] = MarginsJson(x.PartMargins),
                ["containerSepG_tauAssumed"] = Num(x.ContainerSepGTau), ["worstMargin_tauAssumed"] = Num(x.WorstSfTau), ["worstPart_tauAssumed"] = x.WorstPartTau.Length > 0 ? x.WorstPartTau : null,
                ["partMargins_tauAssumed"] = MarginsJson(x.PartMarginsTau),
                ["containerSepG_elastic"] = Num(x.ContainerSepGElastic), ["worstMargin_elastic"] = Num(x.WorstSfElastic), ["maxIndentationSeparation_mm"] = Num(x.MaxIndentation * 1e3),
            }).ToArray()),
            ["contactModel"] = ContactModelJson(doc, r),
            ["contacts"] = ContactsJson(r),
            ["modelComparison"] = ComparisonJson(r),
            ["contactFea"] = new JsonArray(r.ContactFeaRows.Select(x => (JsonNode)x.ToJson()).ToArray()),
            ["designModeSrs"] = DesignModeSrsJson(r),
            ["hingeConvergence"] = new JsonObject
            {
                ["tEnd_s"] = o.HingeCheckTEnd,
                ["rows"] = new JsonArray(r.HingeConv.Select(x => (JsonNode)new JsonObject
                {
                    ["stiffnessCap"] = x.Fixed ? "fixedStabilityDt" : "currentDt", ["dt_s"] = x.Dt, ["hinge"] = x.Hinge, ["maxForceAvg_N"] = x.AvgN,
                    ["maxForce_N"] = x.PeakN, ["effectiveStiffness_Npm"] = x.K, ["releaseTime_s"] = Num(x.ReleaseT), ["petalContacts"] = x.Contacts,
                }).ToArray()),
            },
            ["notes"] = new JsonArray(r.Notes.Select(n => (JsonNode)n).ToArray()),
        };
    }

    private static JsonNode? Num(double v) => double.IsFinite(v) ? v : null;

    private static JsonObject? MarginsJson(IReadOnlyDictionary<string, (double Separation, double Opening, double SeparationModes, double OpeningModes)>? m) =>
        m is null ? null : new JsonObject(m.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key,
            new JsonObject { ["separation"] = Num(kv.Value.Separation), ["opening"] = Num(kv.Value.Opening),
                ["separationModes"] = Num(kv.Value.SeparationModes), ["openingModes"] = Num(kv.Value.OpeningModes) })));

    private static JsonObject ContactModelJson(ScenarioDoc doc, Result r)
    {
        var c = doc.Root["collisions"]?["contact"] as JsonObject;
        return new JsonObject
        {
            ["model"] = r.Compliant ? "compliant" : "impulse",
            ["stiffnessScale"] = c is null ? null : J.Num(c, "stiffnessScale", 1),
            ["stiffnessRange"] = r.Compliant ? new JsonArray(r.StiffRange.Lo, r.StiffRange.Hi) : null,
            ["tauAssumed_s"] = new JsonObject { ["nominal"] = r.Config.TauNominal, ["min"] = r.Config.TauMin, ["max"] = r.Config.TauMax, ["basis"] = r.Config.TauBasis },
            ["basis"] = c is null ? null : J.Str(c, "basis", ""),
            ["restitution"] = doc.Root["collisions"] is JsonObject co ? J.Num(co, "restitution", 0.3) : null,
            ["plasticity"] = c is null ? null : ContactPlasticityModel.FromJson(c)?.ToJson() ?? new JsonObject { ["model"] = "elastic" },
        };
    }

    /// <summary>공칭 실행 접촉(루트 물체가 받은 것): 시각·상대·충격량·CG Δv·순응 펄스(강성 요소·τ·최대 힘) — 보고서 2절 표.</summary>
    private static JsonArray ContactsJson(Result r)
    {
        var nom = r.Nominal;
        double mRoot = nom.Probe.Mass.Count > 0 ? nom.Probe.Mass[0] : double.NaN;
        return new JsonArray(nom.Probe.Impulses.Take(40).Select(i => (JsonNode)new JsonObject
        {
            ["time_s"] = i.T, ["other"] = i.Other, ["impulse_Ns"] = i.J, ["dvCg_mps"] = i.Dv.Norm, ["dw_radps"] = i.Dw.Norm, ["rootMass_kg"] = Num(mRoot),
            ["peakForce_tauNominal_N"] = Math.PI * i.J / (2 * r.Config.TauNominal),
            ["compliant"] = i.Pulse?.ToJson(),
        }).ToArray());
    }

    /// <summary>접촉력 이력 모델 비교(같은 6DOF 실행): 순응 접촉(공칭·강성 범위 끝) vs 충격량 모델 τ 공칭·최소·최대 — 분리 창 컨테이너 G · 최소 여유 · 드론 위치 동적 G.</summary>
    private static JsonObject? ComparisonJson(Result r)
    {
        if (!r.Compliant) return null;
        var cfg = r.Config;
        JsonObject One(PayloadRun? run, string label)
        {
            if (run is null) return new JsonObject { ["label"] = label };
            var sp = run.WindowSpans.GetValueOrDefault("separation");
            double mm = double.PositiveInfinity; string who = "";
            foreach (var pr in run.Parts.Where(p => p.Part.Judged))
                foreach (var ww in pr.Windows.Where(v => v.Window == "separation"))
                {
                    double m = Margin(cfg, pr.Part, ww.SfYield, ww.SfUltimate);
                    if (m < mm) { mm = m; who = pr.Part.Label; }
                }
            var latch = run.Parts.Where(p => p.Part.Name.StartsWith("latch_pin", StringComparison.Ordinal) && !p.Part.Reference).ToList();
            var sepW = latch.SelectMany(p => p.Windows.Where(w => w.Window == "separation")).ToList();
            return new JsonObject
            {
                ["label"] = label, ["tau_s"] = run.UsePulses ? null : run.Tau, ["stiffnessFactor"] = run.UsePulses ? run.PulseFactor : null,
                ["elasticPulses"] = run.UsePulses && (run.ElasticPulses || !run.HasPlasticPulses),
                ["containerG_separation"] = Num(sp.T1 > sp.T0 ? PayloadShock.ContainerPeak(run, sp.T0, sp.T1).G : double.NaN),
                ["minMarginSeparation"] = Num(mm), ["minMarginSeparationPart"] = who,
                ["droneRigidG_separation"] = Num(sepW.Select(w => w.RigidG).DefaultIfEmpty(double.NaN).Max()),
                ["droneDynG_separation"] = Num(sepW.Select(w => w.DynG).DefaultIfEmpty(double.NaN).Max()),
                ["droneDynDesignModesG_separation"] = Num(sepW.Select(w => w.DynModesG).Where(double.IsFinite).DefaultIfEmpty(double.NaN).Max()),
                ["partMargins"] = MarginsJson(PartMargins(run, cfg)),
            };
        }
        return new JsonObject
        {
            ["compliant"] = One(r.Nominal, r.Plastic ? "탄성-소성 순응 접촉(공칭 강성)" : "순응 접촉(공칭 강성)"),
            ["compliantElastic"] = r.Elastic is null ? null : One(r.Elastic, "탄성 순응 접촉(힘 상한 없음 — 이전 값)"),
            ["compliantStiffLo"] = One(r.StiffLo, $"순응 접촉 강성 ×{r.StiffRange.Lo:G3}"),
            ["compliantStiffHi"] = One(r.StiffHi, $"순응 접촉 강성 ×{r.StiffRange.Hi:G3}"),
            ["tauNominal"] = One(r.TauNom, $"충격량 모델 τ {cfg.TauNominal * 1e3:G3} ms(이전 가정)"),
            ["tauMin"] = One(r.TauMin, $"충격량 모델 τ {cfg.TauMin * 1e3:G3} ms"),
            ["tauMax"] = One(r.TauMax, $"충격량 모델 τ {cfg.TauMax * 1e3:G3} ms"),
        };
    }

    /// <summary>설계 FEA 고유진동수에서 드론 1 래치 위치 축 SRS/강체(창별) — 설계 준정적 판단 대조용.</summary>
    private static JsonObject? DesignModeSrsJson(Result r)
    {
        var cfg = r.Config;
        if (cfg.DesignModesHz.Length == 0 || !r.Nominal.Positions.TryGetValue("latch_pin1", out var pos)) return null;
        var rows = PayloadShock.DesignModeSrs(r.Nominal, cfg, pos);
        return new JsonObject
        {
            ["point"] = "latch_pin1", ["zeta"] = cfg.Zeta, ["fn_Hz"] = new JsonArray(cfg.DesignModesHz.Select(f => (JsonNode)f).ToArray()), ["basis"] = cfg.DesignModesBasis,
            ["windows"] = new JsonArray(rows.Select(x => (JsonNode)new JsonObject
            {
                ["window"] = x.Window, ["rigidAxial_g"] = x.RigidG, ["srsAxial_g"] = new JsonArray(x.SrsG.Select(v => (JsonNode)v).ToArray()),
                ["ratio"] = new JsonArray(x.SrsG.Select(v => Num(x.RigidG > 0 ? v / x.RigidG : double.NaN)).ToArray()),
            }).ToArray()),
        };
    }

    /// <summary>공칭·MC 5 % 부품별 여유(분리 창 · 개방 창) — 보고서가 SF 에서 다시 계산하지 않도록.</summary>
    private static JsonObject PartMarginsJson(Result r)
    {
        var cfg = r.Config;
        var o = new JsonObject();
        foreach (var (name, (sep, open, sepM, openM)) in PartMargins(r.Nominal, cfg))
        {
            var part = cfg.Part(name);
            double M05(params string[] wins) => wins.Select(id => r.Mc.TryGetValue($"{name}|{id}", out var st) ? Margin(cfg, part, P(st.SfY, 0.05), P(st.SfU, 0.05)) : double.NaN)
                .Where(double.IsFinite).DefaultIfEmpty(double.NaN).Min();
            o[name] = new JsonObject { ["separation"] = Num(sep), ["opening"] = Num(open), ["separation_p05"] = Num(M05("separation")), ["opening_p05"] = Num(M05("drogue", "main")),
                ["separationModes"] = Num(sepM), ["openingModes"] = Num(openM) };
        }
        return o;
    }

    private static IEnumerable<(string, IReadOnlyList<double>)> SensOutputs(Result r)
    {
        foreach (var k in new[] { "containerG:drogue", "containerG:separation", "minMarginOpening", "minMarginSeparation" })
            if (r.McScalars.TryGetValue(k, out var v)) yield return (k, v);
        if (r.Mc.TryGetValue("latch_pin1|drogue", out var st)) yield return ("latchForce:drogue", st.Force);
    }

    /// <summary>공칭 실행 스칼라(신뢰도 등록용): 창별 컨테이너 G(접촉 포함), 최소 여유, 드론 1 래치 드로그 하중.</summary>
    private static JsonObject NominalScalars(Result r)
    {
        var nom = r.Nominal; var cfg = r.Config;
        var o = new JsonObject();
        foreach (var (id, (t0, t1)) in nom.WindowSpans) o[$"containerG:{id}"] = PayloadShock.ContainerPeak(nom, t0, t1).G;
        foreach (var (key, wins) in new[] { ("minMarginOpening", new[] { "drogue", "main" }), ("minMarginSeparation", new[] { "separation" }) })
        {
            double mm = double.PositiveInfinity; string who = "";
            foreach (var pr in nom.Parts.Where(p => p.Part.Judged))
                foreach (var ww in pr.Windows.Where(v => wins.Contains(v.Window)))
                {
                    double m = Margin(cfg, pr.Part, ww.SfYield, ww.SfUltimate);
                    if (m < mm) { mm = m; who = pr.Part.Label; }
                }
            o[key] = Num(mm); o[key + "Part"] = who;
        }
        var lp = nom.Parts.FirstOrDefault(p => p.Part.Name == "latch_pin1")?.Windows.FirstOrDefault(w => w.Window == "drogue");
        o["latchForce:drogue"] = lp is null ? null : lp.ForceN;
        return o;
    }

    /// <summary>컨테이너 공력 셸 판정 요약(α 90° — CFRP 준등방 Ftu·Fcu, 좌굴 = λ × 끝 구속 보정 × 녹다운 / 와류 계수 상한).</summary>
    private static JsonObject? AeroSummary(Result r)
    {
        var a90 = r.Aero.FirstOrDefault(a => Math.Abs(a.Alpha - 90) < 1e-6);
        if (a90 is null) return null;
        var c = r.Shell;
        double corr = (r.BuckleVerify is { } bv && bv.Fea > 0 ? Math.Min(1, bv.Classical / bv.Fea) : 1) * ShellBuckleScale(c);
        double shed = double.IsFinite(r.ShedHi) ? r.ShedHi : 1;
        double Str(ContainerAeroFea.StaticField f) => ShellStrengthSf(c, f);
        var o = new JsonObject { ["material"] = c.Key, ["buckleCorrection"] = corr, ["knockdown"] = ContainerMaterialStudy.GammaPressure, ["sheddingDlfHi"] = shed };
        foreach (var l in a90.Levels)
        {
            string k = l.Level.StartsWith("CFD") ? "cfd" : l.Level.StartsWith("공칭") ? "nominal" : "high";
            o[$"strengthSf_{k}"] = Num(Str(l.Field) / shed);
            o[$"buckleSf_{k}"] = l.Buckle is { } b ? Num(b * corr * ContainerMaterialStudy.GammaPressure / shed) : null;
            o[$"vm_{k}_MPa"] = l.Field.VmMax / 1e6;
        }
        if (r.ShellResonance is { } vr)
            o["vibration"] = new JsonObject
            {
                ["fn_Hz"] = vr.Fn, ["fsLo_Hz"] = vr.FsLo, ["fsHi_Hz"] = vr.FsHi, ["ratio"] = vr.Ratio, ["crosses"] = vr.Crosses, ["verdict"] = vr.Verdict,
                ["limit"] = VibrationFatigue.FrequencyLimit,
            };
        return o;
    }

    // ---------------- 보고서 ----------------

    private static string Markdown(ScenarioDoc doc, LoadsStudyOptions o, Result r) => LoadsReport.Write(doc, o, r);
}
