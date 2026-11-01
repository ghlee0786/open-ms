using System.Globalization;
using Sim.Core.Models;
using Sim.Core.Numerics;

namespace Sim.Core.Tests;

/// <summary>OpenFOAM 비정상 시간 시퀀스(mns.flow-seq/1 · 색인 mns.flow-seq-index/1) 읽기 — 'OpenFOAM 재생' 모드용 Core API.</summary>
public class CfdSequenceTests
{
    // 원통 R 1, x ∈ [0, 2], 측면 3×4(θ 0·90·180·270), 끝면 2×4. 프레임 k 의 측면 값 = 100k + x + θ/90, 앞 끝면 = 10 + r, 뒤 = −10 − r.
    // 단면 "cross": 원점 (1, −1, −1), axisU = −z(하류), axisV = +y, 3×2 격자(du 0.5), 장 u v w cp omega — 값 = 1000k + 100·장 + iu + 10·iv.
    private static string MakeSequence(int frames = 3, bool truncate = false)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"fseq_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var surf = new List<float>();
        var pl = new List<float>();
        for (int k = 0; k < frames; k++)
        {
            foreach (var x in new[] { 0.0, 1, 2 })
                foreach (var t in new[] { 0.0, 90, 180, 270 })
                    surf.Add((float)(100 * k + x + t / 90));
            foreach (var r in new[] { 0.0, 1 }) for (int j = 0; j < 4; j++) surf.Add((float)(10 + r));
            foreach (var r in new[] { 0.0, 1 }) for (int j = 0; j < 4; j++) surf.Add((float)(-10 - r));
            for (int f = 0; f < 5; f++)
                for (int iv = 0; iv < 2; iv++)
                    for (int iu = 0; iu < 3; iu++)
                        pl.Add(f == 3 && iu == 0 && iv == 0 ? float.NaN : 1000 * k + 100 * f + iu + 10 * iv);
        }
        void Bin(string name, List<float> v)
        {
            var b = new byte[v.Count * 4];
            Buffer.BlockCopy(v.ToArray(), 0, b, 0, b.Length);
            File.WriteAllBytes(Path.Combine(dir, name), truncate ? b[..^4] : b);
        }
        Bin("surface_cp.f32", surf);
        Bin("plane_cross.f32", pl);
        string ts = string.Join(",", Enumerable.Range(0, frames).Select(k => (0.01 + 0.0005 * k).ToString(CultureInfo.InvariantCulture)));
        string td = string.Join(",", Enumerable.Range(0, frames).Select(k => (4 + 0.25 * k).ToString(CultureInfo.InvariantCulture)));
        File.WriteAllText(Path.Combine(dir, "manifest.json"), $$$"""
          {"schema":"mns.flow-seq/1","name":"t","body":"container",
           "method":{"turbulence":"kOmegaSSTDDES"},
           "flow":{"alpha_deg":90,"mach":0.5,"speed":168,"rho":1.11,"q":15700,"re_d":3.5e6},
           "ref":{"d":0.30,"area":0.0707,"length":2,"d_over_u":0.00179},
           "time":{"count":{{{frames}}},"t_s":[{{{ts}}}],"t_du":[{{{td}}}],"dt_du":0.25},
           "surface":{"kind":"cylinder","radius":1,"x0":0,"x1":2,"nx":3,"ntheta":4,"nr":2,"file":"surface_cp.f32"},
           "planes":[{"name":"cross","origin":[1,-1,-1],"axisU":[0,0,-1],"axisV":[0,1,0],"normal":[1,0,0],"nu":3,"nv":2,"du":0.5,
                      "fields":["u","v","w","cp","omega"],"file":"plane_cross.f32"}],
           "forces":{"file":"forces.csv","analysis":{"St":0.21,"mean":{"CD":3.1,"CY":0.01},"cycles":7.5}},
           "status":{"partial":true,"running":false,"t_last_du":30.5,"end_du":60,"cycles":7.5,"note":"부분 결과"}}
          """);
        File.WriteAllText(Path.Combine(dir, "forces.csv"), "t_s,t_du,CX,CY,CZ,Cl,Cm,Cn,CD,CL\n0.001,0.5,0,0.2,-3,0,0.01,0,3,0\n0.002,1.0,0,-0.2,-3.2,0,0.02,0,3.2,0\n");
        return Path.Combine(dir, "manifest.json");
    }

    [Fact]
    public void Loads_manifest_and_reads_frames_lazily()
    {
        var s = CfdSequence.Load(MakeSequence());
        Assert.Equal(3, s.FrameCount);
        Assert.Equal(90, s.AlphaDeg);
        Assert.Equal(0.21, s.Strouhal, 12);
        Assert.True(s.Status.Partial);
        Assert.Equal(7.5, s.Status.Cycles, 12);
        Assert.Equal(3.1, s.MeanCoefficients["CD"], 12);
        Assert.Equal(12 + 8 + 8, s.Surface.ValuesPerFrame);
        var f2 = s.ReadSurfaceFrame(2);
        Assert.Equal(200 + 1 + 1, f2[1 * 4 + 1], 4);            // x = 1, θ 90
        var cp = s.ReadPlaneField("cross", 1, "cp");
        Assert.True(float.IsNaN(cp[0]));
        Assert.Equal(1000 + 300 + 2 + 10, cp[1 * 3 + 2], 3);
        Assert.Throws<ArgumentOutOfRangeException>(() => s.ReadSurfaceFrame(3));
        Assert.Throws<KeyNotFoundException>(() => s.ReadPlaneField("cross", 0, "T"));
    }

    [Fact]
    public void Surface_value_is_periodic_in_theta_and_uses_caps()
    {
        var s = CfdSequence.Load(MakeSequence());
        var f = s.ReadSurfaceFrame(0);
        Assert.Equal(1 + 1, s.SurfaceValue(f, new Vec3(1, 1, 0)), 9);            // +y = θ 90
        Assert.Equal(1 + 3, s.SurfaceValue(f, new Vec3(1, -1, 0)), 9);           // −y = θ 270
        // θ 315: 270(값 4) 과 360 = 0(값 1) 사이 → 주기 경계 보간 2.5
        double a = Math.PI / 4;
        Assert.Equal(2.5, s.SurfaceValue(f, new Vec3(1, -Math.Sin(a), Math.Cos(a))), 9);
        Assert.Equal(10.5, s.SurfaceValue(f, new Vec3(2, 0.5, 0)), 9);            // 앞 끝면 r 0.5
        Assert.Equal(-10.0, s.SurfaceValue(f, new Vec3(0, 0, 0)), 9);
    }

    [Fact]
    public void Plane_value_maps_body_points_onto_grid()
    {
        var s = CfdSequence.Load(MakeSequence());
        var pl = s.Plane("cross");
        var u = s.ReadPlaneField("cross", 0, "u");
        // 점(iu 1.5, iv 0.5) = 원점 + 0.75·(−z) + 0.25·(+y)
        var p = pl.Point(1.5, 0.5);
        Assert.Equal(new Vec3(1, -0.75, -1.75), p);
        Assert.Equal(1.5 + 5, CfdSequence.PlaneValue(pl, u, p), 9);
        Assert.True(double.IsNaN(CfdSequence.PlaneValue(pl, u, new Vec3(1, 5, 0))));
    }

    [Fact]
    public void Frame_lookup_loops_playback_time()
    {
        var s = CfdSequence.Load(MakeSequence(4));        // t_du 4.0 4.25 4.5 4.75
        Assert.Equal(0, s.FrameAt(4.0));
        Assert.Equal(2, s.FrameAt(4.45));
        Assert.Equal(0, s.FrameAt(5.0));                    // 길이 1.0(마지막 프레임 + 한 간격) 뒤 되감기
        Assert.Equal(1, s.FrameAt(5.25));
        Assert.Equal(3, s.FrameAt(9.0, loop: false));
    }

    [Fact]
    public void Forces_csv_is_read_by_header_names()
    {
        var s = CfdSequence.Load(MakeSequence());
        var fr = s.ReadForces();
        Assert.Equal(2, fr.Count);
        Assert.Equal(-0.2, fr[1].CY, 12);
        Assert.Equal(3.2, fr[1].CD, 12);
    }

    [Fact]
    public void Truncated_binary_or_wrong_schema_is_rejected()
    {
        Assert.Throws<InvalidDataException>(() => CfdSequence.Load(MakeSequence(truncate: true)));
        var p = MakeSequence();
        File.WriteAllText(p, File.ReadAllText(p).Replace("mns.flow-seq/1", "mns.flow-seq/9"));
        Assert.Throws<InvalidDataException>(() => CfdSequence.Load(p));
    }

    [Fact]
    public void Repository_flow_sequence_index_is_readable_and_opens_sequence_if_present()
    {
        var p = TestUtil.DesignFile("config/aero_db", "*_flowseq.json");
        if (!File.Exists(p)) return;
        var ix = CfdSequenceIndex.Load(p);
        Assert.NotEmpty(ix.Sequences);
        foreach (var e in ix.Sequences)
        {
            Assert.Equal(81 * 72 + 2 * 9 * 72, e.MeanCp.Length);
            Assert.All(e.MeanCp, v => Assert.True(double.IsFinite(v)));
            var s = ix.TryOpen(e);                       // 큰 시퀀스(results/, git 밖)는 계산한 PC 에만 있다
            if (s is null) continue;
            Assert.Equal(e.Alpha, s.AlphaDeg, 9);
            var f = s.ReadSurfaceFrame(s.FrameCount - 1);
            Assert.InRange(f.Max(), 0.8f, 1.5f);                                     // 정체점 Cp ≈ 1(M 0.5 압축성 ≈ 1.06)
            if (s.AlphaDeg >= 60)        // 횡류: 길이 중앙 +z(바람 맞는 쪽)
                Assert.True(s.SurfaceValue(f, new Vec3((s.Surface.X0 + s.Surface.X1) / 2, 0, s.Surface.Radius)) > 0.8);
            else if (s.AlphaDeg <= 30)   // 축류: 앞 끝면(x = x1) 중심
                Assert.True(s.SurfaceValue(f, new Vec3(s.Surface.X1, 0, 0)) > 0.8);
            foreach (var pl in s.Planes)
                Assert.Contains(s.ReadPlaneField(pl.Name, 0, "omega"), v => float.IsFinite(v));
        }
    }

    [Fact]
    public void Index_resolves_manifest_relative_to_repository_root()
    {
        var man = MakeSequence();
        var root = Path.Combine(Path.GetTempPath(), $"fidx_{Guid.NewGuid():N}");
        var seqDir = Path.Combine(root, "results", "cfd", "flowseq", "t");
        Directory.CreateDirectory(Path.GetDirectoryName(seqDir)!);
        Directory.Move(Path.GetDirectoryName(man)!, seqDir);
        Directory.CreateDirectory(Path.Combine(root, "config", "aero_db"));
        var idx = Path.Combine(root, "config", "aero_db", "c_flowseq.json");
        File.WriteAllText(idx, """
          {"schema":"mns.flow-seq-index/1","sequences":[
            {"name":"t","group":"M0.50","label":"M0.50 α 90°","alpha":90,"mach":0.5,"speed":168,"manifest":"results/cfd/flowseq/t/manifest.json",
             "frames":3,"status":{"partial":true,"note":"부분"},"St":0.21,"cycles":7.5,"mean":{"CD":3.1},
             "preview":{"t_du":[0,1],"CY":[0.1,-0.1]},"meanCp":[1,2,3]},
            {"name":"gone","alpha":45,"manifest":"results/cfd/flowseq/gone/manifest.json","status":{}}]}
          """);
        var ix = CfdSequenceIndex.Load(idx);
        Assert.Equal(2, ix.Sequences.Count);
        var e = ix.Sequences[0];
        Assert.Equal(0.21, e.Strouhal, 12);
        Assert.Equal(new[] { 0.1, -0.1 }, e.PreviewCY);
        Assert.Equal(Path.Combine(seqDir, "manifest.json"), ix.ManifestPath(e));
        Assert.Equal(3, ix.TryOpen(e)!.FrameCount);
        Assert.Null(ix.TryOpen(ix.Sequences[1]));             // 큰 시퀀스가 없으면 미리보기만
    }
}
