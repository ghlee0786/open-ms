using System.Text.Json.Nodes;
using Sim.Core.Analysis;
using Sim.Core.Scenario;

namespace Sim.Core.Reliability;

/// <summary>
/// 입력 출처 등급(PRD FR-REL-02). 시나리오 <c>inputPedigree</c> 배열:
/// <c>{ "path": "connectors.cord1.breakLoad", "grade": "A", "source": "…", "ref": "validation/…", "dist": {분산 정의}, "note": "…" }</c>.
/// 등급: A 실측 표·규격 표 / B 디지타이즈·판단 대응 / C 제조사·2차 / D 가정. 등록되지 않은 수치 입력은 'D(미분류)'로 센다.
/// dist 가 있으면 불확도 정량화(FR-REL-03) MC 의 입력 분포가 된다(시나리오 dispersions 의 같은 경로는 이것으로 대체).
/// </summary>
public sealed record PedigreeEntry(string Path, string Grade, string Source, string? Ref, string? Note, DispersionDef? Dist, double? Value, string? Text);

public sealed record PedigreeSummary(IReadOnlyList<PedigreeEntry> Entries, IReadOnlyDictionary<string, int> ByGrade, int NumericLeaves, int Unregistered,
    IReadOnlyList<string> MissingPaths);

public static class InputPedigree
{
    public static readonly string[] Grades = ["A", "B", "C", "D"];

    public static string GradeLabel(string g) => g switch
    {
        "A" => "A 실측·규격 표",
        "B" => "B 디지타이즈·판단 대응",
        "C" => "C 제조사·2차 자료",
        "D" => "D 가정",
        _ => g,
    };

    public static List<PedigreeEntry> Parse(JsonObject root)
    {
        var list = new List<PedigreeEntry>();
        if (root["inputPedigree"] is not JsonArray arr) return list;
        foreach (var n in arr)
        {
            var o = n!.AsObject();
            var path = J.StrReq(o, "path", "inputPedigree");
            var grade = J.Str(o, "grade", "D").ToUpperInvariant();
            if (!Grades.Contains(grade)) throw new InvalidDataException($"inputPedigree '{path}': 등급 A|B|C|D ('{grade}')");
            DispersionDef? dist = null;
            if (o["dist"] is JsonObject d)
            {
                var copy = (JsonObject)d.DeepClone();
                copy["path"] = path;
                dist = DispersionDef.Parse(new JsonArray(copy))[0];
            }
            double? value = null; string? text = null;
            try
            {
                var node = JsonPath.Resolve(root, path, out _, out _);
                if (node is JsonValue v)
                {
                    if (v.TryGetValue(out double dv)) value = dv;
                    else if (v.TryGetValue(out string? sv)) text = sv;
                }
            }
            catch (KeyNotFoundException) { }
            list.Add(new PedigreeEntry(path, grade, J.Str(o, "source", ""), J.StrOpt(o, "ref"), J.StrOpt(o, "note"), dist, value, text));
        }
        return list;
    }

    private static readonly HashSet<string> SkipKeys = ["description", "metrics", "dispersions", "inputPedigree", "uq", "monteCarlo", "gpuMonteCarlo", "color", "schema", "stages", "channels"];

    /// <summary>시나리오의 숫자 잎(입력 수치) 경로 — 설명·지표·분석 설정·표시 색 등은 제외.</summary>
    public static List<string> NumericLeaves(JsonObject root)
    {
        var res = new List<string>();
        void Walk(JsonNode? n, string path)
        {
            switch (n)
            {
                case JsonObject o:
                    foreach (var (k, v) in o)
                    {
                        if (SkipKeys.Contains(k)) continue;
                        Walk(v, path.Length == 0 ? k : $"{path}.{k}");
                    }
                    break;
                case JsonArray a:
                    for (int i = 0; i < a.Count; i++)
                    {
                        string seg = a[i] is JsonObject ao && J.StrOpt(ao, "name") is { } nm ? nm : i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        Walk(a[i], $"{path}.{seg}");
                    }
                    break;
                case JsonValue v when v.TryGetValue(out double _):
                    res.Add(path);
                    break;
            }
        }
        Walk(root, "");
        return res;
    }

    public static PedigreeSummary Summarize(JsonObject root)
    {
        var entries = Parse(root);
        var leaves = NumericLeaves(root);
        var covered = new HashSet<string>(entries.Select(e => e.Path));
        // 등록 경로가 객체·배열(예: aero.damping)을 가리키면 그 아래 잎도 등록된 것으로 본다
        int unregistered = leaves.Count(l => !covered.Contains(l) && !covered.Any(c => l.StartsWith(c + ".", StringComparison.Ordinal)));
        var missing = entries.Where(e => e.Value is null && e.Text is null).Select(e => e.Path).ToList();
        var by = Grades.ToDictionary(g => g, g => entries.Count(e => e.Grade == g));
        return new PedigreeSummary(entries, by, leaves.Count, unregistered, missing);
    }

    /// <summary>등급별 분포가 있는 항목을 시나리오 dispersions 에 합친다(같은 경로는 출처 등급 분포로 대체).</summary>
    public static List<(DispersionDef Def, string Grade, string Source)> MergedDispersions(JsonObject root)
    {
        var ped = Parse(root);
        var res = new List<(DispersionDef, string, string)>();
        var byPath = ped.Where(p => p.Dist is not null).ToDictionary(p => p.Path);
        foreach (var d in DispersionDef.Parse(root["dispersions"] as JsonArray))
            if (!byPath.ContainsKey(d.Path)) res.Add((d, "환경", "시나리오 분산(자연 변동·운용 조건)"));
        foreach (var p in ped.Where(p => p.Dist is not null))
        {
            try { JsonPath.Resolve(root, p.Path, out _, out _); }
            catch (KeyNotFoundException) { continue; }
            res.Add((p.Dist!, p.Grade, p.Source));
        }
        return res;
    }
}
