using System.Text.Json.Nodes;
using Sim.Core.Analysis;
using Sim.Core.Engine;
using Sim.Core.Geometry;
using Sim.Core.Scenario;
using Sim.Core.Structures;

namespace Sim.Core.Tests;

/// <summary>
/// Fusion 내보내기 1.1 추가 필드(parts[].materialProperties · appearance, userParameters, joints, usd)의 시뮬레이터 반영:
/// 재료 우선순위(Fusion 물성 &gt; 별칭 표), 외관 색 = 물체 색 기본값, 브라우저 값 보기, 결과 폴더 USD, 옛 massprops 하위 호환.
/// </summary>
public class FusionExportExtrasTests
{
    private static readonly string SampleDir = Path.Combine(TestUtil.RepoRoot, "samples", "fusion_export");

    /// <summary>공개 샘플(sensor_dummy) 사본에 1.1 필드를 넣어 임시 폴더에 쓴다. edit 로 parts[0] 과 최상위를 고친다.</summary>
    private static string WriteSample(string dir, Action<JsonObject, JsonObject> edit)
    {
        Directory.CreateDirectory(dir);
        File.Copy(Path.Combine(SampleDir, "sensor_dummy_sample.stl"), Path.Combine(dir, "sensor_dummy_sample.stl"), true);
        var o = J.ParseObject(File.ReadAllText(Path.Combine(SampleDir, "sensor_dummy_sample.massprops.json")));
        edit(o, o["parts"]!.AsArray()[0]!.AsObject());
        var path = Path.Combine(dir, "s.massprops.json");
        File.WriteAllText(path, o.ToJsonString());
        return path;
    }

    private static JsonObject AlProps(double e = 70e9) => new()
    {
        ["density"] = 2700.0, ["E"] = e, ["nu"] = 0.33, ["yield"] = 280e6, ["ultimate"] = 310e6,
        ["thermalExpansion"] = 23.6e-6, ["thermalConductivity"] = 167.0, ["specificHeat"] = 896.0,
    };

    private static World Build(string mp, string extra = "")
    {
        var p = mp.Replace("\\", "/");
        return TestUtil.Build($$"""
          {"schema":"mns.scenario/1","sim":{"dt":0.001,"tEnd":0.02},"environment":{"vacuum":true},
           "bodies":[{"name":"x","position":[0,0,-100],"massProps":"{{p}}","modelFrame":"fusion","noseAxis":"+Z"{{extra}}}]}
          """);
    }

    private static string TempDir() => Path.Combine(Path.GetTempPath(), $"mns_fx_{Guid.NewGuid():N}");

    [Fact]
    public void Old_massprops_without_new_fields_behaves_as_before()
    {
        var mp = MassPropsFile.Load(Path.Combine(SampleDir, "sensor_dummy_sample.massprops.json"));
        Assert.Single(mp.Parts);
        Assert.Null(mp.Parts[0].MaterialProperties);
        Assert.Null(mp.Parts[0].BaseColor);
        Assert.Empty(mp.UserParameters);
        Assert.Empty(mp.Joints);
        Assert.Null(mp.Usd);
        Assert.Null(mp.AppearanceColor);
        Assert.Null(mp.DominantFusionMaterial);
        Assert.Equal("합성 샘플(균일)", mp.DominantMaterial);

        var w = Build(Path.Combine(SampleDir, "sensor_dummy_sample.massprops.json"));
        var b = w.GetBody("x");
        Assert.Equal("#9aa4b1", b.Color);          // 외관 없음 → 기존 기본 색
        Assert.Null(b.Structure);                   // 표에 없는 재료 + 물성 없음 → 지금처럼 생략
        var shape = AssemblyTree.Build(w).Descendants().Single(n => n.Kind == AssemblyNodeKind.Shape && n.Name == "형상");
        Assert.Empty(shape.Children);
    }

    [Fact]
    public void Fusion_material_properties_take_priority_and_support_unlisted_material()
    {
        var dir = TempDir();
        try
        {
            // 표에 없는 재료 이름 — 물성 숫자로 판정 가능
            var mp = WriteSample(dir, (_, p) => { p["material"] = "Custom Alloy X"; p["materialProperties"] = AlProps(); });
            Assert.Null(MaterialLibrary.Find("Custom Alloy X"));
            var s = Build(mp).GetBody("x").Structure!;
            Assert.Equal("fusion-props", s.MaterialSource);
            Assert.Equal(MaterialLibrary.FusionSource, s.Material!.Source);
            Assert.Equal("Custom Alloy X", s.Material.Name);
            Assert.Equal(70e9, s.Material.E, 3);
            Assert.Equal(280e6, s.Material.Yield, 3);
            Assert.Equal(310e6, s.Material.Ultimate, 3);
            Assert.Equal(2700, s.Material.Density, 9);
            Assert.Equal(0.33, s.Material.Nu, 12);

            // 표에 있는 재료 — Fusion 숫자가 표보다 우선, 표의 변형률 속도 모델은 이어받음
            var mp2 = WriteSample(dir, (_, p) => { p["material"] = "Aluminum 6061"; p["materialProperties"] = AlProps(e: 65e9); });
            var table = MaterialLibrary.Find("Aluminum 6061")!;
            var s2 = Build(mp2).GetBody("x").Structure!;
            Assert.Equal("fusion-props", s2.MaterialSource);
            Assert.Equal(65e9, s2.Material!.E, 3);
            Assert.NotEqual(table.E, s2.Material.E);
            Assert.Equal(table.RateModel, s2.Material.RateModel);

            // 시나리오가 재질을 지정하면 그것이 우선
            var s3 = Build(mp2, ""","structure":{"material":"ss304"}""").GetBody("x").Structure!;
            Assert.Equal("scenario", s3.MaterialSource);
            Assert.Equal("ss304", s3.Material!.Key);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Partial_material_properties_fall_back_to_alias_table()
    {
        var dir = TempDir();
        try
        {
            // 밀도만 — 판정에 모자라 별칭 표(fusion-auto)
            var mp = WriteSample(dir, (_, p) => { p["material"] = "Aluminum 6061"; p["materialProperties"] = new JsonObject { ["density"] = 2710.0 }; });
            Assert.Null(MassPropsFile.Load(mp).DominantFusionMaterial);
            var s = Build(mp).GetBody("x").Structure!;
            Assert.Equal("fusion-auto", s.MaterialSource);
            Assert.Equal("al6061-t6", s.Material!.Key);
            // E 와 인장 강도만(항복 없음) → 항복 = 인장
            var m = MaterialLibrary.FromFusion("Polymer Q", new JsonObject { ["E"] = 2.0e9, ["ultimate"] = 40e6 })!;
            Assert.Equal(40e6, m.Yield, 3);
            Assert.Equal(0.3, m.Nu, 12);
            Assert.Null(MaterialLibrary.FromFusion("Polymer Q", new JsonObject { ["E"] = 2.0e9 }));
            Assert.Null(MaterialLibrary.FromFusion("Polymer Q", new JsonObject { ["E"] = -1.0, ["yield"] = 1e6 }));
            // 표에 없는 이름 + 모자란 물성 → 지금처럼 구조 정의 생략
            var mp2 = WriteSample(dir, (_, p) => { p["materialProperties"] = new JsonObject { ["density"] = 1000.0 }; });
            Assert.Null(Build(mp2).GetBody("x").Structure);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Appearance_base_color_becomes_default_body_color()
    {
        var dir = TempDir();
        try
        {
            var mp = WriteSample(dir, (_, p) => p["appearance"] = new JsonObject
            {
                ["name"] = "Anodized Red", ["baseColor"] = new JsonArray(0.8, 0.0, 0.2), ["metallic"] = 1.0, ["roughness"] = 0.3,
            });
            Assert.Equal("#cc0033", MassPropsFile.Load(mp).AppearanceColor);
            Assert.Equal("#cc0033", Build(mp).GetBody("x").Color);
            // 시나리오 color 가 있으면 그것(외관은 기본값 자리)
            Assert.Equal("#123456", Build(mp, ""","color":"#123456" """).GetBody("x").Color);
            // baseColor 없는 외관(이름만) → 기본 색
            var mp2 = WriteSample(dir, (_, p) => p["appearance"] = new JsonObject { ["name"] = "Steel - Satin" });
            Assert.Null(MassPropsFile.Load(mp2).AppearanceColor);
            Assert.Equal("#9aa4b1", Build(mp2).GetBody("x").Color);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Appearance_color_of_heaviest_part_wins_and_hex_is_validated()
    {
        Assert.Equal("#ff8000", MassPropsFile.ColorHex(new JsonArray(1.0, 0.5, 0.0)));
        Assert.Equal("#ff0000", MassPropsFile.ColorHex(new JsonArray(2.0, -1.0, 0.0)));   // 0–1 로 자름
        Assert.Null(MassPropsFile.ColorHex(new JsonArray(1.0, 0.5)));
        Assert.Null(MassPropsFile.ColorHex(new JsonArray("a", 0, 0)));
        Assert.Null(MassPropsFile.ColorHex(null));
        var dir = TempDir();
        try
        {
            var mp = WriteSample(dir, (o, p) =>
            {
                p["appearance"] = new JsonObject { ["baseColor"] = new JsonArray(0.0, 0.0, 1.0) };
                var light = p.DeepClone().AsObject();
                light["body"] = "Cap"; light["mass"] = 0.01; light["appearance"] = new JsonObject { ["baseColor"] = new JsonArray(1.0, 1.0, 1.0) };
                o["parts"]!.AsArray().Add(light);
            });
            Assert.Equal("#0000ff", MassPropsFile.Load(mp).AppearanceColor);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void User_parameters_and_joints_show_under_shape_node()
    {
        var dir = TempDir();
        try
        {
            var mp = WriteSample(dir, (o, _) =>
            {
                o["userParameters"] = new JsonArray(
                    new JsonObject { ["name"] = "arm_len", ["expression"] = "250 mm", ["unit"] = "mm", ["value"] = 25.0, ["valueSI"] = 0.25, ["siUnit"] = "m", ["comment"] = "암 길이" },
                    new JsonObject { ["name"] = "label", ["expression"] = "'A'", ["unit"] = "" });
                o["joints"] = new JsonArray(
                    new JsonObject { ["name"] = "Hinge", ["type"] = "Revolute", ["source"] = "joint", ["occurrenceOne"] = "arm:1", ["occurrenceTwo"] = "", ["origin"] = new JsonArray(0.1, 0.0, 0.05) },
                    new JsonObject { ["name"] = "AB", ["type"] = "Rigid", ["source"] = "asBuiltJoint", ["occurrenceOne"] = "a:1", ["occurrenceTwo"] = "b:1", ["suppressed"] = true });
            });
            var w = Build(mp);
            var shape = AssemblyTree.Build(w).Descendants().Single(n => n.Kind == AssemblyNodeKind.Shape && n.Name == "형상");
            var prm = shape.Children.Single(n => n.Name == "사용자 파라미터");
            Assert.Equal("2", prm.Detail);
            Assert.Equal("250 mm = 0.25 m · 암 길이", prm.Children.Single(n => n.Name == "arm_len").Detail);
            Assert.Equal("'A'", prm.Children.Single(n => n.Name == "label").Detail);
            var jg = shape.Children.Single(n => n.Name == "조인트");
            Assert.Equal("Revolute · arm:1 ↔ (루트) · 원점 (0.1, 0, 0.05) m", jg.Children.Single(n => n.Name == "Hinge").Detail);
            Assert.Equal("Rigid · a:1 ↔ b:1 · as-built · 억제됨", jg.Children.Single(n => n.Name == "AB").Detail);
            Assert.All(shape.Descendants(), n => Assert.Equal(AssemblyNodeKind.Shape, n.Kind));   // 정보 줄(값 보기)만
            Assert.All(shape.Descendants(), n => Assert.Equal(-1, n.BodyIndex));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Result_folder_copies_usd_with_model_to_body()
    {
        var dir = TempDir();
        var outDir = Path.Combine(dir, "out");
        try
        {
            var mp = WriteSample(dir, (o, _) => o["usd"] = new JsonObject { ["file"] = "s.usdz", ["frame"] = "fusion" });
            File.WriteAllText(Path.Combine(dir, "s.usdz"), "usd-stub");
            var doc = ScenarioDoc.Parse($$"""
              {"schema":"mns.scenario/1","sim":{"dt":0.001,"tEnd":0.02},"environment":{"vacuum":true},
               "bodies":[{"name":"x","position":[0,0,-100],"massProps":"{{mp.Replace("\\", "/")}}","modelFrame":"fusion","noseAxis":"+Z"},
                         {"name":"y","position":[0,5,-100],"massProps":"{{Path.Combine(SampleDir, "sensor_dummy_sample.massprops.json").Replace("\\", "/")}}"}]}
              """, TestUtil.RepoRoot);
            var w = doc.Build();
            w.Run();
            Results.WriteAll(doc, w, outDir);
            Assert.Equal("usd-stub", File.ReadAllText(Path.Combine(outDir, "meshes", "x.usdz")));
            var scene = J.ParseObject(File.ReadAllText(Path.Combine(outDir, "scene.json")));
            var bodies = scene["bodies"]!.AsArray();
            var x = bodies.Single(b => b!["name"]!.GetValue<string>() == "x")!.AsObject();
            Assert.Equal("meshes/x.usdz", x["usd"]!.GetValue<string>());
            Assert.Equal("meshes/x.stl", x["mesh"]!.GetValue<string>());                  // STL 도 그대로
            var m2b = w.GetBody("x").ModelToBody;
            Assert.Equal(m2b.M13, J.ToDouble(x["modelToBody"]![0]![2]!), 12);
            var y = bodies.Single(b => b!["name"]!.GetValue<string>() == "y")!.AsObject();
            Assert.Null(y["usd"]);                                                           // USD 없는 옛 massprops
            Assert.False(File.Exists(Path.Combine(outDir, "meshes", "y.usdz")));
        }
        finally { Directory.Delete(dir, true); }
    }
}
