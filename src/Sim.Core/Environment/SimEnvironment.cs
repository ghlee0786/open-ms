using Sim.Core.Numerics;

namespace Sim.Core.Environment;

/// <summary>Young 관입식 토양 계수(SAND97-2426, 대략값). S: 토양 관입성 지수.</summary>
public readonly record struct Soil(string Name, double S);

/// <summary>
/// 지구 모델(FR-DYN-05). 기본은 평면 지구 + 상수 중력. 선택: 고도별 중력(역제곱 구형 / WGS-84 J2)과 지구 자전.
/// 위치는 계속 평면 NED(원점 위도 Latitude, 경도 0)로 적분한다 — 수평 이동이 수 km 이내인 투하 문제에서
/// 곡률·수송률(v/R) 항은 무시(가정). 자전이 켜지면 NED 는 지구 고정(회전) 좌표계이고:
///   병진 a = F/m + g_eff(h) − 2Ω×v  (g_eff = 인력 + 원심),  자세 q̇ = ½ q⊗(ω − Ω_body),  ω = 관성 각속도.
/// NASA/TM-2015-218675 check-case 1~8 재현용(VV-05).
/// </summary>
public sealed class EarthModel
{
    public const double Mu = 3.986004418e14, A = 6378137.0, F = 1 / 298.257223563, J2 = 1.082626684e-3, Omega = 7.292115e-5;

    /// <summary>"constant" | "inverse-square"(구형, 반경 A) | "wgs84-j2"</summary>
    public string Gravity { get; init; } = "constant";
    /// <summary>원점 측지 위도 [deg].</summary>
    public double LatitudeDeg { get; init; }
    public bool Rotation { get; init; }
    /// <summary>inverse-square 구형 지구 반경 [m] (NASA check-case 4·5 는 authalic 반경 6 371 007.181 m).</summary>
    public double RoundRadius { get; init; } = A;

    public bool IsDefault => Gravity == "constant" && !Rotation;

    /// <summary>NED 로 표현한 지구 자전 각속도(자전 미적용이면 0).</summary>
    public Vec3 OmegaNed
    {
        get
        {
            if (!Rotation) return Vec3.Zero;
            double p = LatitudeDeg * Math.PI / 180;
            return new Vec3(Omega * Math.Cos(p), 0, -Omega * Math.Sin(p));
        }
    }

    /// <summary>해발 h 에서의 유효 중력(인력 + 자전 시 원심) NED [m/s²]. constant 모델이면 (0,0,g0).</summary>
    public Vec3 GravityAt(double h, double g0)
    {
        if (Gravity == "constant") return new Vec3(0, 0, g0);
        double p = LatitudeDeg * Math.PI / 180, sp = Math.Sin(p), cp = Math.Cos(p);
        // ECEF(경도 0 자오면): x 적도 방향, z 북극 방향
        double x, z;
        if (Gravity == "inverse-square") { x = (RoundRadius + h) * cp; z = (RoundRadius + h) * sp; }
        else
        {
            double e2 = F * (2 - F), n = A / Math.Sqrt(1 - e2 * sp * sp);
            x = (n + h) * cp; z = (n * (1 - e2) + h) * sp;
        }
        double r2 = x * x + z * z, r = Math.Sqrt(r2), r3 = r2 * r;
        double gx, gz;
        if (Gravity == "inverse-square") { gx = -Mu * x / r3; gz = -Mu * z / r3; }
        else if (Gravity == "wgs84-j2")
        {
            double k = 1.5 * J2 * (A * A / r2), zr = z * z / r2;
            gx = -Mu * x / r3 * (1 + k * (1 - 5 * zr));
            gz = -Mu * z / r3 * (1 + k * (3 - 5 * zr));
        }
        else throw new InvalidDataException($"earth.gravity: constant | inverse-square | wgs84-j2 ('{Gravity}')");
        if (Rotation) gx += Omega * Omega * x;
        // ECEF → NED (경도 0): N = (−sinφ, 0, cosφ), D = (−cosφ, 0, −sinφ)
        return new Vec3(-sp * gx + cp * gz, 0, -cp * gx - sp * gz);
    }
}

/// <summary>대기·바람·난류·지형·토양·지구 모델을 묶은 환경 요소(Environment).</summary>
public sealed class SimEnvironment
{
    public Atmosphere Atmosphere { get; init; } = new();
    public MeanWind Wind { get; init; } = new();
    public string Turbulence { get; init; } = "none";
    /// <summary>평면 지형 해발고도 [m]. NED 원점은 해발 0 m.</summary>
    public double GroundAltitude { get; init; }
    public double Gravity { get; init; } = Atmosphere.G0;
    public Soil Soil { get; init; } = Soils["farmland"];
    public EarthModel Earth { get; init; } = new();

    public static readonly Dictionary<string, Soil> Soils = new()
    {
        ["sand"] = new("sand", 6.0),
        ["farmland"] = new("farmland", 8.0),
        ["clay"] = new("clay", 10.0),
        ["frozen"] = new("frozen", 3.0),
        ["rock"] = new("rock", 0.7),
    };

    public double GroundD => -GroundAltitude;
    public double Agl(double d) => -d - GroundAltitude;
    public Vec3 GravityVector => new(0, 0, Gravity);
    /// <summary>NED 위치 D 좌표에서의 유효 중력 벡터(지구 모델 반영).</summary>
    public Vec3 GravityAtD(double d) => Earth.IsDefault ? GravityVector : Earth.GravityAt(-d, Gravity);
}
