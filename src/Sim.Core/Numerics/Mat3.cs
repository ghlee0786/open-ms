namespace Sim.Core.Numerics;

/// <summary>3×3 행렬(행 우선). 회전행렬(DCM)·관성텐서에 사용.</summary>
public readonly struct Mat3
{
    public readonly double M11, M12, M13, M21, M22, M23, M31, M32, M33;

    public Mat3(double m11, double m12, double m13, double m21, double m22, double m23, double m31, double m32, double m33)
    {
        M11 = m11; M12 = m12; M13 = m13;
        M21 = m21; M22 = m22; M23 = m23;
        M31 = m31; M32 = m32; M33 = m33;
    }

    public static readonly Mat3 Identity = new(1, 0, 0, 0, 1, 0, 0, 0, 1);
    public static readonly Mat3 Zero = new(0, 0, 0, 0, 0, 0, 0, 0, 0);

    public static Mat3 Diagonal(double a, double b, double c) => new(a, 0, 0, 0, b, 0, 0, 0, c);
    public static Mat3 FromRows(Vec3 r0, Vec3 r1, Vec3 r2) => new(r0.X, r0.Y, r0.Z, r1.X, r1.Y, r1.Z, r2.X, r2.Y, r2.Z);
    public static Mat3 FromColumns(Vec3 c0, Vec3 c1, Vec3 c2) => new(c0.X, c1.X, c2.X, c0.Y, c1.Y, c2.Y, c0.Z, c1.Z, c2.Z);

    /// <summary>관성텐서 6성분(곱관성은 공학 부호: Ixy = ∫xy dm, 텐서 비대각 = −Ixy).</summary>
    public static Mat3 Inertia(double ixx, double iyy, double izz, double ixy = 0, double ixz = 0, double iyz = 0) =>
        new(ixx, -ixy, -ixz, -ixy, iyy, -iyz, -ixz, -iyz, izz);

    public double this[int r, int c] => (r * 3 + c) switch
    {
        0 => M11, 1 => M12, 2 => M13, 3 => M21, 4 => M22, 5 => M23, 6 => M31, 7 => M32, 8 => M33,
        _ => throw new IndexOutOfRangeException()
    };

    public Vec3 Row(int r) => r switch { 0 => new(M11, M12, M13), 1 => new(M21, M22, M23), _ => new(M31, M32, M33) };
    public Vec3 Col(int c) => c switch { 0 => new(M11, M21, M31), 1 => new(M12, M22, M32), _ => new(M13, M23, M33) };

    public static Vec3 operator *(Mat3 m, Vec3 v) => new(
        m.M11 * v.X + m.M12 * v.Y + m.M13 * v.Z,
        m.M21 * v.X + m.M22 * v.Y + m.M23 * v.Z,
        m.M31 * v.X + m.M32 * v.Y + m.M33 * v.Z);

    public static Mat3 operator *(Mat3 a, Mat3 b) => new(
        a.M11 * b.M11 + a.M12 * b.M21 + a.M13 * b.M31, a.M11 * b.M12 + a.M12 * b.M22 + a.M13 * b.M32, a.M11 * b.M13 + a.M12 * b.M23 + a.M13 * b.M33,
        a.M21 * b.M11 + a.M22 * b.M21 + a.M23 * b.M31, a.M21 * b.M12 + a.M22 * b.M22 + a.M23 * b.M32, a.M21 * b.M13 + a.M22 * b.M23 + a.M23 * b.M33,
        a.M31 * b.M11 + a.M32 * b.M21 + a.M33 * b.M31, a.M31 * b.M12 + a.M32 * b.M22 + a.M33 * b.M32, a.M31 * b.M13 + a.M32 * b.M23 + a.M33 * b.M33);

    public static Mat3 operator +(Mat3 a, Mat3 b) => new(
        a.M11 + b.M11, a.M12 + b.M12, a.M13 + b.M13, a.M21 + b.M21, a.M22 + b.M22, a.M23 + b.M23, a.M31 + b.M31, a.M32 + b.M32, a.M33 + b.M33);

    public static Mat3 operator *(Mat3 a, double s) => new(
        a.M11 * s, a.M12 * s, a.M13 * s, a.M21 * s, a.M22 * s, a.M23 * s, a.M31 * s, a.M32 * s, a.M33 * s);

    public Mat3 Transpose() => new(M11, M21, M31, M12, M22, M32, M13, M23, M33);

    public double Determinant() =>
        M11 * (M22 * M33 - M23 * M32) - M12 * (M21 * M33 - M23 * M31) + M13 * (M21 * M32 - M22 * M31);

    public double Trace => M11 + M22 + M33;

    public Mat3 Inverse()
    {
        double d = Determinant();
        if (Math.Abs(d) < 1e-300) throw new InvalidOperationException("특이 행렬(역행렬 없음)");
        double id = 1.0 / d;
        return new Mat3(
            (M22 * M33 - M23 * M32) * id, (M13 * M32 - M12 * M33) * id, (M12 * M23 - M13 * M22) * id,
            (M23 * M31 - M21 * M33) * id, (M11 * M33 - M13 * M31) * id, (M13 * M21 - M11 * M23) * id,
            (M21 * M32 - M22 * M31) * id, (M12 * M31 - M11 * M32) * id, (M11 * M22 - M12 * M21) * id);
    }

    /// <summary>평행축 정리 항: m(|d|²E − d dᵀ).</summary>
    public static Mat3 ParallelAxis(double m, Vec3 d)
    {
        double dd = d.NormSq;
        return new Mat3(
            m * (dd - d.X * d.X), -m * d.X * d.Y, -m * d.X * d.Z,
            -m * d.Y * d.X, m * (dd - d.Y * d.Y), -m * d.Y * d.Z,
            -m * d.Z * d.X, -m * d.Z * d.Y, m * (dd - d.Z * d.Z));
    }

    /// <summary>회전 R 로 텐서 축 변환: R I Rᵀ.</summary>
    public Mat3 RotatedBy(Mat3 r) => r * this * r.Transpose();

    public double MaxAbsDiff(Mat3 o)
    {
        double m = 0;
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                m = Math.Max(m, Math.Abs(this[i, j] - o[i, j]));
        return m;
    }

    /// <summary>대칭행렬 고유값(오름차순, 해석적 삼각함수 방법).</summary>
    public double[] SymmetricEigenvalues()
    {
        double p1 = M12 * M12 + M13 * M13 + M23 * M23;
        if (p1 < 1e-30 * (Math.Abs(Trace) + 1e-300))
        {
            var d = new[] { M11, M22, M33 };
            Array.Sort(d);
            return d;
        }
        double q = Trace / 3;
        double p2 = (M11 - q) * (M11 - q) + (M22 - q) * (M22 - q) + (M33 - q) * (M33 - q) + 2 * p1;
        double p = Math.Sqrt(p2 / 6);
        var b = (this + Identity * -q) * (1 / p);
        double r = Math.Clamp(b.Determinant() / 2, -1, 1);
        double phi = Math.Acos(r) / 3;
        double e1 = q + 2 * p * Math.Cos(phi);
        double e3 = q + 2 * p * Math.Cos(phi + 2 * Math.PI / 3);
        double e2 = 3 * q - e1 - e3;
        var e = new[] { e1, e2, e3 };
        Array.Sort(e);
        return e;
    }

    public double[][] ToArray() =>
    [
        [M11, M12, M13], [M21, M22, M23], [M31, M32, M33]
    ];

    public static Mat3 FromArray(IReadOnlyList<IReadOnlyList<double>> a) =>
        new(a[0][0], a[0][1], a[0][2], a[1][0], a[1][1], a[1][2], a[2][0], a[2][1], a[2][2]);
}
