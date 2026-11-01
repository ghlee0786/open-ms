using Sim.Core.Environment;
using Sim.Core.Geometry;
using Sim.Core.Numerics;

namespace Sim.Core.Models;

public enum BodyMode { Stowed, Free, Landed, Kinematic }

public sealed class ImpactRecord
{
    public double Time { get; init; }
    public Vec3 Position { get; init; }
    public Vec3 Velocity { get; init; }
    public Vec3 EulerDeg { get; init; }
    /// <summary>동체 +X 축과 지면 하향 법선 사이 각도 [deg] (스파이크 선행 판정, FR-GND-03).</summary>
    public double NoseAngleDeg { get; init; }
    public double PenetrationDepth { get; init; }
    public double PenetrationG { get; init; }
    public double Mass { get; init; }
    /// <summary>충돌 순간 동체 +X 와 속도 벡터 사이 각(총받음각) [deg].</summary>
    public double AngleOfAttackDeg { get; init; }
    /// <summary>경사 충돌 시 연직 관입 깊이 = D·cos(궤적각).</summary>
    public double PenetrationVerticalDepth { get; init; }
    public double PenetrationArea { get; init; }
    public double PenetrationNoseN { get; init; }
    public string PenetrationSource { get; init; } = "";
}

/// <summary>규정 궤적(운반체, FR-ENV-06): 등속 직선.</summary>
public sealed class KinematicTrajectory
{
    public Vec3 Position0 { get; init; }
    public Vec3 Velocity { get; init; }
    public Quat Attitude { get; init; } = Quat.Identity;
    public Vec3 At(double t) => Position0 + Velocity * t;
}

/// <summary>
/// 6DOF 강체 요소(Body). 동체 기준좌표(BRF) = FRD 축, 원점 = 모델 원점.
/// 붙어 있는(Stowed) 자식 물체는 부모와 함께 움직이며 질량특성이 부모 합성값에 포함된다.
/// 상태(R, V)는 합성 CG 기준(NED), Q 는 동체→NED, W 는 동체 각속도.
/// </summary>
public sealed class Body
{
    public required string Name { get; init; }
    public int Index { get; set; }

    // 고유 질량특성 (BRF)
    public double OwnMass { get; set; }
    public Vec3 OwnCg { get; set; }
    public Mat3 OwnInertia { get; set; } = Mat3.Identity;
    public string MassSource { get; set; } = "scenario";
    /// <summary>massProps "real:" 참조 해석 결과(FR-AN-07): real | synthetic(합성 대체) | null(해당 없음).</summary>
    public string? ModelSource { get; set; }
    /// <summary>실모델 massprops 파일 전체 경로(ModelSource = real 일 때) — 같은 폴더의 STEP 을 FEA 에 쓴다(FR-AN-04).</summary>
    public string? ModelPath { get; set; }
    /// <summary>massProps 로 읽은 Fusion 내보내기(실모델·샘플 모두) — 사용자 파라미터·조인트 표시, USD 결과 출력용. 없으면 null.</summary>
    public MassPropsFile? MassProps { get; set; }

    /// <summary>마지막 switchConfig 구성 이름(FR-GEO-07)과 그 시각 [s] — 없으면 null(초기 구성). 암 전개 표시용.</summary>
    public string? ActiveConfig { get; set; }
    public double ConfigTime { get; set; }

    public Shape? Shape { get; set; }
    /// <summary>
    /// 순응 접촉 강성용 구조 이상화 기본 형상(동체 좌표): 시나리오 contactShape → 기본 형상 shape → aeroShape → 메시 경계 상자.
    /// ContactShapeSource = 어느 것을 썼는지(contactShape | shape | aeroShape | bbox).
    /// </summary>
    public Shape? ContactShape { get; set; }
    public string ContactShapeSource { get; set; } = "";
    /// <summary>모델(STL·STEP) 좌표 → 동체 좌표 회전(modelFrame·noseAxis). CAD 형상을 FEA 메시로 쓸 때 같은 변환을 건다(FR-FEA-03).</summary>
    public Mat3 ModelToBody { get; set; } = Mat3.Identity;
    public AeroModel? Aero { get; set; }
    public List<Device> Devices { get; } = new();
    /// <summary>관입 노즈 계수 N (Young). 미지정 시 0.56(평두).</summary>
    public double NoseCoefficient { get; set; } = 0.56;
    /// <summary>관입체 단면·노즈(FX-02). null 이면 충돌 방향 투영면적으로 대체(경고).</summary>
    public Penetrator? Penetrator { get; set; }

    /// <summary>
    /// 정적 여유(FX-03) = (x_cg − x_cp)/L, 동체 +X=기수. 양수면 CP 가 CG 뒤 → 기수 선행 정적 안정.
    /// CP 가 없으면(CG 작용) null.
    /// </summary>
    public double? StaticMargin
    {
        get
        {
            if (Aero?.Cp is not { } cp) return null;
            double l = Shape?.CharacteristicLength ?? Aero.RefLength;
            return l > 0 ? (OwnCg.X - cp.X) / l : null;
        }
    }
    public string Color { get; set; } = "#9aa4b1";

    // 계층
    public Body? Parent { get; set; }
    /// <summary>분리(World.Separate) 전 부모 — 기록 프레임의 Stowed 사슬을 실행 뒤에도 되짚기 위해(충격 응답의 탑재물 질량).</summary>
    public Body? FormerParent { get; set; }
    public Vec3 MountPos { get; set; }
    public Quat MountQ { get; set; } = Quat.Identity;
    public List<Body> Children { get; } = new();
    public BodyMode Mode { get; set; } = BodyMode.Free;
    public KinematicTrajectory? Trajectory { get; set; }

    // 합성 질량특성 (Free/Landed/Kinematic 루트에서 유효)
    public double Mass { get; private set; }
    public Vec3 Cg { get; private set; }
    public Mat3 Inertia { get; private set; } = Mat3.Identity;
    public Mat3 InvInertia { get; private set; } = Mat3.Identity;

    // 상태
    public Vec3 R, V, W;
    public Quat Q = Quat.Identity;

    // 평가 누산기·파생량
    internal Vec3 F, M;
    public Vec3 Accel;
    /// <summary>마지막 RK4 스텝의 시작·끝 단계 가속도(NED, 중력 포함) — 스텝 안 하중 이력 보간용(FR-AN-08 하중 경로). Accel 은 RK4 가중 평균.</summary>
    public Vec3 AccelStart, AccelEnd;
    public Vec3 AirVelocity;
    public DrydenTurbulence? Turbulence;
    public ImpactRecord? Impact { get; set; }
    public double ReleaseTime { get; set; } = double.NaN;
    /// <summary>자유비행 중 매 스텝 비중력 가속도 최대값 [G]·시각(출력 주기와 무관 — 짧은 충격 펄스 포착, FR-SHL-06).</summary>
    public double MaxLoadG { get; set; }
    public double MaxLoadTime { get; set; } = double.NaN;
    /// <summary>구조·재질 정의(FR-STR/FR-MAT). null 이면 재질 판정 없이 압력·근사 응력만.</summary>
    public Structures.StructureDef? Structure { get; set; }

    public bool IsRoot => Mode is BodyMode.Free or BodyMode.Landed or BodyMode.Kinematic;

    public Body Root
    {
        get
        {
            var b = this;
            while (b.Mode == BodyMode.Stowed && b.Parent != null) b = b.Parent;
            return b;
        }
    }

    /// <summary>이 물체의 BRF 를 루트 BRF 로: 원점 위치 offset, 회전 rot (this→root).</summary>
    public (Body Root, Vec3 Offset, Quat Rot) ChainToRoot()
    {
        var off = Vec3.Zero; var rot = Quat.Identity;
        var b = this;
        while (b.Mode == BodyMode.Stowed && b.Parent != null)
        {
            off = b.MountPos + b.MountQ.Rotate(off);
            rot = b.MountQ * rot;
            b = b.Parent;
        }
        return (b, off, rot);
    }

    public Vec3 ToRootBrf(Vec3 p)
    {
        var (_, off, rot) = ChainToRoot();
        return off + rot.Rotate(p);
    }

    /// <summary>BRF 점의 NED 위치.</summary>
    public Vec3 PointWorld(Vec3 p)
    {
        var (root, off, rot) = ChainToRoot();
        return root.R + root.Q.Rotate(off + rot.Rotate(p) - root.Cg);
    }

    /// <summary>NED 점 → 이 물체 BRF 좌표(PointWorld 의 역).</summary>
    public Vec3 WorldToBrf(Vec3 pw)
    {
        var (root, off, rot) = ChainToRoot();
        return rot.InverseRotate(root.Q.InverseRotate(pw - root.R) + root.Cg - off);
    }

    public Vec3 PointVelocity(Vec3 p)
    {
        var (root, off, rot) = ChainToRoot();
        var rb = off + rot.Rotate(p) - root.Cg;
        return root.V + root.Q.Rotate(Vec3.Cross(root.W, rb));
    }

    public Quat WorldQ
    {
        get { var (root, _, rot) = ChainToRoot(); return root.Q * rot; }
    }

    public Vec3 WorldOmegaBody
    {
        get { var (root, _, rot) = ChainToRoot(); return rot.InverseRotate(root.W); }
    }

    /// <summary>이 물체 고유 CG 의 NED 위치.</summary>
    public Vec3 OwnCgWorld => PointWorld(OwnCg);

    public IEnumerable<Body> SubtreeStowed()
    {
        foreach (var c in Children)
        {
            if (c.Mode != BodyMode.Stowed) continue;
            yield return c;
            foreach (var g in c.SubtreeStowed()) yield return g;
        }
    }

    /// <summary>
    /// 자신 + 붙어 있는 자손의 합성 질량특성 재계산(평행축). shiftState=true 이면 CG 이동만큼 상태를 옮겨
    /// 강체 운동을 그대로 유지한다.
    /// </summary>
    public void RecomputeComposite(bool shiftState)
    {
        double m = OwnMass;
        var first = OwnCg * OwnMass;
        var parts = new List<(double m, Vec3 cg, Mat3 I)> { (OwnMass, OwnCg, OwnInertia) };
        foreach (var d in SubtreeStowed())
        {
            var (_, off, rot) = d.ChainToRootFrom(this);
            var cg = off + rot.Rotate(d.OwnCg);
            var rm = rot.ToMatrix();
            parts.Add((d.OwnMass, cg, d.OwnInertia.RotatedBy(rm)));
            m += d.OwnMass;
            first += cg * d.OwnMass;
        }
        var newCg = m > 0 ? first / m : OwnCg;
        var I = Mat3.Zero;
        foreach (var (pm, pcg, pI) in parts) I = I + pI + Mat3.ParallelAxis(pm, pcg - newCg);
        if (shiftState && Mode != BodyMode.Kinematic)
        {
            var d = Q.Rotate(newCg - Cg);
            R += d;
            V += Vec3.Cross(Q.Rotate(W), d);
        }
        _contactCache = null;
        Mass = m; Cg = Mode == BodyMode.Kinematic ? Vec3.Zero : newCg; Inertia = I;
        InvInertia = m > 0 ? SafeInverse(I) : Mat3.Identity;
    }

    private static Mat3 SafeInverse(Mat3 i)
    {
        try { return i.Inverse(); }
        catch { return Mat3.Diagonal(1 / Math.Max(i.M11, 1e-9), 1 / Math.Max(i.M22, 1e-9), 1 / Math.Max(i.M33, 1e-9)); }
    }

    private (Body Root, Vec3 Offset, Quat Rot) ChainToRootFrom(Body ancestor)
    {
        var off = Vec3.Zero; var rot = Quat.Identity;
        var b = this;
        while (b != ancestor && b.Parent != null)
        {
            off = b.MountPos + b.MountQ.Rotate(off);
            rot = b.MountQ * rot;
            b = b.Parent;
        }
        return (b, off, rot);
    }

    private List<Vec3>? _contactCache;

    /// <summary>지면 접촉 대표점(루트 BRF): 자신과 붙어 있는 자손의 형상 대표점. 합성 재계산 때 무효화.</summary>
    public List<Vec3> ContactPointsRootBrf() => _contactCache ??= BuildContactPoints();

    private List<Vec3> BuildContactPoints()
    {
        var pts = new List<Vec3>();
        void Add(Body b)
        {
            if (b.Shape is null) { pts.Add(b == this ? b.OwnCg : b.ToRootBrfFrom(this, b.OwnCg)); return; }
            foreach (var p in b.Shape.SupportPoints()) pts.Add(b == this ? p : b.ToRootBrfFrom(this, p));
        }
        Add(this);
        foreach (var d in SubtreeStowed()) Add(d);
        return pts;
    }

    private Vec3 ToRootBrfFrom(Body ancestor, Vec3 p)
    {
        var (_, off, rot) = ChainToRootFrom(ancestor);
        return off + rot.Rotate(p);
    }

    public double BoundingRadius => Shape?.BoundingRadius ?? 0.1;

    public double AglOf(SimEnvironment env) => env.Agl(OwnCgWorld.Z);
}
