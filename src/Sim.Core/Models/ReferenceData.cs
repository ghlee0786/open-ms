using System.Text.Json.Nodes;
using Sim.Core.Scenario;

namespace Sim.Core.Models;

/// <summary>
/// 공개 자료에서 옮긴 모델 기준 자료(내장 <c>Models/reference_data.json</c>, tools/validation/convert_public.py 가 생성,
/// 원본 case.json sha256 기록 — PRD FR-REL-06). 원통 횡류 CD(Re, M) · 유한 길이 η · Knacke 캐노피 상수 · 충격 판정 기준 · 부품 값.
/// </summary>
public static class ReferenceData
{
    private static readonly Lazy<JsonObject> _root = new(() =>
    {
        using var s = typeof(ReferenceData).Assembly.GetManifestResourceStream("Sim.Core.reference_data.json")
                      ?? throw new InvalidOperationException("내장 기준 자료(reference_data.json)가 없습니다.");
        using var r = new StreamReader(s);
        return J.ParseObject(r.ReadToEnd());
    });

    public static JsonObject Root => _root.Value;
    public static JsonObject Section(string name) => Root[name] as JsonObject ?? throw new InvalidDataException($"기준 자료 '{name}' 없음");
}

/// <summary>
/// 원통 횡류 항력(FR-REL-06 ①): 2-D CD(Re, M) × 유한 길이 η(L/D).
///  · Re ≥ 행 최소 Re: NASA TR R-300 표 II(일정 마하 행, 저자가 표 I(a) 를 다시 그린 값, A) — 행 안에서 log Re 선형, 행 사이 M 선형.
///  · Re ≤ 2×10⁵(아임계): M ≤ 0.2 → 1.20, M ≥ 0.3 → NACA TN 2960 그림 7(B).
///  · 그 사이(항력 위기): log Re 선형(행에 저 Re 점이 있으면 그 점이 형태를 정한다).
///  · η(L/D): TN 2960 그림 8 아임계 곡선(B), log(L/D) 선형.
/// 범위 밖(M &gt; 0.6, Re &gt; 행 최대, L/D 밖)은 경계값 고정 + 카운트(VV-32 적용 범위 기록).
/// </summary>
public static class CylinderCrossflow
{
    private sealed record Row(double Mach, double[] LogRe, double[] Cd);

    private static readonly Lazy<(Row[] Rows, Table1D Sub, double ReSub, Table1D Eta)> _data = new(() =>
    {
        var c = ReferenceData.Section("cylinderCrossflow");
        var sub = c["subcriticalCdVsMach"]!.AsArray().Select(r => J.Arr(r!)).ToArray();
        var subT = new Table1D(sub.Select(r => r[0]).ToArray(), sub.Select(r => r[1]).ToArray());
        double reSub = J.Num(c, "reSubcritical", 2e5);
        var rows = new List<Row>();
        foreach (var rn in c["machRows"]!.AsArray())
        {
            var o = rn!.AsObject();
            double m = J.NumReq(o, "mach", "machRows");
            var pts = o["points"]!.AsArray().Select(p => J.Arr(p!)).Where(p => p[0] > reSub).OrderBy(p => p[0]).ToList();
            var lr = new List<double> { Math.Log(reSub) };
            var cd = new List<double> { subT.Eval(m) };
            foreach (var p in pts) { lr.Add(Math.Log(p[0])); cd.Add(p[1]); }
            rows.Add(new Row(m, lr.ToArray(), cd.ToArray()));
        }
        var eta = c["eta"]!.AsArray().Select(r => J.Arr(r!)).ToArray();
        var etaT = new Table1D(eta.Select(r => Math.Log(r[0])).ToArray(), eta.Select(r => r[1]).ToArray());
        return (rows.OrderBy(r => r.Mach).ToArray(), subT, reSub, etaT);
    });

    /// <summary>표 적용 상한 마하.</summary>
    public static double MaxMach => _data.Value.Rows[^1].Mach;
    public static double MinRe => _data.Value.ReSub;
    public static double MaxRe => _data.Value.Rows.Max(r => Math.Exp(r.LogRe[^1]));
    /// <summary>범위 밖 조회 수(M, Re, L/D 중 하나라도 표 밖).</summary>
    public static long OutOfRange;

    private static double RowEval(Row r, double logRe, ref bool outside)
    {
        if (logRe <= r.LogRe[0]) return r.Cd[0];
        if (logRe >= r.LogRe[^1]) { if (logRe > r.LogRe[^1] + 1e-9) outside = true; return r.Cd[^1]; }
        int i = 1;
        while (r.LogRe[i] < logRe) i++;
        double f = (logRe - r.LogRe[i - 1]) / (r.LogRe[i] - r.LogRe[i - 1]);
        return r.Cd[i - 1] + (r.Cd[i] - r.Cd[i - 1]) * f;
    }

    /// <summary>2-D(무한 길이) 원통 횡류 CD. outside = 표 범위 밖(경계값 고정).</summary>
    public static double Cd2D(double re, double mach, out bool outside)
    {
        var (rows, sub, reSub, _) = _data.Value;
        outside = mach > rows[^1].Mach + 1e-9 || re <= 0;
        mach = Math.Clamp(mach, 0, rows[^1].Mach);
        if (re <= reSub) return sub.Eval(mach);
        double lr = Math.Log(re);
        if (mach <= rows[0].Mach) return RowEval(rows[0], lr, ref outside);
        int k = 1;
        while (k < rows.Length - 1 && rows[k].Mach < mach) k++;
        var a = rows[k - 1]; var b = rows[k];
        double ca = RowEval(a, lr, ref outside), cb = RowEval(b, lr, ref outside);
        double f = (mach - a.Mach) / (b.Mach - a.Mach);
        return ca + (cb - ca) * Math.Clamp(f, 0, 1);
    }

    public static double Cd2D(double re, double mach) => Cd2D(re, mach, out _);

    /// <summary>유한 길이 비 η = CD(L/D)/CD(∞). 표 밖은 경계값 고정.</summary>
    public static double Eta(double lOverD, out bool outside)
    {
        var t = _data.Value.Eta;
        double x = Math.Log(Math.Max(lOverD, 1e-6));
        outside = x < t.X[0] - 1e-9 || x > t.X[^1] + 1e-9;
        return t.Eval(Math.Clamp(x, t.X[0], t.X[^1]));
    }

    /// <summary>유한 길이 원통 횡류 CD = η(L/D) · CD2D(Re, M). Re 기준 길이 = 지름.</summary>
    public static double Cd(double lOverD, double re, double mach)
    {
        double cd = Eta(lOverD, out bool o1) * Cd2D(re, mach, out bool o2);
        if (o1 || o2) Interlocked.Increment(ref OutOfRange);
        return cd;
    }
}
