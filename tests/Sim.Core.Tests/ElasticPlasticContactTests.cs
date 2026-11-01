using Sim.Core.Engine;
using Sim.Core.Geometry;
using Sim.Core.Numerics;
using Sim.Core.Scenario;
using Sim.Core.Structures;

namespace Sim.Core.Tests;

/// <summary>
/// 탄성-소성 순응 접촉(collisions.contact.plasticity = elastoplastic) 검산 — 문헌·해석해 대비:
/// Thornton(1997) 탄성-완전소성 구 반발 닫힌 식(같은 적분 방식 — RK4 단계 안 소성 상태 고정·단계 뒤 갱신), 직렬 선형 스프링 완전 소성 e = v_y/v ·
/// 선형 경화 에너지 해, 에너지 균형 ½(1 − e²) = 소성 일, 탄성 한계 아래면 탄성 펄스와 같은 수, 6DOF 운동량 교환 불변, 벽 압괴 = ImpactDamage 식.
/// </summary>
public class ElasticPlasticContactTests
{
    [Theory]
    [InlineData(1.5)]
    [InlineData(3)]
    [InlineData(10)]
    [InlineData(40)]
    public void Thornton_sphere_restitution_matches_closed_form(double vOverVy)
    {
        // Al 구(R 5 mm) — E* 39 GPa, 한계 압력 p_y 1 GPa(값은 임의 — 닫힌 식은 V_y/V 만의 함수)
        double eStar = 39e9, r = 0.005, py = 1.0e9, m = 4.0 / 3 * Math.PI * r * r * r * 2700;
        double vy = ThorntonSphere.YieldVelocity(eStar, r, py, m);
        var (e, dp, _) = ThorntonSphere.Integrate(eStar, r, py, m, vOverVy * vy);
        Assert.Equal(ThorntonSphere.Restitution(1 / vOverVy), e, 3);
        Assert.True(dp > 0);
        // 항복 아래: 완전 탄성(Hertz) — e = 1, 영구 변형 없음
        var (e0, dp0, _) = ThorntonSphere.Integrate(eStar, r, py, m, 0.8 * vy);
        Assert.Equal(1.0, e0, 4);
        Assert.Equal(0.0, dp0, 12);
        Assert.Equal(1.0, ThorntonSphere.Restitution(1.0), 9);                        // 닫힌 식 연속(V = V_y)
        Assert.Equal(1.185, ThorntonSphere.Restitution(1e-4) / Math.Pow(1e-4, 0.25), 3);   // 고속 점근 1.185(V_y/V)^¼
    }

    [Theory]
    [InlineData(0.2)]
    [InlineData(0.5)]
    [InlineData(0.9)]
    public void Linear_spring_perfect_plasticity_gives_vy_over_v_and_energy_balance(double cap)
    {
        // 직렬 선형 스프링 + 힘 상한 c(무차원 F_lim/(v√(km)) = v_y/v), 감쇠 없음: 제하 탄성 → e = c,
        // 최대 접근 (1 + c²)/(2c), 영구 눌림 (1 − c²)/(2c), 소성 일 (1 − c²)/2 = ½(1 − e²)
        var p = ContactPulse.Integrate(0, true, cap);
        Assert.Equal(cap, p.Rebound, 4);
        Assert.Equal(cap, p.PeakF, 6);
        Assert.Equal((1 + cap * cap) / (2 * cap), p.XMax, 4);
        Assert.Equal((1 - cap * cap) / (2 * cap), p.PlasticSet, 4);
        Assert.Equal((1 - cap * cap) / 2, p.PlasticWork, 4);
        Assert.Equal(0.5 * (1 - p.Rebound * p.Rebound), p.PlasticWork, 4);
        Assert.True(p.F.Max() <= cap + 1e-12);
        // 운동량: ∫F̃ dt̃ = 1 + e
        Assert.Equal(1 + cap, Trapz(p.T, p.F), 3);
    }

    [Fact]
    public void Linear_hardening_matches_energy_solution()
    {
        // 소성 기울기 α·k(α = 0.2): ½ = c²/2 + c·d + ½α d² → F_m = c + α d, e = F_m, 영구 눌림 = c + d − F_m
        double c = 0.4, a = 0.2;
        double d = (-c + Math.Sqrt(c * c + a * (1 - c * c))) / a;
        double fm = c + a * d;
        var p = ContactPulse.Integrate(0, true, c, a / (1 - a));
        Assert.Equal(fm, p.PeakF, 4);
        Assert.Equal(fm, p.Rebound, 4);
        Assert.Equal(c + d - fm, p.PlasticSet, 4);
        Assert.Equal(0.5 * (1 - fm * fm), p.PlasticWork, 4);
    }

    [Fact]
    public void Below_elastic_limit_is_the_same_elastic_pulse()
    {
        var el = ContactPulse.Master(0.3);
        Assert.Same(el, ContactPulse.Master(0.3, el.PeakF * 1.01));                 // 상한이 탄성 최대 위 → 탄성 펄스 그대로
        var x = ContactPulse.Integrate(0.7, true, 1e9);                                // 상한 ∞ 경로 = 탄성 적분기 같은 수
        var y = ContactPulse.Integrate(0.7, true);
        Assert.Equal(y.T, x.T);
        Assert.Equal(y.F, x.F);
        Assert.Equal(0.0, x.PlasticSet);
        // 차원 펄스: 상한 높으면 탄성 펄스와 같은 모양·지속·최대
        var spring = new ContactElement("a↔b", "spring", 1 / 2e7, double.NaN, true, "test");
        double m = 10, v = 1.5, fEl = el.PeakF * v * Math.Sqrt(2e7 * m);
        var pe = ContactPulse.Build([spring], m, v, 0.3, 1, 1e-4);
        var pp = ContactPulse.Build([spring], m, v, 0.3, 1, 1e-4, plastic: new PlasticLimit(fEl * 1.05, 0, [new ContactLimit("b", "test", fEl * 1.05, "")]));
        Assert.Equal(pe.Shape, pp.Shape);
        Assert.Equal(pe.PeakForce, pp.PeakForce, 9);
        Assert.False(pp.Plastic);
        Assert.Equal(0.0, pp.Indentation);
        // 무감쇠 탄성(e = 1): 에너지 보존 — 되튐 속도 = 접근 속도
        Assert.Equal(1.0, ContactPulse.Master(1.0).Rebound, 6);
    }

    [Fact]
    public void Plastic_pulse_keeps_6dof_restitution_with_damping_or_flags_mismatch()
    {
        // 상한 0.8: 소성만으로 e_p = 0.8 > 0.3 → 감쇠로 전체 되튐 0.3(운동량 = 충격량 모델 1.3)
        var a = ContactPulse.Master(0.3, 0.8);
        Assert.Equal(0.8, a.PlasticRestitution, 3);
        Assert.False(a.Mismatch);
        Assert.True(a.Plastic);
        Assert.True(Math.Abs(a.ReboundError) < 1e-4);
        Assert.Equal(1.3, Trapz(a.T, a.F), 3);
        Assert.True(a.F.Max() <= 0.8 + 1e-9);
        // 상한 0.2: e_p = 0.2 < 0.6 → 감쇠 0, 되튐 0.2, Mismatch
        var b = ContactPulse.Master(0.6, 0.2);
        Assert.True(b.Mismatch);
        Assert.Equal(0.0, b.Q);
        Assert.Equal(0.2, b.Rebound, 3);
        // 차원: 모양 면적 1(펄스 자신의 운동량), 최대 힘 = 상한, 눌림 = x̃_p·v/ω
        var spring = new ContactElement("a↔b", "spring", 1 / 2e7, double.NaN, true, "test");
        double m = 10, v = 2, fs = v * Math.Sqrt(2e7 * m), w = Math.Sqrt(2e7 / m);
        var lim = new PlasticLimit(0.2 * fs, 0, [new ContactLimit("b", "test", 0.2 * fs, "")]);
        var p = ContactPulse.Build([spring], m, v, 0.6, 1, 1e-4, plastic: lim);
        Assert.Equal(1.0, Trapz(p.T, p.Shape), 3);
        Assert.Equal(0.2 * fs, p.PeakForce, 0.2 * fs * 1e-6);
        Assert.Equal((1 - 0.04) / 0.4 * v / w, p.Indentation, 1e-6);
        Assert.Equal(lim, p.Rescaled(2).Plasticity);                                  // 강성 배율 재적분도 같은 상한
        Assert.Null(p.Elastic().Plasticity);                                           // 탄성 비교
        Assert.Equal(p.ElasticPeakForce, p.Elastic().PeakForce, 6);
        Assert.NotNull(p.ToJson()["plasticity"]);
        Assert.Contains("소성", p.Summary());
    }

    [Fact]
    public void Wall_crush_reuses_impact_damage_formulas()
    {
        var mat = MaterialLibrary.Find("al6061-t6")!;
        var shape = new Shape { Kind = ShapeKind.Cylinder, Radius = 0.15, Length = 1.5, Axis = 0, Center = Vec3.Zero };
        var s = new ContactStructure { Body = "tube", Kind = "tube", Shape = shape, Material = mat, Thickness = 0.003, Mass = 20 };
        // 끝 테두리 축하중(정적 — v = 0): Alexander × ¼
        var end = s.WallCrush(new Vec3(-0.75, 0.148, 0), Vec3.UnitX, 0)!;
        double s0 = 0.5 * (mat.Yield + mat.Ultimate);
        Assert.Equal(ImpactDamage.TubeAxialMeanCrush(s0, 0.003, 0.30) / 4, end.Force, 1e-6 * end.Force);
        Assert.Contains("Alexander", end.Kind);
        // 벽 중간 축하중: 압괴 상한 없음
        Assert.Null(s.WallCrush(new Vec3(0, 0.148, 0), Vec3.UnitX, 0));
        // 옆면 반경 하중: 링 압괴 σ₀t²L_eff/R
        var side = s.WallCrush(new Vec3(0, 0.15, 0), Vec3.UnitY, 0)!;
        Assert.Contains("링 압괴", side.Kind);
        Assert.True(side.Force > 0 && side.Force < end.Force);
        // 경도 후보 = c_H·σ_y·A(무른 쪽), 지배 = 최소
        var model = new ContactPlasticityModel();
        var lim = model.Limits((s, new Vec3(-0.75, 0.148, 0), Vec3.UnitX), (null, Vec3.Zero, Vec3.UnitX), 39e-6, 0)!;
        Assert.Equal(2.8 * mat.Yield * 39e-6, lim.Candidates.Single(c => c.Kind.Contains("경도")).Force, 1e-3);
        Assert.Equal(lim.Candidates.Min(c => c.Force), lim.Force);
    }

    private const string TwoBoxes = """
    {
      "schema": "mns.scenario/1", "name": "two_boxes_ep",
      "sim": { "dt": 0.0005, "tEnd": 0.1, "outputRate": 100 },
      "environment": { "vacuum": true },
      "bodies": [
        { "name": "a", "position": [0, 0, -500], "velocity": [30, 0, 0], "mass": 5.0, "shape": { "type": "sphere", "radius": 0.15 },
          "structure": { "material": "al6061-t6", "thickness": 0.004 } },
        { "name": "b", "position": [0.5, 0, -500], "velocity": [0, 0, 0], "mass": 5.0, "shape": { "type": "sphere", "radius": 0.15 },
          "structure": { "material": "al6061-t6", "thickness": 0.004 } }
      ],
      "collisions": { "restitution": 0.3, "friction": 0, "parachutes": false, "contact": { "model": "compliant", "plasticity": "elastoplastic" } }
    }
    """;

    [Fact]
    public void Elastoplastic_world_contact_records_indentation_and_keeps_momentum_exchange()
    {
        var wp = ScenarioDoc.Parse(TwoBoxes, TestUtil.RepoRoot).Build();
        wp.Run();
        var de = ScenarioDoc.Parse(TwoBoxes, TestUtil.RepoRoot);
        de.Root["collisions"]!["contact"]!["plasticity"] = "elastic";
        var we = de.Build();
        we.Run();
        Assert.NotNull(wp.Collisions!.Plasticity);
        Assert.Null(we.Collisions!.Plasticity);
        var p = wp.Collisions.Records.First(r => r.Kind == "contact").Pulse!;
        var q = we.Collisions.Records.First(r => r.Kind == "contact").Pulse!;
        Assert.True(p.Plastic, p.Summary());
        Assert.True(p.Indentation > 0);
        Assert.True(p.PeakForce <= p.Plasticity!.Force * (1 + 1e-6));
        Assert.True(q.PeakForce > p.PeakForce);                                        // 탄성은 상한 없음
        Assert.Equal(q.PeakForce, p.ElasticPeakForce, q.PeakForce * 1e-9);
        Assert.True(p.Duration > q.Duration);                                          // 소성 평탄 — 지속이 길다
        Assert.Equal(we.GetBody("a").V.X, wp.GetBody("a").V.X, 9);                    // 6DOF Δv 는 같은 e — 궤적 동일
        Assert.Equal(we.GetBody("b").V.X, wp.GetBody("b").V.X, 9);
        Assert.Contains(wp.EventLog, e => e.Message.Contains("소성: 상한"));
        var j = wp.Collisions.ToJson();
        Assert.Equal("elastoplastic", j["plasticity"]!["model"]!.GetValue<string>());
        var pj = j["pairs"]![0]!["first"]!["compliant"]!["plasticity"]!;
        Assert.True(pj["plastic"]!.GetValue<bool>());
        Assert.True(pj["indentation_mm"]!.GetValue<double>() > 0);
    }

    private static double Trapz(double[] t, double[] f)
    {
        double s = 0;
        for (int i = 1; i < t.Length; i++) s += 0.5 * (f[i] + f[i - 1]) * (t[i] - t[i - 1]);
        return s;
    }
}
