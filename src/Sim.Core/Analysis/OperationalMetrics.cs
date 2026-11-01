using System.Text.Json.Nodes;
using Sim.Core.Engine;
using Sim.Core.Models;
using Sim.Core.Numerics;
using Sim.Core.Scenario;

namespace Sim.Core.Analysis;

/// <summary>
/// 운용 시나리오 분석 지표(PRD AA절, FR-AN-02/03/05/06/07). 기록 프레임(출력 주기) 기반 — 각속도는 프레임 값,
/// 최대 하중 G 처럼 짧은 펄스가 필요한 값은 엔진의 스텝 추적값(maxLoad)을 쓴다.
///  · tumbling        분리 직후 텀블링: 최대 각속도·횡축 누적 회전·최대 총받음각, 지정 이벤트 순간 자세(드로그 전개 순간 등)
///  · pendulum        낙하산 하강 진자: 매달림 축과 연직의 각 — 진폭·RMS·주기, 이벤트(박스 사출) 전후 흔들림 응답
///  · canopyClearance 캐노피 간 최소 간격(캐노피 구 사이 거리 − 반경 합, 음수 = 겹침)
///  · massBudget      질량 합 vs 허용(시나리오 limit, 예: 50 kg) + 초기/최종 질량 보존
///  · chuteAnomalies  잔해 간섭 전개 이상·고장 주입 집계
///  · phaseTimeline   단계 사건 시각(영상 시각 대비 차이 선택)
///  · ejection        사출 과정: 사출 직후 상대속도·모체와의 최소 이격·사출 충격 G
/// </summary>
public static class OperationalMetrics
{
    private static double Deg(double rad) => rad * 180 / Math.PI;

    private static double EventTime(World w, string? name) =>
        name is not null && w.FiredEvents.TryGetValue(name, out var t) ? t : double.NaN;

    public static JsonObject? TryCompute(string type, JsonObject d, World w, List<string> names) => type switch
    {
        "tumbling" => Tumbling(d, w, names),
        "pendulum" => Pendulum(d, w, names),
        "canopyClearance" => CanopyClearance(d, w),
        "massBudget" => MassBudget(d, w, names),
        "chuteAnomalies" => ChuteAnomalies(d, w),
        "phaseTimeline" => PhaseTimeline(d, w),
        "ejection" => Ejection(d, w, names),
        _ => null,
    };

    /// <summary>총받음각(동체 +X 와 대기상대속도) [deg]. 상대속도 0 이면 NaN.</summary>
    public static double TotalAoaDeg(BodySample s)
    {
        if (s.AirVelocity.NormSq < 1e-12) return double.NaN;
        var vb = s.Q.InverseRotate(s.AirVelocity).Normalized();
        return Deg(Math.Acos(Math.Clamp(vb.X, -1, 1)));
    }

    private static JsonObject Tumbling(JsonObject d, World w, List<string> names)
    {
        double thr = J.Num(d, "thresholdDeg", 360);
        var at = d["attitudeAt"] is JsonArray aa ? aa.Select(x => x!.GetValue<string>()).ToList() : new List<string>();
        var arr = new JsonArray();
        bool any = false;
        foreach (var n in names)
        {
            var b = w.GetBody(n);
            double t0 = J.StrOpt(d, "from") is { } fe ? EventTime(w, fe) : b.ReleaseTime;
            double t1 = J.StrOpt(d, "to") is { } toEv ? EventTime(w, toEv) : double.PositiveInfinity;
            if (!double.IsFinite(t0)) { arr.Add(new JsonObject { ["body"] = n, ["released"] = false }); continue; }
            if (double.IsNaN(t1)) t1 = double.PositiveInfinity;
            double maxRate = 0, maxTrans = 0, cum = 0, maxAoa = 0, tMaxRate = double.NaN, prevT = double.NaN;
            foreach (var f in w.Recorder.Frames)
            {
                if (f.T < t0 - 1e-9 || f.T > t1 + 1e-9) continue;
                var s = f.Bodies[b.Index];
                if (s.Mode != BodyMode.Free) { prevT = double.NaN; continue; }
                double rate = s.Omega.Norm, trans = Math.Sqrt(s.Omega.Y * s.Omega.Y + s.Omega.Z * s.Omega.Z);
                if (rate > maxRate) { maxRate = rate; tMaxRate = f.T; }
                maxTrans = Math.Max(maxTrans, trans);
                if (double.IsFinite(prevT)) cum += trans * (f.T - prevT);
                prevT = f.T;
                double a = TotalAoaDeg(s);
                if (double.IsFinite(a)) maxAoa = Math.Max(maxAoa, a);
            }
            bool tumbling = Deg(cum) >= thr;
            any |= tumbling;
            var ev = new JsonObject();
            foreach (var e in at)
            {
                double te = EventTime(w, e);
                if (!double.IsFinite(te)) { ev[e] = null; continue; }
                var s = w.Recorder.Frames[w.Recorder.IndexAt(te)].Bodies[b.Index];
                var ax = s.Q.Rotate(Vec3.UnitX);
                var eu = s.Q.ToEuler();
                ev[e] = new JsonObject
                {
                    ["t"] = te, ["totalAoA_deg"] = Parachute.Num(TotalAoaDeg(s)), ["pitch_deg"] = Deg(eu.Y),
                    ["noseFromVerticalDown_deg"] = Deg(Math.Acos(Math.Clamp(ax.Z, -1, 1))),
                    ["rate_dps"] = Deg(s.Omega.Norm), ["airspeed_mps"] = s.Airspeed,
                };
            }
            arr.Add(new JsonObject
            {
                ["body"] = n, ["from_s"] = t0, ["to_s"] = Parachute.Num(t1),
                ["maxRate_dps"] = Deg(maxRate), ["maxRateTime_s"] = Parachute.Num(tMaxRate), ["maxTransverseRate_dps"] = Deg(maxTrans),
                ["cumulativeTransverseRotation_deg"] = Deg(cum), ["maxTotalAoA_deg"] = maxAoa, ["tumbling"] = tumbling, ["atEvents"] = ev,
            });
        }
        return new JsonObject { ["type"] = "tumbling", ["thresholdDeg"] = thr, ["anyTumbling"] = any, ["bodies"] = arr,
            ["criterion"] = $"횡축(q,r) 누적 회전 ≥ {thr:F0}° 이면 텀블링" };
    }

    private static JsonObject Pendulum(JsonObject d, World w, List<string> names)
    {
        var axisB = J.V3(d, "axis", new Vec3(-1, 0, 0)).Normalized();
        double settle = J.Num(d, "settle", 3.0);
        double win = J.Num(d, "responseWindow", 3.0);
        var evs = d["events"] is JsonArray ea ? ea.Select(x => x!.GetValue<string>()).ToList() : new List<string>();
        var arr = new JsonArray();
        foreach (var n in names)
        {
            var b = w.GetBody(n);
            double t0 = (J.StrOpt(d, "from") is { } fe ? EventTime(w, fe) : b.ReleaseTime) + settle;
            double t1 = J.StrOpt(d, "to") is { } toEv && double.IsFinite(EventTime(w, toEv)) ? EventTime(w, toEv) : double.PositiveInfinity;
            if (!double.IsFinite(t0)) { arr.Add(new JsonObject { ["body"] = n, ["valid"] = false }); continue; }
            var ts = new List<double>(); var th = new List<double>(); var hs = new List<(double N, double E)>();
            foreach (var f in w.Recorder.Frames)
            {
                if (f.T < t0 || f.T > t1) continue;
                var s = f.Bodies[b.Index];
                if (s.Mode != BodyMode.Free) continue;
                var a = s.Q.Rotate(axisB);
                ts.Add(f.T); th.Add(Deg(Math.Acos(Math.Clamp(-a.Z, -1, 1)))); hs.Add((a.X, a.Y));
            }
            double period = double.NaN; int crossings = 0;
            if (hs.Count > 4)
            {
                double mn = hs.Average(h => h.N), me = hs.Average(h => h.E);
                double snn = hs.Sum(h => (h.N - mn) * (h.N - mn)), see = hs.Sum(h => (h.E - me) * (h.E - me)), sne = hs.Sum(h => (h.N - mn) * (h.E - me));
                double ang = 0.5 * Math.Atan2(2 * sne, snn - see);
                double cx = Math.Cos(ang), cy = Math.Sin(ang);
                var sig = hs.Select(h => (h.N - mn) * cx + (h.E - me) * cy).ToList();
                double amp = sig.Select(Math.Abs).DefaultIfEmpty(0).Max();
                double hyst = 0.05 * amp;
                int sign = 0; var tc = new List<double>();
                for (int i = 0; i < sig.Count; i++)
                {
                    int sgn = sig[i] > hyst ? 1 : sig[i] < -hyst ? -1 : 0;
                    if (sgn == 0) continue;
                    if (sign != 0 && sgn != sign) tc.Add(ts[i]);
                    sign = sgn;
                }
                crossings = tc.Count;
                if (tc.Count >= 3) period = 2 * (tc[^1] - tc[0]) / (tc.Count - 1);
            }
            var resp = new JsonArray();
            foreach (var e in evs)
            {
                double te = EventTime(w, e);
                if (!double.IsFinite(te)) continue;
                double pre = 0, post = 0, preRate = 0, postRate = 0;
                foreach (var f in w.Recorder.Frames)
                {
                    var s = f.Bodies[b.Index];
                    if (s.Mode != BodyMode.Free) continue;
                    double ang = Deg(Math.Acos(Math.Clamp(-s.Q.Rotate(axisB).Z, -1, 1)));
                    if (f.T >= te - 1.0 && f.T < te) { pre = Math.Max(pre, ang); preRate = Math.Max(preRate, s.Omega.Norm); }
                    else if (f.T >= te && f.T <= te + win) { post = Math.Max(post, ang); postRate = Math.Max(postRate, s.Omega.Norm); }
                }
                resp.Add(new JsonObject
                {
                    ["event"] = e, ["t"] = te, ["swingBefore_deg"] = pre, ["swingAfter_deg"] = post,
                    ["rateBefore_dps"] = Deg(preRate), ["rateAfter_dps"] = Deg(postRate),
                });
            }
            arr.Add(new JsonObject
            {
                ["body"] = n, ["from_s"] = t0, ["to_s"] = Parachute.Num(t1 == double.PositiveInfinity ? (ts.Count > 0 ? ts[^1] : double.NaN) : t1),
                ["samples"] = ts.Count,
                ["amplitude_deg"] = th.Count > 0 ? th.Max() : null,
                ["rms_deg"] = th.Count > 0 ? Math.Sqrt(th.Average(x => x * x)) : null,
                ["period_s"] = Parachute.Num(period), ["halfCycles"] = crossings,
                ["eventResponse"] = resp,
            });
        }
        return new JsonObject { ["type"] = "pendulum", ["axis_brf"] = J.ToJson(axisB), ["bodies"] = arr,
            ["note"] = "진자각 = 매달림 축(동체 axis)과 연직 위 방향 사이 각, 주기 = 주 흔들림 방향 성분 0 교차 간격 × 2" };
    }

    private static JsonObject CanopyClearance(JsonObject d, World w)
    {
        var devs = d["devices"] is JsonArray da ? da.Select(x => x!.GetValue<string>()).ToList() : new List<string>();
        double best = double.PositiveInfinity, bt = double.NaN; string pair = "";
        int overlapFrames = 0;
        foreach (var f in w.Recorder.Frames)
        {
            var ss = devs.Select(n => f.Devices.FirstOrDefault(x => x.Name == n)).ToList();
            for (int i = 0; i < ss.Count; i++)
                for (int j = i + 1; j < ss.Count; j++)
                {
                    var a = ss[i]; var b = ss[j];
                    if (a.Name is null || b.Name is null || !a.Visible || !b.Visible) continue;
                    double gap = (a.Tip - b.Tip).Norm - a.Size / 2 * Math.Max(a.Fill, 0.3) - b.Size / 2 * Math.Max(b.Fill, 0.3);
                    if (gap < 0) overlapFrames++;
                    if (gap < best) { best = gap; bt = f.T; pair = $"{a.Name}–{b.Name}"; }
                }
        }
        return new JsonObject
        {
            ["type"] = "canopyClearance", ["minGap_m"] = double.IsFinite(best) ? best : null, ["time_s"] = Parachute.Num(bt), ["pair"] = pair,
            ["overlap"] = double.IsFinite(best) && best < 0, ["overlapFrames"] = overlapFrames,
        };
    }

    private static JsonObject MassBudget(JsonObject d, World w, List<string> names)
    {
        var parts = new JsonArray();
        double sum = 0;
        foreach (var n in names)
        {
            var b = w.GetBody(n);
            sum += b.OwnMass;
            parts.Add(new JsonObject { ["body"] = n, ["mass_kg"] = b.OwnMass, ["source"] = b.ModelSource ?? b.MassSource });
        }
        double limit = J.Num(d, "limit", double.NaN);
        double end = w.Bodies.Where(b => b.Mode != BodyMode.Kinematic).Sum(b => b.OwnMass);
        return new JsonObject
        {
            ["type"] = "massBudget", ["total_kg"] = sum, ["limit_kg"] = Parachute.Num(limit),
            ["pass"] = double.IsFinite(limit) ? sum <= limit : null,
            ["excess_kg"] = double.IsFinite(limit) ? Math.Max(0, sum - limit) : null,
            ["allBodiesInitial_kg"] = w.InitialTotalMass, ["allBodiesFinal_kg"] = end,
            ["conserved"] = Math.Abs(end - w.InitialTotalMass) <= 1e-9 * Math.Max(1, w.InitialTotalMass),
            ["parts"] = parts,
        };
    }

    private static JsonObject ChuteAnomalies(JsonObject d, World w)
    {
        var devs = d["devices"] is JsonArray da ? da.Select(x => x!.GetValue<string>()).ToHashSet() : null;
        var arr = new JsonArray();
        int total = 0, affected = 0, failures = 0;
        foreach (var b in w.Bodies)
            foreach (var ch in b.Devices.OfType<Parachute>())
            {
                if (devs is not null && !devs.Contains(ch.FullName)) continue;
                total += ch.Anomalies.Count;
                if (ch.Anomalies.Count > 0) affected++;
                if (ch.FailureActive) failures++;
                arr.Add(new JsonObject
                {
                    ["device"] = ch.FullName, ["deployed"] = double.IsFinite(ch.DeployTime), ["state"] = ch.State.ToString(),
                    ["anomalies"] = ch.Anomalies.Count,
                    ["effects"] = new JsonArray(ch.Anomalies.Select(a => (JsonNode)JsonValue.Create($"t={a.Time:F2}s {a.Debris}·{a.Region}·{a.Phase}: {a.Effect}")!).ToArray()),
                    ["failure"] = ch.FailureActive ? ch.FailureMode : null,
                    ["extraDelay_s"] = ch.ExtraDelay, ["fillCap"] = ch.FillCap, ["damageFactor"] = ch.DamageFactor,
                    ["maxOpeningG"] = ch.MaxG,
                });
            }
        return new JsonObject { ["type"] = "chuteAnomalies", ["total"] = total, ["affectedDevices"] = affected, ["failures"] = failures, ["devices"] = arr,
            ["model"] = w.Collisions?.Anomaly is null ? "전개 이상 모델 없음(간섭 기록만)" : "잔해 간섭 → 지연/부분 충만/라인 손상(가정 계수)" };
    }

    private static JsonObject PhaseTimeline(JsonObject d, World w)
    {
        var arr = new JsonArray();
        int missing = 0;
        foreach (var pn in d["phases"]?.AsArray() ?? new JsonArray())
        {
            var p = pn!.AsObject();
            var ev = J.StrReq(p, "event", "phaseTimeline.phases");
            double t = EventTime(w, ev);
            if (!double.IsFinite(t)) missing++;
            double? video = J.NumOpt(p, "video");
            arr.Add(new JsonObject
            {
                ["phase"] = J.Str(p, "label", ev), ["event"] = ev, ["t_s"] = Parachute.Num(t),
                ["video_s"] = video, ["delta_s"] = video is { } v && double.IsFinite(t) ? t - v : null,
            });
        }
        // 단계 순서: 정의 순서대로 시각이 증가해야 한다(발생한 것끼리)
        var times = arr.Select(x => x!["t_s"] is JsonValue v ? v.GetValue<double>() : double.NaN).Where(double.IsFinite).ToList();
        bool ordered = times.Zip(times.Skip(1)).All(p => p.Second >= p.First - 1e-9);
        return new JsonObject { ["type"] = "phaseTimeline", ["phases"] = arr, ["missing"] = missing, ["ordered"] = ordered };
    }

    private static JsonObject Ejection(JsonObject d, World w, List<string> names)
    {
        var parent = w.GetBody(J.StrReq(d, "parent", "ejection"));
        double after = J.Num(d, "after", 0.1), ignore = J.Num(d, "ignore", 0.0), win = J.Num(d, "window", 5.0);
        var arr = new JsonArray();
        foreach (var n in names)
        {
            var b = w.GetBody(n);
            if (!double.IsFinite(b.ReleaseTime)) { arr.Add(new JsonObject { ["body"] = n, ["released"] = false }); continue; }
            var frames = w.Recorder.Frames;
            var f1 = frames[w.Recorder.IndexAt(b.ReleaseTime + after)];
            double rel = (f1.Bodies[b.Index].Velocity - f1.Bodies[parent.Index].Velocity).Norm;
            double minD = double.PositiveInfinity, tMin = double.NaN;
            foreach (var f in frames)
            {
                if (f.T < b.ReleaseTime + ignore || f.T > b.ReleaseTime + win) continue;
                double dist = (f.Bodies[b.Index].Cg - f.Bodies[parent.Index].Cg).Norm;
                if (dist < minD) { minD = dist; tMin = f.T; }
            }
            arr.Add(new JsonObject
            {
                ["body"] = n, ["releaseTime_s"] = b.ReleaseTime, ["relativeSpeed_mps"] = rel, ["relativeSpeedAt_s"] = f1.T,
                ["minDistanceToParent_m"] = double.IsFinite(minD) ? minD : null, ["minDistanceTime_s"] = Parachute.Num(tMin),
                ["maxLoadG"] = b.MaxLoadG, ["maxLoadTime_s"] = Parachute.Num(b.MaxLoadTime),
            });
        }
        return new JsonObject { ["type"] = "ejection", ["parent"] = parent.Name, ["bodies"] = arr };
    }
}
