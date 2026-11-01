namespace Sim.Core.Environment;

public readonly record struct AtmState(double Temperature, double Pressure, double Density, double SpeedOfSound, double Viscosity);

/// <summary>
/// US Standard Atmosphere 1976 (0~71 km 지오퍼텐셜, 7개 층 중 하위 6개).
/// 기본은 기하고도 ≈ 지오퍼텐셜 근사(작전 고도 3 km 에서 밀도 차 &lt; 0.05 %, 10 km 0.2 %) — 기존 결과·GPU 경로와 동일.
/// Geopotential=true 이면 기하고도(해발) → H = r0·h/(r0+h) 로 변환(표와 일치, NASA check-case·고고도 비교용).
/// 지상 온도 오프셋(ΔT) 적용 시 표준 압력 분포는 유지하고 밀도는 ρ = P/(R·(T+ΔT)) 로 계산한다(FR-ENV-01).
/// 32~71 km 층은 고고도 낙하산 비행시험(TM X-1451/1414, 37~39 km) 비교용으로 2026-10-07 추가.
/// </summary>
public sealed class Atmosphere
{
    public const double R = 287.05287, Gamma = 1.4, G0 = 9.80665;
    /// <summary>US1976 지오퍼텐셜 변환 지구 반경 [m].</summary>
    public const double R0 = 6356766.0;

    /// <summary>기하고도를 지오퍼텐셜로 변환해 표를 찾는다(기본 false = 근사).</summary>
    public bool Geopotential { get; init; }
    public double TemperatureOffset { get; init; }
    /// <summary>진공(검증용 VV-01).</summary>
    public bool Vacuum { get; init; }
    /// <summary>고정 밀도(검증용 VV-02). null 이면 ISA.</summary>
    public double? ConstantDensity { get; init; }

    public AtmState At(double altitudeMsl)
    {
        if (Vacuum) return new AtmState(288.15, 0, 0, 340.29, 1.789e-5);
        var (tStd, p) = Standard(Geopotential ? GeopotentialOf(altitudeMsl) : altitudeMsl);
        double t = tStd + TemperatureOffset;
        double rho = ConstantDensity ?? p / (R * t);
        double a = Math.Sqrt(Gamma * R * t);
        double mu = 1.458e-6 * Math.Pow(t, 1.5) / (t + 110.4);
        return new AtmState(t, p, rho, a, mu);
    }

    // 층: 기저 지오퍼텐셜 고도[m], 기저 온도[K], 기온감률[K/m], 기저 압력[Pa]
    private static readonly (double H, double T, double L, double P)[] Layers =
    [
        (0, 288.15, -0.0065, 101325.0),
        (11000, 216.65, 0.0, 22632.06),
        (20000, 216.65, 0.001, 5474.889),
        (32000, 228.65, 0.0028, 868.0187),
        (47000, 270.65, 0.0, 110.9063),
        (51000, 270.65, -0.0028, 66.93887),
    ];

    /// <summary>기하고도 h[m] → 지오퍼텐셜 고도[m].</summary>
    public static double GeopotentialOf(double h) => R0 * h / (R0 + h);

    /// <summary>표준 온도·압력. hp = 지오퍼텐셜 고도 [m]. 71 km 위는 마지막 층 외삽.</summary>
    public static (double T, double P) Standard(double hp)
    {
        int i = Layers.Length - 1;
        while (i > 0 && hp < Layers[i].H) i--;
        var (hb, tb, l, pb) = Layers[i];
        double dh = hp - hb;
        if (l == 0) return (tb, pb * Math.Exp(-G0 * dh / (R * tb)));
        double t = tb + l * dh;
        return (t, pb * Math.Pow(t / tb, -G0 / (R * l)));
    }
}
