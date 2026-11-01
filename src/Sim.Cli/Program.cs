using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Sim.Core.Analysis;
using Sim.Core.Engine;
using Sim.Core.Geometry;
using Sim.Core.Numerics;
using Sim.Core.Scenario;
using Sim.Gpu;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
Console.OutputEncoding = Encoding.UTF8;

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Console.WriteLine("""
        mns — 공중투하 M&S 시뮬레이터 CLI (Sim.Core 6DOF 엔진)

          mns run <scenario.json> [--out DIR] [--dt S] [--tend S] [--archive FILE.mnsr] [--structural] [--loadcases]
              단일 실행 → DIR/timeseries.csv, devices.csv, events.json, summary.json, scene.json
              --archive: 앱에서 불러올 결과 파일(.mnsr) · --structural: 근사 응력·재질 판정(structural.json, 이벤트 추가)
              --loadcases: FEA 하중 케이스(DIR/loadcases/, FR-STR-05)
          mns open <result.mnsr>                       결과 파일 요약(불러오기 검사)
          mns fea <result.mnsr|scenario.json> --body NAME [--out DIR] [--deck-only] [--install DIR] [--timeout S]
              OpenCourant(Radioss) 착지 충돌 셸 해석(FR-MAT-05) — 덱 작성 · 실행 · VTK 변환
          mns fea-taylor [--material KEY] [--speed V] [--out DIR] [--deck-only] [--install DIR]
              대표 검증: Taylor 봉 충돌(Wilkins–Guinan 상관식 비교)
          mns ccx <result.mnsr|scenario.json> --body NAME [--analysis static|modal|buckle] [--nonlinear]
                  [--case K | --time T | --loadcases DIR/loadcases.json | --g G [--dir -x] [--at x,y,z] [--patch R]]
                  [--size M] [--mesher auto|gmsh|voxel] [--cad PART.step [--cad-frame body|fusion]] [--converge] [--out DIR] [--archive FILE.mnsr] [--install PATH] [--deck-only]
              CalculiX 정적·진동·좌굴(FR-FEA-01~05): 하중 순간 목록에서 고르거나(기본 = 최대 연결점 하중) 대표 G 하중,
              얇은 벽 = S6 셸 / 솔리드 = Gmsh C3D10(없으면 복셀) · 관성 완화 · 결과 DIR/fea_result.json + faces.csv
          mns simpkg <pkg.adsksimpkg> [--step-dir DIR[;DIR…]] [--out DIR] [--run] [--install PATH] [--modal [--modes N]] [--converge [--converge-top N] [--converge-global]]
                     [--mesh-max MM] [--mesh-min MM] [--mesh-thick N] [--fusion-report FILE [--fusion-legend "sf=…;vm=MPa;u=mm"]] [--design-results FILE [--design-case AX]] [--threads N]
              Fusion 해석 패키지 요약 · study.json(mns.study/1) · 바디↔STEP 매칭(massprops 순서) · --run: LinearStress 를 CalculiX 로
              재현(C3D10 · 결합 접촉 *TIE · 고정 · GRAV · 두께 2요소 메시 · 결합 안 된 바디 질량 이관) → summary.json(평형·반력 오차·안전율 최대/99 %) · report.md · result.vtu
              --step-dir 는 massprops 조립 STEP 폴더 또는 바디별 STEP 목록(station.json · bodies.json) 폴더, --modal = *FREQUENCY, --converge = 전역(h×2→기본)·국부(하위 N 바디 h×½) 수렴
          mns ccx-verify [--out validation/reports] [--install PATH]   CalculiX 해석해 검증 + OpenCourant 정적 한계 교차검증(FR-FEA-06)
          mns ccx-mesh <part.step|part.stl> --size M [--order 1|2] [--out mesh.inp]   Gmsh 메시(FR-FEA-03)
          mns materials                                 재질 라이브러리(출처 포함)
          mns adversarial [--out validation/reports/adversarial]   적대적 시나리오 자체 검증(PRD Y)
          mns mc <scenario.json> --runs N [--body NAME] [--seed S] [--sampling lhs|random] [--out DIR]
              전체 6DOF Monte Carlo (CPU 멀티코어, 시나리오 dispersions 사용)
          mns gpumc <scenario.json> [--runs N] [--cpu] [--seed S] [--out DIR]
              단순 모델(3DOF+낙하산) Monte Carlo — ILGPU(GPU) / --cpu 는 CPU 병렬
          mns massprops <file.stl> (--mass KG | --density KG_M3) [--unit mm|cm|m] [--fusion] [--out FILE.json]
              STL 균일밀도 질량특성 (Fusion JSON 이 없을 때의 대체 경로)
          mns validate <scenario.json>
          mns validate-public [CASE_ROOT=validation/public] [--out validation/reports]
              공개 데이터 Validation(VV-49): 사례별 case.json 재현·판정 → 보고서. A 등급 실패 시 종료코드 1
          mns scenario-report <scenario.json> [--variant <scenario.json>] [--runs N] [--ccx] [--out validation/reports/scenario09] [--raw DIR]
              운용 시나리오 분석 보고서(PRD AA절): 단계 타임라인·텀블링·잔해 간섭·드로그 지연 민감도·MC 전개 이상 확률·단계 응력(+CalculiX)
          mns opening-shock <scenario.json> [--variant <scenario.json>] [--runs N] [--ccx] [--no-sweep] [--out validation/reports/opening_shock] [--raw DIR]
              낙하산 개방 충격 · 브레이크 코드 · 박스 내부 하중 경로(PRD FR-AN-08): 단계별 박스/드론 G, 부품 항복 안전계수(근사 + CalculiX 실모델),
              압괴 패드 비교·두께 스윕, 개방하중 불확도 MC 안전계수 분포(최솟값·5 백분위)
          mns credibility [scenario.json …] [--runs N] [--out validation/reports/credibility] [--raw DIR]
              신뢰도 분석(PRD AB절): 입력 출처 등급 · 불확도 정량화(출처 등급 분포 MC + dt/2 + 공개 Validation 모델 형태, PRCC) ·
              NASA-STD-7009A 8요인 채점 · 결과별 신뢰/참고/추정 등급·90 % 구간 · 시험 우선순위 (기본: 시나리오 10, 09)
          mns tumble-study [scenario09.json] [--runs N] [--seed S] [--aero-db DB.json] [--damping Clp,Cmq,Cnr] [--no-sweeps] [--no-maps] [--fatigue-simpkg summary.json] [--out validation/reports/tumbling09]
              3분할 분리 충격(조각 Δv·비대칭·기폭 지연·충격·받음각·tip-off·감쇠·CP·드로그 지연) → 컨테이너 텀블링 등급 지도·MC·PRCC·하중 포락선
          mns container-material [--envelope tumbling09/envelope.json] [--ccx] [--ccx-jobs N] [--thickness-mm a,b] [--install PATH] [--out validation/reports/container_material]
              M 0.5 컨테이너 튜브 재질·두께 비교: 항복/파손·외압·축압축 좌굴·굽힘(NASA SP-8007) + CalculiX 셸 좌굴/정적, 질량 예산
          mns loads [scenario09.json] [--runs N] [--seed S] [--ccx] [--ccx-threads N] [--no-aero] [--parts FILE] [--envelope FILE] [--raw DIR] [--out validation/reports/loads09]
              낙하산 개방 브레이크 충격 → 내부 수납물 부품별 G·하중·응력·항복 SF(6DOF + SRS 동적 증폭 + UQ MC + CalculiX 대조),
              3분할 분리 직후 하중(재접촉 포함), 컨테이너 외부 공력(CFD Cp → CalculiX 셸 정적·좌굴, 보정·와류 계수) + 조합 하중 + 요약 판정
          mns gpuinfo

          공통: --real-models DIR (massProps "real:…" 실모델 폴더, = MNS_REAL_MODELS) · --synthetic (합성 대체 강제)
        """);
    return 0;
}

string Opt(string name, string def)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : def;
}
bool Flag(string name) => args.Contains(name);

string GitHash()
{
    try
    {
        var psi = new ProcessStartInfo("git", "rev-parse --short HEAD") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var p = Process.Start(psi)!;
        var s = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit(3000);
        return p.ExitCode == 0 ? s : "unknown";
    }
    catch { return "unknown"; }
}

// 실모델 폴더(FR-AN-07): --real-models DIR 또는 --synthetic(합성 대체 강제) → 모든 명령에 적용(MNS_REAL_MODELS)
if (Opt("--real-models", "") is { Length: > 0 } realDir) System.Environment.SetEnvironmentVariable("MNS_REAL_MODELS", Path.GetFullPath(realDir));
else if (Flag("--synthetic")) System.Environment.SetEnvironmentVariable("MNS_REAL_MODELS", "synthetic");

string DefaultOut(ScenarioDoc doc, string kind) =>
    Path.Combine("results", $"{doc.Name}_{kind}_{DateTime.Now:yyyyMMdd_HHmmss}");

// --sweep "경로:값1,값2,…[:설명]" 목록
List<(string Path, string Label, double[] Values)> Sweeps()
{
    var list = new List<(string, string, double[])>();
    for (int i = 0; i + 1 < args.Length; i++)
    {
        if (args[i] != "--sweep") continue;
        var parts = args[i + 1].Split(':');
        if (parts.Length < 2) throw new InvalidDataException($"--sweep 경로:값,값… ('{args[i + 1]}')");
        var vals = parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        list.Add((parts[0], parts.Length > 2 ? parts[2] : parts[0], vals));
    }
    return list;
}

// 현재 설계 운용 시나리오(앱 목록의 유일한 시나리오) — 엔진 검증용 01~10b 는 tests/Sim.Core.Tests/scenarios
// = 앱과 같은 규칙: config/scenarios/*.json 이름순 첫 파일
string CurrentScenario = (Directory.Exists(Path.Combine("config", "scenarios"))
    ? Directory.GetFiles(Path.Combine("config", "scenarios"), "*.json").OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault() : null)
    ?? Path.Combine("config", "scenarios", "scenario.json");
// 하중 경로 정의 config/models/<태그>_payload_parts.json: 시나리오 realModels.dir(fusion_live_<태그>_groups)과 같은 태그의 파일, 없으면 이름순 첫 파일
string DefaultPartsFor(ScenarioDoc doc)
{
    var m = System.Text.RegularExpressions.Regex.Match(doc.Root["realModels"]?["dir"]?.GetValue<string>() ?? "", @"fusion_live_([A-Za-z0-9]+)_groups");
    var tagged = Path.Combine("config", "models", (m.Success ? m.Groups[1].Value.ToLowerInvariant() : "") + "_payload_parts.json");
    if (m.Success && File.Exists(tagged)) return tagged;
    var models = Path.Combine("config", "models");
    return (Directory.Exists(models) ? Directory.GetFiles(models, "*_payload_parts.json").OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault() : null) ?? tagged;
}
// 하중 연구 결과(mns loads) 기본 경로: 하중 경로 정의와 같은 태그의 validation/reports/<태그>/loads 가 있으면 그것, 없으면 loads09
string DefaultLoadsFor(ScenarioDoc doc)
{
    var tag = Path.GetFileName(DefaultPartsFor(doc)).Replace("_payload_parts.json", "");
    var p = Path.Combine("validation", "reports", tag, "loads", "loads.json");
    return tag.Length > 0 && File.Exists(p) ? p : Path.Combine("validation", "reports", "loads09", "loads.json");
}

try
{
    switch (args[0])
    {
        case "run":
        {
            var doc = ScenarioDoc.Load(args[1]);
            if (Opt("--dt", "") is { Length: > 0 } dt) Sim.Core.Analysis.JsonPath.SetNumber(EnsureSim(doc), "sim.dt", double.Parse(dt));
            if (Opt("--tend", "") is { Length: > 0 } te) Sim.Core.Analysis.JsonPath.SetNumber(EnsureSim(doc), "sim.tEnd", double.Parse(te));
            var sw = Stopwatch.StartNew();
            var world = doc.Build();
            world.Run(progress: t => Console.Write($"\r  t = {t,8:F2} s"));
            sw.Stop();
            Console.WriteLine();
            var outDir = Opt("--out", DefaultOut(doc, "run"));
            JsonObject? structural = null;
            if (Flag("--structural"))
            {
                var rep = Sim.Core.Structures.StructuralAnalysis.Analyze(world);
                Sim.Core.Structures.StructuralAnalysis.AppendEvents(world, rep);
                structural = rep.ToJson();
            }
            Results.WriteAll(doc, world, outDir, GitHash());
            var summary = Results.Summary(doc, world, GitHash());
            if (structural is not null)
            {
                summary["structural"] = structural.DeepClone();
                File.WriteAllText(Path.Combine(outDir, "structural.json"), structural.ToJsonString(J.Pretty));
                File.WriteAllText(Path.Combine(outDir, "summary.json"), summary.ToJsonString(J.Pretty));
            }
            if (Flag("--loadcases")) Sim.Core.Structures.LoadCaseExport.Export(world, Path.Combine(outDir, "loadcases"));
            if (Opt("--archive", "") is { Length: > 0 } arch)
            {
                ResultArchive.Save(arch, doc, world, summary, structural);
                Console.WriteLine($"결과 파일: {Path.GetFullPath(arch)} (앱: 파일 → 결과 불러오기)");
            }
            Console.WriteLine(Results.SummaryText(summary));
            if (structural?["bodies"] is JsonObject sb)
                foreach (var (n, node) in sb)
                {
                    Console.WriteLine($"· 구조 {n}: 최대 von Mises {J.ToDouble(node!["peakVonMises_MPa"]!):F1} MPa, 최대 압력 {J.ToDouble(node["maxPressure_kPa"]!):F1} kPa, 최소 SF {(node["minSafetyFactor"] is { } sf ? J.ToDouble(sf).ToString("F2") : "—")}, 상태 {node["worstState"]}");
                    if (node["impact"] is JsonObject im)
                        Console.WriteLine($"    착지 {J.ToDouble(im["normalSpeed_mps"]!):F2} m/s {im["contact"]} ({im["materialName"]}, {im["model"]}): {im["verdict"]}");
                }
            Console.WriteLine($"계산 {sw.Elapsed.TotalSeconds:F2} s (시뮬레이션 {world.T:F1} s, 실시간 대비 ×{world.T / Math.Max(sw.Elapsed.TotalSeconds, 1e-9):F0}, {world.StepCount} 스텝)");
            Console.WriteLine($"결과: {Path.GetFullPath(outDir)}");
            return world.Suspect ? 2 : 0;
        }
        case "open":
        {
            var r = ResultArchive.Load(args[1]);
            Console.WriteLine($"결과 파일 {r.Path}");
            Console.WriteLine($"  시나리오 {r.Manifest["scenario"]} · 저장 {r.Manifest["savedAt"]} · 프레임 {r.World.Recorder.Frames.Count} · 물체 {r.World.Bodies.Count} · 종료 {r.World.T:F2} s ({r.World.StopReason})");
            Console.WriteLine(Results.SummaryText(r.Summary));
            if (r.Structural is not null) Console.WriteLine("  구조 분석 포함(structural.json)");
            if (r.MonteCarlo is not null) Console.WriteLine($"  MC 포함: {r.MonteCarlo["label"]} ({r.MonteCarlo["points"]?.AsArray().Count} 점)");
            return 0;
        }
        case "fea":
        {
            World world; string name;
            if (args[1].EndsWith(ResultArchive.Extension, StringComparison.OrdinalIgnoreCase)) { var r = ResultArchive.Load(args[1]); world = r.World; name = r.Doc.Name; }
            else { var doc = ScenarioDoc.Load(args[1]); world = doc.Build(); world.Run(); name = doc.Name; }
            var body = world.GetBody(Opt("--body", ""));
            var setup = Sim.Core.Fea.FeaImpactCase.FromLanding(world, body, Sim.Core.Structures.StressModel.DisplayMesh(body.Shape ?? throw new InvalidDataException($"{body.Name}: 형상 없음")));
            var dir = Opt("--out", Path.Combine("results", $"fea_{name}_{body.Name}_{DateTime.Now:yyyyMMdd_HHmmss}"));
            var deck = Sim.Core.Fea.RadiossDeck.WriteShellImpact(dir, "impact", setup.MeshFe, setup.Material, setup.Thickness, setup.Velocity, setup.WallPoint, setup.WallNormal, setup.TEnd, totalMass: setup.Mass);
            Console.WriteLine($"덱: {deck.StarterPath} (절점 {deck.Nodes}, 셸 {deck.Elements}, 모델 질량 {deck.ModelMass:F3} kg, 지면 쪽 절반 밀도 × {deck.DensityScale:F2} — 내용물) — {setup.Note}");
            if (Flag("--deck-only")) return 0;
            var inst = Sim.Core.Fea.OpenCourantInstall.TryLocate(Opt("--install", "") is { Length: > 0 } ins ? ins : null, out var why)
                       ?? throw new InvalidOperationException(why);
            var res = Sim.Core.Fea.OpenCourantRunner.Run(inst, deck, TimeSpan.FromSeconds(double.Parse(Opt("--timeout", "1800"))));
            if (!res.Ok) { Console.Error.WriteLine($"FEA 실패({res.Stage}): {res.Error}"); return 1; }
            var outcome = Sim.Core.Fea.FeaOutcome.From(res.Final!, deck, setup.Material);
            Console.WriteLine($"완료 {res.Seconds:F1} s · VTK {res.VtkFiles.Count}개 · {outcome.Verdict}");
            var stage1 = Sim.Core.Structures.ImpactDamage.Assess(body, world.Env.Gravity);
            if (stage1 is not null) Console.WriteLine($"  (1·2단계 근사: {stage1.Verdict})");
            return 0;
        }
        case "fea-taylor":
        {
            var mat = Sim.Core.Structures.MaterialLibrary.Find(Opt("--material", "al6061-t6")) ?? throw new InvalidDataException("재질을 찾을 수 없습니다.");
            double v = double.Parse(Opt("--speed", "200"));
            var dir = Opt("--out", Path.Combine("results", $"fea_taylor_{mat.Key}_{v:F0}"));
            var deck = Sim.Core.Fea.RadiossDeck.WriteTaylorBar(dir, "taylor", mat, 0.0254, 0.0076, v);
            double expected = Sim.Core.Fea.RadiossDeck.TaylorFinalLengthRatio(mat.Density, v, mat.Yield);
            Console.WriteLine($"덱: {deck.StarterPath} (육면체 {deck.Elements}) · Wilkins–Guinan L_f/L_0 ≈ {expected:F3}");
            if (Flag("--deck-only")) return 0;
            var inst = Sim.Core.Fea.OpenCourantInstall.TryLocate(Opt("--install", "") is { Length: > 0 } ins ? ins : null, out var why)
                       ?? throw new InvalidOperationException(why);
            var res = Sim.Core.Fea.OpenCourantRunner.Run(inst, deck, TimeSpan.FromMinutes(30));
            if (!res.Ok) { Console.Error.WriteLine($"FEA 실패({res.Stage}): {res.Error}"); return 1; }
            double lf = Sim.Core.Fea.RadiossDeck.TaylorLength(res.Final!) / 0.0254;
            Console.WriteLine($"해석 L_f/L_0 = {lf:F3}, 상관식 {expected:F3} (차 {(lf / expected - 1) * 100:+0.0;-0.0}%)");
            return Math.Abs(lf / expected - 1) <= 0.15 ? 0 : 1;
        }
        case "ccx":
            return CcxCommand.Run(args, Opt, Flag);
        case "simpkg":
            return SimPkgCommand.Run(args, Opt, Flag);
        case "ccx-verify":
        {
            var inst = Sim.Core.Fea.CalculixInstall.TryLocate(Opt("--install", "") is { Length: > 0 } ins ? ins : null, out var why)
                       ?? throw new InvalidOperationException(why);
            var dir = Path.Combine(Path.GetTempPath(), "mns_ccx_verify", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            var checks = Sim.Core.Fea.CcxBenchmarks.RunAll(inst, dir);
            var oc = Sim.Core.Fea.OpenCourantInstall.Locate();
            if (oc is not null) checks.Add(Sim.Core.Fea.CcxBenchmarks.StaticLimitCrossCheck(inst, oc, Path.Combine(dir, "static_limit")));
            var outDir = Opt("--out", Path.Combine("validation", "reports"));
            Directory.CreateDirectory(outDir);
            var md = CcxCommand.VerifyReport(checks, inst, oc);
            File.WriteAllText(Path.Combine(outDir, "fea_calculix_verification.md"), md, new UTF8Encoding(false));
            foreach (var c in checks) Console.WriteLine($"  {c.Id,-7} {(c.Pass ? "통과" : "실패")} {c.Name}: {c.Measured:G5} vs {c.Reference:G5} {c.Quantity} (비 {c.Ratio:F4})");
            Console.WriteLine($"보고서: {Path.GetFullPath(Path.Combine(outDir, "fea_calculix_verification.md"))}");
            return checks.All(c => c.Pass) ? 0 : 1;
        }
        case "ccx-mesh":
        {
            double size = double.Parse(Opt("--size", "0.005"));
            var outFile = Opt("--out", Path.ChangeExtension(args[1], ".inp"));
            var work = Path.Combine(Path.GetTempPath(), "mns_ccx_mesh", Guid.NewGuid().ToString("N")[..8]);
            var (mesh, code, msg, summary) = Sim.Core.Fea.GmshMesher.Mesh(Path.GetFullPath(args[1]), size, work, int.Parse(Opt("--order", "2")));
            if (mesh is null) { Console.Error.WriteLine($"메시 실패(코드 {code}): {msg}"); return code > 0 ? code : 1; }
            File.Copy(Path.Combine(work, "mesh.inp"), outFile, overwrite: true);
            Console.WriteLine($"{mesh.Source}: 절점 {mesh.Nodes.Count:N0}, 요소 {mesh.Elements.Count:N0}, 부피 {summary?["volume_m3"]} m³ → {Path.GetFullPath(outFile)}");
            return 0;
        }
        case "materials":
        {
            foreach (var m in Sim.Core.Structures.MaterialLibrary.All)
                Console.WriteLine($"  {m.Key,-12} {m.Name,-28} ρ {m.Density,6:F0}  E {m.E / 1e9,6:F1} GPa  σy {m.Yield / 1e6,6:F1}  σu {m.Ultimate / 1e6,6:F1} MPa  {(m.Brittle ? "취성" : "연성")}  속도:{m.RateModel}\n      출처: {m.Source}");
            return 0;
        }
        case "adversarial":
        {
            var root = Directory.GetCurrentDirectory();
            var res = Sim.Core.Validation.AdversarialSuite.Run(root, () => GpuDropBackend.TryCreate(allowCpu: true, out _));
            var outDir = Opt("--out", Path.Combine("validation", "reports", "adversarial"));
            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(outDir, "SUMMARY.md"), Sim.Core.Validation.AdversarialSuite.Report(res), new UTF8Encoding(false));
            foreach (var r in res) Console.WriteLine($"  {r.Id} {r.Status,-5} {r.Category,-12} {r.Title} — {r.Measured}");
            Console.WriteLine($"보고서: {Path.GetFullPath(Path.Combine(outDir, "SUMMARY.md"))}");
            return res.Any(r => r.Status == Sim.Core.Validation.AdvStatus.Fail) ? 1 : 0;
        }
        case "mc":
        {
            var doc = ScenarioDoc.Load(args[1]);
            int runs = int.Parse(Opt("--runs", "100"));
            var mcDef = doc.Root["monteCarlo"] as JsonObject ?? new JsonObject();
            var body = Opt("--body", J.Str(mcDef, "impactBody", ""));
            if (body == "") throw new InvalidDataException("--body 또는 monteCarlo.impactBody 가 필요합니다.");
            ulong seed = ulong.Parse(Opt("--seed", J.Num(mcDef, "seed", 1).ToString(CultureInfo.InvariantCulture)));
            var sampling = Opt("--sampling", J.Str(mcDef, "sampling", "lhs"));
            var progress = new Progress<int>(n => { if (n % Math.Max(1, runs / 50) == 0) Console.Write($"\r  {n}/{runs}"); });
            var res = MonteCarlo.Run(doc, runs, body, sampling, seed, progress);
            Console.WriteLine();
            var outDir = Opt("--out", DefaultOut(doc, "mc"));
            Directory.CreateDirectory(outDir);
            res.WriteCsv(Path.Combine(outDir, "mc_runs.csv"));
            var j = res.ToJson();
            File.WriteAllText(Path.Combine(outDir, "mc_summary.json"), j.ToJsonString(J.Pretty));
            Console.WriteLine(j.ToJsonString(J.Pretty));
            Console.WriteLine($"결과: {Path.GetFullPath(outDir)}");
            return 0;
        }
        case "gpumc":
        {
            var doc = ScenarioDoc.Load(args[1]);
            var mcDef = doc.Root["gpuMonteCarlo"] as JsonObject ?? throw new InvalidDataException("gpuMonteCarlo 절이 없습니다.");
            int runs = int.Parse(Opt("--runs", J.Num(mcDef, "runs", 10000).ToString(CultureInfo.InvariantCulture)));
            ulong seed = ulong.Parse(Opt("--seed", J.Num(mcDef, "seed", 7).ToString(CultureInfo.InvariantCulture)));
            IDropBackend backend;
            GpuDropBackend? gpu = null;
            if (Flag("--cpu")) backend = new CpuDropBackend();
            else
            {
                gpu = GpuDropBackend.TryCreate(allowCpu: false, out var msg);
                Console.WriteLine($"  가속기: {msg}");
                backend = gpu is not null ? gpu : new CpuDropBackend();
            }
            var res = DropMonteCarlo.Run(doc, runs, backend, seed, J.Str(mcDef, "sampling", "lhs"));
            gpu?.Dispose();
            var outDir = Opt("--out", DefaultOut(doc, "gpumc"));
            Directory.CreateDirectory(outDir);
            res.WriteCsv(Path.Combine(outDir, "gpumc_runs.csv"));
            var j = res.ToJson();
            File.WriteAllText(Path.Combine(outDir, "gpumc_summary.json"), j.ToJsonString(J.Pretty));
            Console.WriteLine(j.ToJsonString(J.Pretty));
            Console.WriteLine($"결과: {Path.GetFullPath(outDir)}");
            return 0;
        }
        case "massprops":
        {
            var mesh = TriMesh.LoadStl(args[1]);
            double scale = Frames.UnitScale(Opt("--unit", "mm"));
            var m = mesh.Transformed(v => v * scale);
            var (vol, cg, ipd) = m.VolumeProperties();
            double mass = Opt("--mass", "") is { Length: > 0 } ms ? double.Parse(ms) : double.Parse(Opt("--density", "1000")) * vol;
            var inertia = ipd * (mass / vol);
            var (min, max) = m.Bounds();
            var mp = new MassPropsFile
            {
                Name = Path.GetFileNameWithoutExtension(args[1]), Mass = mass, Cg = cg, Inertia = inertia,
                BBoxMin = min, BBoxMax = max, Stl = Path.GetFileName(args[1]), StlUnit = Opt("--unit", "mm"),
                Frame = Flag("--fusion") ? "fusion" : "body", Source = "mesh-uniform",
            };
            var json = mp.ToJson();
            json["volume_m3"] = vol;
            json["principalMoments"] = new JsonArray(inertia.SymmetricEigenvalues().Select(x => (JsonNode)x).ToArray());
            var text = json.ToJsonString(J.Pretty);
            if (Opt("--out", "") is { Length: > 0 } outFile) File.WriteAllText(outFile, text);
            Console.WriteLine(text);
            return 0;
        }
        case "validate":
        {
            var doc = ScenarioDoc.Load(args[1]);
            var w = doc.Build();
            Console.WriteLine($"OK: {doc.Name} — 물체 {w.Bodies.Count}, 연결 {w.Connectors.Count}, 이벤트 {w.Events.Count}, 해시 {doc.Hash()}");
            foreach (var b in w.Bodies)
            {
                Console.WriteLine($"  {b.Name,-14} {b.Mode,-9} m={b.OwnMass,8:F3} kg  cg={b.OwnCg}  [{b.MassSource}] aero={b.Aero?.Fidelity ?? "none"} devices={string.Join(",", b.Devices.Select(d => d.Name))}");
                if (b.Aero?.Cp is { } cp)
                    Console.WriteLine($"      CP x={cp.X:F4} m, 정적 여유 {(b.StaticMargin is { } sm ? $"{sm * 100:F1}% (기체 길이 기준, + = 기수 선행 안정)" : "—")}");
                if (b.Aero?.Components is { } bu)
                {
                    Console.WriteLine($"      성분 합성(Barrowman): d_ref={bu.RefDiameter * 1000:F1} mm, CNα={bu.CnAlpha:F3}/rad, X_cp={bu.XCp:F4} m, (x_cg−x_cp)/d = {(b.OwnCg.X - bu.XCp) / bu.RefDiameter:F2} cal");
                    foreach (var (pn, cn, x) in bu.Parts) Console.WriteLine($"        {pn,-22} CNα {cn,7:F3}  x {x,8:F4} m");
                    if (bu.SeparatedBaseCnAlpha < 0) Console.WriteLine($"        (박리 베이스로 제외한 급수축 CNα {bu.SeparatedBaseCnAlpha:F3})");
                    if (bu.Fins is { } f)
                        Console.WriteLine($"        핀: {f.Count}장 c_r={f.RootChord * 1000:F1} c_t={f.TipChord * 1000:F1} s={f.Span * 1000:F1} sweep={f.SweepLength * 1000:F1} mm, 뿌리 앞전 x={f.RootLeX:F4}, 몸체 반경 {f.BodyRadius * 1000:F1} mm [{f.Source}]");
                }
            }
            return 0;
        }
        case "validate-public":
        {
            var root = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : Path.Combine("validation", "public");
            var outDir = Opt("--out", Path.Combine("validation", "reports"));
            var results = Sim.Core.Validation.PublicValidation.RunAll(root);
            if (results.Count == 0) { Console.Error.WriteLine($"사례가 없습니다: {Path.GetFullPath(root)}"); return 1; }
            Sim.Core.Validation.PublicValidation.WriteReports(results, root.Replace('\\', '/'), outDir,
            [
                "원자료 사례는 tools/validation/convert_public.py 가 메인 체크아웃 validation/public(PRD O-4)에서 변환(사례 origin 에 원본 sha256). 원문 PDF·CSV 는 저장소에 없음.",
                "결과 해석·설계 영향·변환하지 않은 자료: docs/validation-cases.md",
                "보정 계수: Cruz k0·Young S·VRS 계수(tools/validation/fit_rotor_vrs.py)·정적 CT 지수 — 보정에 쓴 점은 '보정'으로 표시, 검증 통과로 세지 않음.",
            ]);
            foreach (var r in results)
                Console.WriteLine($"  {r.Id,-44} {r.Vv,-11} {r.Grade} {r.Use,-12} 줄 {r.Points.Count,3}  최대 |오차|/허용 {(double.IsNaN(r.MaxToleranceRatio) ? "  —  " : $"{r.MaxToleranceRatio,5:F2}")}  → {Sim.Core.Validation.PublicValidation.StatusLabel(r.Status)}");
            foreach (var r in results)
                foreach (var n in r.Notes.Where(n => n.StartsWith("보정")))
                    Console.WriteLine($"  [{r.Id}] {n}");
            Console.WriteLine($"보고서: {Path.GetFullPath(outDir)}");
            return results.Any(r => r.Status == Sim.Core.Validation.CaseStatus.Fail) ? 1 : 0;
        }
        case "scenario-report":
        {
            // PRD AA절 FR-AN-07: 운용 시나리오(09) 분석 보고서 — 공칭 + 변형 비교 + 드로그 지연 민감도 + MC 전개 이상 + 단계 응력 (+ CalculiX)
            var doc = ScenarioDoc.Load(args[1]);
            ScenarioDoc? variant = Opt("--variant", "") is { Length: > 0 } vp ? ScenarioDoc.Load(vp) : null;
            var outDir = Opt("--out", Path.Combine("validation", "reports", "scenario09"));
            var cmd = "mns " + string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
            var res = OperationalReport.Generate(doc, variant, new OperationalReportOptions
            {
                OutDir = outDir, RawDir = Opt("--raw", Path.Combine("results", $"{doc.Name}_report")),
                McRuns = int.Parse(Opt("--runs", "64")), Ccx = Flag("--ccx"), CcxInstall = Opt("--install", "") is { Length: > 0 } ci ? ci : null,
                Log = s => Console.WriteLine($"  · {s}"), Command = cmd.Replace(Opt("--real-models", "\u0000"), "<실모델 폴더>"),
            });
            Console.WriteLine($"보고서: {Path.GetFullPath(Path.Combine(outDir, "README.md"))} (단계 응력 {res.Stages.Count}, 민감도 {res.Sweep.Count}, MC {res.Mc?.Runs.Length ?? 0}, CalculiX {res.Ccx.Count})");
            return 0;
        }
        case "opening-shock":
        {
            // PRD AA절 FR-AN-08: 낙하산 개방 충격·브레이크 코드·박스 내부 하중 경로 → 박스/드론 G, 부품 SF(근사 + CalculiX), MC SF 분포
            var doc = ScenarioDoc.Load(args[1]);
            ScenarioDoc? variant = Opt("--variant", "") is { Length: > 0 } vp ? ScenarioDoc.Load(vp) : null;
            var outDir = Opt("--out", Path.Combine("validation", "reports", "opening_shock"));
            var cmd = "mns " + string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
            var rep = OpeningShockReport.Generate(doc, variant, new OpeningShockReportOptions
            {
                OutDir = outDir, RawDir = Opt("--raw", Path.Combine("results", $"{doc.Name}_report")),
                McRuns = int.Parse(Opt("--runs", J.Num(doc.Root["monteCarlo"] as JsonObject ?? new JsonObject(), "runs", 200).ToString(CultureInfo.InvariantCulture))),
                Ccx = Flag("--ccx"), CcxInstall = Opt("--install", "") is { Length: > 0 } ci ? ci : null,
                SweepThickness = Flag("--no-sweep") ? [] : new OpeningShockReportOptions { OutDir = "" }.SweepThickness,
                Log = s => Console.WriteLine($"  · {s}"), Command = cmd.Replace(Opt("--real-models", "\u0000"), "<실모델 폴더>"),
            });
            Console.WriteLine($"보고서: {Path.GetFullPath(Path.Combine(outDir, "README.md"))} (단계 {rep.Nominal.Stages.Count}, MC {rep.Mc?.Runs ?? 0}, CalculiX {rep.Ccx.Count}, 최저 SF {rep.Nominal.MinSafety:F2})");
            return 0;
        }
        case "credibility":
        {
            // PRD AB절 FR-REL-02~05: 입력 출처 등급 · UQ(입력·수치·모델 형태, PRCC) · NASA-STD-7009A 8요인 · 신뢰/참고/추정 · 시험 우선순위
            var files = args.Skip(1).TakeWhile(a => !a.StartsWith("--")).ToList();
            if (files.Count == 0) files = [CurrentScenario];
            var docs = files.Select(f => ScenarioDoc.Load(f)).ToList();
            var outDir = Opt("--out", Path.Combine("validation", "reports", "credibility"));
            var cmd = "mns " + string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
            var rep = Sim.Core.Reliability.CredibilityReport.Generate(docs, new Sim.Core.Reliability.CredibilityReportOptions
            {
                OutDir = outDir, RawDir = Opt("--raw", Path.Combine("results", "credibility")),
                Runs = Opt("--runs", "") is { Length: > 0 } rn ? int.Parse(rn) : null,
                FeaConvergenceDir = Opt("--fea-gci", Path.Combine("results", "fea_gci")),
                LoadsResults = Opt("--loads", DefaultLoadsFor(docs[0])),
                Log = s => Console.WriteLine($"  · {s}"), Command = cmd.Replace(Opt("--real-models", "\u0000"), "<실모델 폴더>"),
            });
            foreach (var (s, c) in rep.Outputs)
                Console.WriteLine($"  [{c.Class}] {s[..3]} {c.Label}: 공칭 {c.Nominal:G4} {c.Unit}, 90% {c.Lo90:G4}–{c.Hi90:G4} · " +
                                  string.Join(" ", c.Factors.Select(f => f.Level)));
            Console.WriteLine($"보고서: {Path.GetFullPath(Path.Combine(outDir, "README.md"))}");
            return 0;
        }
        case "tumble-study":
        {
            // 3분할 분리 충격 변화 → 컨테이너 텀블링 지도(1-D 스윕 · 2-D 지도 · LHS MC · PRCC · 하중 포락선)
            var doc = ScenarioDoc.Load(args.Length > 1 && !args[1].StartsWith("--") ? args[1] : CurrentScenario);
            var outDir = Opt("--out", Path.Combine("validation", "reports", "tumbling09"));
            var cmd = "mns " + string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
            Sim.Core.Numerics.Vec3? damping = Opt("--damping", "") is { Length: > 0 } ds
                ? ds.Split(',') is { Length: 3 } p ? new Sim.Core.Numerics.Vec3(double.Parse(p[0]), double.Parse(p[1]), double.Parse(p[2])) : throw new InvalidDataException("--damping Clp,Cmq,Cnr")
                : null;
            var res = TumbleStudy.Generate(doc, new TumbleStudyOptions
            {
                OutDir = outDir, McRuns = int.Parse(Opt("--runs", "256")), Seed = ulong.Parse(Opt("--seed", "4009")),
                AeroDb = Opt("--aero-db", "") is { Length: > 0 } db ? db : null, Damping = damping,
                Sweeps = !Flag("--no-sweeps"), Maps = !Flag("--no-maps"),
                FatigueSimpkg = Opt("--fatigue-simpkg", "") is { Length: > 0 } fs ? fs : null,
                Log = s => Console.WriteLine($"  · {s}"), Command = cmd.Replace(Opt("--real-models", "\u0000"), "<실모델 폴더>"),
            });
            var nom = res.Nominal[0];
            Console.WriteLine($"공칭 {TumbleStudy.ClassNames[nom.Class]} (최대 α_t {nom.MaxAoaFree:F1}°, 드로그 α_t {nom.AoaAtDrogue:F1}°) · MC {res.Mc.Count} · {res.ElapsedSeconds:F0} s");
            Console.WriteLine($"보고서: {Path.GetFullPath(Path.Combine(outDir, "README.md"))}");
            return 0;
        }
        case "container-material":
        {
            // M 0.5 컨테이너 튜브 재질·두께: 항복/파손·좌굴(SP-8007) + CalculiX 셸 횡류 정적·좌굴, 질량 예산
            var outDir = Opt("--out", Path.Combine("validation", "reports", "container_material"));
            var envPath = Opt("--envelope", "");
            var scen = Opt("--scenario", CurrentScenario);
            var cmd = "mns " + string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
            var res = Sim.Core.Structures.ContainerMaterialStudy.Generate(new Sim.Core.Structures.ContainerMaterialOptions
            {
                OutDir = outDir, Ccx = Flag("--ccx"), CcxInstall = Opt("--install", "") is { Length: > 0 } ci ? ci : null,
                Thicknesses = Opt("--thickness-mm", "") is { Length: > 0 } tl ? tl.Split(',').Select(x => double.Parse(x, CultureInfo.InvariantCulture) / 1000).ToArray()
                    : new Sim.Core.Structures.ContainerMaterialOptions { OutDir = "" }.Thicknesses,
                MeshTheta = int.Parse(Opt("--mesh-theta", "48")), MeshLength = int.Parse(Opt("--mesh-length", "24")),
                CcxJobs = Opt("--ccx-jobs", "") is { Length: > 0 } cj ? int.Parse(cj) : Math.Max(1, System.Environment.ProcessorCount / 2),
                Envelope = envPath.Length > 0 ? J.ParseObject(File.ReadAllText(envPath)) : null, EnvelopePath = envPath.Length > 0 ? envPath : null,
                Log = s => Console.WriteLine($"  · {s}"), Command = cmd.Replace(Opt("--real-models", "\u0000"), "<실모델 폴더>"),
            }, File.Exists(scen) ? ScenarioDoc.Load(scen) : null);
            Console.WriteLine($"동압 q = {res.Flight.Q:F0} Pa (ISA {res.Flight.Altitude:F0} m, M {res.Flight.Mach}) · 평가 {res.Evals.Count} · FEA {res.Fea.Count}");
            foreach (var g in res.Evals.GroupBy(e => e.Mat.Key))
            {
                var best = g.OrderBy(e => e.T).FirstOrDefault(e => e.Pass);
                Console.WriteLine($"  {g.First().Mat.Name,-36} {(best is null ? "통과 두께 없음" : $"최소 t {best.T * 1000:F1} mm · {best.Mass:F2} kg · {best.Governing}")}");
            }
            Console.WriteLine($"보고서: {Path.GetFullPath(Path.Combine(outDir, "README.md"))}");
            return 0;
        }
        case "loads":
        {
            // 사용자 질문 2026-10-09: 낙하산 개방 브레이크 충격 → 내부 수납물 부품별 G·하중·응력·항복 SF(SRS 동적 증폭, UQ MC, CalculiX 대조)
            // + 3분할 분리 후 컨테이너 외부 공력(CFD Cp → CalculiX 셸 정적·좌굴) + 조합 하중 + 요약 판정 — 모든 수치는 이 명령의 출력
            var doc = ScenarioDoc.Load(args.Length > 1 && !args[1].StartsWith("--") ? args[1] : CurrentScenario);
            var outDir = Opt("--out", Path.Combine("validation", "reports", "loads09"));
            var cmd = "mns " + string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
            var real = System.Environment.GetEnvironmentVariable("MNS_REAL_MODELS");
            var res = LoadsStudy.Generate(doc, new LoadsStudyOptions
            {
                OutDir = outDir, RawDir = Opt("--raw", "") is { Length: > 0 } rd ? rd : null,
                PartsPath = Opt("--parts", DefaultPartsFor(doc)),
                Runs = int.Parse(Opt("--runs", "128")), Seed = ulong.Parse(Opt("--seed", "2742")),
                Ccx = Flag("--ccx"), CcxInstall = Opt("--install", "") is { Length: > 0 } ci ? ci : null, CcxThreads = int.Parse(Opt("--ccx-threads", "4")),
                NoAero = Flag("--no-aero"), EnvelopePath = Opt("--envelope", Path.Combine("validation", "reports", "tumbling09", "envelope.json")),
                // --sweep "경로:값1,값2,…" (여러 번) — 1-D 민감도 스윕(재접촉 회피 조건: 분리 Δv·방향 등). 없으면 기본(노즈 공력 배율)
                Sweeps = Sweeps() is { Count: > 0 } sw ? sw : new LoadsStudyOptions { OutDir = "" }.Sweeps,
                Log = s => Console.WriteLine($"  · {s}"), Command = cmd.Replace(Opt("--real-models", " "), "<실모델 폴더>"),
                RealModelsNote = string.Equals(real, "synthetic", StringComparison.OrdinalIgnoreCase) ? "합성 대체 형상(--synthetic)"
                    : real is { Length: > 0 } ? $"실모델({Path.GetFileName(real.TrimEnd('/', '\\'))} 그룹 내보내기, git 밖 --real-models)" : "시나리오 realModels.dir(없으면 합성 대체)",
            });
            var worst = res.Nominal.Parts.Where(p => p.Part.Judged).OrderBy(p => Math.Min(p.MinSfYield, p.MinSfUltimate)).FirstOrDefault();
            Console.WriteLine($"부품 {res.Nominal.Parts.Count} · MC {o_runs(res)} (오류 {res.McErrors}) · 부품 FEA {res.PartFea.Count} · 공력 셸 α {res.Aero.Count} · 조합 {res.Combos.Count} · {res.Seconds:F0} s");
            if (worst is not null) Console.WriteLine($"최소 여유 부품(공칭): {worst.Part.Label} 항복 SF {worst.MinSfYield:F2} · 극한 SF {worst.MinSfUltimate:F2}");
            Console.WriteLine($"보고서: {Path.GetFullPath(Path.Combine(outDir, "README.md"))}");
            return 0;
            static int o_runs(LoadsStudy.Result r) => r.Mc.Values.Select(v => v.DynG.Count).DefaultIfEmpty(0).Max();
        }
        case "aero-compare":
        {
            // PRD FR-REL-01: 시나리오를 L1(TR R-300 표) 공력과 CFD 공력 DB 로 각각 돌려 핵심 출력 변화를 보고
            var doc = ScenarioDoc.Load(args[1]);
            var body = Opt("--body", "container");
            var dbs = args.SkipWhile(a => a != "--db").Skip(1).TakeWhile(a => !a.StartsWith("--")).ToList();
            if (dbs.Count == 0) throw new InvalidDataException("--db <aero_db.json …> 가 필요합니다.");
            var outFile = Opt("--out", Path.Combine("validation", "reports", "cfd", "scenario09_cfd_vs_l1.md"));
            var md = Sim.Core.Reliability.AeroCompare.Run(doc, body, dbs.Select(Path.GetFullPath).ToList(), s => Console.WriteLine($"  · {s}"),
                "mns " + string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)).Replace(Opt("--real-models", "\u0000"), "<실모델 폴더>"));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
            File.WriteAllText(outFile, md, new UTF8Encoding(false));
            Console.WriteLine($"보고서: {Path.GetFullPath(outFile)}");
            return 0;
        }
        case "gpuinfo":
        {
            foreach (var d in GpuDropBackend.ListDevices()) Console.WriteLine("  " + d);
            using var g = GpuDropBackend.TryCreate(false, out var msg);
            Console.WriteLine($"선택: {msg}");
            return 0;
        }
        default:
            Console.Error.WriteLine($"알 수 없는 명령: {args[0]}");
            return 1;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"오류: {ex.Message}");
    return 1;
}

static JsonObject EnsureSim(ScenarioDoc doc)
{
    if (doc.Root["sim"] is not JsonObject) doc.Root["sim"] = new JsonObject();
    return doc.Root;
}
