using Avalonia.Media;
using Sim.Core.Analysis;
using Sim.Core.Engine;
using Sim.Core.Geometry;
using Sim.Core.Models;
using Sim.Core.Numerics;

namespace Sim.App.Rendering;

/// <summary>렌더러가 읽는 불변 장면 스냅샷(시뮬레이션 결과에서 생성, UI 스레드에서 교체).</summary>
public sealed class SceneData
{
    public required World World { get; init; }
    public required IReadOnlyList<Frame> Frames { get; init; }
    /// <summary>물체별 평면 음영용 메시(BRF): [x,y,z,nx,ny,nz]* (float).</summary>
    public required float[][] Meshes { get; init; }
    /// <summary>물체별 표시 메시(동체 좌표) — 응력·압력 면 색(StressModel)과 면 순서가 같다.</summary>
    public required TriMesh?[] BodyMeshes { get; init; }
    public required (float R, float G, float B)[] Colors { get; init; }
    /// <summary>궤적 기준 원점(NED) — 궤적 정점은 이 점 기준 GL 좌표(float).</summary>
    public Vec3 Origin { get; init; }
    public required float[][] Trajectories { get; init; }
    public double GroundAltitude { get; init; }
    public Vec3 WindAtGround { get; init; }
    /// <summary>시나리오 토양 이름(environment.soil) — 장면 환경 '자동' 이 지면 프리셋을 고른다.</summary>
    public string? Soil { get; init; }
    public int Version { get; init; }
    /// <summary>자유 물체 궤적 경계(NED) — 전체 보기.</summary>
    public Vec3 BoundsMin { get; init; }
    public Vec3 BoundsMax { get; init; }
    /// <summary>멀티로터 암·프롭 표시(FR-SEP-04).</summary>
    public IReadOnlyList<RotorVisual> Rotors { get; init; } = [];

    private static int _version;

    public static SceneData Build(World w)
    {
        var frames = w.Recorder.Frames;
        var meshes = new float[w.Bodies.Count][];
        var bodyMeshes = new TriMesh?[w.Bodies.Count];
        var colors = new (float, float, float)[w.Bodies.Count];
        for (int i = 0; i < w.Bodies.Count; i++)
        {
            var b = w.Bodies[i];
            // 형상 없는 운반체(기준 궤적)는 그리지 않는다(빈 메시)
            if (b.Shape is null && b.Mode == BodyMode.Kinematic) { meshes[i] = []; colors[i] = ParseColor(b.Color); continue; }
            var mesh = b.Shape is { } s ? Sim.Core.Structures.StressModel.DisplayMesh(s) : TriMesh.Sphere(0.08, b.OwnCg, 8, 12);
            if (b.Shape is not null) bodyMeshes[i] = mesh;
            meshes[i] = Flatten(mesh);
            colors[i] = ParseColor(b.Color);
        }
        var origin = frames.Count > 0 ? frames[0].Bodies.Where((_, i) => w.Bodies[i].Mode != BodyMode.Kinematic)
            .Select(s => s.Cg).DefaultIfEmpty(Vec3.Zero).First() : Vec3.Zero;
        origin = new Vec3(origin.X, origin.Y, -w.Env.GroundAltitude);
        var traj = new float[w.Bodies.Count][];
        for (int i = 0; i < w.Bodies.Count; i++)
        {
            var list = new float[frames.Count * 3];
            for (int f = 0; f < frames.Count; f++)
            {
                var g = Sim.Core.Geometry.Frames.ToGl(frames[f].Bodies[i].Cg - origin);
                list[3 * f] = (float)g.X; list[3 * f + 1] = (float)g.Y; list[3 * f + 2] = (float)g.Z;
            }
            traj[i] = list;
        }
        // 전체 보기용 경계(자유 물체 궤적 — 운반체는 수 km 직선이라 제외, 없으면 전부)
        var min = new Vec3(double.MaxValue, double.MaxValue, double.MaxValue);
        var max = new Vec3(double.MinValue, double.MinValue, double.MinValue);
        bool any = w.Bodies.Any(b => b.Mode != BodyMode.Kinematic);
        foreach (var f in frames)
            for (int i = 0; i < f.Bodies.Length; i++)
            {
                if (any && w.Bodies[i].Mode == BodyMode.Kinematic) continue;
                var p = f.Bodies[i].Cg;
                min = new Vec3(Math.Min(min.X, p.X), Math.Min(min.Y, p.Y), Math.Min(min.Z, p.Z));
                max = new Vec3(Math.Max(max.X, p.X), Math.Max(max.Y, p.Y), Math.Max(max.Z, p.Z));
            }
        if (frames.Count == 0) { min = Vec3.Zero; max = Vec3.Zero; }
        var rotors = new List<RotorVisual>();
        int di = 0;
        for (int i = 0; i < w.Bodies.Count; i++)
            foreach (var d in w.Bodies[i].Devices)
            {
                if (d is Multirotor m) rotors.Add(RotorVisual.Build(m, i, di, bodyMeshes[i], frames));
                di++;
            }
        return new SceneData
        {
            BoundsMin = min, BoundsMax = max, Rotors = rotors,
            World = w, Frames = frames, Meshes = meshes, BodyMeshes = bodyMeshes, Colors = colors, Origin = origin, Trajectories = traj,
            GroundAltitude = w.Env.GroundAltitude, WindAtGround = w.Env.Wind.At(10), Soil = w.Env.Soil.Name,
            Version = Interlocked.Increment(ref _version),
        };
    }

    public static float[] Flatten(TriMesh m)
    {
        var data = new float[m.TriangleCount * 18];
        for (int t = 0; t < m.TriangleCount; t++)
        {
            var a = m.Vertices[3 * t]; var b = m.Vertices[3 * t + 1]; var c = m.Vertices[3 * t + 2];
            var n = Vec3.Cross(b - a, c - a).Normalized();
            int o = t * 18;
            foreach (var v in new[] { a, b, c })
            {
                data[o++] = (float)v.X; data[o++] = (float)v.Y; data[o++] = (float)v.Z;
                data[o++] = (float)n.X; data[o++] = (float)n.Y; data[o++] = (float)n.Z;
            }
        }
        return data;
    }

    public static (float, float, float) ParseColor(string hex)
    {
        try
        {
            var c = Color.Parse(hex);
            return (c.R / 255f, c.G / 255f, c.B / 255f);
        }
        catch { return (0.6f, 0.65f, 0.7f); }
    }
}

/// <summary>
/// 멀티로터 암·프롭 표시 기하(동체 좌표, 추력 = 동체 −Z) — FR-SEP-04 접힘 암 전개 · 프롭 회전.
/// 힌지 = 형상 바운딩박스 윗면(최소 z) 둘레, 접힘 = 힌지에서 아래(+Z)로 늘어짐, 전개 = 수평 방사(4개면 X 배치 45°+90°k).
/// 고정 팔 기체(multirotor props)는 암 없이 지정 허브에서 프롭만 그린다.
/// </summary>
public sealed class RotorVisual
{
    /// <summary>표시용 프롭 회전수 [rev/s] — 실제 수천 rpm 은 화면 갱신에서 앨리어싱되므로 추력비에 따라 이 범위로 늦춘다.</summary>
    public const double IdleRevs = 3, FullRevs = 8;

    public required int Body { get; init; }
    /// <summary>Frame.Devices 인덱스.</summary>
    public required int Device { get; init; }
    public required int ArmCount { get; init; }
    /// <summary>x·y = 형상 중심, z = 힌지 높이(형상 윗면).</summary>
    public required Vec3 Hub { get; init; }
    public required double HingeRadius { get; init; }
    public required double ArmLength { get; init; }
    public required double PropRadius { get; init; }
    /// <summary>기록에 암 전개율이 있는지 — 없으면(옛 결과 파일) 작동 중 = 펴짐으로 본다.</summary>
    public required bool HasArmRecord { get; init; }
    /// <summary>프레임별 누적 프롭 표시 각 [rad] (작동 중에만 증가).</summary>
    public required float[] PropAngle { get; init; }
    /// <summary>고정 팔 프롭 허브(동체 좌표) — 비어 있지 않으면 암 전개 대신 이 허브에서 프롭만 회전 표시.</summary>
    public PropHub[] Props { get; init; } = [];
    /// <summary>오버레이 암 막대·모터 원통을 그릴지.</summary>
    public bool ArmsOverlay { get; init; } = true;

    /// <summary>허브 프롭의 블레이드 기준 방향·옆(축에 수직인 두 단위벡터, 옆 = 축×기준).</summary>
    public static (Vec3 Dir, Vec3 Side) PropPlane(Vec3 axis)
    {
        var a = Math.Abs(axis.X) < 0.9 ? Vec3.UnitX : Vec3.UnitY;
        var dir = (a - axis * Vec3.Dot(a, axis)).Normalized();
        return (dir, Vec3.Cross(axis, dir));
    }

    public double ArmDeploy(DeviceSample d) => HasArmRecord ? Math.Clamp(d.Size, 0, 1) : d.Visible ? 1 : 0;

    /// <summary>암 k 의 힌지점·암 방향·옆(=Up×Dir)·모터 축(동체 좌표). deploy 0 = 아래로 접힘, 1 = 수평 방사, 사이는 힌지 접선축 회전.</summary>
    public (Vec3 Hinge, Vec3 Dir, Vec3 Side, Vec3 Up) Arm(int k, double deploy)
    {
        double phi = Math.PI / ArmCount + 2 * Math.PI * k / ArmCount;
        var e = new Vec3(Math.Cos(phi), Math.Sin(phi), 0);
        var down = new Vec3(0, 0, 1);
        double al = Math.Clamp(deploy, 0, 1) * Math.PI / 2;
        var dir = down * Math.Cos(al) + e * Math.Sin(al);
        var up = e * Math.Cos(al) - down * Math.Sin(al);
        return (Hub + e * HingeRadius, dir, Vec3.Cross(up, dir), up);
    }

    public static RotorVisual Build(Multirotor m, int body, int device, TriMesh? mesh, IReadOnlyList<Frame> frames)
    {
        var half = new Vec3(0.1, 0.1, 0.1);
        var (lo, hi) = mesh is { TriangleCount: > 0 } ? mesh.Bounds() : (m.Owner.OwnCg - half, m.Owner.OwnCg + half);
        var size = hi - lo;
        int n = Math.Max(1, m.ArmCount);
        var angle = new float[frames.Count];
        bool hasArm = false;
        double a = 0;
        for (int f = 0; f < frames.Count; f++)
        {
            angle[f] = (float)a;
            if (device >= frames[f].Devices.Length) continue;
            var d = frames[f].Devices[device];
            if (d.Size > 0) hasArm = true;
            if (d.Visible && f + 1 < frames.Count)
            {
                double revs = IdleRevs + (FullRevs - IdleRevs) * Math.Sqrt(Math.Clamp(d.Fill, 0, 1));
                a = (a + 2 * Math.PI * revs * (frames[f + 1].T - frames[f].T)) % (2 * Math.PI);
            }
        }
        return new RotorVisual
        {
            Body = body, Device = device, ArmCount = n,
            Hub = new Vec3((lo.X + hi.X) / 2, (lo.Y + hi.Y) / 2, lo.Z),
            HingeRadius = Math.Min(size.X, size.Y) / 2,
            ArmLength = m.ArmLength > 0 ? m.ArmLength : Math.Clamp(0.8 * size.Z, 0.08, 1.0),
            PropRadius = m.PropDiameter > 0 ? m.PropDiameter / 2 : Math.Sqrt(m.RotorDiskArea / (n * Math.PI)),
            HasArmRecord = hasArm, PropAngle = angle,
            Props = [.. m.Props], ArmsOverlay = m.ArmsOverlay,
        };
    }
}

/// <summary>MC 착탄 산포 오버레이.</summary>
public sealed class McOverlay
{
    public required (double N, double E)[] Points { get; init; }
    public required ImpactStats Stats { get; init; }
    public double GroundAltitude { get; init; }
    public string Label { get; init; } = "";

    /// <summary>결과 파일(mc.json) 저장용.</summary>
    public System.Text.Json.Nodes.JsonObject ToJson() => new()
    {
        ["label"] = Label, ["groundAltitude"] = GroundAltitude,
        ["points"] = new System.Text.Json.Nodes.JsonArray(Points.Select(p => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonArray(p.N, p.E)).ToArray()),
        ["stats"] = Stats.ToJson(),
    };

    public static McOverlay FromJson(System.Text.Json.Nodes.JsonObject o)
    {
        var pts = (o["points"] as System.Text.Json.Nodes.JsonArray ?? new()).Select(p => { var a = Sim.Core.Scenario.J.Arr(p!); return (a[0], a[1]); }).ToArray();
        return new McOverlay
        {
            Points = pts, Stats = ImpactStats.Compute(pts.ToList()), Label = Sim.Core.Scenario.J.Str(o, "label", "MC"),
            GroundAltitude = Sim.Core.Scenario.J.Num(o, "groundAltitude", 0),
        };
    }
}
