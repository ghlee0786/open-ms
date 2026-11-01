using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Sim.Core.Fea;
using Sim.Core.Scenario;

/// <summary>`mns simpkg` — Fusion 해석 패키지(.adsksimpkg) 요약 · study.json · (--run) CalculiX 재현 · summary.json · report.md · result.vtu.</summary>
internal static class SimPkgCommand
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static int Run(string[] args, Func<string, string, string> opt, Func<string, bool> flag)
    {
        if (args.Length < 2) throw new InvalidDataException("사용법: mns simpkg <pkg.adsksimpkg> [--step-dir DIR[;DIR…]] [--out DIR] [--run [--modal] [--converge] [--mesh-max MM] [--mesh-min MM] [--mesh-thick N] [--fusion-report FILE] [--fusion-legend SPEC] [--design-results FILE]]");
        var pkg = args[1];
        var s = FusionSimPkg.Read(pkg);
        var outDir = Path.GetFullPath(opt("--out", Path.Combine("results", "simpkg", Path.GetFileNameWithoutExtension(pkg))));
        Directory.CreateDirectory(outDir);
        var md = new StringBuilder();
        void Out(string line) { Console.WriteLine(line); md.AppendLine(line); }

        Out($"# {Path.GetFileName(pkg)}");
        Out("");
        Out($"- 프로젝트: {s.ProjectName} / 해석: {s.StudyName} / 유형: **{s.StudyType}** (Fusion {s.FusionVersion}, {s.Producer})");
        Out($"- 바디 {s.Bodies.Count} · 재료 {s.Materials.Count} · 하중 {s.Loads.Count} · 구속 {s.Constraints.Count} · 접촉 {s.Contacts.Count} " +
            $"({string.Join(", ", s.Contacts.GroupBy(c => c.Type).Select(g => $"{g.Key} {g.Count()}"))}) · 강체 {s.RigidBodies.Count}");
        Out("");
        Out("| 하중 | 유형 | 크기 | 벡터 | 켜짐 | 바디 |");
        Out("|---|---|---|---|---|---|");
        foreach (var l in s.Loads)
            Out($"| {l.Name} | {l.Type} | {l.Magnitude:G6} {(l.Type == "LinearVelocity" ? "m/s" : "m/s²")} | ({l.Vector.X:G4}, {l.Vector.Y:G4}, {l.Vector.Z:G4}) | {(l.On ? "예" : "아니오")} | {Names(s, l.Bodies, "전역")} |");
        Out("");
        Out("| 구속 | 유형 | 자유도 | 좌표계 | 바디(면 수) |");
        Out("|---|---|---|---|---|");
        foreach (var c in s.Constraints)
            Out($"| {c.Name} | {c.Type} | {string.Join(" ", c.Dofs.Select(kv => $"{kv.Key}:{kv.Value}"))} | {c.CoordinateSystem} | {Names(s, c.Bodies, "-")} ({c.Faces}) |");
        Out("");
        foreach (var (k, v) in s.Parameters) Out($"- 매개변수 {k}: `{v?.ToJsonString()}`");
        Out($"- 메시 설정: `{s.Mesh.ToJsonString()}`");
        if (s.RigidBodies.Count > 0) Out($"- 강체: {Names(s, s.RigidBodies, "-")}");
        Out("");
        Out("| 재료 | 밀도 kg/m³ | E GPa | ν | 항복 MPa | 인장 MPa | 탄소성 | 바디 수 |");
        Out("|---|---|---|---|---|---|---|---|");
        foreach (var m in s.Materials)
            Out($"| {m.Name} | {m.Density:G5} | {m.E / 1e9:G4} | {m.Nu:G3} | {m.Yield / 1e6:G4} | {m.Tensile / 1e6:G4} | {(m.Elastoplastic is { } ep ? $"`{ep.ToJsonString()}`" : "-")} | {s.Bodies.Count(b => b.MaterialId == m.Id)} |");
        Out("");

        // 바디 매칭
        var dirs = opt("--step-dir", "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Path.GetFullPath).ToList();
        var notes = new List<string>();
        var matches = dirs.Count > 0 ? SimPkgCcx.Match(s, SimPkgCcx.Catalog(dirs, Path.Combine(outDir, "catalog"), notes)) : s.Bodies.Select(b => new SimPkgCcx.BodyMatch(b, null, "--step-dir 없음")).ToList();
        int matched = matches.Count(m => m.Part is not null);
        Out($"## 바디 매칭: {matched}/{s.Bodies.Count} (STEP 폴더: {(dirs.Count > 0 ? string.Join("; ", dirs) : "없음")})");
        foreach (var n in notes) Out($"- {n}");
        var miss = matches.Where(m => m.Part is null).ToList();
        if (miss.Count > 0) Out($"- 못 맞춘 바디 {miss.Count}개: {string.Join(", ", miss.Select(m => string.Join(" > ", m.Body.Name.Split(" > ").Skip(1))))}");
        foreach (var w in s.Warnings) Out($"- 경고: {w}");
        Out("");

        var studyPath = Path.Combine(outDir, "study.json");
        var study = FusionSimPkg.ToStudyJson(s);
        study["matches"] = new JsonArray(matches.Select(m => (JsonNode)new JsonObject { ["body"] = m.Body.Id, ["step"] = m.Part?.Step, ["solid"] = m.Part?.Index, ["note"] = m.Note }).ToArray());
        File.WriteAllText(studyPath, study.ToJsonString(J.Pretty));
        Console.WriteLine($"study.json → {studyPath}");

        if (flag("--run"))
        {
            JsonObject summary;
            if (s.StudyType == "LinearStress")
            {
                var inst = CalculixInstall.TryLocate(opt("--install", "") is { Length: > 0 } ins ? ins : null, out var why) ?? throw new InvalidOperationException(why);
                double Mm(string k) => double.TryParse(opt(k, ""), NumberStyles.Float, Inv, out var v) ? v / 1000 : double.NaN;
                var rule = new SimPkgCcx.MeshRule(Mm("--mesh-max"), Mm("--mesh-min"), 1, int.TryParse(opt("--mesh-thick", "2"), out var nt) ? nt : 2);
                int modes = flag("--modal") ? (int.TryParse(opt("--modes", "6"), out var nm) ? nm : 6) : 0;
                int threads = int.TryParse(opt("--threads", "0"), out var th) ? th : 0;
                summary = SimPkgCcx.RunLinearStress(s, matches, inst, outDir, Console.WriteLine, threads, rule, modes);
                if (flag("--converge"))
                {
                    // 2단계 메시 수렴. 전역: 모든 바디 h × 2(굵게) → 기본. 국부: 기본 → 99 % 안전율 하위 N 바디만 h × 0.5
                    // (기본 메시가 이미 ~70만 절점이라 전 바디 절반(≈ 8배)은 ccx 메모리를 넘는다 — --converge-global 이면 전 바디 절반도 시도)
                    var cv = new JsonObject();
                    Console.WriteLine("메시 수렴 확인(전역): 모든 바디 크기 2배로 …");
                    var coarseDir = Path.Combine(outDir, "coarse");
                    Directory.CreateDirectory(coarseDir);
                    var coarse = SimPkgCcx.RunLinearStress(s, matches, inst, coarseDir, Console.WriteLine, threads, rule with { Scale = 2 });
                    File.WriteAllText(Path.Combine(coarseDir, "summary.json"), coarse.ToJsonString(J.Pretty));
                    cv["global"] = Convergence(coarse, summary, "굵게(h×2)", "기본", "coarse/summary.json");
                    int topN = int.TryParse(opt("--converge-top", "10"), out var tn) ? tn : 10;
                    var weakest = summary["bodies"]!.AsArray().OfType<JsonObject>().Where(x => x["safetyFactorP99"] is JsonValue && !x["fixed"]!.GetValue<bool>())
                        .OrderBy(x => x["safetyFactorP99"]!.GetValue<double>()).Take(topN).Select(x => x["body"]!.GetValue<string>()).ToList();
                    bool global = flag("--converge-global");
                    Console.WriteLine(global ? "메시 수렴 확인: 모든 바디 크기 절반으로 …" : $"메시 수렴 확인(국부): 99 % 안전율 하위 {weakest.Count} 바디 크기 절반으로 …");
                    var halfDir = Path.Combine(outDir, "half");
                    Directory.CreateDirectory(halfDir);
                    var half = SimPkgCcx.RunLinearStress(s, matches, inst, halfDir, Console.WriteLine, threads, rule with { Scale = 0.5, ScaleOnly = global ? null : weakest.ToHashSet() });
                    File.WriteAllText(Path.Combine(halfDir, "summary.json"), half.ToJsonString(J.Pretty));
                    cv["local"] = Convergence(summary, half, "기본", global ? "절반(전 바디)" : $"절반(하위 {weakest.Count} 바디)", "half/summary.json");
                    summary["convergence"] = cv;
                }
                FusionReferenceData? fref = null;
                if (opt("--fusion-report", "") is { Length: > 0 } fr)
                {
                    fref = FusionReport.Read(fr);
                    if (opt("--fusion-legend", "") is { Length: > 0 } lg) FusionReport.ApplyLegend(fref, lg, "보고서 결과 그림 범례(사람이 읽어 --fusion-legend 로 입력)");
                    summary["fusionReference"] = fref.ToJson();
                }
                DesignReference? dref = null;
                if (opt("--design-results", "") is { Length: > 0 } dr)
                {
                    dref = DesignReference.Read(dr, opt("--design-case", "AX"));
                    summary["designReference"] = dref.ToJson();
                }
                ReportLinear(summary, Out, fref, dref);
            }
            else
            {
                // Explicit(동해석) 재현은 전 바디 형상이 있어야 한다 — 패키지 안 형상은 Autodesk SMT 라 읽지 않는다.
                var reason = matched == s.Bodies.Count
                    ? $"{s.StudyType}: 형상은 모두 맞췄으나 이 유형의 자동 재현(OpenCourant 덱 생성)은 구현하지 않았다"
                    : $"{s.StudyType}: 형상 STEP 매칭 {matched}/{s.Bodies.Count} — 패키지 바디({string.Join(", ", miss.Select(m => m.Body.BodyName).Distinct().Take(8))}…)와 같은 이름의 STEP 바디가 없어 재현 불가";
                summary = new JsonObject { ["schema"] = "mns.simpkg.summary/1", ["package"] = s.Package, ["type"] = s.StudyType, ["status"] = "not-run", ["reason"] = reason };
                Out($"## 재현: 실행하지 않음 — {reason}");
            }
            File.WriteAllText(Path.Combine(outDir, "summary.json"), summary.ToJsonString(J.Pretty));
        }
        File.WriteAllText(Path.Combine(outDir, "report.md"), md.ToString(), new UTF8Encoding(false));
        Console.WriteLine($"결과 폴더: {outDir}");
        return 0;
    }

    private static string Names(FusionStudy s, IEnumerable<string> ids, string empty)
    {
        var n = ids.Select(id => s.Bodies.FirstOrDefault(b => b.Id == id)?.BodyName ?? id).ToList();
        return n.Count == 0 ? empty : string.Join(", ", n);
    }

    /// <summary>메시 b → h 변화율(%) = (h − b)/b. 바디별 p99 는 b 메시의 p99 안전율 하위 10 바디.</summary>
    public static JsonObject Convergence(JsonObject b, JsonObject h, string labelB, string labelH, string file)
    {
        double D(JsonNode? n) => n is null ? double.NaN : J.ToDouble(n);
        static JsonNode? Fin(double v) => double.IsFinite(v) ? JsonValue.Create(v) : null;
        JsonObject Row(string name, double x0, double x1) => new() { ["metric"] = name, ["base"] = Fin(x0), ["half"] = Fin(x1), ["changePct"] = Fin((x1 - x0) / x0 * 100) };
        double Rf(JsonObject s) => new Sim.Core.Numerics.Vec3(D(s["equilibrium"]!["reaction_N"]![0]), D(s["equilibrium"]!["reaction_N"]![1]), D(s["equilibrium"]!["reaction_N"]![2])).Norm;
        var rows = new JsonArray
        {
            Row("절점 수", D(b["mesh"]!["nodes"]), D(h["mesh"]!["nodes"])),
            Row("최소 안전율(최대 응력)", D(b["minSafetyFactor"]?["value"]), D(h["minSafetyFactor"]?["value"])),
            Row("최소 안전율(99 %)", D(b["minSafetyFactorP99"]?["value"]), D(h["minSafetyFactorP99"]?["value"])),
            Row("최대 von Mises [Pa]", D(b["maxVonMises"]!["Pa"]), D(h["maxVonMises"]!["Pa"])),
            Row("최대 변위 [m]", D(b["maxDisplacement"]!["m"]), D(h["maxDisplacement"]!["m"])),
            Row("|반력| [N]", Rf(b), Rf(h)),
        };
        var hb = h["bodies"]!.AsArray().OfType<JsonObject>().ToDictionary(x => x["body"]!.GetValue<string>() + "|" + x["path"]!.GetValue<string>());
        var bodies = new JsonArray();
        foreach (var x in b["top10P99"]!.AsArray().OfType<JsonObject>())
        {
            var key = x["body"]!.GetValue<string>() + "|" + x["path"]!.GetValue<string>();
            if (!hb.TryGetValue(key, out var y)) continue;
            var r = Row(x["body"]!.GetValue<string>(), D(x["p99VonMises_Pa"]), D(y["p99VonMises_Pa"]));
            r["maxBase"] = Fin(D(x["maxVonMises_Pa"])); r["maxHalf"] = Fin(D(y["maxVonMises_Pa"]));
            r["maxChangePct"] = Fin((D(y["maxVonMises_Pa"]) - D(x["maxVonMises_Pa"])) / D(x["maxVonMises_Pa"]) * 100);
            r["hBase_m"] = Fin(D(x["meshSize_m"])); r["hHalf_m"] = Fin(D(y["meshSize_m"]));
            bodies.Add(r);
        }
        return new JsonObject { ["from"] = labelB, ["to"] = labelH, ["global"] = rows, ["p99Bodies"] = bodies, ["file"] = file };
    }

    private static void ReportLinear(JsonObject r, Action<string> Out, FusionReferenceData? fref, DesignReference? dref)
    {
        double D(JsonNode? n) => n is null ? double.NaN : J.ToDouble(n);
        string G(double v, string f) => double.IsFinite(v) ? v.ToString(f, Inv) : "—";
        var eq = r["equilibrium"]!;
        var rf = eq["reaction_N"]!.AsArray(); var ex = eq["expected_N"]!.AsArray();
        var mesh = r["mesh"]!;
        Out("## CalculiX 재현 결과");
        Out("");
        Out($"- 가속도 (m/s²): {string.Join(", ", r["acceleration_mps2"]!.AsArray().Select(x => D(x).ToString("G5", Inv)))}");
        Out($"- 메시: {mesh["element"]} 절점 {D(mesh["nodes"]):N0} · 요소 {D(mesh["elements"]):N0} · 규칙 {mesh["rule"]} (상한 {D(mesh["hMax_m"]) * 1000:G3} mm · 하한 {D(mesh["hMin_m"]) * 1000:G3} mm; Fusion 평균 크기 {D(mesh["fusionAverage_m"]) * 1000:G4} mm = 모델 대각선 {D(mesh["modelDiagonal_m"]) * 1000:F0} mm × {D(mesh["sizeFraction"]):G3})");
        var below = mesh["bodiesBelowRule"]!.AsArray();
        if (below.Count > 0) Out($"- 두께 {D(mesh["throughThickness"])}요소를 하한 때문에 못 채운 바디 {below.Count}개: {string.Join(", ", below.Select(x => x!.GetValue<string>()))}");
        Out($"- 결합 접촉 {r["ties"]!["bondedPairs"]}쌍 → TIE {r["ties"]!["tied"]} (양쪽 고정 {r["ties"]!["bothFixed"]}, 형상 없음 {r["ties"]!["missingGeometry"]}, 허용 오차 안 절점 부족 {r["ties"]!["notWithinTolerance"]!.AsArray().Count})");
        Out($"- 풀이: {D(r["solver"]!["seconds"]):F0} s {r["solver"]!["note"]}");
        Out("");
        Out("| 평형 확인 | X | Y | Z |");
        Out("|---|---|---|---|");
        Out($"| 반력 합 RF (N) | {D(rf[0]):G6} | {D(rf[1]):G6} | {D(rf[2]):G6} |");
        Out($"| 기대 −m·a (N) | {D(ex[0]):G6} | {D(ex[1]):G6} | {D(ex[2]):G6} |");
        Out("");
        Out($"- 해석 질량 {D(eq["freeMass_kg"]):G5} kg(FE 질량 + 이관 {D(eq["movedMass_kg"]):G4} kg) · 고정 바디 {D(eq["fixedMass_kg"]):G5} kg · 패키지 모델 질량(고정 아닌 매칭 바디 STEP 부피 × 패키지 밀도) {D(eq["packageFreeMass_kg"]):G5} kg");
        Out($"- **평형 오차 {D(eq["errorPct"]):G3} % · 반력 오차(패키지 모델 질량 × a 대비) {D(eq["coverageErrorPct"]):G3} %** — {eq["note"]}");
        var um = eq["unmatchedFreeBodies"]!.AsArray();
        if (um.Count > 0) Out($"- 형상 없어 질량을 모르는 바디 {um.Count}개(반력 오차에 안 들어감): {string.Join(", ", um.Select(x => x!.GetValue<string>()))}");
        Out($"- 최대 von Mises {D(r["maxVonMises"]!["Pa"]) / 1e6:G4} MPa ({r["maxVonMises"]!["body"]}), 최대 변위 {D(r["maxDisplacement"]!["m"]) * 1000:G4} mm ({r["maxDisplacement"]!["body"]}) — 고정 바디 제외");
        if (r["minSafetyFactor"] is JsonObject ms) Out($"- 최소 안전율(최대 응력 기준) {D(ms["value"]):G3} ({ms["body"]}, {ms["material"]})");
        if (r["minSafetyFactorP99"] is JsonObject mp) Out($"- 최소 안전율(바디별 99 % 백분위 von Mises 기준 — 설계와 같은 정의) {D(mp["value"]):G3} ({mp["body"]}, {mp["material"]})");
        Out("");
        Out("안전율 하위 10 바디(99 % 백분위 기준, 고정 바디 제외 — 99 % = 그 바디 적분점 von Mises 의 99 번째 백분위, 안전율 = 패키지 항복 / 그 값):");
        Out("");
        Out("| # | 바디 | 재료 | 99 % MPa | 최대(절점) MPa | 항복 MPa | 안전율 99 % | 안전율 최대 | 두께 mm | h mm | 요소 |");
        Out("|---|---|---|---|---|---|---|---|---|---|---|");
        int i = 0;
        foreach (var b in r["top10P99"]!.AsArray())
            Out($"| {++i} | {b!["body"]} | {b["material"]} | {D(b["p99VonMises_Pa"]) / 1e6:G4} | {D(b["maxVonMises_Pa"]) / 1e6:G4} | {D(b["yield_Pa"]) / 1e6:G4} | {D(b["safetyFactorP99"]):G3} | {D(b["safetyFactor"]):G3} | {D(b["thickness_m"]) * 1000:G3} | {D(b["meshSize_m"]) * 1000:G3} | {D(b["elements"]):N0} |");
        Out("");
        Out("최대 응력 상위 10 바디(고정 바디는 경계 인접 응력이라 제외):");
        Out("");
        Out("| # | 바디 | 재료 | 최대 von Mises MPa | 항복 MPa | 안전율 | 최대 변위 mm | 요소 |");
        Out("|---|---|---|---|---|---|---|---|");
        i = 0;
        foreach (var b in r["top10"]!.AsArray())
            Out($"| {++i} | {b!["body"]} | {b["material"]} | {D(b["maxVonMises_Pa"]) / 1e6:G4} | {D(b["yield_Pa"]) / 1e6:G4} | {D(b["safetyFactor"]):G3} | {D(b["maxDisplacement_m"]) * 1000:G3} | {D(b["elements"]):N0} |");
        Out("");
        var moved = r["bodies"]!.AsArray().Where(b => b!["movedTo"]?.GetValue<string>() is { Length: > 0 }).ToList();
        if (moved.Count > 0)
        {
            Out($"- 메시에서 빼고 질량을 옮긴 바디 {moved.Count}개(가장 가까운 해석 바디로 — 전체 질량 보존):");
            foreach (var b in moved) Out($"  - {b!["body"]}: {b["status"]}");
        }
        var dropped = r["bodies"]!.AsArray().Where(b => b!["status"]!.GetValue<string>() is not "해석" && !b["fixed"]!.GetValue<bool>() && b["movedTo"]?.GetValue<string>() is not { Length: > 0 }).ToList();
        if (dropped.Count > 0)
        {
            Out($"- 해석에서 뺀 바디 {dropped.Count}개(질량 이관 못 함):");
            foreach (var b in dropped) Out($"  - {b!["body"]}: {b["status"]}");
        }
        foreach (var w in r["warnings"]!.AsArray()) Out($"- 경고: {w}");
        Out($"- ParaView: {r["vtu"]}");
        Out("");

        if (r["convergence"] is JsonObject cvs)
        {
            Out("## 메시 수렴(2단계)");
            Out("");
            foreach (var key in new[] { "global", "local" })
            {
                if (cvs[key] is not JsonObject cv) continue;
                Out($"### {(key == "global" ? "전역" : "국부")}: {cv["from"]} → {cv["to"]} ({cv["file"]})");
                Out("");
                Out($"| 항목 | {cv["from"]} | {cv["to"]} | 변화율 % |");
                Out("|---|---|---|---|");
                foreach (var x in cv["global"]!.AsArray()) Out($"| {x!["metric"]} | {D(x["base"]):G4} | {D(x["half"]):G4} | {G(D(x["changePct"]), "+0.0;-0.0")} |");
                Out("");
                Out($"| 바디({cv["from"]} 99 % 안전율 하위) | 99 % 앞 MPa | 99 % 뒤 MPa | 변화율 % | 최대 앞 MPa | 최대 뒤 MPa | 최대 변화율 % | h 앞→뒤 mm |");
                Out("|---|---|---|---|---|---|---|---|");
                foreach (var x in cv["p99Bodies"]!.AsArray())
                    Out($"| {x!["metric"]} | {D(x["base"]) / 1e6:G4} | {D(x["half"]) / 1e6:G4} | {G(D(x["changePct"]), "+0.0;-0.0")} | {D(x["maxBase"]) / 1e6:G4} | {D(x["maxHalf"]) / 1e6:G4} | {G(D(x["maxChangePct"]), "+0.0;-0.0")} | {D(x["hBase_m"]) * 1000:G3}→{D(x["hHalf_m"]) * 1000:G3} |");
                Out("");
            }
        }

        if (fref is not null || dref is not null)
        {
            var conv = r["convergence"]?["local"] as JsonObject;
            double Half(string metric) => conv is null ? double.NaN : D(conv["global"]!.AsArray().FirstOrDefault(x => x!["metric"]!.GetValue<string>() == metric)?["half"]);
            Out("## Fusion · 설계 결과와 비교");
            Out("");
            Out($"| 항목 | 이 재현(기본) | 이 재현({conv?["to"]?.GetValue<string>() ?? "절반"}) | Fusion 클라우드 | 설계 CalculiX | 비고 |");
            Out("|---|---|---|---|---|---|");
            string FV(FusionValue? v, double scale, string f) => v is null ? "—" : (v.Value * scale).ToString(f, Inv);
            Out($"| 절점 / 요소 | {D(mesh["nodes"]):N0} / {D(mesh["elements"]):N0} | {G(Half("절점 수"), "N0")} | {(fref?.Nodes is { } fn ? fn.ToString("N0", Inv) : "—")} / {(fref?.Elements is { } fe ? fe.ToString("N0", Inv) : "—")} | — | |");
            Out($"| 최소 안전율(최대 응력) | {D(r["minSafetyFactor"]?["value"]):G3} ({r["minSafetyFactor"]?["body"]}) | {G(Half("최소 안전율(최대 응력)"), "G3")} | {FV(fref?.MinSafetyFactor, 1, "G4")} | {((dref?.Rows ?? []).Where(x => x.Allow > 0 && x.Max > 0).Select(x => x.Allow!.Value / x.Max).DefaultIfEmpty(double.NaN).Min() is var dm && double.IsFinite(dm) ? dm.ToString("G3", Inv) : "—")} | Fusion = 국부 최소 1점, 설계 = 설계 허용값 / 최대 |");
            var dmin = dref?.Rows.Where(x => x.SfP99 is not null).MinBy(x => x.SfP99);
            Out($"| 최소 안전율(99 %) | {D(r["minSafetyFactorP99"]?["value"]):G3} ({r["minSafetyFactorP99"]?["body"]}) | {G(Half("최소 안전율(99 %)"), "G3")} | — | {(dmin is null ? "—" : $"{dmin.SfP99:G3} ({dmin.Body})")} | 설계 허용값(Al6061 276 · CFRP 600 MPa 등)은 패키지 항복과 다를 수 있음 |");
            Out($"| 최대 von Mises [MPa] | {D(r["maxVonMises"]!["Pa"]) / 1e6:G4} | {G(Half("최대 von Mises [Pa]") / 1e6, "G4")} | {FV(fref?.MaxVonMises, 1e-6, "G4")} | {((dref?.Rows ?? []).Select(x => x.Max).DefaultIfEmpty(double.NaN).Max() is var dx && double.IsFinite(dx) ? (dx / 1e6).ToString("G4", Inv) : "—")} | |");
            Out($"| 최대 변위 [mm] | {D(r["maxDisplacement"]!["m"]) * 1000:G4} | {G(Half("최대 변위 [m]") * 1000, "G4")} | {FV(fref?.MaxDisplacement, 1000, "G4")} | — | |");
            var rfv = new Sim.Core.Numerics.Vec3(D(rf[0]), D(rf[1]), D(rf[2])).Norm;
            Out($"| \\|반력\\| [N] | {rfv:G5} | {G(Half("|반력| [N]"), "G5")} | {FV(fref?.Reaction, 1, "G5")} | {(dref is null ? "—" : G(dref.ExpectedN, "G5"))} (기대) | 설계 기대 = 드론 {(dref is null ? "—" : G(dref.MassKg, "G4"))} kg × 40 G |");
            if (fref is not null)
            {
                Out("");
                Out($"- Fusion 보고서: {fref.Path} — 학습 '{fref.Study}', 가속도 {(fref.Acceleration is { } a ? $"({a.X:G5}, {a.Y:G5}, {a.Z:G5}) m/s²" : "—")}, 접촉 공차 {(fref.ContactTolerance is { } ct ? $"{ct * 1000:G3} mm" : "—")}");
                foreach (var (name, v) in new[] { ("최소 안전율", fref.MinSafetyFactor), ("최대 von Mises", fref.MaxVonMises), ("최대 변위", fref.MaxDisplacement), ("반력", fref.Reaction) })
                    if (v is not null) Out($"  - {name} 출처: {v.Source}");
                foreach (var n in fref.Notes) Out($"  - {n}");
            }
            if (dref is not null && dref.Rows.Count > 0)
            {
                Out("");
                Out($"바디별 99 % 응력 비교(설계 {Path.GetFileName(dref.Path)} 사례 {dref.Case} 의 99 % 안전율 하위 10 바디):");
                Out("");
                Out("| 바디 | 설계 99 % MPa | 설계 SF 99 % | 이 재현 99 % MPa | 이 재현 SF 99 % | 차이 % |");
                Out("|---|---|---|---|---|---|");
                var ours = r["bodies"]!.AsArray().OfType<JsonObject>().GroupBy(x => x["body"]!.GetValue<string>()).ToDictionary(g => g.Key, g => g.First());
                foreach (var d0 in dref.Rows.Where(x => x.SfP99 is not null).OrderBy(x => x.SfP99).Take(10))
                {
                    var o = ours.GetValueOrDefault(d0.Body);
                    double p = o is null ? double.NaN : D(o["p99VonMises_Pa"]);
                    Out($"| {d0.Body} | {d0.P99 / 1e6:G4} | {d0.SfP99:G3} | {G(p / 1e6, "G4")} | {G(o is null ? double.NaN : D(o["safetyFactorP99"]), "G3")} | {G((p - d0.P99) / d0.P99 * 100, "+0;-0")} |");
                }
            }
            Out("");
        }

        if (r["modal"] is JsonObject md)
        {
            Out("## 고유진동(같은 모델 · 같은 고정)");
            Out("");
            Out("| 모드 | 이 재현 Hz | 주 변위 바디 | 설계 Hz |");
            Out("|---|---|---|---|");
            var df = dref?.FrequenciesHz ?? [];
            foreach (var m in md["modes"]!.AsArray())
            {
                int k = (int)D(m!["mode"]);
                Out($"| {k} | {D(m["hz"]):F1} | {m["dominantBody"]} | {(k - 1 < df.Count ? df[k - 1].ToString("F1", Inv) : "—")} |");
            }
            Out("");
            Out($"- 경계: {md["boundary"]}. 설계(_fea_station.py MODAL)는 하중핀 구멍면 x·z + 레일 접촉면 x·y 구속(z 미끄럼 자유)이라 이 재현(레일 · 래치 바디 전체 고정 + 결합)보다 무르다 — 같은 값을 기대하지 않는다.");
            Out("");
        }
    }
}
