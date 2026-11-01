using Sim.Core.Engine;
using Sim.Core.Environment;
using Sim.Core.Models;
using Sim.Core.Numerics;

namespace Sim.Core.Tests;

/// <summary>엔진 Verification (PRD O-1절 VV-01~04, VV-08 및 이벤트 위치 탐지).</summary>
public class VerificationTests
{
    private const double G = 9.80665;

    [Fact(DisplayName = "VV-01 진공 포물선 탄도 = 해석해 (상대오차 < 1e-9)")]
    public void VV01_VacuumProjectile()
    {
        var w = TestUtil.Run("""
        { "schema":"mns.scenario/1", "sim":{"dt":0.01,"tEnd":5}, "environment":{"vacuum":true},
          "bodies":[{"name":"ball","mass":1,"inertia":[0.1,0.1,0.1],"aero":"none","position":[0,0,-1000],"velocity":[10,5,-20]}] }
        """);
        var b = w.GetBody("ball");
        double t = 5;
        var expect = new Vec3(10 * t, 5 * t, -1000 - 20 * t + 0.5 * G * t * t);
        var disp = expect - new Vec3(0, 0, -1000);
        Assert.True((b.R - expect).Norm / disp.Norm < 1e-9, $"오차 {(b.R - expect).Norm}");
        Assert.True((b.V - new Vec3(10, 5, -20 + G * t)).Norm < 1e-9);
    }

    private static string DragFall(double dt, double tEnd) => $$"""
        { "schema":"mns.scenario/1", "sim":{"dt":{{dt.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"tEnd":{{tEnd}}},
          "environment":{"constantDensity":1.225},
          "bodies":[{"name":"b","mass":1,"inertia":[0.01,0.01,0.01],"aero":{"cd":1.0,"area":0.01},"position":[0,0,-5000]}] }
        """;

    private static (double V, double X) DragAnalytic(double t)
    {
        double vt = Math.Sqrt(2 * 1 * G / (1.225 * 1.0 * 0.01));
        return (vt * Math.Tanh(G * t / vt), vt * vt / G * Math.Log(Math.Cosh(G * t / vt)));
    }

    [Fact(DisplayName = "VV-02 2차 항력 낙하 = tanh 해석해 (상대오차 < 1e-6)")]
    public void VV02_QuadraticDragFall()
    {
        var w = TestUtil.Run(DragFall(0.001, 10));
        var b = w.GetBody("b");
        var (v, x) = DragAnalytic(10);
        Assert.True(Math.Abs(b.V.Z - v) / v < 1e-6, $"v={b.V.Z} vs {v}");
        Assert.True(Math.Abs(b.R.Z + 5000 - x) / x < 1e-6, $"x={b.R.Z + 5000} vs {x}");
    }

    [Fact(DisplayName = "VV-04 RK4 관측 수렴차수 3.8~4.2")]
    public void VV04_Rk4Order()
    {
        var (vExact, _) = DragAnalytic(10);
        double Err(double dt) => Math.Abs(TestUtil.Run(DragFall(dt, 10)).GetBody("b").V.Z - vExact);
        double e1 = Err(0.4), e2 = Err(0.2), e3 = Err(0.1);
        double p1 = Math.Log2(e1 / e2), p2 = Math.Log2(e2 / e3);
        Assert.InRange(p1, 3.8, 4.2);
        Assert.InRange(p2, 3.8, 4.2);
    }

    [Fact(DisplayName = "VV-03 무토크 비대칭 강체: 에너지·각운동량 보존 + 중간축 불안정")]
    public void VV03_TorqueFreeRigidBody()
    {
        var w = TestUtil.Build("""
        { "schema":"mns.scenario/1", "sim":{"dt":0.0005,"tEnd":60}, "environment":{"vacuum":true, "gravity":0},
          "bodies":[{"name":"b","mass":1,"inertia":[1,2,3],"aero":"none","position":[0,0,-1000],"omega":[0.01,2.0,0.01]}] }
        """);
        var b = w.GetBody("b");
        var I = Mat3.Diagonal(1, 2, 3);
        double E0 = 0.5 * Vec3.Dot(b.W, I * b.W);
        var H0 = b.Q.Rotate(I * b.W);
        bool flipped = false;
        while (!w.Stopped)
        {
            w.Step();
            if (b.W.Y < -1.0) flipped = true;
        }
        double E1 = 0.5 * Vec3.Dot(b.W, I * b.W);
        var H1 = b.Q.Rotate(I * b.W);
        Assert.True(Math.Abs(E1 - E0) / E0 < 1e-8, $"에너지 드리프트 {Math.Abs(E1 - E0) / E0:E2}");
        Assert.True((H1 - H0).Norm / H0.Norm < 1e-8, $"각운동량 드리프트 {(H1 - H0).Norm / H0.Norm:E2}");
        Assert.True(flipped, "중간축 회전 뒤집힘(Dzhanibekov)이 재현되지 않음");
    }

    private static string TetherScenario(double z0, double dt, double tEnd) => $$"""
        { "schema":"mns.scenario/1", "sim":{"dt":{{dt.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"tEnd":{{tEnd}}},
          "environment":{"vacuum":true},
          "carrier":{"name":"anchor","altitude":1000,"speed":0},
          "bodies":[{"name":"m","mass":2,"inertia":[0.01,0.01,0.01],"aero":"none","position":[0,0,{{z0.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}}]}],
          "connectors":[{"type":"tether","name":"t","a":"anchor","b":"m","length":1.0,"ea":2000,"damping":0}] }
        """;

    [Fact(DisplayName = "VV-08 테더 스프링-질량 고유진동수 (오차 < 0.5%)")]
    public void VV08_TetherFrequency()
    {
        double k = 2000, m = 2;
        double zEq = -1000 + 1.0 + m * G / k;
        // 진폭 5 mm < 정적 신장 mg/k = 9.8 mm → 항상 팽팽(선형 진동)
        var w = TestUtil.Build(TetherScenario(zEq + 0.005, 1e-4, 2.0));
        var b = w.GetBody("m");
        var crossings = new List<double>();
        double prevV = b.V.Z, prevT = w.T;
        while (!w.Stopped)
        {
            w.Step();
            if (prevV > 0 && b.V.Z <= 0) crossings.Add(prevT + (w.T - prevT) * prevV / (prevV - b.V.Z));
            prevV = b.V.Z; prevT = w.T;
        }
        Assert.True(crossings.Count >= 5);
        double period = (crossings[^1] - crossings[0]) / (crossings.Count - 1);
        double expected = 2 * Math.PI * Math.Sqrt(m / k);
        Assert.True(Math.Abs(period - expected) / expected < 0.005, $"주기 {period} vs {expected}");
    }

    [Fact(DisplayName = "VV-08 테더 인장 전용: 느슨하면 힘 없음(자유낙하)")]
    public void VV08_TetherTensionOnly()
    {
        var w = TestUtil.Build(TetherScenario(-1000 + 0.5, 1e-3, 0.1));
        w.Run();
        var b = w.GetBody("m");
        Assert.True(Math.Abs(b.R.Z - (-999.5 + 0.5 * G * 0.01)) < 1e-9);
        Assert.Equal(0, ((Tether)w.Connectors[0]).MaxTension);
    }

    [Fact(DisplayName = "FR-NUM-02 상태 이벤트 위치 탐지 ±1e-4 s")]
    public void EventLocation()
    {
        var w = TestUtil.Run("""
        { "schema":"mns.scenario/1", "sim":{"dt":0.01,"tEnd":10}, "environment":{"vacuum":true},
          "bodies":[{"name":"b","mass":1,"inertia":[0.1,0.1,0.1],"aero":"none","shape":{"type":"sphere","radius":0.1},"position":[0,0,-100]}],
          "events":[{"name":"below90","when":{"type":"altitude","body":"b","below":90},"do":[{"type":"log","message":"x"}]}] }
        """);
        double tEvent = w.Events[0].FiredTime;
        Assert.True(Math.Abs(tEvent - Math.Sqrt(2 * 10 / G)) <= 1.01e-4, $"이벤트 {tEvent}");
        var imp = w.GetBody("b").Impact!;
        Assert.True(Math.Abs(imp.Time - Math.Sqrt(2 * 99.9 / G)) <= 1.01e-4, $"충돌 {imp.Time}");
        Assert.Equal(BodyMode.Landed, w.GetBody("b").Mode);
    }

    [Fact(DisplayName = "FR-DYN-06 분리 시 선·각운동량 보존")]
    public void SeparationConservesMomentum()
    {
        var w = TestUtil.Build("""
        { "schema":"mns.scenario/1", "sim":{"dt":0.001,"tEnd":0.001}, "environment":{"vacuum":true,"gravity":0},
          "bodies":[
            {"name":"p","mass":10,"inertia":[1,2,2.5],"aero":"none","position":[0,0,-500],"velocity":[10,0,0],"omega":[0.1,0.2,0.5]},
            {"name":"c","parent":"p","mount":[0.5,0.1,0.2],"mass":2,"inertia":[0.1,0.1,0.1],"aero":"none"}],
          "events":[{"name":"sep","when":{"type":"time","t":0},"do":[{"type":"separate","body":"c","relVel":[0,1,3]}]}] }
        """);
        var p = w.GetBody("p");
        var P0 = p.V * p.Mass;
        var H0 = p.Q.Rotate(p.Inertia * p.W) + Vec3.Cross(p.R, p.V) * p.Mass;
        w.Step();
        var c = w.GetBody("c");
        Assert.Equal(BodyMode.Free, c.Mode);
        var P1 = p.V * p.Mass + c.V * c.Mass;
        var H1 = p.Q.Rotate(p.Inertia * p.W) + Vec3.Cross(p.R, p.V) * p.Mass + c.Q.Rotate(c.Inertia * c.W) + Vec3.Cross(c.R, c.V) * c.Mass;
        Assert.True((P1 - P0).Norm < 1e-9, $"ΔP={(P1 - P0).Norm}");
        Assert.True((H1 - H0).Norm < 1e-7, $"ΔH={(H1 - H0).Norm}");
    }

    [Fact(DisplayName = "낙하산 정상 하강률 = √(2mg/ρCdS) (오차 < 0.5%)")]
    public void ParachuteSteadyDescent()
    {
        var w = TestUtil.Run("""
        { "schema":"mns.scenario/1", "sim":{"dt":0.002,"tEnd":60}, "environment":{"constantDensity":1.225},
          "bodies":[{"name":"b","mass":5,"inertia":[0.1,0.1,0.1],"aero":"none","position":[0,0,-2000],
            "devices":[{"type":"parachute","name":"c","d0":1.22,"cd0":2.2,"attach":[0,0,0]}]}],
          "events":[{"name":"d","when":{"type":"time","t":0.5},"do":[{"type":"deploy","device":"b.c"}]}] }
        """);
        var b = w.GetBody("b");
        double cds = 2.2 * Math.PI / 4 * 1.22 * 1.22;
        double vExp = Math.Sqrt(2 * 5 * G / (1.225 * cds));
        Assert.True(Math.Abs(b.V.Z - vExp) / vExp < 0.005, $"{b.V.Z} vs {vExp}");
        var chute = (Parachute)b.Devices[0];
        Assert.True(chute.MaxG > 1.0 && chute.State == ChuteState.Open);
    }

    [Fact(DisplayName = "ISA 1976 표준값")]
    public void StandardAtmosphere()
    {
        var a = new Atmosphere();
        Assert.Equal(1.2250, a.At(0).Density, 4);
        Assert.Equal(0.7364, a.At(5000).Density, 3);
        Assert.Equal(0.3639, a.At(11000).Density, 3);
        Assert.Equal(340.29, a.At(0).SpeedOfSound, 1);
        Assert.Equal(216.65, a.At(15000).Temperature, 2);
    }
}
