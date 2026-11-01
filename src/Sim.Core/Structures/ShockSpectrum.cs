namespace Sim.Core.Structures;

/// <summary>
/// 충격 응답 스펙트럼(SRS, Shock Response Spectrum) — 기저 가속도 a(t) 를 받는 1자유도(질량–스프링–감쇠) 장착물의 절대 가속도 응답 최대값.
/// 디지털 필터 = Smallwood(1981) 램프 불변(ramp-invariant) 재귀식(절대 가속도):
///   E = e^(−ζωₙΔt), K = ω_dΔt, C = E cos K, S = E sin K, S' = S/K
///   y[i] = 2C·y[i−1] − E²·y[i−2] + (1 − S')·x[i] + 2(S' − C)·x[i−1] + (E² − S')·x[i−2]
/// (ISO 18431-4 · MIL-STD-810H 516.8 부록 · Irvine "An Introduction to the Shock Response Spectrum" 과 같은 식). 입력은 균일 간격,
/// Δt ≤ 1/(10 fₙ) 권장. 동적 증폭(DAF) = SRS(fₙ) / max|a|. 검증: 계단 입력 무감쇠 → 2, 반 사인 펄스 → 최대 ≈ 1.77(fₙT ≈ 0.8).
/// </summary>
public static class ShockSpectrum
{
    /// <summary>품질 계수 Q = 10 (ζ = 5 %) — 충격 시험 규격 관례(MIL-STD-810H 516, NASA-STD-7003A).</summary>
    public const double DefaultZeta = 0.05;

    /// <summary>절대 가속도 응답 시간 이력(초기 정지, 같은 길이).</summary>
    public static double[] Response(ReadOnlySpan<double> a, double dt, double fn, double zeta = DefaultZeta)
    {
        var y = new double[a.Length];
        if (a.Length == 0 || !(fn > 0) || !(dt > 0)) return y;
        double wn = 2 * Math.PI * fn, wd = wn * Math.Sqrt(Math.Max(1 - zeta * zeta, 1e-12));
        double e = Math.Exp(-zeta * wn * dt), k = wd * dt, c = e * Math.Cos(k), s = e * Math.Sin(k), sp = s / k;
        double b0 = 1 - sp, b1 = 2 * (sp - c), b2 = e * e - sp, a1 = 2 * c, a2 = -e * e;
        double y1 = 0, y2 = 0, x1 = 0, x2 = 0;
        for (int i = 0; i < a.Length; i++)
        {
            double x = a[i];
            double v = a1 * y1 + a2 * y2 + b0 * x + b1 * x1 + b2 * x2;
            y[i] = v;
            y2 = y1; y1 = v; x2 = x1; x1 = x;
        }
        return y;
    }

    /// <summary>최대 절대 가속도 응답(maximax) — 입력 단위 그대로.</summary>
    public static double Maximax(ReadOnlySpan<double> a, double dt, double fn, double zeta = DefaultZeta)
    {
        var y = Response(a, dt, fn, zeta);
        double m = 0;
        foreach (var v in y) m = Math.Max(m, Math.Abs(v));
        return m;
    }

    /// <summary>
    /// 3축 기저 가속도(같은 fₙ 등방 장착)의 응답 벡터 크기 최대 — 성분(축 x, 횡 yz, 벡터 전체)별: (axial, lateral, vector).
    /// </summary>
    public static (double Axial, double Lateral, double Vector) Maximax3(double[] ax, double[] ay, double[] az, double dt, double fn, double zeta = DefaultZeta)
    {
        var rx = Response(ax, dt, fn, zeta); var ry = Response(ay, dt, fn, zeta); var rz = Response(az, dt, fn, zeta);
        double mA = 0, mL = 0, mV = 0;
        for (int i = 0; i < rx.Length; i++)
        {
            double l2 = ry[i] * ry[i] + rz[i] * rz[i];
            mA = Math.Max(mA, Math.Abs(rx[i]));
            mL = Math.Max(mL, Math.Sqrt(l2));
            mV = Math.Max(mV, Math.Sqrt(l2 + rx[i] * rx[i]));
        }
        return (mA, mL, mV);
    }

    /// <summary>
    /// 2자유도 사슬(기저 → k₁ → m₁ → k₂ → m₂, 감쇠 c₁ = 2ζ√(k₁(m₁+m₂)), c₂ = 2ζ√(k₂m₂)) 의 m₂ 절대 가속도 최대 — 3축(같은 강성).
    /// 상대 좌표 z = x − x_b: M z̈ + C ż + K z = −M·1·a_b, Newmark 평균 가속도(무조건 안정, 2×2 유효 강성 역행렬 한 번).
    /// </summary>
    public static (double Axial, double Lateral, double Vector) Chain2Maximax3(double[] ax, double[] ay, double[] az, double dt,
        double m1, double k1, double m2, double k2, double zeta = DefaultZeta)
    {
        var rx = Chain2(ax, dt, m1, k1, m2, k2, zeta); var ry = Chain2(ay, dt, m1, k1, m2, k2, zeta); var rz = Chain2(az, dt, m1, k1, m2, k2, zeta);
        double mA = 0, mL = 0, mV = 0;
        for (int i = 0; i < rx.Length; i++)
        {
            double l2 = ry[i] * ry[i] + rz[i] * rz[i];
            mA = Math.Max(mA, Math.Abs(rx[i])); mL = Math.Max(mL, Math.Sqrt(l2)); mV = Math.Max(mV, Math.Sqrt(l2 + rx[i] * rx[i]));
        }
        return (mA, mL, mV);
    }

    /// <summary>2자유도 사슬 m₂ 절대 가속도 시간 이력(초기 정지).</summary>
    public static double[] Chain2(ReadOnlySpan<double> a, double dt, double m1, double k1, double m2, double k2, double zeta = DefaultZeta)
    {
        var y = new double[a.Length];
        if (a.Length == 0) return y;
        double c1 = 2 * zeta * Math.Sqrt(k1 * (m1 + m2)), c2 = 2 * zeta * Math.Sqrt(k2 * m2);
        double a0 = 4 / (dt * dt), a1 = 2 / dt, a2 = 4 / dt;
        // 유효 강성 K + (2/Δt)C + (4/Δt²)M, K = [[k1+k2, −k2], [−k2, k2]], C 같은 꼴
        double e11 = k1 + k2 + a1 * (c1 + c2) + a0 * m1, e12 = -k2 - a1 * c2, e22 = k2 + a1 * c2 + a0 * m2;
        double det = e11 * e22 - e12 * e12;
        double i11 = e22 / det, i12 = -e12 / det, i22 = e11 / det;
        double u1 = 0, u2 = 0, v1 = 0, v2 = 0, w1 = -a[0], w2 = -a[0];   // 상대 변위·속도·가속도(초기 M ü = −M a_b)
        y[0] = a[0] + w2;
        for (int i = 1; i < a.Length; i++)
        {
            double f1 = -m1 * a[i], f2 = -m2 * a[i];
            double cv1 = a1 * u1 + v1, cv2 = a1 * u2 + v2;
            double r1 = f1 + m1 * (a0 * u1 + a2 * v1 + w1) + (c1 + c2) * cv1 - c2 * cv2;
            double r2 = f2 + m2 * (a0 * u2 + a2 * v2 + w2) - c2 * cv1 + c2 * cv2;
            double n1 = i11 * r1 + i12 * r2, n2 = i12 * r1 + i22 * r2;
            double nw1 = a0 * (n1 - u1) - a2 * v1 - w1, nw2 = a0 * (n2 - u2) - a2 * v2 - w2;
            v1 += dt / 2 * (w1 + nw1); v2 += dt / 2 * (w2 + nw2);
            w1 = nw1; w2 = nw2; u1 = n1; u2 = n2;
            y[i] = a[i] + w2;
        }
        return y;
    }

    /// <summary>스펙트럼: fmin~fmax 를 옥타브당 perOctave 점(로그 간격). 반환 (주파수, SRS).</summary>
    public static (double[] F, double[] Srs) Spectrum(double[] a, double dt, double fmin = 5, double fmax = 1000, int perOctave = 6, double zeta = DefaultZeta)
    {
        int n = Math.Max(2, (int)Math.Ceiling(Math.Log2(fmax / fmin) * perOctave) + 1);
        var f = new double[n]; var s = new double[n];
        for (int i = 0; i < n; i++)
        {
            f[i] = fmin * Math.Pow(fmax / fmin, i / (double)(n - 1));
            s[i] = Maximax(a, dt, f[i], zeta);
        }
        return (f, s);
    }

    /// <summary>
    /// 해석해(검증·설명용): 무감쇠 1자유도의 선형 램프(상승 시간 t_r) → 일정 가속도 입력 동적 증폭 DAF = 1 + |sin(π fₙ t_r)|/(π fₙ t_r).
    /// 낙하산 개방처럼 상승 시간이 장착 주기보다 길면(fₙ t_r ≫ 1) DAF → 1(준정적).
    /// </summary>
    public static double RampDaf(double riseTime, double fn)
    {
        double x = Math.PI * fn * riseTime;
        return x < 1e-9 ? 2 : 1 + Math.Abs(Math.Sin(x)) / x;
    }

    /// <summary>비균일 시각 (t, v) 를 [t0, t1] 균일 간격 dt 로 선형 보간(구간 밖 = 끝값 0 아님 — 첫/끝 값 유지).</summary>
    public static double[] Resample(IReadOnlyList<double> t, IReadOnlyList<double> v, double t0, double t1, double dt)
    {
        int n = Math.Max(1, (int)Math.Floor((t1 - t0) / dt) + 1);
        var r = new double[n];
        int j = 0;
        for (int i = 0; i < n; i++)
        {
            double ti = t0 + i * dt;
            while (j + 1 < t.Count && t[j + 1] < ti) j++;
            if (t.Count == 0) break;
            if (ti <= t[0]) { r[i] = v[0]; continue; }
            if (j + 1 >= t.Count) { r[i] = v[^1]; continue; }
            double span = t[j + 1] - t[j];
            double u = span > 1e-15 ? Math.Clamp((ti - t[j]) / span, 0, 1) : 1;
            r[i] = v[j] + (v[j + 1] - v[j]) * u;
        }
        return r;
    }
}
