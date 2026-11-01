using System.Text.Json.Nodes;
using Sim.Core.Numerics;
using Sim.Core.Scenario;

namespace Sim.Core.Models;

/// <summary>
/// CFD 공력 DB(PRD FR-REL-01, schema mns.aero-db/1 — tools/cfd/run_cfd.py collect 가 생성).
/// 축대칭(동체 x 축) 물체의 물체축 계수 CX(α_t, M)·CN(α_t, M)·Cm(α_t, M), 기준 S·L·모멘트 기준점(동체 좌표).
/// CFD 정의: 진행 방향 d = (cos α, 0, sin α) 에서 F = q S (CX e_x + CN e_z), M_y = q S L Cm.
/// 임의 방향: e_p = d 의 축 수직 성분 방향, F = q S (CX e_x + CN e_p), M = q S L Cm (e_p × e_x).
/// α_t &gt; 표 최대(90°)이고 foreAftSymmetric 이면 앞뒤 거울: CX(α) = −CX(180−α), CN 같음, Cm = −Cm(180−α).
/// 표 밖(마하·받음각)은 경계값 고정 + 카운트(VV-32).
/// </summary>
public sealed class AeroDb
{
    public required string Name { get; init; }
    public required double RefArea { get; init; }
    public required double RefLength { get; init; }
    public Vec3 MomentRef { get; init; }
    public bool ForeAftSymmetric { get; init; }
    public required double[] Alpha { get; init; }
    public required double[] Mach { get; init; }
    public required double[][] CX { get; init; }
    public required double[][] CN { get; init; }
    public required double[][] Cm { get; init; }
    public string Source { get; init; } = "";
    public long OutOfRange;

    public static AeroDb Load(string path)
    {
        var o = J.ParseObject(File.ReadAllText(path));
        if (J.Str(o, "schema", "") != "mns.aero-db/1") throw new InvalidDataException($"{path}: schema mns.aero-db/1 이 아닙니다.");
        double[][] Grid(string k) => o[k]!.AsArray().Select(r => J.Arr(r!)).ToArray();
        var db = new AeroDb
        {
            Name = J.Str(o, "name", Path.GetFileNameWithoutExtension(path)),
            RefArea = J.NumReq(o, "refArea", "aero-db"), RefLength = J.NumReq(o, "refLength", "aero-db"),
            MomentRef = J.V3(o, "momentRef", Vec3.Zero), ForeAftSymmetric = J.Bool(o, "foreAftSymmetric", false),
            Alpha = J.Arr(o["alpha"]!), Mach = J.Arr(o["mach"]!), CX = Grid("CX"), CN = Grid("CN"), Cm = Grid("Cm"),
            Source = path,
        };
        if (db.CX.Length != db.Mach.Length || db.CX.Any(r => r.Length != db.Alpha.Length))
            throw new InvalidDataException($"{path}: 표 크기가 mach × alpha 와 다릅니다.");
        for (int i = 1; i < db.Alpha.Length; i++)
            if (!(db.Alpha[i] > db.Alpha[i - 1])) throw new InvalidDataException($"{path}: alpha 는 단조 증가여야 합니다.");
        return db;
    }

    private static double Interp(double[] x, double[] y, double v)
    {
        if (x.Length == 1 || v <= x[0]) return y[0];
        if (v >= x[^1]) return y[^1];
        int i = 1;
        while (x[i] < v) i++;
        double f = (v - x[i - 1]) / (x[i] - x[i - 1]);
        return y[i - 1] + (y[i] - y[i - 1]) * f;
    }

    /// <summary>(CX, CN, Cm) at α_t[deg], M.</summary>
    public (double CX, double CN, double Cm) Coefficients(double alphaT, double mach)
    {
        double sgn = 1;
        if (alphaT > Alpha[^1] + 1e-9 && ForeAftSymmetric) { alphaT = 180 - alphaT; sgn = -1; }
        if (alphaT < Alpha[0] - 1e-9 || alphaT > Alpha[^1] + 1e-9 || mach < Mach[0] - 1e-9 || mach > Mach[^1] + 1e-9)
            Interlocked.Increment(ref OutOfRange);
        double At(double[][] t)
        {
            if (Mach.Length == 1) return Interp(Alpha, t[0], alphaT);
            var col = t.Select(r => Interp(Alpha, r, alphaT)).ToArray();
            return Interp(Mach, col, mach);
        }
        return (sgn * At(CX), At(CN), sgn * At(Cm));
    }

    /// <summary>힘(동체축)·모멘트(MomentRef 기준). dir = 대기 대비 진행 단위벡터(동체), qs = 동압 × 배율.</summary>
    public (Vec3 Force, Vec3 Moment) Compute(Vec3 dir, double qs, double mach)
    {
        double dx = Math.Clamp(dir.X, -1, 1);
        var perp = new Vec3(0, dir.Y, dir.Z);
        double pn = perp.Norm;
        var ep = pn > 1e-12 ? perp / pn : Vec3.UnitZ;
        double alphaT = Math.Atan2(pn, dx) * 180 / Math.PI;
        var (cx, cn, cm) = Coefficients(alphaT, mach);
        var f = (Vec3.UnitX * cx + ep * cn) * (qs * RefArea);
        var m = Vec3.Cross(ep, Vec3.UnitX) * (qs * RefArea * RefLength * cm);
        return (f, m);
    }
}
