using Sim.Core.Geometry;
using Sim.Core.Models;
using Sim.Core.Numerics;

namespace Sim.Core.Tests;

/// <summary>풍동 보기 계산(정점 장 값 · 속도비 · 평활 법선 · 풍동 좌표 · 유선 입자).</summary>
public class WindTunnelTests
{
    // 원통 R 1, x ∈ [0, 2]. α 0: 측면 cp = x + θ/90, α 90: × 2. cf 는 α 0 에만.
    private static SurfaceFlowField Field()
    {
        static string Side(double k) => string.Join(",", from x in new[] { 0.0, 1, 2 } from t in new[] { 0.0, 90, 180 } select ((x + t / 90) * k).ToString(System.Globalization.CultureInfo.InvariantCulture));
        string Case(double a, double k, bool cf) => $$$"""
          {"group":"M0.50","label":"a{{{a}}}","alpha":{{{a}}},"mach":0.5,"speed":168,"method":"rans","valid":true,
           "fields":{"cp":{"side":[{{{Side(k)}}}],"capFwd":[1,1,1,1,1,1],"capAft":[0,0,0,0,0,0]}{{{(cf ? ",\"cf\":{\"side\":[" + Side(0.01) + "],\"capFwd\":[0,0,0,0,0,0],\"capAft\":[0,0,0,0,0,0]}" : "")}}}},
           "streamlines":[{"p":[3,0,0.5, -1,0,0.5],"s":[1,1]}]}
          """;
        var json = $$$"""
          {"schema":"mns.surface-flow/1","name":"t","foreAftSymmetric":true,
           "surface":{"kind":"cylinder","radius":1,"x0":0,"x1":2,"nx":3,"ntheta":3,"nr":2},
           "cases":[{{{Case(0, 1, true)}}},{{{Case(90, 2, false)}}}]}
          """;
        var p = Path.Combine(Path.GetTempPath(), $"wt_{Guid.NewGuid():N}.json");
        File.WriteAllText(p, json);
        try { return SurfaceFlowField.Load(p); } finally { File.Delete(p); }
    }

    [Fact]
    public void Values_use_exactly_the_chosen_case()
    {
        var f = Field();
        var pts = new[] { new Vec3(1, 0, 1), new Vec3(1, 1, 0) };   // 바람 쪽(θ 0) · 옆(θ 90)
        var a90 = WindTunnel.Values(f, f.Cases[1], pts, TunnelField.Cp);
        Assert.Equal(2.0, a90[0], 12); Assert.Equal(4.0, a90[1], 12);
        var cf0 = WindTunnel.Values(f, f.Cases[0], pts, TunnelField.Cf);
        Assert.Equal(0.01, cf0[0], 12);
        // 장이 없는 케이스는 NaN(다른 받음각 값을 빌려 오지 않음)
        Assert.All(WindTunnel.Values(f, f.Cases[1], pts, TunnelField.Cf), v => Assert.True(double.IsNaN(v)));
        Assert.Equal(0, f.OutOfRange);
    }

    [Fact]
    public void Speed_ratio_from_cp_is_one_at_freestream_and_zero_at_stagnation()
    {
        Assert.Equal(1.0, WindTunnel.SpeedRatioFromCp(0, 0.5), 9);
        Assert.Equal(1.0, WindTunnel.SpeedRatioFromCp(0, 0), 12);
        Assert.Equal(Math.Sqrt(2), WindTunnel.SpeedRatioFromCp(-1, 0), 12);
        // M 0.5 정체점 Cp = (p0/p∞ − 1)/(γM²/2)
        double cpStag = (Math.Pow(1 + 0.2 * 0.25, 3.5) - 1) / (0.7 * 0.25);
        Assert.Equal(0.0, WindTunnel.SpeedRatioFromCp(cpStag, 0.5), 6);
        // 흡입(Cp < 0)은 빨라짐, 단조 감소
        Assert.True(WindTunnel.SpeedRatioFromCp(-0.5, 0.5) > 1.1);
        Assert.True(WindTunnel.SpeedRatioFromCp(0.5, 0.5) < WindTunnel.SpeedRatioFromCp(0.2, 0.5));
        Assert.True(double.IsNaN(WindTunnel.SpeedRatioFromCp(double.NaN, 0.5)));
    }

    [Fact]
    public void Tunnel_frame_turns_travel_direction_to_plus_x_and_flow_to_minus_x()
    {
        foreach (var a in new[] { 0.0, 30, 90 })
        {
            var c = new Vec3(1, 0, 0);
            double r = a * Math.PI / 180;
            var d = WindTunnel.ToTunnel(c + new Vec3(Math.Cos(r), 0, Math.Sin(r)), a, c);
            Assert.Equal(1.0, d.X, 12); Assert.Equal(0.0, d.Z, 12);
            var flow = WindTunnel.ToTunnel(c + WindTunnel.FlowDirCfd(a), a, c);
            Assert.Equal(-1.0, flow.X, 12);
            Assert.Equal(Vec3.Zero, WindTunnel.ToTunnel(c, a, c));
        }
    }

    [Fact]
    public void Smooth_normals_round_the_side_but_keep_the_cap_edge_sharp()
    {
        var m = TriMesh.CylinderGrid(1, 2, 0, Vec3.Zero, 24, 4, 2);
        var n = WindTunnel.SmoothNormals(m);
        Assert.Equal(m.Vertices.Count, n.Length);
        for (int i = 0; i < n.Length; i++)
        {
            var p = m.Vertices[i];
            Assert.Equal(1.0, n[i].Norm, 9);
            int t = i / 3;
            var fn = Vec3.Cross(m.Vertices[3 * t + 1] - m.Vertices[3 * t], m.Vertices[3 * t + 2] - m.Vertices[3 * t]).Normalized();
            if (Math.Abs(fn.X) < 0.5)
            {
                // 측면: 정점 법선 = 반경 방향(면 법선이 아니라) — 매끈한 음영
                var radial = new Vec3(0, p.Y, p.Z).Normalized();
                Assert.True(Math.Abs(Vec3.Dot(n[i], radial)) > 0.999, $"side {i}");
            }
            else Assert.True(Math.Abs(n[i].X) > 0.999, $"cap {i}");    // 끝면은 축 방향 그대로(모서리에서 섞이지 않음)
        }
    }

    [Fact]
    public void Particles_follow_the_line_downstream_and_spread_where_flow_is_fast()
    {
        // 거꾸로 저장된 선(하류 → 상류)도 흐름 방향으로 뒤집는다. 앞 절반 속력 1, 뒤 절반 속력 2.
        var line = (new[] { new Vec3(-2, 0, 0), new Vec3(0, 0, 0), new Vec3(2, 0, 0) }, new[] { 2.0, 1.0, 1.0 });
        var sp = new StreamlineParticles([line], WindTunnel.FlowDir);
        var l = Assert.Single(sp.Lines);
        Assert.Equal(new Vec3(2, 0, 0), l.P[0]);
        Assert.Equal(2 + 2 / 1.5, l.Duration, 9);
        var (p, s) = StreamlineParticles.At(l, 1);
        Assert.Equal(1.0, p.X, 12); Assert.Equal(1.0, s, 12);
        var out1 = new List<(Vec3 P, double S)>();
        sp.Positions(0, 1, 0.5, out1);
        Assert.All(out1, q => Assert.Equal(0.0, q.P.Y));
        // τ 간격이 같으므로 빠른 뒤쪽(x < 0)의 입자 간격이 더 넓다
        var xs = out1.Select(q => q.P.X).ToArray();
        double front = xs[0] - xs[1], back = xs[^2] - xs[^1];
        Assert.True(back > front * 1.2, $"front {front} back {back}");
        // 시간이 흐르면 하류(−x)로 이동
        var out2 = new List<(Vec3 P, double S)>();
        sp.Positions(0.1, 1, 0.5, out2);
        Assert.True(out2[0].P.X < out1[0].P.X);
    }

    [Fact]
    public void Fallback_cylinder_mesh_covers_the_flow_surface()
    {
        var f = Field();
        var m = WindTunnel.CylinderMesh(f, 0.1);
        var (mn, mx) = m.Bounds();
        Assert.Equal(0.0, mn.X, 9); Assert.Equal(2.0, mx.X, 9);
        Assert.Equal(1.0, mx.Y, 2);
        Assert.Equal(new Vec3(1, 0, 0), WindTunnel.Center(f));
        Assert.Equal(TunnelField.SpeedRatio, WindTunnel.ParseField("speed"));
        Assert.Equal(TunnelField.Cp, WindTunnel.ParseField("??"));
    }
}
