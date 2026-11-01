using System.Text.Json.Nodes;
using Sim.Core.Numerics;
using Sim.Core.Scenario;

namespace Sim.Core.Geometry;

/// <summary>
/// Fusion 내보내기 스크립트(tools/fusion/MnS_ExportForSim)가 쓰는 질량특성 파일(mns.massprops/1).
/// 값은 SI(m, kg, kg·m²), Fusion 모델 좌표계, 관성은 CG 기준 텐서(비대각 = −곱관성).
/// </summary>
public sealed class MassPropsFile
{
    public string Name { get; init; } = "";
    public double Mass { get; init; }
    public Vec3 Cg { get; init; }
    public Mat3 Inertia { get; init; }
    public Vec3? BBoxMin { get; init; }
    public Vec3? BBoxMax { get; init; }
    public string? Stl { get; init; }
    public string StlUnit { get; init; } = "auto";
    public string Frame { get; init; } = "fusion";
    public string Source { get; init; } = "fusion";
    /// <summary>바디별 Fusion 재질 이름과 질량(parts[].material) — 재질 자동 매칭(FR-MAT-01)용.</summary>
    public IReadOnlyList<(string Material, double Mass)> Materials { get; init; } = [];

    /// <summary>질량이 가장 큰 재질 이름(없으면 null).</summary>
    public string? DominantMaterial =>
        Materials.Where(m => !string.IsNullOrWhiteSpace(m.Material)).GroupBy(m => m.Material)
            .OrderByDescending(g => g.Sum(x => x.Mass)).Select(g => g.Key).FirstOrDefault();

    /// <summary>바디별 Fusion 재료 물성(parts[].materialProperties, SI)·외관 색(parts[].appearance.baseColor → #rrggbb). 내보내기 1.1 이전 파일은 null.</summary>
    public IReadOnlyList<MassPropsPart> Parts { get; init; } = [];
    /// <summary>Fusion 사용자 파라미터(userParameters) — 표시용.</summary>
    public IReadOnlyList<CadParameter> UserParameters { get; init; } = [];
    /// <summary>Fusion 조인트(joints) — 표시용.</summary>
    public IReadOnlyList<CadJoint> Joints { get; init; } = [];
    /// <summary>재질 포함 USD 파일 이름(usd.file, massprops 와 같은 폴더) — 없으면 null.</summary>
    public string? Usd { get; init; }
    /// <summary>읽은 파일 전체 경로(Load 로 읽었을 때).</summary>
    public string? SourcePath { get; init; }

    /// <summary>
    /// 질량이 가장 큰 재질(DominantMaterial)의 Fusion 물성으로 만든 재질 — 별칭 표보다 우선(FR-MAT-01). 물성이 없거나 판정에 모자라면 null.
    /// </summary>
    public Structures.Material? DominantFusionMaterial =>
        DominantMaterial is { } name && Parts.FirstOrDefault(p => p.Material == name && p.MaterialProperties is not null) is { } part
            ? Structures.MaterialLibrary.FromFusion(name, part.MaterialProperties) : null;

    /// <summary>외관 색이 있는 바디 중 질량이 가장 큰 것의 기본색(#rrggbb) — 3D 물체 색 기본값. 없으면 null.</summary>
    public string? AppearanceColor =>
        Parts.Where(p => p.BaseColor is not null).OrderByDescending(p => p.Mass).Select(p => p.BaseColor).FirstOrDefault();

    public static MassPropsFile Load(string path)
    {
        JsonObject o;
        try { o = J.ParseObject(File.ReadAllText(path)); }
        catch (System.Text.Json.JsonException ex) { throw new InvalidDataException($"{path}: massprops JSON 을 읽을 수 없습니다 — {ex.Message}"); }
        var schema = J.Str(o, "schema", "");
        if (!schema.StartsWith("mns.massprops/", StringComparison.Ordinal))
            throw new InvalidDataException($"{path}: schema 가 mns.massprops/1 이 아닙니다.");
        var mats = new List<(string, double)>();
        var partList = new List<MassPropsPart>();
        if (o["parts"] is JsonArray parts)
            foreach (var p in parts.OfType<JsonObject>())
            {
                mats.Add((J.Str(p, "material", ""), J.Num(p, "mass", 0)));
                partList.Add(new MassPropsPart(J.Str(p, "body", ""), J.Str(p, "material", ""), J.Num(p, "mass", 0),
                    p["materialProperties"] as JsonObject, ColorHex((p["appearance"] as JsonObject)?["baseColor"])));
            }
        var prms = new List<CadParameter>();
        if (o["userParameters"] is JsonArray ups)
            foreach (var u in ups.OfType<JsonObject>())
                prms.Add(new CadParameter(J.Str(u, "name", ""), J.Str(u, "expression", ""), J.Str(u, "unit", ""),
                    J.NumOpt(u, "value"), J.NumOpt(u, "valueSI"), J.StrOpt(u, "siUnit"), J.StrOpt(u, "comment")));
        var joints = new List<CadJoint>();
        if (o["joints"] is JsonArray js)
            foreach (var j in js.OfType<JsonObject>())
                joints.Add(new CadJoint(J.Str(j, "name", ""), J.Str(j, "type", ""), J.Str(j, "occurrenceOne", ""), J.Str(j, "occurrenceTwo", ""),
                    J.V3Opt(j, "origin"), J.Str(j, "source", "joint"), J.Bool(j, "suppressed", false)));
        return new MassPropsFile
        {
            Materials = mats,
            Parts = partList,
            UserParameters = prms,
            Joints = joints,
            Usd = (o["usd"] as JsonObject) is { } usd ? J.StrOpt(usd, "file") : null,
            SourcePath = Path.GetFullPath(path),
            Name = J.Str(o, "name", Path.GetFileNameWithoutExtension(path)),
            Mass = J.NumReq(o, "mass", path),
            Cg = J.V3(o, "cg", Vec3.Zero),
            Inertia = J.Mat(o["inertia"] ?? throw new InvalidDataException($"{path}: inertia 없음")),
            BBoxMin = J.V3Opt(o, "bboxMin"),
            BBoxMax = J.V3Opt(o, "bboxMax"),
            Stl = J.StrOpt(o, "stl"),
            StlUnit = J.Str(o, "stlUnit", "auto"),
            Frame = J.Str(o, "frame", "fusion"),
            Source = J.Str(o, "source", "fusion"),
        };
    }

    public JsonObject ToJson() => new()
    {
        ["schema"] = "mns.massprops/1",
        ["name"] = Name,
        ["source"] = Source,
        ["frame"] = Frame,
        ["units"] = new JsonObject { ["length"] = "m", ["mass"] = "kg", ["inertia"] = "kg*m^2" },
        ["mass"] = Mass,
        ["cg"] = J.ToJson(Cg),
        ["inertia"] = J.ToJson(Inertia),
        ["bboxMin"] = BBoxMin is { } a ? J.ToJson(a) : null,
        ["bboxMax"] = BBoxMax is { } b ? J.ToJson(b) : null,
        ["stl"] = Stl,
        ["stlUnit"] = StlUnit,
    };

    /// <summary>메시 바운딩박스를 기준 바운딩박스(m)와 비교해 단위 배율 추정(m/cm/mm/in).</summary>
    public static double GuessUnitScale(TriMesh mesh, Vec3? refMin, Vec3? refMax, string fallbackUnit = "mm")
    {
        var (min, max) = mesh.Bounds();
        double diag = (max - min).Norm;
        if (refMin is { } a && refMax is { } b && diag > 0)
        {
            double refDiag = (b - a).Norm;
            double best = 1, bestErr = double.MaxValue;
            foreach (var s in new[] { 1, 0.01, 0.001, 0.0254 })
            {
                double err = Math.Abs(Math.Log(diag * s / refDiag));
                if (err < bestErr) { bestErr = err; best = s; }
            }
            return best;
        }
        return Frames.UnitScale(fallbackUnit);
    }

    /// <summary>[r, g, b] (0–1) → "#rrggbb". 형식이 맞지 않으면 null.</summary>
    public static string? ColorHex(JsonNode? rgb)
    {
        if (rgb is not JsonArray { Count: >= 3 } a) return null;
        var c = new int[3];
        for (int i = 0; i < 3; i++)
        {
            if (a[i] is not JsonValue v || !v.TryGetValue(out double d) || !double.IsFinite(d)) return null;
            c[i] = (int)Math.Round(Math.Clamp(d, 0, 1) * 255);
        }
        return $"#{c[0]:x2}{c[1]:x2}{c[2]:x2}";
    }
}

/// <summary>massprops parts[] 한 바디: 재료 이름 · 질량 · Fusion 재료 물성(SI, 없으면 null) · 외관 기본색(#rrggbb, 없으면 null).</summary>
public sealed record MassPropsPart(string Body, string Material, double Mass, JsonObject? MaterialProperties, string? BaseColor);

/// <summary>Fusion 사용자 파라미터. Value = Fusion 내부 단위(길이 cm · 각도 rad), ValueSI·SiUnit = 내보내기가 SI 로 바꾼 값(길이·각도·무차원).</summary>
public sealed record CadParameter(string Name, string Expression, string Unit, double? Value, double? ValueSI, string? SiUnit, string? Comment);

/// <summary>Fusion 조인트(joint · asBuiltJoint): 종류 · 두 occurrence 경로(루트 = "") · 원점(Fusion 모델 좌표, m).</summary>
public sealed record CadJoint(string Name, string Type, string OccurrenceOne, string OccurrenceTwo, Vec3? Origin, string Source, bool Suppressed);
