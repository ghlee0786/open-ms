using System.Text.Json.Nodes;
using Sim.Core.Analysis;
using Sim.Core.Engine;
using Sim.Core.Models;
using Sim.Core.Numerics;
using Sim.Core.Scenario;
using Sim.Core.Structures;
using Xunit.Abstractions;

namespace Sim.Core.Tests;

/// <summary>PRD AA절 FR-AN-08: 브레이크 코드 스냅·파단, 박스 내부 하중 경로(폼 패드·고정 핀 2질량), 부품 닫힌 식 응력, 단계 표·MC.</summary>
public class OpeningShockTests(ITestOutputHelper output)
{
    private const double G0 = 9.80665;

    // ---------------- 브레이크 코드 ----------------

    private static string CordJson(double breakLoad, double v0, string extraEvents = "") => $$"""
      {"schema":"mns.scenario/1","sim":{"dt":0.0005,"tEnd":0.6},"environment":{"vacuum":true},
       "carrier":{"name":"anchor","position":[0,0,-1000],"velocity":[0,0,0]},
       "bodies":[{"name":"box","position":[0,0,-997.05],"velocity":[0,0,{{v0}}],"mass":8.0,"shape":{"type":"box","size":[0.2,0.2,0.2]},"aero":"none",
                  "devices":[{"type":"parachute","name":"chute","d0":1.0,"cd0":1.0}]}],
       "connectors":[{"type":"breakCord","name":"cord","a":"anchor","pointA":[0,0,0],"b":"box","pointB":[0,0,0],
                      "length":3.0,"breakLoad":{{breakLoad}},"breakElongation":0.15,"dampingRatio":0}],
       "events":[{"name":"open","when":{"type":"after","event":"cord.broken","delay":0},"do":[{"type":"deploy","device":"box.chute"}]}{{extraEvents}}]}
      """;

    [Fact]
    public void Break_cord_snap_matches_energy_solution_and_does_not_break_below_load()
    {
        // 느슨함 5 cm 에서 5 m/s 로 내려가는 8 kg(진공): 팽팽 순간 v = √(v0² + 2g·0.05), ½mv² + mgδ = ½kδ² → T = mg + √((mg)² + k m v²)
        double m = 8, v0 = 5, fb = 1e6, k = fb / 0.15 / 3.0;
        double v = Math.Sqrt(v0 * v0 + 2 * G0 * 0.05);
        var w = TestUtil.Run(CordJson(fb, v0));
        var cord = (BreakCord)w.Connectors[0];
        double expected = m * G0 + Math.Sqrt(Math.Pow(m * G0, 2) + k * m * v * v);
        output.WriteLine($"T_max {cord.MaxTension:F1} N vs 해석 {expected:F1} N, k {cord.Stiffness:F0} N/m, 팽팽 {cord.TautTime:F4} s {cord.TautSpeed:F3} m/s");
        Assert.Equal(k, cord.Stiffness, 6);
        Assert.InRange(cord.MaxTension / expected, 0.995, 1.005);
        Assert.False(cord.Broken);
        Assert.False(w.FiredEvents.ContainsKey("cord.broken"));
        Assert.InRange(cord.TautSpeed / v, 0.995, 1.005);
    }

    [Fact]
    public void Break_cord_breaks_at_load_and_triggers_chute_deploy()
    {
        var w = TestUtil.Run(CordJson(400, 5));
        var cord = (BreakCord)w.Connectors[0];
        Assert.True(cord.Broken);
        Assert.True(w.FiredEvents.ContainsKey("cord.broken"));
        Assert.True(w.FiredEvents.ContainsKey("open"));
        var chute = (Parachute)w.GetBody("box").Devices[0];
        Assert.Equal(cord.BreakTime, chute.DeployTime, 3);
        // 파단 하중 근처(한 스텝 넘어선 값)에서 끊김
        Assert.InRange(cord.MaxTension, 400, 400 * 1.05);
        Assert.True(cord.Impulse > 0);
    }

    [Fact]
    public void Break_cord_rejects_invalid_parameters()
    {
        var ex = Assert.Throws<InvalidDataException>(() => TestUtil.Build(CordJson(-1, 5)));
        Assert.Contains("breakCord", ex.Message);
    }

    // ---------------- 폼 패드 ----------------

    [Fact]
    public void Foam_pad_has_elastic_plateau_densification_and_hysteretic_unloading()
    {
        var p = new FoamPad { Label = "t", Axis = 0, Area = 0.01, Thickness = 0.02, Modulus = 12e6, Plateau = 0.3e6, Densification = 0.7 };
        Assert.Equal(12e6 * 0.01, p.Loading(0.01), 6);                  // 탄성
        Assert.Equal(0.3e6, p.Loading(0.3), 6);                         // 평탄
        Assert.Equal(0.3e6 + 12e6 * 0.1 / 0.2, p.Loading(0.8), 3);      // 치밀화
        Assert.True(p.Loading(0.99) > 50 * p.Loading(0.5));
        p.EpsMax = 0.5;
        Assert.Equal(0.3e6, p.Stress(0.5), 6);
        Assert.Equal(0.3e6 - 12e6 * 0.01, p.Stress(0.49), 6);           // 기울기 E 하역
        Assert.Equal(0, p.Stress(0.4), 9);                              // 영구 압축 → 하중 0
        Assert.True(p.Tangent(0.9) > p.Tangent(0.1));
    }

    // ---------------- 2질량 마운트 ----------------

    /// <summary>진공 자유 낙하 상자(2 kg) + 페이로드(3 kg, 핀 스프링 k, 감쇠 0) — 상자 CG 에 half-sine 펄스(축 x, CG 연결선 위 → 모멘트 없음).</summary>
    private static string MountJson(double k, double peakG, double dur, string pinExtra = "", string pads = "[]", double tEnd = 0.2, double payloadMount = 0.1) => $$"""
      {"schema":"mns.scenario/1","sim":{"dt":0.001,"tEnd":{{tEnd}}},"environment":{"vacuum":true},
       "bodies":[
         {"name":"box","position":[0,0,-1000],"mass":2.0,"cg":[0,0,0],"inertia":[0.02,0.02,0.02],"aero":"none",
          "devices":[{"type":"pulse","name":"kick","peakG":{{peakG}},"duration":{{dur}},"direction":[1,0,0]},
                     {"type":"cushionMount","name":"mount","payload":"pay","pads":{{pads}},
                      "pins":{"count":1,"stiffness":{{k}},"dampingRatio":0{{pinExtra}}},"padDampingRatio":0,"wallGap":[0.5,0.5,0.5],
                      "channels":[{"name":"kick","device":"box.kick"}],
                      "stages":[{"label":"펄스","from":"go","duration":0.1}]}]},
         {"name":"pay","parent":"box","mount":[{{payloadMount}},0,0],"mass":3.0,"cg":[0,0,0],"inertia":[0.01,0.01,0.01],"aero":"none"}],
       "events":[{"name":"go","when":{"type":"time","t":0.02},"do":[{"type":"fire","device":"box.kick"}]}]}
      """;

    /// <summary>독립 계산(Duhamel 적분): μü + k u = −m₂ f(t), f = 펄스 비가속도 → 최대 |k u|.</summary>
    private static double DuhamelPeak(double k, double m1, double m2, double peakG, double dur)
    {
        double mu = m1 * m2 / (m1 + m2), w = Math.Sqrt(k / mu);
        double F(double t) => t >= 0 && t <= dur ? peakG * G0 * Math.Sin(Math.PI * t / dur) : 0;
        double peak = 0; int n = 4000; double T = dur + 4 * 2 * Math.PI / w, h = T / n;
        for (int i = 0; i <= n; i++)
        {
            double t = i * h, s = 0;
            int m = 2000; double dh = Math.Min(t, dur) / m;
            for (int j = 0; j <= m; j++)
            {
                double tau = j * dh, wt = j is 0 || j == m ? 0.5 : 1;
                s += wt * F(tau) * Math.Sin(w * (t - tau));
            }
            double u = -(m2 / mu) / w * s * dh;
            peak = Math.Max(peak, Math.Abs(k * u));
        }
        return peak;
    }

    [Theory]
    [InlineData(2.0e5, 10.0, 0.010)]   // f_n ≈ 63 Hz, 펄스 10 ms (동적 증폭 영역)
    [InlineData(5.0e6, 15.0, 0.025)]   // 강체에 가까움(준정적)
    public void Mount_half_sine_response_matches_duhamel_integral(double k, double peakG, double dur)
    {
        var w = TestUtil.Run(MountJson(k, peakG, dur));
        var m = w.GetBody("box").Devices.OfType<CushionMount>().Single();
        double expected = DuhamelPeak(k, 2.0, 3.0, peakG, dur);
        double measured = m.MaxPayloadGInBox * 3.0 * G0;
        output.WriteLine($"k {k:E1}: 페이로드 최대 반력 {measured:F1} N vs Duhamel {expected:F1} N (비 {measured / expected:F4}), 서브스텝 {m.Substeps}");
        Assert.InRange(measured / expected, 0.98, 1.02);
        // 펄스 전 자유 낙하(0 G) 에서 출발 과도 없음
        Assert.True(m.History.Where(s => s.T < 0.019).All(s => s.PayloadG < 1e-6));
    }

    [Fact]
    public void Mount_starts_in_static_equilibrium_under_steady_1g()
    {
        // 정지 운반체에 실린 상자(1 G 지지) — 첫 스텝 정적 평형 초기화로 출발 과도(2배 동적 하중)가 없어야 한다
        var json = """
          {"schema":"mns.scenario/1","sim":{"dt":0.001,"tEnd":0.3},"environment":{"vacuum":true},
           "carrier":{"name":"heli","position":[0,0,-500],"velocity":[0,0,0]},
           "bodies":[
             {"name":"box","parent":"heli","mount":[0,0,0],"mountAttitude":[0,-90,0],"mass":2.0,"cg":[0,0,0],"aero":"none",
              "devices":[{"type":"cushionMount","name":"mount","payload":"pay",
                          "pads":[{"axis":"+x","material":"epp","area":0.001,"thickness":0.02}]}]},
             {"name":"pay","parent":"box","mount":[0.1,0,0],"mass":3.0,"cg":[0,0,0],"aero":"none"}]}
          """;
        var w = TestUtil.Run(json);
        var m = w.GetBody("box").Devices.OfType<CushionMount>().Single();
        Assert.InRange(m.MaxPayloadGInBox, 0.999, 1.001);
        // 바닥 패드 정적 압축 = m g / (E A / h) (탄성 구간)
        var pad = m.Pads[0];
        double expected = 3.0 * G0 / pad.Stiffness0;
        Assert.Equal(expected, pad.MaxStrainSeen * pad.Thickness, 6);
    }

    [Fact]
    public void Retention_pins_break_and_crushable_pad_then_limits_payload_g()
    {
        // 핀(파단 50 N) + 압괴 패드(평탄 0.3 MPa × 3 cm² = 90 N → 3 kg 기준 3.06 G 상한) — 10 G 펄스 방향(+x) 반대쪽(−x) 패드가 눌림
        // (상대 행정 ≈ 15 mm < 치밀화 시작 35 mm)
        var pads = """[{"axis":"-x","material":"epp","area":0.0003,"thickness":0.05},{"axis":"+x","material":"epp","area":0.0003,"thickness":0.05}]""";
        var w = TestUtil.Run(MountJson(5e5, 10, 0.01, ",\"breakLoad\":50", pads, tEnd: 0.15));
        var m = w.GetBody("box").Devices.OfType<CushionMount>().Single();
        Assert.True(m.PinsBroken);
        Assert.True(w.FiredEvents.ContainsKey("box.mount.pinsBroken"));
        var after = m.History.Where(s => s.T > m.PinsBrokenTime + 0.002).ToList();
        double plateauG = 0.3e6 * 0.0003 / (3.0 * G0);
        output.WriteLine($"핀 파단 {m.PinsBrokenTime:F4} s, 이후 최대 {after.Max(s => s.PayloadG):F2} G (평탄 상한 {plateauG:F2} G), 패드 최대 변형률 {m.Pads.Max(p => p.MaxStrainSeen):F3}");
        Assert.True(after.Max(s => s.PayloadG) < plateauG * 1.15);
        Assert.True(m.Pads.Sum(p => p.Energy) > 0);
        Assert.False(m.Pads.Any(p => p.BottomedOut));
        Assert.True(m.MaxBaseG > 15);   // 페이로드가 떨어져 나간 만큼 상자(2 kg)는 결합체 10 G 보다 더 가속
    }

    [Fact]
    public void Mount_validates_inputs()
    {
        string Bad(string mount) => $$"""
          {"schema":"mns.scenario/1","sim":{"tEnd":0.01},
           "bodies":[{"name":"box","altitude":100,"mass":2.0,"devices":[{{mount}}]},
                     {"name":"pay","parent":"box","mass":3.0},{"name":"other","altitude":50,"mass":1}],
           "connectors":[]}
          """;
        Assert.Contains("직계 자식", Assert.Throws<InvalidDataException>(() => TestUtil.Build(Bad("""{"type":"cushionMount","name":"m","payload":"other"}"""))).Message);
        Assert.Contains("폼이 아닙니다", Assert.Throws<InvalidDataException>(() => TestUtil.Build(Bad(
            """{"type":"cushionMount","name":"m","payload":"pay","pads":[{"axis":"+x","material":"al6061-t6","area":0.01,"thickness":0.01}]}"""))).Message);
        Assert.Contains("+x|-x", Assert.Throws<InvalidDataException>(() => TestUtil.Build(Bad(
            """{"type":"cushionMount","name":"m","payload":"pay","pads":[{"axis":"x","material":"epp","area":0.01,"thickness":0.01}]}"""))).Message);
        Assert.Contains("연결 'nope'", Assert.Throws<InvalidDataException>(() => TestUtil.Build(Bad(
            """{"type":"cushionMount","name":"m","payload":"pay","channels":[{"name":"c","connector":"nope"}]}"""))).Message);
        Assert.Contains("채널 'zz'", Assert.Throws<InvalidDataException>(() => TestUtil.Build(Bad(
            """{"type":"cushionMount","name":"m","payload":"pay","parts":[{"name":"p","kind":"bar","material":"ss304","area":1e-5,"load":{"channel":"zz"}}]}"""))).Message);
        Assert.Contains("재질 'unobtainium'", Assert.Throws<InvalidDataException>(() => TestUtil.Build(Bad(
            """{"type":"cushionMount","name":"m","payload":"pay","parts":[{"name":"p","kind":"bar","material":"unobtainium","area":1e-5,"load":{"inertiaMass":1}}]}"""))).Message);
        Assert.Contains("이벤트 'ghost'", Assert.Throws<InvalidDataException>(() => TestUtil.Build(Bad(
            """{"type":"cushionMount","name":"m","payload":"pay","stages":[{"label":"s","from":"ghost"}]}"""))).Message);
    }

    // ---------------- 부품 닫힌 식 ----------------

    [Theory]
    // Shigley's Mechanical Engineering Design 표 3-4(원형 단면 곡선보 내측 계수 K_i)
    [InlineData(1.2, 3.41)]
    [InlineData(2.0, 1.62)]
    [InlineData(4.0, 1.23)]
    [InlineData(10.0, 1.08)]
    public void Hook_curvature_factor_matches_shigley_table(double rOverC, double ki)
    {
        Assert.Equal(ki, PartStress.HookCurvatureFactor(2.0, rOverC), 2);
    }

    [Fact]
    public void Plate_lug_bolt_cantilever_match_hand_calculations()
    {
        // Roark 원판 중앙 하중, 고정: σ = 3W/(2πt²)(1+ν)ln(a/r0) (r0 ≥ 0.5t)
        var (c, where) = PartStress.PlateCentral(0.1, 0.005, 0.01, 0.3, clamped: true);
        Assert.Equal(3 / (2 * Math.PI * 0.005 * 0.005) * 1.3 * Math.Log(10), c, 6);
        Assert.Contains("중앙", where);
        // 단순지지 = 고정 + 3W/(2πt²)
        Assert.Equal(c + 3 / (2 * Math.PI * 0.005 * 0.005), PartStress.PlateCentral(0.1, 0.005, 0.01, 0.3, false).Coef, 6);
        // 작은 하중 반경: 등가 반경 r0' = √(1.6r0² + t²) − 0.675t
        Assert.Equal(Math.Sqrt(1.6e-6 + 2.5e-5) - 0.675 * 0.005, PartStress.EquivalentRadius(0.001, 0.005), 12);
        // 러그: 폭 20, 구멍 6, 두께 4, 끝 8 mm → 순단면 1/(14·4 mm²)=17.9 MPa/kN, 지압 1/(6·4)/1.5 = 27.8, 전단 이탈 √3/(2·5·4) = 43.3 → 전단 이탈 지배
        var (lc, mode) = PartStress.Lug(0.020, 0.006, 0.004, 0.008);
        Assert.Equal(Math.Sqrt(3) / (2 * 0.005 * 0.004), lc, 6);
        Assert.Contains("전단", mode);
        // 볼트 Ø3 2면 전단: √3/(2·π/4·9e-6)
        Assert.Equal(Math.Sqrt(3) / (2 * Math.PI / 4 * 9e-6), PartStress.Bolt(0.003, 1, "shear", 2).Coef, 3);
        // M4×0.7 인장 응력 단면적 8.78 mm² (ISO 898-1 표)
        Assert.InRange(PartStress.Bolt(0.004, 1, "tension", 1, 0.0007).Coef * 8.78e-6, 0.995, 1.005);
        Assert.Equal(6 * 0.01 / (0.006 * 0.002 * 0.002), PartStress.CantileverRect(0.01, 0.006, 0.002), 3);
    }

    [Fact]
    public void Part_definition_parses_and_computes_safety_with_material()
    {
        var o = JsonNode.Parse("""{"name":"hook","kind":"hook","material":"ss304","wireDiameter":0.004,"bendRadius":0.006,"load":{"channel":"t"}}""")!.AsObject();
        var p = PartStress.Parse(o, "box.mount");
        double s = p.Stress(500);
        Assert.Equal(PartStress.HookInner(0.004, 0.006) * 500, s, 6);
        Assert.Equal(215e6 / s, p.Safety(500), 9);   // SS304 항복 215 MPa
        var brittle = PartStress.Parse(JsonNode.Parse("""{"name":"pl","kind":"bar","material":"cfrp-qi","area":1e-4,"load":{"inertiaMass":1}}""")!.AsObject(), "x");
        Assert.Equal(600e6, brittle.Allowable(), 3);   // 취성 → 인장강도
        Assert.Throws<InvalidDataException>(() => PartStress.Parse(JsonNode.Parse("""{"name":"q","kind":"rivet","material":"ss304","load":{"inertiaMass":1}}""")!.AsObject(), "x"));
    }

    // ---------------- 시나리오 10 ----------------

    private static World RunScenario(string file)
    {
        var doc = ScenarioDoc.Load(TestUtil.Scenario(file));
        doc.Root["realModels"] = new JsonObject { ["mode"] = "synthetic" };   // 테스트는 합성 대체(실모델은 git 밖)
        var w = doc.Build();
        w.Run();
        return w;
    }

    private static ScenarioDoc Synthetic(string file)
    {
        var doc = ScenarioDoc.Load(TestUtil.Scenario(file));
        doc.Root["realModels"] = new JsonObject { ["mode"] = "synthetic" };
        return doc;
    }

    [DesignFact]
    public void Monte_carlo_gives_safety_factor_distribution_per_part()
    {
        var mc = OpeningShock.MonteCarloRun(Synthetic("10_box_chute_opening_shock.json"), 12, 7);
        output.WriteLine($"실행 {mc.Runs}, 오류 {mc.Errors}, 코드 미파단 {mc.CordNotSnapped}");
        foreach (var s in mc.Parts) output.WriteLine($"  {s.Label,-40} min {s.Min:F2} p5 {s.P5:F2} p50 {s.P50:F2} P<1 {s.ProbBelow1:P0}");
        foreach (var s in mc.Scalars) output.WriteLine($"  {s.Name,-36} {s.Min:F2} … {s.Max:F2}");
        Assert.Equal(0, mc.Errors);
        Assert.Equal(9, mc.Parts.Count);
        Assert.All(mc.Parts, s => Assert.True(s.Min <= s.P5 && s.P5 <= s.P50, s.Label));
        Assert.Contains(mc.Scalars, s => s.Name.StartsWith("개방 하중 계수") && s.N == 12 && s.Max > s.Min);
        // 분산이 실제로 들어갔다(충만 상수 4–12)
        Assert.True(mc.Mc.Runs.Select(r => r.Values[0]).Distinct().Count() == 12);
    }

    [DesignFact]
    public void Report_writes_markdown_with_all_sections()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mns_os_" + Guid.NewGuid().ToString("N")[..8]);
        var rep = OpeningShockReport.Generate(Synthetic("10_box_chute_opening_shock.json"), Synthetic("10b_box_chute_opening_shock_crush.json"),
            new OpeningShockReportOptions { OutDir = dir, RawDir = Path.Combine(dir, "raw"), McRuns = 6, SweepThickness = [0.018, 0.06], Command = "test" });
        var md = File.ReadAllText(Path.Combine(dir, "README.md"));
        foreach (var h in new[] { "## 0. 질문에 대한 답", "## 3. 단계별", "## 4. 부품별", "## 5. 비교안", "## 6. 압괴 패드 두께", "## 7. 개방하중 불확도", "## 8. CalculiX", "## 9. 가정" })
            Assert.Contains(h, md);
        foreach (var f in new[] { "fig1_g_history.svg", "fig2_loads.svg", "fig3_eject_zoom.svg", "fig4_pad_sweep.svg" })
            Assert.True(File.Exists(Path.Combine(dir, f)), f);
        Assert.True(File.Exists(Path.Combine(dir, "raw", "mount_history.csv")));
        Assert.Equal(2, rep.Sweep.Count);
        Assert.True(rep.Sweep[1].PayloadG < rep.Sweep[0].PayloadG);   // 두꺼운 패드 → 치밀화 없이 더 낮은 G
    }

    [DesignFact]
    public void Scenario10b_crush_pads_report_isolation_or_bottoming()
    {
        var w = RunScenario("10b_box_chute_opening_shock_crush.json");
        var r = OpeningShock.Analyze(w);
        Dump(r);
        Assert.All(r.Stages, s => Assert.True(s.Fired, s.Stage));
        // 축 여유 18 mm 압괴 패드는 사출 때 치밀화(바닥 닿음) — 판정은 내되 G 는 유한(특이점 발산 없음)
        var m = r.Mounts.Single();
        Assert.Contains(m.Pads, p => p.BottomedOut);
        Assert.True(double.IsFinite(m.MaxPayloadGInBox) && m.MaxPayloadGInBox < 2000, $"{m.MaxPayloadGInBox:F0} G");
        // 하역 이력은 소산만: 패드가 패드 두께를 넘어 뚫리지 않고, 흡수 에너지가 사출 상대 운동에너지 규모(수 J)를 넘지 않는다
        Assert.All(m.Pads, p => Assert.True(p.MaxStrainSeen < 1, $"{p.Label} ε {p.MaxStrainSeen:F3}"));
        Assert.True(m.Pads.Sum(p => p.Energy) < 20, $"{m.Pads.Sum(p => p.Energy):F1} J");
    }

    [DesignFact]
    public void Thick_crush_pads_limit_drone_g_near_plateau_load()
    {
        // 압괴 중에는 점성 감쇠를 더하지 않는다 → 충분히 두꺼운 패드면 드론 G ≈ 평탄 하중/질량(+ 탄성 과도)
        var doc = Synthetic("10b_box_chute_opening_shock_crush.json");
        foreach (int i in new[] { 0, 1 }) JsonPath.SetNumber(doc.Root, $"bodies.box1.devices.mount.pads.{i}.thickness", 0.10);
        var w = doc.Build(); w.Run();
        var m = w.GetBody("box1").Devices.OfType<CushionMount>().Single();
        var pad = m.Pads[0];
        double plateauG = pad.Plateau * pad.Area / (m.PayloadMass * G0);
        output.WriteLine($"박스 안 최대 {m.MaxPayloadGInBox:F2} G, 평탄 {plateauG:F2} G, 바닥 {m.Pads.Any(p => p.BottomedOut)}");
        Assert.False(m.Pads.Any(p => p.BottomedOut));
        Assert.InRange(m.MaxPayloadGInBox, 0.9 * plateauG, 1.25 * plateauG);
    }

    private void Dump(OpeningShock.Result r)
    {
        foreach (var s in r.Stages)
            output.WriteLine($"{s.Stage,-28} t {s.T0:F3}–{s.T1:F3}  드론 {s.PayloadG:F2} G  박스 {s.BaseG:F2} G  패드 {s.PadStrain:F3}  핀 {s.PinForce:F0} N  최약 {s.Weakest?.Label} SF {s.Weakest?.Safety:F2}");
        var m = r.Mounts.Single();
        output.WriteLine($"mount: 드론 최대 {m.MaxPayloadG:F2} G (박스 안 {m.MaxPayloadGInBox:F2}), 박스 {m.MaxBaseG:F2} G, 핀 파단 {m.PinsBroken}, 서브스텝 {m.Substeps}");
        foreach (var p in m.Pads) output.WriteLine($"  pad {p.Label}: ε_max {p.MaxStrainSeen:F3}, σ_max {p.MaxStress / 1e6:F3} MPa, E {p.Energy:F2} J, 바닥 {p.BottomedOut}");
    }

    [DesignFact]
    public void Scenario10_runs_all_stages_and_reports_g_and_safety_factors()
    {
        var w = RunScenario("10_box_chute_opening_shock.json");
        var r = OpeningShock.Analyze(w);
        foreach (var s in r.Stages)
        {
            output.WriteLine($"{s.Stage,-28} {(s.Fired ? "" : "미발생")} t {s.T0:F3}–{s.T1:F3}  드론 {s.PayloadG:F2} G  박스 {s.BaseG:F2} G  패드 {s.PadStrain:F3}  핀 {s.PinForce:F0} N");
            foreach (var p in s.Parts) output.WriteLine($"    {p.Label,-40} F {p.ForceN,8:F1} N  σ {p.StressMPa,8:F2} MPa  SF {p.Safety,8:F2}  (×{p.RateFactor:F3})");
        }
        foreach (var c in r.Cords) output.WriteLine($"cord {c.Name}: 최대 {c.MaxTension:F0} N, 파단 {c.Snapped} @ {c.BreakTime:F3}, 팽팽 {c.TautTime:F3} s ({c.TautSpeed:F2} m/s), 충격량 {c.Impulse:F2}");
        foreach (var c in r.Chutes) output.WriteLine($"chute {c.Name}: 최대 {c.MaxForce:F0} N @ {c.MaxForceTime:F3}, Ck {c.OpeningFactor:F2}, V_s {c.StretchSpeed:F2}");
        var m = r.Mounts.Single();
        output.WriteLine($"mount: 드론 최대 {m.MaxPayloadG:F2} G (박스 안 {m.MaxPayloadGInBox:F2}), 박스 {m.MaxBaseG:F2} G, 서브스텝 {m.Substeps}, 표본 {m.History.Count}");
        Assert.All(r.Stages, s => Assert.True(s.Fired, s.Stage));
        Assert.True(r.Cords.Single().Snapped);
        Assert.All(r.Overall.Values, p => Assert.True(p.Safety > 0 && !double.IsNaN(p.Safety), p.Part));
        Assert.Equal(9, r.Overall.Count);
    }
}
