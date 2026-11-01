using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Sim.Core.Engine;
using Sim.Core.Environment;
using Sim.Core.Geometry;
using Sim.Core.Models;
using Sim.Core.Numerics;

namespace Sim.Core.Scenario;

/// <summary>시나리오 문서 + 기준 폴더(상대 경로 해석용).</summary>
public sealed class ScenarioDoc
{
    public required JsonObject Root { get; init; }
    public required string BaseDir { get; init; }
    public string? FilePath { get; init; }
    public string Name => J.Str(Root, "name", FilePath is null ? "scenario" : Path.GetFileNameWithoutExtension(FilePath));

    /// <summary>보고서 표기용 경로: 저장소(MnS.slnx) 기준 상대 경로, 파일이 없으면 config/scenarios/&lt;name&gt;.json.</summary>
    public string DisplayPath
    {
        get
        {
            if (FilePath is null) return $"config/scenarios/{Name}.json";
            for (var d = new DirectoryInfo(Path.GetDirectoryName(FilePath)!); d != null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "MnS.slnx"))) return Path.GetRelativePath(d.FullName, FilePath).Replace('\\', '/');
            return Path.GetFileName(FilePath);
        }
    }

    public static ScenarioDoc Load(string path)
    {
        var full = Path.GetFullPath(path);
        return new ScenarioDoc { Root = J.ParseObject(File.ReadAllText(full)), BaseDir = Path.GetDirectoryName(full)!, FilePath = full };
    }

    public static ScenarioDoc Parse(string json, string baseDir) => new() { Root = J.ParseObject(json), BaseDir = baseDir };

    public ScenarioDoc Clone() => new() { Root = Root.DeepClone().AsObject(), BaseDir = BaseDir, FilePath = FilePath };

    public string ToJson() => Root.ToJsonString(J.Pretty);

    public string Hash()
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(Root.ToJsonString()));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    public World Build() => ScenarioLoader.Build(this);
}

/// <summary>
/// 범용 시나리오(JSON, schema mns.scenario/1) → World. 엔진 코어는 과제 고유 개념을 모른다(FR-GEN-01/02).
/// 모든 점 좌표는 동체 기준좌표(FRD, 원점=모델 원점, m). 위치는 NED(m, 원점 해발 0).
/// </summary>
public static class ScenarioLoader
{
    public const string Schema = "mns.scenario/1";

    public static World Build(ScenarioDoc doc)
    {
        var root = doc.Root;
        var schema = J.Str(root, "schema", Schema);
        if (schema != Schema) throw new InvalidDataException($"지원하지 않는 schema: {schema} (필요: {Schema})");

        var env = BuildEnvironment(root["environment"]?.AsObject() ?? new JsonObject());
        var world = new World(env);
        if (root["sim"] is JsonObject sim)
        {
            world.Dt = J.Num(sim, "dt", 0.001);
            world.TEnd = J.Num(sim, "tEnd", 600);
            world.OutputInterval = 1.0 / J.Num(sim, "outputRate", 50);
            world.Seed = (ulong)J.Num(sim, "seed", 1);
            world.EventTolerance = J.Num(sim, "eventTolerance", 1e-4);
            world.EventBurst = Math.Max(0, J.Num(sim, "eventBurst", 0));
            if (sim["shockCapture"] is JsonObject sc)
            {
                world.ShockCaptureG = Math.Max(0, J.Num(sc, "dG", 2));
                world.ShockCaptureHold = Math.Max(0, J.Num(sc, "hold", 0.3));
            }
        }

        var bodyDefs = new Dictionary<string, JsonObject>();
        var ctx = new BuildContext(world, doc.BaseDir, bodyDefs);
        ConfigureRealModels(root["realModels"] as JsonObject, ctx);
        if (root["massScales"] is JsonObject mss)
            foreach (var (k, v) in mss)
                ctx.MassScales[k] = v is null ? 1.0 : J.ToDouble(v);

        // 운반체(Carrier): 규정 궤적 물체
        if (root["carrier"] is JsonObject car)
        {
            var name = J.Str(car, "name", "carrier");
            var cb = new Body { Name = name, Mode = BodyMode.Kinematic, OwnMass = 1, OwnInertia = Mat3.Identity, Color = J.Str(car, "color", "#5d6b7a") };
            double heading = J.Num(car, "heading", 0) * Math.PI / 180;
            double speed = J.Num(car, "speed", 0);
            var pos = J.V3Opt(car, "position") ?? new Vec3(0, 0, -J.Num(car, "altitude", 1000) - env.GroundAltitude * (J.Bool(car, "altitudeIsMsl", false) ? 0 : 1));
            var vel = J.V3Opt(car, "velocity") ?? new Vec3(speed * Math.Cos(heading), speed * Math.Sin(heading), 0);
            var att = Quat.FromEuler(0, J.Num(car, "pitch", 0) * Math.PI / 180, heading);
            cb.Trajectory = new KinematicTrajectory { Position0 = pos, Velocity = vel, Attitude = att };
            if (car["shape"] is JsonObject cs) cb.Shape = BuildShape(cs, ctx, null, Mat3.Identity);
            world.AddBody(cb);
        }

        var bodies = root["bodies"]?.AsArray() ?? throw new InvalidDataException("bodies 가 필요합니다.");
        foreach (var n in bodies)
        {
            var d = n!.AsObject();
            var b = BuildBody(d, ctx);
            bodyDefs[b.Name] = d;
            world.AddBody(b);
        }
        // 계층 연결
        foreach (var (name, d) in bodyDefs)
        {
            var b = world.GetBody(name);
            if (J.StrOpt(d, "parent") is { } p)
            {
                var parent = world.GetBody(p);
                b.Parent = parent; parent.Children.Add(b); b.Mode = BodyMode.Stowed;
                switch (J.Str(d, "mountFrame", "body"))
                {
                    case "body":
                        b.MountPos = J.V3(d, "mount", Vec3.Zero);
                        b.MountQ = EulerDeg(J.V3(d, "mountAttitude", Vec3.Zero));
                        break;
                    case "model":
                        // 같은 CAD 조립 좌표(Fusion 등)에서 내보낸 그룹끼리: 자식 동체 → 부모 동체 회전 = P·Cᵀ,
                        // 자식 모델 원점 = 부모 모델 원점 + assemblyOffset(모델 좌표, m) (FR-AN-01 실모델 조립)
                        var rot = parent.ModelToBody * b.ModelToBody.Transpose();
                        b.MountQ = Quat.FromMatrix(rot).Normalized();
                        b.MountPos = parent.ModelToBody * J.V3(d, "assemblyOffset", Vec3.Zero);
                        break;
                    case var mf: throw new InvalidDataException($"{name}.mountFrame: body | model ('{mf}')");
                }
            }
        }
        foreach (var b in world.Bodies)
        {
            var guard = new HashSet<Body>();
            for (var x = b; x != null; x = x.Parent)
                if (!guard.Add(x)) throw new InvalidDataException($"부모 관계에 순환이 있습니다: {b.Name}");
        }
        // 장치 (부모 연결 후)
        foreach (var (name, d) in bodyDefs)
        {
            var b = world.GetBody(name);
            if (d["devices"] is JsonArray devs)
                foreach (var dn in devs) b.Devices.Add(ElementRegistry.CreateDevice(dn!.AsObject(), b, ctx));
        }
        // 초기 상태: 자유물체
        foreach (var b in world.Bodies.Where(b => b.IsRoot)) b.RecomputeComposite(false);
        foreach (var (name, d) in bodyDefs)
        {
            var b = world.GetBody(name);
            if (b.Mode != BodyMode.Free) continue;
            var att = EulerDeg(J.V3(d, "attitude", Vec3.Zero));
            var origin = J.V3Opt(d, "position") ?? new Vec3(0, 0, -(J.NumOpt(d, "altitude") ?? throw new InvalidDataException($"{name}: parent 또는 position/altitude 가 필요합니다.")) - env.GroundAltitude);
            b.Q = att;
            b.R = origin + att.Rotate(b.Cg);
            b.V = J.V3(d, "velocity", Vec3.Zero);
            b.W = J.V3(d, "omega", Vec3.Zero);
            b.ReleaseTime = 0;
        }

        if (root["connectors"] is JsonArray cons)
            foreach (var cn in cons) world.Connectors.Add(ElementRegistry.CreateConnector(cn!.AsObject(), ctx));
        world.Collisions = CollisionModel.FromJson(root["collisions"] as JsonObject);
        if (world.Collisions?.Bodies is { } colBodies)
            foreach (var cn in colBodies) world.GetBody(cn);

        if (root["events"] is JsonArray evs)
        {
            int i = 0;
            foreach (var en in evs)
            {
                var e = en!.AsObject();
                var ev = new SimEvent
                {
                    Name = J.Str(e, "name", $"event{i}"),
                    Trigger = ElementRegistry.CreateTrigger(e["when"]?.AsObject() ?? throw new InvalidDataException("event.when 필요"), ctx),
                };
                if (e["do"] is JsonArray acts)
                    foreach (var a in acts) ev.Actions.Add(ElementRegistry.CreateAction(a!.AsObject(), ctx));
                world.Events.Add(ev);
                i++;
            }
        }
        foreach (var b in world.Bodies)
            foreach (var d in b.Devices) d.Bind(world);
        world.Initialize();
        return world;
    }

    public static Quat EulerDeg(Vec3 e) => Quat.FromEuler(e.X * Math.PI / 180, e.Y * Math.PI / 180, e.Z * Math.PI / 180);

    public static SimEnvironment BuildEnvironment(JsonObject e)
    {
        var w = e["wind"] as JsonObject ?? new JsonObject();
        List<WindLayer>? profile = null;
        if (w["profile"] is JsonArray pa)
            profile = pa.Select(x => { var a = J.Arr(x!); return new WindLayer(a[0], a[1], a[2]); }).OrderBy(l => l.AltitudeAgl).ToList();
        var soilName = J.Str(e, "soil", "farmland");
        var earth = e["earth"] as JsonObject ?? new JsonObject();
        return new SimEnvironment
        {
            Earth = new EarthModel
            {
                Gravity = J.Str(earth, "gravity", "constant"),
                LatitudeDeg = J.Num(earth, "latitude", 0),
                Rotation = J.Bool(earth, "rotation", false),
                RoundRadius = J.Num(earth, "radius", EarthModel.A),
            },
            Atmosphere = new Atmosphere
            {
                TemperatureOffset = J.Num(e, "temperatureOffset", 0),
                Geopotential = J.Bool(e, "geopotentialAltitude", false),
                Vacuum = J.Bool(e, "vacuum", false),
                ConstantDensity = J.NumOpt(e, "constantDensity"),
            },
            Wind = new MeanWind
            {
                Speed = J.Num(w, "speed", 0),
                DirectionFromDeg = J.Num(w, "directionFrom", 270),
                ReferenceHeight = J.Num(w, "refHeight", 10),
                Shear = J.Str(w, "shear", "log"),
                Roughness = J.Num(w, "roughness", 0.03),
                PowerExponent = J.Num(w, "powerExponent", 1.0 / 7),
                ShearTop = J.Num(w, "shearTop", 300),
                Profile = profile,
            },
            Turbulence = J.Str(e, "turbulence", "none"),
            GroundAltitude = J.Num(e, "groundAltitude", 0),
            Gravity = J.Num(e, "gravity", Atmosphere.G0),
            Soil = SimEnvironment.Soils.TryGetValue(soilName, out var s) ? s : new Soil(soilName, J.Num(e, "soilS", 8)),
        };
    }

    public static Mat3 ParseInertia(JsonNode n)
    {
        if (n is JsonArray a && a.Count > 0 && a[0] is JsonArray) return J.Mat(n);
        var v = J.Arr(n);
        return v.Length switch
        {
            3 => Mat3.Diagonal(v[0], v[1], v[2]),
            6 => Mat3.Inertia(v[0], v[1], v[2], v[3], v[4], v[5]),
            9 => J.Mat(n),
            _ => throw new InvalidDataException("inertia 는 [Ixx,Iyy,Izz] / [Ixx,Iyy,Izz,Ixy,Ixz,Iyz] / 3×3 이어야 합니다.")
        };
    }

    private static Mat3 ModelToBody(JsonObject d)
    {
        Mat3 m2b;
        if (d["modelToBody"] is JsonNode m) m2b = J.Mat(m);
        else
            m2b = J.Str(d, "modelFrame", "body") switch
            {
                "fusion" => Frames.FusionToBody,
                "body" => Mat3.Identity,
                var f => throw new InvalidDataException($"알 수 없는 modelFrame: {f}")
            };
        // FX-01: 모델 좌표의 기수축을 동체 +X 로
        if (J.StrOpt(d, "noseAxis") is { } nose) m2b = NoseGeometry.WithNose(m2b, nose);
        return m2b;
    }

    /// <summary>
    /// 관입체(FX-02): penetrator {diameter|area, nose: flat|cone|ogive, lOverD|noseLength, n} 또는 형상 기수 끝 단면 추정.
    /// noseCoefficient(구 키)는 N 직접 지정으로 계속 받는다.
    /// </summary>
    private static Penetrator? BuildPenetrator(JsonObject d, Shape? shape)
    {
        double? nOld = J.NumOpt(d, "noseCoefficient");
        if (d["penetrator"] is JsonObject p)
        {
            double area = J.NumOpt(p, "area") ?? (J.NumOpt(p, "diameter") is { } dia ? Math.PI / 4 * dia * dia
                : throw new InvalidDataException("penetrator: diameter 또는 area 가 필요합니다."));
            double dEq = Math.Sqrt(4 * area / Math.PI);
            double n = J.NumOpt(p, "n") ?? nOld ?? Penetration.NoseN(J.Str(p, "nose", "flat"),
                J.NumOpt(p, "lOverD") ?? (J.NumOpt(p, "noseLength") is { } nl ? nl / dEq : 0));
            return new Penetrator(area, n, "scenario");
        }
        if (shape is null) return null;
        var (a, dia2, noseLen) = NoseGeometry.EndSection(shape.BuildMesh(), Vec3.UnitX);
        if (a <= 0) return null;
        // 형상 추정: 노즈 길이/지름이 0.5 이상이면 원뿔로 보고 N 추정, 아니면 평두
        double nShape = nOld ?? (noseLen / dia2 >= 0.5 ? Penetration.NoseN("cone", Math.Min(noseLen / dia2, 6)) : 0.56);
        return new Penetrator(a, nShape, "shape-nose");
    }

    /// <summary>
    /// 실모델 폴더 설정(FR-AN-07): realModels {dir(시나리오 기준 상대), mode: auto|synthetic|required, document?}.
    /// 찾는 순서 = 환경변수 MNS_REAL_MODELS(CLI·앱 --real-models) → realModels.dir — 파일마다 먼저 있는 폴더(실설계 형상은 저장소 밖, NFR-06).
    /// document 가 있으면 그 CAD 문서에서 내보낸 폴더(groups_summary.json 의 document 가 같은 것)만 쓴다 — 다른 설계(예: 새 설계 폴더를
    /// 이전 설계 시나리오에)를 섞지 않는다(경고 후 다음 폴더 → 합성 대체). groups_summary.json 이 없는 폴더는 검사하지 않는다.
    /// </summary>
    private static readonly AsyncLocal<StrongBox<string?>?> _realModelsOverride = new();

    /// <summary>실모델 폴더 환경값: 현재 실행 흐름의 덮어쓰기(<see cref="OverrideRealModels"/>)가 있으면 그것, 없으면 환경변수 MNS_REAL_MODELS.</summary>
    public static string? RealModelsEnv() =>
        _realModelsOverride.Value is { } o ? o.Value : System.Environment.GetEnvironmentVariable("MNS_REAL_MODELS");

    /// <summary>
    /// 이 실행 흐름(스레드·async)에서만 MNS_REAL_MODELS 를 dir 로 본다(null = 없음) — 프로세스 환경변수를 바꾸면 병렬로 도는
    /// 다른 시나리오 빌드·검사가 같이 바뀐다(적대 검사 A57 이 실모델 테스트를 흔들던 경쟁). Dispose 로 이전 값 복원.
    /// </summary>
    public static IDisposable OverrideRealModels(string? dir)
    {
        var prev = _realModelsOverride.Value;
        _realModelsOverride.Value = new StrongBox<string?>(dir);
        return new Restore(() => _realModelsOverride.Value = prev);
    }

    private sealed class Restore(Action a) : IDisposable { public void Dispose() => a(); }

    private static void ConfigureRealModels(JsonObject? rm, BuildContext ctx)
    {
        var mode = rm is null ? "auto" : J.Str(rm, "mode", "auto");
        if (mode is not ("auto" or "synthetic" or "required")) throw new InvalidDataException($"realModels.mode: auto | synthetic | required ('{mode}')");
        var env = RealModelsEnv();
        if (string.Equals(env, "synthetic", StringComparison.OrdinalIgnoreCase)) { mode = "synthetic"; env = null; }   // CLI --synthetic
        ctx.RealModelsMode = mode;
        string? document = rm is null ? null : J.StrOpt(rm, "document");
        var cands = new List<string>();
        ctx.RealModelsDirs.Clear();
        if (!string.IsNullOrWhiteSpace(env)) cands.Add(env);
        if (rm is not null && J.StrOpt(rm, "dir") is { } d) cands.Add(ctx.ResolvePath(d));
        foreach (var c in cands)
        {
            if (!Directory.Exists(c)) continue;
            var full = Path.GetFullPath(c);
            if (ctx.RealModelsDirs.Contains(full, StringComparer.OrdinalIgnoreCase)) continue;
            if (document is not null && ExportDocument(full) is { } doc && doc != document)
            {
                ctx.World.Warnings.Add($"실모델 폴더 {full} 은 CAD 문서 '{doc}' 내보내기 — 시나리오가 요구하는 '{document}' 와 달라 쓰지 않음");
                continue;
            }
            ctx.RealModelsDirs.Add(full);
        }
    }

    /// <summary>그룹 내보내기 폴더의 CAD 문서 이름(groups_summary.json document) — 없으면 null.</summary>
    public static string? ExportDocument(string dir)
    {
        var f = Path.Combine(dir, "groups_summary.json");
        if (!File.Exists(f)) return null;
        try { return J.ParseObject(File.ReadAllText(f))["document"]?.GetValue<string>(); }
        catch { return null; }
    }

    /// <summary>
    /// massProps "real:&lt;파일&gt;" 해석: 실모델이 있으면 그 경로, 없으면 fallback{shape, aeroShape, mass, cg, inertia …} 을 덮어쓴 정의(합성 대체).
    /// </summary>
    private static (JsonObject Def, string? Source) ResolveRealModel(JsonObject d, BuildContext ctx, string name)
    {
        if (J.StrOpt(d, "massProps") is not { } mpRef || !mpRef.StartsWith("real:", StringComparison.Ordinal)) return (d, null);
        var file = mpRef[5..];
        if (ctx.ResolveRealModel(file) is { } full)
        {
            var real = d.DeepClone().AsObject();
            real["massProps"] = full;
            real.Remove("fallback");
            return (real, "real");
        }
        if (ctx.RealModelsMode == "required")
            throw new InvalidDataException($"{name}: 실모델 '{file}' 이 없습니다 (MNS_REAL_MODELS 또는 realModels.dir, 현재 {(ctx.RealModelsDirs.Count > 0 ? string.Join(" → ", ctx.RealModelsDirs) : "미지정")}).");
        if (d["fallback"] is not JsonObject fb)
            throw new InvalidDataException($"{name}: 실모델 '{file}' 이 없고 fallback(합성 대체) 정의도 없습니다.");
        var syn = d.DeepClone().AsObject();
        syn.Remove("massProps"); syn.Remove("fallback");
        foreach (var (k, v) in fb) syn[k] = v?.DeepClone();
        if (ctx.RealModelsMode == "auto")
            ctx.World.Warnings.Add($"{name}: 실모델 '{file}' 없음 → 합성 대체 형상·질량특성 사용(FR-AN-07)");
        return (syn, "synthetic");
    }

    public static Body BuildBody(JsonObject d, BuildContext ctx)
    {
        var name = J.StrReq(d, "name", "bodies[]");
        (d, var modelSource) = ResolveRealModel(d, ctx, name);
        var m2b = ModelToBody(d);
        MassPropsFile? mp = null;
        var shapeDef = d["shape"] as JsonObject;
        if (J.StrOpt(d, "massProps") is { } mpPath)
        {
            var full = ctx.ResolvePath(mpPath);
            mp = MassPropsFile.Load(full);
            if (mp.Stl is not null && shapeDef is null)
            {
                var stlPath = Path.Combine(Path.GetDirectoryName(full)!, mp.Stl);
                if (File.Exists(stlPath))
                    shapeDef = new JsonObject { ["type"] = "mesh", ["file"] = stlPath, ["unit"] = mp.StlUnit };
            }
        }
        // 물체 색: 시나리오 color → Fusion 외관 기본색(massprops appearance.baseColor) → 기본 회색
        var b = new Body { Name = name, Color = J.Str(d, "color", mp?.AppearanceColor ?? "#9aa4b1"), NoseCoefficient = J.Num(d, "noseCoefficient", 0.56), ModelToBody = m2b, MassProps = mp };
        if (shapeDef is not null) b.Shape = BuildShape(shapeDef, ctx, mp, m2b);

        if (mp is not null && d["massCorrection"] is JsonObject mc)
        {
            // 형상은 CAD 그대로, 질량특성만 바디별 재질·카탈로그로 다시 계산한 값(예: CAD 재질이 기본 강철인 설계 — tools/scenario_gen 의 질량특성 재계산 스크립트).
            // cg [m]·inertia(CG 기준 3×3 또는 [Ixx,Iyy,Izz(,Ixy,Ixz,Iyz)]) 는 모델(massprops) 좌표.
            b.OwnMass = J.NumReq(mc, "mass", $"{name}.massCorrection");
            b.OwnCg = m2b * J.V3(mc, "cg", mp.Cg);
            b.OwnInertia = mc["inertia"] is JsonNode mi ? ParseInertia(mi).RotatedBy(m2b) : mp.Inertia.RotatedBy(m2b) * (b.OwnMass / mp.Mass);
            b.MassSource = "fusion-corrected";
        }
        else if (mp is not null)
        {
            b.OwnMass = J.Num(d, "mass", mp.Mass);
            b.OwnCg = m2b * mp.Cg;
            b.OwnInertia = mp.Inertia.RotatedBy(m2b) * (b.OwnMass / mp.Mass);
            b.MassSource = "fusion";
        }
        else
        {
            double? density = J.NumOpt(d, "density");
            double mass = J.NumOpt(d, "mass") ?? (density is { } rho && b.Shape?.Mesh is { } mesh ? rho * mesh.VolumeProperties().Volume
                          : throw new InvalidDataException($"{name}: mass (또는 massProps / density+mesh) 가 필요합니다."));
            b.OwnMass = mass;
            if (J.Has(d, "cg")) b.OwnCg = J.V3(d, "cg", Vec3.Zero);
            else if (b.Shape is not null) b.OwnCg = b.Shape.UniformCentroid();
            if (d["inertia"] is JsonNode iN) { b.OwnInertia = ParseInertia(iN); b.MassSource = "scenario"; }
            else if (b.Shape is not null) { b.OwnInertia = b.Shape.UniformInertia(mass); b.MassSource = b.Shape.Kind == ShapeKind.Mesh ? "mesh-uniform" : "shape-uniform"; }
            else
            {
                double i = 0.4 * mass * 0.1 * 0.1;
                b.OwnInertia = Mat3.Diagonal(i, i, i); b.MassSource = "default-sphere";
            }
        }
        // massScale: 질량·관성을 같은 배율로(CG 불변 — 형상 분포 그대로 질량만 바뀜). 숫자 또는 시나리오 massScales 의 키
        // (예: 드론 4대가 "drone" 한 표본을 공유 — 질량 범위 UQ, FR-REL-03)
        if (d["massScale"] is JsonNode msN)
        {
            double s = msN.GetValueKind() == System.Text.Json.JsonValueKind.String
                ? (ctx.MassScales.TryGetValue(msN.GetValue<string>(), out var sv) ? sv
                   : throw new InvalidDataException($"{name}: massScale 키 '{msN.GetValue<string>()}' 가 massScales 에 없습니다."))
                : J.ToDouble(msN);
            if (!(s > 0)) throw new InvalidDataException($"{name}: massScale 은 양수여야 합니다.");
            b.OwnMass *= s;
            b.OwnInertia *= s;
            ctx.AppliedMassScale[name] = s;
        }
        if (b.OwnMass <= 0) throw new InvalidDataException($"{name}: 질량은 양수여야 합니다.");

        b.ModelSource = modelSource;
        if (modelSource == "real" && J.StrOpt(d, "massProps") is { } realMp) b.ModelPath = ctx.ResolvePath(realMp);
        // aeroShape: 공력용 기본 형상(예: 실모델 메시 컨테이너 → Ø·L 원통 L1 공력·마하 보정). 표시·접촉은 shape 그대로.
        var aeroShape = d["aeroShape"] is JsonObject asd ? BuildShape(asd, ctx, mp, m2b) : b.Shape;
        // 순응 접촉 구조 이상화(collisions.contact.model = compliant): 메시 형상(실모델)은 기본 형상(aeroShape) 또는 경계 상자로
        (b.ContactShape, b.ContactShapeSource) = d["contactShape"] is JsonObject csd ? (BuildShape(csd, ctx, mp, m2b), "contactShape")
            : b.Shape is { Kind: not ShapeKind.Mesh } ? (b.Shape, "shape")
            : aeroShape is { Kind: not ShapeKind.Mesh } ? (aeroShape, "aeroShape")
            : b.Shape is { } ms ? (new Shape { Kind = ShapeKind.Box, Size = ms.Size, Center = ms.Center }, "bbox") : (null, "");
        b.Structure = Structures.StructureDef.Parse(d["structure"] as JsonObject, mp?.DominantMaterial, b.Shape, ctx.World.Warnings, name, mp?.DominantFusionMaterial);
        b.Aero = BuildAero(d["aero"], aeroShape, name, ctx);
        b.Penetrator = BuildPenetrator(d, b.Shape);
        if (J.NumOpt(d, "noseCoefficient") is { } nc) b.NoseCoefficient = nc;
        else if (b.Penetrator is { } pen) b.NoseCoefficient = pen.NoseN;
        return b;
    }

    /// <summary>
    /// FX-03: 메시 형상은 aero.cp 미지정(또는 "auto") 시 측면 투영 중심을 CP 로 쓴다. "none" 이면 CG 작용.
    /// 기본 형상(상자·원통·구)은 기존대로 cp 미지정 = CG 작용(명시 "auto" 일 때만 계산).
    /// </summary>
    private static Vec3? ResolveCp(JsonNode? cpNode, Shape? shape)
    {
        if (cpNode is JsonValue cv && cv.TryGetValue(out string? cs))
        {
            if (cs == "none") return null;
            if (cs != "auto") throw new InvalidDataException($"aero.cp: [x,y,z] | \"auto\" | \"none\" ('{cs}')");
            return shape is null ? null : NoseGeometry.LateralCentroid(shape.BuildMesh()).Cp;
        }
        if (cpNode is not null) return Vec3.FromArray(J.Arr(cpNode));
        if (shape is { Kind: ShapeKind.Mesh }) return NoseGeometry.LateralCentroid(shape.Mesh!).Cp;
        return null;
    }

    /// <summary>
    /// aero.fins: {count, rootChord, tipChord, span, sweep(뿌리 앞전→끝 앞전 축 거리), rootLeFromNose(기수 끝→뿌리 앞전), bodyRadius} [m].
    /// 동체 좌표 기수 끝 = 형상 최대 x.
    /// </summary>
    private static FinSet ParseFins(JsonObject f, Shape shape)
    {
        double noseX = shape.BuildMesh().Bounds().Max.X;
        return new FinSet((int)J.NumReq(f, "count", "fins"), J.NumReq(f, "rootChord", "fins"), J.NumReq(f, "tipChord", "fins"),
            J.NumReq(f, "span", "fins"), J.Num(f, "sweep", 0), noseX - J.NumReq(f, "rootLeFromNose", "fins"), J.Num(f, "bodyRadius", 0), "scenario");
    }

    private static AeroModel? BuildAero(JsonNode? n, Shape? shape, string name, BuildContext ctx)
    {
        if (n is null) return shape is null ? null : new AeroModel { Shape = shape, RefLength = shape.CharacteristicLength, Cp = ResolveCp(null, shape) };
        if (n is JsonValue v && v.TryGetValue(out string? s))
        {
            if (s == "none") return null;
            if (s == "shape") return new AeroModel { Shape = shape, RefLength = shape?.CharacteristicLength ?? 1, Cp = ResolveCp(null, shape) };
            throw new InvalidDataException($"{name}.aero: 'none' | 'shape' | 객체");
        }
        var a = n.AsObject();
        BuildUp? bu = null;
        if (a["cp"] is JsonValue cpv && cpv.TryGetValue(out string? cps) && cps is "component" or "barrowman")
        {
            if (shape is null) throw new InvalidDataException($"{name}.aero.cp:\"component\" 은 형상(메시/기본 형상)이 필요합니다.");
            bu = ComponentAero.Compute(shape.BuildMesh(), a["fins"] is JsonObject fo ? ParseFins(fo, shape) : null, J.NumOpt(a, "refDiameter"));
        }
        Table1D? table = null;
        if (a["cdTable"] is JsonArray t)
        {
            var rows = t.Select(r => J.Arr(r!)).ToArray();
            table = new Table1D(rows.Select(r => r[0]).ToArray(), rows.Select(r => r[1]).ToArray());
        }
        return new AeroModel
        {
            Cd = J.NumOpt(a, "cd"),
            Area = J.NumOpt(a, "area"),
            CdTable = table,
            Cp = bu is not null ? new Vec3(bu.XCp, shape!.Center.Y, shape.Center.Z) : ResolveCp(a["cp"], shape),
            NormalForceSlope = bu?.CnAlpha ?? 0,
            NormalRefArea = bu?.RefArea ?? 0,
            Components = bu,
            RefLength = J.Num(a, "refLength", shape?.CharacteristicLength ?? 1),
            Damping = J.V3(a, "damping", Vec3.Zero),
            DampingLengths = J.V3Opt(a, "dampingLengths"),
            Scale = J.Num(a, "scale", 1),
            Shape = shape,
            MachCorrection = J.Bool(a, "machCorrection", false),
            Crossflow = J.Str(a, "crossflow", "legacy") is var cf && cf is "legacy" or "tr-r300"
                ? cf : throw new InvalidDataException($"{name}.aero.crossflow: 'legacy' | 'tr-r300' ('{cf}')"),
            Db = J.StrOpt(a, "db") is { } dbPath ? AeroDb.Load(ctx.ResolvePath(dbPath)) : null,
            FlowField = J.StrOpt(a, "flowField") is { } ffPath && File.Exists(ctx.ResolvePath(ffPath)) ? SurfaceFlowField.Load(ctx.ResolvePath(ffPath)) : null,
        };
    }

    public static Shape BuildShape(JsonObject s, BuildContext ctx, MassPropsFile? mp, Mat3 m2b)
    {
        var type = J.StrReq(s, "type", "shape");
        var center = J.V3(s, "center", Vec3.Zero);
        int axis = J.Str(s, "axis", "x") switch { "x" => 0, "y" => 1, "z" => 2, var ax => throw new InvalidDataException($"axis: {ax}") };
        switch (type)
        {
            case "box": return new Shape { Kind = ShapeKind.Box, Size = J.V3(s, "size", new Vec3(0.3, 0.3, 0.3)), Center = center };
            case "cylinder": return new Shape { Kind = ShapeKind.Cylinder, Radius = J.NumReq(s, "radius", "cylinder"), Length = J.NumReq(s, "length", "cylinder"), Axis = axis, Center = center };
            case "sphere": return new Shape { Kind = ShapeKind.Sphere, Radius = J.NumReq(s, "radius", "sphere"), Center = center };
            case "shellSegment":
                return Shape.ShellSegmentOf(J.NumReq(s, "radius", "shellSegment"), J.NumReq(s, "thickness", "shellSegment"),
                    J.NumReq(s, "length", "shellSegment"), J.Num(s, "span", 120), J.Num(s, "centerAngle", 0), axis, center);
            case "mesh":
            {
                var file = ctx.ResolvePath(J.StrReq(s, "file", "mesh"));
                var raw = TriMesh.LoadStl(file);
                var unit = J.Str(s, "unit", "auto");
                double scale = unit == "auto" ? MassPropsFile.GuessUnitScale(raw, mp?.BBoxMin, mp?.BBoxMax) : Frames.UnitScale(unit);
                var mesh = raw.Transformed(v => m2b * (v * scale));
                return Shape.FromMesh(mesh, file);
            }
            default: throw new InvalidDataException($"알 수 없는 shape: {type}");
        }
    }
}
