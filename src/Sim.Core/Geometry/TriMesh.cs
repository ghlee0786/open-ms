using System.Globalization;
using System.Text;
using Sim.Core.Numerics;

namespace Sim.Core.Geometry;

/// <summary>삼각형 메시(정점 공유 없음, 3정점=1삼각형). 좌표 단위는 로더가 결정.</summary>
public sealed class TriMesh
{
    public List<Vec3> Vertices { get; } = new();
    public int TriangleCount => Vertices.Count / 3;

    public (Vec3 Min, Vec3 Max) Bounds()
    {
        var min = new Vec3(double.MaxValue, double.MaxValue, double.MaxValue);
        var max = new Vec3(double.MinValue, double.MinValue, double.MinValue);
        foreach (var v in Vertices) { min = Vec3.Min(min, v); max = Vec3.Max(max, v); }
        return (min, max);
    }

    public TriMesh Transformed(Func<Vec3, Vec3> f)
    {
        var m = new TriMesh();
        foreach (var v in Vertices) m.Vertices.Add(f(v));
        return m;
    }

    /// <summary>STL(바이너리/ASCII) 로드.</summary>
    public static TriMesh LoadStl(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var mesh = new TriMesh();
        if (bytes.Length >= 84)
        {
            uint n = BitConverter.ToUInt32(bytes, 80);
            if (84 + (long)n * 50 == bytes.Length)
            {
                for (int i = 0; i < n; i++)
                {
                    int o = 84 + i * 50 + 12;
                    for (int k = 0; k < 3; k++)
                    {
                        mesh.Vertices.Add(new Vec3(
                            BitConverter.ToSingle(bytes, o + k * 12),
                            BitConverter.ToSingle(bytes, o + k * 12 + 4),
                            BitConverter.ToSingle(bytes, o + k * 12 + 8)));
                    }
                }
                return mesh;
            }
        }
        var text = Encoding.ASCII.GetString(bytes);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("vertex", StringComparison.OrdinalIgnoreCase)) continue;
            var p = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 4
                || !double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                || !double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
                || !double.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
                throw new InvalidDataException($"STL 정점 줄이 손상되었습니다: '{line}' ({path})");
            mesh.Vertices.Add(new Vec3(x, y, z));
        }
        if (mesh.Vertices.Count == 0 || mesh.Vertices.Count % 3 != 0)
            throw new InvalidDataException($"STL 을 읽을 수 없습니다: {path}");
        return mesh;
    }

    public void SaveBinaryStl(string path)
    {
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write(new byte[80]);
        w.Write((uint)TriangleCount);
        for (int i = 0; i < TriangleCount; i++)
        {
            var a = Vertices[3 * i]; var b = Vertices[3 * i + 1]; var c = Vertices[3 * i + 2];
            var n = Vec3.Cross(b - a, c - a).Normalized();
            foreach (var v in new[] { n, a, b, c })
            {
                w.Write((float)v.X); w.Write((float)v.Y); w.Write((float)v.Z);
            }
            w.Write((ushort)0);
        }
    }

    /// <summary>
    /// 균일 밀도 가정 질량특성(부호 있는 사면체 적분). 메시는 닫혀 있고 바깥 법선이어야 한다.
    /// 반환 관성은 CG 기준, 메시 좌표축 기준 텐서.
    /// </summary>
    public (double Volume, Vec3 Centroid, Mat3 InertiaPerDensity) VolumeProperties()
    {
        double vol = 0;
        Vec3 first = Vec3.Zero;
        // 공분산 C = ∫ x xᵀ dV
        double c11 = 0, c22 = 0, c33 = 0, c12 = 0, c13 = 0, c23 = 0;
        for (int i = 0; i < TriangleCount; i++)
        {
            var a = Vertices[3 * i]; var b = Vertices[3 * i + 1]; var c = Vertices[3 * i + 2];
            double det = Vec3.Dot(a, Vec3.Cross(b, c));
            vol += det / 6;
            first += (a + b + c) * (det / 24);
            // ∫ x_i x_j over tet(0,a,b,c) = det/120 * (Σ_k p_ki p_kj + (Σ p_ki)(Σ p_kj))
            var s = a + b + c;
            c11 += det / 120 * (a.X * a.X + b.X * b.X + c.X * c.X + s.X * s.X);
            c22 += det / 120 * (a.Y * a.Y + b.Y * b.Y + c.Y * c.Y + s.Y * s.Y);
            c33 += det / 120 * (a.Z * a.Z + b.Z * b.Z + c.Z * c.Z + s.Z * s.Z);
            c12 += det / 120 * (a.X * a.Y + b.X * b.Y + c.X * c.Y + s.X * s.Y);
            c13 += det / 120 * (a.X * a.Z + b.X * b.Z + c.X * c.Z + s.X * s.Z);
            c23 += det / 120 * (a.Y * a.Z + b.Y * b.Z + c.Y * c.Z + s.Y * s.Z);
        }
        if (vol < 0)
        {
            vol = -vol; first = -first;
            c11 = -c11; c22 = -c22; c33 = -c33; c12 = -c12; c13 = -c13; c23 = -c23;
        }
        if (vol < 1e-18) throw new InvalidDataException("메시 부피가 0 입니다(닫힌 메시가 아님).");
        var cg = first / vol;
        // 원점 기준 관성(밀도 1): I = tr(C)E − C
        double tr = c11 + c22 + c33;
        var io = new Mat3(tr - c11, -c12, -c13, -c12, tr - c22, -c23, -c13, -c23, tr - c33);
        var icg = io + Mat3.ParallelAxis(vol, cg) * -1;
        return (vol, cg, icg);
    }

    /// <summary>지면 접촉 판정용 대표점(볼록 껍질 근사): 여러 방향의 극점 정점.</summary>
    public List<Vec3> SupportPoints(int directions = 98)
    {
        var pts = new List<Vec3>();
        if (Vertices.Count == 0) return pts;
        int n = (int)Math.Ceiling(Math.Sqrt(directions / 2.0));
        var set = new HashSet<Vec3>();
        for (int i = 0; i <= n; i++)
        {
            double th = Math.PI * i / n;
            int m = Math.Max(1, (int)Math.Round(2 * n * Math.Sin(th)));
            for (int j = 0; j < m; j++)
            {
                double ph = 2 * Math.PI * j / m;
                var d = new Vec3(Math.Sin(th) * Math.Cos(ph), Math.Sin(th) * Math.Sin(ph), Math.Cos(th));
                Vec3 best = Vertices[0]; double bd = double.MinValue;
                foreach (var v in Vertices)
                {
                    double dd = Vec3.Dot(v, d);
                    if (dd > bd) { bd = dd; best = v; }
                }
                set.Add(best);
            }
        }
        pts.AddRange(set);
        return pts;
    }

    /// <summary>
    /// 가장 긴 변 이분할로 모든 변이 maxEdge 이하가 되게 세분(응력·압력 색 표시용, 형상 불변).
    /// 삼각형 수가 maxTriangles 에 닿으면 멈춘다(큰 Fusion 메시 보호).
    /// </summary>
    public TriMesh Subdivided(double maxEdge, int maxTriangles = 60000)
    {
        var src = new List<(Vec3 A, Vec3 B, Vec3 C)>(TriangleCount);
        for (int i = 0; i < TriangleCount; i++) src.Add((Vertices[3 * i], Vertices[3 * i + 1], Vertices[3 * i + 2]));
        if (maxEdge <= 0) return this;
        double m2 = maxEdge * maxEdge;
        // 너비 우선(큐): 상한에 닿아도 세분이 메시 전체에 고르게 퍼지도록
        var queue = new Queue<(Vec3 A, Vec3 B, Vec3 C)>(src);
        var outList = new List<(Vec3, Vec3, Vec3)>(src.Count * 2);
        while (queue.Count > 0)
        {
            var (a, b, c) = queue.Dequeue();
            double ab = (b - a).NormSq, bc = (c - b).NormSq, ca = (a - c).NormSq;
            double mx = Math.Max(ab, Math.Max(bc, ca));
            if (mx <= m2 || outList.Count + queue.Count + 2 > maxTriangles) { outList.Add((a, b, c)); continue; }
            if (mx == ab) { var m = (a + b) / 2; queue.Enqueue((a, m, c)); queue.Enqueue((m, b, c)); }
            else if (mx == bc) { var m = (b + c) / 2; queue.Enqueue((a, b, m)); queue.Enqueue((a, m, c)); }
            else { var m = (c + a) / 2; queue.Enqueue((a, b, m)); queue.Enqueue((m, b, c)); }
        }
        var r = new TriMesh();
        foreach (var (a, b, c) in outList) r.Vertices.AddRange([a, b, c]);
        return r;
    }

    /// <summary>사각 패치(p00→p10→p11→p01, 바깥 법선 = (p10−p00)×(p01−p00))를 nu×nv 격자 삼각형으로.</summary>
    public void AddGrid(Vec3 p00, Vec3 p10, Vec3 p11, Vec3 p01, int nu, int nv)
    {
        nu = Math.Max(1, nu); nv = Math.Max(1, nv);
        Vec3 P(int i, int j)
        {
            double u = (double)i / nu, v = (double)j / nv;
            return p00 * ((1 - u) * (1 - v)) + p10 * (u * (1 - v)) + p11 * (u * v) + p01 * ((1 - u) * v);
        }
        for (int i = 0; i < nu; i++)
            for (int j = 0; j < nv; j++)
            {
                var a = P(i, j); var b = P(i + 1, j); var c = P(i + 1, j + 1); var d = P(i, j + 1);
                Vertices.AddRange([a, b, c, a, c, d]);
            }
    }

    /// <summary>구조 격자 상자(면마다 cell ≈ h).</summary>
    public static TriMesh BoxGrid(Vec3 size, Vec3 center, double h)
    {
        var m = new TriMesh();
        var s = size / 2;
        int nx = Math.Max(1, (int)Math.Ceiling(size.X / h)), ny = Math.Max(1, (int)Math.Ceiling(size.Y / h)), nz = Math.Max(1, (int)Math.Ceiling(size.Z / h));
        Vec3 P(int x, int y, int z) => center + new Vec3(x * s.X, y * s.Y, z * s.Z);
        m.AddGrid(P(1, -1, -1), P(1, 1, -1), P(1, 1, 1), P(1, -1, 1), ny, nz);     // +x
        m.AddGrid(P(-1, -1, -1), P(-1, -1, 1), P(-1, 1, 1), P(-1, 1, -1), nz, ny);  // −x
        m.AddGrid(P(-1, 1, -1), P(-1, 1, 1), P(1, 1, 1), P(1, 1, -1), nz, nx);      // +y
        m.AddGrid(P(-1, -1, -1), P(1, -1, -1), P(1, -1, 1), P(-1, -1, 1), nx, nz);  // −y
        m.AddGrid(P(-1, -1, 1), P(1, -1, 1), P(1, 1, 1), P(-1, 1, 1), nx, ny);      // +z
        m.AddGrid(P(-1, -1, -1), P(-1, 1, -1), P(1, 1, -1), P(1, -1, -1), ny, nx);  // −z
        return m;
    }

    /// <summary>구조 격자 원통(둘레 seg, 길이 nl 등분, 끝면은 동심 고리 nr).</summary>
    public static TriMesh CylinderGrid(double radius, double length, int axis, Vec3 center, int seg, int nl, int nr)
    {
        var m = new TriMesh();
        double h = length / 2;
        Vec3 P(double r, double th, double c) => center + Shape.AxisMap(axis, r * Math.Cos(th), r * Math.Sin(th), c);
        for (int i = 0; i < seg; i++)
        {
            double t0 = 2 * Math.PI * i / seg, t1 = 2 * Math.PI * (i + 1) / seg;
            m.AddGrid(P(radius, t0, -h), P(radius, t1, -h), P(radius, t1, h), P(radius, t0, h), 1, nl);
            for (int k = 0; k < nr; k++)
            {
                double r0 = radius * k / nr, r1 = radius * (k + 1) / nr;
                if (k == 0)
                {
                    m.Vertices.AddRange([P(0, 0, h), P(r1, t0, h), P(r1, t1, h)]);
                    m.Vertices.AddRange([P(0, 0, -h), P(r1, t1, -h), P(r1, t0, -h)]);
                }
                else
                {
                    m.AddGrid(P(r0, t0, h), P(r1, t0, h), P(r1, t1, h), P(r0, t1, h), 1, 1);
                    m.AddGrid(P(r0, t0, -h), P(r0, t1, -h), P(r1, t1, -h), P(r1, t0, -h), 1, 1);
                }
            }
        }
        return m;
    }

    // ---- 기본 형상 생성(시각화·테스트용) ----

    public static TriMesh Box(Vec3 size, Vec3 center)
    {
        var h = size / 2;
        var m = new TriMesh();
        Vec3 P(int sx, int sy, int sz) => center + new Vec3(sx * h.X, sy * h.Y, sz * h.Z);
        void Quad(Vec3 a, Vec3 b, Vec3 c, Vec3 d) { m.Vertices.AddRange([a, b, c, a, c, d]); }
        Quad(P(1, -1, -1), P(1, 1, -1), P(1, 1, 1), P(1, -1, 1));
        Quad(P(-1, -1, -1), P(-1, -1, 1), P(-1, 1, 1), P(-1, 1, -1));
        Quad(P(-1, 1, -1), P(-1, 1, 1), P(1, 1, 1), P(1, 1, -1));
        Quad(P(-1, -1, -1), P(1, -1, -1), P(1, -1, 1), P(-1, -1, 1));
        Quad(P(-1, -1, 1), P(1, -1, 1), P(1, 1, 1), P(-1, 1, 1));
        Quad(P(-1, -1, -1), P(-1, 1, -1), P(1, 1, -1), P(1, -1, -1));
        return m;
    }

    /// <summary>축 axis(0=x,1=y,2=z) 원통.</summary>
    public static TriMesh Cylinder(double radius, double length, int axis, Vec3 center, int segments = 48)
    {
        var m = new TriMesh();
        Vec3 Map(double a, double b, double c) => axis switch
        {
            0 => center + new Vec3(c, a, b),
            1 => center + new Vec3(b, c, a),
            _ => center + new Vec3(a, b, c)
        };
        double h = length / 2;
        for (int i = 0; i < segments; i++)
        {
            double t0 = 2 * Math.PI * i / segments, t1 = 2 * Math.PI * (i + 1) / segments;
            double x0 = radius * Math.Cos(t0), y0 = radius * Math.Sin(t0);
            double x1 = radius * Math.Cos(t1), y1 = radius * Math.Sin(t1);
            var a0 = Map(x0, y0, -h); var a1 = Map(x1, y1, -h);
            var b0 = Map(x0, y0, h); var b1 = Map(x1, y1, h);
            m.Vertices.AddRange([a0, a1, b1, a0, b1, b0]);
            m.Vertices.AddRange([Map(0, 0, h), b0, b1]);
            m.Vertices.AddRange([Map(0, 0, -h), a1, a0]);
        }
        return m;
    }

    /// <summary>
    /// 원통 껍질 부채꼴(닫힌 솔리드, 바깥 법선): 바깥 반경 r, 두께 t, 길이 L, 중심각 폭 span[deg], 중앙 각 center[deg]
    /// (축 성분 a=cosθ, b=sinθ — 축 x 이면 +Y 에서 +Z 쪽).
    /// </summary>
    public static TriMesh ShellSegment(double radius, double thickness, double length, double spanDeg, double centerAngleDeg, int axis, Vec3 center, int segments = 0, int nl = 1)
    {
        var m = new TriMesh();
        double ro = radius, ri = radius - thickness, h = length / 2;
        double span = spanDeg * Math.PI / 180, th0 = centerAngleDeg * Math.PI / 180 - span / 2;
        int n = segments > 0 ? segments : Math.Max(4, (int)Math.Ceiling(spanDeg / 5));
        Vec3 P(double r, double th, double c) => center + Shape.AxisMap(axis, r * Math.Cos(th), r * Math.Sin(th), c);
        for (int i = 0; i < n; i++)
        {
            double t0 = th0 + span * i / n, t1 = th0 + span * (i + 1) / n;
            m.AddGrid(P(ro, t0, -h), P(ro, t1, -h), P(ro, t1, h), P(ro, t0, h), 1, nl);   // 바깥면 (+r)
            m.AddGrid(P(ri, t0, -h), P(ri, t0, h), P(ri, t1, h), P(ri, t1, -h), nl, 1);   // 안쪽면 (−r)
            m.AddGrid(P(ro, t0, h), P(ro, t1, h), P(ri, t1, h), P(ri, t0, h), 1, 1);      // 끝면 +c
            m.AddGrid(P(ro, t0, -h), P(ri, t0, -h), P(ri, t1, -h), P(ro, t1, -h), 1, 1);  // 끝면 −c
        }
        if (spanDeg < 360 - 1e-9)
        {
            double te = th0 + span;
            m.AddGrid(P(ri, th0, -h), P(ro, th0, -h), P(ro, th0, h), P(ri, th0, h), 1, nl); // 옆면 θmin (−θ)
            m.AddGrid(P(ri, te, -h), P(ri, te, h), P(ro, te, h), P(ro, te, -h), nl, 1);     // 옆면 θmax (+θ)
        }
        return m;
    }

    public static TriMesh Sphere(double radius, Vec3 center, int stacks = 16, int slices = 32)
    {
        var m = new TriMesh();
        Vec3 P(int i, int j)
        {
            double th = Math.PI * i / stacks, ph = 2 * Math.PI * j / slices;
            return center + new Vec3(radius * Math.Sin(th) * Math.Cos(ph), radius * Math.Sin(th) * Math.Sin(ph), radius * Math.Cos(th));
        }
        for (int i = 0; i < stacks; i++)
            for (int j = 0; j < slices; j++)
            {
                var a = P(i, j); var b = P(i + 1, j); var c = P(i + 1, j + 1); var d = P(i, j + 1);
                if (i != 0) m.Vertices.AddRange([a, b, d]);
                if (i != stacks - 1) m.Vertices.AddRange([b, c, d]);
            }
        return m;
    }
}
