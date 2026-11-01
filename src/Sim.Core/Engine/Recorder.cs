using Sim.Core.Models;
using Sim.Core.Numerics;

namespace Sim.Core.Engine;

/// <summary>
/// 물체 스냅샷. AirVelocity = 루트 CG 대기상대속도(NED) — 표면 압력·응력 재계산(FR-STR-01)용.
/// Root = 기록 시점 루트 물체(수납 사슬 끝) 인덱스 — 재생 보간에서 수납 물체를 루트에 강체로 붙여 그리기 위해. −1 = 모름(옛 프레임).
/// </summary>
public readonly record struct BodySample(
    Vec3 Origin, Quat Q, Vec3 Cg, Vec3 Velocity, Vec3 Omega, double Airspeed, double LoadG, BodyMode Mode, Vec3 AirVelocity = default, int Root = -1);

public sealed class Frame
{
    public double T { get; init; }
    public required BodySample[] Bodies { get; init; }
    public required DeviceSample[] Devices { get; init; }
    public required ConnectorSample[] Connectors { get; init; }
}

/// <summary>출력 주기마다 전 물체·장치·연결 스냅샷을 기록(재생·CSV·Blender 용).</summary>
public sealed class Recorder
{
    public List<Frame> Frames { get; } = new();
    public bool Enabled { get; set; } = true;

    /// <summary>저장된 결과에서 프레임 복원(ResultArchive).</summary>
    public void Restore(IEnumerable<Frame> frames)
    {
        Frames.Clear();
        Frames.AddRange(frames);
    }

    public void Capture(World w)
    {
        if (!Enabled) return;
        var bodies = new BodySample[w.Bodies.Count];
        for (int i = 0; i < bodies.Length; i++)
        {
            var b = w.Bodies[i];
            var root = b.Root;
            double airspeed = root.Mode == BodyMode.Free ? root.AirVelocity.Norm : 0;
            double load = root.Mode == BodyMode.Free ? (root.Accel - w.Env.GravityVector).Norm / w.Env.Gravity : 0;
            bodies[i] = new BodySample(b.PointWorld(Vec3.Zero), b.WorldQ, b.OwnCgWorld, b.PointVelocity(b.OwnCg),
                b.WorldOmegaBody, airspeed, load, b.Mode, root.Mode == BodyMode.Free ? root.AirVelocity : Vec3.Zero, root.Index);
        }
        var devices = new List<DeviceSample>();
        foreach (var b in w.Bodies)
            foreach (var d in b.Devices) devices.Add(d.Sample(w));
        var conns = w.Connectors.Select(c => c.Sample(w)).ToArray();
        Frames.Add(new Frame { T = w.T, Bodies = bodies, Devices = devices.ToArray(), Connectors = conns });
    }

    /// <summary>
    /// 시간 t 의 보간 프레임(재생 표시용) — 기록 주기(기본 50 Hz)와 화면 갱신(약 60 Hz)이 달라 생기는 끊김 제거.
    /// 앞뒤 기록 프레임 사이를 위치·속도는 선형, 자세는 정규화 선형(nlerp)으로 잇는다. 앞 프레임에서 수납(Stowed)인 물체는
    /// 루트 물체의 보간 자세에 강체로 붙여(루트 기준 상대 자세를 보간) 어느 시각에도 부모에서 벗어나 그려지지 않는다.
    /// 모드가 바뀌는 루트 물체(착지 등)·구성이 다른 프레임은 앞 프레임 값을 그대로 쓴다. 기록 프레임과 같은 시각이면 그 프레임 자체를 돌려준다.
    /// </summary>
    public Frame? Interpolate(double t)
    {
        int i = IndexAt(t);
        if (i < 0) return null;
        var a = Frames[i];
        if (i + 1 >= Frames.Count) return a;
        var b = Frames[i + 1];
        double span = b.T - a.T;
        if (span <= 1e-12 || t <= a.T) return a;
        double u = Math.Clamp((t - a.T) / span, 0, 1);
        if (a.Bodies.Length != b.Bodies.Length) return a;
        static Vec3 L(Vec3 p, Vec3 q, double u) => p + (q - p) * u;
        static double S(double p, double q, double u) => p + (q - p) * u;
        static Quat N(Quat p, Quat q, double u)
        {
            if (p.W * q.W + p.X * q.X + p.Y * q.Y + p.Z * q.Z < 0) q = q * -1;   // 짧은 경로
            return (p * (1 - u) + q * u).Normalized();
        }
        var bodies = new BodySample[a.Bodies.Length];
        // 1) 루트(자유·착지·운반체) 와 루트 정보 없는 물체: 같은 모드면 보간, 모드가 바뀌면 앞 프레임 값
        for (int k = 0; k < bodies.Length; k++)
        {
            var p = a.Bodies[k]; var q = b.Bodies[k];
            if (Attached(a, k)) continue;
            bodies[k] = p.Mode != q.Mode ? p
                : p with { Origin = L(p.Origin, q.Origin, u), Q = N(p.Q, q.Q, u), Cg = L(p.Cg, q.Cg, u),
                    Velocity = L(p.Velocity, q.Velocity, u), Omega = L(p.Omega, q.Omega, u), Airspeed = S(p.Airspeed, q.Airspeed, u),
                    LoadG = S(p.LoadG, q.LoadG, u), AirVelocity = L(p.AirVelocity, q.AirVelocity, u) };
        }
        // 2) 앞 프레임에서 수납(붙어 있던) 물체: 그 루트의 보간 자세에 강체로 붙인다. 두 프레임 사이에 분리되는 물체도
        //    분리 순간(뒤 프레임)까지는 붙어 있었으므로 같다 — 모드가 바뀐다고 앞 프레임 위치(최대 출력 주기만큼 뒤)에 남겨 두면
        //    분리 직전 물체가 부모에서 떨어져 그려진다(시나리오 11 의 '날개 두 번 분리'). 뒤 프레임에서도 같은 루트에 붙어 있으면
        //    루트 기준 상대 자세(레일 활주·도어 스윙)를 보간한다.
        for (int k = 0; k < bodies.Length; k++)
        {
            if (!Attached(a, k)) continue;
            var p = a.Bodies[k]; var q = b.Bodies[k];
            var ra = a.Bodies[p.Root]; var rc = bodies[p.Root];
            var relO = ra.Q.InverseRotate(p.Origin - ra.Origin);
            var relCg = ra.Q.InverseRotate(p.Cg - ra.Origin);
            var relQ = ra.Q.Conjugate() * p.Q;
            bool same = q.Mode == BodyMode.Stowed && q.Root == p.Root;
            if (same)
            {
                var rb = b.Bodies[p.Root];
                relO = L(relO, rb.Q.InverseRotate(q.Origin - rb.Origin), u);
                relCg = L(relCg, rb.Q.InverseRotate(q.Cg - rb.Origin), u);
                relQ = N(relQ, rb.Q.Conjugate() * q.Q, u);
            }
            bodies[k] = p with
            {
                Origin = rc.Origin + rc.Q.Rotate(relO), Q = (rc.Q * relQ).Normalized(), Cg = rc.Origin + rc.Q.Rotate(relCg),
                Velocity = same ? L(p.Velocity, q.Velocity, u) : p.Velocity, Omega = same ? L(p.Omega, q.Omega, u) : p.Omega,
                Airspeed = same ? S(p.Airspeed, q.Airspeed, u) : p.Airspeed, LoadG = same ? S(p.LoadG, q.LoadG, u) : p.LoadG,
                AirVelocity = same ? L(p.AirVelocity, q.AirVelocity, u) : p.AirVelocity,
            };
        }
        var devices = a.Devices;
        if (a.Devices.Length == b.Devices.Length)
        {
            devices = new DeviceSample[a.Devices.Length];
            for (int k = 0; k < devices.Length; k++)
            {
                var p = a.Devices[k]; var q = b.Devices[k];
                devices[k] = p.Name != q.Name || p.Visible != q.Visible ? p
                    : p with { Fill = S(p.Fill, q.Fill, u), Anchor = L(p.Anchor, q.Anchor, u), Tip = L(p.Tip, q.Tip, u), Size = S(p.Size, q.Size, u), Force = S(p.Force, q.Force, u) };
            }
        }
        var conns = a.Connectors;
        if (a.Connectors.Length == b.Connectors.Length)
        {
            conns = new ConnectorSample[a.Connectors.Length];
            for (int k = 0; k < conns.Length; k++)
            {
                var p = a.Connectors[k]; var q = b.Connectors[k];
                conns[k] = p.Name != q.Name || p.Visible != q.Visible ? p
                    : p with { A = L(p.A, q.A, u), B = L(p.B, q.B, u), Tension = S(p.Tension, q.Tension, u), ForceB = L(p.ForceB, q.ForceB, u) };
            }
        }
        return new Frame { T = t, Bodies = bodies, Devices = devices, Connectors = conns };
    }

    /// <summary>프레임 f 에서 물체 k 가 루트(자유·착지·운반체)에 붙어 있는 수납 물체이고 루트 인덱스를 아는지.</summary>
    private static bool Attached(Frame f, int k)
    {
        var s = f.Bodies[k];
        return s.Mode == BodyMode.Stowed && s.Root >= 0 && s.Root != k && s.Root < f.Bodies.Length && f.Bodies[s.Root].Mode != BodyMode.Stowed;
    }

    /// <summary>
    /// 루트 인덱스 없이 저장된 프레임(결과 파일)에 기록 시점 루트를 채운다: 수납 물체는 시나리오 부모 사슬(분리 뒤면 이전 부모)을
    /// 그 프레임에서 수납인 동안 거슬러 올라간 끝.
    /// </summary>
    public static BodySample[] WithRoots(BodySample[] bodies, IReadOnlyList<Body> world)
    {
        for (int i = 0; i < bodies.Length && i < world.Count; i++)
        {
            var b = world[i];
            for (int guard = 0; guard < world.Count && bodies[b.Index].Mode == BodyMode.Stowed && (b.Parent ?? b.FormerParent) is { } up; guard++) b = up;
            bodies[i] = bodies[i] with { Root = b.Index };
        }
        return bodies;
    }

    /// <summary>시간 t 에서 가장 가까운(이전) 프레임 인덱스.</summary>
    public int IndexAt(double t)
    {
        if (Frames.Count == 0) return -1;
        int lo = 0, hi = Frames.Count - 1;
        if (t <= Frames[0].T) return 0;
        if (t >= Frames[hi].T) return hi;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) >> 1;
            if (Frames[mid].T <= t) lo = mid; else hi = mid;
        }
        return lo;
    }
}
