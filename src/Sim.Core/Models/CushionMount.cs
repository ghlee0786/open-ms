using System.Text.Json.Nodes;
using Sim.Core.Engine;
using Sim.Core.Numerics;
using Sim.Core.Structures;

namespace Sim.Core.Models;

/// <summary>
/// 폼 패드(압축 전용, FR-AN-08 ②): 탄성–평탄–치밀화 3구간 하중 곡선(Gibson &amp; Ashby, Cellular Solids 2판 5장 개념형)
///   σ(ε) = E·ε (ε ≤ σp/E), σp (≤ εD), σp + E·(ε − εD)/(1 − ε) (치밀화, ε → 1 에서 급증)
/// 하역은 최대 변형률 εmax 에서 기울기 E 로 내려간다(영구 압축·이력 에너지 흡수). 면적 A, 두께 h, 축(±x/±y/±z: 페이로드가 그 방향으로 움직이면 눌림).
/// </summary>
public sealed class FoamPad
{
    public required string Label { get; init; }
    public int Axis { get; init; }
    public int Sign { get; init; } = 1;
    public double Area { get; init; }
    public double Thickness { get; init; }
    public double Modulus { get; init; }
    public double Plateau { get; init; }
    public double Densification { get; init; } = ImpactDamage.FoamDensification;
    public string MaterialKey { get; init; } = "";
    public const double MaxStrain = 0.995;

    public double EpsMax { get; set; }
    public double MaxStrainSeen { get; private set; }
    public double MaxStress { get; private set; }
    public double Energy { get; private set; }
    public bool BottomedOut => MaxStrainSeen >= Densification;

    public double Stiffness0 => Modulus * Area / Thickness;
    public double YieldStrain => Plateau / Modulus;

    /// <summary>치밀화 식 σp + E(ε−εD)/(1−ε) 를 쓰는 상한 변형률. 그 위는 그 점의 접선(≈ 30E)으로 선형 연장 — 특이점(ε→1)에서 강성이 발산해
    /// 서브스텝이 따라가지 못하는 문제(실측 16,000 G 가짜 피크)를 막는다. 바닥 닿음 이후 G 는 이 강성에 좌우되므로 '신뢰 불가' 로 보고한다.</summary>
    public const double LockStrain = 0.9;

    public double Loading(double eps)
    {
        if (eps <= 0) return 0;
        eps = Math.Min(eps, MaxStrain);
        double ey = YieldStrain;
        if (eps <= ey) return Modulus * eps;
        if (eps <= Densification) return Plateau;
        double el = Math.Max(LockStrain, Densification + 1e-6);
        if (eps <= el) return Plateau + Modulus * (eps - Densification) / (1 - eps);
        double sl = Plateau + Modulus * (el - Densification) / (1 - el);
        double kl = Modulus * (1 - Densification) / ((1 - el) * (1 - el));
        return sl + kl * (eps - el);
    }

    /// <summary>
    /// 현재 이력(εmax)에서의 응력: 처녀 하중 곡선 위면 그대로, 아래면 하역 기울기 E_u = max(E, εmax 에서의 하중 곡선 접선)로 내려간다(0 이하 불가).
    /// 치밀화에서 E 로만 하역하면 하역선이 하중 곡선 위로 올라가 반발 때 에너지를 만들어 낸다(실측: 7 J 운동에너지에 2,000 J '흡수') — 접선 이상이면 소산만 한다.
    /// </summary>
    public double Stress(double eps)
    {
        if (eps <= 0) return 0;
        if (eps >= EpsMax) return Loading(eps);
        return Math.Max(0, Loading(EpsMax) - UnloadModulus(EpsMax) * (EpsMax - eps));
    }

    /// <summary>하중 곡선 접선 dσ/dε (응력 단위, 탄성·치밀화 구간; 평탄은 0).</summary>
    public double LoadingSlope(double eps)
    {
        if (eps <= YieldStrain) return Modulus;
        if (eps <= Densification) return 0;
        double el = Math.Max(LockStrain, Densification + 1e-6), e = Math.Min(eps, el);
        return Modulus * (1 - Densification) / ((1 - e) * (1 - e));
    }

    public double UnloadModulus(double epsMax) => Math.Max(Modulus, LoadingSlope(epsMax));

    /// <summary>접선 강성 [N/m] (서브스텝 크기 결정용, 보수적으로 E 이상).</summary>
    public double Tangent(double eps)
    {
        double e = Math.Clamp(eps, 0, Math.Max(LockStrain, Densification + 1e-6));
        double kt = Modulus;
        if (e > Densification) kt = Modulus * (1 - Densification) / ((1 - e) * (1 - e));
        return Math.Max(kt, Modulus) * Area / Thickness;
    }

    public double CompressionOf(Vec3 u) => Sign * u[Axis];

    internal void Commit(double eps, double dEps, double stress)
    {
        if (eps > EpsMax) EpsMax = Math.Min(eps, MaxStrain);
        if (eps > MaxStrainSeen) MaxStrainSeen = eps;
        if (stress > MaxStress) MaxStress = stress;
        Energy += stress * Area * Thickness * dEps;
    }

    public JsonObject ToJson() => new()
    {
        ["label"] = Label, ["axis"] = (Sign > 0 ? "+" : "-") + "xyz"[Axis], ["material"] = MaterialKey, ["area_m2"] = Area, ["thickness_m"] = Thickness,
        ["E_MPa"] = Modulus / 1e6, ["plateau_MPa"] = Plateau / 1e6, ["densification"] = Densification,
        ["maxStrain"] = MaxStrainSeen, ["maxStress_MPa"] = MaxStress / 1e6, ["bottomedOut"] = BottomedOut, ["energy_J"] = Energy,
        ["plateauForce_N"] = Plateau * Area,
    };
}

/// <summary>하중 경로 기록 표본(스텝마다). G 는 비중력 가속도 크기 / g0.</summary>
public readonly record struct MountSample(double T, double PayloadG, double BaseG, Vec3 PayloadSf, Vec3 BaseSf, bool InBox, double PadStrain, double PinForce, double[] Channels);

/// <summary>단계 정의: from 이벤트(+offset) ~ to 이벤트(+pad) 또는 from + duration.</summary>
public sealed record MountStage(string Label, string From, string? To, double Duration, double Offset, double Pad);

/// <summary>
/// 박스 내부 하중 경로(FR-AN-08 ②): 박스(소유 물체)에 수납된 페이로드(드론, 직계 자식)를 고정 핀(선형 강성·파단) + 폼 패드
/// (비선형 압축, <see cref="FoamPad"/>) + 감쇠 + 벽 간극으로 연결한 **2질량 상대운동** 모델.
/// 6DOF 는 결합체를 강체로 적분하고(페이로드가 수 mm 움직여도 외력·자세는 거의 같다 — 가정), 이 장치가 매 스텝 결합체의
/// 페이로드 위치 비중력 가속도 f(회전 항 α×r + ω×(ω×r) 포함, 박스 좌표)를 받아
///   μ·ü = R(u, u̇) − m₂·f,  μ = m₁m₂/(m₁+m₂),  m₁ = 루트 질량 − m₂,  m₂ = 페이로드(+자손) 질량
/// 을 서브스텝(ω·h ≤ 0.05, FR-NUM-03)으로 적분한다(반암시적 오일러, f 는 스텝 사이 선형 보간). 페이로드 G = |R|/m₂, 기저(박스) G = |M·f − R|/m₁.
/// 박스가 컨테이너에 실린 동안(루트 = 컨테이너)도 같은 식이 기저 가진이 된다. 첫 스텝에서 정적 평형(R = m₂f)으로 초기화해 출발 과도를 없앤다.
/// 페이로드가 분리되면 RecordAfter 동안 페이로드 자체 6DOF 가속도(테더 스냅·절단)를 계속 기록한다.
/// 채널(연결 장력·장치 힘)·부품 응력(<see cref="PartStressDef"/>)·단계 창은 분석(OpeningShock)이 쓴다.
/// </summary>
public sealed class CushionMount : Device
{
    public override string Type => "cushionMount";
    public required Body Payload { get; init; }
    public List<FoamPad> Pads { get; init; } = new();
    public int PinCount { get; init; }
    public double PinStiffness { get; init; }
    public double PinBreakLoad { get; init; } = double.PositiveInfinity;
    public bool[] PinAxes { get; init; } = [true, true, true];
    public double PinDampingRatio { get; init; } = 0.05;
    public string PinNote { get; init; } = "";
    public double PadDampingRatio { get; init; } = 0.1;
    public Vec3 WallGap { get; init; } = new(0.01, 0.01, 0.01);
    public double WallStiffness { get; init; } = 2e6;
    public double RecordAfter { get; init; } = 10;
    /// <summary>하중 채널: (이름, "connector"|"device", 참조). 연결·장치가 모두 만들어진 뒤 첫 스텝에서 해석한다.</summary>
    public List<(string Name, string Kind, string Ref)> Channels { get; init; } = new();
    private Func<double>[]? _channelFns;
    public List<PartStressDef> Parts { get; init; } = new();
    public List<MountStage> Stages { get; init; } = new();

    public List<MountSample> History { get; } = new();
    public bool PinsBroken { get; private set; }
    public double PinsBrokenTime { get; private set; } = double.NaN;
    public double MaxPinForce { get; private set; }
    public double SeparationTime { get; private set; } = double.NaN;
    public double MaxPayloadG { get; private set; }
    public double MaxPayloadGTime { get; private set; } = double.NaN;
    public double MaxPayloadGInBox { get; private set; }
    public double MaxBaseG { get; private set; }
    public Vec3 MaxDisplacement { get; private set; }
    public long Substeps { get; private set; }
    public double PayloadMass { get; private set; }
    /// <summary>소유 물체가 처음 독립(루트)이 된 순간의 결합체 질량 [kg] (박스+셸+드론+센서 — FEA 관성 완화용).</summary>
    public double AssemblyMass { get; private set; } = double.NaN;

    private Vec3 _u, _v, _wPrev;
    private Body? _rootPrev;
    private bool _init;

    private double Mu(double m1, double m2) => m1 * m2 / (m1 + m2);

    private (double M2, Vec3 CgRoot) PayloadMassRoot()
    {
        double m = Payload.OwnMass; var s = Payload.ToRootBrf(Payload.OwnCg) * Payload.OwnMass;
        foreach (var d in Payload.SubtreeStowed()) { m += d.OwnMass; s += d.ToRootBrf(d.OwnCg) * d.OwnMass; }
        return (m, s / m);
    }

    private double PinDamping(double mu) => PinCount > 0 ? 2 * PinDampingRatio * Math.Sqrt(PinCount * PinStiffness * mu) : 0;
    private double PadDamping(FoamPad p, double mu) => 2 * PadDampingRatio * Math.Sqrt(p.Stiffness0 * mu);
    private double WallDamping(double mu) => 2 * 0.2 * Math.Sqrt(WallStiffness * mu);

    private bool[]? _hasPad;
    private bool HasPad(int axis, int sign)
    {
        _hasPad ??= Enumerable.Range(0, 6).Select(i => Pads.Any(p => p.Axis == i / 2 && p.Sign == (i % 2 == 0 ? 1 : -1))).ToArray();
        return _hasPad[axis * 2 + (sign > 0 ? 0 : 1)];
    }
    private static readonly int[] Signs = [1, -1];

    /// <summary>페이로드에 걸리는 마운트 반력 R(u, u̇) (박스 좌표). 상태 변경 없음.</summary>
    public Vec3 Reaction(Vec3 u, Vec3 v, double mu, out double pinForce)
    {
        var r = Vec3.Zero;
        foreach (var p in Pads)
        {
            double d = p.CompressionOf(u);
            if (d <= 0) continue;
            double eps = d / p.Thickness;
            // 점성 감쇠는 이력 곡선 안쪽(ε < εmax: 하역·재부하)에서만 — 처녀 하중(ε ≥ εmax)의 에너지 흡수는 평탄 압괴 이력이 담당하고,
            // 접촉 순간 고속(수 m/s)에 탄성 강성 기준 감쇠를 더하면 하중 곡선 위로 튀는 가짜 하중이 생긴다
            // (실측: 두꺼운 압괴 패드에서 평탄 6.6 G 가 감쇠 때문에 10.5 G)
            double damp = PadDamping(p, mu) * p.Sign * v[p.Axis];
            if (damp > 0 && eps >= p.EpsMax - 1e-12) damp = 0;
            double fn = Math.Max(0, p.Stress(eps) * p.Area + damp);
            r = r.Add(p.Axis, -p.Sign * fn);
        }
        pinForce = 0;
        if (PinCount > 0 && !PinsBroken)
        {
            var fp = Vec3.Zero;
            double c = PinDamping(mu);
            for (int a = 0; a < 3; a++)
                if (PinAxes[a]) fp = fp.With(a, -(PinCount * PinStiffness * u[a] + c * v[a]));
            r += fp;
            pinForce = fp.Norm / PinCount;
        }
        for (int a = 0; a < 3; a++)
            foreach (int s in Signs)
            {
                if (HasPad(a, s)) continue;
                double pen = s * u[a] - WallGap[a];
                if (pen <= 0) continue;
                r = r.Add(a, -s * Math.Max(0, WallStiffness * pen + WallDamping(mu) * s * v[a]));
            }
        return r;
    }

    /// <summary>현재 상태 근처의 최대 축 접선 강성(서브스텝 크기용). 이번 스텝 안에 닿을 수 있는 패드·벽(간격 ≤ 2|v|dt + 1 mm)만 센다.</summary>
    private double TangentStiffness(Vec3 u, Vec3 v, double dt)
    {
        double k = 0;
        for (int a = 0; a < 3; a++)
        {
            double reach = 2 * Math.Abs(v[a]) * dt + 1e-3;
            double ka = PinCount > 0 && !PinsBroken && PinAxes[a] ? PinCount * PinStiffness : 0;
            foreach (var p in Pads)
                if (p.Axis == a && p.CompressionOf(u) > -reach) ka += p.Tangent(p.CompressionOf(u) / p.Thickness);
            if (Math.Abs(u[a]) > WallGap[a] - reach) ka += WallStiffness;
            k = Math.Max(k, ka);
        }
        return k;
    }

    /// <summary>정적 평형 R(u) = m₂·f 를 축별 이분법으로 풀어 초기 변위·패드 이력을 맞춘다.</summary>
    internal void InitStatic(Vec3 f, double m1, double m2)
    {
        double mu = Mu(m1, m2);
        var target = f * m2;
        var u = Vec3.Zero;
        for (int a = 0; a < 3; a++)
        {
            double lim = 0.05;
            foreach (var p in Pads.Where(p => p.Axis == a)) lim = Math.Max(lim, p.Thickness);
            lim += Math.Max(WallGap[a], 0) + 0.01;
            double lo = -lim, hi = lim;
            var u0 = u; int ax = a;
            double Ra(double x) => Reaction(u0.With(ax, x), Vec3.Zero, mu, out _)[ax];
            if (Math.Abs(Ra(0) - target[a]) <= 1e-9 * Math.Max(1, Math.Abs(target[a]))) continue;   // 무하중·간극 안: 중앙 유지
            // R 는 u 에 대해 비증가(복원력) → R(x) − target = 0
            for (int i = 0; i < 80; i++)
            {
                double mid = 0.5 * (lo + hi);
                if (Ra(mid) - target[a] > 0) lo = mid; else hi = mid;
            }
            u = u.With(a, 0.5 * (lo + hi));
        }
        _u = u; _v = Vec3.Zero;
        foreach (var p in Pads) { double e = Math.Max(0, p.CompressionOf(u) / p.Thickness); p.EpsMax = e; p.Commit(e, 0, p.Stress(e)); }
    }

    public override void PostStep(World w, double t, double dt)
    {
        var root = Owner.Root;
        bool inBox = Payload.Mode == BodyMode.Stowed && Payload.Parent == Owner;
        double g0 = Environment.Atmosphere.G0;
        if (inBox)
        {
            var (m2, cgRoot) = PayloadMassRoot();
            PayloadMass = m2;
            if (root == Owner && double.IsNaN(AssemblyMass)) AssemblyMass = root.Mass;
            double m1 = Math.Max(root.Mass - m2, 1e-6);
            double mu = Mu(m1, m2);
            // 스텝 시작·끝 비중력 가속도(RK4 1·4단계) + 회전 항(α 는 각속도 차분) → 박스 좌표
            var omega = root.Mode == BodyMode.Free ? root.W : Vec3.Zero;
            bool sameRoot = _rootPrev == root;
            var alpha = sameRoot && dt > 0 ? (omega - _wPrev) / dt : Vec3.Zero;
            var omega0 = sameRoot ? _wPrev : omega;
            var r = cgRoot - root.Cg;
            var (_, _, rotOwner) = Owner.ChainToRoot();
            bool free = root.Mode == BodyMode.Free;
            Vec3 Sf(Vec3 accel, Vec3 om) => rotOwner.InverseRotate(root.Q.InverseRotate((free ? accel : Vec3.Zero) - w.Env.GravityVector)
                + Vec3.Cross(alpha, r) + Vec3.Cross(om, Vec3.Cross(om, r)));
            var fStart = Sf(root.AccelStart, omega0);
            var f = Sf(root.AccelEnd, omega);
            _rootPrev = root; _wPrev = omega;
            if (!_init) { InitStatic(fStart, m1, m2); _init = true; }
            // 적응 서브스텝: 매 서브스텝 접선 강성으로 h ≤ 0.05/ω (치밀화·벽 접촉에서 자동으로 잘게)
            double pgMax = 0, bgMax = 0, pinMax = 0, tau = 0;
            Vec3 rLast = Vec3.Zero;
            int n = 0;
            while (tau < dt - 1e-15 && n < 200_000)
            {
                double omegaN = Math.Sqrt(TangentStiffness(_u, _v, dt - tau) / mu);
                double h = Math.Min(dt - tau, 0.05 / Math.Max(omegaN, 1e-9));
                var fk = fStart + (f - fStart) * ((tau + 0.5 * h) / dt);
                var R = Reaction(_u, _v, mu, out double pin);
                var acc = (R - fk * m2) / mu;
                _v += acc * h;
                var uOld = _u;
                _u += _v * h;
                tau += h; n++;
                foreach (var p in Pads)
                {
                    double e0 = p.CompressionOf(uOld) / p.Thickness, e1 = p.CompressionOf(_u) / p.Thickness;
                    if (e1 > 0 || e0 > 0) p.Commit(Math.Max(e1, 0), Math.Max(e1, 0) - Math.Max(e0, 0), p.Stress(Math.Max(e1, 0)));
                }
                pgMax = Math.Max(pgMax, R.Norm / (m2 * g0));
                bgMax = Math.Max(bgMax, (fk * (m1 + m2) - R).Norm / (m1 * g0));
                pinMax = Math.Max(pinMax, pin);
                rLast = R;
                if (!PinsBroken && PinCount > 0 && pin > PinBreakLoad)
                {
                    PinsBroken = true; PinsBrokenTime = t - dt + tau;
                    w.Log($"{FullName}.pinsBroken", $"{FullName}: 고정 핀 파단 (핀당 {pin:F0} N > {PinBreakLoad:F0} N)");
                }
            }
            Substeps += n;
            MaxPinForce = Math.Max(MaxPinForce, pinMax);
            if (pgMax > MaxPayloadGInBox) MaxPayloadGInBox = pgMax;
            if (_u.Norm > MaxDisplacement.Norm) MaxDisplacement = _u;
            var payloadSf = Payload.MountQ.InverseRotate(rLast / m2);
            var baseSf = (f * (m1 + m2) - rLast) / m1;
            Record(w, t, pgMax, bgMax, payloadSf, baseSf, true, Pads.Count > 0 ? Pads.Max(p => Math.Max(0, p.CompressionOf(_u) / p.Thickness)) : 0, pinMax);
            return;
        }
        if (double.IsNaN(SeparationTime))
        {
            if (!_init) return;   // 처음부터 박스 밖이면 기록 없음
            SeparationTime = t;
        }
        if (t > SeparationTime + RecordAfter) return;
        var pr = Payload.Root;
        var ps = pr.Mode == BodyMode.Free ? pr.Accel - w.Env.GravityVector : -w.Env.GravityVector;
        var bs = root.Mode == BodyMode.Free ? root.Accel - w.Env.GravityVector : -w.Env.GravityVector;
        if (pr.Mode == BodyMode.Landed) ps = -w.Env.GravityVector;
        Record(w, t, ps.Norm / g0, bs.Norm / g0, Payload.WorldQ.InverseRotate(ps), Owner.WorldQ.InverseRotate(bs), false, 0, 0);
    }

    private void Record(World w, double t, double pg, double bg, Vec3 psf, Vec3 bsf, bool inBox, double strain, double pin)
    {
        if (pg > MaxPayloadG) { MaxPayloadG = pg; MaxPayloadGTime = t; }
        MaxBaseG = Math.Max(MaxBaseG, bg);
        _channelFns ??= Channels.Select(c => ResolveChannel(w, c.Kind, c.Ref)).ToArray();
        var ch = new double[Channels.Count];
        for (int i = 0; i < ch.Length; i++) ch[i] = _channelFns[i]();
        History.Add(new MountSample(t, pg, bg, psf, bsf, inBox, strain, pin, ch));
    }

    /// <summary>부품 하중 채널 이름: 고정 핀 1개 하중.</summary>
    public const string PinChannel = "mount:pin";

    public override void Bind(World w)
    {
        _channelFns = Channels.Select(c => ResolveChannel(w, c.Kind, c.Ref)).ToArray();
        foreach (var p in Parts)
        {
            if (p.LoadChannel.StartsWith("inertia:", StringComparison.Ordinal) || p.LoadChannel == PinChannel) continue;
            foreach (var n in p.LoadChannel.Split('+'))
                if (Channels.All(c => c.Name != n))
                    throw new InvalidDataException($"{FullName}.parts.{p.Name}: 하중 채널 '{n}' 이 channels 에 없습니다.");
        }
        foreach (var s in Stages)
            foreach (var e in new[] { s.From, s.To })
                if (e is not null && !w.Events.Any(x => x.Name == e) && !e.Contains('.'))
                    throw new InvalidDataException($"{FullName}.stages '{s.Label}': 이벤트 '{e}' 가 시나리오에 없습니다.");
    }

    private Func<double> ResolveChannel(World w, string kind, string reference)
    {
        if (kind == "connector")
        {
            var c = w.Connectors.FirstOrDefault(x => x.Name == reference)
                    ?? throw new InvalidDataException($"{FullName}.channels: 연결 '{reference}' 이 없습니다.");
            return c switch
            {
                Tether t => () => t.Tension,
                Hinge h => () => h.Force,
                _ => throw new InvalidDataException($"{FullName}.channels: '{reference}'({c.Type}) 는 하중 채널이 될 수 없습니다(tether·breakCord·hinge)."),
            };
        }
        int i = reference.LastIndexOf('.');
        if (i <= 0) throw new InvalidDataException($"{FullName}.channels: 장치 참조는 'body.device' ('{reference}')");
        var dev = w.GetBody(reference[..i]).Devices.FirstOrDefault(d => d.Name == reference[(i + 1)..])
                  ?? throw new InvalidDataException($"{FullName}.channels: 장치 '{reference}' 가 없습니다.");
        return dev switch
        {
            Parachute p => () => p.Force,
            Pulse pu => () => pu.Owner.Root.Mass * Environment.Atmosphere.G0 * pu.PeakG * pu.Weight(w.T),
            Multirotor m => () => m.Thrust,
            _ => throw new InvalidDataException($"{FullName}.channels: '{reference}'({dev.Type}) 는 하중 채널이 될 수 없습니다."),
        };
    }

    /// <summary>부품 하중 [N] (표본 하나): 채널 값 또는 관성(질량 × 비중력 가속도, 축 성분 선택).</summary>
    public double PartLoad(PartStressDef p, in MountSample s)
    {
        if (p.LoadChannel.StartsWith("inertia:", StringComparison.Ordinal))
        {
            var sf = p.LoadChannel == "inertia:payload" ? s.PayloadSf : s.BaseSf;
            double a = p.InertiaAxis is { } ax ? Math.Abs(Vec3.Dot(sf, ax)) : sf.Norm;
            return p.InertiaMass * a;
        }
        if (p.LoadChannel == PinChannel) return s.PinForce;
        int i = Channels.FindIndex(c => c.Name == p.LoadChannel);
        if (i >= 0) return s.Channels[i];
        // 합 채널 "a+b": 같은 결속점에 차례로 걸리는 하중(브레이크 코드 → 낙하산)의 보수적 합
        double sum = 0;
        if (p.LoadChannel.Contains('+'))
            foreach (var n in p.LoadChannel.Split('+'))
            {
                int k = Channels.FindIndex(c => c.Name == n);
                if (k >= 0) sum += s.Channels[k];
            }
        return sum;
    }

    public override void Summarize(JsonObject s)
    {
        s["type"] = Type;
        s["payload"] = Payload.Name;
        s["payloadMass_kg"] = PayloadMass;
        s["maxPayloadG"] = MaxPayloadG;
        s["maxPayloadGTime_s"] = Parachute.Num(MaxPayloadGTime);
        s["maxPayloadGInBox"] = MaxPayloadGInBox;
        s["maxBaseG"] = MaxBaseG;
        s["maxDisplacement_m"] = Arr(MaxDisplacement);
        s["pins"] = PinCount > 0 ? new JsonObject
        {
            ["count"] = PinCount, ["stiffness_Npm"] = PinStiffness, ["breakLoad_N"] = double.IsFinite(PinBreakLoad) ? PinBreakLoad : null,
            ["axes"] = string.Concat(Enumerable.Range(0, 3).Where(a => PinAxes[a]).Select(a => "xyz"[a])),
            ["maxForcePerPin_N"] = MaxPinForce, ["broken"] = PinsBroken, ["brokenTime_s"] = Parachute.Num(PinsBrokenTime), ["note"] = PinNote,
        } : null;
        s["pads"] = new JsonArray(Pads.Select(p => (JsonNode)p.ToJson()).ToArray());
        s["separationTime_s"] = Parachute.Num(SeparationTime);
        s["samples"] = History.Count;
        s["substeps"] = Substeps;
    }

    private static JsonArray Arr(Vec3 v) => new(v.X, v.Y, v.Z);
}
