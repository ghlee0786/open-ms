namespace Sim.Core.Analysis;

/// <summary>
/// GPU Monte Carlo 용 단순화 모델 입력(단일 물체 + 낙하산, 3DOF 점질량). blittable 구조체.
/// </summary>
public struct DropParams
{
    public double Mass;
    /// <summary>물체 항력면적 Cd·S [m²].</summary>
    public double CdA;
    /// <summary>낙하산 정상 항력면적 (CdS)₀ [m²], 0 이면 낙하산 없음.</summary>
    public double ChuteCdS;
    /// <summary>투하 후 전개 명령까지 시간 [s].</summary>
    public double ChuteDelay;
    public double ChuteLineStretch;
    /// <summary>충만 거리 n·D₀ [m] (t_f = n·D₀ / V_s).</summary>
    public double ChuteFillDistance;
    public int ChuteFillExponent;
    public double ChuteInitialFill;
    public double N0, E0, Alt0, Vn0, Ve0, Vd0;
    /// <summary>기준고도 평균풍(NED 성분) [m/s].</summary>
    public double WindN, WindE;
    public double WindRefHeight, WindRoughness, WindShearTop;
    public double TemperatureOffset, GroundAltitude, Gravity;
    public double Dt, TMax;
}

public struct DropResult
{
    public double N, E, T, Speed, MaxLoadG, MaxChuteForce;
    public int Landed;
}

/// <summary>
/// 단순 투하 모델. CPU·GPU(ILGPU 커널)가 이 정적 함수를 그대로 공유한다(PRD S절 검증 원칙).
/// 커널 호환을 위해 사칙연산·Math.Sqrt 만 쓰고 exp/log 는 자체 구현.
/// </summary>
public static class DropModel
{
    private const double Ln2 = 0.69314718055994530942;

    public static double Log(double x)
    {
        if (x <= 0) return -1e300;
        int k = 0;
        while (x >= 2) { x *= 0.5; k++; }
        while (x < 1) { x *= 2; k--; }
        double z = (x - 1) / (x + 1), z2 = z * z, term = z, sum = 0;
        for (int n = 1; n < 40; n += 2) { sum += term / n; term *= z2; } // |z|≤1/3 → 20항이면 1e-19
        return 2 * sum + k * Ln2;
    }

    public static double Exp(double x)
    {
        if (x < -700) return 0;
        if (x > 700) x = 700;
        double kd = x / Ln2;
        int k = (int)(kd >= 0 ? kd + 0.5 : kd - 0.5);
        double r = x - k * Ln2;
        double term = 1, sum = 1;
        for (int n = 1; n < 18; n++) { term *= r / n; sum += term; } // |r|≤ln2/2 → 1e-19
        if (k > 0) for (int i = 0; i < k; i++) sum *= 2;
        else for (int i = 0; i < -k; i++) sum *= 0.5;
        return sum;
    }

    public static double Pow(double a, double b) => a <= 0 ? 0 : Exp(b * Log(a));

    /// <summary>ISA 밀도(대류권/성층권 하부) — Atmosphere 기본 설정(기하고도≈지오퍼텐셜)과 같은 식.</summary>
    public static double Density(double hMsl, double dT)
    {
        double t, p;
        if (hMsl < 11000)
        {
            t = 288.15 - 0.0065 * hMsl;
            p = 101325.0 * Pow(t / 288.15, 5.255876);
        }
        else
        {
            t = 216.65;
            p = 22632.06 * Exp(-0.000157688 * (hMsl - 11000));
        }
        return p / (287.05287 * (t + dT));
    }

    public static double Shear(double agl, double href, double z0, double top)
    {
        double h = agl < 0 ? 0 : (agl > top ? top : agl);
        if (h <= z0) return 0;
        return Log(h / z0) / Log(href / z0);
    }

    private static double Fill(in DropParams p, double t, double tf)
    {
        double ts = p.ChuteDelay + p.ChuteLineStretch;
        if (p.ChuteCdS <= 0 || t < ts) return 0;
        double tau = (t - ts) / tf;
        if (tau > 1) tau = 1;
        double f = 1;
        for (int i = 0; i < p.ChuteFillExponent; i++) f *= tau;
        return f > p.ChuteInitialFill ? f : p.ChuteInitialFill;
    }

    /// <summary>밀도·풍 전단은 스텝 시작 값으로 고정(스텝당 고도 변화 ≪ 1 m, GPU 비용 절감).</summary>
    private static void Deriv(in DropParams p, double t, double tf, double rho, double s, double vn, double ve, double vd,
        out double an, out double ae, out double ad, out double chuteForce)
    {
        double wn = p.WindN * s, we = p.WindE * s;
        double rn = vn - wn, re = ve - we, rd = vd;
        double v = Math.Sqrt(rn * rn + re * re + rd * rd);
        double chute = p.ChuteCdS * Fill(p, t, tf);
        double k = 0.5 * rho * v * (p.CdA + chute) / p.Mass;
        an = -k * rn; ae = -k * re; ad = p.Gravity - k * rd;
        chuteForce = 0.5 * rho * v * v * chute;
    }

    public static DropResult Simulate(DropParams p)
    {
        double n = p.N0, e = p.E0, d = -(p.Alt0 + p.GroundAltitude);
        double vn = p.Vn0, ve = p.Ve0, vd = p.Vd0;
        double t = 0, h = p.Dt, groundD = -p.GroundAltitude;
        double tf = 1e9;
        bool stretched = false;
        double maxG = 0, maxF = 0;
        var r = new DropResult();
        while (t < p.TMax)
        {
            if (!stretched && p.ChuteCdS > 0 && t >= p.ChuteDelay + p.ChuteLineStretch)
            {
                double s0 = Shear(-d - p.GroundAltitude, p.WindRefHeight, p.WindRoughness, p.WindShearTop);
                double rn0 = vn - p.WindN * s0, re0 = ve - p.WindE * s0;
                double vs = Math.Sqrt(rn0 * rn0 + re0 * re0 + vd * vd);
                tf = p.ChuteFillDistance / (vs > 1 ? vs : 1);
                stretched = true;
            }
            double rho = Density(-d, p.TemperatureOffset);
            double sh = Shear(-d - p.GroundAltitude, p.WindRefHeight, p.WindRoughness, p.WindShearTop);
            Deriv(p, t, tf, rho, sh, vn, ve, vd, out var a1n, out var a1e, out var a1d, out var f1);
            double h2 = h * 0.5;
            Deriv(p, t + h2, tf, rho, sh, vn + a1n * h2, ve + a1e * h2, vd + a1d * h2, out var a2n, out var a2e, out var a2d, out _);
            double v2n = vn + a1n * h2, v2e = ve + a1e * h2, v2d = vd + a1d * h2;
            Deriv(p, t + h2, tf, rho, sh, vn + a2n * h2, ve + a2e * h2, vd + a2d * h2, out var a3n, out var a3e, out var a3d, out _);
            double v3n = vn + a2n * h2, v3e = ve + a2e * h2, v3d = vd + a2d * h2;
            Deriv(p, t + h, tf, rho, sh, vn + a3n * h, ve + a3e * h, vd + a3d * h, out var a4n, out var a4e, out var a4d, out _);
            double v4n = vn + a3n * h, v4e = ve + a3e * h, v4d = vd + a3d * h;

            double nn = n + h / 6 * (vn + 2 * v2n + 2 * v3n + v4n);
            double ne = e + h / 6 * (ve + 2 * v2e + 2 * v3e + v4e);
            double nd = d + h / 6 * (vd + 2 * v2d + 2 * v3d + v4d);
            double nvn = vn + h / 6 * (a1n + 2 * a2n + 2 * a3n + a4n);
            double nve = ve + h / 6 * (a1e + 2 * a2e + 2 * a3e + a4e);
            double nvd = vd + h / 6 * (a1d + 2 * a2d + 2 * a3d + a4d);

            double lgn = a1n, lge = a1e, lgd = a1d - p.Gravity;
            double g = Math.Sqrt(lgn * lgn + lge * lge + lgd * lgd) / p.Gravity;
            if (g > maxG) maxG = g;
            if (f1 > maxF) maxF = f1;

            if (nd >= groundD)
            {
                double f = (groundD - d) / (nd - d);
                r.N = n + (nn - n) * f; r.E = e + (ne - e) * f; r.T = t + h * f;
                double sn = vn + (nvn - vn) * f, se = ve + (nve - ve) * f, sd = vd + (nvd - vd) * f;
                r.Speed = Math.Sqrt(sn * sn + se * se + sd * sd);
                r.Landed = 1; r.MaxLoadG = maxG; r.MaxChuteForce = maxF;
                return r;
            }
            n = nn; e = ne; d = nd; vn = nvn; ve = nve; vd = nvd; t += h;
        }
        r.N = n; r.E = e; r.T = t; r.Speed = Math.Sqrt(vn * vn + ve * ve + vd * vd); r.Landed = 0; r.MaxLoadG = maxG; r.MaxChuteForce = maxF;
        return r;
    }
}
