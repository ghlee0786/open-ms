using Sim.Core.Numerics;

namespace Sim.Core.Environment;

/// <summary>고도별 평균풍 표의 한 층. 풍향은 기상 관례(불어오는 방향, 북=0° 시계방향).</summary>
public readonly record struct WindLayer(double AltitudeAgl, double Speed, double DirectionFromDeg);

/// <summary>
/// 평균풍(FR-ENV-03): 고도별 표 보간, 또는 기준고도 풍속 + 로그 전단(z₀) / 멱법칙.
/// </summary>
public sealed class MeanWind
{
    public double Speed { get; init; }
    public double DirectionFromDeg { get; init; }
    public double ReferenceHeight { get; init; } = 10;
    /// <summary>"log" | "power" | "none"</summary>
    public string Shear { get; init; } = "log";
    public double Roughness { get; init; } = 0.03;
    public double PowerExponent { get; init; } = 1.0 / 7;
    /// <summary>전단 적용 상한 고도(이 위로는 일정).</summary>
    public double ShearTop { get; init; } = 300;
    public IReadOnlyList<WindLayer>? Profile { get; init; }

    public static Vec3 FromSpeedDir(double speed, double dirFromDeg)
    {
        double d = dirFromDeg * Math.PI / 180;
        return new Vec3(-speed * Math.Cos(d), -speed * Math.Sin(d), 0);
    }

    public Vec3 At(double agl)
    {
        if (Profile is { Count: > 0 } p)
        {
            if (agl <= p[0].AltitudeAgl) return FromSpeedDir(p[0].Speed, p[0].DirectionFromDeg);
            for (int i = 1; i < p.Count; i++)
            {
                if (agl <= p[i].AltitudeAgl)
                {
                    double f = (agl - p[i - 1].AltitudeAgl) / (p[i].AltitudeAgl - p[i - 1].AltitudeAgl);
                    var a = FromSpeedDir(p[i - 1].Speed, p[i - 1].DirectionFromDeg);
                    var b = FromSpeedDir(p[i].Speed, p[i].DirectionFromDeg);
                    return a + (b - a) * f;
                }
            }
            var l = p[^1];
            return FromSpeedDir(l.Speed, l.DirectionFromDeg);
        }
        return FromSpeedDir(Speed * ShearFactor(agl), DirectionFromDeg);
    }

    public double ShearFactor(double agl)
    {
        double h = Math.Min(Math.Max(agl, 0), ShearTop);
        switch (Shear)
        {
            case "log":
                if (h <= Roughness) return 0;
                return Math.Log(h / Roughness) / Math.Log(ReferenceHeight / Roughness);
            case "power":
                return h <= 0 ? 0 : Math.Pow(h / ReferenceHeight, PowerExponent);
            default:
                return 1;
        }
    }
}

/// <summary>
/// Dryden 난류(MIL-F-8785C 저고도 규격, FR-ENV-04). 시간영역 이산 필터.
/// u 성분은 Dryden 1차 스펙트럼과 정확히 일치, v·w 는 같은 분산·길이척도의 1차 근사(POC 가정).
/// 출력은 NED 성분(u→N, v→E, w→D)으로 적용한다(가정 목록 기록).
/// </summary>
public sealed class DrydenTurbulence
{
    private readonly double _w20; // 6 m 고도 풍속 [m/s]
    private double _xu, _xv, _xw;
    private readonly Rng _rng;

    public DrydenTurbulence(string intensity, Rng rng)
    {
        _rng = rng;
        Intensity = intensity;
        _w20 = intensity switch
        {
            "light" => 15 * 0.514444,
            "moderate" => 30 * 0.514444,
            "severe" => 45 * 0.514444,
            _ => 0
        };
    }

    public string Intensity { get; }
    public bool Enabled => _w20 > 0;
    public Vec3 Velocity => new(_xu, _xv, _xw);

    /// <summary>고도·대기속도에서의 (σ_u, σ_w, L_u, L_w) [m/s, m].</summary>
    public (double SigU, double SigW, double Lu, double Lw) Scales(double agl)
    {
        const double ft = 0.3048;
        double hFt = Math.Clamp(agl / ft, 10, 2000);
        double sigWLow = 0.1 * _w20;
        double denom = 0.177 + 0.000823 * Math.Min(hFt, 1000);
        double sigULow = sigWLow / Math.Pow(denom, 0.4);
        double luLow = Math.Min(hFt, 1000) / Math.Pow(denom, 1.2) * ft;
        double lwLow = Math.Min(hFt, 1000) * ft;
        if (hFt <= 1000) return (sigULow, sigWLow, luLow, lwLow);
        // 1000~2000 ft: 중고도(σ 등방, L=1750 ft)로 선형 전이
        double sigHigh = Intensity switch { "light" => 1.5, "moderate" => 3.0, _ => 6.0 };
        double f = (hFt - 1000) / 1000;
        double lHigh = 1750 * ft;
        return (sigULow + (sigHigh - sigULow) * f, sigWLow + (sigHigh - sigWLow) * f,
                luLow + (lHigh - luLow) * f, lwLow + (lHigh - lwLow) * f);
    }

    public void Step(double dt, double agl, double airspeed)
    {
        if (!Enabled || dt <= 0) return;
        double v = Math.Max(airspeed, 1.0);
        var (su, sw, lu, lw) = Scales(agl);
        double au = Math.Exp(-v * dt / lu), aw = Math.Exp(-v * dt / lw);
        _xu = au * _xu + su * Math.Sqrt(1 - au * au) * _rng.NextGaussian();
        _xv = au * _xv + su * Math.Sqrt(1 - au * au) * _rng.NextGaussian();
        _xw = aw * _xw + sw * Math.Sqrt(1 - aw * aw) * _rng.NextGaussian();
    }
}
