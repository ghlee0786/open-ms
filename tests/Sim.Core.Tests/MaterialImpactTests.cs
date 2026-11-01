using System.Text.Json.Nodes;
using Sim.Core.Geometry;
using Sim.Core.Numerics;
using Sim.Core.Scenario;
using Sim.Core.Structures;

namespace Sim.Core.Tests;

/// <summary>
/// 착지 충돌 재질 판정(1·2단계, ImpactDamage): 공식 해석해·에너지 보존·임계 속도·단조성·접촉 분류·시나리오 08(플라스틱 깨짐 / 양철 찌그러짐).
/// </summary>
public class MaterialImpactTests
{
    private static readonly Shape Box = new() { Kind = ShapeKind.Box, Size = new Vec3(0.40, 0.27, 0.27) };
    private static Quat Deg(double r, double p, double y) => Quat.FromEuler(r * Math.PI / 180, p * Math.PI / 180, y * Math.PI / 180);
    private static readonly Quat Corner = Deg(45, 35.264, 0);

    private static StructureDef Def(string mat, double? t, Shape? s = null, string? section = null)
    {
        var o = new JsonObject { ["material"] = mat };
        if (t is { } tv) o["thickness"] = tv;
        if (section is not null) o["section"] = section;
        return StructureDef.Parse(o, null, s ?? Box, new List<string>(), "b")!;
    }

    /// <summary>속도 효과 없는 연성/취성 시험 재질(임계값 경계를 정확히 시험).</summary>
    private static Material Plain(bool brittle) => MaterialLibrary.Parse(new JsonObject
    {
        ["key"] = brittle ? "test-brittle" : "test-ductile", ["brittle"] = brittle, ["density"] = 2000, ["E_GPa"] = 10, ["nu"] = 0.3,
        ["yield_MPa"] = 50, ["ultimate_MPa"] = 60, ["elongation"] = 0.2,
    });

    private static ImpactAssessment Hit(Material m, double t, double v, Quat q, double mass = 5.5) =>
        ImpactDamage.Assess("b", m, new StructureDef { Material = m, Thickness = t, Section = "thinWall" }, Box, mass, new Vec3(0, 0, v), q);

    [Fact]
    public void Crush_formulas_match_closed_form()
    {
        double s0 = 300e6, t = 0.001, b = 0.2, d = 0.1;
        Assert.Equal(9.56 * s0 * Math.Pow(t, 5.0 / 3) * Math.Pow(b, 1.0 / 3), ImpactDamage.SquareTubeMeanCrush(s0, t, b), 9);
        Assert.Equal(6 * s0 * Math.Pow(t, 1.5) * Math.Sqrt(d), ImpactDamage.TubeAxialMeanCrush(s0, t, d), 9);
        Assert.Equal(s0 * t * t * 0.3 / 0.05, ImpactDamage.RingCrush(s0, t, 0.3, 0.05), 9);
        Assert.Equal(0.983 * Math.Cbrt(t) * Math.Pow(b, 2.0 / 3), ImpactDamage.FoldHalfLength(t, b), 12);
    }

    [Theory]
    [InlineData(0, 0, 0, ContactKind.Face)]
    [InlineData(45, 35.264, 0, ContactKind.Corner)]
    [InlineData(45, 0, 0, ContactKind.Edge)]
    [InlineData(0, 90, 0, ContactKind.Face)]
    public void Box_contact_classification(double r, double p, double y, ContactKind expected)
    {
        var g = ImpactDamage.Contact(Box, Deg(r, p, y).InverseRotate(Vec3.UnitZ));
        Assert.Equal(expected, g.Kind);
    }

    [Theory]
    [InlineData(0, -90, ContactKind.End)]
    [InlineData(0, 0, ContactKind.Side)]
    [InlineData(0, -45, ContactKind.Rim)]
    public void Cylinder_contact_classification(double r, double p, ContactKind expected)
    {
        var cyl = new Shape { Kind = ShapeKind.Cylinder, Radius = 0.05, Length = 0.3, Axis = 0 };
        Assert.Equal(expected, ImpactDamage.Contact(cyl, Deg(r, p, 0).InverseRotate(Vec3.UnitZ)).Kind);
    }

    [Fact]
    public void Ductile_dent_absorbs_exactly_the_energy_beyond_elastic_capacity()
    {
        var a = Hit(Plain(false), 0.0005, 6, Corner);
        Assert.Equal(FailureState.Yield, a.State);
        Assert.Equal(a.Energy, a.ElasticCapacity + a.CrushForce * a.DentDepth, 9);   // 에너지 보존(압궤 한계 안)
        Assert.Equal(0.5 * 5.5 * 36, a.Energy, 9);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Damage_onset_speed_brackets_the_state_change(bool brittle)
    {
        var m = Plain(brittle);
        var probe = Hit(m, 0.002, 3, Corner);
        double vc = probe.DamageSpeed;
        Assert.True(vc > 0 && double.IsFinite(vc));
        var below = Hit(m, 0.002, vc * (1 - 1e-6), Corner);
        var above = Hit(m, 0.002, vc * (1 + 1e-6), Corner);
        Assert.True(below.State <= FailureState.Caution, $"아래 {below.State}");
        Assert.Equal(brittle ? FailureState.Fracture : FailureState.Yield, above.State);
    }

    [Fact]
    public void Damage_is_monotone_in_speed_and_thickness()
    {
        var tin = MaterialLibrary.Find("tinplate")!;
        FailureState prev = FailureState.Unknown; double prevDent = -1;
        for (double v = 0.2; v <= 30; v *= 1.25)
        {
            var a = Hit(tin, 0.0005, v, Corner);
            Assert.True(a.State >= prev, $"v={v}: {a.State} < {prev}");
            Assert.True(a.DentDepth >= prevDent - 1e-12, $"v={v}: dent {a.DentDepth} < {prevDent}");
            prev = a.State; prevDent = a.DentDepth;
        }
        double last = double.PositiveInfinity;
        foreach (var t in new[] { 0.0002, 0.0005, 0.001, 0.002, 0.004 })
        {
            var a = Hit(tin, t, 7, Corner);
            Assert.True(a.DentDepth <= last + 1e-12, $"t={t}: dent {a.DentDepth} > {last}");
            last = a.DentDepth;
        }
    }

    [Fact]
    public void Plastic_breaks_tin_dents_same_box_same_drop()
    {
        var abs = Hit(MaterialLibrary.Find("ABS 플라스틱")!, 0.002, 7.2, Corner);
        var tin = Hit(MaterialLibrary.Find("양철")!, 0.00025, 7.2, Corner);
        Assert.Equal(FailureState.Fracture, abs.State);
        Assert.Contains("깨짐", abs.Verdict);
        Assert.Equal(FailureState.Yield, tin.State);
        Assert.Contains("찌그러짐", tin.Verdict);
        Assert.True(tin.DentDepth > 0.01);
        // 같은 판정이 아주 느린 착지에서는 손상 없음
        Assert.True(Hit(MaterialLibrary.Find("abs")!, 0.002, 1.0, Corner).State <= FailureState.Caution);
    }

    [Fact]
    public void Rate_effect_raises_capacity_and_is_reported()
    {
        var abs = MaterialLibrary.Find("abs")!;
        var a = Hit(abs, 0.002, 7, Corner);
        Assert.True(a.RateFactor > 1 && a.RateFactor < 3);
        Assert.Contains("정적 강도면 용량", a.Method);
        var slow = Hit(abs, 0.002, 0.5, Corner);
        Assert.True(slow.RateFactor < a.RateFactor);
    }

    [Fact]
    public void Solid_section_uses_wave_stress_and_taylor_shortening()
    {
        var al = MaterialLibrary.Find("al6061-t6")!;
        var rod = new Shape { Kind = ShapeKind.Cylinder, Radius = 0.01, Length = 0.1, Axis = 0 };
        var def = new StructureDef { Material = al, Section = "solid" };
        var q = Deg(0, -90, 0);                                 // 축(x) 수직, 끝면 착지
        var a = ImpactDamage.Assess("rod", al, def, rod, 0.1, new Vec3(0, 0, 10), q);
        double c = Math.Sqrt(al.E / al.Density);
        Assert.Equal(al.Density * c * 10, a.PeakStress, 6);    // 강체 지면 ρcv (136 MPa < σy)
        Assert.Equal(FailureState.Safe, a.State);
        var fast = ImpactDamage.Assess("rod", al, def, rod, 0.1, new Vec3(0, 0, 150), q);
        Assert.Equal(FailureState.Yield, fast.State);
        double ratio = Fea.RadiossDeck.TaylorFinalLengthRatio(al.Density, 150, al.Yield * fast.RateFactor);
        Assert.Equal(0.1 * (1 - ratio), fast.Shortening, 9);
        // 지면이 낼 수 있는 응력이 작으면(무른 흙) 그 값이 상한
        var soft = ImpactDamage.Assess("rod", al, def, rod, 0.1, new Vec3(0, 0, 150), q, groundStressLimit: 20e6);
        Assert.Equal(20e6, soft.PeakStress, 6);
        Assert.Equal(FailureState.Safe, soft.State);
    }

    [Fact]
    public void Degenerate_inputs_are_rejected_or_harmless()
    {
        var abs = MaterialLibrary.Find("abs")!;
        var def = new StructureDef { Material = abs, Thickness = 0.002, Section = "thinWall" };
        Assert.Throws<InvalidDataException>(() => ImpactDamage.Assess("b", abs, def, Box, 0, new Vec3(0, 0, 5), Quat.Identity));
        Assert.Throws<InvalidDataException>(() => ImpactDamage.Assess("b", abs, def, Box, 1, new Vec3(0, 0, double.NaN), Quat.Identity));
        var up = ImpactDamage.Assess("b", abs, def, Box, 1, new Vec3(3, 0, -2), Quat.Identity); // 위로 움직이며 닿음 → 법선 에너지 0
        Assert.Equal(0, up.Energy);
        Assert.True(up.State <= FailureState.Safe);
        var huge = Hit(MaterialLibrary.Find("tinplate")!, 0.00025, 300, Corner);
        Assert.Equal(FailureState.Fracture, huge.State);
        Assert.True(double.IsFinite(huge.DentDepth) && huge.DentDepth <= huge.CrushLimit + 1e-12);
    }

    [Fact]
    public void Mirror_attitude_gives_same_verdict_and_mirrored_contact()
    {
        var abs = MaterialLibrary.Find("abs")!;
        var a = Hit(abs, 0.002, 7, Deg(45, 35.264, 0));
        var b = Hit(abs, 0.002, 7, Deg(-45, 35.264, 0));
        Assert.Equal(a.State, b.State);
        Assert.Equal(a.Energy, b.Energy, 12);
        Assert.Equal(a.ElasticCapacity, b.ElasticCapacity, 9);
        Assert.Equal(a.ContactPointBrf.X, b.ContactPointBrf.X, 12);
        Assert.Equal(a.ContactPointBrf.Y, -b.ContactPointBrf.Y, 12);
    }

    [Fact]
    public void Scenario_08_material_comparison_end_to_end()
    {
        var doc = ScenarioDoc.Load(TestUtil.Scenario("08_material_drop_compare.json"));
        var w = doc.Build();
        w.Run();
        var rep = StructuralAnalysis.Analyze(w);
        StructuralAnalysis.AppendEvents(w, rep);
        FailureState S(string n) => rep.Bodies.Single(h => h.Body.Name == n).Impact!.State;
        Assert.Equal(FailureState.Fracture, S("abs_corner"));
        // 면 착지도 국부 파단 — OpenCourant 전체 메시 해석(삭제 0.5%)과 같은 범주(선형 응력 분포 용량)
        Assert.Equal(FailureState.Fracture, S("abs_face"));
        Assert.True(rep.Bodies.Single(h => h.Body.Name == "abs_face").Impact!.Energy
                    < 3 * rep.Bodies.Single(h => h.Body.Name == "abs_face").Impact!.ElasticCapacity);   // 면은 경계 근처(꼭짓점보다 여유 큼)
        Assert.Equal(FailureState.Fracture, S("pla_corner"));
        Assert.Equal(FailureState.Yield, S("pc_corner"));      // 질긴 플라스틱: 깨지지 않고 찌그러짐
        Assert.Equal(FailureState.Yield, S("tin_face"));
        Assert.Equal(FailureState.Yield, S("tin_corner"));
        Assert.Equal(FailureState.Fracture, S("cfrp_corner"));
        Assert.True(S("al_face") <= FailureState.Yield);
        Assert.True(S("al_sensor_solid") <= FailureState.Caution);
        Assert.True(S("abs_puck_solid") <= FailureState.Caution);
        Assert.Equal(FailureState.Yield, S("epp_block"));
        // 이벤트 기록·면 색(접촉부 면이 판정 상태로 칠해짐)·JSON
        Assert.Contains(w.EventLog, e => e.Name == "abs_corner.impactDamage" && e.Message.Contains("깨짐"));
        var h = rep.Bodies.Single(x => x.Body.Name == "abs_corner");
        Assert.Contains(FailureState.Fracture, h.WorstState);
        Assert.True(h.WorstState.Count(s => s == FailureState.Fracture) < h.WorstState.Length / 2); // 국부(접촉부)만
        Assert.True(double.IsFinite(h.FirstFractureTime));
        // 면 착지: 바닥(+z) 쪽 판만 칠하고 뚜껑(−z)은 안 칠함, 옆벽 아래쪽 포함
        var hf = rep.Bodies.Single(x => x.Body.Name == "abs_face");
        var painted = Enumerable.Range(0, hf.WorstState.Length).Where(i => hf.WorstState[i] == FailureState.Fracture).Select(i => hf.Model.Centroid[i]).ToList();
        Assert.NotEmpty(painted);
        Assert.All(painted, c => Assert.True(c.Z > 0, $"뚜껑 쪽 칠함 {c}"));
        Assert.Contains(painted, c => Math.Abs(Math.Abs(c.Y) - 0.135) < 1e-6 && c.Z < 0.13); // 옆벽 아래 띠
        Assert.Equal("Fracture", rep.ToJson()["bodies"]!["abs_corner"]!["impact"]!["state"]!.GetValue<string>());
    }

    [Fact]
    public void Impact_assessment_survives_archive_roundtrip()
    {
        var doc = ScenarioDoc.Load(TestUtil.Scenario("08_material_drop_compare.json"));
        var w = doc.Build();
        w.Run();
        var rep = StructuralAnalysis.Analyze(w);
        var path = Path.Combine(Path.GetTempPath(), "mns_mat", Guid.NewGuid().ToString("N")[..8], "r.mnsr");
        Analysis.ResultArchive.Save(path, doc, w, null, rep.ToJson());
        var r = Analysis.ResultArchive.Load(path);
        Assert.Equal("Fracture", r.Structural!["bodies"]!["abs_corner"]!["impact"]!["state"]!.GetValue<string>());
        // 불러온 월드에서 다시 판정해도 같은 결과(착지 기록 복원)
        var again = ImpactDamage.Assess(r.World.GetBody("tin_face"), r.World.Env.Gravity)!;
        var orig = rep.Bodies.Single(x => x.Body.Name == "tin_face").Impact!;
        Assert.Equal(orig.State, again.State);
        Assert.Equal(orig.DentDepth, again.DentDepth, 9);
    }
}
