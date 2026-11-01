using System.Text.Json.Nodes;
using Sim.Core.Scenario;

namespace Sim.Core.Airflow;

/// <summary>
/// GPU 가상 풍동 옆에 나란히 보여 줄 OpenFOAM 정상 RANS 계수(표면 유동장 파일 mns.surface-flow/1 의 cases[].coeffs CD · CL).
/// 축대칭·앞뒤 대칭 물체 가정으로 받음각을 접는다: CL(−α) = −CL(α), α > 90° 는 180° − α 의 CL 부호 반대(뒤집힌 물체 = −α').
/// </summary>
public sealed class OpenFoamReference
{
    public sealed record Case(string Group, string Method, double Alpha, double Mach, double CD, double CL);

    public required string Source { get; init; }
    public required IReadOnlyList<Case> Cases { get; init; }

    public static OpenFoamReference Load(string surfaceFlowJson)
    {
        var o = J.ParseObject(File.ReadAllText(surfaceFlowJson));
        var list = new List<Case>();
        foreach (var n in o["cases"] as JsonArray ?? [])
        {
            var c = n!.AsObject();
            if (!J.Bool(c, "valid", true) || c["coeffs"] is not JsonObject k || k["CD"] is null) continue;
            list.Add(new Case(J.Str(c, "group", ""), J.Str(c, "method", ""), J.NumReq(c, "alpha", "case"), J.Num(c, "mach", 0), J.Num(k, "CD", 0), J.Num(k, "CL", 0)));
        }
        return new OpenFoamReference { Source = surfaceFlowJson, Cases = list };
    }

    /// <summary>받음각 접기 → (표에서 찾을 0–90° 받음각, CL 부호).</summary>
    public static (double Alpha, int ClSign) Fold(double alphaDeg)
    {
        double a = ((alphaDeg + 180) % 360 + 360) % 360 - 180;    // −180..180
        int sign = a < 0 ? -1 : 1;
        a = Math.Abs(a);
        if (a > 90) { a = 180 - a; sign = -sign; }
        return (a, sign);
    }

    /// <summary>
    /// 가장 많은 받음각 케이스가 있는 묶음(보통 M0.50 정상 RANS)에서 접은 받음각 사이 선형 보간. 표 밖이면 경계값, 케이스가 없으면 null.
    /// </summary>
    public (double CD, double CL, string Label)? At(double alphaDeg, double betaDeg = 0)
    {
        var g = Cases.GroupBy(c => c.Group).OrderByDescending(x => x.Count()).FirstOrDefault();
        if (g is null) return null;
        var cs = g.OrderBy(c => c.Alpha).ToList();
        // 옆미끄럼: 축대칭 물체의 전체 받음각 α_t = acos(cos α cos β)
        double a0 = Math.Abs(betaDeg) > 1e-9
            ? Math.Acos(Math.Clamp(Math.Cos(alphaDeg * Math.PI / 180) * Math.Cos(betaDeg * Math.PI / 180), -1, 1)) * 180 / Math.PI * Math.Sign(alphaDeg == 0 ? 1 : alphaDeg)
            : alphaDeg;
        var (a, sgn) = Fold(a0);
        Case lo = cs[0], hi = cs[^1];
        foreach (var c in cs) { if (c.Alpha <= a) lo = c; }
        foreach (var c in Enumerable.Reverse(cs)) { if (c.Alpha >= a) hi = c; }
        double t = hi.Alpha > lo.Alpha ? Math.Clamp((a - lo.Alpha) / (hi.Alpha - lo.Alpha), 0, 1) : 0;
        double cd = lo.CD + (hi.CD - lo.CD) * t, cl = (lo.CL + (hi.CL - lo.CL) * t) * sgn;
        string method = lo.Method == "urans" ? "URANS" : "정상 RANS";
        return (cd, cl, $"OpenFOAM {method} {g.Key} α {a:0.#}°{(Math.Abs(a - a0) > 1e-6 ? " (대칭 접기)" : "")}");
    }
}
