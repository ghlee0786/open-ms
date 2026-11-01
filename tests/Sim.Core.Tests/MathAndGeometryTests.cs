using Sim.Core.Geometry;
using Sim.Core.Models;
using Sim.Core.Numerics;

namespace Sim.Core.Tests;

/// <summary>VV-06 좌표·자세 변환, VV-10 공력 보간, 메시 질량특성 해석해.</summary>
public class MathAndGeometryTests
{
    [Fact(DisplayName = "VV-06 쿼터니언↔오일러↔DCM 왕복 (오차 < 1e-12)")]
    public void VV06_QuaternionEulerDcm()
    {
        var rng = new Rng(42);
        for (int i = 0; i < 2000; i++)
        {
            double roll = (rng.NextDouble() * 2 - 1) * Math.PI;
            double pitch = (rng.NextDouble() * 2 - 1) * (Math.PI / 2 - 1e-3);
            double yaw = (rng.NextDouble() * 2 - 1) * Math.PI;
            var q = Quat.FromEuler(roll, pitch, yaw);
            var e = q.ToEuler();
            Assert.True(Quat.Distance(q, Quat.FromEuler(e.X, e.Y, e.Z)) < 1e-12);
            var m = q.ToMatrix();
            Assert.True(Quat.Distance(q, Quat.FromMatrix(m)) < 1e-12);
            var v = new Vec3(rng.NextDouble(), rng.NextDouble(), rng.NextDouble());
            Assert.True((q.Rotate(v) - m * v).Norm < 1e-12);
            Assert.True((q.InverseRotate(q.Rotate(v)) - v).Norm < 1e-12);
            Assert.True((m * m.Transpose()).MaxAbsDiff(Mat3.Identity) < 1e-12);
        }
    }

    [Fact(DisplayName = "VV-06 짐벌락(θ=±90°) 근처에서도 NaN 없이 같은 회전")]
    public void VV06_GimbalLock()
    {
        foreach (double pitch in new[] { Math.PI / 2, -Math.PI / 2, Math.PI / 2 - 1e-9 })
        {
            var q = Quat.FromEuler(0.3, pitch, -1.1);
            var e = q.ToEuler();
            Assert.True(e.IsFinite);
            Assert.True(Quat.Distance(q, Quat.FromEuler(e.X, e.Y, e.Z)) < 1e-9);
        }
    }

    [Fact(DisplayName = "VV-06 좌표계 변환표: 정규직교·오른손·왕복")]
    public void VV06_Frames()
    {
        foreach (var m in new[] { Frames.FusionToBody, Frames.NedToGl, Frames.NedToEnu })
        {
            Assert.True((m * m.Transpose()).MaxAbsDiff(Mat3.Identity) < 1e-15);
            Assert.Equal(1.0, m.Determinant(), 12);
        }
        var v = new Vec3(1.5, -2.25, 3.125);
        Assert.Equal(Frames.NedToGl * v, Frames.ToGl(v));
        Assert.Equal(Frames.NedToEnu * v, Frames.ToEnu(v));
        // Fusion: 위(+Y) → 동체 위(−Z), 길이축(+Z) → 동체 전방(+X)
        Assert.Equal(new Vec3(0, 0, -1), Frames.FusionToBody * new Vec3(0, 1, 0));
        Assert.Equal(new Vec3(1, 0, 0), Frames.FusionToBody * new Vec3(0, 0, 1));
        var back = Frames.FusionToBody.Transpose() * (Frames.FusionToBody * v);
        Assert.True((back - v).Norm < 1e-15);
        // NED 아래(+D) 는 GL 아래(−y), Blender 아래(−z)
        Assert.Equal(new Vec3(0, -1, 0), Frames.ToGl(new Vec3(0, 0, 1)));
        Assert.Equal(new Vec3(0, 0, -1), Frames.ToEnu(new Vec3(0, 0, 1)));
    }

    [Fact(DisplayName = "VV-06 config/frames.json = 코드 상수(단일 출처)")]
    public void FramesJsonMatchesCode()
    {
        var o = Sim.Core.Scenario.J.ParseObject(File.ReadAllText(Path.Combine(TestUtil.RepoRoot, "config", "frames.json")));
        Assert.Equal(0, Sim.Core.Scenario.J.Mat(o["fusionToBody"]!).MaxAbsDiff(Frames.FusionToBody));
        Assert.Equal(0, Sim.Core.Scenario.J.Mat(o["nedToGl"]!).MaxAbsDiff(Frames.NedToGl));
        Assert.Equal(0, Sim.Core.Scenario.J.Mat(o["nedToBlender"]!).MaxAbsDiff(Frames.NedToEnu));
    }

    [Fact(DisplayName = "VV-10 공력 보간: 격자점 정확·선형·±180° 주기 연속")]
    public void VV10_TableInterpolation()
    {
        var t = new Table1D([0, 30, 90, 180], [0.8, 1.0, 1.25, 0.9]);
        Assert.Equal(0.8, t.Eval(0), 12);
        Assert.Equal(1.25, t.Eval(90), 12);
        Assert.Equal(1.125, t.Eval(60), 12);
        Assert.Equal(0.9, t.Eval(200), 12);
        Assert.Equal(1, t.OutOfRangeCount);

        var p = new Table1D([-180, -90, 0, 90], [1.0, 2.0, 3.0, 4.0], periodic: true);
        Assert.Equal(3.0, p.Eval(0), 12);
        Assert.Equal(2.5, p.Eval(135), 12);           // 90→180(=−180) 구간 중간
        Assert.Equal(p.Eval(179.999999), p.Eval(-180.000001), 5);
        Assert.Equal(p.Eval(-90), p.Eval(270), 12);
    }

    [Fact(DisplayName = "메시 질량특성: 정육면체 해석해 (오차 < 1e-12)")]
    public void MeshCube()
    {
        double a = 0.2;
        var mesh = TriMesh.Box(new Vec3(a, a, a), new Vec3(0.1, -0.2, 0.3));
        var (vol, cg, ipd) = mesh.VolumeProperties();
        Assert.Equal(a * a * a, vol, 12);
        Assert.True((cg - new Vec3(0.1, -0.2, 0.3)).Norm < 1e-12);
        double i = vol * (a * a + a * a) / 12;
        Assert.True(ipd.MaxAbsDiff(Mat3.Diagonal(i, i, i)) < 1e-12 * 1e3);
    }

    [Fact(DisplayName = "메시 질량특성: 원통 해석해 (512분할, 오차 < 0.1%)")]
    public void MeshCylinder()
    {
        double r = 0.05, l = 0.3;
        var mesh = TriMesh.Cylinder(r, l, 0, new Vec3(0.02, 0, 0), 512);
        var (vol, cg, ipd) = mesh.VolumeProperties();
        double vExp = Math.PI * r * r * l;
        Assert.True(Math.Abs(vol - vExp) / vExp < 1e-3);
        Assert.True((cg - new Vec3(0.02, 0, 0)).Norm < 1e-9);
        var I = ipd * (2.0 / vol); // 2 kg
        double ia = 0.5 * 2 * r * r, it = 2 * (3 * r * r + l * l) / 12;
        Assert.True(Math.Abs(I.M11 - ia) / ia < 1e-3);
        Assert.True(Math.Abs(I.M22 - it) / it < 1e-3);
        Assert.True(Math.Abs(I.M33 - it) / it < 1e-3);
        Assert.True(Math.Abs(I.M12) < 1e-9 && Math.Abs(I.M13) < 1e-9);
    }

    [Fact(DisplayName = "STL 바이너리 저장/로드 왕복 + 지지점")]
    public void StlRoundTrip()
    {
        var mesh = TriMesh.Sphere(0.5, new Vec3(1, 2, 3));
        var path = Path.Combine(Path.GetTempPath(), $"mns_{Guid.NewGuid():N}.stl");
        try
        {
            mesh.SaveBinaryStl(path);
            var back = TriMesh.LoadStl(path);
            Assert.Equal(mesh.TriangleCount, back.TriangleCount);
            var (min, max) = back.Bounds();
            Assert.True(Math.Abs(max.Z - 3.5) < 1e-6 && Math.Abs(min.Z - 2.5) < 1e-6);
            Assert.NotEmpty(back.SupportPoints());
        }
        finally { File.Delete(path); }
    }

    [Fact(DisplayName = "대칭행렬 고유값")]
    public void Eigenvalues()
    {
        var q = Quat.FromEuler(0.3, -0.7, 1.2).ToMatrix();
        var m = Mat3.Diagonal(1, 4, 9).RotatedBy(q);
        var e = m.SymmetricEigenvalues();
        Assert.Equal(1, e[0], 10); Assert.Equal(4, e[1], 10); Assert.Equal(9, e[2], 10);
    }
}
