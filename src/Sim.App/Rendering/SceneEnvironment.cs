using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sim.App.Rendering;

/// <summary>
/// 3D 장면 환경 정의 — Fusion 렌더링 환경(Features: 지면 · 돔, IBL, 방향광, 축척, 배경, 표시 이름)의 구조를 차용한 자체 JSON 형식
/// (<c>config/environments/&lt;id&gt;/environment.json</c>, 형식 설명은 같은 폴더 README.md). Autodesk 파일은 읽지 않는다.
/// Style = "analysis" 는 이전 방식(단색 배경 + 격자, 조명 · 하늘 없음).
/// </summary>
public sealed class EnvironmentDef
{
    public const string SchemaName = "mns.environment/1";
    public required string Id { get; init; }
    public string NameKo { get; init; } = "";
    public string NameEn { get; init; } = "";
    public string Style { get; init; } = "scene";
    public bool IsAnalysis => Style == "analysis";
    /// <summary>환경 기본 정면 각도(Fusion 카탈로그 CenterAngle) — 장면 회전에 더한다.</summary>
    public double CenterAngleDeg { get; init; }
    public EnvScaling Scaling { get; init; } = new(2, true, 10);
    public IReadOnlyList<EnvFeature> Features { get; init; } = [];
    public EnvIbl Ibl { get; init; } = new("procedural-sky", 0);
    public IReadOnlyList<EnvLightDef> Lights { get; init; } = [];
    public (float R, float G, float B) AmbientColor { get; init; } = (0.25f, 0.25f, 0.25f);
    public float AmbientGain { get; init; }
    /// <summary>배경(단색) 색 — null 이면 테마 배경.</summary>
    public (float R, float G, float B)? Background { get; init; }
    /// <summary>토양 → 환경 자동 선택 표(environment.soil 이름, "*" = 그 밖 모두).</summary>
    public IReadOnlyList<string> SoilMatch { get; init; } = [];
    /// <summary>읽은 파일(내장 · 사용자).</summary>
    public string? SourcePath { get; init; }

    public string DisplayName => NameKo.Length > 0 ? NameKo : NameEn.Length > 0 ? NameEn : Id;
    public EnvFeature? Ground => Features.FirstOrDefault(f => f.Kind == "ground");
    public EnvFeature? Dome => Features.FirstOrDefault(f => f.Kind == "dome");
    public EnvLightDef? Sun => Lights.FirstOrDefault(l => l.Type == "directional" && l.Enabled);
    public EnvLightDef? Hemisphere => Lights.FirstOrDefault(l => l.Type == "hemisphere" && l.Enabled);

    /// <summary>파일이 없을 때도 앱이 그릴 수 있는 기본(분석) 환경.</summary>
    public static EnvironmentDef Analysis { get; } = new() { Id = "analysis", NameKo = "기본 (분석)", NameEn = "Analysis", Style = "analysis" };
}

/// <summary>Fusion Scaling(FitTo · AutoScale · AutoScaleFactor). 자동 축척이면 지면 무늬 타일 = 화면 범위 × 2.5 / 배율.</summary>
public sealed record EnvScaling(int FitTo, bool AutoScale, double AutoScaleFactor);

/// <summary>Fusion Feature(Shape · Texture · IsSurrounding · IsGroundPlane). Shape = "plane" · "hemisphere" 또는 메시 JSON.</summary>
public sealed record EnvFeature(string Kind, string Shape, MeshShape? Mesh, EnvTexture Texture, bool IsGroundPlane, bool IsSurrounding);

/// <summary>텍스처: 절차적(종류 · 씨앗 · 매개변수) 또는 이미지 파일(정거원통 .hdr · .png).</summary>
public sealed record EnvTexture(string? Procedural, uint Seed, JsonObject Params, string? ImagePath);

/// <summary>IBL 원천: "procedural-sky"(돔 정의에서) 또는 이미지 경로 + 노출 [EV]. 확산 사전 필터는 앱이 계산(SceneEnvironment.Prepare).</summary>
public sealed record EnvIbl(string Source, double ExposureEV);

/// <summary>
/// 조명(Fusion OGSLighting/RRTLighting Light): directional = 색 · 세기 · 방향(빛이 나아가는 방향, 지면 공간 [동, 북, 위]) · 그림자 색,
/// hemisphere = 하늘 쪽 · 땅 쪽 환경광 색.
/// </summary>
public sealed record EnvLightDef(string Type, string Label, (float R, float G, float B) Color, float Gain, string Space,
    (float E, float N, float U) Direction, (float R, float G, float B) ShadowColor, bool Enabled,
    (float R, float G, float B) Sky, (float R, float G, float B) GroundColor);

/// <summary>메시 형상(Fusion Ground.json · Dome.json 구조 {Vertices:{Positions,UVs},Indices}) + 우리 단위 · 위쪽 축.</summary>
public sealed record MeshShape(float[] Positions, float[] UVs, int[] Indices, string Units, string Up)
{
    /// <summary>수평 반경 [m](위쪽 축을 뺀 두 축의 최대 절댓값 × 단위).</summary>
    public double HalfExtentMeters
    {
        get
        {
            double k = Units switch { "mm" => 0.001, "cm" => 0.01, "in" => 0.0254, "ft" => 0.3048, _ => 1 };
            int up = Up == "y" ? 1 : 2;
            double m = 0;
            for (int i = 0; i < Positions.Length; i++) if (i % 3 != up) m = Math.Max(m, Math.Abs(Positions[i]));
            return m * k;
        }
    }
}

/// <summary>환경 파일 오류 — 파일 경로와 필드 이름을 담는다.</summary>
public sealed class EnvironmentFormatException(string path, string field, string message)
    : Exception($"{path}: '{field}' — {message}")
{
    public string FilePath { get; } = path;
    public string Field { get; } = field;
}

/// <summary>정거원통 실수 영상(선형 RGB, 위 행 = 천정, 열 = 방위 북 0 → 동).</summary>
public sealed record FloatImage(int Width, int Height, float[] Rgb);

/// <summary>
/// 환경 파일 읽기: environment.json(스키마 검증 · 빠진 필드 기본값 · 오류는 경로 + 필드), 메시 JSON, 사용자 영상 한 장(.hdr · .png → 돔 + IBL 로 감싼 환경).
/// </summary>
public static class EnvironmentLoader
{
    /// <summary>.png 등 영상 해독기(앱이 Avalonia 로 넣는다). .hdr(Radiance RGBE)은 내장.</summary>
    public static Func<string, FloatImage>? ImageDecoder { get; set; }

    public static readonly string[] ProceduralKinds = ["grass", "soil", "sand", "rock", "snow", "forestFloor", "sky"];

    /// <summary>환경 폴더 · environment.json · 영상 파일(.hdr · .png) 하나를 읽는다.</summary>
    public static EnvironmentDef Load(string path)
    {
        if (Directory.Exists(path)) path = Path.Combine(path, "environment.json");
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".hdr" or ".png" or ".jpg" or ".jpeg") return FromImage(path);
        if (!File.Exists(path)) throw new EnvironmentFormatException(path, "(파일)", "없음");
        JsonObject root;
        try { root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new EnvironmentFormatException(path, "(루트)", "JSON 객체가 아님"); }
        catch (JsonException ex) { throw new EnvironmentFormatException(path, "(JSON)", ex.Message); }
        return Parse(root, path);
    }

    /// <summary>폴더 아래 &lt;id&gt;/environment.json 을 모두 읽는다(잘못된 파일은 오류 목록으로, 나머지는 계속).</summary>
    public static (List<EnvironmentDef> Envs, List<string> Errors) LoadDirectory(string dir)
    {
        var envs = new List<EnvironmentDef>();
        var errors = new List<string>();
        if (!Directory.Exists(dir)) return (envs, errors);
        foreach (var sub in Directory.GetDirectories(dir).OrderBy(d => d, StringComparer.Ordinal))
        {
            if (!File.Exists(Path.Combine(sub, "environment.json"))) continue;
            try { envs.Add(Load(sub)); }
            catch (EnvironmentFormatException ex) { errors.Add(ex.Message); }
        }
        return (envs, errors);
    }

    public static EnvironmentDef Parse(JsonObject o, string path)
    {
        string Str(JsonObject? n, string key, string field, string? fb)
        {
            var v = n?[key];
            if (v is null) return fb ?? throw new EnvironmentFormatException(path, field, "필수 필드가 없음");
            try { return v.GetValue<string>(); } catch { throw new EnvironmentFormatException(path, field, "문자열이어야 함"); }
        }
        double Num(JsonObject? n, string key, string field, double fb)
        {
            var v = n?[key];
            if (v is null) return fb;
            try { return v.GetValue<double>(); } catch { throw new EnvironmentFormatException(path, field, "숫자여야 함"); }
        }
        bool Bool(JsonObject? n, string key, string field, bool fb)
        {
            var v = n?[key];
            if (v is null) return fb;
            try { return v.GetValue<bool>(); } catch { throw new EnvironmentFormatException(path, field, "true/false 여야 함"); }
        }
        (float, float, float) VecOf(JsonNode? v, string field, (float, float, float) fb)
        {
            if (v is null) return fb;
            if (v is not JsonArray { Count: 3 } a) throw new EnvironmentFormatException(path, field, "숫자 3개 배열이어야 함");
            try { return (a[0]!.GetValue<float>(), a[1]!.GetValue<float>(), a[2]!.GetValue<float>()); }
            catch { throw new EnvironmentFormatException(path, field, "숫자 3개 배열이어야 함"); }
        }
        (float, float, float) Vec(JsonObject? n, string key, string field, (float, float, float) fb) => VecOf(n?[key], field, fb);
        JsonObject? Obj(JsonObject? n, string key, string field) => n?[key] switch
        {
            null => null,
            JsonObject j => j,
            _ => throw new EnvironmentFormatException(path, field, "객체여야 함"),
        };
        JsonArray Arr(JsonObject? n, string key, string field) => n?[key] switch
        {
            null => [],
            JsonArray j => j,
            _ => throw new EnvironmentFormatException(path, field, "배열이어야 함"),
        };

        var schema = Str(o, "schema", "schema", null);
        if (schema != EnvironmentDef.SchemaName) throw new EnvironmentFormatException(path, "schema", $"'{EnvironmentDef.SchemaName}' 이어야 함(받은 값 '{schema}')");
        var id = Str(o, "id", "id", null);
        if (id.Length == 0 || id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            throw new EnvironmentFormatException(path, "id", "영문 · 숫자 · - · _ 만");
        var dn = Obj(o, "displayName", "displayName");
        var style = Str(o, "style", "style", "scene");
        if (style is not ("scene" or "analysis")) throw new EnvironmentFormatException(path, "style", "'scene' 또는 'analysis'");
        var sc = Obj(o, "scaling", "scaling");
        string dir = Path.GetDirectoryName(Path.GetFullPath(path))!;

        var features = new List<EnvFeature>();
        var fa = Arr(o, "features", "features");
        for (int i = 0; i < fa.Count; i++)
        {
            string f = $"features[{i}]";
            if (fa[i] is not JsonObject fo) throw new EnvironmentFormatException(path, f, "객체여야 함");
            var kind = Str(fo, "kind", f + ".kind", null);
            if (kind is not ("ground" or "dome")) throw new EnvironmentFormatException(path, f + ".kind", "'ground' 또는 'dome'");
            var shape = Str(fo, "shape", f + ".shape", kind == "ground" ? "plane" : "hemisphere");
            MeshShape? mesh = null;
            if (shape is not ("plane" or "hemisphere"))
            {
                try { mesh = LoadMesh(Path.Combine(dir, shape)); }
                catch (EnvironmentFormatException ex) { throw new EnvironmentFormatException(path, f + ".shape", ex.Message); }
            }
            var to = Obj(fo, "texture", f + ".texture") ?? throw new EnvironmentFormatException(path, f + ".texture", "필수 필드가 없음");
            EnvTexture tex;
            if (Obj(to, "procedural", f + ".texture.procedural") is { } po)
            {
                string pf = f + ".texture.procedural";
                var pk = Str(po, "kind", pf + ".kind", null);
                if (!ProceduralKinds.Contains(pk)) throw new EnvironmentFormatException(path, pf + ".kind", $"{string.Join(" · ", ProceduralKinds)} 중 하나");
                if ((pk == "sky") != (kind == "dome")) throw new EnvironmentFormatException(path, pf + ".kind", kind == "dome" ? "돔은 'sky'" : "지면은 'sky' 가 아닌 무늬");
                var cols = Arr(po, "colors", pf + ".colors");
                for (int k = 0; k < cols.Count; k++) VecOf(cols[k], $"{pf}.colors[{k}]", default);
                foreach (var key in new[] { "zenith", "horizon", "haze" }) Vec(po, key, $"{pf}.{key}", default);
                Bool(po, "trees", pf + ".trees", false);
                tex = new EnvTexture(pk, (uint)Num(po, "seed", pf + ".seed", 1), (JsonObject)po.DeepClone(), null);
            }
            else
            {
                var img = Path.Combine(dir, Str(to, "image", f + ".texture.image", null));
                if (!File.Exists(img)) throw new EnvironmentFormatException(path, f + ".texture.image", $"영상 파일이 없음: {img}");
                tex = new EnvTexture(null, 0, [], img);
            }
            features.Add(new EnvFeature(kind, shape, mesh, tex, Bool(fo, "isGroundPlane", f + ".isGroundPlane", kind == "ground"), Bool(fo, "isSurrounding", f + ".isSurrounding", true)));
        }

        var ibl = Obj(o, "ibl", "ibl");
        var iblSrc = Str(ibl, "source", "ibl.source", "procedural-sky");
        if (iblSrc != "procedural-sky")
        {
            iblSrc = Path.Combine(dir, iblSrc);
            if (!File.Exists(iblSrc)) throw new EnvironmentFormatException(path, "ibl.source", $"영상 파일이 없음: {iblSrc}");
        }
        var lights = new List<EnvLightDef>();
        var la = Arr(o, "lights", "lights");
        for (int i = 0; i < la.Count; i++)
        {
            string f = $"lights[{i}]";
            if (la[i] is not JsonObject lo) throw new EnvironmentFormatException(path, f, "객체여야 함");
            var type = Str(lo, "type", f + ".type", null);
            if (type is not ("directional" or "hemisphere")) throw new EnvironmentFormatException(path, f + ".type", "'directional' 또는 'hemisphere'");
            var space = Str(lo, "space", f + ".space", "ground");
            if (space is not ("ground" or "view")) throw new EnvironmentFormatException(path, f + ".space", "'ground' 또는 'view'");
            var d = Vec(lo, "direction", f + ".direction", (0.1f, 0.55f, -1f));
            if (type == "directional" && d.Item3 >= 0) throw new EnvironmentFormatException(path, f + ".direction", "빛은 아래로(위 성분 < 0)");
            lights.Add(new EnvLightDef(type, Str(lo, "label", f + ".label", ""), Vec(lo, "color", f + ".color", (1, 1, 1)), (float)Num(lo, "gain", f + ".gain", 1),
                space, d, Vec(lo, "shadowColor", f + ".shadowColor", (-0.3f, -0.3f, -0.3f)), Bool(lo, "enabled", f + ".enabled", true),
                Vec(lo, "sky", f + ".sky", (0.4f, 0.45f, 0.55f)), Vec(lo, "ground", f + ".ground", (0.2f, 0.2f, 0.18f))));
        }
        var amb = Obj(o, "ambient", "ambient");
        var bg = Obj(o, "background", "background");
        var soilArr = Arr(o, "soilMatch", "soilMatch");
        var soil = new List<string>();
        for (int i = 0; i < soilArr.Count; i++)
        {
            try { soil.Add(soilArr[i]!.GetValue<string>().Trim().ToLowerInvariant()); }
            catch { throw new EnvironmentFormatException(path, $"soilMatch[{i}]", "문자열이어야 함"); }
        }
        return new EnvironmentDef
        {
            Id = id, NameKo = Str(dn, "ko", "displayName.ko", ""), NameEn = Str(dn, "en", "displayName.en", ""), Style = style,
            CenterAngleDeg = Num(o, "centerAngleDeg", "centerAngleDeg", 0),
            Scaling = new((int)Num(sc, "fitTo", "scaling.fitTo", 2), Bool(sc, "autoScale", "scaling.autoScale", true), Math.Max(0.1, Num(sc, "autoScaleFactor", "scaling.autoScaleFactor", 10))),
            Features = features, Ibl = new(iblSrc, Num(ibl, "exposureEV", "ibl.exposureEV", 0)), Lights = lights,
            AmbientColor = Vec(amb, "color", "ambient.color", (0.25f, 0.25f, 0.25f)), AmbientGain = (float)Num(amb, "gain", "ambient.gain", 0),
            Background = bg is null ? null : Vec(bg, "rgb", "background.rgb", (0, 0, 0)),
            SoilMatch = soil, SourcePath = Path.GetFullPath(path),
        };
    }

    /// <summary>메시 JSON: {"units":"m","up":"z","Vertices":{"Positions":[…],"UVs":[…]},"Indices":[…]} (Fusion 구조 + 단위 · 축).</summary>
    public static MeshShape LoadMesh(string path)
    {
        if (!File.Exists(path)) throw new EnvironmentFormatException(path, "(파일)", "메시 파일이 없음");
        JsonObject o;
        try { o = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new EnvironmentFormatException(path, "(루트)", "JSON 객체가 아님"); }
        catch (JsonException ex) { throw new EnvironmentFormatException(path, "(JSON)", ex.Message); }
        float[] F(JsonNode? n, string field)
        {
            if (n is not JsonArray a) throw new EnvironmentFormatException(path, field, "숫자 배열이어야 함");
            try { return a.Select(x => x!.GetValue<float>()).ToArray(); } catch { throw new EnvironmentFormatException(path, field, "숫자 배열이어야 함"); }
        }
        var v = o["Vertices"] as JsonObject ?? throw new EnvironmentFormatException(path, "Vertices", "필수 필드가 없음");
        var pos = F(v["Positions"], "Vertices.Positions");
        var uv = v["UVs"] is null ? [] : F(v["UVs"], "Vertices.UVs");
        var idx = F(o["Indices"], "Indices").Select(x => (int)x).ToArray();
        int n = pos.Length / 3;
        if (pos.Length % 3 != 0 || n < 3) throw new EnvironmentFormatException(path, "Vertices.Positions", "x,y,z 묶음 3개 이상");
        if (uv.Length != 0 && uv.Length != 2 * n) throw new EnvironmentFormatException(path, "Vertices.UVs", "정점마다 u,v 2개");
        if (idx.Length % 3 != 0 || idx.Any(i => i < 0 || i >= n)) throw new EnvironmentFormatException(path, "Indices", "삼각형(3 배수) · 정점 범위 안");
        string units, up;
        try { units = o["units"]?.GetValue<string>() ?? "m"; up = o["up"]?.GetValue<string>() ?? "z"; }
        catch { throw new EnvironmentFormatException(path, "units/up", "문자열이어야 함"); }
        if (units is not ("m" or "cm" or "mm" or "in" or "ft")) throw new EnvironmentFormatException(path, "units", "m · cm · mm · in · ft");
        if (up is not ("y" or "z")) throw new EnvironmentFormatException(path, "up", "'y' 또는 'z'");
        return new MeshShape(pos, uv, idx, units, up);
    }

    /// <summary>Radiance .hdr(32-bit_rle_rgbe, 새 RLE · 비압축, "-Y H +X W") → 선형 RGB.</summary>
    public static FloatImage ReadHdr(string path)
    {
        using var s = new BufferedStream(File.OpenRead(path));
        string Line()
        {
            var sb = new StringBuilder();
            int c;
            while ((c = s.ReadByte()) >= 0 && c != '\n') sb.Append((char)c);
            if (c < 0 && sb.Length == 0) throw new EnvironmentFormatException(path, "(헤더)", "파일이 끝남");
            return sb.ToString();
        }
        if (!Line().StartsWith("#?")) throw new EnvironmentFormatException(path, "(헤더)", "Radiance HDR 이 아님");
        string l;
        while ((l = Line()).Length > 0)
            if (l.StartsWith("FORMAT=") && l != "FORMAT=32-bit_rle_rgbe") throw new EnvironmentFormatException(path, "FORMAT", "32-bit_rle_rgbe 만");
        var dims = Line().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (dims.Length != 4 || dims[0] != "-Y" || dims[2] != "+X" || !int.TryParse(dims[1], out int h) || !int.TryParse(dims[3], out int w) || w <= 0 || h <= 0)
            throw new EnvironmentFormatException(path, "(크기 줄)", "'-Y H +X W' 만");
        var rgb = new float[w * h * 3];
        var scan = new byte[w * 4];
        int Rb() { int c = s.ReadByte(); return c >= 0 ? c : throw new EnvironmentFormatException(path, "(픽셀)", "파일이 끝남"); }
        for (int y = 0; y < h; y++)
        {
            int a = Rb(), b = Rb(), c = Rb(), d = Rb();
            if (w >= 8 && w < 32768 && a == 2 && b == 2 && ((c << 8) | d) == w)
            {
                for (int ch = 0; ch < 4; ch++)
                    for (int x = 0; x < w;)
                    {
                        int n = Rb();
                        if (n > 128) { n -= 128; int v = Rb(); for (int k = 0; k < n && x < w; k++) scan[4 * x++ + ch] = (byte)v; }
                        else for (int k = 0; k < n && x < w; k++) scan[4 * x++ + ch] = (byte)Rb();
                    }
            }
            else
            {
                scan[0] = (byte)a; scan[1] = (byte)b; scan[2] = (byte)c; scan[3] = (byte)d;
                for (int x = 1; x < w; x++) for (int ch = 0; ch < 4; ch++) scan[4 * x + ch] = (byte)Rb();
            }
            for (int x = 0; x < w; x++)
            {
                int e = scan[4 * x + 3];
                float f = e == 0 ? 0 : MathF.ScaleB(1f, e - 136);
                int i = 3 * (y * w + x);
                rgb[i] = scan[4 * x] * f; rgb[i + 1] = scan[4 * x + 1] * f; rgb[i + 2] = scan[4 * x + 2] * f;
            }
        }
        return new FloatImage(w, h, rgb);
    }

    /// <summary>Radiance .hdr 쓰기(비압축) — 시험용.</summary>
    public static void WriteHdr(string path, FloatImage img)
    {
        using var s = File.Create(path);
        s.Write(Encoding.ASCII.GetBytes($"#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y {img.Height} +X {img.Width}\n"));
        for (int i = 0; i < img.Width * img.Height; i++)
        {
            float r = img.Rgb[3 * i], g = img.Rgb[3 * i + 1], b = img.Rgb[3 * i + 2], m = Math.Max(r, Math.Max(g, b));
            if (m < 1e-30f) { s.Write([0, 0, 0, 0]); continue; }
            int e = (int)MathF.Floor(MathF.Log2(m)) + 1;
            float k = MathF.ScaleB(256f, -e);
            s.Write([(byte)Math.Min(255, r * k), (byte)Math.Min(255, g * k), (byte)Math.Min(255, b * k), (byte)(e + 128)]);
        }
    }

    public static FloatImage ReadImage(string path)
        => Path.GetExtension(path).Equals(".hdr", StringComparison.OrdinalIgnoreCase) ? ReadHdr(path)
            : ImageDecoder?.Invoke(path) ?? throw new EnvironmentFormatException(path, "(영상)", "이 형식을 읽을 해독기가 없음(.hdr 은 항상 가능)");

    /// <summary>
    /// 사용자 정거원통 영상 한 장 → 환경(Fusion '사용자 설정 환경 부착'): 돔 · IBL = 그 영상(위 · 아래 반구 평균 = 하늘 · 땅 환경광),
    /// 태양 = 위 반구에서 가장 밝은 방향, 지면 = 아래 반구 평균 색의 흙 무늬.
    /// </summary>
    public static EnvironmentDef FromImage(string path)
    {
        if (!File.Exists(path)) throw new EnvironmentFormatException(path, "(파일)", "없음");
        var full = Path.GetFullPath(path);
        var a = AnalyzeImage(ReadImage(full));
        var name = Path.GetFileNameWithoutExtension(full);
        var id = "user-" + new string(name.Select(c => char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray());
        static JsonArray J((float R, float G, float B) c, float k) => [c.R * k, c.G * k, c.B * k];
        var g = a.Ground;
        return new EnvironmentDef
        {
            Id = id, NameKo = name + " (사용자 영상)", NameEn = name, SourcePath = full,
            Features =
            [
                new("ground", "plane", null, new EnvTexture("soil", 1, new JsonObject { ["kind"] = "soil", ["colors"] = new JsonArray(J(g, 0.8f), J(g, 1f), J(g, 0.6f)) }, null), true, true),
                new("dome", "hemisphere", null, new EnvTexture(null, 0, [], full), false, true),
            ],
            Ibl = new(full, 0),
            Lights =
            [
                new("directional", "sun", a.SunColor, 1, "ground", (-a.SunDir.E, -a.SunDir.N, -a.SunDir.U), (-0.3f, -0.3f, -0.3f), true, default, default),
                new("hemisphere", "ibl", (1, 1, 1), 1, "ground", default, default, true, a.Sky, a.Ground),
            ],
        };
    }

    /// <summary>
    /// 정거원통 영상의 확산 사전 필터(근사): 위 · 아래 반구 cos 가중 평균 색, 위 반구(고도 &gt; 3°)에서 가장 밝은 화소 방향(지면 공간 [동, 북, 위], 태양 쪽)과
    /// 그 색(최대 성분 1 로 맞춤).
    /// </summary>
    public static ((float R, float G, float B) Sky, (float R, float G, float B) Ground, (float E, float N, float U) SunDir, (float R, float G, float B) SunColor) AnalyzeImage(FloatImage img)
    {
        double[] up = new double[4], dn = new double[4];
        double best = -1; int bx = 0, by = 0;
        for (int y = 0; y < img.Height; y++)
        {
            double el = Math.PI / 2 - (y + 0.5) / img.Height * Math.PI, wgt = Math.Cos(el) * Math.Abs(Math.Sin(el));
            var acc = el >= 0 ? up : dn;
            for (int x = 0; x < img.Width; x++)
            {
                int i = 3 * (y * img.Width + x);
                float r = img.Rgb[i], g = img.Rgb[i + 1], b = img.Rgb[i + 2];
                acc[0] += r * wgt; acc[1] += g * wgt; acc[2] += b * wgt; acc[3] += wgt;
                double lum = 0.3 * r + 0.59 * g + 0.11 * b;
                if (el > 0.05 && lum > best) { best = lum; bx = x; by = y; }
            }
        }
        static (float, float, float) Avg(double[] a) => a[3] > 0 ? ((float)(a[0] / a[3]), (float)(a[1] / a[3]), (float)(a[2] / a[3])) : (0.3f, 0.3f, 0.3f);
        double az = (bx + 0.5) / img.Width * 2 * Math.PI, e = Math.PI / 2 - (by + 0.5) / img.Height * Math.PI;
        e = Math.Max(e, 0.1);
        int bi = 3 * (by * img.Width + bx);
        float m = Math.Max(1e-6f, Math.Max(img.Rgb[bi], Math.Max(img.Rgb[bi + 1], img.Rgb[bi + 2])));
        return (Avg(up), Avg(dn), ((float)(Math.Sin(az) * Math.Cos(e)), (float)(Math.Cos(az) * Math.Cos(e)), (float)Math.Sin(e)),
            (img.Rgb[bi] / m, img.Rgb[bi + 1] / m, img.Rgb[bi + 2] / m));
    }
}

/// <summary>환경 목록(내장 + 사용자) · 토양 자동 선택.</summary>
public sealed class EnvironmentRegistry
{
    public const string Auto = "auto";
    private readonly List<EnvironmentDef> _envs = [EnvironmentDef.Analysis];
    public IReadOnlyList<EnvironmentDef> All => _envs;
    public List<string> Errors { get; } = new();

    /// <summary>내장 폴더(config/environments) 읽기 — 같은 id 는 교체(파일의 analysis 가 코드 기본값을 대신함).</summary>
    public static EnvironmentRegistry FromDirectory(string dir)
    {
        var r = new EnvironmentRegistry();
        var (envs, errors) = EnvironmentLoader.LoadDirectory(dir);
        foreach (var e in envs) r.Add(e);
        r.Errors.AddRange(errors);
        return r;
    }

    public void Add(EnvironmentDef e)
    {
        int i = _envs.FindIndex(x => x.Id == e.Id);
        if (i >= 0) _envs[i] = e; else _envs.Add(e);
    }

    public EnvironmentDef? Find(string? id) => _envs.FirstOrDefault(e => e.Id == id);

    /// <summary>토양 이름 → soilMatch 에 그 이름이 있는 첫 환경, 없으면 "*" 환경, 그것도 없으면 분석.</summary>
    public EnvironmentDef ForSoil(string? soil)
    {
        var s = soil?.Trim().ToLowerInvariant() ?? "";
        return _envs.FirstOrDefault(e => e.SoilMatch.Contains(s)) ?? _envs.FirstOrDefault(e => e.SoilMatch.Contains("*")) ?? EnvironmentDef.Analysis;
    }

    /// <summary>설정 id → 그릴 환경(auto = 토양, 없는 id = 분석).</summary>
    public EnvironmentDef Resolve(string? id, string? soil) => id == Auto ? ForSoil(soil) : Find(id) ?? EnvironmentDef.Analysis;
}

/// <summary>
/// 렌더러가 GPU 로 올리는 환경 자료(정의에서 환경마다 한 번 계산 · 캐시): 지면 타일 · 돔 텍스처, 태양 · 반구광 · 안개 색, 노출.
/// </summary>
public sealed record EnvironmentAssets(
    byte[] GroundPx, int GroundSize, byte[] SkyPx, int SkyW, int SkyH, double SkyMinEl, bool Trees,
    (float R, float G, float B) TreeColor, (float R, float G, float B) Haze, (float R, float G, float B) Horizon,
    (float X, float Y, float Z) SunDirGl, (float R, float G, float B) Sun, (float R, float G, float B) SkyAmbient, (float R, float G, float B) GroundAmbient,
    float Exposure, double TileFactor, float GroundLuma);

/// <summary>절차적 텍스처(외부 자산 없이 코드로, 결정적 · 지면은 타일링)와 환경 → GPU 자료 변환. Avalonia 없음(시험 링크).</summary>
public static class SceneEnvironment
{
    public const int GroundSize = 256;
    /// <summary>절차적 하늘 [px] — 가로 = 방위 0–360°(북 → 동), 세로 = 고도 SkyMinElevation–90°.</summary>
    public const int SkyWidth = 2048, SkyHeight = 256;
    public const double SkyMinElevation = -10;
    /// <summary>하늘 텍스처 알파 = 그 방위의 나무 꼭대기 고도 / TreeMaxElevation(숲 돔만) — 셰이더가 화소 고도와 비교해 매끈한 실루엣.</summary>
    public const double TreeMaxElevation = 10;

    /// <summary>
    /// 환경 정의 → GPU 자료. 돔이 영상이면 정거원통 −90–90°(IBL 노출 적용 후 0–1 로 자름) + 영상의 반구 평균으로 지평선 · 연무 색,
    /// 절차적이면 −10–90°. 태양 = 첫 directional(빛 방향의 반대), 환경광 = hemisphere + ambient.
    /// </summary>
    public static EnvironmentAssets Prepare(EnvironmentDef d)
    {
        var ground = GroundTexture(d.Ground?.Texture ?? new EnvTexture("grass", 1, [], null));
        var dome = d.Dome?.Texture ?? new EnvTexture("sky", 1, [], null);
        var p = dome.Params;
        var hor = C(p, "horizon", (0.72f, 0.80f, 0.88f));
        var haze = C(p, "haze", (0.62f, 0.66f, 0.62f));
        bool trees = Flag(p, "trees");
        float exposure = (float)Math.Pow(2, d.Ibl.ExposureEV);
        byte[] sky; int sw, sh; double minEl;
        if (dome.ImagePath is { } ip)
        {
            var img = EnvironmentLoader.ReadImage(ip);
            (sw, sh, minEl) = (img.Width, img.Height, -90);
            sky = new byte[sw * sh * 4];
            // 영상 행 0 = 천정 → GL 텍스처 첫 행 = 고도 −90°
            for (int y = 0; y < sh; y++)
                for (int x = 0; x < sw; x++)
                {
                    int si = 3 * ((sh - 1 - y) * sw + x), di = 4 * (y * sw + x);
                    for (int c = 0; c < 3; c++) sky[di + c] = (byte)Math.Clamp((int)(img.Rgb[si + c] * exposure * 255 + 0.5f), 0, 255);
                }
            var a = EnvironmentLoader.AnalyzeImage(img);
            hor = Scale(a.Sky, exposure); haze = Scale(a.Ground, exposure);
            exposure = 1;   // 돔 바이트에 이미 반영
        }
        else
        {
            sky = SkyTexture(dome);
            (sw, sh, minEl) = (SkyWidth, SkyHeight, SkyMinElevation);
        }
        var sun = d.Sun;
        var dir = sun?.Direction ?? (0.1f, 0.55f, -1f);
        double n = Math.Sqrt(dir.E * dir.E + dir.N * dir.N + dir.U * dir.U);
        // 태양 쪽 = −(빛 방향) · 지면 공간 [동, 북, 위] → GL (동, 위, −북)
        var sunGl = ((float)(-dir.E / n), (float)(-dir.U / n), (float)(dir.N / n));
        var sc = sun is null ? (0f, 0f, 0f) : Scale(sun.Color, sun.Gain);
        var hemi = d.Hemisphere;
        float hg = hemi?.Gain ?? 0;
        var amb = Scale(d.AmbientColor, d.AmbientGain);
        (float, float, float) Add((float R, float G, float B) x, (float R, float G, float B) y) => (x.R * hg + y.R, x.G * hg + y.G, x.B * hg + y.B);
        return new EnvironmentAssets(ground, GroundSize, sky, sw, sh, minEl, trees, TreeColor(hor), haze, hor, sunGl, sc,
            Add(hemi?.Sky ?? default, amb), Add(hemi?.GroundColor ?? default, amb), exposure, 2.5 / d.Scaling.AutoScaleFactor, Luma(ground));
    }

    /// <summary>RGBA8 평균 밝기(0–1) — 격자 색(밝은 지면 = 어두운 선)을 고른다.</summary>
    private static float Luma(byte[] px)
    {
        double s = 0;
        for (int i = 0; i < px.Length; i += 4) s += 0.3 * px[i] + 0.59 * px[i + 1] + 0.11 * px[i + 2];
        return (float)(s / (px.Length / 4) / 255);
    }

    private static (float, float, float) Scale((float R, float G, float B) c, float k) => (c.R * k, c.G * k, c.B * k);

    private static bool Flag(JsonObject p, string key) => p[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    private static (float, float, float) C(JsonObject p, string key, (float, float, float) fb)
        => p[key] is JsonArray { Count: 3 } a ? (a[0]!.GetValue<float>(), a[1]!.GetValue<float>(), a[2]!.GetValue<float>()) : fb;

    /// <summary>숲 실루엣 색(먼 숲 = 지평선 색에 살짝 흐림).</summary>
    public static (float R, float G, float B) TreeColor((float R, float G, float B) horizon) => Mix((0.07f, 0.12f, 0.07f), horizon, 0.22f);

    // ---------------- 절차적 노이즈(주기 격자 → 타일링) ----------------

    private static uint Hash(int x, int y, uint seed)
    {
        uint h = unchecked((uint)x * 374761393u + (uint)y * 668265263u + seed * 2246822519u);
        h = unchecked((h ^ (h >> 13)) * 1274126177u);
        return h ^ (h >> 16);
    }

    private static float Rand(int x, int y, uint seed) => (Hash(x, y, seed) & 0xFFFFFF) / 16777215f;

    /// <summary>주기 period 격자의 값 노이즈(0–1). x · y 는 격자 단위.</summary>
    private static float Value(float x, float y, int period, uint seed)
    {
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
        float fx = x - x0, fy = y - y0;
        float u = fx * fx * (3 - 2 * fx), v = fy * fy * (3 - 2 * fy);
        int W(int i) => ((i % period) + period) % period;
        float a = Rand(W(x0), W(y0), seed), b = Rand(W(x0 + 1), W(y0), seed);
        float c = Rand(W(x0), W(y0 + 1), seed), d = Rand(W(x0 + 1), W(y0 + 1), seed);
        return a + (b - a) * u + (c - a) * v + (a - b - c + d) * u * v;
    }

    /// <summary>타일링 fBm(0–1): u · v ∈ [0,1) 한 타일, 옥타브마다 주기 2 배.</summary>
    private static float Fbm(float u, float v, int period, int octaves, uint seed)
    {
        float sum = 0, amp = 0.5f, norm = 0;
        for (int k = 0; k < octaves; k++, amp *= 0.5f, period *= 2)
        {
            sum += amp * Value(u * period, v * period, period, seed + (uint)k * 101u);
            norm += amp;
        }
        return sum / norm;
    }

    private static (float, float, float) Mix((float R, float G, float B) a, (float R, float G, float B) b, float t)
        => (a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t);

    private static float Smooth(float e0, float e1, float x)
    {
        float t = Math.Clamp((x - e0) / (e1 - e0), 0, 1);
        return t * t * (3 - 2 * t);
    }

    private static void Put(byte[] px, int i, (float R, float G, float B) c, float k = 1)
    {
        px[4 * i] = (byte)Math.Clamp((int)MathF.Round(c.R * k * 255), 0, 255);
        px[4 * i + 1] = (byte)Math.Clamp((int)MathF.Round(c.G * k * 255), 0, 255);
        px[4 * i + 2] = (byte)Math.Clamp((int)MathF.Round(c.B * k * 255), 0, 255);
        px[4 * i + 3] = 255;
    }

    /// <summary>
    /// 지면 타일 텍스처(RGBA8, size × size, 가장자리 이어짐). 종류: grass = 흙 자국 · soil = 마른 갈라짐 · sand = 바람 물결 · rock = 균열 ·
    /// snow = 푸른 그늘 · forestFloor = 낙엽. 매개변수 colors = [바탕 어두운 색, 밝은 색, 무늬 색], seed = 씨앗.
    /// </summary>
    public static byte[] GroundTexture(EnvTexture t, int size = GroundSize)
    {
        var kind = t.Procedural ?? "grass";
        var cols = t.Params["colors"] as JsonArray;
        (float, float, float) Col(int i, (float, float, float) fb) => cols is { } a && a.Count > i && a[i] is JsonArray { Count: 3 } c
            ? (c[0]!.GetValue<float>(), c[1]!.GetValue<float>(), c[2]!.GetValue<float>()) : fb;
        var A = Col(0, (0.17f, 0.30f, 0.10f)); var B = Col(1, (0.33f, 0.46f, 0.16f)); var Cc = Col(2, (0.43f, 0.36f, 0.24f));
        uint s0 = unchecked(t.Seed * 7919u);
        var px = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float u = (float)x / size, v = (float)y / size;
                float n = Fbm(u, v, 4, 5, s0 + 1), fine = Fbm(u, v, 32, 2, s0 + 2);
                var c = Mix(A, B, n);
                float k = 0.88f + 0.24f * fine;
                switch (kind)
                {
                    case "grass" or "forestFloor":
                        // 흙 자국(풀밭) · 낙엽(숲) 얼룩
                        bool forest = kind == "forestFloor";
                        float patch = Fbm(u, v, 5, 4, s0 + 3);
                        c = Mix(c, Cc, 0.85f * Smooth(forest ? 0.56f : 0.66f, forest ? 0.72f : 0.80f, patch));
                        break;
                    case "soil" or "rock":
                        // 갈라짐 · 균열: 노이즈 등고선 0.5 근처를 어둡게
                        float r = MathF.Abs(Fbm(u, v, kind == "soil" ? 6 : 4, 4, s0 + 7) - 0.5f);
                        c = Mix(c, Cc, 1 - Smooth(0.006f, kind == "soil" ? 0.02f : 0.014f, r));
                        break;
                    case "sand":
                        // 바람 물결(정수 주기라 타일링 유지)
                        float ripple = 0.5f + 0.5f * MathF.Sin(2 * MathF.PI * (v * 24 + 1.5f * Fbm(u, v, 3, 3, s0 + 9)));
                        k *= 0.92f + 0.12f * ripple;
                        break;
                    case "snow":
                        c = Mix(Cc, c, 0.55f + 0.45f * n);
                        k = 0.96f + 0.06f * fine;
                        break;
                }
                Put(px, y * size + x, c, k);
            }
        return px;
    }

    /// <summary>
    /// 절차적 하늘(RGBA8, 정거원통: 열 = 방위 0–360°(북 → 동), 행 = 고도 SkyMinElevation → 90°). 매개변수 zenith · horizon · haze(지평선 아래) ·
    /// trees(숲 실루엣 — 알파에 나무 꼭대기 고도 / TreeMaxElevation, 그 밖 알파 0). 태양은 셰이더가 그린다.
    /// </summary>
    public static byte[] SkyTexture(EnvTexture t, int width = SkyWidth, int height = SkyHeight)
    {
        var p = t.Params;
        var zen = C(p, "zenith", (0.25f, 0.45f, 0.78f));
        var hor = C(p, "horizon", (0.72f, 0.80f, 0.88f));
        var haze = C(p, "haze", (0.62f, 0.66f, 0.62f));
        uint s0 = unchecked(t.Seed * 7919u);
        var px = new byte[width * height * 4];
        // 숲: 열마다 나무 꼭대기 고도 [°] — 완만한 언덕 + 침엽수 원뿔(방위 0.4° 간격, 높이 · 폭 무작위)
        var treeTop = new float[width];
        if (Flag(p, "trees"))
        {
            const float step = 0.4f;
            int count = (int)(360 / step);
            for (int i = 0; i < width; i++)
            {
                float a = (i + 0.5f) / width * 360;
                float hill = 0.4f + 1.0f * Fbm((float)i / width, 0, 6, 4, s0 + 11);
                float top = hill;
                int k0 = (int)MathF.Floor(a / step);
                for (int k = k0 - 2; k <= k0 + 2; k++)
                {
                    int kk = ((k % count) + count) % count;
                    float cx = (k + 0.2f + 0.6f * Rand(kk, 0, s0 + 21)) * step;
                    float th = 0.9f + 1.6f * Rand(kk, 1, s0 + 21), hw = 0.18f + 0.2f * Rand(kk, 2, s0 + 21);
                    top = MathF.Max(top, hill + th * (1 - MathF.Abs(a - cx) / hw));
                }
                treeTop[i] = top;
            }
        }
        for (int j = 0; j < height; j++)
        {
            double e = SkyMinElevation + (j + 0.5) / height * (90 - SkyMinElevation);
            (float, float, float) baseC = e >= 0
                ? Mix(hor, zen, (float)Math.Pow(Math.Sin(e * Math.PI / 180), 0.45))
                : Mix(hor, haze, (float)Math.Min(1, -e / 4));
            for (int i = 0; i < width; i++)
            {
                Put(px, j * width + i, baseC);
                px[4 * (j * width + i) + 3] = (byte)Math.Clamp((int)Math.Round(treeTop[i] / TreeMaxElevation * 255), 0, 255);
            }
        }
        return px;
    }
}

/// <summary>
/// 장면 설정(보기 → 장면 환경, Fusion 장면 설정 항목을 따른 이름) — 사용자 설정 파일(settings.json)의 <c>"scene"</c> 키에 저장 · 복원.
/// 기본 = 기본(분석) 환경.
/// </summary>
public sealed record SceneSettings
{
    /// <summary>환경 id(config/environments 또는 사용자 환경) · "auto" = 시나리오 토양.</summary>
    public string EnvironmentId { get; init; } = "analysis";
    /// <summary>밝기(노출 배율) 0.25–3.</summary>
    public double Brightness { get; init; } = 1;
    /// <summary>환경 회전 [°] 0–360(환경 centerAngleDeg 에 더함).</summary>
    public double RotationDeg { get; init; }
    /// <summary>"environment"(하늘) · "solid"(단색).</summary>
    public string BackgroundType { get; init; } = "environment";
    /// <summary>단색 배경 색 — null 이면 환경 background, 그것도 없으면 테마 배경.</summary>
    public (float R, float G, float B)? BackgroundColor { get; init; }
    public bool GroundDisplayed { get; init; } = true;
    /// <summary>지면 평탄화: 켬 = 지평선까지 이어진 지면, 끔 = 자동 축척 크기(화면 범위 × 배율)의 지면, 그 밖은 돔.</summary>
    public bool GroundFlattened { get; init; } = true;
    public bool GroundReflections { get; init; }
    /// <summary>지면 반사 거칠기 0(거울) – 1.</summary>
    public double GroundRoughness { get; init; } = 0.5;
    /// <summary>지면 높이 오프셋 [m](시나리오 지면 고도 기준, 위 +).</summary>
    public double GroundOffset { get; init; }
    /// <summary>카메라 노출 [EV].</summary>
    public double ExposureEV { get; init; }
    public bool Shadows { get; init; } = true;
    public bool Fog { get; init; } = true;
    /// <summary>불러온 사용자 환경(environment.json · 폴더 · .hdr/.png 경로) — 다음 실행에 다시 불러옴.</summary>
    public IReadOnlyList<string> UserEnvironments { get; init; } = [];

    public bool SkyBackground => BackgroundType != "solid";

    private const string KeyName = "scene";

    public JsonObject ToJson() => new()
    {
        ["environmentId"] = EnvironmentId,
        ["brightness"] = Brightness,
        ["rotationDeg"] = RotationDeg,
        ["backgroundType"] = BackgroundType,
        ["backgroundColor"] = BackgroundColor is { } c ? new JsonArray(c.R, c.G, c.B) : null,
        ["groundDisplayed"] = GroundDisplayed,
        ["groundFlattened"] = GroundFlattened,
        ["groundReflections"] = GroundReflections,
        ["groundRoughness"] = GroundRoughness,
        ["groundOffset"] = GroundOffset,
        ["exposureEV"] = ExposureEV,
        ["shadows"] = Shadows,
        ["fog"] = Fog,
        ["userEnvironments"] = new JsonArray(UserEnvironments.Select(p => (JsonNode?)p).ToArray()),
    };

    /// <summary>JSON → 설정(빠지거나 잘못된 값은 기본값, 범위는 잘라 맞춤).</summary>
    public static SceneSettings FromJson(JsonObject o)
    {
        var d = new SceneSettings();
        T Get<T>(string k, T fb) { try { return o[k] is { } n ? n.GetValue<T>() : fb; } catch { return fb; } }
        double rot = Get("rotationDeg", d.RotationDeg) % 360;
        (float, float, float)? bg = null;
        try { if (o["backgroundColor"] is JsonArray { Count: 3 } a) bg = (a[0]!.GetValue<float>(), a[1]!.GetValue<float>(), a[2]!.GetValue<float>()); } catch { }
        var users = new List<string>();
        if (o["userEnvironments"] is JsonArray ua) foreach (var n in ua) { try { users.Add(n!.GetValue<string>()); } catch { } }
        var id = Get("environmentId", d.EnvironmentId);
        return new SceneSettings
        {
            EnvironmentId = string.IsNullOrWhiteSpace(id) ? d.EnvironmentId : id,
            Brightness = Math.Clamp(Get("brightness", d.Brightness), 0.25, 3),
            RotationDeg = rot < 0 ? rot + 360 : rot,
            BackgroundType = Get("backgroundType", "environment") == "solid" ? "solid" : "environment",
            BackgroundColor = bg,
            GroundDisplayed = Get("groundDisplayed", d.GroundDisplayed),
            GroundFlattened = Get("groundFlattened", d.GroundFlattened),
            GroundReflections = Get("groundReflections", d.GroundReflections),
            GroundRoughness = Math.Clamp(Get("groundRoughness", d.GroundRoughness), 0, 1),
            GroundOffset = Math.Clamp(Get("groundOffset", d.GroundOffset), -1000, 1000),
            ExposureEV = Math.Clamp(Get("exposureEV", d.ExposureEV), -5, 5),
            Shadows = Get("shadows", d.Shadows),
            Fog = Get("fog", d.Fog),
            UserEnvironments = users,
        };
    }

    /// <summary>저장된 장면 설정(없음 · 손상 = null → 호출자가 기본값).</summary>
    public static SceneSettings? Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return (JsonNode.Parse(File.ReadAllText(path)) as JsonObject)?[KeyName] is JsonObject o ? FromJson(o) : null;
        }
        catch { return null; }
    }

    /// <summary>"scene" 키만 바꿔 기록(다른 키 보존, 임시 파일 → 교체). 실패해도 예외 없음.</summary>
    public bool Save(string path)
    {
        try
        {
            JsonObject root;
            try { root = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject() : new JsonObject(); }
            catch { root = new JsonObject(); }
            root[KeyName] = ToJson();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch { return false; }
    }

    /// <summary>값 비교(사용자 환경 목록은 내용으로).</summary>
    public bool SameAs(SceneSettings o) => this with { UserEnvironments = [] } == o with { UserEnvironments = [] } && UserEnvironments.SequenceEqual(o.UserEnvironments);
}
