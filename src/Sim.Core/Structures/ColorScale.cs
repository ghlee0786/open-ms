using System.Globalization;

namespace Sim.Core.Structures;

/// <summary>색 범위(레전드 최소~최대). Warning = 고정 범위 입력을 고쳐 쓴 이유(역전·비숫자 등).</summary>
public sealed record ScaleRange(double Min, double Max, bool Fixed, string? Warning = null)
{
    public double Span => Max - Min;
}

/// <summary>
/// 면 색·레전드 공용 색 척도(FR-STR-03, 화면 밖에서도 시험 가능하게 Sim.Core 에 둔다):
/// 연속 컬러맵(Turbo · Viridis), 범위 결정(자동/고정, 역전·퇴화·NaN 처리), 값→색, 레전드 그라데이션 정지점, 눈금 라벨.
/// 면 색과 레전드 막대는 같은 함수(<see cref="ValueColor"/>)로 만들어 서로 어긋나지 않는다.
/// </summary>
public static class ColorScale
{
    public static readonly string[] Names = ["Turbo", "Viridis"];

    /// <summary>자료 없음(NaN — 재질 미지정 안전계수 등) 면 색: 어떤 컬러맵 끝색과도 다른 중립 회색.</summary>
    public static readonly (float R, float G, float B) NoData = (0.42f, 0.44f, 0.47f);

    // Google Turbo 공식 256 색 표(Mikhailov 2019, Apache-2.0)를 33 점 균등 추출 — matplotlib 'turbo' 와 같은 값.
    // 이전의 6차 다항 근사는 t=0 에서 (0.14, 0.09, 0.11) 거의 검정(표 값은 짙은 파랑 0.19, 0.07, 0.23)이라,
    // 응력이 낮은 면·안전계수 높은 면이 어두운 3D 배경에 묻혔다(적대적 시나리오 A41/A42 에서 발견).
    private static readonly (double R, double G, double B)[] TurboTable =
    [
        (0.18995, 0.07176, 0.23217), (0.22500, 0.16354, 0.45096), (0.25107, 0.25237, 0.63374), (0.26816, 0.33825, 0.78050),
        (0.27628, 0.42118, 0.89123), (0.27543, 0.50115, 0.96594), (0.25862, 0.57958, 0.99876), (0.21382, 0.65886, 0.97959),
        (0.15844, 0.73551, 0.92305), (0.11167, 0.80569, 0.84525), (0.09267, 0.86554, 0.76230), (0.12014, 0.91193, 0.68660),
        (0.19659, 0.94901, 0.59466), (0.30513, 0.97697, 0.48987), (0.42778, 0.99419, 0.38575), (0.54658, 0.99907, 0.29581),
        (0.64362, 0.98999, 0.23356), (0.71577, 0.96875, 0.20815), (0.79524, 0.93025, 0.20386), (0.86709, 0.87968, 0.21391),
        (0.92666, 0.82025, 0.22570), (0.96931, 0.75519, 0.22663), (0.99163, 0.68408, 0.20706), (0.99644, 0.59846, 0.17376),
        (0.98590, 0.50479, 0.13367), (0.96187, 0.41093, 0.09310), (0.92623, 0.32473, 0.05837), (0.88066, 0.25334, 0.03521),
        (0.82399, 0.19182, 0.01966), (0.75556, 0.13731, 0.00942), (0.67535, 0.08980, 0.00449), (0.58336, 0.04931, 0.00486),
        (0.47960, 0.01583, 0.01055),
    ];

    // matplotlib 'viridis' 256 색 표에서 17 점 균등 추출.
    private static readonly (double R, double G, double B)[] ViridisTable =
    [
        (0.26700, 0.00487, 0.32942), (0.28233, 0.09495, 0.41733), (0.27883, 0.17549, 0.48340), (0.25897, 0.25154, 0.52474),
        (0.22974, 0.32236, 0.54571), (0.19943, 0.38761, 0.55464), (0.17272, 0.44879, 0.55788), (0.14904, 0.50805, 0.55725),
        (0.12757, 0.56695, 0.55056), (0.12008, 0.62216, 0.53495), (0.15389, 0.68020, 0.50417), (0.23937, 0.73559, 0.45569),
        (0.36074, 0.78596, 0.38781), (0.50627, 0.82879, 0.30036), (0.66805, 0.86200, 0.19629), (0.83527, 0.88603, 0.10265),
        (0.99325, 0.90616, 0.14394),
    ];

    /// <summary>균등 간격 표 선형 보간(t 는 [0,1] 로 자름).</summary>
    private static (float R, float G, float B) Lerp((double R, double G, double B)[] table, double t)
    {
        double x = Math.Clamp(t, 0, 1) * (table.Length - 1);
        int i = Math.Min((int)x, table.Length - 2);
        double f = x - i;
        var a = table[i]; var b = table[i + 1];
        return ((float)(a.R + (b.R - a.R) * f), (float)(a.G + (b.G - a.G) * f), (float)(a.B + (b.B - a.B) * f));
    }

    public static (float R, float G, float B) Turbo(double t) => Lerp(TurboTable, t);

    public static (float R, float G, float B) Viridis(double t) => Lerp(ViridisTable, t);

    /// <summary>t ∈ [0,1] → 색(범위 밖은 끝색으로 자름, NaN → 자료 없음 회색). 모르는 이름은 Turbo.</summary>
    public static (float R, float G, float B) Map(string? name, double t)
    {
        if (double.IsNaN(t)) return NoData;
        return name == "Viridis" ? Viridis(t) : Turbo(t);
    }

    /// <summary>
    /// 범위 결정. zeroBased(응력·소성변형률) = 최소 0, cap = 표시 상한(안전계수 5), atLeastMax = 최대의 하한(안전계수 1.5 — 항복선이 보이게).
    /// fixedMin/Max 가 주어지면 고정 범위: 비숫자·무한은 무시(자동값 사용), 역전은 맞바꿈, 같은 값은 넓힌다(경고 기록).
    /// 값이 하나뿐이거나 모두 같으면 눈금이 서로 다르게 보이도록 범위를 넓힌다.
    /// </summary>
    public static ScaleRange Resolve(IEnumerable<double> values, bool zeroBased = false, double cap = double.PositiveInfinity,
        double atLeastMax = double.NegativeInfinity, double? fixedMin = null, double? fixedMax = null)
    {
        double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
        foreach (var v0 in values)
        {
            if (double.IsNaN(v0)) continue;
            double v = Math.Min(v0, cap);
            if (!double.IsFinite(v)) continue;
            if (v < lo) lo = v;
            if (v > hi) hi = v;
        }
        if (lo > hi) { lo = 0; hi = 0; }
        if (zeroBased) lo = Math.Min(0, lo);
        hi = Math.Max(hi, atLeastMax);
        string? warn = null;
        bool isFixed = false;
        if (fixedMin is { } a && !double.IsFinite(a)) { warn = "고정 최소가 숫자가 아님 → 자동값"; fixedMin = null; }
        if (fixedMax is { } b && !double.IsFinite(b)) { warn = (warn is null ? "" : warn + "; ") + "고정 최대가 숫자가 아님 → 자동값"; fixedMax = null; }
        if (fixedMin is { } fa) { lo = fa; isFixed = true; }
        if (fixedMax is { } fb) { hi = fb; isFixed = true; }
        if (lo > hi)
        {
            (lo, hi) = (hi, lo);
            warn = (warn is null ? "" : warn + "; ") + "최소 > 최대 → 맞바꿈";
        }
        if (hi - lo <= Math.Max(Math.Abs(hi), Math.Abs(lo)) * 1e-9 || hi - lo < 1e-300)
        {
            if (lo == 0 && hi == 0) hi = 1;
            else { double d = Math.Abs(hi) * 0.05; lo -= d; hi += d; }
            if (isFixed) warn = (warn is null ? "" : warn + "; ") + "최소 = 최대 → ±5% 로 넓힘";
        }
        return new ScaleRange(lo, hi, isFixed, warn);
    }

    /// <summary>값 → 정규화 t ∈ [0,1](+∞ → 1, −∞ → 0, NaN → NaN).</summary>
    public static double Normalize(double v, ScaleRange r)
    {
        if (double.IsNaN(v)) return double.NaN;
        if (double.IsPositiveInfinity(v)) return 1;
        if (double.IsNegativeInfinity(v)) return 0;
        return Math.Clamp((v - r.Min) / r.Span, 0, 1);
    }

    /// <summary>면 값 → 색. reverse(안전계수): 큰 값(안전)이 컬러맵 시작색, 작은 값(위험)이 끝색(빨강).</summary>
    public static (float R, float G, float B) ValueColor(string? map, double v, ScaleRange r, bool reverse = false)
    {
        double t = Normalize(v, r);
        if (double.IsNaN(t)) return NoData;
        return Map(map, reverse ? 1 - t : t);
    }

    /// <summary>레전드 막대 위치(0 = 위 = 최대, 1 = 아래 = 최소)의 값.</summary>
    public static double ValueAt(ScaleRange r, double fromTop) => r.Max - r.Span * fromTop;

    /// <summary>레전드 그라데이션 정지점 색(위에서 fromTop) — 그 높이 눈금 값의 면 색과 같다.</summary>
    public static (float R, float G, float B) LegendColor(string? map, ScaleRange r, double fromTop, bool reverse = false) =>
        ValueColor(map, ValueAt(r, fromTop), r, reverse);

    /// <summary>눈금 n 개(≥ 5, 위 = 최대 → 아래 = 최소)와 라벨. 라벨이 서로 다를 때까지 유효숫자를 늘린다.</summary>
    public static (double Value, string Label)[] Ticks(ScaleRange r, int n = 6)
    {
        n = Math.Max(n, 5);
        var v = new double[n];
        for (int i = 0; i < n; i++) v[i] = i == n - 1 ? r.Min : ValueAt(r, i / (double)(n - 1));
        for (int extra = 0; extra <= 12; extra++)
        {
            var labels = v.Select(x => Format(x, extra)).ToArray();
            if (labels.Distinct().Count() == n || extra == 12) return v.Select((x, i) => (x, labels[i])).ToArray();
        }
        return v.Select(x => (x, Format(x))).ToArray();
    }

    /// <summary>레전드 라벨 형식: 크기에 따라 F0/F1/F2/G3, 1e6 이상·1e-3 미만은 지수. extra = 추가 유효숫자.</summary>
    public static string Format(double v, int extra = 0)
    {
        var ci = CultureInfo.InvariantCulture;
        if (double.IsNaN(v)) return "—";
        if (double.IsPositiveInfinity(v)) return "∞";
        if (double.IsNegativeInfinity(v)) return "−∞";
        double a = Math.Abs(v);
        if (a == 0) return "0";
        if (a >= 1e6 || a < 1e-3) return v.ToString("0." + new string('#', 2 + extra) + "E+0", ci);
        if (a >= 1000) return v.ToString("F" + extra, ci);
        if (a >= 100) return v.ToString("F" + (1 + extra), ci);
        if (a >= 1) return v.ToString("F" + (2 + extra), ci);
        return v.ToString("G" + (3 + extra), ci);
    }
}
