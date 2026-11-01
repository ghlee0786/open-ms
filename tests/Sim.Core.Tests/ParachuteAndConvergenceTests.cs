using System.Globalization;
using System.Text.Json.Nodes;
using Sim.Core.Models;
using Sim.Core.Scenario;

namespace Sim.Core.Tests;

/// <summary>VV-11 낙하산 개방하중(Pflanz 해석해), NFR-02 dt 수렴(착탄점).</summary>
public class ParachuteAndConvergenceTests
{
    /// <summary>
    /// Pflanz(Knacke, NWC TP 6575 §5 의 개방하중 해석해): 중력 무시 수평 감속, (CdS)(t)=(CdS)₀·(t/t_f)^j.
    /// m dv/dt = −½ρv²(CdS)₀τ^j → 1/v = 1/v₀ + ρ(CdS)₀ t_f τ^{j+1} / (2m(j+1)),  F(τ) = ½ρv²(CdS)₀τ^j.
    /// </summary>
    private static (double Fmax, double X1) Pflanz(double m, double cds, double rho, double v0, double tf, double j)
    {
        double k = rho * cds * v0 * tf / (2 * m * (j + 1));
        double best = 0;
        for (int i = 0; i <= 200000; i++)
        {
            double tau = i / 200000.0;
            double v = v0 / (1 + k * Math.Pow(tau, j + 1));
            best = Math.Max(best, 0.5 * rho * v * v * cds * Math.Pow(tau, j));
        }
        return (best, best / (0.5 * rho * v0 * v0 * cds));
    }

    [Theory(DisplayName = "VV-11 낙하산 최대 개방하중 = Pflanz 해석해 (오차 < 1%)")]
    [InlineData(5.0, 40.0, 2.0)]     // 유한 질량(A≈0.33): 충만 중 감속으로 X1 < 1
    [InlineData(1000.0, 40.0, 2.0)]  // 무한 질량 근사: X1 → 1
    [InlineData(20.0, 60.0, 1.0)]    // 선형 충만 곡선
    [InlineData(5.0, 25.0, 6.0)]     // 늦게 부푸는 캐노피
    public void VV11_OpeningLoadPflanz(double m, double v0, double j)
    {
        const double d0 = 1.22, cd0 = 2.2, n = 8, rho = 1.225;
        string Inv(double x) => x.ToString("R", CultureInfo.InvariantCulture);
        var w = TestUtil.Build($$"""
        { "schema":"mns.scenario/1", "sim":{"dt":0.0001,"tEnd":3}, "environment":{"constantDensity":{{Inv(rho)}},"gravity":0},
          "bodies":[{"name":"b","mass":{{Inv(m)}},"inertia":[1,1,1],"aero":"none","position":[0,0,-1000],"velocity":[{{Inv(v0)}},0,0],
            "devices":[{"type":"parachute","name":"c","d0":{{Inv(d0)}},"cd0":{{Inv(cd0)}},"fillConstant":{{Inv(n)}},"fillExponent":{{Inv(j)}},
                        "lineStretchTime":0,"initialFill":0}]}],
          "events":[{"name":"d","when":{"type":"time","t":0},"do":[{"type":"deploy","device":"b.c"}]}] }
        """);
        w.Run();
        var chute = (Parachute)w.GetBody("b").Devices[0];
        double cds = cd0 * Math.PI / 4 * d0 * d0;
        var (fMax, x1) = Pflanz(m, cds, rho, chute.StretchSpeed, chute.FillTime, j);
        Assert.True(Math.Abs(chute.MaxForce - fMax) / fMax < 0.01, $"sim {chute.MaxForce:F1} N vs Pflanz {fMax:F1} N (X1={x1:F3})");
        if (m >= 1000) Assert.InRange(x1, 0.97, 1.0);
        else Assert.True(x1 < 0.97);
    }

    private static JsonObject Impact(string file, double dt)
    {
        var doc = ScenarioDoc.Load(TestUtil.Scenario(file));
        doc.Root["sim"]!["dt"] = dt;
        if (doc.Root["environment"] is JsonObject env) env["turbulence"] = "none";
        var w = doc.Build();
        w.Run();
        var o = new JsonObject();
        foreach (var b in w.Bodies.Where(b => b.Impact is not null))
            o[b.Name] = new JsonArray(b.Impact!.Position.X, b.Impact.Position.Y);
        return o;
    }

    [Theory(DisplayName = "NFR-02 dt 절반 → 착탄점 변화 < 0.1 m (기본 dt 1 ms, 난류 제외)")]
    [InlineData("01_single_sensor_drop.json")]
    [InlineData("02_box_parachute_drop.json")]
    [InlineData("05_spike_sensor_nose_axis.json")]
    public void NFR02_DtConvergence(string file)
    {
        var a = Impact(file, 0.001);
        var b = Impact(file, 0.0005);
        Assert.NotEmpty(a);
        foreach (var (name, p) in a)
        {
            var q = b[name]!.AsArray();
            double d = Math.Sqrt(Math.Pow(J.ToDouble(p![0]!) - J.ToDouble(q[0]!), 2) + Math.Pow(J.ToDouble(p[1]!) - J.ToDouble(q[1]!), 2));
            Assert.True(d < 0.1, $"{file}/{name}: Δ={d:F4} m");
        }
    }
}
