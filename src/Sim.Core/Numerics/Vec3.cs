using System.Runtime.CompilerServices;

namespace Sim.Core.Numerics;

/// <summary>할당 없는 3차원 벡터(double). NED/FRD 어느 좌표계든 사용하는 쪽에서 의미를 정한다.</summary>
public readonly struct Vec3 : IEquatable<Vec3>
{
    public readonly double X, Y, Z;

    public Vec3(double x, double y, double z) { X = x; Y = y; Z = z; }

    public static readonly Vec3 Zero = new(0, 0, 0);
    public static readonly Vec3 UnitX = new(1, 0, 0);
    public static readonly Vec3 UnitY = new(0, 1, 0);
    public static readonly Vec3 UnitZ = new(0, 0, 1);

    public double this[int i] => i switch { 0 => X, 1 => Y, 2 => Z, _ => throw new IndexOutOfRangeException() };

    /// <summary>성분 i 만 바꾼 벡터.</summary>
    public Vec3 With(int i, double v) => i switch { 0 => new(v, Y, Z), 1 => new(X, v, Z), 2 => new(X, Y, v), _ => throw new IndexOutOfRangeException() };

    /// <summary>성분 i 에 d 를 더한 벡터.</summary>
    public Vec3 Add(int i, double d) => With(i, this[i] + d);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vec3 operator -(Vec3 a) => new(-a.X, -a.Y, -a.Z);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vec3 operator *(Vec3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vec3 operator *(double s, Vec3 a) => new(a.X * s, a.Y * s, a.Z * s);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vec3 operator /(Vec3 a, double s) => new(a.X / s, a.Y / s, a.Z / s);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vec3 Cross(Vec3 a, Vec3 b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    public double Norm => Math.Sqrt(X * X + Y * Y + Z * Z);
    public double NormSq => X * X + Y * Y + Z * Z;
    public double HorizontalNorm => Math.Sqrt(X * X + Y * Y);

    public Vec3 Normalized()
    {
        double n = Norm;
        return n > 1e-300 ? this / n : Zero;
    }

    public static Vec3 Mul(Vec3 a, Vec3 b) => new(a.X * b.X, a.Y * b.Y, a.Z * b.Z);
    public static Vec3 Min(Vec3 a, Vec3 b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));
    public static Vec3 Max(Vec3 a, Vec3 b) => new(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));

    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z);

    public static Vec3 FromArray(IReadOnlyList<double> a) => new(a[0], a[1], a[2]);
    public double[] ToArray() => [X, Y, Z];

    public bool Equals(Vec3 o) => X == o.X && Y == o.Y && Z == o.Z;
    public override bool Equals(object? obj) => obj is Vec3 v && Equals(v);
    public override int GetHashCode() => HashCode.Combine(X, Y, Z);
    public override string ToString() => FormattableString.Invariant($"({X:G6}, {Y:G6}, {Z:G6})");
}
