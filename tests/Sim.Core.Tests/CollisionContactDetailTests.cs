using Sim.Core.Numerics;
using Sim.Core.Scenario;

namespace Sim.Core.Tests;

/// <summary>충돌 기록의 접촉점(양 물체 동체 좌표·NED)·법선·충격량 벡터·상대속도 — 응답 방향을 되짚는 근거(시나리오 11 분리 재접촉).</summary>
public class CollisionContactDetailTests
{
    [DesignFact]
    public void Contact_records_carry_point_normal_impulse_and_relative_velocity()
    {
        var doc = ScenarioDoc.Load(TestUtil.DesignFile("config/scenarios", "11_*.json"));
        doc.Root["sim"]!["tEnd"] = 4.2;
        var w = doc.Build();
        w.Run();
        var contacts = w.Collisions!.Records.Where(r => r.Kind == "contact").ToList();
        Assert.NotEmpty(contacts);
        foreach (var r in contacts)
        {
            Assert.Equal(1.0, r.Normal.Norm, 6);
            Assert.True(r.Depth >= 0);
            Assert.Contains("형상", r.Via);
            // 접근 속도 = −(v_B − v_A)·n, 충격량은 B 쪽으로 법선 성분 ≥ 0(밀어냄), 크기 = 기록 충격량
            Assert.Equal(r.RelSpeed, -Vec3.Dot(r.RelVel, r.Normal), 9);
            if (r.Impulse > 0)
            {
                Assert.True(Vec3.Dot(r.J, r.Normal) > 0, $"{r.A}-{r.B} t={r.Time:F3}: 충격량이 법선 반대");
                Assert.Equal(r.Impulse, r.J.Norm, 6);
            }
            // 접촉점 동체 좌표는 그 물체 형상 경계 구 안
            var a = w.GetBody(r.A); var b = w.GetBody(r.B);
            Assert.True((r.PointA - a.Shape!.BoundingCenter).Norm <= a.Shape.BoundingRadius + 0.01, $"{r.A} 접촉점 {r.PointA}");
            Assert.True((r.PointB - b.Shape!.BoundingCenter).Norm <= b.Shape.BoundingRadius + 0.01, $"{r.B} 접촉점 {r.PointB}");
        }
        // 이벤트 메시지·요약 JSON 에도 남는다
        var first = contacts[0];
        Assert.Contains(w.EventLog, e => e.Name == $"collision:{first.A}-{first.B}" && e.Message.Contains("접촉점") && e.Message.Contains("법선"));
        var pair = w.Collisions.ToJson()["pairs"]!.AsArray().First(p => p!["kind"]!.GetValue<string>() == "contact")!;
        foreach (var k in new[] { "first", "maxImpulse" })
            foreach (var f in new[] { "pointA_brf_m", "pointB_brf_m", "point_ned_m", "normalAB_ned", "impulseOnB_ned_Ns", "relVelBA_ned_mps" })
                Assert.Equal(3, pair[k]![f]!.AsArray().Count);
    }
}
