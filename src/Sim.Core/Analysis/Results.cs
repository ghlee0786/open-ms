using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Sim.Core.Engine;
using Sim.Core.Geometry;
using Sim.Core.Models;
using Sim.Core.Numerics;
using Sim.Core.Scenario;

namespace Sim.Core.Analysis;

/// <summary>결과 요약(summary.json)·시간이력(CSV)·이벤트·장면(scene.json) 출력(FR-EXP-01, FR-ARC-06).</summary>
public static class Results
{
    public static string EngineVersion =>
        typeof(Results).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.1.0";

    public static JsonObject Provenance(ScenarioDoc doc, World w, string? gitHash = null) => new()
    {
        ["engine"] = "Sim.Core " + EngineVersion,
        ["gitHash"] = gitHash ?? System.Environment.GetEnvironmentVariable("MNS_GIT_HASH") ?? "unknown",
        ["scenarioFile"] = doc.FilePath,
        ["scenarioHash"] = doc.Hash(),
        ["seed"] = (double)w.Seed,
        ["dt_s"] = w.Dt,
        ["integrator"] = "RK4 fixed-step + event bisection",
        ["generatedAt"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ssK", CultureInfo.InvariantCulture),
    };

    public static JsonObject Summary(ScenarioDoc doc, World w, string? gitHash = null)
    {
        var s = new JsonObject
        {
            ["schema"] = "mns.summary/1",
            ["scenario"] = doc.Name,
            ["provenance"] = Provenance(doc, w, gitHash),
            ["endTime_s"] = w.T,
            ["stopReason"] = w.StopReason,
            ["steps"] = w.StepCount,
            ["suspect"] = w.Suspect,
        };
        var bodies = new JsonObject();
        foreach (var b in w.Bodies)
        {
            if (b.Mode == BodyMode.Kinematic) continue;
            var o = new JsonObject
            {
                ["mode"] = b.Mode.ToString(),
                ["mass_kg"] = b.OwnMass,
                ["massSource"] = b.MassSource,
                ["aeroFidelity"] = b.Aero?.Fidelity ?? "none",
                ["releaseTime_s"] = Parachute.Num(b.ReleaseTime),
                ["maxLoadG"] = b.MaxLoadG,
                ["maxLoadTime_s"] = Parachute.Num(b.MaxLoadTime),
            };
            if (b.ModelSource is { } ms) o["modelSource"] = ms;
            if (b.Aero is { } ae && ae.MaxMach > 0)
            {
                o["maxMach"] = ae.MaxMach;
                if (ae.MachCorrection) o["machCorrection"] = ae.MaxMach > AeroModel.MaxValidMach ? "범위 이탈(M>0.7, 상한 고정)" : "적용(둔체 압축성 배율)";
            }
            if (b.Aero?.Cp is { } cp)
            {
                o["cp_brf_m"] = J.ToJson(cp);
                o["staticMargin"] = b.StaticMargin is { } sm ? sm : null;
            }
            if (b.Penetrator is { } pen)
                o["penetrator"] = new JsonObject { ["area_m2"] = pen.Area, ["diameter_m"] = pen.Diameter, ["noseN"] = b.NoseCoefficient, ["source"] = pen.Source };
            if (b.Impact is { } imp)
            {
                o["impact"] = new JsonObject
                {
                    ["time_s"] = imp.Time,
                    ["n_m"] = imp.Position.X, ["e_m"] = imp.Position.Y,
                    ["horizontalDistanceFromOrigin_m"] = imp.Position.HorizontalNorm,
                    ["speed_mps"] = imp.Velocity.Norm,
                    ["verticalSpeed_mps"] = imp.Velocity.Z,
                    ["roll_deg"] = imp.EulerDeg.X, ["pitch_deg"] = imp.EulerDeg.Y, ["yaw_deg"] = imp.EulerDeg.Z,
                    ["noseAngleFromVertical_deg"] = imp.NoseAngleDeg,
                    ["youngPenetration_m"] = imp.PenetrationDepth,
                    ["youngMeanDecel_g"] = imp.PenetrationG,
                    ["youngVerticalPenetration_m"] = imp.PenetrationVerticalDepth,
                    ["youngArea_m2"] = imp.PenetrationArea,
                    ["youngNoseN"] = imp.PenetrationNoseN,
                    ["youngAreaSource"] = imp.PenetrationSource,
                    ["youngInEnvelope"] = imp.Mass >= Models.Penetration.MinValidMassSoil,
                    ["soilS"] = w.Env.Soil.S,
                    ["angleOfAttack_deg"] = imp.AngleOfAttackDeg,
                    ["mass_kg"] = imp.Mass,
                };
            }
            bodies[b.Name] = o;
        }
        s["bodies"] = bodies;

        var devices = new JsonObject();
        foreach (var b in w.Bodies)
            foreach (var d in b.Devices) { var o = new JsonObject(); d.Summarize(o); devices[d.FullName] = o; }
        s["devices"] = devices;
        var cons = new JsonObject();
        foreach (var c in w.Connectors) { var o = new JsonObject(); c.Summarize(o); cons[c.Name] = o; }
        s["connectors"] = cons;
        s["metrics"] = Metrics(doc, w);
        if (w.Collisions is { } col) s["collisions"] = col.ToJson();
        s["events"] = new JsonArray(w.EventLog.Select(e => (JsonNode)new JsonObject { ["t"] = e.Time, ["name"] = e.Name, ["message"] = e.Message }).ToArray());
        s["warnings"] = new JsonArray(w.Warnings.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());
        s["assumptions"] = new JsonArray(Assumptions.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());
        return s;
    }

    public static readonly string[] Assumptions =
    [
        "평면 지구·일정 중력(FR-DYN-05 기본). 선택 environment.earth(WGS-84 J2·역제곱 중력, 지구 자전)는 NASA check-case 1~8 로 검증.",
        "낙하산은 결속점에 항력이 작용하는 장치(별도 물체 아님). 겉보기 질량은 선택(apparentMassK0, 유입 항만; Cruz 2003 풍동으로 보정·검증). 비압축(M ≤ 0.5).",
        "물체 간 공력 간섭 무시(FR-AER-09).",
        "Dryden 난류: u 정확, v/w 는 1차 근사, NED 성분 적용, 물체별 독립 필터.",
        "L1 공력: 형상 근사 Cd·투영면적(박스 1.05, 원통 축 0.82/횡 유한길이 표 0.64~1.2, 구 0.47) — 아임계 Re ≤ 2×10⁵ 에서만 유효(항력 위기 미반영, 공개 자료로 확인).",
        "지면 충돌 시 물체 정지(튐·전복 미계산), 관입은 Young 식(SAND97-2426 식 3.1–3.3, 원문 계수 대조 완료, 관입체 단면 기준). 관입체 < 2.27 kg(5 lb)는 원식 적용 범위 밖.",
        "메시 형상의 L1 압력중심 기본값 = 측면 투영 면적 중심(FX-03). 날개 달린 물체는 aero.cp:\"component\"(Barrowman 성분 합성 CP·CNα, FX-07) 권장 — 시험·CFD 보정 전.",
        "레일 분리: 레일 위 상대운동은 스텝 단위 반암시적 적분, 운동량 결합은 근사.",
        "멀티로터: 간이 캐스케이드 제어기, VRS 손실은 선택(vrsModel: gaussian(v1) | uiuc-v2(UIUC 자료 보정·검증, 9–11 in)).",
        "둔체 마하 보정(aero.machCorrection): Lindsey NACA TR-619·Hoerner 개략 배율(M 0.3~0.7), 외피 조각 Cd = 반원통 껍질 Hoerner 값 축소 근사 — 검증 전(가정).",
        "힌지·충돌: 펜널티 힌지(볼조인트+축 정렬), 충돌은 대표점-형상 내부 판정 + 충격량 접촉(근사) — 물체 간 충돌 역학은 판정 중심(PRD 범위 밖).",
        "하중·응력 색 표시는 강체 하중 기반 근사(수정 뉴턴 Cp, 길이축 보 + 관성 완화) — FEA 아님.",
    ];

    private static JsonObject Metrics(ScenarioDoc doc, World w)
    {
        var m = new JsonObject();
        var defs = doc.Root["metrics"] as JsonArray;
        if (defs is null) return m;
        foreach (var dn in defs)
        {
            var d = dn!.AsObject();
            var type = J.Str(d, "type", "");
            var name = J.Str(d, "name", type);
            var names = d["bodies"] is JsonArray ba ? ba.Select(x => x!.GetValue<string>()).ToList()
                : w.Bodies.Where(b => b.Mode != BodyMode.Kinematic).Select(b => b.Name).ToList();
            var idx = names.Select(n => w.GetBody(n).Index).ToArray();
            switch (type)
            {
                case "minSeparation":
                {
                    double best = double.PositiveInfinity, bt = double.NaN; string pair = "";
                    // 분리 직후(부모-자식이 맞닿아 있는 구간)는 제외
                    double window = J.Num(d, "ignoreAfterSeparation", 1.0);
                    var rel = idx.Select(k => w.Bodies[k].ReleaseTime).ToArray();
                    foreach (var f in w.Recorder.Frames)
                        for (int i = 0; i < idx.Length; i++)
                            for (int j = i + 1; j < idx.Length; j++)
                            {
                                var a = f.Bodies[idx[i]]; var b = f.Bodies[idx[j]];
                                if (a.Mode == BodyMode.Stowed || b.Mode == BodyMode.Stowed) continue;
                                if (a.Mode == BodyMode.Landed && b.Mode == BodyMode.Landed) continue;
                                if (f.T - rel[i] < window || f.T - rel[j] < window) continue;
                                double dist = (a.Cg - b.Cg).Norm;
                                if (dist < best) { best = dist; bt = f.T; pair = $"{names[i]}–{names[j]}"; }
                            }
                    m[name] = new JsonObject { ["type"] = type, ["min_m"] = double.IsFinite(best) ? best : null, ["time_s"] = Parachute.Num(bt), ["pair"] = pair };
                    break;
                }
                case "landingSeparation":
                {
                    double best = double.PositiveInfinity; string pair = "";
                    var bodies = names.Select(w.GetBody).ToList();
                    for (int i = 0; i < bodies.Count; i++)
                        for (int j = i + 1; j < bodies.Count; j++)
                        {
                            if (bodies[i].Impact is not { } a || bodies[j].Impact is not { } b) continue;
                            double dist = (a.Position - b.Position).HorizontalNorm;
                            if (dist < best) { best = dist; pair = $"{names[i]}–{names[j]}"; }
                        }
                    double target = J.Num(d, "target", double.NaN);
                    m[name] = new JsonObject
                    {
                        ["type"] = type, ["minHorizontal_m"] = double.IsFinite(best) ? best : null, ["pair"] = pair,
                        ["target_m"] = Parachute.Num(target),
                        ["pass"] = double.IsFinite(target) && double.IsFinite(best) ? best >= target : null,
                    };
                    break;
                }
                case "maxLoad":
                {
                    // 매 스텝 추적값(출력 주기와 무관): 짧은 충격 펄스·개방 충격의 최대 G (FR-SHL-06)
                    var arr = new JsonArray();
                    double best = 0; string who = "";
                    foreach (var n in names)
                    {
                        var b = w.GetBody(n);
                        arr.Add(new JsonObject { ["body"] = n, ["maxG"] = b.MaxLoadG, ["time_s"] = Parachute.Num(b.MaxLoadTime) });
                        if (b.MaxLoadG > best) { best = b.MaxLoadG; who = n; }
                    }
                    double limit = J.Num(d, "limitG", double.NaN);
                    m[name] = new JsonObject
                    {
                        ["type"] = type, ["maxG"] = best, ["body"] = who, ["bodies"] = arr, ["limitG"] = Parachute.Num(limit),
                        ["pass"] = double.IsFinite(limit) ? best <= limit : null,
                    };
                    break;
                }
                case "debrisNearDeploy":
                {
                    // 낙하산 전개 시점 주변 잔해 존재 여부: 전개 후 window 동안 잔해 CG ↔ 캐노피 중심 최소 거리
                    double window = J.Num(d, "window", 3.0), risk = J.Num(d, "riskDistance", 5.0);
                    var debris = d["debris"] is JsonArray da ? da.Select(x => w.GetBody(x!.GetValue<string>())).ToList() : new List<Body>();
                    var devs = d["devices"] is JsonArray dv ? dv.Select(x => x!.GetValue<string>()).ToList() : new List<string>();
                    var arr = new JsonArray();
                    int atRisk = 0;
                    foreach (var devName in devs)
                    {
                        int dot = devName.LastIndexOf('.');
                        if (dot <= 0) throw new InvalidDataException($"debrisNearDeploy: 장치는 'body.device' 형식이어야 합니다: {devName}");
                        var owner = w.GetBody(devName[..dot]);
                        var ch = owner.Devices.OfType<Parachute>().FirstOrDefault(p => p.Name == devName[(dot + 1)..])
                                 ?? throw new InvalidDataException($"debrisNearDeploy: 낙하산 '{devName}' 이 없습니다.");
                        if (!double.IsFinite(ch.DeployTime)) { arr.Add(new JsonObject { ["device"] = devName, ["deployed"] = false }); continue; }
                        double bestD = double.PositiveInfinity, bestT = double.NaN; string bestB = "";
                        foreach (var f in w.Recorder.Frames)
                        {
                            if (f.T < ch.DeployTime || f.T > ch.DeployTime + window) continue;
                            var s = f.Devices.FirstOrDefault(x => x.Name == devName);
                            if (s.Name is null) continue;
                            var c = s.Visible ? s.Tip : s.Anchor;
                            foreach (var b in debris)
                            {
                                var bs = f.Bodies[b.Index];
                                if (bs.Mode == BodyMode.Stowed) continue;
                                double dist = (bs.Cg - c).Norm;
                                if (dist < bestD) { bestD = dist; bestT = f.T; bestB = b.Name; }
                            }
                        }
                        bool r = bestD < risk;
                        if (r) atRisk++;
                        arr.Add(new JsonObject
                        {
                            ["device"] = devName, ["deployed"] = true, ["deployTime_s"] = ch.DeployTime,
                            ["minDistance_m"] = double.IsFinite(bestD) ? bestD : null, ["time_s"] = Parachute.Num(bestT), ["debris"] = bestB, ["atRisk"] = r,
                        });
                    }
                    m[name] = new JsonObject { ["type"] = type, ["window_s"] = window, ["riskDistance_m"] = risk, ["atRisk"] = atRisk, ["devices"] = arr };
                    break;
                }
                default:
                    m[name] = OperationalMetrics.TryCompute(type, d, w, names) ?? new JsonObject { ["type"] = type, ["error"] = "알 수 없는 metric" };
                    break;
            }
        }
        return m;
    }

    /// <summary>시간이력 CSV(긴 형식: 프레임×물체 1행). 변수 사전은 헤더 단위 표기.</summary>
    public static void WriteTimeseriesCsv(World w, string path)
    {
        using var sw = new StreamWriter(path, false, new UTF8Encoding(false));
        sw.WriteLine("t_s,body,mode,n_m,e_m,d_m,alt_agl_m,origin_n_m,origin_e_m,origin_d_m,qw,qx,qy,qz,roll_deg,pitch_deg,yaw_deg,vn_mps,ve_mps,vd_mps,speed_mps,airspeed_mps,p_dps,q_dps,r_dps,load_g");
        var inv = CultureInfo.InvariantCulture;
        foreach (var f in w.Recorder.Frames)
        {
            for (int i = 0; i < f.Bodies.Length; i++)
            {
                var b = f.Bodies[i];
                var body = w.Bodies[i];
                var e = b.Q.ToEuler() * (180 / Math.PI);
                var om = b.Omega * (180 / Math.PI);
                sw.WriteLine(string.Format(inv,
                    "{0:F4},{1},{2},{3:F4},{4:F4},{5:F4},{6:F4},{7:F4},{8:F4},{9:F4},{10:F7},{11:F7},{12:F7},{13:F7},{14:F3},{15:F3},{16:F3},{17:F4},{18:F4},{19:F4},{20:F4},{21:F4},{22:F3},{23:F3},{24:F3},{25:F4}",
                    f.T, body.Name, b.Mode, b.Cg.X, b.Cg.Y, b.Cg.Z, w.Env.Agl(b.Cg.Z), b.Origin.X, b.Origin.Y, b.Origin.Z,
                    b.Q.W, b.Q.X, b.Q.Y, b.Q.Z, e.X, e.Y, e.Z, b.Velocity.X, b.Velocity.Y, b.Velocity.Z, b.Velocity.Norm, b.Airspeed,
                    om.X, om.Y, om.Z, b.LoadG));
            }
        }
    }

    /// <summary>장치·연결 시간이력 CSV(낙하산 충만비·힘, 테더 장력).</summary>
    public static void WriteDevicesCsv(World w, string path)
    {
        using var sw = new StreamWriter(path, false, new UTF8Encoding(false));
        sw.WriteLine("t_s,element,type,visible,fill,force_or_tension_N,anchor_n,anchor_e,anchor_d,tip_n,tip_e,tip_d,size_m");
        var inv = CultureInfo.InvariantCulture;
        foreach (var f in w.Recorder.Frames)
        {
            foreach (var d in f.Devices)
                sw.WriteLine(string.Format(inv, "{0:F4},{1},{2},{3},{4:F4},{5:F2},{6:F3},{7:F3},{8:F3},{9:F3},{10:F3},{11:F3},{12:F3}",
                    f.T, d.Name, d.Type, d.Visible ? 1 : 0, d.Fill, d.Force, d.Anchor.X, d.Anchor.Y, d.Anchor.Z, d.Tip.X, d.Tip.Y, d.Tip.Z, d.Size));
            foreach (var c in f.Connectors)
                sw.WriteLine(string.Format(inv, "{0:F4},{1},{2},{3},{4:F4},{5:F2},{6:F3},{7:F3},{8:F3},{9:F3},{10:F3},{11:F3},{12:F3}",
                    f.T, c.Name, c.Type, c.Visible ? 1 : 0, 0.0, c.Tension, c.A.X, c.A.Y, c.A.Z, c.B.X, c.B.Y, c.B.Z, 0.0));
        }
    }

    /// <summary>장면 정의(scene.json): 물체별 메시 경로·형상·색 — Blender 가져오기·외부 뷰어용.</summary>
    public static JsonObject Scene(World w)
    {
        var bodies = new JsonArray();
        foreach (var b in w.Bodies)
        {
            var o = new JsonObject { ["name"] = b.Name, ["color"] = b.Color, ["kinematic"] = b.Mode == BodyMode.Kinematic };
            if (b.Shape is { } s)
            {
                o["shape"] = s.Kind.ToString().ToLowerInvariant();
                o["center"] = J.ToJson(s.Center);
                o["size"] = J.ToJson(s.Size);
                o["radius"] = s.Radius; o["length"] = s.Length; o["axis"] = s.Axis;
                if (s.Kind == ShapeKind.ShellSegment) { o["thickness"] = s.Thickness; o["span"] = s.SpanDeg; o["centerAngle"] = s.CenterAngleDeg; }
                if (s.Kind == ShapeKind.Mesh) o["meshPath"] = s.MeshPath;
            }
            bodies.Add(o);
        }
        return new JsonObject
        {
            ["schema"] = "mns.scene/1",
            ["frames"] = new JsonObject
            {
                ["inertial"] = "NED (m), origin at sea level",
                ["body"] = "FRD, origin at model origin",
                ["timeseries_origin_columns"] = "origin_* = body reference origin in NED; q = body->NED",
                ["nedToBlender"] = J.ToJson(Frames.NedToEnu),
                ["fusionToBody"] = J.ToJson(Frames.FusionToBody),
            },
            ["groundAltitude_m"] = w.Env.GroundAltitude,
            ["bodies"] = bodies,
        };
    }

    /// <summary>결과 폴더에 전체 출력.</summary>
    public static string WriteAll(ScenarioDoc doc, World w, string outDir, string? gitHash = null)
    {
        Directory.CreateDirectory(outDir);
        WriteTimeseriesCsv(w, Path.Combine(outDir, "timeseries.csv"));
        WriteDevicesCsv(w, Path.Combine(outDir, "devices.csv"));
        var summary = Summary(doc, w, gitHash);
        File.WriteAllText(Path.Combine(outDir, "summary.json"), summary.ToJsonString(J.Pretty), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(outDir, "events.json"), summary["events"]!.ToJsonString(J.Pretty), new UTF8Encoding(false));
        // 물체별 메시(동체 기준좌표, m) — Blender·외부 뷰어가 단위/좌표 추정 없이 쓰도록
        var scene = Scene(w);
        var meshDir = Path.Combine(outDir, "meshes");
        Directory.CreateDirectory(meshDir);
        foreach (var (b, node) in w.Bodies.Zip(scene["bodies"]!.AsArray()))
        {
            var mesh = b.Shape?.BuildMesh() ?? TriMesh.Sphere(0.08, b.OwnCg, 8, 12);
            var file = $"{string.Concat(b.Name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch))}.stl";
            mesh.SaveBinaryStl(Path.Combine(meshDir, file));
            node!["mesh"] = $"meshes/{file}";
            // Fusion 이 재질 포함 USD 를 함께 내보냈으면(massprops usd.file) 복사 — Fusion 모델 좌표 그대로라 동체 변환을 같이 적는다
            // (Blender import_mns_result.py --usd: 동체 = modelToBody · 모델[m])
            if (b.MassProps is { Usd: { } usd, SourcePath: { } mpPath } && Path.Combine(Path.GetDirectoryName(mpPath)!, usd) is var usdSrc && File.Exists(usdSrc))
            {
                var usdFile = Path.ChangeExtension(file, Path.GetExtension(usd));
                File.Copy(usdSrc, Path.Combine(meshDir, usdFile), true);
                node["usd"] = $"meshes/{usdFile}";
                node["modelToBody"] = J.ToJson(b.ModelToBody);
            }
        }
        File.WriteAllText(Path.Combine(outDir, "scene.json"), scene.ToJsonString(J.Pretty), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(outDir, "scenario.json"), doc.ToJson(), new UTF8Encoding(false));
        return outDir;
    }

    /// <summary>사람이 읽는 요약 텍스트(UI·CLI).</summary>
    public static string SummaryText(JsonObject s)
    {
        var sb = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;
        sb.AppendLine($"시나리오: {s["scenario"]}  종료 {J.ToDouble(s["endTime_s"]!):F2} s ({s["stopReason"]})");
        if (s["suspect"]!.GetValue<bool>()) sb.AppendLine("⚠ 수치 의심(suspect) 실행");
        foreach (var (name, node) in s["bodies"]!.AsObject())
        {
            var b = node!.AsObject();
            if (b["impact"] is JsonObject i)
                sb.AppendLine(string.Format(inv, "· {0}: 착지 t={1:F2}s  N={2:F1} E={3:F1} m  v={4:F2} m/s  노즈각 {5:F1}°  관입 {6:F3} m ({7:F0} g)  [{8}/{9}]",
                    name, J.ToDouble(i["time_s"]!), J.ToDouble(i["n_m"]!), J.ToDouble(i["e_m"]!), J.ToDouble(i["speed_mps"]!),
                    J.ToDouble(i["noseAngleFromVertical_deg"]!), J.ToDouble(i["youngPenetration_m"]!), J.ToDouble(i["youngMeanDecel_g"]!),
                    b["aeroFidelity"], b["massSource"]) + (i["youngInEnvelope"] is JsonValue ie && !ie.GetValue<bool>() ? "  ※관입: 질량이 Young 식 하한(2.27 kg) 미만 — 외삽" : ""));
            else
                sb.AppendLine($"· {name}: {b["mode"]} [{b["aeroFidelity"]}/{b["massSource"]}]");
            if (b["staticMargin"] is JsonValue smv)
                sb.AppendLine(string.Format(inv, "    정적 여유 {0:+0.0;-0.0}% ({1})", J.ToDouble(smv) * 100, J.ToDouble(smv) > 0 ? "기수 선행 안정" : "불안정/역방향"));
        }
        foreach (var (name, node) in s["devices"]!.AsObject())
        {
            var d = node!.AsObject();
            if (J.Str(d, "type", "") == "parachute")
                sb.AppendLine(string.Format(inv, "· 낙하산 {0}: {1}, 최대 개방하중 {2:F0} N ({3:F1} G) @ {4:F2} s",
                    name, d["state"], J.ToDouble(d["maxForce_N"]!), J.ToDouble(d["maxOpeningG"]!), d["maxForceTime_s"] is { } mt ? J.ToDouble(mt) : double.NaN));
        }
        foreach (var (name, node) in s["connectors"]!.AsObject())
        {
            var c = node!.AsObject();
            if (J.Str(c, "type", "") == "tether")
                sb.AppendLine(string.Format(inv, "· 테더 {0}: 최대 장력 {1:F0} N{2}", name, J.ToDouble(c["maxTension_N"]!), c["broken"]!.GetValue<bool>() ? " (파단)" : ""));
            else if (J.Str(c, "type", "") == "hinge")
                sb.AppendLine(string.Format(inv, "· 힌지 {0}: 최대 반력 {1:F0} N, 최대 열림 {2:F0}°{3}", name, J.ToDouble(c["maxForce_N"]!), J.ToDouble(c["maxAngle_deg"]!),
                    c["released"]!.GetValue<bool>() ? string.Format(inv, " → 이탈 {0:F3} s ({1})", c["releaseTime_s"] is { } rt ? J.ToDouble(rt) : double.NaN, c["releaseReason"]) : ""));
        }
        if (s["collisions"] is JsonObject col)
            sb.AppendLine($"· 충돌 판정: 접촉 {col["contacts"]}건, 낙하산 간섭 {col["interferences"]}건");
        foreach (var (name, node) in s["metrics"]!.AsObject())
            sb.AppendLine($"· 지표 {name}: {node!.ToJsonString()}");
        var warns = s["warnings"]!.AsArray();
        if (warns.Count > 0) sb.AppendLine($"경고 {warns.Count}건: " + string.Join(" | ", warns.Take(5)));
        return sb.ToString();
    }
}
