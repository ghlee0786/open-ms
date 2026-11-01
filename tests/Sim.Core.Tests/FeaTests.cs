using Sim.Core.Fea;
using Sim.Core.Geometry;
using Sim.Core.Numerics;
using Sim.Core.Scenario;
using Sim.Core.Structures;

namespace Sim.Core.Tests;

/// <summary>
/// OpenCourant(Radioss) 연동(FR-MAT-05): 덱 작성·결과 파서·미설치/실행 실패/결과 손상 처리(VV-61 ⑩).
/// 실제 솔버 실행은 MNS_RUN_OPENCOURANT=1 이고 설치가 있을 때만(대표 검증: Taylor 봉).
/// </summary>
public class FeaTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "mns_fea_tests", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void Shell_impact_deck_has_all_cards_and_one_element_per_face()
    {
        var mesh = StressModel.DisplayMesh(new Shape { Kind = ShapeKind.Box, Size = new Vec3(0.40, 0.27, 0.27) });
        var mat = MaterialLibrary.Find("al6061-t6")!;
        var deck = RadiossDeck.WriteShellImpact(TempDir(), "box", mesh, mat, 0.002, new Vec3(0, 0, 7.5), new Vec3(0, 0, 0.2), new Vec3(0, 0, -1), 0.01);
        var text = File.ReadAllText(deck.StarterPath);
        foreach (var card in new[] { "#RADIOSS STARTER", "/BEGIN", "/NODE", "/PART/1", "/PROP/SHELL/1", "/MAT/PLAS_JOHNS/1", "/SH3N/1", "/GRNOD/PART/1", "/INIVEL/TRA/1", "/RWALL/PLANE/1", "/END" })
            Assert.Contains(card, text);
        Assert.Equal(mesh.TriangleCount, deck.Elements);
        // 용접 후 절점 수 = 구조 격자 고유 정점 수(중복 제거)
        Assert.Equal(mesh.Vertices.Distinct().Count(), deck.Nodes);
        // 고정 폭 필드: /NODE 줄 = I10 + 3×F20
        var nodeLine = text.Split('\n').SkipWhile(l => !l.StartsWith("/NODE")).Skip(1).First().TrimEnd('\r');
        Assert.Equal(70, nodeLine.Length);
        var eng = File.ReadAllText(deck.EnginePath);
        Assert.Contains("/RUN/box/1", eng);
        Assert.Contains("/ANIM/ELEM/EPSP", eng);
    }

    [Fact]
    public void Taylor_bar_deck_counts_and_reference_ratio()
    {
        var mat = MaterialLibrary.Find("al6061-t6")!;
        var d = RadiossDeck.WriteTaylorBar(TempDir(), "taylor", mat, 0.0254, 0.0076, 200, n: 4, nl: 10);
        Assert.Equal(4 * 4 * 10, d.Elements);
        Assert.Equal(5 * 5 * 11, d.Nodes);
        Assert.Contains("/BRICK/1", File.ReadAllText(d.StarterPath));
        double ratio = RadiossDeck.TaylorFinalLengthRatio(mat.Density, 200, mat.Yield);
        Assert.Equal(Math.Exp(-2700.0 * 200 * 200 / (2 * 276e6)), ratio, 12); // 6061-T6 200 m/s ≈ 0.822
    }

    [Fact]
    public void Vtk_parser_reads_points_cells_and_plastic_strain_and_rejects_truncation()
    {
        var dir = TempDir();
        var ok = Path.Combine(dir, "a.vtk");
        File.WriteAllText(ok, """
            # vtk DataFile Version 3.0
            test
            ASCII
            DATASET UNSTRUCTURED_GRID
            POINTS 3 float
            0 0 0  1 0 0  0 1 0
            CELLS 1 4
            3 0 1 2
            CELL_TYPES 1
            5
            CELL_DATA 1
            SCALARS EPSP float 1
            LOOKUP_TABLE default
            0.125
            POINT_DATA 3
            VECTORS Displacement float
            0 0 0  0 0 -0.01  0 0 0
            """);
        var v = VtkData.Parse(ok);
        Assert.Equal(3, v.Points.Count);
        Assert.Single(v.Cells);
        Assert.Equal(0.125, v.PlasticStrain![0], 12);
        Assert.Equal(-0.01, v.Displacement![1].Z, 12);
        var bad = Path.Combine(dir, "b.vtk");
        File.WriteAllText(bad, "# vtk DataFile Version 3.0\nx\nASCII\nDATASET UNSTRUCTURED_GRID\nPOINTS 3 float\n0 0 0 1 0");
        Assert.Throws<InvalidDataException>(() => VtkData.Parse(bad));
        Assert.Throws<FileNotFoundException>(() => VtkData.Parse(Path.Combine(dir, "none.vtk")));
    }

    [Fact]
    public void Missing_install_and_broken_executables_fail_cleanly()
    {
        Assert.Null(OpenCourantInstall.TryLocate(Path.Combine(TempDir(), "nope"), out var why));
        Assert.Contains("없습니다", why);
        // exec 는 있지만 실행 파일이 깨진 경우: 예외 대신 실패 결과
        var root = TempDir();
        Directory.CreateDirectory(Path.Combine(root, "exec"));
        File.WriteAllText(Path.Combine(root, "exec", "starter_win64.exe"), "not an executable");
        File.WriteAllText(Path.Combine(root, "exec", "engine_win64.exe"), "not an executable");
        var inst = OpenCourantInstall.Locate(root)!;
        Assert.NotNull(inst);
        var mesh = StressModel.DisplayMesh(new Shape { Kind = ShapeKind.Box, Size = new Vec3(0.2, 0.2, 0.2) });
        var deck = RadiossDeck.WriteShellImpact(TempDir(), "x", mesh, MaterialLibrary.Find("abs")!, 0.002, new Vec3(0, 0, 5), new Vec3(0, 0, 0.11), new Vec3(0, 0, -1), 0.005);
        var r = OpenCourantRunner.Run(inst, deck, TimeSpan.FromSeconds(20));
        Assert.False(r.Ok);
        Assert.Equal("starter", r.Stage);
        Assert.False(string.IsNullOrWhiteSpace(r.Error));
    }

    [Fact]
    public void Landing_case_requires_material_thickness_and_impact()
    {
        var doc = ScenarioDoc.Load(TestUtil.Scenario("01_single_sensor_drop.json"));
        var w = doc.Build();
        w.Run();
        var b = w.Bodies.First(x => x.Impact is not null);
        Assert.Throws<InvalidDataException>(() => FeaImpactCase.FromLanding(w, b, StressModel.DisplayMesh(b.Shape!)));
        b.Structure = StructureDef.Parse(new System.Text.Json.Nodes.JsonObject { ["material"] = "abs", ["thickness"] = 0.002 }, null, b.Shape, new List<string>(), b.Name);
        var s = FeaImpactCase.FromLanding(w, b, StressModel.DisplayMesh(b.Shape!));
        Assert.True(s.Velocity.Z > 0);                         // NED 아래로 충돌
        Assert.True(s.MeshFe.Vertices.Max(v => v.Z) < s.WallPoint.Z);
        Assert.InRange(s.TEnd, 2e-3, 0.05);
    }

    /// <summary>설치되어 있으면 실제 솔버 실행(PRD VV-61 ⑩ "설치 시 소형 검증 케이스 실행"), MNS_SKIP_OPENCOURANT=1 이면 생략.</summary>
    internal static OpenCourantInstall? Installed() =>
        System.Environment.GetEnvironmentVariable("MNS_SKIP_OPENCOURANT") == "1" ? null : OpenCourantInstall.Locate();

    [Fact]
    public void Taylor_bar_runs_on_installed_solver()
    {
        if (Installed() is not { } inst) return;
        var mat = MaterialLibrary.Find("al6061-t6")!;
        var d = RadiossDeck.WriteTaylorBar(TempDir(), "taylor", mat, 0.0254, 0.0076, 200);
        var r = OpenCourantRunner.Run(inst, d, TimeSpan.FromMinutes(10));
        Assert.True(r.Ok, r.Error);
        Assert.False(r.Diverged);
        double lf = RadiossDeck.TaylorLength(r.Final!);
        double expected = RadiossDeck.TaylorFinalLengthRatio(mat.Density, 200, mat.Yield);
        Assert.InRange(lf / 0.0254 / expected, 0.85, 1.15);
        Assert.True(lf < 0.0254);                              // 실제로 짧아짐(영구 변형)
    }

    [Fact]
    public void Taylor_bar_shortening_grows_with_speed_on_installed_solver()
    {
        if (Installed() is not { } inst) return;
        var mat = MaterialLibrary.Find("al6061-t6")!;
        double Lf(double v)
        {
            var r = OpenCourantRunner.Run(inst, RadiossDeck.WriteTaylorBar(TempDir(), "taylor", mat, 0.0254, 0.0076, v), TimeSpan.FromMinutes(10));
            Assert.True(r.Ok, r.Error);
            return RadiossDeck.TaylorLength(r.Final!);
        }
        double l1 = Lf(120), l2 = Lf(200), l3 = Lf(280);
        Assert.True(l1 > l2 && l2 > l3, $"{l1} {l2} {l3}");
    }

    /// <summary>같은 상자·같은 꼭짓점 착지: 실제 솔버로 플라스틱(ABS)은 요소 삭제(깨짐), 양철은 넓은 소성(찌그러짐).</summary>
    [Fact]
    public void Plastic_breaks_and_tinplate_dents_on_installed_solver()
    {
        if (Installed() is not { } inst) return;
        var root = TempDir();
        var abs = FeaBenchmarks.CornerDrop(inst, Path.Combine(root, "abs"), MaterialLibrary.Find("abs")!, 0.002);
        var tin = FeaBenchmarks.CornerDrop(inst, Path.Combine(root, "tin"), MaterialLibrary.Find("tinplate")!, 0.00025);
        Assert.Equal(FailureState.Fracture, abs.State);        // 깨짐: 삭제 요소
        Assert.True(abs.Deleted > 0, abs.Verdict);
        Assert.True(tin.State >= FailureState.Yield, tin.Verdict);
        Assert.True(tin.Yielded > 3 * Math.Max(tin.Deleted, 1), tin.Verdict);   // 연성: 소성 영역이 찢어진 곳보다 훨씬 넓다
        Assert.True(abs.Yielded <= 2 * abs.Deleted + 5, abs.Verdict);            // 취성: 소성이 거의 없이 깨짐
    }

    [Fact]
    public void Rigid_motion_is_removed_from_deformation_measure()
    {
        var rng = new Random(3);
        var p0 = Enumerable.Range(0, 400).Select(_ => new Vec3(rng.NextDouble() - 0.5, rng.NextDouble() - 0.5, rng.NextDouble() - 0.5)).ToList();
        var q = Numerics.Quat.FromEuler(0.7, -0.4, 1.9);
        var t = new Vec3(3, -2, 0.5);
        var rigid = p0.Select(p => (p, q.Rotate(p) + t)).ToList();
        Assert.True(FeaOutcome.RigidResidualMax(rigid) < 1e-9);       // 회전·이동만 → 변형 0
        var dented = rigid.Select((x, i) => i == 7 ? (x.p, x.Item2 + new Vec3(0, 0, 0.05)) : x).ToList();
        Assert.InRange(FeaOutcome.RigidResidualMax(dented), 0.045, 0.055); // 한 점 5 cm 눌림 → ≈ 5 cm
    }

    [Fact]
    public void Termination_detection_accepts_warnings_but_not_errors()
    {
        var dir = TempDir();
        string W(string name, string body) { var p = Path.Combine(dir, name); File.WriteAllText(p, body); return p; }
        Assert.True(OpenCourantRunner.Normal(W("a.out", "...\n     NORMAL TERMINATION\n")));
        Assert.True(OpenCourantRunner.Normal(W("b.out", "     TERMINATION WITH WARNING\n              0 ERROR(S)\n              2 WARNING(S)\n")));
        Assert.False(OpenCourantRunner.Normal(W("c.out", "     TERMINATION WITH WARNING\n             10 ERROR(S)\n")));
        Assert.False(OpenCourantRunner.Normal(W("d.out", "     ERROR TERMINATION\n              1 ERROR(S)\n")));
        Assert.False(OpenCourantRunner.Normal(Path.Combine(dir, "none.out")));
    }

    [Fact]
    public void Vtk_parser_handles_real_anim_to_vtk_layout()
    {
        // anim_to_vtk 실제 형식: 전역 FIELD(TIME, CYCLE), 강체 벽 셀(ELEMENT_ID 0) 앞, 2D/3D 배열 분리, 발산 시 inf
        var p = Path.Combine(TempDir(), "r.vtk");
        File.WriteAllText(p, """
            # vtk DataFile Version 3.0
            vtk output
            ASCII
            DATASET UNSTRUCTURED_GRID
            FIELD FieldData 2
            TIME 1 1 double
            0.00125
            CYCLE 1 1 int
            700
            POINTS 5 float
            0 0 0  1 0 0  0 1 0  -inf inf 0  0 0 nan
            CELLS 3 13
            4 3 3 3 3
            3 0 1 2
            3 0 2 4
            CELL_TYPES 3
            9 5 5
            POINT_DATA 5
            SCALARS NODE_ID int 1
            LOOKUP_TABLE default
            0 1 2 3 4
            VECTORS Displacement float
            0 0 0  0 0 -0.01  0 0 0  0 0 0  0 0 0
            CELL_DATA 3
            SCALARS ELEMENT_ID int 1
            LOOKUP_TABLE default
            0 1 2
            SCALARS EROSION_STATUS int 1
            LOOKUP_TABLE default
            1 1 0
            SCALARS 2DELEM_Plastic_Strain float 1
            LOOKUP_TABLE default
            0 0.02 0.07
            SCALARS 3DELEM_Plastic_Strain float 1
            LOOKUP_TABLE default
            0.5 0 0
            """);
        var v = VtkData.Parse(p);
        Assert.Equal(0.00125, v.Time, 12);
        Assert.True(v.HasNonFinitePoints);
        var eps = v.ByElement(v.PlasticStrain!, 2);
        Assert.Equal(new[] { 0.02, 0.07 }, eps);                 // 벽 셀(0.5)은 버리고 요소 1,2 순
        Assert.Equal(new[] { 1.0, 0.0 }, v.ByElement(v.Erosion!, 2));
        Assert.Equal(4, v.ElementPoints().Count());             // 벽 점(3) 제외: 요소가 쓰는 점 0,1,2,4
    }
}
