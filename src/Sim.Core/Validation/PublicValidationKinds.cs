using System.Globalization;
using System.Text.Json.Nodes;
using Sim.Core.Engine;
using Sim.Core.Environment;
using Sim.Core.Geometry;
using Sim.Core.Models;
using Sim.Core.Numerics;
using Sim.Core.Scenario;

namespace Sim.Core.Validation;

/// <summary>재현기(kind) 구현. 각 재현기는 엔진(World) 또는 엔진이 쓰는 모델 함수를 그대로 호출한다.</summary>
public static partial class PublicValidation
{
    private const double Ft = 0.3048;

    private static string Inv(double x) => x.ToString("R", CultureInfo.InvariantCulture);

    private static World BuildWorld(JsonObject scenario) =>
        new ScenarioDoc { Root = scenario, BaseDir = System.IO.Directory.GetCurrentDirectory() }.Build();

    private static JsonArray Arr(params double[] v) => new(v.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());

    // ---------------- 낙하산 ----------------

    /// <summary>낙하산 정상 하강률: 엔진에서 낙하산 장치(F절)를 단 물체를 정지 상태에서 떨어뜨려 정상 하강 속도를 얻는다.
    /// 밀도는 측정 고도의 US1976(지오퍼텐셜 변환, geopotential=false 로 끌 수 있음)로 고정한다.</summary>
    private static IEnumerable<KindOutput> ParachuteDescent(JsonObject i, JsonObject p)
    {
        double d0 = J.NumReq(i, "d0_m", "parachute-steady-descent"), cd0 = J.NumReq(i, "cd0", "parachute-steady-descent");
        double mass = J.NumReq(i, "mass_kg", "parachute-steady-descent"), alt = J.Num(i, "altitude_m", 0);
        double rho = new Atmosphere { Geopotential = J.Bool(i, "geopotential", true) }.At(alt).Density;
        double vt = Math.Sqrt(2 * mass * Atmosphere.G0 / (rho * cd0 * Math.PI / 4 * d0 * d0));
        double tEnd = Math.Max(40, 8 * vt / Atmosphere.G0);
        var w = ScenarioDoc.Parse($$"""
        { "schema":"mns.scenario/1", "sim":{"dt":0.002,"tEnd":{{Inv(tEnd)}}}, "environment":{"constantDensity":{{Inv(rho)}}},
          "bodies":[{"name":"b","mass":{{Inv(mass)}},"inertia":[0.1,0.1,0.1],"aero":"none","position":[0,0,-1000000],
            "devices":[{"type":"parachute","name":"c","d0":{{Inv(d0)}},"cd0":{{Inv(cd0)}},"attach":[0,0,0]}]}],
          "events":[{"name":"d","when":{"type":"time","t":0},"do":[{"type":"deploy","device":"b.c"}]}] }
        """, System.IO.Directory.GetCurrentDirectory()).Build();
        w.Run();
        yield return new KindOutput(J.Str(p, "label", ""), null, w.GetBody("b").V.Z, "m/s");
    }

    private static JsonObject ChuteDevice(JsonObject i, double lineStretch) => new()
    {
        ["type"] = "parachute", ["name"] = "c", ["d0"] = J.NumReq(i, "d0_m", "parachute"), ["cd0"] = J.NumReq(i, "cd0", "parachute"),
        ["attach"] = Arr(0, 0, 0), ["lineStretchTime"] = lineStretch,
        ["fillConstant"] = J.Num(i, "fillConstant", 8), ["fillExponent"] = J.Num(i, "fillExponent", 2),
        ["apparentMassK0"] = J.Num(i, "apparentMassK0", 0), ["initialFill"] = J.Num(i, "initialFill", 0.02),
    };

    private static JsonArray DeployAtZero() => new(new JsonObject
    {
        ["name"] = "deploy", ["when"] = new JsonObject { ["type"] = "time", ["t"] = 0 },
        ["do"] = new JsonArray(new JsonObject { ["type"] = "deploy", ["device"] = "b.c" }),
    });

    /// <summary>
    /// 무한 질량(풍동) 개방하중: 일정 유속 V·밀도 ρ 에서 충만 시작(SI)부터 낙하산 장치를 펼쳐 최대 하중을 얻는다.
    /// 물체 질량 10⁷ kg·중력 0 으로 속도가 변하지 않게 한다(풍동 고정 받침과 같음).
    /// </summary>
    private static IEnumerable<KindOutput> ParachuteOpeningInfiniteMass(JsonObject i, JsonObject p)
    {
        double v = J.NumReq(i, "v_m_s", "parachute-opening-infinite-mass"), rho = J.NumReq(i, "rho_kg_m3", "parachute-opening-infinite-mass");
        double d0 = J.NumReq(i, "d0_m", "parachute"), cd0 = J.NumReq(i, "cd0", "parachute");
        double tf = J.Num(i, "fillConstant", 8) * d0 / v;
        var scn = new JsonObject
        {
            ["schema"] = "mns.scenario/1",
            ["sim"] = new JsonObject { ["dt"] = Math.Min(0.002, tf / 2000), ["tEnd"] = 1.3 * tf + 0.2 },
            ["environment"] = new JsonObject { ["constantDensity"] = rho, ["gravity"] = 0 },
            ["bodies"] = new JsonArray(new JsonObject
            {
                ["name"] = "b", ["mass"] = 1e7, ["inertia"] = Arr(1e7, 1e7, 1e7), ["aero"] = "none",
                ["position"] = Arr(0, 0, -1000), ["velocity"] = Arr(v, 0, 0),
                ["devices"] = new JsonArray(ChuteDevice(i, 0)),
            }),
            ["events"] = DeployAtZero(),
        };
        var w = BuildWorld(scn);
        w.Run();
        var chute = (Parachute)w.GetBody("b").Devices[0];
        string label = J.Str(p, "label", "");
        yield return new KindOutput(label + " 최대 하중", null, chute.MaxForce, "N");
        double q = 0.5 * rho * v * v, s0 = Math.PI / 4 * d0 * d0;
        if (J.NumOpt(i, "measuredCx") is { } cx)
            yield return new KindOutput(label + " 개방하중 계수 Cx", cx, chute.MaxForce / (q * cd0 * s0), "-") { Role = "info", Note = "Cx = 최대 하중/(q·CD0·S0), 참고" };
    }

    /// <summary>
    /// 유한 질량 비행시험 전개: 탑재체(+낙하산) 질량을 배치 고도·속도·경로각에서 출발시켜 전개 → 최대 하중·시각.
    /// 탑재체 자체 항력은 생략(전개 수 초 동안 낙하산 하중 대비 작음). 배치 마하수가 낙하산 모델 범위(M ≤ 0.5)를 넘으면 외삽.
    /// </summary>
    private static IEnumerable<KindOutput> ParachuteDeploymentFlight(JsonObject i, JsonObject p)
    {
        double mass = J.NumReq(i, "mass_kg", "parachute-deployment-flight"), alt = J.NumReq(i, "altitude_m", "parachute-deployment-flight");
        double v = J.NumReq(i, "speed_mps", "parachute-deployment-flight"), gamma = J.Num(i, "flightPathDeg", 0) * Math.PI / 180;
        double ls = J.Num(i, "lineStretchTime_s", 0.3);
        var atm = new Atmosphere { Geopotential = true }.At(alt);
        double mach = v / atm.SpeedOfSound;
        var scn = new JsonObject
        {
            ["schema"] = "mns.scenario/1",
            ["sim"] = new JsonObject { ["dt"] = 0.0005, ["tEnd"] = J.Num(i, "duration_s", 3) },
            ["environment"] = new JsonObject { ["geopotentialAltitude"] = true },
            ["bodies"] = new JsonArray(new JsonObject
            {
                ["name"] = "b", ["mass"] = mass, ["inertia"] = Arr(5, 5, 5), ["aero"] = "none",
                ["position"] = Arr(0, 0, -alt), ["velocity"] = Arr(v * Math.Cos(gamma), 0, -v * Math.Sin(gamma)),
                ["devices"] = new JsonArray(ChuteDevice(i, ls)),
            }),
            ["events"] = DeployAtZero(),
        };
        var w = BuildWorld(scn);
        w.Run();
        var chute = (Parachute)w.GetBody("b").Devices[0];
        bool inEnv = mach <= Parachute.MaxValidMach;
        string note = string.Format(CultureInfo.InvariantCulture, "배치 M {0:0.00}, ρ {1:G3} kg/m³{2}", mach, atm.Density,
            inEnv ? "" : $" — 비압축 낙하산 모델 범위(M ≤ {Parachute.MaxValidMach}) 밖");
        string label = J.Str(p, "label", "");
        yield return new KindOutput(label + " 최대 하중", null, chute.MaxForce, "N") { Judged = inEnv, Note = note };
        if (J.NumOpt(i, "peakTime_s") is { } tp)
            yield return new KindOutput(label + " 최대 하중 시각", tp, chute.MaxForceTime, "s") { ToleranceAbs = J.Num(i, "peakTimeTolerance_s", 0.1), Judged = inEnv, Note = note };
    }

    // ---------------- 항력 ----------------

    private static bool ReInEnvelope(JsonObject i, out string note)
    {
        note = "";
        if (J.NumOpt(i, "re") is not { } re) return true;
        bool ok = re <= Shape.MaxValidReynolds;
        note = string.Format(CultureInfo.InvariantCulture, "Re {0:0.##E+0}{1}", re, ok ? "" : $" > L1 형상 Cd 적용 한계 {Shape.MaxValidReynolds:0.#E+0}(항력 위기)");
        return ok;
    }

    /// <summary>L1 형상 항력계수: Shape.Projected(유입방향).Cd. 입력 re 가 있으면 L1 적용 범위(아임계) 판정.</summary>
    private static IEnumerable<KindOutput> DragCoefficient(JsonObject i, JsonObject p)
    {
        var shape = ScenarioLoader.BuildShape(i["shape"]!.AsObject(), null!, null, Mat3.Identity);
        var dir = Vec3.FromArray(J.Arr(i["flowDirection"] ?? new JsonArray(1, 0, 0))).Normalized();
        bool ok = ReInEnvelope(i, out var note);
        yield return new KindOutput(J.Str(p, "label", ""), null, shape.Projected(dir).Cd, "-") { Judged = ok, Note = note };
    }

    /// <summary>
    /// 구 공중 자유낙하(Bacon & Reid 1924): 엔진에서 L1 구(형상 Cd)를 낙하 고도에서 정지 낙하시켜 최대 속도를 얻는다.
    /// 측정 최대 속도의 Re 가 L1 적용 범위를 넘으면 외삽.
    /// </summary>
    private static IEnumerable<KindOutput> SphereFreeFall(JsonObject i, JsonObject p)
    {
        double m = J.NumReq(i, "mass_kg", "sphere-free-fall"), d = J.NumReq(i, "diameter_m", "sphere-free-fall");
        double h = J.NumReq(i, "dropHeight_m", "sphere-free-fall");
        var scn = new JsonObject
        {
            ["schema"] = "mns.scenario/1",
            ["sim"] = new JsonObject { ["dt"] = 0.005, ["tEnd"] = 200 },
            ["bodies"] = new JsonArray(new JsonObject
            {
                ["name"] = "b", ["mass"] = m, ["shape"] = new JsonObject { ["type"] = "sphere", ["radius"] = d / 2 },
                ["aero"] = "shape", ["position"] = Arr(0, 0, -h),
            }),
        };
        var w = BuildWorld(scn);
        var b = w.GetBody("b");
        double vmax = 0;
        while (!w.Stopped) { w.Step(); if (b.Mode == BodyMode.Free) vmax = Math.Max(vmax, b.V.Norm); }
        double vMeas = J.NumReq(p, "measured", "sphere-free-fall");
        var a0 = new Atmosphere().At(0);
        double re = a0.Density * vMeas * d / a0.Viscosity;
        var ii = new JsonObject { ["re"] = re };
        bool ok = ReInEnvelope(ii, out var note);
        yield return new KindOutput(J.Str(p, "label", ""), null, vmax, "m/s") { Judged = ok, Note = note };
    }

    /// <summary>
    /// 텀블링 원통 평균 항력(Britcher 외, 자기부상 풍동): 축에 수직인 축으로 회전하는 원통의 L1 투영 항력을 회전각 평균,
    /// 기준면적 = 원형 단면 πD²/4. (마그누스·자동회전 효과는 L1 에 없음.)
    /// </summary>
    private static IEnumerable<KindOutput> TumblingMeanDrag(JsonObject i, JsonObject p)
    {
        double d = J.NumReq(i, "diameter_m", "tumbling-mean-drag"), l = J.NumReq(i, "length_m", "tumbling-mean-drag");
        var shape = new Shape { Kind = ShapeKind.Cylinder, Radius = d / 2, Length = l, Axis = 0 };
        const int n = 720;
        double sum = 0;
        for (int k = 0; k < n; k++)
        {
            double th = 2 * Math.PI * (k + 0.5) / n;
            var (a, cd) = shape.Projected(new Vec3(Math.Cos(th), Math.Sin(th), 0));
            sum += a * cd;
        }
        bool ok = ReInEnvelope(i, out var note);
        yield return new KindOutput(J.Str(p, "label", ""), null, sum / n / (Math.PI / 4 * d * d), "-") { Judged = ok, Note = note };
    }

    /// <summary>
    /// 2-D 원통 횡류 CD(Re, M)(FR-REL-06 ①): <see cref="CylinderCrossflow.Cd2D(double, double, out bool)"/> — aero.crossflow "tr-r300" 의 표.
    /// 표 범위 밖(M &gt; 0.6 등)이면 외삽.
    /// </summary>
    private static IEnumerable<KindOutput> CylinderCrossflow2D(JsonObject i, JsonObject p)
    {
        double re = J.NumReq(i, "re", "cylinder-crossflow-2d"), m = J.NumReq(i, "mach", "cylinder-crossflow-2d");
        double cd = CylinderCrossflow.Cd2D(re, m, out bool outside);
        yield return new KindOutput(J.Str(p, "label", ""), null, cd, "-")
        {
            Judged = !outside,
            Note = outside ? string.Format(CultureInfo.InvariantCulture, "표 범위 밖(M ≤ {0}, Re ≤ {1:0.#E+0})", CylinderCrossflow.MaxMach, CylinderCrossflow.MaxRe) : "",
        };
    }

    // ---------------- 관입 ----------------

    /// <summary>Young 관입 깊이(FR-GND-02). 관입체 무게가 원문 적용 하한(토양 5 lb) 미만이면 외삽.</summary>
    private static IEnumerable<KindOutput> YoungPenetration(JsonObject i, JsonObject p)
    {
        double mass = J.NumReq(i, "mass_kg", "young-penetration");
        double area = J.NumOpt(i, "area_m2") ?? Math.PI / 4 * Math.Pow(J.NumReq(i, "diameter_m", "young-penetration"), 2);
        double dEq = Math.Sqrt(4 * area / Math.PI);
        double n = J.NumOpt(i, "noseN") ?? Penetration.NoseN(J.Str(i, "nose", "flat"),
            J.NumOpt(i, "lOverD") ?? (J.NumOpt(i, "noseLength_m") is { } nl ? nl / dEq : 0));
        double s = J.NumReq(i, "soilS", "young-penetration");
        var (d, g) = Penetration.Young(mass, area, J.NumReq(i, "speed_mps", "young-penetration"), s, n);
        bool ok = mass >= Penetration.MinValidMassSoil - 1e-9;
        string note = string.Format(CultureInfo.InvariantCulture, "S {0:0.##}, N {1:0.###}, 평균 {2:0} g{3}", s, n, g,
            ok ? "" : $" — 질량 {mass:0.##} kg < 원문 하한 {Penetration.MinValidMassSoil:0.##} kg(5 lb)");
        yield return new KindOutput(J.Str(p, "label", ""), null, d, "m") { Judged = ok, Note = note };
    }

    /// <summary>표준대기 표: quantity = density | pressure | temperature. geopotential=true 면 기하고도 → 지오퍼텐셜 변환.</summary>
    private static (double, string) AtmosphereTable(JsonObject i, JsonObject p)
    {
        var a = new Atmosphere { Geopotential = J.Bool(i, "geopotential", false) }.At(J.NumReq(i, "altitude_m", "atmosphere-table"));
        return J.Str(i, "quantity", "density") switch
        {
            "density" => (a.Density, "kg/m³"),
            "pressure" => (a.Pressure, "Pa"),
            "temperature" => (a.Temperature, "K"),
            var q => throw new InvalidDataException($"atmosphere-table quantity: {q}")
        };
    }

    // ---------------- 로터 ----------------

    /// <summary>
    /// VRS 하강 추력비(FR-UAV-02 v2, VV-43): 프로펠러 한 개의 CT/CT0–J 곡선 전체를 RotorModel 로 계산해
    /// RMS 오차(허용 0.1)와 최저점 J 위치(허용 ±0.1)를 판정. 측정 최저점 깊이 &lt; 5 % 면 위치가 정의되지 않아 위치 판정 제외.
    /// </summary>
    private static IEnumerable<KindOutput> RotorVrsCtRatio(JsonObject i, JsonObject p)
    {
        double sig = J.NumReq(i, "solidity", "rotor-vrs-ct-ratio"), ct0 = J.NumReq(i, "ct0", "rotor-vrs-ct-ratio");
        var rows = i["data"]!.AsArray().Select(r => J.Arr(r!)).ToList(); // [J, CT/CT0]
        var pred = rows.Select(r => RotorModel.CtRatioAtJ(r[0], sig, ct0)).ToList();
        double rms = Math.Sqrt(rows.Select((r, k) => (pred[k] - r[1]) * (pred[k] - r[1])).Average());
        double xMax = rows.Max(r => Math.Abs(r[0])) / Math.Sqrt(2 * ct0 / Math.PI);
        bool inEnv = xMax <= RotorModel.MaxValidDescentRatio + 1e-9;
        string envNote = inEnv ? "" : $"V/vh 최대 {xMax:0.00} > 모델 범위 {RotorModel.MaxValidDescentRatio}";
        string id = J.Str(p, "label", "");
        yield return new KindOutput($"{id} RMS(CT/CT0)", 0, rms, "-") { ToleranceAbs = 0.1, Judged = inEnv, Note = $"{rows.Count}점 " + envNote };
        int km = Enumerable.Range(0, rows.Count).MinBy(k => rows[k][1]);
        int kp = Enumerable.Range(0, rows.Count).MinBy(k => pred[k]);
        double depth = 1 - rows[km][1];
        bool locJudged = inEnv && depth >= 0.05;
        yield return new KindOutput($"{id} 최저점 J", rows[km][0], rows[kp][0], "-")
        {
            ToleranceAbs = 0.1, Judged = locJudged,
            Note = depth >= 0.05 ? "" : string.Format(CultureInfo.InvariantCulture, "측정 최저점 깊이 {0:0.0}% < 5% — 위치가 정의되지 않아 판정 제외", depth * 100),
        };
        yield return new KindOutput($"{id} 최저 CT/CT0", rows[km][1], pred[kp], "-")
        { ToleranceAbs = 0.08, Role = "info", Note = "참고(VV-43 판정 지표 아님)" };
    }

    /// <summary>정적 CT–RPM(VV-44): RotorModel.StaticCt(지수 p) 판정 + 일정 k_T(p=0) 참고 줄.</summary>
    private static IEnumerable<KindOutput> PropellerStaticCt(JsonObject i, JsonObject p)
    {
        double ctRef = J.NumReq(i, "ctRef", "propeller-static-ct"), rpmRef = J.NumReq(i, "rpmRef", "propeller-static-ct"), rpm = J.NumReq(i, "rpm", "propeller-static-ct");
        string label = J.Str(p, "label", "");
        yield return new KindOutput(label, null, RotorModel.StaticCt(ctRef, rpmRef, rpm), "-");
        yield return new KindOutput(label + " (일정 k_T)", null, ctRef, "-") { Role = "info", Note = "참고: CT 를 기준 RPM 값으로 고정(T = k_T·ω²)" };
    }

    // ---------------- NASA 6-DOF check-case ----------------

    private static double NasaVar(Body b, string name)
    {
        var e = b.Q.ToEuler() * (180 / Math.PI);
        var w = b.W * (180 / Math.PI);
        return name switch
        {
            "altitudeMsl_ft" => -b.R.Z / Ft,
            "feVelocity_ft_s_X" => b.V.X / Ft,
            "feVelocity_ft_s_Y" => b.V.Y / Ft,
            "feVelocity_ft_s_Z" => b.V.Z / Ft,
            "eulerAngle_deg_Roll" => e.X,
            "eulerAngle_deg_Pitch" => e.Y,
            "eulerAngle_deg_Yaw" => e.Z,
            "bodyAngularRateWrtEi_deg_s_Roll" => w.X,
            "bodyAngularRateWrtEi_deg_s_Pitch" => w.Y,
            "bodyAngularRateWrtEi_deg_s_Yaw" => w.Z,
            _ => throw new InvalidDataException($"nasa-checkcase 변수 미지원: {name}")
        };
    }

    private static string NasaUnit(string name) => name.Contains("_deg_s_") ? "deg/s" : name.Contains("_deg_") ? "deg" : name.Contains("_ft_s_") ? "ft/s" : "ft";

    /// <summary>
    /// NASA/TM-2015-218675 대기권 check-case 1~8(VV-05/VV-48): 정의대로 시나리오를 만들어 엔진으로 30 s 적분하고,
    /// 기준 도구들의 평균 시간이력과 변수별 최대 편차(0–30 s)를 절대 허용치로 판정한다.
    /// </summary>
    private static IEnumerable<KindOutput> NasaCheckcase(JsonObject i, JsonObject p)
    {
        var veh = i["vehicle"]!.AsObject();
        double cd = J.Num(veh, "cd", 0);
        JsonNode aero = "none";
        if (cd > 0 || veh["damping"] is not null)
        {
            var a = new JsonObject { ["cd"] = cd, ["area"] = J.NumReq(veh, "area", "vehicle") };
            if (veh["damping"] is JsonNode dmp) a["damping"] = dmp.DeepClone();
            if (veh["dampingLengths"] is JsonNode dl) a["dampingLengths"] = dl.DeepClone();
            aero = a;
        }
        var wdeg = J.Arr(i["omegaInertial_deg_s"] ?? Arr(0, 0, 0));
        var env = new JsonObject { ["geopotentialAltitude"] = true, ["earth"] = i["earth"]!.DeepClone() };
        if (i["windProfile"] is JsonArray wp) env["wind"] = new JsonObject { ["profile"] = wp.DeepClone() };
        double dur = J.Num(i, "duration_s", 30);
        var scn = new JsonObject
        {
            ["schema"] = "mns.scenario/1",
            ["sim"] = new JsonObject { ["dt"] = J.Num(i, "dt", 0.005), ["tEnd"] = dur },
            ["environment"] = env,
            ["bodies"] = new JsonArray(new JsonObject
            {
                ["name"] = "b", ["mass"] = J.NumReq(veh, "mass", "vehicle"), ["inertia"] = veh["inertia"]!.DeepClone(), ["aero"] = aero,
                ["position"] = Arr(0, 0, -J.NumReq(i, "altitude_m", "nasa-checkcase")), ["velocity"] = Arr(0, 0, 0),
                ["omega"] = Arr(wdeg.Select(x => x * Math.PI / 180).ToArray()), ["attitude"] = Arr(0, 0, 0),
            }),
        };
        var w = BuildWorld(scn);
        var b = w.GetBody("b");
        var refObj = i["reference"]!.AsObject();
        var ts = J.Arr(refObj["t"]!);
        var vars = refObj["vars"]!.AsObject();
        var sim = vars.ToDictionary(kv => kv.Key, _ => new double[ts.Length]);
        for (int k = 0; k < ts.Length; k++)
        {
            while (w.T < ts[k] - 1e-6 && !w.Stopped) w.Step();
            foreach (var name in sim.Keys) sim[name][k] = NasaVar(b, name);
        }
        string label = J.Str(p, "label", "");
        foreach (var (name, node) in vars)
        {
            var v = node!.AsObject();
            var mean = J.Arr(v["mean"]!);
            var lo = v["lo"] is JsonNode ln ? J.Arr(ln) : mean;
            var hi = v["hi"] is JsonNode hn ? J.Arr(hn) : mean;
            bool angle = NasaUnit(name) == "deg";
            int kMax = 0; double dMax = -1;
            for (int k = 0; k < ts.Length; k++)
            {
                double dd = sim[name][k] - mean[k];
                if (angle) dd = Quat.WrapPi(dd * Math.PI / 180) * 180 / Math.PI;
                if (Math.Abs(dd) > dMax) { dMax = Math.Abs(dd); kMax = k; }
            }
            double pr = sim[name][kMax];
            if (angle) pr = mean[kMax] + Quat.WrapPi((pr - mean[kMax]) * Math.PI / 180) * 180 / Math.PI;
            yield return new KindOutput(string.Format(CultureInfo.InvariantCulture, "{0} {1} (최대 편차 t={2:0.#} s)", label, name, ts[kMax]),
                mean[kMax], pr, NasaUnit(name))
            {
                ToleranceAbs = J.NumReq(v, "tol", name),
                Note = string.Format(CultureInfo.InvariantCulture, "기준 {0}개 도구 범위 [{1:G7}, {2:G7}]", J.Num(v, "n", 0), lo[kMax], hi[kMax]),
            };
        }
    }
}
