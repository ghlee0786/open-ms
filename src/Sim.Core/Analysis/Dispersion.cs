using System.Text.Json.Nodes;
using Sim.Core.Numerics;
using Sim.Core.Scenario;

namespace Sim.Core.Analysis;

/// <summary>
/// 분산 요소(Dispersion, FR-MC-01): 시나리오의 어떤 숫자든 경로("environment.wind.speed",
/// "bodies.sensor1.mass", "bodies.box1.devices.chute.cd0", "events.release.when.t")로 지정해 분포를 준다.
/// 배열 원소는 "name" 값 또는 숫자 인덱스로 찾는다.
/// mode: add(공칭+표본, 기본) | scale(공칭×표본) | set(표본).
/// dist: normal(mean,sd) | uniform(min,max) | loguniform(min,max — ln 균등, 배율 불확도) | truncnormal(mean,sd,min,max) | choice(values).
/// </summary>
public sealed class DispersionDef
{
    public required string Path { get; init; }
    public string Dist { get; init; } = "normal";
    public string Mode { get; init; } = "add";
    public double? Mean { get; init; }
    public double Sd { get; init; }
    public double Min { get; init; }
    public double Max { get; init; }
    public double[] Values { get; init; } = [];

    public static List<DispersionDef> Parse(JsonArray? arr)
    {
        var list = new List<DispersionDef>();
        if (arr is null) return list;
        foreach (var n in arr)
        {
            var o = n!.AsObject();
            list.Add(new DispersionDef
            {
                Path = J.StrReq(o, "path", "dispersions"),
                Dist = J.Str(o, "dist", "normal"),
                Mode = J.Str(o, "mode", "add"),
                Mean = J.NumOpt(o, "mean"),
                Sd = J.Num(o, "sd", 0),
                Min = J.Num(o, "min", 0),
                Max = J.Num(o, "max", 0),
                Values = o["values"] is JsonArray v ? J.Arr(v) : [],
            });
        }
        return list;
    }

    /// <summary>균등난수 u∈(0,1) → 분포 표본(역변환).</summary>
    public double Sample(double u)
    {
        u = Math.Clamp(u, 1e-12, 1 - 1e-12);
        double defMean = Mode == "scale" ? 1 : 0;
        switch (Dist)
        {
            case "normal": return (Mean ?? defMean) + Sd * Stat.NormInv(u);
            case "uniform": return Min + (Max - Min) * u;
            case "loguniform":
                if (!(Min > 0) || !(Max >= Min)) throw new InvalidDataException($"{Path}: loguniform 은 0 < min ≤ max");
                return Math.Exp(Math.Log(Min) + (Math.Log(Max) - Math.Log(Min)) * u);
            case "truncnormal":
            {
                double m = Mean ?? defMean;
                double pa = NormCdf((Min - m) / Sd), pb = NormCdf((Max - m) / Sd);
                return m + Sd * Stat.NormInv(pa + u * (pb - pa));
            }
            case "choice":
                if (Values.Length == 0) throw new InvalidDataException($"{Path}: choice 에 values 필요");
                return Values[Math.Min(Values.Length - 1, (int)(u * Values.Length))];
            default: throw new InvalidDataException($"{Path}: 알 수 없는 분포 {Dist}");
        }
    }

    public double Apply(double nominal, double sample) => Mode switch
    {
        "add" => nominal + sample,
        "scale" => nominal * sample,
        "set" => sample,
        _ => throw new InvalidDataException($"{Path}: 알 수 없는 mode {Mode}")
    };

    public static double NormCdf(double x) => 0.5 * Erfc(-x / Math.Sqrt(2));

    private static double Erfc(double x)
    {
        // Numerical Recipes erfc (Chebyshev), 상대오차 < 1.2e-7
        double z = Math.Abs(x);
        double t = 1 / (1 + 0.5 * z);
        double r = t * Math.Exp(-z * z - 1.26551223 + t * (1.00002368 + t * (0.37409196 + t * (0.09678418 +
                   t * (-0.18628806 + t * (0.27886807 + t * (-1.13520398 + t * (1.48851587 + t * (-0.82215223 + t * 0.17087277)))))))));
        return x >= 0 ? r : 2 - r;
    }
}

public static class JsonPath
{
    public static JsonNode Resolve(JsonObject root, string path, out JsonNode parent, out string key)
    {
        var parts = path.Split('.');
        JsonNode cur = root;
        parent = root; key = "";
        for (int i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            parent = cur; key = p;
            JsonNode? next = cur switch
            {
                JsonObject o => o[p],
                JsonArray a => int.TryParse(p, out int idx) ? a[idx] : a.FirstOrDefault(x => x is JsonObject xo && J.StrOpt(xo, "name") == p),
                _ => null
            };
            if (next is null)
            {
                if (i == parts.Length - 1 && cur is JsonObject) return null!;
                throw new KeyNotFoundException($"경로 '{path}' 의 '{p}' 를 찾을 수 없습니다.");
            }
            cur = next;
        }
        return cur;
    }

    public static double? GetNumber(JsonObject root, string path)
    {
        var n = Resolve(root, path, out _, out _);
        return n is null ? null : J.ToDouble(n);
    }

    /// <summary>경로가 없거나 숫자가 아니면 null.</summary>
    public static double? GetNumberSafe(JsonObject root, string path)
    {
        try { return GetNumber(root, path); }
        catch { return null; }
    }

    public static void SetNumber(JsonObject root, string path, double value)
    {
        Resolve(root, path, out var parent, out var key);
        switch (parent)
        {
            case JsonObject o: o[key] = value; break;
            case JsonArray a when int.TryParse(key, out int idx): a[idx] = value; break;
            default: throw new KeyNotFoundException($"경로 '{path}' 에 값을 쓸 수 없습니다.");
        }
    }

    public static void Set(JsonObject root, string path, JsonNode? value)
    {
        Resolve(root, path, out var parent, out var key);
        switch (parent)
        {
            case JsonObject o: o[key] = value; break;
            case JsonArray a when int.TryParse(key, out int idx): a[idx] = value; break;
            default: throw new KeyNotFoundException($"경로 '{path}' 에 값을 쓸 수 없습니다.");
        }
    }
}

/// <summary>표본 생성: 단순 무작위 / 라틴 하이퍼큐브(FR-MC-02).</summary>
public static class Sampler
{
    public static double[][] Uniforms(int runs, int dims, string method, ulong seed)
    {
        var rng = new Rng(seed);
        var u = new double[runs][];
        for (int i = 0; i < runs; i++) u[i] = new double[dims];
        if (method == "lhs")
        {
            for (int d = 0; d < dims; d++)
            {
                var perm = Enumerable.Range(0, runs).ToArray();
                for (int i = runs - 1; i > 0; i--)
                {
                    int j = (int)(rng.NextULong() % (ulong)(i + 1));
                    (perm[i], perm[j]) = (perm[j], perm[i]);
                }
                for (int i = 0; i < runs; i++) u[i][d] = (perm[i] + rng.NextDouble()) / runs;
            }
        }
        else
        {
            for (int i = 0; i < runs; i++)
                for (int d = 0; d < dims; d++) u[i][d] = rng.NextDouble();
        }
        return u;
    }
}
