using System.Diagnostics;
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

public sealed class OpeningShockReportOptions
{
    public required string OutDir { get; init; }
    public string? RawDir { get; init; }
    public int McRuns { get; init; } = 200;
    public bool Ccx { get; init; }
    public string? CcxInstall { get; init; }
    /// <summary>비교안 패드 두께 스윕 [m] (축방향 두 패드 동시). 빈 배열 = 생략.</summary>
    public double[] SweepThickness { get; init; } = [0.018, 0.03, 0.045, 0.06, 0.08, 0.10];
    public Action<string>? Log { get; init; }
    public string Command { get; init; } = "";
}

/// <summary>
/// FR-AN-08 보고서: 공칭(기준안) 단계 표·부품 안전계수 + 비교안(압괴 패드) + 패드 두께 스윕 + 개방하중 불확도 MC + CalculiX 실모델 정적 → Markdown + SVG.
/// </summary>
public static class OpeningShockReport
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public sealed record CcxCase(string Body, string Case, double ForceN, double VonMisesMPa, Vec3 At, string NearPart, double MinSafety, double DispMm,
        string Material, string Mesh, double Seconds, string Approx);

    public sealed class Report
    {
        public required OpeningShock.Result Nominal { get; init; }
        public OpeningShock.Result? Variant { get; init; }
        public List<(double Thickness, double PayloadG, double MaxStrain, bool Bottomed)> Sweep { get; } = new();
        public OpeningShock.McSummary? Mc { get; set; }
        public List<CcxCase> Ccx { get; } = new();
        public List<string> CcxNotes { get; } = new();
    }

    private static World RunDoc(ScenarioDoc doc)
    {
        var w = doc.Build();
        w.Run();
        return w;
    }

    public static Report Generate(ScenarioDoc doc, ScenarioDoc? variant, OpeningShockReportOptions o)
    {
        Directory.CreateDirectory(o.OutDir);
        var sw = Stopwatch.StartNew();
        o.Log?.Invoke($"공칭 실행 {doc.Name}");
        var w = RunDoc(doc);
        var nominal = OpeningShock.Analyze(w);
        World? wv = null;
        OpeningShock.Result? vres = null;
        if (variant is not null)
        {
            o.Log?.Invoke($"비교안 실행 {variant.Name}");
            wv = RunDoc(variant);
            vres = OpeningShock.Analyze(wv);
        }
        var rep = new Report { Nominal = nominal, Variant = vres };
        if (variant is not null && o.SweepThickness.Length > 0)
        {
            foreach (double t in o.SweepThickness)
            {
                var d = variant.Clone();
                foreach (int i in AxialPadIndices(d)) JsonPath.SetNumber(d.Root, $"bodies.box1.devices.mount.pads.{i}.thickness", t);
                var ws = RunDoc(d);
                var m = ws.BodyMap["box1"].Devices.OfType<CushionMount>().Single();
                rep.Sweep.Add((t, m.MaxPayloadGInBox, m.Pads.Where(p => p.Axis == 0).Max(p => p.MaxStrainSeen), m.Pads.Any(p => p.Axis == 0 && p.BottomedOut)));
                o.Log?.Invoke($"스윕 두께 {t * 1000:F0} mm → 드론 {m.MaxPayloadGInBox:F1} G");
            }
        }
        if (o.McRuns > 0)
        {
            o.Log?.Invoke($"MC {o.McRuns}회");
            var mcDef = doc.Root["monteCarlo"] as JsonObject ?? new JsonObject();
            rep.Mc = OpeningShock.MonteCarloRun(doc, o.McRuns, (ulong)J.Num(mcDef, "seed", 1), J.Str(mcDef, "sampling", "lhs"));
        }
        if (o.Ccx) RunCcx(rep, w, o);
        if (o.RawDir is { } raw)
        {
            Directory.CreateDirectory(raw);
            File.WriteAllText(Path.Combine(raw, "opening_shock_nominal.json"), nominal.ToJson().ToJsonString(J.Pretty));
            if (vres is not null) File.WriteAllText(Path.Combine(raw, "opening_shock_variant.json"), vres.ToJson().ToJsonString(J.Pretty));
            WriteHistoryCsv(nominal, Path.Combine(raw, "mount_history.csv"));
        }
        Figures(rep, w, wv, o.OutDir);
        File.WriteAllText(Path.Combine(o.OutDir, "README.md"), Markdown(rep, doc, variant, w, o, sw.Elapsed.TotalSeconds), new UTF8Encoding(false));
        return rep;
    }

    private static IEnumerable<int> AxialPadIndices(ScenarioDoc d)
    {
        var mount = (JsonPath.Resolve(d.Root, "bodies.box1.devices.mount", out _, out _) as JsonObject)!;
        var pads = mount["pads"]!.AsArray();
        for (int i = 0; i < pads.Count; i++)
            if (J.Str(pads[i]!.AsObject(), "axis", "").EndsWith('x')) yield return i;
    }

    private static void WriteHistoryCsv(OpeningShock.Result r, string path)
    {
        var m = r.Mounts.FirstOrDefault();
        if (m is null) return;
        using var sw = new StreamWriter(path, false, new UTF8Encoding(false));
        sw.WriteLine("t_s,payload_g,base_g,in_box,pad_strain,pin_N," + string.Join(",", m.Channels.Select(c => c.Name + "_N")));
        foreach (var s in m.History)
            sw.WriteLine(string.Join(",", new[] { s.T, s.PayloadG, s.BaseG, s.InBox ? 1 : 0, s.PadStrain, s.PinForce }.Concat(s.Channels).Select(x => x.ToString("G6", Inv))));
    }

    // ---------------- CalculiX ----------------

    private sealed record CcxSpec(string Body, string Case, double Mass, List<PointLoad> Loads, double Patch, string Approx, double Force);

    private static void RunCcx(Report rep, World w, OpeningShockReportOptions o)
    {
        var inst = CalculixInstall.TryLocate(o.CcxInstall, out var why);
        if (inst is null) { rep.CcxNotes.Add($"CalculiX 미실행: {why}"); return; }
        var m = rep.Nominal.Mounts.FirstOrDefault();
        if (m is null) { rep.CcxNotes.Add("cushionMount 가 없어 CalculiX 하중 순간을 정할 수 없습니다."); return; }
        var box = m.Owner; var drone = m.Payload;
        var chute = box.Devices.OfType<Parachute>().FirstOrDefault();
        var tether = w.Connectors.OfType<Tether>().FirstOrDefault(t => t is not BreakCord && (t.A == box && t.B == drone));
        var cord = w.Connectors.OfType<BreakCord>().FirstOrDefault(c => c.B == box);
        int ich = m.Channels.FindIndex(c => c.Name == "chute"), ico = m.Channels.FindIndex(c => c.Name == "cord"), ite = m.Channels.FindIndex(c => c.Name == "tether");
        var h = m.History;
        double Ch(MountSample s, int i) => i >= 0 ? s.Channels[i] : 0;
        var specs = new List<CcxSpec>();
        double mBox = double.IsFinite(m.AssemblyMass) ? m.AssemblyMass
            : box.OwnMass + box.SubtreeStowed().Sum(x => x.OwnMass) + drone.OwnMass + drone.SubtreeStowed().Sum(x => x.OwnMass);
        // (a) 라이저(코드+낙하산) 최대 순간 — 박스 상판, 결합체 질량으로 관성 완화
        if (chute is not null && h.Count > 0)
        {
            var s = h.MaxBy(x => Ch(x, ich) + Ch(x, ico));
            double f = Ch(s, ich) + Ch(s, ico);
            if (f > 0)
                specs.Add(new CcxSpec(box.Name, $"라이저 최대(코드+낙하산) t={s.T:F3}s", mBox, [new PointLoad(chute.Attach, new Vec3(-f, 0, 0), "riser")], 0.015,
                    Approx(rep.Nominal, "top_plate_riser"), f));
        }
        // (b) 테더 스냅 최대 — 박스 상판(상판 단독 질량, 라이저 + 테더 두 점) / 드론(드론+센서 질량, 테더 한 점)
        if (tether is not null && ite >= 0 && h.Count > 0)
        {
            var s = h.MaxBy(x => Ch(x, ite));
            double T = Ch(s, ite), fc = Ch(s, ich);
            if (T > 0)
            {
                var hookBox = tether.PointA;
                var loads = new List<PointLoad> { new(hookBox, new Vec3(T, 0, 0), "tether") };
                if (chute is not null && fc > 0) loads.Add(new PointLoad(chute.Attach, new Vec3(-fc, 0, 0), "riser"));
                specs.Add(new CcxSpec(box.Name, $"테더 스냅 최대 t={s.T:F3}s", box.OwnMass, loads, 0.010, Approx(rep.Nominal, "top_plate_tether"), T));
                // 드론 동체: 테더는 위(−z, 드론 동체)로 당긴다
                double mDrone = drone.OwnMass + drone.SubtreeStowed().Sum(x => x.OwnMass);
                specs.Add(new CcxSpec(drone.Name, $"테더 스냅 최대 t={s.T:F3}s", mDrone, [new PointLoad(tether.PointB, new Vec3(0, 0, -T), "tether")], 0.008,
                    Approx(rep.Nominal, "drone_top_plate"), T));
            }
        }
        foreach (var sp in specs)
        {
            var b = w.BodyMap[sp.Body];
            if (b.Shape is null) { rep.CcxNotes.Add($"{sp.Body}: 형상 없음"); continue; }
            if (b.Structure?.Material is null) { rep.CcxNotes.Add($"{sp.Body}: 재질 없음(structure.material) — CalculiX 생략"); continue; }
            var display = StressModel.DisplayMesh(b.Shape);
            string? cad = b.ModelPath is { } mp && File.Exists(Path.ChangeExtension(mp, null).Replace(".massprops", "") + ".step")
                ? Path.ChangeExtension(mp, null).Replace(".massprops", "") + ".step" : null;
            var dir = Path.GetFullPath(Path.Combine(o.RawDir ?? Path.Combine(Path.GetTempPath(), "mns_opening_shock"), $"ccx_{sp.Body}_{rep.Ccx.Count}"));
            BodyFeModel fm;
            try { fm = CcxBodyFea.BuildModel(b, display, dir, null, "auto", cad); }
            catch (Exception ex) when (cad is not null)
            {
                // 다중 바디 STEP 에서 Gmsh 가 죽는 경우(실측: 드론 89 바디, 접근 위반) → 표시 메시(STL) Gmsh → 복셀 대체
                rep.CcxNotes.Add($"{sp.Body}: CAD(STEP) 메시 실패 — {ex.Message} → 표시 메시(STL) 경로로 대체");
                try { fm = CcxBodyFea.BuildModel(b, display, dir + "_stl", null, "auto", null); }
                catch (Exception ex2) { rep.CcxNotes.Add($"{sp.Body}: FE 모델 실패 — {ex2.Message}"); continue; }
            }
            catch (Exception ex) { rep.CcxNotes.Add($"{sp.Body}: FE 모델 실패 — {ex.Message}"); continue; }
            // 관성 완화 질량 = 하중을 받치는 결합체 질량(내용물 질량을 구조에 고르게 — 가정)
            var scaled = new BodyFeModel
            {
                Mesh = fm.Mesh, Material = CcxBodyFea.WithDensity(fm.BaseMaterial, fm.Material.Density * sp.Mass / Math.Max(b.OwnMass, 1e-9)),
                BaseMaterial = fm.BaseMaterial, DensityScale = fm.DensityScale * sp.Mass / Math.Max(b.OwnMass, 1e-9), Shell = fm.Shell, Display = fm.Display,
                Surface = fm.Surface, Size = fm.Size, BodyName = fm.BodyName, AttachRadius = fm.AttachRadius, InternalPressure = fm.InternalPressure,
            };
            var lc = new FeaLoadInput(sp.Case, 0, Vec3.Zero, Vec3.Zero, Vec3.Zero, sp.Loads, [], [], sp.Patch);
            o.Log?.Invoke($"CalculiX {sp.Body} {sp.Case} (절점 {fm.Mesh.Nodes.Count:N0})");
            var (run, r) = CcxBodyFea.Solve(inst, scaled, CcxAnalysis.Static, lc, Path.Combine(dir, "case"));
            if (!run.Ok || r is null) { rep.CcxNotes.Add($"{sp.Body} {sp.Case}: CalculiX 실패 — {run.Error}"); continue; }
            rep.Ccx.Add(new CcxCase(sp.Body, sp.Case, sp.Force, r.MaxVonMises / 1e6, r.MaxVonMisesAt, NearestPart(b, r.MaxVonMisesAt), r.MinSafety, r.MaxDisplacement * 1e3,
                fm.BaseMaterial.Key, $"{fm.Mesh.Source} {fm.Mesh.Elements.Count:N0}요소", run.Seconds, sp.Approx));
            foreach (var n in fm.Notes.Select(n => $"{sp.Body}: {n}")) if (!rep.CcxNotes.Contains(n)) rep.CcxNotes.Add(n);
            if (fm.Mesh.Source.Contains("복셀"))
                rep.CcxNotes.Add($"{sp.Body}: 복셀 대체 메시(면으로 이어진 최대 덩어리만, {fm.Mesh.Elements.Count:N0}요소)라 이 결과는 **참고용** — 다중 바디 STEP 단순화(작은 부품 제거·바디 결합) 후 재해석 필요(PRD FR-FEA-08 ③). 이 부품은 4절 닫힌 식 값으로 판단한다.");
            rep.CcxNotes.Add($"{sp.Body} {sp.Case}: 관성 완화 질량 {sp.Mass:F2} kg (FE 밀도 ×{scaled.DensityScale:G3}), 하중 반경 {sp.Patch * 1000:F0} mm, 지지 반력 {(r.Reaction is { } rr ? rr.Norm : double.NaN):G3} N");
        }
    }

    private static string Approx(OpeningShock.Result r, string part) =>
        r.Overall.FirstOrDefault(kv => kv.Key.Part == part).Value is { } p ? $"{p.StressMPa:F1} MPa (SF {p.Safety:F2}, {p.Label})" : "—";

    /// <summary>최대 응력 위치에 가장 가까운 실모델 부품(massprops parts 의 바디 CG, 동체 좌표).</summary>
    private static string NearestPart(Body b, Vec3 at)
    {
        if (b.ModelPath is null || !File.Exists(b.ModelPath)) return "—";
        try
        {
            var root = J.ParseObject(File.ReadAllText(b.ModelPath));
            string best = "—"; double bd = double.PositiveInfinity;
            foreach (var p in (root["parts"] as JsonArray ?? new()).OfType<JsonObject>())
            {
                var cg = b.ModelToBody * Vec3.FromArray(J.Arr(p["cg"]!));
                double d = (cg - at).Norm;
                if (d < bd) { bd = d; best = J.Str(p, "body", "?").Replace("DISPLAY_", ""); }
            }
            return double.IsFinite(bd) ? $"{best} ({bd * 1000:F0} mm)" : "—";
        }
        catch { return "—"; }
    }

    // ---------------- 그림 ----------------

    private static void Figures(Report rep, World w, World? wv, string dir)
    {
        var m = rep.Nominal.Mounts.FirstOrDefault();
        if (m is null) return;
        double t0 = m.History.Count > 0 ? Math.Max(0, m.History[0].T) : 0;
        double t1 = m.History.Count > 0 ? m.History[^1].T : 1;
        var ser = new List<SvgPlot.Series>
        {
            new("드론(기준안)", m.History.Select(s => (s.T, s.PayloadG)).ToList()),
            new("박스", m.History.Select(s => (s.T, s.BaseG)).ToList()),
        };
        if (rep.Variant?.Mounts.FirstOrDefault() is { } mv) ser.Add(new("드론(압괴 패드안)", mv.History.Select(s => (s.T, s.PayloadG)).ToList()));
        File.WriteAllText(Path.Combine(dir, "fig1_g_history.svg"), SvgPlot.Line("박스·드론 비중력 가속도", "시간 [s]", "G", ser, t0, t1,
            note: "드론: 박스 안 = 마운트 반력/질량(2질량 서브스텝), 분리 후 = 6DOF. 스텝(1 ms) 안 최대값."));
        var loads = m.Channels.Select((c, i) => new SvgPlot.Series(c.Name switch { "chute" => "박스 낙하산", "cord" => "브레이크 코드", "tether" => "테더", "eject" => "사출 펄스", var n => n },
            m.History.Select(s => (s.T, s.Channels[i])).ToList())).ToList();
        File.WriteAllText(Path.Combine(dir, "fig2_loads.svg"), SvgPlot.Line("하중 경로 힘", "시간 [s]", "N", loads, t0, t1));
        // 사출·코드·개방 확대
        double te = w.FiredEvents.GetValueOrDefault("eject1", t0), tc = w.FiredEvents.GetValueOrDefault("box1_chute", te + 1);
        File.WriteAllText(Path.Combine(dir, "fig3_eject_zoom.svg"), SvgPlot.Line("사출~박스 낙하산 개방 확대", "시간 [s]", "G",
            ser.Select(s => s with { Points = s.Points.Where(p => p.X >= te - 0.05 && p.X <= tc + 1.0).ToList() }).ToList(), te - 0.05, tc + 1.0));
        if (rep.Sweep.Count > 0)
            File.WriteAllText(Path.Combine(dir, "fig4_pad_sweep.svg"), SvgPlot.Line("압괴 패드 두께 vs 박스 안 드론 최대 G", "축방향 패드 두께(상·하 각각) [mm]", "G",
                [new("드론 최대 G", rep.Sweep.Select(s => (s.Thickness * 1000, s.PayloadG)).ToList(), Markers: true)],
                note: "비교안 패드 면적 고정(6 G 평탄 하중 × 1.1). 박스 축 여유는 약 38 mm(상·하 합)."));
    }

    // ---------------- Markdown ----------------

    private static string F(double v, string fmt = "F2") => double.IsFinite(v) ? v.ToString(fmt, Inv) : (double.IsPositiveInfinity(v) ? "∞" : "—");

    private static string Markdown(Report rep, ScenarioDoc doc, ScenarioDoc? variant, World w, OpeningShockReportOptions o, double seconds)
    {
        var sb = new StringBuilder();
        void P(string s = "") => sb.AppendLine(s);
        var nom = rep.Nominal;
        var m = nom.Mounts.First();
        bool real = w.Bodies.Any(b => b.ModelSource == "real");
        var cord = nom.Cords.FirstOrDefault();
        var chute = nom.Chutes.FirstOrDefault();
        var weakest = nom.Overall.Values.Where(p => double.IsFinite(p.Safety)).OrderBy(p => p.Safety).ToList();

        P("# 낙하산 개방 충격 · 브레이크 코드 · 박스 내부 하중 경로 — 박스/드론 G 와 부품 항복 안전계수 (PRD AA절 FR-AN-08)");
        P();
        P($"생성: `{o.Command}` · {DateTime.Now:yyyy-MM-dd HH:mm} · 엔진 `{Results.EngineVersion}` · 시나리오 해시 `{doc.Hash()}` · 계산 {seconds:F0} s");
        P();
        P($"- 시나리오: `{doc.DisplayPath}`{(variant is not null ? $" · 비교안 `{variant.DisplayPath}`" : "")} (생성기 `tools/scenario_gen/make_box_opening_shock.py`)");
        P($"- 형상·질량: {(real ? "**실모델(그룹 box_upper·box_lower·drone·sensor, git 밖)**" : "합성 대체(실모델 폴더 없음)")} — 실설계 형상은 저장소에 넣지 않고 수치만 기록(NFR-06).");
        P("- **시험 보정 전 사전 예측**이다. 입력은 공개 자료로 바꿨다(PRD FR-REL-06): 브레이크 코드 = V-T-295 나일론 실 80 lb 규격(A), 테더 EA = 2 mm SK78 제조사 값(C), " +
          "EPP 패드 = ARPRO 45 g/l(C), 낙하산 k0·n = Cruz·NOLTR·Knacke(A/B). 사출 펄스·충만 지수·부품 치수는 여전히 (가정). 입력별 출처 등급·불확도·신뢰 등급은 " +
          "`validation/reports/credibility/README.md`(NASA-STD-7009A) — 7절 MC 는 개방하중 불확도 범위.");
        P();
        P("## 0. 질문에 대한 답");
        P();
        P("| 질문 | 답 | 근거(이 보고서) |");
        P("|---|---|---|");
        P($"| 낙하산을 펼 때 브레이크 코드에 의해 박스·드론이 받는 충격을 예측할 수 있나? | **예** — 6DOF 다물체 + 정적 끈/브레이크 코드(인장 전용, 파단) + Knacke 충만 곡선 + 박스 내부 2질량(핀·폼) 서브스텝 | 브레이크 코드 최대 {F(cord?.MaxTension ?? double.NaN, "F0")} N → 박스 {F(nom.Stages.FirstOrDefault(s => s.Stage.StartsWith("②"))?.BaseG ?? double.NaN, "F1")} G, 낙하산 최대 {F(chute?.MaxForce ?? double.NaN, "F0")} N (Ck {F(chute?.OpeningFactor ?? double.NaN)}) |");
        P($"| 응력에 따른 항복(안전)계수도? | **예(근사 + FEA)** — 부품별 닫힌 식(Roark 판·러그·볼트·Winkler 곡선보·외팔보) + 재질 라이브러리 항복/인장강도·변형률 속도 배율, 실모델은 CalculiX 정적 | 최저 SF {F(weakest.FirstOrDefault()?.Safety ?? double.NaN)} ({weakest.FirstOrDefault()?.Label}) · 4절·8절 |");
        P($"| FEA 로 개발 가능? 오픈소스·외부 솔버 활용? | **예** — 자체 FEA 를 만들지 않고 **CalculiX 2.23**(GPL, 외부 프로세스) + **Gmsh**(STEP→C3D10) 로 실모델 정적 해석, 충돌·폭발은 **OpenCourant**(OpenRadioss 포크) 경로(PRD Z·X절) | 8절 |");
        P($"| 투하방안 문서의 '폼으로 5–7 G' 가 가능한가? | {SweepVerdict(rep)} | 5·6절 |");
        P();
        var byG = nom.Stages.Where(s => s.Fired && double.IsFinite(s.PayloadG)).OrderByDescending(s => s.PayloadG).ToList();
        if (byG.Count > 0)
        {
            P("핵심 판단:");
            P($"- 드론 G 를 지배하는 단계: {string.Join(", ", byG.Take(2).Select(s => $"{s.Stage} {s.PayloadG:F1} G"))}. " +
              $"낙하산 개방·브레이크 코드 파단은 {string.Join(", ", byG.Where(s => s.Stage.StartsWith("②") || s.Stage.StartsWith("③")).Select(s => $"{s.Stage[..1]} {s.PayloadG:F1} G"))} 로 작다(48\" 낙하산 개방 하중 ≈ 2–3 G, 코드 파단하중/결합체 무게 ≈ 5 G).");
            if (weakest.Count > 0)
                P($"- 가장 약한 부품: {string.Join(", ", weakest.Take(3).Select(p => $"{p.Label} SF {F(p.Safety)}"))} — SF < 1.5 부품은 치수·재질·결속 방식을 다시 정한다(치수는 가정값).");
            P("- 넓은 패드 + 핀 고정(기준안)은 사실상 강체 장착이라 드론이 사출 G 를 그대로 받는다. 폼으로 G 를 낮추려면 압괴 행정이 필요하고, 행정이 모자라면 바닥 닿음으로 더 나빠진다(5·6절).");
            P();
        }
        P("## 1. 하중 경로 모델");
        P();
        P("```text");
        P("컨테이너(주낙하산 하강 6.2 m/s, 규정 궤적) ──정적 끈 + 브레이크 코드(인장 전용, k = F_b/(ε_b·L), 파단 → 이벤트)──┐");
        P("                                                                                                     ▼");
        P("박스 상판(라이저 결속 피팅) ◀── 박스 낙하산(Knacke (CdS)(t), 코드 파단 = 라인 신장 완료 → 충만 시작)");
        P("   │  고정 핀(선형 k, 파단) ∥ EPP 폼 패드(탄성–평탄–치밀화, 이력 하역) ∥ 감쇠 ∥ 벽 간극   ← cushionMount: μü = R(u,u̇) − m₂·f");
        P("   ▼");
        P("드론(+센서) ── 셸 레일 이탈 후 테더 1.5 m(인장 전용) 매달림 → 절단");
        P("```");
        P();
        P("- 6DOF 는 박스+드론 결합체를 강체로 적분하고, cushionMount 가 매 스텝(1 ms) 결합체 비중력 가속도 f(시작·끝 RK4 단계, 회전 항 포함)를 받아 드론의 상대 운동을 적응 서브스텝(ω·h ≤ 0.05)으로 적분한다. 드론 G = |R|/m₂, 박스 G = |M·f − R|/m₁.");
        P("- 드론의 상대 변위(수 mm)가 외력·자세에 주는 역영향은 무시(가정). 드론이 박스에서 나간 뒤에는 드론 자체 6DOF 가속도로 기록을 이어 간다(테더 스냅·절단).");
        P("- 검증(단위 테스트 `OpeningShockTests`): 스냅 장력 에너지 해 T = mg + √((mg)² + k·m·v²) ±0.5 %, half-sine 펄스 응답 Duhamel 적분 ±2 %, 정적 1 G 초기 평형(출발 과도 없음), 핀 파단 후 폼 평탄 하중 상한, Roark·러그·볼트 손계산, Winkler 곡률 계수 = Shigley 표 3-4.");
        P();
        P("## 2. 공칭 결과(기준안: 넓은 EPP 패드 + 나일론 고정 핀 3축)");
        P();
        P("| 항목 | 값 | 비고 |");
        P("|---|---|---|");
        if (cord is not null)
            P($"| 브레이크 코드 | 파단 {(cord.Snapped ? "예" : "**아니오(박스가 정적 끈에 매달림 — 낙하산 미전개)**")} @ {F(cord.BreakTime, "F3")} s · 최대 {cord.MaxTension:F0} N (파단 {cord.BreakLoad:F0} N) | 팽팽 {F(cord.TautTime, "F3")} s, 신장 속도 {F(cord.TautSpeed)} m/s, k {cord.Stiffness:F0} N/m, 충격량 {cord.Impulse:F1} N·s |");
        if (chute is not null)
            P($"| 박스 낙하산 개방 | 최대 {chute.MaxForce:F0} N @ {F(chute.MaxForceTime, "F3")} s | 라인 신장 속도 {F(chute.StretchSpeed)} m/s, Ck = F/(q_s·CdS) = {F(chute.OpeningFactor)} |");
        P($"| 드론 최대 G (박스 안 / 전 구간) | {m.MaxPayloadGInBox:F1} G / {m.MaxPayloadG:F1} G | 박스 최대 {m.MaxBaseG:F1} G |");
        P($"| 고정 핀 | 핀당 최대 {m.MaxPinForce:F0} N, 파단 {(m.PinsBroken ? $"예 @ {F(m.PinsBrokenTime, "F3")} s" : "아니오")} | {m.PinNote} |");
        foreach (var p in m.Pads.Where(p => p.Axis == 0))
            P($"| 패드 {p.Label} ({p.MaterialKey}, {p.Area * 1e4:F1} cm² × {p.Thickness * 1000:F0} mm) | 최대 변형률 {p.MaxStrainSeen:F3}{(p.BottomedOut ? " **(치밀화 — 바닥 닿음)**" : "")} | 평탄 하중 {p.Plateau * p.Area:F0} N, 흡수 {p.Energy:F2} J |");
        P();
        P("![G 시간이력](fig1_g_history.svg)");
        P();
        P("![하중](fig2_loads.svg)");
        P();
        P("![확대](fig3_eject_zoom.svg)");
        P();
        P("## 3. 단계별 박스/드론 G 와 최약 부품 (FR-AN-08 ③)");
        P();
        StageTable(sb, nom);
        P();
        P("## 4. 부품별 최대 응력과 항복 안전계수 (근사 닫힌 식, 전 구간)");
        P();
        P("| 부품 | 재질(허용) | 식 · 지배 | 최대 하중 [N] | 시각 [s] | σ_eq [MPa] | 속도 배율 | **SF** | 판정 |");
        P("|---|---|---|---|---|---|---|---|---|");
        foreach (var p in m.Parts)
        {
            if (!nom.Overall.TryGetValue((m.FullName, p.Name), out var pk)) continue;
            P($"| {p.Label} | {p.Material.Key} ({p.Allowable() / 1e6:F0} MPa, {(p.Material.Brittle ? "인장강도" : "항복")}) | {p.Formula} · {p.Governing} | {pk.ForceN:F0} | {F(pk.Time, "F3")} | {pk.StressMPa:F1} | ×{pk.RateFactor:F3} | **{F(pk.Safety)}** | {Verdict(pk.Safety, p.Material.Brittle)} |");
        }
        P();
        P("부품 치수와 하중 분담은 시나리오 `parts` 에 적은 (가정) 값이다(질량은 실모델 부품 목록). SF < 1.5 부품은 치수·재질·결속 방식을 다시 정해야 한다. 근사 식은 국부 응력 집중(모서리·나사산)을 Kt 로만 반영한다 — 8절 CalculiX 와 대조.");
        P();
        if (rep.Variant is { } vr)
        {
            P("## 5. 비교안: 축방향 압괴형 소면적 EPP 패드 + 횡방향 위치결정 핀");
            P();
            var vm = vr.Mounts.First();
            P($"패드 면적을 '6 G 에서 평탄응력(0.30 MPa)'에 닿도록 줄인 안(투하방안 문서 '폼으로 5–7 G' 의도). 축 여유 38 mm 안에서 상·하 각 18 mm.");
            P();
            P("| 지표 | 기준안 | 압괴 패드안 |");
            P("|---|---|---|");
            P($"| 드론 최대 G (박스 안) | {m.MaxPayloadGInBox:F1} | {vm.MaxPayloadGInBox:F1} |");
            P($"| 박스 최대 G | {m.MaxBaseG:F1} | {vm.MaxBaseG:F1} |");
            foreach (var st in nom.Stages)
            {
                var v2 = vr.Stages.FirstOrDefault(s => s.Stage == st.Stage);
                if (v2 is null) continue;
                P($"| {st.Stage} 드론 G | {F(st.PayloadG, "F1")} | {F(v2.PayloadG, "F1")} |");
            }
            foreach (var p in vm.Pads.Where(p => p.Axis == 0))
                P($"| 패드 {p.Label} 최대 변형률 | — | {p.MaxStrainSeen:F3}{(p.BottomedOut ? " (치밀화 — 바닥 닿음)" : "")} |");
            P();
            StageTable(sb, vr);
            P();
        }
        if (rep.Sweep.Count > 0)
        {
            P("## 6. 압괴 패드 두께 스윕 (축방향 상·하 패드 동시)");
            P();
            P("![스윕](fig4_pad_sweep.svg)");
            P();
            P("| 두께(각) [mm] | 상·하 합 [mm] | 드론 최대 G (박스 안) | 최대 변형률 | 치밀화 |");
            P("|---|---|---|---|---|");
            foreach (var s in rep.Sweep) P($"| {s.Thickness * 1000:F0} | {2 * s.Thickness * 1000:F0} | {s.PayloadG:F1} | {s.MaxStrain:F3} | {(s.Bottomed ? "예" : "아니오")} |");
            P();
            P("해석: 사출 펄스(15 G·25 ms, Δv ≈ 2.3 m/s)를 6 G 로 낮추려면 상대 행정이 패드 두께 × 치밀화 변형률(0.7)보다 짧아야 한다. 행정이 모자라면 패드가 치밀화 구간에 들어가 드론 G 가 오히려 강체 장착보다 커진다(바닥 닿음).");
            P($"치밀화(바닥 닿음) 행의 G 크기는 압밀 강성 가정(ε > {FoamPad.LockStrain} 에서 접선 ≈ 30E 선형)과 박스 바닥 강성에 좌우되어 **크기 자체는 신뢰할 수 없다** — '피해야 할 상태'라는 판정만 쓴다.");
            P();
        }
        if (rep.Mc is { } mc)
        {
            P($"## 7. 개방하중 불확도 Monte Carlo ({mc.Runs}회, 오류 {mc.Errors}) — 안전계수 분포 (FR-AN-08 ④)");
            P();
            P("소형 낙하산(48\")의 개방하중 계수에는 공개 시험 자료가 없으므로(PRD O-4 대체 불가 항목), 충만 상수 n·지수 j·Cd·브레이크 코드 파단하중·정적 끈 길이·사출 G·패드 평탄응력을 범위로 주었다.");
            P();
            P("| 분산 경로 | 분포 | 범위 |");
            P("|---|---|---|");
            foreach (var d in mc.Mc.Dispersions) P($"| `{d.Path}` | {d.Dist} ({d.Mode}) | {DispRange(doc, d.Path)} |");
            P();
            P("| 지표 | 최솟값 | 5 % | 중앙값 | 95 % | 최댓값 |");
            P("|---|---|---|---|---|---|");
            foreach (var s in mc.Scalars) P($"| {s.Name} | {F(s.Min)} | {F(s.P5)} | {F(s.P50)} | {F(s.P95)} | {F(s.Max)} |");
            P();
            P($"브레이크 코드 미파단 실행 {mc.CordNotSnapped}/{mc.Runs}, 고정 핀 파단 {mc.PinsBroken}/{mc.Runs}, 패드 치밀화 {mc.Bottomed}/{mc.Runs}.");
            P();
            P("| 부품 | 실행 | **SF 최솟값** | **SF 5 백분위** | 중앙값 | P(SF<1) | P(SF<1.5) | 최대 하중 [N] |");
            P("|---|---|---|---|---|---|---|---|");
            foreach (var s in mc.Parts.OrderBy(x => x.P5))
                P($"| {s.Label} | {s.N} | **{F(s.Min)}** | **{F(s.P5)}** | {F(s.P50)} | {s.ProbBelow1:P1} | {s.ProbBelow15:P1} | {s.WorstForce:F0} |");
            P();
            P("| 단계(최약 부품) | 실행 | SF 최솟값 | SF 5 백분위 | 중앙값 | P(SF<1) |");
            P("|---|---|---|---|---|---|");
            foreach (var s in mc.StageParts) P($"| {s.Part} ({s.Label}) | {s.N} | {F(s.Min)} | {F(s.P5)} | {F(s.P50)} | {s.ProbBelow1:P1} |");
            P();
        }
        P("## 8. CalculiX 실모델 정적 해석 (오픈소스 외부 솔버, PRD Z절)");
        P();
        if (rep.Ccx.Count > 0)
        {
            P("하중 = 위 시뮬레이션의 최대 순간 연결점 힘(라이저·테더, 결속 피팅 반경에 분배) + 수동 관성 완화(하중을 받치는 결합체 질량으로 밀도 환산) + 3-2-1 정정 지지. 단일 재질(물체 structure.material) 가정.");
            P();
            P("| 물체 | 하중 순간 | 하중 [N] | σvm 최대 [MPa] | 위치 (동체 m) | 근처 실모델 부품 | 최소 SF | 변위 [mm] | 재질 | 근사(닫힌 식) | 메시 | 계산 |");
            P("|---|---|---|---|---|---|---|---|---|---|---|---|");
            foreach (var c in rep.Ccx)
                P($"| {c.Body} | {c.Case} | {c.ForceN:F0} | {c.VonMisesMPa:F1} | ({c.At.X:F3}, {c.At.Y:F3}, {c.At.Z:F3}) | {c.NearPart} | {F(c.MinSafety)} | {c.DispMm:F3} | {c.Material} | {c.Approx} | {c.Mesh} | {c.Seconds:F0} s |");
            P();
        }
        else P(o.Ccx ? "CalculiX 결과 없음(아래 메모)." : "이번 생성에서는 CalculiX 를 실행하지 않았다(`--ccx`).");
        foreach (var n in rep.CcxNotes) P($"- {n}");
        P();
        P("## 9. 가정 · 알려진 한계");
        P();
        P("- 컨테이너는 규정 궤적(연직 6.2 m/s)으로 두어 사출·정적 끈 반작용을 받지 않는다 — 박스 쪽 하중은 보수적(실제 컨테이너는 끈 장력에 끌려 함께 움직여 상대 속도가 약간 작다).");
        P("- 정적 끈 + 브레이크 코드는 끈 하나(길이 = 정적 끈 + 백·라인·캐노피 신장)로 묶었다. 백·캐노피 질량의 스냅 하중(라인 신장 충격)과 캐노피 부가질량은 생략 — 개방하중 계수 불확도(MC)에 포함된 것으로 본다.");
        P("- 박스 내부: 드론을 질점으로 보고 회전(흔들림)·패드 마찰·패드 국부 굽힘은 생략. 폼 하중 곡선은 Gibson–Ashby 형 3구간(평탄응력 = 재질 라이브러리 항복값, 치밀화 0.7)이며 제조사 곡선이 있으면 pads 의 E_MPa·plateau_MPa·densification 으로 덮어쓴다.");
        P("- 부품 응력은 닫힌 식 근사(FEA 아님) — 국부 응력 집중은 Kt 입력으로만. 실모델 FEA 는 단일 재질·접촉/볼트 결합 없음(관성 완화 정적). 동적 증폭은 2질량 마운트·6DOF 하중 이력에 이미 들어 있고, 부품 자체의 고유진동(수백 Hz 이상)에 의한 추가 증폭은 무시.");
        P("- 브레이크 코드 파단하중은 규격 최소값(V-T-295 80 lb, 실제는 −0/+25 %), 신율 20 %(AFFDL 그림 4.6 평균 곡선), 정적 끈 길이(3 m)·사출 펄스(15 G·25 ms)는 (가정) — 시험(V2·V3, PRD O-3)에서 측정해 바꾼다.");
        P();
        P("재현:");
        P();
        P("```");
        P(o.Command);
        P("```");
        return sb.ToString();
    }

    private static string SweepVerdict(Report rep)
    {
        if (rep.Variant is null && rep.Sweep.Count == 0) return "비교안 미실행";
        var ok = rep.Sweep.Where(s => !s.Bottomed && s.PayloadG <= 7).OrderBy(s => s.Thickness).FirstOrDefault();
        var vm = rep.Variant?.Mounts.FirstOrDefault();
        string cur = vm is not null ? $"현 박스 여유(상·하 각 18 mm)에서는 드론 {vm.MaxPayloadGInBox:F0} G{(vm.Pads.Any(p => p.BottomedOut) ? "(패드 치밀화)" : "")}" : "";
        return ok.Thickness > 0
            ? $"**현 박스로는 아니오(조건부)** — {cur}; 패드 두께 각 {ok.Thickness * 1000:F0} mm 이상(축 여유 {2 * ok.Thickness * 1000:F0} mm — 현 박스 축 여유 약 38 mm 의 {2 * ok.Thickness / 0.038:F0}배, 박스 재설계 필요)이면 {ok.PayloadG:F1} G"
            : $"**현 사양으로는 아니오** — {cur}; 스윕 범위(최대 {rep.Sweep.Select(s => s.Thickness).DefaultIfEmpty(0).Max() * 1000:F0} mm)에서 7 G 이하 없음";
    }

    private static string Verdict(double sf, bool brittle) =>
        !double.IsFinite(sf) ? "하중 없음" : sf < 1 ? (brittle ? "**파단**" : "**항복**") : sf < StressModel.CautionSafety ? "주의(SF<1.5)" : "안전";

    private static void StageTable(StringBuilder sb, OpeningShock.Result r)
    {
        sb.AppendLine("| 단계 | 창 [s] | 박스 G | 드론 G | 드론 위치 | 패드 최대 변형률 | 핀당 하중 [N] | 최약 부품 | 하중 [N] | σ [MPa] | SF |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var s in r.Stages)
        {
            if (!s.Fired) { sb.AppendLine($"| {s.Stage} | 미발생 | — | — | — | — | — | — | — | — | — |"); continue; }
            var wk = s.Weakest;
            sb.AppendLine($"| {s.Stage} | {s.T0:F3}–{s.T1:F3} | {F(s.BaseG, "F1")} | {F(s.PayloadG, "F1")} | {(s.InBox ? "박스 안" : "분리 후")} | {s.PadStrain:F3} | {s.PinForce:F0} | {wk?.Label ?? "—"} | {(wk is null ? "—" : wk.ForceN.ToString("F0", Inv))} | {(wk is null ? "—" : wk.StressMPa.ToString("F1", Inv))} | {(wk is null ? "—" : F(wk.Safety))} |");
        }
    }

    private static string DispRange(ScenarioDoc doc, string path)
    {
        var o = (doc.Root["dispersions"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(d => J.Str(d, "path", "") == path);
        if (o is null) return "—";
        var parts = new List<string>();
        foreach (var k in new[] { "min", "max", "sd" })
            if (J.NumOpt(o, k) is { } v) parts.Add($"{k} {v.ToString("G4", Inv)}");
        return string.Join(", ", parts);
    }
}
