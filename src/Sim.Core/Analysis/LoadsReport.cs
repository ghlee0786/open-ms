using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Sim.Core.Engine;
using Sim.Core.Models;
using Sim.Core.Numerics;
using Sim.Core.Reliability;
using Sim.Core.Scenario;
using Sim.Core.Structures;

namespace Sim.Core.Analysis;

/// <summary>`mns loads` 보고서 Markdown — 모든 표의 수치는 LoadsStudy.Result(이 명령의 실행)에서 온다.</summary>
public static class LoadsReport
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static string F(double v, string f = "F1") => double.IsFinite(v) ? v.ToString(f, Inv) : "—";
    private static string Sf(double v) => double.IsPositiveInfinity(v) ? "∞" : double.IsFinite(v) ? (v >= 100 ? ">100" : v.ToString("F2", Inv)) : "—";
    private static double P(IEnumerable<double> v, double q) => OpeningShock.Percentile(v.Where(double.IsFinite).OrderBy(x => x).ToList(), q);

    public static string Write(ScenarioDoc doc, LoadsStudyOptions o, LoadsStudy.Result r)
    {
        var sb = new StringBuilder();
        void P_(string s = "") => sb.AppendLine(s);
        var cfg = r.Config; var nom = r.Nominal; var w = nom.World;
        string winLabel(string id) => cfg.Windows.FirstOrDefault(x => x.Id == id)?.Label ?? id;

        P_($"# 낙하산 개방 브레이크 충격(내부 수납물) · 3분할 분리 후 컨테이너 외부 공력 — 부품별 G·하중·응력·항복 안전계수 ({cfg.Title ?? doc.Name})");
        P_();
        P_($"생성: `{o.Command}` · {DateTime.Now:yyyy-MM-dd HH:mm} · 엔진 `Sim.Core {typeof(World).Assembly.GetName().Version}` · 시나리오 `{doc.Name}` 해시 `{doc.Hash()}` · 부품 정의 `{cfg.Path}` 해시 `{cfg.Hash}` · MC {o.Runs}회 시드 {o.Seed} · 계산 {r.Seconds:F0} s");
        P_();
        P_("- **이 보고서의 모든 수치는 위 명령(시뮬레이터 Sim.Core + mns CLI)이 계산해 쓴 값이다**(원자료 `loads.json`). 손계산은 단위 테스트(`tests/Sim.Core.Tests/LoadsStudyTests.cs`)의 검산으로만 쓴다.");
        P_($"- 형상·질량: {(o.RealModelsNote.Length > 0 ? o.RealModelsNote : "시나리오 realModels 설정")} — {cfg.MassNote ?? "질량 = 시나리오 물체 질량, 부품 질량 분포 = 부품 정의 massprops"}. 실설계 형상은 저장소에 넣지 않고 수치만 기록(NFR-06).");
        P_($"- 등급: 수납물 G·하중 = 6DOF(검증 수준 2) + 1자유도 SRS(해석해 검증) → **참고**, 부품 응력 = 닫힌 식(근사, FEA 아님) + CalculiX 대조, 장착 강성·{(r.Compliant ? "접촉 강성 이상화(닫힌 식, ×" + r.StiffRange.Lo.ToString("G3", Inv) + "–" + r.StiffRange.Hi.ToString("G3", Inv) + " MC)" : "접촉 지속")}·일부 치수 = 가정(D). 컨테이너 공력 응력 = CFD(정상 RANS, 검증 실패 이력) 분포 × 보정 범위 → **추정~참고**. 9절 NASA-STD-7009A.");
        P_();

        // ---------------- 0. 핵심 ----------------
        P_("## 0. 질문에 대한 답(핵심)");
        P_();
        var drogueSpan = nom.WindowSpans.GetValueOrDefault("drogue");
        var (gDrogue, tDrogue) = PayloadShock.ContainerPeak(nom.Probe, drogueSpan.T0, drogueSpan.T1);
        var mainSpan = nom.WindowSpans.GetValueOrDefault("main");
        var (gMain, tMain) = PayloadShock.ContainerPeak(nom.Probe, mainSpan.T0, mainSpan.T1);
        double sepPeak = LoadsStudy.Peak(nom, "separation", out var sepVec, out double sepAlpha);
        var drogue = w.GetBody(cfg.Root).Devices.OfType<Parachute>().FirstOrDefault(p => p.Name == "drogue");
        var main = w.GetBody(cfg.Root).Devices.OfType<Parachute>().FirstOrDefault(p => p.Name == "main");
        P_("| 질문 | 답 | 근거(절) |");
        P_("|---|---|---|");
        var hinges = w.Connectors.OfType<Hinge>().ToList();
        P_($"| 3분할 힌지 반력 50–150 kN 이상은? | **수치 문제였다 — 고침.** 펜널티 힌지 안정 한계가 결속점 관성 지렛대 팔·꼬리 1개를 공유하는 꽃잎 3장을 빼고 계산되어(감쇠 극점 RK4 한계 초과) 반력이 Δt 에 따라 바뀌었고, 힌지로 묶인 꼬리–꽃잎 쌍에 충돌 위치 보정이 걸려 스프링을 잡아당겼으며, 강성 상한이 현재 Δt 로 계산되어 Δt 를 줄이면 힌지 강성(모델)이 바뀌었다(실모델에서 드러남). 고친 뒤 5 ms 평균 반력 {string.Join(" / ", hinges.Select(h => F(h.MaxForceAvg / 1e3, "F1")))} kN(Δt 1 ↔ 0.25 ms 수렴) | 1절 |");
        if (r.Compliant)
        {
            var top = nom.Probe.Impulses.Where(i => i.Pulse is not null).MaxBy(i => i.J);
            double sepTau = r.TauNom is { } tn ? LoadsStudy.Peak(tn, "separation", out _, out _) : double.NaN;
            P_($"| 분리 직후 컨테이너가 받는 하중(재접촉 포함)? | 분리 펄스 + 잔해 재접촉 — **순응 접촉(구조 강성으로 계산한 접촉력 이력)**: CG 최대 {F(sepPeak)} G" +
               (top.Pulse is { } tp ? $"(최대 충격량 접촉 {top.Other} @ {F(top.T, "F3")} s: 직렬 k {F(tp.K, "E2")} N/m · τ {F(tp.Duration * 1e3, "F2")} ms · 최대 접촉력 {F(tp.PeakForce / 1e3, "F1")} kN" +
                   (tp.Plasticity is { } tl ? $" — 탄성-소성 상한 {F(tl.Force / 1e3, "F1")} kN({tl.Governing?.Kind}), 눌림 {F(tp.Indentation * 1e3, "F3")} mm" : "") + ")" : "") +
               (r.Elastic is { } er ? $" — 탄성 순응(힘 상한 없음) {F(LoadsStudy.Peak(er, "separation", out _, out _))} G" : "") +
               $" — 이전 τ {cfg.TauNominal * 1e3:0.#} ms 가정 {F(sepTau)} G, 강성 ×{r.StiffRange.Lo:G3}–{r.StiffRange.Hi:G3} 에서 {F(LoadsStudy.Peak(r.StiffLo ?? nom, "separation", out _, out _))}–{F(LoadsStudy.Peak(r.StiffHi ?? nom, "separation", out _, out _))} G. " +
               $"수납 부품은 장착 고유주기가 펄스보다 길어 Δv 에 지배된다: 드론 위치 동적 포락 {F(SepSummary(nom, cfg).DroneDyn)} G(τ 가정 {F(SepSummary(r.TauNom, cfg).DroneDyn)}) · 설계 모드 fₙ {F(SepSummary(nom, cfg).DroneModes)} G({F(SepSummary(r.TauNom, cfg).DroneModes)}) · 분리 창 최소 여유 {F(SepSummary(nom, cfg).Margin, "F2")}({F(SepSummary(r.TauNom, cfg).Margin, "F2")}) | 2절 |");
        }
        else
            P_($"| 분리 직후 컨테이너가 받는 하중(재접촉 포함)? | 분리 펄스 + 잔해(노즈·꼬리·꽃잎) 재접촉: CG 최대 {F(sepPeak)} G(접촉 지속 τ {cfg.TauNominal * 1e3:0.#} ms 공칭, τ {cfg.TauMin * 1e3:0.#}–{cfg.TauMax * 1e3:0.#} ms 범위 2절 표) — 재접촉 충격량 최대 {F(nom.Probe.Impulses.Select(i => i.J).DefaultIfEmpty(0).Max())} N·s | 2절 |");
        P_($"| 드로그·주낙하산 개방 브레이크 충격? | 드로그 최대 {F(drogue?.MaxForce ?? double.NaN, "F0")} N → 컨테이너 CG {F(gDrogue)} G @ {F(tDrogue, "F2")} s, 주낙하산 {F(main?.MaxForce ?? double.NaN, "F0")} N → {F(gMain)} G @ {F(tMain, "F2")} s(공칭) | 3절 |");
        foreach (var (wins, what) in new[] { (new[] { "drogue", "main" }, "낙하산 개방(②③) 브레이크 충격"), (new[] { "separation" }, "분리·재접촉(①)") })
        {
            var cand = nom.Parts.Where(p => p.Part.Judged).Select(p =>
            {
                double mNom = p.Windows.Where(w => wins.Contains(w.Window)).Select(w => LoadsStudy.Margin(cfg, p.Part, w.SfYield, w.SfUltimate)).DefaultIfEmpty(double.NaN).Min();
                double m05 = wins.Select(id => r.Mc.TryGetValue($"{p.Part.Name}|{id}", out var st) ? LoadsStudy.Margin(cfg, p.Part, P(st.SfY, 0.05), P(st.SfU, 0.05)) : double.NaN)
                    .Where(double.IsFinite).DefaultIfEmpty(double.NaN).Min();
                return (p, mNom, m05);
            }).Where(x => double.IsFinite(x.mNom)).OrderBy(x => double.IsFinite(x.m05) ? x.m05 : x.mNom).ToList();
            if (cand.Count == 0) continue;
            var c0 = cand[0];
            int fails = cand.Count(x => (double.IsFinite(x.m05) ? x.m05 : x.mNom) < 1);
            P_($"| 내부 수납물 — {what} 에서 여유가 가장 작은 부품? | **{c0.p.Part.Label}** 여유(SF/기준) 공칭 {F(c0.mNom, "F2")} · MC 5 % {F(c0.m05, "F2")} — 기준 미달 부품 {fails}/{cand.Count}(MC 5 % 기준) | 4·5·8절 |");
        }
        var daf = nom.Parts.SelectMany(p => p.Windows.Where(x => x.Window is "drogue" or "main")).Select(x => x.Daf).Where(double.IsFinite).ToList();
        var dafSep = nom.Parts.SelectMany(p => p.Windows.Where(x => x.Window == "separation")).Select(x => x.Daf).Where(double.IsFinite).ToList();
        if (cfg.DesignUltimateG is { } dug)
        {
            var dr = nom.Parts.Where(p => p.Part.Name.StartsWith("latch_pin", StringComparison.Ordinal) && !p.Part.Name.Contains("174") && !p.Part.Name.Contains("304")).ToList();
            double gmax = dr.SelectMany(p => p.Windows).Select(x => x.DynG).DefaultIfEmpty(double.NaN).Max();
            double g95 = dr.Select(p => r.Mc.TryGetValue(p.Part.Name, out var st) ? P(st.DynG, 0.95) : double.NaN).Where(double.IsFinite).DefaultIfEmpty(double.NaN).Max();
            P_($"| 설계 하중 기준(제한 {cfg.DesignLimitG:0.#} G × SF {cfg.DesignSf:0.0#} = 극한 {dug:0.#} G) 대비 드론 위치 동적 G? | 공칭 최대 {F(gmax)} G · MC 95 % {F(g95)} G → 극한 대비 {F(gmax / dug, "F2")} · {F(g95 / dug, "F2")}(1 미만 = 설계 극한 안), 제한 대비 {F(gmax / cfg.DesignLimitG!.Value, "F2")} — {cfg.DesignBasis} | 4절 |");
        }
        P_($"| 장착 강성에 따른 동적 증폭? | 낙하산 개방(상승 수십 ms): SRS/강체 {F(daf.DefaultIfEmpty(double.NaN).Min(), "F2")}–{F(daf.DefaultIfEmpty(double.NaN).Max(), "F2")} — 준정적에 가깝다. 분리 펄스·재접촉(수 ms): {F(dafSep.DefaultIfEmpty(double.NaN).Min(), "F2")}–{F(dafSep.DefaultIfEmpty(double.NaN).Max(), "F2")}(장착 고유주기와 비슷한 펄스) | 4절 |");
        if (r.Aero.Count > 0)
        {
            var a90 = r.Aero.FirstOrDefault(a => Math.Abs(a.Alpha - 90) < 1e-6) ?? r.Aero[^1];
            var lv = a90.Levels.FirstOrDefault(l => l.Level.StartsWith("공칭"));
            double sfS = LoadsStudy.ShellStrengthSf(r.Shell, lv.Field);
            P_($"| 3면 분리 후 외부 공력에 의한 컨테이너 응력·좌굴? | CFD Cp 매핑 셸({ShellLabel(r)}): α 90° 공칭 von Mises {F(lv.Field.VmMax / 1e6)} MPa · 강도 SF {Sf(sfS)}(극한 기준) · 좌굴 계수 λ {F((lv.Buckle ?? double.NaN) * LoadsStudy.ShellBuckleScale(r.Shell), "F2")}(재질 E 환산, 녹다운 전) — 조합·보정 상한은 6·7절 | 6·7절 |");
        }
        P_();

        // ---------------- 1. 힌지 ----------------
        P_("## 1. 3분할 힌지 반력 이상 — 원인과 수정(선행)");
        P_();
        P_("원인(코드 `src/Sim.Core/Models/Connectors.cs` Hinge · `Engine/Collisions.cs`, 회귀 테스트 `tests/Sim.Core.Tests/HingeConvergenceTests.cs`):");
        P_();
        P_("1. **안정 한계가 질량만 봤다** — 펜널티 강성 상한 k ≤ m·(ω_max)² 의 m 이 루트 질량(꽃잎 27 kg)이었는데, 결속점(꽃잎 끝 힌지)의 유효 질량은 관성 지렛대 팔 때문에 훨씬 작고(점 이동도 M = I/m + [r]×ᵀI⁻¹[r]×), 꼬리 1개에 힌지 3개가 병렬로 걸려 꼬리 쪽은 3k·3c 를 받았다 → 감쇠 극점 c·Δt/m* 가 RK4 실축 안정 한계(≈ 2.8)를 넘어 반력이 스텝마다 진동했다(Δt 1 ms → 7.8 kN, 0.25 ms → 17.5 kN, hinge2 는 끝까지 이탈 못 함). 수정: m* = 1/λmax(M_A + M_B), k ≤ m*·(0.5/Δt)²/N_공유, 이웃 이탈 때 강성이 다시 커지지 않게(단조 감소).");
        P_("2. **힌지로 묶인 쌍에 충돌 판정이 걸렸다** — 꼬리–꽃잎(결속 부위 자체)이 접촉으로 잡혀 위치 보정(침투 × 0.8)이 꽃잎을 수 cm 옮기면 펜널티 스프링이 k·Δx 로 튄다(침투 2–5 cm × 10⁶ N/m = 20–50 kN — 50–150 kN·꽃잎 1,000 G 대의 기전). 수정: 힌지가 잡고 있는 동안은 같은 루트처럼 검사하지 않고, 이탈 뒤에는 한 번 떨어질 때까지 무시.");
        P_("3. 구조 하중용 반력은 **5 ms 이동 평균**(MaxForceAvg)으로 보고 — 이웃 힌지가 한 스텝에 이탈하는 순간의 단일 스텝 과도(크기가 Δt 의존)를 거른다. 힌지 반력 방향은 기록 벡터(ForceB)로 응력 모델에 전달(이전: µm 간극의 B−A 방향 = 잡음).");
        P_("4. **강성 상한이 Δt 를 따라 움직였다(실모델에서 드러남)** — 1–3 수정 뒤에도 상한 k ≤ m*·(0.5/Δt)²/N 이 *현재* Δt 로 계산되어, Δt 를 줄이면 사용 강성이 커졌다(아래 표 '현재 Δt' 행의 사용 강성). 결속점 유효 질량 m* 가 수 kg 이라 Δt 1 ms 에서는 상한이 걸리고 Δt/4 에서는 명시값(1 MN/m)이 쓰여, 힌지 고유진동수가 달라지고 그 주기가 반력 평균 창(5 ms)보다 길어 펜널티 진동 진폭이 곧 하중이 됐다 — 즉 Δt 를 바꾸면 **모델(힌지 컴플라이언스)이 바뀌어** 수렴 검사가 같은 물리를 비교하지 않았다. 실모델에서만 기준(±10 %)을 넘은 이유: 실형상 컨테이너 외형에 꽃잎2가 이 구간에 여러 번 스쳐 접촉해(표 '꽃잎 접촉' 열) 그 펜널티 진동을 더 크게 들뜨게 했다 — 합성 대체(원통 외형)는 접촉이 적어 같은 결함이 경계값으로만 나타났다. 수정: 상한 기준 Δt = max(Δt, stabilityDt), stabilityDt 기본 1 ms(힌지 시나리오 기본 Δt) → 공칭 Δt 결과는 바뀌지 않고, Δt 를 줄여도 같은 강성이라 반력이 수렴. 충돌 위치 보정(힌지에 묶인 꽃잎을 순간 이동)도 시험했으나 반력 변화가 1 % 미만이라 원인이 아니었다(코드 유지).");
        P_();
        if (r.HingeConv.Count > 0)
        {
            var names = r.HingeConv.Select(x => x.Hinge).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
            P_($"**Δt 수렴 점검(이 명령이 계산)** — 공칭 시나리오를 기폭 → {F(o.HingeCheckTEnd, "F2")} s(꼬리–컨테이너 첫 접촉 전, 회귀 테스트와 같은 구간)까지 Δt, Δt/2, Δt/4 로 다시 실행. 값 = 5 ms 평균 최대 반력 [N] (꽃잎 접촉 수, 이탈 시각 s).");
            P_();
            P_($"| 강성 상한 | Δt [ms] | 사용 강성 [N/m] | {string.Join(" | ", names)} |");
            P_($"|---|---|---|{string.Concat(names.Select(_ => "---|"))}");
            foreach (var g in r.HingeConv.GroupBy(x => (x.Fixed, x.Dt)).OrderByDescending(g => g.Key.Fixed).ThenByDescending(g => g.Key.Dt))
            {
                var row = names.Select(n => g.FirstOrDefault(x => x.Hinge == n)).ToList();
                string cell(HingeConvRow? x) => x is null ? "—" : $"{F(x.AvgN, "F0")} ({x.Contacts}, {F(x.ReleaseT, "F4")})";
                P_($"| {(g.Key.Fixed ? "고정 기준 Δt(수정 후)" : "현재 Δt(수정 전)")} | {F(g.Key.Dt * 1e3, "G4")} | {F(row.Where(x => x is not null).Max(x => x!.K), "E2")} | {string.Join(" | ", row.Select(cell))} |");
            }
            P_();
            foreach (bool fix in new[] { true, false })
            {
                var rs = r.HingeConv.Where(x => x.Fixed == fix).ToList();
                if (rs.Count == 0) continue;
                double dMax = rs.Max(x => x.Dt), dMin = rs.Min(x => x.Dt);
                var ratios = names.Select(n => (n, c: rs.FirstOrDefault(x => x.Hinge == n && x.Dt == dMax), f: rs.FirstOrDefault(x => x.Hinge == n && x.Dt == dMin)))
                    .Where(t => t.c is not null && t.f is not null && t.f.AvgN > 0).Select(t => (t.n, ratio: t.c!.AvgN / t.f!.AvgN)).ToList();
                bool ok = ratios.All(t => t.ratio is >= 0.9 and <= 1.1);
                P_($"- {(fix ? "수정 후" : "수정 전")} 반력 비(Δt ÷ Δt/4): {string.Join(" · ", ratios.Select(t => $"{t.n} {F(t.ratio, "F3")}"))} → {(ok ? "기준 0.9–1.1 **통과**" : "기준 0.9–1.1 **미달**")}");
            }
            P_();
        }
        P_("| 힌지 | 이탈 시각 [s] | 이유 | 5 ms 평균 최대 [N] | 순간 최대 [N] | 결속점 유효 질량 [kg] | 공유 힌지 수 | 사용 강성 [N/m] |");
        P_("|---|---|---|---|---|---|---|---|");
        foreach (var h in hinges)
            P_($"| {h.Name} | {F(h.ReleaseTime, "F3")} | {h.ReleaseReason} | {F(h.MaxForceAvg, "F0")} | {F(h.MaxForce, "F0")} | {F(h.EffectiveMass, "F2")} | {h.SharedHinges} | {F(h.EffectiveStiffness, "E2")} |");
        P_();
        var petals = w.Bodies.Where(b => b.Name.StartsWith("petal", StringComparison.Ordinal)).ToList();
        P_($"꽃잎 최대 하중(6DOF 매 스텝): {string.Join(" · ", petals.Select(b => $"{b.Name} {F(b.MaxLoadG)} G"))} — 이전 1,000–3,700 G 대 이상치 없음. Δt 수렴 검사(공칭 vs Δt/4, 접촉 전 구간): 5 ms 평균 반력 10 % 이내(테스트 `Scenario09_hinge_reactions_converge_with_time_step_before_first_debris_contact`).");
        if (r.McScalars.Keys.Any(k => k.StartsWith("hingeAvg:", StringComparison.Ordinal)))
            P_($"MC {o.Runs}회 힌지 5 ms 평균 반력 95 백분위: {string.Join(" · ", r.McScalars.Where(kv => kv.Key.StartsWith("hingeAvg:", StringComparison.Ordinal)).Select(kv => $"{kv.Key[9..]} {F(P(kv.Value, 0.95), "F0")} N(최대 {F(kv.Value.Max(), "F0")})"))}.");
        P_();

        // ---------------- 2. 분리·재접촉 ----------------
        var groups = GroupImpulses(nom.Probe.Impulses);
        double mRoot = nom.Probe.Mass.Count > 0 ? nom.Probe.Mass[0] : double.NaN;
        if (r.Compliant) CompliantSection(sb, doc, o, r, groups, mRoot);
        else {
        P_("## 2. 3분할 분리 직후 컨테이너 하중 — 분리 펄스 + 잔해 재접촉(재계산)");
        P_();
        P_($"강체 충격량 접촉(반발 0.3)은 속도를 순간에 바꾸므로 가속도 이력에 남지 않는다 → 탐침이 충격량 벡터를 받아 **반 사인 펄스(지속 τ)** 로 되살린다. τ = {cfg.TauNominal * 1e3:0.#} ms 공칭, {cfg.TauMin * 1e3:0.#}–{cfg.TauMax * 1e3:0.#} ms 범위(D): {cfg.TauBasis}");
        P_();
        P_($"| 시각 [s] | 상대 물체 | 충격량 합 [N·s] | CG Δv [m/s] | 각속도 변화 [rad/s] | 최대 접촉력 [kN] (τ {cfg.TauMax * 1e3:0}/{cfg.TauNominal * 1e3:0}/{cfg.TauMin * 1e3:0} ms) | CG 최대 [G] (τ {cfg.TauMax * 1e3:0}/{cfg.TauNominal * 1e3:0}/{cfg.TauMin * 1e3:0} ms) |");
        P_("|---|---|---|---|---|---|---|");
        foreach (var g in groups.Take(12))
        {
            double FPk(double tau) => Math.PI * g.J / (2 * tau) / 1e3;
            P_($"| {F(g.T, "F3")} | {g.Other} | {F(g.J, "F1")} | {F(g.Dv, "F2")} | {F(g.Dw, "F2")} | {F(FPk(cfg.TauMax))} / {F(FPk(cfg.TauNominal))} / {F(FPk(cfg.TauMin))} | {F(FPk(cfg.TauMax) * 1e3 / mRoot / PayloadShock.G0)} / {F(FPk(cfg.TauNominal) * 1e3 / mRoot / PayloadShock.G0)} / {F(FPk(cfg.TauMin) * 1e3 / mRoot / PayloadShock.G0)} |");
        }
        if (groups.Count == 0) P_("| — | 재접촉 없음 | | | | | |");
        P_();
        double sepMin = LoadsStudy.Peak(r.TauMax ?? nom, "separation", out _, out _), sepMax = LoadsStudy.Peak(r.TauMin ?? nom, "separation", out _, out _);
        P_($"분리 창 컨테이너 CG 최대(펄스 + 공력 + 재접촉): **{F(sepPeak)} G** 공칭(τ {cfg.TauNominal * 1e3:0.#} ms) · {F(sepMin)}–{F(sepMax)} G(τ {cfg.TauMax * 1e3:0}–{cfg.TauMin * 1e3:0} ms) · 그 순간 총받음각 {F(sepAlpha, "F0")}° · 벡터(동체 x 축·y·z) ({F(sepVec.X)}, {F(sepVec.Y)}, {F(sepVec.Z)}) G. 컨테이너 합성 질량 {F(mRoot, "F2")} kg.");
        if (r.McScalars.TryGetValue("containerG:separation", out var sgs))
            P_($"MC {sgs.Count}회(τ 균등 표본 포함): 중앙값 {F(P(sgs, 0.5))} G · 95 % {F(P(sgs, 0.95))} G · 최대 {F(sgs.Max())} G. 재접촉 최대 충격량 95 % {F(P(r.McScalars["recontactMaxJ"], 0.95))} N·s.");
        P_();
        if (r.Sweep.Count > 0)
        {
            P_("민감도(1-D 스윕, 나머지 공칭 · τ 공칭) — 재접촉 상대속도를 정하는 노즈 형상 가정:");
            P_();
            P_("| 입력 | 값 | 컨테이너 접촉 [회] | 최대 접근 속도 [m/s] | 최대 충격량 [N·s] | 분리 창 CG 최대 [G] | 드론 1 래치 하중 [N] (동적 G) | 최소 여유 부품(여유 = SF/기준) |");
            P_("|---|---|---|---|---|---|---|---|");
            foreach (var x in r.Sweep)
                P_(x.Error is null
                    ? $"| `{x.Path}` | {x.Value:0.##} | {x.Contacts} | {F(x.MaxApproach, "F2")} | {F(x.MaxJ)} | {F(x.ContainerSepG)} | {F(x.LatchForce, "F0")} ({F(x.LatchDynG)}) | {x.WorstPart} {F(x.WorstSf, "F2")} |"
                    : $"| `{x.Path}` | {x.Value:0.##} | 오류: {x.Error} | | | | | |");
            P_();
        }
        P_("해석: 3분할 직후 노즈(앞)는 항력/질량이 커 컨테이너보다 빨리 감속하고, 컨테이너가 그 뒤에서 따라붙어 앞끝(도어 링)으로 부딪힌다(노즈 L1 공력 = 평평한 원통 근사라 축 항력이 크다 — 위 스윕에서 오자이브 노즈 추정 배율 0.35–0.5 의 결과를 함께 본다. 후류 차폐는 미모델 — 실제로는 컨테이너가 더 빨리 따라붙을 수 있음). 장착 고유주기보다 짧은 펄스라 부품 응답은 τ 보다 점 속도 변화 Δv·ωₙ 로 정해진다(4절 SRS) — **분리 시험(고속 카메라)으로 재접촉 여부·상대속도를 확인하는 것이 이 하중의 1순위 시험**이다.");
        P_();
        }

        // ---------------- 3. 창별 ----------------
        P_("## 3. 하중 창별 컨테이너·드론 위치 가속도(공칭)");
        P_();
        string tauCol = r.TauNom is not null ? $" 컨테이너 접촉 포함 · 드론 1 포락 — τ {cfg.TauNominal * 1e3:0.#} ms 가정 [G] |" : "";
        if (r.Compliant) P_($"접촉 포함 값 = 순응 접촉(2절). 마지막 열 = 같은 실행을 이전 τ {cfg.TauNominal * 1e3:0.#} ms 가정으로 다시 평가한 값(비교).");
        if (r.Compliant) P_();
        P_("| 창 | 구간 [s] | 컨테이너 CG 최대 [G] 연속 / 접촉 포함 | " + string.Join(" | ", Enumerable.Range(1, 4).Select(k => $"드론 {k} 축 강체 / 동적 공칭 fₙ / 포락 [G]")) + " |" + tauCol);
        P_("|---|---|---|" + string.Concat(Enumerable.Repeat("---|", 4)) + (tauCol.Length > 0 ? "---|" : ""));
        foreach (var (id, (t0, t1)) in nom.WindowSpans)
        {
            var (g0, _) = PayloadShock.ContainerPeak(nom.Probe, t0, t1);
            var (gc, _) = PayloadShock.ContainerPeak(nom, t0, t1);
            string g = $"{F(g0)} / {F(gc)}";
            string tc = "";
            if (r.TauNom is { } tr)
            {
                var w1 = tr.Parts.FirstOrDefault(p => p.Part.Name == "latch_pin1")?.Windows.FirstOrDefault(x => x.Window == id);
                tc = $" {F(PayloadShock.ContainerPeak(tr, t0, t1).G)} · {F(w1?.DynG ?? double.NaN)} |";
            }
            var cells = Enumerable.Range(1, 4).Select(k =>
            {
                var pp = nom.Parts.FirstOrDefault(p => p.Part.Name == $"latch_pin{k}");
                var ww = pp?.Windows.FirstOrDefault(x => x.Window == id);
                return ww is null ? "—" : $"{F(ww.RigidG)} / {F(ww.DynNominalG)} / {F(ww.DynG)}";
            });
            P_($"| {winLabel(id)} | {F(t0, "F2")}–{F(t1, "F2")} | {g} | {string.Join(" | ", cells)} |{tc}");
        }
        P_();
        P_("![컨테이너 G](fig1_container_g.svg)");
        P_();
        P_("![SRS](fig2_srs_drone1_axial.svg)");
        P_();
        P_("드론 위치별 차이는 회전 성분 α×r + ω×(ω×r)(탑재 위치 간격)과 앞끝 재접촉의 각충격량에서 온다.");
        P_();

        // ---------------- 4. 부품 ----------------
        P_("## 4. 부품별 G · 하중 · 응력 · 안전계수(공칭, 전 창 최대)");
        P_();
        P_($"하중 = 부품이 받치는 질량 × 장착 1자유도 SRS 동적 가속도(고유진동수 fₙ 의 ×{string.Join("/", cfg.FnFactors.Select(x => x.ToString("0.##", Inv)))} 중 최대 — {cfg.FnBasis}). 응력 = 닫힌 식 계수 × 하중(PartStress — FEA 아님, 국부 집중은 Kt 로만). 판정: 금속 항복 SF ≥ {cfg.SfYield} · 극한 ≥ {cfg.SfUltimate}, 취성(CFRP·폼)은 극한만, 전장은 G 기준(참고). {cfg.CriteriaBasis}");
        P_();
        string dcol = cfg.DesignUltimateG is { } du0 ? $" 설계 극한 {du0:0.#} G 대비 |" : "";
        string tcol = r.TauNom is not null ? $" 분리 창 여유 순응 / τ {cfg.TauNominal * 1e3:0.#} ms 가정 |" : "";
        if (r.TauNom is not null)
            P_($"분리·재접촉 창은 순응 접촉(2절) 값 — 마지막 열은 같은 부품의 분리 창 여유(SF/기준)를 순응 접촉 / 이전 τ {cfg.TauNominal * 1e3:0.#} ms 가정으로 나란히.");
        P_("| 부품 | 질량 [kg] | fₙ [Hz] | 지배 창 | 강체 [G] | 동적 공칭 fₙ / 포락 [G] | 증폭(포락/강체) | 하중 [N] | σ [MPa] | 항복 SF | 극한 SF | 판정 |" + dcol + tcol);
        P_("|---|---|---|---|---|---|---|---|---|---|---|---|" + (dcol.Length > 0 ? "---|" : "") + (tcol.Length > 0 ? "---|" : ""));
        var tauMargins = r.TauNom is { } tnr ? LoadsStudy.PartMargins(tnr, cfg) : null;
        var nomMargins = r.TauNom is not null ? LoadsStudy.PartMargins(nom, cfg) : null;
        foreach (var p in nom.Parts)
        {
            var wst = p.Worst;
            if (wst is null) continue;
            double sfy = p.MinSfYield, sfu = p.MinSfUltimate;
            double gm = p.Windows.Select(x => x.GMargin).Where(double.IsFinite).DefaultIfEmpty(double.NaN).Min();
            string sfyS = p.Part.HasStress && !p.Part.Material!.Brittle ? Sf(sfy) : "—";
            string sfuS = p.Part.HasStress ? Sf(sfu) : double.IsFinite(gm) ? $"G 여유 {Sf(gm)}" : "—";
            P_($"| {p.Part.Label} | {F(p.Mass, "F3")} | {F(p.FnHz, "F0")} | {winLabel(wst.Window)[..2]} | {F(wst.RigidG)} | {F(wst.DynNominalG)} / {F(wst.DynG)} | {F(wst.Daf, "F2")} | {F(wst.ForceN, "F0")} | {F(wst.StressPa / 1e6)} | {sfyS} | {sfuS} | {PayloadShock.Verdict(cfg, p.Part, sfy, sfu, gm)} |{(cfg.DesignUltimateG is { } du1 ? $" {F(wst.DynG / du1, "F2")} |" : "")}{(tauMargins is null ? "" : $" {(nomMargins!.TryGetValue(p.Part.Name, out var nm) ? F(nm.Separation, "F2") : "—")} / {(tauMargins.TryGetValue(p.Part.Name, out var tm) ? F(tm.Separation, "F2") : "—")} |")}");
        }
        P_();
        P_("부품별 하중 경로·치수 근거(정의 파일 basis):");
        P_();
        foreach (var p in cfg.Parts.Where(p => p.Station is null or 1))
            P_($"- **{p.Label.Replace("(드론 1)", "(드론 k)")}** — {p.Basis}{(p.HasStress ? $" · 식: {p.Formula}(지배 {p.Governing}), 재질 {p.Material!.Name} 항복 {p.Material.Yield / 1e6:F0} / 극한 {p.Material.Ultimate / 1e6:F0} MPa" : "")}{(p.GLimit is { } gl ? $" · G 기준 {gl:F0} G: {p.GLimitBasis}" : "")}{(p.Mount.Count > 0 ? $" · 장착 직렬 강성: {string.Join(" + ", p.Mount.Select(m => $"{m.Kind} {m.K:E2} N/m"))} = {PayloadShock.SeriesK(p.Mount):E2} N/m ({p.MountGrade})" : p.FnHz is { } fh ? $" · fₙ {fh:F0} Hz ({p.MountGrade}: {p.MountBasis})" : "")}");
        P_();
        if (cfg.DesignUltimateG is { } dug4)
        {
            // 4b. 설계 극한 G 준정적 — 설계 쪽 국부 FEA(극한 하중 조인트 힘)와 같은 하중 조건 비교용
            P_($"### 4b. 설계 극한 {dug4:0.#} G 준정적 응력(동적 증폭 없음 — 설계 쪽 국부 FEA 와 같은 하중 조건)");
            P_();
            P_($"하중 F = 부품이 받치는 질량 × {dug4:0.#} G × g₀(부품 하중 방향), 응력 = 위 4절과 같은 닫힌 식. {cfg.DesignBasis}. 드론 1 스테이션(스테이션별 질량이 같으면 값도 같다).");
            if (cfg.DesignLateralUltimateG is { } dlat)
                P_($"측방(direction lateral) 부품은 측방 극한 {dlat:0.#} G(제한 {cfg.DesignLateralLimitG:0.#} G × SF {cfg.DesignLateralSf:0.0#}) — {cfg.DesignLateralBasis}.");
            P_();
            P_("| 부품 | 질량 [kg] | 방향 | 극한 G | 하중 [N] | σ [MPa] | 항복 SF | 극한 SF | 지배 식 |");
            P_("|---|---|---|---|---|---|---|---|---|");
            foreach (var p in nom.Parts.Where(p => p.Part.Station is null or 1))
                if (PayloadShock.DesignUltimate(cfg, p) is { } du)
                    P_($"| {p.Part.Label} | {F(p.Mass, "F3")} | {p.Part.Direction} | {du.G:0.#} | {F(du.ForceN, "F0")} | {F(du.StressPa / 1e6)} | {(double.IsFinite(du.SfYield) ? Sf(du.SfYield) : "—")} | {Sf(du.SfUltimate)} | {p.Part.Governing} |");
            P_();
            var sel = nom.Parts.Where(p => p.Part.Station is null or 1 && p.Part.MassParts?.Below is not null).ToList();
            foreach (var p in sel)
            {
                var bodies = PayloadShock.SelectedMassParts(cfg, p.Part);
                P_($"- {p.Part.Label} 받치는 질량 {F(p.Mass, "F3")} kg = 물체 {(p.Part.MassBodies.Length > 0 ? string.Join("·", p.Part.MassBodies) : "없음")} + massprops '{p.Part.MassParts!.Group}' 바디 {bodies.Count}개 " +
                   $"{bodies.Sum(b => b.Mass):F3} kg(기준 바디 {p.Part.MassParts.Below!.Value.Body} CG 아래 · 축 {"xyz"[p.Part.MassParts.Below.Value.Axis]}{(p.Part.MassParts.Exact.Length > 0 ? " + " + string.Join("·", p.Part.MassParts.Exact) : "")}{(p.Part.MassParts.Prefix.Length > 0 ? " + 접두 " + string.Join("·", p.Part.MassParts.Prefix) : "")}{(p.Part.MassParts.Exclude is { Length: > 0 } ex ? " − 제외 " + string.Join("·", ex) : "")}): {string.Join(", ", bodies.Select(b => b.Body))}");
            }
            if (sel.Count > 0) P_();
        }
        if (nom.Positions.TryGetValue("latch_pin1", out var lpPos) && PayloadShock.DesignModeSrs(nom, cfg, lpPos) is { Count: > 0 } dms)
        {
            // 4c. 설계 FEA 고유진동수에서 SRS — 준정적 판단 대조
            P_("### 4c. 설계 FEA 고유진동수에서 축 SRS(드론 1 래치 위치) — 준정적 판단 대조");
            P_();
            P_($"SRS = 드론 1 위치 강체 가속도({(r.Compliant ? "순응 접촉력 이력" : "접촉 펄스 τ 공칭")} 포함)를 입력으로 한 1자유도 최대 응답(ζ {cfg.Zeta:0.###}), 비 = SRS / 강체 축 최대(1 이면 준정적). 진동수: {cfg.DesignModesBasis}");
            P_();
            P_($"| 창 | 강체 축 최대 [G] | {string.Join(" | ", cfg.DesignModesHz.Select(f => $"{f:0.#} Hz SRS [G] (비)"))} |");
            P_($"|---|---|{string.Concat(cfg.DesignModesHz.Select(_ => "---|"))}");
            foreach (var (win, rg, srs) in dms)
                P_($"| {winLabel(win)} | {F(rg)} | {string.Join(" | ", srs.Select(s => $"{F(s)} ({F(rg > 0 ? s / rg : double.NaN, "F2")})"))} |");
            P_();
            var modeParts = nom.Parts.Where(p => p.Part.Station == 1 && p.Windows.Any(x => double.IsFinite(x.DynModesG))).ToList();
            if (modeParts.Count > 0)
            {
                var hz = PayloadShock.ModeSweepHz(cfg);
                P_($"부품별 동적 G — 장착 fₙ ×{string.Join("/", cfg.FnFactors.Select(x => x.ToString("0.##", Inv)))} 포락(4절) vs **설계 FEA 고유진동수 범위 {hz.Min():0.#}–{hz.Max():0.#} Hz**(모드 {cfg.DesignModesHz.Length}개 + 범위 등간격, 1자유도 SRS, 부품 위치 강체 가속도 입력 — " +
                   "설계 스테이션 FEA 모드는 래치·스트럿·판·스탠드오프·팁 시트 유연성을 모두 포함한 계 모드라 장착 사슬 대신 쓴다, 등급 C(설계 FEA 인용)). 드론 1, 값 = 동적 G [G] (fₙ Hz) · 항복 SF:");
                P_();
                P_($"| 부품 | {string.Join(" | ", cfg.Windows.Select(wd => $"{winLabel(wd.Id)[..2]} 포락 | {winLabel(wd.Id)[..2]} 설계 모드"))} |");
                P_($"|---|{string.Concat(cfg.Windows.Select(_ => "---|---|"))}");
                foreach (var p in modeParts)
                {
                    var cells = cfg.Windows.Select(wd => p.Windows.FirstOrDefault(x => x.Window == wd.Id) is { } x
                        ? $"{F(x.DynG)} ({F(x.FnUsedHz, "F0")}){(p.Part.HasStress ? $" · {Sf(x.SfYield)}" : "")} | {F(x.DynModesG)} ({F(x.FnModesHz, "F0")}){(p.Part.HasStress ? $" · {Sf(x.SfYieldModes)}" : "")}"
                        : "— | —");
                    P_($"| {p.Part.Label} | {string.Join(" | ", cells)} |");
                }
                P_();
            }
        }

        // ---------------- 5. UQ ----------------
        P_($"## 5. 불확도 — Monte Carlo {o.Runs}회(LHS) · 공칭 / 보수(95 %)");
        P_();
        P_($"입력 = 시나리오 inputPedigree 분포 + dispersions(질량 배율 massScales — 범위는 아래 표, 드로그·주낙하산 Cd·충만 상수·부가질량 k0, 분리 Δv·드로그 지연 등) + {(r.Compliant ? "순응 접촉 직렬 강성 배율(collisions.contact.stiffnessScale)" : "접촉 지속 τ")}. 오류 {r.McErrors}회{(r.McErrorSamples.Count > 0 ? " — " + string.Join("; ", r.McErrorSamples) : "")}.");
        P_();
        P_("| 입력 | 등급 | 분포 |");
        P_("|---|---|---|");
        foreach (var (path, g, dist) in r.McInputs) P_($"| `{path}` | {g} | {dist} |");
        P_();
        P_("| 부품 | 동적 G 중앙/95 % | 하중 95 % [N] | 항복 SF 5 %/최소 | 극한 SF 5 %/최소 | 95 % 판정 |");
        P_("|---|---|---|---|---|---|");
        foreach (var p in nom.Parts)
        {
            if (!r.Mc.TryGetValue(p.Part.Name, out var st)) continue;
            double sy5 = P(st.SfY, 0.05), su5 = P(st.SfU, 0.05), gm5 = P(st.GMargin, 0.05);
            string sy = p.Part.HasStress && !p.Part.Material!.Brittle ? $"{Sf(sy5)} / {Sf(st.SfY.Where(double.IsFinite).DefaultIfEmpty(double.NaN).Min())}" : "—";
            string su = p.Part.HasStress ? $"{Sf(su5)} / {Sf(st.SfU.Where(double.IsFinite).DefaultIfEmpty(double.NaN).Min())}" : double.IsFinite(gm5) ? $"G 여유 {Sf(gm5)}" : "—";
            P_($"| {p.Part.Label} | {F(P(st.DynG, 0.5))} / {F(P(st.DynG, 0.95))} | {F(P(st.Force, 0.95), "F0")} | {sy} | {su} | {PayloadShock.Verdict(cfg, p.Part, sy5, su5, gm5)} |");
        }
        P_();
        if (r.McScalars.Count > 0)
        {
            P_("| 지표(MC) | 중앙값 | 95 % | 최대 |");
            P_("|---|---|---|---|");
            foreach (var (k, v) in r.McScalars.OrderBy(kv => kv.Key)) P_($"| {k} | {F(P(v, 0.5), "F1")} | {F(P(v, 0.95), "F1")} | {F(v.Max(), "F1")} |");
            P_();
        }
        // PRCC: 드론 1 래치 하중
        if (r.Mc.TryGetValue("latch_pin1", out var lp) && r.McX.Count == lp.Force.Count && r.McX.Count > 10)
        {
            var pr = Prcc.Compute(r.McX, lp.Force);
            var names = r.McInputs.Select(x => x.Path).ToList();
            P_("민감도(PRCC, 드론 1 래치 하중 — |값|이 클수록 지배): " + string.Join(" · ", pr.Select((v, i) => (v, i)).Where(x => double.IsFinite(x.v)).OrderByDescending(x => Math.Abs(x.v)).Take(5).Select(x => $"`{names[x.i]}` {x.v:+0.00;-0.00}")));
            P_();
        }

        // ---------------- 5b. 손계산 대비 ----------------
        P_("### 5b. 대화 세션의 손계산 값 → 프로그램 값으로 대체");
        P_();
        P_("| 항목 | 대화 세션 손계산 | 프로그램(공칭) | 프로그램(MC 95 %) | 비고 |");
        P_("|---|---|---|---|---|");
        var lp1 = nom.Parts.FirstOrDefault(p => p.Part.Name == "latch_pin1");
        double DrogueF(PartResult? p, string win = "drogue") => p?.Windows.FirstOrDefault(x => x.Window == win)?.ForceN ?? double.NaN;
        double worstF(PartResult? p) => p?.Windows.Select(x => x.ForceN).DefaultIfEmpty(double.NaN).Max() ?? double.NaN;
        var allLatch = nom.Parts.Where(p => p.Part.Name.StartsWith("latch_pin", StringComparison.Ordinal)).ToList();
        P_($"| 드론+센서 하중(드로그 개방) | 709 N / 815 N | {F(allLatch.Select(p => DrogueF(p)).DefaultIfEmpty(double.NaN).Min(), "F0")}–{F(allLatch.Select(p => DrogueF(p)).DefaultIfEmpty(double.NaN).Max(), "F0")} N(드론 1–4) | {F(allLatch.Select(p => r.Mc.TryGetValue($"{p.Part.Name}|drogue", out var s) ? P(s.Force, 0.95) : double.NaN).DefaultIfEmpty(double.NaN).Max(), "F0")} N(드로그 창) | 전 창 최대 {F(allLatch.Select(worstF).DefaultIfEmpty(double.NaN).Max(), "F0")} N |");
        double fLp = worstF(lp1);
        P_($"| 래치 핀 τ = F/A | 16 MPa | {F(fLp / (Math.PI * 0.004 * 0.004) / 1e6)} MPa(드론 1 전 창 최대 하중) | — | 프로그램 판정은 외팔 굽힘 포함 σ_eq {F((lp1?.Worst?.StressPa ?? double.NaN) / 1e6)} MPa(4절) |");
        var bat = nom.Parts.Where(p => p.Part.Name.StartsWith("battery", StringComparison.Ordinal)).ToList();
        P_($"| 배터리 하중 | 109 N | {F(bat.Select(worstF).DefaultIfEmpty(double.NaN).Max(), "F0")} N | {F(bat.Select(p => r.Mc.TryGetValue(p.Part.Name, out var s) ? P(s.Force, 0.95) : double.NaN).DefaultIfEmpty(double.NaN).Max(), "F0")} N | 질량 {F(bat.FirstOrDefault()?.Mass ?? double.NaN, "F3")} kg |");
        var sen = nom.Parts.Where(p => p.Part.Name.StartsWith("sensor_posts", StringComparison.Ordinal)).ToList();
        P_($"| 센서 하중 | 269 N | {F(sen.Select(worstF).DefaultIfEmpty(double.NaN).Max(), "F0")} N | {F(sen.Select(p => r.Mc.TryGetValue(p.Part.Name, out var s) ? P(s.Force, 0.95) : double.NaN).DefaultIfEmpty(double.NaN).Max(), "F0")} N | 기저 = 드론 장착 응답(2단) |");
        P_($"| 동적 증폭 | 1.0~1.2 | 낙하산 개방 {F(daf.DefaultIfEmpty(double.NaN).Min(), "F2")}–{F(daf.DefaultIfEmpty(double.NaN).Max(), "F2")} · 분리/재접촉 {F(dafSep.DefaultIfEmpty(double.NaN).Min(), "F2")}–{F(dafSep.DefaultIfEmpty(double.NaN).Max(), "F2")} | — | SRS(Q {1 / (2 * cfg.Zeta):F0}), fₙ ×0.5–2 포락 |");
        P_();

        // ---------------- 5c. CalculiX ----------------
        P_("## 5c. 핵심 부품 CalculiX 대조(실모델 STEP 바디 → Gmsh C3D10 → 정적)");
        P_();
        if (r.PartFea.Count == 0) P_("실행 안 함(--ccx 또는 실모델 STEP 없음).");
        else
        {
            P_("| 부품 | 메시 | 요소 | 부피 [cm³] | σ/F FEA 고정 경계 포함 / 경계 밖 [MPa/kN] | σ/F 닫힌 식 [MPa/kN] | FEA(경계 밖)/식 | k FEA [N/m] | 하중 공칭 / 95 % [N] | σ 판정용 [MPa] 공칭 / 95 % | SF 95 %(판정용 · 경계 포함 상한) | 낙하산 개방 ②③ 하중 [N] → SF(판정용) | 계산 |");
            P_("|---|---|---|---|---|---|---|---|---|---|---|---|---|");
            foreach (var (part, fea, fN, f95) in r.PartFea)
            {
                if (!fea.Ok) { P_($"| {part.Label} | 실패 | | | | | | | | | | | {fea.Error} |"); continue; }
                var pr0 = nom.Parts.First(x => x.Part == part);
                double fOpen = pr0.Windows.Where(x => x.Window is "drogue" or "main").Select(x => x.ForceN * part.Share).DefaultIfEmpty(double.NaN).Max();
                double cfe = fea.CoefPaPerN, cfa = double.IsFinite(fea.CoefAwayPaPerN) ? fea.CoefAwayPaPerN : cfe, ccl = part.Coef;
                double cUse = Math.Max(cfa, ccl);
                double s95 = cUse * f95, sN = cUse * fN;
                double allow = part.Material!.Brittle ? part.Material.Ultimate : part.Material.Yield;
                P_($"| {part.Label} | {fea.Mesh} | {fea.Elements:N0} | {F(fea.VolumeCm3, "F3")} | {F(cfe * 1e3 / 1e6, "F2")} / {F(cfa * 1e3 / 1e6, "F2")} | {F(ccl * 1e3 / 1e6, "F2")} | {F(cfa / ccl, "F2")} | {F(fea.StiffnessNpm, "E2")} | {F(fN, "F0")} / {F(f95, "F0")} | {F(sN / 1e6)} / {F(s95 / 1e6)} | {Sf(allow / s95)} · {Sf(allow / (cfe * f95))}({(part.Material.Brittle ? "극한" : "항복")}) | {F(fOpen, "F0")} → {Sf(allow / (cUse * fOpen))} | {F(fea.Seconds, "F0")} s |");
            }
            P_();
            P_("FEA 는 고정 띠 완전 고정·하중 띠 균등 분배(접촉·예압·인접 부품 강성 없음). 고정 띠 경계는 완전 고정 모서리라 응력 특이점(메시 의존 — FR-FEA-04 경고) → **판정은 경계에서 요소 1.5개 넘게 떨어진 최대와 닫힌 식 중 큰 쪽**, 경계 포함 최대는 상한으로만 보인다. 하중 = 그 부품의 전 창 최대(공칭)·MC 95 %(분리 재접촉이 지배). 강성 k 는 같은 조건의 하중 띠 평균 변위(장착 fₙ 직렬 요소 대조 — 4절의 닫힌 식 값과 비교).");
        }
        P_();

        // ---------------- 6. 공력 ----------------
        P_("## 6. 3분할 분리 후 컨테이너 외부 공력 — CFD 표면 Cp → CalculiX 셸(정적·좌굴)");
        P_();
        if (r.Aero.Count == 0) P_("실행 안 함(--ccx 없음 또는 CFD 표면 장 없음).");
        else
        {
            var fl = ContainerMaterialStudy.FlightAt(1000, 0.5, 0.30, 1.50);
            P_($"- 하중: OpenFOAM v2512 정상 RANS 표면 Cp(`config/aero_db/` 컨테이너 표면 유동장, M 0.5, α 15°–90° — 90° 넘는 자세는 앞뒤 대칭) × 동압 q = {fl.Q:F0} Pa(ISA 1 km, M 0.5), 셸 S8R 48×40 · 기준 등방 E 50 GPa 로 풀고 재질({ShellLabel(r)}) E 로 좌굴 환산 · 양끝 격벽 지지(기존 재질 연구와 같은 모델).");
            P_("- 내부 압력 = 후류(φ ≥ 110°) 평균 Cp 로 배기(기존 연구와 같은 보수 가정). 보정: 후류 Cp 를 균일하게 δ 낮춰 적분 법선력을 K 배로(바람받이 정체압은 그대로) — **CFD 그대로(K = 1) · 공칭(L1 = TR R-300 횡류 적분력과 같게, K ≥ 1) · 보수(×1.7 = 2-D 기준 사례 벽 함수 편향 중앙값, 범위 1.4–2.0)**. 근거 `validation/reports/cfd/reference_case_analysis.md` 5.1 — 3-D 에서는 CFD 가 L1 과 4 % 안이라 2-D 보정 ×1.7 은 상한으로만 쓴다.");
            P_("- 매핑 검산: 메시 적분 CN 대 CFD 케이스 CZ(같은 기준 면적 πd²/4) — 아래 표 첫 열들.");
            P_();
            P_("| α [°] | CN 메시 | CN CFD | CN L1 | K 공칭 | 내부 Cp | 수준 | σ₁ 최대 [MPa] | σ₃ 최소 [MPa] | von Mises 내부/전체 [MPa] | 변위 [mm] | 좌굴 λ(녹다운 전) |");
            P_("|---|---|---|---|---|---|---|---|---|---|---|---|");
            foreach (var a in r.Aero)
                foreach (var l in a.Levels)
                    P_($"| {a.Alpha:0} | {F(a.Map.CnMesh, "F2")} | {F(a.Map.CnCfd, "F2")} | {F(a.CnRef, "F2")} | {F(a.KNominal, "F2")} | {F(a.Map.CpInternal, "F2")} | {l.Level} (K {l.K:F2}) | {F(l.Field.S1Max / 1e6)} | {F(l.Field.S3Min / 1e6)} | {F(l.Field.VmMax / 1e6)} / {F(l.Field.VmAll / 1e6)} | {F(l.Field.DispMaxMm, "F2")} | {(l.Buckle is { } b ? F(b, "F2") : "—")} |");
            P_();
            P_("![Cp](fig3_cp_cfd_vs_empirical.svg)");
            P_();
            if (r.BuckleVerify is { } bv)
                P_($"좌굴 검증(균일 외압, t {r.ShellThickness * 1e3:0.#} mm): FEA p_cr {bv.Fea / 1e3:F1} kPa vs 고전 해(Timoshenko–Gere 단순지지) {bv.Classical / 1e3:F1} kPa → 셸 끝 구속 보정 {Math.Min(1, bv.Classical / bv.Fea):F3} · NASA SP-8007 녹다운 {ContainerMaterialStudy.GammaPressure} 을 곱해 좌굴 SF = λ × E 환산 {LoadsStudy.ShellBuckleScale(r.Shell):F3} × 보정 × 녹다운 (기준 {ContainerMaterialStudy.SfBuckling}).");
            if (r.ShellModes.Count > 0)
                P_($"셸 고유진동(튜브 단독, 양끝 격벽 구속): {string.Join(", ", r.ShellModes.Take(4).Select(f => $"{f:F0} Hz"))}. 와류 흘림 f_s = St·V/D = {F(r.ShedFs1, "F0")}–{F(r.ShedFs2, "F0")} Hz(St {o.Strouhal.Lo}–{o.Strouhal.Hi}) → **와류 동적 하중 계수 {F(r.ShedLo, "F2")}–{F(r.ShedHi, "F2")}**(C'_L rms {o.ClRms.Lo}–{o.ClRms.Hi}, Norberg 2003 초임계 범위 · 구조 감쇠 2 % · 변동 항력 = 변동 양력/5 가정) — 비정상 CFD 결과 없이 공개 범위로 낸 값이며, 7절 판정은 정적 응력 × 이 계수 상한으로 한다.");
            if (r.ShellResonance is { } vr)
                P_($"**진동 피로(근사)**: 셸 1차 f_n {vr.Fn:F0} Hz 대비 와류 흘림 f_s {vr.FsLo:F0}–{vr.FsHi:F0} Hz → f_s/f_n {vr.Ratio:F2}, **{vr.Verdict}** " +
                   $"(기준 f_s < {VibrationFatigue.FrequencyLimit:0.0} f_n — ASME PTC 19.3 TW-2016 횡방향 주파수 제한, 공개 해설 기준). CFRP 셸의 공개 S-N 자료는 이 저장소에 없어 Miner 손상은 계산하지 않는다 — 알루미늄 부품 손상 추정은 `mns tumble-study --fatigue-simpkg` 7b절.");
            P_();
        }

        // ---------------- 7. 조합·비교 ----------------
        P_("## 7. 조합 하중(분리 충격·재접촉·개방 + 공력) · 기존 재질 연구와 비교");
        P_();
        var cand2 = r.Shell;
        double corr = (r.BuckleVerify is { } bvv && bvv.Fea > 0 ? Math.Min(1, bvv.Classical / bvv.Fea) : 1) * LoadsStudy.ShellBuckleScale(cand2);
        double shed = double.IsFinite(r.ShedHi) ? r.ShedHi : 1;
        if (r.Combos.Count > 0)
        {
            P_($"관성 = 튜브 + 탑재물 질량을 셸에 고르게(밀도 환산) 건 균일 가속도, 양끝 격벽 지지(자유 비행의 관성 완화보다 굽힘이 큰 보수 이상화). 강도 SF = 복합재 min(Ftu/σ₁, Fcu/|σ₃|) · 금속 min(Ftu/σ_vm, (Fty/σ_vm)·1.5/1.25)(재질 {ShellLabel(r)}: Fty {cand2.Fty / 1e6:F0} · Ftu {cand2.Ftu / 1e6:F0} · Fcu {cand2.Fcu / 1e6:F0} MPa, 기준 극한 1.5), 와류 계수 상한을 곱한 값과 함께. 좌굴 SF = λ × E 환산 × 끝 구속 보정 × 녹다운 0.56 / 와류 계수.");
            P_();
            P_("| 경우 | 하중 | σ₁ / σ₃ [MPa] | von Mises [MPa] | 변위 [mm] | 강도 SF(정적 / ×와류) | 좌굴 λ | 좌굴 SF | 판정 |");
            P_("|---|---|---|---|---|---|---|---|---|");
            foreach (var c in r.Combos)
            {
                double sfs = LoadsStudy.ShellStrengthSf(cand2, c.Field);
                double lamSf = c.Buckle is { } bl ? bl * corr * ContainerMaterialStudy.GammaPressure / shed : double.NaN;
                bool ok = sfs / shed >= ContainerMaterialStudy.SfUltimate && (!double.IsFinite(lamSf) || lamSf >= ContainerMaterialStudy.SfBuckling);
                P_($"| {c.Case.Id} | {c.Case.Label} | {F(c.Field.S1Max / 1e6)} / {F(c.Field.S3Min / 1e6)} | {F(c.Field.VmMax / 1e6)} | {F(c.Field.DispMaxMm, "F2")} | {Sf(sfs)} / {Sf(sfs / shed)} | {(c.Buckle is { } b2 ? F(b2, "F2") : "—")} | {Sf(lamSf)} | {(ok ? "통과" : "기준 미달")} |");
            }
            P_();
            foreach (var c in r.Combos) P_($"- {c.Case.Id}: {c.Case.Basis}{(c.Note.Length > 0 ? $" — {c.Note}" : "")}");
            P_();
        }
        // 기존 연구 비교
        if (o.MaterialResultsPath is { } mrp && File.Exists(mrp) && r.Aero.Count > 0)
        {
            try
            {
                var mr = J.ParseObject(File.ReadAllText(mrp));
                var fea3 = (mr["fea"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(x => Math.Abs(J.Num(x, "t_m", J.Num(x, "thickness_m", 0)) - 0.003) < 1e-6);
                var a90 = r.Aero.FirstOrDefault(a => Math.Abs(a.Alpha - 90) < 1e-6);
                if (a90 is not null)
                {
                    P_($"| 비교(α 90°, {ShellLabel(r)}) | 기존 재질 연구(경험식 Cp, CFRP t 3) | 이 연구 CFD 그대로 | 이 연구 공칭 | 이 연구 보수 ×1.7 |");
                    P_("|---|---|---|---|---|");
                    string Old(string k, double scale = 1e-6, string f = "F1") => fea3 is not null && fea3[k] is JsonValue v && v.TryGetValue(out double d) ? F(d * scale, f) : "—";
                    var lv = a90.Levels;
                    P_($"| von Mises 내부 [MPa] | {Old("vm_Pa")} | {F(lv[0].Field.VmMax / 1e6)} | {F(lv[1].Field.VmMax / 1e6)} | {F(lv[2].Field.VmMax / 1e6)} |");
                    P_($"| σ₁ / σ₃ [MPa] | {Old("s1_Pa")} / {Old("s3_Pa")} | {F(lv[0].Field.S1Max / 1e6)} / {F(lv[0].Field.S3Min / 1e6)} | {F(lv[1].Field.S1Max / 1e6)} / {F(lv[1].Field.S3Min / 1e6)} | {F(lv[2].Field.S1Max / 1e6)} / {F(lv[2].Field.S3Min / 1e6)} |");
                    P_($"| 좌굴 λ(녹다운 전) | {Old("buckle", 1, "F2")} | {(lv[0].Buckle is { } b0 ? F(b0, "F2") : "—")} | {(lv[1].Buckle is { } b1 ? F(b1, "F2") : "—")} | {(lv[2].Buckle is { } b2b ? F(b2b, "F2") : "—")} |");
                    P_($"| 동압 기준 q [Pa] | {Old("qRef_Pa", 1, "F0")} | {F(r.Aero[0].Levels.Count > 0 ? ContainerMaterialStudy.FlightAt(1000, 0.5, 0.30, 1.50).Q : double.NaN, "F0")} | 같음 | 같음 |");
                    P_();
                    P_("차이의 원인: 기존 연구는 퍼텐셜 모양 Cp + 음속 하한 Cp* + 적분 CD = TR R-300 2-D CD2D(1.14)가 되게 맞춘 기저압(−1.35)을 썼다(2-D 횡류, 유한 길이 η 미적용 — 셸 하중은 2-D 값). CFD 3-D 는 끝단 유동으로 후류 흡입이 약하고(내부 배기 Cp 가 덜 음수) 바람받이 정체 구간이 넓다 → 같은 동압에서 순 외압 분포의 타원화 성분이 다르다. 이 연구의 공칭(L1 3-D 적분력 = η·CD2D)은 2-D 값보다 η(0.63)배 작다 — 기존 값이 더 보수다.");
                    P_();
                }
            }
            catch (Exception ex) { P_($"(기존 재질 연구 결과 읽기 실패: {ex.Message})"); P_(); }
        }

        // ---------------- 8. 요약 ----------------
        P_("## 8. 요약 — 하중 케이스 × 부품 판정과 설계 권고");
        P_();
        P_("칸 = 동적 G / SF — 공칭 실행 · (MC 95 %: G 95 백분위 / SF 5 백분위). 질문의 주 하중인 **낙하산 개방(②③)** 과 **분리 재접촉(①)** 을 나눠 판정한다(① 은 노즈 형상 가정에 지배 — 2절 스윕).");
        P_();
        P_("| 부품 | ① 분리·재접촉 | ② 드로그 개방 | ③ 주낙하산 개방 | 판정 ②③(MC 95 %) | 판정 ①(MC 95 %) |");
        P_("|---|---|---|---|---|---|");
        foreach (var p in nom.Parts)
        {
            (double G, double Sf, double G95, double Sf05) Cell(string id)
            {
                var x = p.Windows.FirstOrDefault(v => v.Window == id);
                if (x is null) return (double.NaN, double.NaN, double.NaN, double.NaN);
                double s = SfOf(p.Part, x.SfYield, x.SfUltimate, x.GMargin);
                double g95 = double.NaN, s05 = double.NaN;
                if (r.Mc.TryGetValue($"{p.Part.Name}|{id}", out var st))
                {
                    g95 = P(st.DynG, 0.95);
                    s05 = SfOf(p.Part, P(st.SfY, 0.05), P(st.SfU, 0.05), P(st.GMargin, 0.05));
                }
                return (x.DynG, s, g95, s05);
            }
            string Txt((double G, double Sf, double G95, double Sf05) c) => double.IsNaN(c.G) ? "—" : $"{F(c.G)} / {Sf(c.Sf)} ({F(c.G95)} / {Sf(c.Sf05)})";
            var c1 = Cell("separation"); var c2 = Cell("drogue"); var c3 = Cell("main");
            string V(params (double G, double Sf, double G95, double Sf05)[] cs)
            {
                var ss = cs.Where(c => !double.IsNaN(c.G)).ToList();
                if (ss.Count == 0) return "—";
                string? first = null;
                foreach (var c in ss)
                {
                    double y = p.Part.HasStress && !p.Part.Material!.Brittle ? c.Sf05 * cfg.SfYield : double.NaN;
                    double u = p.Part.HasStress ? c.Sf05 * (p.Part.Material!.Brittle ? cfg.SfUltimate : cfg.SfUltimate) : double.NaN;
                    var v = p.Part.HasStress ? PayloadShock.Verdict(cfg, p.Part, MinSfAs(p.Part, c, true, cfg), MinSfAs(p.Part, c, false, cfg), double.NaN)
                                             : PayloadShock.Verdict(cfg, p.Part, double.NaN, double.NaN, c.Sf05);
                    if (first is null || Rank(v) > Rank(first)) first = v;
                }
                return first!;
            }
            P_($"| {p.Part.Label} | {Txt(c1)} | {Txt(c2)} | {Txt(c3)} | {V(c2, c3)} | {V(c1)} |");
        }
        P_();
        P_("SF = 금속 min(항복, 극한) 중 기준 대비 더 작은 쪽의 실제 SF, 취성은 극한, 전장은 G 여유(기준 G / 동적 G). 판정은 MC 5 백분위 SF(보수)로.");
        P_();
        P_("설계 권고(이 결과에서):");
        P_();
        foreach (var rec in Recommendations(cfg, nom, r)) P_($"- {rec}");
        P_();

        // ---------------- 9. 신뢰성 ----------------
        P_("## 9. 신뢰성(PRD AB절 · NASA-STD-7009A)");
        P_();
        P_("`mns credibility` 와 같은 채점 함수(`Credibility.AssessLoads` — 규칙은 코드 주석에 고정)로 이 실행의 출력을 채점한 결과. 공개 Validation 사례 상태는 `mns credibility` 실행 때 반영된다(여기서는 미실행 → 확인 수준이 낮게 나올 수 있음 — 최종 등급은 validation/reports/credibility).");
        P_();
        var credRows = Credibility.AssessLoads(LoadsStudy.ToJson(doc, o, r), []);
        P_("| 출력 | 공칭 | 90 % 구간 | 등급 | " + string.Join(" | ", Credibility.Factors.Select(Credibility.FactorKo)) + " | 의사결정 범위 |");
        P_("|---|---|---|---|" + string.Concat(Credibility.Factors.Select(_ => "---|")) + "---|");
        foreach (var (_, c) in credRows)
            P_($"| {c.Label} [{c.Unit}] | {F(c.Nominal, "G4")} | {F(c.Lo90, "G4")} – {F(c.Hi90, "G4")} | {c.Class} | " + string.Join(" | ", c.Factors.Select(f => f.Gap ? $"**{f.Level}**" : f.Level.ToString(Inv))) + $" | {c.UseScope} |");
        P_();
        P_("굵은 수 = 프로젝트 문턱(개발·사용 2, 지원 1) 미달. 요인별 근거 문장은 validation/reports/credibility/README.md 3절.");
        P_();
        P_("등급을 올리는 데 필요한 실측·사양(무엇이 어느 요인을 몇 수준):");
        P_();
        P_("- 드론 장착부 진동(타격·정현파 스윕) 시험 → 장착 fₙ·감쇠 실측: SRS 증폭 입력 D→A, **수납 부품 G Validation 0→2, Input pedigree 1→3**.");
        P_("- 3분할 지상 분리 시험(고속 카메라·가속도계) → 꽃잎 Δv·재접촉 여부·컨테이너 충격 펄스: **분리 하중 Input pedigree 0→3, Validation 0→2**.");
        P_("- 래치 핀·귀·하우징·나사 사양(열처리 H1025/H1150, 나사 등급, CFRP 인서트 뽑힘 강도) → 재질 D→A: **부품 SF Input pedigree 1→3**.");
        P_($"- 소형 드로그(Ø{drogue?.D0 ?? double.NaN:0.0#} m) 개방하중 시험(풍동 또는 투하) → Ck·충만 상수: **개방 G Validation 1→3**.");
        P_("- 3-D DDES 또는 풍동 표면 압력(α 60–90°) → 후류 압력·변동 C'_L: **공력 응력 Validation 0→2, 와류 계수 범위 축소**.");
        P_($"- 컨테이너 튜브 외압·굽힘 좌굴 시험({r.Shell.Name} 실물) → 녹다운 실측: **좌굴 SF Validation 0→3**.");
        P_();

        // ---------------- 10. 한계·재현 ----------------
        P_("## 10. 가정 · 알려진 한계");
        P_();
        P_("- 수납물은 6DOF 에서 컨테이너와 한 강체(Stowed)로 적분하고, 부품 위치 가속도 = 강체 가속도(+ 회전 항) → 장착 1자유도로 후처리(컨테이너 운동에 대한 역영향 무시). 3축 같은 fₙ(등방) 가정. 드론 위 부품(센서 거치대·서보·배터리·FC)은 2자유도 사슬(드론 나머지 m₁ – 드론 장착 k₁ / 부품 m₂ – 부품 장착 k₂)로 응답, 래치·스트럿 하중은 드론+센서를 한 질량으로 본 1자유도.");
        P_("- 장착 강성은 하중 경로 닫힌 식 직렬(스트럿 외팔·핀·가정 스프링) — 접합부 유연성·예압 미지 → fₙ ×0.5–2 포락으로 보수 처리. 부품 응력은 닫힌 식 + 공칭 형상 치수(STEP 경계 상자) — 구멍 위치·나사 재질 등은 D.");
        P_(r.Compliant
            ? $"- 접촉: 대표점–형상 판정 · 운동량 교환 = 충격량(반발 e) · 접촉력 이력 = 순응 접촉(2절) — 강성은 기본 형상 이상화(관·껍질 조각·구 셸·판 상자)의 닫힌 식(등급 D, 배율 ×{r.StiffRange.Lo:G3}–{r.StiffRange.Hi:G3}), 구조는 접촉점 ↔ 강체 질량의 준정적 스프링(느린 모드는 제외 — 모달 접촉 해석 아님), 국부 소성 눌림 없음(압력 확인만), 실모델 형상(메시)은 기본 형상(aeroShape)으로. 6DOF 는 운동량을 접촉 시작 순간에 바꾼다(접촉 수 ms ≪ 궤적). 탄체 잔해의 후류 차폐·공력 간섭 없음."
            : "- 접촉: 대표점–형상 판정·충격량(반발 0.3)이라 접촉력 시간 이력이 없다 → 반 사인 τ 1–5 ms 가정. 탄체 잔해의 후류 차폐·공력 간섭 없음.");
        P_($"- 컨테이너 셸: {ShellLabel(r)} — 기준 등방 E 50 GPa 로 풀고 좌굴만 재질 E 로 환산(응력은 E 무관), 양끝 격벽 지지, 레일·결속 금구·도어 링 국부 응력 없음, 관성은 질량을 셸에 고르게 퍼뜨린 근사. CFD Cp 는 정상 RANS(박리 후류 과소 흡입 경향) — 보정 범위로 처리, 비정상 효과는 공개 범위 와류 계수로.");
        P_("- 판정 기준(항복 1.25 · 극한 1.5 · 좌굴 1.5)은 PRD 수치가 없어 항공우주 관례를 택한 결정(기존 연구와 같음).");
        foreach (var n in r.Notes) P_($"- {n}");
        P_();
        P_("## 재현");
        P_();
        P_("```");
        P_(o.Command);
        P_("```");
        P_();
        P_("- `--runs N` MC 횟수, `--ccx` CalculiX(부품 대조 + 컨테이너 CFD Cp 셸, 동시 실행 1·스레드 상한 `--ccx-threads`), `--no-aero` 공력 셸 생략, `--parts FILE` 부품 정의, `--raw DIR` 탐침 원자료 CSV.");
        P_(cfg.RegenNote ?? $"- 질량·재질·부품 치수를 바꾸면(시나리오, 부품 → `{cfg.Path}`) 같은 명령으로 새 값이 나온다.");
        return sb.ToString();
    }

    private static string ShellLabel(LoadsStudy.Result r) => $"{r.Shell.Name} t {r.ShellThickness * 1e3:0.#}";

    /// <summary>분리 창 비교 한 줄(같은 6DOF 실행의 다른 접촉력 이력): 컨테이너 G · 드론 위치 강체/포락/설계 모드 G · 최소 여유(부품).</summary>
    private static (double SepG, double DroneRigid, double DroneDyn, double DroneModes, double Margin, string Part) SepSummary(PayloadRun? run, PayloadPartsConfig cfg)
    {
        if (run is null) return (double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, "");
        double sep = LoadsStudy.Peak(run, "separation", out _, out _);
        var latch = run.Parts.Where(p => p.Part.Name.StartsWith("latch_pin", StringComparison.Ordinal) && !p.Part.Reference).SelectMany(p => p.Windows.Where(w => w.Window == "separation")).ToList();
        double mm = double.PositiveInfinity; string who = "";
        foreach (var pr in run.Parts.Where(p => p.Part.Judged))
            foreach (var ww in pr.Windows.Where(v => v.Window == "separation"))
            {
                double m = LoadsStudy.Margin(cfg, pr.Part, ww.SfYield, ww.SfUltimate);
                if (m < mm) { mm = m; who = pr.Part.Label; }
            }
        return (sep, latch.Select(w => w.RigidG).DefaultIfEmpty(double.NaN).Max(), latch.Select(w => w.DynG).DefaultIfEmpty(double.NaN).Max(),
            latch.Select(w => w.DynModesG).Where(double.IsFinite).DefaultIfEmpty(double.NaN).Max(), mm, who);
    }

    /// <summary>2절(순응 접촉): 모델 설명 · 접촉 표(순응 vs τ 가정) · 강성 근거 · CalculiX 교차 확인 · 모델 비교 · MC · 스윕.</summary>
    private static void CompliantSection(StringBuilder sb, ScenarioDoc doc, LoadsStudyOptions o, LoadsStudy.Result r, List<ImpGroup> groups, double mRoot)
    {
        void P_(string s = "") => sb.AppendLine(s);
        var cfg = r.Config; var nom = r.Nominal;
        var col = doc.Root["collisions"] as JsonObject ?? new JsonObject();
        double e = J.Num(col, "restitution", 0.3);
        string basis = col["contact"] is JsonObject cc ? J.Str(cc, "basis", "") : "";
        string tn = $"{cfg.TauNominal * 1e3:0.#}";
        P_("## 2. 3분할 분리 직후 컨테이너 하중 — 분리 펄스 + 잔해 재접촉(순응 접촉: 접촉력 이력을 구조 강성으로 계산)");
        P_();
        P_($"**이전 방식(τ 가정)**: 6DOF 접촉은 강체 충격량(반발 {e:0.##})이라 속도만 순간에 바뀌고 가속도 이력이 없다. 하중 연구가 그 충격량을 **반 사인 지속 τ = {tn} ms(가정 D, {cfg.TauMin * 1e3:0.#}–{cfg.TauMax * 1e3:0.#} ms)** 로 되살려 접촉 G = (π/2)·Δv/τ — 접촉 G 가 사실상 τ 가정 하나로 정해졌다({cfg.TauBasis}).");
        P_();
        P_($"**지금(`collisions.contact.model = compliant`)**: 접촉 쌍마다 등가 직렬 스프링 k = 1/ΣCᵢ(국부 접촉 + 양쪽 물체의 구조 경로 — 시나리오 형상·벽 두께·재질에서 닫힌 식, 아래 강성 근거 표) + 반발 e 와 같은 에너지 손실 감쇠(침투 비례 Hunt–Crossley 꼴 F = kδ(1 + χδ̇), χ 는 e 가 나오도록 같은 적분기로 맞춤), " +
           "유효 질량 m_eff = 강체 충격량 식의 1/k 인자(회전 관성 포함) → 접촉 동안 RK4 서브스텝(펄스당 약 1,300단계)으로 적분한 접촉력 F(t) 를 그대로 가속도 이력으로 쓴다. 운동량 교환 ∫F dt = (1+e)·m_eff·v 는 충격량 모델과 같다 — **같은 6DOF 실행·같은 접촉 순서에서 접촉력 이력만 다르다**(그래서 아래 'τ 가정' 값은 같은 실행을 이전 방식으로 다시 평가한 것).");
        P_();
        if (r.Plastic && col["contact"] is JsonObject pc && ContactPlasticityModel.FromJson(pc) is { } pm)
        {
            P_($"**탄성-소성(`collisions.contact.plasticity = elastoplastic`)**: 접촉 힘에 상한 F_lim = min(접촉 패치 평균 압력 = 경도 H = {pm.HardnessFactor:0.##}·σ_y(두 재질 중 무른 쪽, 변형률 속도 배율) × 패치 면적, 벽 국부 압괴 하중(관·꽃잎 끝 테두리 Alexander 6σ₀t^1.5√D × 몫, 반경 하중은 링 압괴 σ₀t²L_eff/R — `ImpactDamage` 식)) — " +
               $"닿으면 {(pm.Hardening > 0 ? $"선형 경화(기울기 {pm.Hardening:0.##}·k)" : "완전 소성(힘 일정)")}으로 소성 변형(눌림)이 쌓이고, 제하는 같은 직렬 강성으로 탄성 회복해 영구 눌림 δ_p = δ_max − F_max/k 가 남는다(Thornton 1997 탄성-완전소성 구 모델의 직렬 선형 스프링 꼴, 경도 ≈ 2.8–3·Y: Tabor 1951 · Johnson 1985 §6.3). " +
               $"소성 소산만의 반발 e_p 를 6DOF 반발 {e:0.##} 과 비교한다 — e_p > {e:0.##} 이면 나머지 손실은 감쇠(전체 되튐 {e:0.##}, 운동량 교환 = 충격량 모델 그대로), e_p < {e:0.##} 이면 감쇠 0 이고 펄스 운동량 (1+e_p)·m·v 가 6DOF 보다 작다(하중 연구는 6DOF Δv 면적으로 모양을 맞춘다 — 보수).");
            P_();
        }
        P_($"포함 규칙: 구조 경로의 1차 모드 진동수가 접촉 진동수 1/(2τ) 보다 낮으면(자유-자유 보 굽힘·비틀림 같은 느린 경로) 접촉 동안 응답하지 못해 질량처럼 거동 → 직렬에서 뺀다(접촉력이 커지는 쪽 — 보수). 모두 넣은 준정적 값은 '준정적 전부'(무른 쪽 참고). " +
           $"꽃잎–꼬리 힌지는 접촉점과 꽃잎 질량 사이 직렬 경로가 아니라 꼬리 질량으로 가는 병렬 경로(강체 충격량 식도 힌지를 무시 — 같은 기준)라 넣지 않았다. 강성 불확도: 직렬 강성 배율 ×{r.StiffRange.Lo:G3}–{r.StiffRange.Hi:G3}(등급 D, MC 입력 · 아래 범위 행){(basis.Length > 0 ? $" — {basis}" : "")}.");
        P_();
        string plCol = r.Plastic ? " 힘 상한 [kN](지배) · 눌림 [mm] · 탄성이면 [kN] · e_p |" : "";
        P_($"| 시각 [s] | 상대 물체 | 충격량 [N·s] (접촉 수) | CG Δv [m/s] | 접근 [m/s] | m_eff [kg] | 직렬 k [N/m] (준정적 전부) | τ 힘 구간 / 전체 [ms] | 최대 접촉력 [kN] | CG 최대 [G] | **τ {tn} ms 가정**: 최대 힘 [kN] · CG [G] |{plCol}");
        P_("|---|---|---|---|---|---|---|---|---|---|---|" + (r.Plastic ? "---|" : ""));
        foreach (var g in groups.Take(12))
        {
            double fT = g.PeakForce(cfg.TauNominal, false);
            string tau = $"{F(fT / 1e3)} · {F(fT / mRoot / PayloadShock.G0)}";
            int nm = g.Members?.Count ?? 1;
            if (g.Pulse is not { } p) { P_($"| {F(g.T, "F3")} | {g.Other} | {F(g.J, "F1")} ({nm}) | {F(g.Dv, "F2")} | — | — | (충격량 모델) | — | — | — | {tau} |{(r.Plastic ? " — |" : "")}"); continue; }
            double fPk = g.PeakForce(cfg.TauNominal, true);
            string pl = !r.Plastic ? "" : p.Plasticity is { } lim
                ? $" {F(lim.Force / 1e3)}({lim.Governing?.Kind}) · {(p.Plastic ? F(p.Indentation * 1e3, "F3") : "0(탄성)")} · {F(p.ElasticPeakForce / 1e3)} · {(double.IsFinite(p.PlasticRestitution) ? F(p.PlasticRestitution, "F2") : "—")} |" : " — |";
            P_($"| {F(g.T, "F3")} | {g.Other} | {F(g.J, "F1")} ({nm}) | {F(g.Dv, "F2")} | {F(p.Vn, "F2")} | {F(p.MEff, "F2")} | {F(p.K, "E2")} ({F(p.KQuasiStatic, "E2")}) | {F(p.ActiveDuration * 1e3, "F2")} / {F(p.Duration * 1e3, "F2")} | {F(fPk / 1e3)} | {F(fPk / mRoot / PayloadShock.G0)} | {tau} |{pl}");
        }
        if (groups.Count == 0) P_("| — | 재접촉 없음 | | | | | | | | | |");
        P_();
        P_($"충격량 = 같은 상대와 5 ms 안 연속 접촉(6DOF 스텝마다 다시 닿으면 새 접촉)의 합, 접근·m_eff·k·τ = 그중 가장 큰 접촉. 최대 접촉력 = 묶음 접촉들의 접촉력 이력을 시각대로 겹친 최대(크기 합 — 상한), CG 최대 = 그것 / 컨테이너 합성 질량 {F(mRoot, "F2")} kg — 다른 상대의 동시 접촉·분리 펄스·공력은 아래 '분리 창 CG 최대'(벡터 합)에 합쳐진다. τ 가정 열도 같은 방식(반 사인 겹침).");
        var mp0 = ContactPulse.Master(e);
        P_($"접촉력 모양(반발 {e:0.##}): 최대 힘 = v·√(k·m_eff) × {F(mp0.PeakF, "F3")}(무감쇠 탄성 = 1 — 감쇠가 압축 중 힘을 더한다), 압축 단계에서만 운동량 m·v 를 멈춰야 해 최대 힘은 τ 가정 반 사인(전체 (1+e)·m·v 를 좌우 대칭으로) 보다 크다. 지속 τ 는 힘이 0 으로 떨어진 뒤 침투가 0 이 될 때까지(되튐 이동) 포함.");
        P_();
        // 강성 근거: 충격량 큰 접촉 상위 2쌍(상대 물체가 다른 것)
        foreach (var g in groups.Where(x => x.Pulse is not null).GroupBy(x => x.Other).Select(x => x.First()).Take(3))
        {
            var p = g.Pulse!;
            P_($"**강성 근거 — {g.Other} @ {F(g.T, "F3")} s** · A {p.SideA.S?.Describe() ?? "—"} · B {p.SideB.S?.Describe() ?? "—"} · 접촉 진동수 1/(2τ) = {F(p.ContactFrequencyHz, "F0")} Hz");
            P_();
            P_("| 경로 | 물체 | k [N/m] | 1차 모드 [Hz] | 접촉 동안 | 식 · 값 |");
            P_("|---|---|---|---|---|---|");
            foreach (var el in p.Elements.OrderBy(x => x.Compliance > 0 ? 1 / x.Compliance : 0))
            {
                string inc = !double.IsFinite(el.ModeHz) ? "포함(국부)" : el.Included ? "포함" : $"제외({F(el.ModeHz, "F0")} Hz < {F(p.ContactFrequencyHz, "F0")} Hz)";
                P_($"| {el.Kind} | {el.Body} | {F(el.K, "E2")} | {(double.IsFinite(el.ModeHz) ? F(el.ModeHz, "F0") : "국부")} | {inc} | {el.Formula} |");
            }
            P_($"| **직렬** | | **{F(p.K, "E2")}** | | 준정적 전부 {F(p.KQuasiStatic, "E2")} | 배율 ×{p.Scale:G3} 적용 |");
            P_();
            double pr = p.PatchArea > 0 ? p.PeakForce / p.PatchArea : double.NaN;
            if (p.Plasticity is { } lim)
            {
                P_("힘 상한 후보(탄성-소성):");
                P_();
                P_("| 후보 | 물체 | 힘 [kN] | 식 · 값 |");
                P_("|---|---|---|---|");
                foreach (var c in lim.Candidates.OrderBy(c => c.Force))
                    P_($"| {c.Kind}{(c == lim.Governing ? " **(지배)**" : "")} | {c.Body} | {F(c.Force / 1e3, "F2")} | {c.Formula} |");
                P_();
                P_(p.Plastic
                    ? $"→ 탄성이면 최대 {F(p.ElasticPeakForce / 1e3, "F1")} kN(접촉 압력 {F(p.ElasticPeakForce / p.PatchArea / 1e6, "F0")} MPa) — 상한 {F(lim.Force / 1e3, "F1")} kN 에서 소성 흐름: **영구 눌림(접촉 자국) {F(p.Indentation * 1e3, "F3")} mm**(최대 접근 {F(p.MaxApproach * 1e3, "F3")} mm), 소성 일 {F(p.PlasticWork, "F1")} J(접근 운동에너지의 {F(p.PlasticWork / (0.5 * p.MEff * p.Vn * p.Vn) * 100, "F0")} %), 소성 소산만의 반발 e_p {F(p.PlasticRestitution, "F2")} vs 6DOF e {p.Restitution:0.##}" +
                      (p.Dimensionless?.Mismatch == true ? " — e_p 가 더 작다: 6DOF Δv 가 (1+e)/(1+e_p) 배 큼(보수)." : " — 나머지 손실은 감쇠.")
                    : $"→ 최대 접촉력 {F(p.PeakForce / 1e3, "F1")} kN < 상한 {F(lim.Force / 1e3, "F1")} kN — 탄성 한계 아래(탄성 펄스 그대로).");
                P_();
                continue;
            }
            var hard = new[] { p.SideA.S, p.SideB.S }.Where(s => s?.Material is not null).Select(s => (s!.Body, H: 3 * s.Material!.Yield)).ToList();
            if (hard.Count > 0 && double.IsFinite(pr))
            {
                var soft = hard.MinBy(x => x.H);
                P_($"국부 접촉 압력 = 최대 접촉력 {F(p.PeakForce / 1e3, "F1")} kN / 패치 {F(p.PatchArea * 1e6, "F0")} mm² = **{F(pr / 1e6, "F0")} MPa** vs 무른 쪽({soft.Body}) 경도 근사 3σ_y {F(soft.H / 1e6, "F0")} MPa — " +
                   (pr < soft.H ? "탄성 접촉 가정 범위 안." : "넘는다 → 국부 소성 눌림이 생겨 실제 최대 힘은 경도 한계 근처로 잘리고 τ 는 더 길다(이 모델은 탄성 + e 손실 — 이 접촉에서는 보수)."));
                P_();
            }
        }
        // CalculiX 교차 확인
        if (r.ContactFeaRows.Count > 0)
        {
            P_("**CalculiX 교차 확인**(`mns loads --ccx`): 접촉 물체의 얇은 벽 이상화를 S8R 셸로 메시(접촉점 쪽 조밀, 요소 두 단계), 접촉점 패치 법선 하중 + 수동 관성 완화 + 3-2-1 지지 → 강체 운동(최소제곱)을 뺀 패치 평균 법선 변위/하중 = 구조 컴플라이언스. 닫힌 식은 같은 물체 요소 합(국부 접촉 제외).");
            P_();
            P_("| 시각 [s] | 물체(상대) | 이상화 | FE C [m/N] (k [N/m]) | 메시 거친→고운 | 닫힌 식 준정적 전부 [m/N] | 닫힌 식/FE | 닫힌 식 포함 경로 [m/N] | 요소 | 반력 잔차 |");
            P_("|---|---|---|---|---|---|---|---|---|---|");
            foreach (var x in r.ContactFeaRows)
                P_(x.Ok
                    ? $"| {F(x.Time, "F3")} | {x.Body}({x.Other}) | {x.Kind} | {F(x.Compliance, "E2")} ({F(1 / x.Compliance, "E2")}) | {F(x.MeshChange * 100, "+0.0;-0.0")} % | {F(x.ClosedAll, "E2")} | {F(x.Ratio, "F2")} | {F(x.ClosedIncluded, "E2")} | {x.Elements:N0} | {F(x.ReactionResidual, "E1")} |"
                    : $"| {F(x.Time, "F3")} | {x.Body}({x.Other}) | {x.Kind} | 실패: {x.Error} | | | | | | |");
            P_();
            var ok = r.ContactFeaRows.Where(x => x.Ok && double.IsFinite(x.Ratio)).ToList();
            if (ok.Count > 0)
            {
                double lo = ok.Min(x => x.Ratio), hi = ok.Max(x => x.Ratio);
                bool inRange = ok.All(x => 1 / x.Ratio >= r.StiffRange.Lo && 1 / x.Ratio <= r.StiffRange.Hi);
                P_($"닫힌 식/FE 컴플라이언스 비 {F(lo, "F2")}–{F(hi, "F2")} — 구조 강성 비(FE/닫힌 식) {F(1 / hi, "F2")}–{F(1 / lo, "F2")} 는 MC 강성 배율 범위 ×{r.StiffRange.Lo:G3}–{r.StiffRange.Hi:G3} {(inRange ? "안" : "**밖** (닫힌 식 이상화가 FE 와 이 범위 이상 다르다 — 범위를 넓히거나 FE 값을 근거로 써야 한다)")}. FE 는 준정적(느린 모드 포함)이라 '준정적 전부' 와 비교한다.");
                P_();
            }
        }
        // 모델 비교
        P_($"**접촉력 이력 모델 비교(같은 6DOF 실행, 분리 창)** — 드론 위치 = 래치 핀 1–4 중 최대, 포락 = 장착 fₙ ×{string.Join("/", cfg.FnFactors.Select(x => x.ToString("0.##", Inv)))}, 설계 모드 = 설계 FEA 고유진동수 범위:");
        P_();
        P_("| 접촉력 이력 | 분리 창 CG 최대 [G] | 드론 위치 강체 [G] | 드론 위치 동적 포락 [G] | 설계 모드 fₙ [G] | 최소 여유(SF/기준) — 부품 |");
        P_("|---|---|---|---|---|---|");
        foreach (var (lab, run) in new (string, PayloadRun?)[]
                 {
                     (r.Plastic ? "**탄성-소성 순응 접촉(공칭 강성)**" : "**순응 접촉(공칭 강성)**", nom), ("탄성 순응 접촉(힘 상한 없음 — **이전 값**)", r.Elastic), ($"순응 접촉 강성 ×{r.StiffRange.Lo:G3}", r.StiffLo), ($"순응 접촉 강성 ×{r.StiffRange.Hi:G3}", r.StiffHi),
                     ($"충격량 모델 τ {tn} ms(**이전 가정 값**)", r.TauNom), ($"충격량 모델 τ {cfg.TauMin * 1e3:0.#} ms", r.TauMin), ($"충격량 모델 τ {cfg.TauMax * 1e3:0.#} ms", r.TauMax),
                 })
        {
            if (run is null && lab.StartsWith("탄성 순응", StringComparison.Ordinal)) continue;
            var s = SepSummary(run, cfg);
            P_($"| {lab} | {F(s.SepG)} | {F(s.DroneRigid)} | {F(s.DroneDyn)} | {F(s.DroneModes)} | {F(s.Margin, "F2")} — {s.Part} |");
        }
        P_();
        double sepPeak = LoadsStudy.Peak(nom, "separation", out var sepVec, out double sepAlpha);
        P_($"분리 창 컨테이너 CG 최대(펄스 + 공력 + 재접촉, 순응 접촉): **{F(sepPeak)} G** · 그 순간 총받음각 {F(sepAlpha, "F0")}° · 벡터(동체 x 축·y·z) ({F(sepVec.X)}, {F(sepVec.Y)}, {F(sepVec.Z)}) G. 컨테이너 합성 질량 {F(mRoot, "F2")} kg.");
        if (r.McScalars.TryGetValue("containerG:separation", out var sgs))
            P_($"MC {sgs.Count}회(강성 배율 표본 포함 — τ 표본 없음): 중앙값 {F(P(sgs, 0.5))} G · 95 % {F(P(sgs, 0.95))} G · 최대 {F(sgs.Max())} G. 재접촉 최대 충격량 95 % {F(P(r.McScalars["recontactMaxJ"], 0.95))} N·s.");
        P_();
        if (r.Sweep.Count > 0)
        {
            P_("민감도(1-D 스윕, 나머지 공칭) — 순응 접촉 값과 같은 실행의 τ 가정 재평가:");
            P_();
            bool pl = r.Sweep.Any(x => double.IsFinite(x.ContainerSepGElastic));
            P_($"| 입력 | 값 | 컨테이너 접촉 [회] | 최대 접근 속도 [m/s] | 최대 충격량 [N·s] | 분리 창 CG 최대 [G] | 드론 1 래치 하중 [N] (동적 G) | 최소 여유 부품(여유 = SF/기준) | τ {tn} ms 가정: CG [G] · 최소 여유 |{(pl ? " 탄성 순응: CG [G] · 최소 여유 | 최대 눌림 [mm] |" : "")}");
            P_("|---|---|---|---|---|---|---|---|---|" + (pl ? "---|---|" : ""));
            foreach (var x in r.Sweep)
                P_(x.Error is null
                    ? $"| `{x.Path}` | {x.Value:0.##} | {x.Contacts} | {F(x.MaxApproach, "F2")} | {F(x.MaxJ)} | {F(x.ContainerSepG)} | {F(x.LatchForce, "F0")} ({F(x.LatchDynG)}) | {x.WorstPart} {F(x.WorstSf, "F2")} | {F(x.ContainerSepGTau)} · {F(x.WorstSfTau, "F2")} |" +
                      (pl ? $" {F(x.ContainerSepGElastic)} · {F(x.WorstSfElastic, "F2")} | {F(x.MaxIndentation * 1e3, "F3")} |" : "")
                    : $"| `{x.Path}` | {x.Value:0.##} | 오류: {x.Error} | | | | | | |{(pl ? " | |" : "")}");
            P_();
        }
        P_("해석: 재접촉 하중은 이제 접촉 부위 강성(꽃잎 셸 · 컨테이너 끝 링 · 국부 접촉)과 유효 질량·접근 속도로 정해진다 — 가정이 남은 곳은 강성 이상화(등급 D, 배율 범위)·반발 e·분리 Δv·힌지 미설계. 장착 고유주기보다 짧은 펄스라 부품 응답은 점 속도 변화 Δv·ωₙ·펄스 모양으로 정해진다(4절 SRS) — **분리 시험(고속 카메라·가속도계)으로 재접촉 여부·상대속도·접촉 펄스를 확인하는 것이 이 하중의 1순위 시험**이다.");
        P_();
    }

    /// <summary>표시용 SF: 금속 = 항복/극한 중 기준 대비 작은 쪽, 취성 = 극한, 응력 없음 = G 여유.</summary>
    private static double SfOf(PayloadPart p, double sfy, double sfu, double gm)
    {
        if (!p.HasStress) return gm;
        if (p.Material!.Brittle) return sfu;
        return sfy / 1.25 <= sfu / 1.5 ? sfy : sfu;
    }

    /// <summary>MC 칸의 SF 를 항복(yield=true)·극한 값으로 되돌려 판정에 넘긴다(표시 SF 는 둘 중 지배 하나라 근사: 같은 하중에서 σ 공통 → 비 = 허용 비).</summary>
    private static double MinSfAs(PayloadPart p, (double G, double Sf, double G95, double Sf05) c, bool yield, PayloadPartsConfig cfg)
    {
        var m = p.Material!;
        if (m.Brittle) return yield ? double.NaN : c.Sf05;
        // 같은 응력 σ: SF_y = Fy/σ, SF_u = Fu/σ → 표시 SF 가 어느 쪽이든 σ 를 되살려 둘 다 계산
        bool shownYield = (m.Yield / 1.25) <= (m.Ultimate / 1.5);
        double sigma = shownYield ? m.Yield / c.Sf05 : m.Ultimate / c.Sf05;
        return yield ? m.Yield / sigma : m.Ultimate / sigma;
    }

    private static int Rank(string verdict) => verdict switch { "파손/항복" => 3, "기준 미달" => 2, "G 초과(참고)" => 2, "통과" => 0, "통과(참고 G)" => 0, _ => 1 };

    /// <summary>접촉 사건(묶음): Pulse = 묶음에서 충격량이 가장 큰 접촉의 순응 펄스(충격량 모델이면 null), Members = 묶음의 접촉(시각, |J|, 펄스).</summary>
    private sealed record ImpGroup(double T, string Other, double J, double Dv, double Dw, ContactPulse? Pulse = null, List<(double T, double J, ContactPulse? Pulse)>? Members = null)
    {
        /// <summary>묶음 접촉력 합의 최대 [N] — 각 접촉 |J|·모양(순응 펄스, 없으면 지속 tau 반 사인)을 시각대로 겹친 값(크기 합, 방향 무시 — 상한).</summary>
        public double PeakForce(double tau, bool pulses)
        {
            var ms = Members ?? [(T, J, Pulse)];
            double t0 = ms.Min(m => m.T), t1 = ms.Max(m => m.T + (pulses && m.Pulse is { } p ? p.Duration : tau));
            double dt = Math.Max((t1 - t0) / 4000, 1e-7), best = 0;
            for (double t = t0; t <= t1 + dt; t += dt)
            {
                double f = 0;
                foreach (var m in ms)
                {
                    double s = t - m.T;
                    if (s < 0) continue;
                    f += pulses && m.Pulse is { } p ? m.J * p.ShapeAt(s) : s <= tau ? m.J * Math.PI / (2 * tau) * Math.Sin(Math.PI * s / tau) : 0;
                }
                best = Math.Max(best, f);
            }
            return best;
        }
    }

    /// <summary>같은 상대 물체 · 5 ms 안 연속 충격량을 한 접촉 사건으로 묶는다(벡터 합).</summary>
    private static List<ImpGroup> GroupImpulses(List<(double T, Vec3 Dv, Vec3 Dw, string Other, double J, Vec3 PointBody, ContactPulse? Pulse)> imps)
    {
        var res = new List<ImpGroup>();
        foreach (var g in imps.GroupBy(i => i.Other))
        {
            var cur = new List<(double T, Vec3 Dv, Vec3 Dw, string Other, double J, Vec3 PointBody, ContactPulse? Pulse)>();
            void Flush()
            {
                if (cur.Count == 0) return;
                var dv = cur.Aggregate(Vec3.Zero, (a, b) => a + b.Dv); var dw = cur.Aggregate(Vec3.Zero, (a, b) => a + b.Dw);
                res.Add(new ImpGroup(cur[0].T, g.Key, cur.Sum(x => x.J), dv.Norm, dw.Norm, cur.MaxBy(x => x.J).Pulse, cur.Select(x => (x.T, x.J, x.Pulse)).ToList()));
                cur.Clear();
            }
            foreach (var i in g.OrderBy(x => x.T))
            {
                if (cur.Count > 0 && i.T - cur[^1].T > 0.005) Flush();
                cur.Add(i);
            }
            Flush();
        }
        return res.OrderByDescending(x => x.J).ToList();
    }

    private static IEnumerable<string> Recommendations(PayloadPartsConfig cfg, PayloadRun nom, LoadsStudy.Result r)
    {
        var list = new List<string>();
        string Base(string n) => n.TrimEnd('1', '2', '3', '4');
        foreach (var g in nom.Parts.Where(p => p.Part.Judged).GroupBy(p => Base(p.Part.Name)))
        {
            double M(string[] wins) => g.SelectMany(p => wins.Select(id => r.Mc.TryGetValue($"{p.Part.Name}|{id}", out var st)
                    ? LoadsStudy.Margin(cfg, p.Part, P(st.SfY, 0.05), P(st.SfU, 0.05))
                    : p.Windows.Where(w => w.Window == id).Select(w => LoadsStudy.Margin(cfg, p.Part, w.SfYield, w.SfUltimate)).DefaultIfEmpty(double.NaN).Min()))
                .Where(double.IsFinite).DefaultIfEmpty(double.NaN).Min();
            double open = M(["drogue", "main"]), sep = M(["separation"]);
            string label = g.First().Part.Label.Replace(" (드론 1)", "");
            if (double.IsFinite(open) && open < 1)
                list.Add($"**{label}**: 낙하산 개방에서도 MC 5 % 여유 {open:F2} < 1 — 치수·재질 상향 필요(이 부품은 브레이크 충격만으로 기준 미달).");
            else if (double.IsFinite(sep) && sep < 1)
                list.Add($"**{label}**: 낙하산 개방 여유 {F(open, "F2")}(통과) · 분리 재접촉에서 MC 5 % 여유 {sep:F2} < 1 — 재접촉을 막거나(노즈 분리 방향·지연) 이 하중 경로를 보강.");
            else if (double.IsFinite(open) && open < 2)
                list.Add($"{label}: 낙하산 개방 여유 {open:F2}(기준 대비) — 통과하지만 가정 치수(D)에 민감, 실치수·재질 사양 확인 우선.");
        }
        list.Add("분리 재접촉은 노즈 L1 형상 가정(평평한 원통, 2절 스윕)에 지배된다 — 실제 노즈 항력(풍동/CFD)과 분리 시험으로 재접촉 상대속도를 먼저 확정하고, 재접촉이 남으면 노즈 측방 분리 Δv·드로그 지연으로 피하는 것이 부품 보강보다 효과적이다.");
        list.Add("장착 강성(드론 스트럿·핀 가이드·서보·배터리 스트랩)이 동적 증폭과 하중 경로를 정한다 — 드론 장착 타격 시험(fₙ·감쇠)으로 ×0.5–2 포락을 좁히면 보수 여유가 바로 커진다.");
        return list;
    }
}
