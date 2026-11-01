using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Sim.Core.Analysis;
using Sim.Core.Scenario;
using Sim.Core.Validation;

namespace Sim.Core.Reliability;

public sealed class CredibilityReportOptions
{
    public required string OutDir { get; init; }
    public string? RawDir { get; init; }
    public int? Runs { get; init; }
    public string ValidationRoot { get; init; } = Path.Combine("validation", "public");
    public string CfdResults { get; init; } = Path.Combine("validation", "reports", "cfd", "cfd_results.json");
    /// <summary>CalculiX 메시 수렴 결과(convergence.json, `mns ccx … --converge`)를 찾을 폴더 — GCI 를 보고서에 넣는다.</summary>
    public string? FeaConvergenceDir { get; init; }
    /// <summary>`mns loads` 결과(mns.loads/1) — 있으면 그 핵심 출력(수납물 G·하중·여유, 컨테이너 공력 SF)을 같은 틀로 등록.</summary>
    public string? LoadsResults { get; init; } = Path.Combine("validation", "reports", "loads09", "loads.json");
    public Action<string>? Log { get; init; }
    public string Command { get; init; } = "";
}

public sealed class CredibilityReportResult
{
    public List<UqResult> Uq { get; } = new();
    public List<(string Scenario, OutputCredibility C)> Outputs { get; } = new();
    public List<Credibility.TestPriority> Priorities { get; set; } = new();
    public IReadOnlyList<CaseResult> Validation { get; set; } = [];
}

/// <summary>
/// 신뢰도 보고서(PRD FR-REL-02~05): 입력 출처 등급 · UQ(입력·수치·모델 형태, PRCC) · NASA-STD-7009A 8요인 채점 · 신뢰/참고/추정 등급 ·
/// 시험 우선순위 · CFD 기준 사례(FR-REL-01) 요약 → OutDir/README.md, credibility.json(앱 배지), radar_*.svg.
/// </summary>
public static class CredibilityReport
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static CredibilityReportResult Generate(IReadOnlyList<ScenarioDoc> docs, CredibilityReportOptions opt)
    {
        var res = new CredibilityReportResult();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        opt.Log?.Invoke("공개 데이터 Validation 실행(모델 형태·확인 근거)");
        res.Validation = PublicValidation.RunAll(opt.ValidationRoot);
        foreach (var doc in docs)
        {
            var uqDef = doc.Root["uq"] as JsonObject ?? throw new InvalidDataException($"{doc.Name}: uq 절 없음");
            int runs = opt.Runs ?? (int)J.Num(uqDef, "runs", 128);
            opt.Log?.Invoke($"{doc.Name}: UQ MC {runs}회 + dt/2");
            var uq = UqEngine.Run(doc, new UqOptions
            {
                Runs = runs, Validation = res.Validation, Log = opt.Log,
                Progress = opt.Log is { } lg ? new Progress<int>(n => { if (n % 32 == 0 || n == runs) lg($"  MC {n}/{runs}"); }) : null,
            });
            res.Uq.Add(uq);
            foreach (var o in uq.Outputs) res.Outputs.Add((doc.Name, Credibility.Assess(o, uq, res.Validation)));
            if (opt.RawDir is { } raw) WriteSamples(Path.Combine(raw, $"uq_{doc.Name}.csv"), uq);
        }
        if (opt.LoadsResults is { } lr && File.Exists(lr))
        {
            try
            {
                var added = Credibility.AssessLoads(J.ParseObject(File.ReadAllText(lr)), res.Validation, Path.GetDirectoryName(lr));
                res.Outputs.AddRange(added);
                opt.Log?.Invoke($"하중 연구 출력 {added.Count}개 등록({lr})");
            }
            catch (Exception ex) { opt.Log?.Invoke($"하중 연구 결과 읽기 실패({lr}): {ex.Message}"); }
        }
        res.Priorities = Credibility.Priorities(res.Uq);
        Directory.CreateDirectory(opt.OutDir);
        var enc = new UTF8Encoding(false);
        foreach (var g in res.Outputs.GroupBy(x => x.Scenario))
            File.WriteAllText(Path.Combine(opt.OutDir, $"radar_{g.Key[..Math.Min(3, g.Key.Length)].TrimEnd('_')}.svg"), Radar(g.Select(x => x.C).ToList()), enc);
        File.WriteAllText(Path.Combine(opt.OutDir, "credibility.json"), Json(res).ToJsonString(J.Pretty), enc);
        File.WriteAllText(Path.Combine(opt.OutDir, "README.md"), Markdown(res, opt, sw.Elapsed.TotalSeconds), enc);
        return res;
    }

    private static string F(double v, string fmt = "G4") => double.IsFinite(v) ? v.ToString(fmt, Inv) : "—";

    private static string Interval(OutputCredibility c) => double.IsFinite(c.Prob)
        ? string.Format(Inv, "{0:P1} [{1:P1}, {2:P1}]", c.Prob, c.ProbLo, c.ProbHi)
        : $"{F(c.Lo90)} – {F(c.Hi90)}";

    private static string Badge(string cls) => cls switch { "신뢰" => "🟢 신뢰", "참고" => "🟡 참고", _ => "🔴 추정" };

    public static JsonObject Json(CredibilityReportResult r) => new()
    {
        ["schema"] = "mns.credibility/1",
        ["engine"] = Results.EngineVersion,
        ["framework"] = "NASA-STD-7009A Appendix E (8 factors, levels 0-4, ordinal — not summed)",
        ["outputs"] = new JsonArray(r.Outputs.Select(x => (JsonNode)new JsonObject
        {
            ["scenario"] = x.Scenario, ["key"] = x.C.Key, ["label"] = x.C.Label, ["unit"] = x.C.Unit, ["model"] = x.C.Model,
            ["class"] = x.C.Class, ["useScope"] = x.C.UseScope,
            ["nominal"] = Fin(x.C.Nominal), ["lo90"] = Fin(x.C.Lo90), ["hi90"] = Fin(x.C.Hi90),
            ["probability"] = Fin(x.C.Prob), ["probLo95"] = Fin(x.C.ProbLo), ["probHi95"] = Fin(x.C.ProbHi),
            ["factors"] = new JsonArray(x.C.Factors.Select(f => (JsonNode)new JsonObject
            {
                ["factor"] = f.Factor, ["level"] = f.Level, ["threshold"] = f.Threshold, ["evidence"] = f.Evidence,
            }).ToArray()),
        }).ToArray()),
        ["testPriorities"] = new JsonArray(r.Priorities.Take(12).Select(p => (JsonNode)new JsonObject
        {
            ["test"] = p.Test, ["score"] = p.Score, ["inputs"] = new JsonArray(p.Inputs.Select(i => (JsonNode)i).ToArray()),
            ["outputs"] = new JsonArray(p.Outputs.Select(i => (JsonNode)i).ToArray()),
        }).ToArray()),
    };

    private static JsonNode? Fin(double v) => double.IsFinite(v) ? v : null;

    private static void WriteSamples(string path, UqResult uq)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var w = new StreamWriter(path);
        w.WriteLine("run," + string.Join(",", uq.Outputs.Select(o => o.Def.Key)));
        int n = uq.Outputs.Count > 0 ? uq.Outputs[0].Samples.Length : 0;
        for (int i = 0; i < n; i++)
            w.WriteLine(i + "," + string.Join(",", uq.Outputs.Select(o => o.Samples[i].ToString("G9", Inv))));
    }

    // ---------------- 레이더 차트(요인 수준 0–4, 출력별 다각형) ----------------

    public static string Radar(IReadOnlyList<OutputCredibility> outs)
    {
        const int W = 760, H = 560;
        double cx = 260, cy = 290, R = 200;
        var f = Credibility.Factors;
        int n = f.Length;
        var sb = new StringBuilder();
        sb.Append(Inv, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{W}\" height=\"{H}\" font-family=\"sans-serif\" font-size=\"12\">");
        sb.Append("<rect width=\"100%\" height=\"100%\" fill=\"white\"/>");
        sb.Append("<text x=\"20\" y=\"28\" font-size=\"15\" font-weight=\"bold\">NASA-STD-7009A 신뢰도 요인 수준(0–4, 서수 — 합산 금지)</text>");
        (double X, double Y) P(int k, double lvl) { double a = -Math.PI / 2 + 2 * Math.PI * k / n; return (cx + R * lvl / 4 * Math.Cos(a), cy + R * lvl / 4 * Math.Sin(a)); }
        for (int l = 1; l <= 4; l++)
        {
            var pts = string.Join(" ", Enumerable.Range(0, n).Select(k => { var p = P(k, l); return string.Format(Inv, "{0:F1},{1:F1}", p.X, p.Y); }));
            sb.Append(Inv, $"<polygon points=\"{pts}\" fill=\"none\" stroke=\"#ccc\" stroke-width=\"1\"/>");
            var lp = P(0, l);
            sb.Append(Inv, $"<text x=\"{lp.X + 4:F1}\" y=\"{lp.Y + 4:F1}\" fill=\"#999\" font-size=\"10\">{l}</text>");
        }
        // 프로젝트 문턱(점선)
        var tpts = string.Join(" ", Enumerable.Range(0, n).Select(k => { var p = P(k, Credibility.Threshold(k >= 6 ? "Supporting Evidence" : "x")); return string.Format(Inv, "{0:F1},{1:F1}", p.X, p.Y); }));
        sb.Append(Inv, $"<polygon points=\"{tpts}\" fill=\"none\" stroke=\"#333\" stroke-width=\"1.5\" stroke-dasharray=\"5,4\"/>");
        for (int k = 0; k < n; k++)
        {
            var p = P(k, 4.6);
            var a = P(k, 4);
            sb.Append(Inv, $"<line x1=\"{cx:F1}\" y1=\"{cy:F1}\" x2=\"{a.X:F1}\" y2=\"{a.Y:F1}\" stroke=\"#ddd\"/>");
            string anchor = p.X < cx - 10 ? "end" : p.X > cx + 10 ? "start" : "middle";
            sb.Append(Inv, $"<text x=\"{p.X:F1}\" y=\"{p.Y:F1}\" text-anchor=\"{anchor}\">{Credibility.FactorKo(f[k])}</text>");
        }
        for (int i = 0; i < outs.Count; i++)
        {
            var col = SvgPlot.Palette[i % SvgPlot.Palette.Length];
            double jitter = (i - (outs.Count - 1) / 2.0) * 0.04;
            var pts = string.Join(" ", Enumerable.Range(0, n).Select(k => { var p = P(k, Math.Max(0, outs[i].Factors[k].Level + jitter)); return string.Format(Inv, "{0:F1},{1:F1}", p.X, p.Y); }));
            sb.Append(Inv, $"<polygon points=\"{pts}\" fill=\"{col}\" fill-opacity=\"0.06\" stroke=\"{col}\" stroke-width=\"1.8\"/>");
            sb.Append(Inv, $"<rect x=\"540\" y=\"{60 + i * 22}\" width=\"12\" height=\"12\" fill=\"{col}\"/>");
            sb.Append(Inv, $"<text x=\"558\" y=\"{70 + i * 22}\" font-size=\"11\">{Esc(Trunc(outs[i].Label, 26))} ({outs[i].Class})</text>");
        }
        sb.Append(Inv, $"<text x=\"540\" y=\"{80 + outs.Count * 22}\" font-size=\"11\" fill=\"#333\">- - 프로젝트 문턱(개발·사용 2, 지원 1)</text>");
        sb.Append("</svg>");
        return sb.ToString();
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
    private static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    // ---------------- 보고서 ----------------

    private static string Markdown(CredibilityReportResult r, CredibilityReportOptions opt, double seconds)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# 신뢰성 강화와 신뢰도 분석 (PRD AB절 FR-REL-01~06)");
        sb.AppendLine();
        sb.AppendLine($"생성: `{opt.Command}` · {DateTime.Now:yyyy-MM-dd HH:mm} · 엔진 `{Results.EngineVersion}` · 계산 {seconds:F0} s");
        sb.AppendLine();
        sb.AppendLine("- 틀: **NASA-STD-7009A 부록 E**(2016, 공개) 8요인 × 수준 0–4 — 요인별로만 보고하고 **합산·평균하지 않는다**(E.4 원문). " +
                      "'CAS(Credibility Assessment Scale)'는 2008 기준판 용어이고 7009A 에는 없다(원문 확인). PRD 의 '수행자 자격' 요인은 2008 판 요인이라 7009A 에 없음 → 채점하지 않음.");
        sb.AppendLine("- 수준 정의 원문: `src/Sim.Core/Models/reference_data.json` → `credibility7009A`(NTRS 20160011121 에서 그대로, sha256 기록). 채점 규칙: `src/Sim.Core/Reliability/Credibility.cs` 주석(결과를 보기 전에 코드로 고정).");
        sb.AppendLine("- 프로젝트 충분성 문턱(표준은 문턱을 정하지 않음, E.5): 개발·사용 요인 **2**, 지원 근거 요인 **1** — 예비 설계 판단용. 문턱 미달 = '격차'.");
        sb.AppendLine("- 등급: **신뢰** = 검증·확인·입력 출처·불확도 모두 ≥ 2 · **참고** = 검증 ≥ 2, 확인 ≥ 1, 불확도 ≥ 2 · **추정** = 그 밖, 또는 어느 요인이든 수준 0(7009A '근거 부족').");
        sb.AppendLine();
        sb.AppendLine("## 1. 결과별 신뢰 등급과 90 % 구간");
        sb.AppendLine();
        sb.AppendLine("90 % 구간 = 입력 불확도(출처 등급 분포 + 시나리오 분산, LHS MC)와 모델 형태 불확도(공개 Validation 오차)를 함께 돌린 5–95 백분위를 수치 불확도(|f(dt) − f(dt/2)|)만큼 넓힌 값. 확률 출력은 Wilson 95 % 구간.");
        sb.AppendLine();
        sb.AppendLine("| 시나리오 | 출력 | 공칭 | 90 % 구간 | 등급 | 의사결정 사용 범위 |");
        sb.AppendLine("|---|---|---:|---|---|---|");
        foreach (var (s, c) in r.Outputs)
            sb.AppendLine($"| {s[..Math.Min(3, s.Length)].TrimEnd('_')} | {c.Label} [{c.Unit}] | {(double.IsFinite(c.Prob) ? "—" : F(c.Nominal))} | {Interval(c)} | {Badge(c.Class)} | {c.UseScope} |");
        sb.AppendLine();
        foreach (var g in r.Outputs.GroupBy(x => x.Scenario))
            sb.AppendLine($"![{g.Key} 레이더](radar_{g.Key[..Math.Min(3, g.Key.Length)].TrimEnd('_')}.svg)");
        sb.AppendLine();

        sb.AppendLine("## 2. NASA-STD-7009A 8요인 채점 (출력 × 요인, 수준 0–4)");
        sb.AppendLine();
        sb.AppendLine("| 시나리오 | 출력 | " + string.Join(" | ", Credibility.Factors.Select(Credibility.FactorKo)) + " | 등급 |");
        sb.AppendLine("|---|---|" + string.Join("", Credibility.Factors.Select(_ => "---:|")) + "---|");
        foreach (var (s, c) in r.Outputs)
            sb.AppendLine($"| {s[..Math.Min(3, s.Length)].TrimEnd('_')} | {c.Label} | " + string.Join(" | ", c.Factors.Select(f => f.Gap ? $"**{f.Level}**" : f.Level.ToString(Inv))) + $" | {c.Class} |");
        sb.AppendLine();
        sb.AppendLine("굵은 수 = 프로젝트 문턱 미달(격차). 근거(요인별 문장·링크)는 아래 3절.");
        sb.AppendLine();

        sb.AppendLine("## 3. 채점 근거");
        sb.AppendLine();
        foreach (var (s, c) in r.Outputs)
        {
            sb.AppendLine($"### {s[..Math.Min(3, s.Length)].TrimEnd('_')} · {c.Label} — {Badge(c.Class)}");
            sb.AppendLine();
            sb.AppendLine($"지배 모델: {c.Model}");
            sb.AppendLine();
            sb.AppendLine("| 요인 | 수준(문턱) | 근거 | 수준 정의(7009A 원문) |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var f in c.Factors)
                sb.AppendLine($"| {Credibility.FactorKo(f.Factor)} | {f.Level} ({f.Threshold}){(f.Gap ? " 격차" : "")} | {f.Evidence}{(f.Links.Count > 0 ? " — " + string.Join(", ", f.Links.Select(l => $"[{Path.GetFileName(l)}](../../../{l})")) : "")} | {f.Definition} |");
            sb.AppendLine();
        }

        sb.AppendLine("## 4. 입력 출처 등급 (FR-REL-02)");
        sb.AppendLine();
        foreach (var uq in r.Uq)
        {
            var p = uq.Pedigree;
            sb.AppendLine($"### {uq.Scenario}");
            sb.AppendLine();
            sb.AppendLine($"등록 {p.Entries.Count}개 — A {p.ByGrade["A"]} · B {p.ByGrade["B"]} · C {p.ByGrade["C"]} · D {p.ByGrade["D"]}. 시나리오 수치 잎 {p.NumericLeaves}개 중 미등록 {p.Unregistered}개(설계 기하·좌표·표시 설정 등 — 'D(미분류)'로 취급).");
            if (p.MissingPaths.Count > 0) sb.AppendLine($"경로를 찾지 못한 등록 항목(기본값 사용): {string.Join(", ", p.MissingPaths.Select(Credibility.Short))}");
            sb.AppendLine();
            sb.AppendLine("| 입력 | 등급 | 값 | 출처 | 불확도 분포 |");
            sb.AppendLine("|---|---|---:|---|---|");
            foreach (var e in p.Entries)
                sb.AppendLine($"| `{Credibility.Short(e.Path)}` | {e.Grade} | {(e.Value is { } v ? F(v) : e.Text ?? "기본값")} | {e.Source}{(e.Ref is { } rf ? $" (`{rf}`)" : "")} | {(e.Dist is { } d ? UqEngine.DistText(d) : "—")} |");
            sb.AppendLine();
        }

        sb.AppendLine("## 5. 불확도 정량화 상세 (FR-REL-03)");
        sb.AppendLine();
        foreach (var uq in r.Uq)
        {
            sb.AppendLine($"### {uq.Scenario} — {uq.Runs}회 LHS (오류 {uq.Errors}), dt {uq.Dt} s, 시드 {uq.Seed}, {uq.Seconds:F0} s");
            sb.AppendLine();
            sb.AppendLine("입력 분포(출처 등급 · 환경 = 시나리오 자연 변동 · 모델형태 = 공개 Validation 오차로 만든 계수 분포):");
            sb.AppendLine();
            sb.AppendLine("| # | 입력 | 구분 | 분포 |");
            sb.AppendLine("|---|---|---|---|");
            for (int j = 0; j < uq.Inputs.Count; j++) sb.AppendLine($"| {j + 1} | `{Credibility.Short(uq.Inputs[j].Path)}` | {uq.Inputs[j].Grade} | {uq.Inputs[j].Dist} |");
            sb.AppendLine();
            sb.AppendLine("| 출력 | 공칭 | dt/2 | 수치 불확도 | 입력 MC 5 % | 50 % | 95 % | 모델 형태 | 합친 90 % 구간 | 상위 민감 입력(PRCC) |");
            sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---|---|---|");
            foreach (var o in uq.Outputs)
            {
                var top = Enumerable.Range(0, o.PrccValues.Length).Where(j => Math.Abs(o.PrccValues[j]) >= 0.1).OrderByDescending(j => Math.Abs(o.PrccValues[j])).Take(3)
                    .Select(j => string.Format(Inv, "{0} {1:+0.00;-0.00}{2}", j + 1, o.PrccValues[j], o.PValues[j] < 0.05 ? "" : "(n.s.)"));
                string mf = o.ModelForm is { } m ? string.Format(Inv, "편향 {0:+0.0%;-0.0%}, 산포 {1:0.0%}", m.Bias, m.Sd) : "—";
                string iv = o.Def.Probability ? string.Format(Inv, "{0:P1} [{1:P1}, {2:P1}]", o.Prob, o.ProbLo, o.ProbHi) : $"{F(o.Lo90)} – {F(o.Hi90)}";
                sb.AppendLine($"| {o.Def.Label} [{o.Def.Unit}] | {F(o.Nominal)} | {F(o.NominalHalfDt)} | {(double.IsFinite(o.NumericalRel) ? o.NumericalRel.ToString("P2", Inv) : "—")} | {F(o.P05)} | {F(o.P50)} | {F(o.P95)} | {mf} | {iv} | {string.Join(", ", top)} |");
            }
            sb.AppendLine();
            foreach (var n in uq.Notes) sb.AppendLine($"- {n}");
            sb.AppendLine();
        }
        sb.AppendLine("### 모델 형태 불확도 근거");
        sb.AppendLine();
        sb.AppendLine("| 모델 | 편향 | 산포(1σ) | N | 근거 | 반영 방식 |");
        sb.AppendLine("|---|---:|---:|---:|---|---|");
        foreach (var m in r.Uq.SelectMany(u => u.ModelForms).GroupBy(m => m.Key).Select(g => g.First()))
            sb.AppendLine(string.Format(Inv, "| {0} | {1:+0.0%;-0.0%} | {2:0.0%} | {3} | {4} | {5} |", m.Label, m.Bias, m.Sd, m.N, m.Evidence,
                m.AsParameter ? $"계수 분포 `{m.ParameterPath}`" : m.Key.EndsWith("apriori") ? "보고용(입력 k0 범위로 이미 반영)" : "출력 배율 (1+ε)"));
        sb.AppendLine();
        sb.AppendLine("PRCC: 부분 순위 상관(Marino et al. 2008) — |PRCC| ≥ 0.2·p < 0.05 를 '유의 입력'으로 채점(입력 출처 요인)에 쓴다. 입력 번호는 위 입력 표의 #.");
        sb.AppendLine();

        sb.AppendLine("## 6. 신뢰도를 올리기 위한 시험 우선순위 (FR-REL-05)");
        sb.AppendLine();
        sb.AppendLine("점수 = Σ_출력 PRCC² × 등급 가중(D 1.0 · 모델형태 0.8 · C 0.7 · B 0.4 · A 0.2 · 환경 0) — 그 시험으로 입력을 확정했을 때 줄어드는 출력 분산 몫의 순위 지표(판단 가중 포함).");
        sb.AppendLine();
        sb.AppendLine("| 순위 | 시험 | 점수 | 확정되는 입력 | 불확도가 줄어드는 출력(|PRCC| ≥ 0.2) |");
        sb.AppendLine("|---:|---|---:|---|---|");
        int rank = 1;
        foreach (var p in r.Priorities.Take(12))
            sb.AppendLine(string.Format(Inv, "| {0} | {1} | {2:0.000} | {3} | {4} |", rank++, p.Test, p.Score, string.Join(", ", p.Inputs.Take(6)), string.Join(", ", p.Outputs.Take(6))));
        sb.AppendLine();

        sb.AppendLine("## 7. 공개 데이터 반영 (FR-REL-06) · Validation 현황");
        sb.AppendLine();
        sb.AppendLine("| 사례 | 등급 | 용도 | 판정 | 최대 오차/허용 |");
        sb.AppendLine("|---|---|---|---|---:|");
        foreach (var c in r.Validation)
            sb.AppendLine(string.Format(Inv, "| [{0}](../{0}.md) | {1} | {2} | {3} | {4} |", c.Id, c.Grade, c.Use, PublicValidation.StatusLabel(c.Status),
                double.IsFinite(c.MaxToleranceRatio) ? c.MaxToleranceRatio.ToString("0.00", Inv) : "—"));
        sb.AppendLine();
        sb.AppendLine(CfdSection(opt.CfdResults));
        sb.AppendLine(FeaSection(opt.FeaConvergenceDir));
        sb.AppendLine("## 8. 알려진 한계");
        sb.AppendLine();
        sb.AppendLine("- 7009A 수준 3 이상(대표 참조계·실제 시스템 RWS 자료)은 어느 출력도 주장하지 않는다 — 공개 자료는 우리 형상·질량의 시험이 아니다. 신뢰도를 올리는 길은 6절 시험이다.");
        sb.AppendLine("- 모델 형태 불확도가 검증 자료로 정량된 것은 낙하산 개방하중(대형 DGB·소형 십자형 무한 질량)과 원통 횡류 2-D 항력뿐이다. 패드·끈·부품 응력·잔해 간섭의 모델 형태는 미정량(구간에 들어가지 않음 — 구간이 실제보다 좁을 수 있다).");
        sb.AppendLine("- 등급 가중·문턱·'유의 입력' 기준(|PRCC| ≥ 0.2)은 프로젝트 판단이다(표준 값 아님). 등급 D 분포(가정 입력의 범위)는 판단값이라 그 출력 구간도 판단을 포함한다.");
        sb.AppendLine("- 수치 불확도는 시간 간격(dt/2)만 — 이벤트·서브스텝 구조 차이는 포함하지만 공간 해상도(FEA·CFD 격자)는 각 보고서의 GCI 를 따로 본다.");
        return sb.ToString();
    }

    private static string FeaSection(string? dir)
    {
        var sb = new StringBuilder();
        sb.AppendLine("### FEA 메시 수렴 — CalculiX GCI (FR-REL-03 ②)");
        sb.AppendLine();
        var files = dir is not null && Directory.Exists(dir) ? Directory.GetFiles(dir, "convergence.json", SearchOption.AllDirectories).OrderBy(f => f).ToList() : [];
        if (files.Count == 0) { sb.AppendLine("메시 수렴 결과 없음 — `mns ccx <시나리오> --body <물체> --cad <STEP> --converge --out results/fea_gci/<이름>` 후 다시 생성."); return sb.ToString(); }
        sb.AppendLine("| 해석 | 요소 수(굵음→촘촘) | 최대 σvm [MPa] | 최대 변위 [mm] | GCI σvm | GCI 변위 | 판정 |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var f in files)
        {
            var o = J.ParseObject(File.ReadAllText(f));
            var lv = (o["levels"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
            string G(string k) => o[k] is JsonObject g
                ? string.Format(Inv, "{0:P1} ({1}{2})", J.Num(g, "gci21", double.NaN), J.Str(g, "convergence", ""), J.Num(g, "fs", 1.25) > 1.25 ? ", Fs 3" : "")
                : "—";
            sb.AppendLine(string.Format(Inv, "| {0} | {1} | {2} | {3} | {4} | {5} | {6} |", Path.GetFileName(Path.GetDirectoryName(f)),
                string.Join(" → ", lv.Select(l => J.Num(l, "elements", 0).ToString("N0", Inv))),
                string.Join(" → ", lv.Select(l => (J.Num(l, "maxVonMises_Pa", 0) / 1e6).ToString("0.0", Inv))),
                string.Join(" → ", lv.Select(l => (J.Num(l, "maxDisplacement_m", 0) * 1000).ToString("0.000", Inv))),
                G("gciStress"), G("gciDisplacement"), J.Str(o, "message", "")));
        }
        sb.AppendLine();
        sb.AppendLine("최대 σvm 이 점하중·모서리 특이점이면 세분할수록 계속 커져 GCI 가 정의되지 않는다(진동/발산 → Fs 3 대체) — 그때는 변위 GCI 와 닫힌 식 근사를 함께 본다.");
        sb.AppendLine();
        return sb.ToString();
    }

    private static string CfdSection(string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("### CFD (FR-REL-01) — OpenFOAM v2512 기준 사례·DB 요약");
        sb.AppendLine();
        if (!File.Exists(path)) { sb.AppendLine($"CFD 결과 파일 없음(`{path}`) — `python tools/cfd/run_cfd.py collect` 후 다시 생성."); return sb.ToString(); }
        var o = J.ParseObject(File.ReadAllText(path));
        sb.AppendLine($"이미지 `{J.Str(o, "image", "")}` ({J.Str(o, "image_id", "")[..19]}…), 설정 출발점 {J.Str(o, "tutorial_base", "")}. 상세: [cfd/README.md](../cfd/README.md)");
        sb.AppendLine();
        sb.AppendLine("| 기준 사례 | 격자·방법 | 셀 | CD(CFD) | CD(TR R-300) | 오차 |");
        sb.AppendLine("|---|---|---:|---:|---:|---:|");
        foreach (var r in (o["ref2d"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            if (r["cd_cfd"] is null) continue;
            var name = Path.GetFileName(J.Str(r, "case", ""));
            var partial = r["partial"] is JsonValue pv0 && pv0.TryGetValue(out bool pb) && pb ? " (부분·미수렴 참고)" : "";
            sb.AppendLine(string.Format(Inv, "| {0} | {1} | {2:0} | {3:0.000} | {4:0.000} | {5:+0.0%;-0.0%} |", name.Split('_')[0], string.Join(" ", name.Split('_').Skip(1)) + partial,
                r["mesh"]?["cells"] is { } cells ? J.ToDouble(cells) : double.NaN, J.ToDouble(r["cd_cfd"]!), J.ToDouble(r["cd_ref"]!), J.ToDouble(r["rel_err"]!)));
        }
        sb.AppendLine();
        foreach (var g in (o["gci"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            sb.AppendLine(string.Format(Inv, "- GCI {0}: 관측 차수 p {1:0.00}, 외삽 {2:0.000}, GCI₂₁ {3:P1} ({4}) {5}", (J.Str(g, "point", "") + " " + J.Str(g, "quantity", "")).Trim(), g["p"] is JsonValue pv && pv.TryGetValue(out double pp) ? pp : double.NaN,
                J.ToDouble(g["phi_ext"]!), J.ToDouble(g["gci21"]!), J.Str(g, "convergence", ""), J.Str(g, "note", "")));
        sb.AppendLine();
        // 기준 사례 방법별 CD 상대 오차 = 모델 형태 오차 표본(UQ 편향·산포) — validation/reports/cfd/reference_case_analysis.md
        foreach (var m in (o["model_form"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            sb.AppendLine(string.Format(Inv, "- 모델 형태 오차 {0}: n {1}, 평균 {2:+0.0%;-0.0%}, 표준편차 {3}, 범위 {4:+0.0%;-0.0%} ~ {5:+0.0%;-0.0%}",
                J.Str(m, "method", ""), J.ToDouble(m["n"]!), J.ToDouble(m["mean"]!),
                m["sd"] is JsonValue sv && sv.TryGetValue(out double sd) ? sd.ToString("0.0%", Inv) : "—", J.ToDouble(m["min"]!), J.ToDouble(m["max"]!)));
        sb.AppendLine();
        return sb.ToString();
    }
}
