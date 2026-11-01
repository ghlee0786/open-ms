using System.Text.Json.Nodes;
using Sim.Core.Analysis;
using Sim.Core.Engine;
using Sim.Core.Geometry;
using Sim.Core.Models;
using Sim.Core.Numerics;
using Sim.Core.Scenario;

namespace Sim.Core.Tests;

/// <summary>운반 탄체 3분할(PRD V절): 외피 조각 형상, 마하 보정, 힌지, 충격 펄스, 충돌 판정, 템플릿 06/07.</summary>
public class MunitionSplitTests
{
    [Fact]
    public void Shell_segment_mesh_volume_and_mass_properties_match_analytic()
    {
        double ro = 0.2, t = 0.003, l = 1.7, span = 120;
        var s = Shape.ShellSegmentOf(ro, t, l, span, 0, 0, Vec3.Zero);
        var (vol, cg, _) = s.Mesh!.VolumeProperties();
        double exact = span / 360 * Math.PI * (ro * ro - (ro - t) * (ro - t)) * l;
        Assert.InRange(vol / exact, 0.998, 1.0005);
        // 중심각 0° (+Y) → CG 는 +Y 쪽, x·z 대칭
        double ri = ro - t, half = span * Math.PI / 360;
        double yExact = 2.0 / 3 * (ro * ro * ro - ri * ri * ri) / (ro * ro - ri * ri) * Math.Sin(half) / half;
        Assert.InRange(cg.Y / yExact, 0.998, 1.002);
        Assert.True(Math.Abs(cg.X) < 1e-9 && Math.Abs(cg.Z) < 1e-9);
        Assert.Equal(ShapeKind.ShellSegment, s.Kind);
        Assert.True(s.UniformInertia(6.0).M11 > 0);
    }

    [Fact]
    public void Shell_segment_inside_test_distinguishes_wall_hollow_and_outside()
    {
        var s = Shape.ShellSegmentOf(0.2, 0.01, 1.0, 120, 90, 0, Vec3.Zero); // 중앙 +Z
        Assert.True(s.Penetration(new Vec3(0, 0, 0.195), out var d, out var n));
        Assert.InRange(d, 0, 0.0051);
        Assert.True(Math.Abs(Math.Abs(n.Z) - 1) < 1e-9);
        Assert.False(s.Penetration(new Vec3(0, 0, 0.1), out _, out _));   // 오목 안쪽 빈 공간
        Assert.False(s.Penetration(new Vec3(0, 0, -0.195), out _, out _)); // 반대편(부채꼴 밖)
        Assert.False(s.Penetration(new Vec3(0.6, 0, 0.195), out _, out _)); // 길이 밖
    }

    [Fact]
    public void Bluff_mach_factor_is_unity_below_03_and_increases_to_07()
    {
        Assert.Equal(1.0, AeroModel.BluffMachFactor(0.1), 12);
        Assert.Equal(1.0, AeroModel.BluffMachFactor(0.3), 12);
        Assert.Equal(1.12, AeroModel.BluffMachFactor(0.5), 9);
        Assert.Equal(AeroModel.BluffMachFactor(0.7), AeroModel.BluffMachFactor(0.95), 12);
        for (double m = 0.3; m < 0.7; m += 0.05) Assert.True(AeroModel.BluffMachFactor(m + 0.05) >= AeroModel.BluffMachFactor(m));
    }

    [Fact]
    public void Shell_normal_force_opens_concave_side_facing_flow()
    {
        var s = Shape.ShellSegmentOf(0.2, 0.003, 1.7, 120, 0, 0, Vec3.Zero); // 바깥 법선 +Y
        // 후방 힌지(+z 축) 둘레로 앞끝이 바깥(+Y)으로 θ 열림 = 조각 좌표가 R_z(θ) 만큼 돎.
        // 탄체 진행(+x)을 조각 좌표로 보면 R_z(−θ)·x̂ = (cosθ, −sinθ, 0): 법선 성분 dn<0(오목면이 바람) → 힘 +Y(더 벌림)
        double th = 20 * Math.PI / 180;
        var dirBody = new Vec3(Math.Cos(th), -Math.Sin(th), 0);
        var f = AeroModel.ShellForce(s, dirBody, 1000);
        Assert.True(f.Y > 0, $"조각이 닫히는 방향의 힘: {f}");
        // 볼록면이 바람을 받으면(반대로 닫히는 쪽 받음각) 힘은 −Y 이고 크기는 볼록/오목 Cd 비
        var g = AeroModel.ShellForce(s, new Vec3(Math.Cos(th), Math.Sin(th), 0), 1000);
        Assert.True(g.Y < 0);
        Assert.Equal(Shape.ShellCdConcave / Shape.ShellCdConvex, f.Y / -g.Y, 9);
    }

    private const string HingeJson = """
    {
      "schema": "mns.scenario/1", "name": "hinge_test",
      "sim": { "dt": 0.0005, "tEnd": 0.6, "outputRate": 200 },
      "environment": { "vacuum": true },
      "carrier": { "name": "base", "altitude": 1000, "speed": 0 },
      "bodies": [
        { "name": "petal", "parent": "base", "mount": [0, 0, 0], "mass": 6.0,
          "shape": { "type": "shellSegment", "radius": 0.2, "thickness": 0.003, "length": 1.7, "span": 120, "centerAngle": 0, "axis": "x" } }
      ],
      "connectors": [
        { "type": "hinge", "name": "h", "a": "base", "b": "petal", "pointA": [-0.85, 0.1985, 0], "axisA": [0, 0, 1], "releaseAngle": 30 }
      ],
      "events": [
        { "name": "cut", "when": { "type": "time", "t": 0.01 },
          "do": [ { "type": "separate", "body": "petal", "relVel": [0, 1.5, 0], "relOmega": [0, 0, 1.7647058823529411] } ] }
      ]
    }
    """;

    [Fact]
    public void Hinge_keeps_pivot_fixed_while_petal_rotates_then_releases_at_angle()
    {
        var w = TestUtil.Build(HingeJson);
        var h = (Hinge)w.Connectors[0];
        double maxGap = 0;
        while (!w.Stopped)
        {
            w.Step();
            if (!h.Released && w.T > 0.011)
            {
                var gap = (w.GetBody("petal").PointWorld(new Vec3(-0.85, 0.1985, 0)) - w.GetBody("base").PointWorld(new Vec3(-0.85, 0.1985, 0))).Norm;
                maxGap = Math.Max(maxGap, gap);
            }
        }
        Assert.True(h.Released, "열림각에서 이탈해야 함");
        Assert.Contains("열림각", h.ReleaseReason);
        // ω = 1.765 rad/s(앞끝 3 m/s) 로 30° = 0.524 rad → ≈ 0.297 s 후
        Assert.InRange(h.ReleaseTime - 0.01, 0.27, 0.33);
        Assert.True(maxGap < 0.002, $"힌지 점 벌어짐 {maxGap:F4} m");
        Assert.Contains(w.EventLog, e => e.Name == "h.released");
    }

    [Fact]
    public void Pulse_device_gives_half_sine_delta_v()
    {
        var w = TestUtil.Build("""
        {
          "schema": "mns.scenario/1", "name": "pulse",
          "sim": { "dt": 0.0005, "tEnd": 0.1, "outputRate": 1000 },
          "environment": { "vacuum": true },
          "bodies": [ { "name": "c", "altitude": 500, "mass": 5.5, "shape": { "type": "box", "size": [0.40, 0.27, 0.27] },
            "devices": [ { "type": "pulse", "name": "shock", "peakG": 10, "duration": 0.02, "direction": [0, 0, 1] } ] } ],
          "events": [ { "name": "go", "when": { "type": "time", "t": 0.01 }, "do": [ { "type": "fire", "device": "c.shock" } ] } ]
        }
        """);
        w.Run();
        var b = w.GetBody("c");
        double dv = 9.80665 * 10 * 0.02 * 2 / Math.PI;
        Assert.InRange((b.V.Z - 9.80665 * w.T) / dv, 0.995, 1.005);   // 중력 낙하분 제외
        Assert.InRange(b.MaxLoadG, 9.9, 10.01);
    }

    private static string TwoBoxes(double e) => $$"""
    {
      "schema": "mns.scenario/1", "name": "collide",
      "sim": { "dt": 0.0005, "tEnd": 1.0, "outputRate": 100 },
      "environment": { "vacuum": true },
      "bodies": [
        { "name": "a", "position": [0, 0, -500], "velocity": [2, 0, 0], "mass": 2, "shape": { "type": "box", "size": [0.2, 0.2, 0.2] } },
        { "name": "b", "position": [1, 0, -500], "velocity": [-2, 0, 0], "mass": 2, "shape": { "type": "box", "size": [0.2, 0.2, 0.2] } }
      ],
      "collisions": { "restitution": {{e}}, "friction": 0 }
    }
    """;

    [Theory]
    [InlineData(1.0)]
    [InlineData(0.0)]
    public void Head_on_box_collision_conserves_momentum_and_applies_restitution(double e)
    {
        var w = TestUtil.Run(TwoBoxes(e));
        var a = w.GetBody("a"); var b = w.GetBody("b");
        Assert.Equal(0, a.V.X + b.V.X, 6);                // 운동량 보존(질량 같음)
        double rel = b.V.X - a.V.X;
        Assert.InRange(rel, 4 * e - 0.05, 4 * e + 0.05);   // 분리 속도 = e × 접근 속도(4 m/s)
        Assert.NotNull(w.Collisions);
        Assert.True(w.Collisions!.ContactCount >= 1);
        Assert.Contains(w.EventLog, x => x.Name == "collision:a-b");
    }

    [Fact]
    public void Template_06_three_petal_split_runs_with_hinge_release_and_container_loads()
    {
        var doc = ScenarioDoc.Load(TestUtil.Scenario("06_munition_3petal_split.json"));
        doc.Root["sim"]!["tEnd"] = 3.0;
        var w = doc.Build();
        // M 0.5 @ 1 km (ISA)
        double a = w.Env.Atmosphere.At(1000).SpeedOfSound;
        Assert.InRange(w.GetBody("munition").Trajectory!.Velocity.Norm / a, 0.499, 0.501);
        Assert.Equal(3, w.Bodies.Count(b => b.Shape?.Kind == ShapeKind.ShellSegment));
        w.Run();
        var hinges = w.Connectors.OfType<Hinge>().ToList();
        Assert.Equal(3, hinges.Count);
        Assert.All(hinges, h =>
        {
            Assert.True(h.Released);
            Assert.Contains("열림각", h.ReleaseReason);          // 반력 파단이 아니라 열림으로 이탈
            Assert.InRange(h.ReleaseTime, 0.5, 1.0);
            Assert.True(h.MaxForce < 60000);
        });
        for (int k = 1; k <= 4; k++)
        {
            var c = w.GetBody($"c{k}");
            Assert.Equal(BodyMode.Free, c.Mode);
            Assert.InRange(c.MaxLoadG, 15, 40);                 // 충격 펄스 15 G + 공력 감속(≤ 40 G)
        }
        var s = Results.Summary(doc, w);
        Assert.NotNull(s["collisions"]);
        Assert.Equal("maxLoad", s["metrics"]!["containerPeakG"]!["type"]!.GetValue<string>());
        Assert.True(s["metrics"]!["debrisAtDrogue"]!["devices"]!.AsArray().Count == 4);
        Assert.True(J.ToDouble(s["bodies"]!["petal1"]!["maxMach"]!) > 0.45);
    }

    [Fact]
    public void Template_07_tail_cut_releases_containers_aft_one_by_one()
    {
        var doc = ScenarioDoc.Load(TestUtil.Scenario("07_munition_tail_cut_aft_exit.json"));
        doc.Root["sim"]!["tEnd"] = 3.5;
        var w = doc.Build();
        w.Run();
        var rel = new[] { 4, 3, 2, 1 }.Select(k => w.GetBody($"c{k}").ReleaseTime).ToArray();
        Assert.All(rel, t => Assert.True(double.IsFinite(t)));
        for (int i = 1; i < rel.Length; i++) Assert.True(rel[i] > rel[i - 1], "후방(c4)부터 순서대로");
        Assert.All(new[] { 1, 2, 3, 4 }, k => Assert.True(w.GetBody($"c{k}").MaxLoadG < 60));
    }
}
