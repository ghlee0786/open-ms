using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Sim.Core.Engine;
using Sim.Core.Models;
using Sim.Core.Scenario;

namespace Sim.Core.Analysis;

public sealed class McRun
{
    public int Index { get; init; }
    public ulong Seed { get; init; }
    public double[] Values { get; init; } = [];
    public bool Landed { get; init; }
    public double N { get; init; }
    public double E { get; init; }
    public double Time { get; init; }
    public double Speed { get; init; }
    public double NoseAngleDeg { get; init; }
    public bool Suspect { get; init; }
    public string? Error { get; init; }
    /// <summary>충돌 판정 사용 시: 물체 간 접촉 수 · 낙하산 캐노피 간섭 수(FR-SHL-06 잔해 간섭).</summary>
    public int Contacts { get; init; }
    public int Interferences { get; init; }
    /// <summary>잔해 간섭에 의한 낙하산 전개 이상 수(FR-AN-03, collisions.chuteAnomaly 사용 시).</summary>
    public int Anomalies { get; init; }
}

public sealed class McResult
{
    public required string ImpactBody { get; init; }
    public required List<DispersionDef> Dispersions { get; init; }
    public required McRun[] Runs { get; init; }
    public required ImpactStats Stats { get; init; }
    public double ElapsedSeconds { get; init; }
    public string Backend { get; init; } = "cpu-full-6dof";

    public JsonObject ToJson() => new()
    {
        ["backend"] = Backend,
        ["impactBody"] = ImpactBody,
        ["runs"] = Runs.Length,
        ["landed"] = Runs.Count(r => r.Landed),
        ["failed"] = Runs.Count(r => !r.Landed || r.Error != null || r.Suspect),
        ["elapsed_s"] = ElapsedSeconds,
        ["runsPerSecond"] = Runs.Length / Math.Max(ElapsedSeconds, 1e-9),
        ["stats"] = Stats.ToJson(),
        ["debrisInterferenceProbability"] = Runs.Length == 0 ? 0 : Runs.Count(r => r.Error is null && r.Interferences > 0) / (double)Runs.Length,
        ["chuteAnomalyProbability"] = Runs.Length == 0 ? 0 : Runs.Count(r => r.Error is null && r.Anomalies > 0) / (double)Runs.Length,
        ["contactProbability"] = Runs.Length == 0 ? 0 : Runs.Count(r => r.Error is null && r.Contacts > 0) / (double)Runs.Length,
        ["dispersions"] = new JsonArray(Dispersions.Select(d => (JsonNode)new JsonObject { ["path"] = d.Path, ["dist"] = d.Dist, ["mode"] = d.Mode }).ToArray()),
    };

    public void WriteCsv(string path)
    {
        using var w = new StreamWriter(path);
        w.WriteLine("run,seed," + string.Join(",", Dispersions.Select(d => d.Path)) + ",landed,impact_n_m,impact_e_m,time_s,speed_mps,nose_angle_deg,suspect,contacts,interferences,anomalies,error");
        foreach (var r in Runs)
            w.WriteLine(FormattableString.Invariant(
                $"{r.Index},{r.Seed},{string.Join(",", r.Values.Select(v => v.ToString("G9", System.Globalization.CultureInfo.InvariantCulture)))}{(r.Values.Length > 0 ? "," : "")}{(r.Landed ? 1 : 0)},{r.N:F3},{r.E:F3},{r.Time:F3},{r.Speed:F3},{r.NoseAngleDeg:F2},{(r.Suspect ? 1 : 0)},{r.Contacts},{r.Interferences},{r.Anomalies},{r.Error?.Replace(',', ';')}"));
    }
}

/// <summary>
/// 전체 6DOF 엔진 Monte Carlo(CPU 멀티코어, FR-MC-01~04). 실행별 시드 = 마스터 시드 + 인덱스 → 단독 재현 가능.
/// </summary>
public static class MonteCarlo
{
    public static McResult Run(ScenarioDoc nominal, int runs, string impactBody, string sampling = "lhs", ulong seed = 1,
        IProgress<int>? progress = null, CancellationToken ct = default, int maxParallel = -1, Action<int, World>? inspect = null, bool record = false)
    {
        var defs = DispersionDef.Parse(nominal.Root["dispersions"] as JsonArray);
        var u = Sampler.Uniforms(runs, defs.Count, sampling, seed);
        var nominalValues = defs.Select(d => JsonPath.GetNumber(nominal.Root, d.Path) ?? 0).ToArray();
        var results = new McRun[runs];
        int done = 0;
        var sw = Stopwatch.StartNew();
        Parallel.For(0, runs, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = maxParallel }, i =>
        {
            ulong runSeed = seed * 1_000_003UL + (ulong)i;
            var values = new double[defs.Count];
            try
            {
                var doc = nominal.Clone();
                for (int d = 0; d < defs.Count; d++)
                {
                    values[d] = defs[d].Apply(nominalValues[d], defs[d].Sample(u[i][d]));
                    JsonPath.SetNumber(doc.Root, defs[d].Path, values[d]);
                }
                var sim = doc.Root["sim"] as JsonObject ?? new JsonObject();
                sim["seed"] = (double)runSeed;
                doc.Root["sim"] = sim;
                var world = doc.Build();
                world.Recorder.Enabled = record;   // 기록 프레임이 필요한 지표(텀블링 등)를 inspect 에서 쓰면 true
                world.Run(ct);
                inspect?.Invoke(i, world);   // 실행별 상세(예: 낙하산별 전개 이상) — 호출자가 스레드 안전하게 모은다
                var b = world.GetBody(impactBody);
                var imp = b.Root.Impact;
                var pos = imp is not null ? b.OwnCgWorld : b.OwnCgWorld;
                results[i] = new McRun
                {
                    Index = i, Seed = runSeed, Values = values, Landed = b.Root.Mode == BodyMode.Landed,
                    N = pos.X, E = pos.Y, Time = imp?.Time ?? world.T, Speed = imp?.Velocity.Norm ?? double.NaN,
                    NoseAngleDeg = imp?.NoseAngleDeg ?? double.NaN, Suspect = world.Suspect,
                    Contacts = world.Collisions?.ContactCount ?? 0, Interferences = world.Collisions?.InterferenceCount ?? 0,
                    Anomalies = world.Collisions?.AnomalyCount ?? 0,
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                results[i] = new McRun { Index = i, Seed = runSeed, Values = values, Error = ex.Message };
            }
            progress?.Report(Interlocked.Increment(ref done));
        });
        sw.Stop();
        var pts = results.Where(r => r.Landed && r.Error is null).Select(r => (r.N, r.E)).ToList();
        return new McResult
        {
            ImpactBody = impactBody, Dispersions = defs, Runs = results, Stats = ImpactStats.Compute(pts),
            ElapsedSeconds = sw.Elapsed.TotalSeconds,
        };
    }
}
