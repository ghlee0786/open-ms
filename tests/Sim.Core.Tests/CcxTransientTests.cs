using System.Globalization;
using Sim.Core.Fea;
using Sim.Core.Numerics;
using Sim.Core.Structures;
using Xunit.Abstractions;

namespace Sim.Core.Tests;

/// <summary>CalculiX 모달 과도(등급 B) — 설치돼 있을 때만: 막대 끝 계단 하중의 중앙 응력 동적 증폭(≈ 2)을 보 동해석(근사 C)과 교차 확인.</summary>
public class CcxTransientTests(ITestOutputHelper output)
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "mns_ccx_shock_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void Deck_writes_frequency_storage_then_modal_dynamic_with_amplitudes_and_masses()
    {
        var mesh = FeMesh.Voxel(StressModel.DisplayMesh(new Geometry.Shape { Kind = Geometry.ShapeKind.Box, Size = new Vec3(0.2, 0.04, 0.04) }), 0.02, 10_000);
        var mat = MaterialLibrary.Find("al6061-t6")!;
        var fm = new BodyFeModel { Mesh = mesh, Material = mat, BaseMaterial = mat, Display = StressModel.DisplayMesh(new Geometry.Shape { Kind = Geometry.ShapeKind.Box, Size = new Vec3(0.2, 0.04, 0.04) }), Size = 0.02, BodyName = "bar", Surface = mesh.ExteriorFaces() };
        var p = new Vec3(-0.1, 0, 0);
        var hist = new List<CcxTransient.LoadSample>
        {
            new(0, [new PointLoad(p, new Vec3(-10, 0, 0), "pull")]),
            new(0.001, [new PointLoad(p, new Vec3(-500, 0, 0), "pull")]),
        };
        var (m, origin, notes) = CcxTransient.Build(fm, hist, [new CarriedMass("payload", 1.0, Vec3.Zero)], elasticModes: 10, dt: 1e-5);
        var dir = TempDir();
        var text = File.ReadAllText(CcxDeck.Write(m, dir, "shock"));
        Assert.Contains("*FREQUENCY, STORAGE=YES", text);
        Assert.Contains("*MODAL DYNAMIC", text);
        Assert.Contains("*MODAL DAMPING", text);
        Assert.Contains("*AMPLITUDE, NAME=A1", text);
        Assert.Contains("*CLOAD, AMPLITUDE=A1", text);
        Assert.Contains("TYPE=MASS", text);
        Assert.Equal(-CcxTransient.RampSeconds, origin, 12);
        // 탑재물 질량 합 = 1 kg
        Assert.Equal(1.0, m.PointMasses.Sum(x => x.MassEach * x.Nodes.Length), 9);
        // 시간 영역 관성 완화: 마지막 순간 절점 하중 합(x)·모멘트 ≈ 0 (가해진 500 N 대비)
        var amp = m.Amplitudes.ToDictionary(a => a.Name, a => a.Points[^1].V);
        double fx = m.AmplitudeLoads.Where(l => l.Dof == 1).Sum(l => l.Scale * amp[l.Amplitude]);
        double fy = m.AmplitudeLoads.Where(l => l.Dof == 2).Sum(l => l.Scale * amp[l.Amplitude]);
        Assert.True(Math.Abs(fx) < 1e-6 * 500 && Math.Abs(fy) < 1e-6 * 500, $"합력 {fx}, {fy}");
        Assert.Contains(m.AmplitudeLoads, l => Math.Abs(amp[l.Amplitude]) > 1); // 실제 하중이 걸림
        foreach (var n in notes) output.WriteLine(n);
    }

    [Fact]
    public void Shell_models_are_refused_before_running_the_solver()
    {
        var disp = StressModel.DisplayMesh(new Geometry.Shape { Kind = Geometry.ShapeKind.Box, Size = new Vec3(0.2, 0.04, 0.04) });
        var mesh = FeMesh.ShellFromTriangles(disp, 0.002);
        var mat = MaterialLibrary.Find("al6061-t6")!;
        var fm = new BodyFeModel { Mesh = mesh, Material = mat, BaseMaterial = mat, Display = disp, Shell = true, Size = 0.01, BodyName = "shell" };
        var hist = new List<CcxTransient.LoadSample> { new(0, [new PointLoad(Vec3.Zero, Vec3.UnitX, "p")]), new(0.001, [new PointLoad(Vec3.Zero, Vec3.UnitX * 10, "p")]) };
        var inst = new CalculixInstall { Ccx = "없음.exe" };
        var ex = Assert.Throws<NotSupportedException>(() => CcxTransient.Solve(inst, fm, hist, null, new ShockWindow("p", "p", 0, 0, 0.001, 10), disp, TempDir()));
        Assert.Contains("셸", ex.Message);
    }

    [Fact]
    public void Free_free_bar_step_load_modal_transient_amplifies_midspan_stress_like_beam_model()
    {
        if (CalculixInstall.Locate() is not { } inst) return;
        double l = 0.4, h = 0.04, force = 2000;
        var al = MaterialLibrary.Find("al6061-t6")!;
        var w = TestUtil.Build($$"""
            { "schema": "mns.scenario/1", "name": "bar", "sim": { "dt": 0.001, "tEnd": 0.01 },
              "environment": { "vacuum": true, "gravity": 0 },
              "bodies": [ { "name": "bar", "position": [0, 0, -100], "mass": {{(al.Density * l * h * h).ToString(CultureInfo.InvariantCulture)}},
                "shape": { "type": "box", "size": [{{l.ToString(CultureInfo.InvariantCulture)}}, 0.04, 0.04] }, "structure": { "material": "al6061-t6", "section": "solid" } } ] }
            """);
        var b = w.Bodies[0];
        var display = StressModel.DisplayMesh(b.Shape!);
        var dir = TempDir();
        var fm = CcxBodyFea.BuildModel(b, display, dir, size: 0.01, mesher: "voxel");
        var m = StressModel.Build(b, display)!;
        var p = m.FromAxisFrame(m.X0, 0, 0);
        var fVec = m.FromAxisFrame(-force, 0, 0);
        var hist = new List<CcxTransient.LoadSample>
        {
            new(0, [new PointLoad(p, Vec3.Zero, "pull")]),
            new(2e-6, [new PointLoad(p, fVec, "pull")]),
            new(0.0015, [new PointLoad(p, fVec, "pull")]),
        };
        var win = new ShockWindow("pull 계단", "pull", 0, 0, 0.0015, force);
        var (run, res) = CcxTransient.Solve(inst, fm, hist, null, win, display, dir, elasticModes: 30, dt: 4e-6, maxOutputs: 75);
        Assert.True(run.Ok, run.Error);
        Assert.NotNull(res);
        // 중앙(축 좌표 |x − 중간| < 1 cm) 면 평균 von Mises — 준정적 = (F/2)/A
        var mid = Enumerable.Range(0, display.TriangleCount).Where(f => Math.Abs(m.ToAxisFrame(m.Centroid[f]).X - (m.X0 + m.X1) / 2) < 0.01).ToArray();
        Assert.NotEmpty(mid);
        double qs = force / 2 / (h * h);
        double peak = res!.FaceVonMises.Max(fv => mid.Average(f => (double)fv[f]));
        // 보 동해석(근사 C) 같은 하중
        void Loads(double t, Vec3[] F, Vec3[] M) { Array.Clear(F); Array.Clear(M); if (t > 2e-6) { F[0] = fVec; M[0] = Vec3.Cross(p, fVec); } }
        var (ts, acc) = ShockResponse.Integrate(m, Loads, 0, 0.0015, zeta: 0.03, dt: 2e-6, outDt: 1e-5);
        var inp = new StressInput(Vec3.Zero, 0, 0, Vec3.Zero, [new PointLoad(p, fVec, "pull")]);
        double beamPeak = acc.Max(a => Math.Abs(m.Evaluate(inp, sliceAccel: a).AxialForce[m.Slices / 2])) / (h * h);
        output.WriteLine($"{res.Verdict}; 출력 {res.Times.Length}개, ccx {run.Seconds:F1} s");
        output.WriteLine($"중앙 σ: 준정적 {qs / 1e6:F3} MPa, CalculiX 과도 최대 {peak / 1e6:F3} MPa (DAF {peak / qs:F2}), 보 동해석 최대 {beamPeak / 1e6:F3} MPa (DAF {beamPeak / qs:F2})");
        Assert.InRange(peak / qs, 1.6, 2.4);
        Assert.InRange(peak / beamPeak, 0.8, 1.2);
    }
}
