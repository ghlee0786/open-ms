using Sim.Core.Analysis;
using Sim.Core.Engine;
using Sim.Core.Fea;
using Sim.Core.Geometry;
using Sim.Core.Numerics;
using Sim.Core.Scenario;
using Sim.Core.Structures;

namespace Sim.Core.Tests;

/// <summary>
/// 순응 접촉(collisions.contact.model = compliant) 검산 — 해석해 대비: 질량–스프링 반 사인(지속 π√(m/k)·최대 v√(km)), 반발 e 재현,
/// 자유-자유 봉·보 관성 완화(끝 하중 L/(3EA) · L³/(105EI) · 끝 모멘트 13L/(35EI)), 보 끝 충격(직렬 강성 → 지속), 운동량 교환 = 충격량 모델, BaseAccel 면적 = Δv.
/// </summary>
public class CollisionComplianceTests
{
    [Fact]
    public void Beam_relief_matches_free_free_analytic_compliances()
    {
        Assert.Equal(1.0 / 3, BeamRelief.Bar(1), 4);
        Assert.Equal(1.0 / 3, BeamRelief.Bar(0), 4);
        Assert.Equal(1.0 / 12, BeamRelief.Bar(0.5), 4);        // 가운데 하중: u(½) − ū = 1/8 − 1/24
        Assert.Equal(1.0 / 105, BeamRelief.Bending(1), 5);
        Assert.Equal(1.0 / 105, BeamRelief.Bending(0), 5);
        Assert.Equal(13.0 / 35, BeamRelief.Moment(1), 4);
        Assert.Equal(13.0 / 35, BeamRelief.Moment(0), 4);
        Assert.True(BeamRelief.Bending(0.5) < BeamRelief.Bending(1));   // 가운데는 끝보다 단단하다
    }

    [Fact]
    public void Undamped_contact_is_the_mass_spring_half_sine()
    {
        var mp = ContactPulse.Master(1.0);
        Assert.Equal(Math.PI, mp.TEnd, 4);
        Assert.Equal(1.0, mp.PeakF, 4);
        for (int i = 0; i < mp.T.Length; i += 97) Assert.Equal(Math.Sin(mp.T[i]), mp.F[i], 4);
        // 차원: m 10 kg, k 1e7 N/m, v 1.5 m/s → τ = π√(m/k), F_max = v√(km), 면적 = 1
        double m = 10, k = 1e7, v = 1.5;
        var el = new ContactElement("a↔b", "spring", 1 / k, double.NaN, true, "test");
        var p = ContactPulse.Build([el], m, v, 1.0, 1, 1e-4);
        Assert.Equal(1.0, p.Duration / (Math.PI * Math.Sqrt(m / k)), 6);
        Assert.Equal(1.0, p.PeakForce / (v * Math.Sqrt(k * m)), 6);
        Assert.Equal(1.0, Integral(p), 3);
        // 배율 4 → 지속 ½
        Assert.Equal(0.5, p.Rescaled(4).Duration / p.Duration, 9);
    }

    [Fact]
    public void Damped_contact_reproduces_restitution_and_impulse()
    {
        foreach (var e in new[] { 0.3, 0.6 })
        {
            var mp = ContactPulse.Master(e);
            Assert.True(Math.Abs(mp.ReboundError) < 1e-4, $"e {e}: 되튐 오차 {mp.ReboundError}");
            // 무차원 충격량 ∫F̃ dt̃ = 1 + e(운동량 교환이 충격량 모델 (1+e)·m·v 와 같다)
            double s = 0;
            for (int i = 1; i < mp.T.Length; i++) s += 0.5 * (mp.F[i] + mp.F[i - 1]) * (mp.T[i] - mp.T[i - 1]);
            Assert.Equal(1 + e, s, 3);
            Assert.Equal(0.0, mp.F[0], 12);   // 침투 비례 감쇠: 시작 계단 없음
            Assert.Equal(0.0, mp.F[^1], 12);
        }
    }

    [Fact]
    public void Beam_end_impact_uses_free_free_relief_and_series_spring()
    {
        // 얇은 관(Al6061 R 50 mm t 2 mm L 1 m, 2 kg) 끝에 횡하중: 보 굽힘 요소 = L³/(105EI), 축하중: 봉 요소 = L/(3EA)
        var mat = MaterialLibrary.Find("al6061-t6")!;
        var shape = new Shape { Kind = ShapeKind.Cylinder, Radius = 0.05, Length = 1.0, Axis = 0, Center = Vec3.Zero };
        var s = new ContactStructure { Body = "tube", Kind = "tube", Shape = shape, Material = mat, Thickness = 0.002, Mass = 2 };
        double Rm = 0.049, I = Math.PI * Rm * Rm * Rm * 0.002, A = 2 * Math.PI * Rm * 0.002;
        var tip = new Vec3(0.5, 0, 0.05);
        var lat = s.Elements(tip, new Vec3(0, 0, 1), 0.002);
        Assert.Equal(1.0 / (105 * mat.E * I), lat.Single(x => x.Kind.StartsWith("보 굽힘")).Compliance, 1e-3 / (105 * mat.E * I));
        Assert.Contains(lat, x => x.Kind.StartsWith("국부 판 굽힘"));
        var ax = s.Elements(tip, new Vec3(1, 0, 0), 0.002);
        Assert.Equal(1.0 / (3 * mat.E * A), ax.Single(x => x.Kind.StartsWith("봉 축")).Compliance, 1e-3 / (3 * mat.E * A));
        Assert.Contains(ax, x => x.Kind.StartsWith("편심 축하중"));   // 관 벽(축에서 R)에 걸린 축하중 → 보 굽힘
        // 질량 m 이 관 끝을 친다(무감쇠): 직렬 k = 1/ΣC(포함 경로), 지속 = π√(m/k) — 무거운 질량(느린 접촉)이면 보 굽힘 모드도 포함
        var local = ContactStructure.Local(s, null).Element;
        var els = new List<ContactElement> { local };
        els.AddRange(lat);
        var p = ContactPulse.Build(els, 50, 1.0, 1.0, 1, 1e-5);
        double c = p.Elements.Where(x => x.Included).Sum(x => x.Compliance);
        Assert.Equal(1.0, p.Duration / (Math.PI * Math.Sqrt(50 * c)), 6);
        Assert.Contains(p.Elements, x => x.Kind.StartsWith("보 굽힘") && x.Included);
        // 가벼운 질량(빠른 접촉)이면 느린 보 굽힘 모드는 빠진다(질량처럼 거동) — 더 단단
        var q = ContactPulse.Build(els, 1e-3, 1.0, 1.0, 1, 1e-5);
        Assert.Contains(q.Elements, x => x.Kind.StartsWith("보 굽힘") && !x.Included);
        Assert.True(q.K > q.KQuasiStatic);
        Assert.Equal(1.0, p.KQuasiStatic * els.Sum(x => x.Compliance), 9);
    }

    [Fact]
    public void Shell_segment_axial_corner_load_has_membrane_eccentric_and_torsion_paths()
    {
        var mat = MaterialLibrary.Find("al7075-t6")!;
        var seg = Shape.ShellSegmentOf(0.18, 0.012, 1.7, 120, 330, 0, new Vec3(0.01525, 0, 0));
        var s = new ContactStructure { Body = "petal", Kind = "panel", Shape = seg, Material = mat, Thickness = 0.012, Mass = 27.0 };
        // 뒤끝(x = −0.835) 옆 모서리(30°) — 외피 조각 뒤끝 접촉점 예, 법선 = 축 + 약간 반경
        double th = 30 * Math.PI / 180, r = 0.174;
        var p = new Vec3(-0.835 + 0.01525 - 0.01525, r * Math.Cos(th), r * Math.Sin(th));
        var els = s.Elements(p, new Vec3(1, 0.05, 0.1).Normalized(), 0.003);
        Assert.Contains(els, x => x.Kind.StartsWith("막 퍼짐") && x.Formula.Contains("옆 변"));
        Assert.Contains(els, x => x.Kind.StartsWith("편심 축하중"));
        Assert.Contains(els, x => x.Kind.StartsWith("비틀림"));
        Assert.All(els, x => Assert.True(x.Compliance > 0 && double.IsFinite(x.Compliance)));
        // 열린 단면 전단 중심: 120° 원호 e = 2R(sinβ − βcosβ)/(β − sinβcosβ) = 1.115 R(볼록면 쪽) — 중앙선 반경 하중은 비틀림 없음
        var radial = s.Elements(new Vec3(0, r * Math.Cos(-th), r * Math.Sin(-th)), new Vec3(0, Math.Cos(-th), Math.Sin(-th)), 0.003);
        Assert.DoesNotContain(radial, x => x.Kind.StartsWith("비틀림"));
    }

    private const string TwoBoxes = """
    {
      "schema": "mns.scenario/1", "name": "two_boxes",
      "sim": { "dt": 0.0005, "tEnd": 0.3, "outputRate": 100 },
      "environment": { "vacuum": true },
      "bodies": [
        { "name": "a", "position": [0, 0, -500], "velocity": [2, 0, 0], "mass": 5.0, "shape": { "type": "sphere", "radius": 0.15 },
          "structure": { "material": "al6061-t6", "thickness": 0.004 } },
        { "name": "b", "position": [0.5, 0, -500], "velocity": [0, 0, 0], "mass": 5.0, "shape": { "type": "sphere", "radius": 0.15 },
          "structure": { "material": "al6061-t6", "thickness": 0.004 } }
      ],
      "collisions": { "restitution": 0.3, "friction": 0, "parachutes": false, "contact": { "model": "compliant" } }
    }
    """;

    [Fact]
    public void Compliant_contact_exchanges_the_same_momentum_as_the_impulse_model()
    {
        var wc = ScenarioDoc.Parse(TwoBoxes, TestUtil.RepoRoot).Build();
        wc.Run();
        var di = ScenarioDoc.Parse(TwoBoxes, TestUtil.RepoRoot);
        di.Root["collisions"]!["contact"]!["model"] = "impulse";
        var wi = di.Build();
        wi.Run();
        Assert.True(wc.Collisions!.Compliant);
        var rc = wc.Collisions.Records.First(r => r.Kind == "contact");
        Assert.NotNull(rc.Pulse);
        Assert.Null(wi.Collisions!.Records.First(r => r.Kind == "contact").Pulse);
        var p = rc.Pulse!;
        Assert.Equal("sphereShell", p.SideA.S!.Kind);
        Assert.Contains(p.Elements, x => x.Kind.StartsWith("구 셸 점하중"));
        Assert.Equal(2.5, p.MEff, 6);                                 // 같은 질량 둘 — 정면(회전 팔 0)
        Assert.Equal(rc.Impulse, (1 + 0.3) * p.MEff * p.Vn, 6);       // ∫F dt = (1+e)·m·v = 충격량 모델
        Assert.Equal(1.0, Integral(p), 3);
        Assert.Equal(wi.GetBody("a").V.X, wc.GetBody("a").V.X, 9);    // 궤적 동일
        Assert.Equal(1.3, wc.GetBody("b").V.X, 6);
        Assert.Contains(wc.EventLog, e => e.Message.Contains("순응 접촉"));
        Assert.NotNull(wc.Collisions.ToJson()["pairs"]![0]!["first"]!["compliant"]);
    }

    [Fact]
    public void Base_accel_area_equals_point_velocity_change_for_pulse_and_tau()
    {
        var pr = new ShockProbe { RootName = "r" };
        for (int i = 0; i <= 200; i++)
        {
            pr.T.Add(i * 1e-3); pr.F.Add(Vec3.Zero); pr.W.Add(Vec3.Zero); pr.Alpha.Add(Vec3.Zero); pr.Cg.Add(Vec3.Zero); pr.Mass.Add(10); pr.Air.Add(Vec3.Zero);
        }
        var el = new ContactElement("a↔b", "spring", 1 / 2e6, double.NaN, true, "test");
        var pulse = ContactPulse.Build([el], 5, 1.2, 0.3, 1, 1e-4);
        pr.Impulses.Add((0.05, new Vec3(0.8, 0, 0), Vec3.Zero, "x", 8, Vec3.Zero, pulse));
        double dt = 5e-5;
        var (x, _, _) = PayloadShock.BaseAccel(pr, Vec3.Zero, 0, 0.2, 0.002, dt);
        Assert.Equal(0.8, x.Sum() * dt, 6);
        // 펄스 모양 그대로: 최대 ≈ Δv·max(F/J)
        Assert.Equal(0.8 * pulse.Shape.Max(), x.Max(), 0.8 * pulse.Shape.Max() * 0.02);
        // 충격량 모델(펄스 무시) → 반 사인 τ: 최대 = (π/2)·Δv/τ
        var (xt, _, _) = PayloadShock.BaseAccel(pr, Vec3.Zero, 0, 0.2, 0.002, dt, _ => null);
        Assert.Equal(0.8, xt.Sum() * dt, 2);
        Assert.Equal(Math.PI / 2 * 0.8 / 0.002, xt.Max(), 1.0);
    }

    [Fact]
    public void Loguniform_dispersion_spans_ratio_symmetrically()
    {
        var d = new DispersionDef { Path = "collisions.contact.stiffnessScale", Dist = "loguniform", Mode = "set", Min = 0.5, Max = 2 };
        Assert.Equal(0.5, d.Sample(1e-15), 6);
        Assert.Equal(2.0, d.Sample(1 - 1e-15), 6);
        Assert.Equal(1.0, d.Sample(0.5), 9);
        Assert.Contains("logU", Reliability.UqEngine.DistText(d));
    }

    [Fact]
    public void Contact_fea_rigid_motion_is_removed_and_cluster_map_is_monotone()
    {
        // 강체 병진 + 회전만 있는 변위장 → 탄성 컴플라이언스 0
        var coords = new Dictionary<int, Vec3>(); var disp = new Dictionary<int, Vec3>();
        var rot = new Vec3(0.01, -0.02, 0.005); var tr = new Vec3(1e-3, 2e-3, -1e-3);
        int id = 1;
        for (int i = 0; i < 5; i++) for (int j = 0; j < 5; j++) for (int k = 0; k < 3; k++)
        {
            var q = new Vec3(i * 0.1, j * 0.07, k * 0.05);
            coords[id] = q; disp[id] = tr + Vec3.Cross(rot, q); id++;
        }
        Assert.Equal(0.0, ContactFea.ElasticCompliance(coords, disp, new Vec3(0.2, 0.14, 0.05), Vec3.UnitX, 0.02, 1000), 12);
        double prev = -1;
        for (int i = 0; i <= 40; i++)
        {
            double v = ContactFea.Cluster(i / 40.0, 0.3, 0.1);
            Assert.True(v > prev);
            prev = v;
        }
        Assert.Equal(0.0, ContactFea.Cluster(0, 0.3, 0.1), 12);
        Assert.Equal(1.0, ContactFea.Cluster(1, 0.3, 0.1), 12);
    }

    [Fact]
    public void Compliant_loads_output_uses_compliant_evidence_and_stays_estimate()
    {
        var j = new System.Text.Json.Nodes.JsonObject
        {
            ["schema"] = "mns.loads/1", ["scenario"] = "11_example", ["mcRuns"] = 128, ["mcErrorRate"] = 0.0,
            ["contactModel"] = new System.Text.Json.Nodes.JsonObject { ["model"] = "compliant" },
            ["nominalScalars"] = new System.Text.Json.Nodes.JsonObject { ["containerG:separation"] = 130.0, ["minMarginSeparation"] = 0.4, ["minMarginSeparationPart"] = "스탠드오프" },
            ["mcScalars"] = new System.Text.Json.Nodes.JsonObject { ["containerG:separation"] = new System.Text.Json.Nodes.JsonObject { ["p05"] = 60.0, ["p95"] = 190.0 } },
            ["sensitivity"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
                { ["output"] = "containerG:separation", ["path"] = "collisions.contact.stiffnessScale", ["grade"] = "D", ["prcc"] = 0.9 }),
        };
        var sep = Reliability.Credibility.AssessLoads(j, []).Single(o => o.C.Key == "loads:containerG:separation").C;
        Assert.Equal(Reliability.Credibility.CompliantSeparationModel, sep.Model);
        Assert.Equal("추정", sep.Class);                       // 반발·분리 Δv·힌지가 여전히 가정(D) — 개발 자료 수준 0
        Assert.Equal(1, sep.Level("Input Pedigree"));          // 유의 입력 = 강성 배율(D)
        Assert.Contains("강성 배율", sep.Factors.Single(f => f.Factor == "Uncertainty Characterization").Evidence);
        Assert.Contains("CollisionComplianceTests", sep.Factors.Single(f => f.Factor == "Verification").Evidence);
    }

    private static double Integral(ContactPulse p)
    {
        double s = 0;
        for (int i = 1; i < p.T.Length; i++) s += 0.5 * (p.Shape[i] + p.Shape[i - 1]) * (p.T[i] - p.T[i - 1]);
        return s;
    }
}
