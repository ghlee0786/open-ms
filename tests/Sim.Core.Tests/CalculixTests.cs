using Sim.Core.Fea;
using Sim.Core.Geometry;
using Sim.Core.Numerics;
using Sim.Core.Structures;
using Xunit.Abstractions;

namespace Sim.Core.Tests;

/// <summary>
/// CalculiX 연동(PRD Z절 FR-FEA-01~06): 덱·파서·메셔 단위 테스트 + 설치되어 있으면 실제 ccx 로 해석해 검증.
/// MNS_SKIP_CALCULIX=1 이면 실제 실행 생략.
/// </summary>
public class CalculixTests(ITestOutputHelper output)
{
    internal static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "mns_ccx_tests", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    internal static CalculixInstall? Installed() =>
        System.Environment.GetEnvironmentVariable("MNS_SKIP_CALCULIX") == "1" ? null : CalculixInstall.Locate();

    private void Check(CcxCheck c)
    {
        output.WriteLine($"{c.Id} {c.Name}: {c.Measured:G5} vs {c.Reference:G5} {c.Quantity} (비 {c.Ratio:F4}, 허용 ±{c.Tolerance:P0})");
        Assert.True(c.Pass, $"{c.Id} {c.Name}: {c.Measured:G5} vs {c.Reference:G5} (비 {c.Ratio:F4})");
    }

    [Fact]
    public void Cantilever_matches_beam_theory_on_installed_solver()
    {
        if (Installed() is not { } inst) return;
        var (d, s) = CcxBenchmarks.Cantilever(inst, TempDir());
        Check(d); Check(s);
    }

    [Fact]
    public void Roark_plate_matches_on_installed_solver()
    {
        if (Installed() is not { } inst) return;
        var (s, w) = CcxBenchmarks.RoarkPlate(inst, TempDir());
        Check(s); Check(w);
    }

    [Fact]
    public void Cylinder_hoop_and_shell_pressure_sign_on_installed_solver()
    {
        if (Installed() is not { } inst) return;
        var (h, u) = CcxBenchmarks.CylinderHoop(inst, TempDir());
        Check(h); Check(u);
    }

    [Fact]
    public void Kirsch_hole_concentration_on_installed_solver()
    {
        if (Installed() is not { } inst) return;
        Check(CcxBenchmarks.KirschHole(inst, TempDir()));
    }

    [Fact]
    public void Beam_frequency_on_installed_solver()
    {
        if (Installed() is not { } inst) return;
        Check(CcxBenchmarks.BeamFrequency(inst, TempDir()));
    }

    [Fact]
    public void Euler_buckling_on_installed_solver()
    {
        if (Installed() is not { } inst) return;
        Check(CcxBenchmarks.EulerColumn(inst, TempDir()));
    }

    [Fact]
    public void OpenCourant_and_CalculiX_agree_in_static_limit_on_installed_solvers()
    {
        if (Installed() is not { } inst || FeaTests.Installed() is not { } oc) return;
        var c = CcxBenchmarks.StaticLimitCrossCheck(inst, oc, TempDir());
        output.WriteLine(c.Note);
        Check(c);
    }

    // ---------------- 시뮬레이션 물체 → CalculiX (FR-FEA-02·03) ----------------

    internal static (Engine.World W, Models.Body B, FeaLoadInput Lc, StressModel Model) ContainerMaxLoad()
    {
        var w = Scenario.ScenarioDoc.Load(TestUtil.Scenario("06_munition_3petal_split.json")).Build();
        w.Run();
        var b = w.Bodies.First(x => x.Name == "c2");
        var model = StressModel.Build(b)!;
        var inst = LoadCaseExport.Instants(w, b, 12);
        Assert.NotEmpty(inst);
        // FE 에 걸리는 외력이 가장 큰 순간(CLI 기본 선택과 같음)
        var best = inst.Select(x => CcxBodyFea.FromWorld(w, model, x.Frame, x.Why)).MaxBy(lc => lc.Severity(model.Normal, model.Area))!;
        return (w, b, best, model);
    }

    [Fact]
    public void Loaded_model_is_self_equilibrated_by_inertia_relief()
    {
        var (_, b, lc, model) = ContainerMaxLoad();
        var fm = CcxBodyFea.BuildModel(b, model.Mesh, TempDir());
        Assert.True(fm.Shell);
        Assert.Equal(model.FaceCount, fm.Mesh.Elements.Count);           // 셸 요소 = 표시 면 1:1
        Assert.InRange(fm.Mesh.MassProperties(fm.Material.Density).Mass, b.OwnMass * 0.999, b.OwnMass * 1.001); // 질량 환산
        var (m, relief) = CcxBodyFea.BuildLoaded(fm, lc);
        Assert.True(relief.ExternalForce.Norm > 0);
        // 체적 관성력(GRAV) + 원심(CENTRIF, CG 축) + 외력 = 0 (힘·모멘트)
        var inertia = Vec3.Zero; var moment = Vec3.Zero;
        var om = lc.Omega;
        for (int e = 0; e < fm.Mesh.Elements.Count; e++)
        {
            var r = fm.Mesh.ElementCentroid(e) - relief.Cg;
            var f = (m.ElementAccel![e] - Vec3.Cross(om, Vec3.Cross(om, r))) * (fm.Material.Density * fm.Mesh.ElementVolume(e));
            inertia += f; moment += Vec3.Cross(fm.Mesh.ElementCentroid(e) - relief.Cg, f);
        }
        Assert.True((inertia + relief.ExternalForce).Norm < 1e-6 * relief.ExternalForce.Norm + 1e-9, $"{inertia} + {relief.ExternalForce}");
        var mRef = relief.ExternalMoment.Norm + relief.ExternalForce.Norm * 0.4;
        Assert.True((moment + relief.ExternalMoment).Norm < 0.02 * mRef + 1e-9, $"{moment} + {relief.ExternalMoment}");
        Assert.Equal(6, m.Fixed.Sum(x => x.Last - x.First + 1));           // 3-2-1 = 자유도 6개
        output.WriteLine($"{lc.Name}: F={relief.ExternalForce} a={relief.AccelG:F2} G (기록 {lc.RecordedAccel.Norm / 9.80665:F2} G)");
    }

    [Fact]
    public void Container_shell_static_from_simulation_on_installed_solver()
    {
        if (Installed() is not { } inst) return;
        var (w, b, lc, model) = ContainerMaxLoad();
        var dir = TempDir();
        var fm = CcxBodyFea.BuildModel(b, model.Mesh, dir);
        var (r, res) = CcxBodyFea.Solve(inst, fm, CcxAnalysis.Static, lc, Path.Combine(dir, "static"));
        Assert.True(r.Ok, r.Error);
        Assert.NotNull(res);
        Assert.Equal(model.FaceCount, res!.FaceVonMises.Length);
        Assert.True(res.MaxVonMises > 0 && double.IsFinite(res.MaxVonMises));
        // 관성 완화: 지지 반력 ≈ 0 (외력 대비 2% 미만)
        Assert.NotNull(res.Reaction);
        Assert.True(res.Reaction!.Value.Norm < 0.02 * res.Relief!.ExternalForce.Norm + 1, $"반력 {res.Reaction} vs 외력 {res.Relief.ExternalForce}");
        var approx = model.Evaluate(model.InputFrom(w, w.Recorder.Frames[w.Recorder.IndexAt(lc.Time)]));
        output.WriteLine($"{lc.Name}: CalculiX σvm max {res.MaxVonMises / 1e6:F2} MPa @ {res.MaxVonMisesAt}, 근사 {approx.MaxVonMises / 1e6:F2} MPa (비 {approx.MaxVonMises / res.MaxVonMises:F2}), 변위 {res.MaxDisplacement * 1e3:F3} mm, 반력 {res.Reaction}, {res.Verdict}");
        foreach (var n in fm.Notes) output.WriteLine(n);
    }

    private static string SolidBarScenario(double mass) => $$"""
        { "schema": "mns.scenario/1", "name": "solid_bar", "sim": { "dt": 0.001, "tEnd": 0.05, "outputRate": 100, "seed": 1 },
          "environment": { "groundAltitude": 0, "wind": { "speed": 0, "directionFrom": 0, "shear": "none" }, "turbulence": "none" },
          "bodies": [ { "name": "bar", "position": [0, 0, -100], "mass": {{mass.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, "attitude": [0, 0, 0],
            "shape": { "type": "box", "size": [0.4, 0.04, 0.04] }, "structure": { "material": "al6061-t6", "section": "solid" } } ] }
        """;

    [Theory]
    [InlineData("gmsh")]
    [InlineData("voxel")]
    public void Free_free_solid_bar_modes_match_beam_theory_on_installed_solver(string mesher)
    {
        if (Installed() is not { } inst) return;
        var al = MaterialLibrary.Find("al6061-t6")!;
        double L = 0.4, h = 0.04;
        var w = TestUtil.Build(SolidBarScenario(al.Density * L * h * h));
        var b = w.Bodies[0];
        var dir = TempDir();
        var display = StressModel.DisplayMesh(b.Shape!);
        var fm = CcxBodyFea.BuildModel(b, display, dir, size: 0.01, mesher: mesher);
        Assert.InRange(fm.DensityScale, 0.97, 1.03);
        var (r, res) = CcxBodyFea.Solve(inst, fm, CcxAnalysis.Modal, null, Path.Combine(dir, "modal"));
        Assert.True(r.Ok, r.Error);
        double rigid = CcxBodyFea.RigidHz(r.Dat.FrequenciesHz);
        output.WriteLine($"{mesher}: {string.Join(", ", r.Dat.FrequenciesHz.Select(f => f.ToString("G4")))}");
        Assert.Equal(6, r.Dat.FrequenciesHz.Count(f => f <= rigid));          // 강체 모드 6개(약한 접지 스프링)
        Assert.True(r.Dat.FrequenciesHz.Where(f => f <= rigid).Max() < 0.01 * r.Dat.FrequenciesHz.First(f => f > rigid));
        double f1 = r.Dat.FrequenciesHz.First(f => f > rigid);
        double fRef = 22.373 / (2 * Math.PI * L * L) * Math.Sqrt(al.E * Math.Pow(h, 4) / 12 / (al.Density * h * h));
        output.WriteLine($"{mesher}: {fm.Mesh.Source}, f1 {f1:F1} Hz vs Euler–Bernoulli 자유-자유 {fRef:F1} Hz (비 {f1 / fRef:F3}) — {res!.Verdict}");
        // L/h = 10: 전단·회전 관성으로 Euler–Bernoulli 보다 2~4% 낮음
        Assert.InRange(f1 / fRef, 0.93, 1.02);
        // 모드 응력은 정규화 값 — 안전계수를 내면 안 된다(실모델 modal 'SF 0.0002' 회귀, 2026-10-07)
        Assert.True(double.IsPositiveInfinity(res.MinSafety));
        Assert.All(res.FaceSafety, sf => Assert.True(double.IsPositiveInfinity(sf)));
    }

    [Theory]
    [InlineData("gmsh")]
    [InlineData("voxel")]
    public void Solid_inertia_relief_reaction_is_negligible_on_installed_solver(string mesher)
    {
        if (Installed() is not { } inst) return;
        var al = MaterialLibrary.Find("al6061-t6")!;
        var w = TestUtil.Build(SolidBarScenario(al.Density * 0.4 * 0.04 * 0.04));
        var b = w.Bodies[0];
        var display = StressModel.DisplayMesh(b.Shape!);
        var dir = TempDir();
        var fm = CcxBodyFea.BuildModel(b, display, dir, 0.01, mesher);
        // 끝 중앙 축 하중, 끝 모서리 축 하중(편심 → 회전 가속, 하중점 = CG 에서 가장 먼 절점 = 지지 후보), 옆 하중
        foreach (var (g, d, at) in new (double, Vec3, Vec3?)[] { (50.0, Vec3.UnitX, new Vec3(-0.2, 0, 0)), (50.0, Vec3.UnitX, null), (20.0, new Vec3(0, 0, 1), null) })
        {
            var lc = CcxBodyFea.Synthetic(b.OwnMass, display, g, d, at);
            var (m, relief) = CcxBodyFea.BuildLoaded(fm, lc);
            var sum = relief.ExternalForce;
            for (int e = 0; e < fm.Mesh.Elements.Count; e++) sum += m.ElementAccel![e] * (fm.Material.Density * fm.Mesh.ElementVolume(e));
            var (r, res) = CcxBodyFea.Solve(inst, fm, CcxAnalysis.Static, lc, Path.Combine(dir, $"s{g}{at is null}"));
            Assert.True(r.Ok, r.Error);
            output.WriteLine($"{mesher} {g} G {d} at {at}: 외력 {relief.ExternalForce.Norm:F2} N, α {relief.Alpha}, C# 합 잔차 {sum.Norm:E2} N, ccx 반력 {res!.Reaction}, σmax {res.MaxVonMises / 1e6:F2} MPa");
            Assert.True(sum.Norm < 1e-6 * relief.ExternalForce.Norm);
            Assert.True(res.Reaction!.Value.Norm < 0.01 * relief.ExternalForce.Norm, $"반력 {res.Reaction} vs 외력 {relief.ExternalForce.Norm:F2}");
        }
    }

    [Fact]
    public void Cad_mesh_in_fusion_frame_is_placed_in_body_frame()
    {
        if (GmshMesher.FindScript() is null) return;
        var al = MaterialLibrary.Find("al6061-t6")!;
        var w = TestUtil.Build(SolidBarScenario(al.Density * 0.4 * 0.04 * 0.04));
        var b = w.Bodies[0];
        var display = StressModel.DisplayMesh(b.Shape!);
        // 같은 형상을 Fusion 좌표(동체 → Fusion 역회전)로 저장한 'CAD'
        var toFusion = Geometry.Frames.FusionToBody.Transpose();
        var dir = TempDir();
        var cad = Path.Combine(dir, "part_fusion.stl");
        display.Transformed(v => toFusion * v).SaveBinaryStl(cad);
        BodyFeModel fm;
        try { fm = CcxBodyFea.BuildModel(b, display, dir, 0.01, "gmsh", cad, Geometry.Frames.FusionToBody); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("python") || ex.Message.Contains("gmsh")) { output.WriteLine(ex.Message); return; }
        foreach (var n in fm.Notes) output.WriteLine(n);
        var (min, max) = fm.Mesh.Bounds(); var (dmin, dmax) = display.Bounds();
        Assert.True((min - dmin).Norm < 1e-4 && (max - dmax).Norm < 1e-4, $"{min}~{max} vs {dmin}~{dmax}");
        Assert.DoesNotContain(fm.Notes, n => n.StartsWith("경고"));
        Assert.Equal(FeType.C3D10, fm.Mesh.Elements[0].Type);
        Assert.InRange(fm.DensityScale, 0.98, 1.02);
    }

    [Fact]
    public void Mesh_convergence_flags_clamp_singularity_but_not_smooth_field()
    {
        if (Installed() is not { } inst) return;
        // 외팔보 고정단 모서리: 세분할수록 최대 응력이 계속 커지는 특이점
        var dir = TempDir();
        var steel = CcxBenchmarks.Steel;
        int k = 0;
        BodyFeModel Build(double hh)
        {
            int nl = (int)Math.Round(1.0 / hh), nw = Math.Max(1, (int)Math.Round(0.1 / hh));
            var mesh = FeMesh.Beam(1, 0.1, 0.1, nl, nw, nw);
            return new BodyFeModel { Mesh = mesh, Material = steel, BaseMaterial = steel, Display = new TriMesh(), Size = hh };
        }
        (CcxResult, BodyFeaResult?) Clamp(BodyFeModel fm)
        {
            var m = new CcxModel { Mesh = fm.Mesh, Material = steel };
            foreach (var n in fm.Mesh.NodesWhere(p => p.X < 1e-9)) m.Fixed.Add((n, 1, 3));
            var tip = fm.Mesh.NodesWhere(p => p.X > 1 - 1e-9);
            foreach (var n in tip) m.NodalForces.Add((n, new Vec3(0, 0, -1000.0 / tip.Count)));
            var r = CcxRunner.Solve(inst, m, Path.Combine(dir, $"c{++k}"), "fea", TimeSpan.FromMinutes(5));
            return (r, r.Ok ? CcxBodyFea.Evaluate(fm, r, CcxAnalysis.Static) : null);
        }
        var rep = CcxBodyFea.Converge(Build, Clamp, 0.05, levels: 3, ratio: 0.5);
        foreach (var l in rep.Levels) output.WriteLine($"h {l.Size * 1000:F1} mm, 요소 {l.Elements}, σmax {l.MaxVonMises / 1e6:F2} MPa @ {l.At}");
        output.WriteLine(rep.Message);
        Assert.Null(rep.Error);
        Assert.Equal(3, rep.Levels.Count);
        Assert.True(rep.Levels[^1].At.X < 0.05, "최대는 고정단");
        Assert.False(rep.Converged);
        Assert.True(rep.SingularityWarning, rep.Message);
        // 매끄러운 장(구멍 판 Kt)은 수렴
        var kts = new[] { 8, 16, 24 }.Select(n => CcxBenchmarks.KirschHole(inst, Path.Combine(dir, $"k{n}"), nr: n, nt: n / 2).Measured).ToList();
        output.WriteLine($"Kirsch Kt: {string.Join(", ", kts.Select(x => x.ToString("F3")))}");
        Assert.True(Math.Abs(kts[2] - kts[1]) / kts[2] < ConvergenceReport.Tolerance);
    }

    // ---------------- 오류 처리(VV-61 ⑩ 와 같은 원칙) ----------------

    [Fact]
    public void Missing_install_broken_exe_and_bad_deck_fail_cleanly()
    {
        Assert.Null(CalculixInstall.TryLocate(Path.Combine(TempDir(), "nope"), out var why));
        Assert.Contains("없습니다", why);
        var root = TempDir();
        File.WriteAllText(Path.Combine(root, "ccx_static.exe"), "not an executable");
        var fake = CalculixInstall.Locate(root)!;
        var m = new CcxModel { Mesh = FeMesh.Beam(1, 0.1, 0.1, 2, 1, 1), Material = CcxBenchmarks.Steel };
        m.Fixed.Add((0, 1, 3));
        var r = CcxRunner.Solve(fake, m, TempDir(), "x", TimeSpan.FromSeconds(20));
        Assert.False(r.Ok);
        Assert.False(string.IsNullOrWhiteSpace(r.Error));
        // 덱 검사: 구속 없음 · NaN 하중 · 두께 없는 셸 → 실행 전 거부(ccx 는 잘못된 입력에서 메시지 없이 죽는다)
        var noBc = new CcxModel { Mesh = FeMesh.Beam(1, 0.1, 0.1, 2, 1, 1), Material = CcxBenchmarks.Steel };
        Assert.Throws<InvalidDataException>(() => CcxDeck.Validate(noBc));
        var nan = new CcxModel { Mesh = FeMesh.Beam(1, 0.1, 0.1, 2, 1, 1), Material = CcxBenchmarks.Steel };
        nan.Fixed.Add((0, 1, 3)); nan.NodalForces.Add((1, new Vec3(double.NaN, 0, 0)));
        Assert.Throws<InvalidDataException>(() => CcxDeck.Validate(nan));
        var shell = FeMesh.Quad8(2, 2, (u, v) => new Vec3(u, v, 0), FeType.S8R, 0.001);
        shell.Thickness = null;
        var sh = new CcxModel { Mesh = shell, Material = CcxBenchmarks.Steel };
        sh.Fixed.Add((0, 1, 3));
        Assert.Throws<InvalidDataException>(() => CcxDeck.Validate(sh));
        // 실제 ccx: 알 수 없는 키워드 덱 → 실패 결과(예외 아님)
        if (Installed() is { } inst)
        {
            var d = TempDir();
            File.WriteAllText(Path.Combine(d, "bad.inp"), "*NODE, NSET=NALL\n1, 0, 0, 0\n*FOOBAR\n*STEP\n*STATIC\n*END STEP\n");
            var rb = CcxRunner.Run(inst, Path.Combine(d, "bad.inp"), TimeSpan.FromSeconds(60));
            Assert.False(rb.Ok);
            output.WriteLine(rb.Error);
        }
    }

    [Fact]
    public void Frd_and_dat_parsers_handle_layout_and_corruption()
    {
        var d = TempDir();
        var frd = Path.Combine(d, "a.frd");
        File.WriteAllText(frd, string.Join("\n",
            "    1C", "    2C                             2                                     1",
            " -1         1 0.00000E+00 0.00000E+00 0.00000E+00", " -1         2 1.00000E+00 0.00000E+00 0.00000E+00", " -3",
            "    3C                             1                                     1", " -1         1    1    0    1", " -2         1         2", " -3",
            "    1PMODE                         2",
            "  100CL  102 652.6197018           2                     2    2MODAL      1",
            " -4  DISP        4    1", " -5  D1          1    2    1    0",
            " -1         1 0.00000E+00 0.00000E+00 0.00000E+00", " -1         2 1.00000E-03-2.00000E-03 3.00000E-03", " -3",
            " -4  STRESS      6    1",
            " -1         1 1.00000E+06 0.00000E+00 0.00000E+00 0.00000E+00 0.00000E+00 0.00000E+00",
            " -1         2 1.00000E+06 1.00000E+06 1.00000E+06 0.00000E+00 0.00000E+00 0.00000E+00", " -3", " 9999"));
        var f = FrdData.Parse(frd);
        Assert.Equal(2, f.Coords.Count);
        var disp = f.Block("DISP", 2)!;
        Assert.Equal(652.6197018, disp.Value, 6);
        Assert.Equal(-2e-3, disp.Values[2][1], 12);
        var r = new CcxResult { Frd = f };
        Assert.Equal(1e6, r.VonMises(2)[1], 3);       // 단축 → σvm = σ
        Assert.Equal(0, r.VonMises(2)[2], 3);         // 정수압 → 0
        File.WriteAllText(Path.Combine(d, "b.frd"), "    2C\n -1         1 0.00000E+00 0.0000");
        Assert.Throws<InvalidDataException>(() => FrdData.Parse(Path.Combine(d, "b.frd")));
        File.WriteAllText(Path.Combine(d, "c.frd"), "    1C\n 9999\n");
        Assert.Throws<InvalidDataException>(() => FrdData.Parse(Path.Combine(d, "c.frd")));
        var dat = Path.Combine(d, "a.dat");
        File.WriteAllText(dat, "\n     B U C K L I N G   F A C T O R   O U T P U T\n\n MODE NO       BUCKLING\n                FACTOR\n\n      1   0.1122004E+05\n      2   0.2000000E+05\n\n"
            + " total force (fx,fy,fz) for set SUPPORT and time  0.1000000E+01\n\n       -2.6E-03 1.0E-04  3.8E-03\n");
        var dd = DatData.Parse(dat);
        Assert.Equal(new[] { 11220.04, 20000.0 }, dd.BucklingFactors.Select(x => Math.Round(x, 2)));
        Assert.Equal(-2.6e-3, dd.ReactionTotal!.Value.X, 12);
    }

    [Fact]
    public void Structured_meshes_inp_roundtrip_and_voxel_volume()
    {
        var beam = FeMesh.Beam(1, 0.1, 0.2, 4, 2, 2);
        Assert.Equal(16, beam.Elements.Count);
        var (mass, cg, _) = beam.MassProperties(1000);
        Assert.Equal(1000 * 0.02, mass, 9);
        Assert.True((cg - new Vec3(0.5, 0, 0)).Norm < 1e-12);
        // 외곽 면: 4×2 ×2(위·아래) + 4×2 ×2(옆) + 2×2 ×2(끝) = 40, 면적 합 = 겉넓이
        var faces = beam.ExteriorFaces();
        Assert.Equal(40, faces.Count);
        Assert.Equal(2 * (0.1 + 0.2 + 0.02), faces.Sum(f => f.Area), 9);
        Assert.All(faces, f => Assert.True(Vec3.Dot(f.Normal, f.Center - new Vec3(0.5, 0, 0)) > 0)); // 바깥 법선
        // inp 왕복
        var m = new CcxModel { Mesh = beam, Material = CcxBenchmarks.Steel };
        m.Fixed.Add((0, 1, 3));
        var inp = CcxDeck.Write(m, TempDir(), "rt");
        var back = FeMesh.ReadInp(inp);
        Assert.Equal(beam.Elements.Count, back.Elements.Count);
        Assert.Equal(FeType.C3D20R, back.Elements[0].Type);
        Assert.Equal(beam.MassProperties(1000).Mass, back.MassProperties(1000).Mass, 9);
        // 복셀: 축 정렬 상자는 정확히 채워짐
        var box = TriMesh.Box(new Vec3(0.4, 0.04, 0.04), Vec3.Zero);
        var vox = FeMesh.Voxel(box, 0.01);
        Assert.Equal(0.4 * 0.04 * 0.04, vox.Elements.Count * 1e-6, 9);
        // S6 셸: 표시 면 1:1, 용접된 닫힌 상자
        var sh = FeMesh.ShellFromTriangles(StressModel.DisplayMesh(new Shape { Kind = ShapeKind.Box, Size = new Vec3(0.3, 0.2, 0.1) }), 0.002);
        Assert.Equal(0, sh.KeepLargestConnected());
        Assert.Equal(2 * (0.06 + 0.03 + 0.02) * 0.002, Enumerable.Range(0, sh.Elements.Count).Sum(sh.ElementVolume), 9);
    }
}
