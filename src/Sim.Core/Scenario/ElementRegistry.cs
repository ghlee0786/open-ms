using System.Text.Json.Nodes;
using Sim.Core.Engine;
using Sim.Core.Models;
using Sim.Core.Numerics;

namespace Sim.Core.Scenario;

/// <summary>빌드 중 이름 해석 컨텍스트.</summary>
public sealed class BuildContext(World world, string baseDir, Dictionary<string, JsonObject> bodyDefs)
{
    public World World { get; } = world;
    public string BaseDir { get; } = baseDir;
    public Dictionary<string, JsonObject> BodyDefs { get; } = bodyDefs;

    public string ResolvePath(string p) => Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(BaseDir, p));

    /// <summary>실모델 폴더(FR-AN-07): 찾는 순서 = 환경변수 MNS_REAL_MODELS → 시나리오 realModels.dir. 첫 폴더(null = 없음, 합성 대체).</summary>
    public string? RealModelsDir => RealModelsDirs.Count > 0 ? RealModelsDirs[0] : null;
    public List<string> RealModelsDirs { get; } = new();
    /// <summary>auto | synthetic(강제 대체) | required(없으면 오류).</summary>
    public string RealModelsMode { get; set; } = "auto";

    /// <summary>"real:&lt;파일&gt;" 참조 → 실모델 파일 전체 경로(폴더 순서대로 첫 존재, 없거나 synthetic 모드면 null).</summary>
    public string? ResolveRealModel(string file)
    {
        if (RealModelsMode == "synthetic") return null;
        foreach (var d in RealModelsDirs)
        {
            var p = Path.Combine(d, file);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>시나리오 massScales: 키 → 질량 배율(물체 massScale 이 키로 참조 — 여러 물체가 한 UQ 표본을 공유).</summary>
    public Dictionary<string, double> MassScales { get; } = new();
    /// <summary>물체별로 적용한 질량 배율(구성 전환 switchConfig 의 절대 질량·관성에도 같은 배율).</summary>
    public Dictionary<string, double> AppliedMassScale { get; } = new();

    public Body Body(string name) => World.GetBody(name);

    /// <summary>"body.device" 참조.</summary>
    public Device Device(string reference)
    {
        int i = reference.LastIndexOf('.');
        if (i <= 0) throw new InvalidDataException($"장치 참조는 'body.device' 형식이어야 합니다: {reference}");
        var b = Body(reference[..i]);
        var name = reference[(i + 1)..];
        return b.Devices.FirstOrDefault(d => d.Name == name)
               ?? throw new KeyNotFoundException($"장치 '{reference}' 가 없습니다.");
    }

    public Connector Connector(string name) =>
        World.Connectors.FirstOrDefault(c => c.Name == name) ?? throw new KeyNotFoundException($"연결 '{name}' 이 없습니다.");
}

/// <summary>
/// 요소 등록부(FR-GEN-03): 타입 이름 → 구현. 새 Device/Connector/Trigger/Action 은 Register* 로 추가하며 코어 변경이 없다.
/// </summary>
public static class ElementRegistry
{
    public delegate Device DeviceFactory(JsonObject def, Body owner, BuildContext ctx);
    public delegate Connector ConnectorFactory(JsonObject def, BuildContext ctx);
    public delegate ITrigger TriggerFactory(JsonObject def, BuildContext ctx);
    public delegate IAction ActionFactory(JsonObject def, BuildContext ctx);

    private static readonly Dictionary<string, DeviceFactory> _devices = new();
    private static readonly Dictionary<string, ConnectorFactory> _connectors = new();
    private static readonly Dictionary<string, TriggerFactory> _triggers = new();
    private static readonly Dictionary<string, ActionFactory> _actions = new();

    public static IReadOnlyCollection<string> DeviceTypes => _devices.Keys;
    public static IReadOnlyCollection<string> ConnectorTypes => _connectors.Keys;
    public static IReadOnlyCollection<string> TriggerTypes => _triggers.Keys;
    public static IReadOnlyCollection<string> ActionTypes => _actions.Keys;

    public static void RegisterDevice(string type, DeviceFactory f) => _devices[type] = f;
    public static void RegisterConnector(string type, ConnectorFactory f) => _connectors[type] = f;
    public static void RegisterTrigger(string type, TriggerFactory f) => _triggers[type] = f;
    public static void RegisterAction(string type, ActionFactory f) => _actions[type] = f;

    public static Device CreateDevice(JsonObject d, Body owner, BuildContext ctx) =>
        Lookup(_devices, J.StrReq(d, "type", $"{owner.Name}.devices"), "device")(d, owner, ctx);
    public static Connector CreateConnector(JsonObject d, BuildContext ctx) =>
        Lookup(_connectors, J.StrReq(d, "type", "connectors"), "connector")(d, ctx);
    public static ITrigger CreateTrigger(JsonObject d, BuildContext ctx) =>
        Lookup(_triggers, J.StrReq(d, "type", "when"), "trigger")(d, ctx);
    public static IAction CreateAction(JsonObject d, BuildContext ctx) =>
        Lookup(_actions, J.StrReq(d, "type", "do"), "action")(d, ctx);

    private static string ParseFailureMode(string m) => m is "none" or "noDeploy" or "delay" or "partial" or "lineTwist"
        ? m : throw new InvalidDataException($"parachute.failure.mode: none | noDeploy | delay | partial | lineTwist ('{m}')");

    private static double ReefRatioOf(JsonObject r)
    {
        double v = J.NumReq(r, "ratio", "parachute.reefing");
        return v > 0 && v < 1 ? v : throw new InvalidDataException($"parachute.reefing.ratio 는 0 과 1 사이여야 합니다 ({v}).");
    }

    /// <summary>
    /// cushionMount(FR-AN-08 ②): payload(직계 자식), pads[{axis:"+x", material(폼), area, thickness, E_MPa?, plateau_MPa?, densification?}],
    /// pins{count, diameter, material, length?, axes:"xyz", stiffness?, breakLoad?, dampingRatio?}, padDampingRatio, wallGap, wallStiffness,
    /// recordAfter, channels[{name, connector|device}], parts[](PartStress), stages[{label, from, to?, duration?, offset?, pad?}].
    /// </summary>
    private static Device BuildCushionMount(JsonObject d, Body owner, BuildContext ctx)
    {
        var name = J.StrReq(d, "name", owner.Name);
        string where = $"{owner.Name}.{name}";
        var payload = ctx.Body(J.StrReq(d, "payload", where));
        if (payload.Parent != owner || payload.Mode != BodyMode.Stowed)
            throw new InvalidDataException($"{where}: payload '{payload.Name}' 은 {owner.Name} 의 직계 자식(parent)이어야 합니다.");
        var pads = new List<FoamPad>();
        foreach (var pn in d["pads"] as JsonArray ?? new JsonArray())
        {
            var p = pn!.AsObject();
            var ax = J.StrReq(p, "axis", $"{where}.pads");
            if (ax.Length != 2 || ax[0] is not ('+' or '-') || "xyz".IndexOf(ax[1]) < 0)
                throw new InvalidDataException($"{where}.pads.axis: +x|-x|+y|-y|+z|-z ('{ax}')");
            var matName = J.StrReq(p, "material", $"{where}.pads");
            var mat = Structures.MaterialLibrary.Find(matName) ?? throw new InvalidDataException($"{where}.pads: 재질 '{matName}' 이 없습니다.");
            if (mat.Class != "foam" && J.NumOpt(p, "plateau_MPa") is null)
                throw new InvalidDataException($"{where}.pads: '{mat.Key}' 는 폼이 아닙니다(class={mat.Class}) — 폼 재질(epp·eva) 또는 plateau_MPa 지정.");
            double area = J.NumReq(p, "area", $"{where}.pads"), h = J.NumReq(p, "thickness", $"{where}.pads");
            double e = (J.NumOpt(p, "E_MPa") ?? mat.E / 1e6) * 1e6, sp = (J.NumOpt(p, "plateau_MPa") ?? mat.Yield / 1e6) * 1e6;
            double ed = J.Num(p, "densification", Structures.ImpactDamage.FoamDensification);
            if (!(area > 0) || !(h > 0) || !(e > 0) || !(sp > 0) || !(ed > sp / e && ed < 1))
                throw new InvalidDataException($"{where}.pads: area·thickness·E·plateau > 0, plateau/E < densification < 1 이어야 합니다.");
            pads.Add(new FoamPad
            {
                Label = J.Str(p, "label", ax), Axis = "xyz".IndexOf(ax[1]), Sign = ax[0] == '+' ? 1 : -1, Area = area, Thickness = h,
                Modulus = e, Plateau = sp, Densification = ed, MaterialKey = mat.Key,
            });
        }
        int pinCount = 0; double pinK = 0, pinBreak = double.PositiveInfinity, pinZeta = 0.05; bool[] pinAxes = [true, true, true]; string pinNote = "";
        if (d["pins"] is JsonObject pi)
        {
            pinCount = (int)J.NumReq(pi, "count", $"{where}.pins");
            if (pinCount < 1) throw new InvalidDataException($"{where}.pins.count ≥ 1");
            var axes = J.Str(pi, "axes", "xyz");
            pinAxes = [axes.Contains('x'), axes.Contains('y'), axes.Contains('z')];
            pinZeta = J.Num(pi, "dampingRatio", 0.05);
            double? dia = J.NumOpt(pi, "diameter");
            var pm = J.StrOpt(pi, "material") is { } pmn ? Structures.MaterialLibrary.Find(pmn) ?? throw new InvalidDataException($"{where}.pins: 재질 '{pmn}' 이 없습니다.") : null;
            if (J.NumOpt(pi, "stiffness") is { } k) { pinK = k; pinNote = "강성 입력"; }
            else if (dia is { } dd && pm is not null)
            {
                double len = J.Num(pi, "length", 2 * dd);
                double I = Math.PI * Math.Pow(dd, 4) / 64;
                pinK = 3 * pm.E * I / (len * len * len);
                pinNote = $"강성 = 외팔 핀 3EI/L³ (d {dd * 1000:G3} mm, L {len * 1000:G3} mm, {pm.Key})";
            }
            else throw new InvalidDataException($"{where}.pins: stiffness 또는 diameter+material 이 필요합니다.");
            if (J.NumOpt(pi, "breakLoad") is { } bl) { pinBreak = bl; pinNote += ", 파단하중 입력"; }
            else if (dia is { } dd2 && pm is not null)
            {
                pinBreak = 0.6 * pm.Ultimate * Math.PI / 4 * dd2 * dd2;
                pinNote += $", 파단 = 단일 전단 0.6σu·πd²/4 = {pinBreak:F0} N/핀";
            }
            if (!(pinK > 0)) throw new InvalidDataException($"{where}.pins: 강성이 양수여야 합니다.");
        }
        var channels = new List<(string, string, string)>();
        foreach (var cn in d["channels"] as JsonArray ?? new JsonArray())
        {
            var c = cn!.AsObject();
            var cname = J.StrReq(c, "name", $"{where}.channels");
            if (J.StrOpt(c, "connector") is { } con) channels.Add((cname, "connector", con));
            else if (J.StrOpt(c, "device") is { } dev) channels.Add((cname, "device", dev));
            else throw new InvalidDataException($"{where}.channels.{cname}: connector 또는 device 가 필요합니다.");
        }
        var parts = (d["parts"] as JsonArray ?? new JsonArray()).Select(x => Structures.PartStress.Parse(x!.AsObject(), where)).ToList();
        var stages = (d["stages"] as JsonArray ?? new JsonArray()).Select(x =>
        {
            var s = x!.AsObject();
            return new MountStage(J.StrReq(s, "label", $"{where}.stages"), J.StrReq(s, "from", $"{where}.stages"), J.StrOpt(s, "to"),
                J.Num(s, "duration", 1.0), J.Num(s, "offset", 0), J.Num(s, "pad", 0.05));
        }).ToList();
        return new CushionMount
        {
            Name = name, Owner = owner, Payload = payload, Pads = pads,
            PinCount = pinCount, PinStiffness = pinK, PinBreakLoad = pinBreak, PinAxes = pinAxes, PinDampingRatio = pinZeta, PinNote = pinNote,
            PadDampingRatio = J.Num(d, "padDampingRatio", 0.1),
            WallGap = J.V3(d, "wallGap", new Vec3(0.01, 0.01, 0.01)),
            WallStiffness = J.Num(d, "wallStiffness", Math.Max(2e6, pads.Count > 0 ? 20 * pads.Max(p => p.Stiffness0) : 0)),
            RecordAfter = J.Num(d, "recordAfter", 10),
            Channels = channels, Parts = parts, Stages = stages,
        };
    }

    private static T Lookup<T>(Dictionary<string, T> map, string type, string kind) =>
        map.TryGetValue(type, out var f) ? f : throw new InvalidDataException($"알 수 없는 {kind} 타입 '{type}' (등록: {string.Join(", ", map.Keys)})");

    static ElementRegistry()
    {
        // ---- Devices ----
        RegisterDevice("parachute", (d, owner, ctx) => new Parachute
        {
            Name = J.StrReq(d, "name", owner.Name), Owner = owner,
            D0 = J.Num(d, "d0", 1.22), Cd0 = J.Num(d, "cd0", 2.2),
            Attach = J.V3(d, "attach", Vec3.Zero), RiserLength = J.Num(d, "riser", 2),
            FillConstant = J.Num(d, "fillConstant", 8), FillExponent = J.Num(d, "fillExponent", 2),
            LineStretchTime = J.Num(d, "lineStretchTime", 0.3), InitialFill = J.Num(d, "initialFill", 0.02),
            CdSScale = J.Num(d, "cdsScale", 1), ApparentMassK0 = J.Num(d, "apparentMassK0", 0),
            // 1단 리핑: reefing {ratio: 리핑 CdS / 완전 CdS (0~1), disreefDelay: 라인 신장 완료 → 디스리프 [s]}
            ReefRatio = d["reefing"] is JsonObject ro ? ReefRatioOf(ro) : 0,
            DisreefDelay = d["reefing"] is JsonObject ro2 ? Math.Max(0, J.Num(ro2, "disreefDelay", 2.0)) : 2.0,
            FailureMode = d["failure"] is JsonObject fo ? ParseFailureMode(J.Str(fo, "mode", "none")) : "none",
            FailureDelay = d["failure"] is JsonObject f1 ? J.Num(f1, "delay", 1.0) : 1.0,
            FailureFill = d["failure"] is JsonObject f2 ? Math.Clamp(J.Num(f2, "fill", 0.5), 0.02, 1) : 0.5,
            FailureCdsLoss = d["failure"] is JsonObject f3 ? Math.Clamp(J.Num(f3, "cdsLoss", 0.3), 0, 0.98) : 0.3,
            FailureProbability = d["failure"] is JsonObject f4 ? Math.Clamp(J.Num(f4, "probability", 1.0), 0, 1) : 1.0,
        });
        RegisterDevice("multirotor", (d, owner, ctx) =>
        {
            // 고정 팔 기체: props = 실모델 허브 위치·축(동체 좌표) — 오버레이 암 없이 프롭만 회전 표시, 접힘 구성 자동 감지 안 함
            List<PropHub> props = d["props"] is JsonArray pa
                ? pa.Select((p, i) => p is JsonObject po
                    ? new PropHub(J.V3(po, "pos", Vec3.Zero), J.V3(po, "axis", new Vec3(0, 0, -1)).Normalized(),
                                  J.Num(po, "spin", i % 2 == 0 ? 1 : -1) < 0 ? -1 : 1)
                    : throw new InvalidDataException($"multirotor props[{i}] 는 {{pos, axis, spin}} 객체여야 합니다.")).ToList()
                : [];
            return new Multirotor
            {
                Name = J.StrReq(d, "name", owner.Name), Owner = owner,
                MaxThrust = J.Num(d, "maxThrust", 85), Tau = J.Num(d, "tau", 0.15), MaxTiltDeg = J.Num(d, "maxTiltDeg", 35),
                CruiseSpeed = J.Num(d, "cruiseSpeed", 10), LandSpeed = J.Num(d, "landSpeed", 1.2),
                MaxClimb = J.Num(d, "maxClimb", 3), MaxDescent = J.Num(d, "maxDescent", 3),
                VrsDepth = J.Num(d, "vrsDepth", 0), VrsModel = J.Str(d, "vrsModel", "gaussian"),
                PropSolidity = J.Num(d, "propSolidity", 0.09), PropCt0 = J.Num(d, "propCt0", 0.1), RotorDiskArea = J.Num(d, "rotorDiskArea", 4 * Math.PI * 0.0889 * 0.0889),
                AttKp = J.Num(d, "attKp", 40), AttKd = J.Num(d, "attKd", 12),
                // 접힘 암: 소유 물체에 "deployed" 구성이 있으면 그 구성으로 바뀔 때 펴진다(표시, FR-SEP-04)
                ArmsConfig = J.StrOpt(d, "armsConfig")
                             ?? (props.Count == 0 && ctx.BodyDefs.TryGetValue(owner.Name, out var od) && od["configs"]?["deployed"] is not null ? "deployed" : null),
                ArmDeployTime = J.Num(d, "armDeployTime", 0.5),
                ArmCount = props.Count > 0 ? props.Count : Math.Max(1, (int)J.Num(d, "arms", 4)),
                ArmLength = J.Num(d, "armLength", 0), PropDiameter = J.Num(d, "propDiameter", 0),
                Props = props, ArmsOverlay = J.Bool(d, "armsOverlay", props.Count == 0),
            };
        });

        RegisterDevice("pulse", (d, owner, ctx) => new Pulse
        {
            Name = J.StrReq(d, "name", owner.Name), Owner = owner,
            PeakG = J.Num(d, "peakG", 15), Duration = J.Num(d, "duration", 0.02),
            Direction = J.V3(d, "direction", new Vec3(-1, 0, 0)), Shape = J.Str(d, "shape", "halfsine"),
            Reaction = J.StrOpt(d, "reaction") is { } rb ? ctx.Body(rb) : null,
        });

        RegisterDevice("cushionMount", BuildCushionMount);

        // ---- Connectors ----
        RegisterConnector("hinge", (d, ctx) => new Hinge
        {
            Name = J.StrReq(d, "name", "hinge"),
            A = ctx.Body(J.StrReq(d, "a", "hinge")), B = ctx.Body(J.StrReq(d, "b", "hinge")),
            PointA = J.V3(d, "pointA", Vec3.Zero), AxisA = J.V3(d, "axisA", new Vec3(0, 1, 0)),
            Stiffness = J.Num(d, "stiffness", 1e6), Damping = J.Num(d, "damping", -1),
            RotStiffness = J.Num(d, "rotStiffness", 5e4), RotDamping = J.Num(d, "rotDamping", -1),
            ReleaseAngleDeg = J.Num(d, "releaseAngle", 45), BreakLoad = J.Num(d, "breakLoad", double.PositiveInfinity),
            StabilityDt = Math.Max(0, J.Num(d, "stabilityDt", 0.001)),
        });
        RegisterConnector("tether", (d, ctx) => new Tether
        {
            Name = J.StrReq(d, "name", "tether"),
            A = ctx.Body(J.StrReq(d, "a", "tether")), B = ctx.Body(J.StrReq(d, "b", "tether")),
            PointA = J.V3(d, "pointA", Vec3.Zero), PointB = J.V3(d, "pointB", Vec3.Zero),
            Length = J.Num(d, "length", 1.5), EA = J.Num(d, "ea", 2e4), Damping = J.Num(d, "damping", 50),
            BreakLoad = J.Num(d, "breakLoad", double.PositiveInfinity),
            Active = J.Bool(d, "active", true),
            // 정하중형 에너지 흡수기(선택): absorber {force [N], rise [m] (0→force 선형 상승), stroke [m]}
            AbsorberForce = d["absorber"] is JsonObject ab ? Math.Max(0, J.NumReq(ab, "force", "tether.absorber")) : 0,
            AbsorberRise = d["absorber"] is JsonObject ab2 ? Math.Max(1e-6, J.Num(ab2, "rise", 0.1)) : 0.1,
            AbsorberStroke = d["absorber"] is JsonObject ab3 ? Math.Max(0, J.NumReq(ab3, "stroke", "tether.absorber")) : 0,
        });
        RegisterConnector("breakCord", (d, ctx) =>
        {
            var name = J.StrReq(d, "name", "breakCord");
            double fb = J.NumReq(d, "breakLoad", name), eb = J.Num(d, "breakElongation", 0.15), len = J.Num(d, "length", 2.0);
            if (!(fb > 0) || !(eb > 0) || !(len > 0)) throw new InvalidDataException($"breakCord '{name}': breakLoad·breakElongation·length 는 양수여야 합니다.");
            var b = ctx.Body(J.StrReq(d, "b", name));
            double mB = b.OwnMass + b.SubtreeStowed().Sum(x => x.OwnMass);
            double zeta = J.Num(d, "dampingRatio", 0.05);
            double ea = fb / eb;
            return new BreakCord
            {
                Name = name, A = ctx.Body(J.StrReq(d, "a", name)), B = b,
                PointA = J.V3(d, "pointA", Vec3.Zero), PointB = J.V3(d, "pointB", Vec3.Zero),
                Length = len, EA = ea, BreakLoad = fb, BreakElongation = eb, DampingRatio = zeta,
                Damping = J.NumOpt(d, "damping") ?? 2 * zeta * Math.Sqrt(ea / len * mB),
                Active = J.Bool(d, "active", true),
            };
        });
        RegisterConnector("rail", (d, ctx) => new Rail
        {
            Name = J.StrReq(d, "name", "rail"),
            Parent = ctx.Body(J.StrReq(d, "parent", "rail")), Child = ctx.Body(J.StrReq(d, "child", "rail")),
            Axis = J.V3(d, "axis", new Vec3(0, 0, 1)), Length = J.Num(d, "length", 0.34),
            Friction = J.Num(d, "friction", 0.1), ExitPush = J.Num(d, "exitPush", 0),
            Active = false,
        });
        RegisterConnector("swing", (d, ctx) => new Swing
        {
            Name = J.StrReq(d, "name", "swing"),
            Parent = ctx.Body(J.StrReq(d, "parent", "swing")), Child = ctx.Body(J.StrReq(d, "child", "swing")),
            Point = J.V3(d, "point", Vec3.Zero), Axis = J.V3(d, "axis", new Vec3(0, 0, 1)),
            AngleDeg = J.Num(d, "angle", 90), Duration = Math.Max(1e-3, J.Num(d, "duration", 0.3)),
            Active = false,
        });

        // ---- Triggers ----
        RegisterTrigger("time", (d, ctx) => new TimeTrigger(J.NumReq(d, "t", "time")));
        RegisterTrigger("after", (d, ctx) => new AfterTrigger(J.StrReq(d, "event", "after"), J.Num(d, "delay", 0)));
        RegisterTrigger("altitude", (d, ctx) => new AltitudeTrigger(ctx.Body(J.StrReq(d, "body", "altitude")),
            J.NumOpt(d, "below"), J.NumOpt(d, "above"), J.Bool(d, "msl", false)));
        RegisterTrigger("speed", (d, ctx) => new SpeedTrigger(ctx.Body(J.StrReq(d, "body", "speed")),
            J.NumOpt(d, "below"), J.NumOpt(d, "above"), J.Bool(d, "airspeed", true)));
        RegisterTrigger("distance", (d, ctx) => new DistanceTrigger(ctx.Body(J.StrReq(d, "body", "distance")),
            J.V3Opt(d, "point"), J.StrOpt(d, "to") is { } o ? ctx.Body(o) : null,
            J.NumOpt(d, "below"), J.NumOpt(d, "above"), J.Bool(d, "horizontal", false)));
        RegisterTrigger("arrived", (d, ctx) => new ArrivedTrigger(
            ctx.Device(J.StrReq(d, "device", "arrived")) as Multirotor ?? throw new InvalidDataException("arrived 트리거는 multirotor 장치만"),
            J.Num(d, "radius", 3)));
        RegisterTrigger("accel", (d, ctx) => new AccelTrigger(ctx.Body(J.StrReq(d, "body", "accel")), J.NumReq(d, "above", "accel")));
        RegisterTrigger("tension", (d, ctx) => new TensionTrigger(
            ctx.Connector(J.StrReq(d, "connector", "tension")) as Tether ?? throw new InvalidDataException("tension 트리거는 tether 연결만"),
            J.NumReq(d, "above", "tension")));
        RegisterTrigger("and", (d, ctx) => new AndTrigger(d["all"]!.AsArray().Select(x => CreateTrigger(x!.AsObject(), ctx)).ToList()));
        RegisterTrigger("or", (d, ctx) => new OrTrigger(d["any"]!.AsArray().Select(x => CreateTrigger(x!.AsObject(), ctx)).ToList()));

        // ---- Actions ----
        RegisterAction("separate", (d, ctx) => new SeparateAction(ctx.Body(J.StrReq(d, "body", "separate")),
            J.V3(d, "relVel", Vec3.Zero), J.V3(d, "relOmega", Vec3.Zero)));
        foreach (var cmd in new[] { "deploy", "release", "activate", "deactivate", "set", "startRail", "fire" })
        {
            var command = cmd == "startRail" ? "start" : cmd;
            RegisterAction(cmd, (d, ctx) =>
            {
                if (J.StrOpt(d, "device") is { } dev) return new DeviceCommandAction(ctx.Device(dev), command, d);
                if (J.StrOpt(d, "connector") is { } con) return new ConnectorCommandAction(ctx.Connector(con), command, d);
                throw new InvalidDataException($"'{cmd}' 액션에는 device 또는 connector 가 필요합니다.");
            });
        }
        RegisterAction("switchConfig", (d, ctx) =>
        {
            var name = J.StrReq(d, "body", "switchConfig");
            var config = J.StrReq(d, "config", "switchConfig");
            var b = ctx.Body(name);
            var cfg = ctx.BodyDefs[name]["configs"]?[config]?.AsObject()
                      ?? throw new InvalidDataException($"{name}: 구성 '{config}' 이 없습니다.");
            double ms = ctx.AppliedMassScale.GetValueOrDefault(name, 1.0);
            double m = J.NumOpt(cfg, "mass") is { } cm ? cm * ms : b.OwnMass;
            var cg = J.V3(cfg, "cg", b.OwnCg);
            var I = cfg["inertia"] is { } iN ? ScenarioLoader.ParseInertia(iN) * ms : b.OwnInertia;
            // inertiaScale [sx,sy,sz] (또는 스칼라): 실모델·합성 어느 쪽이든 현재 관성에 상대 배율(예: 암 전개) — I'ij = Iij·√(si·sj)
            if (cfg["inertiaScale"] is { } isN)
            {
                var s = isN is JsonArray ? J.Arr(isN) : [J.ToDouble(isN), J.ToDouble(isN), J.ToDouble(isN)];
                if (s.Length != 3 || s.Any(x => !(x > 0))) throw new InvalidDataException($"{name}.configs.{config}.inertiaScale: 양수 3개(또는 스칼라)");
                double F(int i, int j) => Math.Sqrt(s[i] * s[j]);
                I = new Mat3(I.M11 * F(0, 0), I.M12 * F(0, 1), I.M13 * F(0, 2), I.M21 * F(1, 0), I.M22 * F(1, 1), I.M23 * F(1, 2), I.M31 * F(2, 0), I.M32 * F(2, 1), I.M33 * F(2, 2));
            }
            return new SwitchConfigAction(b, config, m, cg, I);
        });
        RegisterAction("stop", (d, ctx) => new StopAction());
        RegisterAction("log", (d, ctx) => new LogAction(J.Str(d, "message", "")));
    }
}
