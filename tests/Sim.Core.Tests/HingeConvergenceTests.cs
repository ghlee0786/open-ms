using Sim.Core.Engine;
using Sim.Core.Models;
using Sim.Core.Numerics;
using Sim.Core.Scenario;

namespace Sim.Core.Tests;

/// <summary>
/// 3분할 힌지 반력 이상(시나리오 09: 반력이 Δt 에 따라 7.8 → 17.5 kN 으로 바뀌고 hinge2 가 이탈하지 못함) 회귀:
/// 원인 ① 안정 한계가 결속점 관성 지렛대 팔·공유 힌지 수를 빼고 계산(감쇠 극점이 RK4 한계 초과)
///      ② 힌지로 묶인 꼬리–꽃잎 쌍에 충돌 위치 보정·충격량이 걸려 펜널티 스프링과 싸움.
/// </summary>
public class HingeConvergenceTests
{
    [Fact]
    public void Point_mobility_of_uniform_rod_end_is_four_over_mass()
    {
        // 균일 막대(길이 L, 질량 m) 끝점 횡방향: 1/m + (L/2)²/(mL²/12) = 4/m (해석해)
        var w = TestUtil.Build("""
        {
          "schema": "mns.scenario/1", "name": "rod",
          "sim": { "dt": 0.001, "tEnd": 0.01 },
          "environment": { "vacuum": true },
          "bodies": [ { "name": "rod", "altitude": 500, "mass": 2.0, "shape": { "type": "cylinder", "radius": 0.005, "length": 1.0, "axis": "x" } } ]
        }
        """);
        var rod = w.GetBody("rod");
        var tip = rod.PointWorld(rod.Cg + new Vec3(0.5, 0, 0));
        double lam = Hinge.PointMobility(rod, tip);
        Assert.InRange(lam * 2.0, 3.99, 4.01);
        // CG 에서는 병진만: 1/m
        Assert.InRange(Hinge.PointMobility(rod, rod.PointWorld(rod.Cg)) * 2.0, 0.999, 1.001);
    }

    private static Hinge RunSinglePetal(double dt, double? stabilityDt)
    {
        string stab = stabilityDt is { } s ? $", \"stabilityDt\": {s.ToString(System.Globalization.CultureInfo.InvariantCulture)}" : "";
        var w = TestUtil.Run($$"""
        {
          "schema": "mns.scenario/1", "name": "hinge_dt",
          "sim": { "dt": {{dt.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, "tEnd": 0.2 },
          "environment": { "vacuum": true },
          "carrier": { "name": "base", "altitude": 1000, "speed": 0 },
          "bodies": [
            { "name": "petal", "parent": "base", "mount": [0, 0, 0], "mass": 6.0,
              "shape": { "type": "shellSegment", "radius": 0.2, "thickness": 0.003, "length": 1.7, "span": 120, "centerAngle": 0, "axis": "x" } }
          ],
          "connectors": [
            { "type": "hinge", "name": "h", "a": "base", "b": "petal", "pointA": [-0.85, 0.1985, 0], "axisA": [0, 0, 1], "releaseAngle": 80{{stab}} }
          ],
          "events": [
            { "name": "cut", "when": { "type": "time", "t": 0.01 },
              "do": [ { "type": "separate", "body": "petal", "relVel": [0, 6, 0], "relOmega": [0, 0, 7.0588] } ] }
          ]
        }
        """);
        return (Hinge)w.Connectors[0];
    }

    [DesignFact]
    public void Hinge_stiffness_cap_is_independent_of_time_step_below_stability_dt()
    {
        // 실모델 시나리오 09 에서 드러난 결함: 상한을 현재 Δt 로 계산하면 Δt 를 줄일 때 강성(모델)이 커져 반력이 수렴하지 않는다.
        var c = RunSinglePetal(0.001, null);
        var f = RunSinglePetal(0.00025, null);
        Assert.True(c.EffectiveStiffness < c.Stiffness, "이 결속점 유효 질량이면 Δt 1 ms 에서 상한이 걸려야 시험이 의미 있다");
        Assert.Equal(c.EffectiveStiffness, f.EffectiveStiffness, 6);
        Assert.InRange(c.MaxForceAvg / f.MaxForceAvg, 0.97, 1.03);
        // stabilityDt = 0 → 현재 Δt 기준(수정 전 방식): Δt/4 에서 강성이 다시 커진다
        var old = RunSinglePetal(0.00025, 0);
        Assert.True(old.EffectiveStiffness > 4 * f.EffectiveStiffness || old.EffectiveStiffness == old.Stiffness);
        Assert.True(old.EffectiveStiffness > f.EffectiveStiffness);
    }

    private static World Run09(double dt, double tEnd)
    {
        var doc = ScenarioDoc.Load(TestUtil.Scenario("09_munition_3petal_longcontainer_real.json"));
        doc.Root["sim"]!["dt"] = dt;
        doc.Root["sim"]!["tEnd"] = tEnd;
        var w = doc.Build();
        w.Run();
        return w;
    }

    [DesignFact]
    public void Scenario09_hinge_reactions_converge_with_time_step_before_first_debris_contact()
    {
        // 수렴 구간 = 기폭(4.0 s) → 꼬리–컨테이너 첫 접촉(합성 4.157 s · 실모델 4.18 s) 전. 접촉은 이산 충격 사건이라
        // 그 뒤 궤적은 Δt 에 따라 갈린다(접촉 시각 민감 — 펜널티 수치 오차와 별개).
        var coarse = Run09(0.001, 4.15);
        var fine = Run09(0.00025, 4.15);
        var hc = coarse.Connectors.OfType<Hinge>().OrderBy(h => h.Name).ToList();
        var hf = fine.Connectors.OfType<Hinge>().OrderBy(h => h.Name).ToList();
        Assert.Equal(3, hc.Count);
        for (int i = 0; i < 3; i++)
        {
            // 구조 하중(5 ms 평균) 반력: Δt 1 ms ↔ 0.25 ms 10 % 이내 — 수정 전 hinge2 는 2.2 배 차
            double ratio = hc[i].MaxForceAvg / hf[i].MaxForceAvg;
            Assert.InRange(ratio, 0.9, 1.1);
            Assert.True(hc[i].SharedHinges >= 1 && double.IsFinite(hc[i].EffectiveMass));
        }
        // 꽃잎 1·3 은 이 구간에 열림각 이탈, 시각 차 < 5 ms
        foreach (int i in new[] { 0, 2 })
        {
            Assert.True(hc[i].Released && hf[i].Released, $"{hc[i].Name} 이 이탈하지 않음");
            Assert.InRange(Math.Abs(hc[i].ReleaseTime - hf[i].ReleaseTime), 0, 0.005);
        }
    }

    [DesignFact]
    public void Scenario09_all_petals_release_by_angle_with_physical_hinge_reaction()
    {
        var w = Run09(0.001, 4.8);
        foreach (var h in w.Connectors.OfType<Hinge>())
        {
            Assert.True(h.Released, $"{h.Name} 이 이탈하지 않음(수정 전 hinge2 는 끝까지 붙어 있었다)");
            Assert.Contains("열림각", h.ReleaseReason);
            // 물리 규모: 27 kg 꽃잎 · 동압 15.7 kPa → 수 kN. 50–150 kN(꽃잎 1,000 G 이상) 이상치 재발 금지
            Assert.InRange(h.MaxForceAvg, 500, 20000);
            Assert.True(h.MaxForce < 30000, $"{h.Name} 순간 반력 {h.MaxForce:F0} N");
        }
        foreach (var p in new[] { "petal1", "petal2", "petal3" })
            Assert.InRange(w.GetBody(p).MaxLoadG, 1, 200);
    }

    [DesignFact]
    public void Hinged_pair_is_not_collision_checked_while_engaged()
    {
        var w = Run09(0.001, 4.6);
        foreach (var h in w.Connectors.OfType<Hinge>())
        {
            string key = $"collision:{h.A.Name}-{h.B.Name}", key2 = $"collision:{h.B.Name}-{h.A.Name}";
            Assert.DoesNotContain(w.EventLog, e => (e.Name == key || e.Name == key2) && e.Time < h.ReleaseTime);
        }
    }
}
