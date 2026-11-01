using System.Text.Json.Nodes;
using Sim.Core.Analysis;
using Sim.Core.Geometry;
using Sim.Core.Models;
using Sim.Core.Numerics;
using Sim.Core.Scenario;

namespace Sim.Core.Tests;

/// <summary>PRD U절 실모델 시험 결함 수정(FX-01 기수축, FX-02 관입, FX-03 CP) 검증.</summary>
public class FieldFixTests
{
    // ---------------- FX-02 Young 관입 ----------------

    [Fact(DisplayName = "FX-02 Young: SI 계산 = 영국 단위 원식 (단위 변환 일관성)")]
    public void YoungSiMatchesImperial()
    {
        foreach (var (m, a, v) in new[] { (1.0, 2.0e-4, 30.0), (10.0, 0.005, 70.0), (100.0, 0.03, 150.0), (500.0, 0.1, 40.0) })
        {
            double dFt = Penetration.YoungImperial(m / Penetration.Lb, a / Penetration.In2, v / Penetration.Ft, 8, 0.8);
            var (d, g) = Penetration.Young(m, a, v, 8, 0.8);
            Assert.Equal(dFt * Penetration.Ft, d, 12);
            Assert.Equal(v * v / (2 * d * 9.80665), g, 9);
        }
    }

    [Fact(DisplayName = "FX-02 Young: 저속/고속 식 200 fps 연속(< 1%), 경량 보정 60 lb 연속(< 3%)")]
    public void YoungContinuity()
    {
        double lo = Penetration.YoungImperial(100, 10, 199.999, 5, 1);
        double hi = Penetration.YoungImperial(100, 10, 200.0, 5, 1);
        Assert.True(Math.Abs(lo - hi) / hi < 0.01, $"{lo} vs {hi}");
        double light = Penetration.YoungImperial(59.999, 10, 300, 5, 1);
        double heavy = Penetration.YoungImperial(60.0, 10, 300, 5, 1);
        Assert.True(Math.Abs(light - heavy) / heavy < 0.03, $"{light} vs {heavy}");
        // SI 경량 보정 0.27·m^0.4 ≡ 0.2·W^0.4
        Assert.Equal(0.2 * Math.Pow(10 / Penetration.Lb, 0.4), 0.27 * Math.Pow(10, 0.4), 1);
    }

    [Fact(DisplayName = "FX-02 Young: 관입 깊이는 속도·S·N·(m/A) 에 단조 증가")]
    public void YoungMonotone()
    {
        double D(double m, double a, double v, double s, double n) => Penetration.Young(m, a, v, s, n).Depth;
        Assert.True(D(1, 2e-4, 30, 8, 0.8) > D(1, 2e-4, 20, 8, 0.8));
        Assert.True(D(1, 2e-4, 30, 10, 0.8) > D(1, 2e-4, 30, 8, 0.8));
        Assert.True(D(1, 2e-4, 30, 8, 1.0) > D(1, 2e-4, 30, 8, 0.8));
        Assert.True(D(1, 1e-4, 30, 8, 0.8) > D(1, 2e-4, 30, 8, 0.8));
        Assert.Equal(0.56, Penetration.NoseN("flat", 0), 12);
        Assert.Equal(0.25 * 2 + 0.56, Penetration.NoseN("cone", 2), 12);
        Assert.Equal(0.18 * 2 + 0.56, Penetration.NoseN("ogive", 2), 12);
    }

    [Fact(DisplayName = "FX-02 소형 센서 조건(1.0 kg, 30 m/s): 스파이크 단면이면 관입 수 cm·수백 g, 날개 투영면적이면 mm 수준")]
    public void YoungRealSensorCondition()
    {
        // 결함 재현: 날개 포함 투영면적(≈0.03 m²)을 쓰면 비현실적으로 얕다
        var (dWing, _) = Penetration.Young(1.0, 0.03, 30.0, 8, 0.8);
        // 수정: 스파이크 Ø16 mm 단면
        var (dSpike, gSpike) = Penetration.Young(1.0, Math.PI / 4 * 0.016 * 0.016, 30.0, 8, 1.0);
        Assert.True(dWing < 0.01, $"wing {dWing}");
        Assert.InRange(dSpike, 0.05, 0.6);
        Assert.InRange(gSpike, 50, 1500);
    }

    // ---------------- FX-01 기수축 ----------------

    [Fact(DisplayName = "FX-01 AlignToX: 6방향 모두 det=+1 회전으로 기수를 +X 로, −X 는 Z(위·아래) 유지")]
    public void AlignToXAllAxes()
    {
        foreach (var name in NoseGeometry.AxisNames)
        {
            var n = NoseGeometry.ParseAxis(name);
            var r = NoseGeometry.AlignToX(n);
            Assert.True((r * n - Vec3.UnitX).Norm < 1e-12, name);
            Assert.Equal(1.0, r.Determinant(), 12);
            Assert.True((r * r.Transpose()).MaxAbsDiff(Mat3.Identity) < 1e-12);
        }
        Assert.True((NoseGeometry.AlignToX(-Vec3.UnitX) * Vec3.UnitZ - Vec3.UnitZ).Norm < 1e-12);
        // Fusion −Z 기수: FusionToBody 후 기수가 +X
        var m2b = NoseGeometry.WithNose(Frames.FusionToBody, "-Z");
        Assert.True((m2b * new Vec3(0, 0, -1) - Vec3.UnitX).Norm < 1e-12);
        Assert.True((m2b * new Vec3(0, 1, 0) - new Vec3(0, 0, -1)).Norm < 1e-12, "Fusion +Y(위) 는 동체 −Z(위) 유지");
    }

    [Fact(DisplayName = "FX-01 기수 후보 제안: 스파이크 쪽(Fusion −Z)")]
    public void SuggestNoseAxis()
    {
        var mesh = TriMesh.LoadStl(Path.Combine(TestUtil.RepoRoot, "samples", "fusion_export", "spike_sensor_sample.stl"));
        Assert.Equal("-Z", NoseGeometry.SuggestNoseAxis(mesh));
    }

    private static ScenarioDoc Spike(string nose)
    {
        var doc = ScenarioDoc.Load(TestUtil.Scenario("05_spike_sensor_nose_axis.json"));
        var body = doc.Root["bodies"]![0]!.AsObject();
        body["noseAxis"] = nose;
        body["mountAttitude"] = new JsonArray(0, 0, 0);
        doc.Root["environment"]!["wind"]!["speed"] = 0;
        return doc;
    }

    [Fact(DisplayName = "FX-01 같은 메시를 −Z/+Z 기수로: 질량특성 요 180° 거울, 낙하 궤적 동일, 노즈각 보각")]
    public void NoseAxisMirror()
    {
        var wm = Spike("-Z").Build(); var wp = Spike("+Z").Build();
        var bm = wm.GetBody("sensor"); var bp = wp.GetBody("sensor");
        Assert.Equal(bm.OwnCg.X, -bp.OwnCg.X, 12);
        Assert.Equal(bm.OwnCg.Y, -bp.OwnCg.Y, 12);
        Assert.Equal(bm.OwnCg.Z, bp.OwnCg.Z, 12);
        Assert.True(bm.OwnInertia.MaxAbsDiff(bp.OwnInertia.RotatedBy(Mat3.Diagonal(-1, -1, 1))) < 1e-12);
        Assert.True(bm.StaticMargin > 0.05, $"−Z 정적 여유 {bm.StaticMargin}");
        Assert.Equal(bm.StaticMargin!.Value, -bp.StaticMargin!.Value, 9);

        wm.Run(); wp.Run();
        var im = bm.Impact!; var ip = bp.Impact!;
        // 물리적으로는 요 180° 돌린 같은 물체: 무풍 수직 낙하면 수평 이동이 뒤집히고 나머지는 같다
        Assert.True(Math.Abs(im.Time - ip.Time) < 5e-3, $"{im.Time} vs {ip.Time}");
        Assert.True((new Vec3(im.Position.X + ip.Position.X, im.Position.Y + ip.Position.Y, 0)).Norm < 0.05);
        Assert.True(Math.Abs(im.Velocity.Norm - ip.Velocity.Norm) < 0.05);
        Assert.True(im.NoseAngleDeg < 15, $"−Z(스파이크 기수) 노즈각 {im.NoseAngleDeg}");
        Assert.True(Math.Abs(im.NoseAngleDeg + ip.NoseAngleDeg - 180) < 0.5, $"{im.NoseAngleDeg} + {ip.NoseAngleDeg}");
    }

    [Fact(DisplayName = "FX-02/03 시나리오 05: 스파이크 단면 자동 추정, 메시 CP 정적 안정, 스파이크 선행 착지")]
    public void SpikeScenario()
    {
        var doc = ScenarioDoc.Load(TestUtil.Scenario("05_spike_sensor_nose_axis.json"));
        var w = doc.Build();
        var b = w.GetBody("sensor");
        Assert.NotNull(b.Penetrator);
        Assert.Equal("shape-nose", b.Penetrator!.Source);
        Assert.InRange(b.Penetrator.Diameter, 0.014, 0.018);
        Assert.InRange(b.NoseCoefficient, 0.9, 1.2); // 원뿔 L/d≈1.9
        w.Run();
        Assert.Empty(w.Warnings);
        var imp = b.Impact!;
        Assert.True(imp.NoseAngleDeg < 15, $"노즈각 {imp.NoseAngleDeg}");
        Assert.Equal("shape-nose", imp.PenetrationSource);
        Assert.InRange(imp.PenetrationDepth, 0.03, 0.5);
        var s = Results.Summary(doc, w);
        Assert.True(J.ToDouble(s["bodies"]!["sensor"]!["staticMargin"]!) > 0.05);
        Assert.Contains("정적 여유", Results.SummaryText(s));
    }

    [Fact(DisplayName = "FX-03 측면 투영 CP: 원통 = 기하 중심, 꼬리 날개가 CP 를 뒤로")]
    public void LateralCentroid()
    {
        var cyl = TriMesh.Cylinder(0.04, 0.3, 0, new Vec3(0.15, 0, 0));
        var (cp, area) = NoseGeometry.LateralCentroid(cyl);
        Assert.True((cp - new Vec3(0.15, 0, 0)).Norm < 1e-9);
        Assert.Equal(0.08 * 0.3, area, 3);
        var finned = TriMesh.Cylinder(0.04, 0.3, 0, new Vec3(0.15, 0, 0));
        finned.Vertices.AddRange(TriMesh.Box(new Vec3(0.08, 0.2, 0.003), new Vec3(0.04, 0, 0)).Vertices);
        Assert.True(NoseGeometry.LateralCentroid(finned).Cp.X < 0.15);
    }

    [Fact(DisplayName = "FX-02 penetrator 명시 지정 · noseCoefficient 하위 호환")]
    public void PenetratorExplicit()
    {
        var w = TestUtil.Build("""
        { "schema":"mns.scenario/1", "sim":{"dt":0.001,"tEnd":30},
          "bodies":[{"name":"s","mass":1,"inertia":[0.001,0.01,0.01],"shape":{"type":"cylinder","radius":0.04,"length":0.3,"axis":"x"},
                     "penetrator":{"diameter":0.02,"nose":"ogive","lOverD":3},"position":[0,0,-50],"attitude":[0,-90,0]}] }
        """);
        var b = w.GetBody("s");
        Assert.Equal(Math.PI / 4 * 0.02 * 0.02, b.Penetrator!.Area, 12);
        Assert.Equal(0.18 * 3 + 0.56, b.NoseCoefficient, 12);
        w.Run();
        Assert.Equal("scenario", b.Impact!.PenetrationSource);

        var w2 = TestUtil.Build("""
        { "schema":"mns.scenario/1", "sim":{"dt":0.001,"tEnd":30},
          "bodies":[{"name":"s","mass":1,"inertia":[0.001,0.01,0.01],"shape":{"type":"cylinder","radius":0.04,"length":0.3,"axis":"x"},
                     "noseCoefficient":0.8,"position":[0,0,-50]}] }
        """);
        Assert.Equal(0.8, w2.GetBody("s").NoseCoefficient, 12);
        Assert.InRange(w2.GetBody("s").Penetrator!.Diameter, 0.079, 0.081); // 원통 끝 단면
    }
}
