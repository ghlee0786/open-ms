namespace Sim.Core.Reliability;

/// <summary>
/// 부분 순위 상관계수(PRCC, Marino·Hogue·Ray·Kirschner 2008 J. Theor. Biol. 254:178) — 단조 비선형 모델의 전역 민감도.
/// 입력·출력을 순위로 바꾼 뒤, 입력 j 와 출력을 나머지 입력 순위로 각각 선형 회귀한 잔차끼리의 상관.
/// 유의성: t = r·√((N−2−p)/(1−r²)), 자유도 N−2−p (p = 나머지 입력 수).
/// </summary>
public static class Prcc
{
    public static double[] Ranks(IReadOnlyList<double> x)
    {
        int n = x.Count;
        var idx = Enumerable.Range(0, n).OrderBy(i => x[i]).ToArray();
        var r = new double[n];
        for (int k = 0; k < n;)
        {
            int m = k;
            while (m + 1 < n && x[idx[m + 1]] == x[idx[k]]) m++;
            double avg = 0.5 * (k + m) + 1;
            for (int q = k; q <= m; q++) r[idx[q]] = avg;
            k = m + 1;
        }
        return r;
    }

    /// <summary>X[run][input], y[run] → 입력별 PRCC. 상수 입력·출력은 0.</summary>
    public static double[] Compute(IReadOnlyList<double[]> x, IReadOnlyList<double> y)
    {
        int n = y.Count;
        int p = n == 0 ? 0 : x[0].Length;
        var res = new double[p];
        if (n < p + 3) return res;
        var ry = Ranks(y);
        var rx = new double[p][];
        for (int j = 0; j < p; j++) rx[j] = Ranks(x.Select(r => r[j]).ToList());
        if (Variance(ry) < 1e-12) return res;
        for (int j = 0; j < p; j++)
        {
            if (Variance(rx[j]) < 1e-12) continue;
            var others = Enumerable.Range(0, p).Where(k => k != j && Variance(rx[k]) > 1e-12).ToArray();
            var ex = Residual(rx[j], rx, others);
            var ey = Residual(ry, rx, others);
            res[j] = Corr(ex, ey);
        }
        return res;
    }

    /// <summary>PRCC 유의 확률(양측, t 분포 정규 근사 — N ≥ 50 이면 충분).</summary>
    public static double PValue(double r, int n, int p)
    {
        int df = n - 2 - (p - 1);
        if (df <= 0 || Math.Abs(r) >= 1) return Math.Abs(r) >= 1 ? 0 : 1;
        double t = r * Math.Sqrt(df / (1 - r * r));
        return 2 * (1 - Analysis.DispersionDef.NormCdf(Math.Abs(t)));
    }

    private static double Variance(double[] v)
    {
        double m = v.Average();
        return v.Sum(x => (x - m) * (x - m)) / v.Length;
    }

    private static double Corr(double[] a, double[] b)
    {
        double ma = a.Average(), mb = b.Average(), sab = 0, saa = 0, sbb = 0;
        for (int i = 0; i < a.Length; i++) { sab += (a[i] - ma) * (b[i] - mb); saa += (a[i] - ma) * (a[i] - ma); sbb += (b[i] - mb) * (b[i] - mb); }
        return saa > 0 && sbb > 0 ? sab / Math.Sqrt(saa * sbb) : 0;
    }

    /// <summary>y 를 [1, X_cols] 로 최소제곱 회귀한 잔차(정규방정식 + 부분 피벗 가우스 소거).</summary>
    private static double[] Residual(double[] y, double[][] x, int[] cols)
    {
        int n = y.Length, m = cols.Length + 1;
        var a = new double[m, m];
        var b = new double[m];
        double Col(int c, int i) => c == 0 ? 1 : x[cols[c - 1]][i];
        for (int r = 0; r < m; r++)
        {
            for (int c = 0; c < m; c++)
            {
                double s = 0;
                for (int i = 0; i < n; i++) s += Col(r, i) * Col(c, i);
                a[r, c] = s;
            }
            double sb = 0;
            for (int i = 0; i < n; i++) sb += Col(r, i) * y[i];
            b[r] = sb;
        }
        var beta = Solve(a, b);
        var res = new double[n];
        for (int i = 0; i < n; i++)
        {
            double f = 0;
            for (int c = 0; c < m; c++) f += beta[c] * Col(c, i);
            res[i] = y[i] - f;
        }
        return res;
    }

    private static double[] Solve(double[,] a, double[] b)
    {
        int m = b.Length;
        for (int k = 0; k < m; k++)
        {
            int piv = k;
            for (int r = k + 1; r < m; r++) if (Math.Abs(a[r, k]) > Math.Abs(a[piv, k])) piv = r;
            if (Math.Abs(a[piv, k]) < 1e-12) continue;
            if (piv != k)
            {
                for (int c = 0; c < m; c++) (a[k, c], a[piv, c]) = (a[piv, c], a[k, c]);
                (b[k], b[piv]) = (b[piv], b[k]);
            }
            for (int r = k + 1; r < m; r++)
            {
                double f = a[r, k] / a[k, k];
                for (int c = k; c < m; c++) a[r, c] -= f * a[k, c];
                b[r] -= f * b[k];
            }
        }
        var x = new double[m];
        for (int k = m - 1; k >= 0; k--)
        {
            if (Math.Abs(a[k, k]) < 1e-12) { x[k] = 0; continue; }
            double s = b[k];
            for (int c = k + 1; c < m; c++) s -= a[k, c] * x[c];
            x[k] = s / a[k, k];
        }
        return x;
    }
}
