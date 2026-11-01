namespace Sim.Core.Reliability;

/// <summary>
/// 격자 수렴 지수(GCI) — Celik, Ghia, Roache, Freitas, Coleman, Raad, J. Fluids Eng. 130(7) 078001 (2008) 절차
/// (tools/cfd/post.py 의 gci 와 같은 식). h1 &lt; h2 &lt; h3 (1 = 가장 촘촘), φ = 해당 해.
/// 겉보기 차수 p(고정점 반복), Richardson 외삽값, 근사 상대오차 e_a, GCI_fine = Fs·e_a/(r21^p − 1).
/// 진동·발산 수렴 또는 p 가 [0.5, 4] 밖이면 이론 차수(기본 2)와 안전계수 3.0(Roache)으로 보수적 대체.
/// </summary>
public sealed record GciResult(double R21, double R32, double P, double PUsed, double Fs, double PhiExt, double ApproxRelError, double Gci21, string Convergence, string Note);

public static class Gci
{
    public static GciResult Compute(double h1, double h2, double h3, double f1, double f2, double f3, double pTheory = 2.0)
    {
        double r21 = h2 / h1, r32 = h3 / h2, e21 = f2 - f1, e32 = f3 - f2;
        if (e21 == 0 || e32 == 0) return new GciResult(r21, r32, double.NaN, pTheory, 1.25, f1, 0, 0, "exact", "");
        double ratio = e32 / e21;
        double s = ratio > 0 ? 1 : -1;
        string conv = ratio > 0 && Math.Abs(ratio) > 1 ? "monotone" : ratio < 0 ? "oscillatory" : "divergent";
        double p = pTheory;
        for (int k = 0; k < 200; k++)
        {
            double a = Math.Pow(r21, p) - s, b = Math.Pow(r32, p) - s;
            double q = a > 0 && b > 0 ? Math.Log(a / b) : 0;
            double pn = Math.Abs(Math.Log(Math.Abs(ratio)) + q) / Math.Log(r21);
            if (Math.Abs(pn - p) < 1e-10) { p = pn; break; }
            p = pn;
        }
        double fs = 1.25, pUsed = p;
        string note = "";
        if (conv != "monotone" || !(p >= 0.5 && p <= 4.0))
        {
            note = $"{conv}, p={p:G3} → 이론 차수 {pTheory:G2}·안전계수 3.0 대체";
            pUsed = pTheory; fs = 3.0;
        }
        double ea = f1 != 0 ? Math.Abs((f1 - f2) / f1) : double.PositiveInfinity;
        double ext = (Math.Pow(r21, pUsed) * f1 - f2) / (Math.Pow(r21, pUsed) - 1);
        return new GciResult(r21, r32, p, pUsed, fs, ext, ea, fs * ea / (Math.Pow(r21, pUsed) - 1), conv, note);
    }
}
