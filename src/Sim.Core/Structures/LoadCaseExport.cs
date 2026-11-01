using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Sim.Core.Engine;
using Sim.Core.Models;
using Sim.Core.Numerics;
using Sim.Core.Scenario;

namespace Sim.Core.Structures;

/// <summary>
/// FEA 하중 케이스 내보내기(FR-STR-05, 형식 정의): 물체별 최대 하중 순간(사출·분할 기폭·낙하산 개방·충돌·착지 직전·스텝 최대 G)의
/// 가속도·각속도·각가속도(프레임 차분), 연결점 힘, 표면 압력 분포 → loadcases.json + 면별 CSV.
/// 좌표: 동체 기준좌표(FRD, m). 이후 CalculiX·OpenCourant 연동은 이 형식을 입력으로 쓴다.
/// </summary>
public static class LoadCaseExport
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static JsonObject Export(World w, string outDir, IEnumerable<Body>? bodies = null, int maxPerBody = 8)
    {
        Directory.CreateDirectory(outDir);
        var frames = w.Recorder.Frames;
        var list = (bodies ?? w.Bodies.Where(b => b.Mode != BodyMode.Kinematic && b.Shape is not null)).ToList();
        var cases = new JsonArray();
        foreach (var b in list)
        {
            var model = StressModel.Build(b);
            if (model is null) continue;
            int k = 0;
            foreach (var (t, why, i) in Instants(w, b, maxPerBody))
            {
                var f = frames[i];
                var s = f.Bodies[b.Index];
                int i0 = Math.Max(0, i - 1), i1 = Math.Min(frames.Count - 1, i + 1);
                double dt = frames[i1].T - frames[i0].T;
                var acc = dt > 0 ? (frames[i1].Bodies[b.Index].Velocity - frames[i0].Bodies[b.Index].Velocity) / dt : Vec3.Zero;
                var alpha = dt > 0 ? (frames[i1].Bodies[b.Index].Omega - frames[i0].Bodies[b.Index].Omega) / dt : Vec3.Zero;
                var inp = model.InputFrom(w, f);
                var res = model.Evaluate(inp);
                var file = $"faces_{Safe(b.Name)}_{++k:D2}.csv";
                using (var sw = new StreamWriter(Path.Combine(outDir, file), false, new UTF8Encoding(false)))
                {
                    sw.WriteLine("face,cx_m,cy_m,cz_m,nx,ny,nz,area_m2,pressure_Pa,sigma_vm_Pa,state");
                    for (int j = 0; j < model.FaceCount; j++)
                    {
                        var c = model.Centroid[j]; var n = model.Normal[j];
                        sw.WriteLine(string.Format(Inv, "{0},{1:G7},{2:G7},{3:G7},{4:G6},{5:G6},{6:G6},{7:G6},{8:G6},{9:G6},{10}",
                            j, c.X, c.Y, c.Z, n.X, n.Y, n.Z, model.Area[j], res.Pressure[j], res.VonMises[j], res.State[j]));
                    }
                }
                cases.Add(new JsonObject
                {
                    ["body"] = b.Name, ["time_s"] = f.T, ["reason"] = why,
                    ["accelNonGravBody_mps2"] = J.ToJson(s.Q.InverseRotate(acc - w.Env.GravityVector)),
                    ["omegaBody_rps"] = J.ToJson(s.Omega),
                    ["alphaBody_rps2"] = J.ToJson(alpha),
                    ["airVelocityBody_mps"] = J.ToJson(inp.VAirBody),
                    ["density_kgpm3"] = inp.Rho, ["mach"] = inp.Mach,
                    ["pointLoads"] = new JsonArray(inp.PointLoads.Select(p => (JsonNode)new JsonObject
                    {
                        ["source"] = p.Source, ["point_brf_m"] = J.ToJson(p.Point), ["force_brf_N"] = J.ToJson(p.Force),
                    }).ToArray()),
                    ["totalAeroForce_brf_N"] = J.ToJson(res.TotalAeroForce),
                    ["maxPressure_Pa"] = res.MaxPressure, ["maxVonMises_Pa"] = res.MaxVonMises,
                    ["facesCsv"] = file,
                });
            }
        }
        var root = new JsonObject
        {
            ["schema"] = "mns.loadcases/1",
            ["frame"] = "body FRD (m), forces N, pressure Pa (relative to ambient)",
            ["note"] = "근사 하중(강체 동역학 + 수정 뉴턴 압력) — FEA 입력용, FEA 결과 아님",
            ["cases"] = cases,
        };
        File.WriteAllText(Path.Combine(outDir, "loadcases.json"), root.ToJsonString(J.Pretty), new UTF8Encoding(false));
        return root;
    }

    /// <summary>
    /// 물체의 하중 순간 후보(최대 G · 이벤트 직후 · 낙하산 최대 개방 하중 · 착지 직전) → (시각, 이유, 프레임 인덱스).
    /// 자유 비행 프레임만, 0.01 s 단위 중복 제거, 시간 순 최대 max 개. CalculiX 하중 케이스(FR-FEA-02)도 같은 목록을 쓴다.
    /// </summary>
    public static List<(double T, string Why, int Frame)> Instants(World w, Body b, int max = 8)
    {
        var frames = w.Recorder.Frames;
        var instants = new List<(double T, string Why)>();
        if (double.IsFinite(b.MaxLoadTime)) instants.Add((b.MaxLoadTime, $"max load {b.MaxLoadG:F1} G"));
        foreach (var e in w.EventLog)
            if (e.Name.StartsWith(b.Name + ".", StringComparison.Ordinal) || e.Message.Contains(b.Name, StringComparison.Ordinal))
                instants.Add((e.Time + 0.02, e.Name));
        foreach (var d in b.Devices.OfType<Parachute>())
            if (double.IsFinite(d.MaxForceTime)) instants.Add((d.MaxForceTime, $"{d.FullName} max opening load"));
        if (b.Impact is { } imp) instants.Add((imp.Time - 0.02, "pre-impact"));
        var res = new List<(double, string, int)>();
        foreach (var (t, why) in instants.OrderBy(x => x.T).DistinctBy(x => Math.Round(x.T, 2)).Take(max))
        {
            int i = w.Recorder.IndexAt(t);
            if (i < 0 || frames[i].Bodies[b.Index].Mode != BodyMode.Free) continue;
            res.Add((frames[i].T, why, i));
        }
        return res;
    }

    private static string Safe(string n) => string.Concat(n.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
}
