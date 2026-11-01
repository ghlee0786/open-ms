using Sim.Core.Numerics;

namespace Sim.Core.Geometry;

/// <summary>
/// 좌표계 변환 표(FR-GEO-03). config/frames.json 과 같은 값이며 단위 테스트(VV-06)로 고정한다.
/// - Fusion 모델(Y-up, 길이축 Z, 단위 cm/mm) → 동체 FRD: X_b = Z_f, Y_b = −X_f, Z_b = −Y_f
/// - NED → OpenGL 뷰(x=동, y=위, z=−북)
/// - NED → Blender(ENU, Z-up): x=동, y=북, z=위
/// </summary>
public static class Frames
{
    public static readonly Mat3 FusionToBody = new(
        0, 0, 1,
        -1, 0, 0,
        0, -1, 0);

    public static readonly Mat3 NedToGl = new(
        0, 1, 0,
        0, 0, -1,
        -1, 0, 0);

    public static readonly Mat3 NedToEnu = new(
        0, 1, 0,
        1, 0, 0,
        0, 0, -1);

    public static Vec3 ToGl(Vec3 ned) => new(ned.Y, -ned.Z, -ned.X);
    public static Vec3 ToEnu(Vec3 ned) => new(ned.Y, ned.X, -ned.Z);

    /// <summary>길이 단위 문자열 → m 배율.</summary>
    public static double UnitScale(string unit) => unit.ToLowerInvariant() switch
    {
        "m" => 1,
        "cm" => 0.01,
        "mm" => 0.001,
        "in" => 0.0254,
        _ => throw new ArgumentException($"알 수 없는 길이 단위: {unit}")
    };
}
