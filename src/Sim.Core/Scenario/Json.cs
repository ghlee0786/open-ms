using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sim.Core.Numerics;

namespace Sim.Core.Scenario;

/// <summary>JsonNode 읽기 도우미(숫자 형식 관대하게 처리).</summary>
public static class J
{
    public static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static readonly JsonDocumentOptions DocOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    public static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = false };

    public static JsonObject ParseObject(string text) =>
        JsonNode.Parse(text, NodeOptions, DocOptions)?.AsObject() ?? throw new InvalidDataException("빈 JSON");

    public static double ToDouble(JsonNode n)
    {
        var v = n.AsValue();
        if (v.TryGetValue(out double d)) return d;
        if (v.TryGetValue(out int i)) return i;
        if (v.TryGetValue(out long l)) return l;
        if (v.TryGetValue(out float f)) return f;
        if (v.TryGetValue(out decimal m)) return (double)m;
        if (v.TryGetValue(out string? s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
        if (v.TryGetValue(out JsonElement e) && e.ValueKind == JsonValueKind.Number) return e.GetDouble();
        throw new InvalidDataException($"숫자가 아닙니다: {n.ToJsonString()}");
    }

    public static bool Has(JsonObject o, string k) => o.TryGetPropertyValue(k, out var n) && n is not null;

    public static double Num(JsonObject o, string k, double def) =>
        o.TryGetPropertyValue(k, out var n) && n is not null ? ToDouble(n) : def;

    public static double? NumOpt(JsonObject o, string k) =>
        o.TryGetPropertyValue(k, out var n) && n is not null ? ToDouble(n) : null;

    public static double NumReq(JsonObject o, string k, string ctx) =>
        o.TryGetPropertyValue(k, out var n) && n is not null ? ToDouble(n) : throw new InvalidDataException($"{ctx}: '{k}' 가 필요합니다.");

    public static string Str(JsonObject o, string k, string def) =>
        o.TryGetPropertyValue(k, out var n) && n is not null ? n.GetValue<string>() : def;

    public static string? StrOpt(JsonObject o, string k) =>
        o.TryGetPropertyValue(k, out var n) && n is not null ? n.GetValue<string>() : null;

    public static string StrReq(JsonObject o, string k, string ctx) =>
        StrOpt(o, k) ?? throw new InvalidDataException($"{ctx}: '{k}' 가 필요합니다.");

    public static bool Bool(JsonObject o, string k, bool def) =>
        o.TryGetPropertyValue(k, out var n) && n is not null ? n.GetValue<bool>() : def;

    public static Vec3 V3(JsonObject o, string k, Vec3 def)
    {
        if (!o.TryGetPropertyValue(k, out var n) || n is null) return def;
        var a = n.AsArray();
        if (a.Count != 3) throw new InvalidDataException($"'{k}' 는 3성분 배열이어야 합니다.");
        return new Vec3(ToDouble(a[0]!), ToDouble(a[1]!), ToDouble(a[2]!));
    }

    public static Vec3? V3Opt(JsonObject o, string k) => Has(o, k) ? V3(o, k, Vec3.Zero) : null;

    public static double[] Arr(JsonNode n) => n.AsArray().Select(x => ToDouble(x!)).ToArray();

    public static Mat3 Mat(JsonNode n)
    {
        var a = n.AsArray();
        if (a.Count == 3 && a[0] is JsonArray)
            return Mat3.FromArray(a.Select(r => (IReadOnlyList<double>)Arr(r!)).ToList());
        var v = Arr(n);
        if (v.Length == 9) return new Mat3(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8]);
        throw new InvalidDataException("행렬은 3×3 배열이어야 합니다.");
    }

    public static JsonArray ToJson(Vec3 v) => new(v.X, v.Y, v.Z);
    public static JsonArray ToJson(Mat3 m) => new(new JsonArray(m.M11, m.M12, m.M13), new JsonArray(m.M21, m.M22, m.M23), new JsonArray(m.M31, m.M32, m.M33));
}
