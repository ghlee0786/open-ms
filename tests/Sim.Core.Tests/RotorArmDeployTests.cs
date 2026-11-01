using Sim.Core.Models;
using Sim.Core.Numerics;

namespace Sim.Core.Tests;

/// <summary>FR-SEP-04 접힘 암 전개 · 프롭 회전 표시 기록: switchConfig 시각부터 armDeployTime 동안 multirotor Size(암 전개율) 0 → 1.</summary>
public class RotorArmDeployTests
{
    private const string Drone = """
        { "schema":"mns.scenario/1", "sim":{"dt":0.002,"tEnd":3},
          "bodies":[{"name":"d","mass":3,"inertia":[0.02,0.02,0.04],"aero":"none","position":[0,0,-100],
            "shape":{"type":"box","size":[0.30,0.30,0.30],"center":[0,0,-0.15]},
            "configs":{"deployed":{"inertiaScale":2.0}},
            "devices":[{"type":"multirotor","name":"rotor","maxThrust":85,"armDeployTime":0.4}]}],
          "events":[{"name":"arms","when":{"type":"time","t":0.5},
            "do":[{"type":"switchConfig","body":"d","config":"deployed"},{"type":"set","device":"d.rotor","mode":"stabilize"}]}] }
        """;

    private static void StepTo(Sim.Core.Engine.World w, double t)
    {
        while (w.T < t - 1e-9) w.Step();
    }

    [Fact(DisplayName = "FR-SEP-04 암 전개: switchConfig 전 접힘(0)·프롭 정지 → 전개 시간 동안 증가 → 펴짐(1)·프롭 작동")]
    public void ArmsDeployAfterSwitchConfig()
    {
        var w = TestUtil.Build(Drone);
        var m = (Multirotor)w.GetBody("d").Devices[0];
        Assert.Equal("deployed", m.ArmsConfig);

        StepTo(w, 0.3);
        var s0 = m.Sample(w);
        Assert.Equal(0, s0.Size);
        Assert.False(s0.Visible);

        StepTo(w, 0.7);
        var s1 = m.Sample(w);
        Assert.InRange(s1.Size, 0.4, 0.6);   // (0.7 − 0.5) / 0.4
        Assert.True(s1.Visible);

        StepTo(w, 1.2);
        var s2 = m.Sample(w);
        Assert.Equal(1, s2.Size);
        Assert.True(s2.Visible && s2.Fill > 0);
    }

    [Fact(DisplayName = "FR-SEP-04 암 전개: 접힘 구성(configs.deployed)이 없는 드론은 처음부터 펴짐(1)")]
    public void ArmsAlwaysDeployedWithoutConfig()
    {
        var w = TestUtil.Build(Drone.Replace("\"configs\":{\"deployed\":{\"inertiaScale\":2.0}},", "")
                                    .Replace("{\"type\":\"switchConfig\",\"body\":\"d\",\"config\":\"deployed\"},", ""));
        var m = (Multirotor)w.GetBody("d").Devices[0];
        Assert.Null(m.ArmsConfig);
        Assert.Equal(1, m.Sample(w).Size);
    }

    [Fact(DisplayName = "고정 팔(props): 허브 위치·축·회전 방향 그대로, 오버레이 암 끔, configs.deployed 가 있어도 접힘 자동 감지 안 함")]
    public void FixedArmPropsDisableArmOverlayAndDeploy()
    {
        var w = TestUtil.Build(Drone.Replace("\"armDeployTime\":0.4",
            "\"props\":[{\"pos\":[-0.08,-0.10,-0.05],\"axis\":[0,0,-2]},{\"pos\":[-0.08,0.10,-0.05],\"spin\":1},{\"pos\":[-0.35,0.10,-0.05]}]"));
        var m = (Multirotor)w.GetBody("d").Devices[0];
        Assert.Null(m.ArmsConfig);
        Assert.False(m.ArmsOverlay);
        Assert.Equal(3, m.ArmCount);
        Assert.Equal(new Vec3(-0.08, -0.10, -0.05), m.Props[0].Position);
        Assert.Equal(new Vec3(0, 0, -1), m.Props[0].Axis);          // 정규화, 기본 축 = 동체 −Z
        Assert.Equal(new Vec3(0, 0, -1), m.Props[2].Axis);
        Assert.Equal([1, 1, 1], m.Props.Select(p => p.Spin));      // 명시 spin, 기본 교대(0:+1, 2:+1)
        StepTo(w, 0.3);
        Assert.Equal(1, m.Sample(w).Size);                          // 전개 동작 없음
        Assert.False(m.Sample(w).Visible);                          // 모터 기동 전 정지
        StepTo(w, 0.6);
        Assert.True(m.Sample(w).Visible);
    }

    [DesignFact(DisplayName = "설계 09·09b: 드론은 고정 팔 — props 4개(실모델 허브), 접힘 구성·switchConfig 없음, 구 설계 09r34 는 암 전개 유지")]
    public void Design09DronesUseFixedArmProps()
    {
        foreach (var f in new[] { "09_munition_3petal_longcontainer_real.json", "09b_munition_bellydoor_longcontainer_real.json" })
        {
            var doc = Sim.Core.Scenario.ScenarioDoc.Load(TestUtil.Scenario(f));
            doc.Root["realModels"] = new System.Text.Json.Nodes.JsonObject { ["mode"] = "synthetic" };
            Assert.DoesNotContain(doc.Root["events"]!.AsArray(), e => e!["do"]!.AsArray().Any(a => a!["type"]!.GetValue<string>() == "switchConfig"
                                                                                                 && a["body"]!.GetValue<string>().StartsWith("drone")));
            var w = doc.Build();
            for (int k = 1; k <= 4; k++)
            {
                var m = w.GetBody($"drone{k}").Devices.OfType<Multirotor>().Single();
                Assert.Null(m.ArmsConfig);
                Assert.False(m.ArmsOverlay);
                Assert.Equal(4, m.Props.Count);
                Assert.Equal(0.1778, m.PropDiameter, 9);
                Assert.All(m.Props, p => Assert.Equal(new Vec3(0, 0, -1), p.Axis));
                Assert.Equal(0, m.Props.Sum(p => p.Spin));               // 2 정회전 · 2 역회전
                Assert.Equal(0.26, m.Props[0].Position.X - m.Props[3].Position.X, 6);   // 앞·뒤 허브 간격 = mot_span
                Assert.Equal(0.19, m.Props[1].Position.Y - m.Props[0].Position.Y, 6);   // 좌·우 허브 간격
            }
        }
        var old = Sim.Core.Scenario.ScenarioDoc.Load(TestUtil.DesignFile("tests/Sim.Core.Tests/scenarios", "09r34_*.json"));
        old.Root["realModels"] = new System.Text.Json.Nodes.JsonObject { ["mode"] = "synthetic" };
        var mo = old.Build().GetBody("drone1").Devices.OfType<Multirotor>().Single();
        Assert.Equal("deployed", mo.ArmsConfig);
        Assert.True(mo.ArmsOverlay);
        Assert.Empty(mo.Props);
    }
}
