using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Sim.Core.Numerics;
using Sim.Core.Scenario;

namespace Sim.Core.Fea;

/// <summary>Fusion 결과 값 하나: 값(SI) · 어디서 읽었는지.</summary>
public sealed record FusionValue(double Value, string Source);

/// <summary>
/// Fusion Simulation 학습 보고서(HTML 또는 그 본문을 텍스트로 저장한 .txt)에서 읽은 기준값 — 재현 결과와 비교용.
/// 보고서 '결과 요약' 표(결과 유형 · 하위 결과 · 최소 · 최대)에 수치가 있으면 그것을, 없으면(실측: 실모델 정적 해석 보고서는 표가 비어 있고 값은
/// 결과 그림 범례에만 있다) <see cref="FusionReport.ApplyLegend"/> 로 사람이 그림에서 읽은 값을 출처와 함께 넣는다.
/// </summary>
public sealed class FusionReferenceData
{
    public string Path { get; init; } = "";
    public string Study { get; set; } = "";
    public int? Nodes { get; set; }
    public int? Elements { get; set; }
    public Vec3? Acceleration { get; set; }
    public double? ContactTolerance { get; set; }
    public FusionValue? MinSafetyFactor { get; set; }
    public FusionValue? MaxVonMises { get; set; }
    public FusionValue? MaxDisplacement { get; set; }
    public FusionValue? Reaction { get; set; }
    public List<string> Notes { get; } = new();

    public JsonObject ToJson()
    {
        static JsonNode? V(FusionValue? v) => v is null ? null : new JsonObject { ["value"] = v.Value, ["source"] = v.Source };
        return new JsonObject
        {
            ["report"] = Path, ["study"] = Study, ["nodes"] = Nodes, ["elements"] = Elements,
            ["acceleration_mps2"] = Acceleration is { } a ? J.ToJson(a) : null, ["contactTolerance_m"] = ContactTolerance,
            ["minSafetyFactor"] = V(MinSafetyFactor), ["maxVonMises_Pa"] = V(MaxVonMises), ["maxDisplacement_m"] = V(MaxDisplacement), ["reaction_N"] = V(Reaction),
            ["notes"] = new JsonArray(Notes.Select(n => (JsonNode)JsonValue.Create(n)!).ToArray()),
        };
    }
}

public static class FusionReport
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly Regex Num = new(@"^\s*([-+]?\d+(?:[.,]\d+)?(?:[eE][-+]?\d+)?)\s*(.*)$", RegexOptions.Compiled);

    /// <summary>보고서 파일(.html/.htm 또는 텍스트) 읽기 — HTML 은 태그를 줄바꿈으로 바꾸고 data: 그림을 지운 뒤 같은 텍스트 규칙으로.</summary>
    public static FusionReferenceData Read(string path)
    {
        var text = File.ReadAllText(path);
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return Parse(ext is ".html" or ".htm" ? HtmlToLines(text) : text.Split('\n'), path);
    }

    public static string[] HtmlToLines(string html)
    {
        html = Regex.Replace(html, @"data:[^""')\s]+", "", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<(script|style)\b.*?</\1>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<[^>]+>", "\n");
        return html.Split('\n').Select(l => WebUtility.HtmlDecode(l)).ToArray();
    }

    /// <summary>값 + 단위 → SI(MPa/kPa/Pa → Pa, mm/cm/m → m, N/kN, m/s^2·mm/s^2). 단위 없으면 그대로. 숫자가 아니면 null.</summary>
    public static double? ParseQuantity(string s)
    {
        var m = Num.Match(s.Trim());
        if (!m.Success || !double.TryParse(m.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, Inv, out var v)) return null;
        var u = m.Groups[2].Value.Replace(" ", "").ToLowerInvariant();
        return u switch
        {
            "" => v, "mpa" or "n/mm^2" => v * 1e6, "kpa" => v * 1e3, "pa" => v, "gpa" => v * 1e9,
            "mm" => v * 1e-3, "cm" => v * 1e-2, "m" => v, "n" => v, "kn" => v * 1e3,
            "m/s^2" or "m/s²" => v, "mm/s^2" or "mm/s²" => v * 1e-3,
            _ => u.StartsWith("mpa", StringComparison.Ordinal) ? v * 1e6 : u.StartsWith("mm", StringComparison.Ordinal) ? v * 1e-3 : v,
        };
    }

    private static readonly string[] SafetyKeys = ["안전계수", "Safety Factor"];
    private static readonly string[] StressKeys = ["응력", "Stress"];
    private static readonly string[] DispKeys = ["변위", "Displacement"];
    private static readonly string[] ReactKeys = ["반력", "Reaction Force"];

    /// <summary>보고서 줄(한국어 · 영어 Fusion 보고서 머리말)에서 메시 수 · 가속도 · 접촉 공차 · 결과 요약 표.</summary>
    public static FusionReferenceData Parse(IEnumerable<string> raw, string path = "")
    {
        var lines = raw.Select(l => l.Trim().TrimStart('﻿')).Where(l => l.Length > 0).ToList();
        var d = new FusionReferenceData { Path = path };
        string? Next(int i) => i + 1 < lines.Count ? lines[i + 1] : null;
        double ax = double.NaN, ay = double.NaN, az = double.NaN;
        int summaryAt = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            if ((l is "해석된 파일" or "Analyzed File") && Next(i) is { } st) d.Study = st;
            else if ((l is "노드" or "Nodes") && int.TryParse(Next(i), NumberStyles.Integer, Inv, out var nn)) d.Nodes = nn;
            else if ((l is "요소" or "Elements") && int.TryParse(Next(i), NumberStyles.Integer, Inv, out var ne)) d.Elements = ne;
            else if ((l is "접촉 공차" or "Contact Tolerance") && ParseQuantity(Next(i) ?? "") is { } ct) d.ContactTolerance = ct;
            else if ((l is "X 값" or "X Value") && ParseQuantity(Next(i) ?? "") is { } x) ax = x;
            else if ((l is "Y 값" or "Y Value") && ParseQuantity(Next(i) ?? "") is { } y) ay = y;
            else if ((l is "Z 값" or "Z Value") && ParseQuantity(Next(i) ?? "") is { } z) az = z;
            else if (l is "결과 요약" or "Result Summary") summaryAt = i;
        }
        if (double.IsFinite(ax) && double.IsFinite(ay) && double.IsFinite(az)) d.Acceleration = new Vec3(ax, ay, az);
        if (summaryAt < 0) { d.Notes.Add("결과 요약 표가 없습니다"); return d; }
        // 결과 요약: 머리줄(결과 유형 · 하위 결과 · 최소 · 최대) 다음 — [유형] 하위 결과 최소 최대 [하위 결과 최소 최대 …] (HTML 은 유형 칸이 행 병합이라 한 번만)
        string? TypeOf(string l) => SafetyKeys.Contains(l) ? "safety" : StressKeys.Contains(l) ? "stress" : DispKeys.Contains(l) ? "disp" : ReactKeys.Contains(l) ? "react" : null;
        string type = "";
        int found = 0;
        for (int i = summaryAt + 1; i < lines.Count; i++)
        {
            var l = lines[i];
            if (l is "결과 유형" or "하위 결과" or "최소" or "최대" or "Result Type" or "Sub Result" or "Min" or "Max") continue;
            var mn = i + 1 < lines.Count ? ParseQuantity(lines[i + 1]) : null;
            var mx = i + 2 < lines.Count ? ParseQuantity(lines[i + 2]) : null;
            bool row = mn is not null && mx is not null;
            if (!row) { if (TypeOf(l) is { } t) type = t; continue; }
            if (type == "") type = TypeOf(l) ?? "";
            var src = $"보고서 결과 요약 표({l})";
            if (type == "safety") d.MinSafetyFactor ??= new FusionValue(mn!.Value, src);
            else if (type == "stress" && l is "폰 미세스" or "Von Mises") d.MaxVonMises ??= new FusionValue(mx!.Value, src);
            else if (type == "disp" && l is "전체" or "Total") d.MaxDisplacement ??= new FusionValue(mx!.Value, src);
            else if (type == "react" && l is "전체" or "Total") d.Reaction ??= new FusionValue(mx!.Value, src);
            found++;
            i += 2;
        }
        if (found == 0) d.Notes.Add("결과 요약 표에 수치가 없습니다(유형 이름만) — 값은 결과 그림 범례에만 있음(--fusion-legend 로 출처와 함께 입력)");
        return d;
    }

    /// <summary>
    /// 그림 범례에서 사람이 읽은 값 "sf=1.5;vm=200.5;u=1.25;rf=…" (vm MPa · u mm · rf N). 이미 표에서 읽은 값은 덮지 않는다.
    /// </summary>
    public static void ApplyLegend(FusionReferenceData d, string spec, string source)
    {
        foreach (var kv in spec.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = kv.Split('=', 2, StringSplitOptions.TrimEntries);
            if (p.Length != 2 || !double.TryParse(p[1], NumberStyles.Float, Inv, out var v)) throw new InvalidDataException($"--fusion-legend 항목 '{kv}' (예: sf=1.5;vm=200.5;u=1.25)");
            switch (p[0].ToLowerInvariant())
            {
                case "sf": d.MinSafetyFactor ??= new FusionValue(v, source); break;
                case "vm": d.MaxVonMises ??= new FusionValue(v * 1e6, source); break;
                case "u": d.MaxDisplacement ??= new FusionValue(v * 1e-3, source); break;
                case "rf": d.Reaction ??= new FusionValue(v, source); break;
                default: throw new InvalidDataException($"--fusion-legend 키 '{p[0]}' — sf · vm(MPa) · u(mm) · rf(N)");
            }
        }
    }
}

/// <summary>
/// 설계 측 CalculiX 결과(_fea_station.py 의 fea_station_results.json): cases.{사례}.rows[{body, vm_p99, vm_max, SF_p99, allow}] (MPa) ·
/// cases.MODAL.freq_hz · cases.{사례}.reaction · expected_N · mass_kg. 바디별 비교용.
/// </summary>
public sealed record DesignBodyResult(string Body, double P99, double Max, double? SfP99, double? Allow);

public sealed class DesignReference
{
    public string Path { get; init; } = "";
    public string Case { get; init; } = "";
    public double MassKg { get; init; } = double.NaN;
    public double ExpectedN { get; init; } = double.NaN;
    public List<DesignBodyResult> Rows { get; } = new();
    public List<double> FrequenciesHz { get; } = new();

    public static DesignReference Read(string path, string caseName)
    {
        var root = J.ParseObject(File.ReadAllText(path));
        var cases = root["cases"] as JsonObject ?? throw new InvalidDataException($"{path}: cases 가 없습니다.");
        var c = cases[caseName] as JsonObject ?? throw new InvalidDataException($"{path}: 사례 '{caseName}' 이 없습니다({string.Join(", ", cases.Select(k => k.Key))}).");
        var d = new DesignReference { Path = path, Case = caseName, MassKg = J.Num(root, "mass_kg", double.NaN), ExpectedN = J.Num(c, "expected_N", double.NaN) };
        foreach (var r in (c["rows"] as JsonArray ?? []).OfType<JsonObject>())
            d.Rows.Add(new DesignBodyResult(J.Str(r, "body", ""), J.Num(r, "vm_p99", double.NaN) * 1e6, J.Num(r, "vm_max", double.NaN) * 1e6, J.NumOpt(r, "SF_p99"), J.NumOpt(r, "allow") * 1e6));
        if (cases["MODAL"]?["freq_hz"] is JsonArray f) d.FrequenciesHz.AddRange(f.Select(x => J.ToDouble(x!)));
        return d;
    }

    public JsonObject ToJson() => new()
    {
        ["results"] = Path, ["case"] = Case, ["mass_kg"] = double.IsFinite(MassKg) ? MassKg : null, ["expected_N"] = double.IsFinite(ExpectedN) ? ExpectedN : null,
        ["frequencies_hz"] = new JsonArray(FrequenciesHz.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()),
        ["minSafetyFactorP99"] = Rows.Where(r => r.SfP99 is not null).MinBy(r => r.SfP99) is { } m ? new JsonObject { ["value"] = m.SfP99, ["body"] = m.Body } : null,
    };
}
