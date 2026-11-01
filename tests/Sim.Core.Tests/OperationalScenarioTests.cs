using System.Text.Json.Nodes;
using System.Xml.Linq;
using Sim.Core.Analysis;
using Sim.Core.Engine;
using Sim.Core.Geometry;
using Sim.Core.Models;
using Sim.Core.Numerics;
using Sim.Core.Scenario;

namespace Sim.Core.Tests;

/// <summary>
/// 프로세스 환경 변수 MNS_REAL_MODELS 를 잠시 바꾸는 테스트는 다른 테스트(실모델 폴더를 환경 변수로 찾는 시나리오 빌드)와 동시에 돌면 경쟁한다 —
/// 이 컬렉션은 병렬 실행을 끄고 다른 컬렉션이 모두 끝난 뒤 따로 돈다.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RealModelsEnvCollection
{
    public const string Name = "MNS_REAL_MODELS 환경 변수";
}

/// <summary>PRD AA절(FR-AN-01~07): 실모델 해석·조립 장착·낙하산 고장/전개 이상·사출 반작용·운용 지표·템플릿 09/09b.</summary>
[Collection(RealModelsEnvCollection.Name)]
public class OperationalScenarioTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "mns_op_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    private static string RealBodyJson(string dir, string mode) => $$"""
      {"schema":"mns.scenario/1","sim":{"tEnd":0.01},"realModels":{"dir":"{{dir.Replace("\\", "/")}}","mode":"{{mode}}"},
       "bodies":[{"name":"x","altitude":100,"massProps":"real:part.massprops.json","modelFrame":"fusion","noseAxis":"-Z",
                  "fallback":{"mass":2.5,"shape":{"type":"box","size":[0.1,0.1,0.1]} } } ]}
      """;

    [Fact]
    public void Real_model_reference_uses_folder_when_present_and_synthetic_fallback_otherwise()
    {
        var dir = TempDir();
        var mp = new MassPropsFile { Name = "part", Mass = 3.0, Cg = new Vec3(0, 0, 0.2), Inertia = Mat3.Diagonal(0.01, 0.02, 0.03),
            BBoxMin = new Vec3(-0.1, -0.1, 0), BBoxMax = new Vec3(0.1, 0.1, 0.4), Frame = "fusion", Source = "fusion" };
        File.WriteAllText(Path.Combine(dir, "part.massprops.json"), mp.ToJson().ToJsonString());
        // 이 테스트 흐름에서만 MNS_REAL_MODELS 를 바꾼다(프로세스 환경변수를 바꾸면 병렬로 도는 실모델 테스트가 흔들린다)
        var scope = Sim.Core.Scenario.ScenarioLoader.OverrideRealModels(null);
        try
        {
            var real = TestUtil.Build(RealBodyJson(dir, "auto")).GetBody("x");
            Assert.Equal("real", real.ModelSource);
            Assert.Equal(3.0, real.OwnMass, 12);
            // noseAxis −Z: Fusion (0,0,0.2) → 동체 x = −0.2
            Assert.Equal(-0.2, real.OwnCg.X, 12);
            Assert.EndsWith("part.massprops.json", real.ModelPath);

            var syn = TestUtil.Build(RealBodyJson(dir, "synthetic"));
            Assert.Equal("synthetic", syn.GetBody("x").ModelSource);
            Assert.Equal(2.5, syn.GetBody("x").OwnMass, 12);

            var missing = TestUtil.Build(RealBodyJson(Path.Combine(dir, "nope"), "auto"));
            Assert.Equal("synthetic", missing.GetBody("x").ModelSource);
            Assert.Contains(missing.Warnings, w => w.Contains("합성 대체"));
            Assert.Throws<InvalidDataException>(() => TestUtil.Build(RealBodyJson(Path.Combine(dir, "nope"), "required")));

            using (Sim.Core.Scenario.ScenarioLoader.OverrideRealModels("synthetic"))
                Assert.Equal("synthetic", TestUtil.Build(RealBodyJson(dir, "auto")).GetBody("x").ModelSource);
            using (Sim.Core.Scenario.ScenarioLoader.OverrideRealModels(dir))
                Assert.Equal("real", TestUtil.Build(RealBodyJson(Path.Combine(dir, "nope"), "auto")).GetBody("x").ModelSource);
        }
        finally { scope.Dispose(); }
    }

    [Fact]
    public void Model_mount_frame_places_children_at_their_assembly_coordinates()
    {
        // 부모 = Fusion·noseAxis −Z, 자식 = 드론 프레임(추력 축 Fusion +Z), 자식 모델 원점은 Fusion Z 로 0.39 이동
        var w = TestUtil.Build("""
          {"schema":"mns.scenario/1","sim":{"tEnd":0.01},"environment":{"vacuum":true},
           "bodies":[{"name":"p","altitude":500,"mass":10,"cg":[-0.5,0,0],"inertia":[1,1,1],"modelFrame":"fusion","noseAxis":"-Z"},
                     {"name":"c","parent":"p","mountFrame":"model","assemblyOffset":[0,0,0.39],"mass":2,"cg":[0.01,0.02,-0.3],"inertia":[0.1,0.1,0.1],
                      "modelFrame":"fusion","modelToBody":[[1,0,0],[0,-1,0],[0,0,-1]]}]}
          """);
        var p = w.GetBody("p"); var c = w.GetBody("c");
        var P = NoseGeometry.WithNose(Frames.FusionToBody, "-Z");
        var D = new Mat3(1, 0, 0, 0, -1, 0, 0, 0, -1);
        // 자식 CG 의 Fusion 좌표(자기 모델 좌표 + 이동) → 부모 동체 좌표
        var cgFusion = D.Transpose() * c.OwnCg + new Vec3(0, 0, 0.39);
        var expect = p.PointWorld(P * cgFusion);
        Assert.True((c.OwnCgWorld - expect).Norm < 1e-12, $"{c.OwnCgWorld} vs {expect}");
        // 자식 추력 축(동체 −Z) = Fusion +Z = 부모 동체 −X
        var thrustWorld = c.WorldQ.Rotate(new Vec3(0, 0, -1));
        var parentMinusX = p.WorldQ.Rotate(new Vec3(-1, 0, 0));
        Assert.True((thrustWorld - parentMinusX).Norm < 1e-12);
        Assert.Throws<InvalidDataException>(() => TestUtil.Build("""
          {"schema":"mns.scenario/1","bodies":[{"name":"p","altitude":5,"mass":1},{"name":"c","parent":"p","mountFrame":"cad","mass":1}]}
          """));
    }

    private static Parachute DropWithChute(string failure, ulong seed = 1, double tEnd = 6)
    {
        var w = TestUtil.Run($$"""
          {"schema":"mns.scenario/1","sim":{"dt":0.002,"tEnd":{{tEnd}},"seed":{{seed}}},
           "bodies":[{"name":"b","altitude":2000,"velocity":[0,0,30],"mass":5,"shape":{"type":"box","size":[0.3,0.3,0.3]},
                      "devices":[{"type":"parachute","name":"ch","d0":1.22,"cd0":2.2,"lineStretchTime":0.2{{(failure.Length > 0 ? "," + failure : "")}} }]}],
           "events":[{"name":"dep","when":{"type":"time","t":0.5},"do":[{"type":"deploy","device":"b.ch"}]}]}
          """);
        return w.GetBody("b").Devices.OfType<Parachute>().Single();
    }

    [Fact]
    public void Parachute_failure_modes_change_deployment_as_specified()
    {
        var ok = DropWithChute("");
        Assert.Equal(ChuteState.Open, ok.State);
        var none = DropWithChute("""
          "failure":{"mode":"noDeploy"}
          """);
        Assert.True(none.FailureActive);
        Assert.Equal(ChuteState.Stowed, none.State);
        Assert.Equal(0, none.MaxForce);
        var delay = DropWithChute("""
          "failure":{"mode":"delay","delay":1.5}
          """);
        Assert.Equal(1.5, delay.ExtraDelay, 12);
        Assert.True(delay.MaxForceTime > ok.MaxForceTime + 1.0);
        var partial = DropWithChute("""
          "failure":{"mode":"partial","fill":0.4}
          """);
        Assert.Equal(0.4, partial.FillCap, 12);
        Assert.Equal(0.4, partial.FillFraction(100, 10), 12);
        var twist = DropWithChute("""
          "failure":{"mode":"lineTwist","cdsLoss":0.5}
          """);
        Assert.Equal(ok.CdS0 * 0.5, twist.CdS0, 9);
        // 확률 0 → 걸리지 않음, 확률 0.5 → 시드마다 결정적(같은 시드 같은 결과)
        Assert.False(DropWithChute("""
          "failure":{"mode":"noDeploy","probability":0}
          """).FailureActive);
        var a = Enumerable.Range(1, 12).Select(s => DropWithChute("""
          "failure":{"mode":"noDeploy","probability":0.5}
          """, (ulong)s, 0.6).FailureActive).ToList();
        var b = Enumerable.Range(1, 12).Select(s => DropWithChute("""
          "failure":{"mode":"noDeploy","probability":0.5}
          """, (ulong)s, 0.6).FailureActive).ToList();
        Assert.Equal(a, b);
        Assert.Contains(true, a); Assert.Contains(false, a);
        Assert.Throws<InvalidDataException>(() => DropWithChute("""
          "failure":{"mode":"sometimes"}
          """));
    }

    [Fact]
    public void Debris_through_lines_during_stretch_delays_fill_and_damages_lines()
    {
        // 컨테이너가 +x 로 날고 드로그 라인이 −x 로 뻗는 동안(라인 신장 1 s) 잔해가 옆에서 3 m/s 로 라인 중간을 가로지른다(대기 거의 0 → 힘 무시)
        var w = TestUtil.Run("""
          {"schema":"mns.scenario/1","sim":{"dt":0.0005,"tEnd":0.9},"environment":{"constantDensity":1e-9,"gravity":0},
           "bodies":[{"name":"c","position":[0,0,-500],"velocity":[10,0,0],"mass":50,"aero":"none","shape":{"type":"box","size":[0.2,0.2,0.2]},
                      "devices":[{"type":"parachute","name":"ch","d0":2.0,"attach":[-0.1,0,0],"riser":5,"lineStretchTime":1.0}]},
                     {"name":"d","position":[-3.0,2.5,-500],"velocity":[10,-3,0],"mass":1,"aero":"none","shape":{"type":"sphere","radius":0.01}}],
           "events":[{"name":"dep","when":{"type":"time","t":0},"do":[{"type":"deploy","device":"c.ch"}]}],
           "collisions":{"parachutes":true,"canopyRegion":"sphere+lines","chuteAnomaly":{"delay":0.3,"lineDamagePerHit":0.2,"minRelSpeed":2.0}}}
          """);
        var ch = w.GetBody("c").Devices.OfType<Parachute>().Single();
        Assert.Single(ch.Anomalies);
        Assert.Equal("lines", ch.Anomalies[0].Region);
        Assert.Equal("LineStretch", ch.Anomalies[0].Phase);
        Assert.Equal(0.3, ch.ExtraDelay, 12);
        Assert.Equal(0.8, ch.DamageFactor, 12);
        Assert.Equal(1.3, ch.StretchCompleteTime - ch.DeployTime, 9);
        Assert.Contains(w.EventLog, e => e.Name == "anomaly:c.ch");
        Assert.Equal(1, w.Collisions!.AnomalyCount);
        var s = Results.Summary(ScenarioDoc.Parse("{}", TestUtil.RepoRoot), w);
        Assert.Single(s["devices"]!["c.ch"]!["anomalies"]!.AsArray());
    }

    [Fact]
    public void Pulse_reaction_conserves_momentum_between_ejected_and_parent()
    {
        var w = TestUtil.Run("""
          {"schema":"mns.scenario/1","sim":{"dt":0.0005,"tEnd":0.1},"environment":{"vacuum":true,"gravity":0},
           "bodies":[{"name":"p","position":[0,0,-500],"mass":20,"inertia":[1,1,1]},
                     {"name":"k","position":[1,0.2,-500],"mass":4,"inertia":[0.1,0.1,0.1],
                      "devices":[{"type":"pulse","name":"e","peakG":15,"duration":0.025,"direction":[1,0,0],"reaction":"p"}]}],
           "events":[{"name":"f","when":{"type":"time","t":0.01},"do":[{"type":"fire","device":"k.e"}]}]}
          """);
        var p = w.GetBody("p"); var k = w.GetBody("k");
        var mom = p.V * p.Mass + k.V * k.Mass;
        Assert.True(mom.Norm < 1e-9, $"운동량 {mom}");
        double dv = Environment.Atmosphere.G0 * 15 * 0.025 * 2 / Math.PI;
        Assert.Equal(dv, k.V.X, 3);
        Assert.Equal(-dv * 4 / 20, p.V.X, 3);
        // 작용선이 부모 CG 를 지나지 않으면(y 0.2 m 어긋남) 부모가 회전 반작용을 받는다
        Assert.True(Math.Abs(p.W.Z) > 1e-6);
    }

    [Fact]
    public void Tumbling_metric_integrates_transverse_rotation()
    {
        var doc = ScenarioDoc.Parse("""
          {"schema":"mns.scenario/1","sim":{"dt":0.001,"tEnd":2.0,"outputRate":100},"environment":{"vacuum":true,"gravity":0},
           "bodies":[{"name":"b","position":[0,0,-500],"omega":[0,6.0,0],"mass":1,"inertia":[0.1,0.1,0.1]}],
           "metrics":[{"type":"tumbling","name":"t","bodies":["b"],"from":"start","attitudeAt":["start"]}],
           "events":[{"name":"start","when":{"type":"time","t":0.5},"do":[{"type":"log","message":"x"}]}]}
          """, TestUtil.RepoRoot);
        var w = doc.Build(); w.Run();
        var m = Results.Summary(doc, w)["metrics"]!["t"]!["bodies"]![0]!;
        Assert.Equal(6.0 * 1.5 * 180 / Math.PI, m["cumulativeTransverseRotation_deg"]!.GetValue<double>(), 1);
        Assert.True(m["tumbling"]!.GetValue<bool>());
        Assert.Equal(6.0 * 180 / Math.PI, m["maxRate_dps"]!.GetValue<double>(), 6);
        Assert.NotNull(m["atEvents"]!["start"]);
    }

    private static ScenarioDoc Load09(string file = "09_munition_3petal_longcontainer_real.json", double tEnd = 40)
    {
        var doc = ScenarioDoc.Load(TestUtil.Scenario(file));
        doc.Root["realModels"] = new JsonObject { ["mode"] = "synthetic" };
        doc.Root["sim"]!["tEnd"] = tEnd;
        return doc;
    }

    [DesignFact]
    public void Template_09_runs_the_animation_sequence_with_plausible_loads()
    {
        // 구 설계(박스 4개) 기준 — 설계 변경으로 09 가 바뀌어 보존본 09r34 로 검사(FR-CHG-01)
        var doc = Load09(TestUtil.DesignFile("tests/Sim.Core.Tests/scenarios", "09r34_*.json"));
        var w = doc.Build(); w.Run();
        Assert.False(w.Suspect, string.Join("; ", w.Warnings.Take(5)));
        var s = Results.Summary(doc, w);
        var m = s["metrics"]!;
        // 운용 영상 순서(A55 와 별개로 대표 사건만)
        double T(string e) => w.FiredEvents[e];
        Assert.True(T("split_detonation") < T("lid_drogue") && T("lid_drogue") < T("main_deploy") && T("main_deploy") < T("door_open") && T("door_open") < T("eject1"));
        Assert.Equal(2.0, T("eject4") - T("eject3"), 3);
        Assert.True(w.FiredEvents.ContainsKey("tether4_cut"));
        // 질량: 합계 > 한계
        Assert.True(m["massBudget"]!["total_kg"]!.GetValue<double>() > m["massBudget"]!["limit_kg"]!.GetValue<double>());
        Assert.False(m["massBudget"]!["pass"]!.GetValue<bool>());
        Assert.True(m["massBudget"]!["conserved"]!.GetValue<bool>());
        // 하중: 컨테이너 ≤ 40 G(기폭 펄스 15 G 이상), 낙하산 개방 하중은 양수
        double cg = m["containerPeakG"]!["maxG"]!.GetValue<double>();
        Assert.InRange(cg, 14.0, 40.0);
        Assert.True(s["devices"]!["container.drogue"]!["maxForce_N"]!.GetValue<double>() > 1000);
        Assert.True(s["devices"]!["box1.chute"]!["maxForce_N"]!.GetValue<double>() > 100);
        // 사출 속도 FR-SEP-01 3–5 m/s 근처(0.1 s 동안의 상대 중력 가속 포함 ≤ 6)
        foreach (var b in m["boxEjection"]!["bodies"]!.AsArray()) Assert.InRange(b!["relativeSpeed_mps"]!.GetValue<double>(), 3.0, 6.0);
        // 주낙하산 진자: 유한 주기·작은 진폭
        var pend = m["containerPendulum"]!["bodies"]![0]!;
        Assert.InRange(pend["period_s"]!.GetValue<double>(), 1.0, 8.0);
        Assert.InRange(pend["amplitude_deg"]!.GetValue<double>(), 0.0, 30.0);
        // 텀블링 판정·드로그 순간 자세 기록
        Assert.NotNull(m["containerTumbling"]!["bodies"]![0]!["atEvents"]!["lid_drogue"]!["totalAoA_deg"]);
        Assert.True(m["timeline"]!["ordered"]!.GetValue<bool>());
        // 이벤트 직후 1 ms 촘촘 기록(eventBurst): 박스1 사출 50 ms 안 프레임 간격 ≤ 1.1 ms
        double te = T("eject1");
        var burst = w.Recorder.Frames.Where(f => f.T >= te && f.T <= te + 0.05).ToList();
        Assert.True(burst.Count >= 40, $"{burst.Count} 프레임");
        Assert.True(burst.Zip(burst.Skip(1)).All(p => p.Second.T - p.First.T <= 0.0011));
        // 실모델 없음 → 전부 합성 대체
        Assert.All(w.Bodies.Where(b => b.ModelSource is not null), b => Assert.Equal("synthetic", b.ModelSource));
    }

    [DesignFact]
    public void Template_09_glide_dispenser_drops_wing_part_before_three_petal_split()
    {
        var doc = Load09(tEnd: 6);
        var w = doc.Build(); w.Run();
        Assert.False(w.Suspect, string.Join("; ", w.Warnings.Take(5)));
        // 외형: 노즈·꼬리·날개 파트는 메시(STL), 날개 폭 ≈ 2.3 m 가 캐니스터 위(−Z)에 얹힘
        var kit = w.GetBody("wingkit");
        Assert.Equal(ShapeKind.Mesh, kit.Shape!.Kind);
        Assert.Equal(ShapeKind.Mesh, w.GetBody("nose").Shape!.Kind);
        Assert.Equal(ShapeKind.Mesh, w.GetBody("tail").Shape!.Kind);
        var (min, max) = kit.Shape.Mesh!.Bounds();
        Assert.InRange(max.Y - min.Y, 2.2, 2.4);
        Assert.True(max.Z < -0.15, $"날개 파트 최저점 z {max.Z:F3}");
        // 순서: 날개 파트 분리 → 3분할 → 컨테이너 노출
        double T(string e) => w.FiredEvents[e];
        Assert.True(T("wing_release") < T("split_detonation") && T("split_detonation") < T("container_exposed"));
        Assert.Equal(T("wing_release"), kit.ReleaseTime, 6);
        Assert.True(w.GetBody("petal1").ReleaseTime > kit.ReleaseTime);
        // 분리 후 날개 파트는 자유 비행 · 3분할 시각에 탄체(운반체)보다 위·뒤로 떨어져 있음
        Assert.Equal(BodyMode.Free, kit.Mode);
        int i = w.Recorder.IndexAt(T("split_detonation"));
        var f = w.Recorder.Frames[i];
        var car = w.GetBody("munition");
        var d = f.Bodies[kit.Index].Origin - f.Bodies[car.Index].Origin;
        Assert.True(d.Norm > 1.0, $"3분할 시 날개 파트–탄체 거리 {d.Norm:F2} m");
        Assert.True(w.FiredEvents.ContainsKey("lid_drogue"));
    }

    [DesignFact]
    public void Template_09b_belly_door_drops_container_out_of_carrier()
    {
        var doc = Load09("09b_munition_bellydoor_longcontainer_real.json", 12);
        var w = doc.Build(); w.Run();
        Assert.False(w.Suspect);
        var hinge = w.Connectors.OfType<Hinge>().Single();
        Assert.True(hinge.Released);
        var c = w.GetBody("container");
        Assert.Equal(BodyMode.Free, c.Mode);
        // 운반체(탄체)는 규정 궤적으로 계속 가고 컨테이너는 아래(+D)로 떨어진다
        var car = w.GetBody("munition");
        Assert.True(c.R.Z > car.R.Z + 5, $"컨테이너 D {c.R.Z:F1} vs 탄체 {car.R.Z:F1}");
        Assert.True(w.FiredEvents.ContainsKey("main_deploy"));
    }

    [DesignFact]
    public void Committed_templates_09_and_09b_build_and_reference_real_groups()
    {
        foreach (var f in new[] { TestUtil.DesignFile("tests/Sim.Core.Tests/scenarios", "09r34_*.json"), TestUtil.DesignFile("tests/Sim.Core.Tests/scenarios", "09r34b_*.json") })
        {
            var doc = ScenarioDoc.Load(TestUtil.Scenario(f));
            var refs = doc.Root["bodies"]!.AsArray().Select(b => b!["massProps"]?.GetValue<string>()).Where(x => x is not null).ToList();
            Assert.Contains("real:container.massprops.json", refs);
            Assert.Contains("real:drone.massprops.json", refs);
            Assert.All(doc.Root["bodies"]!.AsArray().Where(b => b!["massProps"] is not null), b => Assert.NotNull(b!["fallback"]));
            doc.Root["realModels"] = new JsonObject { ["mode"] = "synthetic" };
            var w = doc.Build();
            Assert.True(w.Bodies.Count >= 20);
        }
    }

    [Fact]
    public void Report_helpers_wilson_interval_and_svg_are_well_formed()
    {
        var (lo, hi) = OperationalReport.Wilson(0, 64);
        Assert.Equal(0, lo, 12);
        Assert.InRange(hi, 0.05, 0.06);   // 0/64 → 95% 상한 ≈ 5.7 %
        var (lo2, hi2) = OperationalReport.Wilson(32, 64);
        Assert.True(lo2 < 0.5 && hi2 > 0.5 && Math.Abs(0.5 - lo2 - (hi2 - 0.5)) < 1e-12);
        var svg = SvgPlot.Line("t", "x", "y", [new("a", [(0, 0), (1, 2), (2, 1)]), new("b", [(0, 1), (2, 3)], Markers: true)]);
        var x = XDocument.Parse(svg);
        Assert.Equal("svg", x.Root!.Name.LocalName);
        Assert.Contains("polyline", svg);
        Assert.Throws<ArgumentException>(() => SvgPlot.Line("t", "x", "y", Enumerable.Range(0, 9).Select(i => new SvgPlot.Series($"s{i}", [(0, i)])).ToList()));
        Assert.Equal([0, 2, 4, 6, 8, 10], SvgPlot.Ticks(0, 10));
    }
}
