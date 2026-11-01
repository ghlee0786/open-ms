using System.Text.Json.Nodes;

namespace Sim.Core.Analysis;

/// <summary>착탄 산포 통계(FR-MC-04): MPI, CEP(50%), R95, 95% 오차 타원.</summary>
public sealed class ImpactStats
{
    public int Count { get; init; }
    public double MeanN { get; init; }
    public double MeanE { get; init; }
    public double Cep50 { get; init; }
    public double R95 { get; init; }
    public double SigmaN { get; init; }
    public double SigmaE { get; init; }
    /// <summary>95% 타원 장/단반경 [m], 장축 방위각(북에서 동쪽으로) [deg].</summary>
    public double EllipseMajor { get; init; }
    public double EllipseMinor { get; init; }
    public double EllipseAzimuthDeg { get; init; }

    public static ImpactStats Compute(IReadOnlyList<(double N, double E)> pts)
    {
        int n = pts.Count;
        if (n == 0) return new ImpactStats();
        double mn = pts.Average(p => p.N), me = pts.Average(p => p.E);
        double snn = 0, see = 0, sne = 0;
        var r = new double[n];
        for (int i = 0; i < n; i++)
        {
            double dn = pts[i].N - mn, de = pts[i].E - me;
            snn += dn * dn; see += de * de; sne += dn * de;
            r[i] = Math.Sqrt(dn * dn + de * de);
        }
        double div = Math.Max(n - 1, 1);
        snn /= div; see /= div; sne /= div;
        Array.Sort(r);
        double tr = snn + see, det = snn * see - sne * sne;
        double disc = Math.Sqrt(Math.Max(tr * tr / 4 - det, 0));
        double l1 = tr / 2 + disc, l2 = Math.Max(tr / 2 - disc, 0);
        double ang = 0.5 * Math.Atan2(2 * sne, snn - see) * 180 / Math.PI;
        const double chi2_95 = 5.991464547;
        return new ImpactStats
        {
            Count = n, MeanN = mn, MeanE = me,
            Cep50 = Quantile(r, 0.5), R95 = Quantile(r, 0.95),
            SigmaN = Math.Sqrt(snn), SigmaE = Math.Sqrt(see),
            EllipseMajor = Math.Sqrt(chi2_95 * l1), EllipseMinor = Math.Sqrt(chi2_95 * l2), EllipseAzimuthDeg = ang,
        };
    }

    public static double Quantile(double[] sorted, double q)
    {
        if (sorted.Length == 0) return double.NaN;
        double pos = q * (sorted.Length - 1);
        int i = (int)Math.Floor(pos);
        int j = Math.Min(i + 1, sorted.Length - 1);
        return sorted[i] + (sorted[j] - sorted[i]) * (pos - i);
    }

    public JsonObject ToJson() => new()
    {
        ["count"] = Count,
        ["mpi_n_m"] = MeanN, ["mpi_e_m"] = MeanE,
        ["cep50_m"] = Cep50, ["r95_m"] = R95,
        ["sigma_n_m"] = SigmaN, ["sigma_e_m"] = SigmaE,
        ["ellipse95"] = new JsonObject { ["major_m"] = EllipseMajor, ["minor_m"] = EllipseMinor, ["azimuth_deg"] = EllipseAzimuthDeg },
    };
}
