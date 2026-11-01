namespace Sim.Core.Numerics;

/// <summary>
/// 단위 쿼터니언 (W, X, Y, Z). 엔진 관례: q 는 동체(FRD) → 관성(NED) 회전.
/// v_ned = q ⊗ v_body ⊗ q*. 자세 운동학 q̇ = ½ q ⊗ [0, ω_body].
/// </summary>
public readonly struct Quat
{
    public readonly double W, X, Y, Z;

    public Quat(double w, double x, double y, double z) { W = w; X = x; Y = y; Z = z; }

    public static readonly Quat Identity = new(1, 0, 0, 0);

    public static Quat operator *(Quat a, Quat b) => new(
        a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z,
        a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
        a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
        a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W);

    public static Quat operator +(Quat a, Quat b) => new(a.W + b.W, a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Quat operator *(Quat a, double s) => new(a.W * s, a.X * s, a.Y * s, a.Z * s);

    public Quat Conjugate() => new(W, -X, -Y, -Z);
    public double Norm => Math.Sqrt(W * W + X * X + Y * Y + Z * Z);

    public Quat Normalized()
    {
        double n = Norm;
        return n > 0 ? new Quat(W / n, X / n, Y / n, Z / n) : Identity;
    }

    /// <summary>동체 → NED 벡터 회전.</summary>
    public Vec3 Rotate(Vec3 v)
    {
        // v' = v + 2w(u×v) + 2u×(u×v)
        var u = new Vec3(X, Y, Z);
        var t = Vec3.Cross(u, v) * 2;
        return v + t * W + Vec3.Cross(u, t);
    }

    /// <summary>NED → 동체 벡터 회전.</summary>
    public Vec3 InverseRotate(Vec3 v) => Conjugate().Rotate(v);

    /// <summary>q̇ = ½ q ⊗ [0, ω].</summary>
    public Quat Derivative(Vec3 wBody) => this * new Quat(0, wBody.X, wBody.Y, wBody.Z) * 0.5;

    public static Quat FromAxisAngle(Vec3 axis, double angle)
    {
        var a = axis.Normalized();
        double s = Math.Sin(angle / 2);
        return new Quat(Math.Cos(angle / 2), a.X * s, a.Y * s, a.Z * s);
    }

    /// <summary>3-2-1 (Yaw ψ → Pitch θ → Roll φ) 오일러각 [rad] → 쿼터니언.</summary>
    public static Quat FromEuler(double roll, double pitch, double yaw)
    {
        double cr = Math.Cos(roll / 2), sr = Math.Sin(roll / 2);
        double cp = Math.Cos(pitch / 2), sp = Math.Sin(pitch / 2);
        double cy = Math.Cos(yaw / 2), sy = Math.Sin(yaw / 2);
        return new Quat(
            cr * cp * cy + sr * sp * sy,
            sr * cp * cy - cr * sp * sy,
            cr * sp * cy + sr * cp * sy,
            cr * cp * sy - sr * sp * cy);
    }

    /// <summary>
    /// 쿼터니언 → 3-2-1 오일러 (roll, pitch, yaw) [rad].
    /// 짐벌락(|θ|→90°) 근처에서는 roll=0 으로 두고 yaw 에 합쳐 NaN 없이 계산한다(FR-DYN-03).
    /// </summary>
    public Vec3 ToEuler()
    {
        var q = Normalized();
        double sinp = 2 * (q.W * q.Y - q.Z * q.X);
        if (Math.Abs(sinp) >= 1 - 1e-12)
        {
            double pitch = Math.CopySign(Math.PI / 2, sinp);
            double yaw = -2 * Math.Sign(sinp) * Math.Atan2(q.X, q.W);
            return new Vec3(0, pitch, WrapPi(yaw));
        }
        double roll = Math.Atan2(2 * (q.W * q.X + q.Y * q.Z), 1 - 2 * (q.X * q.X + q.Y * q.Y));
        double p = Math.Asin(sinp);
        double y = Math.Atan2(2 * (q.W * q.Z + q.X * q.Y), 1 - 2 * (q.Y * q.Y + q.Z * q.Z));
        return new Vec3(roll, p, y);
    }

    public static double WrapPi(double a)
    {
        a = Math.IEEERemainder(a, 2 * Math.PI);
        return a;
    }

    /// <summary>DCM (동체 → NED): v_ned = C v_body.</summary>
    public Mat3 ToMatrix()
    {
        var q = Normalized();
        double w = q.W, x = q.X, y = q.Y, z = q.Z;
        return new Mat3(
            1 - 2 * (y * y + z * z), 2 * (x * y - w * z), 2 * (x * z + w * y),
            2 * (x * y + w * z), 1 - 2 * (x * x + z * z), 2 * (y * z - w * x),
            2 * (x * z - w * y), 2 * (y * z + w * x), 1 - 2 * (x * x + y * y));
    }

    /// <summary>회전행렬 → 쿼터니언 (Shepperd 방법, W ≥ 0 로 정규화).</summary>
    public static Quat FromMatrix(Mat3 m)
    {
        double tr = m.Trace;
        Quat q;
        if (tr > 0)
        {
            double s = Math.Sqrt(tr + 1) * 2;
            q = new Quat(0.25 * s, (m.M32 - m.M23) / s, (m.M13 - m.M31) / s, (m.M21 - m.M12) / s);
        }
        else if (m.M11 > m.M22 && m.M11 > m.M33)
        {
            double s = Math.Sqrt(1 + m.M11 - m.M22 - m.M33) * 2;
            q = new Quat((m.M32 - m.M23) / s, 0.25 * s, (m.M12 + m.M21) / s, (m.M13 + m.M31) / s);
        }
        else if (m.M22 > m.M33)
        {
            double s = Math.Sqrt(1 + m.M22 - m.M11 - m.M33) * 2;
            q = new Quat((m.M13 - m.M31) / s, (m.M12 + m.M21) / s, 0.25 * s, (m.M23 + m.M32) / s);
        }
        else
        {
            double s = Math.Sqrt(1 + m.M33 - m.M11 - m.M22) * 2;
            q = new Quat((m.M21 - m.M12) / s, (m.M13 + m.M31) / s, (m.M23 + m.M32) / s, 0.25 * s);
        }
        if (q.W < 0) q = q * -1;
        return q.Normalized();
    }

    /// <summary>같은 회전인지(±q 동치) 비교용 거리.</summary>
    public static double Distance(Quat a, Quat b)
    {
        double d = Math.Abs(a.W * b.W + a.X * b.X + a.Y * b.Y + a.Z * b.Z);
        return 1 - Math.Min(1, d);
    }

    public override string ToString() => FormattableString.Invariant($"[{W:G6}; {X:G6}, {Y:G6}, {Z:G6}]");
}
