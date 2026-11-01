using System.IO.Compression;
using System.Text.Json.Nodes;
using Sim.Core.Fea;
using Sim.Core.Numerics;

namespace Sim.Core.Tests;

/// <summary>Fusion 해석 패키지(.adsksimpkg) 리더 · study.json · CalculiX 재현 보조 계산 — 합성 패키지(원자료 없이 테스트 안에서 만든다).</summary>
public class FusionSimPkgTests
{
    private static JsonObject Obj(string type, string id, JsonObject attrs, JsonObject? rels = null)
    {
        var o = new JsonObject { ["type"] = type, ["id"] = id, ["attributes"] = attrs };
        if (rels is not null) o["relationships"] = rels;
        return o;
    }

    private static JsonObject One(string id, string type) => new() { ["data"] = new JsonObject { ["id"] = id, ["type"] = type } };

    private static JsonObject Many(string type, params string[] ids) =>
        new() { ["data"] = new JsonArray(ids.Select(i => (JsonNode)new JsonObject { ["id"] = i, ["type"] = type }).ToArray()) };

    /// <summary>바디 2개(판·레일), 재료 2개, 가속도 50 G(+Z), 레일 면 고정, 결합 접촉 1개, 모르는 type 1개.</summary>
    private static JsonObject Project()
    {
        var data = new JsonArray
        {
            Obj("ProjectSimulation", "ps", new JsonObject { ["Name"] = "합성_50G" }, new JsonObject
            {
                ["geometry"] = Many("Geometry", "g1", "g2"), ["problemdefinitionstudies"] = Many("ProblemDefinitionStudySimulation", "pd"),
            }),
            Obj("ProblemDefinitionStudySimulation", "pd", new JsonObject
            {
                ["Type"] = "LinearStress", ["Name"] = "정적 응력", ["Version"] = "14.160.6",
                ["ExternalData"] = new JsonObject { ["x"] = new JsonObject { ["ActiveLoadCase"] = "lc" } },
            }, new JsonObject
            {
                // Fusion 이 내는 그대로 뒤 공백이 붙은 관계 키
                ["attributes "] = new JsonObject { ["data"] = new JsonArray(new JsonObject { ["id"] = "c1", ["type"] = "Contact" }, new JsonObject { ["id"] = "scs", ["type"] = "SurfaceContactSettings" }) },
                ["attributesassignment"] = Many("AttributeAssignment", "aa1", "aa2"),
                ["constraints"] = Many("Constraint", "fix"), ["loadcases"] = Many("LoadCase", "lc"), ["loads"] = Many("LinearAcceleration", "acc"),
                ["materials"] = Many("Material", "m1", "m2"), ["meshsettingsvariables"] = One("ms", "MeshSettingsVariablesFEACS"), ["gravityload"] = One("grav", "GravityLoad"),
            }),
            Obj("Geometry", "g1", new JsonObject { ["Name"] = "모형 1:1 > STATION:1 > Plate_A", ["Description"] = "{\"bodyName\":\"Plate_A\",\"componentNames\":[\"모형 1:1\",\"STATION:1\"]}" }),
            Obj("Geometry", "g2", new JsonObject { ["Name"] = "모형 1:1 > RAIL:1 > Rail_B", ["Description"] = "{\"bodyName\":\"Rail_B\",\"componentNames\":[\"모형 1:1\",\"RAIL:1\"]}" }),
            Obj("AttributeAssignment", "aa1", new JsonObject { ["GeometryId"] = "g1" }, new JsonObject { ["attributes"] = Many("AttributeGeometryAssignment", "aga1") }),
            Obj("AttributeAssignment", "aa2", new JsonObject { ["GeometryId"] = "g2" }, new JsonObject { ["attributes"] = Many("AttributeGeometryAssignment", "aga2", "aga3") }),
            Obj("AttributeGeometryAssignment", "aga1", new JsonObject { ["AttributeId"] = "m1" }, new JsonObject { ["geometryreferences"] = Many("GeometryReference", "r1") }),
            Obj("AttributeGeometryAssignment", "aga2", new JsonObject { ["AttributeId"] = "m2" }, new JsonObject { ["geometryreferences"] = Many("GeometryReference", "r2") }),
            Obj("AttributeGeometryAssignment", "aga3", new JsonObject { ["AttributeId"] = "fix" }, new JsonObject { ["geometryreferences"] = Many("GeometryReference", "r3", "r4") }),
            Obj("GeometryReference", "r1", new JsonObject { ["Type"] = "Body", ["GeometryComponentId"] = 0 }),
            Obj("GeometryReference", "r2", new JsonObject { ["Type"] = "Body", ["GeometryComponentId"] = 0 }),
            Obj("GeometryReference", "r3", new JsonObject { ["Type"] = "Surface", ["GeometryComponentId"] = 3 }),
            Obj("GeometryReference", "r4", new JsonObject { ["Type"] = "Surface", ["GeometryComponentId"] = 7 }),
            Obj("Contact", "c1", new JsonObject { ["Name"] = "[S] 접착1", ["ContactType"] = "Bonded", ["PenetrationType"] = "Symmetric", ["Friction"] = null },
                new JsonObject { ["primarygeometryassignments"] = One("ga1", "GeometryAssignment"), ["secondarygeometryassignments"] = One("ga2", "GeometryAssignment") }),
            Obj("GeometryAssignment", "ga1", new JsonObject { ["GeometryId"] = "g1" }),
            Obj("GeometryAssignment", "ga2", new JsonObject { ["GeometryId"] = "g2" }),
            Obj("SurfaceContactSettings", "scs", new JsonObject { ["Type"] = "SimulationParameter", ["SimulationParameterType"] = "SurfaceContactSettings", ["Tolerance"] = 0.001 }),
            Obj("LinearAcceleration", "acc", new JsonObject { ["Name"] = "선형 전역 하중1", ["LoadCaseId"] = "lc", ["LoadType"] = "LinearAcceleration", ["Magnitude"] = 490.33 },
                new JsonObject { ["directionunitvector"] = One("v1", "Vector3d") }),
            Obj("Vector3d", "v1", new JsonObject { ["X"] = 0.0, ["Y"] = 0.0, ["Z"] = 1.0 }),
            Obj("GravityLoad", "grav", new JsonObject { ["LoadCaseId"] = "", ["IsOn"] = false, ["Magnitude"] = 9.81 }, new JsonObject { ["directionunitvector"] = One("v2", "Vector3d") }),
            Obj("Vector3d", "v2", new JsonObject { ["X"] = 0.0, ["Y"] = -1.0, ["Z"] = 0.0 }),
            Obj("LoadCase", "lc", new JsonObject { ["Name"] = "하중 조건1" }),
            Obj("Constraint", "fix", new JsonObject
            {
                ["ConstraintType"] = "Fixed", ["Name"] = "고정1", ["LoadCaseId"] = "lc", ["UX"] = "Fixed", ["UY"] = "Fixed", ["UZ"] = "Fixed",
                ["RX"] = "Fixed", ["RY"] = "Fixed", ["RZ"] = "Fixed", ["CoordinateSystem"] = "Global",
            }),
            Obj("Material", "m1", new JsonObject { ["Name"] = "CFRP" }, new JsonObject { ["mechanical"] = One("mm1", "MaterialMechanical"), ["specific"] = One("sp1", "MaterialSpecificMetal") }),
            Obj("MaterialMechanical", "mm1", new JsonObject(), new JsonObject { ["behavior"] = One("iso1", "MaterialMechanicalBehaviorIsotropic") }),
            Obj("MaterialMechanicalBehaviorIsotropic", "iso1", new JsonObject { ["Density"] = 1430.0, ["YoungModulus"] = 1.33e11, ["PoissonRatio"] = 0.39, ["ShearModulus"] = 4.78e10 }),
            Obj("MaterialSpecificMetal", "sp1", new JsonObject { ["YieldStrength"] = 3e8, ["TensileStrength"] = 5.77e8 }),
            Obj("Material", "m2", new JsonObject { ["Name"] = "지반" }, new JsonObject { ["mechanical"] = One("mm2", "MaterialMechanical"), ["physical"] = One("ph2", "MaterialPhysical") }),
            Obj("MaterialMechanical", "mm2", new JsonObject(), new JsonObject { ["behavior"] = One("iso2", "MaterialMechanicalBehaviorIsotropic") }),
            Obj("MaterialMechanicalBehaviorIsotropic", "iso2", new JsonObject { ["Density"] = 1800.0, ["YoungModulus"] = 3e7, ["PoissonRatio"] = 0.3, ["ShearModulus"] = 1.15e7 }),
            Obj("MaterialPhysical", "ph2", new JsonObject { ["AssetId"] = "x" }, new JsonObject { ["advanced"] = One("ep2", "MaterialPhysicalElastoplastic") }),
            Obj("MaterialPhysicalElastoplastic", "ep2", new JsonObject { ["TangentModulus"] = 3e5, ["InitialYieldStress"] = 2e5 }),
            Obj("MeshSettingsVariablesFEACS", "ms", new JsonObject { ["MeshSizeFraction"] = 0.1, ["MinSizeFraction"] = 0.2, ["MeshElementOrderType"] = "Parabolic" }),
            Obj("SomethingNew", "z1", new JsonObject()),
        };
        return new JsonObject { ["data"] = data };
    }

    private static string WritePackage(JsonObject project)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mns_simpkg_{Guid.NewGuid():N}.adsksimpkg");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Put(string name, string text)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open());
            w.Write(text);
        }
        Put("manifest.json", new JsonObject
        {
            ["producer"] = "SimTranslator 테스트", ["workingModelFormat"] = new JsonObject { ["type"] = "AutodeskSMT" },
            ["workingModels"] = new JsonArray(new JsonObject
            {
                ["folder"] = "G-1", ["file"] = "model.7z", ["studies"] = new JsonArray(new JsonObject { ["file"] = "project.json", ["name"] = "정적 응력", ["type"] = "SimCaseLSS" }),
            }),
        }.ToJsonString());
        Put("G-1/project.json", project.ToJsonString());
        Put("G-1/model.7z", "SMT");
        return path;
    }

    [Fact]
    public void Reads_synthetic_package_graph()
    {
        var path = WritePackage(Project());
        try
        {
            var s = FusionSimPkg.Read(path);
            Assert.Equal("LinearStress", s.StudyType);
            Assert.Equal("합성_50G", s.ProjectName);
            Assert.Equal("SimTranslator 테스트", s.Producer);
            Assert.Equal("lc", s.ActiveLoadCase);
            Assert.Equal(new[] { "Plate_A", "Rail_B" }, s.Bodies.Select(b => b.BodyName));
            Assert.Equal(new[] { "모형 1:1", "STATION:1" }, s.Bodies[0].Components);
            // 재료는 AttributeAssignment → AttributeGeometryAssignment 를 따라 바디별로
            Assert.Equal("CFRP", s.MaterialOf(s.Bodies[0])!.Name);
            Assert.Equal(1.33e11, s.MaterialOf(s.Bodies[0])!.E);
            Assert.Equal(3e8, s.MaterialOf(s.Bodies[0])!.Yield);
            Assert.Equal("지반", s.MaterialOf(s.Bodies[1])!.Name);
            Assert.Equal(2e5, s.MaterialOf(s.Bodies[1])!.Elastoplastic!["InitialYieldStress"]!.GetValue<double>());
            var acc = s.Loads.Single(l => l.Type == "LinearAcceleration");
            Assert.Equal(490.33, acc.Magnitude);
            Assert.Equal(Vec3.UnitZ, acc.Vector);
            Assert.False(s.Loads.Single(l => l.Type == "Gravity").On);
            var fix = s.Constraints.Single();
            Assert.Equal("Fixed", fix.Type);
            Assert.Equal(new[] { "g2" }, fix.Bodies);
            Assert.Equal(2, fix.Faces);
            var c = s.Contacts.Single();
            Assert.Equal(("Bonded", "g1", "g2"), (c.Type, c.Primary, c.Secondary));
            Assert.Null(c.Friction);
            Assert.Equal(0.001, s.Parameters["SurfaceContactSettings"]!["Tolerance"]!.GetValue<double>());
            Assert.Equal(0.1, s.MeshNum("MeshSizeFraction", 0));
            Assert.Contains(s.Warnings, w => w.Contains("SomethingNew"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Study_json_is_flat_with_resolvable_ids()
    {
        var s = FusionSimPkg.Parse(Project(), "x.adsksimpkg");
        var j = FusionSimPkg.ToStudyJson(s);
        Assert.Equal("mns.study/1", j["schema"]!.GetValue<string>());
        Assert.Equal("LinearStress", j["analysis"]!["type"]!.GetValue<string>());
        var bodies = j["bodies"]!.AsArray().Select(b => b!["id"]!.GetValue<string>()).ToHashSet();
        var mats = j["materials"]!.AsArray().Select(m => m!["id"]!.GetValue<string>()).ToHashSet();
        Assert.All(j["bodies"]!.AsArray(), b => Assert.Contains(b!["material"]!.GetValue<string>(), mats));
        Assert.All(j["constraints"]!.AsArray().SelectMany(c => c!["bodies"]!.AsArray()), b => Assert.Contains(b!.GetValue<string>(), bodies));
        Assert.All(j["contacts"]!.AsArray(), c => { Assert.Contains(c!["primary"]!.GetValue<string>(), bodies); Assert.Contains(c["secondary"]!.GetValue<string>(), bodies); });
        var acc = j["loads"]!.AsArray().Single(l => l!["type"]!.GetValue<string>() == "LinearAcceleration")!;
        Assert.Equal(1.0, acc["direction"]![2]!.GetValue<double>());
        // 직렬화 → 다시 읽어도 같은 문서
        Assert.Equal(j.ToJsonString(), JsonNode.Parse(j.ToJsonString())!.ToJsonString());
    }

    [Fact]
    public void Inverted_curved_tet10_is_straightened_and_mass_matches_ccx_quadrature()
    {
        var m = new FeMesh();
        Vec3[] c = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(0, 0, 1)];
        m.Nodes.AddRange(c);
        foreach (var (a, b) in new[] { (0, 1), (1, 2), (2, 0), (0, 3), (1, 3), (2, 3) }) m.Nodes.Add((c[a] + c[b]) * 0.5);
        m.Elements.Add(new FeElement(FeType.C3D10, Enumerable.Range(0, 10).ToArray()));
        // 곧은 변: 부피 1/6
        Assert.Equal(1000.0 / 6, SimPkgCcx.CcxMass(m, 1000), 9);
        // 중간 절점(0-1 변)을 요소 안쪽 깊이 넣으면 적분점 야코비안이 음수 → 곧은 변으로 되돌린다
        m.Nodes[4] = new Vec3(0.5, 0.9, 0.9);
        Assert.Equal(1, SimPkgCcx.StraightenInvalid(m));
        Assert.Equal(new Vec3(0.5, 0, 0), m.Nodes[4]);
        Assert.Equal(0, SimPkgCcx.StraightenInvalid(m));
        // 바깥으로 살짝 휜 변(유효)은 그대로 두고, 질량은 곧은 변 부피와 다르다(ccx 와 같은 4점 적분)
        m.Nodes[4] = new Vec3(0.5, -0.05, -0.05);
        Assert.Equal(0, SimPkgCcx.StraightenInvalid(m));
        Assert.True(SimPkgCcx.CcxMass(m, 1000) > 1000.0 / 6);
    }

    [Fact]
    public void Point_triangle_distance_regions()
    {
        Vec3 a = new(0, 0, 0), b = new(1, 0, 0), c = new(0, 1, 0);
        Assert.Equal(0.5, SimPkgCcx.PointTriangleDistance(new(0.2, 0.2, 0.5), a, b, c), 12);   // 면 안쪽
        Assert.Equal(1.0, SimPkgCcx.PointTriangleDistance(new(-1, 0, 0), a, b, c), 12);       // 꼭짓점 a
        Assert.Equal(0.5, SimPkgCcx.PointTriangleDistance(new(0.5, -0.5, 0), a, b, c), 12);   // 변 ab
        Assert.Equal(Math.Sqrt(0.5), SimPkgCcx.PointTriangleDistance(new(1, 1, 0), a, b, c), 12); // 변 bc
    }

    [Fact]
    public void Mojibake_cp949_read_as_latin1_is_restored()
    {
        // 실측 Explicit 패키지 이름 "BatteryCase:1 > ÄÉÀÌ½º:1 > 본체1" — CP949 "케이스"(C4 C9 C0 CC BD BA)를 Latin-1 로 읽은 꼴
        var mb = new string([(char)0xC4, (char)0xC9, (char)0xC0, (char)0xCC, (char)0xBD, (char)0xBA]);
        Assert.Equal("BatteryCase:1 > 케이스:1 > 본체1", FusionSimPkg.FixMojibake("BatteryCase:1 > " + mb + ":1 > 본체1"));
        // 정상 문자열 · 단독 Latin-1 기호 · 한글이 안 되는 짝은 그대로
        Assert.Equal("Plate_A", FusionSimPkg.FixMojibake("Plate_A"));
        Assert.Equal("각도 90°C", FusionSimPkg.FixMojibake("각도 90°C"));
        Assert.Equal("×÷", FusionSimPkg.FixMojibake("×÷"));
        // 그래프 전체(Description 안 JSON 문자열 포함)에 적용되고 경고가 남는다
        var p = Project();
        var g1 = p["data"]!.AsArray().OfType<JsonObject>().First(o => o["id"]!.GetValue<string>() == "g1");
        g1["attributes"]!["Name"] = $"모형 1:1 > {mb}:1 > Plate_A";
        g1["attributes"]!["Description"] = "{\"bodyName\":\"Plate_A\",\"componentNames\":[\"모형 1:1\",\"" + mb + ":1\"]}";
        var s = FusionSimPkg.Parse(p);
        Assert.Equal("모형 1:1 > 케이스:1 > Plate_A", s.Bodies[0].Name);
        Assert.Equal("케이스:1", s.Bodies[0].Components[1]);
        Assert.Contains(s.Warnings, w => w.Contains("복원"));
    }

    [Fact]
    public void Unbonded_body_mass_goes_to_nearest_receiver()
    {
        var recv = new List<Vec3> { new(0, 0, 0), new(1, 0, 0), new(0, 5, 0) };
        var giv = new List<Vec3> { new(0.9, 0.1, 0), new(0, 4, 0), new(-3, 0, 0) };
        Assert.Equal(new[] { 1, 2, 0 }, SimPkgCcx.NearestReceivers(giv, recv));
        Assert.Equal(new[] { -1 }, SimPkgCcx.NearestReceivers([Vec3.Zero], []));
    }

    [Fact]
    public void Percentile_matches_numpy_linear_and_thickness_rule()
    {
        double[] v = [5, 1, 4, 2, 3];                                   // numpy.percentile([1..5], 99) = 4.96
        Assert.Equal(4.96, SimPkgCcx.Percentile(v, 0.99), 12);
        Assert.Equal(3, SimPkgCcx.Percentile(v, 0.5), 12);
        Assert.Equal(1, SimPkgCcx.Percentile(v, 0), 12);
        Assert.True(double.IsNaN(SimPkgCcx.Percentile(Array.Empty<double>(), 0.99)));
        // 두께 추정 2V/A: 100 × 100 × 2 mm 판 → 2·20000/(2·10000 + 4·200) = 1.923 mm, 겉넓이 없으면 경계 상자 최소 치수
        var plate = new SimPkgCcx.StepPart("p.step", 1, "P", 2e-5, Vec3.Zero, new Vec3(0.1, 0.1, 0.002), 2 * 0.01 + 4 * 0.0002);
        Assert.Equal(2 * 2e-5 / 0.0208, SimPkgCcx.Thickness(plate), 12);
        Assert.Equal(0.002, SimPkgCcx.Thickness(plate with { Area = double.NaN }), 12);
    }

    [Fact]
    public void Reads_integration_point_stresses_from_dat()
    {
        var dat = Path.Combine(Path.GetTempPath(), $"mns_ip_{Guid.NewGuid():N}.dat");
        File.WriteAllText(dat, string.Join("\n",
            "",
            "                        S T E P       1",
            "",
            "       total force (fx,fy,fz) for set NFIX and time  0.1000000E+01",
            "",
            "        1.0E+00  2.0E+00  3.0E+00",
            "",
            " stresses (elem, integ.pnt.,sxx,syy,szz,sxy,sxz,syz) for set B3 and time  0.1000000E+01",
            "",
            "         1   1  1.000000E+08  0.000000E+00  0.000000E+00  0.000000E+00  0.000000E+00  0.000000E+00",
            "         1   2  0.000000E+00  0.000000E+00  0.000000E+00  0.000000E+00  1.000000E+06  0.000000E+00",
            "",
            " stresses (elem, integ.pnt.,sxx,syy,szz,sxy,sxz,syz) for set B10 and time  0.1000000E+01",
            "",
            "         7   1  0.000000E+00  0.000000E+00  0.000000E+00  0.000000E+00  0.000000E+00  2.000000E+06",
            ""));
        try
        {
            var r = SimPkgCcx.ReadElementVonMises(dat);
            Assert.Equal(new[] { "B3", "B10" }, r.Keys);
            Assert.Equal(1e8, r["B3"][0], 3);                       // 단축 응력 = von Mises
            Assert.Equal(Math.Sqrt(3) * 1e6, r["B3"][1], 3);        // 순수 전단 sxz → √3·τ
            Assert.Equal(Math.Sqrt(3) * 2e6, r["b10"].Single(), 3); // 집합 이름 대소문자 무관
        }
        finally { File.Delete(dat); }
    }

    [Fact]
    public void Fusion_report_text_parser_reads_mesh_load_and_result_summary()
    {
        // 실모델 정적 해석 보고서 텍스트와 같은 줄 구조(결과 요약 표는 이름만 — 수치 없음)
        string[] empty =
        [
            "\uFEFF", "학습 보고서", "해석된 파일", "샘플_FEA_정적", "접촉 공차", "1.00 mm", "메쉬", "유형", "솔리드", "노드", "120000", "요소", "60000",
            "선형 전역 하중1", "X 값", "0.00 m / s^2", "Y 값", "0.00 m / s^2", "Z 값", "-490.33 m / s^2",
            "하중 조건1 - 결과", "결과 요약", "결과 유형", "하위 결과", "최소", "최대", "안전계수", "안전계수", "응력", "폰 미세스", "첫 번째 주", "세 번째 주", "변위", "전체",
        ];
        var d = FusionReport.Parse(empty, "r.txt");
        Assert.Equal("샘플_FEA_정적", d.Study);
        Assert.Equal(120000, d.Nodes);
        Assert.Equal(60000, d.Elements);
        Assert.Equal(new Vec3(0, 0, -490.33), d.Acceleration);
        Assert.Equal(0.001, d.ContactTolerance!.Value, 12);
        Assert.Null(d.MinSafetyFactor);
        Assert.Contains(d.Notes, n => n.Contains("수치가 없습니다"));
        // 범례 값(사람이 읽은 것) — 단위 MPa · mm → SI, 출처 남김
        FusionReport.ApplyLegend(d, "sf=1.5;vm=200.5;u=1.25", "범례");
        Assert.Equal(1.5, d.MinSafetyFactor!.Value);
        Assert.Equal(200.5e6, d.MaxVonMises!.Value, 3);
        Assert.Equal(1.25e-3, d.MaxDisplacement!.Value, 12);
        Assert.Equal("범례", d.MaxVonMises.Source);
        Assert.Throws<InvalidDataException>(() => FusionReport.ApplyLegend(d, "x=1", "범례"));
        // 수치가 채워진 표(유형 칸 행 병합 — HTML)
        string[] filled =
        [
            "결과 요약", "결과 유형", "하위 결과", "최소", "최대", "안전계수", "안전계수", "1.5", "15", "응력", "폰 미세스", "1.326E-18 MPa", "200.5 MPa",
            "첫 번째 주", "-50.1 MPa", "180.2 MPa", "변위", "전체", "0 mm", "1.25 mm",
        ];
        var f = FusionReport.Parse(filled);
        Assert.Equal(1.5, f.MinSafetyFactor!.Value, 12);
        Assert.Equal(200.5e6, f.MaxVonMises!.Value, 3);
        Assert.Equal(1.25e-3, f.MaxDisplacement!.Value, 12);
        Assert.StartsWith("보고서 결과 요약 표", f.MaxVonMises.Source);
        // HTML: 태그 · data: 그림 제거 후 같은 규칙
        var lines = FusionReport.HtmlToLines("<tr><td><span>노드</span></td><td><span>120000</span></td></tr><img src=\"data:image/png;base64,AAAA\"/>");
        Assert.Equal(120000, FusionReport.Parse(lines).Nodes);
    }

    [Fact]
    public void Design_step_manifest_resolves_moved_paths()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mns_man_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, "station"));
        try
        {
            File.WriteAllText(Path.Combine(dir, "station", "S000.step"), "x");
            File.WriteAllText(Path.Combine(dir, "A_Plate.step"), "x");
            // 다른 PC 의 절대 경로 → 같은 폴더의 같은 파일 이름
            File.WriteAllText(Path.Combine(dir, "station", "station.json"),
                "[{\"body\":\"Plate\",\"step\":\"Z:/other/pc/S000.step\",\"vol_mm3\":1000},{\"body\":\"Gone\",\"step\":\"Z:/x/S999.step\",\"vol_mm3\":1}]");
            File.WriteAllText(Path.Combine(dir, "bodies.json"), "{\"cases\":{\"A\":[{\"body\":\"Plate\",\"step\":\"Z:/y/A_Plate.step\",\"vol_mm3\":1000}]}}");
            var notes = new List<string>();
            var m = SimPkgCcx.Manifest(dir, notes);
            Assert.Equal(2, m.Count);
            Assert.Equal(Path.Combine(dir, "station", "S000.step"), m[0].Step);   // station 우선, 그다음 bodies.json
            Assert.Equal(Path.Combine(dir, "A_Plate.step"), m[1].Step);
            Assert.Equal(1e-6, m[0].VolumeM3, 15);
            Assert.Contains(notes, n => n.Contains("파일 없음 1"));
        }
        finally { Directory.Delete(dir, true); }
    }
}
