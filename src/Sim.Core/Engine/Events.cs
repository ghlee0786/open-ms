using System.Text.Json.Nodes;
using Sim.Core.Models;
using Sim.Core.Numerics;

namespace Sim.Core.Engine;

/// <summary>조건 요소(Trigger). StateDependent 이면 엔진이 스텝 내 이분 탐색으로 발생 시각을 찾는다.</summary>
public interface ITrigger
{
    bool Evaluate(World w);
    /// <summary>발생 시각을 미리 알 수 있으면 그 시각(스텝 맞춤용), 모르면 +∞.</summary>
    double NextTime(World w);
    bool StateDependent { get; }
}

/// <summary>동작 요소(Action).</summary>
public interface IAction
{
    void Execute(World w);
    string Describe();
}

/// <summary>이벤트 규칙: Trigger → Action 목록(1회 발생).</summary>
public sealed class SimEvent
{
    public required string Name { get; init; }
    public required ITrigger Trigger { get; init; }
    public List<IAction> Actions { get; } = new();
    public bool Fired { get; set; }
    public double FiredTime { get; set; } = double.NaN;
}

// ---------------- Triggers ----------------

public sealed class TimeTrigger(double t) : ITrigger
{
    public double Time { get; set; } = t;
    // 허용오차 1e-9: 시간 누적 반올림(0.9999999999 vs 1.0)으로 한 스텝 늦게 발생하던 문제 방지(스텝 맞춤 기준과 같은 값)
    public bool Evaluate(World w) => w.T >= Time - 1e-9;
    public double NextTime(World w) => Time;
    public bool StateDependent => false;
}

/// <summary>다른 이벤트 발생 후 지연(예: "박스 사출 후 1.2 s").</summary>
public sealed class AfterTrigger(string eventName, double delay) : ITrigger
{
    public string EventName { get; } = eventName;
    public double Delay { get; set; } = delay;
    public bool Evaluate(World w) => w.FiredEvents.TryGetValue(EventName, out var t0) && w.T >= t0 + Delay - 1e-9;
    public double NextTime(World w) => w.FiredEvents.TryGetValue(EventName, out var t0) ? t0 + Delay : double.PositiveInfinity;
    public bool StateDependent => false;
}

/// <summary>고도(AGL 기본 / MSL) 조건.</summary>
public sealed class AltitudeTrigger(Body body, double? below, double? above, bool msl) : ITrigger
{
    public double? Below { get; set; } = below;
    public double? Above { get; set; } = above;
    public bool Evaluate(World w)
    {
        if (body.Mode == BodyMode.Stowed && body.Root.Mode == BodyMode.Kinematic) return false;
        double z = body.OwnCgWorld.Z;
        double h = msl ? -z : w.Env.Agl(z);
        return (Below is null || h < Below) && (Above is null || h > Above);
    }
    public double NextTime(World w) => double.PositiveInfinity;
    public bool StateDependent => true;
}

/// <summary>대기속도 또는 지면속도 조건.</summary>
public sealed class SpeedTrigger(Body body, double? below, double? above, bool airspeed) : ITrigger
{
    public bool Evaluate(World w)
    {
        if (body.Root.Mode != BodyMode.Free) return false;
        var v = body.PointVelocity(body.OwnCg);
        double s = airspeed ? (v - w.WindAt(body.Root, body.OwnCgWorld)).Norm : v.Norm;
        return (below is null || s < below) && (above is null || s > above);
    }
    public double NextTime(World w) => double.PositiveInfinity;
    public bool StateDependent => true;
}

/// <summary>점 또는 다른 물체까지 거리 조건(수평 선택).</summary>
public sealed class DistanceTrigger(Body body, Vec3? point, Body? other, double? below, double? above, bool horizontal) : ITrigger
{
    public bool Evaluate(World w)
    {
        if (body.Root.Mode == BodyMode.Kinematic) return false;
        var p = body.OwnCgWorld;
        var q = other is not null ? other.OwnCgWorld : point ?? Vec3.Zero;
        var d = p - q;
        double dist = horizontal ? d.HorizontalNorm : d.Norm;
        return (below is null || dist < below) && (above is null || dist > above);
    }
    public double NextTime(World w) => double.PositiveInfinity;
    public bool StateDependent => true;
}

/// <summary>비중력 가속도(충격 감지) 조건 [G].</summary>
public sealed class AccelTrigger(Body body, double aboveG) : ITrigger
{
    public bool Evaluate(World w)
    {
        var r = body.Root;
        if (r.Mode != BodyMode.Free) return false;
        return (r.Accel - w.Env.GravityVector).Norm / w.Env.Gravity > aboveG;
    }
    public double NextTime(World w) => double.PositiveInfinity;
    public bool StateDependent => true;
}

/// <summary>멀티로터가 목표 수평 반경 안에 도착.</summary>
public sealed class ArrivedTrigger(Multirotor rotor, double radius) : ITrigger
{
    public bool Evaluate(World w)
    {
        if (rotor.Target is not { } t || rotor.Owner.Mode != BodyMode.Free) return false;
        var p = rotor.Owner.R;
        return new Vec3(p.X - t.X, p.Y - t.Y, 0).Norm < radius;
    }
    public double NextTime(World w) => double.PositiveInfinity;
    public bool StateDependent => true;
}

public sealed class TensionTrigger(Tether tether, double above) : ITrigger
{
    public bool Evaluate(World w) => tether.Tension > above;
    public double NextTime(World w) => double.PositiveInfinity;
    public bool StateDependent => false; // 장력은 스텝 후 갱신값 사용
}

public sealed class AndTrigger(IReadOnlyList<ITrigger> items) : ITrigger
{
    public bool Evaluate(World w) => items.All(i => i.Evaluate(w));
    public double NextTime(World w) => items.All(i => !i.StateDependent) ? items.Max(i => i.NextTime(w)) : double.PositiveInfinity;
    public bool StateDependent => items.Any(i => i.StateDependent);
}

public sealed class OrTrigger(IReadOnlyList<ITrigger> items) : ITrigger
{
    public bool Evaluate(World w) => items.Any(i => i.Evaluate(w));
    public double NextTime(World w) => items.Min(i => i.NextTime(w));
    public bool StateDependent => items.Any(i => i.StateDependent);
}

// ---------------- Actions ----------------

/// <summary>분리(Spawn 포함, FR-GEN-04): 붙어 있던 물체를 자유물체로. 상대속도는 부모 동체계.</summary>
public sealed class SeparateAction(Body body, Vec3 relVel, Vec3 relOmega) : IAction
{
    public Body Body { get; } = body;
    public Vec3 RelVel { get; set; } = relVel;
    public void Execute(World w) => w.Separate(Body, RelVel, relOmega, null);
    public string Describe() => $"separate {Body.Name} Δv={RelVel}";
}

public sealed class DeviceCommandAction(Device device, string command, JsonObject args) : IAction
{
    public void Execute(World w) => device.Command(w, command, args);
    public string Describe() => $"{command} {device.FullName}";
}

public sealed class ConnectorCommandAction(Connector connector, string command, JsonObject args) : IAction
{
    public void Execute(World w)
    {
        connector.Command(w, command, args);
        if (command is "release" or "deactivate") w.Log($"{connector.Name}.released", $"{connector.Name} 해제");
    }
    public string Describe() => $"{command} {connector.Name}";
}

/// <summary>질량특성 구성 교체(FR-GEO-07): 예) 암 전개.</summary>
public sealed class SwitchConfigAction(Body body, string config, double mass, Vec3 cg, Mat3 inertia) : IAction
{
    public void Execute(World w)
    {
        body.OwnMass = mass; body.OwnCg = cg; body.OwnInertia = inertia;
        body.ActiveConfig = config; body.ConfigTime = w.T;
        var root = body.Root;
        if (root.Mode == BodyMode.Free) root.RecomputeComposite(true);
        else root.RecomputeComposite(false);
    }
    public string Describe() => $"switchConfig {body.Name} → {config}";
}

public sealed class StopAction : IAction
{
    public void Execute(World w) => w.Stop("stop 액션");
    public string Describe() => "stop";
}

public sealed class LogAction(string message) : IAction
{
    public void Execute(World w) => w.Log("log", message);
    public string Describe() => $"log {message}";
}
