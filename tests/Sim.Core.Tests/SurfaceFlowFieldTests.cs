using Sim.Core.Geometry;
using Sim.Core.Models;
using Sim.Core.Numerics;

namespace Sim.Core.Tests;

/// <summary>CFD 표면 유동장(mns.surface-flow/1) 표본화 — 공기흐름 시각화용 Core API(면마다 Cp·Cf).</summary>
public class SurfaceFlowFieldTests
{
    // 원통 R 1, x ∈ [0, 2], 측면 3×3(x 0·1·2, θ 0·90·180), 끝면 2×3(r 0·1).
    // α 0: cp = x + θ/90 (측면), 앞 끝면 = 10 + r, 뒤 끝면 = −10 − r. α 90: 측면 값 × 2.
    private static string Synthetic()
    {
        static string Side(double k) => string.Join(",", from x in new[] { 0.0, 1, 2 } from t in new[] { 0.0, 90, 180 } select ((x + t / 90) * k).ToString(System.Globalization.CultureInfo.InvariantCulture));
        string Case(double a, double k) => $$$"""
          {"group":"M0.50","label":"a{{{a}}}","alpha":{{{a}}},"mach":0.5,"speed":168,"method":"rans","valid":true,
           "fields":{"cp":{"side":[{{{Side(k)}}}],"capFwd":[10,10,10,11,11,11],"capAft":[-10,-10,-10,-11,-11,-11]}},
           "streamlines":[{"p":[-1,0,0.5, 3,0,0.5],"s":[1,1.2]}]}
          """;
        return $$$"""
          {"schema":"mns.surface-flow/1","name":"t","foreAftSymmetric":true,
           "surface":{"kind":"cylinder","radius":1,"x0":0,"x1":2,"nx":3,"ntheta":3,"nr":2},
           "cases":[{{{Case(0, 1)}}},{{{Case(90, 2)}}}]}
          """;
    }

    private static SurfaceFlowField LoadSynthetic()
    {
        var p = Path.Combine(Path.GetTempPath(), $"sff_{Guid.NewGuid():N}.json");
        File.WriteAllText(p, Synthetic());
        try { return SurfaceFlowField.Load(p); } finally { File.Delete(p); }
    }

    private static Vec3 Dir(double alphaDeg, double rollDeg = 0)
    {
        double a = alphaDeg * Math.PI / 180, r = rollDeg * Math.PI / 180;
        return new Vec3(Math.Cos(a), Math.Sin(a) * Math.Sin(r), Math.Sin(a) * Math.Cos(r));
    }

    [Fact]
    public void Side_values_interpolate_in_alpha_and_theta()
    {
        var f = LoadSynthetic();
        var pts = new[] { new Vec3(1, 0, 1), new Vec3(1, 1, 0), new Vec3(0.5, 0, -1) };   // 바람 쪽 · 옆 · 뒤
        var v0 = f.Sample(pts, Dir(0));
        Assert.Equal(1.0, v0[0], 12); Assert.Equal(2.0, v0[1], 12); Assert.Equal(2.5, v0[2], 12);
        var v45 = f.Sample(pts, Dir(45));
        Assert.Equal(1.5, v45[0], 12); Assert.Equal(3.0, v45[1], 12);
        Assert.Equal(0, f.OutOfRange);
    }

    [Fact]
    public void Aero_roll_turns_the_windward_side_with_the_crossflow()
    {
        var f = LoadSynthetic();
        // 횡류가 +y 쪽이면(롤 90°) +y 면이 바람 쪽(θ 0), +z 면은 옆(θ 90)
        var v = f.Sample(new[] { new Vec3(1, 1, 0), new Vec3(1, 0, 1), new Vec3(1, -1, 0) }, Dir(90, 90));
        Assert.Equal(2.0, v[0], 12); Assert.Equal(4.0, v[1], 12); Assert.Equal(6.0, v[2], 12);
    }

    [Fact]
    public void Fore_aft_mirror_beyond_table_and_caps_by_nearest_face()
    {
        var f = LoadSynthetic();
        // α 135 → 거울 α 45, x → 2 − x: (0.5, 0, 1) 은 x 1.5 값 → (1.5 + 0)·1.5
        Assert.Equal(2.25, f.Sample(new[] { new Vec3(0.5, 0, 1) }, Dir(135))[0], 12);
        // 앞 끝면(x 2) 중심 r 0 → 10, r 0.5 → 10.5 · 뒤 끝면 r 1 쪽 → −11
        var c = f.Sample(new[] { new Vec3(2, 0, 0), new Vec3(2, 0.5, 0), new Vec3(0, 0, -0.95) }, Dir(0));
        Assert.Equal(10.0, c[0], 12); Assert.Equal(10.5, c[1], 12); Assert.Equal(-10.95, c[2], 12);
        // 거울이면 앞뒤 끝면이 바뀐다
        Assert.Equal(-10.0, f.Sample(new[] { new Vec3(2, 0, 0) }, Dir(180))[0], 12);
        Assert.Equal(0, f.OutOfRange);
    }

    [Fact]
    public void Face_values_one_per_triangle_and_missing_field_is_nan()
    {
        var f = LoadSynthetic();
        var mesh = TriMesh.Cylinder(1, 2, 0, new Vec3(1, 0, 0), 24);
        var v = f.FaceValues(mesh, Dir(30));
        Assert.Equal(mesh.TriangleCount, v.Length);
        Assert.All(v, x => Assert.True(double.IsFinite(x)));
        Assert.All(f.FaceValues(mesh, Dir(30), "cf"), x => Assert.True(double.IsNaN(x)));
        Assert.Equal("M0.50", f.GroupNearest(0.45));
    }

    [Fact]
    public void Streamlines_follow_roll_and_mirror_into_body_frame()
    {
        var f = LoadSynthetic();
        var (c, lines) = f.StreamlinesBody(Dir(80, 90));
        Assert.Equal(90, c!.Alpha);
        var p = lines[0].Points[0];
        Assert.Equal(-1, p.X, 12); Assert.Equal(0.5, p.Y, 12); Assert.Equal(0, p.Z, 12);   // CFD z(횡류) → 동체 +y
        var (_, m) = f.StreamlinesBody(Dir(170));
        Assert.Equal(3.0, m[0].Points[0].X, 12);                                         // x → 2 − x
    }

    [Fact]
    public void Repository_design_flow_file_is_physical_if_present()
    {
        var p = TestUtil.DesignFile("config/aero_db", "container_*_flow.json");
        if (!File.Exists(p)) return;
        var f = SurfaceFlowField.Load(p);
        Assert.Contains("M0.50", f.Groups);
        Assert.True(f.Radius is > 0.15 and < 0.17 && f.X1 - f.X0 is > 1.5 and < 1.6);
        // M 0.5 등엔트로피 정체점 Cp = ((1 + 0.2 M²)^3.5 − 1)/(0.7 M²) = 1.064 — α 90 중앙 바람 쪽, α 0 앞 끝면 중심
        double cp0 = (Math.Pow(1 + 0.2 * 0.25, 3.5) - 1) / (0.7 * 0.25);
        double xm = (f.X0 + f.X1) / 2;
        var s90 = f.Sample(new[] { new Vec3(xm, 0, f.Radius) }, Dir(90), "cp", "M0.50")[0];
        var s0 = f.Sample(new[] { new Vec3(f.X1, 0, 0) }, Dir(0), "cp", "M0.50")[0];
        Assert.InRange(s90, cp0 - 0.08, cp0 + 0.05);
        Assert.InRange(s0, cp0 - 0.08, cp0 + 0.05);
        // 뒤쪽(θ 180)은 흡입(Cp < 0)
        Assert.True(f.Sample(new[] { new Vec3(xm, 0, -f.Radius) }, Dir(90), "cp", "M0.50")[0] < 0);
    }
}
