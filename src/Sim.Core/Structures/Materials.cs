using System.Text.Json.Nodes;
using Sim.Core.Scenario;

namespace Sim.Core.Structures;

/// <summary>Johnson-Cook 강도식 계수(σ = (A + Bεⁿ)(1 + C ln ε̇*)(1 − T*ᵐ)). SI(Pa).</summary>
public sealed record JohnsonCook(double A, double B, double N, double C, double M, double EpsDot0);

/// <summary>일방향 ply 물성(Tsai-Wu, FR-MAT-06). SI(Pa, m).</summary>
public sealed record PlyProps(double E1, double E2, double G12, double Nu12, double Xt, double Xc, double Yt, double Yc, double S, double Thickness);

/// <summary>재질(FR-MAT-01). 내부 단위 SI: Pa, kg/m³.</summary>
public sealed class Material
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public string Class { get; init; } = "metal";
    public bool Brittle { get; init; }
    public double Density { get; init; }
    public double E { get; init; }
    public double Nu { get; init; } = 0.3;
    public double Yield { get; init; }
    public double Ultimate { get; init; }
    public double Elongation { get; init; }
    public JohnsonCook? JC { get; init; }
    /// <summary>고분자 동적 증가 계수 k (로그 10배당 증가율)와 기준 변형률 속도.</summary>
    public double? DifK { get; init; }
    public double DifEpsDot0 { get; init; } = 1e-3;
    public PlyProps? Ply { get; init; }
    public double[]? Layup { get; init; }
    public double? DefaultThickness { get; init; }
    public IReadOnlyList<string> Aliases { get; init; } = [];
    public string Source { get; init; } = "";

    public string RateModel => JC is not null ? "Johnson-Cook" : DifK is not null ? "DIF(log10)" : "none";

    /// <summary>
    /// 변형률 속도 보정 배율(FR-MAT-03). 금속: Johnson-Cook 속도 항 1 + C·ln(ε̇/ε̇₀) (ε̇ ≤ ε̇₀ 이면 1).
    /// 고분자: DIF = 1 + k·log10(ε̇/ε̇₀). 보정 없음 = 1. 상한 3(외삽 억제).
    /// </summary>
    public double RateFactor(double epsDot)
    {
        if (!(epsDot > 0)) return 1;
        double f = 1;
        if (JC is { } jc && epsDot > jc.EpsDot0) f = 1 + jc.C * Math.Log(epsDot / jc.EpsDot0);
        else if (DifK is { } k && epsDot > DifEpsDot0) f = 1 + k * Math.Log10(epsDot / DifEpsDot0);
        return Math.Clamp(f, 1, 3);
    }

    public JsonObject ToJson() => new()
    {
        ["key"] = Key, ["name"] = Name, ["class"] = Class, ["brittle"] = Brittle,
        ["density"] = Density, ["E_GPa"] = E / 1e9, ["nu"] = Nu, ["yield_MPa"] = Yield / 1e6, ["ultimate_MPa"] = Ultimate / 1e6,
        ["elongation"] = Elongation, ["rateModel"] = RateModel, ["source"] = Source,
    };
}

/// <summary>
/// 재질 라이브러리(내장 JSON <c>Structures/materials.json</c>, 출처 표기). Fusion 재질명(한글 포함)·별칭으로 찾는다.
/// </summary>
public static class MaterialLibrary
{
    private static IReadOnlyList<Material>? _all;

    public static IReadOnlyList<Material> All => _all ??= LoadEmbedded();

    private static IReadOnlyList<Material> LoadEmbedded()
    {
        using var s = typeof(MaterialLibrary).Assembly.GetManifestResourceStream("Sim.Core.materials.json")
                      ?? throw new InvalidOperationException("내장 재질 라이브러리(materials.json)가 없습니다.");
        using var r = new StreamReader(s);
        return ParseLibrary(J.ParseObject(r.ReadToEnd()));
    }

    public static IReadOnlyList<Material> ParseLibrary(JsonObject root)
    {
        var list = new List<Material>();
        foreach (var n in root["materials"]?.AsArray() ?? throw new InvalidDataException("materials 배열이 없습니다."))
            list.Add(Parse(n!.AsObject()));
        return list;
    }

    /// <summary>라이브러리 형식 항목 → Material(MPa/GPa → Pa).</summary>
    public static Material Parse(JsonObject o, Material? baseMat = null)
    {
        double Mpa(string k, double def) => J.NumOpt(o, k) is { } v ? v * 1e6 : def;
        JohnsonCook? jc = baseMat?.JC;
        if (o["jc"] is JsonObject j)
            jc = new JohnsonCook(J.Num(j, "A_MPa", 0) * 1e6, J.Num(j, "B_MPa", 0) * 1e6, J.Num(j, "n", 0), J.Num(j, "C", 0), J.Num(j, "m", 1), J.Num(j, "epsDot0", 1));
        PlyProps? ply = baseMat?.Ply;
        if (o["ply"] is JsonObject p)
            ply = new PlyProps(J.Num(p, "E1_GPa", 135) * 1e9, J.Num(p, "E2_GPa", 10) * 1e9, J.Num(p, "G12_GPa", 5) * 1e9, J.Num(p, "nu12", 0.3),
                J.Num(p, "Xt_MPa", 2000) * 1e6, J.Num(p, "Xc_MPa", 1200) * 1e6, J.Num(p, "Yt_MPa", 50) * 1e6, J.Num(p, "Yc_MPa", 200) * 1e6,
                J.Num(p, "S_MPa", 80) * 1e6, J.Num(p, "thickness_m", 0.000125));
        var m = new Material
        {
            Key = J.Str(o, "key", baseMat?.Key ?? "custom"),
            Name = J.Str(o, "name", baseMat?.Name ?? J.Str(o, "key", "custom")),
            Class = J.Str(o, "class", baseMat?.Class ?? "metal"),
            Brittle = J.Bool(o, "brittle", baseMat?.Brittle ?? false),
            Density = J.Num(o, "density", baseMat?.Density ?? 0),
            E = J.NumOpt(o, "E_GPa") is { } e ? e * 1e9 : baseMat?.E ?? 0,
            Nu = J.Num(o, "nu", baseMat?.Nu ?? 0.3),
            Yield = Mpa("yield_MPa", baseMat?.Yield ?? 0),
            Ultimate = Mpa("ultimate_MPa", baseMat?.Ultimate ?? 0),
            Elongation = J.Num(o, "elongation", baseMat?.Elongation ?? 0),
            JC = jc,
            DifK = o["dif"] is JsonObject d ? J.Num(d, "k", 0.05) : baseMat?.DifK,
            DifEpsDot0 = o["dif"] is JsonObject d2 ? J.Num(d2, "epsDot0", 1e-3) : baseMat?.DifEpsDot0 ?? 1e-3,
            Ply = ply,
            Layup = o["layup"] is JsonArray la ? J.Arr(la) : baseMat?.Layup,
            DefaultThickness = J.NumOpt(o, "defaultThickness_m") ?? baseMat?.DefaultThickness,
            Aliases = o["aliases"] is JsonArray al ? al.Select(x => x!.GetValue<string>()).ToList() : baseMat?.Aliases ?? [],
            Source = J.Str(o, "source", baseMat?.Source ?? "scenario"),
        };
        if (m.E <= 0) throw new InvalidDataException($"재질 '{m.Key}': E_GPa 가 양수여야 합니다.");
        if (m.Yield <= 0 && m.Ultimate <= 0) throw new InvalidDataException($"재질 '{m.Key}': yield_MPa 또는 ultimate_MPa 가 필요합니다.");
        if (m.Ultimate <= 0) m = Clone(m, ultimate: m.Yield);
        if (m.Yield <= 0) m = Clone(m, yieldS: m.Ultimate);
        return m;
    }

    private static Material Clone(Material m, double? yieldS = null, double? ultimate = null) => new()
    {
        Key = m.Key, Name = m.Name, Class = m.Class, Brittle = m.Brittle, Density = m.Density, E = m.E, Nu = m.Nu,
        Yield = yieldS ?? m.Yield, Ultimate = ultimate ?? m.Ultimate, Elongation = m.Elongation, JC = m.JC, DifK = m.DifK,
        DifEpsDot0 = m.DifEpsDot0, Ply = m.Ply, Layup = m.Layup, DefaultThickness = m.DefaultThickness, Aliases = m.Aliases, Source = m.Source,
    };

    /// <summary>비교용 정규화: 소문자, 공백·기호 제거.</summary>
    public static string Normalize(string s) =>
        new(s.ToLowerInvariant().Where(ch => !char.IsWhiteSpace(ch) && ch is not ('-' or '_' or '/' or '(' or ')' or '.' or ',')).ToArray());

    /// <summary>
    /// 키·이름·별칭 정확 일치 → 없으면 가장 긴 별칭이 포함된 항목. 못 찾으면 null(미지원 재질).
    /// 예: "스테인리스스틸 AISI 304" → ss304, "ABS 플라스틱" → abs, "Aluminum 6061" → al6061-t6.
    /// </summary>
    public static Material? Find(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var q = Normalize(name);
        foreach (var m in All)
            if (Normalize(m.Key) == q || Normalize(m.Name) == q || m.Aliases.Any(a => Normalize(a) == q)) return m;
        Material? best = null; int bestLen = 2;
        foreach (var m in All)
            foreach (var a in m.Aliases.Append(m.Key))
            {
                var na = Normalize(a);
                if (na.Length > bestLen && q.Contains(na, StringComparison.Ordinal)) { best = m; bestLen = na.Length; }
            }
        return best;
    }

    public const string FusionSource = "Fusion 재료 물성";

    /// <summary>
    /// Fusion 내보내기의 바디 재료 물성(massprops parts[].materialProperties, SI: density kg/m³, E·yield·ultimate Pa, nu) → 재질.
    /// 별칭 표보다 우선한다(표에 없는 재료도 지원). 표에 같은 재료가 있으면 그 분류·취성·변형률 속도 모델은 이어받고 숫자만 Fusion 값.
    /// E 와 항복·인장 강도 중 하나가 없으면 null(판정에 쓸 수 없음 → 호출자가 별칭 표로 대체).
    /// </summary>
    public static Material? FromFusion(string? name, JsonObject? props)
    {
        if (props is null || string.IsNullOrWhiteSpace(name)) return null;
        double? Pos(string k) => J.NumOpt(props, k) is { } v && double.IsFinite(v) && v > 0 ? v : null;
        if (Pos("E") is not { } e || (Pos("yield") is null && Pos("ultimate") is null)) return null;
        var baseMat = Find(name);
        var o = new JsonObject
        {
            ["key"] = "fusion:" + name, ["name"] = name, ["source"] = FusionSource,
            ["E_GPa"] = e / 1e9,
            ["yield_MPa"] = (Pos("yield") ?? 0) / 1e6,
            ["ultimate_MPa"] = (Pos("ultimate") ?? 0) / 1e6,
            ["aliases"] = new JsonArray(),
        };
        if (Pos("density") is { } rho) o["density"] = rho;
        if (J.NumOpt(props, "nu") is { } nu && nu is > -1 and < 0.5) o["nu"] = nu;
        return Parse(o, baseMat);
    }
}
