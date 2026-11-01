using System.Text.Json.Nodes;
using Sim.Core.Analysis;
using Sim.Core.Geometry;
using Sim.Core.Models;
using Sim.Core.Numerics;
using Sim.Core.Reliability;
using Sim.Core.Scenario;
using Sim.Core.Validation;

namespace Sim.Core.Tests;

/// <summary>PRD AB절(FR-REL-01~06): 공개 자료 모델 표 · CFD DB 로더 · 입력 출처 등급 · UQ(PRCC) · NASA-STD-7009A 채점.</summary>
public class ReliabilityTests
{
    [Fact(DisplayName = "FR-REL-06 원통 횡류 CD2D: TR R-300 표 II 행 점 재현 · 아임계 1.2 · 마하 상한 밖 표시")]
    public void CylinderCrossflowTable()
    {
        Assert.Equal(1.184, CylinderCrossflow.Cd2D(2.53e6, 0.5), 9);
        Assert.Equal(1.028, CylinderCrossflow.Cd2D(7.75e6, 0.5), 9);
        Assert.Equal(0.143, CylinderCrossflow.Cd2D(4.9e5, 0.1), 9);            // 항력 위기 바닥(Freon 1/10 atm)
        Assert.Equal(1.2, CylinderCrossflow.Cd2D(1e5, 0.1), 9);                 // 아임계
        // 컨테이너 조건(M 0.5, Re 4.4e6) — 두 점 사이 log Re 보간
        double f = Math.Log(4.4e6 / 2.53e6) / Math.Log(7.75e6 / 2.53e6);
        Assert.Equal(1.184 + (1.028 - 1.184) * f, CylinderCrossflow.Cd2D(4.4e6, 0.5), 9);
        CylinderCrossflow.Cd2D(3e6, 0.8, out bool outside);
        Assert.True(outside);
        Assert.Equal(0.62, CylinderCrossflow.Eta(4, out _), 9);
        Assert.InRange(CylinderCrossflow.Eta(2000.0 / 420, out _), 0.62, 0.64);   // L/D 4.8 ≈ 0.63 (README)
    }

    [Fact(DisplayName = "FR-REL-06 aero.crossflow tr-r300: 횡류 성분 = η·CD2D(Re sinα, M sinα), 축류 0.82")]
    public void ProjectedReMach()
    {
        var s = new Shape { Kind = ShapeKind.Cylinder, Radius = 0.21, Length = 2.0, Axis = 0 };
        var (a90, cd90) = s.ProjectedReMach(new Vec3(0, 0, 1), 4.4e6, 0.5, 1.0);
        Assert.Equal(2 * 0.21 * 2.0, a90, 9);
        Assert.Equal(CylinderCrossflow.Eta(2000.0 / 420, out _) * CylinderCrossflow.Cd2D(4.4e6, 0.5), cd90, 9);
        var (_, cd0) = s.ProjectedReMach(new Vec3(1, 0, 0), 4.4e6, 0.5, 1.12);
        Assert.Equal(0.82 * 1.12, cd0, 9);
        // 시나리오 파싱: 잘못된 값 거부
        Assert.Throws<InvalidDataException>(() => TestUtil.Build("""
            {"schema":"mns.scenario/1","sim":{"dt":0.01,"tEnd":0.1},"bodies":[{"name":"c","mass":20,
             "shape":{"type":"cylinder","radius":0.2,"length":1.9,"axis":"x"},"aero":{"crossflow":"bogus"}}]}
            """));
    }

    [Fact(DisplayName = "FR-REL-06 tr-r300 원통이 M 0.5 에서 받는 횡류 항력 = q·S·CD (엔진 경로)")]
    public void CrossflowInEngine()
    {
        var w = TestUtil.Build("""
            {"schema":"mns.scenario/1","sim":{"dt":0.001,"tEnd":0.001},"environment":{"gravity":0},
             "bodies":[{"name":"c","mass":20,"inertia":[1,1,1],"shape":{"type":"cylinder","radius":0.21,"length":2.0,"axis":"x"},
               "aero":{"crossflow":"tr-r300","cp":"none"},"position":[0,0,-1000],"velocity":[0,0,168.2]}]}
            """);
        var b = w.GetBody("c");
        var atm = w.Env.Atmosphere.At(1000);
        var (fb, _) = b.Aero!.Compute(new Vec3(0, 0, 168.2), Vec3.Zero, atm.Density, b.Cg, 168.2 / atm.SpeedOfSound, atm.Viscosity);
        double re = atm.Density * 168.2 * 0.42 / atm.Viscosity;
        double cd = CylinderCrossflow.Eta(2000.0 / 420, out _) * CylinderCrossflow.Cd2D(re, 168.2 / atm.SpeedOfSound);
        Assert.Equal(-0.5 * atm.Density * 168.2 * 168.2 * 0.42 * 2.0 * cd, fb.Z, 6);
        Assert.InRange(b.Aero.MaxReynolds, 4.0e6, 4.8e6);
    }

    [Fact(DisplayName = "FR-REL-03 GCI(Celik 2008): 2차 수렴 복원 · 진동 수렴 보수적 대체 (tools/cfd/post.py 와 같은 값)")]
    public void GciProcedure()
    {
        double h1 = 0.1, h2 = 0.1 * Math.Sqrt(2), h3 = 0.2;
        double F(double h) => 1 + 0.5 * h * h;
        var g = Gci.Compute(h1, h2, h3, F(h1), F(h2), F(h3));
        Assert.Equal("monotone", g.Convergence);
        Assert.Equal(2.0, g.P, 6);
        Assert.Equal(1.0, g.PhiExt, 9);
        Assert.Equal(1.25 * Math.Abs((F(h1) - F(h2)) / F(h1)) / (2 - 1), g.Gci21, 12);
        var o = Gci.Compute(1.0, 1.4, 2.0, 1.00, 1.02, 0.99);
        Assert.Equal("oscillatory", o.Convergence);
        Assert.Equal(3.0, o.Fs);
        var rep = new Sim.Core.Fea.ConvergenceReport();
        foreach (var h in new[] { h3, h2, h1 }) rep.Levels.Add(new Sim.Core.Fea.ConvergenceLevel(h, 100, F(h), Vec3.Zero, 2 * F(h)));
        Assert.Equal(1.0, rep.GciEstimates().Stress!.PhiExt, 9);
        Assert.NotNull(rep.ToJson()["gciDisplacement"]);
    }

    [Fact(DisplayName = "FR-REL-03 FEA 수렴: 요소별 관성 GRAV 가 많으면 집합(≤512)으로 묶어 *STEP 앞에 ELSET — ccx int32 색인 넘침 회피")]
    public void CcxGravGrouping()
    {
        var mesh = Sim.Core.Fea.FeMesh.Beam(1.0, 0.1, 0.1, 60, 20, 20, quadratic: false);
        Assert.True(mesh.Elements.Count > Sim.Core.Fea.CcxDeck.GravGroupThreshold);
        var model = new Sim.Core.Fea.CcxModel
        {
            Mesh = mesh, Material = Sim.Core.Structures.MaterialLibrary.Find("al6061-t6")!,
            ElementAccel = Enumerable.Range(0, mesh.Elements.Count).Select(e => new Vec3(10, 0.5 * e / mesh.Elements.Count, 0)).ToArray(),
        };
        model.Fixed.Add((0, 1, 3));
        var dir = Path.Combine(Path.GetTempPath(), "mns_grav_" + Guid.NewGuid().ToString("N"));
        try
        {
            var text = File.ReadAllText(Sim.Core.Fea.CcxDeck.Write(model, dir, "g"));
            int step = text.IndexOf("*STEP", StringComparison.Ordinal);
            int grav = text.Split('\n').Count(l => l.Contains(", GRAV,"));
            Assert.InRange(grav, 2, Sim.Core.Fea.CcxDeck.MaxGravGroups);
            Assert.True(text.IndexOf("*ELSET, ELSET=GR1", StringComparison.Ordinal) is > 0 and var p && p < step);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact(DisplayName = "FR-REL-01 CFD 공력 DB: 축대칭 회전 · 앞뒤 거울 · 마하 보간")]
    public void AeroDbMapping()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mns_adb_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var f = Path.Combine(dir, "db.json");
            File.WriteAllText(f, """
                {"schema":"mns.aero-db/1","name":"t","refArea":0.1,"refLength":0.4,"momentRef":[0,0,0],"foreAftSymmetric":true,
                 "mach":[0.3,0.5],"alpha":[0,45,90],
                 "CX":[[-0.8,-0.5,0],[-1.0,-0.6,0]],"CN":[[0,0.6,1.0],[0,0.8,1.2]],"Cm":[[0,0.1,0],[0,0.2,0]]}
                """);
            var db = AeroDb.Load(f);
            var c = db.Coefficients(45, 0.4);
            Assert.Equal(-0.55, c.CX, 9); Assert.Equal(0.7, c.CN, 9); Assert.Equal(0.15, c.Cm, 9);
            var m = db.Coefficients(135, 0.4);                    // 앞뒤 거울
            Assert.Equal(0.55, m.CX, 9); Assert.Equal(0.7, m.CN, 9); Assert.Equal(-0.15, m.Cm, 9);
            double a = Math.PI / 4;
            var (f1, m1) = db.Compute(new Vec3(Math.Cos(a), 0, Math.Sin(a)), 100, 0.5);
            var (f2, m2) = db.Compute(new Vec3(Math.Cos(a), Math.Sin(a), 0), 100, 0.5);   // 축 둘레 90° 회전
            Assert.Equal(f1.X, f2.X, 9); Assert.Equal(f1.Z, f2.Y, 9); Assert.Equal(m1.Y, -m2.Z, 9);
            Assert.Equal(100 * 0.1 * 0.8, f1.Z, 9); Assert.Equal(100 * 0.1 * 0.4 * 0.2, m1.Y, 9);
            Assert.Throws<InvalidDataException>(() =>
            {
                File.WriteAllText(f, """{"schema":"mns.aero-db/1","refArea":1,"refLength":1,"mach":[0.5],"alpha":[0,90],"CX":[[0]],"CN":[[0,1]],"Cm":[[0,0]]}""");
                AeroDb.Load(f);
            });
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact(DisplayName = "FR-REL-03 PRCC: 단조 지배 입력 ≈ 1, 약한 입력 > 잡음 입력 ≈ 0")]
    public void PrccRanks()
    {
        var rng = new Rng(7);
        var x = new List<double[]>(); var y = new List<double>();
        for (int i = 0; i < 300; i++)
        {
            double a = rng.NextDouble(), b = rng.NextDouble(), n = rng.NextDouble();
            x.Add([a, b, n]);
            y.Add(Math.Exp(3 * a) + 0.3 * b);      // 단조 비선형
        }
        var r = Prcc.Compute(x, y);
        Assert.True(r[0] > 0.95, $"{r[0]}");
        Assert.True(r[1] > 0.5 && r[1] < r[0], $"{r[1]}");
        Assert.True(Math.Abs(r[2]) < 0.15, $"{r[2]}");
        Assert.True(Prcc.PValue(r[0], 300, 3) < 1e-6 && Prcc.PValue(r[2], 300, 3) > 1e-3);
        Assert.Equal([1.5, 1.5, 3.0], Prcc.Ranks([2.0, 2.0, 5.0]));
    }

    [Fact(DisplayName = "FR-REL-02 입력 출처 등급: 파싱 · 미등록 수치 · 분산 병합(같은 경로는 등급 분포)")]
    public void PedigreeRegistry()
    {
        var root = J.ParseObject("""
            {"sim":{"dt":0.01},"bodies":[{"name":"b","mass":3,"color":"#fff","devices":[{"type":"parachute","name":"c","cd0":2.2,"d0":1.2}]}],
             "dispersions":[{"path":"bodies.b.mass","dist":"normal","sd":0.1},{"path":"bodies.b.devices.c.cd0","dist":"normal","sd":0.5,"mode":"scale"}],
             "inputPedigree":[{"path":"bodies.b.devices.c.cd0","grade":"C","source":"제조사","dist":{"dist":"uniform","min":0.9,"max":1.1,"mode":"scale"}},
                              {"path":"bodies.b.devices.c.d0","grade":"a","source":"설계"},{"path":"bodies.b.missing","grade":"D","source":"x"}]}
            """);
        var s = InputPedigree.Summarize(root);
        Assert.Equal(3, s.Entries.Count);
        Assert.Equal(1, s.ByGrade["A"]); Assert.Equal(1, s.ByGrade["C"]); Assert.Equal(1, s.ByGrade["D"]);
        Assert.Equal(["bodies.b.missing"], s.MissingPaths);
        Assert.Equal(4, s.NumericLeaves);           // sim.dt, mass, cd0, d0 (색·분산·등급 절 제외)
        Assert.Equal(2, s.Unregistered);
        var m = InputPedigree.MergedDispersions(root);
        Assert.Equal(2, m.Count);
        Assert.Equal(("bodies.b.mass", "환경"), (m[0].Def.Path, m[0].Grade));
        Assert.Equal(("uniform", "C"), (m[1].Def.Dist, m[1].Grade));
        Assert.Throws<InvalidDataException>(() => InputPedigree.Parse(J.ParseObject("""{"inputPedigree":[{"path":"x","grade":"E"}]}""")));
    }

    [DesignTheory(DisplayName = "FR-REL-02 시나리오 09·10: 등록 경로가 모두 존재하고 분포가 적용 가능")]
    [InlineData("09_munition_3petal_longcontainer_real.json")]
    [InlineData("09b_munition_bellydoor_longcontainer_real.json")]
    [InlineData("09c_munition_3petal_longcontainer_cfd.json")]
    [InlineData("10_box_chute_opening_shock.json")]
    public void ScenarioPedigreeResolves(string file)
    {
        var doc = ScenarioDoc.Load(TestUtil.Scenario(file));
        var s = InputPedigree.Summarize(doc.Root);
        Assert.True(s.Entries.Count >= 10);
        Assert.DoesNotContain(s.MissingPaths, p => !p.EndsWith("apparentMassK0") && !p.EndsWith("massProps"));
        foreach (var e in s.Entries.Where(e => e.Dist is not null))
        {
            var d = doc.Clone();
            var v = e.Dist!.Apply(JsonPath.GetNumber(d.Root, e.Path) ?? 0, e.Dist.Sample(0.5));
            JsonPath.SetNumber(d.Root, e.Path, v);
            Assert.True(double.IsFinite(v), e.Path);
        }
        Assert.NotNull(doc.Root["uq"]);
        doc.Build();
    }

    [DesignFact(DisplayName = "FR-REL-03 UQ 엔진(시나리오 10, 소규모): 출력·PRCC·수치 불확도·모델 형태 배율")]
    public void UqSmallRun()
    {
        var doc = ScenarioDoc.Load(TestUtil.Scenario("10_box_chute_opening_shock.json"));
        var mf = new CaseResult { Id = "parachute_dgb_opening_cruz2003", Title = "", Vv = "", Grade = "A", Use = "validation", Kind = "", Model = "", Tolerance = 0.25, Status = CaseStatus.Pass };
        foreach (var e in new[] { 0.1, -0.1, 0.05, 0.0 }) mf.Points.Add(new PointResult("p", 100, 100 * (1 + e), "N", e, true, ""));
        var res = UqEngine.Run(doc, new UqOptions { Runs = 12, Seed = 3, Validation = [mf] });
        Assert.Equal(0, res.Errors);
        var chute = res.Outputs.Single(o => o.Def.Key == "chuteForce");
        Assert.NotNull(chute.ModelForm);
        Assert.Equal(0.0125, chute.ModelForm!.Bias, 9);
        Assert.Equal(12, chute.Samples.Length);
        Assert.Equal(res.Inputs.Count, chute.PrccValues.Length);
        Assert.True(double.IsFinite(chute.Nominal) && double.IsFinite(chute.NominalHalfDt));
        Assert.True(chute.Lo90 <= chute.P05 + chute.NumericalU + 1e-9 || chute.ModelForm is not null);
        Assert.Contains(res.Outputs, o => o.Def.Key.StartsWith("sf:"));
        Assert.Contains(res.Inputs, i => i.Path == "connectors.cord1.breakLoad" && i.Grade == "A");
        Assert.Contains(res.Inputs, i => i.Grade == "D");
    }

    [Fact(DisplayName = "FR-REL-04 7009A: 원문 8요인 내장 · Wilson 구간 · 채점 규칙(유의 D 입력 → 입력 출처 1, 등급 판정)")]
    public void CredibilityRules()
    {
        var c = ReferenceData.Section("credibility7009A");
        Assert.Equal(8, c["factors"]!.AsArray().Count);
        Assert.Equal(Credibility.Factors, c["factors"]!.AsArray().Select(f => f!["factor"]!.GetValue<string>()).ToArray());
        var (lo, hi) = UqEngine.Wilson(5, 100);
        Assert.Equal(0.0215, lo, 3); Assert.Equal(0.1118, hi, 3);

        UqResult Make(string grade, double prcc)
        {
            var def = new UqOutputDef("x", "출력", "N", "낙하산 충만·개방하중", "opening-load");
            var o = new UqOutputResult
            {
                Def = def, Nominal = 100, NominalHalfDt = 100.5, Samples = [1], SamplesWithModelForm = [1], PrccValues = [prcc], PValues = [0.001],
                ModelForm = new ModelFormStat("opening-load", "l", 0.01, 0.1, 30, "e"),
            };
            return new UqResult
            {
                Scenario = "10_test", Profile = "opening-shock", Inputs = [new UqInput("bodies.box1.devices.chute.fillExponent", grade, "s", "U")],
                Outputs = [o], ModelForms = [], Runs = 200, Errors = 0,
                Pedigree = new PedigreeSummary([], InputPedigree.Grades.ToDictionary(g => g, _ => 0), 10, 5, []),
            };
        }
        var pass = new[] { "parachute_dgb_opening_cruz2003", "parachute_cross40in_opening_noltr73157" }
            .Select(id => new CaseResult { Id = id, Title = "", Vv = "", Grade = "A", Use = "validation", Kind = "", Model = "", Tolerance = 0.25, Status = CaseStatus.Pass }).ToList();
        var d = Make("D", 0.8);
        var cd = Credibility.Assess(d.Outputs[0], d, pass);
        Assert.Equal(1, cd.Level("Input Pedigree"));
        Assert.Equal(2, cd.Level("Validation"));
        Assert.Equal(2, cd.Level("Verification"));      // 수치 0.5 % ≤ 5 %
        Assert.Equal("참고", cd.Class);
        var a = Make("A", 0.8);
        var ca = Credibility.Assess(a.Outputs[0], a, pass);
        Assert.Equal(2, ca.Level("Input Pedigree"));
        Assert.Equal(3, ca.Level("Uncertainty Characterization"));
        Assert.Equal("신뢰", ca.Class);
        var cn = Credibility.Assess(a.Outputs[0], a, []);   // 검증 사례 실행 결과 없음 → 확인 1 → 참고
        Assert.Equal(1, cn.Level("Validation"));
        Assert.Equal("참고", cn.Class);
        Assert.All(ca.Factors, f => Assert.False(string.IsNullOrWhiteSpace(f.Definition)));
        // 시험 우선순위: D 입력 가중 1.0
        var pr = Credibility.Priorities([d]);
        Assert.Contains("박스 낙하산 개방 시험", pr[0].Test);
        Assert.Equal(0.64, pr[0].Score, 9);
    }
}
