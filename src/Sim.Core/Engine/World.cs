using Sim.Core.Environment;
using Sim.Core.Models;
using Sim.Core.Numerics;

namespace Sim.Core.Engine;

public sealed record LogEntry(double Time, string Name, string Message);

/// <summary>
/// 다물체 6DOF 시뮬레이션 월드. 전 자유물체 상태를 하나로 묶어 고정 스텝 RK4 로 동시 적분(FR-NUM-04),
/// 상태 의존 이벤트·지면 접촉은 스텝 내 이분 탐색으로 EventTolerance 이내 위치 탐지(FR-NUM-02).
/// </summary>
public sealed class World
{
    public World(SimEnvironment env) => Env = env;

    public SimEnvironment Env { get; }
    public List<Body> Bodies { get; } = new();
    public Dictionary<string, Body> BodyMap { get; } = new();
    public List<Connector> Connectors { get; } = new();
    public List<SimEvent> Events { get; } = new();
    public Dictionary<string, double> FiredEvents { get; } = new();
    public List<LogEntry> EventLog { get; } = new();
    public List<string> Warnings { get; } = new();
    public Recorder Recorder { get; } = new();
    /// <summary>물체 간 충돌 판정·접촉(시나리오 collisions 절, 없으면 null = 미사용).</summary>
    public CollisionModel? Collisions { get; set; }

    public double T { get; private set; }
    public double Dt { get; set; } = 0.001;
    public double TEnd { get; set; } = 600;
    public double OutputInterval { get; set; } = 0.02;
    public double EventTolerance { get; set; } = 1e-4;
    /// <summary>이벤트 발생 후 이 시간 동안 매 스텝 기록(짧은 충격 펄스·개방 순간을 출력 주기와 무관하게 남김, sim.eventBurst) [s]. 0 = 끔.</summary>
    public double EventBurst { get; set; }
    private double _burstUntil = double.NegativeInfinity;
    /// <summary>
    /// 충격 적응 기록(sim.shockCapture.dG) [G]: 자유 물체 하중 G 가 마지막 기록 프레임보다 이만큼 바뀌면 ShockCaptureHold 동안 매 스텝 기록
    /// — 출력 주기(50 Hz)로는 놓치는 수 ms 스냅 하중·개방 충격 피크를 충격 응답 해석(ShockResponse)이 쓸 수 있게. 0 = 끔.
    /// </summary>
    public double ShockCaptureG { get; set; }
    public double ShockCaptureHold { get; set; } = 0.3;
    public ulong Seed { get; set; } = 1;
    public bool Stopped { get; private set; }
    public string StopReason { get; private set; } = "";
    public bool Suspect { get; private set; }
    public long StepCount { get; private set; }

    private double _nextOutput;
    private readonly List<Body> _free = new();
    private State[] _s0 = [], _k1 = [], _k2 = [], _k3 = [], _k4 = [];
    private Vec3[] _a1 = [], _a2 = [], _a3 = [], _a4 = [];

    private struct State { public Vec3 R, V, W; public Quat Q; }

    public void AddBody(Body b)
    {
        b.Index = Bodies.Count;
        Bodies.Add(b);
        BodyMap[b.Name] = b;
    }

    public Body GetBody(string name) =>
        BodyMap.TryGetValue(name, out var b) ? b : throw new KeyNotFoundException($"물체 '{name}' 이 없습니다.");

    public void Log(string name, string message)
    {
        EventLog.Add(new LogEntry(T, name, message));
        FiredEvents.TryAdd(name, T);
    }

    public void Stop(string reason)
    {
        if (Stopped) return;
        Stopped = true; StopReason = reason;
    }

    /// <summary>
    /// 저장된 결과 복원(ResultArchive): 적분 없이 기록 프레임·이벤트·경고·종료 상태를 넣는다.
    /// 물체 모드는 마지막 프레임 값(운반체 제외)으로 맞춘다.
    /// </summary>
    public void RestoreRecorded(IEnumerable<Frame> frames, IEnumerable<LogEntry> log, IEnumerable<string> warnings,
        double t, string stopReason, bool suspect, long steps)
    {
        Recorder.Restore(frames);
        EventLog.Clear(); EventLog.AddRange(log);
        FiredEvents.Clear();
        foreach (var e in EventLog) FiredEvents.TryAdd(e.Name, e.Time);
        Warnings.Clear(); Warnings.AddRange(warnings);
        T = t; Stopped = true; StopReason = stopReason; Suspect = suspect; StepCount = steps;
        if (Recorder.Frames.Count > 0)
        {
            var last = Recorder.Frames[^1];
            for (int i = 0; i < Bodies.Count && i < last.Bodies.Length; i++)
                if (Bodies[i].Mode != BodyMode.Kinematic) Bodies[i].Mode = last.Bodies[i].Mode;
        }
    }

    public Vec3 WindAt(Body root, Vec3 pos)
    {
        var w = Env.Wind.At(Env.Agl(pos.Z));
        if (root.Turbulence is { Enabled: true } t) w += t.Velocity;
        return w;
    }

    /// <summary>NED 점에 NED 힘 작용 — 붙어 있는 물체면 루트 자유물체로 전달.</summary>
    public void ApplyForce(Body b, Vec3 worldPoint, Vec3 f)
    {
        var root = b.Root;
        if (root.Mode != BodyMode.Free) return;
        root.F += f;
        root.M += root.Q.InverseRotate(Vec3.Cross(worldPoint - root.R, f));
    }

    public void ApplyMomentBody(Body b, Vec3 mBody)
    {
        var root = b.Root;
        if (root.Mode != BodyMode.Free) return;
        root.M += mBody;
    }

    /// <summary>초기화: 합성 질량특성 계산, 난류 생성, 첫 프레임 기록.</summary>
    /// <summary>초기화 시 비운반체 물체 고유 질량 합 [kg] — 분리·사출 전후 질량 보존 점검(FR-AN-07).</summary>
    public double InitialTotalMass { get; private set; }

    public void Initialize()
    {
        SetKinematic(T);
        foreach (var b in Bodies.Where(b => b.IsRoot)) b.RecomputeComposite(false);
        InitialTotalMass = Bodies.Where(b => b.Mode != BodyMode.Kinematic).Sum(b => b.OwnMass);
        foreach (var b in Bodies) EnsureTurbulence(b);
        _nextOutput = 0;
        RecordIfDue(force: true);
    }

    private void EnsureTurbulence(Body b)
    {
        if (b.Mode == BodyMode.Free && b.Turbulence is null && Env.Turbulence != "none")
            b.Turbulence = new DrydenTurbulence(Env.Turbulence, Rng.ForStream(Seed, 1000 + (ulong)b.Index));
    }

    // ---------------- 분리 ----------------

    public void Separate(Body child, Vec3 relVelParentBrf, Vec3 relOmega, string? eventName)
    {
        if (child.Mode != BodyMode.Stowed || child.Parent is null)
        {
            Warnings.Add($"t={T:F3}: {child.Name} 은 이미 분리되어 있습니다.");
            return;
        }
        var parent = child.Parent;
        var (root, off, rot) = child.ChainToRoot();
        var parentQ = parent.WorldQ;
        child.RecomputeComposite(false);
        var rb = off + rot.Rotate(child.Cg) - root.Cg;
        var pos = root.R + root.Q.Rotate(rb);
        var vel = root.V + root.Q.Rotate(Vec3.Cross(root.W, rb));
        var dv = parentQ.Rotate(relVelParentBrf);

        parent.Children.Remove(child);
        child.FormerParent = parent;
        child.Parent = null;
        child.Mode = BodyMode.Free;
        child.R = pos;
        child.V = vel + dv;
        child.Q = root.Q * rot;
        child.W = rot.InverseRotate(root.W) + relOmega;
        child.Accel = root.Accel; child.AccelStart = root.AccelStart; child.AccelEnd = root.AccelEnd;
        child.ReleaseTime = T;
        child.RecomputeComposite(false);
        EnsureTurbulence(child);

        if (root.Mode == BodyMode.Free)
        {
            root.RecomputeComposite(true);
            root.V -= dv * (child.Mass / root.Mass);
            var r = root.Q.InverseRotate(child.R - root.R);
            var dvb = root.Q.InverseRotate(dv);
            root.W += root.InvInertia * Vec3.Cross(r, dvb * -child.Mass);
        }
        else root.RecomputeComposite(false);

        Log(eventName ?? $"{child.Name}.separated", $"{child.Name} 분리 (|Δv|={dv.Norm:F2} m/s)");
    }

    // ---------------- 시간 진행 ----------------

    public void Run(CancellationToken ct = default, Action<double>? progress = null)
    {
        if (StepCount == 0 && Recorder.Frames.Count == 0) Initialize();
        long n = 0;
        while (!Stopped && !ct.IsCancellationRequested)
        {
            Step();
            if (progress != null && (++n % 2000) == 0) progress(T);
        }
    }

    public void Step()
    {
        if (Stopped) return;
        FireReadyEvents();
        if (Stopped) return;

        double h = Math.Min(Dt, TEnd - T);
        if (h <= 1e-12) { Stop("종료 시각 도달"); return; }
        double tn = NextTimedEvent();
        if (tn > T + 1e-9 && tn < T + h) h = tn - T;

        CollectFree();
        double t0 = T;
        Save();
        Integrate(t0, h);
        T = t0 + h;
        if (ConditionHit())
        {
            double lo = 0, hi = h;
            while (hi - lo > EventTolerance)
            {
                double mid = 0.5 * (lo + hi);
                Restore(); Integrate(t0, mid); T = t0 + mid;
                if (ConditionHit()) hi = mid; else lo = mid;
            }
            Restore(); Integrate(t0, hi); T = t0 + hi;
            h = hi;
        }
        StepCount++;

        HandleGround();
        PostStep(h);
        FireReadyEvents();
        HealthCheck();
        RecordIfDue(force: false);

        if (!Stopped)
        {
            bool anyFree = Bodies.Any(b => b.Mode == BodyMode.Free);
            bool anyLanded = Bodies.Any(b => b.Mode == BodyMode.Landed);
            bool pendingTimed = Events.Any(e => !e.Fired && double.IsFinite(e.Trigger.NextTime(this)));
            if (!anyFree && anyLanded && !pendingTimed) Stop("모든 물체 착지");
            else if (T >= TEnd - 1e-12) Stop("종료 시각 도달");
        }
        if (Stopped) RecordIfDue(force: true);
    }

    private void RecordIfDue(bool force)
    {
        if (!force && ShockCaptureG > 0 && Recorder.Frames.Count > 0 && T > _burstUntil + 1e-12)
        {
            var last = Recorder.Frames[^1].Bodies;
            for (int i = 0; i < Bodies.Count && i < last.Length; i++)
            {
                var b = Bodies[i];
                if (b.Mode != BodyMode.Free || last[i].Mode != BodyMode.Free) continue;
                double g = (b.Accel - Env.GravityVector).Norm / Env.Gravity;
                if (Math.Abs(g - last[i].LoadG) >= ShockCaptureG) { _burstUntil = T + ShockCaptureHold; break; }
            }
        }
        if (!force && T <= _burstUntil + 1e-12 && Recorder.Frames.Count > 0 && Recorder.Frames[^1].T < T - 1e-12)
        {
            Recorder.Capture(this);
            return;
        }
        if (force || T >= _nextOutput - 1e-9)
        {
            if (Recorder.Frames.Count == 0 || Recorder.Frames[^1].T < T - 1e-12) Recorder.Capture(this);
            _nextOutput = Math.Floor(T / OutputInterval + 1e-9) * OutputInterval + OutputInterval;
        }
    }

    private double NextTimedEvent()
    {
        double tn = double.PositiveInfinity;
        foreach (var e in Events)
            if (!e.Fired) tn = Math.Min(tn, e.Trigger.NextTime(this));
        return tn;
    }

    private void FireReadyEvents()
    {
        for (int guard = 0; guard < 32; guard++)
        {
            bool any = false;
            foreach (var e in Events)
            {
                if (e.Fired || !e.Trigger.Evaluate(this)) continue;
                e.Fired = true; e.FiredTime = T; any = true;
                if (EventBurst > 0) _burstUntil = Math.Max(_burstUntil, T + EventBurst);
                Log(e.Name, string.Join("; ", e.Actions.Select(a => a.Describe())));
                foreach (var a in e.Actions)
                {
                    try { a.Execute(this); }
                    catch (Exception ex) { Warnings.Add($"t={T:F3} 이벤트 '{e.Name}' 실패: {ex.Message}"); }
                    if (Stopped) return;
                }
            }
            if (!any) return;
        }
    }

    private bool ConditionHit()
    {
        foreach (var e in Events)
            if (!e.Fired && e.Trigger.StateDependent && e.Trigger.Evaluate(this)) return true;
        foreach (var b in _free)
            if (LowestPointD(b) >= Env.GroundD) return true;
        return false;
    }

    public double LowestPointD(Body root)
    {
        double maxD = double.MinValue;
        foreach (var p in root.ContactPointsRootBrf())
            maxD = Math.Max(maxD, root.R.Z + root.Q.Rotate(p - root.Cg).Z);
        return maxD;
    }

    private void HandleGround()
    {
        foreach (var b in _free)
        {
            if (b.Mode != BodyMode.Free || LowestPointD(b) < Env.GroundD - 1e-9) continue;
            var vel = b.V;
            var dirBody = b.Q.InverseRotate(vel).Normalized();
            var nose = b.Q.Rotate(Vec3.UnitX);
            double noseAngle = Math.Acos(Math.Clamp(nose.Z, -1, 1)) * 180 / Math.PI;
            // FX-02: 관입체 단면(스파이크) 사용. 기수가 선행하지 않으면(받음각 > 30°) 관입체가 아닌 몸체가 먼저 닿으므로 투영면적 사용.
            double attack = Math.Acos(Math.Clamp(dirBody.X, -1, 1)) * 180 / Math.PI;
            double area; double n; string src;
            if (b.Penetrator is { } pen && attack <= 30) { area = pen.Area; n = b.NoseCoefficient; src = pen.Source; }
            else { area = b.Shape?.Projected(dirBody).Area ?? 0.01; n = b.Penetrator is null ? b.NoseCoefficient : 0.56; src = b.Penetrator is null ? "projected" : "projected(nose-not-leading)"; }
            var (depth, g) = Penetration.Young(b.Mass, area, vel.Norm, Env.Soil.S, n);
            double pathFromVertical = Math.Acos(Math.Clamp(vel.Z / Math.Max(vel.Norm, 1e-12), -1, 1));
            var e = b.Q.ToEuler() * (180 / Math.PI);
            b.Impact = new ImpactRecord
            {
                Time = T, Position = b.R, Velocity = vel, EulerDeg = e, NoseAngleDeg = noseAngle,
                PenetrationDepth = depth, PenetrationG = g, Mass = b.Mass,
                AngleOfAttackDeg = attack, PenetrationVerticalDepth = depth * Math.Cos(pathFromVertical),
                PenetrationArea = area, PenetrationNoseN = n, PenetrationSource = src,
            };
            b.Mode = BodyMode.Landed;
            b.V = Vec3.Zero; b.W = Vec3.Zero; b.Accel = Vec3.Zero; b.AccelStart = Vec3.Zero; b.AccelEnd = Vec3.Zero;
            Log($"{b.Name}.landed", $"{b.Name} 지면 충돌 {vel.Norm:F2} m/s, 노즈각 {noseAngle:F1}°");
        }
    }

    private void PostStep(double h)
    {
        foreach (var b in Bodies)
            if (b.Mode == BodyMode.Free)
            {
                b.Turbulence?.Step(h, Env.Agl(b.R.Z), b.AirVelocity.Norm);
                double g = (b.Accel - Env.GravityVector).Norm / Env.Gravity;
                if (g > b.MaxLoadG) { b.MaxLoadG = g; b.MaxLoadTime = T; }
            }
        foreach (var b in Bodies.ToArray())
            foreach (var d in b.Devices) d.PostStep(this, T, h);
        foreach (var c in Connectors.ToArray()) c.PostStep(this, T, h);
        Collisions?.Check(this);
    }

    private void HealthCheck()
    {
        foreach (var b in Bodies)
        {
            if (b.Mode != BodyMode.Free) continue;
            if (!b.R.IsFinite || !b.V.IsFinite || !b.W.IsFinite || !double.IsFinite(b.Q.W))
            {
                Suspect = true;
                Warnings.Add($"t={T:F4}: {b.Name} 상태 NaN/Inf — 실행 중단(suspect)");
                Stop("수치 발산");
                return;
            }
            if (Math.Abs(b.Q.Norm - 1) > 1e-6)
            {
                Suspect = true;
                Warnings.Add($"t={T:F4}: {b.Name} 쿼터니언 노름 편차 {Math.Abs(b.Q.Norm - 1):E2}");
            }
        }
    }

    // ---------------- RK4 ----------------

    private void CollectFree()
    {
        _free.Clear();
        foreach (var b in Bodies) if (b.Mode == BodyMode.Free) _free.Add(b);
        int n = _free.Count;
        if (_s0.Length < n)
        {
            _s0 = new State[n]; _k1 = new State[n]; _k2 = new State[n]; _k3 = new State[n]; _k4 = new State[n];
            _a1 = new Vec3[n]; _a2 = new Vec3[n]; _a3 = new Vec3[n]; _a4 = new Vec3[n];
        }
    }

    private Vec3[] _accelSave = [];
    private Vec3[] _airSave = [];

    private void Save()
    {
        if (_accelSave.Length < _free.Count) { _accelSave = new Vec3[_free.Count]; _airSave = new Vec3[_free.Count]; }
        for (int i = 0; i < _free.Count; i++)
        {
            var b = _free[i];
            _s0[i] = new State { R = b.R, V = b.V, Q = b.Q, W = b.W };
            _accelSave[i] = b.Accel; _airSave[i] = b.AirVelocity;
        }
    }

    private void Restore()
    {
        for (int i = 0; i < _free.Count; i++)
        {
            var b = _free[i];
            b.R = _s0[i].R; b.V = _s0[i].V; b.Q = _s0[i].Q; b.W = _s0[i].W;
            b.Accel = _accelSave[i]; b.AirVelocity = _airSave[i];
        }
    }

    private void Integrate(double t0, double h)
    {
        int n = _free.Count;
        Derivs(t0, _k1, _a1);
        SetStage(_k1, h / 2); Derivs(t0 + h / 2, _k2, _a2);
        SetStage(_k2, h / 2); Derivs(t0 + h / 2, _k3, _a3);
        SetStage(_k3, h); Derivs(t0 + h, _k4, _a4);
        for (int i = 0; i < n; i++)
        {
            var b = _free[i];
            ref var s = ref _s0[i];
            b.R = s.R + (_k1[i].R + _k2[i].R * 2 + _k3[i].R * 2 + _k4[i].R) * (h / 6);
            b.V = s.V + (_k1[i].V + _k2[i].V * 2 + _k3[i].V * 2 + _k4[i].V) * (h / 6);
            b.Q = (s.Q + (_k1[i].Q + _k2[i].Q * 2 + _k3[i].Q * 2 + _k4[i].Q) * (h / 6)).Normalized();
            b.W = s.W + (_k1[i].W + _k2[i].W * 2 + _k3[i].W * 2 + _k4[i].W) * (h / 6);
            b.Accel = (_a1[i] + _a2[i] * 2 + _a3[i] * 2 + _a4[i]) / 6;
            b.AccelStart = _a1[i]; b.AccelEnd = _a4[i];
        }
        SetKinematic(t0 + h);
    }

    private void SetStage(State[] k, double h)
    {
        for (int i = 0; i < _free.Count; i++)
        {
            var b = _free[i];
            ref var s = ref _s0[i];
            b.R = s.R + k[i].R * h;
            b.V = s.V + k[i].V * h;
            b.Q = (s.Q + k[i].Q * h).Normalized();
            b.W = s.W + k[i].W * h;
        }
    }

    private void SetKinematic(double t)
    {
        foreach (var b in Bodies)
        {
            if (b.Mode != BodyMode.Kinematic || b.Trajectory is null) continue;
            b.R = b.Trajectory.At(t); b.V = b.Trajectory.Velocity; b.Q = b.Trajectory.Attitude; b.W = Vec3.Zero;
        }
    }

    private void Derivs(double t, State[] k, Vec3[] acc)
    {
        SetKinematic(t);
        bool earthDefault = Env.Earth.IsDefault;
        var omegaNed = Env.Earth.OmegaNed;
        foreach (var b in _free)
        {
            b.F = (earthDefault ? Env.GravityVector : Env.GravityAtD(b.R.Z)) * b.Mass;
            b.M = Vec3.Zero;
            var vAir = b.V - WindAt(b, b.R);
            b.AirVelocity = vAir;
            if (b.Aero is not null)
            {
                var atm = Env.Atmosphere.At(-b.R.Z);
                double mach = atm.SpeedOfSound > 0 ? vAir.Norm / atm.SpeedOfSound : 0;
                var (fb, mb) = b.Aero.Compute(b.Q.InverseRotate(vAir), b.W, atm.Density, b.Cg, mach, atm.Viscosity);
                b.F += b.Q.Rotate(fb);
                b.M += mb;
            }
        }
        foreach (var b in Bodies)
            foreach (var d in b.Devices) d.Apply(this, t);
        foreach (var c in Connectors) c.Apply(this, t);
        for (int i = 0; i < _free.Count; i++)
        {
            var b = _free[i];
            var a = b.F / b.Mass;
            var iw = b.Inertia * b.W;
            var wAtt = b.W;
            if (!earthDefault && omegaNed.NormSq > 0)
            {
                // 지구 고정(회전) NED: 코리올리, 자세는 지구 상대 각속도로 전파(W 는 관성 각속도)
                a -= Vec3.Cross(omegaNed, b.V) * 2;
                wAtt = b.W - b.Q.InverseRotate(omegaNed);
            }
            k[i] = new State
            {
                R = b.V,
                V = a,
                Q = b.Q.Derivative(wAtt),
                W = b.InvInertia * (b.M - Vec3.Cross(b.W, iw))
            };
            acc[i] = a;
        }
    }
}
