using System.Text.Json.Nodes;
using Sim.Core.Engine;
using Sim.Core.Numerics;

namespace Sim.Core.Models;

/// <summary>물체 간 결합 요소(Connector).</summary>
public abstract class Connector
{
    public required string Name { get; init; }
    public abstract string Type { get; }
    public bool Active { get; set; } = true;
    public virtual void Apply(World world, double t) { }
    public virtual void PostStep(World world, double t, double dt) { }
    public virtual void Command(World world, string command, JsonObject args)
    {
        switch (command)
        {
            case "release": case "deactivate": Active = false; break;
            case "activate": Active = true; break;
            default: throw new InvalidOperationException($"{Type} '{Name}' 은 '{command}' 명령을 지원하지 않습니다.");
        }
    }
    public virtual void Summarize(JsonObject s) { }
    public virtual ConnectorSample Sample(World world) => new(Name, Type, false, Vec3.Zero, Vec3.Zero, 0);
}

/// <param name="ForceB">B 쪽에 작용하는 힘(NED, N) — 방향이 A→B 선과 다른 연결(힌지: 펜널티 간극이 µm 라 점 차 방향은 잡음)만 채운다.
/// 0 이면 소비자는 Tension·(B−A) 방향을 쓴다(저장 결과 .mnsr 에는 없음 → 불러온 결과는 그 근사).</param>
public readonly record struct ConnectorSample(string Name, string Type, bool Visible, Vec3 A, Vec3 B, double Tension, Vec3 ForceB = default);

/// <summary>
/// 테더/라이저(FR-TET-01): 인장 전용 스프링-댐퍼. k = EA/L₀. 파단하중 초과 시 끊김.
/// 양 끝이 붙어 있는(Stowed) 물체여도 루트 물체에 힘이 전달된다.
/// </summary>
public class Tether : Connector
{
    public override string Type => "tether";
    public required Body A { get; init; }
    public required Body B { get; init; }
    public Vec3 PointA { get; init; }
    public Vec3 PointB { get; init; }
    public double Length { get; init; } = 1.5;
    public double EA { get; init; } = 2.0e4;
    public double Damping { get; init; } = 50;
    public double BreakLoad { get; init; } = double.PositiveInfinity;

    public double Tension { get; private set; }
    public double MaxTension { get; private set; }
    public double MaxTensionTime { get; private set; } = double.NaN;
    public bool Broken { get; private set; }

    public double Stiffness => EA / Length;

    /// <summary>
    /// 정하중형 에너지 흡수기(선택, 라인과 직렬 — 번지·립스티치 완충 팩): 흡수기 늘음 s 에 대해 F_a(s) = AbsorberForce·min(1, s/AbsorberRise)
    /// (0→Rise 선형 상승 후 일정), 행정 AbsorberStroke. 소성: 늘어난 만큼 되돌아가지 않고(하역은 라인 강성), 행정을 다 쓰면 라인만 남는다(바닥).
    /// 라인 장력 k(e − s) 가 F_a(s) 를 넘으면 흡수기가 늘어나 장력 = F_a(s)(속도 무관 — 감쇠 없음). AbsorberForce 0 = 없음.
    /// </summary>
    public double AbsorberForce { get; init; }
    public double AbsorberRise { get; init; } = 0.1;
    public double AbsorberStroke { get; init; }
    /// <summary>흡수기 누적 늘음 [m](소성, 스텝 끝 갱신) · 흡수 에너지 [J] · 행정 소진 여부.</summary>
    public double AbsorberUsed { get; private set; }
    public double AbsorberEnergy { get; private set; }
    public bool AbsorberBottomed { get; private set; }

    private double AbsorberCurve(double s) => AbsorberForce * Math.Min(1, s / Math.Max(AbsorberRise, 1e-9));

    private (double T, Vec3 Dir, Vec3 Pa, Vec3 Pb, double S) Eval()
    {
        var pa = A.PointWorld(PointA);
        var pb = B.PointWorld(PointB);
        var d = pb - pa;
        double len = d.Norm;
        double s0 = AbsorberUsed;
        if (len <= Length + s0 || len < 1e-12) return (0, Vec3.Zero, pa, pb, s0);
        var u = d / len;
        double ldot = Vec3.Dot(B.PointVelocity(PointB) - A.PointVelocity(PointA), u);
        double e = len - Length, k = Stiffness;
        double t = k * (e - s0) + Damping * ldot;
        if (AbsorberForce > 0 && s0 < AbsorberStroke && k * (e - s0) > AbsorberCurve(s0))
        {
            // 흡수기 늘음: k(e − s) = F_a(s) — 선형 구간 해, 아니면 일정 구간, 행정을 넘으면 바닥(라인만)
            double ka = AbsorberForce / Math.Max(AbsorberRise, 1e-9);
            double s1 = k * e / (k + ka);
            if (s1 <= AbsorberRise) return (ka * s1, u, pa, pb, Math.Max(s0, s1));
            double s2 = e - AbsorberForce / k;
            if (s2 <= AbsorberStroke) return (AbsorberForce, u, pa, pb, Math.Max(s0, s2));
            t = k * (e - AbsorberStroke) + Damping * ldot;
            return (Math.Max(t, 0), u, pa, pb, AbsorberStroke);
        }
        return (Math.Max(t, 0), u, pa, pb, s0);
    }

    public override void Apply(World w, double t)
    {
        if (!Active || Broken) return;
        if (A.Root == B.Root) return;
        var (T, u, pa, pb, _) = Eval();
        if (T <= 0) return;
        w.ApplyForce(A, pa, u * T);
        w.ApplyForce(B, pb, u * -T);
    }

    public override void PostStep(World w, double t, double dt)
    {
        if (!Active || Broken || A.Root == B.Root) { Tension = 0; return; }
        var ev = Eval();
        Tension = ev.T;
        if (ev.S > AbsorberUsed)
        {
            AbsorberEnergy += Tension * (ev.S - AbsorberUsed);
            AbsorberUsed = ev.S;
            if (!AbsorberBottomed && AbsorberUsed >= AbsorberStroke - 1e-9)
            {
                AbsorberBottomed = true;
                w.Log($"{Name}.absorberBottomed", $"{Name} 완충기 행정 {AbsorberStroke:F2} m 소진(흡수 {AbsorberEnergy:F0} J) — 이후 라인 강성만");
            }
        }
        if (Tension > MaxTension) { MaxTension = Tension; MaxTensionTime = t; }
        if (Tension > BreakLoad)
        {
            Broken = true;
            w.Log($"{Name}.broken", $"테더 파단 (장력 {Tension:F0} N > {BreakLoad:F0} N)");
        }
    }

    public override ConnectorSample Sample(World w) =>
        new(Name, Type, Active && !Broken, A.PointWorld(PointA), B.PointWorld(PointB), Tension);

    public override void Summarize(JsonObject s)
    {
        s["type"] = Type;
        s["maxTension_N"] = MaxTension;
        s["maxTensionTime_s"] = Parachute.Num(MaxTensionTime);
        s["broken"] = Broken;
        s["released"] = !Active;
        if (AbsorberForce > 0)
        {
            s["absorberForce_N"] = AbsorberForce;
            s["absorberStroke_m"] = AbsorberStroke;
            s["absorberUsed_m"] = AbsorberUsed;
            s["absorberEnergy_J"] = AbsorberEnergy;
            s["absorberBottomed"] = AbsorberBottomed;
        }
    }
}

/// <summary>
/// 브레이크 코드(약한 고리, FR-AN-08 ①): 정적 끈(static line) + 파단 끈. 모체(A, 예: 컨테이너)와 사출 물체(B, 예: 박스) 사이의
/// 인장 전용 스프링-댐퍼로, 길이 L(정적 끈 + 백·라인·캐노피 신장 길이)이 팽팽해지면 장력이 서고 파단하중 F_b 에서 끊긴다.
/// 강성은 파단 신장률 ε_b 에서 F_b 가 되도록 EA = F_b/ε_b (k = EA/L). 감쇠는 임계감쇠비 ζ(B 루트 질량 기준).
/// 끊기는 순간 이벤트 '{name}.broken' — 정적 끈 방식에서는 이때 캐노피가 백에서 풀려 충만이 시작되므로 낙하산 deploy 를 이 이벤트에 건다.
/// 끊기지 않으면(스냅 장력 &lt; F_b) 물체가 모체에 매달린 채 남는다 — 요약 snapped=false 로 드러낸다(미파단 위험).
/// </summary>
public sealed class BreakCord : Tether
{
    public override string Type => "breakCord";
    public double BreakElongation { get; init; } = 0.15;
    public double DampingRatio { get; init; } = 0.05;

    public double BreakTime { get; private set; } = double.NaN;
    public double TautTime { get; private set; } = double.NaN;
    /// <summary>팽팽해진 뒤 파단(또는 현재)까지 장력 충격량 ∫T dt [N·s].</summary>
    public double Impulse { get; private set; }
    /// <summary>팽팽해지는 순간 B 쪽 결속점의 끈 방향 상대속도 [m/s] (스냅 해석해 T ≈ v·√(k·m) 비교용).</summary>
    public double TautSpeed { get; private set; } = double.NaN;
    private double _slackSpeed = double.NaN;

    public override void PostStep(World w, double t, double dt)
    {
        bool wasBroken = Broken;
        base.PostStep(w, t, dt);
        if (wasBroken || !Active || A.Root == B.Root) return;
        var pa = A.PointWorld(PointA); var pb = B.PointWorld(PointB);
        double closing = Vec3.Dot(B.PointVelocity(PointB) - A.PointVelocity(PointA), (pb - pa).Normalized());
        if (Tension > 0 && double.IsNaN(TautTime))
        {
            TautTime = t;
            // 팽팽해지기 직전 스텝의 신장 속도(첫 스텝부터 팽팽하면 현재 값)
            TautSpeed = double.IsNaN(_slackSpeed) ? closing : _slackSpeed;
        }
        if (Tension <= 0) _slackSpeed = closing;
        Impulse += Tension * dt;
        if (Broken) BreakTime = t;
    }

    public override void Summarize(JsonObject s)
    {
        base.Summarize(s);
        s["breakLoad_N"] = double.IsFinite(BreakLoad) ? BreakLoad : null;
        s["breakElongation"] = BreakElongation;
        s["stiffness_Npm"] = Stiffness;
        s["snapped"] = Broken;
        s["tautTime_s"] = Parachute.Num(TautTime);
        s["tautSpeed_mps"] = Parachute.Num(TautSpeed);
        s["breakTime_s"] = Parachute.Num(BreakTime);
        s["impulse_Ns"] = Impulse;
    }
}

/// <summary>
/// 힌지(FR-SHL-02): 외피 조각 한쪽 끝을 모체에 잡아 두는 회전 1자유도 구속(펜널티 방식).
///   위치: 볼조인트 스프링-댐퍼 F_B = −k·d − c·ḋ (d = B 결속점 − A 결속점, NED)
///   자세: 힌지축 정렬 토크 τ_B = k_r·(u_B × u_A) − c_r·(ω_B − ω_A)⊥  (축 둘레 회전은 자유)
/// 열림각(축 둘레 상대 회전)이 releaseAngle 이상이거나 반력이 breakLoad 를 넘으면 이탈 → 이벤트 '{name}.released'.
/// B 가 A 와 같은 루트(수납)인 동안은 비활성이고, 분리 후 첫 평가 때 B 쪽 결속점·축·기준방향을 B 좌표로 고정한다(초기 어긋남 0).
/// 감쇠 미지정(음수)이면 임계감쇠비 0.7 로 자동.
/// 안정 한계(고정 스텝 RK4): 결속점 유효 질량 m* = 1/λmax(M_A + M_B), M = I₃/m + [r]×ᵀ I⁻¹ [r]× (결속점 점 이동도 — 질량만이 아니라
/// 관성 지렛대 팔 포함), 같은 루트를 공유하는 힌지 N 개(꼬리 1개에 꽃잎 3장)가 병렬이면 k·N 이 그 루트에 걸리므로
/// k ≤ m*·(0.5/Δt)²/N. 이전(질량만, N 무시)에는 감쇠 극점 c·Δt/m* 가 RK4 실축 한계(≈2.8)를 넘어 반력이 Δt 에 따라 수렴하지 않았다
/// (시나리오 09: Δt 1 ms → 7.8 kN, 0.25 ms → 17.5 kN).
/// 힌지로 묶인 쌍은 충돌 판정에서 빠진다(CollisionModel — 관절 접합면 자체가 힌지, 위치 보정이 펜널티 스프링을 잡아당기던 문제).
/// </summary>
public sealed class Hinge : Connector
{
    public override string Type => "hinge";
    public required Body A { get; init; }
    public required Body B { get; init; }
    public Vec3 PointA { get; init; }
    public Vec3 AxisA { get; init; } = new(0, 1, 0);
    public double Stiffness { get; init; } = 1e6;
    public double Damping { get; init; } = -1;
    public double RotStiffness { get; init; } = 5e4;
    public double RotDamping { get; init; } = -1;
    public double ReleaseAngleDeg { get; init; } = 45;
    public double BreakLoad { get; init; } = double.PositiveInfinity;

    public double AngleDeg { get; private set; }
    public double Force { get; private set; }
    public double MaxForce { get; private set; }
    public double MaxForceTime { get; private set; } = double.NaN;
    public double MaxAngleDeg { get; private set; }
    public double ReleaseTime { get; private set; } = double.NaN;
    public bool Released { get; private set; }
    public string ReleaseReason { get; private set; } = "";

    private bool _bound;
    private Vec3 _pb, _axb, _refA, _refB;
    private double _k, _c, _kr, _cr, _kPrev, _krPrev;
    /// <summary>안정성 제한 후 실제 사용한 병진·회전 강성 [N/m, N·m/rad].</summary>
    public double EffectiveStiffness => _k;
    public double EffectiveRotStiffness => _kr;

    private bool Engaged => Active && !Released && A.Root != B.Root;
    /// <summary>두 물체를 지금 잡고 있는가(충돌 판정 제외 대상).</summary>
    public bool IsEngaged => Engaged;

    private static double MinInertia(Body r) => Math.Max(Math.Min(r.Inertia.M11, Math.Min(r.Inertia.M22, r.Inertia.M33)), 1e-9);

    /// <summary>안정 한계 ω_n·Δt (고정 스텝 RK4 — 진폭 오차 (ωΔt)⁶/144 ≈ 1e-4/스텝, 감쇠 극점 2ζωΔt = 0.7).</summary>
    public const double OmegaDt = 0.5;
    /// <summary>
    /// 강성 상한 기준 Δt [s]: 상한은 max(Δt, StabilityDt) 로 계산한다. 펜널티 강성은 힌지 금구 컴플라이언스(모델 값)이지 수치 값이 아니므로
    /// Δt 를 줄여도 바뀌면 안 된다 — 이전(상한 = 현재 Δt)에는 Δt 1 ms → k 0.25 MN/m, 0.25 ms → 1 MN/m(명시값)로 힌지 고유진동수가
    /// 46 ↔ 92 Hz 로 달라져, 반력 평균 창(5 ms)보다 긴 펜널티 진동이 하중을 정했다(시나리오 09 실모델 hinge2 5 ms 평균 반력 비 0.83).
    /// 기본 1 ms = 힌지 시나리오 기본 Δt(06·09 계열) — 그 Δt 결과는 그대로이고, 더 작은 Δt 는 같은 강성으로 수렴 확인용.
    /// </summary>
    public double StabilityDt { get; init; } = 0.001;

    private (Body, Body, int) _gainRoots;
    /// <summary>안정 한계에 쓴 결속점 유효 질량 [kg]·공유 힌지 수.</summary>
    public double EffectiveMass { get; private set; } = double.NaN;
    public int SharedHinges { get; private set; } = 1;

    /// <summary>
    /// 자유 루트 물체의 결속점(NED) 점 이동도 최대 고유값 [1/kg]: 그 점에 충격량 J 를 줄 때 점 속도 변화 Δv = M·J,
    /// M = I₃/m + [r]×ᵀ I⁻¹ [r]× (r = CG → 점, 동체 좌표). 운반체·착지 물체는 0(무한 질량). 3×3 대칭 PSD → 거듭제곱법.
    /// </summary>
    public static double PointMobility(Body root, Vec3 pointWorld)
    {
        if (root.Mode != BodyMode.Free || !(root.Mass > 0)) return 0;
        var r = root.Q.InverseRotate(pointWorld - root.R);
        double im = 1 / root.Mass;
        var inv = root.InvInertia;
        Vec3 Mul(Vec3 x) => x * im + Vec3.Cross(inv * Vec3.Cross(r, x), r);
        double best = 0;
        foreach (var x0 in new[] { Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ, new Vec3(1, 1, 1).Normalized() })
        {
            var x = x0;
            for (int it = 0; it < 40; it++)
            {
                var y = Mul(x);
                double n = y.Norm;
                if (n < 1e-30) break;
                x = y / n;
            }
            best = Math.Max(best, Vec3.Dot(x, Mul(x)));
        }
        return best;
    }

    private void Bind(World w)
    {
        var ra = A.Root; var rb = B.Root;
        int na = 0, nb = 0;
        foreach (var c in w.Connectors)
            if (c is Hinge h && h.Engaged)
            {
                if (ra.Mode == BodyMode.Free && (h.A.Root == ra || h.B.Root == ra)) na++;
                if (rb.Mode == BodyMode.Free && (h.A.Root == rb || h.B.Root == rb)) nb++;
            }
        int share = Math.Max(1, Math.Max(na, nb));
        if (_gainRoots != (ra, rb, share))
        {
            // 결속점 유효 질량(병진 + 관성 지렛대 팔): 운반체(규정 궤적)는 무한대. 명시 강성이 고정 스텝 RK4 에 비해 너무 크면
            // ω_n·dt ≤ OmegaDt(공유 힌지 N 개면 N 배 강성이 한 루트에 걸리므로 1/N)로 낮춘다(감쇠비 0.7, 자동 감쇠).
            // 루트가 바뀌거나(한쪽이 늦게 분리) 공유 수가 바뀌면 다시 계산.
            Gains(w, ra, rb, A.PointWorld(PointA), share);
            _gainRoots = (ra, rb, share);
        }
        if (_bound) return;
        var pa = A.PointWorld(PointA);
        var ua = A.WorldQ.Rotate(AxisA.Normalized());
        _pb = B.WorldToBrf(pa);
        _axb = B.WorldQ.InverseRotate(ua);
        var toB = B.OwnCgWorld - pa;
        var perp = toB - ua * Vec3.Dot(toB, ua);
        if (perp.Norm < 1e-9) perp = Vec3.Cross(ua, Math.Abs(ua.Z) < 0.9 ? Vec3.UnitZ : Vec3.UnitX);
        perp = perp.Normalized();
        _refA = A.WorldQ.InverseRotate(perp);
        _refB = B.WorldQ.InverseRotate(perp);
        _bound = true;
    }

    private void Gains(World w, Body ra, Body rb, Vec3 pointWorld, int share)
    {
        double mob = PointMobility(ra, pointWorld) + PointMobility(rb, pointWorld);
        double iia = ra.Mode == BodyMode.Free ? 1 / MinInertia(ra) : 0, iib = rb.Mode == BodyMode.Free ? 1 / MinInertia(rb) : 0;
        double mEff = 1 / Math.Max(mob, 1e-12), iEff = 1 / Math.Max(iia + iib, 1e-12);
        double wMax = OmegaDt / Math.Max(Math.Max(w.Dt, StabilityDt), 1e-6);
        // 한 번 정한 강성은 줄기만 한다 — 이웃 힌지가 이탈해 공유 수가 줄 때 k 를 키우면 남은 간극 x 에서 반력 k·x 가 계단으로 튄다
        bool first = _k <= 0;
        _k = Math.Min(Stiffness, mEff * wMax * wMax / share);
        _kr = Math.Min(RotStiffness, iEff * wMax * wMax / share);
        if (!first) { _k = Math.Min(_k, _kPrev); _kr = Math.Min(_kr, _krPrev); }
        _kPrev = _k; _krPrev = _kr;
        // 명시 감쇠도 극점 c·Δt/m* ≤ 2·0.7·OmegaDt 로 자른다(RK4 실축 안정)
        double cMax = 2 * 0.7 * Math.Sqrt(_k * mEff), crMax = 2 * 0.7 * Math.Sqrt(_kr * iEff);
        _c = Damping >= 0 ? Math.Min(Damping, cMax) : cMax;
        _cr = RotDamping >= 0 ? Math.Min(RotDamping, crMax) : crMax;
        EffectiveMass = mEff; SharedHinges = share;
    }

    /// <summary>B 에 작용하는 마지막 반력 벡터(NED) [N].</summary>
    public Vec3 ForceVector { get; private set; }

    /// <summary>반력 이동 평균 창 [s] — 힌지 금구·외피 접합부 구조 응답 시간 규모(수 ms, 가정)이며 Δt 수렴 지표.</summary>
    public double AvgWindow { get; init; } = 0.005;
    /// <summary>AvgWindow 이동 평균 반력의 최대 [N]·시각 — 구조 하중·보고용(순간 MaxForce 는 펜널티 과도 포함).</summary>
    public double MaxForceAvg { get; private set; }
    public double MaxForceAvgTime { get; private set; } = double.NaN;
    private readonly Queue<(double T, double Dt, double F)> _win = new();
    private double _winSum, _winSpan;

    private (Vec3 F, Vec3 Pa, Vec3 Pb, Vec3 TauB) Eval()
    {
        var pa = A.PointWorld(PointA);
        var pb = B.PointWorld(_pb);
        var vrel = B.PointVelocity(_pb) - A.PointVelocity(PointA);
        var f = (pb - pa) * -_k - vrel * _c;
        var ua = A.WorldQ.Rotate(AxisA).Normalized();
        var ub = B.WorldQ.Rotate(_axb).Normalized();
        var wa = A.Root.Mode == BodyMode.Free ? A.Root.Q.Rotate(A.Root.W) : Vec3.Zero;
        var wb = B.Root.Mode == BodyMode.Free ? B.Root.Q.Rotate(B.Root.W) : Vec3.Zero;
        var dw = wb - wa;
        dw -= ua * Vec3.Dot(dw, ua);
        var tau = Vec3.Cross(ub, ua) * _kr - dw * _cr;
        return (f, pa, pb, tau);
    }

    public override void Apply(World w, double t)
    {
        if (!Engaged) return;
        Bind(w);
        var (f, pa, pb, tau) = Eval();
        w.ApplyForce(B, pb, f);
        w.ApplyForce(A, pa, f * -1);
        if (B.Root.Mode == BodyMode.Free) w.ApplyMomentBody(B, B.Root.Q.InverseRotate(tau));
        if (A.Root.Mode == BodyMode.Free) w.ApplyMomentBody(A, A.Root.Q.InverseRotate(tau * -1));
    }

    public override void PostStep(World w, double t, double dt)
    {
        if (!Engaged) { Force = 0; return; }
        Bind(w);
        var (f, _, _, _) = Eval();
        Force = f.Norm;
        ForceVector = f;
        var ua = A.WorldQ.Rotate(AxisA).Normalized();
        var ra = A.WorldQ.Rotate(_refA); var rb = B.WorldQ.Rotate(_refB);
        ra -= ua * Vec3.Dot(ra, ua); rb -= ua * Vec3.Dot(rb, ua);
        AngleDeg = Math.Atan2(Vec3.Dot(Vec3.Cross(ra, rb), ua), Vec3.Dot(ra, rb)) * 180 / Math.PI;
        if (Force > MaxForce) { MaxForce = Force; MaxForceTime = t; }
        // 구조 하중용 이동 평균(AvgWindow): 이웃 힌지가 한 스텝에 이탈할 때의 단일 스텝 펜널티 과도(Δt 에 따라 크기가 바뀜)를 걸러
        // Δt 수렴하는 반력 — 시나리오 09 에서 Δt 1 ms ↔ 0.25 ms 차 < 2 % (순간값은 최대 50 % 차)
        _win.Enqueue((t, dt, Force)); _winSum += Force * dt; _winSpan += dt;
        while (_win.Count > 1 && _win.Peek().T <= t - AvgWindow + 1e-12)
        {
            var o = _win.Dequeue(); _winSum -= o.F * o.Dt; _winSpan -= o.Dt;
        }
        double avg = _winSpan > 1e-15 ? _winSum / _winSpan : Force;
        if (avg > MaxForceAvg) { MaxForceAvg = avg; MaxForceAvgTime = t; }
        MaxAngleDeg = Math.Max(MaxAngleDeg, Math.Abs(AngleDeg));
        string? why = Math.Abs(AngleDeg) >= ReleaseAngleDeg ? $"열림각 {Math.Abs(AngleDeg):F1}° ≥ {ReleaseAngleDeg:F0}°"
            : Force > BreakLoad ? $"반력 {Force:F0} N > {BreakLoad:F0} N" : null;
        if (why is null) return;
        Released = true; ReleaseTime = t; ReleaseReason = why;
        w.Log($"{Name}.released", $"힌지 {Name} 이탈 ({why})");
    }

    public override void Command(World w, string command, JsonObject args)
    {
        if (command is "release" or "deactivate" && !Released) { Released = true; ReleaseTime = w.T; ReleaseReason = "명령"; }
        base.Command(w, command, args);
    }

    public override ConnectorSample Sample(World w)
    {
        if (!_bound || !Engaged) return new(Name, Type, false, A.PointWorld(PointA), A.PointWorld(PointA), 0);
        return new(Name, Type, true, A.PointWorld(PointA), B.PointWorld(_pb), Force, ForceVector);
    }

    public override void Summarize(JsonObject s)
    {
        s["type"] = Type;
        s["maxForce_N"] = MaxForce;
        s["maxForceTime_s"] = Parachute.Num(MaxForceTime);
        s["maxForceAvg_N"] = MaxForceAvg;
        s["maxForceAvgTime_s"] = Parachute.Num(MaxForceAvgTime);
        s["avgWindow_s"] = AvgWindow;
        s["maxAngle_deg"] = MaxAngleDeg;
        s["released"] = Released;
        s["releaseTime_s"] = Parachute.Num(ReleaseTime);
        s["releaseReason"] = ReleaseReason;
        s["releaseAngle_deg"] = ReleaseAngleDeg;
        s["effectiveStiffness_Npm"] = _k;
        s["effectiveRotStiffness_Nmprad"] = _kr;
        s["effectiveMass_kg"] = Parachute.Num(EffectiveMass);
        s["sharedHinges"] = SharedHinges;
    }
}

/// <summary>
/// 레일 구속 분리(FR-SEP-02): 자식 물체가 부모의 레일 축을 따라 길이 L 만큼 1자유도로 미끄러진 뒤 자유물체가 된다.
/// 상대 가속도 = (g − a_부모)·e − μ|법선성분|. 레일 위치는 스텝마다 반암시적 적분(POC).
/// </summary>
public sealed class Rail : Connector
{
    public override string Type => "rail";
    public required Body Parent { get; init; }
    public required Body Child { get; init; }
    public Vec3 Axis { get; init; } = new(0, 0, 1);
    public double Length { get; init; } = 0.34;
    public double Friction { get; init; } = 0.1;
    public double ExitPush { get; init; }

    public bool Started { get; private set; }
    public double S { get; private set; }
    public double SDot { get; private set; }
    private Vec3 _mount0;

    public override void Command(World w, string command, JsonObject args)
    {
        if (command is "start" or "activate")
        {
            if (Child.Parent != Parent || Child.Mode != BodyMode.Stowed)
                throw new InvalidOperationException($"rail '{Name}': {Child.Name} 이 {Parent.Name} 에 붙어 있지 않습니다.");
            Started = true; _mount0 = Child.MountPos; Active = true;
            return;
        }
        base.Command(w, command, args);
    }

    public override void PostStep(World w, double t, double dt)
    {
        if (!Started || !Active || Child.Mode != BodyMode.Stowed || Child.Parent != Parent) return;
        var root = Parent.Root;
        var e = Parent.WorldQ.Rotate(Axis.Normalized());
        var rel = w.Env.GravityVector - root.Accel;
        double ae = Vec3.Dot(rel, e);
        double an = (rel - e * ae).Norm;
        double fr = Friction * an;
        double a;
        if (SDot > 1e-9) a = ae - fr;
        else a = ae > fr ? ae - fr : 0;
        SDot = Math.Max(0, SDot + a * dt);
        S += SDot * dt;
        if (S >= Length)
        {
            S = Length;
            Child.MountPos = _mount0 + Axis.Normalized() * S;
            Active = false;
            var relVel = Axis.Normalized() * (SDot + ExitPush);
            w.Separate(Child, relVel, Vec3.Zero, $"{Name}.exit");
            return;
        }
        Child.MountPos = _mount0 + Axis.Normalized() * S;
        if (root.Mode == BodyMode.Free) root.RecomputeComposite(true);
    }

    public override ConnectorSample Sample(World w)
    {
        var a = Parent.PointWorld(_mount0);
        return new(Name, Type, Started && Active, a, Child.PointWorld(Vec3.Zero), 0);
    }

    public override void Summarize(JsonObject s)
    {
        s["type"] = Type;
        s["travel_m"] = S;
        s["exitSpeed_mps"] = SDot;
    }
}

/// <summary>
/// 붙은 채 열리는 도어(스윙): 수납(Stowed) 자식을 부모 동체 좌표의 힌지 점·축 둘레로 정해진 각도까지 운동학적으로 돌린다
/// (토션 스프링 개방 — 각도 이력 θ(t) = θ_max·(1 − cos πs)/2, s = (t − t0)/duration). 자식은 계속 부모에 붙어 있어
/// 질량은 부모 합성값에 남고, 장착 자세가 바뀔 때마다 합성 CG·관성을 다시 계산한다. 끝나면 이벤트 '{name}.open'.
/// 외피 조각처럼 떨어져 나가는 힌지(Hinge)와 다르다 — 반력·도어 공력 하중은 계산하지 않는다(표시·질량 분포용).
/// </summary>
public sealed class Swing : Connector
{
    public override string Type => "swing";
    public required Body Parent { get; init; }
    public required Body Child { get; init; }
    /// <summary>힌지 점·축(부모 동체 좌표). 회전 방향은 축 기준 오른손.</summary>
    public Vec3 Point { get; init; }
    public Vec3 Axis { get; init; } = new(0, 0, 1);
    public double AngleDeg { get; init; } = 90;
    public double Duration { get; init; } = 0.3;

    public bool Started { get; private set; }
    public bool Opened { get; private set; }
    public double StartTime { get; private set; } = double.NaN;
    public double OpenTime { get; private set; } = double.NaN;
    public double CurrentAngleDeg { get; private set; }
    private Vec3 _pos0;
    private Quat _q0;

    public override void Command(World w, string command, JsonObject args)
    {
        if (command is "start" or "activate")
        {
            if (Started) return;
            if (Child.Parent != Parent || Child.Mode != BodyMode.Stowed)
                throw new InvalidOperationException($"swing '{Name}': {Child.Name} 이 {Parent.Name} 에 붙어 있지 않습니다.");
            Started = true; Active = true; StartTime = w.T; _pos0 = Child.MountPos; _q0 = Child.MountQ;
            return;
        }
        base.Command(w, command, args);
    }

    public override void PostStep(World w, double t, double dt)
    {
        if (!Started || Opened || !Active || Child.Mode != BodyMode.Stowed || Child.Parent != Parent) return;
        double s = Math.Clamp((t - StartTime) / Math.Max(Duration, 1e-9), 0, 1);
        CurrentAngleDeg = AngleDeg * (1 - Math.Cos(Math.PI * s)) / 2;
        var r = Quat.FromAxisAngle(Axis.Normalized(), CurrentAngleDeg * Math.PI / 180);
        Child.MountQ = (r * _q0).Normalized();
        Child.MountPos = Point + r.Rotate(_pos0 - Point);
        var root = Parent.Root;
        if (root.Mode == BodyMode.Free) root.RecomputeComposite(true);
        if (s >= 1)
        {
            Opened = true; OpenTime = t;
            w.Log($"{Name}.open", $"{Child.Name} 개방 {AngleDeg:F0}°");
        }
    }

    public override ConnectorSample Sample(World w) => new(Name, Type, false, Parent.PointWorld(Point), Child.PointWorld(Vec3.Zero), 0);

    public override void Summarize(JsonObject s)
    {
        s["type"] = Type;
        s["angle_deg"] = CurrentAngleDeg;
        s["startTime_s"] = Parachute.Num(StartTime);
        s["openTime_s"] = Parachute.Num(OpenTime);
    }
}
