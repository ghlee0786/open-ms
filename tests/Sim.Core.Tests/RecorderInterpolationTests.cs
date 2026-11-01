using Sim.Core.Engine;
using Sim.Core.Models;
using Sim.Core.Numerics;

namespace Sim.Core.Tests;

/// <summary>재생 표시용 기록 프레임 보간(Recorder.Interpolate) — 50 Hz 기록과 화면 갱신 차이로 생기는 끊김 제거.</summary>
public class RecorderInterpolationTests
{
    private static Frame F(double t, Vec3 p, Quat q, BodyMode mode = BodyMode.Free, double tension = 0) => new()
    {
        T = t,
        Bodies = [new BodySample(p, q, p, Vec3.Zero, Vec3.Zero, 0, 1, mode)],
        Devices = [],
        Connectors = [new ConnectorSample("c", "tether", true, p, p + new Vec3(0, 0, 1), tension)],
    };

    private static Quat AboutZ(double ang) => new(Math.Cos(ang / 2), 0, 0, Math.Sin(ang / 2));

    [Fact]
    public void Midpoint_is_linear_in_position_and_halfway_in_attitude()
    {
        var r = new Recorder();
        r.Restore([F(0.00, new Vec3(0, 0, 0), AboutZ(0), tension: 10), F(0.02, new Vec3(2, 0, -4), AboutZ(0.2), tension: 30)]);
        var m = r.Interpolate(0.01)!;
        var b = m.Bodies[0];
        Assert.Equal(1.0, b.Origin.X, 9);
        Assert.Equal(-2.0, b.Origin.Z, 9);
        double ang = 2 * Math.Atan2(b.Q.Z, b.Q.W);
        Assert.Equal(0.1, ang, 4);
        Assert.Equal(20.0, m.Connectors[0].Tension, 9);
        Assert.Equal(0.01, m.T, 12);
    }

    [Fact]
    public void Exact_sample_times_and_ends_return_recorded_frames()
    {
        var r = new Recorder();
        r.Restore([F(0.00, Vec3.Zero, AboutZ(0)), F(0.02, new Vec3(1, 0, 0), AboutZ(0))]);
        Assert.Same(r.Frames[0], r.Interpolate(0.0));
        Assert.Same(r.Frames[0], r.Interpolate(-1.0));
        Assert.Same(r.Frames[1], r.Interpolate(5.0));
        Assert.Null(new Recorder().Interpolate(0.0));
    }

    [Fact]
    public void Mode_change_between_frames_is_not_blended()
    {
        var r = new Recorder();
        r.Restore([F(0.00, Vec3.Zero, AboutZ(0), BodyMode.Stowed), F(0.02, new Vec3(3, 0, 0), AboutZ(0), BodyMode.Free)]);
        var b = r.Interpolate(0.01)!.Bodies[0];
        Assert.Equal(BodyMode.Stowed, b.Mode);
        Assert.Equal(0.0, b.Origin.X, 12);
    }

    [Fact]
    public void Stowed_body_separating_at_next_frame_stays_on_its_root_between_frames()
    {
        // 운반체(루트 0)가 +X 168 m/s, 물체 1 은 앞 프레임에서 루트 기준 (0.8, 0, 0)에 수납, 뒤 프레임(분리 순간)에 자유
        var q = AboutZ(0.3);
        Frame Fr(double t, BodyMode m1) => new()
        {
            T = t,
            Bodies =
            [
                new BodySample(new Vec3(168 * t, 0, -1000), q, new Vec3(168 * t, 0, -1000), new Vec3(168, 0, 0), Vec3.Zero, 168, 0, BodyMode.Kinematic, Root: 0),
                new BodySample(new Vec3(168 * t, 0, -1000) + q.Rotate(new Vec3(0.8, 0, 0)), q, Vec3.Zero, new Vec3(168, 0, 0), Vec3.Zero, 0, 0, m1, Root: m1 == BodyMode.Stowed ? 0 : 1),
            ],
            Devices = [],
            Connectors = [],
        };
        var r = new Recorder();
        r.Restore([Fr(3.48, BodyMode.Stowed), Fr(3.50, BodyMode.Free)]);
        var m = r.Interpolate(3.49)!;
        var expect = m.Bodies[0].Origin + m.Bodies[0].Q.Rotate(new Vec3(0.8, 0, 0));
        Assert.Equal(168 * 3.49, m.Bodies[0].Origin.X, 9);
        Assert.True((m.Bodies[1].Origin - expect).Norm < 1e-9, $"수납 물체가 루트에서 {(m.Bodies[1].Origin - expect).Norm:F3} m 벗어남");
        Assert.Equal(BodyMode.Stowed, m.Bodies[1].Mode);
        // 루트 정보 없는(옛) 프레임은 기존 규칙(앞 프레임 값)
        r.Restore([F(0.00, Vec3.Zero, AboutZ(0), BodyMode.Stowed), F(0.02, new Vec3(3, 0, 0), AboutZ(0), BodyMode.Free)]);
        Assert.Equal(0.0, r.Interpolate(0.01)!.Bodies[0].Origin.X, 12);
    }

    [DesignFact]
    public void Scenario11_stowed_bodies_stay_on_parent_and_playback_does_not_jump_around_separations()
    {
        // 시나리오 11: 3.5 s 날개 키트 분리, 4.0~4.002 s 3분할(노즈·꽃잎·꼬리·컨테이너). 기록 프레임과 보간 화면 모두
        // 수납 물체 자세 = 부모 자세 ⊕ 장착(mount), 화면 위치가 프레임 사이에서 점프하지 않는다(보고: 0.005배속 재생에서 '두 번 분리').
        var doc = Sim.Core.Scenario.ScenarioDoc.Load(TestUtil.DesignFile("config/scenarios", "11_*.json"));
        doc.Root["sim"]!["tEnd"] = 4.1;
        var w = doc.Build();
        w.Run();
        var rec = w.Recorder;
        void CheckOnParent(Frame f, string where)
        {
            foreach (var b in w.Bodies)
            {
                var s = f.Bodies[b.Index];
                if (s.Mode != BodyMode.Stowed || (b.Parent ?? b.FormerParent) is not { } par) continue;
                var ps = f.Bodies[par.Index];
                var o = ps.Origin + ps.Q.Rotate(b.MountPos);
                var qq = ps.Q * b.MountQ;
                double dq = 1 - Math.Abs(qq.W * s.Q.W + qq.X * s.Q.X + qq.Y * s.Q.Y + qq.Z * s.Q.Z);
                Assert.True((s.Origin - o).Norm < 1e-6 && dq < 1e-9, $"{where} t={f.T:F4}: {b.Name} 가 부모 {par.Name}+mount 에서 {(s.Origin - o).Norm:F3} m 벗어남");
            }
        }
        foreach (var f in rec.Frames.Where(f => f.T is > 3.4 and < 4.1)) CheckOnParent(f, "기록");
        var last = new Dictionary<int, Vec3>();
        for (double t = 3.40; t < 4.08; t += 0.00025)
        {
            var f = rec.Interpolate(t)!;
            CheckOnParent(f, "보간");
            foreach (var b in w.Bodies)
            {
                var o = f.Bodies[b.Index].Origin;
                if (last.TryGetValue(b.Index, out var p0))
                    Assert.True((o - p0).Norm < 200 * 0.00025 + 0.01, $"보간 t={t:F5}: {b.Name} 화면 위치가 {(o - p0).Norm:F3} m 점프");
                last[b.Index] = o;
            }
        }
        Assert.Equal(3.5, w.FiredEvents["wingkit.separated"], 3);
        Assert.Single(w.EventLog, e => e.Name == "wingkit.separated");
    }

    [Fact]
    public void Attitude_takes_the_short_path_when_quaternion_sign_flips()
    {
        var r = new Recorder();
        var q1 = AboutZ(0.2);
        r.Restore([F(0.00, Vec3.Zero, AboutZ(0)), F(0.02, Vec3.Zero, q1 * -1)]);   // 같은 자세, 부호만 반대
        var q = r.Interpolate(0.01)!.Bodies[0].Q;
        double ang = 2 * Math.Atan2(Math.Abs(q.Z), Math.Abs(q.W));
        Assert.Equal(0.1, ang, 4);
    }
}
