using Sim.Core.Numerics;

namespace Sim.App.Rendering;

/// <summary>공간 이동 방향(키 W/S/A/D/E/Q).</summary>
public enum FlyDir { Forward, Back, Left, Right, Up, Down }

/// <summary>
/// 3D 뷰 카메라의 상태와 조작 수학(GL·Avalonia 없음 — 조작 시나리오 테스트 대상, 테스트 프로젝트가 이 소스를 링크한다).
/// 좌표 NED. 초점 Focus = Target(추적 대상) + Pan(오프셋), 눈 Eye = Focus − Forward·Distance.
/// 조작량은 모두 현재 거리나 커서 아래 깊이에 비례해 0.1 m 근접부터 수백 km 까지 같은 손맛으로 움직인다.
/// · 확대(Wheel·Zoom) — 궤도 거리만 바꾼다(커서 아래 점 고정). 최소 거리에 닿으면 남은 칸은 커서 방향 전진으로 이어 간다.
/// · 공간 이동(Dolly·Fly) — 눈과 초점을 함께 옮긴다(거리·방향 그대로). 계속 굴리거나 누르고 있으면 가속.
/// · 궤도 회전(Orbit) — 초점 중심. 시점 회전(Look) — 눈 고정.
/// · 화면 평면 이동(PanPixels) — 기준 깊이의 점이 커서를 정확히 따라온다.
/// 시간은 호출자가 초 단위로 넘긴다(누르고 있는 동안의 이동은 가속 곡선의 닫힌 형식 적분 — 프레임 간격과 무관).
/// </summary>
public sealed class ViewCamera
{
    /// <summary>세로 시야각 [rad].</summary>
    public const double Fov = 0.9;
    public const double MinDistance = 0.1, MaxDistance = 200000;
    public const double MinPitch = -1.5, MaxPitch = 1.55;
    public const double DefaultYaw = -0.6, DefaultPitch = 0.35;
    /// <summary>휠 한 칸 확대 배율.</summary>
    public const double WheelZoom = 1.15;
    /// <summary>Ctrl+휠 한 칸 이동 = 거리 × DollyFraction(최소 거리 근처 휠 확대 한 칸과 비슷한 크기).</summary>
    public const double DollyFraction = 0.15;
    /// <summary>키 비행 시작 속도 = 거리 × FlyRate [1/s].</summary>
    public const double FlyRate = 0.5;
    /// <summary>계속 조작할 때 속도가 두 배가 되는 시간 [s]과 최대 배율.</summary>
    public const double RampDoubling = 0.7, RampMax = 256;
    /// <summary>같은 방향 휠 입력을 연속으로 보는 최대 간격 [s].</summary>
    public const double WheelStreakGap = 0.3;
    /// <summary>한 번에 적분하는 최대 시간 [s] — 프레임이 오래 멈췄다 와도 한 번에 튀지 않게.</summary>
    public const double MaxStep = 0.1;
    private static readonly double TanHalf = Math.Tan(Fov / 2);

    private Vec3 _target;
    public double Yaw { get; private set; } = DefaultYaw;
    public double Pitch { get; private set; } = DefaultPitch;
    public double Distance { get; private set; } = 25;
    /// <summary>추적 대상(그리기 루프가 추적 중일 때 매 프레임 갱신).</summary>
    public Vec3 Target { get => _target; set { if (value.IsFinite) _target = value; } }
    public Vec3 Pan { get; private set; }
    /// <summary>뷰 크기 [DIP].</summary>
    public double ViewWidth { get; private set; } = 800;
    public double ViewHeight { get; private set; } = 600;

    public Vec3 Focus => _target + Pan;
    public Vec3 Eye => Focus - Forward * Distance;
    public Vec3 Forward => new(Math.Cos(Pitch) * Math.Cos(Yaw), Math.Cos(Pitch) * Math.Sin(Yaw), Math.Sin(Pitch));
    public Vec3 Right => new(-Math.Sin(Yaw), Math.Cos(Yaw), 0);
    public Vec3 Up => Vec3.Cross(Right, Forward);

    public void SetViewport(double width, double height)
    {
        if (double.IsFinite(width) && width >= 1) ViewWidth = width;
        if (double.IsFinite(height) && height >= 1) ViewHeight = height;
    }

    // ---------------- 상태 지정 ----------------

    public void Reset(double distance)
    {
        SetDistance(distance);
        Pan = Vec3.Zero;
        SetAngles(DefaultYaw, DefaultPitch);
    }

    public void SetAngles(double yaw, double pitch)
    {
        if (!double.IsFinite(yaw) || !double.IsFinite(pitch)) return;
        Yaw = Math.IEEERemainder(yaw, 2 * Math.PI);
        Pitch = Math.Clamp(pitch, MinPitch, MaxPitch);
    }

    public void SetDistance(double distance)
    {
        if (double.IsFinite(distance)) Distance = Math.Clamp(distance, MinDistance, MaxDistance);
    }

    /// <summary>다른 카메라의 시점(추적 대상 · 오프셋 · 방향 · 거리)을 그대로 복사 — 분할 화면 카메라 동기 해제·재동기. 비행 키 상태는 복사하지 않는다.</summary>
    public void CopyFrom(ViewCamera o)
    {
        if (ReferenceEquals(o, this)) return;
        _target = o._target;
        Pan = o.Pan;
        Yaw = o.Yaw;
        Pitch = o.Pitch;
        Distance = o.Distance;
    }

    /// <summary>초점을 focus 로(오프셋 없음) 옮기고 거리 지정.</summary>
    public void LookAt(Vec3 focus, double distance)
    {
        if (!focus.IsFinite) return;
        _target = focus;
        Pan = Vec3.Zero;
        SetDistance(distance);
    }

    // ---------------- 회전 ----------------

    /// <summary>초점 중심 궤도 회전 [rad].</summary>
    public void Orbit(double dYaw, double dPitch)
    {
        if (!double.IsFinite(dYaw) || !double.IsFinite(dPitch)) return;
        SetAngles(Yaw + dYaw, Pitch + dPitch);
    }

    /// <summary>제자리 시점 회전 [rad] — 눈은 그대로, 초점이 눈 주위를 돈다.</summary>
    public void Look(double dYaw, double dPitch)
    {
        if (!double.IsFinite(dYaw) || !double.IsFinite(dPitch)) return;
        var back = Forward * Distance;
        SetAngles(Yaw + dYaw, Pitch + dPitch);
        Pan += Forward * Distance - back;
    }

    /// <summary>궤도 회전 드래그 [DIP] — 뷰 높이만큼 끌면 180°.</summary>
    public void OrbitDrag(double dx, double dy)
    {
        double k = Math.PI / Math.Max(ViewHeight, 300);
        Orbit(-dx * k, dy * k);
    }

    /// <summary>시점 회전 드래그 [DIP] — 화면 가운데의 장면이 커서를 그대로 따라오는 비율.</summary>
    public void LookDrag(double dx, double dy)
    {
        double k = 2 * TanHalf / ViewHeight;
        Look(-dx * k, -dy * k);
    }

    // ---------------- 화면 평면 이동 ----------------

    /// <summary>깊이 depth [m](시선 방향, 없으면 초점 거리)에서 화면 1 DIP 의 길이 [m].</summary>
    public double MetersPerPixel(double depth = double.NaN) => 2 * AnchorDepth(depth) * TanHalf / ViewHeight;

    /// <summary>화면 평면 이동 — 깊이 depth 의 점이 커서와 함께 정확히 dx·dy DIP 움직인다(없으면 초점 깊이).</summary>
    public void PanPixels(double dx, double dy, double depth = double.NaN)
    {
        if (!double.IsFinite(dx) || !double.IsFinite(dy)) return;
        double s = MetersPerPixel(depth);
        Pan = Pan - Right * (dx * s) + Up * (dy * s);
    }

    // ---------------- 확대 · 공간 이동 ----------------

    /// <summary>화면 가운데 기준 확대(f &lt; 1) · 축소(f &gt; 1) — 최소 거리에서는 전진으로 이어 감.</summary>
    public void Zoom(double f)
    {
        if (!double.IsFinite(f) || f <= 0) return;
        Wheel(-Math.Log(f) / Math.Log(WheelZoom), ViewWidth / 2, ViewHeight / 2);
    }

    /// <summary>
    /// 휠 notches 칸(+ 확대) — 커서 (px, py) 아래, 깊이 depth 의 점(없으면 초점 평면)을 화면에 고정한 채 궤도 거리 × 1.15^−n.
    /// 최소 거리에 닿으면 남은 칸은 Dolly 와 같은 커서 방향 전진으로 이어 간다(멈추지 않음).
    /// </summary>
    public void Wheel(double notches, double px, double py, double depth = double.NaN, double time = double.NaN)
    {
        if (!double.IsFinite(notches) || notches == 0) return;
        double want = Distance * Math.Pow(WheelZoom, -notches);
        double next = Math.Clamp(want, MinDistance, MaxDistance);
        if (next != Distance)
        {
            // 고정점 p = 눈 + 광선·깊이 를 중심으로 닮음 변환(초점·눈 모두 p 쪽으로 같은 배율)
            double f = next / Distance;
            Pan += (CursorOffset(px, py) * AnchorDepth(depth) - Forward * Distance) * (1 - f);
            Distance = next;
        }
        if (notches > 0 && want < MinDistance) Dolly(Math.Log(MinDistance / want, WheelZoom), px, py, time);
        else _streakSign = 0;
    }

    /// <summary>
    /// 커서 방향 공간 이동(Ctrl+휠) — 눈·초점을 함께 옮기므로 거리·방향은 그대로, 커서 아래 점은 화면에서 움직이지 않는다.
    /// 한 칸 = 거리 × 0.15. 같은 방향으로 계속 굴리면 가속.
    /// </summary>
    public void Dolly(double notches, double px, double py, double time = double.NaN)
    {
        if (!double.IsFinite(notches) || notches == 0) return;
        double step = notches * DollyFraction * Distance * WheelRamp(Math.Sign(notches), time);
        Pan += CursorRay(px, py) * step;
    }

    private int _streakSign;
    private double _streakStart, _streakLast;

    private double WheelRamp(int sign, double time)
    {
        if (!double.IsFinite(time)) { _streakSign = 0; return 1; }
        if (sign != _streakSign || time < _streakLast || !(time - _streakLast <= WheelStreakGap))
        {
            _streakSign = sign;
            _streakStart = time;
        }
        _streakLast = time;
        return Ramp(time - _streakStart);
    }

    // ---------------- 누르고 있는 동안 비행(W/S/A/D/E/Q) ----------------

    private readonly double[] _held = [double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN];
    private double _flyLast = double.NaN;

    public bool IsFlying
    {
        get
        {
            foreach (var h in _held) if (double.IsFinite(h)) return true;
            return false;
        }
    }

    /// <summary>비행 방향 단위 벡터(NED) — 전후는 시선, 좌우는 수평, 상하는 연직.</summary>
    public Vec3 Direction(FlyDir d) => d switch
    {
        FlyDir.Forward => Forward,
        FlyDir.Back => -Forward,
        FlyDir.Right => Right,
        FlyDir.Left => -Right,
        FlyDir.Up => new Vec3(0, 0, -1),
        _ => new Vec3(0, 0, 1),
    };

    /// <summary>방향 키를 누름·뗌(시각 time [s]). 키 반복으로 같은 누름이 다시 와도 가속 기준은 처음 누른 시각.</summary>
    public void Fly(FlyDir dir, bool down, double time)
    {
        int i = (int)dir;
        if (!double.IsFinite(time) || (uint)i >= (uint)_held.Length) return;
        Advance(time);
        if (down)
        {
            if (!double.IsFinite(_held[i])) _held[i] = time;
            if (!double.IsFinite(_flyLast)) _flyLast = time;
        }
        else _held[i] = double.NaN;
        if (!IsFlying) _flyLast = double.NaN;
    }

    /// <summary>모든 방향 키를 놓은 것으로(창 비활성 등 KeyUp 을 못 받을 때).</summary>
    public void StopFlying()
    {
        Array.Fill(_held, double.NaN);
        _flyLast = double.NaN;
    }

    /// <summary>누르고 있는 방향으로 time 까지 이동. 속도 = 거리 × 0.5/s × 가속 배율(누른 시간).</summary>
    public void Advance(double time)
    {
        if (!double.IsFinite(time) || !double.IsFinite(_flyLast) || time <= _flyLast) return;
        double gap = time - _flyLast - MaxStep;
        if (gap > 0)
        {
            // 멈춰 있던 시간은 건너뛴다(가속 기준도 함께 미룸)
            for (int i = 0; i < _held.Length; i++) _held[i] += gap;
            _flyLast += gap;
        }
        double speed = Distance * FlyRate;
        var move = Vec3.Zero;
        for (int i = 0; i < _held.Length; i++)
        {
            if (!double.IsFinite(_held[i])) continue;
            double s = speed * (RampIntegral(time - _held[i]) - RampIntegral(_flyLast - _held[i]));
            move += Direction((FlyDir)i) * s;
        }
        Pan += move;
        _flyLast = time;
    }

    /// <summary>계속 조작한 시간 t [s] 의 속도 배율 — 0.7 s 마다 ×2, 최대 ×256.</summary>
    public static double Ramp(double t) => Math.Min(Math.Pow(2, Math.Max(0, t) / RampDoubling), RampMax);

    /// <summary>∫₀ᵗ Ramp(τ) dτ [s] — 누르고 있던 동안의 이동 거리 = 시작 속도 × 이 값.</summary>
    public static double RampIntegral(double t)
    {
        if (!(t > 0)) return 0;
        double k = RampDoubling / Math.Log(2);
        double tCap = RampDoubling * Math.Log2(RampMax);
        return t <= tCap ? k * (Math.Pow(2, t / RampDoubling) - 1) : k * (RampMax - 1) + RampMax * (t - tCap);
    }

    // ---------------- 투영 ----------------

    /// <summary>커서 (px, py) [DIP] 방향, 시선 방향 성분이 1 인 벡터(깊이 d 의 점 = 눈 + 이 값 × d). 화면 밖 좌표는 가장자리로.</summary>
    public Vec3 CursorOffset(double px, double py)
    {
        double w = ViewWidth, h = ViewHeight;
        double nx = Math.Clamp(2 * px / w - 1, -1, 1), ny = Math.Clamp(1 - 2 * py / h, -1, 1);
        if (!double.IsFinite(nx) || !double.IsFinite(ny)) nx = ny = 0;
        return Forward + Right * (nx * TanHalf * w / h) + Up * (ny * TanHalf);
    }

    /// <summary>눈에서 커서 (px, py) 를 지나는 단위 광선.</summary>
    public Vec3 CursorRay(double px, double py) => CursorOffset(px, py).Normalized();

    /// <summary>NED 점 → 화면 위치 [DIP] 와 시선 방향 깊이 [m](깊이 ≤ 0 이면 카메라 뒤).</summary>
    public (double X, double Y, double Depth) Project(Vec3 p)
    {
        // 초점 기준으로 먼저 빼서 원점에서 먼 좌표에서도 정밀도 유지
        var r = p - Focus + Forward * Distance;
        double z = Vec3.Dot(r, Forward);
        double x = Vec3.Dot(r, Right) / (z * TanHalf * ViewWidth / ViewHeight), y = Vec3.Dot(r, Up) / (z * TanHalf);
        return ((x + 1) / 2 * ViewWidth, (1 - y) / 2 * ViewHeight, z);
    }

    private double AnchorDepth(double depth) =>
        double.IsFinite(depth) && depth > 0 ? Math.Clamp(depth, MinDistance * 0.1, MaxDistance * 10) : Distance;
}
