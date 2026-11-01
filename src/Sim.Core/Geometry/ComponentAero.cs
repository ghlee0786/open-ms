using Sim.Core.Numerics;

namespace Sim.Core.Geometry;

/// <summary>사다리꼴 등가 핀 세트(동체 좌표, 기수 +X). RootLeX: 뿌리 앞전의 동체 x, SweepLength: 뿌리 앞전→끝 앞전 축 거리.</summary>
public sealed record FinSet(int Count, double RootChord, double TipChord, double Span, double SweepLength, double RootLeX, double BodyRadius, string Source);

/// <summary>성분 합성 결과: 법선력 기울기 CNα [1/rad, 기준면적 π·d²/4] 와 압력중심 x(동체 좌표).</summary>
public sealed record BuildUp(double RefDiameter, double CnAlpha, double XCp, IReadOnlyList<(string Name, double CnAlpha, double X)> Parts, FinSet? Fins)
{
    public double RefArea => Math.PI / 4 * RefDiameter * RefDiameter;
    /// <summary>박리 베이스(급수축)로 보고 제외한 세장체 CNα (음수, 보고용).</summary>
    public double SeparatedBaseCnAlpha { get; init; }
}

/// <summary>
/// 성분 합성(component build-up) 정적 공력 — FX-07. 소받음각 Barrowman(1967, NASA/Estes TIR-33 형식) 방법:
///  · 축대칭 몸체(노즈·어깨·보트테일): 세장체 이론 dCNα/dx = (2/A_ref)·dA/dx, 단면적 A(x) 는 메시 단면의 몸체 반경(핀 제외)에서.
///    (원뿔 노즈면 CP 0.666 L, 보트테일은 음의 CNα — Barrowman 과 같은 결과.)
///  · 핀 세트: CNα_F = K_fb·4N(s/d)² / (1 + √(1 + (2 l_m/(c_r + c_t))²)), K_fb = 1 + R/(s+R),
///    X_F = X_LE + x_r(c_r + 2c_t)/(3(c_r + c_t)) + [c_r + c_t − c_r c_t/(c_r + c_t)]/6.
///  · CP = Σ CNα_i X_i / Σ CNα_i. 몸체 양력(고받음각 횡류) 은 소받음각 기울기에 기여 0 이라 생략(횡류 항력은 L1 형상 항력이 담당).
/// 핀은 시나리오 fins 로 주거나, 메시 단면에서 몸체보다 크게 튀어나온 각 구역으로 자동 추출(사다리꼴 등가: 면적·스팬·끝 시위 보존).
/// </summary>
public static class ComponentAero
{
    public const int Sectors = 36;
    /// <summary>
    /// 부착류 보트테일로 보는 최대 수축 기울기 |dr/dx| = tan 30°. 더 급한 단면 감소(계단·뭉툭한 베이스)는 박리되어
    /// 세장체 음의 양력이 생기지 않으므로 제외한다(Barrowman 도 베이스는 양력 0). 제외량은 SeparatedBaseCnAlpha 로 보고.
    /// </summary>
    public static readonly double MaxAttachedBoattailSlope = Math.Tan(30 * Math.PI / 180);

    /// <summary>메시 단면 프로파일: 축 위치 x, 몸체 반경, 핀 바깥 반경(없으면 몸체 반경), 핀 개수.</summary>
    public sealed record Station(double X, double BodyRadius, double OuterRadius, int FinCount);

    /// <summary>동체 좌표 메시(기수 +X)를 n 개 단면으로 잘라 몸체 반경·핀 반경을 얻는다. 축 중심은 바운딩박스 중심(y,z).</summary>
    public static List<Station> Profile(TriMesh mesh, int n = 240)
    {
        var (min, max) = mesh.Bounds();
        double cy = 0.5 * (min.Y + max.Y), cz = 0.5 * (min.Z + max.Z);
        var vs = mesh.Vertices;
        var list = new List<Station>();
        var sec = new double[Sectors];
        Span<Vec3> hit = stackalloc Vec3[3];
        for (int k = 0; k < n; k++)
        {
            double x = max.X - (k + 0.5) / n * (max.X - min.X);
            Array.Fill(sec, 0.0);
            for (int i = 0; i + 2 < vs.Count; i += 3)
            {
                var a = vs[i]; var b = vs[i + 1]; var c = vs[i + 2];
                int h = 0;
                Edge(a, b, x, hit, ref h); Edge(b, c, x, hit, ref h); Edge(c, a, x, hit, ref h);
                if (h < 2) continue;
                for (int s = 0; s <= 4; s++)
                {
                    var p = hit[0] + (hit[1] - hit[0]) * (s / 4.0);
                    double y = p.Y - cy, z = p.Z - cz, r = Math.Sqrt(y * y + z * z);
                    int idx = (int)Math.Floor((Math.Atan2(z, y) + Math.PI) / (2 * Math.PI) * Sectors) % Sectors;
                    if (r > sec[idx]) sec[idx] = r;
                }
            }
            var filled = sec.Where(r => r > 0).OrderBy(r => r).ToArray();
            if (filled.Length == 0) { list.Add(new Station(x, 0, 0, 0)); continue; }
            // 몸체 반경: 채워진 구역 반경의 중앙값(얇은 핀은 소수 구역만 차지)
            double rb = filled.Length >= Sectors / 2 ? filled[filled.Length / 2] : filled[0];
            double outer = rb; int fins = 0; bool prev = false, first = false;
            for (int s = 0; s < Sectors; s++)
            {
                bool f = sec[s] > Math.Max(1.5 * rb, rb + 0.002);
                if (f) outer = Math.Max(outer, sec[s]);
                if (f && !prev) fins++;
                if (s == 0) first = f;
                prev = f;
            }
            if (prev && first && fins > 1) fins--; // 0°/360° 경계에서 이어진 구역
            list.Add(new Station(x, rb, outer, fins));
        }
        return list;
    }

    private static void Edge(Vec3 a, Vec3 b, double x, Span<Vec3> hit, ref int h)
    {
        if (h >= 2) return;
        double da = a.X - x, db = b.X - x;
        if ((da > 0) == (db > 0) || da == db) return;
        double t = da / (da - db);
        hit[h++] = a + (b - a) * t;
    }

    /// <summary>프로파일에서 핀 세트(사다리꼴 등가)를 추출. 핀이 없으면 null.</summary>
    public static FinSet? ExtractFins(IReadOnlyList<Station> st)
    {
        double dx = st.Count > 1 ? Math.Abs(st[0].X - st[1].X) : 0;
        var spans = st.Select(s => Math.Max(0, s.OuterRadius - s.BodyRadius)).ToArray();
        double sMax = spans.Max();
        if (sMax <= 0) return null;
        int kMax = Array.IndexOf(spans, sMax);
        int count = st.Where((s, k) => spans[k] > 0.5 * sMax).Select(s => s.FinCount).DefaultIfEmpty(0).Max();
        if (count < 2) return null;
        double area = spans.Sum() * dx;                     // 핀 한 장 평면 면적
        var finIdx = Enumerable.Range(0, st.Count).Where(k => spans[k] > 0.02 * sMax).ToList();
        double rootLe = st[finIdx.First()].X;               // 앞쪽(기수 쪽)이 x 큼
        var tipIdx = Enumerable.Range(0, st.Count).Where(k => spans[k] >= 0.9 * sMax).ToList();
        double tipLe = st[tipIdx.First()].X, tipTe = st[tipIdx.Last()].X;
        double ct = Math.Abs(tipLe - tipTe) + dx;
        double cr = Math.Max(2 * area / sMax - ct, ct);
        double rBody = st[kMax].BodyRadius;
        return new FinSet(count, cr, ct, sMax, rootLe - tipLe, rootLe, rBody, "mesh");
    }

    /// <summary>핀 세트 CNα·CP (Barrowman). d: 기준 지름.</summary>
    public static (double CnAlpha, double X) Fins(FinSet f, double d)
    {
        double cr = f.RootChord, ct = f.TipChord, s = f.Span, xr = f.SweepLength;
        double lm = Math.Sqrt(s * s + Math.Pow(xr + ct / 2 - cr / 2, 2));    // 중간 시위선 길이
        double kfb = 1 + f.BodyRadius / (s + f.BodyRadius);
        double cn = kfb * 4 * f.Count * Math.Pow(s / d, 2) / (1 + Math.Sqrt(1 + Math.Pow(2 * lm / (cr + ct), 2)));
        double xb = xr * (cr + 2 * ct) / (3 * (cr + ct)) + (cr + ct - cr * ct / (cr + ct)) / 6; // 뿌리 앞전에서 뒤로
        return (cn, f.RootLeX - xb);
    }

    /// <summary>메시(동체 좌표, 기수 +X)와 선택 핀 지정으로 성분 합성. refDiameter 미지정이면 몸체 최대 지름.</summary>
    public static BuildUp Compute(TriMesh bodyMesh, FinSet? fins = null, double? refDiameter = null)
    {
        var st = Profile(bodyMesh);
        double d = refDiameter ?? 2 * st.Max(s => s.BodyRadius);
        double aref = Math.PI / 4 * d * d;
        var parts = new List<(string, double, double)>();
        // 몸체: 단면적 변화 구간을 확장(+)/수축(−)으로 묶어 성분화
        double cnPos = 0, mPos = 0, cnNeg = 0, mNeg = 0, cnSep = 0;
        for (int k = 1; k < st.Count; k++)
        {
            double a0 = Math.PI * st[k - 1].BodyRadius * st[k - 1].BodyRadius, a1 = Math.PI * st[k].BodyRadius * st[k].BodyRadius;
            double dcn = 2 * (a1 - a0) / aref, xm = 0.5 * (st[k - 1].X + st[k].X);
            double slope = Math.Abs(st[k].BodyRadius - st[k - 1].BodyRadius) / Math.Max(Math.Abs(st[k].X - st[k - 1].X), 1e-12);
            if (dcn >= 0) { cnPos += dcn; mPos += dcn * xm; }
            else if (slope > MaxAttachedBoattailSlope) cnSep += dcn;   // 급격한 단면 감소 = 박리된 베이스: 양력 없음
            else { cnNeg += dcn; mNeg += dcn * xm; }
        }
        if (cnPos > 0) parts.Add(("몸체 확장(노즈·어깨)", cnPos, mPos / cnPos));
        if (cnNeg < 0) parts.Add(("몸체 수축(보트테일)", cnNeg, mNeg / cnNeg));
        var f = fins ?? ExtractFins(st);
        if (f is not null)
        {
            var (cnf, xf) = Fins(f, d);
            parts.Add(($"핀 {f.Count}장({f.Source})", cnf, xf));
        }
        double cn = parts.Sum(p => p.Item2);
        double xcp = cn != 0 ? parts.Sum(p => p.Item2 * p.Item3) / cn : st[0].X;
        return new BuildUp(d, cn, xcp, parts, f) { SeparatedBaseCnAlpha = cnSep };
    }
}
