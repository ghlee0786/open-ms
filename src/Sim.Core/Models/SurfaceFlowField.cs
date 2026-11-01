using System.Text.Json.Nodes;
using Sim.Core.Geometry;
using Sim.Core.Numerics;
using Sim.Core.Scenario;

namespace Sim.Core.Models;

/// <summary>
/// CFD 표면 유동장(공기흐름 시각화, schema mns.surface-flow/1 — tools/cfd/flowviz.py 가 OpenFOAM 결과에서 생성).
/// 축대칭 원통 외형(동체 x 축, 반경 R, x ∈ [x0, x1])의 표면을 (x, θ) 측면 격자와 앞·뒤 끝면 (r, θ) 격자로 펼쳐
/// 조건(케이스)마다 장(cp, cf, cpRms …)을 저장한다. θ = 바람 맞는 쪽(+z, CFD 횡류 방향)에서 잰 각 0–180°(y 대칭).
/// CFD 정의: 진행 방향 d = (cos α, 0, sin α), 유동 −d (AeroDb 와 같음).
/// 한 인스턴스를 물체 면(삼각형 중심) 목록에 대해 조건만 바꿔 여러 번 표본화한다 — 분할 화면에서 같은 메시를 공유하고
/// 면 색 배열만 바꾸는 용도(메모리 절약).
/// 임의 자세: α_t = acos(d·e_x), 공력 롤 φ' = 횡류 방향 e_p = (0, d_y, d_z)/|·|. 동체 점 p → CFD 좌표 (x, p·(e_p×e_x), p·e_p).
/// α_t &gt; 표 최대(90°)이고 foreAftSymmetric 이면 앞뒤 거울: x → x0 + x1 − x, α → 180 − α.
/// 받음각 사이는 선형 보간(같은 조건 묶음 안), 표 밖은 경계값 고정 + OutOfRange 카운트.
/// </summary>
public sealed class SurfaceFlowField
{
    public required string Name { get; init; }
    public required double Radius { get; init; }
    public required double X0 { get; init; }
    public required double X1 { get; init; }
    public required int Nx { get; init; }
    public required int Ntheta { get; init; }
    public required int Nr { get; init; }
    public bool ForeAftSymmetric { get; init; }
    public required IReadOnlyList<FlowCase> Cases { get; init; }
    public string Source { get; init; } = "";
    public string Warning { get; init; } = "";
    public long OutOfRange;

    /// <summary>조건 하나: 묶음(group, 예 "M0.50") · 받음각 · 장 이름 → (측면 Nx×Nθ, 앞 끝면 Nr×Nθ, 뒤 끝면 Nr×Nθ) 값 · 유선.</summary>
    public sealed record FlowCase(string Group, string Label, double Alpha, double Mach, double Speed, string Method, bool Valid,
        IReadOnlyDictionary<string, FieldGrid> Fields, IReadOnlyList<Streamline> Streamlines, IReadOnlyDictionary<string, double>? Coeffs = null);

    /// <summary>묶음 안 유효 케이스의 힘 계수(coeffs, 예 "CZ") 받음각 선형 보간 — 앞뒤 대칭이면 α &gt; 90° 를 180 − α 로. 없으면 NaN.</summary>
    public double Coefficient(string name, double alphaDeg, string? group = null)
    {
        group ??= Cases.FirstOrDefault(c => c.Valid)?.Group ?? "";
        var set = Cases.Where(c => c.Valid && c.Group == group && c.Coeffs is not null && c.Coeffs.ContainsKey(name)).OrderBy(c => c.Alpha).ToList();
        if (set.Count == 0) return double.NaN;
        if (ForeAftSymmetric && alphaDeg > set[^1].Alpha + 1e-9) alphaDeg = 180 - alphaDeg;
        if (alphaDeg <= set[0].Alpha) return set[0].Coeffs![name];
        if (alphaDeg >= set[^1].Alpha) return set[^1].Coeffs![name];
        int i = 1;
        while (set[i].Alpha < alphaDeg) i++;
        double f = (alphaDeg - set[i - 1].Alpha) / (set[i].Alpha - set[i - 1].Alpha);
        return set[i - 1].Coeffs![name] + (set[i].Coeffs![name] - set[i - 1].Coeffs![name]) * f;
    }

    public sealed record FieldGrid(double[] Side, double[] CapFwd, double[] CapAft);

    /// <summary>유선(CFD 좌표, m)과 점마다 속력비 |U|/V∞.</summary>
    public sealed record Streamline(Vec3[] Points, double[] SpeedRatio);

    public IEnumerable<string> Groups => Cases.Select(c => c.Group).Distinct();

    public IEnumerable<string> FieldNames => Cases.SelectMany(c => c.Fields.Keys).Distinct();

    public static SurfaceFlowField Load(string path)
    {
        var o = J.ParseObject(File.ReadAllText(path));
        if (J.Str(o, "schema", "") != "mns.surface-flow/1") throw new InvalidDataException($"{path}: schema mns.surface-flow/1 이 아닙니다.");
        var s = o["surface"]!.AsObject();
        if (J.Str(s, "kind", "") != "cylinder") throw new InvalidDataException($"{path}: surface.kind 는 cylinder 만 지원합니다.");
        int nx = (int)J.NumReq(s, "nx", "surface"), nt = (int)J.NumReq(s, "ntheta", "surface"), nr = (int)J.NumReq(s, "nr", "surface");
        var cases = new List<FlowCase>();
        foreach (var cn in o["cases"]!.AsArray())
        {
            var c = cn!.AsObject();
            var fields = new Dictionary<string, FieldGrid>();
            foreach (var (k, v) in c["fields"]!.AsObject())
            {
                var f = v!.AsObject();
                var g = new FieldGrid(J.Arr(f["side"]!), J.Arr(f["capFwd"]!), J.Arr(f["capAft"]!));
                if (g.Side.Length != nx * nt || g.CapFwd.Length != nr * nt || g.CapAft.Length != nr * nt)
                    throw new InvalidDataException($"{path}: 장 {k} 크기가 surface 격자(nx·nθ, nr·nθ)와 다릅니다.");
                fields[k] = g;
            }
            var lines = new List<Streamline>();
            if (c["streamlines"] is JsonArray sl)
                foreach (var ln in sl)
                {
                    var lo = ln!.AsObject();
                    var p = J.Arr(lo["p"]!);
                    var sp = J.Arr(lo["s"]!);
                    if (p.Length != 3 * sp.Length) throw new InvalidDataException($"{path}: 유선 점·속력 개수가 다릅니다.");
                    lines.Add(new Streamline(Enumerable.Range(0, sp.Length).Select(i => new Vec3(p[3 * i], p[3 * i + 1], p[3 * i + 2])).ToArray(), sp));
                }
            Dictionary<string, double>? coeffs = null;
            if (c["coeffs"] is JsonObject co)
            {
                coeffs = new Dictionary<string, double>();
                foreach (var (k, v) in co) if (v is JsonValue jv && jv.TryGetValue(out double d)) coeffs[k] = d;
            }
            cases.Add(new FlowCase(J.Str(c, "group", ""), J.Str(c, "label", ""), J.NumReq(c, "alpha", "case"), J.Num(c, "mach", 0),
                J.Num(c, "speed", 0), J.Str(c, "method", ""), J.Bool(c, "valid", true), fields, lines, coeffs));
        }
        return new SurfaceFlowField
        {
            Name = J.Str(o, "name", Path.GetFileNameWithoutExtension(path)),
            Radius = J.NumReq(s, "radius", "surface"), X0 = J.NumReq(s, "x0", "surface"), X1 = J.NumReq(s, "x1", "surface"),
            Nx = nx, Ntheta = nt, Nr = nr, ForeAftSymmetric = J.Bool(o, "foreAftSymmetric", false), Cases = cases,
            Source = path, Warning = J.Str(o, "warning", ""),
        };
    }

    /// <summary>마하가 가장 가까운 묶음(유효 케이스가 있는 것).</summary>
    public string GroupNearest(double mach) =>
        Cases.Where(c => c.Valid).OrderBy(c => Math.Abs(c.Mach - mach)).Select(c => c.Group).FirstOrDefault() ?? "";

    /// <summary>동체 진행 단위벡터 → (α_t[deg], e_p 횡류 단위벡터). 순수 축 방향이면 e_p = +z.</summary>
    public static (double AlphaT, Vec3 Ep) Attitude(Vec3 dirBody)
    {
        var d = dirBody.Normalized();
        double at = Math.Acos(Math.Clamp(d.X, -1, 1)) * 180 / Math.PI;
        var perp = new Vec3(0, d.Y, d.Z);
        return (at, perp.Norm > 1e-9 ? perp / perp.Norm : Vec3.UnitZ);
    }

    /// <summary>동체 좌표 점마다 장 값. dirBody = 대기 대비 진행 방향(동체). 장이 없거나 묶음이 비면 NaN.</summary>
    public double[] Sample(IReadOnlyList<Vec3> pointsBody, Vec3 dirBody, string field = "cp", string? group = null)
    {
        var (at, ep) = Attitude(dirBody);
        return Sample(pointsBody, at, ep, field, group);
    }

    /// <summary>α_t[deg] · 횡류 단위벡터 e_p(동체, x 성분 0) 로 표본화.</summary>
    public double[] Sample(IReadOnlyList<Vec3> pointsBody, double alphaT, Vec3 ep, string field = "cp", string? group = null)
    {
        var res = new double[pointsBody.Count];
        group ??= Cases.FirstOrDefault(c => c.Valid)?.Group ?? "";
        bool mirror = false;
        var set = Cases.Where(c => c.Valid && c.Group == group && c.Fields.ContainsKey(field)).OrderBy(c => c.Alpha).ToList();
        if (set.Count == 0) { Array.Fill(res, double.NaN); return res; }
        if (ForeAftSymmetric && alphaT > set[^1].Alpha + 1e-9) { alphaT = 180 - alphaT; mirror = true; }
        var (a, b, f) = Bracket(set, alphaT);
        var eq = Vec3.Cross(ep, Vec3.UnitX);
        for (int i = 0; i < res.Length; i++)
        {
            var p = pointsBody[i];
            double x = mirror ? X0 + X1 - p.X : p.X;
            double y = Vec3.Dot(p, eq), z = Vec3.Dot(p, ep);
            double va = At(a.Fields[field], x, y, z);
            res[i] = f <= 0 ? va : va + (At(b.Fields[field], x, y, z) - va) * f;
        }
        return res;
    }

    /// <summary>삼각형 메시(동체 좌표)의 면마다 값 — 면 중심에서 표본화.</summary>
    public double[] FaceValues(TriMesh meshBody, Vec3 dirBody, string field = "cp", string? group = null)
    {
        var c = new Vec3[meshBody.TriangleCount];
        for (int k = 0; k < c.Length; k++) c[k] = (meshBody.Vertices[3 * k] + meshBody.Vertices[3 * k + 1] + meshBody.Vertices[3 * k + 2]) / 3;
        return Sample(c, dirBody, field, group);
    }

    /// <summary>가장 가까운 받음각 케이스의 유선(동체 좌표로 변환). 반환: 케이스(없으면 null)와 선 목록.</summary>
    public (FlowCase? Case, IReadOnlyList<Streamline> Lines) StreamlinesBody(Vec3 dirBody, string? group = null)
    {
        var (at, ep) = Attitude(dirBody);
        group ??= Cases.FirstOrDefault(c => c.Valid)?.Group ?? "";
        var set = Cases.Where(c => c.Valid && c.Group == group && c.Streamlines.Count > 0).ToList();
        if (set.Count == 0) return (null, []);
        bool mirror = ForeAftSymmetric && at > set.Max(c => c.Alpha) + 1e-9;
        double aq = mirror ? 180 - at : at;
        var best = set.OrderBy(c => Math.Abs(c.Alpha - aq)).First();
        var eq = Vec3.Cross(ep, Vec3.UnitX);
        Vec3 ToBody(Vec3 q) => new Vec3(mirror ? X0 + X1 - q.X : q.X, 0, 0) + eq * q.Y + ep * q.Z;
        return (best, best.Streamlines.Select(s => new Streamline(s.Points.Select(ToBody).ToArray(), s.SpeedRatio)).ToList());
    }

    private (FlowCase A, FlowCase B, double F) Bracket(List<FlowCase> set, double alpha)
    {
        if (alpha < set[0].Alpha - 1e-9 || alpha > set[^1].Alpha + 1e-9) Interlocked.Increment(ref OutOfRange);
        if (set.Count == 1 || alpha <= set[0].Alpha) return (set[0], set[0], 0);
        if (alpha >= set[^1].Alpha) return (set[^1], set[^1], 0);
        int i = 1;
        while (set[i].Alpha < alpha) i++;
        return (set[i - 1], set[i], (alpha - set[i - 1].Alpha) / (set[i].Alpha - set[i - 1].Alpha));
    }

    /// <summary>CFD 좌표 점 → 가장 가까운 외형 면(측면 · 앞 끝면 x1 · 뒤 끝면 x0) 격자 쌍선형 보간.</summary>
    private double At(FieldGrid g, double x, double y, double z)
    {
        double r = Math.Sqrt(y * y + z * z);
        double th = Math.Atan2(Math.Abs(y), z) * 180 / Math.PI;          // 0 = 바람 맞는 쪽(+z), 180 = 뒤쪽
        double dSide = Math.Abs(Radius - r) + Math.Max(0, X0 - x) + Math.Max(0, x - X1);
        double dFwd = Math.Abs(x - X1) + Math.Max(0, r - Radius), dAft = Math.Abs(x - X0) + Math.Max(0, r - Radius);
        double tt = th / 180 * (Ntheta - 1);
        if (dSide <= dFwd && dSide <= dAft)
            return Bilinear(g.Side, Nx, Ntheta, (x - X0) / (X1 - X0) * (Nx - 1), tt);
        return Bilinear(dFwd <= dAft ? g.CapFwd : g.CapAft, Nr, Ntheta, Math.Min(r / Radius, 1) * (Nr - 1), tt);
    }

    private static double Bilinear(double[] v, int n0, int n1, double u, double w)
    {
        u = Math.Clamp(u, 0, n0 - 1); w = Math.Clamp(w, 0, n1 - 1);
        int i = Math.Min((int)u, n0 - 2), j = Math.Min((int)w, n1 - 2);
        if (n0 == 1) i = 0;
        if (n1 == 1) j = 0;
        double fu = n0 == 1 ? 0 : u - i, fw = n1 == 1 ? 0 : w - j;
        double V(int a, int b) => v[Math.Min(a, n0 - 1) * n1 + Math.Min(b, n1 - 1)];
        return (1 - fu) * ((1 - fw) * V(i, j) + fw * V(i, j + 1)) + fu * ((1 - fw) * V(i + 1, j) + fw * V(i + 1, j + 1));
    }
}
