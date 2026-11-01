using Sim.Core.Numerics;

namespace Sim.Core.Geometry;

public enum ShapeKind { Box, Cylinder, Sphere, Mesh, ShellSegment }

/// <summary>
/// 물체 형상(동체 기준좌표, 원점=모델 원점). 시각화·L1 공력(투영면적·형상 Cd)·지면 접촉 대표점·균일밀도 관성에 쓴다.
/// </summary>
public sealed class Shape
{
    public ShapeKind Kind { get; init; }
    /// <summary>Box: 변 길이 (x,y,z). Mesh: 바운딩박스 크기.</summary>
    public Vec3 Size { get; init; }
    public double Radius { get; init; }
    public double Length { get; init; }
    /// <summary>원통 축 0=x,1=y,2=z.</summary>
    public int Axis { get; init; }
    public Vec3 Center { get; init; }
    /// <summary>동체 좌표로 변환된 메시(Mesh 형상 또는 시각화 대체용).</summary>
    public TriMesh? Mesh { get; init; }
    public string? MeshPath { get; init; }
    /// <summary>ShellSegment: 벽 두께 [m] (Radius = 바깥 반경).</summary>
    public double Thickness { get; init; }
    /// <summary>ShellSegment: 부채꼴 중심각 폭 [deg].</summary>
    public double SpanDeg { get; init; }
    /// <summary>ShellSegment: 부채꼴 중앙 방사 방향 각 [deg] — 축 x 기준 +Y(0°)에서 +Z(90°) 쪽으로.</summary>
    public double CenterAngleDeg { get; init; }

    private List<Vec3>? _support;

    public static Shape FromMesh(TriMesh mesh, string? path)
    {
        var (min, max) = mesh.Bounds();
        return new Shape { Kind = ShapeKind.Mesh, Mesh = mesh, MeshPath = path, Size = max - min, Center = (min + max) / 2 };
    }

    /// <summary>
    /// 원통 껍질 부채꼴(운반 탄체 외피 조각, FR-SHL-01): 바깥 반경 r, 두께 t, 길이 L, 중심각 폭 span, 중앙 방향 각.
    /// Center = 원통 축 중심(조각 중심이 아님). 닫힌 메시를 만들어 균일 밀도 질량특성·접촉점에 쓴다.
    /// </summary>
    public static Shape ShellSegmentOf(double radius, double thickness, double length, double spanDeg, double centerAngleDeg, int axis, Vec3 center)
    {
        if (radius <= 0 || thickness <= 0 || thickness >= radius || length <= 0 || spanDeg <= 0 || spanDeg > 360)
            throw new InvalidDataException($"shellSegment: 0 < thickness({thickness}) < radius({radius}), length>0, 0<span≤360 이어야 합니다.");
        var mesh = TriMesh.ShellSegment(radius, thickness, length, spanDeg, centerAngleDeg, axis, center);
        var (min, max) = mesh.Bounds();
        return new Shape
        {
            Kind = ShapeKind.ShellSegment, Radius = radius, Thickness = thickness, Length = length, SpanDeg = spanDeg,
            CenterAngleDeg = centerAngleDeg, Axis = axis, Center = center, Mesh = mesh, Size = max - min,
        };
    }

    public TriMesh BuildMesh() => Kind switch
    {
        ShapeKind.Box => TriMesh.Box(Size, Center),
        ShapeKind.Cylinder => TriMesh.Cylinder(Radius, Length, Axis, Center),
        ShapeKind.Sphere => TriMesh.Sphere(Radius, Center),
        _ => Mesh!
    };

    /// <summary>축 성분(a,b = 단면, c = 축) → 동체 좌표 (TriMesh.Cylinder 와 같은 축 배치).</summary>
    internal static Vec3 AxisMap(int axis, double a, double b, double c) => axis switch
    {
        0 => new Vec3(c, a, b),
        1 => new Vec3(b, c, a),
        _ => new Vec3(a, b, c)
    };

    /// <summary>동체 좌표 → (a, b, c) 축 성분(AxisMap 의 역).</summary>
    internal static (double A, double B, double C) AxisUnmap(int axis, Vec3 v) => axis switch
    {
        0 => (v.Y, v.Z, v.X),
        1 => (v.Z, v.X, v.Y),
        _ => (v.X, v.Y, v.Z)
    };

    /// <summary>ShellSegment 중앙 바깥 방사 방향(동체 좌표 단위 벡터).</summary>
    public Vec3 SegmentNormal
    {
        get
        {
            double c = CenterAngleDeg * Math.PI / 180;
            return AxisMap(Axis, Math.Cos(c), Math.Sin(c), 0);
        }
    }

    /// <summary>
    /// 점 p(동체 좌표)가 형상 안에 있으면 true 와 침투 깊이·가장 가까운 면의 바깥 법선(동체 좌표).
    /// 물체 간 충돌 판정(FR-SHL-04) 근사용 — 메시는 바운딩 박스로 근사.
    /// </summary>
    public bool Penetration(Vec3 p, out double depth, out Vec3 normal)
    {
        depth = 0; normal = Vec3.Zero;
        switch (Kind)
        {
            case ShapeKind.Sphere:
            {
                var d = p - Center;
                double r = d.Norm;
                if (r >= Radius) return false;
                depth = Radius - r;
                normal = r > 1e-12 ? d / r : Vec3.UnitX;
                return true;
            }
            case ShapeKind.Cylinder:
            {
                var (a, b, c) = AxisUnmap(Axis, p - Center);
                double r = Math.Sqrt(a * a + b * b), h = Length / 2;
                if (r >= Radius || Math.Abs(c) >= h) return false;
                double dr = Radius - r, dc = h - Math.Abs(c);
                if (dr < dc) { depth = dr; normal = r > 1e-12 ? AxisMap(Axis, a / r, b / r, 0) : AxisMap(Axis, 1, 0, 0); }
                else { depth = dc; normal = AxisMap(Axis, 0, 0, Math.Sign(c) == 0 ? 1 : Math.Sign(c)); }
                return true;
            }
            case ShapeKind.ShellSegment:
            {
                var (a, b, c) = AxisUnmap(Axis, p - Center);
                double r = Math.Sqrt(a * a + b * b), h = Length / 2, ri = Radius - Thickness;
                if (r >= Radius || r <= ri || Math.Abs(c) >= h) return false;
                double th = Math.Atan2(b, a) - CenterAngleDeg * Math.PI / 180;
                th = Math.Atan2(Math.Sin(th), Math.Cos(th));
                double half = SpanDeg * Math.PI / 360;
                if (Math.Abs(th) >= half) return false;
                double dOut = Radius - r, dIn = r - ri, dEnd = h - Math.Abs(c), dSide = (half - Math.Abs(th)) * r;
                double m = Math.Min(Math.Min(dOut, dIn), Math.Min(dEnd, dSide));
                depth = m;
                var radial = AxisMap(Axis, a / r, b / r, 0);
                if (m == dOut) normal = radial;
                else if (m == dIn) normal = -radial;
                else if (m == dEnd) normal = AxisMap(Axis, 0, 0, Math.Sign(c) == 0 ? 1 : Math.Sign(c));
                else
                {
                    // 옆면: 접선 방향(각이 커지는 쪽 = +접선)
                    var tangent = AxisMap(Axis, -b / r, a / r, 0);
                    normal = th >= 0 ? tangent : -tangent;
                }
                return true;
            }
            default:
            {
                // Box · Mesh(바운딩 박스 근사)
                var d = p - Center;
                var hs = Size / 2;
                double dx = hs.X - Math.Abs(d.X), dy = hs.Y - Math.Abs(d.Y), dz = hs.Z - Math.Abs(d.Z);
                if (dx <= 0 || dy <= 0 || dz <= 0) return false;
                if (dx <= dy && dx <= dz) { depth = dx; normal = new Vec3(d.X >= 0 ? 1 : -1, 0, 0); }
                else if (dy <= dz) { depth = dy; normal = new Vec3(0, d.Y >= 0 ? 1 : -1, 0); }
                else { depth = dz; normal = new Vec3(0, 0, d.Z >= 0 ? 1 : -1); }
                return true;
            }
        }
    }

    /// <summary>형상 경계 구 중심(동체 좌표) — 근접·충돌 광역 판정.</summary>
    public Vec3 BoundingCenter
    {
        get
        {
            if (Kind != ShapeKind.ShellSegment || Mesh is null) return Center;
            if (_meshCenter is { } c) return c;
            var (min, max) = Mesh.Bounds();
            _meshCenter = (min + max) / 2;
            return _meshCenter.Value;
        }
    }

    private Vec3? _meshCenter;

    /// <summary>지면 접촉 대표점(동체 기준좌표).</summary>
    public IReadOnlyList<Vec3> SupportPoints()
    {
        if (_support != null) return _support;
        _support = Kind switch
        {
            ShapeKind.Box => Corners(Size, Center),
            ShapeKind.Sphere => TriMesh.Sphere(Radius, Center, 6, 12).SupportPoints(26),
            ShapeKind.Cylinder => TriMesh.Cylinder(Radius, Length, Axis, Center, 16).SupportPoints(62),
            // 껍질 조각은 오목하므로 극점만으로는 안쪽 면이 빠짐 → 메시 정점(중복 제거) 사용
            ShapeKind.ShellSegment => Mesh!.Vertices.Distinct().ToList(),
            _ => Mesh!.SupportPoints()
        };
        return _support;
    }

    /// <summary>상자 대표점: 꼭짓점 8 + 모서리 중점 12 + 면 중심 6 (면끼리 맞닿는 충돌도 잡도록, 지면 최저점은 꼭짓점과 같음).</summary>
    private static List<Vec3> Corners(Vec3 size, Vec3 c)
    {
        var l = new List<Vec3>();
        for (int i = 0; i < 8; i++)
            l.Add(c + new Vec3((i & 1) == 0 ? -size.X / 2 : size.X / 2, (i & 2) == 0 ? -size.Y / 2 : size.Y / 2, (i & 4) == 0 ? -size.Z / 2 : size.Z / 2));
        for (int x = -1; x <= 1; x++)
            for (int y = -1; y <= 1; y++)
                for (int z = -1; z <= 1; z++)
                {
                    int zeros = (x == 0 ? 1 : 0) + (y == 0 ? 1 : 0) + (z == 0 ? 1 : 0);
                    if (zeros is 1 or 2) l.Add(c + new Vec3(x * size.X / 2, y * size.Y / 2, z * size.Z / 2));
                }
        return l;
    }

    /// <summary>경계 구 반경(근접 판정용).</summary>
    public double BoundingRadius => Kind switch
    {
        ShapeKind.Sphere => Radius,
        ShapeKind.Cylinder => Math.Sqrt(Radius * Radius + Length * Length / 4),
        _ => Size.Norm / 2
    };

    // 곡면 판(외피 조각) L1 Cd (FR-SHL-05): Hoerner, Fluid-Dynamic Drag (1965) 3장 반원통 껍질 값
    // (오목면 정면 2.3 / 볼록면 정면 1.2, 무한 길이)에 유한 길이·얕은 120° 곡률 보정(원통 횡류 표 비 ≈ 0.7)을 곱한 근사 — (가정, 검증 전).
    public const double ShellCdConcave = 1.6, ShellCdConvex = 0.85, ShellCdEdge = 1.0;

    /// <summary>
    /// L1 형상 Cd 의 적용 범위(VV-32): 아임계 레이놀즈수. 매끈한 구·원통은 Re ≈ 2–4×10⁵ 에서 항력 위기(Cd 급감)가 있어
    /// 이 범위 위의 상수 Cd 는 항력을 과대 추정한다(공개 자료 reference_drag_curves 로 확인).
    /// </summary>
    public const double MaxValidReynolds = 2.0e5;

    /// <summary>
    /// 동체 기준 단위 유입방향 d 에 대한 (투영면적, 형상 Cd) — L1 형상 근사(FR-AER-08).
    /// 박스: 면별 투영 합, Cd≈1.05. 원통: 축류 Cd≈0.82(L/D≥2), 횡류 = 유한 길이 표 <see cref="CylinderCrossflowCd"/>.
    /// 구: Cd≈0.47. 메시: 바운딩박스 근사.
    /// </summary>
    public (double Area, double Cd) Projected(Vec3 d)
    {
        switch (Kind)
        {
            case ShapeKind.Sphere:
                return (Math.PI * Radius * Radius, 0.47);
            case ShapeKind.Cylinder:
            {
                double ca = Math.Abs(d[Axis]);
                double sa = Math.Sqrt(Math.Max(0, 1 - ca * ca));
                double aAx = Math.PI * Radius * Radius, aCross = 2 * Radius * Length;
                double area = ca * aAx + sa * aCross;
                double cdx = CylinderCrossflowCd(Length / (2 * Radius));
                double cd = area > 0 ? (0.82 * ca * aAx + cdx * sa * aCross) / area : 1.0;
                return (area, cd);
            }
            case ShapeKind.ShellSegment:
            {
                // 조각 국부축: e_n(중앙 바깥 방사, 볼록면), e_t(접선), e_x(원통 축)
                var en = SegmentNormal;
                var ex = AxisMap(Axis, 0, 0, 1);
                var et = Vec3.Cross(ex, en);
                double half = SpanDeg * Math.PI / 360;
                double chord = 2 * Radius * Math.Sin(Math.Min(half, Math.PI / 2));
                double sag = Radius * (1 - Math.Cos(Math.Min(half, Math.PI))) + Thickness;
                double dn = Vec3.Dot(d, en), dt = Vec3.Dot(d, et), dx = Vec3.Dot(d, ex);
                double an = Math.Abs(dn) * Length * chord, at = Math.Abs(dt) * Length * sag, ax = Math.Abs(dx) * chord * sag * 0.67;
                double area = an + at + ax;
                // d = 대기 대비 물체 이동 방향: dn > 0 이면 볼록면이 앞(바람이 볼록면에), dn < 0 이면 오목면이 바람을 받음
                double cn = dn >= 0 ? ShellCdConvex : ShellCdConcave;
                double cd = area > 0 ? (cn * an + ShellCdEdge * (at + ax)) / area : 1.0;
                return (area, cd);
            }
            default:
            {
                var s = Size;
                double area = Math.Abs(d.X) * s.Y * s.Z + Math.Abs(d.Y) * s.X * s.Z + Math.Abs(d.Z) * s.X * s.Y;
                return (area, 1.05);
            }
        }
    }

    /// <summary>
    /// 원통 투영 항력(FR-REL-06 ①, aero.crossflow "tr-r300"): 횡류 성분 = η(L/D)·CD2D(Re_D, M)(TR R-300·TN 2960 표),
    /// 축류 성분 = 0.82 × axialMachFactor(평평한 끝 둔체, 기존 L1 값 — 압축성 배율은 호출자가 줌). 원통이 아니면 <see cref="Projected"/>.
    /// </summary>
    public (double Area, double Cd) ProjectedReMach(Vec3 d, double reD, double mach, double axialMachFactor)
    {
        if (Kind != ShapeKind.Cylinder) return Projected(d);
        double ca = Math.Abs(d[Axis]);
        double sa = Math.Sqrt(Math.Max(0, 1 - ca * ca));
        double aAx = Math.PI * Radius * Radius, aCross = 2 * Radius * Length;
        double area = ca * aAx + sa * aCross;
        // 횡류 독립 원리: 횡류 레이놀즈·마하수 = 수직 성분(Re·sinα, M·sinα)
        double cdx = Models.CylinderCrossflow.Cd(Length / (2 * Radius), reD * sa, mach * sa);
        double cd = area > 0 ? (0.82 * axialMachFactor * ca * aAx + cdx * sa * aCross) / area : 1.0;
        return (area, cd);
    }

    // 유한 길이 원통 횡류 Cd (Re 10⁴~10⁵): White, Fluid Mechanics Table 7.3 (Hoerner 자료). validation/public/drag_cylinder_crossflow_finite 참조.
    private static readonly double[] CrossLd = [1, 2, 3, 5, 10, 20, 40];
    private static readonly double[] CrossCd = [0.64, 0.68, 0.72, 0.74, 0.82, 0.91, 0.98];

    /// <summary>
    /// 유한 길이 원통 횡류 항력계수(L/D). log(L/D) 선형 보간, L/D&lt;1 은 0.64 고정, L/D&gt;40 은 40/(L/D) 비례로 무한 원통 1.20 에 접근.
    /// 이 표로 L1 계수를 정했으므로 같은 표로 '검증 통과'를 주장하지 않는다(VV-50 보정 사례).
    /// </summary>
    public static double CylinderCrossflowCd(double lOverD)
    {
        if (lOverD <= CrossLd[0]) return CrossCd[0];
        if (lOverD >= CrossLd[^1]) return 1.20 - (1.20 - CrossCd[^1]) * (CrossLd[^1] / lOverD);
        int i = 1;
        while (CrossLd[i] < lOverD) i++;
        double f = Math.Log(lOverD / CrossLd[i - 1]) / Math.Log(CrossLd[i] / CrossLd[i - 1]);
        return CrossCd[i - 1] + (CrossCd[i] - CrossCd[i - 1]) * f;
    }

    /// <summary>균일 밀도 가정 관성(형상 중심 기준) — 질량특성 미지정 시 대체값.</summary>
    public Mat3 UniformInertia(double mass)
    {
        switch (Kind)
        {
            case ShapeKind.Sphere:
            {
                double i = 0.4 * mass * Radius * Radius;
                return Mat3.Diagonal(i, i, i);
            }
            case ShapeKind.Cylinder:
            {
                double ia = 0.5 * mass * Radius * Radius;
                double it = mass * (3 * Radius * Radius + Length * Length) / 12;
                return Axis switch
                {
                    0 => Mat3.Diagonal(ia, it, it),
                    1 => Mat3.Diagonal(it, ia, it),
                    _ => Mat3.Diagonal(it, it, ia)
                };
            }
            case ShapeKind.Mesh:
            case ShapeKind.ShellSegment:
            {
                var (vol, _, ipd) = Mesh!.VolumeProperties();
                return ipd * (mass / vol);
            }
            default:
            {
                var s = Size;
                return Mat3.Diagonal(mass * (s.Y * s.Y + s.Z * s.Z) / 12, mass * (s.X * s.X + s.Z * s.Z) / 12, mass * (s.X * s.X + s.Y * s.Y) / 12);
            }
        }
    }

    public Vec3 UniformCentroid() => Kind is ShapeKind.Mesh or ShapeKind.ShellSegment ? Mesh!.VolumeProperties().Centroid : Center;

    /// <summary>대표 길이(감쇠 미계수 무차원화용).</summary>
    public double CharacteristicLength => Kind switch
    {
        ShapeKind.Sphere => 2 * Radius,
        ShapeKind.Cylinder => Math.Max(Length, 2 * Radius),
        _ => Math.Max(Size.X, Math.Max(Size.Y, Size.Z))
    };

    public double MeanFrontalArea => Kind switch
    {
        ShapeKind.Sphere => Math.PI * Radius * Radius,
        ShapeKind.Cylinder => 0.5 * (Math.PI * Radius * Radius + 2 * Radius * Length),
        _ => (Size.X * Size.Y + Size.Y * Size.Z + Size.X * Size.Z) / 3
    };
}
