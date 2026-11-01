using Sim.Core.Geometry;
using Sim.Core.Numerics;

namespace Sim.Core.Models;

/// <summary>풍동 보기 표면 장(표면 색): 압력 계수 · 경계층 바깥 속력비(Cp 에서 환산) · 벽 마찰 계수.</summary>
public enum TunnelField { Cp, SpeedRatio, Cf }

/// <summary>
/// 풍동 보기(CFD 후처리 장면) 계산 — GL 없음(시험 대상). 물체 메시 정점마다 표면 장 값, 평활 법선, 풍동 좌표 변환.
/// 풍동 좌표: CFD 좌표(동체 x 축, 진행 d = (cos α, 0, sin α), 유동 −d)를 y 축으로 α 회전해 d → +X, 물체 중심 → 원점.
/// 흐름은 화면에서 항상 −X 쪽으로 수평 — 모형이 받음각만큼 기운 풍동 장면.
/// </summary>
public static class WindTunnel
{
    /// <summary>표면 장 목록(콤보 순서): 키(smoke) · 이름 · 단위 · 각주.</summary>
    public static readonly (TunnelField Field, string Key, string Label, string Unit, string Note)[] Fields =
    [
        (TunnelField.Cp, "cp", "표면 압력 계수 Cp", "(p − p∞)/q∞", "OpenFOAM 정상 RANS 표면 압력"),
        (TunnelField.SpeedRatio, "speed", "표면 속도비 |Uₑ|/V∞", "경계층 바깥 근사", "Cp → 등엔트로피 환산(벽면 속도 아님)"),
        (TunnelField.Cf, "cf", "벽 마찰 계수 Cf", "|τ_w|/q∞", "OpenFOAM 벽 전단응력"),
    ];

    /// <summary>키 → 장(모르는 키 = Cp).</summary>
    public static TunnelField ParseField(string key)
    {
        foreach (var f in Fields)
            if (string.Equals(f.Key, key.Trim(), StringComparison.OrdinalIgnoreCase)) return f.Field;
        return TunnelField.Cp;
    }

    /// <summary>물체 중심(외형 원통 축 가운데, CFD·동체 좌표).</summary>
    public static Vec3 Center(SurfaceFlowField ff) => new((ff.X0 + ff.X1) / 2, 0, 0);

    /// <summary>CFD 좌표 점 → 풍동 좌표(받음각 α[deg] 회전 + 중심 이동).</summary>
    public static Vec3 ToTunnel(Vec3 p, double alphaDeg, Vec3 center)
    {
        double a = alphaDeg * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
        var q = p - center;
        return new Vec3(q.X * c + q.Z * s, q.Y, -q.X * s + q.Z * c);
    }

    /// <summary>풍동 좌표의 자유류 방향(유동 −d → −X).</summary>
    public static readonly Vec3 FlowDir = new(-1, 0, 0);

    /// <summary>CFD 좌표의 자유류 방향 −d = −(cos α, 0, sin α).</summary>
    public static Vec3 FlowDirCfd(double alphaDeg)
    {
        double a = alphaDeg * Math.PI / 180;
        return new Vec3(-Math.Cos(a), 0, -Math.Sin(a));
    }

    /// <summary>
    /// 케이스 c 의 표면 장을 점(CFD·동체 좌표, 보통 메시 정점)마다 — 그 케이스 받음각 그대로(보간 없음), 횡류 = +z.
    /// 장이 없으면 NaN. 속도비는 Cp 에서 등엔트로피 환산(<see cref="SpeedRatioFromCp"/>).
    /// </summary>
    public static double[] Values(SurfaceFlowField ff, SurfaceFlowField.FlowCase c, IReadOnlyList<Vec3> points, TunnelField field)
    {
        string name = field == TunnelField.Cf ? "cf" : "cp";
        if (!c.Fields.ContainsKey(name)) { var nan = new double[points.Count]; Array.Fill(nan, double.NaN); return nan; }
        // 이 케이스 하나만 든 장으로 표본화(같은 묶음의 다른 받음각과 섞이지 않게 · 유효 표시와 무관)
        var single = new SurfaceFlowField
        {
            Name = ff.Name, Radius = ff.Radius, X0 = ff.X0, X1 = ff.X1, Nx = ff.Nx, Ntheta = ff.Ntheta, Nr = ff.Nr,
            Cases = [c with { Group = "", Valid = true }],
        };
        var v = single.Sample(points, c.Alpha, Vec3.UnitZ, name, "");
        if (field == TunnelField.SpeedRatio)
            for (int i = 0; i < v.Length; i++) v[i] = SpeedRatioFromCp(v[i], c.Mach);
        return v;
    }

    /// <summary>
    /// Cp → 경계층 바깥 속력비 |Uₑ|/V∞. 압축성(M∞ ≥ 0.05): 등엔트로피 p/p∞ = 1 + γM∞²Cp/2 → 국소 마하 → V/V∞ = (M/M∞)·√(T/T∞).
    /// 비압축: √(1 − Cp). 정체점(Cp 최대)에서 0, Cp = 0 에서 1.
    /// </summary>
    public static double SpeedRatioFromCp(double cp, double mach, double gamma = 1.4)
    {
        if (double.IsNaN(cp)) return double.NaN;
        if (mach < 0.05) return Math.Sqrt(Math.Max(0, 1 - cp));
        double g1 = gamma - 1;
        double pr = Math.Max(1e-6, 1 + gamma / 2 * mach * mach * cp);                       // p/p∞
        double p0 = Math.Pow(1 + g1 / 2 * mach * mach, gamma / g1);                          // p0/p∞
        double m2 = Math.Max(0, 2 / g1 * (Math.Pow(p0 / pr, g1 / gamma) - 1));
        double tr = (1 + g1 / 2 * mach * mach) / (1 + g1 / 2 * m2);                          // T/T∞
        return Math.Sqrt(m2) / mach * Math.Sqrt(tr);
    }

    /// <summary>
    /// 꼭짓점(삼각형 순서, 정점 수 = 3 × 면)마다 평활 법선: 같은 위치 정점을 용접하고, 이 면과 creaseDeg 이내로 기운 이웃 면의
    /// 단위 법선을 그 꼭짓점 내각으로 가중 평균(삼각분할 방식과 무관 — 원통 측면은 정확히 반경 방향, 끝면 모서리는 날카롭게).
    /// 퇴화 면은 면 법선 그대로. 면이 뒤집혀 있어도(STL 감김 혼재) 이 면 쪽으로 맞춰 더한다.
    /// </summary>
    public static Vec3[] SmoothNormals(TriMesh m, double creaseDeg = 40)
    {
        int nt = m.TriangleCount;
        var fn = new Vec3[nt];
        for (int t = 0; t < nt; t++)
        {
            var c = Vec3.Cross(m.Vertices[3 * t + 1] - m.Vertices[3 * t], m.Vertices[3 * t + 2] - m.Vertices[3 * t]);
            fn[t] = c.Norm > 1e-30 ? c / c.Norm : Vec3.Zero;
        }
        double Angle(int t, int k)
        {
            var p = m.Vertices[3 * t + k];
            var a = m.Vertices[3 * t + (k + 1) % 3] - p; var b = m.Vertices[3 * t + (k + 2) % 3] - p;
            double na = a.Norm, nb = b.Norm;
            return na < 1e-30 || nb < 1e-30 ? 0 : Math.Acos(Math.Clamp(Vec3.Dot(a, b) / (na * nb), -1, 1));
        }
        var (mn, mx) = m.Bounds();
        double q = Math.Max((mx - mn).Norm, 1e-9) * 1e-6;
        var groups = new Dictionary<(long, long, long), List<(int T, double W)>>();
        (long, long, long) Key(Vec3 v) => ((long)Math.Round(v.X / q), (long)Math.Round(v.Y / q), (long)Math.Round(v.Z / q));
        for (int t = 0; t < nt; t++)
            for (int k = 0; k < 3; k++)
            {
                var key = Key(m.Vertices[3 * t + k]);
                if (!groups.TryGetValue(key, out var l)) groups[key] = l = new List<(int, double)>(6);
                l.Add((t, Angle(t, k)));
            }
        double cosC = Math.Cos(creaseDeg * Math.PI / 180);
        var res = new Vec3[3 * nt];
        for (int t = 0; t < nt; t++)
        {
            var own = fn[t].Norm > 0 ? fn[t] : Vec3.UnitZ;
            for (int k = 0; k < 3; k++)
            {
                var sum = Vec3.Zero;
                foreach (var (o, w) in groups[Key(m.Vertices[3 * t + k])])
                {
                    double d = Vec3.Dot(fn[o], own);
                    if (Math.Abs(d) >= cosC) sum += fn[o] * (w * Math.Sign(d));
                }
                res[3 * t + k] = sum.Norm > 1e-30 ? sum.Normalized() : own;
            }
        }
        return res;
    }

    /// <summary>외형 메시가 없을 때(유동장 파일만) 쓰는 원통 외형(동체 좌표, x ∈ [x0, x1], 반경 R, 측면·끝면 격자).</summary>
    public static TriMesh CylinderMesh(SurfaceFlowField ff, double h = 0.02)
    {
        double len = ff.X1 - ff.X0;
        int seg = Math.Clamp((int)Math.Ceiling(2 * Math.PI * ff.Radius / h), 24, 256);
        int nl = Math.Clamp((int)Math.Ceiling(len / h), 4, 400), nr = Math.Clamp((int)Math.Ceiling(ff.Radius / h), 2, 64);
        return TriMesh.CylinderGrid(ff.Radius, len, 0, Center(ff), seg, nl, nr);
    }
}

/// <summary>
/// 유선을 따라 흐르는 입자(풍동 보기 애니메이션). 유선마다 τ = ∫ ds / max(s, sMin)(s = |U|/V∞ — 자유류 속력으로 1 m 를 가는 시간 단위)
/// 로 매개화하고, 시각 t 에 τ = rate·t + k·spacing 위치에 입자를 둔다(k = 0, 1, …) — 빠른 곳에서 간격이 벌어진다.
/// 선 방향은 흐름 방향(flowDir)과 맞게 정렬(첫 점 → 끝 점이 하류).
/// </summary>
public sealed class StreamlineParticles
{
    public const double SpeedFloor = 0.05;

    public sealed record Line(Vec3[] P, double[] S, double[] Tau)
    {
        public double Duration => Tau[^1];
    }

    public IReadOnlyList<Line> Lines { get; }

    public StreamlineParticles(IEnumerable<(Vec3[] P, double[] S)> lines, Vec3 flowDir)
    {
        var res = new List<Line>();
        foreach (var (p0, s0) in lines)
        {
            if (p0.Length < 2 || s0.Length != p0.Length) continue;
            var p = p0; var s = s0;
            if (Vec3.Dot(p[^1] - p[0], flowDir) < 0) { p = Enumerable.Reverse(p).ToArray(); s = Enumerable.Reverse(s).ToArray(); }
            var tau = new double[p.Length];
            for (int i = 1; i < p.Length; i++)
                tau[i] = tau[i - 1] + (p[i] - p[i - 1]).Norm / Math.Max((s[i] + s[i - 1]) / 2, SpeedFloor);
            if (tau[^1] <= 0) continue;
            res.Add(new Line(p, s, tau));
        }
        Lines = res;
    }

    /// <summary>선 위 τ 위치의 점과 속력비(선형 보간).</summary>
    public static (Vec3 P, double S) At(Line l, double tau)
    {
        var t = l.Tau;
        if (tau <= 0) return (l.P[0], l.S[0]);
        if (tau >= t[^1]) return (l.P[^1], l.S[^1]);
        int i = Array.BinarySearch(t, tau);
        if (i >= 0) return (l.P[i], l.S[i]);
        i = ~i;                       // t[i-1] < tau < t[i]
        double f = (tau - t[i - 1]) / (t[i] - t[i - 1]);
        return (l.P[i - 1] + (l.P[i] - l.P[i - 1]) * f, l.S[i - 1] + (l.S[i] - l.S[i - 1]) * f);
    }

    /// <summary>시각 t[s] 의 입자들(출력 목록에 덧붙임). rate = 초당 τ 진행, spacing = 입자 τ 간격.</summary>
    public void Positions(double t, double rate, double spacing, List<(Vec3 P, double S)> output)
    {
        if (!(spacing > 0) || !double.IsFinite(t)) return;
        double phase = rate * t % spacing;
        if (phase < 0) phase += spacing;
        foreach (var l in Lines)
            for (double tau = phase; tau < l.Duration; tau += spacing)
                output.Add(At(l, tau));
    }
}
