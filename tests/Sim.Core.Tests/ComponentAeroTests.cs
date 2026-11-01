using Sim.Core.Geometry;
using Sim.Core.Models;
using Sim.Core.Numerics;

namespace Sim.Core.Tests;

/// <summary>FX-07 성분 합성(Barrowman) CP·CNα: 해석해 극한, 메시 핀 추출, 법선력 방향, 시나리오 적용.</summary>
public class ComponentAeroTests
{
    /// <summary>원뿔 노즈(+X 끝 tipX, 길이 ln) + 원통 몸통(길이 lb) 닫힌 메시.</summary>
    private static TriMesh ConeCylinder(double r, double ln, double lb, double tipX = 0, int seg = 48)
    {
        var m = new TriMesh();
        Vec3 P(double x, double a) => new(x, r * Math.Cos(a), r * Math.Sin(a));
        double xb = tipX - ln, xe = xb - lb;
        var tip = new Vec3(tipX, 0, 0);
        var tail = new Vec3(xe, 0, 0);
        for (int i = 0; i < seg; i++)
        {
            double a0 = 2 * Math.PI * i / seg, a1 = 2 * Math.PI * (i + 1) / seg;
            m.Vertices.AddRange([tip, P(xb, a1), P(xb, a0)]);                       // 노즈 원뿔
            m.Vertices.AddRange([P(xb, a0), P(xb, a1), P(xe, a1)]);                 // 원통
            m.Vertices.AddRange([P(xb, a0), P(xe, a1), P(xe, a0)]);
            m.Vertices.AddRange([tail, P(xe, a0), P(xe, a1)]);                      // 바닥
        }
        return m;
    }

    private static TriMesh WithFins(TriMesh body, double r, double span, double chord, double teX, double thick = 0.002)
    {
        foreach (var (y, z, sy, sz) in new[] { (r + span / 2, 0.0, span, thick), (-(r + span / 2), 0.0, span, thick), (0.0, r + span / 2, thick, span), (0.0, -(r + span / 2), thick, span) })
            body.Vertices.AddRange(TriMesh.Box(new Vec3(chord, sy, sz), new Vec3(teX + chord / 2, y, z)).Vertices);
        return body;
    }

    [Fact(DisplayName = "FX-07 Barrowman 극한: 원뿔 노즈 CNα=2·CP 0.666 L, 직사각 무후퇴 핀 CP = 앞전 + c/4")]
    public void BarrowmanLimits()
    {
        var bu = ComponentAero.Compute(ConeCylinder(0.04, 0.2, 0.6));
        Assert.Equal(2.0, bu.CnAlpha, 1);
        Assert.True(Math.Abs(-bu.XCp / 0.2 - 2.0 / 3) < 0.02, $"cone CP {-bu.XCp / 0.2:F3} L");
        var (cn, x) = ComponentAero.Fins(new FinSet(4, 0.1, 0.1, 0.08, 0, -0.5, 0.04, "t"), 0.08);
        Assert.Equal(-0.5 - 0.025, x, 9);
        double expect = (1 + 0.04 / 0.12) * 16 * 1.0 / (1 + Math.Sqrt(1 + Math.Pow(2 * 0.08 / 0.2, 2)));
        Assert.Equal(expect, cn, 9);
    }

    [Fact(DisplayName = "FX-07 메시 핀 자동 추출: 4장·스팬·시위 추정, 핀이 CP 를 CG 뒤로(무핀은 앞)")]
    public void MeshFinExtraction()
    {
        double r = 0.04;
        var plain = ConeCylinder(r, 0.12, 0.5);
        var finned = WithFins(ConeCylinder(r, 0.12, 0.5), r, 0.08, 0.1, -0.62);
        var b0 = ComponentAero.Compute(plain);
        var b1 = ComponentAero.Compute(finned);
        Assert.Null(b0.Fins);
        Assert.NotNull(b1.Fins);
        Assert.Equal(4, b1.Fins!.Count);
        Assert.InRange(b1.Fins.Span, 0.07, 0.09);
        Assert.InRange(b1.Fins.RootChord, 0.08, 0.12);
        double xcg = -0.33; // 몸통 중앙 부근
        Assert.True(b0.XCp > xcg, $"무핀 CP {b0.XCp}");
        Assert.True(b1.XCp < xcg - 0.05, $"핀 CP {b1.XCp}");
        // 급격한 단면 감소(베이스)는 박리로 제외: 원통 끝 바닥은 보트테일 음의 양력이 아님
        Assert.DoesNotContain(b1.Parts, p => p.Name.Contains("보트테일") && p.CnAlpha < -0.1);
    }

    [Fact(DisplayName = "FX-07 법선력: α>0 에서 CP 가 CG 뒤면 기수 내림(복원) 모멘트, 크기 q·A·CNα·sinα·cosα")]
    public void NormalForceRestoring()
    {
        var a = new AeroModel { Cd = 0, Area = 0.005, Cp = new Vec3(-0.3, 0, 0), NormalForceSlope = 8, NormalRefArea = 0.005 };
        double al = 5 * Math.PI / 180, v = 30;
        var vb = new Vec3(v * Math.Cos(al), 0, v * Math.Sin(al)); // w>0 → α>0
        var (f, m) = a.Compute(vb, Vec3.Zero, 1.2, new Vec3(-0.2, 0, 0));
        double q = 0.5 * 1.2 * v * v;
        Assert.Equal(-q * 0.005 * 8 * Math.Sin(al) * Math.Cos(al), f.Z, 9);
        Assert.True(m.Y < 0, $"pitch moment {m.Y}");
    }

    [Fact(DisplayName = "FX-07 시나리오 cp:\"component\" + fins 지정: 기수 들린 채 놓아도 기수 선행으로 바로 서서 착지")]
    public void ComponentScenarioRightsItself()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mns_fx07_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var mesh = ConeCylinder(0.03, 0.08, 0.32, 0.0);
            File.WriteAllBytes(Path.Combine(dir, "rocket.stl"), Stl(mesh));
            var w = Sim.Core.Scenario.ScenarioDoc.Parse($$"""
            { "schema":"mns.scenario/1", "sim":{"dt":0.001,"tEnd":60},
              "bodies":[{"name":"s","mass":0.8,"cg":[-0.14,0,0],"inertia":[0.0004,0.006,0.006],
                "shape":{"type":"mesh","file":"rocket.stl","unit":"m"},
                "aero":{"cp":"component","fins":{"count":4,"rootChord":0.08,"tipChord":0.04,"span":0.06,"sweep":0.04,"rootLeFromNose":0.31,"bodyRadius":0.03},"damping":[-2,-10,-10]},
                "position":[0,0,-150],"attitude":[0,60,0]}] }
            """, dir).Build();
            var b = w.GetBody("s");
            Assert.NotNull(b.Aero!.Components);
            Assert.Equal("scenario", b.Aero.Components!.Fins!.Source);
            Assert.True(b.StaticMargin > 0.05, $"SM {b.StaticMargin}");
            w.Run();
            Assert.True(b.Impact!.NoseAngleDeg < 15, $"노즈각 {b.Impact.NoseAngleDeg}");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static byte[] Stl(TriMesh m)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(new byte[80]);
        bw.Write((uint)m.TriangleCount);
        for (int i = 0; i + 2 < m.Vertices.Count; i += 3)
        {
            bw.Write(0f); bw.Write(0f); bw.Write(0f);
            for (int k = 0; k < 3; k++) { var v = m.Vertices[i + k]; bw.Write((float)v.X); bw.Write((float)v.Y); bw.Write((float)v.Z); }
            bw.Write((ushort)0);
        }
        bw.Flush();
        return ms.ToArray();
    }
}
