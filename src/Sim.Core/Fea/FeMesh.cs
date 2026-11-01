using System.Globalization;
using Sim.Core.Geometry;
using Sim.Core.Numerics;

namespace Sim.Core.Fea;

/// <summary>CalculiX 요소 종류(Abaqus 이름·절점 순서 그대로).</summary>
public enum FeType { C3D4, C3D10, C3D8, C3D8I, C3D20R, C3D20, S6, S8R, CPS8 }

/// <summary>요소: 종류 + 절점 인덱스(0 기반, 절점 번호 = 인덱스 + 1).</summary>
public sealed record FeElement(FeType Type, int[] N);

/// <summary>
/// FE 메시(FR-FEA-01·03). 구조 격자 메셔(블록 C3D20R, 사변형 S8R/CPS8, 삼각 셸 S6), 복셀 대체 메셔(C3D20),
/// Gmsh/Abaqus .inp 읽기. 좌표 단위 m. 메시 생성은 외부 도구(Gmsh)가 기본이고 이 클래스의 격자는 검증 케이스·기본 형상·대체용이다.
/// </summary>
public sealed class FeMesh
{
    public List<Vec3> Nodes { get; } = new();
    public List<FeElement> Elements { get; } = new();
    public Dictionary<string, List<int>> NodeSets { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>셸·평면응력 두께 [m] (솔리드는 null).</summary>
    public double? Thickness { get; set; }
    /// <summary>메시 출처(구조 격자 · gmsh · 복셀 · inp).</summary>
    public string Source { get; set; } = "";

    public static bool IsShell(FeType t) => t is FeType.S6 or FeType.S8R;
    public static bool IsPlane(FeType t) => t == FeType.CPS8;
    public static bool IsSolid(FeType t) => !IsShell(t) && !IsPlane(t);
    public static int NodeCount(FeType t) => t switch
    {
        FeType.C3D4 => 4, FeType.C3D10 => 10, FeType.C3D8 or FeType.C3D8I => 8, FeType.C3D20R or FeType.C3D20 => 20,
        FeType.S6 => 6, FeType.S8R or FeType.CPS8 => 8, _ => 0,
    };

    public bool HasShells => Elements.Any(e => IsShell(e.Type));
    public bool HasPlane => Elements.Any(e => IsPlane(e.Type));

    public (Vec3 Min, Vec3 Max) Bounds()
    {
        var min = new Vec3(double.MaxValue, double.MaxValue, double.MaxValue);
        var max = new Vec3(double.MinValue, double.MinValue, double.MinValue);
        foreach (var v in Nodes) { min = Vec3.Min(min, v); max = Vec3.Max(max, v); }
        return (min, max);
    }

    public void AddNodeSet(string name, IEnumerable<int> nodes)
    {
        if (!NodeSets.TryGetValue(name, out var l)) NodeSets[name] = l = new List<int>();
        l.AddRange(nodes);
    }

    /// <summary>조건을 만족하는 절점 인덱스.</summary>
    public List<int> NodesWhere(Func<Vec3, bool> f)
    {
        var used = UsedNodes();
        return Enumerable.Range(0, Nodes.Count).Where(i => used[i] && f(Nodes[i])).ToList();
    }

    public bool[] UsedNodes()
    {
        var u = new bool[Nodes.Count];
        foreach (var e in Elements) foreach (var n in e.N) u[n] = true;
        return u;
    }

    // ---------------- 면(솔리드 외곽 · 하중) ----------------

    /// <summary>Abaqus 솔리드 면 번호 → 모서리 절점(요소 내 위치, 0 기반).</summary>
    public static int[][] FaceCorners(FeType t) => t switch
    {
        FeType.C3D4 or FeType.C3D10 => [[0, 1, 2], [0, 3, 1], [1, 3, 2], [2, 3, 0]],
        FeType.C3D8 or FeType.C3D8I or FeType.C3D20R or FeType.C3D20 => [[0, 1, 2, 3], [4, 7, 6, 5], [0, 4, 5, 1], [1, 5, 6, 2], [2, 6, 7, 3], [3, 7, 4, 0]],
        _ => [],
    };

    /// <summary>외곽 면(한 요소에만 속한 면): (요소, 면 번호 1 기반, 바깥 법선, 면적, 중심). 4각형 면은 평면 근사.</summary>
    public List<FeFace> ExteriorFaces()
    {
        var count = new Dictionary<string, int>();
        var all = new List<(int E, int F, int[] C)>();
        for (int e = 0; e < Elements.Count; e++)
        {
            var el = Elements[e];
            var fc = FaceCorners(el.Type);
            for (int f = 0; f < fc.Length; f++)
            {
                var c = fc[f].Select(k => el.N[k]).ToArray();
                var key = string.Join(",", c.OrderBy(x => x));
                count[key] = count.TryGetValue(key, out var k0) ? k0 + 1 : 1;
                all.Add((e, f + 1, c));
            }
        }
        var res = new List<FeFace>();
        foreach (var (e, f, c) in all)
        {
            if (count[string.Join(",", c.OrderBy(x => x))] != 1) continue;
            var p = c.Select(i => Nodes[i]).ToArray();
            var ctr = p.Aggregate(Vec3.Zero, (s, x) => s + x) / p.Length;
            var n = p.Length == 3 ? Vec3.Cross(p[1] - p[0], p[2] - p[0]) * 0.5 : Vec3.Cross(p[2] - p[0], p[3] - p[1]) * 0.5;
            var ec = ElementCentroid(e);
            if (Vec3.Dot(n, ctr - ec) < 0) n = -n;
            double a = n.Norm;
            if (a <= 0) continue;
            res.Add(new FeFace(e, f, n / a, a, ctr, c));
        }
        return res;
    }

    public Vec3 ElementCentroid(int e)
    {
        var el = Elements[e];
        int corners = el.Type switch { FeType.C3D4 or FeType.C3D10 => 4, FeType.S6 => 3, FeType.S8R or FeType.CPS8 => 4, _ => 8 };
        var s = Vec3.Zero;
        for (int k = 0; k < corners; k++) s += Nodes[el.N[k]];
        return s / corners;
    }

    // ---------------- 질량특성 ----------------

    /// <summary>
    /// 질량·질량중심·CG 기준 관성(밀도 rho, 셸은 두께 Thickness 의 중립면 질량). 직선 변 가정(모서리 절점 사면체 분해).
    /// 하중 케이스의 관성 완화(FR-FEA-02)에 쓴다.
    /// </summary>
    public (double Mass, Vec3 Cg, Mat3 Inertia) MassProperties(double rho)
    {
        double m = 0; var first = Vec3.Zero;
        double c11 = 0, c22 = 0, c33 = 0, c12 = 0, c13 = 0, c23 = 0;
        void Tet(Vec3 p0, Vec3 p1, Vec3 p2, Vec3 p3)
        {
            double v = Math.Abs(Vec3.Dot(p1 - p0, Vec3.Cross(p2 - p0, p3 - p0))) / 6 * rho;
            m += v;
            var s = p0 + p1 + p2 + p3;
            first += s * (v / 4);
            double k = v / 20;
            c11 += k * (p0.X * p0.X + p1.X * p1.X + p2.X * p2.X + p3.X * p3.X + s.X * s.X);
            c22 += k * (p0.Y * p0.Y + p1.Y * p1.Y + p2.Y * p2.Y + p3.Y * p3.Y + s.Y * s.Y);
            c33 += k * (p0.Z * p0.Z + p1.Z * p1.Z + p2.Z * p2.Z + p3.Z * p3.Z + s.Z * s.Z);
            c12 += k * (p0.X * p0.Y + p1.X * p1.Y + p2.X * p2.Y + p3.X * p3.Y + s.X * s.Y);
            c13 += k * (p0.X * p0.Z + p1.X * p1.Z + p2.X * p2.Z + p3.X * p3.Z + s.X * s.Z);
            c23 += k * (p0.Y * p0.Z + p1.Y * p1.Z + p2.Y * p2.Z + p3.Y * p3.Z + s.Y * s.Z);
        }
        void Tri(Vec3 a, Vec3 b, Vec3 c, double t)
        {
            double w = 0.5 * Vec3.Cross(b - a, c - a).Norm * t * rho;
            m += w;
            var s = a + b + c;
            first += s * (w / 3);
            // ∫ x xᵀ dA = A/12 (Σ p pᵀ + s sᵀ)
            double k = w / 12;
            c11 += k * (a.X * a.X + b.X * b.X + c.X * c.X + s.X * s.X);
            c22 += k * (a.Y * a.Y + b.Y * b.Y + c.Y * c.Y + s.Y * s.Y);
            c33 += k * (a.Z * a.Z + b.Z * b.Z + c.Z * c.Z + s.Z * s.Z);
            c12 += k * (a.X * a.Y + b.X * b.Y + c.X * c.Y + s.X * s.Y);
            c13 += k * (a.X * a.Z + b.X * b.Z + c.X * c.Z + s.X * s.Z);
            c23 += k * (a.Y * a.Z + b.Y * b.Z + c.Y * c.Z + s.Y * s.Z);
        }
        foreach (var el in Elements)
        {
            Vec3 P(int k) => Nodes[el.N[k]];
            switch (el.Type)
            {
                case FeType.C3D4 or FeType.C3D10: Tet(P(0), P(1), P(2), P(3)); break;
                case FeType.C3D8 or FeType.C3D8I or FeType.C3D20R or FeType.C3D20:
                    // 육면체 → 사면체 6개(대각 0-6)
                    Tet(P(0), P(1), P(2), P(6)); Tet(P(0), P(2), P(3), P(6)); Tet(P(0), P(3), P(7), P(6));
                    Tet(P(0), P(7), P(4), P(6)); Tet(P(0), P(4), P(5), P(6)); Tet(P(0), P(5), P(1), P(6));
                    break;
                case FeType.S6: Tri(P(0), P(1), P(2), Thickness ?? 0); break;
                case FeType.S8R or FeType.CPS8: Tri(P(0), P(1), P(2), Thickness ?? 0); Tri(P(0), P(2), P(3), Thickness ?? 0); break;
            }
        }
        if (m <= 0) return (0, Vec3.Zero, Mat3.Zero);
        var cg = first / m;
        double tr = c11 + c22 + c33;
        var io = new Mat3(tr - c11, -c12, -c13, -c12, tr - c22, -c23, -c13, -c23, tr - c33);
        return (m, cg, io + Mat3.ParallelAxis(m, cg) * -1);
    }

    /// <summary>요소 부피(셸·평면은 면적 × 두께), 직선 변 가정.</summary>
    public double ElementVolume(int e)
    {
        var el = Elements[e];
        Vec3 P(int k) => Nodes[el.N[k]];
        static double Tet(Vec3 a, Vec3 b, Vec3 c, Vec3 d) => Math.Abs(Vec3.Dot(b - a, Vec3.Cross(c - a, d - a))) / 6;
        double t = Thickness ?? 0;
        return el.Type switch
        {
            FeType.C3D4 or FeType.C3D10 => Tet(P(0), P(1), P(2), P(3)),
            FeType.S6 => 0.5 * Vec3.Cross(P(1) - P(0), P(2) - P(0)).Norm * t,
            FeType.S8R or FeType.CPS8 => (0.5 * Vec3.Cross(P(1) - P(0), P(2) - P(0)).Norm + 0.5 * Vec3.Cross(P(2) - P(0), P(3) - P(0)).Norm) * t,
            _ => Tet(P(0), P(1), P(2), P(6)) + Tet(P(0), P(2), P(3), P(6)) + Tet(P(0), P(3), P(7), P(6))
                 + Tet(P(0), P(7), P(4), P(6)) + Tet(P(0), P(4), P(5), P(6)) + Tet(P(0), P(5), P(1), P(6)),
        };
    }

    // ---------------- 구조 격자 ----------------

    /// <summary>
    /// 사상 블록 C3D20R(또는 C3D8I) 격자: f(u,v,w), u,v,w ∈ [0,1]. 2차는 중간 절점을 사상 위에 둔다(곡면 정확).
    /// 반환 절점 인덱스 함수 Grid(i,j,k)(2차 격자 인덱스 0..2n).
    /// </summary>
    public static FeMesh Block(int nx, int ny, int nz, Func<double, double, double, Vec3> f, bool quadratic = true)
    {
        var m = new FeMesh { Source = $"구조 격자 {(quadratic ? "C3D20R" : "C3D8I")} {nx}×{ny}×{nz}" };
        int q = quadratic ? 2 : 1;
        var ids = new Dictionary<(int, int, int), int>();
        int Id(int i, int j, int k)
        {
            if (ids.TryGetValue((i, j, k), out var id)) return id;
            m.Nodes.Add(f((double)i / (q * nx), (double)j / (q * ny), (double)k / (q * nz)));
            return ids[(i, j, k)] = m.Nodes.Count - 1;
        }
        for (int k = 0; k < nz; k++)
            for (int j = 0; j < ny; j++)
                for (int i = 0; i < nx; i++)
                {
                    int I = q * i, J = q * j, K = q * k;
                    var c = new[]
                    {
                        Id(I, J, K), Id(I + q, J, K), Id(I + q, J + q, K), Id(I, J + q, K),
                        Id(I, J, K + q), Id(I + q, J, K + q), Id(I + q, J + q, K + q), Id(I, J + q, K + q),
                    };
                    if (!quadratic) { m.Elements.Add(new FeElement(FeType.C3D8I, c)); continue; }
                    var mid = new[]
                    {
                        Id(I + 1, J, K), Id(I + 2, J + 1, K), Id(I + 1, J + 2, K), Id(I, J + 1, K),
                        Id(I + 1, J, K + 2), Id(I + 2, J + 1, K + 2), Id(I + 1, J + 2, K + 2), Id(I, J + 1, K + 2),
                        Id(I, J, K + 1), Id(I + 2, J, K + 1), Id(I + 2, J + 2, K + 1), Id(I, J + 2, K + 1),
                    };
                    m.Elements.Add(new FeElement(FeType.C3D20R, c.Concat(mid).ToArray()));
                }
        return m;
    }

    /// <summary>직육면체 블록 [0,L]×[−b/2,b/2]×[−h/2,h/2] (외팔보·기둥·단순보 검증).</summary>
    public static FeMesh Beam(double length, double width, double height, int nl, int nw, int nh, bool quadratic = true) =>
        Block(nl, nw, nh, (u, v, w) => new Vec3(u * length, (v - 0.5) * width, (w - 0.5) * height), quadratic);

    /// <summary>사상 사변형 8절점(S8R 셸 또는 CPS8 평면응력) 격자: f(u,v). 면 1=v0, 2=u1, 3=v1, 4=u0 변.</summary>
    public static FeMesh Quad8(int nu, int nv, Func<double, double, Vec3> f, FeType type, double thickness)
    {
        if (type is not (FeType.S8R or FeType.CPS8)) throw new ArgumentException("S8R 또는 CPS8 만 지원합니다.", nameof(type));
        var m = new FeMesh { Thickness = thickness, Source = $"구조 격자 {type} {nu}×{nv}" };
        var ids = new Dictionary<(int, int), int>();
        int Id(int i, int j)
        {
            if (ids.TryGetValue((i, j), out var id)) return id;
            m.Nodes.Add(f((double)i / (2 * nu), (double)j / (2 * nv)));
            return ids[(i, j)] = m.Nodes.Count - 1;
        }
        for (int j = 0; j < nv; j++)
            for (int i = 0; i < nu; i++)
            {
                int I = 2 * i, J = 2 * j;
                m.Elements.Add(new FeElement(type, [Id(I, J), Id(I + 2, J), Id(I + 2, J + 2), Id(I, J + 2), Id(I + 1, J), Id(I + 2, J + 1), Id(I + 1, J + 2), Id(I, J + 1)]));
            }
        return m;
    }

    /// <summary>다른 메시를 붙이고 허용오차 안의 절점을 병합(블록 이음).</summary>
    public void Merge(FeMesh other, double tol)
    {
        var map = new int[other.Nodes.Count];
        var hash = new SpatialHash(Nodes, Math.Max(tol * 10, 1e-12));
        for (int i = 0; i < other.Nodes.Count; i++)
        {
            int near = hash.Within(other.Nodes[i], tol).DefaultIfEmpty(-1).First();
            if (near >= 0) map[i] = near;
            else { Nodes.Add(other.Nodes[i]); map[i] = Nodes.Count - 1; hash.Add(Nodes.Count - 1); }
        }
        foreach (var e in other.Elements) Elements.Add(e with { N = e.N.Select(n => map[n]).ToArray() });
        Thickness ??= other.Thickness;
    }

    /// <summary>허용오차 안에서 겹친 절점을 하나로(닫힌 원통 이음매 등). 쓰이지 않게 된 절점은 남는다(덱에는 안 씀).</summary>
    public void WeldCoincident(double tol)
    {
        var hash = new SpatialHash(Nodes, Math.Max(tol * 10, 1e-12), Array.Empty<int>());
        var map = new int[Nodes.Count];
        for (int i = 0; i < Nodes.Count; i++)
        {
            int near = hash.Within(Nodes[i], tol).DefaultIfEmpty(-1).First();
            if (near >= 0) map[i] = near;
            else { map[i] = i; hash.Add(i); }
        }
        for (int e = 0; e < Elements.Count; e++) Elements[e] = Elements[e] with { N = Elements[e].N.Select(n => map[n]).ToArray() };
        foreach (var k in NodeSets.Keys.ToList()) NodeSets[k] = NodeSets[k].Select(n => map[n]).Distinct().ToList();
    }

    /// <summary>
    /// 삼각형 표면 메시 → S6 셸(중간 절점 추가, 평면 삼각형). 요소 i = 삼각형 i(표시 메시 면 색 1:1).
    /// 정점은 허용오차로 용접해 연결한다.
    /// </summary>
    public static FeMesh ShellFromTriangles(TriMesh mesh, double thickness)
    {
        if (mesh.TriangleCount == 0) throw new InvalidDataException("셸 메시가 비었습니다.");
        if (!(thickness > 0)) throw new InvalidDataException("셸 두께가 양수여야 합니다.");
        var (min, max) = mesh.Bounds();
        double tol = Math.Max((max - min).Norm * 1e-7, 1e-9);
        var m = new FeMesh { Thickness = thickness, Source = $"표시 메시 → S6 셸 {mesh.TriangleCount}" };
        var corner = new Dictionary<(long, long, long), int>();
        var mids = new Dictionary<(int, int), int>();
        int C(Vec3 p)
        {
            var key = ((long)Math.Round(p.X / tol), (long)Math.Round(p.Y / tol), (long)Math.Round(p.Z / tol));
            if (corner.TryGetValue(key, out var id)) return id;
            m.Nodes.Add(p);
            return corner[key] = m.Nodes.Count - 1;
        }
        int Mid(int a, int b)
        {
            var key = a < b ? (a, b) : (b, a);
            if (mids.TryGetValue(key, out var id)) return id;
            m.Nodes.Add((m.Nodes[a] + m.Nodes[b]) * 0.5);
            return mids[key] = m.Nodes.Count - 1;
        }
        for (int i = 0; i < mesh.TriangleCount; i++)
        {
            int a = C(mesh.Vertices[3 * i]), b = C(mesh.Vertices[3 * i + 1]), c = C(mesh.Vertices[3 * i + 2]);
            if (a == b || b == c || a == c) throw new InvalidDataException($"퇴화 삼각형(면 {i}) — 셸 요소를 만들 수 없습니다.");
            m.Elements.Add(new FeElement(FeType.S6, [a, b, c, Mid(a, b), Mid(b, c), Mid(c, a)]));
        }
        return m;
    }

    /// <summary>
    /// 복셀 대체 메셔(Gmsh 가 없을 때): 닫힌 표면 메시 내부(광선 홀짝 판정)의 정육면체 셀 → C3D8I.
    /// 여러 바디가 겹친 조립체도 합집합 하나로 결합된다. 표면은 계단 모양이므로 응력 집중은 과대·부정확(경고 대상).
    /// </summary>
    public static FeMesh Voxel(TriMesh surface, double h, int maxCells = 200_000)
    {
        if (surface.TriangleCount == 0) throw new InvalidDataException("복셀 메시: 표면 메시가 비었습니다.");
        if (!(h > 0)) throw new InvalidDataException("복셀 크기가 양수여야 합니다.");
        var (min, max) = surface.Bounds();
        var size = max - min;
        // 부동소수 오차로 칸이 하나 늘지 않게(0.4/0.01 = 40.000000000000007)
        int N(double len) => Math.Max(1, (int)Math.Ceiling(len / h - 1e-9));
        int nx = N(size.X), ny = N(size.Y), nz = N(size.Z);
        if ((long)nx * ny * nz > maxCells * 8L) throw new InvalidDataException($"복셀 격자가 너무 큽니다({nx}×{ny}×{nz}) — 크기 h 를 키우세요.");
        var o = min + new Vec3(nx * h - size.X, ny * h - size.Y, nz * h - size.Z) * -0.5;
        // z 방향 광선: 셀 중심 (x,y) 마다 교차 z 목록
        var inside = new bool[nx, ny, nz];
        int count = 0;
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < ny; j++)
            {
                double x = o.X + (i + 0.5) * h + h * 1e-4, y = o.Y + (j + 0.5) * h + h * 1.3e-4;
                var hits = new List<double>();
                for (int t = 0; t < surface.TriangleCount; t++)
                {
                    Vec3 a = surface.Vertices[3 * t], b = surface.Vertices[3 * t + 1], c = surface.Vertices[3 * t + 2];
                    double d = (b.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (b.Y - a.Y);
                    if (Math.Abs(d) < 1e-30) continue;
                    double l1 = ((b.X - x) * (c.Y - y) - (c.X - x) * (b.Y - y)) / d;
                    double l2 = ((c.X - x) * (a.Y - y) - (a.X - x) * (c.Y - y)) / d;
                    double l3 = 1 - l1 - l2;
                    if (l1 < 0 || l2 < 0 || l3 < 0) continue;
                    hits.Add(l1 * a.Z + l2 * b.Z + l3 * c.Z);
                }
                hits.Sort();
                for (int k = 0; k < nz; k++)
                {
                    double z = o.Z + (k + 0.5) * h;
                    int below = 0;
                    foreach (var hz in hits) { if (hz < z) below++; else break; }
                    if ((below & 1) == 1) { inside[i, j, k] = true; count++; }
                }
            }
        if (count == 0) throw new InvalidDataException("복셀 메시: 내부 셀이 없습니다(닫힌 메시가 아니거나 h 가 너무 큼).");
        // 면(6-이웃)으로 이어진 가장 큰 덩어리만 — 모서리·꼭짓점으로만 닿은 셀은 힌지(기구)가 되어 강성 행렬이 특이해진다
        // (실모델 조립체 STL 에서 실측: 변위 2 km, 반력 24 N)
        {
            var label = new int[nx, ny, nz];
            int best = 0, bestCount = 0, cur = 0;
            var stack = new Stack<(int, int, int)>();
            for (int k = 0; k < nz; k++)
                for (int j = 0; j < ny; j++)
                    for (int i = 0; i < nx; i++)
                    {
                        if (!inside[i, j, k] || label[i, j, k] != 0) continue;
                        int n = 0; cur++;
                        stack.Push((i, j, k)); label[i, j, k] = cur;
                        while (stack.Count > 0)
                        {
                            var (a, b, c) = stack.Pop(); n++;
                            foreach (var (da, db, dc) in new[] { (1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1) })
                            {
                                int x = a + da, y = b + db, z = c + dc;
                                if (x < 0 || y < 0 || z < 0 || x >= nx || y >= ny || z >= nz || !inside[x, y, z] || label[x, y, z] != 0) continue;
                                label[x, y, z] = cur; stack.Push((x, y, z));
                            }
                        }
                        if (n > bestCount) { bestCount = n; best = cur; }
                    }
            for (int k = 0; k < nz; k++)
                for (int j = 0; j < ny; j++)
                    for (int i = 0; i < nx; i++)
                        if (inside[i, j, k] && label[i, j, k] != best) { inside[i, j, k] = false; count--; }
        }
        if (count > maxCells) throw new InvalidDataException($"복셀 셀 {count:N0}개 > 한도 {maxCells:N0} — h 를 키우세요.");
        // 2차 완전적분 C3D20(굽힘에 강하고 모래시계 모드 없음). C3D8I 는 ccx 고유진동에서 가짜 음의 고유값(2요소 시험),
        // C3D20R(감차적분)은 한 요소 두께 사슬(스파이크·날개)에서 모래시계 모드 → 음의 고유값(실모델 실측)이라 쓰지 않는다.
        var m = new FeMesh { Source = $"복셀 C3D20 h={h * 1000:G4} mm, {count:N0} 셀(Gmsh 대체)" };
        var ids = new Dictionary<(int, int, int), int>();
        int Id(int i, int j, int k) // 반 격자 인덱스(2배)
        {
            if (ids.TryGetValue((i, j, k), out var id)) return id;
            m.Nodes.Add(o + new Vec3(i * h / 2, j * h / 2, k * h / 2));
            return ids[(i, j, k)] = m.Nodes.Count - 1;
        }
        for (int k = 0; k < nz; k++)
            for (int j = 0; j < ny; j++)
                for (int i = 0; i < nx; i++)
                {
                    if (!inside[i, j, k]) continue;
                    int I = 2 * i, J = 2 * j, K = 2 * k;
                    m.Elements.Add(new FeElement(FeType.C3D20,
                    [
                        Id(I, J, K), Id(I + 2, J, K), Id(I + 2, J + 2, K), Id(I, J + 2, K),
                        Id(I, J, K + 2), Id(I + 2, J, K + 2), Id(I + 2, J + 2, K + 2), Id(I, J + 2, K + 2),
                        Id(I + 1, J, K), Id(I + 2, J + 1, K), Id(I + 1, J + 2, K), Id(I, J + 1, K),
                        Id(I + 1, J, K + 2), Id(I + 2, J + 1, K + 2), Id(I + 1, J + 2, K + 2), Id(I, J + 1, K + 2),
                        Id(I, J, K + 1), Id(I + 2, J, K + 1), Id(I + 2, J + 2, K + 1), Id(I, J + 2, K + 1),
                    ]));
                }
        m.KeepLargestConnected();
        return m;
    }

    /// <summary>절점을 공유하는 가장 큰 연결 요소 집합만 남긴다(떠 있는 조각 → 강성 행렬 특이 방지). 제거한 요소 수 반환.</summary>
    public int KeepLargestConnected()
    {
        int ne = Elements.Count;
        if (ne == 0) return 0;
        var parent = Enumerable.Range(0, Nodes.Count).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        foreach (var e in Elements) for (int k = 1; k < e.N.Length; k++) { int a = Find(e.N[0]), b = Find(e.N[k]); if (a != b) parent[a] = b; }
        var groups = Elements.GroupBy(e => Find(e.N[0])).OrderByDescending(g => g.Count()).ToList();
        if (groups.Count <= 1) return 0;
        var keep = groups[0].ToList();
        Elements.Clear(); Elements.AddRange(keep);
        return ne - keep.Count;
    }

    /// <summary>
    /// Abaqus/CalculiX .inp 메시 읽기(*NODE, *ELEMENT TYPE=C3D4/C3D10/C3D8/C3D8I/C3D20R/S6/S8R). 다른 키워드는 무시.
    /// Gmsh 도구(tools/fea/mesh_geometry.py)의 출력 형식.
    /// </summary>
    public static FeMesh ReadInp(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"메시 파일이 없습니다: {path}");
        var m = new FeMesh { Source = $"inp {Path.GetFileName(path)}" };
        var idOf = new Dictionary<long, int>();
        string mode = ""; FeType type = FeType.C3D10;
        var pending = new List<long>();
        var pendingElems = new List<long[]>();
        int line = 0;
        foreach (var raw in File.ReadLines(path))
        {
            line++;
            var s = raw.Trim();
            if (s.Length == 0 || s.StartsWith("**")) continue;
            if (s.StartsWith('*'))
            {
                var kw = s.Split(',')[0].Trim().ToUpperInvariant();
                mode = kw switch { "*NODE" => "node", "*ELEMENT" => "elem", _ => "" };
                if (mode == "elem")
                {
                    var t = s.Split(',').Select(p => p.Trim()).FirstOrDefault(p => p.StartsWith("TYPE=", StringComparison.OrdinalIgnoreCase))?[5..];
                    if (t is null || !Enum.TryParse(t.ToUpperInvariant(), out type))
                        throw new InvalidDataException($"{path}:{line} 지원하지 않는 요소 종류 '{t}'");
                }
                continue;
            }
            var parts = s.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (mode == "node")
            {
                if (parts.Length < 4) throw new InvalidDataException($"{path}:{line} 절점 줄이 손상되었습니다.");
                long id = long.Parse(parts[0], CultureInfo.InvariantCulture);
                var p = new Vec3(double.Parse(parts[1], CultureInfo.InvariantCulture), double.Parse(parts[2], CultureInfo.InvariantCulture), double.Parse(parts[3], CultureInfo.InvariantCulture));
                if (!p.IsFinite) throw new InvalidDataException($"{path}:{line} 절점 좌표가 유한하지 않습니다.");
                idOf[id] = m.Nodes.Count;
                m.Nodes.Add(p);
            }
            else if (mode == "elem")
            {
                // 연속 줄 지원: 첫 값은 요소 번호, 나머지는 절점(필요 개수까지 이어 읽음)
                foreach (var tok in parts) pending.Add(long.Parse(tok, CultureInfo.InvariantCulture));
                int need = NodeCount(type) + 1;
                if (pending.Count >= need)
                {
                    pendingElems.Add(pending.Take(need).ToArray());
                    var nodes = pending.Skip(1).Take(need - 1).Select(n => idOf.TryGetValue(n, out var ix) ? ix : throw new InvalidDataException($"{path}:{line} 없는 절점 {n}")).ToArray();
                    m.Elements.Add(new FeElement(type, nodes));
                    pending.Clear();
                }
            }
        }
        if (m.Nodes.Count == 0 || m.Elements.Count == 0) throw new InvalidDataException($"{path}: 절점/요소가 없습니다.");
        return m;
    }
}

/// <summary>외곽 면: 요소 인덱스, Abaqus 면 번호(1 기반), 바깥 단위 법선, 면적, 중심, 모서리 절점.</summary>
public sealed record FeFace(int Element, int Face, Vec3 Normal, double Area, Vec3 Center, int[] Corners);

/// <summary>균일 격자 공간 해시(최근접 점 질의).</summary>
public sealed class SpatialHash
{
    private readonly IReadOnlyList<Vec3> _pts;
    private readonly double _h;
    private readonly Dictionary<(long, long, long), List<int>> _cells = new();

    public SpatialHash(IReadOnlyList<Vec3> pts, double cell, IEnumerable<int>? subset = null)
    {
        _pts = pts; _h = cell;
        foreach (var i in subset ?? Enumerable.Range(0, pts.Count)) Add(i);
    }

    private (long, long, long) Key(Vec3 p) => ((long)Math.Floor(p.X / _h), (long)Math.Floor(p.Y / _h), (long)Math.Floor(p.Z / _h));

    public void Add(int i)
    {
        var k = Key(_pts[i]);
        if (!_cells.TryGetValue(k, out var l)) _cells[k] = l = new List<int>();
        l.Add(i);
    }

    /// <summary>가장 가까운 점(없으면 −1). 셀을 넓혀 가며 찾는다(최대 maxRings 고리 — 셀 크기는 점 간격 수준으로).</summary>
    public int Nearest(Vec3 p, out double dist, int maxRings = 12)
    {
        var (cx, cy, cz) = Key(p);
        int best = -1; double bd = double.PositiveInfinity;
        for (int r = 0; r <= maxRings; r++)
        {
            for (long i = cx - r; i <= cx + r; i++)
                for (long j = cy - r; j <= cy + r; j++)
                    for (long k = cz - r; k <= cz + r; k++)
                    {
                        if (Math.Max(Math.Abs(i - cx), Math.Max(Math.Abs(j - cy), Math.Abs(k - cz))) != r) continue;
                        if (!_cells.TryGetValue((i, j, k), out var l)) continue;
                        foreach (var q in l)
                        {
                            double d = (_pts[q] - p).NormSq;
                            if (d < bd) { bd = d; best = q; }
                        }
                    }
            // r 고리까지 봤으면 r·h 안의 점은 모두 확인됨
            if (best >= 0 && Math.Sqrt(bd) <= r * _h) break;
            if (_cells.Count == 0) break;
        }
        dist = Math.Sqrt(bd);
        return best;
    }

    /// <summary>반경 안의 점.</summary>
    public IEnumerable<int> Within(Vec3 p, double radius)
    {
        var (cx, cy, cz) = Key(p);
        int r = (int)Math.Ceiling(radius / _h);
        double r2 = radius * radius;
        for (long i = cx - r; i <= cx + r; i++)
            for (long j = cy - r; j <= cy + r; j++)
                for (long k = cz - r; k <= cz + r; k++)
                    if (_cells.TryGetValue((i, j, k), out var l))
                        foreach (var q in l)
                            if ((_pts[q] - p).NormSq <= r2) yield return q;
    }
}
