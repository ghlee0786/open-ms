using System.Text.Json.Nodes;
using Sim.Core.Analysis;
using Sim.Core.Fea;
using Sim.Core.Models;
using Sim.Core.Numerics;
using Sim.Core.Scenario;
using Sim.Core.Structures;

namespace Sim.Core.Tests;

/// <summary>`mns loads` 구성 요소 검산(손계산·해석해 대비) — SRS, 2자유도 사슬, 부품 응력 식, F = m·a, CFD Cp 셸 매핑, 보고서, 재질 수정.</summary>
public class LoadsStudyTests
{
    private static double[] Const(int n, double v) => Enumerable.Repeat(v, n).ToArray();

    [Fact]
    public void Srs_step_input_undamped_peaks_at_twice_the_step()
    {
        // 계단 입력(무감쇠) 1자유도 절대 가속도 최대 = 2 × 계단 (해석해)
        double dt = 1e-5;
        var a = Const(20000, 1.0);
        Assert.InRange(ShockSpectrum.Maximax(a, dt, 100, 0), 1.98, 2.0001);
        // ζ = 5 % 면 1 + e^(−ζπ/√(1−ζ²)) = 1.854
        Assert.InRange(ShockSpectrum.Maximax(a, dt, 100, 0.05), 1.84, 1.87);
    }

    [Fact]
    public void Srs_half_sine_has_known_peak_and_limits()
    {
        // 반 사인 펄스(지속 T) 무감쇠 SRS 최대 ≈ 1.77 (fₙT ≈ 0.8), 고주파 → 1, 저주파 → 작음
        double dt = 1e-5, T = 0.01;
        int n = 8000;
        var a = new double[n];
        for (int i = 0; i < n; i++) { double t = i * dt; a[i] = t <= T ? Math.Sin(Math.PI * t / T) : 0; }
        double best = Enumerable.Range(40, 120).Select(k => ShockSpectrum.Maximax(a, dt, k, 0)).Max();
        Assert.InRange(best, 1.74, 1.79);
        Assert.InRange(ShockSpectrum.Maximax(a, dt, 2000, 0.05), 0.99, 1.08);
        Assert.True(ShockSpectrum.Maximax(a, dt, 5, 0.05) < 0.35);
    }

    [Fact]
    public void Srs_of_ramp_matches_analytic_ramp_daf()
    {
        // 낙하산 개방형 램프(상승 45 ms) → 일정: 무감쇠 DAF = 1 + |sin(πfₙt_r)|/(πfₙt_r)
        double dt = 2e-5, tr = 0.045;
        var a = Enumerable.Range(0, 30000).Select(i => Math.Min(i * dt / tr, 1.0)).ToArray();
        foreach (var fn in new[] { 15.0, 37.0, 90.0 })
            Assert.InRange(ShockSpectrum.Maximax(a, dt, fn, 0) / ShockSpectrum.RampDaf(tr, fn), 0.99, 1.01);
        Assert.InRange(ShockSpectrum.RampDaf(tr, 90), 1.0, 1.1);   // 장착 90 Hz 에서 낙하산 개방은 준정적
    }

    [Fact]
    public void Chain2_reduces_to_single_dof_when_top_mass_is_rigid_and_light()
    {
        // m₂ → 매우 작고 k₂ → 매우 크면 m₂ 응답 = m₁ 1자유도 응답(같은 감쇠 정의 c₁ = 2ζ√(k₁(m₁+m₂)))
        double dt = 2e-5, m1 = 4.0, k1 = 4.0 * Math.Pow(2 * Math.PI * 90, 2);
        var a = new double[6000];
        for (int i = 0; i < a.Length; i++) { double t = i * dt; a[i] = t <= 0.01 ? 150 * Math.Sin(Math.PI * t / 0.01) : 0; }
        var sdof = ShockSpectrum.Maximax(a, dt, 90, 0.05);
        var (ax, _, _) = ShockSpectrum.Chain2Maximax3(a, new double[a.Length], new double[a.Length], dt, m1, k1, 1e-4, 1e-4 * Math.Pow(2 * Math.PI * 5000, 2), 0.05);
        Assert.InRange(ax / sdof, 0.97, 1.03);
    }

    [Fact]
    public void Part_stress_pin_and_tube_match_hand_calculation()
    {
        // 핀 Ø8, 외팔 3.5 mm: σ_b = 32·F·a/(πd³) = 0.0696 MPa/N, τ_avg = F/A = F/(π·16 mm²)
        var (c, mode) = PartStress.Pin(0.008, 0.0035);
        Assert.Equal("외팔 굽힘", mode);
        Assert.InRange(c / 1e6, 0.0695, 0.0698);
        double f = 815;   // 대화 세션 손계산 τ 16 MPa(= 815 N / 50.3 mm²) 재현
        Assert.InRange(f / (Math.PI * 0.004 * 0.004) / 1e6, 16.1, 16.3);
        // 굽힘 없는 핀(a = 0)은 횡전단 최대 √3·(4/3)·F/A
        var (c0, m0) = PartStress.Pin(0.008, 0);
        Assert.Equal("횡전단(√3τ_max)", m0);
        Assert.InRange(c0 * Math.PI * 0.004 * 0.004, Math.Sqrt(3) * 4 / 3 - 1e-9, Math.Sqrt(3) * 4 / 3 + 1e-9);
        // 정사각 관 □20×2, L 107 mm: I = (20⁴ − 16⁴)/12 = 7872 mm⁴ → σ/F = 107·10/7872 = 0.1359 MPa/N
        Assert.InRange(PartStress.SquareTubeI(0.02, 0.002) * 1e12, 7871.9, 7872.1);
        Assert.InRange(PartStress.TubeCantilever(0.107, 0.02, 0.002) / 1e6, 0.1358, 0.1361);
    }

    private const string PulseScenario = """
    {
      "schema": "mns.scenario/1", "name": "payload_pulse",
      "sim": { "dt": 0.0005, "tEnd": 0.25, "outputRate": 100 },
      "environment": { "vacuum": true },
      "bodies": [
        { "name": "box", "altitude": 500, "mass": 10.0, "shape": { "type": "box", "size": [0.4, 0.2, 0.2] },
          "devices": [ { "type": "pulse", "name": "shock", "peakG": 10, "duration": 0.04, "direction": [1, 0, 0] } ] },
        { "name": "item", "parent": "box", "mount": [0.1, 0, 0], "mass": 2.0, "shape": { "type": "box", "size": [0.05, 0.05, 0.05] } }
      ],
      "events": [ { "name": "go", "when": { "type": "time", "t": 0.02 }, "do": [ { "type": "fire", "device": "box.shock" } ] } ]
    }
    """;

    private static string PartsJson(string extra = "") => $$"""
    {
      "schema": "mns.payload-parts/1", "root": "box", "zeta": 0.05, "fnFactors": [1.0],
      "contactDuration_s": { "nominal": 0.002, "min": 0.001, "max": 0.005 },
      "windows": [ { "id": "pulse", "label": "펄스", "from": "go", "duration": 0.15 } ],
      "parts": [
        { "name": "pin", "label": "핀", "mass": { "bodies": ["item"] }, "at": "cg", "direction": "axial",
          "mount": { "fn_Hz": 2000 }, "stress": { "kind": "pin", "diameter": 0.005, "lever": 0.002, "material": "17-4ph-h1150" } }{{extra}}
      ]
    }
    """;

    [Fact]
    public void Payload_part_force_equals_mass_times_rigid_acceleration_for_stiff_mount()
    {
        // 40 ms 반 사인 10 G(총 질량 12 kg 에 작용) → 비중력 가속도 10 G·(12/12) — 매우 강한 장착(2 kHz)은 강체와 같다: F = m·a
        var dir = Path.Combine(Path.GetTempPath(), "mns_loads_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var pp = Path.Combine(dir, "parts.json");
            File.WriteAllText(pp, PartsJson());
            var cfg = PayloadPartsConfig.Load(pp, TestUtil.RepoRoot);
            var doc = ScenarioDoc.Parse(PulseScenario, TestUtil.RepoRoot);
            var run = PayloadShock.Run(doc, cfg, cfg.TauNominal);
            var p = run.Parts.Single();
            var w = p.Windows.Single();
            Assert.InRange(w.RigidG, 9.9, 10.05);
            Assert.InRange(w.DynG / w.RigidG, 0.99, 1.02);
            Assert.InRange(w.ForceN / (2.0 * w.DynG * PayloadShock.G0), 0.999, 1.001);
            Assert.InRange(w.StressPa / (PartStress.Pin(0.005, 0.002).Coef * w.ForceN), 0.999, 1.001);
            Assert.InRange(w.SfYield * w.StressPa / 724e6, 0.999, 1.001);
            // 장착 고유진동수 = 반 사인 지속의 역수 근처면 증폭(SRS ≈ 1.7)
            File.WriteAllText(pp, PartsJson().Replace("\"fn_Hz\": 2000", "\"fn_Hz\": 20"));
            var cfg2 = PayloadPartsConfig.Load(pp, TestUtil.RepoRoot);
            var run2 = PayloadShock.Run(doc, cfg2, cfg2.TauNominal);
            Assert.InRange(run2.Parts.Single().Windows.Single().Daf, 1.5, 1.8);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Design_mode_srs_ratio_and_sweep_part_margins_come_from_the_run()
    {
        // 설계 FEA 고유진동수에서 SRS/강체: 2 kHz(펄스 40 ms 보다 훨씬 짧은 주기) → 1(준정적), 20 Hz(fₙT = 0.8) → 반 사인 증폭 1.5–1.8
        var dir = Path.Combine(Path.GetTempPath(), "mns_loads_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var pp = Path.Combine(dir, "parts.json");
            File.WriteAllText(pp, PartsJson().Replace("\"id\": \"pulse\"", "\"id\": \"separation\"")
                .Replace("\"zeta\": 0.05,", "\"zeta\": 0.05, \"designModes\": { \"fn_Hz\": [2000, 20], \"basis\": \"시험\" },"));
            var cfg = PayloadPartsConfig.Load(pp, TestUtil.RepoRoot);
            Assert.Equal([2000.0, 20.0], cfg.DesignModesHz);
            var doc = ScenarioDoc.Parse(PulseScenario, TestUtil.RepoRoot);
            var run = PayloadShock.Run(doc, cfg, cfg.TauNominal);
            var srs = PayloadShock.DesignModeSrs(run, cfg, run.Positions["pin"]).Single();
            Assert.Equal("separation", srs.Window);
            Assert.InRange(srs.RigidG, 9.9, 10.05);
            Assert.InRange(srs.SrsG[0] / srs.RigidG, 0.99, 1.02);
            Assert.InRange(srs.SrsG[1] / srs.RigidG, 1.5, 1.8);
            // 스윕 점 부품별 여유 = min(항복 SF/1.25, 극한 SF/1.5) — 개방 창(드로그·주낙하산)이 없으면 NaN
            var w = run.Parts.Single().Windows.Single();
            var m = LoadsStudy.PartMargins(run, cfg)["pin"];
            Assert.Equal(Math.Min(w.SfYield / cfg.SfYield, w.SfUltimate / cfg.SfUltimate), m.Separation, 9);
            Assert.True(double.IsNaN(m.Opening));
            // 설계 모드 범위 SRS 는 탑재 부품(stations)·run.DesignModes 일 때만 — 범위 20–2000 Hz 에 반 사인 증폭 최대(fₙT ≈ 0.8)가 들어 있다
            Assert.True(double.IsNaN(w.DynModesG) && double.IsNaN(m.SeparationModes));
            File.WriteAllText(pp, File.ReadAllText(pp).Replace("\"name\": \"pin\"", "\"name\": \"pin{k}\", \"stations\": [1]"));
            var cfgS = PayloadPartsConfig.Load(pp, TestUtil.RepoRoot);
            var runS = PayloadShock.Run(doc, cfgS, cfgS.TauNominal, designModes: true);
            var ws = runS.Parts.Single().Windows.Single();
            Assert.InRange(ws.DynModesG / ws.RigidG, 1.5, 1.8);
            Assert.InRange(ws.FnModesHz, 20, 2000);
            Assert.Equal(cfgS.Part("pin1").Material!.Yield / (cfgS.Part("pin1").Coef * 2.0 * ws.DynModesG * PayloadShock.G0), ws.SfYieldModes, 6);
            var mS = LoadsStudy.PartMargins(runS, cfgS)["pin1"];
            Assert.Equal(Math.Min(ws.SfYieldModes / cfg.SfYield, ws.SfUltimateModes / cfg.SfUltimate), mS.SeparationModes, 9);
            Assert.Contains(20.0, PayloadShock.ModeSweepHz(cfgS));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Massprops_selection_below_reference_body_cg_with_exclude_prefix()
    {
        // 하판(y 0.001) 아래 CG 바디 + 하판 자신 − 제외 접두(레일 슈): 손으로 질량을 적지 않고 CAD CG 로 고른다
        var mp = JsonNode.Parse("""
          {"groups":{"d":{"mass":10.0,"parts":[
            {"body":"Plate_Bottom","mass_kg":0.05,"cg_m":[0,0.001,0.2]},
            {"body":"Plate_Top","mass_kg":0.05,"cg_m":[0,0.021,0.2]},
            {"body":"Battery","mass_kg":1.0,"cg_m":[0,0.044,0.2]},
            {"body":"Leg","mass_kg":0.2,"cg_m":[0,-0.05,0.2]},
            {"body":"Shoe_A","mass_kg":0.3,"cg_m":[0,-0.07,0.1]},
            {"body":"Telemetry","mass_kg":0.03,"cg_m":[0,-0.005,0.1]}]}}}
          """)!.AsObject();
        var cfg = new PayloadPartsConfig { Path = "x", Root = "r", MassProps = mp };
        PayloadPart Part(string[]? exclude) => new()
        {
            Name = "so", Label = "so", Direction = "axial", MassParts = new MassPropsSelection("d", ["Plate_Bottom"], [], "drone", ("Plate_Bottom", 1), exclude),
        };
        var all = PayloadShock.SelectedMassParts(cfg, Part(null));
        Assert.Equal(["Plate_Bottom", "Leg", "Shoe_A", "Telemetry"], all.Select(x => x.Body));
        Assert.Equal(0.58, all.Sum(x => x.Mass), 12);
        var lat = PayloadShock.SelectedMassParts(cfg, Part(["Shoe_"]));
        Assert.Equal(0.28, lat.Sum(x => x.Mass), 12);
        // 기준 바디가 그룹에 없으면 오류
        var bad = new PayloadPart { Name = "b", Label = "b", Direction = "axial", MassParts = new MassPropsSelection("d", [], [], "drone", ("Nope", 1)) };
        Assert.Throws<InvalidDataException>(() => PayloadShock.SelectedMassParts(cfg, bad));
    }

    [Fact]
    public void Mount_round_cantilever_and_end_moment_arm_stiffness_match_beam_formulas()
    {
        // 원형 중실 외팔 4개: k = 4·3EI/L³, I = πd⁴/64 (Al6061 E 68.9 GPa, Ø6 × 18 mm)
        var r = PayloadPartsConfig.MountStiffness(JsonNode.Parse("""{"kind":"cantileverRound","diameter":0.006,"length":0.018,"count":4,"material":"al6061-t6"}""")!.AsObject(), "t");
        Assert.Equal(4 * 3 * 68.9e9 * (Math.PI * Math.Pow(0.006, 4) / 64) / Math.Pow(0.018, 3), r.K, 1e-3);
        // 끝 모멘트 보(자유 길이 L, 16×6)에 레버 a 로 달린 하중점: θ = F·a·L/(EI) → δ = θ·a → k = EI/(a²L) (Al7075 E 71.7 GPa)
        var m = PayloadPartsConfig.MountStiffness(JsonNode.Parse("""{"kind":"momentArm","width":0.016,"height":0.006,"length":0.043,"arm":0.063,"material":"al7075-t6"}""")!.AsObject(), "t");
        Assert.Equal(71.7e9 * (0.016 * Math.Pow(0.006, 3) / 12) / (0.063 * 0.063 * 0.043), m.K, 1e-3);
    }

    [Fact]
    public void Cfd_cp_mapping_integrates_to_analytic_normal_force_and_correction_scales_it()
    {
        // 해석 Cp = cos φ(φ = 바람받이에서 잰 각) → 순 법선력 = q·r·L·π → CN = r·L/R²(S = πR²). 내부 균일 압력은 합력 0.
        int nx = 21, nt = 37, nr = 3;
        var side = new double[nx * nt];
        for (int i = 0; i < nx; i++) for (int j = 0; j < nt; j++) side[i * nt + j] = Math.Cos(j * Math.PI / (nt - 1));
        var grid = new SurfaceFlowField.FieldGrid(side, new double[nr * nt], new double[nr * nt]);
        var ff = new SurfaceFlowField
        {
            Name = "analytic", Radius = 0.15, X0 = -1.51, X1 = 0, Nx = nx, Ntheta = nt, Nr = nr,
            Cases = [new SurfaceFlowField.FlowCase("G", "a90", 90, 0.5, 168, "test", true, new Dictionary<string, SurfaceFlowField.FieldGrid> { ["cp"] = grid }, [])],
        };
        var o = new ContainerAeroFea.Options { Flow = ff, Group = "G", Install = null!, WorkDir = "" };
        var mesh = ContainerAeroFea.Mesh(o);
        var map = ContainerAeroFea.Map(mesh, ff, "G", 90, o.Length, 15728);
        double rMid = o.OuterDiameter / 2 - o.Thickness / 2;
        Assert.InRange(map.CnMesh / (rMid * o.Length / (ff.Radius * ff.Radius)), 0.98, 1.01);
        // 보정 δ: 바람받이 반쪽 +δq 의 법선력 = q·δ·(2 r L) → LeewardArm = q·2rL(면 투영)
        Assert.InRange(map.LeewardArm / (15728 * 2 * rMid * o.Length), 0.99, 1.01);
    }

    [DesignFact]
    public void Structure_material_none_opts_out_and_design09_drone_sensor_use_named_materials()
    {
        var w = new List<string>();
        Assert.Null(StructureDef.Parse(new JsonObject { ["material"] = "none" }, "Steel", null, w, "pack"));
        // 설계 09 CAD 원본은 전 바디 기본 강철 — 드론·센서·팩이 '연강'으로 판정되던 문제(사용자 지적 2026-10-09)
        var doc = ScenarioDoc.Load(TestUtil.Scenario("09_munition_3petal_longcontainer_real.json"));
        var world = doc.Build();
        Assert.Equal("cfrp-qi", world.GetBody("drone1").Structure?.Material?.Key);
        Assert.Equal("al6061-t6", world.GetBody("sensor1").Structure?.Material?.Key);
        Assert.Null(world.GetBody("main_pack").Structure);
        Assert.Equal("C", world.GetBody("sensor1").Structure?.MaterialGrade);
        Assert.NotNull(MaterialLibrary.Find("17-4PH"));
        Assert.Equal(724e6, MaterialLibrary.Find("17-4ph-h1150")!.Yield, 3);
    }

    [Fact]
    public void Loads_outputs_are_registered_in_credibility_framework_with_grades()
    {
        var j = new JsonObject
        {
            ["schema"] = "mns.loads/1", ["scenario"] = "09_munition_3petal_longcontainer_real", ["mcRuns"] = 128, ["mcErrorRate"] = 0.0,
            ["nominalScalars"] = new JsonObject
            {
                ["containerG:drogue"] = 15.0, ["containerG:separation"] = 150.0, ["minMarginOpening"] = 3.0, ["minMarginOpeningPart"] = "부품 A",
                ["minMarginSeparation"] = 0.2, ["minMarginSeparationPart"] = "부품 B", ["latchForce:drogue"] = 600.0,
            },
            ["mcScalars"] = new JsonObject
            {
                ["containerG:drogue"] = new JsonObject { ["p05"] = 12.0, ["p95"] = 19.2 },
                ["containerG:separation"] = new JsonObject { ["p05"] = 15.0, ["p95"] = 250.0 },
                ["minMarginOpening"] = new JsonObject { ["p05"] = 2.1, ["p95"] = 3.5 },
                ["minMarginSeparation"] = new JsonObject { ["p05"] = 0.1, ["p95"] = 2.0 },
            },
            ["sensitivity"] = new JsonArray(new JsonObject { ["output"] = "containerG:separation", ["path"] = "bodies.nose.aero.scale", ["grade"] = "D", ["prcc"] = 0.87 }),
        };
        var outs = Sim.Core.Reliability.Credibility.AssessLoads(j, []);
        Assert.True(outs.Count >= 5);
        Assert.All(outs, o => Assert.Equal("09_munition_3petal_longcontainer_real", o.Scenario));
        Assert.All(outs, o => Assert.Equal(8, o.C.Factors.Count));
        var sep = outs.Single(o => o.C.Key == "loads:containerG:separation").C;
        Assert.Equal("추정", sep.Class);                  // 재접촉 모델은 근거 수준 0(가정 τ·노즈 형상) → 추정
        Assert.Equal(1, sep.Level("Input Pedigree"));    // 유의 입력이 D
        var drg = outs.Single(o => o.C.Key == "loads:containerG:drogue").C;
        Assert.InRange(drg.Lo90, 11.9, 12.1);
        Assert.InRange(drg.Hi90, 19.1, 19.3);
    }

    [DesignFact]
    public void Loads_study_writes_report_from_program_output()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mns_loads_rep_" + Guid.NewGuid().ToString("N"));
        try
        {
            var doc = ScenarioDoc.Load(TestUtil.Scenario("09_munition_3petal_longcontainer_real.json"));
            var res = LoadsStudy.Generate(doc, new LoadsStudyOptions
            {
                OutDir = dir, Runs = 3, PartsPath = TestUtil.DesignFile("config/models", "*_payload_parts.json"),   // 이름순 첫 파일 = 시나리오 09 설계
                Sweeps = [("bodies.nose.aero.scale", "노즈", [0.5])], EnvelopePath = null, MaterialResultsPath = null, Command = "mns loads (test)",
            });
            var md = File.ReadAllText(Path.Combine(dir, "README.md"));
            Assert.Contains("## 1. 3분할 힌지 반력 이상", md);
            Assert.Contains("## 8. 요약", md);
            Assert.Contains("래치 핀 17-4PH Ø8 (드론 4)", md);
            var j = J.ParseObject(File.ReadAllText(Path.Combine(dir, "loads.json")));
            Assert.Equal("mns.loads/1", J.Str(j, "schema", ""));
            Assert.Equal(3, res.Mc["latch_pin1"].Force.Count);
            // 드로그 창: 드론+센서 하중 = 질량 × 동적 G (F = m·a), 낙하산 개방은 준정적(증폭 < 1.3)
            var lp = res.Nominal.Parts.First(p => p.Part.Name == "latch_pin1");
            var dw = lp.Windows.First(x => x.Window == "drogue");
            Assert.InRange(dw.ForceN / (lp.Mass * dw.DynG * PayloadShock.G0), 0.999, 1.001);
            Assert.InRange(dw.Daf, 0.95, 1.3);
            Assert.InRange(lp.Mass, 3.9, 4.1);   // 드론 + 센서(설계 질량표)
            Assert.All(res.Nominal.World.Connectors.OfType<Hinge>(), h => Assert.True(h.MaxForceAvg < 20000));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
