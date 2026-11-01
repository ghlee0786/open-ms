using System.Text.Json.Nodes;
using Sim.Core.Engine;
using Sim.Core.Models;
using Sim.Core.Scenario;
using Sim.Core.Structures;

namespace Sim.Core.Analysis;

/// <summary>
/// 낙하산 개방 충격 · 브레이크 코드 · 박스 내부 하중 경로 분석(FR-AN-08 ③): cushionMount 기록(스텝 해상도)에서
/// 단계 창별 박스/드론 최대 G 와 부품별 최대 응력·항복 안전계수(근사 닫힌 식)를 뽑는다.
/// </summary>
public static class OpeningShock
{
    public sealed record PartPeak(string Part, string Label, double ForceN, double StressMPa, double Safety, double Time, double RateFactor, string Governing);

    public sealed record StageRow(string Mount, string Stage, bool Fired, double T0, double T1,
        double PayloadG, double PayloadGTime, double BaseG, double BaseGTime, bool InBox, double PadStrain, double PinForce, IReadOnlyList<PartPeak> Parts)
    {
        public PartPeak? Weakest => Parts.Where(p => double.IsFinite(p.Safety)).MinBy(p => p.Safety);
    }

    public sealed record MountScalar(string Mount, double PayloadGInBox, double PayloadG, double BaseG, double PadStrain, bool PinsBroken, bool Bottomed);
    public sealed record CordRow(string Name, double BreakLoad, double MaxTension, bool Snapped, double TautTime, double TautSpeed, double BreakTime, double Impulse, double Stiffness);
    public sealed record ChuteRow(string Name, double DeployTime, double StretchSpeed, double MaxForce, double MaxForceTime, double OpeningFactor, double CdS0);

    public sealed class Result
    {
        public List<StageRow> Stages { get; } = new();
        public List<CordRow> Cords { get; } = new();
        public List<ChuteRow> Chutes { get; } = new();
        /// <summary>(mount, part) → 전 구간 최소 SF.</summary>
        public Dictionary<(string Mount, string Part), PartPeak> Overall { get; } = new();
        public List<CushionMount> Mounts { get; } = new();
        public List<MountScalar> MountScalars { get; } = new();

        public double MinSafety => Overall.Values.Select(p => p.Safety).DefaultIfEmpty(double.PositiveInfinity).Min();

        public JsonObject ToJson() => new()
        {
            ["stages"] = new JsonArray(Stages.Select(s => (JsonNode)new JsonObject
            {
                ["mount"] = s.Mount, ["stage"] = s.Stage, ["fired"] = s.Fired, ["t0_s"] = Fin(s.T0), ["t1_s"] = Fin(s.T1),
                ["payloadG"] = s.PayloadG, ["payloadGTime_s"] = Fin(s.PayloadGTime), ["baseG"] = s.BaseG, ["baseGTime_s"] = Fin(s.BaseGTime),
                ["inBox"] = s.InBox, ["padStrain"] = s.PadStrain, ["pinForce_N"] = s.PinForce,
                ["parts"] = new JsonArray(s.Parts.Select(p => (JsonNode)PartJson(p)).ToArray()),
            }).ToArray()),
            ["overall"] = new JsonArray(Overall.Select(kv => (JsonNode)new JsonObject { ["mount"] = kv.Key.Mount, ["part"] = PartJson(kv.Value) }).ToArray()),
            ["cords"] = new JsonArray(Cords.Select(c => (JsonNode)new JsonObject
            {
                ["name"] = c.Name, ["breakLoad_N"] = c.BreakLoad, ["maxTension_N"] = c.MaxTension, ["snapped"] = c.Snapped, ["tautTime_s"] = Fin(c.TautTime),
                ["tautSpeed_mps"] = Fin(c.TautSpeed), ["breakTime_s"] = Fin(c.BreakTime), ["impulse_Ns"] = c.Impulse, ["stiffness_Npm"] = c.Stiffness,
            }).ToArray()),
            ["chutes"] = new JsonArray(Chutes.Select(c => (JsonNode)new JsonObject
            {
                ["name"] = c.Name, ["deployTime_s"] = Fin(c.DeployTime), ["stretchSpeed_mps"] = Fin(c.StretchSpeed), ["maxForce_N"] = c.MaxForce,
                ["maxForceTime_s"] = Fin(c.MaxForceTime), ["openingFactor"] = Fin(c.OpeningFactor), ["cdS0_m2"] = c.CdS0,
            }).ToArray()),
            ["minSafety"] = Fin(MinSafety),
        };

        private static JsonObject PartJson(PartPeak p) => new()
        {
            ["part"] = p.Part, ["label"] = p.Label, ["force_N"] = p.ForceN, ["stress_MPa"] = p.StressMPa, ["safety"] = Fin(p.Safety),
            ["time_s"] = Fin(p.Time), ["rateFactor"] = p.RateFactor, ["governing"] = p.Governing,
        };
    }

    internal static double? Fin(double v) => double.IsFinite(v) ? v : null;

    /// <summary>실행이 끝난 World 분석.</summary>
    public static Result Analyze(World w)
    {
        var res = new Result();
        foreach (var b in w.Bodies)
            foreach (var m in b.Devices.OfType<CushionMount>())
            {
                res.Mounts.Add(m);
                res.MountScalars.Add(new MountScalar(m.FullName, m.MaxPayloadGInBox, m.MaxPayloadG, m.MaxBaseG,
                    m.Pads.Select(p => p.MaxStrainSeen).DefaultIfEmpty(0).Max(), m.PinsBroken, m.Pads.Any(p => p.BottomedOut)));
                foreach (var st in m.Stages) res.Stages.Add(Stage(w, m, st));
                if (m.History.Count == 0) continue;
                foreach (var p in m.Parts)
                {
                    var pk = Peak(m, p, 0, m.History.Count - 1);
                    if (pk is not null) res.Overall[(m.FullName, p.Name)] = pk;
                }
            }
        foreach (var c in w.Connectors.OfType<BreakCord>())
            res.Cords.Add(new CordRow(c.Name, c.BreakLoad, c.MaxTension, c.Broken, c.TautTime, c.TautSpeed, c.BreakTime, c.Impulse, c.Stiffness));
        foreach (var b in w.Bodies)
            foreach (var p in b.Devices.OfType<Parachute>())
            {
                if (double.IsNaN(p.DeployTime)) continue;
                double rho = w.Env.Atmosphere.At(-b.OwnCgWorld.Z).Density;
                double ck = double.IsFinite(p.StretchSpeed) && p.StretchSpeed > 0 ? p.MaxForce / (0.5 * rho * p.StretchSpeed * p.StretchSpeed * p.CdS0) : double.NaN;
                res.Chutes.Add(new ChuteRow(p.FullName, p.DeployTime, p.StretchSpeed, p.MaxForce, p.MaxForceTime, ck, p.CdS0));
            }
        return res;
    }

    private static StageRow Stage(World w, CushionMount m, MountStage st)
    {
        var h = m.History;
        bool fired = w.FiredEvents.TryGetValue(st.From, out double tf);
        if (!fired || h.Count == 0) return new StageRow(m.FullName, st.Label, false, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, false, 0, 0, []);
        double t0 = tf + st.Offset;
        double t1 = st.To is not null && w.FiredEvents.TryGetValue(st.To, out double tt) && tt >= t0 ? tt + st.Pad : t0 + st.Duration;
        int i0 = LowerBound(h, t0 - 1e-9), i1 = LowerBound(h, t1 + 1e-9) - 1;
        if (i1 < i0) return new StageRow(m.FullName, st.Label, true, t0, t1, double.NaN, double.NaN, double.NaN, double.NaN, false, 0, 0, []);
        int ip = i0, ib = i0;
        double strain = 0, pin = 0; bool inBox = false;
        for (int i = i0; i <= i1; i++)
        {
            if (h[i].PayloadG > h[ip].PayloadG) ip = i;
            if (h[i].BaseG > h[ib].BaseG) ib = i;
            strain = Math.Max(strain, h[i].PadStrain); pin = Math.Max(pin, h[i].PinForce);
            inBox |= h[i].InBox;
        }
        var parts = m.Parts.Select(p => Peak(m, p, i0, i1)).Where(p => p is not null).Select(p => p!).ToList();
        return new StageRow(m.FullName, st.Label, true, t0, t1, h[ip].PayloadG, h[ip].T, h[ib].BaseG, h[ib].T, inBox, strain, pin, parts);
    }

    private static int LowerBound(List<MountSample> h, double t)
    {
        int lo = 0, hi = h.Count;
        while (lo < hi) { int mid = (lo + hi) / 2; if (h[mid].T < t) lo = mid + 1; else hi = mid; }
        return lo;
    }

    /// <summary>
    /// 창 [i0, i1] 안 부품 최대 응력. 변형률 속도 ε̇ ≈ (σ_peak/E)/t_rise (t_rise = 피크 전 10% 지점부터) → 재질 속도 배율(FR-MAT-03)을
    /// 허용응력에 곱한다(연성 금속 JC 는 1 근처, 고분자 DIF 는 수 %).
    /// </summary>
    private static PartPeak? Peak(CushionMount m, PartStressDef p, int i0, int i1)
    {
        var h = m.History;
        int ip = -1; double fMax = 0;
        for (int i = i0; i <= i1; i++)
        {
            double f = m.PartLoad(p, h[i]);
            if (f > fMax) { fMax = f; ip = i; }
        }
        if (ip < 0) return new PartPeak(p.Name, p.Label, 0, 0, double.PositiveInfinity, double.NaN, 1, p.Governing);
        int ir = ip;
        while (ir > 0 && m.PartLoad(p, h[ir - 1]) > 0.1 * fMax && h[ip].T - h[ir - 1].T < 5) ir--;
        double rise = Math.Max(h[ip].T - h[ir].T, 1e-3);
        double sigma = p.Stress(fMax);
        double rate = p.Material.RateFactor(sigma / p.Material.E / rise);
        return new PartPeak(p.Name, p.Label, fMax, sigma / 1e6, p.Safety(fMax, rate), h[ip].T, rate, p.Governing);
    }

    // ---------------- Monte Carlo ----------------

    public sealed record SafetyStats(string Mount, string Part, string Label, int N, double Min, double P5, double P50, double Mean, double ProbBelow1, double ProbBelow15, double WorstForce);
    public sealed record ScalarStats(string Name, int N, double Min, double P5, double P50, double P95, double Max, double Mean);

    public sealed class McSummary
    {
        public required McResult Mc { get; init; }
        public List<SafetyStats> Parts { get; } = new();
        public List<SafetyStats> StageParts { get; } = new();
        public List<ScalarStats> Scalars { get; } = new();
        public int CordNotSnapped { get; set; }
        public int PinsBroken { get; set; }
        public int Bottomed { get; set; }
        public int Runs => Mc.Runs.Length;
        public int Errors => Mc.Runs.Count(r => r.Error is not null);
    }

    /// <summary>
    /// 개방하중 불확도 MC(FR-AN-08 ④): 시나리오 dispersions(충만 상수·지수·CdS·브레이크 코드 파단하중·사출 G 등)로 runs 회 실행 →
    /// 부품별 최소 SF 분포(최솟값·5 백분위·중앙값·P(SF&lt;1)), 드론/박스 최대 G·개방 계수 분포.
    /// </summary>
    public static McSummary MonteCarloRun(ScenarioDoc doc, int runs, ulong seed, string sampling = "lhs", IProgress<int>? progress = null, string? impactBody = null)
    {
        var perRun = new Result?[runs];
        var body = impactBody ?? J.Str(doc.Root["monteCarlo"] as JsonObject ?? new JsonObject(), "impactBody", "");
        if (body == "") body = (doc.Root["bodies"] as JsonArray)!.OfType<JsonObject>().Select(b => J.Str(b, "name", "")).First(n => n != "");
        var mc = MonteCarlo.Run(doc, runs, body, sampling, seed, progress, inspect: (i, w) =>
        {
            var r = Analyze(w);
            r.Mounts.Clear();   // 스텝 기록(수만 표본)은 실행마다 버린다 — 요약만 보관
            perRun[i] = r;
        });
        var sum = new McSummary { Mc = mc };
        var ok = perRun.Where(r => r is not null).Select(r => r!).ToList();
        foreach (var key in ok.SelectMany(r => r.Overall.Keys).Distinct())
        {
            var pk = ok.Where(r => r.Overall.ContainsKey(key)).Select(r => r.Overall[key]).ToList();
            sum.Parts.Add(Stats(key.Mount, key.Part, pk[0].Label, pk.Select(p => p.Safety).ToList(), pk.Max(p => p.ForceN)));
        }
        foreach (var g in ok.SelectMany(r => r.Stages).Where(s => s.Fired).GroupBy(s => (s.Mount, s.Stage)))
        {
            var weakest = g.Select(s => s.Weakest).Where(p => p is not null).Select(p => p!).ToList();
            if (weakest.Count == 0) continue;
            sum.StageParts.Add(Stats(g.Key.Mount, g.Key.Stage, string.Join("/", weakest.GroupBy(p => p.Label).OrderByDescending(x => x.Count()).Select(x => x.Key).Take(2)),
                weakest.Select(p => p.Safety).ToList(), weakest.Max(p => p.ForceN)));
        }
        var ms = ok.SelectMany(r => r.MountScalars).ToList();
        sum.Scalars.Add(Scalar("드론 최대 G (박스 안, 마운트 경유)", ms.Select(m => m.PayloadGInBox)));
        sum.Scalars.Add(Scalar("드론 최대 G (분리 후 포함)", ms.Select(m => m.PayloadG)));
        sum.Scalars.Add(Scalar("박스 최대 G", ms.Select(m => m.BaseG)));
        sum.Scalars.Add(Scalar("박스 낙하산 최대 하중 [N]", ok.SelectMany(r => r.Chutes).Select(c => c.MaxForce)));
        sum.Scalars.Add(Scalar("개방 하중 계수 Ck = F_max/(q_s·CdS)", ok.SelectMany(r => r.Chutes).Select(c => c.OpeningFactor)));
        sum.Scalars.Add(Scalar("브레이크 코드 최대 장력 [N]", ok.SelectMany(r => r.Cords).Select(c => c.MaxTension)));
        sum.Scalars.Add(Scalar("패드 최대 변형률", ms.Select(m => m.PadStrain)));
        sum.CordNotSnapped = ok.Count(r => r.Cords.Any(c => !c.Snapped));
        sum.PinsBroken = ok.Count(r => r.MountScalars.Any(m => m.PinsBroken));
        sum.Bottomed = ok.Count(r => r.MountScalars.Any(m => m.Bottomed));
        return sum;
    }

    public static double Percentile(IReadOnlyList<double> sorted, double q)
    {
        if (sorted.Count == 0) return double.NaN;
        double x = q * (sorted.Count - 1);
        int i = (int)Math.Floor(x);
        if (i >= sorted.Count - 1) return sorted[^1];
        return sorted[i] + (sorted[i + 1] - sorted[i]) * (x - i);
    }

    private static SafetyStats Stats(string mount, string part, string label, List<double> sf, double worstForce)
    {
        var s = sf.Where(double.IsFinite).OrderBy(x => x).ToList();
        int n = sf.Count;
        return new SafetyStats(mount, part, label, n, s.Count > 0 ? s[0] : double.PositiveInfinity, Percentile(s, 0.05), Percentile(s, 0.5),
            s.Count > 0 ? s.Average() : double.NaN, n > 0 ? sf.Count(x => x < 1) / (double)n : 0, n > 0 ? sf.Count(x => x < 1.5) / (double)n : 0, worstForce);
    }

    private static ScalarStats Scalar(string name, IEnumerable<double> v)
    {
        var s = v.Where(double.IsFinite).OrderBy(x => x).ToList();
        return new ScalarStats(name, s.Count, s.FirstOrDefault(double.NaN), Percentile(s, 0.05), Percentile(s, 0.5), Percentile(s, 0.95), s.LastOrDefault(double.NaN),
            s.Count > 0 ? s.Average() : double.NaN);
    }
}
