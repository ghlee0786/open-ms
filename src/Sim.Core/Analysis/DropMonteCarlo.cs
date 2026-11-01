using System.Diagnostics;
using System.Text.Json.Nodes;
using Sim.Core.Environment;
using Sim.Core.Scenario;

namespace Sim.Core.Analysis;

/// <summary>DropParams 배열을 실행하는 백엔드(CPU 병렬 / GPU).</summary>
public interface IDropBackend
{
    string Name { get; }
    DropResult[] Run(DropParams[] ps);
}

public sealed class CpuDropBackend : IDropBackend
{
    public string Name => $"CPU Parallel ({System.Environment.ProcessorCount} threads)";
    public DropResult[] Run(DropParams[] ps)
    {
        var r = new DropResult[ps.Length];
        Parallel.For(0, ps.Length, i => r[i] = DropModel.Simulate(ps[i]));
        return r;
    }
}

/// <summary>
/// 시나리오의 "gpuMonteCarlo" 절 → DropParams 표본 생성 → 백엔드 실행 → 통계.
/// 분산 경로는 gpuMonteCarlo.model 내부 필드명(mass, cdA, chute.cdS, release.altitude, wind.speed 등).
/// </summary>
public static class DropMonteCarlo
{
    public sealed class Output
    {
        public required DropParams[] Params { get; init; }
        public required DropResult[] Results { get; init; }
        public required ImpactStats Stats { get; init; }
        public required string Backend { get; init; }
        public double ElapsedSeconds { get; init; }
        public double KernelSeconds { get; init; }
        public required List<DispersionDef> Dispersions { get; init; }
        public required double[][] Values { get; init; }

        public JsonObject ToJson() => new()
        {
            ["backend"] = Backend,
            ["model"] = "3DOF 점질량 + 낙하산 충만 곡선 (DropModel)",
            ["runs"] = Results.Length,
            ["landed"] = Results.Count(r => r.Landed == 1),
            ["elapsed_s"] = ElapsedSeconds,
            ["kernel_s"] = KernelSeconds,
            ["runsPerSecond"] = Results.Length / Math.Max(KernelSeconds, 1e-9),
            ["stats"] = Stats.ToJson(),
            ["meanFlightTime_s"] = Results.Where(r => r.Landed == 1).Select(r => r.T).DefaultIfEmpty(double.NaN).Average(),
            ["meanImpactSpeed_mps"] = Results.Where(r => r.Landed == 1).Select(r => r.Speed).DefaultIfEmpty(double.NaN).Average(),
            ["maxChuteForce_N"] = Results.Select(r => r.MaxChuteForce).DefaultIfEmpty(0).Max(),
        };

        public void WriteCsv(string path)
        {
            using var w = new StreamWriter(path);
            w.WriteLine("run," + string.Join(",", Dispersions.Select(d => d.Path)) + (Dispersions.Count > 0 ? "," : "") + "landed,impact_n_m,impact_e_m,time_s,speed_mps,max_load_g,max_chute_force_N");
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            for (int i = 0; i < Results.Length; i++)
            {
                var r = Results[i];
                w.WriteLine(string.Join(",", new[] { i.ToString(inv) }.Concat(Values[i].Select(v => v.ToString("G9", inv)))
                    .Concat([r.Landed.ToString(inv), r.N.ToString("F3", inv), r.E.ToString("F3", inv), r.T.ToString("F3", inv),
                        r.Speed.ToString("F3", inv), r.MaxLoadG.ToString("F3", inv), r.MaxChuteForce.ToString("F1", inv)])));
            }
        }
    }

    /// <summary>gpuMonteCarlo.model JSON → 공칭 DropParams.</summary>
    public static DropParams Nominal(JsonObject model, JsonObject? environment)
    {
        var env = environment ?? new JsonObject();
        var wind = env["wind"] as JsonObject ?? new JsonObject();
        var chute = model["chute"] as JsonObject ?? new JsonObject();
        var rel = model["release"] as JsonObject ?? new JsonObject();
        double heading = J.Num(rel, "heading", 0) * Math.PI / 180;
        double speed = J.Num(rel, "speed", 0);
        var wv = MeanWind.FromSpeedDir(J.Num(wind, "speed", 0), J.Num(wind, "directionFrom", 270));
        double d0 = J.Num(chute, "d0", 0);
        double cds = J.NumOpt(chute, "cdS") ?? J.Num(chute, "cd0", 0) * Math.PI / 4 * d0 * d0;
        return new DropParams
        {
            Mass = J.NumReq(model, "mass", "gpuMonteCarlo.model"),
            CdA = J.Num(model, "cdA", 0.05),
            ChuteCdS = cds,
            ChuteDelay = J.Num(chute, "delay", 1.0),
            ChuteLineStretch = J.Num(chute, "lineStretchTime", 0.3),
            ChuteFillDistance = J.Num(chute, "fillConstant", 8) * Math.Max(d0, 0.1),
            ChuteFillExponent = (int)J.Num(chute, "fillExponent", 2),
            ChuteInitialFill = J.Num(chute, "initialFill", 0.02),
            N0 = J.Num(rel, "n", 0), E0 = J.Num(rel, "e", 0), Alt0 = J.Num(rel, "altitude", 300),
            Vn0 = speed * Math.Cos(heading), Ve0 = speed * Math.Sin(heading), Vd0 = J.Num(rel, "vd", 0),
            WindN = wv.X, WindE = wv.Y,
            WindRefHeight = J.Num(wind, "refHeight", 10), WindRoughness = J.Num(wind, "roughness", 0.03),
            WindShearTop = J.Num(wind, "shearTop", 300),
            TemperatureOffset = J.Num(env, "temperatureOffset", 0), GroundAltitude = J.Num(env, "groundAltitude", 0),
            Gravity = J.Num(env, "gravity", Atmosphere.G0),
            Dt = J.Num(model, "dt", 0.01), TMax = J.Num(model, "tMax", 900),
        };
    }

    /// <summary>
    /// 분산 경로를 단순 모델 JSON 에 적용해 표본 DropParams 생성. 경로 접두사 "model." 또는 "environment." 사용.
    /// </summary>
    public static (DropParams[] Params, double[][] Values, List<DispersionDef> Defs) Sample(ScenarioDoc doc, int runs, ulong seed, string sampling)
    {
        var mc = doc.Root["gpuMonteCarlo"] as JsonObject ?? throw new InvalidDataException("시나리오에 gpuMonteCarlo 절이 없습니다.");
        var defs = DispersionDef.Parse(mc["dispersions"] as JsonArray);
        var baseObj = new JsonObject
        {
            ["model"] = mc["model"]!.DeepClone(),
            ["environment"] = doc.Root["environment"]?.DeepClone() ?? new JsonObject(),
        };
        // 경로에 없는 선택 필드(예: environment.wind.speed) 를 위해 기본 구조 보장
        var env = baseObj["environment"]!.AsObject();
        if (env["wind"] is null) env["wind"] = new JsonObject { ["speed"] = 0.0, ["directionFrom"] = 270.0 };
        var u = Sampler.Uniforms(runs, defs.Count, sampling, seed);
        var nominal = defs.Select(d => JsonPath.GetNumber(baseObj, d.Path) ?? 0).ToArray();
        var ps = new DropParams[runs];
        var values = new double[runs][];
        var nominalParams = Nominal(baseObj["model"]!.AsObject(), env);
        for (int i = 0; i < runs; i++)
        {
            values[i] = new double[defs.Count];
            if (defs.Count == 0) { ps[i] = nominalParams; continue; }
            var o = baseObj.DeepClone().AsObject();
            for (int d = 0; d < defs.Count; d++)
            {
                values[i][d] = defs[d].Apply(nominal[d], defs[d].Sample(u[i][d]));
                JsonPath.SetNumber(o, defs[d].Path, values[i][d]);
            }
            ps[i] = Nominal(o["model"]!.AsObject(), o["environment"]!.AsObject());
        }
        return (ps, values, defs);
    }

    public static Output Run(ScenarioDoc doc, int runs, IDropBackend backend, ulong seed = 7, string sampling = "lhs")
    {
        var sw = Stopwatch.StartNew();
        var (ps, values, defs) = Sample(doc, runs, seed, sampling);
        var ks = Stopwatch.StartNew();
        var res = backend.Run(ps);
        ks.Stop();
        var pts = res.Where(r => r.Landed == 1).Select(r => (r.N, r.E)).ToList();
        sw.Stop();
        return new Output
        {
            Params = ps, Results = res, Stats = ImpactStats.Compute(pts), Backend = backend.Name,
            ElapsedSeconds = sw.Elapsed.TotalSeconds, KernelSeconds = ks.Elapsed.TotalSeconds, Dispersions = defs, Values = values,
        };
    }
}
