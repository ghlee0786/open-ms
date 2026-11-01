using Sim.Core.Geometry;
using Sim.Core.Models;
using Sim.Core.Numerics;
using Sim.Core.Scenario;
using Sim.Core.Structures;

namespace Sim.Core.Tests;

/// <summary>하중·근사 응력(PRD W)·재질 거동(PRD X) 해석해 검증(FR-STR-06).</summary>
public class StructuresTests
{
    private static Body Beam(string section, string material = "al6061-t6", double r = 0.05, double l = 1.0, double t = 0.002, double mass = 10)
    {
        var shape = new Shape { Kind = ShapeKind.Cylinder, Radius = r, Length = l, Axis = 0 };
        var b = new Body { Name = "beam", OwnMass = mass, Shape = shape, OwnInertia = shape.UniformInertia(mass) };
        var def = new System.Text.Json.Nodes.JsonObject { ["material"] = material, ["section"] = section };
        if (section == "thinWall") def["thickness"] = t;
        b.Structure = StructureDef.Parse(def, null, shape, new List<string>(), "beam");
        return b;
    }

    private static StressResult Eval(StressModel m, params PointLoad[] loads) =>
        m.Evaluate(new StressInput(Vec3.Zero, 1.225, 0, Vec3.Zero, loads));

    [Fact]
    public void Free_free_beam_with_central_load_has_inertia_relief_moment_PL_over_8()
    {
        // 균일 질량 자유-자유 보 중앙 집중하중 P: 관성 완화 분포하중 P/L → M(중앙) = PL/4 − PL/8 = PL/8
        var b = Beam("solid");
        var m = StressModel.Build(b)!;
        double p = 1000, l = 1.0;
        var r = Eval(m, new PointLoad(new Vec3(0, 0, 0), new Vec3(0, 0, p), "test"));
        double mMax = r.BendingMoment.Max();
        Assert.InRange(mMax / (p * l / 8), 0.95, 1.05);
        // 자유단 평형(관성 완화)
        Assert.True(r.ResidualMoment < 1e-6 * p * l && r.ResidualForce < 1e-6 * p, $"잔차 F={r.ResidualForce} M={r.ResidualMoment}");
        // 중앙 단면 최대 굽힘 응력 σ = M r / (π r⁴/4)
        double sigma = p * l / 8 * 0.05 / (Math.PI * Math.Pow(0.05, 4) / 4);
        double sMax = r.Axial.Max();
        Assert.InRange(sMax / sigma, 0.85, 1.05);
        // 하중 반대쪽(+z 면)이 인장·압축 부호 대칭
        Assert.InRange(-r.Axial.Min() / sMax, 0.9, 1.1);
    }

    [Fact]
    public void Thin_wall_tube_section_inertia_is_pi_r3_t()
    {
        var b = Beam("thinWall", r: 0.1, t: 0.002);
        var m = StressModel.Build(b)!;
        int mid = m.Slices / 2;
        double expected = Math.PI * Math.Pow(0.1, 3) * 0.002;
        Assert.InRange(m.SecIu[mid] / expected, 0.97, 1.03);
        Assert.InRange(m.SecIv[mid] / expected, 0.97, 1.03);
        Assert.InRange(m.SecArea[mid] / (2 * Math.PI * 0.1 * 0.002), 0.98, 1.02);
    }

    [Fact]
    public void Uniform_flow_pressure_follows_modified_newtonian_and_stagnation_cp()
    {
        Assert.Equal(1.0, StressModel.StagnationCp(0), 12);
        Assert.InRange(StressModel.StagnationCp(0.5), 1.063, 1.065);          // 등엔트로피 정체점 Cp(M 0.5) = 1.0641
        Assert.Equal(1.0, StressModel.PressureCoefficient(1, 0), 12);
        Assert.Equal(0.25, StressModel.PressureCoefficient(0.5, 0), 12);
        Assert.Equal(StressModel.CpBase, StressModel.PressureCoefficient(-1, 0), 12);
        // 원통 축방향 흐름: 앞면(+x) 이 최대 압력 q·Cp0, 뒷면 기저압
        var b = Beam("solid");
        var m = StressModel.Build(b)!;
        double v = 100, rho = 1.225, q = 0.5 * rho * v * v;
        var r = m.Evaluate(new StressInput(new Vec3(v, 0, 0), rho, 0, Vec3.Zero, []));
        Assert.InRange(r.MaxPressure / q, 0.999, 1.0001);
        Assert.InRange(r.MinPressure / q, StressModel.CpBase - 1e-9, StressModel.CpBase + 1e-9);
        // 축 대칭 흐름 → 굽힘 없음, 축력만
        Assert.True(r.BendingMoment.Max() < 1e-6 * q * 0.01, $"M={r.BendingMoment.Max()}");
        Assert.True(Math.Abs(r.TotalAeroForce.X) > 0);
    }

    [Fact]
    public void Material_library_matches_fusion_names_and_rate_factors()
    {
        Assert.Equal("ss304", MaterialLibrary.Find("스테인리스스틸 AISI 304")!.Key);
        Assert.Equal("abs", MaterialLibrary.Find("ABS 플라스틱")!.Key);
        Assert.Equal("al6061-t6", MaterialLibrary.Find("Aluminum 6061")!.Key);
        Assert.Equal("al7075-t6", MaterialLibrary.Find("알루미늄 7075")!.Key);
        Assert.Equal("nylon66", MaterialLibrary.Find("나일론 6/6")!.Key);
        Assert.Equal("tinplate", MaterialLibrary.Find("양철")!.Key);
        Assert.Null(MaterialLibrary.Find("unobtainium"));
        Assert.True(MaterialLibrary.All.Count >= 12);
        Assert.All(MaterialLibrary.All, m => Assert.False(string.IsNullOrWhiteSpace(m.Source)));
        var al = MaterialLibrary.Find("al6061-t6")!;
        Assert.Equal(1 + 0.002 * Math.Log(1000), al.RateFactor(1000), 9);       // Johnson-Cook 속도 항
        Assert.Equal(1.0, al.RateFactor(0.1), 12);
        var abs = MaterialLibrary.Find("abs")!;
        Assert.Equal(1 + 0.06 * 3, abs.RateFactor(1.0), 9);                      // DIF: 1 + k·log10(1/1e-3)
    }

    [Fact]
    public void Unsupported_material_warns_and_skips_judgement()
    {
        var warns = new List<string>();
        var shape = new Shape { Kind = ShapeKind.Box, Size = new Vec3(0.3, 0.2, 0.2) };
        var def = StructureDef.Parse(new System.Text.Json.Nodes.JsonObject { ["material"] = "mithril" }, null, shape, warns, "x")!;
        Assert.Null(def.Material);
        Assert.Equal("unsupported", def.MaterialSource);
        Assert.Contains(warns, w => w.Contains("미지원 재질"));
        var b = new Body { Name = "x", OwnMass = 2, Shape = shape, OwnInertia = shape.UniformInertia(2), Structure = def };
        var r = Eval(StressModel.Build(b)!, new PointLoad(Vec3.Zero, new Vec3(0, 0, 500), "t"));
        Assert.All(r.State, s => Assert.Equal(FailureState.Unknown, s));
        // Fusion 재질명 자동 매칭
        var auto = StructureDef.Parse(null, "알루미늄 6061", shape, warns, "y")!;
        Assert.Equal("al6061-t6", auto.Material!.Key);
        Assert.Equal("fusion-auto", auto.MaterialSource);
    }

    [Fact]
    public void Brittle_plastic_fractures_while_ductile_metal_yields_under_same_load()
    {
        double p = 30000; // 중앙 하중 → σ = PL/8·r/I
        var abs = Beam("solid", "abs");
        var ra = Eval(StressModel.Build(abs)!, new PointLoad(Vec3.Zero, new Vec3(0, 0, p), "t"));
        double sigma = p / 8 * 0.05 / (Math.PI * Math.Pow(0.05, 4) / 4); // ≈ 30.6 MPa → ABS 43 MPa 미만이면 주의
        Assert.True(sigma < 43e6);
        var ra2 = Eval(StressModel.Build(abs)!, new PointLoad(Vec3.Zero, new Vec3(0, 0, p * 2), "t"));
        Assert.Equal(FailureState.Fracture, ra2.Worst);                                 // 61 MPa > 43 MPa → 파단(취성)
        Assert.DoesNotContain(FailureState.Yield, ra2.State);                           // 취성은 항복 단계 없음
        var al = Beam("solid", "al6061-t6");
        // 면 평가점(슬라이스 중앙)의 모멘트는 중앙 단면값의 ≈0.95 → 목표 305 MPa 이면 면 응력 ≈ 285 MPa
        var rl = Eval(StressModel.Build(al)!, new PointLoad(Vec3.Zero, new Vec3(0, 0, 305e6 / sigma * p), "t"));
        Assert.Equal(FailureState.Yield, rl.Worst);                                     // 276 ≤ σ < 310 → 항복(영구 변형)
        Assert.True(ra.Worst <= FailureState.Caution);
    }

    [Fact]
    public void Tinplate_tube_buckles_in_axial_compression_before_yield()
    {
        double rad = 0.15, len = 0.3, t = 0.00025;
        var shape = new Shape { Kind = ShapeKind.Cylinder, Radius = rad, Length = len, Axis = 0 };
        var b = new Body { Name = "can", OwnMass = 0.3, Shape = shape, OwnInertia = shape.UniformInertia(0.3) };
        b.Structure = StructureDef.Parse(new System.Text.Json.Nodes.JsonObject { ["material"] = "양철", ["thickness"] = t }, null, shape, new List<string>(), "can");
        var m = StressModel.Build(b)!;
        var mat = b.Structure!.Material!;
        double area = 2 * Math.PI * rad * t;
        double scr = StressModel.Buckling(shape, mat, t, area, 2 * Math.PI * Math.Pow(rad, 3) * t, 2 * rad);
        double phi = Math.Sqrt(rad / t) / 16, gamma = 1 - 0.901 * (1 - Math.Exp(-phi));
        Assert.Equal(gamma * mat.E * t / (rad * Math.Sqrt(3 * (1 - mat.Nu * mat.Nu))), scr, 3);
        Assert.True(scr < mat.Yield / 2, "얇은 양철 원통은 항복 훨씬 전에 좌굴");
        // 양 끝 압축(축력) 하중 — 단면 압축 응력 1.5 σ_cr (< 항복) → 좌굴(찌그러짐) 위험
        double f = scr * area * 1.5;
        var r = m.Evaluate(new StressInput(Vec3.Zero, 1.2, 0, Vec3.Zero,
            [new PointLoad(new Vec3(-len / 2, 0, 0), new Vec3(f, 0, 0), "a"), new PointLoad(new Vec3(len / 2, 0, 0), new Vec3(-f, 0, 0), "b")]));
        Assert.Contains(FailureState.Buckling, r.State);
        Assert.DoesNotContain(FailureState.Fracture, r.State);
    }

    [Fact]
    public void Tsai_wu_index_is_one_at_ply_tensile_strength()
    {
        var cf = MaterialLibrary.Find("cfrp-qi")!;
        var p = cf.Ply!;
        Assert.Equal(1.0, StressModel.TsaiWuMax(p, [0], p.Xt, 0), 9);
        Assert.Equal(1.0, StressModel.TsaiWuMax(p, [90], p.Yt, 0), 9);
        Assert.True(StressModel.TsaiWuMax(p, cf.Layup!, 100e6, 0) < 1);
    }

    [Fact]
    public void External_pressure_buckling_matches_formula()
    {
        double e = 70e9, nu = 0.33, t = 0.003, r = 0.2, l = 1.7;
        double k = 1 - nu * nu;
        double expected = 0.807 * e * t * t / (l * r) * Math.Pow(t * t / (r * r * k * k * k), 0.25);
        Assert.Equal(expected, StressModel.ExternalPressureBuckling(e, nu, t, r, l), 6);
    }

    [Fact]
    public void Structural_analysis_of_split_template_reports_bodies_and_appends_events()
    {
        var doc = ScenarioDoc.Load(TestUtil.Scenario("06_munition_3petal_split.json"));
        doc.Root["sim"]!["tEnd"] = 2.0;
        var w = doc.Build();
        w.Run();
        var rep = StructuralAnalysis.Analyze(w, maxFrames: 60);
        Assert.Contains(rep.Bodies, h => h.Body.Name == "petal1");
        Assert.Contains(rep.Bodies, h => h.Body.Name == "c1");
        var c1 = rep.Bodies.First(h => h.Body.Name == "c1");
        Assert.True(c1.PeakVm > 0 && c1.MaxPressure > 1000);       // M 0.5 동압 ≈ 15.7 kPa 근처의 정체압
        Assert.True(c1.MaxResidualRatio < 1e-6, $"평형 잔차 {c1.MaxResidualRatio}");
        int before = w.EventLog.Count;
        StructuralAnalysis.AppendEvents(w, rep);
        Assert.True(w.EventLog.Count >= before);
        for (int i = 1; i < w.EventLog.Count; i++) Assert.True(w.EventLog[i].Time >= w.EventLog[i - 1].Time);
        var j = rep.ToJson();
        Assert.Equal("mns.structural/1", j["schema"]!.GetValue<string>());
    }
}
