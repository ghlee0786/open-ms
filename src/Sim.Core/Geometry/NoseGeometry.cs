using Sim.Core.Numerics;

namespace Sim.Core.Geometry;

/// <summary>
/// 기수축(FX-01)·관입체 단면(FX-02)·측면 투영 압력중심(FX-03) 형상 계산.
/// 동체 FRD 의 기수는 +X 다. 모델(Fusion 등) 좌표에서 기수가 어느 축인지는 물체마다 다르므로
/// 시나리오 body.noseAxis(모델 좌표 "±X|±Y|±Z")로 지정하고, 기본 프레임 변환 뒤에 기수를 +X 로 돌리는 회전을 곱한다.
/// </summary>
public static class NoseGeometry
{
    public static readonly string[] AxisNames = ["+X", "-X", "+Y", "-Y", "+Z", "-Z"];

    public static Vec3 ParseAxis(string s) => s.Trim().ToUpperInvariant() switch
    {
        "+X" or "X" => Vec3.UnitX,
        "-X" => -Vec3.UnitX,
        "+Y" or "Y" => Vec3.UnitY,
        "-Y" => -Vec3.UnitY,
        "+Z" or "Z" => Vec3.UnitZ,
        "-Z" => -Vec3.UnitZ,
        _ => throw new ArgumentException($"noseAxis 는 ±X|±Y|±Z 이어야 합니다: '{s}'")
    };

    /// <summary>
    /// 동체 좌표 단위축 n 을 +X 로 보내는 회전(행렬식 +1). n=−X 는 Z 축 180°(요 반전 — 위/아래 유지),
    /// n=±Y/±Z 는 n×X 축 90° 회전.
    /// </summary>
    public static Mat3 AlignToX(Vec3 n)
    {
        n = n.Normalized();
        if ((n - Vec3.UnitX).Norm < 1e-9) return Mat3.Identity;
        if ((n + Vec3.UnitX).Norm < 1e-9) return Mat3.Diagonal(-1, -1, 1);
        var k = Vec3.Cross(n, Vec3.UnitX);
        double s = k.Norm, c = Vec3.Dot(n, Vec3.UnitX);
        k /= s;
        var K = new Mat3(0, -k.Z, k.Y, k.Z, 0, -k.X, -k.Y, k.X, 0);
        return Mat3.Identity + K * s + K * K * (1 - c);
    }

    /// <summary>모델→동체 변환에 기수 정렬을 합성: 모델 좌표 noseAxis 가 동체 +X 가 된다.</summary>
    public static Mat3 WithNose(Mat3 modelToBody, string noseAxis)
    {
        var nb = modelToBody * ParseAxis(noseAxis);
        return AlignToX(nb) * modelToBody;
    }

    /// <summary>
    /// 축 방향 끝 단면(관입체 추정): 축 최대점에서 안쪽으로 slab 구간의 꼭짓점 횡방향 폭으로 등가 지름 → 타원 면적.
    /// 끝이 뾰족해(폭≈0) 정보가 없으면 구간을 넓혀 축(샤프트) 지름을 찾는다. Young 식의 A 는 관입체 몸통 단면이다.
    /// </summary>
    public static (double Area, double Diameter, double NoseLength) EndSection(TriMesh mesh, Vec3 axis, double startFraction = 0.1)
    {
        axis = axis.Normalized();
        var (u, v) = Perp(axis);
        double amin = double.MaxValue, amax = double.MinValue;
        foreach (var p in mesh.Vertices) { double a = Vec3.Dot(p, axis); amin = Math.Min(amin, a); amax = Math.Max(amax, a); }
        double len = amax - amin;
        if (len <= 0) return (0, 0, 0);
        double tipWidth = 0, tipDepth = 0;
        for (double f = startFraction; f <= 1.0 + 1e-9; f += 0.05)
        {
            double lo = amax - f * len;
            double umin = double.MaxValue, umax = double.MinValue, vmin = double.MaxValue, vmax = double.MinValue;
            foreach (var p in mesh.Vertices)
            {
                if (Vec3.Dot(p, axis) < lo - 1e-12) continue;
                double pu = Vec3.Dot(p, u), pv = Vec3.Dot(p, v);
                umin = Math.Min(umin, pu); umax = Math.Max(umax, pu); vmin = Math.Min(vmin, pv); vmax = Math.Max(vmax, pv);
            }
            double eu = umax - umin, ev = vmax - vmin;
            if (eu > 0.02 * len && ev > 0.02 * len)
            {
                tipWidth = Math.Sqrt(eu * ev); tipDepth = f * len;
                return (Math.PI / 4 * eu * ev, tipWidth, NoseLengthEstimate(mesh, axis, amax, tipWidth));
            }
        }
        return (0, tipWidth, tipDepth);
    }

    /// <summary>끝점에서 횡폭이 등가 지름의 90% 에 이르는 축 길이(원뿔/오자이브 노즈 길이 추정).</summary>
    private static double NoseLengthEstimate(TriMesh mesh, Vec3 axis, double amax, double d)
    {
        var (u, v) = Perp(axis);
        double best = 0;
        // 끝에서 가장 가까운 '폭이 충분한' 꼭짓점까지의 거리
        double nearest = double.MaxValue;
        var c = Centerline(mesh, axis, amax, d);
        foreach (var p in mesh.Vertices)
        {
            double a = amax - Vec3.Dot(p, axis);
            double r = Math.Sqrt(Math.Pow(Vec3.Dot(p, u) - c.U, 2) + Math.Pow(Vec3.Dot(p, v) - c.V, 2));
            if (r >= 0.45 * d && a < nearest) nearest = a;
        }
        if (nearest < double.MaxValue) best = nearest;
        return best;
    }

    private static (double U, double V) Centerline(TriMesh mesh, Vec3 axis, double amax, double d)
    {
        var (u, v) = Perp(axis);
        double su = 0, sv = 0; int n = 0;
        foreach (var p in mesh.Vertices)
        {
            if (amax - Vec3.Dot(p, axis) > 3 * d) continue;
            su += Vec3.Dot(p, u); sv += Vec3.Dot(p, v); n++;
        }
        return n > 0 ? (su / n, sv / n) : (0, 0);
    }

    private static (Vec3 U, Vec3 V) Perp(Vec3 axis)
    {
        var t = Math.Abs(axis.X) < 0.9 ? Vec3.UnitX : Vec3.UnitY;
        var u = Vec3.Cross(axis, t).Normalized();
        return (u, Vec3.Cross(axis, u));
    }

    /// <summary>
    /// 측면 투영 압력중심(FX-03): 닫힌 메시를 동체 Y·Z 방향으로 투영한 면적 가중 중심.
    /// 투영 방향 성분은 보존되므로(볼록체면 앞·뒷면이 같은 x 에 쌍으로 기여) 투영 실루엣 중심과 같다.
    /// 반환: CP(x: 두 투영 평균, y: Z 투영, z: Y 투영), 평균 측면 투영면적.
    /// </summary>
    public static (Vec3 Cp, double SideArea) LateralCentroid(TriMesh mesh)
    {
        var vs = mesh.Vertices;
        double ay = 0, az = 0, xy = 0, xz = 0, zy = 0, yz = 0;
        for (int i = 0; i + 2 < vs.Count; i += 3)
        {
            var a = vs[i]; var b = vs[i + 1]; var c = vs[i + 2];
            var n = Vec3.Cross(b - a, c - a) * 0.5;
            var g = (a + b + c) / 3;
            double wy = Math.Abs(n.Y), wz = Math.Abs(n.Z);
            ay += wy; xy += wy * g.X; zy += wy * g.Z;
            az += wz; xz += wz * g.X; yz += wz * g.Y;
        }
        if (ay + az <= 0) return (Vec3.Zero, 0);
        double x = (xy + xz) / (ay + az);
        double y = az > 0 ? yz / az : 0;
        double z = ay > 0 ? zy / ay : 0;
        return (new Vec3(x, y, z), 0.25 * (ay + az));
    }

    /// <summary>
    /// 가져오기 기수 후보 제안: 모델 좌표 바운딩박스 최장축의 양 끝 중 끝 단면이 작은 쪽(스파이크·노즈 쪽).
    /// 사용자가 확인·수정하는 제안값이다.
    /// </summary>
    public static string SuggestNoseAxis(TriMesh modelMesh)
    {
        var (min, max) = modelMesh.Bounds();
        var size = max - min;
        int ax = size.X >= size.Y && size.X >= size.Z ? 0 : size.Y >= size.Z ? 1 : 2;
        var e = ax switch { 0 => Vec3.UnitX, 1 => Vec3.UnitY, _ => Vec3.UnitZ };
        var plus = EndSection(modelMesh, e);
        var minus = EndSection(modelMesh, -e);
        string name = "XYZ"[ax].ToString();
        return plus.Area <= minus.Area ? "+" + name : "-" + name;
    }
}
