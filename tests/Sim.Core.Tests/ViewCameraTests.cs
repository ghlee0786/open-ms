using Sim.App.Rendering;
using Sim.Core.Geometry;
using Sim.Core.Numerics;

namespace Sim.Core.Tests;

/// <summary>
/// 3D 뷰 카메라 조작 시나리오(GL·창 없이 ViewCamera 에 입력 시퀀스를 넣고 위치·방향·거리를 수치로 확인).
/// 0.1 m 물체 근접부터 원점에서 수백 km·고도 수 km 공간까지 — 멈춤·튐·NaN 없음, 되돌리면 원위치, 커서 아래 점 고정.
/// </summary>
public class ViewCameraTests
{
    private const double W = 1200, H = 700;
    private const double Frame60 = 1 / 60.0;

    private static ViewCamera Cam(Vec3 focus, double dist, double yaw = ViewCamera.DefaultYaw, double pitch = ViewCamera.DefaultPitch)
    {
        var c = new ViewCamera();
        c.SetViewport(W, H);
        c.LookAt(focus, dist);
        c.SetAngles(yaw, pitch);
        return c;
    }

    /// <summary>장면 규모: 이름, 초점 N·E·D [m], 궤도 거리 [m].</summary>
    public static TheoryData<string, double, double, double, double> Scales => new()
    {
        { "근접 0.1 m 물체(최소 거리)", 0, 0, -1.2, ViewCamera.MinDistance },
        { "센서 0.3 m", 3, 2, -400, 0.3 },
        { "드론 추적 25 m", 120, -40, -300, 25 },
        { "궤적 전체 3 km", 1500, 800, -1500, 3000 },
        { "원점 300 km·고도 5 km 근접", 300e3, -250e3, -5000, 40 },
        { "원점 500 km·MC 산포 50 km", -500e3, 480e3, -10, 50e3 },
        { "최대 거리 200 km", 80e3, 60e3, -3000, ViewCamera.MaxDistance },
    };

    private static (Vec3 Eye, Vec3 Fwd, double Dist) Snap(ViewCamera c) => (c.Eye, c.Forward, c.Distance);

    /// <summary>원점에서 먼 좌표의 반올림까지 감안한 위치 허용 오차.</summary>
    private static double Tol(ViewCamera c) => 1e-11 * c.Eye.Norm + 1e-9 * c.Distance;

    private static void Near(double expected, double actual, double tol, string what) =>
        Assert.True(Math.Abs(expected - actual) <= tol, $"{what}: 기대 {expected:G17}, 실제 {actual:G17}, 허용 {tol:G3}");

    private static void NearV(Vec3 expected, Vec3 actual, double tol, string what) =>
        Assert.True((expected - actual).Norm <= tol, $"{what}: 기대 {expected}, 실제 {actual}, 차 {(expected - actual).Norm:G3}, 허용 {tol:G3}");

    private static void AssertSameView(ViewCamera c, (Vec3 Eye, Vec3 Fwd, double Dist) s, string what)
    {
        NearV(s.Eye, c.Eye, Tol(c), $"{what} 눈 위치");
        NearV(s.Fwd, c.Forward, 1e-9, $"{what} 시선");
        Near(s.Dist, c.Distance, 1e-9 * s.Dist, $"{what} 거리");
    }

    private static void Invariants(ViewCamera c)
    {
        Assert.True(c.Eye.IsFinite && c.Focus.IsFinite && c.Pan.IsFinite, $"NaN/무한: 눈 {c.Eye} 초점 {c.Focus}");
        Assert.InRange(c.Distance, ViewCamera.MinDistance, ViewCamera.MaxDistance);
        Assert.InRange(c.Pitch, ViewCamera.MinPitch, ViewCamera.MaxPitch);
        Assert.InRange(c.Yaw, -Math.PI - 1e-12, Math.PI + 1e-12);
        Near(1, c.Forward.Norm, 1e-12, "|앞|");
        Near(1, c.Right.Norm, 1e-12, "|오른쪽|");
        Near(1, c.Up.Norm, 1e-12, "|위|");
        Near(0, Vec3.Dot(c.Forward, c.Right), 1e-12, "앞·오른쪽");
        Near(0, Vec3.Dot(c.Forward, c.Up), 1e-12, "앞·위");
        Assert.True(c.Up.Z < 0, "화면 위쪽이 하늘(NED −Z)을 향해야 함");
        Near(c.Distance, (c.Focus - c.Eye).Norm, 1e-12 * (c.Focus.Norm + c.Distance) + 1e-12, "눈–초점 거리");
    }

    /// <summary>키를 dur 초 누르고 있다 뗀다(프레임 간격 dt 로 Advance). 시각 t 를 진행.</summary>
    private static void Hold(ViewCamera c, FlyDir dir, ref double t, double dur, double dt)
    {
        c.Fly(dir, true, t);
        double end = t + dur;
        while (t + dt < end) { t += dt; c.Advance(t); }
        t = end;
        c.Fly(dir, false, t);
    }

    private static FlyDir Opposite(FlyDir d) => d switch
    {
        FlyDir.Forward => FlyDir.Back,
        FlyDir.Back => FlyDir.Forward,
        FlyDir.Left => FlyDir.Right,
        FlyDir.Right => FlyDir.Left,
        FlyDir.Up => FlyDir.Down,
        _ => FlyDir.Up,
    };

    /// <summary>키를 dur 초 누를 때 이동 거리 [m](가속 곡선 적분).</summary>
    private static double FlyDistance(double dist, double dur) => dist * ViewCamera.FlyRate * ViewCamera.RampIntegral(dur);

    // ======================= 기본 =======================

    [Fact]
    public void Basis_is_orthonormal_and_screen_up_points_skyward_for_all_angles()
    {
        var c = Cam(Vec3.Zero, 10);
        for (double yaw = -12; yaw <= 12; yaw += 0.37)
            for (double pitch = -2; pitch <= 2; pitch += 0.05)
            {
                c.SetAngles(yaw, pitch);
                Invariants(c);
                Assert.Equal(0, c.Right.Z);
            }
        c.SetAngles(0, 9);
        Assert.Equal(ViewCamera.MaxPitch, c.Pitch);
        c.SetAngles(0, -9);
        Assert.Equal(ViewCamera.MinPitch, c.Pitch);
    }

    [Theory, MemberData(nameof(Scales))]
    public void Focus_projects_to_screen_center_and_eye_sits_distance_behind(string name, double n, double e, double d, double dist)
    {
        var c = Cam(new Vec3(n, e, d), dist);
        var p = c.Project(c.Focus);
        Near(W / 2, p.X, 1e-9, $"{name} 초점 x");
        Near(H / 2, p.Y, 1e-9, $"{name} 초점 y");
        Near(dist, p.Depth, 1e-9 * dist, $"{name} 초점 깊이");
        NearV(c.Focus - c.Forward * dist, c.Eye, Tol(c), $"{name} 눈");
        Invariants(c);
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(1.0, -0.4)]
    [InlineData(-2.5, 1.5)]
    public void Projection_matches_the_gl_renderer_view_and_projection(double yaw, double pitch)
    {
        // GlView 와 같은 방식(초점 기준 GL 좌표 · LookAt(up=+Y) · 원근)으로 투영한 화면 위치가 ViewCamera.Project 와 같아야
        // 테스트의 "커서 아래 점 고정"이 실제 화면에서도 성립한다.
        var c = Cam(new Vec3(250e3, -120e3, -2000), 30, yaw, pitch);
        var eyeGl = Frames.ToGl(c.Forward * -c.Distance);
        var view = System.Numerics.Matrix4x4.CreateLookAt(new((float)eyeGl.X, (float)eyeGl.Y, (float)eyeGl.Z), System.Numerics.Vector3.Zero, System.Numerics.Vector3.UnitY);
        float fov = (float)ViewCamera.Fov, aspect = (float)(W / H), near = 0.05f, far = 60000f;
        float tq = 1f / MathF.Tan(fov / 2);
        var proj = new System.Numerics.Matrix4x4(tq / aspect, 0, 0, 0, 0, tq, 0, 0, 0, 0, (far + near) / (near - far), -1, 0, 0, 2 * far * near / (near - far), 0);
        var vp = view * proj;
        var r = new Random(5);
        for (int i = 0; i < 50; i++)
        {
            var p = c.Focus + new Vec3(r.NextDouble() * 40 - 20, r.NextDouble() * 40 - 20, r.NextDouble() * 40 - 20);
            var q = c.Project(p);
            if (q.Depth < 1) continue;
            var g = Frames.ToGl(p - c.Focus);
            var clip = System.Numerics.Vector4.Transform(new System.Numerics.Vector4((float)g.X, (float)g.Y, (float)g.Z, 1), vp);
            double sx = (clip.X / clip.W + 1) / 2 * W, sy = (1 - clip.Y / clip.W) / 2 * H;
            Near(sx, q.X, 0.05, $"GL x #{i}");
            Near(sy, q.Y, 0.05, $"GL y #{i}");
            Near(clip.W, q.Depth, 1e-4 * q.Depth, $"GL 깊이 #{i}");
        }
    }

    [Fact]
    public void Invalid_inputs_are_ignored_without_nan()
    {
        var c = Cam(new Vec3(1000, 2000, -500), 25);
        var s = Snap(c);
        foreach (var bad in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            c.Orbit(bad, 0.1); c.Orbit(0.1, bad);
            c.Look(bad, 0); c.Look(0, bad);
            c.PanPixels(bad, 3); c.PanPixels(3, bad);
            c.Wheel(bad, 10, 10);
            c.Dolly(bad, 10, 10, 1);
            c.Zoom(bad); c.Zoom(0); c.Zoom(-1);
            c.SetDistance(bad);
            c.SetAngles(bad, 0);
            c.LookAt(new Vec3(bad, 0, 0), 5);
            c.Target = new Vec3(0, bad, 0);
            c.SetViewport(bad, bad); c.SetViewport(0, 0); c.SetViewport(-5, 0.5);
            c.Fly(FlyDir.Forward, true, bad);
            c.Advance(bad);
            Invariants(c);
        }
        // 휠 0칸·화면 밖 커서·0 깊이도 안전
        c.Wheel(0, 10, 10); c.Wheel(1, -1e9, 1e12, 0); c.Wheel(-1, -1e9, 1e12, -5);
        c.PanPixels(10, 10, 0); c.PanPixels(-10, -10, -3);
        Invariants(c);
        Assert.False(c.IsFlying);
        Near(s.Dist, c.Distance, 1e-9 * s.Dist, "거리");
    }

    // ======================= 누르고 있는 동안 비행(W/S/A/D/E/Q) =======================

    [Theory, MemberData(nameof(Scales))]
    public void Holding_W_then_S_for_the_same_time_returns_to_start(string name, double n, double e, double d, double dist)
    {
        var c = Cam(new Vec3(n, e, d), dist);
        var s = Snap(c);
        double t = 37.25;
        Hold(c, FlyDir.Forward, ref t, 1.7, Frame60);
        // 시선 방향으로만, 정확히 가속 곡선 적분만큼 전진(거리·방향 그대로 — 줌이 아님)
        var moved = c.Eye - s.Eye;
        double expect = FlyDistance(dist, 1.7);
        Near(expect, Vec3.Dot(moved, s.Fwd), 1e-9 * expect + Tol(c), $"{name} 전진량");
        Near(0, (moved - s.Fwd * Vec3.Dot(moved, s.Fwd)).Norm, 1e-9 * expect + Tol(c), $"{name} 옆 성분");
        Near(s.Dist, c.Distance, 0, $"{name} 거리 불변");
        NearV(s.Fwd, c.Forward, 0, $"{name} 방향 불변");
        Assert.False(c.IsFlying);
        t += 0.8;
        Hold(c, FlyDir.Back, ref t, 1.7, Frame60);
        AssertSameView(c, s, name);
    }

    [Fact]
    public void Fly_distance_is_independent_of_frame_rate()
    {
        double Run(Func<double, double> nextDt)
        {
            var c = Cam(new Vec3(400e3, 100e3, -3000), 25);
            var e0 = c.Eye;
            double t = 0;
            c.Fly(FlyDir.Forward, true, t);
            while (true)
            {
                double next = t + nextDt(t);
                if (next >= 3) break;
                t = next;
                c.Advance(t);
            }
            c.Fly(FlyDir.Forward, false, 3);
            return (c.Eye - e0).Norm;
        }
        var r = new Random(9);
        double expect = FlyDistance(25, 3);
        foreach (var (label, dt) in new (string, Func<double, double>)[]
                 {
                     ("30 Hz", _ => 1 / 30.0), ("60 Hz", _ => Frame60), ("144 Hz", _ => 1 / 144.0),
                     ("불규칙 2~92 ms", _ => 0.002 + r.NextDouble() * 0.09),
                 })
            Near(expect, Run(dt), 1e-9 * expect, label);
    }

    [Theory]
    [InlineData(ViewCamera.MinDistance, 0.003)]
    [InlineData(0.3, 0.008)]
    [InlineData(25.0, 0.7)]
    [InlineData(5000.0, 130.0)]
    public void Short_tap_moves_a_small_scale_proportional_step(double dist, double maxMove)
    {
        // 50 ms 톡 누름 = 거리의 약 2.5 % — 0.1 m 물체 앞에서는 mm 단위로 정밀
        var c = Cam(new Vec3(-30e3, 70e3, -800), dist);
        var e0 = c.Eye;
        c.Fly(FlyDir.Forward, true, 5);
        c.Fly(FlyDir.Forward, false, 5.05);
        double moved = (c.Eye - e0).Norm;
        Near(FlyDistance(dist, 0.05), moved, 1e-9 * dist, "톡 누름 이동");
        Assert.InRange(moved, 0.02 * dist, maxMove);
    }

    [Fact]
    public void Long_hold_near_a_tiny_object_accelerates_smoothly_and_never_stalls()
    {
        // 0.1 m 거리에서 W 를 20 초 — 처음엔 mm 단위, 계속 누르면 0.7 s 마다 두 배로 빨라져 수백 m 까지 간다(멈춤 없음)
        var c = Cam(new Vec3(5, 5, -200), ViewCamera.MinDistance);
        var fwd = c.Forward;
        var e0 = c.Eye;
        double t = 0, prevStep = 0, maxStep = 0;
        c.Fly(FlyDir.Forward, true, t);
        var prev = c.Eye;
        for (int i = 1; i <= 1200; i++)
        {
            t = i * Frame60;
            c.Advance(t);
            double step = Vec3.Dot(c.Eye - prev, fwd);
            prev = c.Eye;
            Assert.True(step > 0, $"프레임 {i}: 멈춤");
            Assert.True(step >= prevStep * (1 - 1e-9), $"프레임 {i}: 감속 {step} < {prevStep}");
            // 한 프레임 이동은 최고 속도 × 프레임 간격을 넘지 않는다(튐 없음)
            Assert.True(step <= ViewCamera.MinDistance * ViewCamera.FlyRate * ViewCamera.RampMax * Frame60 * (1 + 1e-9), $"프레임 {i}: 튐 {step}");
            prevStep = step;
            maxStep = Math.Max(maxStep, step);
            Invariants(c);
        }
        c.Fly(FlyDir.Forward, false, t);
        double total = (c.Eye - e0).Norm;
        Near(FlyDistance(ViewCamera.MinDistance, 20), total, 1e-9 * total, "20 s 이동");
        Assert.True(total > 100, $"20 초에 {total:F1} m — 근접에서도 공간 이동이 멈추지 않아야");
        Assert.True(maxStep < 0.25, $"최고 프레임 이동 {maxStep:F3} m");
    }

    [Fact]
    public void Frame_hitch_does_not_teleport_the_camera()
    {
        var c = Cam(new Vec3(100, 100, -500), 25);
        var fwd = c.Forward;
        var e0 = c.Eye;
        double t = 0;
        c.Fly(FlyDir.Forward, true, t);
        for (int i = 1; i <= 60; i++) { t = i * Frame60; c.Advance(t); }
        var before = c.Eye;
        t += 5; // 5 초 동안 프레임이 오지 않음(창 끌기·GC 등)
        c.Advance(t);
        double jump = Vec3.Dot(c.Eye - before, fwd);
        double allowed = 25 * ViewCamera.FlyRate * (ViewCamera.RampIntegral(1 + ViewCamera.MaxStep) - ViewCamera.RampIntegral(1));
        Near(allowed, jump, 1e-9 * allowed, "멈춘 뒤 첫 프레임 이동 = 0.1 s 분량");
        double t1 = t;
        for (int i = 1; i <= 60; i++) { t = t1 + i * Frame60; c.Advance(t); }
        c.Fly(FlyDir.Forward, false, t);
        // 멈춘 시간은 건너뛴 것으로 — 전체 = 1 s + 0.1 s + 1 s 누른 것과 같다
        double total = Vec3.Dot(c.Eye - e0, fwd);
        Near(FlyDistance(25, 2.1), total, 1e-9 * total, "전체 이동");
    }

    [Fact]
    public void Strafe_and_vertical_keys_move_along_screen_side_and_world_vertical()
    {
        // 아래를 비스듬히 내려다보는 상태(피치 1.2)
        var c = Cam(new Vec3(20e3, -8e3, -2500), 300, 0.7, 1.2);
        double t = 0;
        var right = c.Right; var fwd = c.Forward;
        double s1 = FlyDistance(300, 1);

        var e0 = c.Eye;
        Hold(c, FlyDir.Left, ref t, 1, Frame60);
        NearV(e0 - right * s1, c.Eye, 1e-9 * s1 + Tol(c), "A = 왼쪽(수평)");
        Near(e0.Z, c.Eye.Z, 1e-9, "A 는 고도 불변");

        e0 = c.Eye;
        Hold(c, FlyDir.Right, ref t, 1, Frame60);
        NearV(e0 + right * s1, c.Eye, 1e-9 * s1 + Tol(c), "D = 오른쪽");

        e0 = c.Eye;
        Hold(c, FlyDir.Up, ref t, 1, Frame60);
        NearV(e0 + new Vec3(0, 0, -s1), c.Eye, 1e-9 * s1 + Tol(c), "E = 연직 상승(피치와 무관)");

        e0 = c.Eye;
        Hold(c, FlyDir.Down, ref t, 1, Frame60);
        NearV(e0 + new Vec3(0, 0, s1), c.Eye, 1e-9 * s1 + Tol(c), "Q = 연직 하강");

        e0 = c.Eye;
        Hold(c, FlyDir.Forward, ref t, 1, Frame60);
        NearV(e0 + fwd * s1, c.Eye, 1e-9 * s1 + Tol(c), "W = 시선 방향(내려다보면 지면 쪽)");
        Assert.True(c.Eye.Z > e0.Z + 0.9 * s1, "피치 1.2 에서 W 는 대부분 하강");
        Invariants(c);
    }

    [Fact]
    public void Diagonal_keys_compose_and_opposite_keys_cancel()
    {
        var c = Cam(new Vec3(0, 0, -100), 25);
        var fwd = c.Forward; var right = c.Right;
        var e0 = c.Eye;
        c.Fly(FlyDir.Forward, true, 0);
        c.Fly(FlyDir.Right, true, 0);
        for (int i = 1; i <= 90; i++) c.Advance(i * Frame60);
        c.Fly(FlyDir.Forward, false, 1.5);
        c.Fly(FlyDir.Right, false, 1.5);
        double s = FlyDistance(25, 1.5);
        NearV(e0 + (fwd + right) * s, c.Eye, 1e-9 * s, "W+D 대각선");

        e0 = c.Eye;
        c.Fly(FlyDir.Forward, true, 10);
        c.Fly(FlyDir.Back, true, 10);
        for (int i = 1; i <= 90; i++) c.Advance(10 + i * Frame60);
        c.Fly(FlyDir.Back, false, 11.5);
        c.Fly(FlyDir.Forward, false, 11.5);
        NearV(e0, c.Eye, 1e-9 * s, "W+S 동시 = 제자리");
    }

    [Fact]
    public void Key_repeat_does_not_restart_acceleration()
    {
        var a = Cam(new Vec3(7, 7, -70), 25);
        var b = Cam(new Vec3(7, 7, -70), 25);
        a.Fly(FlyDir.Forward, true, 0);
        b.Fly(FlyDir.Forward, true, 0);
        for (int i = 1; i <= 120; i++)
        {
            double t = i * Frame60;
            a.Advance(t);
            if (i > 30 && i % 2 == 0) b.Fly(FlyDir.Forward, true, t); // OS 키 반복(0.5 s 뒤부터 30 Hz)
            else b.Advance(t);
        }
        a.Fly(FlyDir.Forward, false, 2);
        b.Fly(FlyDir.Forward, false, 2);
        NearV(a.Eye, b.Eye, 1e-9, "키 반복 유무");
    }

    [Fact]
    public void Stop_flying_releases_every_key()
    {
        var c = Cam(Vec3.Zero, 25);
        c.Fly(FlyDir.Forward, true, 0);
        c.Fly(FlyDir.Up, true, 0);
        c.Advance(0.5);
        Assert.True(c.IsFlying);
        c.StopFlying();
        Assert.False(c.IsFlying);
        var e = c.Eye;
        c.Advance(3);
        c.Fly(FlyDir.Forward, false, 4); // 늦게 온 KeyUp 도 무해
        NearV(e, c.Eye, 0, "정지 후 이동 없음");
    }

    [Fact]
    public void Zooming_while_flying_scales_the_speed_without_a_jump()
    {
        var c = Cam(new Vec3(0, 0, -1000), 40);
        var fwd = c.Forward;
        c.Fly(FlyDir.Forward, true, 0);
        for (int i = 1; i <= 30; i++) c.Advance(i * Frame60);
        var p0 = c.Eye;
        c.Advance(31 * Frame60);
        double stepBefore = Vec3.Dot(c.Eye - p0, fwd);
        c.Zoom(0.5); // 비행 중 확대 → 거리 20 m
        Near(20, c.Distance, 1e-12, "확대 후 거리");
        var p1 = c.Eye;
        c.Advance(32 * Frame60);
        double stepAfter = Vec3.Dot(c.Eye - p1, fwd);
        double ratio = stepAfter / stepBefore;
        Assert.InRange(ratio, 0.5, 0.52); // 속도는 거리에 비례(가속은 그대로 이어 감)
        c.StopFlying();
    }

    [Fact]
    public void Long_range_flight_across_hundreds_of_km_and_back()
    {
        // 원점에서 300 km·고도 5 km, 거리 2 km 로 축소한 상태에서 W 6 초 → 수백 km 이동, S 6 초 → 원위치
        var c = Cam(new Vec3(300e3, -250e3, -5000), 2000, 1.1, 0.05);
        var s = Snap(c);
        double t = 1e4;
        Hold(c, FlyDir.Forward, ref t, 6, Frame60);
        double moved = (c.Eye - s.Eye).Norm;
        Assert.InRange(moved, 1e5, 1e6);
        Invariants(c);
        t += 2;
        Hold(c, FlyDir.Back, ref t, 6, Frame60);
        AssertSameView(c, s, "왕복");
    }

    // ======================= Ctrl+휠 공간 전진·후진 =======================

    [Theory, MemberData(nameof(Scales))]
    public void Dolly_keeps_the_point_under_the_cursor_fixed_and_the_distance_unchanged(string name, double n, double e, double d, double dist)
    {
        foreach (var (px, py) in new[] { (W / 2, H / 2), (10.0, 10.0), (W - 1, H - 1), (300.0, 520.0) })
        {
            var c = Cam(new Vec3(n, e, d), dist, 0.3, 0.6);
            var s = Snap(c);
            double depth = dist * 3;
            var p = c.Eye + c.CursorOffset(px, py) * depth; // 커서 아래, 이동량보다 먼 점
            c.Dolly(2, px, py);
            var q = c.Project(p);
            Near(px, q.X, 1e-6, $"{name} ({px},{py}) x");
            Near(py, q.Y, 1e-6, $"{name} ({px},{py}) y");
            Assert.True(q.Depth < depth, $"{name}: 다가가야 함");
            Near(2 * ViewCamera.DollyFraction * dist, (c.Eye - s.Eye).Norm, 1e-9 * dist + Tol(c), $"{name} 두 칸 = 거리의 30 %");
            Assert.Equal(s.Dist, c.Distance);
            NearV(s.Fwd, c.Forward, 0, $"{name} 방향 불변");
        }
    }

    [Theory, MemberData(nameof(Scales))]
    public void Dolly_in_then_out_with_the_same_rhythm_returns_to_start(string name, double n, double e, double d, double dist)
    {
        var c = Cam(new Vec3(n, e, d), dist, -2.2, 0.9);
        var s = Snap(c);
        double t = 500;
        for (int i = 0; i < 25; i++) { t += 0.04; c.Dolly(1, 820, 210, t); }
        Assert.True((c.Eye - s.Eye).Norm > 25 * ViewCamera.DollyFraction * dist, $"{name}: 연속으로 굴리면 가속");
        t += 1;
        for (int i = 0; i < 25; i++) { t += 0.04; c.Dolly(-1, 820, 210, t); }
        AssertSameView(c, s, name);
    }

    [Fact]
    public void Dolly_accelerates_while_scrolling_and_resets_after_a_pause_or_reversal()
    {
        var c = Cam(new Vec3(1e3, 2e3, -300), 25);
        double t = 0;
        double Step(double notches)
        {
            var e0 = c.Eye;
            c.Dolly(notches, W / 2, H / 2, t);
            return (c.Eye - e0).Norm;
        }
        double baseStep = ViewCamera.DollyFraction * 25;
        Near(baseStep, Step(1), 1e-9, "첫 칸");
        double prev = baseStep;
        for (int i = 1; i < 40; i++)
        {
            t += 0.05;
            double s = Step(1);
            Assert.True(s > prev, $"칸 {i}: 가속");
            Assert.True(s < prev * 1.1, $"칸 {i}: 튐 없음({s / prev:F3}배)");
            prev = s;
        }
        Near(baseStep * ViewCamera.Ramp(39 * 0.05), prev, 1e-9 * prev, "2 초 연속 배율");
        t += 0.5; // 쉼
        Near(baseStep, Step(1), 1e-9, "쉰 뒤 다시 기본");
        t += 0.05;
        Assert.True(Step(1) > baseStep);
        t += 0.05;
        Near(baseStep, Step(-1), 1e-9, "방향을 바꾸면 기본");
        Near(baseStep, Step(1), 1e-9, "다시 반대 = 기본");
    }

    [Theory]
    [InlineData(ViewCamera.MinDistance)]
    [InlineData(2.0)]
    [InlineData(25.0)]
    [InlineData(3000.0)]
    [InlineData(ViewCamera.MaxDistance)]
    public void Single_dolly_notch_is_fifteen_percent_of_distance_and_touchpad_fractions_are_proportional(double dist)
    {
        var c = Cam(new Vec3(-7e3, 9e3, -50), dist);
        var e0 = c.Eye;
        c.Dolly(1, W / 2, H / 2);
        Near(0.15 * dist, (c.Eye - e0).Norm, 1e-9 * dist + Tol(c), "한 칸");
        e0 = c.Eye;
        c.Dolly(0.1, W / 2, H / 2);
        Near(0.015 * dist, (c.Eye - e0).Norm, 1e-9 * dist + Tol(c), "터치패드 0.1 칸");
    }

    // ======================= 휠 확대 =======================

    [Theory, MemberData(nameof(Scales))]
    public void Wheel_zoom_keeps_the_point_under_the_cursor_fixed(string name, double n, double e, double d, double dist)
    {
        foreach (var (px, py) in new[] { (W / 2, H / 2), (5.0, 690.0), (1190.0, 15.0), (777.0, 333.0) })
        {
            var c = Cam(new Vec3(n, e, d), dist, 2.0, 0.45);
            var p = c.Eye + c.CursorOffset(px, py) * c.Distance; // 커서 아래 초점 평면의 점
            foreach (var notches in new[] { 3.0, -5.0, 0.25, 2.0 })
            {
                c.Wheel(notches, px, py, double.NaN, 0);
                var q = c.Project(p);
                Near(px, q.X, 1e-6, $"{name} 휠 {notches} ({px},{py}) x");
                Near(py, q.Y, 1e-6, $"{name} 휠 {notches} ({px},{py}) y");
                Invariants(c);
            }
        }
    }

    [Theory]
    [InlineData(25.0)]
    [InlineData(3000.0)]
    [InlineData(0.5)]
    public void Wheel_zoom_toward_an_object_keeps_that_object_fixed_and_closes_in(double dist)
    {
        // 초점보다 먼(또는 가까운) 물체 위에서 휠 → 그 물체가 화면에서 움직이지 않고 다가온다
        foreach (double rel in new[] { 4.0, 0.4 })
        {
            var c = Cam(new Vec3(220e3, 10e3, -900), dist, -1, 0.3);
            double px = 300, py = 450, depth = dist * rel;
            var obj = c.Eye + c.CursorOffset(px, py) * depth;
            double before = (obj - c.Eye).Norm;
            for (int i = 0; i < 5; i++) c.Wheel(1, px, py, depth * Math.Pow(ViewCamera.WheelZoom, -i), 0);
            var q = c.Project(obj);
            Near(px, q.X, 1e-6, $"물체 x (깊이 {rel}×거리)");
            Near(py, q.Y, 1e-6, $"물체 y (깊이 {rel}×거리)");
            Near(before * Math.Pow(ViewCamera.WheelZoom, -5), (obj - c.Eye).Norm, 1e-9 * before, "물체까지 거리도 같은 비율로");
        }
    }

    [Theory, MemberData(nameof(Scales))]
    public void Wheel_in_then_out_returns_exactly(string name, double n, double e, double d, double dist)
    {
        var c = Cam(new Vec3(n, e, d), dist, 0.9, -0.3);
        var s = Snap(c);
        // 경계(최소·최대 거리)에 닿지 않는 순서로
        int sign = dist * Math.Pow(ViewCamera.WheelZoom, -6) >= ViewCamera.MinDistance ? 1 : -1;
        for (int i = 0; i < 6; i++) c.Wheel(sign, 640, 120, double.NaN, i * 0.05);
        Assert.NotEqual(s.Dist, c.Distance);
        for (int i = 0; i < 6; i++) c.Wheel(-sign, 640, 120, double.NaN, 1 + i * 0.05);
        AssertSameView(c, s, name);
    }

    [Fact]
    public void Wheel_at_minimum_distance_keeps_moving_forward_without_stall_or_jump()
    {
        // 2 m 에서 0.1 m 물체 쪽으로 휠을 계속 — 최소 거리에 닿은 뒤에도 같은 크기로 전진이 이어진다
        var c = Cam(new Vec3(3, -4, -150), 2.0, 0.2, 0.25);
        var fwd = c.Forward;
        var e0 = c.Eye;
        double t = 0, prevStep = double.NaN;
        bool reachedMin = false;
        for (int i = 0; i < 80; i++)
        {
            t += 0.12;
            var before = c.Eye;
            c.Wheel(1, W / 2, H / 2, double.NaN, t);
            double step = Vec3.Dot(c.Eye - before, fwd);
            Assert.True(step > 1e-3, $"칸 {i}: 멈춤({step:G3} m)");
            if (!double.IsNaN(prevStep))
            {
                Assert.True(step < prevStep * 1.6, $"칸 {i}: 튐 {step:G3} / {prevStep:G3}");
                Assert.True(step > prevStep * 0.8, $"칸 {i}: 급정지 {step:G3} / {prevStep:G3}");
            }
            prevStep = step;
            reachedMin |= c.Distance == ViewCamera.MinDistance;
            Invariants(c);
        }
        Assert.True(reachedMin);
        Assert.Equal(ViewCamera.MinDistance, c.Distance);
        Assert.True(Vec3.Dot(c.Eye - e0, fwd) > 3, "최소 거리 뒤에도 계속 전진");
    }

    [Fact]
    public void Wheel_out_at_maximum_distance_stops_cleanly()
    {
        var c = Cam(new Vec3(0, 0, -3000), 150e3);
        for (int i = 0; i < 50; i++) c.Wheel(-3, 100, 600, double.NaN, i * 0.02);
        Assert.Equal(ViewCamera.MaxDistance, c.Distance);
        Invariants(c);
        var e = c.Eye;
        c.Wheel(-1, 100, 600, double.NaN, 5);
        NearV(e, c.Eye, 0, "최대 거리에서 더 축소해도 그대로");
    }

    [Fact]
    public void Zoom_keys_and_buttons_also_push_through_the_minimum_distance()
    {
        var c = Cam(new Vec3(0, 0, -50), 0.2);
        var fwd = c.Forward;
        var prev = c.Eye;
        for (int i = 0; i < 20; i++)
        {
            c.Zoom(1 / 1.25);
            double step = Vec3.Dot(c.Eye - prev, fwd);
            prev = c.Eye;
            Assert.True(step > 0.01, $"+ 키 {i}: {step:G3} m");
            Assert.True(step < 0.06, $"+ 키 {i}: 튐 {step:G3} m");
        }
        Assert.Equal(ViewCamera.MinDistance, c.Distance);
        c.Zoom(1.25);
        Near(0.125, c.Distance, 1e-12, "- 키 = 축소");
    }

    // ======================= 화면 평면 이동(팬) =======================

    [Theory, MemberData(nameof(Scales))]
    public void Pan_drag_keeps_the_grabbed_point_exactly_under_the_cursor(string name, double n, double e, double d, double dist)
    {
        var c = Cam(new Vec3(n, e, d), dist, -0.8, 1.0);
        double px0 = 410, py0 = 260;
        var grabbed = c.Eye + c.CursorOffset(px0, py0) * c.Distance;
        var r = new Random(3);
        double sx = 0, sy = 0;
        for (int i = 0; i < 37; i++)
        {
            double dx = r.NextDouble() * 24 - 9, dy = r.NextDouble() * 18 - 7;
            c.PanPixels(dx, dy);
            sx += dx; sy += dy;
            var q = c.Project(grabbed);
            Near(px0 + sx, q.X, 1e-6, $"{name} 드래그 {i} x");
            Near(py0 + sy, q.Y, 1e-6, $"{name} 드래그 {i} y");
        }
        Assert.Equal(dist, c.Distance);
    }

    [Theory]
    [InlineData(0.1, 7.0)]
    [InlineData(25.0, 0.3)]
    [InlineData(40000.0, 2.5)]
    public void Pan_grabbing_an_object_at_another_depth_tracks_that_object(double dist, double rel)
    {
        var c = Cam(new Vec3(-120e3, 33e3, -1500), dist, 0.1, 0.5);
        double px0 = 900, py0 = 500, depth = dist * rel;
        var obj = c.Eye + c.CursorOffset(px0, py0) * depth;
        c.PanPixels(-250, 130, depth);
        c.PanPixels(12.5, -3.25, depth);
        var q = c.Project(obj);
        Near(px0 - 237.5, q.X, 1e-6, "물체 x");
        Near(py0 + 126.75, q.Y, 1e-6, "물체 y");
        Near(depth, q.Depth, 1e-9 * depth, "깊이 불변(화면 평면 이동)");
    }

    [Fact]
    public void Close_up_pan_is_sub_millimetre_per_pixel_and_far_pan_scales_up()
    {
        var near = Cam(Vec3.Zero, ViewCamera.MinDistance);
        double mpp = near.MetersPerPixel();
        // 이전 구현은 5 m 하한 때문에 0.3 m 거리에서도 ≈ 6.9 mm/DIP(물체가 커서보다 16배 빨리 튐)
        Assert.InRange(mpp, 1e-4, 2e-4);
        var f0 = near.Focus;
        near.PanPixels(1, 0);
        Near(mpp, (near.Focus - f0).Norm, 1e-15, "1 DIP 이동");

        var far = Cam(new Vec3(450e3, -450e3, -8000), 100e3);
        Assert.InRange(far.MetersPerPixel(), 100, 200);
        Near(far.MetersPerPixel() * 3, far.MetersPerPixel(3 * far.Distance), 1e-9, "깊이에 비례");
    }

    [Theory, MemberData(nameof(Scales))]
    public void Pan_there_and_back_returns(string name, double n, double e, double d, double dist)
    {
        var c = Cam(new Vec3(n, e, d), dist);
        var s = Snap(c);
        c.PanPixels(321.5, -77.25, dist * 2);
        c.PanPixels(-40, 12);
        c.PanPixels(40, -12);
        c.PanPixels(-321.5, 77.25, dist * 2);
        AssertSameView(c, s, name);
    }

    // ======================= 궤도 회전 =======================

    [Theory, MemberData(nameof(Scales))]
    public void Orbit_keeps_the_focus_fixed_on_screen_center_and_the_eye_on_the_sphere(string name, double n, double e, double d, double dist)
    {
        var c = Cam(new Vec3(n, e, d), dist);
        var focus = c.Focus;
        var r = new Random(17);
        for (int i = 0; i < 200; i++)
        {
            c.OrbitDrag(r.NextDouble() * 60 - 30, r.NextDouble() * 40 - 20);
            NearV(focus, c.Focus, 0, $"{name} 초점 고정");
            Near(dist, (c.Eye - focus).Norm, 1e-12 * (focus.Norm + dist) + 1e-12, $"{name} 구면");
            var q = c.Project(focus);
            Near(W / 2, q.X, 1e-9, $"{name} 화면 중앙 x");
            Near(H / 2, q.Y, 1e-9, $"{name} 화면 중앙 y");
            Invariants(c);
        }
    }

    [Fact]
    public void Orbit_drag_turns_half_a_turn_per_view_height()
    {
        var c = Cam(Vec3.Zero, 10, 0.5, 0.0);
        c.OrbitDrag(0, H / 4);
        Near(Math.PI / 4, c.Pitch, 1e-12, "뷰 높이 1/4 = 45°");
        c.OrbitDrag(H, 0);
        Near(Math.IEEERemainder(0.5 - Math.PI, 2 * Math.PI), c.Yaw, 1e-12, "뷰 높이만큼 = 180°");
        // 1 DIP 의 각도 분해능(이전 0.008 rad 보다 촘촘)
        Assert.True(Math.PI / H < 0.005);
    }

    [Fact]
    public void Full_turn_in_a_thousand_small_drags_returns_to_the_same_view()
    {
        var c = Cam(new Vec3(-250e3, 300e3, -4000), 75, 1.3, 0.4);
        var s = Snap(c);
        for (int i = 0; i < 1000; i++) c.OrbitDrag(2 * H / 1000.0, 0); // 합계 2·H DIP = 360°
        AssertSameView(c, s, "360° 궤도");
    }

    [Fact]
    public void Orbit_and_look_past_the_pitch_limits_stay_finite()
    {
        var c = Cam(new Vec3(5, 5, -5), 12);
        for (int i = 0; i < 100; i++) { c.OrbitDrag(3, 500); Invariants(c); }
        Assert.Equal(ViewCamera.MaxPitch, c.Pitch);
        for (int i = 0; i < 100; i++) { c.OrbitDrag(-3, -500); Invariants(c); }
        Assert.Equal(ViewCamera.MinPitch, c.Pitch);
        var eye = c.Eye;
        for (int i = 0; i < 100; i++) { c.LookDrag(7, -900); Invariants(c); }
        Assert.Equal(ViewCamera.MaxPitch, c.Pitch);
        NearV(eye, c.Eye, 1e-12, "시점 회전은 한계에서도 눈 고정");
    }

    // ======================= 제자리 시점 회전 =======================

    [Theory, MemberData(nameof(Scales))]
    public void Look_keeps_the_eye_fixed_and_round_trips(string name, double n, double e, double d, double dist)
    {
        var c = Cam(new Vec3(n, e, d), dist);
        var s = Snap(c);
        var r = new Random(23);
        var drags = new List<(double, double)>();
        for (int i = 0; i < 150; i++)
        {
            double dx = r.NextDouble() * 80 - 40, dy = r.NextDouble() * 40 - 20;
            double next = c.Pitch - dy * 2 * Math.Tan(ViewCamera.Fov / 2) / H;
            if (next <= ViewCamera.MinPitch || next >= ViewCamera.MaxPitch) continue;
            c.LookDrag(dx, dy);
            drags.Add((dx, dy));
            NearV(s.Eye, c.Eye, Tol(c), $"{name} 눈 고정 {i}");
            Assert.Equal(dist, c.Distance);
        }
        Assert.True((c.Forward - s.Fwd).Norm > 1e-3 || drags.Count < 10);
        for (int i = drags.Count - 1; i >= 0; i--) c.LookDrag(-drags[i].Item1, -drags[i].Item2);
        AssertSameView(c, s, name);
    }

    [Fact]
    public void Look_drag_moves_the_scene_with_the_cursor_near_the_center()
    {
        var c = Cam(new Vec3(12e3, 5e3, -700), 30, 0.25, 0.0);
        var p = c.Eye + c.Forward * 10; // 화면 가운데, 10 m 앞
        c.LookDrag(5, 0);
        var q = c.Project(p);
        Near(W / 2 + 5, q.X, 0.01, "오른쪽으로 끌면 장면도 오른쪽으로");
        Near(H / 2, q.Y, 0.01, "수평 끌기에 세로 변화 없음");
        c.LookDrag(-5, 0);
        c.LookDrag(0, -3);
        q = c.Project(p);
        Near(W / 2, q.X, 0.01, "세로 끌기 x");
        Near(H / 2 - 3, q.Y, 0.01, "위로 끌면 장면도 위로");
    }

    [Fact]
    public void Look_then_fly_moves_along_the_new_heading()
    {
        var c = Cam(new Vec3(0, 0, -2000), 25, 0, 0);
        c.Look(Math.PI / 2, 0); // 동쪽을 봄
        NearV(new Vec3(0, 1, 0), c.Forward, 1e-12, "동쪽");
        var e0 = c.Eye;
        double t = 0;
        Hold(c, FlyDir.Forward, ref t, 2, Frame60);
        double s = FlyDistance(25, 2);
        NearV(e0 + new Vec3(0, s, 0), c.Eye, 1e-9 * s, "동쪽으로 비행");
    }

    // ======================= 복합 조작 시나리오 =======================

    [Fact]
    public void Scenario_inspect_a_tiny_sensor_up_close_then_back_out()
    {
        // 0.1 m 센서(반경 0.05 m)를 25 m 에서 휠로 끝까지 확대 → 둘레 360° 회전 → 살짝 팬·시점 → 되돌아 나오기
        var sensor = new Vec3(12, -7, -300);
        var c = Cam(sensor, 25);
        var s = Snap(c);
        double t = 0;
        int notches = 0;
        while (c.Distance * Math.Pow(ViewCamera.WheelZoom, -1) >= ViewCamera.MinDistance)
        {
            t += 0.08;
            c.Wheel(1, W / 2, H / 2, c.Distance, t);
            notches++;
            Assert.True(OnScreen(c, sensor), $"확대 {notches}: 센서가 화면에 있어야");
        }
        Assert.InRange(c.Distance, ViewCamera.MinDistance, ViewCamera.MinDistance * ViewCamera.WheelZoom);
        Assert.True(ProjectedRadius(c, sensor, 0.05) > 250, "센서가 화면을 채움");
        // 둘레 360°
        for (int i = 0; i < 720; i++)
        {
            c.OrbitDrag(2 * H / 720.0, i < 360 ? 0.3 : -0.3);
            Assert.True(OnScreen(c, sensor));
            Near(W / 2, c.Project(sensor).X, 1e-6, "센서가 회전 중심");
        }
        // 팬 30 DIP: 센서가 정확히 30 DIP 이동(근접에서도 튀지 않음)
        c.PanPixels(30, -20);
        var q = c.Project(sensor);
        Near(W / 2 + 30, q.X, 1e-6, "근접 팬 x");
        Near(H / 2 - 20, q.Y, 1e-6, "근접 팬 y");
        c.PanPixels(-30, 20);
        c.LookDrag(40, 25);
        Assert.True(OnScreen(c, sensor));
        c.LookDrag(-40, -25);
        // 되돌아 나오기
        for (int i = 0; i < notches; i++) { t += 0.08; c.Wheel(-1, W / 2, H / 2, c.Distance, t); }
        AssertSameView(c, s, "근접 점검 후 복귀");
    }

    [Fact]
    public void Scenario_wide_area_survey_flight_and_retrace()
    {
        // 원점에서 북 200 km·서 150 km, 고도 3 km 항공기를 50 km 거리에서 내려다보다가(MC 산포 보기)
        // 확대해 들어가 공간 안을 비행 → 모든 조작을 역순으로 되돌려 원위치. 눈은 줄곧 지면(고도 0) 위.
        var c = Cam(new Vec3(200e3, -150e3, -3000), 50e3, 0.8, 1.2);
        var s = Snap(c);
        double t = 1000;
        void AboveGround(string step)
        {
            Invariants(c);
            Assert.True(c.Eye.Z < 0, $"{step}: 눈 고도 {-c.Eye.Z:F0} m");
        }
        // 1) 휠 20칸 확대(50 km → 3 km)
        for (int i = 0; i < 20; i++) { t += 0.05; c.Wheel(1, W / 2, H / 2, double.NaN, t); }
        Near(50e3 * Math.Pow(ViewCamera.WheelZoom, -20), c.Distance, 1e-6, "확대 후 거리");
        AboveGround("확대");
        // 2) Ctrl+휠 6칸(0.06 s 간격 — 가속) 커서 방향 접근
        double alt0 = -c.Eye.Z;
        for (int i = 0; i < 6; i++) { t += 0.06; c.Dolly(1, 700, 300, t); }
        Assert.True(-c.Eye.Z < alt0 - 2000, "접근하면 고도가 낮아짐");
        AboveGround("접근");
        // 3) 둘러보기
        c.LookDrag(120, -40);
        // 4) W 0.6 s 전진
        t += 0.5;
        Hold(c, FlyDir.Forward, ref t, 0.6, Frame60);
        AboveGround("전진");
        // 5) E 1 s 상승
        double z0 = c.Eye.Z;
        Hold(c, FlyDir.Up, ref t, 1, Frame60);
        Near(-FlyDistance(c.Distance, 1), c.Eye.Z - z0, 1e-6, "상승량");
        // 6) D 0.8 s 오른쪽
        Hold(c, FlyDir.Right, ref t, 0.8, Frame60);
        // 7) 궤도 회전 · 8) 팬 · 9) 휠 확대
        c.OrbitDrag(-200, 30);
        c.PanPixels(80, -45);
        c.Wheel(4, 500, 200, double.NaN, t);
        AboveGround("회전·팬·확대");
        // 역순으로 되돌리기
        c.Wheel(-4, 500, 200, double.NaN, t + 0.01);
        c.PanPixels(-80, 45);
        c.OrbitDrag(200, -30);
        t += 1;
        Hold(c, FlyDir.Left, ref t, 0.8, Frame60);
        Hold(c, FlyDir.Down, ref t, 1, Frame60);
        Hold(c, FlyDir.Back, ref t, 0.6, Frame60);
        c.LookDrag(-120, 40);
        t += 1;
        for (int i = 0; i < 6; i++) { t += 0.06; c.Dolly(-1, 700, 300, t); }
        AboveGround("되돌리기");
        for (int i = 0; i < 20; i++) { t += 0.05; c.Wheel(-1, W / 2, H / 2, double.NaN, t); }
        AssertSameView(c, s, "광역 비행 후 복귀");
    }

    [Fact]
    public void Scenario_flying_while_following_a_falling_drone_moves_relative_to_it()
    {
        var c = Cam(new Vec3(0, 0, -3000), 25);
        var offset0 = c.Eye - c.Target;
        var fwd = c.Forward;
        double t = 0;
        c.Fly(FlyDir.Forward, true, t);
        for (int i = 1; i <= 120; i++)
        {
            t = i * Frame60;
            c.Target = new Vec3(3 * t, 0, -3000 + 60 * t); // 추적 대상이 60 m/s 로 낙하
            c.Advance(t);
        }
        c.Fly(FlyDir.Forward, false, t);
        double s = FlyDistance(25, 2);
        NearV(offset0 + fwd * s, c.Eye - c.Target, 1e-9 * s, "추적 대상 기준 상대 이동");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Random_operation_fuzz_stays_finite_bounded_and_consistent(int seed)
    {
        var r = new Random(seed);
        var c = Cam(Vec3.Zero, 25);
        double t = 0;
        for (int i = 0; i < 20000; i++)
        {
            double px = r.NextDouble() * W, py = r.NextDouble() * H;
            double depth = r.Next(3) == 0 ? double.NaN : c.Distance * Math.Exp(r.NextDouble() * 6 - 3);
            t += r.Next(10) == 0 ? r.NextDouble() * 3 : r.NextDouble() * 0.05;
            double dist = c.Distance;
            var before = c.Eye;
            int op = r.Next(12);
            switch (op)
            {
                case 0: c.OrbitDrag(r.NextDouble() * 200 - 100, r.NextDouble() * 200 - 100); break;
                case 1: c.LookDrag(r.NextDouble() * 200 - 100, r.NextDouble() * 200 - 100); break;
                case 2: c.PanPixels(r.NextDouble() * 200 - 100, r.NextDouble() * 200 - 100, depth); break;
                case 3: c.Wheel(r.Next(-4, 5) + r.NextDouble() * 0.3, px, py, depth, t); break;
                case 4: c.Dolly(r.Next(-3, 4), px, py, t); break;
                case 5: c.Fly((FlyDir)r.Next(6), r.Next(2) == 0, t); break;
                case 6: c.Advance(t); break;
                case 7: c.SetDistance(ViewCamera.MinDistance * Math.Exp(r.NextDouble() * Math.Log(ViewCamera.MaxDistance / ViewCamera.MinDistance))); break;
                case 8: c.LookAt(new Vec3((r.NextDouble() * 2 - 1) * 1e6, (r.NextDouble() * 2 - 1) * 1e6, -r.NextDouble() * 1e4), c.Distance); break;
                case 9: c.Target = c.Target + new Vec3(r.NextDouble() - 0.5, r.NextDouble() - 0.5, r.NextDouble() - 0.5) * 100; break;
                case 10: c.SetViewport(r.NextDouble() * 2000, r.NextDouble() * 1500); break;
                default: c.StopFlying(); break;
            }
            Invariants(c);
            var center = c.Project(c.Focus);
            Near(c.ViewWidth / 2, center.X, 1e-6 * c.ViewWidth, "초점 = 화면 가운데 x");
            Near(c.ViewHeight / 2, center.Y, 1e-6 * c.ViewHeight, "초점 = 화면 가운데 y");
            if (op is 5 or 6)
            {
                // 한 번의 시간 진행으로 움직이는 양은 (방향 6개 모두 최고 속도) × 0.1 s 를 넘지 않는다(튐 없음)
                double bound = 6 * dist * ViewCamera.FlyRate * ViewCamera.RampMax * ViewCamera.MaxStep;
                Assert.True((c.Eye - before).Norm <= bound * (1 + 1e-9) + Tol(c), $"#{i}: 비행 튐 {(c.Eye - before).Norm:G4} > {bound:G4}");
            }
        }
    }

    [Theory]
    [InlineData(11, 0.0, 0.0, -1.2, ViewCamera.MinDistance)]
    [InlineData(12, 120.0, -40.0, -300.0, 25.0)]
    [InlineData(13, 300e3, -250e3, -5000.0, 400.0)]
    [InlineData(14, -500e3, 480e3, -10.0, 50e3)]
    [InlineData(15, 1e3, 1e3, -8000.0, 150e3)]
    public void Random_reversible_operations_undone_in_reverse_order_return_to_start(int seed, double n, double e, double d, double dist)
    {
        var r = new Random(seed);
        var c = Cam(new Vec3(n, e, d), dist, 0.4, 0.3);
        var s = Snap(c);
        var undo = new List<Action>();
        double t = 0;
        double orbitK = Math.PI / Math.Max(H, 300), lookK = 2 * Math.Tan(ViewCamera.Fov / 2) / H;
        bool PitchOk(double p) => p > ViewCamera.MinPitch + 1e-3 && p < ViewCamera.MaxPitch - 1e-3;
        for (int i = 0; i < 300; i++)
        {
            double px = r.NextDouble() * W, py = r.NextDouble() * H;
            switch (r.Next(6))
            {
                case 0:
                {
                    double dx = r.NextDouble() * 300 - 150, dy = r.NextDouble() * 300 - 150;
                    double depth = r.Next(2) == 0 ? double.NaN : c.Distance * (0.2 + 5 * r.NextDouble());
                    c.PanPixels(dx, dy, depth);
                    undo.Add(() => c.PanPixels(-dx, -dy, depth));
                    break;
                }
                case 1:
                {
                    double dx = r.NextDouble() * 120 - 60, dy = r.NextDouble() * 120 - 60;
                    if (!PitchOk(c.Pitch + dy * orbitK)) break;
                    c.OrbitDrag(dx, dy);
                    undo.Add(() => c.OrbitDrag(-dx, -dy));
                    break;
                }
                case 2:
                {
                    double dx = r.NextDouble() * 120 - 60, dy = r.NextDouble() * 120 - 60;
                    if (!PitchOk(c.Pitch - dy * lookK)) break;
                    c.LookDrag(dx, dy);
                    undo.Add(() => c.LookDrag(-dx, -dy));
                    break;
                }
                case 3:
                {
                    double notches = r.Next(1, 4) * (r.Next(2) == 0 ? 1 : -1);
                    double want = c.Distance * Math.Pow(ViewCamera.WheelZoom, -notches);
                    if (want < ViewCamera.MinDistance * 1.001 || want > ViewCamera.MaxDistance / 1.001) break;
                    double depth = r.Next(2) == 0 ? double.NaN : c.Distance * (0.3 + 3 * r.NextDouble());
                    double d0 = c.Distance;
                    c.Wheel(notches, px, py, depth);
                    double depth2 = depth * (c.Distance / d0); // 고정점의 새 깊이(NaN 이면 그대로 초점 평면)
                    undo.Add(() => c.Wheel(-notches, px, py, depth2));
                    break;
                }
                case 4:
                {
                    double notches = r.Next(1, 4) * (r.Next(2) == 0 ? 1 : -1);
                    c.Dolly(notches, px, py);
                    undo.Add(() => c.Dolly(-notches, px, py));
                    break;
                }
                default:
                {
                    var dir = (FlyDir)r.Next(6);
                    double dur = 0.05 + r.NextDouble() * 2;
                    Hold(c, dir, ref t, dur, Frame60);
                    var back = Opposite(dir);
                    undo.Add(() => Hold(c, back, ref t, dur, Frame60));
                    break;
                }
            }
            Invariants(c);
        }
        Assert.True(undo.Count > 150, $"되돌릴 조작 {undo.Count}개");
        for (int i = undo.Count - 1; i >= 0; i--) undo[i]();
        AssertSameView(c, s, $"시드 {seed}");
    }

    private static bool OnScreen(ViewCamera c, Vec3 p)
    {
        var q = c.Project(p);
        return q.Depth > 0 && q.X >= 0 && q.X <= c.ViewWidth && q.Y >= 0 && q.Y <= c.ViewHeight;
    }

    private static double ProjectedRadius(ViewCamera c, Vec3 p, double radius) =>
        radius / (c.Project(p).Depth * Math.Tan(ViewCamera.Fov / 2)) * c.ViewHeight / 2;
}
