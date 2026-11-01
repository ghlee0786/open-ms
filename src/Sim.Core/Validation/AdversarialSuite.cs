using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Sim.Core.Analysis;
using Sim.Core.Engine;
using Sim.Core.Fea;
using Sim.Core.Geometry;
using Sim.Core.Models;
using Sim.Core.Numerics;
using Sim.Core.Scenario;
using Sim.Core.Structures;

namespace Sim.Core.Validation;

public enum AdvStatus { Pass, Fail, Info }

/// <summary>적대적 시나리오 1건 결과: 무엇이 깨지면 실패인지(Breaks), 측정값 vs 기준.</summary>
public sealed record AdvResult(string Id, string Category, string Title, string Breaks, AdvStatus Status, string Measured, string Criterion, string Note = "");

/// <summary>
/// 적대적 시나리오 자체 검증(PRD Y, VV-60~62). 시나리오 JSON + 기대 판정. 저장소 테스트(xUnit)가 전 항목 Pass 를 요구하고,
/// CLI <c>mns adversarial</c> 가 validation/reports/adversarial/SUMMARY.md 를 쓴다. 기준은 통과시키려고 느슨하게 바꾸지 않는다.
/// </summary>
public static class AdversarialSuite
{
    /// <summary>엔진 검증용 시나리오 폴더(저장소 기준) — 앱 목록(config/scenarios)에는 운용 시나리오만 둔다.</summary>
    public const string TestScenarios = "tests/Sim.Core.Tests/scenarios";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static List<AdvResult> Run(string repoRoot, Func<IDropBackend?>? gpuFactory = null, string? smokeLog = null)
    {
        var list = new List<AdvResult>();
        void Add(string id, string cat, string title, string breaks, Func<(bool Ok, string Measured, string Criterion, string Note)> f)
        {
            try
            {
                var (ok, m, c, n) = f();
                list.Add(new AdvResult(id, cat, title, breaks, ok ? AdvStatus.Pass : AdvStatus.Fail, m, c, n));
            }
            catch (Exception ex)
            {
                list.Add(new AdvResult(id, cat, title, breaks, AdvStatus.Fail, $"예외 {ex.GetType().Name}: {ex.Message}", "예외 없음"));
            }
        }
        // 엔진 검증용 시나리오(01~10b)는 테스트 전용 폴더, 앱 운용 시나리오(11~)는 config/scenarios
        string scen(string f) => File.Exists(Path.Combine(repoRoot, TestScenarios, f)) ? Path.Combine(repoRoot, TestScenarios, f) : Path.Combine(repoRoot, "config", "scenarios", f);
        World Build(string json) => ScenarioDoc.Parse(json, repoRoot).Build();
        World RunJson(string json) { var w = Build(json); w.Run(); return w; }

        // ---------- ① 극한 조건 ----------
        Add("A01", "① 극한", "초고고도 30 km 구 낙하", "대기 모델(32 km 이상 층)·적분 발산 → NaN/비현실 속도",
            () =>
            {
                var w = RunJson("""{"schema":"mns.scenario/1","sim":{"dt":0.002,"tEnd":120},"bodies":[{"name":"s","altitude":30000,"mass":1,"shape":{"type":"sphere","radius":0.05}}]}""");
                double vmax = w.Recorder.Frames.Max(f => f.Bodies[0].Velocity.Norm);
                return (!w.Suspect && double.IsFinite(vmax) && vmax < 1500, $"최대 속도 {vmax:F1} m/s, suspect={w.Suspect}", "유한, < 1500 m/s, suspect 아님", "");
            });
        Add("A02", "① 극한", "지면 아래에서 시작(AGL −5 m)", "지면 접촉 판정이 시작 상태를 놓쳐 영원히 낙하",
            () =>
            {
                var w = RunJson("""{"schema":"mns.scenario/1","sim":{"dt":0.001,"tEnd":10},"bodies":[{"name":"s","altitude":-5,"mass":1,"shape":{"type":"sphere","radius":0.05}}]}""");
                var b = w.GetBody("s");
                return (b.Mode == BodyMode.Landed && w.T <= 0.0011, $"모드 {b.Mode}, 종료 t={w.T:F4} s ({w.StopReason})", "첫 스텝에 착지 처리", "");
            });
        Add("A03", "① 극한", "적분 dt 과대(0.5 s)", "큰 스텝에서 예외·NaN·무한 루프",
            () =>
            {
                var w = RunJson("""{"schema":"mns.scenario/1","sim":{"dt":0.5,"tEnd":60},"bodies":[{"name":"s","altitude":100,"mass":1,"shape":{"type":"sphere","radius":0.05}}]}""");
                var r = RunJson("""{"schema":"mns.scenario/1","sim":{"dt":0.001,"tEnd":60},"bodies":[{"name":"s","altitude":100,"mass":1,"shape":{"type":"sphere","radius":0.05}}]}""");
                double t1 = w.GetBody("s").Impact?.Time ?? double.NaN, t0 = r.GetBody("s").Impact?.Time ?? double.NaN;
                return (double.IsFinite(t1) && Math.Abs(t1 - t0) < 0.05 * t0, $"착지 {t1:F3} s (기준 dt 1 ms: {t0:F3} s)", "완주 + 착지시각 5% 이내(이벤트 이분 탐색)", "");
            });
        Add("A04", "① 극한", "tEnd = 0", "종료 조건 누락으로 실행 지속 또는 빈 결과로 UI 예외",
            () =>
            {
                var w = RunJson("""{"schema":"mns.scenario/1","sim":{"tEnd":0},"bodies":[{"name":"s","altitude":100,"mass":1}]}""");
                return (w.Stopped && w.T == 0 && w.Recorder.Frames.Count >= 1, $"T={w.T}, 프레임 {w.Recorder.Frames.Count}, '{w.StopReason}'", "즉시 정지, 프레임 ≥ 1", "");
            });

        // ---------- ② 수치 스트레스 ----------
        Add("A05", "② 수치", "초고속 무토크 회전(100 rad/s, 비대칭 관성)", "쿼터니언 정규화·자이로 항 오류 → 각운동량·에너지 표류",
            () =>
            {
                var w = Build("""
                  {"schema":"mns.scenario/1","sim":{"dt":0.0002,"tEnd":2},"environment":{"vacuum":true},
                  "bodies":[{"name":"b","altitude":5000,"mass":2,"inertia":[0.01,0.02,0.03],"omega":[100,1,0.5]}]}
                  """);
                var b = w.GetBody("b");
                Vec3 H() => b.Q.Rotate(b.Inertia * b.W);
                double E() => 0.5 * Vec3.Dot(b.W, b.Inertia * b.W);
                var h0 = H(); double e0 = E();
                w.Run();
                double dh = (H() - h0).Norm / h0.Norm, de = Math.Abs(E() - e0) / e0;
                return (dh < 1e-6 && de < 1e-6 && Math.Abs(b.Q.Norm - 1) < 1e-9, $"|ΔH|/H={dh:E2}, |ΔE|/E={de:E2}, |q|−1={b.Q.Norm - 1:E1}", "< 1e-6, < 1e-6, < 1e-9", "");
            });
        Add("A06", "② 수치", "강성 테더 스냅(EA 1e7, 2 m 자유낙하 후)", "강성 스프링 발산(NaN) · 파단 이벤트 누락",
            () =>
            {
                var w = RunJson("""
                  {"schema":"mns.scenario/1","sim":{"dt":0.0005,"tEnd":3},"environment":{"vacuum":true},
                  "carrier":{"name":"h","altitude":500,"speed":0},
                  "bodies":[{"name":"m","position":[0,0,-500],"mass":10,"inertia":[0.1,0.1,0.1]}],
                  "connectors":[{"type":"tether","name":"t","a":"h","b":"m","length":2,"ea":1e7,"damping":50,"breakLoad":20000}]}
                  """);
                var t = (Tether)w.Connectors[0];
                return (!w.Suspect && double.IsFinite(t.MaxTension) && t.Broken && w.EventLog.Any(e => e.Name == "t.broken"),
                    $"최대 장력 {t.MaxTension:F0} N, 파단={t.Broken}, suspect={w.Suspect}", "유한, 20 kN 초과 시 파단 이벤트", "");
            });
        Add("A07", "② 수치", "동시 다중 이벤트 + 연쇄 + 순환 참조", "같은 시각 이벤트 누락·중복, 순환(after A↔B)에서 무한 루프",
            () =>
            {
                var w = RunJson("""
                  {"schema":"mns.scenario/1","sim":{"dt":0.01,"tEnd":2},"bodies":[{"name":"s","altitude":1000,"mass":1}],
                  "events":[{"name":"e1","when":{"type":"time","t":1.0},"do":[{"type":"log","message":"a"}]},
                            {"name":"e2","when":{"type":"time","t":1.0},"do":[{"type":"log","message":"b"}]},
                            {"name":"e3","when":{"type":"after","event":"e1","delay":0},"do":[{"type":"log","message":"c"}]},
                            {"name":"x","when":{"type":"after","event":"y","delay":0},"do":[{"type":"log","message":"x"}]},
                            {"name":"y","when":{"type":"after","event":"x","delay":0},"do":[{"type":"log","message":"y"}]}]}
                  """);
                var t = w.Events.ToDictionary(e => e.Name, e => e.FiredTime);
                bool ok = w.Events.Where(e => e.Name.StartsWith('e')).All(e => e.Fired && Math.Abs(e.FiredTime - 1.0) < 1e-9) && !w.Events.First(e => e.Name == "x").Fired;
                return (ok, $"e1/e2/e3 = {t["e1"]:F3}/{t["e2"]:F3}/{t["e3"]:F3} s, 순환 x 발생={w.Events.First(e => e.Name == "x").Fired}", "모두 1.000 s 에 1회, 순환은 미발생·정지", "");
            });

        // ---------- ③ 대칭 ----------
        Add("A08", "③ 대칭", "바람 방향 90° 회전 → 착지점 90° 회전", "바람 좌표 변환(풍향 정의) 오류",
            () =>
            {
                (double N, double E) Drop(double dir)
                {
                    var w = RunJson($$$"""
                      {"schema":"mns.scenario/1","sim":{"dt":0.002,"tEnd":120},"environment":{"wind":{"speed":6,"directionFrom":{{{dir.ToString(Inv)}}},"shear":"none"}},
                      "bodies":[{"name":"s","altitude":200,"mass":1,"shape":{"type":"sphere","radius":0.08}}]}
                      """);
                    var p = w.GetBody("s").Impact!.Position; return (p.X, p.Y);
                }
                var a = Drop(270); var b = Drop(0);
                double err = Math.Abs(a.E + b.N) + Math.Abs(a.N - b.E);
                return (err < 1e-6 * Math.Max(1, Math.Abs(a.E)), $"서풍 (N,E)=({a.N:F3},{a.E:F3}), 북풍 ({b.N:F3},{b.E:F3})", "R(90°) 일치 < 1e-6", "");
            });
        Add("A09", "③ 대칭", "좌우 거울 자세(roll ±30°) 상자 낙하", "부호 규약 오류로 거울 대칭 깨짐",
            () =>
            {
                double Drop(double roll)
                {
                    var w = RunJson($$$"""
                      {"schema":"mns.scenario/1","sim":{"dt":0.001,"tEnd":60},"bodies":[{"name":"b","altitude":100,"mass":2,"attitude":[{{{roll.ToString(Inv)}}},20,0],
                      "shape":{"type":"box","size":[0.4,0.2,0.1]},"aero":{"cp":[-0.05,0,0],"damping":[-2,-4,-4]}}]}
                      """);
                    return w.GetBody("b").Impact!.Position.Y;
                }
                double e1 = Drop(30), e2 = Drop(-30);
                return (Math.Abs(e1 + e2) < 1e-6, $"E(+30°)={e1:F6}, E(−30°)={e2:F6}", "E(+) = −E(−) (< 1e-6 m)", "");
            });

        // ---------- ④ 보존량 ----------
        Add("A10", "④ 보존", "진공 포물선 낙하 에너지 보존", "적분기·중력 항 오류",
            () =>
            {
                var w = Build("""{"schema":"mns.scenario/1","sim":{"dt":0.001,"tEnd":3},"environment":{"vacuum":true},"bodies":[{"name":"s","altitude":1000,"mass":3,"velocity":[50,0,0]}]}""");
                var b = w.GetBody("s"); double g = w.Env.Gravity;
                double E() => 0.5 * b.Mass * b.V.NormSq + b.Mass * g * (-b.R.Z);
                double e0 = E(); w.Run(); double de = Math.Abs(E() - e0) / Math.Abs(e0);
                return (de < 1e-9, $"|ΔE|/E = {de:E2}", "< 1e-9", "");
            });
        Add("A11", "④ 보존", "분리 운동량 보존(상대속도 3 m/s 사출)", "분리 시 반작용 누락",
            () =>
            {
                var w = Build("""
                  {"schema":"mns.scenario/1","sim":{"dt":0.001,"tEnd":1},"environment":{"vacuum":true},
                  "bodies":[{"name":"p","altitude":1000,"mass":20,"inertia":[1,1,1],"velocity":[30,0,0]},{"name":"c","parent":"p","mount":[0.5,0,0],"mass":5,"inertia":[0.1,0.1,0.1]}],
                  "events":[{"name":"sep","when":{"type":"time","t":0.5},"do":[{"type":"separate","body":"c","relVel":[3,0,0]}]}]}
                  """);
                Vec3 P() => w.Bodies.Where(b => b.Mode == BodyMode.Free).Aggregate(Vec3.Zero, (s, b) => s + b.V * b.Mass);
                while (w.T < 0.499) w.Step();
                var p0 = P(); double t0 = w.T;
                while (w.T < 0.6) w.Step();
                var p1 = P() - new Vec3(0, 0, 25 * w.Env.Gravity * (w.T - t0));
                double err = (p1 - p0).Norm / p0.Norm;
                return (err < 1e-9 && w.GetBody("c").Mode == BodyMode.Free, $"|ΔP|/P = {err:E2}", "< 1e-9", "");
            });

        // ---------- ⑤ 잘못된 입력 ----------
        Add("A12", "⑤ 오류 입력", "스키마·참조·음수 질량·깨진 STL·빈 massprops·미지 장치·문자열 숫자", "크래시(NullReference 등) 또는 모호한 오류",
            () =>
            {
                var tmp = Path.Combine(Path.GetTempPath(), "mns_adv", Guid.NewGuid().ToString("N")[..8]);
                Directory.CreateDirectory(tmp);
                File.WriteAllText(Path.Combine(tmp, "bad.stl"), "solid x\n facet normal 0 0 1\n  outer loop\n   vertex 0 0\n");
                File.WriteAllText(Path.Combine(tmp, "empty.massprops.json"), "");
                var cases = new (string Name, string Json)[]
                {
                    ("schema", """{"schema":"mns.scenario/9","bodies":[]}"""),
                    ("참조", """{"schema":"mns.scenario/1","bodies":[{"name":"a","altitude":10,"mass":1}],"events":[{"name":"e","when":{"type":"time","t":1},"do":[{"type":"separate","body":"ghost"}]}]}"""),
                    ("음수 질량", """{"schema":"mns.scenario/1","bodies":[{"name":"a","altitude":10,"mass":-1}]}"""),
                    ("깨진 STL", """{"schema":"mns.scenario/1","bodies":[{"name":"a","altitude":10,"mass":1,"shape":{"type":"mesh","file":"bad.stl"}}]}"""),
                    ("빈 massprops", """{"schema":"mns.scenario/1","bodies":[{"name":"a","altitude":10,"massProps":"empty.massprops.json"}]}"""),
                    ("미지 장치", """{"schema":"mns.scenario/1","bodies":[{"name":"a","altitude":10,"mass":1,"devices":[{"type":"warpdrive","name":"w"}]}]}"""),
                    ("문자열 숫자", """{"schema":"mns.scenario/1","bodies":[{"name":"a","altitude":10,"mass":"heavy"}]}"""),
                    ("JSON 문법", """{"schema":"mns.scenario/1","bodies":[ {"name":"a",, }]}"""),
                };
                var bad = new List<string>();
                foreach (var (n, j) in cases)
                {
                    try { ScenarioDoc.Parse(j, tmp).Build(); bad.Add($"{n}: 오류 없음"); }
                    catch (Exception ex) when (ex is NullReferenceException or IndexOutOfRangeException or ArgumentOutOfRangeException) { bad.Add($"{n}: {ex.GetType().Name}"); }
                    catch (Exception ex) { if (string.IsNullOrWhiteSpace(ex.Message)) bad.Add($"{n}: 빈 메시지"); }
                }
                return (bad.Count == 0, bad.Count == 0 ? $"{cases.Length}종 모두 명확한 오류 메시지" : string.Join("; ", bad), "크래시 없음 + 메시지", "");
            });

        // ---------- ⑥ 3분할 외피 분리 ----------
        Add("A13", "⑥ 3분할", "템플릿 06: 힌지 열림 이탈·컨테이너 하중", "힌지 강성 발산(반력 파단 오판)·펄스 누락",
            () =>
            {
                var doc = ScenarioDoc.Load(scen("06_munition_3petal_split.json"));
                doc.Root["sim"]!["tEnd"] = 2.0;
                var w = doc.Build(); w.Run();
                var hs = w.Connectors.OfType<Hinge>().ToList();
                double gmax = Enumerable.Range(1, 4).Max(k => w.GetBody($"c{k}").MaxLoadG);
                bool ok = hs.Count == 3 && hs.All(h => h.Released && h.ReleaseReason.Contains("열림각") && h.MaxForce < 60000) && gmax is > 15 and < 40;
                return (ok, $"힌지 이탈 {string.Join(", ", hs.Select(h => $"{h.ReleaseTime:F3}s/{h.MaxForce / 1000:F1}kN"))}, 컨테이너 최대 {gmax:F1} G",
                    "3개 모두 열림각 이탈, 반력 < 60 kN, 15 < G < 40", "");
            });
        Add("A14", "⑥ 3분할", "정면 충돌 반발(e=1·e=0)", "충돌 판정 누락(면-면 접촉)·운동량 비보존",
            () =>
            {
                double Rel(double e)
                {
                    var w = RunJson($$$"""
                      {"schema":"mns.scenario/1","sim":{"dt":0.0005,"tEnd":1},"environment":{"vacuum":true},
                      "bodies":[{"name":"a","position":[0,0,-500],"velocity":[2,0,0],"mass":2,"shape":{"type":"box","size":[0.2,0.2,0.2]}},
                                {"name":"b","position":[1,0,-500],"velocity":[-2,0,0],"mass":2,"shape":{"type":"box","size":[0.2,0.2,0.2]}}],
                      "collisions":{"restitution":{{{e.ToString(Inv)}}},"friction":0}}
                      """);
                    return w.GetBody("b").V.X - w.GetBody("a").V.X;
                }
                double r1 = Rel(1), r0 = Rel(0);
                return (Math.Abs(r1 - 4) < 0.05 && Math.Abs(r0) < 0.05, $"분리 속도 e=1: {r1:F3}, e=0: {r0:F3} m/s", "4 ± 0.05, 0 ± 0.05", "");
            });

        // ---------- ⑦ 하중·응력·재질 ----------
        Add("A15", "⑦ 응력", "자유-자유 보 중앙 하중 M = PL/8 · 평형 잔차", "관성 완화·단면 내력 적분 오류",
            () =>
            {
                var shape = new Shape { Kind = ShapeKind.Cylinder, Radius = 0.05, Length = 1, Axis = 0 };
                var b = new Body { Name = "beam", OwnMass = 10, Shape = shape, OwnInertia = shape.UniformInertia(10) };
                b.Structure = StructureDef.Parse(new JsonObject { ["material"] = "al6061-t6", ["section"] = "solid" }, null, shape, new List<string>(), "beam");
                var r = StressModel.Build(b)!.Evaluate(new StressInput(Vec3.Zero, 1.2, 0, Vec3.Zero, [new PointLoad(Vec3.Zero, new Vec3(0, 0, 1000), "t")]));
                double ratio = r.BendingMoment.Max() / 125.0;
                return (Math.Abs(ratio - 1) < 0.01 && r.ResidualMoment < 1e-6, $"M/(PL/8) = {ratio:F4}, 잔차 {r.ResidualMoment:E1} N·m", "|1−비| < 1%, 잔차 < 1e-6", "");
            });
        Add("A16", "⑦ 재질", "재질 미지정·미지원 · 양철 좌굴 · ABS 취성 파단", "미지원 재질 크래시, 연성/취성/좌굴 판정 오분류",
            () =>
            {
                var warns = new List<string>();
                var box = new Shape { Kind = ShapeKind.Box, Size = new Vec3(0.3, 0.2, 0.2) };
                var un = StructureDef.Parse(new JsonObject { ["material"] = "unobtainium" }, null, box, warns, "u")!;
                var nb = new Body { Name = "u", OwnMass = 1, Shape = box, OwnInertia = box.UniformInertia(1), Structure = un };
                var ru = StressModel.Build(nb)!.Evaluate(new StressInput(new Vec3(30, 0, 0), 1.2, 0.1, Vec3.Zero, []));
                var cyl = new Shape { Kind = ShapeKind.Cylinder, Radius = 0.15, Length = 0.3, Axis = 0 };
                var can = new Body { Name = "can", OwnMass = 0.3, Shape = cyl, OwnInertia = cyl.UniformInertia(0.3),
                    Structure = StructureDef.Parse(new JsonObject { ["material"] = "양철", ["thickness"] = 0.00025 }, null, cyl, warns, "can") };
                var cm = StressModel.Build(can)!;
                double f = cm.BucklingStress.Min() * 2 * Math.PI * 0.15 * 0.00025 * 1.5;
                var rc = cm.Evaluate(new StressInput(Vec3.Zero, 1.2, 0, Vec3.Zero, [new PointLoad(new Vec3(-0.15, 0, 0), new Vec3(f, 0, 0), "a"), new PointLoad(new Vec3(0.15, 0, 0), new Vec3(-f, 0, 0), "b")]));
                var bar = new Shape { Kind = ShapeKind.Cylinder, Radius = 0.05, Length = 1, Axis = 0 };
                var abs = new Body { Name = "abs", OwnMass = 10, Shape = bar, OwnInertia = bar.UniformInertia(10),
                    Structure = StructureDef.Parse(new JsonObject { ["material"] = "ABS 플라스틱", ["section"] = "solid" }, null, bar, warns, "abs") };
                var ra = StressModel.Build(abs)!.Evaluate(new StressInput(Vec3.Zero, 1.2, 0, Vec3.Zero, [new PointLoad(Vec3.Zero, new Vec3(0, 0, 60000), "t")]));
                bool ok = ru.Worst == FailureState.Unknown && warns.Any(x => x.Contains("미지원")) && rc.State.Contains(FailureState.Buckling) && ra.Worst == FailureState.Fracture && !ra.State.Contains(FailureState.Yield);
                return (ok, $"미지원→{ru.Worst}, 양철 1.5σcr→{(rc.State.Contains(FailureState.Buckling) ? "좌굴" : "?")}, ABS 2×σu→{ra.Worst}", "Unknown + 경고, 좌굴, 파단(항복 단계 없음)", "");
            });

        // ---------- ⑧ 저장 → 불러오기 ----------
        Add("A17", "⑧ 저장/불러오기", "결과 .mnsr 왕복 동일성 + 자기완결 재실행", "프레임·이벤트 손실, 원본 STL 경로 의존",
            () =>
            {
                var doc = ScenarioDoc.Load(scen("04_fusion_model_drop.json"));
                var w = doc.Build(); w.Run();
                var path = Path.Combine(Path.GetTempPath(), "mns_adv", Guid.NewGuid().ToString("N")[..8], "r.mnsr");
                ResultArchive.Save(path, doc, w);
                var r = ResultArchive.Load(path);
                bool same = r.World.Recorder.Frames.Count == w.Recorder.Frames.Count
                    && r.World.Recorder.Frames.Zip(w.Recorder.Frames).All(p => p.First.T == p.Second.T && p.First.Bodies.SequenceEqual(p.Second.Bodies))
                    && r.World.EventLog.SequenceEqual(w.EventLog);
                var w2 = r.Doc.Build(); w2.Run();
                double d = (w2.Recorder.Frames[^1].Bodies[^1].Cg - w.Recorder.Frames[^1].Bodies[^1].Cg).Norm;
                return (same && d < 1e-6, $"프레임 {w.Recorder.Frames.Count} 비트 동일={same}, 재실행 최종 위치 차 {d:E1} m", "비트 동일, < 1e-6 m", "");
            });

        // ---------- ⑨ GPU ↔ CPU ----------
        Add("A18", "⑨ GPU/CPU", "GPU(ILGPU) ↔ CPU 일치 또는 GPU 없음 대체", "커널·호스트 물리식 불일치, GPU 없을 때 예외",
            () =>
            {
                var doc = ScenarioDoc.Load(scen("02_box_parachute_drop.json"));
                var (ps, _, _) = DropMonteCarlo.Sample(doc, 256, 9, "lhs");
                var cpu = new CpuDropBackend().Run(ps);
                IDropBackend? gpu = null;
                try { gpu = gpuFactory?.Invoke(); } catch { gpu = null; }
                if (gpu is null)
                {
                    var out2 = DropMonteCarlo.Run(doc, 64, new CpuDropBackend());
                    return (out2.Results.Length == 64, "GPU 없음 → CPU 대체 실행 64회", "대체 경로 동작", "GPU 미사용 환경");
                }
                var g = gpu.Run(ps);
                double maxd = ps.Select((_, i) => Math.Max(Math.Abs(cpu[i].N - g[i].N), Math.Abs(cpu[i].E - g[i].E))).Max();
                (gpu as IDisposable)?.Dispose();
                return (maxd < 1e-6, $"{gpu.Name}: 최대 |Δ| {maxd:E1} m (256회)", "< 1e-6 m", "");
            });

        // ---------- ⑩ OpenCourant ----------
        Add("A19", "⑩ OpenCourant", "미설치 · 실행 파일 손상 · 결과 손상 처리", "외부 해석기 실패가 앱/CLI 예외로 번짐",
            () =>
            {
                var root = Path.Combine(Path.GetTempPath(), "mns_adv", Guid.NewGuid().ToString("N")[..8]);
                bool missing = OpenCourantInstall.TryLocate(Path.Combine(root, "none"), out _) is null;
                Directory.CreateDirectory(Path.Combine(root, "exec"));
                File.WriteAllText(Path.Combine(root, "exec", "starter_win64.exe"), "x");
                File.WriteAllText(Path.Combine(root, "exec", "engine_win64.exe"), "x");
                var inst = OpenCourantInstall.Locate(root)!;
                var mesh = StressModel.DisplayMesh(new Shape { Kind = ShapeKind.Box, Size = new Vec3(0.2, 0.2, 0.2) });
                var deck = RadiossDeck.WriteShellImpact(Path.Combine(root, "deck"), "x", mesh, MaterialLibrary.Find("abs")!, 0.002, new Vec3(0, 0, 5), new Vec3(0, 0, 0.11), new Vec3(0, 0, -1), 0.005);
                var res = OpenCourantRunner.Run(inst, deck, TimeSpan.FromSeconds(20));
                var vtk = Path.Combine(root, "c.vtk");
                File.WriteAllText(vtk, "# vtk DataFile Version 3.0\nx\nASCII\nDATASET UNSTRUCTURED_GRID\nPOINTS 2 float\n0 0");
                bool corruptOk;
                try { VtkData.Parse(vtk); corruptOk = false; } catch (InvalidDataException) { corruptOk = true; }
                var real = OpenCourantInstall.Locate();
                return (missing && !res.Ok && res.Error.Length > 0 && corruptOk,
                    $"미설치 감지={missing}, 손상 실행 → '{res.Stage}: {Trim(res.Error)}', 손상 VTK 거부={corruptOk}",
                    "모두 예외 없이 실패 보고", real is null ? "이 환경에 OpenCourant 없음(실제 실행 A28·A29 는 정보로 표시)" : $"설치 감지: {real.Root} — 실제 실행은 A28·A29");
            });

        // ---------- ⑪ UI 스모크 ----------
        {
            var log = smokeLog ?? Path.Combine(repoRoot, "results", "smoke", "smoke.log");
            if (File.Exists(log))
            {
                var text = File.ReadAllText(log);
                bool ok = !text.Contains("EXCEPTION") && text.Contains("framesRendered=") && !text.Contains("framesRendered=0");
                list.Add(new AdvResult("A20", "⑪ UI 스모크", "MnS.Simulator --smoke (헤드리스 메뉴·레전드·트리·로그·재생)", "UI 예외·렌더 없음",
                    ok ? AdvStatus.Pass : AdvStatus.Fail, Trim(string.Join(" · ", text.Split('\n').Where(l => l.StartsWith("ui.") || l.StartsWith("framesRendered") || l.StartsWith("treeBodies")).Take(8)), 300),
                    "예외 없음, 프레임 렌더", $"로그: {log} ({File.GetLastWriteTime(log):yyyy-MM-dd HH:mm})"));
            }
            else
                list.Add(new AdvResult("A20", "⑪ UI 스모크", "MnS.Simulator --smoke", "UI 예외·렌더 없음", AdvStatus.Info, "미실행(테스트 명령 밖 — 앱 실행 필요)", "—",
                    "src/Sim.App/bin/Release/net8.0/MnS.Simulator.exe --smoke tests/Sim.Core.Tests/scenarios/06_munition_3petal_split.json --at 0.7"));
        }

        // ---------- ⑦ 재질 거동: 착지 충돌(1·2단계) ----------
        var box = new Shape { Kind = ShapeKind.Box, Size = new Vec3(0.40, 0.27, 0.27) };
        var cornerQ = Quat.FromEuler(45 * Math.PI / 180, 35.264 * Math.PI / 180, 0);
        ImpactAssessment Hit(Material m, double t, double v, Quat q, double mass = 5.5) =>
            ImpactDamage.Assess("b", m, new StructureDef { Material = m, Thickness = t, Section = "thinWall" }, box, mass, new Vec3(0, 0, v), q);
        Material Plain(bool brittle) => MaterialLibrary.Parse(new JsonObject
        {
            ["key"] = "adv", ["brittle"] = brittle, ["density"] = 2000, ["E_GPa"] = 10, ["yield_MPa"] = 50, ["ultimate_MPa"] = 60, ["elongation"] = 0.2,
        });
        Add("A21", "⑦ 재질", "템플릿 08: 같은 상자·3 m 낙하 — 플라스틱 깨짐 / 양철 찌그러짐 / PC 찌그러짐 / 고체 안전", "재질별 거동 오분류(사용자 질문의 핵심)",
            () =>
            {
                var w = ScenarioDoc.Load(scen("08_material_drop_compare.json")).Build(); w.Run();
                var rep = StructuralAnalysis.Analyze(w);
                FailureState S(string n) => rep.Bodies.Single(h => h.Body.Name == n).Impact!.State;
                var expect = new (string Body, Func<FailureState, bool> Ok, string Want)[]
                {
                    ("abs_corner", s => s == FailureState.Fracture, "깨짐"), ("abs_face", s => s >= FailureState.Fracture, "깨짐(국부, FEA 교차검증)"),
                    ("pla_corner", s => s == FailureState.Fracture, "깨짐"), ("cfrp_corner", s => s == FailureState.Fracture, "깨짐"),
                    ("pc_corner", s => s == FailureState.Yield, "찌그러짐"), ("tin_face", s => s == FailureState.Yield, "찌그러짐"),
                    ("tin_corner", s => s == FailureState.Yield, "찌그러짐"), ("al_face", s => s is FailureState.Yield or <= FailureState.Caution, "얕은 찌그러짐 또는 손상 없음(FEA 탄성)"),
                    ("epp_block", s => s == FailureState.Yield, "완충 압축"), ("al_sensor_solid", s => s <= FailureState.Caution, "손상 없음"),
                    ("abs_puck_solid", s => s <= FailureState.Caution, "손상 없음"),
                };
                var bad = expect.Where(e => !e.Ok(S(e.Body))).Select(e => $"{e.Body}={S(e.Body)}(기대 {e.Want})").ToList();
                var tin = rep.Bodies.Single(h => h.Body.Name == "tin_face").Impact!;
                return (bad.Count == 0, bad.Count == 0 ? $"11개 물체 모두 기대 범주 · 양철 면 찌그러짐 {tin.DentDepth * 1000:F0} mm" : string.Join("; ", bad),
                    "취성 플라스틱·CFRP 꼭짓점 깨짐, 양철·PC·Al 찌그러짐, 고체 안전", "");
            });
        Add("A22", "⑦ 재질", "에너지 닫힘 + 손상 시작 속도 경계(±1e-6)", "임계 속도·흡수 에너지 계산 불일치(경계에서 판정 뒤집힘)",
            () =>
            {
                var bad = new List<string>();
                foreach (var brittle in new[] { false, true })
                {
                    var m = Plain(brittle);
                    double vc = Hit(m, 0.002, 3, cornerQ).DamageSpeed;
                    var lo = Hit(m, 0.002, vc * (1 - 1e-6), cornerQ); var hi = Hit(m, 0.002, vc * (1 + 1e-6), cornerQ);
                    if (lo.State > FailureState.Caution || hi.State != (brittle ? FailureState.Fracture : FailureState.Yield)) bad.Add($"{(brittle ? "취성" : "연성")}: {lo.State}/{hi.State}");
                }
                var d = Hit(Plain(false), 0.0005, 6, cornerQ);
                double closure = Math.Abs(d.ElasticCapacity + d.CrushForce * d.DentDepth - d.Energy) / d.Energy;
                if (closure > 1e-12) bad.Add($"에너지 닫힘 {closure:E1}");
                return (bad.Count == 0, bad.Count == 0 ? $"경계 아래 손상 없음 / 위 항복·파단, U + P·δ = E_n (오차 {closure:E1})" : string.Join("; ", bad), "판정 전환 정확, 닫힘 < 1e-12", "");
            });
        Add("A23", "⑦ 재질", "속도 0.2→30 m/s · 두께 0.2→4 mm 스윕 단조성", "속도가 커졌는데 손상이 줄거나, 두꺼워졌는데 더 찌그러짐",
            () =>
            {
                var bad = new List<string>();
                foreach (var key in new[] { "tinplate", "abs", "al6061-t6", "pc", "cfrp-qi" })
                {
                    var m = MaterialLibrary.Find(key)!;
                    var prev = FailureState.Unknown; double prevDent = 0;
                    for (double v = 0.2; v <= 30; v *= 1.2)
                    {
                        var a = Hit(m, 0.001, v, cornerQ);
                        double dent = double.IsFinite(a.DentDepth) ? a.DentDepth : 0;
                        if (a.State < prev || dent < prevDent - 1e-12) { bad.Add($"{key} v={v:F2}"); break; }
                        prev = a.State; prevDent = dent;
                    }
                    double last = double.PositiveInfinity; var lastS = FailureState.Fracture;
                    foreach (var t in new[] { 0.0002, 0.0005, 0.001, 0.002, 0.004 })
                    {
                        var a = Hit(m, t, 7, cornerQ);
                        double dent = double.IsFinite(a.DentDepth) ? a.DentDepth : 0;
                        if (dent > last + 1e-12 || a.State > lastS) { bad.Add($"{key} t={t}"); break; }
                        last = dent; lastS = a.State;
                    }
                }
                return (bad.Count == 0, bad.Count == 0 ? "5개 재질 × (속도 26점 · 두께 5점) 단조" : string.Join("; ", bad), "속도↑ → 손상 비감소, 두께↑ → 손상 비증가", "");
            });
        Add("A24", "⑤ 오류 입력", "착지 판정: 질량 0 · NaN 속도 · 위로 이동 · 미지원 재질 · 재질 E 음수", "크래시·NaN 판정·미지원 재질에서 예외",
            () =>
            {
                var abs = MaterialLibrary.Find("abs")!;
                var def = new StructureDef { Material = abs, Thickness = 0.002, Section = "thinWall" };
                var bad = new List<string>();
                void Expect<T>(string n, Action a) where T : Exception
                {
                    try { a(); bad.Add($"{n}: 오류 없음"); }
                    catch (T ex) { if (string.IsNullOrWhiteSpace(ex.Message)) bad.Add($"{n}: 빈 메시지"); }
                    catch (Exception ex) { bad.Add($"{n}: {ex.GetType().Name}"); }
                }
                Expect<InvalidDataException>("질량 0", () => ImpactDamage.Assess("b", abs, def, box, 0, new Vec3(0, 0, 5), Quat.Identity));
                Expect<InvalidDataException>("NaN 속도", () => ImpactDamage.Assess("b", abs, def, box, 1, new Vec3(double.NaN, 0, 5), Quat.Identity));
                Expect<InvalidDataException>("E 음수", () => MaterialLibrary.Parse(new JsonObject { ["key"] = "x", ["E_GPa"] = -1, ["yield_MPa"] = 10 }));
                var up = ImpactDamage.Assess("b", abs, def, box, 1, new Vec3(2, 0, -3), Quat.Identity);
                if (up.Energy != 0 || up.State > FailureState.Safe) bad.Add($"위로 이동: {up.State}");
                var warns = new List<string>();
                var un = new Body { Name = "u", OwnMass = 1, Shape = box, Structure = StructureDef.Parse(new JsonObject { ["material"] = "vibranium", ["thickness"] = 0.001 }, null, box, warns, "u"),
                    Impact = new ImpactRecord { Mass = 1, Velocity = new Vec3(0, 0, 5) } };
                if (ImpactDamage.Assess(un) is not null) bad.Add("미지원 재질: 판정함");
                return (bad.Count == 0, bad.Count == 0 ? "5종 모두 명확한 오류 또는 무해(미지원 → 판정 생략 + 경고)" : string.Join("; ", bad), "예외 메시지·NaN 없음", "");
            });
        Add("A25", "③ 대칭", "착지 자세 거울(roll ±45°)·방위(yaw 0/90°) 불변", "좌표 변환 오류로 같은 물리 상황에서 판정이 달라짐",
            () =>
            {
                var abs = MaterialLibrary.Find("abs")!;
                var a = Hit(abs, 0.002, 7, Quat.FromEuler(45 * Math.PI / 180, 35.264 * Math.PI / 180, 0));
                var b = Hit(abs, 0.002, 7, Quat.FromEuler(-45 * Math.PI / 180, 35.264 * Math.PI / 180, 0));
                var c = Hit(abs, 0.002, 7, Quat.FromEuler(45 * Math.PI / 180, 35.264 * Math.PI / 180, Math.PI / 2));
                bool ok = a.State == b.State && a.State == c.State && Math.Abs(a.ElasticCapacity - b.ElasticCapacity) < 1e-9 && Math.Abs(a.ElasticCapacity - c.ElasticCapacity) < 1e-9
                          && Math.Abs(a.ContactPointBrf.Y + b.ContactPointBrf.Y) < 1e-12 && (a.ContactPointBrf - c.ContactPointBrf).Norm < 1e-12;
                return (ok, $"{a.State}/{b.State}/{c.State}, 용량 {a.ElasticCapacity:F3}/{b.ElasticCapacity:F3}/{c.ElasticCapacity:F3} J, 접촉점 y {a.ContactPointBrf.Y:F4}/{b.ContactPointBrf.Y:F4}",
                    "같은 판정·용량, 접촉점 y 반전, yaw 불변", "");
            });
        Add("A26", "② 수치", "극한 착지 속도(0 · 1e-9 · 300 · 3000 m/s) 전 재질", "NaN·무한 찌그러짐·음수 깊이",
            () =>
            {
                var bad = new List<string>();
                foreach (var m in MaterialLibrary.All)
                    foreach (var v in new[] { 0.0, 1e-9, 300, 3000 })
                    {
                        var def = new StructureDef { Material = m, Thickness = m.Class == "foam" ? null : 0.001, Section = m.Class == "foam" ? "solid" : "thinWall" };
                        var a = ImpactDamage.Assess("b", m, def, box, 5, new Vec3(0, 0, v), cornerQ);
                        bool finite = double.IsFinite(a.Energy) && (double.IsNaN(a.DentDepth) || (double.IsFinite(a.DentDepth) && a.DentDepth >= 0 && a.DentDepth <= a.CrushLimit + 1e-12));
                        bool sane = v <= 1e-9 ? a.State <= FailureState.Caution || m.Class == "foam" : v >= 300 ? a.State >= FailureState.Yield : true;
                        if (!finite || !sane || a.Verdict.Contains("NaN")) bad.Add($"{m.Key}@{v}: {a.State} δ={a.DentDepth}");
                    }
                return (bad.Count == 0, bad.Count == 0 ? $"{MaterialLibrary.All.Count}개 재질 × 4 속도 유한·단조(정지 손상 없음, 고속 손상)" : string.Join("; ", bad.Take(6)), "유한, 0 ≤ δ ≤ 압궤 한계", "");
            });
        Add("A27", "⑧ 저장/불러오기", "착지 판정 포함 결과 .mnsr 왕복 → 다시 판정 동일", "착지 기록(속도·자세·질량) 손실로 불러온 결과의 재질 판정이 달라짐",
            () =>
            {
                var doc = ScenarioDoc.Load(scen("08_material_drop_compare.json"));
                var w = doc.Build(); w.Run();
                var rep = StructuralAnalysis.Analyze(w);
                var path = Path.Combine(Path.GetTempPath(), "mns_adv", Guid.NewGuid().ToString("N")[..8], "m.mnsr");
                ResultArchive.Save(path, doc, w, null, rep.ToJson());
                var r = ResultArchive.Load(path);
                var diffs = rep.Bodies.Where(h => h.Impact is not null).Select(h =>
                {
                    var again = ImpactDamage.Assess(r.World.GetBody(h.Body.Name), r.World.Env.Gravity);
                    return again is null ? $"{h.Body.Name}: 판정 없음" : again.State != h.Impact!.State || Math.Abs(again.Energy - h.Impact.Energy) > 1e-9 ? $"{h.Body.Name}: {again.State}≠{h.Impact.State}" : null;
                }).Where(x => x is not null).ToList();
                bool json = r.Structural?["bodies"]?["tin_corner"]?["impact"]?["verdict"] is not null;
                return (diffs.Count == 0 && json, diffs.Count == 0 ? $"11개 물체 판정·에너지 동일, structural.json impact 보존={json}" : string.Join("; ", diffs), "동일", "");
            });

        // ---------- ⑩ OpenCourant 실제 실행(설치되어 있을 때) ----------
        var oc = System.Environment.GetEnvironmentVariable("MNS_SKIP_OPENCOURANT") == "1" ? null : OpenCourantInstall.Locate();
        string ocDir() => Path.Combine(Path.GetTempPath(), "mns_adv_fea", Guid.NewGuid().ToString("N")[..8]);
        if (oc is null)
        {
            list.Add(new AdvResult("A28", "⑩ OpenCourant", "실제 솔버 Taylor 봉(Al 6061-T6, 200 m/s)", "덱 형식·종료 판정·결과 변환 오류", AdvStatus.Info, "OpenCourant 미설치(또는 MNS_SKIP_OPENCOURANT=1)", "—"));
            list.Add(new AdvResult("A29", "⑩ OpenCourant", "실제 솔버 셸 충돌: ABS vs 양철 꼭짓점 낙하", "재질 카드·파단 삭제·질량 분포 오류", AdvStatus.Info, "OpenCourant 미설치(또는 MNS_SKIP_OPENCOURANT=1)", "—"));
        }
        else
        {
            Add("A28", "⑩ OpenCourant", "실제 솔버 Taylor 봉(Al 6061-T6, 200 m/s) vs Wilkins–Guinan", "덱 형식·종료 판정(경고 종료)·inf 결과·강체 벽 셀 혼입",
                () =>
                {
                    var mat = MaterialLibrary.Find("al6061-t6")!;
                    var r = OpenCourantRunner.Run(oc, RadiossDeck.WriteTaylorBar(ocDir(), "taylor", mat, 0.0254, 0.0076, 200), TimeSpan.FromMinutes(10));
                    if (!r.Ok) return (false, $"{r.Stage}: {Trim(r.Error)}", "정상 종료", "");
                    double ratio = RadiossDeck.TaylorLength(r.Final!) / 0.0254, exp = RadiossDeck.TaylorFinalLengthRatio(mat.Density, 200, mat.Yield);
                    return (Math.Abs(ratio / exp - 1) <= 0.15, $"L_f/L_0 = {ratio:F3} vs 상관식 {exp:F3} ({(ratio / exp - 1) * 100:+0.0;-0.0}%), {r.Seconds:F1} s", "±15%", $"설치: {oc.Root}");
                });
            Add("A29", "⑩ OpenCourant", "실제 솔버 셸 충돌: ABS vs 양철 꼭짓점 7.2 m/s (1단계와 범주 비교)", "재질 카드·파단 삭제·질량 분포 오류, 1단계와 상반된 판정",
                () =>
                {
                    var abs = FeaBenchmarks.CornerDrop(oc, ocDir(), MaterialLibrary.Find("abs")!, 0.002);
                    var tin = FeaBenchmarks.CornerDrop(oc, ocDir(), MaterialLibrary.Find("tinplate")!, 0.00025);
                    var s1Abs = Hit(MaterialLibrary.Find("abs")!, 0.002, 7.2, cornerQ); var s1Tin = Hit(MaterialLibrary.Find("tinplate")!, 0.00025, 7.2, cornerQ);
                    bool ok = abs.State == FailureState.Fracture && abs.Deleted > 0 && abs.Yielded <= 2 * abs.Deleted + 5
                              && tin.State >= FailureState.Yield && tin.Yielded > 3 * Math.Max(tin.Deleted, 1)
                              && s1Abs.State == FailureState.Fracture && s1Tin.State == FailureState.Yield;
                    return (ok, $"FEA ABS: {Trim(abs.Verdict, 90)} / 양철: {Trim(tin.Verdict, 110)} · 1단계 {s1Abs.State}/{s1Tin.State}",
                        "ABS 요소 삭제(소성 거의 없음), 양철 넓은 소성(삭제의 3배 초과), 1단계 깨짐/찌그러짐", "거친 메시(면당 10×10), 내용물 질량 지면 쪽 절반 분포");
                });
        }
        Add("A30", "⑩ OpenCourant", "발산 결과(inf/nan)·경고 종료·오류 종료 판정", "발산을 정상 결과로 표시하거나 경고만 있는 정상 종료를 실패로 처리",
            () =>
            {
                var dir = ocDir(); Directory.CreateDirectory(dir);
                string W(string n, string s) { var p = Path.Combine(dir, n); File.WriteAllText(p, s); return p; }
                var v = VtkData.Parse(W("d.vtk", "# vtk DataFile Version 3.0\nx\nASCII\nDATASET UNSTRUCTURED_GRID\nPOINTS 3 float\n0 0 0 -inf inf 0 1.#QNAN 0 0\nCELLS 1 4\n3 0 1 2\nCELL_TYPES 1\n5\n"));
                bool warnOk = OpenCourantRunner.Normal(W("w.out", "TERMINATION WITH WARNING\n 0 ERROR(S)\n 2 WARNING(S)\n"));
                bool errRejected = !OpenCourantRunner.Normal(W("e.out", "TERMINATION WITH WARNING\n 3 ERROR(S)\n"));
                bool ok = v.HasNonFinitePoints && warnOk && errRejected;
                return (ok, $"inf/nan 감지={v.HasNonFinitePoints}, 경고 종료 정상={warnOk}, 오류 종료 거부={errRejected}", "모두 참",
                    "실제 실행에서 발견: 기존 코드는 경고 종료를 실패로, Taylor 고체 EPS_max 붕괴 결과(inf)를 파서 예외로 처리했음");
            });
        var smoke = smokeLog ?? Path.Combine(repoRoot, "results", "smoke", "smoke.log");
        StressColorScenarios(list, Add, scen, smoke);
        CalculixScenarios(list, Add, scen, oc);
        // ⑪ UI 스모크: CalculiX 메뉴 경로(F7) → 면 색 · 변형 표시 · .mnsr 저장/복원(앱 실행 로그가 있을 때)
        {
            var lines = File.Exists(smoke) ? File.ReadAllLines(smoke) : [];
            var ccxLine = lines.FirstOrDefault(l => l.StartsWith("ui.ccx "));
            if (ccxLine is null)
                list.Add(new AdvResult("A53", "⑪ UI 스모크", "CalculiX 해석 메뉴 → 면 색 · 변형 표시 · 결과 파일 저장/복원", "FEA 결과가 3D 면 색·레전드에 안 붙거나 .mnsr 에서 사라짐", AdvStatus.Info,
                    "스모크 로그에 ui.ccx 없음(앱 실행 필요)", "—", "MnS.Simulator --smoke tests/Sim.Core.Tests/scenarios/06_munition_3petal_split.json --ccx c2 --color ccx --save results/smoke/ccx_c2.mnsr --reload"));
            else
            {
                bool colored = ccxLine.Contains("colored=True") && !ccxLine.Contains("faces=0 ");
                bool deformed = System.Text.RegularExpressions.Regex.Match(ccxLine, @"deformed=(\d+)") is { Success: true } dm && int.Parse(dm.Groups[1].Value) > 0;
                var restore = lines.FirstOrDefault(l => l.StartsWith("ui.save.fea "));
                bool restored = restore is not null && System.Text.RegularExpressions.Regex.Match(restore, @"restored=(\d+)") is { Success: true } rm && int.Parse(rm.Groups[1].Value) > 0;
                var legend = lines.FirstOrDefault(l => l.StartsWith("ui.legend ")) ?? "";
                bool leg = legend.Contains("CalculiX");
                bool ok = colored && deformed && (restore is null || restored) && leg;
                list.Add(new AdvResult("A53", "⑪ UI 스모크", "CalculiX 해석 메뉴 → 면 색 · 변형 표시 · 결과 파일 저장/복원", "FEA 결과가 3D 면 색·레전드에 안 붙거나 .mnsr 에서 사라짐",
                    ok ? AdvStatus.Pass : AdvStatus.Fail, $"{ccxLine[7..]} · {(restore ?? "저장 안 함")} · 레전드 CalculiX={leg}", "색·변형·레전드, 저장 시 복원 ≥ 1",
                    $"로그: {smoke} ({File.GetLastWriteTime(smoke):yyyy-MM-dd HH:mm})"));
            }
        }

        // ---------- 템플릿 09 (PRD AA절, FR-AN-07) ----------
        // 실제 설계 자료(시나리오 09 · config/models/*_mass_budget.json)가 있어야 하는 항목 — 없으면(공개판) 정보 항목으로 건너뛴다
        var budgetDir = Path.Combine(repoRoot, "config", "models");
        string? budget = Directory.Exists(budgetDir) ? Directory.GetFiles(budgetDir, "*_mass_budget.json").OrderBy(p => p, StringComparer.Ordinal).FirstOrDefault() : null;
        bool has09 = File.Exists(scen("09_munition_3petal_longcontainer_real.json"));
        void AddDesign(bool available, string id, string cat, string title, string breaks, Func<(bool Ok, string Measured, string Criterion, string Note)> f)
        {
            if (available) Add(id, cat, title, breaks, f);
            else list.Add(new AdvResult(id, cat, title, breaks, AdvStatus.Info, "설계 자료 없음 — 건너뜀", "자료 파일 존재", "실제 설계 시나리오·질량표가 필요한 항목"));
        }
        AddDesign(has09 && budget is not null, "A54", "④ 보존", "템플릿 09: 단계 전이 내내 질량 보존 + 탑재 합 = 질량 예산(config/models 의 질량표 — 사용자 질량표 × CAD 분포) 컨테이너·뚜껑·도어 2·낙하산 팩 2 + 드론·센서 4세트(질량 한계 판정)", "분리·드로그 팩 이탈·레일 이탈·센서 투하에서 질량이 사라지거나 중복, 질량 한계 판정 누락",
            () =>
            {
                var doc = ScenarioDoc.Load(scen("09_munition_3petal_longcontainer_real.json"));
                doc.Root["realModels"] = new JsonObject { ["mode"] = "synthetic" };
                doc.Root["sim"]!["tEnd"] = 60.0;
                var w = doc.Build(); w.Run();
                var s = Results.Summary(doc, w);
                var mb = s["metrics"]!["massBudget"]!;
                double total = mb["total_kg"]!.GetValue<double>(), init = mb["allBodiesInitial_kg"]!.GetValue<double>(), fin = mb["allBodiesFinal_kg"]!.GetValue<double>();
                var g = J.ParseObject(File.ReadAllText(budget!))["groups"]!;
                double M(string n) => g[n]!["mass"]!.GetValue<double>();
                double expect = M("container") + M("container_lid") + M("container_door_L") + M("container_door_R") + M("drogue_pack") + M("main_pack") + 4 * (M("drone") + M("sensor"));
                // 루트 물체 합성 질량 합 = 전 물체 고유 질량 합(자유·착지 루트만 세면 중복·누락 없음)
                double roots = w.Bodies.Where(b => b.IsRoot && b.Mode != BodyMode.Kinematic).Sum(b => b.Mass);
                bool pass = mb["pass"]!.GetValue<bool>();
                bool ok = Math.Abs(total - expect) < 1e-9 && Math.Abs(init - fin) < 1e-9 && Math.Abs(roots - init) < 1e-9 && pass == expect <= 47.0
                          && w.FiredEvents.ContainsKey("tether4_cut");
                return (ok, $"탑재 {total:F2} kg(기대 {expect:F2}), 초기 {init:F3} → 최종 {fin:F3} kg, 루트 합성 합 {roots:F3}, 질량 한계 판정 {(pass ? "통과" : "초과")}",
                    "합 일치 · 보존 오차 < 1e-9 kg · 질량 한계 판정 일치 · tether4 릴리스까지 진행", "");
            });
        AddDesign(has09, "A55", "⑥ 3분할", "템플릿 09: 단계 전이 순서(기폭→노출→뚜껑·드로그→주낙하산→도어 개방→드론 1..4 래치 해제→레일 이탈→암 전개→테더 릴리스, 1대씩)", "이벤트 연쇄 누락·순서 뒤바뀜(도어 개방 전 래치 해제, 앞 드론 릴리스 전 다음 드론 출구 등)",
            () =>
            {
                var doc = ScenarioDoc.Load(scen("09_munition_3petal_longcontainer_real.json"));
                doc.Root["realModels"] = new JsonObject { ["mode"] = "synthetic" };
                doc.Root["sim"]!["tEnd"] = 60.0;
                var w = doc.Build(); w.Run();
                var chain = new List<string> { "split_detonation", "container_exposed", "lid_drogue", "main_deploy", "door_open", "door_L_swing.open", "door_R_swing.open" };
                for (int k = 1; k <= 4; k++) chain.AddRange([$"latch{k}_release", $"rail{k}.exit", $"drone{k}_out", $"drone{k}_arms", $"tether{k}_cut"]);
                var bad = new List<string>();
                double T(string e) => w.FiredEvents.TryGetValue(e, out var t) ? t : double.NaN;
                foreach (var e in chain) if (!double.IsFinite(T(e))) bad.Add($"{e} 누락");
                void Before(string a, string b) { if (double.IsFinite(T(a)) && double.IsFinite(T(b)) && !(T(a) < T(b))) bad.Add($"{a}({T(a):F2}) ≥ {b}({T(b):F2})"); }
                Before("split_detonation", "container_exposed"); Before("container_exposed", "lid_drogue"); Before("lid_drogue", "main_deploy"); Before("main_deploy", "door_open");
                Before("door_L_swing.open", "latch1_release"); Before("door_R_swing.open", "latch1_release");
                for (int k = 1; k <= 4; k++)
                {
                    Before($"latch{k}_release", $"rail{k}.exit"); Before($"rail{k}.exit", $"drone{k}_arms"); Before($"drone{k}_arms", $"tether{k}_cut");
                    if (k > 1) Before($"tether{k - 1}_cut", $"latch{k}_release");
                }
                // 실제 물체 상태: 드론은 레일 이탈 순간 자유, 드로그 팩은 주낙하산 때 분리, 도어는 끝까지 컨테이너에 붙어 있음
                for (int k = 1; k <= 4; k++)
                    if (Math.Abs(w.GetBody($"drone{k}").ReleaseTime - T($"rail{k}.exit")) > 1e-9) bad.Add($"drone{k} 분리 시각 ≠ rail{k}.exit");
                if (Math.Abs(w.GetBody("drogue_pack").ReleaseTime - T("main_deploy")) > 1e-9) bad.Add("드로그 팩 분리 시각 ≠ 주낙하산");
                if (w.GetBody("door_L").Mode != BodyMode.Stowed || w.GetBody("door_R").Mode != BodyMode.Stowed) bad.Add("도어가 컨테이너에서 떨어짐");
                return (bad.Count == 0, bad.Count == 0 ? $"{chain.Count}개 이벤트 순서·1대씩 출구 일치" : string.Join("; ", bad.Take(6)), "전부 발생 · 선후 관계 · 앞 드론 릴리스 뒤 다음 래치", "");
            });
        Add("A56", "⑥ 3분할", "전개 이상 판정 경계: 라인 원뿔 반경 ±1 mm · 상대속도 문턱 ±0.01 m/s · 수납 낙하산 무시", "간섭 영역 경계에서 판정 뒤집힘 오류, 느린 접촉을 이상으로 오판, 전개 전 낙하산에 이상 적용",
            () =>
            {
                return ProbeCone();

                (bool, string, string, string) ProbeCone()
                {
                    var bad = new List<string>();
                    // 대기(밀도 고정) 안에서 c 가 +x 로 10 m/s → 상대풍 −x → 캐노피 축 −x, 라인 신장 중(길게)이므로 끝 = 결속점 − x̂·(riser+0.4·D0)·ext
                    (int A, int I) Run(double y, double rel, bool deploy, double tProbe)
                    {
                        var json = $$$"""
                          {"schema":"mns.scenario/1","sim":{"dt":0.0005,"tEnd":{{{tProbe.ToString("R", Inv)}}},"outputRate":2000},"environment":{"constantDensity":1e-9,"gravity":0},
                           "bodies":[{"name":"c","position":[0,0,-500],"velocity":[10,0,0],"mass":50,"shape":{"type":"box","size":[0.2,0.2,0.2]},"aero":"none",
                                      "devices":[{"type":"parachute","name":"ch","d0":2.0,"attach":[-0.1,0,0],"riser":5,"lineStretchTime":0.0001,"fillConstant":1000}]},
                                     {"name":"d","position":[-3.6,{{{y.ToString("R", Inv)}}},-500],"velocity":[{{{(10 - rel).ToString("R", Inv)}}},0,0],"mass":1,"aero":"none","shape":{"type":"sphere","radius":0.0005}}],
                           "events":[{{{(deploy ? """{"name":"dep","when":{"type":"time","t":0},"do":[{"type":"deploy","device":"c.ch"}]}""" : "")}}}],
                           "collisions":{"parachutes":true,"canopyRegion":"sphere+lines","chuteAnomaly":{"delay":0.3,"partialFill":0.6,"minRelSpeed":2.0} } }
                          """;
                        var w = RunJson(json);
                        var ch = w.GetBody("c").Devices.OfType<Parachute>().Single();
                        return (ch.Anomalies.Count, w.Collisions!.InterferenceCount);
                    }
                    // 기하: 결속점 x = −0.1, 캐노피 끝 x = −0.1 − (5 + 0.8) = −5.9, 길이 5.8. 탐침 x = −3.6 → f = 3.5/5.8, 캐노피 반경 rc = 1.0·max(fill, 0.3) = 0.3
                    double f = 3.5 / 5.8, r = 0.3 * f;
                    var inside = Run(r - 0.001, 5, true, 0.001); var outside = Run(r + 0.001, 5, true, 0.001);
                    var slow = Run(0, 1.99, true, 0.001); var fast = Run(0, 2.01, true, 0.001); var stowed = Run(0, 5, false, 0.001);
                    if (inside.A != 1) bad.Add($"원뿔 안 1 mm: 이상 {inside.A}");
                    if (outside.I != 0 || outside.A != 0) bad.Add($"원뿔 밖 1 mm: 간섭 {outside.I}, 이상 {outside.A}");
                    if (slow.A != 0 || slow.I != 1) bad.Add($"1.99 m/s: 이상 {slow.A}, 간섭 {slow.I}");
                    if (fast.A != 1) bad.Add($"2.01 m/s: 이상 {fast.A}");
                    if (stowed.A != 0 || stowed.I != 0) bad.Add($"수납: 이상 {stowed.A}, 간섭 {stowed.I}");
                    return (bad.Count == 0, bad.Count == 0 ? $"반경 {r * 1000:F1} mm 경계 안/밖 = 이상 1/0, 상대속도 1.99/2.01 = 0/1, 수납 0" : string.Join("; ", bad),
                        "경계 ±1 mm · 문턱 ±0.01 m/s 판정 일치", "");
                }
            });
        Add("A57", "⑤ 오류 입력", "실모델 해석: 폴더 없음 → 합성 대체(경고) · required 모드 → 명확한 오류 · fallback 없음 → 오류 · 잘못된 mode", "실모델이 없을 때 크래시 또는 조용히 엉뚱한 질량 사용, 오류 메시지 없음",
            () =>
            {
                var bad = new List<string>();
                string J(string mode, bool fb) => $$$"""
                  {"schema":"mns.scenario/1","sim":{"tEnd":0.01},"realModels":{"dir":"__no_such_dir__","mode":"{{{mode}}}"},
                   "bodies":[{"name":"x","altitude":100,"massProps":"real:x.massprops.json"{{{(fb ? ""","fallback":{"mass":2.5,"shape":{"type":"box","size":[0.1,0.1,0.1]}}""" : "")}}} }]}
                  """;
                // 이 실행 흐름에서만 실모델 폴더 없음 — 프로세스 환경변수를 바꾸면 병렬 실행 중인 다른 빌드가 흔들린다
                using (Sim.Core.Scenario.ScenarioLoader.OverrideRealModels(null))
                {
                    var w = Build(J("auto", true));
                    if (w.GetBody("x").ModelSource != "synthetic" || Math.Abs(w.GetBody("x").OwnMass - 2.5) > 1e-12 || !w.Warnings.Any(s => s.Contains("합성 대체"))) bad.Add("auto: 대체·경고 없음");
                    foreach (var (n, j) in new[] { ("required", J("required", true)), ("fallback 없음", J("auto", false)), ("mode 오타", J("sometimes", true)) })
                    {
                        try { Build(j); bad.Add($"{n}: 오류 없음"); }
                        catch (InvalidDataException ex) { if (string.IsNullOrWhiteSpace(ex.Message)) bad.Add($"{n}: 빈 메시지"); }
                        catch (Exception ex) { bad.Add($"{n}: {ex.GetType().Name}"); }
                    }
                }
                return (bad.Count == 0, bad.Count == 0 ? "대체 + 경고, 오류 3종 InvalidDataException(메시지)" : string.Join("; ", bad), "크래시 없음 + 메시지", "");
            });
        return list;
    }

    // ================= CalculiX 외부 FEA(PRD Z, FR-FEA-01~06) =================

    private static void CalculixScenarios(List<AdvResult> list, Action<string, string, string, string, Func<(bool Ok, string Measured, string Criterion, string Note)>> Add,
        Func<string, string> scen, OpenCourantInstall? oc)
    {
        var ccx = System.Environment.GetEnvironmentVariable("MNS_SKIP_CALCULIX") == "1" ? null : CalculixInstall.Locate();
        string dir() { var d = Path.Combine(Path.GetTempPath(), "mns_adv_ccx", Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(d); return d; }
        Add("A46", "⑩ CalculiX", "미설치 · 깨진 실행 파일 · 잘못된 덱(ccx segfault) · 덱 사전 검사", "외부 해석기 실패가 예외로 번지거나, ccx 가 메시지 없이 죽은 것을 성공으로 처리",
            () =>
            {
                bool missing = CalculixInstall.TryLocate(Path.Combine(dir(), "none"), out var why) is null && why.Length > 0;
                var root = dir();
                File.WriteAllText(Path.Combine(root, "ccx_static.exe"), "not an executable");
                var m = new CcxModel { Mesh = FeMesh.Beam(1, 0.1, 0.1, 2, 1, 1), Material = CcxBenchmarks.Steel };
                m.Fixed.Add((0, 1, 3));
                var broken = CcxRunner.Solve(CalculixInstall.Locate(root)!, m, dir(), "x", TimeSpan.FromSeconds(20));
                var bad = new List<string>();
                foreach (var (n, mm) in new (string, CcxModel)[]
                {
                    ("구속 없음", new CcxModel { Mesh = FeMesh.Beam(1, 0.1, 0.1, 2, 1, 1), Material = CcxBenchmarks.Steel }),
                    ("음의 E", new CcxModel { Mesh = FeMesh.Beam(1, 0.1, 0.1, 2, 1, 1), Material = new Material { Key = "bad", Name = "bad", E = -1, Density = 7850 } }),
                })
                {
                    if (n != "구속 없음") mm.Fixed.Add((0, 1, 3));
                    try { CcxDeck.Validate(mm); bad.Add($"{n}: 통과됨"); } catch (InvalidDataException) { } catch (Exception ex) { bad.Add($"{n}: {ex.GetType().Name}"); }
                }
                string segf = "실제 ccx 없음";
                bool segOk = true;
                if (ccx is not null)
                {
                    var d = dir();
                    File.WriteAllText(Path.Combine(d, "bad.inp"), "*NODE, NSET=NALL\n1, 0, 0, 0\n*FOOBAR\n*STEP\n*STATIC\n*END STEP\n");
                    var rb = CcxRunner.Run(ccx, Path.Combine(d, "bad.inp"), TimeSpan.FromSeconds(60));
                    segOk = !rb.Ok && rb.Error.Length > 0;
                    segf = Trim(rb.Error, 90);
                }
                bool ok = missing && !broken.Ok && broken.Error.Length > 0 && bad.Count == 0 && segOk;
                return (ok, $"미설치 감지={missing}, 깨진 exe → '{Trim(broken.Error, 60)}', 덱 검사 {(bad.Count == 0 ? "2종 거부" : string.Join("; ", bad))}, 잘못된 키워드 → '{segf}'",
                    "모두 예외 없이 실패 보고", "ccx 2.23 Windows 는 알 수 없는 키워드에서 메시지 없이 종료 코드 139 로 죽는다(실측) → 종료 코드·'Job finished'·.frd 로 판정");
            });
        if (ccx is null)
        {
            foreach (var (id, t) in new[] { ("A47", "해석해 6종(외팔보·Roark 판·후프·Kirsch·단순보 진동·오일러 좌굴)"), ("A48", "근사 응력(W절) vs CalculiX 같은 하중"),
                         ("A49", "메시 수렴·특이점 경고"), ("A50", "템플릿 06 컨테이너 하중 순간 → CalculiX(관성 완화)"), ("A51", "OpenCourant ↔ CalculiX 정적 한계"), ("A52", "Gmsh·복셀 메시 자유-자유 고유진동") })
                list.Add(new AdvResult(id, "⑦ FEA", t, "—", AdvStatus.Info, "CalculiX 미설치(또는 MNS_SKIP_CALCULIX=1)", "—"));
            return;
        }
        Add("A47", "⑦ FEA", "CalculiX 해석해 6종 9값(외팔보 처짐·굽힘, Roark 판 응력·처짐, 원통 후프·반경 팽창 부호, Kirsch Kt, 단순보 f₁, 오일러 P_cr)",
            "덱 카드·요소 절점 순서·결과 파서(frd 블록·셸 확장 절점)·압력 부호·고유값/좌굴 출력 해석 오류",
            () =>
            {
                var checks = CcxBenchmarks.RunAll(ccx, dir());
                var bad = checks.Where(c => !c.Pass).ToList();
                return (bad.Count == 0, string.Join(" · ", checks.Select(c => $"{c.Id} {c.Ratio:F3}")), "각 ±3~5%(항목별 허용치)",
                    "C3D20R·S8R·CPS8 구조 격자 — 외부 메셔 없이 재현. 셸 압력 양수 = 요소 법선 방향(원통 내압으로 확인)");
            });
        Add("A48", "⑦ FEA", "근사 응력(W절 Roark 판) vs CalculiX: 닫힌 얇은 상자(0.6×0.3×0.3, Al t 2 mm) 내압 10 kPa", "근사 판 굽힘이 FEA 대비 크게 어긋남(FX-09 재발) 또는 셸 하중 매핑 오류",
            () =>
            {
                var box = new Shape { Kind = ShapeKind.Box, Size = new Vec3(0.6, 0.3, 0.3) };
                var al = MaterialLibrary.Find("al6061-t6")!;
                double mass = al.Density * 0.002 * 2 * (0.18 + 0.18 + 0.09);
                var b = StressBody("box", box, mass, new JsonObject { ["material"] = "al6061-t6", ["thickness"] = 0.002, ["section"] = "thinWall", ["internalPressure_kPa"] = 10 });
                var sm = StressModel.Build(b)!;
                var approx = sm.Evaluate(Still());
                var fm = CcxBodyFea.BuildModel(b, sm.Mesh, dir());
                var lc = new FeaLoadInput("internal 10 kPa", 0, Vec3.Zero, Vec3.Zero, Vec3.Zero, [], sm.Centroid.ToArray(), new double[sm.FaceCount]);
                var (r, res) = CcxBodyFea.Solve(ccx, fm, CcxAnalysis.Static, lc, dir());
                if (!r.Ok || res is null) return (false, r.Error, "해석 성공", "");
                // 옆면(400 쪽 긴 면, a/b = 2) 중앙 비교 — FE 면 최대(모서리 제외 중앙 30%)
                double Center(double[] v) => Enumerable.Range(0, sm.FaceCount)
                    .Where(i => Math.Abs(sm.Normal[i].X) < 0.5 && Math.Abs(sm.Centroid[i].X) < 0.09).Max(i => v[i]);
                double fe = Center(res.FaceVonMises), ap = Center(approx.VonMises);
                double react = res.Reaction?.Norm ?? double.NaN;
                bool ok = ap / fe is >= 0.5 and <= 2 && react < 1;
                return (ok, $"긴 옆면 중앙 σvm: 근사 {ap / 1e6:F1} MPa / CalculiX {fe / 1e6:F1} MPa (비 {ap / fe:F2}), 전체 최대 FE {res.MaxVonMises / 1e6:F1} MPa (모서리), 반력 {react:E1} N",
                    "비 0.5~2 (근사 = 4변 단순지지 판, 실제 상자 모서리는 단순지지와 고정 사이), 반력 < 1 N", "선형(소처짐) 비교 — w/t > 1 이면 둘 다 보수적");
            });
        Add("A49", "⑦ FEA", "메시 수렴(FR-FEA-04): 외팔보 고정단 모서리 = 특이점 경고, 구멍 판 Kt = 수렴", "특이점을 수렴으로 판정해 모서리 무한 응력을 설계값으로 쓰거나, 매끄러운 장을 미수렴으로 오판",
            () =>
            {
                var steel = CcxBenchmarks.Steel; var d = dir(); int k = 0;
                BodyFeModel Build(double hh) => new() { Mesh = FeMesh.Beam(1, 0.1, 0.1, (int)Math.Round(1 / hh), Math.Max(1, (int)Math.Round(0.1 / hh)), Math.Max(1, (int)Math.Round(0.1 / hh))), Material = steel, BaseMaterial = steel, Display = new TriMesh(), Size = hh };
                (CcxResult, BodyFeaResult?) Solve(BodyFeModel fm)
                {
                    var m = new CcxModel { Mesh = fm.Mesh, Material = steel };
                    foreach (var n in fm.Mesh.NodesWhere(p => p.X < 1e-9)) m.Fixed.Add((n, 1, 3));
                    var tip = fm.Mesh.NodesWhere(p => p.X > 1 - 1e-9);
                    foreach (var n in tip) m.NodalForces.Add((n, new Vec3(0, 0, -1000.0 / tip.Count)));
                    var r = CcxRunner.Solve(ccx, m, Path.Combine(d, $"c{++k}"), "fea", TimeSpan.FromMinutes(5));
                    return (r, r.Ok ? CcxBodyFea.Evaluate(fm, r, CcxAnalysis.Static) : null);
                }
                var rep = CcxBodyFea.Converge(Build, Solve, 0.05, 3, 0.5);
                var kts = new[] { 8, 16, 24 }.Select(n => CcxBenchmarks.KirschHole(ccx, Path.Combine(d, $"k{n}"), nr: n, nt: n / 2).Measured).ToList();
                double kch = Math.Abs(kts[2] - kts[1]) / kts[2];
                bool ok = rep.SingularityWarning && !rep.Converged && kch < ConvergenceReport.Tolerance;
                return (ok, $"외팔보 σmax {string.Join(" → ", rep.Levels.Select(l => $"{l.MaxVonMises / 1e6:F2}"))} MPa → {(rep.SingularityWarning ? "특이점 경고" : rep.Converged ? "수렴(오판)" : "미수렴")}; Kirsch Kt {string.Join(" → ", kts.Select(x => x.ToString("F3", Inv)))} (변화 {kch:P2})",
                    "외팔보 = 특이점 경고, Kt 변화 < 5%", "");
            });
        Add("A50", "⑦ FEA", "템플릿 06 컨테이너 c2 하중 순간 → CalculiX 셸(관성 완화 · 3-2-1 지지)", "하중 매핑·관성 완화 부호 오류로 지지 반력이 외력만큼 생기거나(자기평형 깨짐) 결과가 표시 면과 어긋남",
            () =>
            {
                var w = ScenarioDoc.Load(scen("06_munition_3petal_split.json")).Build(); w.Run();
                var b = w.GetBody("c2");
                var sm = StressModel.Build(b)!;
                var inst = LoadCaseExport.Instants(w, b, 12);
                var best = inst.Select(x => CcxBodyFea.FromWorld(w, sm, x.Frame, x.Why)).MaxBy(lc => lc.Severity(sm.Normal, sm.Area))!;
                var fm = CcxBodyFea.BuildModel(b, sm.Mesh, dir());
                var (r, res) = CcxBodyFea.Solve(ccx, fm, CcxAnalysis.Static, best, dir());
                if (!r.Ok || res is null) return (false, r.Error, "해석 성공", "");
                double fr = res.Reaction!.Value.Norm / Math.Max(res.Relief!.ExternalForce.Norm, 1e-9);
                var approx = sm.Evaluate(sm.InputFrom(w, w.Recorder.Frames[w.Recorder.IndexAt(best.Time)]));
                bool ok = fr < 0.02 && res.FaceVonMises.Length == sm.FaceCount && res.MaxVonMises > 0 && double.IsFinite(res.MinSafety);
                return (ok, $"{best.Name}: 외력 {res.Relief.ExternalForce.Norm:F0} N, FE 가속 {res.Relief.AccelG:F2} G (기록 {best.RecordedAccel.Norm / 9.80665:F2} G), 반력/외력 {fr:E1}, σvm 최대 {res.MaxVonMises / 1e6:F1} MPa (근사 {approx.MaxVonMises / 1e6:F1}), SF {res.MinSafety:F1}",
                    "반력/외력 < 2%, 면 수 일치, 유한", string.Join(" · ", fm.Notes));
            });
        if (oc is null)
            list.Add(new AdvResult("A51", "⑦ FEA", "OpenCourant ↔ CalculiX 정적 한계", "—", AdvStatus.Info, "OpenCourant 미설치(또는 MNS_SKIP_OPENCOURANT=1)", "—"));
        else
            Add("A51", "⑦ FEA", "OpenCourant(양해법, 10 T₁ 램프) ↔ CalculiX(정적) 외팔보 끝 처짐", "두 솔버의 단위·하중·경계조건 해석이 서로 다름(같은 모델에 다른 답)",
                () =>
                {
                    var c = CcxBenchmarks.StaticLimitCrossCheck(ccx, oc, dir());
                    return (c.Pass, $"{c.Measured:F4} vs {c.Reference:F4} mm (비 {c.Ratio:F4}) — {c.Note}", "±5%", "");
                });
        Add("A52", "⑦ FEA", "자유-자유 고유진동: Gmsh C3D10 vs 복셀 C3D20 vs 보 이론(□40 mm × 400 mm Al)", "강체 모드 처리·메시 대체 경로 오류(ccx 구속 없는 고유값의 가짜 모드)",
            () =>
            {
                var al = MaterialLibrary.Find("al6061-t6")!;
                var shape = new Shape { Kind = ShapeKind.Box, Size = new Vec3(0.4, 0.04, 0.04) };
                var b = StressBody("bar", shape, al.Density * 0.4 * 0.04 * 0.04, new JsonObject { ["material"] = "al6061-t6", ["section"] = "solid" });
                var disp = StressModel.DisplayMesh(shape);
                double fRef = 22.373 / (2 * Math.PI * 0.16) * Math.Sqrt(al.E * Math.Pow(0.04, 4) / 12 / (al.Density * 0.0016));
                var meas = new List<string>(); bool ok = true;
                foreach (var mesher in new[] { "gmsh", "voxel" })
                {
                    BodyFeModel fm;
                    try { fm = CcxBodyFea.BuildModel(b, disp, dir(), 0.01, mesher); }
                    catch (InvalidOperationException ex) when (mesher == "gmsh") { meas.Add($"gmsh 없음({Trim(ex.Message, 40)})"); continue; }
                    var (r, _) = CcxBodyFea.Solve(ccx, fm, CcxAnalysis.Modal, null, dir());
                    if (!r.Ok) { ok = false; meas.Add($"{mesher}: {r.Error}"); continue; }
                    double rigid = CcxBodyFea.RigidHz(r.Dat.FrequenciesHz);
                    int nr = r.Dat.FrequenciesHz.Count(f => f <= rigid);
                    double f1 = r.Dat.FrequenciesHz.First(f => f > rigid);
                    ok &= nr == 6 && f1 / fRef is >= 0.93 and <= 1.0;
                    meas.Add($"{mesher}: 강체 {nr}개, f₁ {f1:F0} Hz (비 {f1 / fRef:F3})");
                }
                return (ok, string.Join(" · ", meas) + $" · EB {fRef:F0} Hz", "강체 모드 6개, f₁/f_EB 0.93~1.0 (L/h 10 전단 효과 ≈ −3.5%)", "구속 없는 *FREQUENCY 는 실행마다 가짜 저주파 모드 → 약한 SPRING1 접지로 해결(실측)");
            });
    }

    // ================= 응력 색·레전드(PRD W, FR-STR-01~06 · FX-09) =================

    private static Body StressBody(string name, Shape shape, double mass, JsonObject? structure)
    {
        var b = new Body { Name = name, OwnMass = mass, Shape = shape, OwnInertia = shape.UniformInertia(mass) };
        if (structure is not null) b.Structure = StructureDef.Parse(structure, null, shape, new List<string>(), name);
        return b;
    }

    private static StressInput Still(params PointLoad[] loads) => new(Vec3.Zero, 1.225, 0, Vec3.Zero, loads);

    private static void StressColorScenarios(List<AdvResult> list, Action<string, string, string, string, Func<(bool Ok, string Measured, string Criterion, string Note)>> Add,
        Func<string, string> scen, string smokeLog)
    {
        JsonObject Thin(string mat, double t, double pinKPa = 0, string edge = "simple") => new()
        {
            ["material"] = mat, ["thickness"] = t, ["section"] = "thinWall", ["internalPressure_kPa"] = pinKPa, ["edge"] = edge,
        };

        Add("A31", "⑦ 압력", "구 표면 압력: 정체점 q·Cp₀ · 면별 Cp 공식 · 적분 항력 Cd", "입사각·법선 부호·면적 적분 오류로 압력 색이 틀린 곳에 칠해짐",
            () =>
            {
                var sph = new Shape { Kind = ShapeKind.Sphere, Radius = 0.1 };
                var m = StressModel.Build(StressBody("s", sph, 1, null))!;
                double v = 50, rho = 1.225, q = 0.5 * rho * v * v;
                var r = m.Evaluate(new StressInput(new Vec3(v, 0, 0), rho, 0, Vec3.Zero, []));
                double formula = 0;
                for (int i = 0; i < m.FaceCount; i++)
                    formula = Math.Max(formula, Math.Abs(r.Pressure[i] - q * StressModel.PressureCoefficient(m.Normal[i].X, 0)) / q);
                // 해석 적분: 앞 반구 2∫cos³θ sinθ dθ = 0.5, 뒤(기저압 연결) 2·(0.140625 + 0.00625) = 0.29375
                double cd = r.TotalAeroForce.X * -1 / (q * Math.PI * 0.01), cdExact = 0.79375;
                var r5 = m.Evaluate(new StressInput(new Vec3(170, 0, 0), rho, 0.5, Vec3.Zero, []));
                double stag5 = r5.MaxPressure / (0.5 * rho * 170 * 170) / StressModel.StagnationCp(0.5);
                bool ok = formula < 1e-12 && r.MaxPressure / q is >= 0.97 and <= 1 + 1e-12 && Math.Abs(cd / cdExact - 1) < 0.03 && stag5 is >= 0.97 and <= 1 + 1e-12;
                return (ok, $"면별 |Cp−공식| {formula:E1}, 최대 p/q {r.MaxPressure / q:F4}, Cd {cd:F4} vs 해석 {cdExact:F5} ({(cd / cdExact - 1) * 100:+0.00;-0.00}%), M0.5 정체 {stag5:F4}",
                    "< 1e-12, 0.97~1, ±3%, 0.97~1 (×Cp₀(M))", "다면체(16×32) 근사 — 정체점 면 중심이 정확히 축 위가 아님");
            });
        Add("A32", "⑦ 압력", "압력 스케일링(속도 2배 → 4배, 밀도 ½ → ½)·정지 상태 0", "동압 q 계산·누적 상태(이전 프레임 값 재사용) 오류",
            () =>
            {
                var box = new Shape { Kind = ShapeKind.Box, Size = new Vec3(0.4, 0.25, 0.2) };
                var m = StressModel.Build(StressBody("b", box, 5, Thin("al6061-t6", 0.002)))!;
                var res = new StressResult();
                var dir = new Vec3(30, 7, -4);
                var p1 = m.Evaluate(new StressInput(dir, 1.2, 0, Vec3.Zero, []), res).Pressure.ToArray();
                var p2 = m.Evaluate(new StressInput(dir * 2, 1.2, 0, Vec3.Zero, []), res).Pressure.ToArray();
                var p3 = m.Evaluate(new StressInput(dir, 0.6, 0, Vec3.Zero, []), res).Pressure.ToArray();
                double e2 = p1.Select((p, i) => Math.Abs(p2[i] - 4 * p)).Max() / p1.Max(Math.Abs);
                double e3 = p1.Select((p, i) => Math.Abs(p3[i] - 0.5 * p)).Max() / p1.Max(Math.Abs);
                var r0 = m.Evaluate(Still(), res); // 같은 결과 객체 재사용 → 이전 값이 남으면 실패
                bool zero = r0.Pressure.All(p => p == 0) && r0.VonMises.All(x => x == 0) && r0.Safety.All(double.IsPositiveInfinity) && r0.State.All(s => s == FailureState.Safe);
                var range = ColorScale.Resolve(r0.VonMises, zeroBased: true);
                var ticks = ColorScale.Ticks(range);
                bool ok = e2 < 1e-12 && e3 < 1e-12 && zero && ticks.Select(t => t.Label).Distinct().Count() == ticks.Length;
                return (ok, $"4배 오차 {e2:E1}, ½ 오차 {e3:E1}, 정지: 압력·응력 0={zero}, 레전드 범위 [{range.Min}, {range.Max}] 눈금 {string.Join("/", ticks.Select(t => t.Label))}",
                    "< 1e-12, 정지 시 0·SF ∞·안전, 전부 0 이어도 레전드 눈금 서로 다름", "");
            });
        Add("A33", "⑦ 응력", "FX-09 균일 압력 직사각판 = Roark(단순지지·고정, a/b 1·2·5)", "얇은 판 국부 굽힘 누락(FX-09) 또는 판 계수·분포·가장자리 조건 오류",
            () =>
            {
                double p = 10e3, t = 0.002, b0 = 0.3;
                var bad = new List<string>(); var meas = new List<string>();
                foreach (var edge in new[] { "simple", "clamped" })
                    foreach (var ratio in new[] { 1.0, 2.0, 5.0 })
                    {
                        var shape = new Shape { Kind = ShapeKind.Box, Size = new Vec3(b0 * ratio, b0, b0) };
                        var m = StressModel.Build(StressBody("p", shape, 2, Thin("al6061-t6", t, p / 1e3, edge)))!;
                        var r = m.Evaluate(Still());
                        var (beta, _, _) = StressModel.PlateCoefficients(ratio, edge == "clamped");
                        double hand = beta * p * b0 * b0 / (t * t);
                        // 긴 옆면(법선 ±y, ±z) 만 — 끝면은 정사각
                        double mx = Enumerable.Range(0, m.FaceCount).Where(i => Math.Abs(m.Normal[i].X) < 0.5).Max(i => r.VonMises[i]);
                        double beamMax = r.BendingMoment.Max();
                        meas.Add($"{edge[0]}{ratio:0}: {mx / hand:F4}");
                        if (mx / hand is < 0.98 or > 1 + 1e-9 || beamMax > 1e-6 || r.DominantAtMax != StressComponent.Panel) bad.Add($"{edge} a/b={ratio}: 비 {mx / hand:F4}, M {beamMax:E1}, {r.DominantAtMax}");
                    }
                var (bs, _, _) = StressModel.PlateCoefficients(1.49, false);
                return (bad.Count == 0, bad.Count == 0 ? $"σ/σ_Roark {string.Join(", ", meas)} · 보 굽힘 0 · 지배 패널 (β(1.49)={bs:F4})" : string.Join("; ", bad),
                    "0.98 ≤ 비 ≤ 1 (격자 면 중심이 판 중앙·가장자리에서 조금 떨어짐), 지배 = 패널", "Roark Table 11.4 case 1/8 (ν 0.3)");
            });
        Add("A34", "⑦ 응력", "원통 내압 후프 p·r/t · 축 p·r/2t · 구 p·r/2t (막응력 해석해)", "외피 막응력 부호(바깥 압력 → 압축)·2축 von Mises 합성 오류",
            () =>
            {
                double p = 100e3, t = 0.001, rad = 0.1;
                var cyl = new Shape { Kind = ShapeKind.Cylinder, Radius = rad, Length = 0.5, Axis = 0 };
                var mc = StressModel.Build(StressBody("c", cyl, 1, Thin("al6061-t6", t, p / 1e3)))!;
                var rc = mc.Evaluate(Still());
                var side = Enumerable.Range(0, mc.FaceCount).Where(i => Math.Abs(mc.Normal[i].X) < 0.7).ToList();
                double hoop = p * rad / t, ax = hoop / 2, vm = Math.Sqrt(hoop * hoop - hoop * ax + ax * ax);
                double eh = side.Max(i => Math.Abs(rc.Local[i] - hoop)) / hoop, ea = side.Max(i => Math.Abs(rc.Axial[i] - ax)) / ax, ev = side.Max(i => Math.Abs(rc.VonMises[i] - vm)) / vm;
                var sph = new Shape { Kind = ShapeKind.Sphere, Radius = rad };
                var ms = StressModel.Build(StressBody("s", sph, 1, Thin("al6061-t6", t, p / 1e3)))!;
                var rs = ms.Evaluate(Still());
                double es = rs.VonMises.Max(x => Math.Abs(x - p * rad / (2 * t))) / (p * rad / (2 * t));
                // 바깥 압력(내압 음수) → 후프 압축
                var mo = StressModel.Build(StressBody("o", cyl, 1, Thin("al6061-t6", t, -p / 1e3)))!;
                bool comp = side.All(i => mo.Evaluate(Still()).Local[i] < 0);
                bool ok = eh < 1e-9 && ea < 1e-9 && ev < 1e-9 && es < 1e-9 && comp;
                return (ok, $"후프 오차 {eh:E1}, 축 {ea:E1}, vm {ev:E1} (= {vm / 1e6:F3} MPa), 구 {es:E1}, 외압 → 압축={comp}", "< 1e-9, 외압 시 후프 < 0", "");
            });
        Add("A35", "⑦ 응력", "FX-09 재현: 템플릿 06 컨테이너 c2(Al 6061, t 2 mm, □270) SF 손계산 범위", "패널 굽힘 누락으로 SF 712(손계산 ~70~120 MPa)처럼 응력 크기가 100배 작게 표시",
            () =>
            {
                var doc = ScenarioDoc.Load(scen("06_munition_3petal_split.json"));
                doc.Root["sim"]!["tEnd"] = 2.0;
                var w = doc.Build(); w.Run();
                var h = StructuralAnalysis.Analyze(w, [w.GetBody("c2")], 400).Bodies.Single();
                // 최대점이 있는 면의 판 비: 끝면(x = ±0.2, 정면으로 바람 받는 270×270) a/b = 1, 옆면 400×270 a/b = 1.49
                bool endFace = Math.Abs(Math.Abs(h.PeakVmPoint.X) - 0.2) < 0.005;
                double aOverB = endFace ? 1.0 : 0.40 / 0.27;
                var (beta, _, _) = StressModel.PlateCoefficients(aOverB, false);
                double hand = beta * h.MaxPressure * Math.Pow(0.27 / 0.002, 2);
                double ratio = h.PeakVm / hand;
                bool ok = ratio is >= 0.5 and <= 2 && h.MinSafetyValue is >= 1 and <= 20 && h.DominantAtPeak == StressComponent.Panel;
                return (ok, $"σvm 최대 {h.PeakVm / 1e6:F1} MPa @ {h.PeakVmTime:F2}s ({(endFace ? "끝면" : "옆면")}) vs 손계산 β(a/b {aOverB:F2})·p·(b/t)² = {beta:F3}·{h.MaxPressure / 1e3:F1} kPa·(270/2)² = {hand / 1e6:F1} MPa (비 {ratio:F2}), 최소 SF {h.MinSafetyValue:F2}, 지배 {h.DominantAtPeak}, 판 w/t {h.MaxPanelDeflectionRatio:F1}",
                    "비 0.5~2, SF 1~20 (수정 전 712), 지배 = 패널", "손계산은 정체압 전체를 판에 건 값 — 모델은 관성 압력(감속 시 벽·내용물 관성)이 반대로 작용해 조금 작다. 선형 판 이론: w/t > 0.5 이면 막 작용으로 실제 응력은 더 작음(보수적)");
            });
        Add("A36", "③ 대칭", "응력 장 대칭: 흐름 거울(±y)·축 둘레 90° 회전(y↔z, 정사각 단면)", "면 좌표·법선 부호·단면 축 선택 오류로 같은 하중에 다른 색 분포",
            () =>
            {
                var box = new Shape { Kind = ShapeKind.Box, Size = new Vec3(0.4, 0.25, 0.25) };
                var m = StressModel.Build(StressBody("b", box, 4, Thin("al6061-t6", 0.0015)))!;
                double[] Sorted(Vec3 v, PointLoad[] pl) { var r = m.Evaluate(new StressInput(v, 1.2, 0.1, Vec3.Zero, pl)); return r.VonMises.OrderBy(x => x).ToArray(); }
                double Diff(double[] a, double[] b) => a.Select((x, i) => Math.Abs(x - b[i])).Max() / Math.Max(a.Max(), 1e-30);
                var s1 = Sorted(new Vec3(40, 12, 0), [new PointLoad(new Vec3(0.2, 0, 0), new Vec3(0, 300, 0), "t")]);
                var s2 = Sorted(new Vec3(40, -12, 0), [new PointLoad(new Vec3(0.2, 0, 0), new Vec3(0, -300, 0), "t")]);
                var s3 = Sorted(new Vec3(40, 0, 12), [new PointLoad(new Vec3(0.2, 0, 0), new Vec3(0, 0, 300), "t")]);
                double d12 = Diff(s1, s2), d13 = Diff(s1, s3);
                return (d12 < 1e-9 && d13 < 1e-9, $"거울 차 {d12:E1}, 90° 회전 차 {d13:E1} (정렬한 면 응력 분포, 최대 {s1[^1] / 1e6:F2} MPa)", "< 1e-9", "");
            });
        Add("A37", "④ 보존", "선형성(하중 2배 → 응력 2배)·자유단 평형 잔차(국부 항 포함)", "국부 항·관성 압력이 비선형으로 섞이거나 평형이 깨짐",
            () =>
            {
                var box = new Shape { Kind = ShapeKind.Box, Size = new Vec3(0.5, 0.2, 0.3) };
                var m = StressModel.Build(StressBody("b", box, 6, Thin("al6061-t6", 0.002)))!;
                PointLoad[] L(double k) => [new PointLoad(new Vec3(0.25, 0.05, 0), new Vec3(0, 200 * k, -500 * k), "r"), new PointLoad(new Vec3(-0.25, 0, 0.1), new Vec3(80 * k, 0, 0), "h")];
                var r1 = m.Evaluate(new StressInput(new Vec3(35, 5, -9), 1.0, 0, Vec3.Zero, L(1)));
                var v1 = r1.VonMises.ToArray(); var l1 = r1.Local.ToArray();
                var r2 = m.Evaluate(new StressInput(new Vec3(35, 5, -9), 2.0, 0, Vec3.Zero, L(2)));
                double e = v1.Select((x, i) => Math.Abs(r2.VonMises[i] - 2 * x)).Max() / v1.Max();
                double el = l1.Select((x, i) => Math.Abs(r2.Local[i] - 2 * x)).Max() / Math.Max(l1.Max(), 1e-30);
                double res = r2.ResidualMoment / Math.Max(r2.BendingMoment.Max(), 1e-30);
                return (e < 1e-9 && el < 1e-9 && res < 1e-9, $"응력 2배 오차 {e:E1}, 국부 항 {el:E1}, 평형 잔차/최대 모멘트 {res:E1}", "< 1e-9", "");
            });
        Add("A38", "⑦ 응력", "연결점 핫스팟: 최대 응력 위치 = 하중 점(라이저·힌지), 방향 반전 불변", "핫스팟이 엉뚱한 면에 칠해지거나(좌표계 혼동) 방향에 따라 크기가 바뀜",
            () =>
            {
                var box = new Shape { Kind = ShapeKind.Box, Size = new Vec3(0.40, 0.27, 0.27) };
                // 4 mm 벽 + 반경 5 mm 러그: 관성 완화 패널 굽힘(55 G)보다 국부 하중이 큰 조건
                var def = Thin("al6061-t6", 0.004); def["attachRadius"] = 0.005;
                var m = StressModel.Build(StressBody("c", box, 5.6, def))!;
                var want = new Vec3(0.2, 0.08, -0.06); // 뒷면(+x) 모서리 쪽 라이저 고정점
                var pt = m.Centroid[Enumerable.Range(0, m.FaceCount).MinBy(i => (m.Centroid[i] - want).NormSq)];
                var r = m.Evaluate(Still(new PointLoad(pt, new Vec3(3000, 0, 0), "riser")));
                var at = m.Centroid[r.MaxFace];
                double peak = r.MaxVonMises;
                var r2 = m.Evaluate(Still(new PointLoad(pt, new Vec3(-3000, 0, 0), "riser")));
                double d = (at - pt).Norm, expect = 3000 / (Math.PI * 0.005 * 0.005);
                bool ok = d < 0.03 && r.DominantAtMax == StressComponent.Hotspot && Math.Abs(r2.MaxVonMises - peak) < 1e-9 * peak && peak <= expect * (1 + 1e-12) && peak > 0.9 * expect && r.Hotspots.Count == 1;
                return (ok, $"최대점–하중점 {d * 1000:F1} mm, 지배 {r.DominantAtMax}, σ_h {peak / 1e6:F2} MPa (F/πr² = {expect / 1e6:F2}), 반전 차 {Math.Abs(r2.MaxVonMises - peak):E1}",
                    "< 30 mm, 핫스팟, 0.9~1 × F/πr², 반전 동일", "");
            });
        Add("A39", "⑦ 응력", "최대값 유지(peak-hold) ≥ 모든 프레임 순간값 (템플릿 06 c1·petal1)", "peak-hold 누적 누락·프레임 추출에서 최대 하중 순간 빠짐",
            () =>
            {
                var doc = ScenarioDoc.Load(scen("06_munition_3petal_split.json"));
                doc.Root["sim"]!["tEnd"] = 1.5;
                var w = doc.Build(); w.Run();
                var bad = new List<string>(); int checkedFrames = 0;
                foreach (var name in new[] { "c1", "petal1" })
                {
                    var b = w.GetBody(name);
                    var h = StructuralAnalysis.Analyze(w, [b], 200).Bodies.Single();
                    var frames = StructuralAnalysis.SampleFrames(w, b, 200);
                    var res = new StressResult();
                    foreach (int fi in frames)
                    {
                        var r = h.Model.Evaluate(h.Model.InputFrom(w, w.Recorder.Frames[fi]), res);
                        checkedFrames++;
                        for (int k = 0; k < r.VonMises.Length; k++)
                            if (r.VonMises[k] > h.PeakVonMises[k] * (1 + 1e-12) + 1e-9 || r.Pressure[k] > h.PeakPressure[k] + 1e-9) { bad.Add($"{name} f{fi} 면 {k}"); break; }
                    }
                    if (h.PeakVm < h.PeakVonMises.Max() * (1 - 1e-12)) bad.Add($"{name}: 요약 최대 < 면 최대");
                }
                return (bad.Count == 0, bad.Count == 0 ? $"{checkedFrames} 프레임 × 면 전부 순간값 ≤ 최대값 유지" : string.Join("; ", bad.Take(5)), "위반 0", "");
            });
        Add("A40", "⑪ 표시", "레전드 눈금: ≥ 5개, 위=최대·아래=최소, 단조, 라벨 서로 다름(극소·극대·음수·같은 값·NaN만)", "눈금 라벨이 모두 '0' 이나 같은 숫자로 보이거나 순서가 뒤집힘",
            () =>
            {
                var cases = new (string Name, double[] Values, bool Zero)[]
                {
                    ("극소 1e-9", [0, 1e-9], true), ("음수 포함", [-3.2, 7.7], false), ("극대 1.2e9", [0, 1.2e9], true), ("좁은 276±", [276, 276.0004], false),
                    ("전부 0", [0, 0, 0], true), ("값 하나 5", [5], false), ("NaN 만", [double.NaN, double.NaN], false), ("±∞ 섞임", [double.NegativeInfinity, 2, 3, double.PositiveInfinity], false),
                };
                var bad = new List<string>(); var meas = new List<string>();
                foreach (var (n, vals, z) in cases)
                {
                    var r = ColorScale.Resolve(vals, zeroBased: z);
                    var tk = ColorScale.Ticks(r);
                    bool mono = tk.Zip(tk.Skip(1)).All(p => p.First.Value > p.Second.Value);
                    bool ends = tk[0].Value == r.Max && tk[^1].Value == r.Min;
                    bool distinct = tk.Select(x => x.Label).Distinct().Count() == tk.Length;
                    bool finite = double.IsFinite(r.Min) && double.IsFinite(r.Max) && r.Max > r.Min;
                    bool parse = tk.All(x => double.TryParse(x.Label, NumberStyles.Float, Inv, out var pv) && Math.Abs(pv - x.Value) <= 0.02 * r.Span);
                    if (!(tk.Length >= 5 && mono && ends && distinct && finite && parse)) bad.Add($"{n}: n={tk.Length} mono={mono} ends={ends} distinct={distinct} finite={finite} parse={parse} [{string.Join("|", tk.Select(x => x.Label))}]");
                    else if (n is "전부 0" or "좁은 276±" or "±∞ 섞임") meas.Add($"{n} [{string.Join("|", tk.Select(x => x.Label))}]");
                }
                return (bad.Count == 0, bad.Count == 0 ? $"{cases.Length}종 통과 · " + string.Join(" · ", meas) : string.Join("; ", bad), "모든 경우 ≥5·단조·끝값 일치·라벨 다름·라벨 = 값(±2% 범위)", "");
            });
        Add("A41", "⑪ 표시", "레전드 막대 색 = 같은 값의 면 색 (Turbo·Viridis × 정/역방향, 범위 밖 자름, NaN 회색)", "레전드와 3D 면 색이 어긋나 사용자가 응력 크기를 잘못 읽음(안전계수 역방향에서 특히)",
            () =>
            {
                var rng = new Random(7);
                var range = new ScaleRange(-2.5, 40, false);
                double worst = 0; int n = 0;
                foreach (var map in ColorScale.Names)
                    foreach (var rev in new[] { false, true })
                        for (int i = 0; i < 400; i++)
                        {
                            double v = range.Min - 10 + rng.NextDouble() * (range.Span + 20);
                            var fc = ColorScale.ValueColor(map, v, range, rev);
                            double fromTop = (range.Max - Math.Clamp(v, range.Min, range.Max)) / range.Span;
                            var lc = ColorScale.LegendColor(map, range, fromTop, rev);
                            worst = Math.Max(worst, Math.Max(Math.Abs(fc.R - lc.R), Math.Max(Math.Abs(fc.G - lc.G), Math.Abs(fc.B - lc.B)))); n++;
                        }
                // 안전계수(역방향): 낮은 SF = Turbo 끝(빨강), 높은 SF = 시작(파랑)
                var low = ColorScale.ValueColor("Turbo", 0.2, new ScaleRange(0, 5, false), true);
                var high = ColorScale.ValueColor("Turbo", 5, new ScaleRange(0, 5, false), true);
                var nd = ColorScale.ValueColor("Turbo", double.NaN, range);
                double ndDist = double.MaxValue;
                foreach (var map in ColorScale.Names)
                    for (int i = 0; i <= 200; i++) { var c = ColorScale.Map(map, i / 200.0); ndDist = Math.Min(ndDist, Math.Abs(c.R - nd.R) + Math.Abs(c.G - nd.G) + Math.Abs(c.B - nd.B)); }
                bool ok = worst < 1e-6 && low.R > low.B && high.B > high.R && ndDist > 0.05;
                return (ok, $"{n}점 최대 색 차 {worst:E1}, SF 0.2 RGB({low.R:F2},{low.G:F2},{low.B:F2}) / SF 5 ({high.R:F2},{high.G:F2},{high.B:F2}), NaN 회색–컬러맵 최소 거리 {ndDist:F3}",
                    "< 1e-6, 낮은 SF 빨강 쪽·높은 SF 파랑 쪽, NaN 은 컬러맵에 없는 색(> 0.05)", "수정 전: NaN(재질 없는 안전계수) 면이 최소/최대 끝색으로 칠해져 '안전'으로 보일 수 있었음");
            });
        Add("A42", "⑪ 표시", "컬러맵 성질: Viridis 밝기 단조 증가 · Turbo 파랑→빨강 · 범위 밖·모르는 이름·NaN", "컬러맵 표 보간·다항식 오류로 큰 값이 더 어둡게/다른 색으로 보임",
            () =>
            {
                double Y((float R, float G, float B) c) => 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;
                bool mono = true; double prev = -1;
                for (int i = 0; i <= 400; i++) { double y = Y(ColorScale.Viridis(i / 400.0)); if (y <= prev) mono = false; prev = y; }
                var t0 = ColorScale.Turbo(0); var t1 = ColorScale.Turbo(1); var tm = ColorScale.Turbo(0.5);
                bool turbo = t0.B > t0.R && t1.R > t1.B && tm.G > 0.8;
                bool clamp = ColorScale.Map("Turbo", -3) == ColorScale.Map("Turbo", 0) && ColorScale.Map("Viridis", 7) == ColorScale.Map("Viridis", 1);
                bool unknown = ColorScale.Map("Jet?", 0.3) == ColorScale.Turbo(0.3) && ColorScale.Map(null, 0.3) == ColorScale.Turbo(0.3);
                bool nan = ColorScale.Map("Viridis", double.NaN) == ColorScale.NoData;
                bool inRange = Enumerable.Range(0, 101).All(i => { var c = ColorScale.Turbo(i / 100.0); return c.R is >= 0 and <= 1 && c.G is >= 0 and <= 1 && c.B is >= 0 and <= 1; });
                // 3D 뷰 배경(GlView ClearColor 0.07, 0.09, 0.12)과 구분: 컬러맵 어느 색도 배경과 L1 거리 ≥ 0.2
                double bgDist = double.MaxValue;
                foreach (var map in ColorScale.Names)
                    for (int i = 0; i <= 200; i++) { var c = ColorScale.Map(map, i / 200.0); bgDist = Math.Min(bgDist, Math.Abs(c.R - 0.07) + Math.Abs(c.G - 0.09) + Math.Abs(c.B - 0.12)); }
                bool ok = mono && turbo && clamp && unknown && nan && inRange && bgDist >= 0.2;
                return (ok, $"Viridis 단조={mono}, Turbo 0/0.5/1 = ({t0.R:F2},{t0.G:F2},{t0.B:F2})/({tm.R:F2},{tm.G:F2},{tm.B:F2})/({t1.R:F2},{t1.G:F2},{t1.B:F2}), 자름={clamp}, 모르는 이름→Turbo={unknown}, NaN→회색={nan}, [0,1]={inRange}, 배경과 최소 거리 {bgDist:F3}",
                    "모두 참, 배경 거리 ≥ 0.2", "수정 전 Turbo 6차 다항 근사는 t=0 이 (0.14, 0.09, 0.11) — 배경과 거리 0.08 로 낮은 응력 면이 배경에 묻힘 → 공식 표(33 점) 보간으로 교체");
            });
        Add("A43", "⑤ 오류 입력", "고정 범위 역전·비숫자·같은 값 + structure.edge/internalPressure/thickness 잘못된 값", "UI 입력 실수로 레전드가 거꾸로·NaN 이 되거나 파서가 NullReference 로 죽음",
            () =>
            {
                var bad = new List<string>();
                var vals = new[] { 1.0, 3, 9 };
                var a = ColorScale.Resolve(vals, fixedMin: 10, fixedMax: 2);
                if (!(a.Min == 2 && a.Max == 10 && a.Warning is not null)) bad.Add($"역전 → [{a.Min},{a.Max}]");
                var b = ColorScale.Resolve(vals, fixedMin: double.NaN, fixedMax: 5);
                if (!(b.Min == 1 && b.Max == 5 && b.Warning is not null)) bad.Add($"비숫자 → [{b.Min},{b.Max}]");
                var c = ColorScale.Resolve(vals, fixedMin: 4, fixedMax: 4);
                if (!(c.Max > c.Min && c.Warning is not null)) bad.Add($"같은 값 → [{c.Min},{c.Max}]");
                var d = ColorScale.Resolve(vals, fixedMin: double.PositiveInfinity, fixedMax: double.NegativeInfinity);
                if (!(d.Min == 1 && d.Max == 9)) bad.Add($"±∞ → [{d.Min},{d.Max}]");
                var box = new Shape { Kind = ShapeKind.Box, Size = new Vec3(0.3, 0.2, 0.2) };
                foreach (var (n, j) in new (string, string)[]
                {
                    ("edge", """{"material":"al6061-t6","thickness":0.002,"edge":"hinged"}"""),
                    ("내압 문자열", """{"material":"al6061-t6","thickness":0.002,"internalPressure_kPa":"high"}"""),
                    ("두께 음수", """{"material":"al6061-t6","thickness":-0.001}"""),
                    ("thinWall 두께 없음", """{"material":"al6061-t6","section":"thinWall"}"""),
                })
                {
                    try { StructureDef.Parse(JsonNode.Parse(j)!.AsObject(), null, box, new List<string>(), "x"); bad.Add($"{n}: 오류 없음"); }
                    catch (InvalidDataException ex) when (ex.Message.Length > 0) { }
                    catch (Exception ex) { bad.Add($"{n}: {ex.GetType().Name}"); }
                }
                return (bad.Count == 0, bad.Count == 0 ? "범위 4종 교정 + 경고, structure 4종 명확한 오류(InvalidDataException)" : string.Join("; ", bad), "크래시 없음·교정 + 경고 메시지", "");
            });
        Add("A44", "② 수치", "극한 하중에서 응력·색 유한: M 9 · ω 1000 rad/s · t 10 μm · 밀도 1e-6", "오버플로·0 나눗셈으로 NaN 색 또는 레전드 범위 붕괴",
            () =>
            {
                var bad = new List<string>();
                var shapes = new[]
                {
                    new Shape { Kind = ShapeKind.Box, Size = new Vec3(0.4, 0.2, 0.2) }, new Shape { Kind = ShapeKind.Cylinder, Radius = 0.1, Length = 0.6, Axis = 0 },
                    new Shape { Kind = ShapeKind.Sphere, Radius = 0.1 }, Shape.ShellSegmentOf(0.2, 0.003, 1.7, 120, 0, 0, Vec3.Zero),
                };
                foreach (var s in shapes)
                    foreach (var (v, rho, mach, wz, t) in new[] { (3000.0, 1.2, 9.0, 1000.0, 1e-5), (1e-12, 1e-6, 0.0, 0.0, 0.002), (300.0, 1e-6, 0.9, 0.0, 0.5) })
                    {
                        var m = StressModel.Build(StressBody("x", s, 3, Thin("ss304", t)))!;
                        var r = m.Evaluate(new StressInput(new Vec3(v, v / 3, 0), rho, mach, new Vec3(0, 0, wz), []));
                        bool fin = r.VonMises.All(double.IsFinite) && r.Pressure.All(double.IsFinite) && r.Local.All(double.IsFinite) && double.IsFinite(r.MaxPanelDeflectionRatio)
                                   && r.Safety.All(x => !double.IsNaN(x));
                        var range = ColorScale.Resolve(r.VonMises, zeroBased: true);
                        var tk = ColorScale.Ticks(range);
                        if (!fin || !(range.Max > range.Min) || tk.Select(x => x.Label).Distinct().Count() != tk.Length) bad.Add($"{s.Kind} v={v} t={t}: fin={fin} [{range.Min},{range.Max}]");
                    }
                return (bad.Count == 0, bad.Count == 0 ? "4 형상 × 3 극한 조건 모두 유한·레전드 정상" : string.Join("; ", bad.Take(4)), "NaN/∞ 없음, 레전드 범위 > 0, 라벨 다름", "");
            });
        // ⑪ UI 스모크: 레전드가 3D 뷰 오른쪽에 그려지는지(앱 실행 로그가 있을 때)
        {
            string? line = File.Exists(smokeLog) ? File.ReadAllLines(smokeLog).FirstOrDefault(l => l.StartsWith("ui.legendPos ")) : null;
            if (line is null)
                list.Add(new AdvResult("A45", "⑪ UI 스모크", "레전드 위치·눈금(3D 뷰 오른쪽 세로 막대)", "레전드가 뷰 밖·왼쪽에 그려지거나 눈금이 없음", AdvStatus.Info,
                    "스모크 로그에 ui.legendPos 없음(앱 실행 필요)", "—", "MnS.Simulator --smoke tests/Sim.Core.Tests/scenarios/06_munition_3petal_split.json --structural --color stress --all --at 0.7"));
            else
            {
                double G(string k) => double.Parse(System.Text.RegularExpressions.Regex.Match(line, $@"\b{k}=(-?[\d.]+)").Groups[1].Value, Inv);
                var ticks = System.Text.RegularExpressions.Regex.Match(line, "ticks='([^']*)'").Groups[1].Value.Split('|');
                double l = G("left"), r = G("right"), top = G("top"), bot = G("bottom"), vw = G("viewW"), vh = G("viewH");
                bool ok = l > vw / 2 && r <= vw + 0.5 && top >= 0 && bot <= vh + 0.5 && ticks.Length >= 5 && ticks.Distinct().Count() == ticks.Length;
                list.Add(new AdvResult("A45", "⑪ UI 스모크", "레전드 위치·눈금(3D 뷰 오른쪽 세로 막대)", "레전드가 뷰 밖·왼쪽에 그려지거나 눈금이 없음", ok ? AdvStatus.Pass : AdvStatus.Fail,
                    $"레전드 x {l:F0}~{r:F0} / 뷰 폭 {vw:F0}, y {top:F0}~{bot:F0} / {vh:F0}, 눈금 {string.Join("|", ticks)}", "뷰 오른쪽 절반 안, 눈금 ≥ 5 서로 다름",
                    $"로그: {smokeLog} ({File.GetLastWriteTime(smokeLog):yyyy-MM-dd HH:mm})"));
            }
        }
    }

    private static string Trim(string s, int n = 160) => s.Length <= n ? s : s[..n] + "…";

    public static string Report(IReadOnlyList<AdvResult> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# 적대적 시나리오 자체 검증 (PRD Y · VV-60~62)");
        sb.AppendLine();
        sb.AppendLine($"생성: `mns adversarial` · {DateTime.Now:yyyy-MM-dd HH:mm} · 저장소 테스트 `AdversarialTests` 가 같은 항목을 판정(Pass 아니면 실패).");
        int pass = results.Count(r => r.Status == AdvStatus.Pass), fail = results.Count(r => r.Status == AdvStatus.Fail);
        sb.AppendLine();
        sb.AppendLine($"**결과: 통과 {pass} · 실패 {fail} · 정보 {results.Count - pass - fail} (총 {results.Count})**");
        sb.AppendLine();
        sb.AppendLine("| ID | 범주 | 시나리오 | 무엇이 깨지면 실패 | 판정 | 측정값 | 기준 |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var r in results)
            sb.AppendLine($"| {r.Id} | {r.Category} | {r.Title} | {r.Breaks} | {(r.Status == AdvStatus.Pass ? "✅ 통과" : r.Status == AdvStatus.Fail ? "❌ 실패" : "ℹ 정보")} | {r.Measured.Replace("|", "/")} | {r.Criterion} |");
        var notes = results.Where(r => r.Note.Length > 0).ToList();
        if (notes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## 비고");
            foreach (var r in notes) sb.AppendLine($"- {r.Id}: {r.Note}");
        }
        sb.AppendLine();
        sb.AppendLine("## 알려진 한계");
        sb.AppendLine("- 충돌 판정은 대표점-형상 내부 판정이라 모서리끼리 교차(정점이 상대 안에 들어가지 않는 경우)는 놓칠 수 있다(FR-SHL-04 근사).");
        sb.AppendLine("- 응력 색은 강체 하중 기반 근사(보 모델 + 얇은 벽 Roark 판 굽힘·막응력 + 수정 뉴턴 압력 + 연결점 핫스팟)로 FEA 가 아니다. 국부 좌굴·접촉 응력은 3단계(OpenCourant) 대상.");
        sb.AppendLine("- 판 굽힘은 선형 소처짐 이론이다. 템플릿 06 컨테이너(t 2 mm, □270)는 판 처짐 w/t ≈ 2.6 으로 막 작용 영역이라 실제 응력은 표시값보다 작다(보수적). 판 분포는 sin·sin(단순지지)/가장자리 최대(고정) 근사.");
        sb.AppendLine("- 관성 압력은 물체 질량(내용물 포함)을 벽 면적에 고르게 퍼뜨린 근사 — 내용물이 한 벽에 몰려 지지되는 경우는 과소. 열린 외피 조각(petal)은 막응력만, 자유 가장자리 굽힘 없음.");
        sb.AppendLine("- 표면 압력은 수정 뉴턴 + 일정 기저압이라 옆면 흡입(구 θ≈75° 의 Cp ≈ −1 수준)·박리 위치는 표현하지 않는다 — 압력 색은 바람받이 쪽 크기 판단용.");
        sb.AppendLine("- 외피 조각 Cd·마하 보정·폭약 Δv·충격 펄스는 (가정) 값 — 시험·CFD 전.");
        sb.AppendLine("- 착지 재질 판정(1·2단계, ImpactDamage)은 공학 추정식(탄성 에너지 용량 + Wierzbicki/Alexander 평균 압궤력 + 1D 파동)이다.");
        sb.AppendLine("  OpenCourant 전체 메시(11,264 셸) 교차검증(템플릿 08, 7.1~7.4 m/s, 암반): 깨짐/안 깨짐은 ABS 꼭짓점·면, CFRP 꼭짓점, PC·양철 꼭짓점·면, Al 면 모두 일치,");
        sb.AppendLine("  PC 꼭짓점 찌그러짐 30 mm vs FEA 29 mm. 연성 벽 면 착지는 1단계가 크게 보수적(양철 65 vs 4 mm, Al 얕은 찌그러짐 vs FEA 탄성 — 내용물이 바닥판으로 지면에 직접 하중 전달),");
        sb.AppendLine("  양철 꼭짓점 252 vs 132 mm(약 2배 과대). FEA 변형 = 강체 맞춤 잔차.");
        sb.AppendLine("- 셸 FEA 의 내용물 질량은 지면 쪽 절반 셸 밀도에 싣는 근사(내용물 강성·내부 충돌 없음). 전체에 고르게 퍼뜨리면 무거운 뚜껑이 휘청여 뚜껑이 깨지는 인공 결과가 남(실제 실행에서 확인 후 수정).");
        sb.AppendLine("- 셸 파단은 LAW2 EPS_max(연성 = 연신율, 취성 = 연신율/2) 요소 삭제 — 취성 고분자의 실제 균열 진전·변형률 속도 의존 파단은 표현하지 않는다(재질 카드 보정은 시험 필요).");
        sb.AppendLine("- 얇은 벽 착지 판정은 지면을 강체로 본다(Young 관입식은 가볍고 뭉툭한 물체 적용 범위 밖) — 흙 위 착지는 보수적.");
        sb.AppendLine("- CalculiX FEA(PRD Z)는 선형 탄성 소변형이 기본(--nonlinear 로 NLGEOM + 이선형 소성)이고 물체마다 단일 재질이다. 하중 순간의 면 압력은 수정 뉴턴(W절)이라 합력이 시뮬레이션 공력과 다를 수 있다(템플릿 06 c2: FE 26 G vs 기록 19 G). 충돌 충격은 하중으로 들어가지 않는다(충돌은 OpenCourant).");
        sb.AppendLine("- STL 조립체(여러 닫힌 껍질)는 Gmsh 가 결합 정보를 몰라 복셀 C3D20 으로 대체 — 계단 표면이라 모서리 국부 응력은 참고용. Fusion STEP(`--cad`)을 쓰면 바디 접촉면을 공유한 C3D10.");
        sb.AppendLine("- 근사 연결점 핫스팟(F/πr²)은 얇은 판의 국부 굽힘을 빠뜨린다 — 템플릿 06 c2 주낙하산 라이저 365 N 에서 근사 3 MPa vs CalculiX 108 MPa(실모델 FEA 보고서).");
        return sb.ToString();
    }
}
