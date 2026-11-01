using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Sim.Core.Engine;
using Sim.Core.Fea;
using Sim.Core.Models;
using Sim.Core.Numerics;
using Sim.Core.Scenario;
using Sim.Core.Structures;

namespace Sim.Core.Analysis;

/// <summary>운용 시나리오 보고서 옵션(FR-AN-07).</summary>
public sealed class OperationalReportOptions
{
    public required string OutDir { get; init; }
    /// <summary>원자료(시간이력·요약) 폴더 — 보고서와 분리(git 제외 results/).</summary>
    public string? RawDir { get; init; }
    public int McRuns { get; init; } = 64;
    public ulong McSeed { get; init; } = 91;
    /// <summary>MC·민감도 실행의 종료 시각 [s] — 전개 이상은 박스 낙하산(~30 s)까지면 충분.</summary>
    public double ShortTEnd { get; init; } = 40;
    /// <summary>민감도: 컨테이너 노출 → 뚜껑·드로그 지연 [s].</summary>
    public double[] DrogueDelays { get; init; } = [0.05, 0.1, 0.2, 0.3, 0.5, 0.75, 1.0, 1.5, 2.0];
    public bool Ccx { get; init; }
    public string? CcxInstall { get; init; }
    public Action<string>? Log { get; init; }
    /// <summary>재현 명령(보고서 머리말).</summary>
    public string Command { get; init; } = "";
}

/// <summary>
/// 애니메이션 기준 실모델 운용 시나리오 분석 보고서(PRD AA절 FR-AN-02~07): 공칭 실행 + 변형(09b) 비교 + 드로그 지연 민감도 +
/// Monte Carlo 전개 이상 확률 + 단계별 근사 응력(W절) + 선택 CalculiX(Z절, 실모델 STEP) → Markdown + SVG.
/// 시나리오 지표(metrics)는 시나리오 파일의 정의를 그대로 쓴다(Results.Summary).
/// </summary>
public static class OperationalReport
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public sealed record StageStress(string Stage, string Body, double Time, double VonMisesMPa, double MinSafety, Vec3 At, string Dominant, string State, double LoadG);
    public sealed record SweepRow(double Delay, double DrogueTime, int Interferences, int Anomalies, double MinDebrisDistance, string NearestDebris, double DrogueG, double AoaAtDrogue, bool Tumbling, string Effects);
    public sealed record CcxRow(string Body, string Case, double Time, double VonMisesMPa, Vec3 At, double MinSafety, double DispMm, double ApproxMPa, string Mesh, string Note);

    public sealed class Result
    {
        public required World World { get; init; }
        public required JsonObject Summary { get; init; }
        public JsonObject? SummaryB { get; init; }
        public List<StageStress> Stages { get; } = new();
        public List<SweepRow> Sweep { get; } = new();
        public McResult? Mc { get; set; }
        /// <summary>MC 실행별 낙하산 전개 이상(장치 → 잔해 목록).</summary>
        public Dictionary<int, List<(string Device, string Debris, string Phase)>> McAnomalies { get; } = new();
        public List<CcxRow> Ccx { get; } = new();
        public List<string> CcxNotes { get; } = new();
        public string Markdown { get; set; } = "";
    }

    private static double Ev(World w, string name) => w.FiredEvents.TryGetValue(name, out var t) ? t : double.NaN;

    public static Result Generate(ScenarioDoc doc, ScenarioDoc? variant, OperationalReportOptions o)
    {
        Directory.CreateDirectory(o.OutDir);
        void L(string s) => o.Log?.Invoke(s);
        L($"공칭 실행: {doc.Name}");
        var w = doc.Build();
        w.Run();
        var structural = StructuralAnalysis.Analyze(w, PayloadBodies(w));
        var summary = Results.Summary(doc, w);
        summary["structural"] = structural.ToJson();
        if (o.RawDir is { } raw) { Results.WriteAll(doc, w, raw); File.WriteAllText(Path.Combine(raw, "structural.json"), structural.ToJson().ToJsonString(J.Pretty)); }
        JsonObject? sumB = null;
        World? wb = null;
        if (variant is not null)
        {
            L($"변형 실행: {variant.Name}");
            wb = variant.Build(); wb.Run();
            sumB = Results.Summary(variant, wb);
            if (o.RawDir is { } raw2) Results.WriteAll(variant, wb, Path.Combine(raw2, "variant"));
        }
        var res = new Result { World = w, Summary = summary, SummaryB = sumB };
        res.Stages.AddRange(Stages(w));
        L("민감도(드로그 지연) 실행");
        res.Sweep.AddRange(Sweep(doc, o));
        if (o.McRuns > 0)
        {
            L($"Monte Carlo {o.McRuns}회");
            var mdoc = doc.Clone();
            JsonPath.SetNumber(mdoc.Root, "sim.tEnd", o.ShortTEnd);
            res.Mc = MonteCarlo.Run(mdoc, o.McRuns, "container", "lhs", o.McSeed, inspect: (i, mw) =>
            {
                var l = mw.Bodies.SelectMany(b => b.Devices.OfType<Parachute>()).SelectMany(ch => ch.Anomalies.Select(a => (ch.FullName, a.Debris, a.Phase))).ToList();
                lock (res.McAnomalies) res.McAnomalies[i] = l;
            });
        }
        if (o.Ccx) RunCcx(res, w, o);
        WriteFigures(res, wb, o.OutDir);
        res.Markdown = Markdown(doc, variant, res, structural, o);
        File.WriteAllText(Path.Combine(o.OutDir, "README.md"), res.Markdown, new UTF8Encoding(false));
        return res;
    }

    private static IEnumerable<Body> PayloadBodies(World w) =>
        w.Bodies.Where(b => b.Mode != BodyMode.Kinematic && b.Shape is not null && b.Structure is not null
                            && (b.Name == "container" || b.Name.StartsWith("box", StringComparison.Ordinal)));

    // ---------------- 단계별 근사 응력(FR-AN-04) ----------------

    /// <summary>단계 창(시작 이벤트, 길이) 안에서 근사 von Mises 최대 프레임.</summary>
    public static StageStress? StageMax(World w, Body b, string stage, double t0, double t1)
    {
        if (!double.IsFinite(t0)) return null;
        var model = StressModel.Build(b);
        if (model is null) return null;
        StressResult? best = null; double bt = double.NaN; double bg = 0;
        foreach (var f in w.Recorder.Frames)
        {
            if (f.T < t0 - 1e-9 || f.T > t1 + 1e-9 || f.Bodies[b.Index].Mode != BodyMode.Free) continue;
            var r = model.Evaluate(model.InputFrom(w, f));
            if (best is null || r.MaxVonMises > best.MaxVonMises)
            {
                best = new StressResult { MaxVonMises = r.MaxVonMises, MinSafety = r.MinSafety, MaxFace = r.MaxFace, DominantAtMax = r.DominantAtMax, Worst = r.Worst };
                bt = f.T; bg = f.Bodies[b.Index].LoadG;
            }
        }
        if (best is null) return null;
        var at = best.MaxFace >= 0 ? model.Centroid[best.MaxFace] : Vec3.Zero;
        return new StageStress(stage, b.Name, bt, best.MaxVonMises / 1e6, best.MinSafety, at, best.DominantAtMax.ToString(), best.Worst.ToString(), bg);
    }

    public static List<StageStress> Stages(World w)
    {
        var list = new List<StageStress>();
        void Add(string body, string stage, double t0, double len)
        {
            if (!w.BodyMap.TryGetValue(body, out var b)) return;
            if (StageMax(w, b, stage, t0, t0 + len) is { } s) list.Add(s);
        }
        string exposed = w.FiredEvents.ContainsKey("container_exposed") ? "container_exposed" : "container_drop";
        Add("container", "① 탄체 분리 충격(기폭·노출)", Ev(w, exposed), 0.06);
        Add("container", "② 드로그 개방", Ev(w, "lid_drogue"), 2.0);
        Add("container", "③ 주낙하산 개방", Ev(w, "main_deploy"), 3.0);
        Add("container", "④ 박스1 사출 반작용", Ev(w, "eject1"), 0.06);
        Add("box1", "④ 박스1 사출 충격", Ev(w, "eject1"), 0.06);
        Add("box1", "⑤ 박스 낙하산 개방", Ev(w, "box1_chute"), 2.0);
        Add("box1", "⑥ 셸 이탈·테더 스냅", Ev(w, "rail1.exit"), 1.2);
        if (!w.BodyMap.ContainsKey("box1") && double.IsFinite(Ev(w, "rail1.exit")))
        {
            // 박스 없는 구성: 드론이 레일로 도어 밖에 나와 테더에 매달리는 순간
            Add("container", "④ 드론1 출구·테더 스냅 반작용", Ev(w, "rail1.exit"), 1.2);
            Add("drone1", "⑥ 드론1 테더 스냅", Ev(w, "rail1.exit"), 1.2);
        }
        if (w.BodyMap.TryGetValue("container", out var c) && c.Impact is { } ci) Add("container", "⑦ 착지 직전", ci.Time - 1.0, 1.0);
        if (w.BodyMap.TryGetValue("box1", out var b1) && b1.Impact is { } bi) Add("box1", "⑦ 착지 직전", bi.Time - 1.0, 1.0);
        return list;
    }

    // ---------------- 드로그 지연 민감도(FR-AN-03) ----------------

    public static List<SweepRow> Sweep(ScenarioDoc doc, OperationalReportOptions o)
    {
        var rows = new SweepRow[o.DrogueDelays.Length];
        Parallel.For(0, rows.Length, i =>
        {
            var d = doc.Clone();
            JsonPath.SetNumber(d.Root, "sim.tEnd", Math.Min(o.ShortTEnd, 20));
            JsonPath.SetNumber(d.Root, "events.lid_drogue.when.delay", o.DrogueDelays[i]);
            var w = d.Build(); w.Run();
            var ch = w.GetBody("container").Devices.OfType<Parachute>().First(p => p.Name == "drogue");
            var debris = (w.Collisions?.Bodies ?? new HashSet<string>()).Where(n => n is "nose" or "tail" or "panel" || n.StartsWith("petal", StringComparison.Ordinal))
                .Select(w.GetBody).ToList();
            double best = double.PositiveInfinity; string who = "";
            foreach (var f in w.Recorder.Frames)
            {
                if (f.T < ch.DeployTime || f.T > ch.DeployTime + 3) continue;
                var s = f.Devices.FirstOrDefault(x => x.Name == ch.FullName);
                if (s.Name is null) continue;
                foreach (var b in debris)
                {
                    var bs = f.Bodies[b.Index];
                    if (bs.Mode == BodyMode.Stowed) continue;
                    // 캐노피 중심·결속점 선분까지 거리(라인 영역 포함)
                    double dist = SegDist(bs.Cg, s.Anchor, s.Visible ? s.Tip : s.Anchor) - (b.Shape?.BoundingRadius ?? 0);
                    if (dist < best) { best = dist; who = b.Name; }
                }
            }
            var tum = new JsonObject();
            var tdoc = new JsonObject { ["type"] = "tumbling", ["bodies"] = new JsonArray("container"), ["from"] = w.FiredEvents.ContainsKey("container_exposed") ? "container_exposed" : "container_drop", ["to"] = "lid_drogue", ["attitudeAt"] = new JsonArray("lid_drogue") };
            var tm = OperationalMetrics.TryCompute("tumbling", tdoc, w, ["container"])!;
            var tb = tm["bodies"]![0]!;
            double aoa = tb["atEvents"]?["lid_drogue"]?["totalAoA_deg"] is JsonValue av ? av.GetValue<double>() : double.NaN;
            rows[i] = new SweepRow(o.DrogueDelays[i], ch.DeployTime, w.Collisions?.Records.Count(r => r.B == ch.FullName && r.Kind != "anomaly") ?? 0,
                ch.Anomalies.Count, double.IsFinite(best) ? Math.Max(best, 0) : double.NaN, who, ch.MaxG, aoa,
                tb["tumbling"]!.GetValue<bool>(), string.Join("; ", ch.Anomalies.Select(a => $"{a.Debris}:{a.Effect}")));
        });
        return rows.ToList();
    }

    private static double SegDist(Vec3 p, Vec3 a, Vec3 b)
    {
        var ab = b - a; double l2 = ab.NormSq;
        double t = l2 > 1e-12 ? Math.Clamp(Vec3.Dot(p - a, ab) / l2, 0, 1) : 0;
        return (p - (a + ab * t)).Norm;
    }

    // ---------------- CalculiX(FR-AN-04, Z절) ----------------

    private static void RunCcx(Result res, World w, OperationalReportOptions o)
    {
        var inst = CalculixInstall.TryLocate(o.CcxInstall, out var why);
        if (inst is null) { res.CcxNotes.Add($"CalculiX 미실행: {why}"); return; }
        foreach (var name in new[] { "container", "box1" })
        {
            if (!w.BodyMap.TryGetValue(name, out var b) || b.Shape is null) continue;
            var display = StressModel.DisplayMesh(b.Shape);
            string? cad = b.ModelPath is { } mp && File.Exists(Path.ChangeExtension(mp, null).Replace(".massprops", "") + ".step")
                ? Path.ChangeExtension(mp, null).Replace(".massprops", "") + ".step" : null;
            var dir = Path.GetFullPath(Path.Combine(o.RawDir ?? Path.Combine(Path.GetTempPath(), "mns_report"), $"ccx_{name}"));
            BodyFeModel fm;
            try { fm = CcxBodyFea.BuildModel(b, display, dir, null, "auto", cad); }
            catch (Exception ex) { res.CcxNotes.Add($"{name}: FE 모델 실패 — {ex.Message}"); continue; }
            var model = StressModel.Build(b, display)!;
            var stages = res.Stages.Where(s => s.Body == name && !s.Stage.StartsWith("⑦", StringComparison.Ordinal)).OrderByDescending(s => s.VonMisesMPa).Take(2).ToList();
            foreach (var st in stages)
            {
                int fi = w.Recorder.IndexAt(st.Time);
                var lc = CcxBodyFea.FromWorld(w, model, fi, st.Stage);
                var cdir = Path.Combine(dir, $"case_{res.Ccx.Count}");
                o.Log?.Invoke($"CalculiX {name} {st.Stage} (절점 {fm.Mesh.Nodes.Count:N0})");
                var (run, r) = CcxBodyFea.Solve(inst, fm, CcxAnalysis.Static, lc, cdir);
                if (!run.Ok || r is null) { res.CcxNotes.Add($"{name} {st.Stage}: CalculiX 실패 — {run.Error}"); continue; }
                res.Ccx.Add(new CcxRow(name, st.Stage, st.Time, r.MaxVonMises / 1e6, r.MaxVonMisesAt, r.MinSafety, r.MaxDisplacement * 1e3, st.VonMisesMPa,
                    $"{fm.Mesh.Source} {fm.Mesh.Elements.Count:N0}요소", $"{run.Seconds:F0} s"));
            }
            res.CcxNotes.AddRange(fm.Notes.Select(n => $"{name}: {n}"));
        }
    }

    // ---------------- 그림 ----------------

    private static List<(double, double)> BodySeries(World w, string body, Func<BodySample, double> f, double t0 = 0, double t1 = double.PositiveInfinity)
    {
        if (!w.BodyMap.TryGetValue(body, out var b)) return new();
        return w.Recorder.Frames.Where(x => x.T >= t0 && x.T <= t1 && x.Bodies[b.Index].Mode == BodyMode.Free)
            .Select(x => (x.T, f(x.Bodies[b.Index]))).ToList();
    }

    private static List<(double, double)> DeviceSeries(World w, string dev, double t0 = 0, double t1 = double.PositiveInfinity) =>
        w.Recorder.Frames.Where(x => x.T >= t0 && x.T <= t1).Select(x => (x.T, x.Devices.FirstOrDefault(d => d.Name == dev) is { Name: not null } s ? s.Force : double.NaN)).ToList();

    private static List<(double, double)> TetherSeries(World w, string con, double t0, double t1)
    {
        int ci = w.Connectors.FindIndex(c => c.Name == con);
        return ci < 0 ? new() : w.Recorder.Frames.Where(x => x.T >= t0 && x.T <= t1 && ci < x.Connectors.Length).Select(x => (x.T, x.Connectors[ci].Tension)).ToList();
    }

    private static void WriteFigures(Result res, World? wb, string dir)
    {
        var w = res.World;
        double ts = w.FiredEvents.Values.DefaultIfEmpty(0).Min();
        double tMain = Ev(w, "main_deploy");
        double e0 = double.IsFinite(Ev(w, "split_detonation")) ? Ev(w, "split_detonation") : ts;
        void Save(string file, string svg) => File.WriteAllText(Path.Combine(dir, file), svg, new UTF8Encoding(false));
        var sp = new List<SvgPlot.Series>
        {
            new("09 컨테이너 총받음각 [°]", BodySeries(w, "container", s => OperationalMetrics.TotalAoaDeg(s), e0, e0 + 8)),
            new("09 각속도 [°/s]", BodySeries(w, "container", s => s.Omega.Norm * 180 / Math.PI, e0, e0 + 8)),
        };
        if (wb is not null)
        {
            double eb = Ev(wb, "panel_open");
            sp.Add(new("09b 총받음각 [°]", BodySeries(wb, "container", s => OperationalMetrics.TotalAoaDeg(s), eb, eb + 8).Select(p => (p.Item1 - eb + e0, p.Item2)).ToList()));
            sp.Add(new("09b 각속도 [°/s]", BodySeries(wb, "container", s => s.Omega.Norm * 180 / Math.PI, eb, eb + 8).Select(p => (p.Item1 - eb + e0, p.Item2)).ToList()));
        }
        Save("fig1_container_attitude.svg", SvgPlot.Line("분리 직후 컨테이너 자세 (FR-AN-02)", "시간 [s] (09b 는 개방 시각 정렬)", "각 [°] · 각속도 [°/s]", sp,
            note: $"드로그 {Ev(w, "lid_drogue"):F2} s · 주낙하산 {tMain:F2} s"));
        Save("fig2_container_speed.svg", SvgPlot.Line("컨테이너 대기속도 · 고도 변화 (FR-AN-02/05)", "시간 [s]", "대기속도 [m/s]",
            [new("대기속도", BodySeries(w, "container", s => s.Airspeed, e0, e0 + 40))]));
        // 잔해 – 드로그 거리
        var ch = w.BodyMap.TryGetValue("container", out var cb) ? cb.Devices.OfType<Parachute>().FirstOrDefault(p => p.Name == "drogue") : null;
        if (ch is not null && double.IsFinite(ch.DeployTime))
        {
            var deb = new[] { "nose", "tail", "petal1", "petal2", "petal3", "lid" }.Where(w.BodyMap.ContainsKey).ToList();
            var ser = deb.Select(n => new SvgPlot.Series(n, w.Recorder.Frames.Where(f => f.T >= ch.DeployTime - 0.5 && f.T <= ch.DeployTime + 3)
                .Select(f =>
                {
                    var s = f.Devices.FirstOrDefault(x => x.Name == ch.FullName);
                    var bs = f.Bodies[w.GetBody(n).Index];
                    return (f.T, bs.Mode == BodyMode.Stowed || s.Name is null ? double.NaN : SegDist(bs.Cg, s.Anchor, s.Visible ? s.Tip : s.Anchor));
                }).ToList())).ToList();
            Save("fig3_debris_drogue_distance.svg", SvgPlot.Line("잔해 – 드로그 영역(결속점→캐노피) 거리 (FR-AN-03)", "시간 [s]", "거리 [m]", ser, yMin: 0,
                note: $"드로그 전개 {ch.DeployTime:F2} s, 위험 거리 5 m 기준"));
        }
        if (res.Sweep.Count > 0)
            Save("fig4_sweep_drogue_delay.svg", SvgPlot.Line("드로그 지연 민감도: 잔해 최근접 거리 (FR-AN-03)", "노출 → 드로그 지연 [s]", "최근접 거리 [m]",
                [new("최근접 잔해 거리", res.Sweep.Select(r => (r.Delay, r.MinDebrisDistance)).ToList(), Markers: true)], yMin: 0));
        if (double.IsFinite(tMain))
        {
            var swing = BodySeries(w, "container", s => Math.Acos(Math.Clamp(-s.Q.Rotate(new Vec3(-1, 0, 0)).Z, -1, 1)) * 180 / Math.PI, tMain, tMain + 40);
            Save("fig5_container_pendulum.svg", SvgPlot.Line("주낙하산 이후 컨테이너 진자각 · 박스 사출 반응 (FR-AN-05)", "시간 [s]", "매달림 축 – 연직 [°]",
                [new("진자각", swing)], yMin: 0, note: $"하부 도어 {Ev(w, "door_open"):F1} s, 사출 {string.Join(" · ", Enumerable.Range(1, 4).Select(k => Ev(w, $"eject{k}").ToString("F1", Inv)))} s"));
        }
        double tE = Ev(w, "eject1");
        if (double.IsFinite(tE))
        {
            var forces = new List<SvgPlot.Series>();
            for (int k = 1; k <= 4; k++) forces.Add(new($"box{k} 낙하산 [N]", DeviceSeries(w, $"box{k}.chute", tE, tE + 14)));
            Save("fig6_box_chute_force.svg", SvgPlot.Line("박스 낙하산 개방 하중 (FR-AN-06)", "시간 [s]", "힘 [N]", forces, yMin: 0));
            var teth = new List<SvgPlot.Series>();
            for (int k = 1; k <= 4; k++) teth.Add(new($"tether{k} [N]", TetherSeries(w, $"tether{k}", tE, tE + 16)));
            Save("fig7_tether_tension.svg", SvgPlot.Line("드론 테더 장력 (셸 이탈 → 매달림 → 절단, FR-AN-06)", "시간 [s]", "장력 [N]", teth, yMin: 0));
        }
        Save("fig8_container_altitude.svg", SvgPlot.Line("컨테이너 · 박스1 · 드론1 · 센서1 고도", "시간 [s]", "AGL [m]",
            new[] { "container", "box1", "drone1", "sensor1" }.Where(w.BodyMap.ContainsKey).Select(n => new SvgPlot.Series(n, BodySeries(w, n, s => w.Env.Agl(s.Cg.Z)))).ToList(), yMin: 0));
    }

    // ---------------- Markdown ----------------

    private static string N(JsonNode? n, string fmt = "F2") => n is JsonValue v && v.TryGetValue(out double d) ? d.ToString(fmt, Inv) : n is JsonValue v2 && v2.TryGetValue(out bool b) ? (b ? "예" : "아니오") : n?.ToString() ?? "—";

    public static (double Lo, double Hi) Wilson(int k, int n, double z = 1.96)
    {
        if (n == 0) return (double.NaN, double.NaN);
        double p = (double)k / n, d = 1 + z * z / n, c = p + z * z / (2 * n), h = z * Math.Sqrt(p * (1 - p) / n + z * z / (4.0 * n * n));
        return (Math.Max(0, (c - h) / d), Math.Min(1, (c + h) / d));
    }

    private static string Markdown(ScenarioDoc doc, ScenarioDoc? variant, Result res, StructuralReport structural, OperationalReportOptions o)
    {
        var w = res.World; var s = res.Summary; var m = s["metrics"]!.AsObject();
        var sb = new StringBuilder();
        void P(string line = "") => sb.AppendLine(line);
        var src = w.Bodies.Where(b => b.ModelSource is not null).GroupBy(b => b.ModelSource!).ToDictionary(g => g.Key, g => g.Count());
        bool real = src.ContainsKey("real");
        // 생성기 = 시나리오 설명의 'tools/scenario_gen/<이름>.py', 실모델 표기 = realModels.document(CAD 문서 이름)
        var genMatch = System.Text.RegularExpressions.Regex.Match(doc.Root["description"]?.GetValue<string>() ?? "", @"tools/scenario_gen/([\w.]+\.py)");
        string gen = genMatch.Success ? genMatch.Groups[1].Value : "—";
        string realLabel = $"실모델({doc.Root["realModels"]?["document"]?.GetValue<string>() ?? "CAD"} 그룹 내보내기, git 밖)";
        P($"# 시나리오 09 — 애니메이션 기준 실모델 운용 분석 (PRD AA절 FR-AN-01~07)");
        P();
        P($"생성: `{o.Command}` · {DateTime.Now:yyyy-MM-dd HH:mm} · 엔진 `{s["provenance"]?["engine"]}` · 시나리오 해시 `{doc.Hash()}` · 시드 {w.Seed}");
        P();
        P($"- 시나리오: `{doc.DisplayPath}`{(variant is null ? "" : $" · 변형 `{variant.DisplayPath}`")} (생성기 `tools/scenario_gen/{gen}`)");
        P($"- 형상·질량 출처: **{(real ? realLabel : "합성 대체(실모델 폴더 없음)")}** — 물체 {string.Join(", ", src.Select(kv => $"{kv.Key} {kv.Value}개"))}. 실설계 형상은 저장소에 넣지 않고 이 보고서는 수치 요약만 담는다(NFR-06).");
        P($"- 공력 L1(형상 근사·마하 보정), 낙하산 Knacke 충만 곡선, 충돌·간섭은 판정 중심 근사 — **시험 보정 전 사전 예측**이다. 응력은 W절 근사(FEA 아님){(res.Ccx.Count > 0 ? " + CalculiX 정적 해석(Z절)" : "")}.");
        P($"- 의도 근거: 운용 시나리오 영상 프레임 판독 + 발표 자료 (PRD AA절 표).");
        P();

        // ---- 0. 핵심 결과 ----
        var mb = m["massBudget"]; var tum = m["containerTumbling"]?["bodies"]?[0];
        var an = m["chuteAnomalies"]; var pend = m["containerPendulum"]?["bodies"]?[0];
        P("## 0. 핵심 결과");
        P();
        P("| 항목 | 값 | 판정·비고 |");
        P("|---|---|---|");
        P($"| 탑재 질량 합(컨테이너+뚜껑+도어+박스 4세트) | {N(mb?["total_kg"])} kg | 탑재 조건 {N(mb?["limit_kg"], "F0")} kg {(mb?["pass"] is JsonValue pv && pv.GetValue<bool>() ? "이내" : $"**초과 {N(mb?["excess_kg"])} kg**")} · 분리 전후 질량 보존 {N(mb?["conserved"])} |");
        P($"| 분리 직후 컨테이너 최대 각속도 / 누적 횡회전 | {N(tum?["maxRate_dps"], "F0")} °/s / {N(tum?["cumulativeTransverseRotation_deg"], "F0")}° | 텀블링(≥360°) {N(tum?["tumbling"])} · 최대 총받음각 {N(tum?["maxTotalAoA_deg"], "F1")}° |");
        P($"| 드로그 전개 순간 자세 | 총받음각 {N(tum?["atEvents"]?["lid_drogue"]?["totalAoA_deg"], "F1")}°, 대기속도 {N(tum?["atEvents"]?["lid_drogue"]?["airspeed_mps"], "F1")} m/s | 주낙하산 순간 총받음각 {N(tum?["atEvents"]?["main_deploy"]?["totalAoA_deg"], "F1")}°, 기수–연직 {N(tum?["atEvents"]?["main_deploy"]?["noseFromVerticalDown_deg"], "F0")}° |");
        P($"| 컨테이너 최대 하중 | {N(m["containerPeakG"]?["maxG"], "F1")} G | 설계 하중 {(m["containerPeakG"]?["pass"] is JsonValue cg && cg.GetValue<bool>() ? "이내" : "초과")} |");
        P($"| 박스 최대 하중 | {N(m["boxPeakG"]?["maxG"], "F1")} G ({m["boxPeakG"]?["body"]}) | 셸 이탈 후 테더 스냅이 지배(상판 단독 질량 기준) |");
        P($"| 낙하산 전개 이상(공칭) | {N(an?["total"], "F0")}건 / 영향 장치 {N(an?["affectedDevices"], "F0")} | 잔해 간섭 {N(s["collisions"]?["interferences"], "F0")}건 · 물체 접촉 {N(s["collisions"]?["contacts"], "F0")}건 |");
        if (res.Mc is { } mc)
        {
            int ok = mc.Runs.Count(r => r.Error is null), ka = mc.Runs.Count(r => r.Error is null && r.Anomalies > 0), ki = mc.Runs.Count(r => r.Error is null && r.Interferences > 0);
            var (lo, hi) = Wilson(ka, ok); var (lo2, hi2) = Wilson(ki, ok);
            P($"| MC 전개 이상 확률 ({ok}회) | {(double)ka / Math.Max(ok, 1):P1} (95% {lo:P1}–{hi:P1}) | 간섭 확률 {(double)ki / Math.Max(ok, 1):P1} ({lo2:P1}–{hi2:P1}) · 접촉 확률 {mc.Runs.Count(r => r.Error is null && r.Contacts > 0) / (double)Math.Max(ok, 1):P0} |");
        }
        P($"| 주낙하산 하강 진자 | 진폭 {N(pend?["amplitude_deg"], "F1")}° · RMS {N(pend?["rms_deg"], "F1")}° · 주기 {N(pend?["period_s"], "F2")} s | 도어 개방 전 구간 |");
        P($"| 박스–박스 / 캐노피 간 최소 간격 | {N(m["boxBoxClearance"]?["min_m"])} m ({m["boxBoxClearance"]?["pair"]}) / {N(m["canopyClearance"]?["minGap_m"])} m | 캐노피 겹침 {N(m["canopyClearance"]?["overlap"])} |");
        P($"| 드론–잔해(셸·도어·뚜껑) 최소 이격 | {N(m["droneDebrisClearance"]?["min_m"])} m ({m["droneDebrisClearance"]?["pair"]}) | 분리 직후 0.3 s 제외 |");
        P($"| 센서 착지 최소 수평 이격 | {N(m["sensorLandingSeparation"]?["minHorizontal_m"], "F1")} m | 목표 ≥ 70 m {(m["sensorLandingSeparation"]?["pass"] is JsonValue sp && sp.GetValue<bool>() ? "충족" : "미충족")} |");
        var sens = s["bodies"]?["sensor1"]?["impact"];
        if (sens is not null) P($"| 센서1 착지 | {N(sens["speed_mps"], "F1")} m/s, 노즈–연직 {N(sens["noseAngleFromVertical_deg"], "F1")}° | 관입 {N(sens["youngPenetration_m"], "F3")} m (Young, 질량 하한 밖 외삽) |");
        P();

        // ---- 1. 타임라인 ----
        P("## 1. 단계 타임라인 (영상 시각 대비)");
        P();
        P("영상 시각은 연출(키프레임)이고 시뮬레이션 시각은 물리 조건(속도 문턱·지연 가정)으로 정해진다. 센서 투하는 박스 낙하산 하강(≈ 7 m/s) 후 드론이 목표 상공 100 m 까지 내려오는 시간 때문에 영상(36 s)보다 훨씬 늦다 — 영상은 시간을 압축했다.");
        P();
        P("| 단계 | 이벤트 | 시뮬레이션 [s] | 영상 [s] | 차 [s] |");
        P("|---|---|---|---|---|");
        foreach (var ph in m["timeline"]?["phases"]?.AsArray() ?? new JsonArray())
            P($"| {ph!["phase"]} | `{ph["event"]}` | {N(ph["t_s"])} | {N(ph["video_s"], "F1")} | {(ph["delta_s"] is JsonValue dv && dv.TryGetValue(out double dd) ? ((Math.Round(dd, 2) + 0.0) >= 0 ? "+" : "") + (Math.Round(dd, 2) + 0.0).ToString("0.00", Inv) : "—")} |");
        P();
        P($"단계 순서(정의 순) 단조: {N(m["timeline"]?["ordered"])} · 누락 {N(m["timeline"]?["missing"], "F0")}. 그림: ![고도](fig8_container_altitude.svg)");
        P();

        // ---- 2. 분리 직후 ----
        P("## 2. 3분할 분리 직후 컨테이너 거동 (FR-AN-02)");
        P();
        P("![자세](fig1_container_attitude.svg)");
        P();
        P("![속도](fig2_container_speed.svg)");
        P();
        P("| 물체 | 최대 각속도 [°/s] | 누적 횡회전 [°] | 최대 총받음각 [°] | 텀블링 |");
        P("|---|---|---|---|---|");
        foreach (var key in new[] { "containerTumbling", "debrisTumbling" })
            foreach (var b in m[key]?["bodies"]?.AsArray() ?? new JsonArray())
                P($"| {b!["body"]} | {N(b["maxRate_dps"], "F0")} | {N(b["cumulativeTransverseRotation_deg"], "F0")} | {N(b["maxTotalAoA_deg"], "F1")} | {N(b["tumbling"])} |");
        P();
        P("해석: 절단 폭약 충격(15 G·10 ms)과 외피 조각의 접촉·충격량, 마하 0.5 동압(≈15.7 kPa)에서의 원통 횡류 모멘트가 컨테이너를 흔든다. 누적 횡회전이 360° 미만이면 '흔들림(oscillation)'이고 텀블링(뒤집힘 연속)은 아니다. 드로그(뒤끝 결속)가 펴지면 기수(도어) 쪽이 진행 방향으로 정렬되고, 감속과 함께 궤적이 수평 → 수직으로 돈다.");
        P();
        var hinges = s["connectors"]!.AsObject().Where(kv => kv.Value?["type"]?.ToString() == "hinge").ToList();
        if (hinges.Count > 0)
        {
            P("| 외피 힌지 | 이탈 시각 [s] | 이유 | 최대 반력 [N] |");
            P("|---|---|---|---|");
            foreach (var (n, h) in hinges) P($"| {n} | {N(h!["releaseTime_s"], "F3")} | {h["releaseReason"]} | {N(h["maxForce_N"], "F0")} |");
            P();
        }

        // ---- 3. 간섭·전개 이상 ----
        P("## 3. 파편 간섭에 의한 낙하산 전개 이상 (FR-AN-03)");
        P();
        P("판정 영역 = 캐노피 구(반경 D₀/2·max(충만비, 0.3)) ∪ 라인 원뿔(결속점 반경 0 → 캐노피 반경), 잔해 대표점 기준. 라인 신장~충만 중 진입 → 이상: 라인 신장 중 충만 지연 +0.3 s(라인 영역이면 CdS×0.85), 충만 중 부분 충만(상한 0.6), 개방 후 캐노피·라인 손상 CdS×0.85, 상대속도 2 m/s 미만은 기록만 — **계수는 모두 가정**(시험 근거 없음, 고장 모델 FR-PAR-05 와 같은 효과 체계).");
        P();
        P("![잔해 거리](fig3_debris_drogue_distance.svg)");
        P();
        var pairs = s["collisions"]?["pairs"]?.AsArray() ?? new JsonArray();
        if (pairs.Count > 0)
        {
            P("| 접촉·간섭 | 종류 | 횟수 | 처음 [s] | 최대 상대속도 [m/s] | 최대 충격량 [N·s] |");
            P("|---|---|---|---|---|---|");
            foreach (var p in pairs) P($"| {p!["a"]} ↔ {p["b"]} | {p["kind"]} | {p["count"]} | {N(p["firstTime_s"], "F3")} | {N(p["maxRelSpeed_mps"], "F1")} | {N(p["maxImpulse_Ns"], "F1")} |");
            P();
        }
        P("### 기폭(노출) → 드로그 전개 지연 민감도");
        P();
        P("![민감도](fig4_sweep_drogue_delay.svg)");
        P();
        P("| 지연 [s] | 드로그 전개 [s] | 최근접 잔해 | 간격(경계 구 − 드로그 영역) [m] | 간섭 | 이상 | 드로그 개방 [G] | 전개 순간 총받음각 [°] | 효과 |");
        P("|---|---|---|---|---|---|---|---|---|");
        foreach (var r in res.Sweep)
            P(string.Format(Inv, "| {0:F2} | {1:F2} | {2} | {3:F2} | {4} | {5} | {6:F1} | {7:F1} | {8} |", r.Delay, r.DrogueTime, r.NearestDebris, r.MinDebrisDistance, r.Interferences, r.Anomalies, r.DrogueG, r.AoaAtDrogue, r.Effects.Length == 0 ? "—" : r.Effects));
        P();
        P("해석: 지연이 짧으면 드로그가 아직 컨테이너 가까이에 있는 노즈·꼬리·외피 조각(감속이 빠른 가벼운 잔해가 뒤로 밀려옴) 사이로 펴진다. 지연이 길면 잔해가 멀어지지만 컨테이너가 더 오래 고속 자유비행(흔들림·감속)한다 — 표의 거리와 개방 G 로 절충점을 고른다.");
        P();
        if (res.Mc is { } mc2)
        {
            P($"### Monte Carlo ({mc2.Runs.Length}회, LHS, 시드 {o.McSeed}, 종료 {o.ShortTEnd:F0} s)");
            P();
            P($"분산: {string.Join(", ", mc2.Dispersions.Select(d => $"`{d.Path}` ({d.Dist}{(d.Mode == "add" ? "" : "·" + d.Mode)})"))}.");
            P();
            int ok = mc2.Runs.Count(r => r.Error is null);
            P($"| 지표 | 값 |");
            P("|---|---|");
            foreach (var (label, k) in new[] { ("전개 이상 ≥ 1건", mc2.Runs.Count(r => r.Error is null && r.Anomalies > 0)), ("잔해 간섭 ≥ 1건", mc2.Runs.Count(r => r.Error is null && r.Interferences > 0)), ("물체 접촉 ≥ 1건", mc2.Runs.Count(r => r.Error is null && r.Contacts > 0)) })
            {
                var (lo, hi) = Wilson(k, ok);
                P($"| {label} | {k}/{ok} = {(double)k / Math.Max(ok, 1):P1} (Wilson 95% {lo:P1}–{hi:P1}) |");
            }
            P($"| 실행 오류 | {mc2.Runs.Count(r => r.Error is not null)} |");
            P($"| 계산 시간 | {mc2.ElapsedSeconds:F1} s |");
            P();
            // 낙하산별: 이상이 난 실행 수 · 원인 잔해
            var byDev = res.McAnomalies.SelectMany(kv => kv.Value.Select(a => (Run: kv.Key, a.Device, a.Debris, a.Phase))).GroupBy(a => a.Device).OrderBy(g => g.Key).ToList();
            if (byDev.Count > 0)
            {
                P("| 낙하산 | 이상 실행 수 | 확률 (Wilson 95%) | 원인 잔해(건) | 단계 |");
                P("|---|---|---|---|---|");
                foreach (var g in byDev)
                {
                    int kr = g.Select(a => a.Run).Distinct().Count();
                    var (lo, hi) = Wilson(kr, ok);
                    P($"| {g.Key} | {kr} | {(double)kr / Math.Max(ok, 1):P1} ({lo:P1}–{hi:P1}) | {string.Join(", ", g.GroupBy(a => a.Debris).OrderByDescending(x => x.Count()).Select(x => $"{x.Key} {x.Count()}"))} | {string.Join(", ", g.Select(a => a.Phase).Distinct())} |");
                }
                P();
                int dRuns = byDev.Where(g => g.Key.StartsWith("container.", StringComparison.Ordinal)).SelectMany(g => g.Select(a => a.Run)).Distinct().Count();
                int bRuns = byDev.Where(g => !g.Key.StartsWith("container.", StringComparison.Ordinal)).SelectMany(g => g.Select(a => a.Run)).Distinct().Count();
                P($"해석: 컨테이너 드로그·주낙하산 이상 실행 {dRuns}회, 박스 낙하산 이상 실행 {bRuns}회. 박스 낙하산 이상의 원인은 뒤따라 사출된 박스·드론·셸(그리고 도어)이 앞 박스 캐노피·라인 영역을 지나는 것 — " +
                  "사출 간격(2 s)·드론 이탈 방향·박스 낙하산 지연이 설계 변수다. 드로그 이상은 위 민감도 표처럼 노출 직후(≲ 0.1 s) 전개에서만 나타난다.");
                P();
            }
            // 지연별 이상 비율(LHS 표본을 구간으로 묶음)
            int di = mc2.Dispersions.FindIndex(d => d.Path == "events.lid_drogue.when.delay");
            if (di >= 0)
            {
                P("| MC 드로그 지연 구간 [s] | 실행 | 간섭 | 전개 이상(전 낙하산) | 드로그 전개 이상 |");
                P("|---|---|---|---|---|");
                foreach (var (lo, hi) in new[] { (0.0, 0.6), (0.6, 1.0), (1.0, 1.4), (1.4, 9.0) })
                {
                    var g = mc2.Runs.Where(r => r.Error is null && r.Values[di] >= lo && r.Values[di] < hi).ToList();
                    if (g.Count == 0) continue;
                    int dro = g.Count(r => res.McAnomalies.TryGetValue(r.Index, out var l) && l.Any(a => a.Device == "container.drogue"));
                    P($"| {lo:F1}–{(hi > 5 ? "" : hi.ToString("F1", Inv))} | {g.Count} | {g.Count(r => r.Interferences > 0)} | {g.Count(r => r.Anomalies > 0)} | {dro} |");
                }
                P();
            }
        }

        // ---- 4. 응력 ----
        P("## 4. 단계별 응력 (FR-AN-04)");
        P();
        P("근사 응력(W절: 수정 뉴턴 압력 + 길이축 보 + 얇은 판 국부 굽힘 + 연결점 핫스팟, 관성 완화) — **FEA 아님**. 각 단계 창 안 최대 von Mises 프레임. 위치는 물체 동체 좌표(+x = 도어 쪽).");
        P();
        P("| 단계 | 물체 | 시각 [s] | 하중 [G] | σvm 최대 [MPa] | 위치 (x, y, z) [m] | 지배 성분 | 최소 SF | 상태 |");
        P("|---|---|---|---|---|---|---|---|---|");
        foreach (var st in res.Stages)
            P(string.Format(Inv, "| {0} | {1} | {2:F3} | {3:F1} | {4:F2} | ({5:F3}, {6:F3}, {7:F3}) | {8} | {9} | {10} |", st.Stage, st.Body, st.Time, st.LoadG, st.VonMisesMPa,
                st.At.X, st.At.Y, st.At.Z, st.Dominant, double.IsFinite(st.MinSafety) ? st.MinSafety.ToString("F2", Inv) : "—", st.State));
        P();
        foreach (var h in structural.Bodies.Where(h => h.Body.Name is "container" or "box1"))
        {
            var j = h.ToJson();
            P($"- {h.Body.Name} 전 구간: 최대 σvm {N(j["peakVonMises_MPa"])} MPa @ {N(j["peakVonMisesTime_s"], "F2")} s, 최소 SF {N(j["minSafetyFactor"])}, 최악 상태 {j["worstState"]}{(h.Impact is { } im ? $", 착지 {im.Verdict}" : "")}");
        }
        P();
        if (res.Ccx.Count > 0)
        {
            P("### CalculiX 정적 해석 (Z절, 하중 = 같은 시뮬레이션 순간의 면 압력 + 연결점 힘 + 관성 완화)");
            P();
            P("| 물체 | 하중 순간 | 시각 [s] | σvm 최대 [MPa] | 위치 [m] | 최소 SF | 변위 [mm] | 근사 [MPa] | 근사/FEA | 메시 | 계산 |");
            P("|---|---|---|---|---|---|---|---|---|---|---|");
            foreach (var c in res.Ccx)
                P(string.Format(Inv, "| {0} | {1} | {2:F3} | {3:F2} | ({4:F3}, {5:F3}, {6:F3}) | {7:F2} | {8:F3} | {9:F2} | {10:F2} | {11} | {12} |", c.Body, c.Case, c.Time, c.VonMisesMPa, c.At.X, c.At.Y, c.At.Z, c.MinSafety, c.DispMm, c.ApproxMPa, c.ApproxMPa / Math.Max(c.VonMisesMPa, 1e-12), c.Mesh, c.Note));
            P();
        }
        if (res.Ccx.Count > 0)
        {
            P("해석 주의: FE 모델은 그 물체 고유 질량만 갖는데(밀도 환산), 사출 펄스·라이저처럼 결합체 전체 질량에 걸리는 연결점 힘은 그대로 들어가므로 관성 완화 가속도가 커져 **보수적**이다 " +
              "(예: 박스 사출 펄스 = 결합체 전체 질량 × 15 G 를 상판 질량만으로 받음). 근사/FEA 비가 1 에서 먼 곳은 근사 응력의 판 굽힘·연결점 국부 굽힘 누락(FX-09 와 같은 종류) 또는 이 보수성 때문이다.");
            P();
        }
        foreach (var n in res.CcxNotes) P($"- {n}");
        if (res.CcxNotes.Count > 0) P();

        // ---- 5. 본낙하산 이후 ----
        P("## 5. 주낙하산 이후 컨테이너 거동 (FR-AN-05)");
        P();
        P("![진자](fig5_container_pendulum.svg)");
        P();
        P("| 구간 | 진폭 [°] | RMS [°] | 주기 [s] |");
        P("|---|---|---|---|");
        foreach (var key in new[] { "containerPendulum", "containerEjectionResponse" })
            foreach (var b in m[key]?["bodies"]?.AsArray() ?? new JsonArray())
                P($"| {key} ({N(b!["from_s"], "F1")}–{N(b["to_s"], "F1")} s) | {N(b["amplitude_deg"], "F1")} | {N(b["rms_deg"], "F1")} | {N(b["period_s"])} |");
        P();
        P("| 사출 | 직전 1 s 최대 진자각 [°] | 직후 2 s 최대 진자각 [°] | 직전 최대 각속도 [°/s] | 직후 최대 각속도 [°/s] |");
        P("|---|---|---|---|---|");
        foreach (var e in m["containerEjectionResponse"]?["bodies"]?[0]?["eventResponse"]?.AsArray() ?? new JsonArray())
            P($"| {e!["event"]} ({N(e["t"], "F1")} s) | {N(e["swingBefore_deg"], "F1")} | {N(e["swingAfter_deg"], "F1")} | {N(e["rateBefore_dps"], "F1")} | {N(e["rateAfter_dps"], "F1")} |");
        P();
        P($"- 박스–컨테이너 최소 이격(사출 1 s 이후): {N(m["boxContainerClearance"]?["min_m"])} m ({m["boxContainerClearance"]?["pair"]}) · 박스–박스: {N(m["boxBoxClearance"]?["min_m"])} m · 캐노피 간 최소 간격 {N(m["canopyClearance"]?["minGap_m"])} m ({m["canopyClearance"]?["pair"]}, {N(m["canopyClearance"]?["time_s"], "F1")} s)");
        P($"- 주낙하산 개방 하중: {N(s["devices"]?["container.main"]?["maxForce_N"], "F0")} N ({N(s["devices"]?["container.main"]?["maxOpeningG"], "F1")} G), 드로그 {N(s["devices"]?["container.drogue"]?["maxForce_N"], "F0")} N ({N(s["devices"]?["container.drogue"]?["maxOpeningG"], "F1")} G)");
        P($"- 컨테이너 착지: {N(s["bodies"]?["container"]?["impact"]?["speed_mps"], "F2")} m/s @ {N(s["bodies"]?["container"]?["impact"]?["time_s"], "F1")} s");
        P();

        // ---- 6. 박스 사출 ----
        P("## 6. 박스 사출 과정 (FR-AN-06)");
        P();
        P("사출 = 초기 상대속도 1 m/s + 사출 펄스(15 G·25 ms half-sine, 반작용 컨테이너 — FR-SEP-01), 1 s 후 박스 낙하산(48\", Cd 2.2), 3 s 후 하부 셸 레일 340 mm(마찰 0.1, 상대 중력으로 미끄러짐) → 0.2 s 후 드론 테더 매달림(1.5 m, EA 1.5×10⁴ N, 파단 4,020 N) → 1 s 후 암 전개(관성 ×2)·프롭 → 1.5 s 후 테더 절단·S2 이탈.");
        P();
        P("| 박스 | 사출 [s] | 사출 0.1 s 후 상대속도 [m/s] | 컨테이너 최소 이격 [m] | 낙하산 최대 하중 [N] (G) | 셸 이탈 속도 [m/s] | 테더 최대 장력 [N] |");
        P("|---|---|---|---|---|---|---|");
        foreach (var b in m["boxEjection"]?["bodies"]?.AsArray() ?? new JsonArray())
        {
            var name = b!["body"]!.ToString(); var k = name[3..];
            var dv = s["devices"]?[$"{name}.chute"]; var rail = s["connectors"]?[$"rail{k}"]; var teth = s["connectors"]?[$"tether{k}"];
            P($"| {name} | {N(b["releaseTime_s"])} | {N(b["relativeSpeed_mps"])} | {N(b["minDistanceToParent_m"])} | {N(dv?["maxForce_N"], "F0")} ({N(dv?["maxOpeningG"], "F1")}) | {N(rail?["exitSpeed_mps"])} | {N(teth?["maxTension_N"], "F0")} |");
        }
        P();
        P("![박스 낙하산](fig6_box_chute_force.svg)");
        P();
        P("![테더](fig7_tether_tension.svg)");
        P();
        P($"- 드론–잔해 최소 이격 {N(m["droneDebrisClearance"]?["min_m"])} m ({m["droneDebrisClearance"]?["pair"]}, {N(m["droneDebrisClearance"]?["time_s"], "F2")} s). 셸은 레일 끝에서 드론보다 빨리 떨어지므로(셸 2.0 kg, 드론은 테더에 매달려 감속) 이격이 벌어진다.");
        P($"- 하부 도어: 사출 0.5 s 전에 분리(상대 3 m/s) — 도어가 박스1 낙하산 영역을 지나는지는 3절 간섭 표에 포함.");
        P();

        // ---- 7. 09 vs 09b ----
        if (res.SummaryB is { } b2)
        {
            var mb2 = b2["metrics"]!.AsObject();
            P("## 7. 개방 방식 비교: 3분할(09) vs 하부 패널(09b, 영상 방식)");
            P();
            P("| 지표 | 09 3분할 | 09b 하부 패널 |");
            P("|---|---|---|");
            string Row(string label, Func<JsonObject, JsonObject, string> f) => $"| {label} | {f(s, m)} | {f(b2, mb2)} |";
            P(Row("컨테이너 최대 각속도 [°/s]", (x, y) => N(y["containerTumbling"]?["bodies"]?[0]?["maxRate_dps"], "F0")));
            P(Row("최대 총받음각 [°]", (x, y) => N(y["containerTumbling"]?["bodies"]?[0]?["maxTotalAoA_deg"], "F1")));
            P(Row("드로그 전개 순간 대기속도 [m/s]", (x, y) => N(y["containerTumbling"]?["bodies"]?[0]?["atEvents"]?["lid_drogue"]?["airspeed_mps"], "F1")));
            P(Row("드로그 개방 [G]", (x, y) => N(x["devices"]?["container.drogue"]?["maxOpeningG"], "F1")));
            P(Row("컨테이너 최대 하중 [G]", (x, y) => N(y["containerPeakG"]?["maxG"], "F1")));
            P(Row("물체 접촉 / 잔해 간섭 [건]", (x, y) => $"{N(x["collisions"]?["contacts"], "F0")} / {N(x["collisions"]?["interferences"], "F0")}"));
            P(Row("드로그 주변 최근접 잔해 [m]", (x, y) => $"{N(y["debrisAtDrogue"]?["devices"]?[0]?["minDistance_m"], "F1")} ({y["debrisAtDrogue"]?["devices"]?[0]?["debris"]})"));
            P(Row("전개 이상 [건]", (x, y) => N(y["chuteAnomalies"]?["total"], "F0")));
            P(Row("센서 최소 이격 [m]", (x, y) => N(y["sensorLandingSeparation"]?["minHorizontal_m"], "F1")));
            P();
            P("09b 에서는 탄체 나머지(노즈·꼬리 포함)가 운반체(규정 궤적)로 남아 잔해가 하부 패널 1장뿐이다 — 대신 컨테이너가 탄체 아래로 빠지는 동안 탄체와의 재접촉(접촉 건수)이 위험 지표다.");
            P();
        }

        // ---- 8. 캡처 ----
        var caps = Directory.Exists(o.OutDir) ? Directory.GetFiles(o.OutDir, "capture_*.png").OrderBy(x => x).ToList() : new List<string>();
        P("## 8. 앱 3D 재생 캡처");
        P();
        if (caps.Count == 0) P($"앱 `MnS.Simulator --smoke {doc.DisplayPath} --at <t> --color stress --structural` 로 단계 시각 캡처를 이 폴더에 `capture_<t>.png` 로 둔다(이번 생성에는 없음).");
        if (caps.Count > 0) P("캡처는 **합성 대체 형상**(`--synthetic`)으로 찍었다 — 실설계 형상 화면은 저장소에 넣지 않는다(NFR-06). 실모델 화면은 `--real-models <폴더>` 로 앱에서 직접 본다.");
        if (caps.Count > 0) P();
        foreach (var c in caps) { P($"![{Path.GetFileNameWithoutExtension(c)}]({Path.GetFileName(c)})"); P(); }
        P("앱에서 09 를 불러와 F5 → 이벤트 목록 클릭 또는 PageUp/PageDown 으로 단계별 이동, 색상 모드 '근사 응력'/'안전계수' + 레전드로 응력 색 표시.");
        P();

        // ---- 9. 가정·한계 ----
        P("## 9. 가정 · 알려진 한계");
        P();
        foreach (var a in new[]
        {
            "운반 탄체 3D 모델이 없어 외피·노즈·꼬리·폭약 Δv·충격 펄스는 파라미터 모델(가정) — V절과 같은 방식, 외경은 컨테이너를 수납하는 크기(가정).",
            "박스 피치 = 박스 높이, 박스 1 = 도어 쪽. 실모델 내보내기는 박스 1세트만 있어 나머지 3세트는 같은 형상을 Fusion Z 로 이동해 배치.",
            "사출 박스 결합체·컨테이너 공력은 메시 대신 기본 형상(상자 · 원통) L1 — 실모델 메시는 표시·접촉·응력에 쓴다.",
            "낙하산은 결속점 항력 장치(별도 물체 아님) — 캐노피 위치는 상대풍 반대 방향 라이저 끝으로 추정. 낙하산끼리의 공력 간섭·FSI 없음.",
            "뚜껑은 드로그 팩 덮개(브라이들 연결)로 보고 간섭 판정에서 제외. 전개 이상 계수(지연 0.3 s, 부분 충만 0.6, 손상 0.85)는 가정.",
            "근사 응력의 컨테이너 모델은 컨테이너 고유 질량만 구조에 분포(수납 박스 관성은 연결점이 아닌 합력으로만 반영). CalculiX 도 관성 완화 + 3-2-1 지지(자기평형).",
            "Dryden 난류는 물체 대기속도로 시간 척도를 정하므로(L/V) 박스 상판처럼 아주 느린 물체에는 수십~수백 s 지속 상승류가 생겨 착지 시각·위치 산포가 과대할 수 있다.",
            "Young 관입식은 질량 2.27 kg 미만에서 외삽(소형 센서).",
        }) P($"- {a}");
        P();
        P("원자료(시간이력·요약·구조 분석) 재현:");
        P();
        P("```");
        P(o.Command);
        P("```");
        return sb.ToString();
    }
}
