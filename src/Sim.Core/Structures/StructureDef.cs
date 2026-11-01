using System.Text.Json.Nodes;
using Sim.Core.Geometry;
using Sim.Core.Scenario;

namespace Sim.Core.Structures;

/// <summary>
/// 물체 구조 정의(시나리오 body.structure, FR-STR-02 · FR-MAT-01):
/// <code>{ "material": "al6061-t6" | Fusion 재질명, "thickness": 0.002, "section": "auto|thinWall|solid",
///   "attachRadius": 0.01, "layup": [0,45,-45,90], "override": { "yield_MPa": … } }</code>
/// structure 가 없어도 Fusion massprops 의 바디 재질(질량 최대)이 라이브러리에 있으면 자동 매칭한다.
/// </summary>
public sealed class StructureDef
{
    public Material? Material { get; init; }
    /// <summary>요청한 재질 이름(미지원이면 Material = null).</summary>
    public string? RequestedMaterial { get; init; }
    /// <summary>scenario | fusion-props(Fusion 재료 물성 숫자) | fusion-auto(Fusion 재료 이름 → 별칭 표) | unsupported | none</summary>
    public string MaterialSource { get; init; } = "none";
    public double? Thickness { get; init; }
    /// <summary>thinWall | solid (auto 해석 후 값).</summary>
    public string Section { get; init; } = "solid";
    /// <summary>연결점(라이저·테더·힌지) 국부 하중 핫스팟 등가 반경 [m].</summary>
    public double AttachRadius { get; init; } = 0.01;
    public double[]? Layup { get; init; }
    public int Slices { get; init; } = 40;
    /// <summary>얇은 벽 패널 가장자리 조건(FX-09 판 굽힘): simple(4변 단순지지, 기본·중앙 최대) | clamped(4변 고정, 가장자리 최대).</summary>
    public string Edge { get; init; } = "simple";
    /// <summary>내압 [Pa, 게이지](폼팩 실린더 압력차 등). 바깥 압력과 반대로 작용.</summary>
    public double InternalPressure { get; init; }

    /// <summary>재질 근거(시나리오 structure.basis — 예: "이름 재질 CFRP (massprops 재계산)")·출처 등급 A–D(structure.grade).</summary>
    public string? MaterialBasis { get; init; }
    public string? MaterialGrade { get; init; }

    /// <param name="fusionProps">Fusion 내보내기의 재료 물성으로 만든 재질(MassPropsFile.DominantFusionMaterial) — 시나리오가 재질을
    /// 지정하지 않았으면 별칭 표(fusion-auto)보다 우선한다(materialSource = fusion-props, 표에 없는 재료도 지원).</param>
    public static StructureDef? Parse(JsonObject? o, string? fusionMaterial, Shape? shape, List<string> warnings, string bodyName, Material? fusionProps = null)
    {
        // structure.material = "none": 구조 재질 판정 제외를 명시(직물 낙하산 팩처럼 라이브러리 재질이 없는 물체) —
        // Fusion 바디 재질 자동 매칭(fusion-auto)도 막는다(CAD 원본이 전 바디 기본 강철이면 잘못 매칭됨).
        if (o is not null && J.StrOpt(o, "material") is "none") return null;
        if (o is null && string.IsNullOrWhiteSpace(fusionMaterial)) return null;
        // structure 미지정 + Fusion 재질이 라이브러리에 없고 Fusion 물성도 없으면 조용히 생략(자동 매칭은 있을 때만)
        if (o is null && fusionProps is null && MaterialLibrary.Find(fusionMaterial) is null) return null;
        string? req = o is not null ? J.StrOpt(o, "material") : null;
        string source = req is not null ? "scenario" : "none";
        if (req is null && !string.IsNullOrWhiteSpace(fusionMaterial)) { req = fusionMaterial; source = fusionProps is not null ? "fusion-props" : "fusion-auto"; }
        Material? mat = req is null ? null : source == "fusion-props" ? fusionProps : MaterialLibrary.Find(req);
        if (req is not null && mat is null)
        {
            warnings.Add($"{bodyName}: 미지원 재질 '{req}' — 재질 판정 없이 압력·근사 응력만 표시(재질 라이브러리에 추가하거나 structure.material 지정)");
            source = "unsupported";
        }
        if (mat is not null && o?["override"] is JsonObject ov) mat = MaterialLibrary.Parse(ov, mat);
        double? t = o is not null ? J.NumOpt(o, "thickness") : null;
        if (t is null && shape?.Kind == ShapeKind.ShellSegment) t = shape.Thickness;
        if (t is null && mat?.DefaultThickness is { } dt && o is not null) t = dt;
        if (t is { } tv && !(tv > 0)) throw new InvalidDataException($"{bodyName}.structure.thickness 는 양수여야 합니다.");
        var sec = o is not null ? J.Str(o, "section", "auto") : "auto";
        if (sec == "auto") sec = t is not null ? "thinWall" : "solid";
        if (sec is not ("thinWall" or "solid")) throw new InvalidDataException($"{bodyName}.structure.section: auto | thinWall | solid ('{sec}')");
        if (sec == "thinWall" && t is null) throw new InvalidDataException($"{bodyName}.structure: thinWall 단면에는 thickness 가 필요합니다.");
        var edge = o is not null ? J.Str(o, "edge", "simple") : "simple";
        if (edge is not ("simple" or "clamped")) throw new InvalidDataException($"{bodyName}.structure.edge: simple | clamped ('{edge}')");
        double pin = o is not null ? J.Num(o, "internalPressure_kPa", 0) * 1e3 : 0;
        if (!double.IsFinite(pin)) throw new InvalidDataException($"{bodyName}.structure.internalPressure_kPa 는 유한한 숫자여야 합니다.");
        return new StructureDef
        {
            Material = mat, RequestedMaterial = req, MaterialSource = mat is null ? (req is null ? "none" : "unsupported") : source,
            Thickness = t, Section = sec,
            AttachRadius = o is not null ? J.Num(o, "attachRadius", 0.01) : 0.01,
            Layup = o?["layup"] is JsonArray la ? J.Arr(la) : mat?.Layup,
            Slices = o is not null ? (int)Math.Clamp(J.Num(o, "slices", 40), 4, 400) : 40,
            Edge = edge, InternalPressure = pin,
            MaterialBasis = o is not null ? J.StrOpt(o, "basis") : null,
            MaterialGrade = o is not null ? J.StrOpt(o, "grade") : null,
        };
    }

    public JsonObject ToJson() => new()
    {
        ["material"] = Material?.Key,
        ["materialName"] = Material?.Name,
        ["requestedMaterial"] = RequestedMaterial,
        ["materialSource"] = MaterialSource,
        ["thickness_m"] = Thickness,
        ["section"] = Section,
        ["attachRadius_m"] = AttachRadius,
        ["edge"] = Edge,
        ["internalPressure_kPa"] = InternalPressure / 1e3,
        ["materialBasis"] = MaterialBasis,
        ["materialGrade"] = MaterialGrade,
    };
}
