using System.Text.Json.Nodes;
using Sim.Core.Engine;
using Sim.Core.Numerics;

namespace Sim.Core.Models;

/// <summary>물체에 붙는 능동/수동 장치 요소(Device). Apply 는 순수 함수여야 한다(이분 탐색 재적분 대비).</summary>
public abstract class Device
{
    public required string Name { get; init; }
    public required Body Owner { get; init; }
    public abstract string Type { get; }
    public string FullName => $"{Owner.Name}.{Name}";

    /// <summary>RK4 단계마다 호출: 힘/모멘트를 world.ApplyForce 등으로 누산.</summary>
    public virtual void Apply(World world, double t) { }
    /// <summary>스텝 종료 후 1회: 이산 상태 갱신·피크 기록.</summary>
    public virtual void PostStep(World world, double t, double dt) { }
    /// <summary>Action 명령: deploy / release / activate / deactivate / set.</summary>
    public virtual void Command(World world, string command, JsonObject args) =>
        throw new InvalidOperationException($"{Type} '{FullName}' 은 '{command}' 명령을 지원하지 않습니다.");
    public virtual void Summarize(JsonObject summary) { }
    /// <summary>시나리오 빌드 끝(모든 물체·장치·연결 생성 후)에 1회: 다른 요소 참조 해석·검증.</summary>
    public virtual void Bind(World world) { }
    /// <summary>시각화·텔레메트리용 스냅샷.</summary>
    public virtual DeviceSample Sample(World world) => new(FullName, Type, false, 0, Vec3.Zero, Vec3.Zero, 0, 0);
}

/// <summary>기록용 장치 스냅샷. Anchor=결속점(NED), Tip=캐노피 중심(NED), Size=캐노피 직경(multirotor 는 암 전개율 0~1).</summary>
public readonly record struct DeviceSample(string Name, string Type, bool Visible, double Fill, Vec3 Anchor, Vec3 Tip, double Size, double Force);

/// <summary>고정 팔 멀티로터의 프롭 허브(표시용, 동체 좌표): 허브 중심·회전축(추력 방향 단위벡터)·회전 방향 ±1.</summary>
public readonly record struct PropHub(Vec3 Position, Vec3 Axis, int Spin);

public enum ChuteState { Stowed, LineStretch, Inflating, Open, Released }

/// <summary>
/// 낙하산(FR-PAR-01/02): 팩 사출 → 라인 신장(지연) → 충만 (CdS)(t)=(CdS)₀·(t/t_f)^j, t_f = n·D₀/V_s → 정상.
/// POC: 결속점에 항력이 작용하는 장치로 모델링(별도 물체·부가질량은 후속, 가정 목록 기록).
/// </summary>
public sealed class Parachute : Device
{
    public override string Type => "parachute";
    public double D0 { get; init; } = 1.22;
    public double Cd0 { get; init; } = 2.2;
    public Vec3 Attach { get; init; }
    public double RiserLength { get; init; } = 2;
    public double FillConstant { get; init; } = 8;
    public double FillExponent { get; init; } = 2;
    public double LineStretchTime { get; init; } = 0.3;
    public double InitialFill { get; init; } = 0.02;
    public double CdSScale { get; set; } = 1;
    /// <summary>
    /// 겉보기 질량 계수 k0 (Cruz·Kandis·Witkowski AIAA 2003-2131 식 3·6): m_a = k0·ρ·Γ0·Sr^1.5, Γ0 = π·D0³/12.
    /// 충만 중 유입 항 1.5·k0·ρ·Γ0·Sr^0.5·(dSr/dt)·V 를 항력에 더한다(개방 충격 초과분). 0 = 미적용(기본).
    /// m_a·dV/dt 항은 생략(감속 중 하중을 약간 과대 → 보수적, 가정 목록 기록).
    /// </summary>
    public double ApparentMassK0 { get; init; }
    /// <summary>모델 적용 범위: 비압축 항력·충만 모델이므로 마하수 이 값 이하에서만 검증 대상(VV-32).</summary>
    public const double MaxValidMach = 0.5;

    /// <summary>
    /// 고장 모드(FR-PAR-05, MC 고장 주입): none | noDeploy(미전개) | delay(라인 신장 지연 FailureDelay s) |
    /// partial(충만 상한 FailureFill) | lineTwist(라인 꼬임 — CdS × (1 − FailureCdsLoss)).
    /// FailureProbability &lt; 1 이면 실행 시드(sim.seed)·장치 이름으로 정한 난수로 발생 여부를 정한다(실행별 재현).
    /// </summary>
    public string FailureMode { get; init; } = "none";
    public double FailureDelay { get; init; } = 1.0;
    public double FailureFill { get; init; } = 0.5;
    public double FailureCdsLoss { get; init; } = 0.3;
    public double FailureProbability { get; init; } = 1.0;
    /// <summary>이번 실행에서 고장이 실제로 걸렸는지(배포 명령 때 결정).</summary>
    public bool FailureActive { get; private set; }

    /// <summary>잔해 간섭에 의한 전개 이상 기록(FR-AN-03).</summary>
    public sealed record Anomaly(double Time, string Debris, string Region, string Phase, string Effect, double RelSpeed);
    public List<Anomaly> Anomalies { get; } = new();
    /// <summary>이상·고장으로 더해진 라인 신장 지연 [s], 충만 상한(1 = 정상), 라인 손상 누적 CdS 배율.</summary>
    public double ExtraDelay { get; private set; }
    public double FillCap { get; private set; } = 1;
    public double DamageFactor { get; private set; } = 1;

    public ChuteState State { get; private set; } = ChuteState.Stowed;
    public double DeployTime { get; private set; } = double.NaN;
    public double StretchSpeed { get; private set; } = double.NaN;
    public double FillTime { get; private set; } = double.NaN;
    public double MaxForce { get; private set; }
    public double MaxForceTime { get; private set; } = double.NaN;
    public double MaxG { get; private set; }
    private double _lastForce;
    /// <summary>마지막 스텝 낙하산 힘 크기 [N] (하중 경로 기록 채널, FR-AN-08).</summary>
    public double Force => _lastForce;

    public double CdS0 => Cd0 * Math.PI / 4 * D0 * D0 * CdSScale * DamageFactor;

    /// <summary>라인 신장 완료 시각(전개 + 신장 시간 + 이상·고장 지연).</summary>
    public double StretchCompleteTime => DeployTime + LineStretchTime + ExtraDelay;

    /// <summary>
    /// 1단 리핑(reefing): ReefRatio = 리핑 단계 CdS / 완전 개방 CdS (0 = 리핑 없음). 1단계 충만 t_f1 = n·D₀·√r / V_s 로 r 까지,
    /// 라인 신장 완료 후 DisreefDelay(리핑 커터 지연, 1단계 충만보다 짧으면 충만 끝) 에 디스리프 → 2단계 t_f2 = n·D₀·(1 − √r) / V_d 로
    /// r → 1 (Sr = r + (1 − r)·τ₂^j). 충만 거리를 직경 증가분에 비례로 둔 단순화(Knacke 5장 리핑 충만 — 가정 목록 기록).
    /// </summary>
    public double ReefRatio { get; init; }
    public double DisreefDelay { get; init; } = 2.0;
    public double DisreefTime { get; private set; } = double.NaN;
    public double DisreefSpeed { get; private set; } = double.NaN;
    public double FillTime2 { get; private set; } = double.NaN;
    /// <summary>리핑 단계 최대 하중 [N](디스리프 전) · 디스리프 뒤 최대 하중 [N].</summary>
    public double MaxForceReefed { get; private set; }
    public double MaxForceDisreef { get; private set; }
    public bool Reefed => ReefRatio > 0 && ReefRatio < 1;

    private double Tf1(double speed) => !double.IsNaN(FillTime) ? FillTime
        : FillConstant * D0 * (Reefed ? Math.Sqrt(ReefRatio) : 1) / Math.Max(speed, 1);

    /// <summary>디스리프 시각(리핑 없으면 NaN): 라인 신장 완료 + max(DisreefDelay, 1단계 충만 시간).</summary>
    private double DisreefAt(double speed) => Reefed ? StretchCompleteTime + Math.Max(DisreefDelay, Tf1(speed)) : double.NaN;

    private double Tf2(double speed) => !double.IsNaN(FillTime2) ? FillTime2 : FillConstant * D0 * (1 - Math.Sqrt(ReefRatio)) / Math.Max(speed, 1);

    public double FillFraction(double t, double speed)
    {
        if (State is ChuteState.Stowed or ChuteState.Released || double.IsNaN(DeployTime)) return 0;
        double ts = StretchCompleteTime;
        if (t < ts) return 0;
        double tf = Tf1(speed);
        double tau = Math.Clamp((t - ts) / tf, 0, 1);
        if (!Reefed) return Math.Min(FillCap, Math.Max(InitialFill, Math.Pow(tau, FillExponent)));
        double td = DisreefAt(speed);
        double s = t < td ? ReefRatio * Math.Pow(tau, FillExponent)
            : ReefRatio + (1 - ReefRatio) * Math.Pow(Math.Clamp((t - td) / Tf2(speed), 0, 1), FillExponent);
        return Math.Min(FillCap, Math.Max(InitialFill, s));
    }

    /// <summary>충만비 변화율 dSr/dt [1/s] (충만 곡선 해석 미분, 충만 전·후·상한 도달 후 0).</summary>
    public double FillRate(double t, double speed)
    {
        if (State is ChuteState.Stowed or ChuteState.Released || double.IsNaN(DeployTime)) return 0;
        double ts = StretchCompleteTime;
        if (t < ts) return 0;
        double tf = Tf1(speed);
        double tau = (t - ts) / tf;
        if (Reefed)
        {
            double td = DisreefAt(speed);
            double f = FillFraction(t, speed);
            if (f <= InitialFill || f >= FillCap) return 0;
            if (t < td) return tau <= 0 || tau >= 1 ? 0 : ReefRatio * FillExponent * Math.Pow(tau, FillExponent - 1) / tf;
            double tf2 = Tf2(speed), tau2 = (t - td) / tf2;
            return tau2 <= 0 || tau2 >= 1 ? 0 : (1 - ReefRatio) * FillExponent * Math.Pow(tau2, FillExponent - 1) / tf2;
        }
        double s = Math.Pow(Math.Max(tau, 0), FillExponent);
        if (tau <= 0 || tau >= 1 || s <= InitialFill || s >= FillCap) return 0;
        return FillExponent * Math.Pow(tau, FillExponent - 1) / tf;
    }

    /// <summary>
    /// 잔해 간섭 전개 이상(FR-AN-03): 전개 단계별 효과 — 라인 신장 중: 충만 지연 + (라인 영역이면) 라인 손상,
    /// 충만 중: 부분 충만(상한), 완전 개방 후: 캐노피·라인 손상(CdS 감소). 상대속도가 문턱 미만이면 효과 없음(간섭 기록만).
    /// 스텝 사이(PostStep)에서만 호출 — RK4 단계 함수는 순수하게 유지.
    /// </summary>
    public string? ApplyDebrisAnomaly(World w, string debris, string region, double relSpeed, ChuteAnomalyModel m)
    {
        if (State is ChuteState.Stowed or ChuteState.Released || relSpeed < m.MinRelSpeed) return null;
        string phase = State.ToString(), effect;
        switch (State)
        {
            case ChuteState.LineStretch:
                ExtraDelay += m.Delay;
                effect = $"충만 지연 +{m.Delay:F2} s";
                if (region == "lines") { DamageFactor *= 1 - m.LineDamagePerHit; effect += $", 라인 손상 CdS×{1 - m.LineDamagePerHit:F2}"; }
                break;
            case ChuteState.Inflating:
                FillCap = Math.Min(FillCap, m.PartialFill);
                effect = $"부분 충만(상한 {FillCap:F2})";
                break;
            default:
                DamageFactor *= 1 - m.LineDamagePerHit;
                effect = $"캐노피·라인 손상 CdS×{1 - m.LineDamagePerHit:F2}";
                break;
        }
        Anomalies.Add(new Anomaly(w.T, debris, region, phase, effect, relSpeed));
        w.Log($"anomaly:{FullName}", $"전개 이상 {FullName}: {debris} 가 {(region == "lines" ? "라인" : "캐노피")} 영역 통과({phase}, {relSpeed:F1} m/s) → {effect}");
        return effect;
    }

    private static ulong StableHash(string s)
    {
        ulong h = 1469598103934665603UL;
        foreach (char c in s) { h ^= c; h *= 1099511628211UL; }
        return h;
    }

    private void ArmFailure(World w)
    {
        if (FailureMode == "none") return;
        FailureActive = FailureProbability >= 1 || Rng.ForStream(w.Seed, 5000 + (StableHash(FullName) & 0xFFFFF)).NextDouble() < FailureProbability;
        if (!FailureActive) return;
        switch (FailureMode)
        {
            case "delay": ExtraDelay += FailureDelay; break;
            case "partial": FillCap = Math.Min(FillCap, FailureFill); break;
            case "lineTwist": DamageFactor *= 1 - FailureCdsLoss; break;
        }
        w.Log($"failure:{FullName}", $"낙하산 고장 주입 {FullName}: {FailureMode}");
    }

    private (Vec3 Force, Vec3 Point, Vec3 VAir) Eval(World w, double t)
    {
        var p = Owner.PointWorld(Attach);
        var vAir = Owner.PointVelocity(Attach) - w.WindAt(Owner.Root, p);
        double v = vAir.Norm;
        double fill = FillFraction(t, v);
        if (fill <= 0 || v < 1e-9) return (Vec3.Zero, p, vAir);
        double rho = w.Env.Atmosphere.At(-p.Z).Density;
        double k = 0.5 * rho * v * CdS0 * fill;
        if (ApparentMassK0 > 0)
        {
            double gamma0 = Math.PI * D0 * D0 * D0 / 12;
            // |F_am| = 1.5·k0·ρ·Γ0·√Sr·(dSr/dt)·V  (vAir 를 곱하므로 여기서는 V 를 뺀 계수)
            k += 1.5 * ApparentMassK0 * rho * gamma0 * Math.Sqrt(fill) * FillRate(t, v);
        }
        var f = vAir * -k;
        return (f, p, vAir);
    }

    public override void Apply(World w, double t)
    {
        if (Owner.Root.Mode != BodyMode.Free) return;
        var (f, p, _) = Eval(w, t);
        if (f.NormSq > 0) w.ApplyForce(Owner, p, f);
    }

    public override void PostStep(World w, double t, double dt)
    {
        if (State is ChuteState.Stowed or ChuteState.Released) return;
        var (f, _, vAir) = Eval(w, t);
        double ts = StretchCompleteTime;
        if (State == ChuteState.LineStretch && t >= ts)
        {
            StretchSpeed = Math.Max(vAir.Norm, 1);
            FillTime = FillConstant * D0 * (Reefed ? Math.Sqrt(ReefRatio) : 1) / StretchSpeed;
            State = ChuteState.Inflating;
        }
        if (Reefed)
        {
            double td = DisreefAt(StretchSpeed);
            if (State == ChuteState.Inflating && double.IsNaN(DisreefTime) && t >= td)
            {
                DisreefTime = td;
                DisreefSpeed = Math.Max(vAir.Norm, 1);
                FillTime2 = FillConstant * D0 * (1 - Math.Sqrt(ReefRatio)) / DisreefSpeed;
                w.Log($"{FullName}.disreef", $"{FullName} 디스리프 (리핑 CdS 비 {ReefRatio:F2} → 1, {DisreefSpeed:F1} m/s)");
            }
            if (State == ChuteState.Inflating && !double.IsNaN(DisreefTime) && t >= DisreefTime + FillTime2) State = ChuteState.Open;
        }
        else if (State == ChuteState.Inflating && t >= ts + FillTime) State = ChuteState.Open;
        _lastForce = f.Norm;
        if (Reefed)
        {
            if (double.IsNaN(DisreefTime)) MaxForceReefed = Math.Max(MaxForceReefed, _lastForce);
            else MaxForceDisreef = Math.Max(MaxForceDisreef, _lastForce);
        }
        if (_lastForce > MaxForce)
        {
            MaxForce = _lastForce; MaxForceTime = t;
            MaxG = _lastForce / (Owner.Root.Mass * w.Env.Gravity);
        }
    }

    public override void Command(World w, string command, JsonObject args)
    {
        switch (command)
        {
            case "deploy":
                if (State != ChuteState.Stowed || !double.IsNaN(DeployTime) && FailureActive) break;
                ArmFailure(w);
                if (FailureActive && FailureMode == "noDeploy") { DeployTime = w.T; break; }   // 팩 사출 실패: 수납 유지
                State = ChuteState.LineStretch; DeployTime = w.T;
                break;
            case "release":
                State = ChuteState.Released;
                break;
            default: base.Command(w, command, args); break;
        }
    }

    public override DeviceSample Sample(World w)
    {
        var anchor = Owner.PointWorld(Attach);
        if (State is ChuteState.Stowed or ChuteState.Released || Owner.Root.Mode == BodyMode.Stowed)
            return new(FullName, Type, false, 0, anchor, anchor, D0, 0);
        var vAir = Owner.PointVelocity(Attach) - w.WindAt(Owner.Root, anchor);
        var dir = vAir.NormSq > 1e-12 ? vAir.Normalized() : new Vec3(0, 0, 1);
        double fill = FillFraction(w.T, vAir.Norm);
        double ext = State == ChuteState.LineStretch ? Math.Clamp((w.T - DeployTime) / Math.Max(LineStretchTime, 1e-3), 0, 1) : 1;
        var tip = anchor - dir * ((RiserLength + D0 * 0.4) * ext);
        if (Owner.Root.Mode == BodyMode.Landed) tip = anchor + new Vec3(RiserLength, 0, -0.05);
        return new(FullName, Type, true, fill, anchor, tip, D0, _lastForce);
    }

    public override void Summarize(JsonObject s)
    {
        s["type"] = Type;
        s["state"] = State.ToString();
        s["cdS0_m2"] = CdS0;
        s["deployTime_s"] = Num(DeployTime);
        s["lineStretchSpeed_mps"] = Num(StretchSpeed);
        s["fillTime_s"] = Num(FillTime);
        s["maxForce_N"] = MaxForce;
        s["maxForceTime_s"] = Num(MaxForceTime);
        s["maxOpeningG"] = MaxG;
        if (Reefed)
        {
            s["reefRatio"] = ReefRatio;
            s["disreefDelay_s"] = DisreefDelay;
            s["disreefTime_s"] = Num(DisreefTime);
            s["disreefSpeed_mps"] = Num(DisreefSpeed);
            s["fillTime2_s"] = Num(FillTime2);
            s["maxForceReefed_N"] = MaxForceReefed;
            s["maxForceDisreef_N"] = MaxForceDisreef;
        }
        if (FailureMode != "none") { s["failureMode"] = FailureMode; s["failureActive"] = FailureActive; }
        if (Anomalies.Count > 0 || FailureActive)
        {
            s["anomalies"] = new JsonArray(Anomalies.Select(a => (JsonNode)new JsonObject
            {
                ["t"] = a.Time, ["debris"] = a.Debris, ["region"] = a.Region, ["phase"] = a.Phase, ["effect"] = a.Effect, ["relSpeed_mps"] = a.RelSpeed,
            }).ToArray());
            s["extraDelay_s"] = ExtraDelay;
            s["fillCap"] = FillCap;
            s["damageFactor"] = DamageFactor;
        }
    }

    internal static JsonNode? Num(double v) => double.IsFinite(v) ? JsonValue.Create(v) : null;
}

/// <summary>
/// 충격 펄스(FR-SHL-03 · FR-SEP-01): 절단 폭약·사출 충격이 물체에 전달되는 가속도 펄스.
/// fire(=deploy/activate) 시각 t0 부터 duration 동안 F = m_root·g0·peakG·w(t), 방향 = 동체 direction.
/// w(t): halfsine = sin(π(t−t0)/τ) (기본), square = 1. 순수 시간 함수(RK4 안전).
/// </summary>
public sealed class Pulse : Device
{
    public override string Type => "pulse";
    public double PeakG { get; init; } = 15;
    public double Duration { get; init; } = 0.02;
    public Vec3 Direction { get; init; } = new(-1, 0, 0);
    public string Shape { get; init; } = "halfsine";
    public double FireTime { get; private set; } = double.NaN;
    /// <summary>반작용을 받는 물체(FR-SEP-01 사출기: 같은 크기 반대 방향 힘을 모체에, 작용선 = 사출 물체 CG). null = 반작용 없음.</summary>
    public Body? Reaction { get; init; }

    public double Weight(double t)
    {
        if (double.IsNaN(FireTime) || t < FireTime || t > FireTime + Duration || Duration <= 0) return 0;
        return Shape == "square" ? 1 : Math.Sin(Math.PI * (t - FireTime) / Duration);
    }

    public override void Apply(World w, double t)
    {
        double k = Weight(t);
        if (k <= 0 || Owner.Root.Mode != BodyMode.Free) return;
        var dir = Owner.WorldQ.Rotate(Direction.Normalized());
        var p = Owner.OwnCgWorld;
        var f = dir * (Owner.Root.Mass * Environment.Atmosphere.G0 * PeakG * k);
        w.ApplyForce(Owner, p, f);
        if (Reaction is { } r && r.Root != Owner.Root) w.ApplyForce(r, p, f * -1);
    }

    public override void Command(World w, string command, JsonObject args)
    {
        if (command is "fire" or "deploy" or "activate") { if (double.IsNaN(FireTime)) FireTime = w.T; return; }
        base.Command(w, command, args);
    }

    public override DeviceSample Sample(World w)
    {
        double k = Weight(w.T);
        var p = Owner.PointWorld(Owner.OwnCg);
        return new(FullName, Type, k > 0, k, p, p + Owner.WorldQ.Rotate(Direction.Normalized()) * 0.3, 0, Owner.Root.Mass * Environment.Atmosphere.G0 * PeakG * k);
    }

    public override void Summarize(JsonObject s)
    {
        s["type"] = Type;
        s["peakG"] = PeakG;
        s["duration_s"] = Duration;
        s["fireTime_s"] = Parachute.Num(FireTime);
        s["deltaV_mps"] = Environment.Atmosphere.G0 * PeakG * Duration * (Shape == "square" ? 1 : 2 / Math.PI);
    }
}

/// <summary>
/// 멀티로터 추진 + 내장 제어기(FR-UAV-01/04 간이판). 추력은 동체 −Z(위) 방향, 1차 지연 τ.
/// 제어: 위치(목표 N,E) → 속도 → 가속도 → 원하는 추력 벡터 → 자세 PD. 모드: off / hold / goto / land.
/// VRS 추력 손실은 선택(vrsDepth, 기본 0 = 미적용) — sim_release.py 경험식 이식은 후속(가정 목록).
/// </summary>
public sealed class Multirotor : Device
{
    public override string Type => "multirotor";
    public double MaxThrust { get; init; } = 85;
    public double Tau { get; init; } = 0.15;
    public double MaxTiltDeg { get; init; } = 35;
    public double CruiseSpeed { get; set; } = 10;
    public double MaxClimb { get; set; } = 3;
    public double MaxDescent { get; set; } = 3;
    public double LandSpeed { get; set; } = 1.2;
    public double KpPos { get; init; } = 0.6;
    public double KpVel { get; init; } = 1.8;
    public double KpAlt { get; init; } = 0.8;
    public double AttKp { get; init; } = 40;
    public double AttKd { get; init; } = 12;
    public double RotorDiskArea { get; init; } = 4 * Math.PI * 0.0889 * 0.0889;
    public double VrsDepth { get; init; }
    /// <summary>"gaussian"(v1 간이, vrsDepth&gt;0 일 때) | "uiuc-v2"(RotorModel: BET+운동량+경험 VRS, UIUC 자료 보정·검증) | "none"</summary>
    public string VrsModel { get; init; } = "gaussian";
    /// <summary>프로펠러 솔리디티 σ (uiuc-v2).</summary>
    public double PropSolidity { get; init; } = 0.09;
    /// <summary>프로펠러 정지 CT0 (T/ρn²D⁴, uiuc-v2).</summary>
    public double PropCt0 { get; init; } = 0.1;
    public double IdleRatio { get; set; } = 0.3;
    /// <summary>
    /// 접힘 암 전개(FR-SEP-04, 표시용): 소유 물체가 이 구성으로 switchConfig 되면 ArmDeployTime 동안 펴진다.
    /// null 이면 암이 처음부터 펴진 것으로 본다(접힘 구성 없음).
    /// </summary>
    public string? ArmsConfig { get; init; }
    /// <summary>스프링 힌지 전개 시간 [s] (가정).</summary>
    public double ArmDeployTime { get; init; } = 0.5;
    public int ArmCount { get; init; } = 4;
    /// <summary>힌지 → 모터 축 거리 [m]. 0 = 표시 시 동체 형상 높이 기준 자동.</summary>
    public double ArmLength { get; init; }
    /// <summary>프로펠러 직경 [m] — 미지정 시 RotorDiskArea(전 로터 합)에서 역산.</summary>
    public double PropDiameter { get; init; }
    /// <summary>
    /// 고정 팔 기체의 프롭 허브(표시용, 동체 좌표): 위치·회전축(추력 방향 단위벡터)·회전 방향(±1).
    /// 비어 있으면 접힘 암 오버레이(ArmCount·ArmLength)로 그린다.
    /// </summary>
    public IReadOnlyList<PropHub> Props { get; init; } = [];
    /// <summary>오버레이 암(막대·모터 원통)을 그릴지 — props 를 주면 기본 false(실모델 형상의 팔을 그대로 쓴다).</summary>
    public bool ArmsOverlay { get; init; } = true;

    /// <summary>암 전개율 0(접힘) ~ 1(펴짐).</summary>
    public double ArmDeploy(World w)
    {
        if (ArmsConfig is null) return 1;
        if (Owner.ActiveConfig != ArmsConfig) return 0;
        return ArmDeployTime > 0 ? Math.Clamp((w.T - Owner.ConfigTime) / ArmDeployTime, 0, 1) : 1;
    }

    /// <summary>off | stabilize | hold | goto | land</summary>
    public string Mode { get; set; } = "off";
    public Vec3? Target { get; set; }
    public double TargetAgl { get; set; } = double.NaN;

    public double Thrust { get; private set; }
    private double _thrustCmd;
    private Vec3 _upDesired = new(0, 0, -1);
    private double _yawDesired = double.NaN;
    public double VrsFactor { get; private set; } = 1;

    public bool Running => Mode != "off" && Owner.Root == Owner && Owner.Mode == BodyMode.Free;

    public override void Apply(World w, double t)
    {
        if (!Running) return;
        var b = Owner;
        var upBody = new Vec3(0, 0, -1);
        double T = Thrust * VrsFactor;
        w.ApplyForce(b, b.R, b.Q.Rotate(upBody) * T);
        // 자세 PD: 현재 위 축 → 원하는 위 축
        var u = b.Q.Rotate(upBody);
        var e = Vec3.Cross(u, _upDesired);
        var eb = b.Q.InverseRotate(e);
        double yawErr = 0;
        if (!double.IsNaN(_yawDesired)) yawErr = Quat.WrapPi(_yawDesired - b.Q.ToEuler().Z);
        var acc = new Vec3(AttKp * eb.X - AttKd * b.W.X, AttKp * eb.Y - AttKd * b.W.Y, 0.3 * AttKp * yawErr - AttKd * b.W.Z);
        w.ApplyMomentBody(b, b.Inertia * acc);
    }

    public override void PostStep(World w, double t, double dt)
    {
        if (!Running) { Thrust = 0; return; }
        var b = Owner;
        var env = w.Env;
        double g = env.Gravity;
        double agl = env.Agl(b.R.Z);
        if (Mode == "stabilize")
        {
            // 테더 매달림 중: 자세 수평 유지 + 아이들 추력(무게의 IdleRatio)
            _upDesired = new Vec3(0, 0, -1);
            if (double.IsNaN(_yawDesired)) _yawDesired = b.Q.ToEuler().Z;
            _thrustCmd = IdleRatio * b.Mass * g;
            Thrust += (_thrustCmd - Thrust) * (1 - Math.Exp(-dt / Tau));
            return;
        }
        // 수평 속도 명령
        var vCmd = Vec3.Zero;
        if (Mode is "goto" && Target is { } tg)
        {
            var d = new Vec3(tg.X - b.R.X, tg.Y - b.R.Y, 0);
            vCmd = d * KpPos;
            if (vCmd.Norm > CruiseSpeed) vCmd = vCmd.Normalized() * CruiseSpeed;
        }
        // 수직 속도 명령 (NED, +아래)
        double vzCmd = 0;
        if (Mode == "land") vzCmd = LandSpeed;
        else if (!double.IsNaN(TargetAgl)) vzCmd = Math.Clamp(-KpAlt * (TargetAgl - agl), -MaxClimb, MaxDescent);
        var aCmd = new Vec3(KpVel * (vCmd.X - b.V.X), KpVel * (vCmd.Y - b.V.Y), 1.5 * KpVel * (vzCmd - b.V.Z));
        // 원하는 추력 벡터 F = m(a − g)
        var fDes = (aCmd - env.GravityVector) * b.Mass;
        double vert = Math.Max(-fDes.Z, 0.2 * b.Mass * g);
        double hMax = vert * Math.Tan(MaxTiltDeg * Math.PI / 180);
        var fh = new Vec3(fDes.X, fDes.Y, 0);
        if (fh.Norm > hMax) fh = fh.Normalized() * hMax;
        var f = new Vec3(fh.X, fh.Y, -vert);
        _upDesired = f.Normalized();
        if (double.IsNaN(_yawDesired)) _yawDesired = b.Q.ToEuler().Z;
        // 추력 크기 = 원하는 벡터를 현재 위 축에 투영
        var up = b.Q.Rotate(new Vec3(0, 0, -1));
        _thrustCmd = Math.Clamp(Vec3.Dot(f, up) / Math.Max(Vec3.Dot(_upDesired, up), 0.3), 0, MaxThrust);
        Thrust += (_thrustCmd - Thrust) * (1 - Math.Exp(-dt / Tau));
        // VRS 추력 변화: x = 축방향 하강속도 / 정지 유도속도
        bool v2 = VrsModel == "uiuc-v2";
        if ((v2 || (VrsModel == "gaussian" && VrsDepth > 0)) && Thrust > 0)
        {
            double rho = env.Atmosphere.At(-b.R.Z).Density;
            double vh = Math.Sqrt(Thrust / (2 * rho * RotorDiskArea));
            double vAxialDown = Vec3.Dot(b.AirVelocity, -up);
            double x = vAxialDown / Math.Max(vh, 0.1);
            VrsFactor = v2
                ? RotorModel.DescentThrustRatio(Math.Min(Math.Max(x, 0), RotorModel.MaxValidDescentRatio), PropSolidity, PropCt0)
                : 1 - VrsDepth * Math.Exp(-Math.Pow((x - 0.9) / 0.35, 2));
        }
    }

    public override void Command(World w, string command, JsonObject args)
    {
        switch (command)
        {
            case "activate":
                if (Mode == "off") Mode = "hold";
                Thrust = Owner.Mass * w.Env.Gravity * 0.5;
                break;
            case "deactivate": Mode = "off"; break;
            case "set":
                foreach (var kv in args)
                {
                    switch (kv.Key)
                    {
                        case "mode": Mode = kv.Value!.GetValue<string>(); break;
                        case "target":
                            var a = kv.Value!.AsArray();
                            Target = new Vec3(a[0]!.GetValue<double>(), a[1]!.GetValue<double>(), 0);
                            break;
                        case "targetRelative":
                            var r = kv.Value!.AsArray();
                            Target = new Vec3(Owner.R.X + r[0]!.GetValue<double>(), Owner.R.Y + r[1]!.GetValue<double>(), 0);
                            break;
                        case "targetAgl": TargetAgl = kv.Value!.GetValue<double>(); break;
                        case "cruiseSpeed": CruiseSpeed = kv.Value!.GetValue<double>(); break;
                        case "landSpeed": LandSpeed = kv.Value!.GetValue<double>(); break;
                        case "idleRatio": IdleRatio = kv.Value!.GetValue<double>(); break;
                        case "type": case "device": break;
                        default: throw new InvalidOperationException($"multirotor 에 '{kv.Key}' 파라미터가 없습니다.");
                    }
                }
                break;
            default: base.Command(w, command, args); break;
        }
    }

    /// <summary>Visible = 작동 중, Fill = 추력비, Size = 암 전개율(0~1).</summary>
    public override DeviceSample Sample(World w) =>
        new(FullName, Type, Running, Thrust / MaxThrust, Owner.PointWorld(Owner.OwnCg), Owner.PointWorld(Owner.OwnCg), ArmDeploy(w), Thrust);

    public override void Summarize(JsonObject s)
    {
        s["type"] = Type;
        s["mode"] = Mode;
    }
}
