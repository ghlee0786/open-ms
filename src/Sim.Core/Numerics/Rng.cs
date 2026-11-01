namespace Sim.Core.Numerics;

/// <summary>
/// 결정적 난수(xoshiro256**, SplitMix64 시딩). .NET 버전과 무관하게 같은 시드 → 같은 수열(FR-NUM-05).
/// </summary>
public sealed class Rng
{
    private ulong _s0, _s1, _s2, _s3;
    private double _spare;
    private bool _hasSpare;

    public Rng(ulong seed) => Reseed(seed);

    /// <summary>마스터 시드와 실행/스트림 번호로 독립 스트림 생성.</summary>
    public static Rng ForStream(ulong masterSeed, ulong stream) => new(Mix(masterSeed ^ Mix(stream + 0x9E3779B97F4A7C15UL)));

    public void Reseed(ulong seed)
    {
        ulong x = seed;
        _s0 = SplitMix(ref x); _s1 = SplitMix(ref x); _s2 = SplitMix(ref x); _s3 = SplitMix(ref x);
        _hasSpare = false;
    }

    public static ulong Mix(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    private static ulong SplitMix(ref ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        return Mix(x);
    }

    private static ulong Rotl(ulong x, int k) => (x << k) | (x >> (64 - k));

    public ulong NextULong()
    {
        ulong result = Rotl(_s1 * 5, 7) * 9;
        ulong t = _s1 << 17;
        _s2 ^= _s0; _s3 ^= _s1; _s1 ^= _s2; _s0 ^= _s3;
        _s2 ^= t; _s3 = Rotl(_s3, 45);
        return result;
    }

    /// <summary>[0,1) 균등.</summary>
    public double NextDouble() => (NextULong() >> 11) * (1.0 / 9007199254740992.0);

    /// <summary>표준정규 (Box–Muller).</summary>
    public double NextGaussian()
    {
        if (_hasSpare) { _hasSpare = false; return _spare; }
        double u1;
        do u1 = NextDouble(); while (u1 <= 1e-300);
        double u2 = NextDouble();
        double r = Math.Sqrt(-2 * Math.Log(u1));
        _spare = r * Math.Sin(2 * Math.PI * u2);
        _hasSpare = true;
        return r * Math.Cos(2 * Math.PI * u2);
    }
}

public static class Stat
{
    /// <summary>표준정규 역누적분포 (Acklam 근사, 상대오차 ~1e-9).</summary>
    public static double NormInv(double p)
    {
        if (p <= 0) return double.NegativeInfinity;
        if (p >= 1) return double.PositiveInfinity;
        double[] a = [-3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02, 1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00];
        double[] b = [-5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02, 6.680131188771972e+01, -1.328068155288572e+01];
        double[] c = [-7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00, -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00];
        double[] d = [7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00, 3.754408661907416e+00];
        const double plow = 0.02425, phigh = 1 - plow;
        double q, r;
        if (p < plow)
        {
            q = Math.Sqrt(-2 * Math.Log(p));
            return (((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
        }
        if (p <= phigh)
        {
            q = p - 0.5; r = q * q;
            return (((((a[0] * r + a[1]) * r + a[2]) * r + a[3]) * r + a[4]) * r + a[5]) * q / (((((b[0] * r + b[1]) * r + b[2]) * r + b[3]) * r + b[4]) * r + 1);
        }
        q = Math.Sqrt(-2 * Math.Log(1 - p));
        return -(((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
    }
}
