using System.Text.Json.Nodes;
using Sim.App.Rendering;
using Sim.Core.Environment;

namespace Sim.Core.Tests;

/// <summary>
/// 3D 장면 환경: 환경 파일 형식(내장 7종 로드 · 스키마 검증 · 오류 메시지 · 메시 JSON · 사용자 .hdr 한 장 → 환경),
/// 절차적 텍스처(크기 · 값 범위 · 결정성 · 타일링), 토양 → 환경, 장면 설정 저장/복원.
/// </summary>
public class SceneEnvironmentTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mns_scene_" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "settings.json");
    private static string BuiltinDir => Path.Combine(TestUtil.RepoRoot, "config", "environments");
    private static readonly string[] BuiltinIds = ["analysis", "dirt", "forest-edge", "grass", "rock", "sand", "snow"];

    public SceneEnvironmentTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static EnvironmentRegistry Builtin()
    {
        var r = EnvironmentRegistry.FromDirectory(BuiltinDir);
        Assert.Empty(r.Errors);
        return r;
    }

    public static IEnumerable<object[]> SceneIds => BuiltinIds.Where(i => i != "analysis").Select(i => new object[] { i });

    // ---------------- 내장 환경 파일 ----------------

    [Fact]
    public void Builtin_seven_environments_load()
    {
        var r = Builtin();
        Assert.Equal(BuiltinIds, r.All.Select(e => e.Id).Order(StringComparer.Ordinal));
        Assert.True(r.Find("analysis")!.IsAnalysis);
        foreach (var e in r.All.Where(e => !e.IsAnalysis))
        {
            Assert.False(string.IsNullOrEmpty(e.NameKo));
            Assert.NotNull(e.Ground);
            Assert.NotNull(e.Dome);
            Assert.NotNull(e.Sun);
            Assert.NotNull(e.Hemisphere);
            Assert.True(e.Ground!.IsGroundPlane);
            Assert.Equal("sky", e.Dome!.Texture.Procedural);
        }
        Assert.True(r.Find("forest-edge")!.Dome!.Texture.Params["trees"]!.GetValue<bool>());
    }

    [Theory]
    [MemberData(nameof(SceneIds))]
    public void Builtin_assets_size_range_and_determinism(string id)
    {
        var env = Builtin().Find(id)!;
        var a = SceneEnvironment.Prepare(env);
        Assert.Equal(SceneEnvironment.GroundSize * SceneEnvironment.GroundSize * 4, a.GroundPx.Length);
        Assert.Equal(SceneEnvironment.SkyWidth * SceneEnvironment.SkyHeight * 4, a.SkyPx.Length);
        Assert.Equal(a.GroundPx, SceneEnvironment.Prepare(Builtin().Find(id)!).GroundPx);
        for (int i = 3; i < a.GroundPx.Length; i += 4) Assert.Equal(255, a.GroundPx[i]);
        var lum = Enumerable.Range(0, a.GroundPx.Length / 4).Select(i => (a.GroundPx[4 * i] + a.GroundPx[4 * i + 1] + a.GroundPx[4 * i + 2]) / 3.0).ToArray();
        Assert.True(lum.Max() - lum.Min() > 15, $"{id}: 무늬 폭 {lum.Max() - lum.Min():F1}");
        Assert.InRange(lum.Average(), 20, 250);
        // 태양은 위에서 비춤(GL y > 0), 단위 벡터
        Assert.True(a.SunDirGl.Y > 0.3);
        Assert.Equal(1, Math.Sqrt(a.SunDirGl.X * a.SunDirGl.X + a.SunDirGl.Y * a.SunDirGl.Y + a.SunDirGl.Z * a.SunDirGl.Z), 4);
        Assert.InRange(a.Exposure, 0.5f, 2f);
    }

    [Theory]
    [MemberData(nameof(SceneIds))]
    public void Ground_texture_tiles_seamlessly(string id)
    {
        const int n = SceneEnvironment.GroundSize;
        var a = SceneEnvironment.GroundTexture(Builtin().Find(id)!.Ground!.Texture);
        int Px(int x, int y, int c) => a[4 * (y * n + x) + c];
        double Diff(Func<int, (int, int, int, int)> pair) => Enumerable.Range(0, n).Average(k =>
        {
            var (x0, y0, x1, y1) = pair(k);
            return Enumerable.Range(0, 3).Sum(c => Math.Abs(Px(x0, y0, c) - Px(x1, y1, c)));
        });
        double insideX = Diff(k => (n / 2, k, n / 2 + 1, k)), insideY = Diff(k => (k, n / 2, k, n / 2 + 1));
        Assert.True(Diff(k => (n - 1, k, 0, k)) < insideX * 3 + 6, id);
        Assert.True(Diff(k => (k, n - 1, k, 0)) < insideY * 3 + 6, id);
    }

    [Theory]
    [MemberData(nameof(SceneIds))]
    public void Sky_gradient_and_tree_alpha(string id)
    {
        var dome = Builtin().Find(id)!.Dome!.Texture;
        var s = SceneEnvironment.SkyTexture(dome);
        int w = SceneEnvironment.SkyWidth, h = SceneEnvironment.SkyHeight;
        Assert.Equal(s, SceneEnvironment.SkyTexture(dome));
        (float R, float G, float B) C(string k) { var a = (JsonArray)dome.Params[k]!; return (a[0]!.GetValue<float>(), a[1]!.GetValue<float>(), a[2]!.GetValue<float>()); }
        int Row(double e) => (int)((e - SceneEnvironment.SkyMinElevation) / (90 - SceneEnvironment.SkyMinElevation) * h);
        void Near((float R, float G, float B) c, int row)
        {
            int i = 4 * (row * w + 5);
            Assert.InRange(s[i], c.R * 255 - 6, c.R * 255 + 6);
            Assert.InRange(s[i + 1], c.G * 255 - 6, c.G * 255 + 6);
            Assert.InRange(s[i + 2], c.B * 255 - 6, c.B * 255 + 6);
        }
        Near(C("zenith"), h - 1);
        Near(C("horizon"), Row(0));
        var alpha = Enumerable.Range(0, w).Select(i => s[4 * i + 3]).ToArray();
        if (dome.Params["trees"]!.GetValue<bool>())
        {
            Assert.All(alpha, x => Assert.InRange(x, 1, 254));
            Assert.True(alpha.Max() - alpha.Min() > 20, "나무 높이가 고르지 않아야 실루엣");
        }
        else Assert.All(alpha, x => Assert.Equal(0, x));
    }

    [Fact]
    public void Builtin_ground_textures_differ()
    {
        var r = Builtin();
        var tex = SceneIds.Select(o => SceneEnvironment.GroundTexture(r.Find((string)o[0])!.Ground!.Texture)).ToArray();
        for (int i = 0; i < tex.Length; i++)
            for (int j = i + 1; j < tex.Length; j++)
                Assert.NotEqual(tex[i], tex[j]);
    }

    // ---------------- 토양 → 환경 ----------------

    [Theory]
    [InlineData("sand", "sand")]
    [InlineData("clay", "dirt")]
    [InlineData("soil", "dirt")]
    [InlineData("rock", "rock")]
    [InlineData("frozen", "snow")]
    [InlineData("farmland", "grass")]
    [InlineData("ROCK ", "rock")]
    [InlineData("unknown", "grass")]
    [InlineData(null, "grass")]
    public void Soil_maps_to_environment(string? soil, string expected) => Assert.Equal(expected, Builtin().ForSoil(soil).Id);

    [Fact]
    public void Every_builtin_soil_resolves_and_ids_resolve()
    {
        var r = Builtin();
        foreach (var name in SimEnvironment.Soils.Keys) Assert.False(r.ForSoil(name).IsAnalysis, name);
        Assert.Equal("sand", r.Resolve(EnvironmentRegistry.Auto, "sand").Id);
        Assert.Equal("snow", r.Resolve("snow", "sand").Id);
        Assert.True(r.Resolve("no-such-env", "sand").IsAnalysis);
        Assert.True(new EnvironmentRegistry().Resolve(EnvironmentRegistry.Auto, "sand").IsAnalysis);
    }

    // ---------------- 스키마 검증 · 오류 메시지 ----------------

    private string WriteEnv(string id, JsonObject o)
    {
        var d = Path.Combine(_dir, "envs", id);
        Directory.CreateDirectory(d);
        var p = Path.Combine(d, "environment.json");
        File.WriteAllText(p, o.ToJsonString());
        return p;
    }

    private static JsonObject Valid(string id = "mine") => (JsonObject)JsonNode.Parse("""
        {"schema":"mns.environment/1","id":"@ID",
         "features":[{"kind":"ground","texture":{"procedural":{"kind":"sand","seed":3}}},
                     {"kind":"dome","texture":{"procedural":{"kind":"sky"}}}],
         "lights":[{"type":"directional","direction":[0,0.5,-1]}]}
        """.Replace("@ID", id))!;

    [Fact]
    public void Minimal_file_gets_defaults()
    {
        var e = EnvironmentLoader.Load(WriteEnv("mine", Valid()));
        Assert.Equal("mine", e.DisplayName);
        Assert.True(e.Scaling.AutoScale);
        Assert.Equal(10, e.Scaling.AutoScaleFactor);
        Assert.Equal("procedural-sky", e.Ibl.Source);
        Assert.Equal("plane", e.Ground!.Shape);
        Assert.Equal((1f, 1f, 1f), e.Sun!.Color);
        Assert.Equal(3u, e.Ground.Texture.Seed);
    }

    [Theory]
    [InlineData("schema", "\"other/1\"", "schema")]
    [InlineData("id", "\"bad id!\"", "id")]
    [InlineData("style", "\"fancy\"", "style")]
    [InlineData("features", "[{\"kind\":\"wall\",\"texture\":{}}]", "features[0].kind")]
    [InlineData("features", "[{\"kind\":\"ground\",\"texture\":{\"procedural\":{\"kind\":\"lava\"}}}]", "features[0].texture.procedural.kind")]
    [InlineData("features", "[{\"kind\":\"dome\",\"texture\":{\"procedural\":{\"kind\":\"grass\"}}}]", "features[0].texture.procedural.kind")]
    [InlineData("features", "[{\"kind\":\"ground\",\"texture\":{\"procedural\":{\"kind\":\"grass\",\"colors\":[[1,2]]}}}]", "features[0].texture.procedural.colors[0]")]
    [InlineData("features", "[{\"kind\":\"ground\",\"shape\":\"nope.json\",\"texture\":{\"procedural\":{\"kind\":\"grass\"}}}]", "features[0].shape")]
    [InlineData("features", "[{\"kind\":\"dome\",\"texture\":{\"image\":\"missing.hdr\"}}]", "features[0].texture.image")]
    [InlineData("lights", "[{\"type\":\"directional\",\"direction\":[0,0,1]}]", "lights[0].direction")]
    [InlineData("lights", "[{\"type\":\"spot\"}]", "lights[0].type")]
    [InlineData("scaling", "{\"autoScale\":\"yes\"}", "scaling.autoScale")]
    [InlineData("soilMatch", "[1]", "soilMatch[0]")]
    [InlineData("ibl", "{\"source\":\"nope.hdr\"}", "ibl.source")]
    public void Bad_files_are_rejected_with_path_and_field(string key, string json, string field)
    {
        var o = Valid();
        o[key] = JsonNode.Parse(json);
        var p = WriteEnv("bad", o);
        var ex = Assert.Throws<EnvironmentFormatException>(() => EnvironmentLoader.Load(p));
        Assert.Equal(field, ex.Field);
        Assert.Contains(p, ex.Message);
        Assert.Contains($"'{field}'", ex.Message);
    }

    [Fact]
    public void Not_json_and_missing_schema_are_rejected()
    {
        var d = Path.Combine(_dir, "envs", "x");
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "environment.json"), "{ nope");
        Assert.Equal("(JSON)", Assert.Throws<EnvironmentFormatException>(() => EnvironmentLoader.Load(d)).Field);
        File.WriteAllText(Path.Combine(d, "environment.json"), "{\"id\":\"x\"}");
        Assert.Equal("schema", Assert.Throws<EnvironmentFormatException>(() => EnvironmentLoader.Load(d)).Field);
    }

    [Fact]
    public void Directory_load_keeps_good_files_and_reports_bad()
    {
        WriteEnv("good", Valid("good"));
        var bad = Valid("broken");
        bad["lights"] = JsonNode.Parse("[{\"type\":\"spot\"}]");
        WriteEnv("broken", bad);
        var r = EnvironmentRegistry.FromDirectory(Path.Combine(_dir, "envs"));
        Assert.NotNull(r.Find("good"));
        Assert.Null(r.Find("broken"));
        Assert.Single(r.Errors);
        Assert.Contains("lights[0].type", r.Errors[0]);
    }

    // ---------------- 메시 JSON ----------------

    [Fact]
    public void Mesh_json_with_fusion_structure_parses()
    {
        var mp = Path.Combine(_dir, "ground.mesh.json");
        File.WriteAllText(mp, """
            {"units":"cm","up":"z",
             "Vertices":{"Positions":[-2057,-2057,0, 2057,-2057,0, 2057,2057,0, -2057,2057,0],"UVs":[0,0, 1,0, 1,1, 0,1]},
             "Indices":[0,1,2, 0,2,3]}
            """);
        var m = EnvironmentLoader.LoadMesh(mp);
        Assert.Equal(12, m.Positions.Length);
        Assert.Equal(8, m.UVs.Length);
        Assert.Equal([0, 1, 2, 0, 2, 3], m.Indices);
        Assert.Equal(20.57, m.HalfExtentMeters, 6);
        // 환경 파일의 shape 로 참조
        var o = Valid("meshy");
        var p = WriteEnv("meshy", o);
        File.Copy(mp, Path.Combine(Path.GetDirectoryName(p)!, "ground.mesh.json"));
        ((JsonObject)o["features"]![0]!)["shape"] = "ground.mesh.json";
        File.WriteAllText(p, o.ToJsonString());
        Assert.Equal(20.57, EnvironmentLoader.Load(p).Ground!.Mesh!.HalfExtentMeters, 6);
    }

    [Theory]
    [InlineData("""{"Vertices":{"Positions":[0,0,0, 1,0,0, 0,1,0]},"Indices":[0,1,5]}""", "Indices")]
    [InlineData("""{"Vertices":{"Positions":[0,0,0, 1,0]},"Indices":[]}""", "Vertices.Positions")]
    [InlineData("""{"Vertices":{"Positions":[0,0,0, 1,0,0, 0,1,0],"UVs":[0,0]},"Indices":[0,1,2]}""", "Vertices.UVs")]
    [InlineData("""{"Indices":[0,1,2]}""", "Vertices")]
    [InlineData("""{"units":"furlong","Vertices":{"Positions":[0,0,0, 1,0,0, 0,1,0]},"Indices":[0,1,2]}""", "units")]
    public void Bad_mesh_json_is_rejected(string json, string field)
    {
        var mp = Path.Combine(_dir, "bad.mesh.json");
        File.WriteAllText(mp, json);
        Assert.Equal(field, Assert.Throws<EnvironmentFormatException>(() => EnvironmentLoader.LoadMesh(mp)).Field);
    }

    // ---------------- 사용자 영상 한 장 → 환경 ----------------

    /// <summary>정거원통 합성 영상: 위 = 파랑 하늘, 아래 = 갈색 땅, 방위 90°(동) · 고도 30° 에 아주 밝은 태양.</summary>
    private static FloatImage SyntheticSky(int w = 128, int h = 64)
    {
        var rgb = new float[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double el = 90 - (y + 0.5) / h * 180, az = (x + 0.5) / w * 360;
                (float, float, float) c = el >= 0 ? (0.3f, 0.5f, 0.9f) : (0.4f, 0.3f, 0.2f);
                if (Math.Abs(el - 30) < 3 && Math.Abs(az - 90) < 3) c = (50f, 45f, 40f);
                int i = 3 * (y * w + x);
                (rgb[i], rgb[i + 1], rgb[i + 2]) = c;
            }
        return new FloatImage(w, h, rgb);
    }

    [Fact]
    public void Hdr_round_trip()
    {
        var p = Path.Combine(_dir, "sky.hdr");
        var img = SyntheticSky();
        EnvironmentLoader.WriteHdr(p, img);
        var back = EnvironmentLoader.ReadHdr(p);
        Assert.Equal((img.Width, img.Height), (back.Width, back.Height));
        for (int i = 0; i < img.Rgb.Length; i++) Assert.InRange(back.Rgb[i], img.Rgb[i] * 0.98 - 1e-3, img.Rgb[i] * 1.02 + 1e-3);
    }

    [Fact]
    public void User_hdr_becomes_environment()
    {
        var p = Path.Combine(_dir, "My Sky.hdr");
        EnvironmentLoader.WriteHdr(p, SyntheticSky());
        var env = EnvironmentLoader.Load(p);
        Assert.Equal("user-my-sky", env.Id);
        Assert.Contains("My Sky", env.DisplayName);
        Assert.Equal(Path.GetFullPath(p), env.Dome!.Texture.ImagePath);
        Assert.Equal(Path.GetFullPath(p), env.Ibl.Source);
        // 태양 = 가장 밝은 방향(동쪽 고도 30°) — 빛 방향은 그 반대
        var d = env.Sun!.Direction;
        Assert.InRange(-d.E, 0.8, 0.9);
        Assert.InRange(-d.U, 0.45, 0.55);
        // 하늘 · 땅 환경광 = 반구 평균(하늘은 파랑, 땅은 갈색)
        Assert.True(env.Hemisphere!.Sky.B > env.Hemisphere.Sky.R);
        Assert.True(env.Hemisphere.GroundColor.R > env.Hemisphere.GroundColor.B);
        var a = SceneEnvironment.Prepare(env);
        Assert.Equal(-90, a.SkyMinEl);
        Assert.Equal(128 * 64 * 4, a.SkyPx.Length);
        Assert.False(a.Trees);
        // 텍스처 첫 행 = 고도 −90°(땅 색), 마지막 행 = 천정(하늘 색)
        Assert.True(a.SkyPx[0] > a.SkyPx[2]);
        Assert.True(a.SkyPx[a.SkyPx.Length - 2] > a.SkyPx[a.SkyPx.Length - 4]);
        var reg = Builtin();
        reg.Add(env);
        Assert.Same(env, reg.Resolve("user-my-sky", null));
    }

    [Fact]
    public void Png_without_decoder_is_rejected_clearly()
    {
        var p = Path.Combine(_dir, "sky.png");
        File.WriteAllBytes(p, [137, 80, 78, 71]);
        var keep = EnvironmentLoader.ImageDecoder;
        EnvironmentLoader.ImageDecoder = null;
        try { Assert.Equal("(영상)", Assert.Throws<EnvironmentFormatException>(() => EnvironmentLoader.Load(p)).Field); }
        finally { EnvironmentLoader.ImageDecoder = keep; }
    }

    // ---------------- 장면 설정 ----------------

    [Fact]
    public void Default_settings_are_analysis()
    {
        var d = new SceneSettings();
        Assert.Equal("analysis", d.EnvironmentId);
        Assert.True(d.GroundDisplayed && d.GroundFlattened && d.Shadows && d.Fog && d.SkyBackground);
        Assert.False(d.GroundReflections);
        Assert.Null(SceneSettings.Load(FilePath));
    }

    [Fact]
    public void Settings_round_trip_and_keep_other_keys()
    {
        File.WriteAllText(FilePath, "{\"theme\": \"light\", \"other\": 42}");
        var s = new SceneSettings
        {
            EnvironmentId = "forest-edge", Brightness = 1.25, RotationDeg = 135, BackgroundType = "solid", BackgroundColor = (0.1f, 0.2f, 0.3f),
            GroundDisplayed = false, GroundFlattened = false, GroundReflections = true, GroundRoughness = 0.1, GroundOffset = -0.5, ExposureEV = 1,
            Shadows = false, Fog = false, UserEnvironments = [@"C:\envs\a.hdr", @"D:\b\environment.json"],
        };
        Assert.True(s.Save(FilePath));
        Assert.True(s.SameAs(SceneSettings.Load(FilePath)!));
        var text = File.ReadAllText(FilePath);
        Assert.Contains("\"theme\": \"light\"", text);
        Assert.Contains("\"other\": 42", text);
        Assert.Contains("\"groundRoughness\"", text);
        Assert.True((s with { EnvironmentId = EnvironmentRegistry.Auto }).Save(FilePath));
        Assert.Equal(EnvironmentRegistry.Auto, SceneSettings.Load(FilePath)!.EnvironmentId);
    }

    [Fact]
    public void Bad_values_fall_back_or_clamp()
    {
        File.WriteAllText(FilePath, "{\"scene\": {\"environmentId\": \"\", \"brightness\": 99, \"rotationDeg\": -90, \"fog\": \"yes\", \"exposureEV\": 40, \"groundRoughness\": 7, \"backgroundType\": \"plaid\", \"backgroundColor\": [1]}}");
        var s = SceneSettings.Load(FilePath)!;
        Assert.Equal("analysis", s.EnvironmentId);
        Assert.Equal(3, s.Brightness);
        Assert.Equal(270, s.RotationDeg);
        Assert.True(s.Fog);
        Assert.Equal(5, s.ExposureEV);
        Assert.Equal(1, s.GroundRoughness);
        Assert.True(s.SkyBackground);
        Assert.Null(s.BackgroundColor);
        File.WriteAllText(FilePath, "{ not json");
        Assert.Null(SceneSettings.Load(FilePath));
    }
}
